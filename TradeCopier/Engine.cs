#region Using declarations
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using NinjaTrader.Cbi;
#endregion

namespace NinjaTrader.Custom.AddOns.TradeCopier
{
    public class TradeCopierEngine : IDisposable, INotifyPropertyChanged
    {
        private readonly ObservableCollection<AccountSelection> availableAccounts;
        private readonly ObservableCollection<AccountSelection> selectableFollowerAccounts;
        private readonly ObservableCollection<AccountSelection> followerAccounts;
        private readonly Dictionary<string, int> leadQuantityByInstrument;
        private readonly Dictionary<string, DateTime> flattenAllSuppressionUntilByInstrument;
        private readonly HashSet<Account> executionSubscribedAccounts;
        private readonly HashSet<Account> positionSubscribedAccounts;

        private AccountSelection leadAccount;
        private bool isRunning;
        private bool isFlattenAllInProgress;

        public string CopierStatusText
        {
            get { return isRunning ? "Copier läuft" : "Copier gestoppt"; }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public TradeCopierEngine()
        {
            availableAccounts = new ObservableCollection<AccountSelection>();
            selectableFollowerAccounts = new ObservableCollection<AccountSelection>();
            followerAccounts = new ObservableCollection<AccountSelection>();
            leadQuantityByInstrument = new Dictionary<string, int>();
            executionSubscribedAccounts = new HashSet<Account>();
            positionSubscribedAccounts = new HashSet<Account>();
            flattenAllSuppressionUntilByInstrument = new Dictionary<string, DateTime>(StringComparer.Ordinal);

            followerAccounts.CollectionChanged += FollowerAccountsChanged;
        }

        public ObservableCollection<AccountSelection> AvailableAccounts
        {
            get { return availableAccounts; }
        }

        public ObservableCollection<AccountSelection> FollowerAccounts
        {
            get { return followerAccounts; }
        }

        public ObservableCollection<AccountSelection> SelectableFollowerAccounts
        {
            get { return selectableFollowerAccounts; }
        }

        public AccountSelection LeadAccount
        {
            get { return leadAccount; }
            set
            {
                if (ReferenceEquals(leadAccount, value))
                    return;

                leadAccount = value;

                if (leadAccount != null && leadAccount.FollowEnabled)
                    leadAccount.FollowEnabled = false;

                InitializeLeadPositionCache();
                OnPropertyChanged("LeadAccount");
                RefreshSelectableFollowerAccounts();
            }
        }

        public bool IsRunning
        {
            get { return isRunning; }
            set
            {
                if (isRunning == value)
                    return;

                isRunning = value;
                OnPropertyChanged("IsRunning");
                OnPropertyChanged("CopierStatusText");
            }
        }

        public bool SelectAllFollowers
        {
            get
            {
                return selectableFollowerAccounts.Count > 0
                    && selectableFollowerAccounts.All(a => a.FollowEnabled);
            }
            set
            {
                foreach (AccountSelection account in selectableFollowerAccounts)
                    account.FollowEnabled = value;

                OnPropertyChanged("SelectAllFollowers");
            }
        }

        public void ToggleRunning()
        {
            IsRunning = !IsRunning;
        }

        public void Start()
        {
            SubscribePlatformEvents();
            SyncAccountsWithControlCenter();
        }

        public void SyncAccountsWithControlCenter()
        {
            List<Account> currentAccounts = Account.All
                .Where(IsTradeableAccount)
                .OrderBy(a => a.Name)
                .ToList();

            SyncExecutionSubscriptions(currentAccounts);
            SyncPositionSubscriptions(currentAccounts);

            // Entferne Accounts, die im Control Center nicht mehr verfügbar sind.
            for (int i = availableAccounts.Count - 1; i >= 0; i--)
            {
                if (currentAccounts.All(a => a.Name != availableAccounts[i].Name))
                {
                    AccountSelection removed = availableAccounts[i];
                    removed.PropertyChanged -= AccountSelectionPropertyChanged;
                    availableAccounts.RemoveAt(i);
                    followerAccounts.Remove(removed);

                    if (ReferenceEquals(leadAccount, removed))
                        LeadAccount = null;
                }
            }

            // Ergänze neue Accounts aus dem Control Center.
            foreach (Account account in currentAccounts)
            {
                if (availableAccounts.All(a => a.Name != account.Name))
                {
                    AccountSelection added = new AccountSelection(account);
                    added.PropertyChanged += AccountSelectionPropertyChanged;
                    availableAccounts.Add(added);
                }
            }

            // Leadkonto beibehalten, ansonsten Default setzen.
            if (leadAccount == null && availableAccounts.Count > 0)
                LeadAccount = availableAccounts[0];

            RefreshSelectableFollowerAccounts();
        }

        private void RefreshSelectableFollowerAccounts()
        {
            selectableFollowerAccounts.Clear();

            foreach (AccountSelection account in availableAccounts.Where(a => leadAccount == null || a.Name != leadAccount.Name))
                selectableFollowerAccounts.Add(account);

            OnPropertyChanged("SelectableFollowerAccounts");
            OnPropertyChanged("SelectAllFollowers");
        }

        private void SubscribePlatformEvents()
        {
            Account.AccountStatusUpdate += AccountStatusUpdate;

            foreach (Account account in Account.All.Where(IsTradeableAccount))
            {
                SubscribeExecutionForAccount(account);
                SubscribePositionForAccount(account);
            }
        }

        private void SyncExecutionSubscriptions(IEnumerable<Account> accounts)
        {
            List<Account> accountList = accounts != null ? accounts.ToList() : new List<Account>();

            foreach (Account account in executionSubscribedAccounts.ToList())
            {
                if (accountList.Contains(account))
                    continue;

                account.ExecutionUpdate -= AccountExecutionUpdate;
                executionSubscribedAccounts.Remove(account);
            }

            foreach (Account account in accountList)
                SubscribeExecutionForAccount(account);
        }

        private void SyncPositionSubscriptions(IEnumerable<Account> accounts)
        {
            List<Account> accountList = accounts != null ? accounts.ToList() : new List<Account>();

            foreach (Account account in positionSubscribedAccounts.ToList())
            {
                if (accountList.Contains(account))
                    continue;

                account.PositionUpdate -= AccountPositionUpdate;
                positionSubscribedAccounts.Remove(account);
            }

            foreach (Account account in accountList)
                SubscribePositionForAccount(account);
        }

        private void SubscribeExecutionForAccount(Account account)
        {
            if (account == null || executionSubscribedAccounts.Contains(account))
                return;

            account.ExecutionUpdate += AccountExecutionUpdate;
            executionSubscribedAccounts.Add(account);
        }

        private void SubscribePositionForAccount(Account account)
        {
            if (account == null || positionSubscribedAccounts.Contains(account))
                return;

            account.PositionUpdate += AccountPositionUpdate;
            positionSubscribedAccounts.Add(account);
        }

        private void AccountExecutionUpdate(object sender, ExecutionEventArgs e)
        {
            if (e == null || e.Execution == null || leadAccount == null)
                return;

            Account executionAccount = sender as Account;
            if (executionAccount == null || executionAccount.Name != leadAccount.Name)
                return;

            OnLeadExecution(e.Execution);
        }

        private void AccountPositionUpdate(object sender, PositionEventArgs e)
        {
            if (e == null || e.Position == null || e.Position.Instrument == null || leadAccount == null)
                return;

            Account positionAccount = sender as Account;
            if (positionAccount == null || positionAccount.Name != leadAccount.Name)
                return;

            OnLeadPositionChanged(e.Position.Instrument);
        }

        private void AccountStatusUpdate(object sender, AccountStatusEventArgs e)
        {
            // Account-Liste bei Plattformänderungen sauber mit dem Control Center synchron halten.
            SyncAccountsWithControlCenter();
        }

        private static bool IsTradeableAccount(Account account)
        {
            if (account == null)
                return false;

            if (account.Connection == null || !account.Connection.Status.Equals(ConnectionStatus.Connected))
                return false;

            return account.Name != null && account.Name.Length > 0;
        }

        private void FollowerAccountsChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (AccountSelection selection in e.NewItems)
                    selection.FollowEnabled = true;
            }

