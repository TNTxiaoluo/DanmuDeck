using System;
using System.Collections.Generic;
using System.Diagnostics;
using BiliXbox.Models;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace BiliXbox.Controls
{
    /// <summary>Borderless pictures above the comment drawer, with local gamepad input.</summary>
    public sealed class CommentImageViewer : IDisposable
    {
        private readonly CommentImageNavigationState _state = new CommentImageNavigationState();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Popup _popup = new Popup { IsLightDismissEnabled = false };
        private readonly UserControl _root;
        private readonly Grid _layout;
        private readonly ScrollViewer _scroller;
        private readonly Image _image = new Image { Stretch = Stretch.Uniform };
        private readonly TextBlock _title;
        private readonly TextBlock _status;
        private readonly Button _close;
        private readonly Window _window;
        private IReadOnlyList<CommentPicture> _pictures = Array.Empty<CommentPicture>();
        private Control _restoreFocus;
        private BitmapImage _bitmap;
        private bool _disposed;
        private bool _openNotified;
        private int _generation;
        private long _nextKey;
        public bool IsOpen => _state.IsOpen;
        public int CurrentIndex => _state.Index;
        public event EventHandler Closed;

        public CommentImageViewer()
        {
            _window = Window.Current;
            _root = new UserControl
            {
                RequiresPointer = RequiresPointer.Never, RequestedTheme = ElementTheme.Dark,
                XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Enabled,
                IsTabStop = true, UseSystemFocusVisuals = false
            };
            AutomationProperties.SetAutomationId(_root, "CommentImageViewer");
            _layout = new Grid { Background = new SolidColorBrush(Colors.Black) };

            var imageHost = new Grid();
            _scroller = new ScrollViewer
            {
                Content = _image, ZoomMode = ZoomMode.Enabled, MinZoomFactor = 1, MaxZoomFactor = 5,
                HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Enabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, IsTabStop = false
            };
            _status = new TextBlock
            {
                FontSize = 18, Foreground = new SolidColorBrush(Color.FromArgb(210, 242, 242, 247)),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap, MaxWidth = 500
            };
            imageHost.Children.Add(_scroller); imageHost.Children.Add(_status);
            imageHost.SizeChanged += (sender, args) => SizeImage();
            AutomationProperties.SetAutomationId(_status, "CommentImageStatus");
            _layout.Children.Add(imageHost);

            var header = new Grid { ColumnSpacing = 18, Margin = new Thickness(32), VerticalAlignment = VerticalAlignment.Top };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _close = new Button
            {
                Content = new FontIcon { FontFamily = new FontFamily("Segoe MDL2 Assets"), Glyph = "\uE72B", FontSize = 22 },
                Width = 48, Height = 48, Padding = new Thickness(12), CornerRadius = new CornerRadius(24),
                Background = new SolidColorBrush(Color.FromArgb(180, 35, 35, 39))
            };
            AutomationProperties.SetAutomationId(_close, "CommentImageClose");
            AutomationProperties.SetName(_close, "返回评论");
            _close.Click += (sender, args) => Close();
            _title = new TextBlock { FontSize = 17, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetAutomationId(_title, "CommentImagePosition");
            header.Children.Add(_close); Grid.SetColumn(_title, 1); header.Children.Add(_title);
            _layout.Children.Add(header);
            _layout.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(185, 18, 18, 21)),
                CornerRadius = new CornerRadius(20), Padding = new Thickness(18, 10, 18, 10),
                Margin = new Thickness(24), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                Text = "A 放大 / 还原 · LB / RB 换图 · 右摇杆移动 · B 返回评论",
                FontSize = 14, Foreground = new SolidColorBrush(Color.FromArgb(160, 242, 242, 247)),
                TextAlignment = TextAlignment.Center
                }
            });
            _root.Content = _layout;
            _popup.Child = _root;
            _root.PreviewKeyDown += KeyDown;
            _root.PreviewKeyUp += KeyUp;
            _root.SizeChanged += RootSizeChanged;
            _popup.Opened += PopupOpened;
            _popup.Closed += PopupClosed;
        }

        public bool Open(IReadOnlyList<CommentPicture> pictures, int index, Control restoreFocus)
        {
            if (_disposed || _window == null || pictures == null) return false;
            var urls = new List<string>(pictures.Count);
            foreach (var picture in pictures) urls.Add(picture?.ImageUrl ?? "");
            if (!_state.Open(urls, index, _clock.ElapsedMilliseconds)) return false;
            _openNotified = true;
            ++_generation;
            _pictures = pictures; _restoreFocus = restoreFocus;
            if (_window != null) _window.SizeChanged -= WindowSizeChanged;
            _window.SizeChanged += WindowSizeChanged;
            Resize();
            _popup.IsOpen = true;
            ShowImage();
            _root.Focus(FocusState.Keyboard);
            return true;
        }

        // A CoreWindow B event and a routed GamepadB/Escape can be emitted for
        // one press. Retain the reader for a short period after closing a photo.
        public bool RequestBack()
        {
            bool wasOpen = IsOpen;
            bool consumed = _state.ConsumeBack(_clock.ElapsedMilliseconds);
            if (wasOpen) FinishClose(true);
            return consumed;
        }

        public void Close()
        {
            if (!IsOpen && !_popup.IsOpen) return;
            _state.Close(_clock.ElapsedMilliseconds);
            FinishClose(true);
        }

        private void FinishClose(bool restore)
        {
            bool notify = _openNotified;
            _openNotified = false;
            int generation = ++_generation;
            _popup.IsOpen = false;
            if (_window != null) _window.SizeChanged -= WindowSizeChanged;
            ReleaseImage();
            _pictures = Array.Empty<CommentPicture>();
            var target = _restoreFocus; _restoreFocus = null;
            if (restore && target != null && !_disposed)
                RestoreFocusAsync(target, generation);
            if (notify && !_disposed) Closed?.Invoke(this, EventArgs.Empty);
        }

        private async void RestoreFocusAsync(Control target, int generation)
        {
            try
            {
                await _window.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    if (!_disposed && !IsOpen && !_popup.IsOpen && generation == _generation && target.IsLoaded &&
                        target.IsEnabled && target.Visibility == Visibility.Visible)
                        target.Focus(FocusState.Keyboard);
                });
            }
            catch (InvalidOperationException) { }
            catch (System.Runtime.InteropServices.COMException) { }
        }

        private void PopupClosed(object sender, object args)
        {
            if (IsOpen) { _state.Close(_clock.ElapsedMilliseconds); FinishClose(false); }
        }
        private void PopupOpened(object sender, object args)
        {
            if (IsOpen && !_disposed) _root.Focus(FocusState.Keyboard);
        }

        private void WindowSizeChanged(object sender, WindowSizeChangedEventArgs args) => Resize();
        private void RootSizeChanged(object sender, SizeChangedEventArgs args) => SizeImage();
        private void Resize()
        {
            if (_window == null) return;
            _root.Width = Math.Max(1, _window.Bounds.Width);
            _root.Height = Math.Max(1, _window.Bounds.Height);
            SizeImage();
        }
        private void SizeImage()
        {
            _image.Width = Math.Max(1, _scroller.ActualWidth > 0 ? _scroller.ActualWidth : _root.Width);
            _image.Height = Math.Max(1, _scroller.ActualHeight > 0 ? _scroller.ActualHeight : _root.Height);
        }

        private void Move(int direction)
        {
            if (_state.Move(direction))
            {
                ShowImage();
                var focused = FocusManager.GetFocusedElement() as Control;
                if (focused == null || !focused.IsEnabled) _root.Focus(FocusState.Keyboard);
            }
        }
        private void ShowImage()
        {
            ReleaseImage();
            if (!IsOpen) return;
            Uri address;
            if (!CommentImageNavigationState.TryGetAddress(_state.CurrentUrl, out address)) return;
            _title.Text = (_state.Index + 1) + " / " + _state.Count;
            _status.Text = "正在加载图片…"; _status.Visibility = Visibility.Visible;
            _image.Visibility = Visibility.Visible;
            _scroller.ChangeView(0, 0, 1, true);
            var picture = _pictures[_state.Index];
            _bitmap = new BitmapImage();
            // Request the original URL while bounding decoded bitmap memory.
            // Never append a server-side thumbnail suffix to the source URL.
            if (picture.Height > picture.Width && picture.Height > 0) _bitmap.DecodePixelHeight = Math.Min(picture.Height, 4096);
            else _bitmap.DecodePixelWidth = picture.Width > 0 ? Math.Min(picture.Width, 4096) : 2048;
            _bitmap.ImageOpened += ImageOpened;
            _bitmap.ImageFailed += ImageFailed;
            _image.Source = _bitmap;
            AutomationProperties.SetName(_image, "评论图片 " + (_state.Index + 1));
            _bitmap.UriSource = address;
        }
        private void ImageOpened(object sender, RoutedEventArgs args)
        {
            if (!ReferenceEquals(sender, _bitmap) || !IsOpen) return;
            _status.Visibility = Visibility.Collapsed;
        }
        private void ImageFailed(object sender, ExceptionRoutedEventArgs args)
        {
            if (!ReferenceEquals(sender, _bitmap) || !IsOpen) return;
            _image.Visibility = Visibility.Collapsed;
            _status.Text = "图片暂时无法加载，请返回评论后重试。";
            _status.Visibility = Visibility.Visible;
        }
        private void ReleaseImage()
        {
            if (_bitmap != null)
            {
                _bitmap.ImageOpened -= ImageOpened; _bitmap.ImageFailed -= ImageFailed;
                _bitmap.UriSource = null; _bitmap = null;
            }
            _image.Source = null;
        }
        private void ToggleZoom()
        {
            if (_scroller.ZoomFactor > 1.01f) _scroller.ChangeView(0, 0, 1, true);
            else _scroller.ChangeView(null, null, 2, true);
        }

        private void KeyDown(object sender, KeyRoutedEventArgs args)
        {
            if (!IsOpen) return;
            var key = args.OriginalKey;
            if (IsBack(key) || IsBack(args.Key)) { args.Handled = true; if (!args.KeyStatus.WasKeyDown) RequestBack(); return; }
            int move = key == VirtualKey.GamepadLeftShoulder ? -1 : key == VirtualKey.GamepadRightShoulder ? 1 : 0;
            bool rightUp = key == VirtualKey.GamepadRightThumbstickUp;
            bool rightDown = key == VirtualKey.GamepadRightThumbstickDown;
            bool rightLeft = key == VirtualKey.GamepadRightThumbstickLeft;
            bool rightRight = key == VirtualKey.GamepadRightThumbstickRight;
            bool confirm = IsConfirm(key) || IsConfirm(args.Key);
            FocusNavigationDirection direction;
            direction = FocusNavigationDirection.None;
            bool navigate = !rightUp && !rightDown && !rightLeft && !rightRight &&
                (TryDirection(key, out direction) || TryDirection(args.Key, out direction));
            if (!confirm && !navigate && move == 0 && !rightUp && !rightDown && !rightLeft && !rightRight) return;
            args.Handled = true;
            long now = _clock.ElapsedMilliseconds;
            if (confirm && !_state.ConsumeConfirm(now, args.KeyStatus.WasKeyDown)) return;
            if (args.KeyStatus.WasKeyDown && now < _nextKey) return;
            _nextKey = now + (args.KeyStatus.WasKeyDown ? 220 : 550);
            if (navigate)
            {
                // The photograph is the primary surface. Directional input stays
                // in this popup and cannot move the comment list behind it.
                if (direction == FocusNavigationDirection.Up) _close.Focus(FocusState.Keyboard);
                else if (direction == FocusNavigationDirection.Down) _root.Focus(FocusState.Keyboard);
                else Move(direction == FocusNavigationDirection.Left ? -1 : 1);
                return;
            }
            if (move != 0) { Move(move); return; }
            if (rightUp || rightDown || rightLeft || rightRight)
            {
                _scroller.ChangeView(
                    Math.Max(0, Math.Min(_scroller.ScrollableWidth, _scroller.HorizontalOffset + (rightLeft ? -110 : rightRight ? 110 : 0))),
                    Math.Max(0, Math.Min(_scroller.ScrollableHeight, _scroller.VerticalOffset + (rightUp ? -110 : rightDown ? 110 : 0))), null, true);
                return;
            }
            if (args.KeyStatus.WasKeyDown) return;
            var focused = FocusManager.GetFocusedElement() as DependencyObject;
            while (focused != null && !(focused is Button)) focused = VisualTreeHelper.GetParent(focused);
            var button = focused as Button;
            if (button == _close) Close();
            else ToggleZoom();
        }
        private void KeyUp(object sender, KeyRoutedEventArgs args)
        {
            if (IsOpen && (IsConfirm(args.OriginalKey) || IsConfirm(args.Key)))
            {
                _state.ReleaseConfirm();
                args.Handled = true;
            }
        }
        private static bool IsBack(VirtualKey key) => key == VirtualKey.GamepadB || key == VirtualKey.NavigationCancel || key == VirtualKey.Escape;
        private static bool IsConfirm(VirtualKey key) => key == VirtualKey.GamepadA || key == VirtualKey.Enter || key == VirtualKey.Space || key == VirtualKey.NavigationAccept;
        private static bool TryDirection(VirtualKey key, out FocusNavigationDirection direction)
        {
            switch (key)
            {
                case VirtualKey.Left: case VirtualKey.NavigationLeft: case VirtualKey.GamepadDPadLeft: case VirtualKey.GamepadLeftThumbstickLeft:
                    direction = FocusNavigationDirection.Left; return true;
                case VirtualKey.Right: case VirtualKey.NavigationRight: case VirtualKey.GamepadDPadRight: case VirtualKey.GamepadLeftThumbstickRight:
                    direction = FocusNavigationDirection.Right; return true;
                case VirtualKey.Up: case VirtualKey.NavigationUp: case VirtualKey.GamepadDPadUp: case VirtualKey.GamepadLeftThumbstickUp:
                    direction = FocusNavigationDirection.Up; return true;
                case VirtualKey.Down: case VirtualKey.NavigationDown: case VirtualKey.GamepadDPadDown: case VirtualKey.GamepadLeftThumbstickDown:
                    direction = FocusNavigationDirection.Down; return true;
                default: direction = FocusNavigationDirection.None; return false;
            }
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _state.Close(_clock.ElapsedMilliseconds);
            FinishClose(false);
            _root.PreviewKeyDown -= KeyDown; _root.PreviewKeyUp -= KeyUp; _root.SizeChanged -= RootSizeChanged;
            _popup.Opened -= PopupOpened; _popup.Closed -= PopupClosed; _popup.Child = null;
            Closed = null;
        }
    }
}
