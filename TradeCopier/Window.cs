#region Using declarations
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Linq;
using NinjaTrader.Gui.Tools;
#endregion

namespace NinjaTrader.Custom.AddOns.TradeCopier
{
    public class TradeCopierWindow : NTWindow
    {
        private readonly TradeCopierEngine engine;

        public TradeCopierWindow(TradeCopierEngine engine)
        {
            this.engine = engine;
            DataContext = engine;

            Caption = "Trade Copier";
            Width = 440;
            Height = 480;

            Content = BuildLayout();
        }

        private UIElement BuildLayout()
        {
            Grid grid = new Grid();
            grid.Margin = new Thickness(12);

            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition());
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBlock leadLabel = new TextBlock
            {
                Text = "Lead Konto",
                Margin = new Thickness(0, 0, 0, 4)
            };
            Grid.SetRow(leadLabel, 0);
            grid.Children.Add(leadLabel);

            ComboBox leadCombo = new ComboBox
            {
                DisplayMemberPath = "Name",
                Margin = new Thickness(0, 20, 0, 12)
            };
            leadCombo.SetBinding(ItemsControl.ItemsSourceProperty, new Binding("AvailableAccounts"));
            leadCombo.SetBinding(Selector.SelectedItemProperty, new Binding("LeadAccount") { Mode = BindingMode.TwoWay });
            Grid.SetRow(leadCombo, 1);
            grid.Children.Add(leadCombo);

            GroupBox followerBox = new GroupBox { Header = "Follower Konten" };
            Grid.SetRow(followerBox, 2);

            ListBox followerList = new ListBox
            {
                Margin = new Thickness(8),
                SelectionMode = SelectionMode.Multiple
            };
            followerList.SetBinding(ItemsControl.ItemsSourceProperty, new Binding("AvailableAccounts"));
            followerList.ItemTemplate = BuildFollowerTemplate();
            followerList.SelectionChanged += delegate
            {
                foreach (AccountSelection removed in followerList.SelectedItems.Cast<object>().Select(i => i as AccountSelection).Where(a => a != null))
                    removed.FollowEnabled = true;

                foreach (AccountSelection account in engine.AvailableAccounts.Where(a => !followerList.SelectedItems.Contains(a) && a.FollowEnabled))
                    account.FollowEnabled = false;
            };

            followerBox.Content = followerList;
            grid.Children.Add(followerBox);

            StackPanel footer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };

            Button refreshButton = new Button
            {
                Content = "Konten synchronisieren",
                Margin = new Thickness(0, 0, 8, 0),
                MinWidth = 140
            };
            refreshButton.Click += delegate { engine.SyncAccountsWithControlCenter(); };

            CheckBox runCheckbox = new CheckBox
            {
                Content = "Copier aktiv",
                VerticalAlignment = VerticalAlignment.Center
            };
            runCheckbox.SetBinding(ToggleButton.IsCheckedProperty, new Binding("IsRunning") { Mode = BindingMode.TwoWay });

            footer.Children.Add(refreshButton);
            footer.Children.Add(runCheckbox);

            Grid.SetRow(footer, 3);
            grid.Children.Add(footer);

            return grid;
        }

        private static DataTemplate BuildFollowerTemplate()
        {
            FrameworkElementFactory check = new FrameworkElementFactory(typeof(CheckBox));
            check.SetBinding(ContentControl.ContentProperty, new Binding("Name"));
            check.SetBinding(ToggleButton.IsCheckedProperty, new Binding("FollowEnabled") { Mode = BindingMode.TwoWay });

            DataTemplate template = new DataTemplate { VisualTree = check };
            return template;
        }
    }
}
