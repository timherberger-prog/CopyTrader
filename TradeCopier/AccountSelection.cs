#region Using declarations
using System.ComponentModel;
using NinjaTrader.Cbi;
#endregion

namespace NinjaTrader.Custom.AddOns.TradeCopier
{
    public class AccountSelection : INotifyPropertyChanged
    {
        private bool followEnabled;

        public event PropertyChangedEventHandler PropertyChanged;

        public AccountSelection(Account account)
        {
            Account = account;
            Name = account != null ? account.Name : string.Empty;
        }

        public Account Account { get; private set; }

        public string Name { get; private set; }

        public bool FollowEnabled
        {
            get { return followEnabled; }
            set
            {
                if (followEnabled == value)
                    return;

                followEnabled = value;
                OnPropertyChanged("FollowEnabled");
            }
        }

        public override string ToString()
        {
            return Name;
        }

        private void OnPropertyChanged(string propertyName)
        {
            if (PropertyChanged != null)
                PropertyChanged(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
