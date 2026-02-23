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
        private readonly HashSet<Account> orderSubscribedAccounts;
        private readonly HashSet<string> replicatedLeadOrderKeys;
        private readonly Dictionary<string, List<Order>> followerOrdersByLeadOrderKey;
        private readonly Dictionary<string, int> replicatedEntryLimitOrderCountByInstrument;
        private readonly Dictionary<string, DateTime> suppressPositionReplicationUntilByInstrument;
        private readonly Dictionary<string, DateTime> lastPositionReplicationAtByInstrument;

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
            orderSubscribedAccounts = new HashSet<Account>();
            replicatedLeadOrderKeys = new HashSet<string>(StringComparer.Ordinal);
            followerOrdersByLeadOrderKey = new Dictionary<string, List<Order>>(StringComparer.Ordinal);
            replicatedEntryLimitOrderCountByInstrument = new Dictionary<string, int>(StringComparer.Ordinal);
            suppressPositionReplicationUntilByInstrument = new Dictionary<string, DateTime>(StringComparer.Ordinal);
            lastPositionReplicationAtByInstrument = new Dictionary<string, DateTime>(StringComparer.Ordinal);
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
            SyncOrderSubscriptions(currentAccounts);

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
                SubscribeOrderForAccount(account);
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

        private void SyncOrderSubscriptions(IEnumerable<Account> accounts)
        {
            List<Account> accountList = accounts != null ? accounts.ToList() : new List<Account>();

            foreach (Account account in orderSubscribedAccounts.ToList())
            {
                if (accountList.Contains(account))
                    continue;

                account.OrderUpdate -= AccountOrderUpdate;
                orderSubscribedAccounts.Remove(account);
            }

            foreach (Account account in accountList)
                SubscribeOrderForAccount(account);
        }

        private void SubscribeOrderForAccount(Account account)
        {
            if (account == null || orderSubscribedAccounts.Contains(account))
                return;

            account.OrderUpdate += AccountOrderUpdate;
            orderSubscribedAccounts.Add(account);
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

        private void AccountOrderUpdate(object sender, OrderEventArgs e)
        {
            if (!IsRunning || e == null || e.Order == null || leadAccount == null || isFlattenAllInProgress)
                return;

            Account orderAccount = sender as Account ?? e.Order.Account;
            if (orderAccount == null || orderAccount.Name != leadAccount.Name)
                return;

            if (!IsLeadEntryLimitOrder(e.Order))
                return;

            string orderKey = GetLeadOrderKey(e.Order);
            if (orderKey == null)
                return;

            if (IsOpenOrderState(e.Order.OrderState))
            {
                if (!replicatedLeadOrderKeys.Add(orderKey))
                    return;

                ReplicateLimitEntryOrder(e.Order, orderKey);
                return;
            }

            if (IsCanceledOrderState(e.Order.OrderState))
            {
                CancelReplicatedFollowerOrders(orderKey);
                return;
            }

            if (!IsTerminalOrderState(e.Order.OrderState))
                return;

            RemoveReplicatedOrderTracking(orderKey);
        }

        private void AccountExecutionUpdate(object sender, ExecutionEventArgs e)
        {
            if (e == null || e.Execution == null || leadAccount == null)
                return;

            Account executionAccount = sender as Account ?? e.Execution.Account;
            if (executionAccount == null || executionAccount.Name != leadAccount.Name)
                return;

            OnLeadExecution(e.Execution);
        }

        private void AccountPositionUpdate(object sender, PositionEventArgs e)
        {
            if (e == null || e.Position == null || e.Position.Instrument == null || leadAccount == null)
                return;

            Account positionAccount = sender as Account ?? e.Position.Account;
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

            if (ShouldSuppressPositionReplication(instrumentKey))
                return;

            if (currentQty == 0)
            {
                FlattenFollowers(instrument);
                return;
            }

            lastPositionReplicationAtByInstrument[instrumentKey] = DateTime.UtcNow;
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

            if (execution.Order != null && WasReplicatedLeadEntryLimitOrder(execution.Order))
            {
                int currentLeadQty = GetLeadPositionQuantity(instrumentKey);
                leadQuantityByInstrument[instrumentKey] = currentLeadQty;
                return;
            }

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

        private static bool IsOpenOrderState(OrderState state)
        {
            return state == OrderState.Initialized
                || state == OrderState.Submitted
                || state == OrderState.Accepted
                || state == OrderState.Working
                || state == OrderState.TriggerPending;
        }

        private static bool IsCanceledOrderState(OrderState state)
        {
            return state == OrderState.CancelPending
                || state == OrderState.CancelSubmitted
                || state == OrderState.Cancelled;
        }

        private static bool IsTerminalOrderState(OrderState state)
        {
            return state == OrderState.Cancelled
                || state == OrderState.Filled
                || state == OrderState.Rejected;
        }

        private static bool IsLeadEntryLimitOrder(Order order)
        {
            if (order == null || order.OrderType != OrderType.Limit)
                return false;

            return order.OrderAction == OrderAction.Buy
                || order.OrderAction == OrderAction.Sell
                || order.OrderAction == OrderAction.SellShort;
        }

        private bool WasReplicatedLeadEntryLimitOrder(Order order)
        {
            if (!IsLeadEntryLimitOrder(order))
                return false;

            string orderKey = GetLeadOrderKey(order);
            if (orderKey == null)
                return false;

            return followerOrdersByLeadOrderKey.ContainsKey(orderKey);
        }

        private static string GetLeadOrderKey(Order order)
        {
            if (order == null)
                return null;

            string primary = order.OrderId;
            if (primary == null || primary.Length == 0)
                primary = order.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);

            if (primary == null || primary.Length == 0)
                return null;

            return primary;
        }

        private void ReplicateLimitEntryOrder(Order leadOrder, string leadOrderKey)
        {
            if (leadOrder == null || leadOrder.Instrument == null)
                return;

            int quantity = leadOrder.Quantity;
            if (quantity <= 0)
                return;

            List<Order> followerOrders = new List<Order>();
            foreach (AccountSelection follower in followerAccounts.Where(f => f.FollowEnabled))
            {
                if (leadAccount != null && follower.Name == leadAccount.Name)
                    continue;

                Order followerOrder = SubmitFollowerOrder(
                    follower.Account,
                    leadOrder.Instrument,
                    leadOrder.OrderAction,
                    quantity,
                    OrderType.Limit,
                    leadOrder.LimitPrice,
                    0,
                    leadOrder.TimeInForce);

                if (followerOrder != null)
                    followerOrders.Add(followerOrder);
            }

            if (followerOrders.Count > 0)
            {
                followerOrdersByLeadOrderKey[leadOrderKey] = followerOrders;

                string instrumentKey = leadOrder.Instrument.FullName;
                if (instrumentKey != null && instrumentKey.Length > 0)
                {
                    int activeCount = replicatedEntryLimitOrderCountByInstrument.ContainsKey(instrumentKey)
                        ? replicatedEntryLimitOrderCountByInstrument[instrumentKey]
                        : 0;
                    replicatedEntryLimitOrderCountByInstrument[instrumentKey] = activeCount + 1;
                }
            }
        }

        private void CancelReplicatedFollowerOrders(string leadOrderKey)
        {
            List<Order> followerOrders;
            if (!followerOrdersByLeadOrderKey.TryGetValue(leadOrderKey, out followerOrders))
            {
                replicatedLeadOrderKeys.Remove(leadOrderKey);
                return;
            }

            foreach (Order followerOrder in followerOrders)
            {
                if (followerOrder == null || followerOrder.Account == null)
                    continue;

                if (!IsOpenOrderState(followerOrder.OrderState) && !IsCanceledOrderState(followerOrder.OrderState))
                    continue;

                followerOrder.Account.Cancel(new[] { followerOrder });
            }

            RemoveReplicatedOrderTracking(leadOrderKey);
        }

        private void RemoveReplicatedOrderTracking(string leadOrderKey)
        {
            if (leadOrderKey == null)
                return;

            List<Order> followerOrders;
            if (followerOrdersByLeadOrderKey.TryGetValue(leadOrderKey, out followerOrders) && followerOrders != null)
            {
                string instrumentKey = followerOrders
                    .Where(o => o != null && o.Instrument != null)
                    .Select(o => o.Instrument.FullName)
                    .FirstOrDefault(k => k != null && k.Length > 0);

                if (instrumentKey != null && instrumentKey.Length > 0 && replicatedEntryLimitOrderCountByInstrument.ContainsKey(instrumentKey))
                {
                    int nextCount = replicatedEntryLimitOrderCountByInstrument[instrumentKey] - 1;
                    if (nextCount > 0)
                        replicatedEntryLimitOrderCountByInstrument[instrumentKey] = nextCount;
                    else
                    {
                        replicatedEntryLimitOrderCountByInstrument.Remove(instrumentKey);
                        suppressPositionReplicationUntilByInstrument[instrumentKey] = DateTime.UtcNow.AddSeconds(2);
                    }
                }
            }

            replicatedLeadOrderKeys.Remove(leadOrderKey);
            followerOrdersByLeadOrderKey.Remove(leadOrderKey);
        }

        private bool ShouldSuppressPositionReplication(string instrumentKey)
        {
            if (instrumentKey == null || instrumentKey.Length == 0)
                return false;

            if (replicatedEntryLimitOrderCountByInstrument.ContainsKey(instrumentKey))
                return true;

            DateTime suppressUntil;
            if (!suppressPositionReplicationUntilByInstrument.TryGetValue(instrumentKey, out suppressUntil))
                return false;

            if (DateTime.UtcNow <= suppressUntil)
                return true;

            suppressPositionReplicationUntilByInstrument.Remove(instrumentKey);
            return false;
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

            int currentLeadQuantity = GetLeadPositionQuantity(instrumentKey);
            int liveDelta = currentLeadQuantity - previousQuantity;
            if (liveDelta != 0)
                return liveDelta;

            if (WasRecentlyReplicatedFromPositionUpdate(instrumentKey))
                return 0;

            if (execution.Order == null)
                return 0;

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

        private bool WasRecentlyReplicatedFromPositionUpdate(string instrumentKey)
        {
            if (instrumentKey == null || instrumentKey.Length == 0)
                return false;

            DateTime replicatedAt;
            if (!lastPositionReplicationAtByInstrument.TryGetValue(instrumentKey, out replicatedAt))
                return false;

            if (DateTime.UtcNow - replicatedAt <= TimeSpan.FromSeconds(1))
                return true;

            lastPositionReplicationAtByInstrument.Remove(instrumentKey);
            return false;
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

                SubmitFollowerOrder(follower.Account, instrument, followerAction, quantity, OrderType.Market, 0, 0, TimeInForce.Day);
            }
        }

        private bool ReplicateExecutionTrade(Order leadOrder, Instrument instrument, int deltaQuantity)
        {
            if (leadOrder == null || instrument == null || deltaQuantity == 0)
                return false;

            OrderAction leadAction = leadOrder.OrderAction;
            switch (leadAction)
            {
                case OrderAction.Buy:
                case OrderAction.BuyToCover:
                case OrderAction.Sell:
                case OrderAction.SellShort:
                    break;
                default:
                    return false;
            }

            int quantity = Math.Abs(deltaQuantity);
            foreach (AccountSelection follower in followerAccounts.Where(f => f.FollowEnabled))
            {
                if (leadAccount != null && follower.Name == leadAccount.Name)
                    continue;

                SubmitFollowerOrder(follower.Account, instrument, leadAction, quantity, OrderType.Market, 0, 0, TimeInForce.Day);
            }

            return true;
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

        private static Order SubmitFollowerOrder(
            Account account,
            Instrument instrument,
            OrderAction action,
            int quantity,
            OrderType orderType,
            double limitPrice,
            double stopPrice,
            TimeInForce timeInForce)
        {
            if (account == null || instrument == null || quantity <= 0)
                return null;

            Order order = account.CreateOrder(
                instrument,
                action,
                orderType,
                timeInForce,
                quantity,
                limitPrice,
                stopPrice,
                string.Empty,
                "TradeCopierFollower",
                null);

            if (order == null)
                return null;

            account.Submit(new[] { order });
            return order;
        }

        public void Dispose()
        {
            Account.AccountStatusUpdate -= AccountStatusUpdate;

            foreach (Account account in executionSubscribedAccounts.ToList())
                account.ExecutionUpdate -= AccountExecutionUpdate;

            foreach (Account account in positionSubscribedAccounts.ToList())
                account.PositionUpdate -= AccountPositionUpdate;

            foreach (Account account in orderSubscribedAccounts.ToList())
                account.OrderUpdate -= AccountOrderUpdate;

            executionSubscribedAccounts.Clear();
            positionSubscribedAccounts.Clear();
            orderSubscribedAccounts.Clear();
            replicatedLeadOrderKeys.Clear();
            followerOrdersByLeadOrderKey.Clear();
            replicatedEntryLimitOrderCountByInstrument.Clear();
            suppressPositionReplicationUntilByInstrument.Clear();
            lastPositionReplicationAtByInstrument.Clear();
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
