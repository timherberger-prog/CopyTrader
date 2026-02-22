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
        private readonly ObservableCollection<AccountSelection> followerAccounts;
        private readonly Dictionary<string, int> leadQuantityByInstrument;
        private readonly HashSet<Account> executionSubscribedAccounts;

        private AccountSelection leadAccount;
        private bool isRunning;

        public event PropertyChangedEventHandler PropertyChanged;

        public TradeCopierEngine()
        {
            availableAccounts = new ObservableCollection<AccountSelection>();
            followerAccounts = new ObservableCollection<AccountSelection>();
            leadQuantityByInstrument = new Dictionary<string, int>();
            executionSubscribedAccounts = new HashSet<Account>();

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

        public AccountSelection LeadAccount
        {
            get { return leadAccount; }
            set
            {
                if (ReferenceEquals(leadAccount, value))
                    return;

                leadAccount = value;
                OnPropertyChanged("LeadAccount");
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
            }
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
        }

        private void SubscribePlatformEvents()
        {
            Account.AccountStatusUpdate += AccountStatusUpdate;

            foreach (Account account in Account.All.Where(IsTradeableAccount))
                SubscribeExecutionForAccount(account);
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

        private void SubscribeExecutionForAccount(Account account)
        {
            if (account == null || executionSubscribedAccounts.Contains(account))
                return;

            account.ExecutionUpdate += AccountExecutionUpdate;
            executionSubscribedAccounts.Add(account);
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

                return;
            }

            if (followerAccounts.Contains(selection))
                followerAccounts.Remove(selection);
        }

        public void OnLeadExecution(Execution execution)
        {
            if (!IsRunning || leadAccount == null)
                return;

            if (execution == null || execution.Instrument == null)
                return;

            string instrumentKey = execution.Instrument.FullName;
            int previousQty = leadQuantityByInstrument.ContainsKey(instrumentKey)
                ? leadQuantityByInstrument[instrumentKey]
                : 0;

            int delta = execution.Order != null ? execution.Order.Filled : 0;
            if (execution.MarketPosition == MarketPosition.Short)
                delta = -delta;

            int currentQty = previousQty + delta;
            leadQuantityByInstrument[instrumentKey] = currentQty;

            if (currentQty == 0)
            {
                FlattenFollowers(execution.Instrument);
                return;
            }

            ReplicateDirectionalTrade(execution, delta);
        }

        private void ReplicateDirectionalTrade(Execution leadExecution, int deltaQuantity)
        {
            if (deltaQuantity == 0)
                return;

            OrderAction followerAction = deltaQuantity > 0 ? OrderAction.Buy : OrderAction.Sell;
            int quantity = Math.Abs(deltaQuantity);

            foreach (AccountSelection follower in followerAccounts.Where(f => f.FollowEnabled))
            {
                if (leadAccount != null && follower.Name == leadAccount.Name)
                    continue;

                SubmitFollowerOrder(follower.Account, leadExecution.Instrument, followerAction, quantity);
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

        private static void SubmitFollowerOrder(Account account, Instrument instrument, OrderAction action, int quantity)
        {
            if (account == null || instrument == null || quantity <= 0)
                return;

            account.CreateOrder(
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
        }

        public void Dispose()
        {
            Account.AccountStatusUpdate -= AccountStatusUpdate;

            foreach (Account account in executionSubscribedAccounts.ToList())
                account.ExecutionUpdate -= AccountExecutionUpdate;

            executionSubscribedAccounts.Clear();
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
