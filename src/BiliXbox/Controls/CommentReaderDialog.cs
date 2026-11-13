using System;
using System.Diagnostics;
using BiliXbox.Models;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace BiliXbox.Controls
{
    /// <summary>Full rich comment content outside the bounded list summary.</summary>
    public sealed class CommentReaderDialog : ContentDialog, IDisposable
    {
        private readonly ScrollViewer _scroller;
        private readonly CommentCard _card;
        private readonly CommentImageViewer _images;
        private readonly TextBlock _reactionNotice;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _nextScroll;
        private VirtualKey _scrollKey;
        private bool _disposed;
        public event EventHandler<CommentVoteRequestedEventArgs> VoteRequested;
        public bool IsImageOpen => _images.IsOpen;

        public CommentReaderDialog(CommentItem item, bool canViewReplies)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            Title = "评论全文";
            AutomationProperties.SetAutomationId(this, "CommentReaderDialog");
            RequestedTheme = ElementTheme.Dark;
            RequiresPointer = RequiresPointer.Never;
            XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Enabled;
            Resources["ContentDialogMaxWidth"] = 760d;
            CloseButtonText = "返回";
            PrimaryButtonText = canViewReplies ? "查看回复" : "";
            DefaultButton = ContentDialogButton.Close;
            _images = new CommentImageViewer();
            _card = new CommentCard { Item = item, ShowThreadButton = false, IsExpanded = true };
            _card.ImageRequested += ImageRequested;
            _card.VoteRequested += CardVoteRequested;
            _scroller = new ScrollViewer
            {
                Content = _card,
                Width = 620, MaxHeight = 420,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollMode = ScrollMode.Disabled,
                IsTabStop = true
            };
            var panel = new StackPanel { Spacing = 10 };
            panel.Children.Add(new TextBlock { Text = "↑ / ↓ 阅读全文 · A 查看图片 / 选择操作 · B 返回", FontSize = 14 });
            panel.Children.Add(_scroller);
            _reactionNotice = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
            AutomationProperties.SetAutomationId(_reactionNotice, "CommentReaderReactionStatus");
            AutomationProperties.SetLiveSetting(_reactionNotice, Windows.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
            panel.Children.Add(_reactionNotice);
            Content = panel;
            Opened += (sender, args) => _scroller.Focus(FocusState.Keyboard);
            _scroller.PreviewKeyDown += Scroll_KeyDown;
            _scroller.PreviewKeyUp += (sender, args) => { _scrollKey = VirtualKey.None; _nextScroll = 0; };
            PreviewKeyDown += Dialog_KeyDown;
            Closing += Dialog_Closing;
            Closed += (sender, args) => Dispose();
        }

        public void SetReactionBusy(bool busy) { if (!_disposed) _card.SetReactionBusy(busy); }
        public void RefreshReaction() { if (!_disposed) _card.RefreshReaction(); }
        public void SetReactionNotice(string message)
        {
            if (_disposed) return;
            _reactionNotice.Text = message ?? "";
            _reactionNotice.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
            AutomationProperties.SetName(_reactionNotice, message ?? "");
        }
        private void CardVoteRequested(object sender, CommentVoteRequestedEventArgs args) => VoteRequested?.Invoke(this, args);
        private void ImageRequested(object sender, CommentImageRequestedEventArgs args)
        {
            if (_disposed || args.Comment == null) return;
            _images.Open(args.Comment.Pictures, args.ImageIndex, FocusManager.GetFocusedElement() as Control);
        }

        public bool RequestBack()
        {
            if (_disposed || _images.RequestBack()) return true;
            Hide();
            return true;
        }
        private void Dialog_KeyDown(object sender, KeyRoutedEventArgs args)
        {
            if (IsBack(args.Key) || IsBack(args.OriginalKey))
            { args.Handled = true; if (!args.KeyStatus.WasKeyDown) RequestBack(); return; }
            if (IsConfirm(args.Key) || IsConfirm(args.OriginalKey))
            {
                var focused = FocusManager.GetFocusedElement() as DependencyObject;
                while (focused != null && !ReferenceEquals(focused, _card) && !(focused is Button))
                    focused = VisualTreeHelper.GetParent(focused);
                var button = focused as Button;
                if (button != null && !args.KeyStatus.WasKeyDown && _card.TryActivate(button)) args.Handled = true;
                else if (button != null && args.KeyStatus.WasKeyDown) args.Handled = true;
            }
        }
        private void Dialog_Closing(ContentDialog sender, ContentDialogClosingEventArgs args)
        {
            if (!_disposed && _images.RequestBack()) args.Cancel = true;
        }
        private static bool IsBack(VirtualKey key) => key == VirtualKey.GamepadB || key == VirtualKey.NavigationCancel || key == VirtualKey.Escape;
        private static bool IsConfirm(VirtualKey key) => key == VirtualKey.GamepadA || key == VirtualKey.NavigationAccept || key == VirtualKey.Enter || key == VirtualKey.Space;

        private void Scroll_KeyDown(object sender, KeyRoutedEventArgs args)
        {
            // A focused picture or reaction is a native button: leave vertical
            // navigation to XY focus instead of swallowing every press as scroll.
            var focused = FocusManager.GetFocusedElement() as DependencyObject;
            while (focused != null && !ReferenceEquals(focused, _scroller))
            {
                if (focused is Button) return;
                focused = VisualTreeHelper.GetParent(focused);
            }
            var key = args.OriginalKey;
            if (key == VirtualKey.None) key = args.Key;
            if (key == VirtualKey.GamepadA || key == VirtualKey.NavigationAccept || key == VirtualKey.Enter || key == VirtualKey.Space)
            {
                args.Handled = true;
                if (!args.KeyStatus.WasKeyDown)
                    (FocusManager.FindFirstFocusableElement(_card) as Control)?.Focus(FocusState.Keyboard);
                return;
            }
            bool up = key == VirtualKey.Up || key == VirtualKey.GamepadDPadUp || key == VirtualKey.NavigationUp ||
                key == VirtualKey.GamepadLeftThumbstickUp;
            bool down = key == VirtualKey.Down || key == VirtualKey.GamepadDPadDown || key == VirtualKey.NavigationDown ||
                key == VirtualKey.GamepadLeftThumbstickDown;
            if (!up && !down) return;
            double maximum = _scroller.ScrollableHeight;
            // The bottom edge hands focus back to ContentDialog's native buttons.
            if (down && _scroller.VerticalOffset >= maximum - 1) return;
            args.Handled = true;
            long now = _clock.ElapsedMilliseconds;
            if (args.KeyStatus.WasKeyDown && key == _scrollKey && now < _nextScroll) return;
            _nextScroll = now + (args.KeyStatus.WasKeyDown ? 220 : 550);
            _scrollKey = key;
            _scroller.ChangeView(null, Math.Max(0, Math.Min(maximum, _scroller.VerticalOffset + (up ? -110 : 110))), null, true);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _images.Dispose();
            _card.ImageRequested -= ImageRequested; _card.VoteRequested -= CardVoteRequested;
            _scroller.PreviewKeyDown -= Scroll_KeyDown;
            PreviewKeyDown -= Dialog_KeyDown; Closing -= Dialog_Closing;
            VoteRequested = null;
        }
    }
}
