#region Using declarations
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
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
            leadCombo.SetBinding(UIElement.IsEnabledProperty,
                new Binding("IsRunning")
                {
                    Converter = new BooleanInvertConverter()
                });
            Grid.SetRow(leadCombo, 1);
            grid.Children.Add(leadCombo);

            GroupBox followerBox = new GroupBox { Header = "Follower Konten" };
            Grid.SetRow(followerBox, 2);

            ListBox followerList = new ListBox
            {
                Margin = new Thickness(8),
                SelectionMode = SelectionMode.Multiple
            };
            followerList.SetBinding(ItemsControl.ItemsSourceProperty, new Binding("SelectableFollowerAccounts"));
            followerList.SetBinding(UIElement.IsEnabledProperty,
                new Binding("IsRunning")
                {
                    Converter = new BooleanInvertConverter()
                });
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

            Border statusBar = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 12, 0, 0)
            };

            TextBlock statusText = new TextBlock
            {
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            statusText.SetBinding(TextBlock.TextProperty, new Binding("CopierStatusText"));
            statusBar.Child = statusText;
            statusBar.SetBinding(Border.BackgroundProperty,
                new Binding("IsRunning")
                {
                    Converter = new BooleanToBrushConverter(Brushes.ForestGreen, Brushes.Firebrick)
                });

            Grid.SetRow(statusBar, 3);
            grid.Children.Add(statusBar);

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

            Button runButton = new Button
            {
                MinWidth = 110,
                VerticalAlignment = VerticalAlignment.Center
            };
            runButton.SetBinding(ContentControl.ContentProperty,
                new Binding("IsRunning")
                {
                    Converter = new BooleanToTextConverter("Copier stoppen", "Copier starten")
                });
            runButton.Click += delegate { engine.ToggleRunning(); };

            Button flattenAllButton = new Button
            {
                Content = "Flatten All",
                MinWidth = 110,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.FromRgb(220, 57, 0)),
                Foreground = Brushes.White
            };
            flattenAllButton.Click += delegate { engine.FlattenAllManagedPositions(); };

            footer.Children.Add(refreshButton);
            footer.Children.Add(flattenAllButton);
            footer.Children.Add(runButton);

            Grid.SetRow(footer, 4);
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


        private class BooleanInvertConverter : IValueConverter
        {
            public object Convert(object value, System.Type targetType, object parameter, System.Globalization.CultureInfo culture)
            {
                return !(value is bool && (bool)value);
            }

            public object ConvertBack(object value, System.Type targetType, object parameter, System.Globalization.CultureInfo culture)
            {
                return Binding.DoNothing;
            }
        }

        private class BooleanToBrushConverter : IValueConverter
        {
            private readonly Brush trueBrush;
            private readonly Brush falseBrush;

            public BooleanToBrushConverter(Brush trueBrush, Brush falseBrush)
            {
                this.trueBrush = trueBrush;
                this.falseBrush = falseBrush;
            }

            public object Convert(object value, System.Type targetType, object parameter, System.Globalization.CultureInfo culture)
            {
                return value is bool && (bool)value ? trueBrush : falseBrush;
            }

            public object ConvertBack(object value, System.Type targetType, object parameter, System.Globalization.CultureInfo culture)
            {
                return Binding.DoNothing;
            }
        }

        private class BooleanToTextConverter : IValueConverter
        {
            private readonly string trueText;
            private readonly string falseText;

            public BooleanToTextConverter(string trueText, string falseText)
            {
                this.trueText = trueText;
                this.falseText = falseText;
            }

            public object Convert(object value, System.Type targetType, object parameter, System.Globalization.CultureInfo culture)
            {
                return value is bool && (bool)value ? trueText : falseText;
            }

            public object ConvertBack(object value, System.Type targetType, object parameter, System.Globalization.CultureInfo culture)
            {
                return Binding.DoNothing;
            }
        }
    }
}
