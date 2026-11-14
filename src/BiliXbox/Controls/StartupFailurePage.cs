using System;
using BiliXbox.Services;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace BiliXbox.Controls
{
    // Native controls only: no MainPage, media initialization or custom XAML.
    internal sealed class StartupFailurePage : Page
    {
        public StartupFailurePage(string details, Action retry)
        {
            RequiresPointer = RequiresPointer.Never;
            RequestedTheme = ElementTheme.Dark;
            XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Enabled;
            Background = new SolidColorBrush(Colors.Black);
            var panel = new StackPanel
            {
                MaxWidth = 900, Margin = new Thickness(64), Spacing = 24,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            panel.Children.Add(new TextBlock { Text = "弹幕舱 暂时无法打开首页", FontSize = 32, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock
            {
                Text = "可以按 A 重试。如果仍然失败，请保留下面的错误信息。\n\nv" + AppVersionInfo.DisplayVersion + " · " + details,
                FontSize = 22, TextWrapping = TextWrapping.Wrap
            });
            var button = new Button { Content = "重新打开", FontSize = 24, MinHeight = 64, IsTabStop = true };
            button.Click += (sender, args) => retry();
            panel.Children.Add(button);
            Content = panel;
            Loaded += (sender, args) => button.Focus(FocusState.Keyboard);
        }
    }
}
