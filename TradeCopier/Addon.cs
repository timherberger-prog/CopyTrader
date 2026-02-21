#region Using declarations
using System.Windows;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
#endregion

namespace NinjaTrader.Custom.AddOns.TradeCopier
{
    public class Addon : AddOnBase
    {
        private TradeCopierEngine engine;
        private TradeCopierWindow window;
        private NTMenuItem menuItem;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "Trade Copier";
                Description = "Kopiert Trades vom Lead-Konto auf Follow-Konten ohne doppelte Protection.";
            }
            else if (State == State.Active)
            {
                engine = new TradeCopierEngine();
                engine.Start();
            }
            else if (State == State.Terminated)
            {
                if (menuItem != null)
                    menuItem.Click -= MenuItemClick;

                if (window != null)
                {
                    window.Close();
                    window = null;
                }

                if (engine != null)
                {
                    engine.Dispose();
                    engine = null;
                }
            }
        }

        protected override void OnWindowCreated(Window createdWindow)
        {
            ControlCenter controlCenter = createdWindow as ControlCenter;
            if (controlCenter == null || menuItem != null)
                return;

            menuItem = new NTMenuItem { Header = "Trade Copier" };
            menuItem.Click += MenuItemClick;
            controlCenter.MainMenu.Add(menuItem);
        }

        private void MenuItemClick(object sender, RoutedEventArgs e)
        {
            OpenWindow();
        }

        private void OpenWindow()
        {
            if (engine == null)
                return;

            if (window != null)
            {
                window.Activate();
                return;
            }

            window = new TradeCopierWindow(engine);
            window.Closed += delegate { window = null; };
            window.Show();
        }
    }
}