            if (e.OldItems != null)
            {
                foreach (AccountSelection selection in e.OldItems)
                    selection.FollowEnabled = false;
            }
        }

        private void AccountSelectionPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e == null || e.PropertyName != "FollowEnabled")
                return;

            AccountSelection selection = sender as AccountSelection;
            if (selection == null)
                return;

            if (selection.FollowEnabled)
            {
                if (!followerAccounts.Contains(selection))
                    followerAccounts.Add(selection);

                OnPropertyChanged("SelectAllFollowers");

                return;
            }

            if (followerAccounts.Contains(selection))
                followerAccounts.Remove(selection);

            OnPropertyChanged("SelectAllFollowers");
        }

        private void InitializeLeadPositionCache()
        {
            leadQuantityByInstrument.Clear();

            if (leadAccount == null || leadAccount.Account == null || leadAccount.Account.Positions == null)
                return;

            foreach (Position position in leadAccount.Account.Positions)
            {
                if (position == null || position.Instrument == null)
                    continue;

                string instrumentKey = position.Instrument.FullName;
                if (instrumentKey == null || instrumentKey.Length == 0)
                    continue;

                leadQuantityByInstrument[instrumentKey] = GetSignedQuantity(position.MarketPosition, position.Quantity);
            }
        }

        private void OnLeadPositionChanged(Instrument instrument)
        {
            if (!IsRunning || instrument == null || isFlattenAllInProgress)
                return;

            string instrumentKey = instrument.FullName;
            if (ShouldIgnoreLeadExecutionForFlattenAll(instrumentKey))
                return;

            int previousQty = leadQuantityByInstrument.ContainsKey(instrumentKey)
                ? leadQuantityByInstrument[instrumentKey]
                : 0;

            int currentQty = GetLeadPositionQuantity(instrumentKey);
            int delta = currentQty - previousQty;
            if (delta == 0)
                return;

            leadQuantityByInstrument[instrumentKey] = currentQty;

            if (currentQty == 0)
            {
                FlattenFollowers(instrument);
                return;
            }

            ReplicateDirectionalTrade(instrument, delta);
        }

        public void OnLeadExecution(Execution execution)
        {
            if (!IsRunning || execution == null || execution.Instrument == null || isFlattenAllInProgress)
                return;

            string instrumentKey = execution.Instrument.FullName;
            if (ShouldIgnoreLeadExecutionForFlattenAll(instrumentKey))
                return;

            int previousQty = leadQuantityByInstrument.ContainsKey(instrumentKey)
                ? leadQuantityByInstrument[instrumentKey]
                : 0;

            int delta = CalculateLeadDelta(execution, instrumentKey, previousQty);
            if (delta == 0)
                return;

            int currentQty = GetLeadPositionQuantity(instrumentKey);
            if (currentQty == previousQty)
                currentQty = previousQty + delta;

            leadQuantityByInstrument[instrumentKey] = currentQty;

            if (currentQty == 0)
            {
                FlattenFollowers(execution.Instrument);
                return;
            }

            if (!ReplicateExecutionTrade(execution.Order, execution.Instrument, delta))
                ReplicateDirectionalTrade(execution.Instrument, delta);
        }

        private bool ShouldIgnoreLeadExecutionForFlattenAll(string instrumentKey)
        {
            if (instrumentKey == null || instrumentKey.Length == 0)
                return false;

            DateTime suppressUntil;
            if (!flattenAllSuppressionUntilByInstrument.TryGetValue(instrumentKey, out suppressUntil))
                return false;

            if (DateTime.UtcNow > suppressUntil)
            {
                flattenAllSuppressionUntilByInstrument.Remove(instrumentKey);
                return false;
            }

            if (GetLeadPositionQuantity(instrumentKey) == 0)
                leadQuantityByInstrument[instrumentKey] = 0;

            return true;
        }

        private int GetLeadPositionQuantity(string instrumentKey)
        {
            if (leadAccount == null || leadAccount.Account == null || leadAccount.Account.Positions == null)
                return 0;

            Position position = leadAccount.Account.Positions
                .FirstOrDefault(p => p != null && p.Instrument != null && p.Instrument.FullName == instrumentKey);

            if (position == null)
                return 0;

            if (position.MarketPosition == MarketPosition.Long)
                return position.Quantity;

            if (position.MarketPosition == MarketPosition.Short)
                return -position.Quantity;

            return 0;
        }

        private int CalculateLeadDelta(Execution execution, string instrumentKey, int previousQuantity)
        {
            if (execution == null)
                return 0;

            if (execution.Order == null)
                return GetLeadPositionQuantity(instrumentKey) - previousQuantity;

            int filledQuantity = execution.Quantity;

            switch (execution.Order.OrderAction)
            {
                case OrderAction.Buy:
                case OrderAction.BuyToCover:
                    return filledQuantity;
                case OrderAction.Sell:
                case OrderAction.SellShort:
                    return -filledQuantity;
                default:
                    return 0;
            }
        }

        private bool ReplicateExecutionTrade(Order order, Instrument instrument, int deltaQuantity)
        {
            if (order == null || instrument == null)
                return false;

            OrderAction action = order.OrderAction;
            if (action != OrderAction.Buy
                && action != OrderAction.BuyToCover
                && action != OrderAction.Sell
                && action != OrderAction.SellShort)
                return false;

            int quantity = Math.Abs(deltaQuantity);
            if (quantity <= 0)
                return false;

            foreach (AccountSelection follower in followerAccounts.Where(f => f.FollowEnabled))
            {
                if (leadAccount != null && follower.Name == leadAccount.Name)
                    continue;

                SubmitFollowerOrder(follower.Account, instrument, action, quantity);
            }

            return true;
        }

        private static int GetSignedQuantity(MarketPosition marketPosition, int quantity)
        {
            if (marketPosition == MarketPosition.Long)
                return quantity;

            if (marketPosition == MarketPosition.Short)
                return -quantity;

            return 0;
        }

        private void ReplicateDirectionalTrade(Instrument instrument, int deltaQuantity)
        {
            if (deltaQuantity == 0)
                return;

            OrderAction followerAction = deltaQuantity > 0 ? OrderAction.Buy : OrderAction.Sell;
            int quantity = Math.Abs(deltaQuantity);

            foreach (AccountSelection follower in followerAccounts.Where(f => f.FollowEnabled))
            {
                if (leadAccount != null && follower.Name == leadAccount.Name)
                    continue;

                SubmitFollowerOrder(follower.Account, instrument, followerAction, quantity);
            }
        }

        private void FlattenFollowers(Instrument instrument)
        {
            foreach (AccountSelection follower in followerAccounts.Where(f => f.FollowEnabled))
            {
                if (leadAccount != null && follower.Name == leadAccount.Name)
                    continue;

                // Protection wird nur auf Lead gemanagt. Bei Flat im Lead alle Follower flatten.
                follower.Account.Flatten(new[] { instrument });
            }
        }

        public void FlattenAllManagedPositions()
        {
            isFlattenAllInProgress = true;

            try
            {
                HashSet<Account> managedAccounts = new HashSet<Account>();

                if (leadAccount != null && leadAccount.Account != null)
                    managedAccounts.Add(leadAccount.Account);

                foreach (AccountSelection follower in followerAccounts.Where(f => f.FollowEnabled && f.Account != null))
                    managedAccounts.Add(follower.Account);

                foreach (Account account in managedAccounts)
                    FlattenAccountPositions(account);

                DateTime suppressionUntil = DateTime.UtcNow.AddSeconds(2);
                foreach (string instrumentKey in leadQuantityByInstrument.Keys.ToList())
                    flattenAllSuppressionUntilByInstrument[instrumentKey] = suppressionUntil;

                leadQuantityByInstrument.Clear();
            }
            finally
            {
                isFlattenAllInProgress = false;
            }
        }

        private static void FlattenAccountPositions(Account account)
        {
            if (account == null || account.Positions == null)
                return;

            Instrument[] instruments = account.Positions
                .Where(p => p != null && p.Instrument != null && p.MarketPosition != MarketPosition.Flat)
                .Select(p => p.Instrument)
                .Distinct()
                .ToArray();

            if (instruments.Length == 0)
                return;

            account.Flatten(instruments);
        }

        private static void SubmitFollowerOrder(Account account, Instrument instrument, OrderAction action, int quantity)
        {
            if (account == null || instrument == null || quantity <= 0)
                return;

            Order order = account.CreateOrder(
                instrument,
                action,
                OrderType.Market,
                TimeInForce.Day,
                quantity,
                0,
                0,
                string.Empty,
                "TradeCopierFollower",
                null);

            if (order == null)
                return;

            account.Submit(new[] { order });
        }

        public void Dispose()
        {
            Account.AccountStatusUpdate -= AccountStatusUpdate;

            foreach (Account account in executionSubscribedAccounts.ToList())
                account.ExecutionUpdate -= AccountExecutionUpdate;

            foreach (Account account in positionSubscribedAccounts.ToList())
                account.PositionUpdate -= AccountPositionUpdate;

            executionSubscribedAccounts.Clear();
            positionSubscribedAccounts.Clear();
            followerAccounts.CollectionChanged -= FollowerAccountsChanged;

            foreach (AccountSelection selection in availableAccounts)
                selection.PropertyChanged -= AccountSelectionPropertyChanged;
        }

        private void OnPropertyChanged(string propertyName)
        {
            if (PropertyChanged != null)
                PropertyChanged(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
