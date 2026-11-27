using System;
using BiliXbox.Controls;
using BiliXbox.Services;
using Windows.Devices.Input;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace BiliXbox
{
    // USB mouse and keyboard on Xbox. Moving a mouse enters pointer mode: the
    // cursor shows, the controller focus frame hides, and clicks, the wheel
    // and the progress bar work directly. Any controller, remote or keyboard
    // navigation returns to the focus-frame mode.
    public sealed partial class MainPage
    {
        private DateTimeOffset _lastPointerControls = DateTimeOffset.MinValue;
        private readonly CoreCursor _arrowCursor = new CoreCursor(CoreCursorType.Arrow, 0);
        private bool _loggedPointer, _loggedKeyboard;

        private void InitializePointerInput()
        {
            var window = Window.Current.CoreWindow;
            window.PointerMoved += Core_PointerMoved;
            window.PointerPressed += Core_PointerPressed;
        }

        private void Core_PointerMoved(CoreWindow sender, PointerEventArgs args)
        {
            if (_disposed || args.CurrentPoint.PointerDevice.PointerDeviceType != PointerDeviceType.Mouse) return;
            EnterPointerMode();
            if (_watching && !_creatorVisible && !_libraryVisible && !_dialogOpen)
            {
                var now = DateTimeOffset.UtcNow;
                if ((now - _lastPointerControls).TotalMilliseconds >= 250)
                {
                    _lastPointerControls = now;
                    ShowControls();
                    _playerUi.Touch(now);
                }
            }
        }

        // Right click and the mouse "back" side button act like B.
        private async void Core_PointerPressed(CoreWindow sender, PointerEventArgs args)
        {
            if (_disposed || args.CurrentPoint.PointerDevice.PointerDeviceType != PointerDeviceType.Mouse) return;
            EnterPointerMode();
            var properties = args.CurrentPoint.Properties;
            if (!properties.IsRightButtonPressed && !properties.IsXButton1Pressed) return;
            if (_inputPaneVisible || IsNativeGamepadEditorActive) return;
            args.Handled = true;
            if (OwnsTvPanelNavigation) { FinishTvPanel(false); return; }
            if (_dialogOpen) return;
            await HandleBackAsync();
        }

        private void EnterPointerMode()
        {
            if (!_loggedPointer && AppVersionInfo.IsTestBuild) { _loggedPointer = true; PlaybackDiagnosticsLog.Record("pointer-input mouse=detected"); }
            if (_pointerMode && Window.Current.CoreWindow.PointerCursor != null) return;
            _pointerMode = true;
            Window.Current.CoreWindow.PointerCursor = _arrowCursor;
            GamepadFocusOutline.Visibility = Visibility.Collapsed;
        }

        // Controller, remote or keyboard navigation: hide the cursor and show
        // the focus frame again.
        private void ExitPointerMode()
        {
            if (!_pointerMode) return;
            _pointerMode = false;
            DisablePointerCursor();
            UpdateGamepadFocusOutline();
        }

        // While watching with hidden controls the cursor hides as well, and
        // returns on the next mouse movement.
        private void HidePointerWithControls()
        {
            if (_pointerMode) Window.Current.CoreWindow.PointerCursor = null;
        }

        private static bool IsKeyboardOrigin(KeyRoutedEventArgs args)
            => !GamepadKeyRouting.IsNavigationKey(args.OriginalKey) && args.OriginalKey == args.Key
                && (args.Key < VirtualKey.GamepadA || args.Key > VirtualKey.GamepadRightThumbstickLeft)
                && (args.Key < VirtualKey.NavigationView || args.Key > VirtualKey.NavigationCancel);

        private bool IsEditingText()
            => _inputPaneVisible || IsNativeGamepadEditorActive || FocusManager.GetFocusedElement() is TextBox;

        private static bool IsControlDown()
        {
            try { return (CoreWindow.GetForCurrentThread().GetKeyState(VirtualKey.Control) & CoreVirtualKeyStates.Down) != 0; }
            catch (Exception) { return false; }
        }

        // Keyboard keys in the player. Arrows keep the remote behaviour: with
        // the controls hidden left/right seek and up/down change the volume;
        // once Enter or a controls row is entered, arrows move between buttons.
        private bool HandlePlayerKeyboard(KeyRoutedEventArgs args)
        {
            if (!IsKeyboardOrigin(args) || IsEditingText() || _playerUi.Drawer != PlayerDrawer.None || _pausedRecommendationsVisible) return false;
            var key = args.Key;
            bool repeat = args.KeyStatus.WasKeyDown;
            LogKeyboard(key);
            ExitPointerMode();
            bool hidden = _playerInput.Mode == PlayerInputMode.Playback;
            switch (key)
            {
                case VirtualKey.Space:
                    if (!repeat) { ShowControls(); PlayPause_Click(PlayPauseButton, new RoutedEventArgs()); }
                    return true;
                case VirtualKey.Up:
                case VirtualKey.Down:
                    if (!hidden) return false;
                    _media.IsMuted = false;
                    VolumeSlider.Value = Math.Max(0, Math.Min(100, VolumeSlider.Value + (key == VirtualKey.Up ? 5 : -5)));
                    ShowControls(); UpdateVolumeLabel();
                    return true;
                case VirtualKey.Enter:
                    if (!hidden) return false;
                    if (!repeat) { ShowControls(); _playerInput.SelectButton(0, PlayerButtons.Length); FocusPlayerInput(); }
                    return true;
                case VirtualKey.F:
                    if (IsControlDown()) return false;
                    if (!repeat) { DanmakuToggle.IsChecked = DanmakuToggle.IsChecked != true; DanmakuToggle_Click(DanmakuToggle, new RoutedEventArgs()); }
                    return true;
                case VirtualKey.M:
                    if (!repeat) { _media.IsMuted = !_media.IsMuted; ShowControls(); UpdateVolumeLabel(); }
                    return true;
                case VirtualKey.Back:
                    if (!repeat) _ = HandleBackAsync();
                    return true;
                default:
                    return false;
            }
        }

        // Keyboard arrows and Enter outside the player use the same navigation
        // as the controller instead of the platform's own focus movement.
        private bool HandleBrowseKeyboard(KeyRoutedEventArgs args)
        {
            if (!IsKeyboardOrigin(args) || IsEditingText()) return false;
            var key = args.Key;
            bool repeat = args.KeyStatus.WasKeyDown;
            if (key == VirtualKey.Back) { LogKeyboard(key); if (!repeat) _ = HandleBackAsync(); return true; }
            bool direction = key == VirtualKey.Up || key == VirtualKey.Down || key == VirtualKey.Left || key == VirtualKey.Right;
            if (!direction && key != VirtualKey.Enter && key != VirtualKey.Space) return false;
            LogKeyboard(key);
            ExitPointerMode();
            return DispatchPlayerGamepadKey(direction ? key : VirtualKey.GamepadA, repeat);
        }

        // "/" or Ctrl+F opens search from anywhere outside a text field.
        private bool HandleSearchShortcut(VirtualKey key, bool repeat)
        {
            if (IsEditingText() || _dialogOpen) return false;
            bool slash = key == (VirtualKey)191 || key == VirtualKey.Divide;
            if (!slash && !(key == VirtualKey.F && IsControlDown())) return false;
            if (!repeat) _ = ShowSearchInputAsync();
            return true;
        }

        private void LogKeyboard(VirtualKey key)
        {
            if (_loggedKeyboard || !AppVersionInfo.IsTestBuild) return;
            _loggedKeyboard = true;
            PlaybackDiagnosticsLog.Record("keyboard-input first=" + key);
        }
    }
}
