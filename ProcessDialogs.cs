using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ProcessesKiller
{
    internal static class ProcessDialogs
    {
        // A scrollable text surface also keeps long commands and large selections readable.
        internal static Window Create(Window owner, string title, string summary, string text, bool confirmation)
        {
            var dialog = new Window
            {
                Title = title, Width = 660, Height = 420, MinWidth = 460, MinHeight = 280,
                FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"), FontSize = 13,
                Background = (Brush)Application.Current.FindResource("BackgroundBrush"),
                WindowStartupLocation = owner.IsVisible ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                ShowInTaskbar = false
            };
            if (owner.IsVisible) dialog.Owner = owner;
            var layout = new Grid { Margin = new Thickness(16) };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.Children.Add(new TextBlock { Text = summary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
            var contents = new TextBox
            {
                Name = "DialogText", Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(12),
                Background = Brushes.White, BorderBrush = (Brush)Application.Current.FindResource("BorderBrush"),
                BorderThickness = new Thickness(1), VerticalContentAlignment = VerticalAlignment.Top
            };
            Grid.SetRow(contents, 1);
            layout.Children.Add(contents);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var cancel = new Button { Content = confirmation ? "取消" : "关闭", IsCancel = true, IsDefault = true, MinWidth = 80 };
            cancel.Style = (Style)Application.Current.FindResource("BaseButtonStyle");
            buttons.Children.Add(cancel);
            if (confirmation)
            {
                var accept = new Button { Content = "结束进程", MinWidth = 100, Margin = new Thickness(8, 0, 0, 0) };
                accept.Style = (Style)Application.Current.FindResource("DangerButtonStyle");
                accept.Click += (sender, args) => dialog.DialogResult = true;
                buttons.Children.Add(accept);
            }
            Grid.SetRow(buttons, 2);
            layout.Children.Add(buttons);
            dialog.Content = layout;
            dialog.Loaded += (sender, args) => cancel.Focus();
            return dialog;
        }
    }
}
