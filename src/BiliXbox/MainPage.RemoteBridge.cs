using System;
using System.Threading.Tasks;
using BiliXbox.Services;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation.Peers;
using Windows.UI.Xaml.Automation.Provider;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;


namespace BiliXbox
{
    public sealed partial class MainPage
    {
        private RemoteControlBridge _remoteBridge;

        // Test builds only: accept simulated remote keys from the LAN tool.
        private async void StartRemoteBridge()
        {
            if (!AppVersionInfo.IsTestBuild || _remoteBridge != null) return;
            _remoteBridge = new RemoteControlBridge(DispatchBridgeKeyAsync, DispatchBridgeMediaAsync);
            try { await _remoteBridge.StartAsync(); }
            catch (Exception ex)
            {
                PlaybackDiagnosticsLog.Record("remote-bridge start-failed error=" + ex.GetType().Name + " hresult=" + ex.HResult.ToString("X8"));
                _remoteBridge.Dispose(); _remoteBridge = null;
            }
        }

        // Like real CoreWindow key events, a simulated key starts its handler
        // and returns at once. Some handlers run for a long time (Y opens the
        // search page, B may close a view), and waiting for them used to stall
        // the bridge so every later key was ignored until that view closed.
        private async Task DispatchBridgeKeyAsync(string action, VirtualKey key)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                try
                {
                    if (_disposed) return;
                    RecordRemoteKey(key, "simulated-" + action);
                    if (action != "up" && TryDriveOpenDialog(key)) return;
                    if (action != "up") ObserveBridgeTask(SimulateKeyDownAsync(key, action == "repeat"), key.ToString());
                    if (action == "up" || action == "press") DispatchCoreKeyUp(key, _ => { });
                }
                catch (Exception ex) { PlaybackDiagnosticsLog.Record("remote-bridge dispatch-failed key=" + key + " error=" + ex.GetType().Name); }
            });
        }

        // A media button arrives as a MediaPlayer command, the same event the
        // system raises for a real remote's play/pause, skip and seek buttons.
        private async Task DispatchBridgeMediaAsync(string command)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (!_disposed) ObserveBridgeTask(HandleRemoteMediaAsync(command), command);
            });
        }

        private static async void ObserveBridgeTask(Task task, string name)
        {
            try { await task; }
            catch (Exception ex) { PlaybackDiagnosticsLog.Record("remote-bridge dispatch-failed input=" + name + " error=" + ex.GetType().Name); }
        }
        // A content dialog (login, re-authorization, coin, favorite...) owns
        // the screen: real remotes and controllers drive it through the
        // system's own XY focus. Simulated keys bypass that engine, so move
        // focus, press the focused control and close the dialog here.
        private static ContentDialog OpenContentDialog()
        {
            try
            {
                foreach (var popup in VisualTreeHelper.GetOpenPopups(Window.Current))
                    if (popup.Child is ContentDialog dialog) return dialog;
            }
            catch (Exception) { }
            return null;
        }

        private bool TryDriveOpenDialog(VirtualKey key)
        {
            var dialog = OpenContentDialog();
            if (dialog == null) return false;
            FocusNavigationDirection? direction = null;
            if (key == VirtualKey.Up || key == VirtualKey.GamepadDPadUp || key == VirtualKey.NavigationUp || key == VirtualKey.GamepadLeftThumbstickUp) direction = FocusNavigationDirection.Up;
            else if (key == VirtualKey.Down || key == VirtualKey.GamepadDPadDown || key == VirtualKey.NavigationDown || key == VirtualKey.GamepadLeftThumbstickDown) direction = FocusNavigationDirection.Down;
            else if (key == VirtualKey.Left || key == VirtualKey.GamepadDPadLeft || key == VirtualKey.NavigationLeft || key == VirtualKey.GamepadLeftThumbstickLeft) direction = FocusNavigationDirection.Left;
            else if (key == VirtualKey.Right || key == VirtualKey.GamepadDPadRight || key == VirtualKey.NavigationRight || key == VirtualKey.GamepadLeftThumbstickRight) direction = FocusNavigationDirection.Right;
            if (direction.HasValue)
            {
                var options = new FindNextElementOptions { SearchRoot = dialog };
                bool moved = false;
                try { moved = FocusManager.TryMoveFocus(direction.Value, options); } catch (Exception) { }
                if (!moved) try { FocusManager.TryMoveFocus(direction.Value); } catch (Exception) { }
                return true;
            }
            if (key == VirtualKey.Enter || key == VirtualKey.GamepadA || key == VirtualKey.NavigationAccept || key == VirtualKey.Space)
            {
                var focused = FocusManager.GetFocusedElement() as UIElement;
                try
                {
                    var peer = focused == null ? null : FrameworkElementAutomationPeer.FromElement(focused) ?? FrameworkElementAutomationPeer.CreatePeerForElement(focused);
                    if (peer?.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke) invoke.Invoke();
                    else if (peer?.GetPattern(PatternInterface.Toggle) is IToggleProvider toggle) toggle.Toggle();
                }
                catch (Exception) { }
                return true;
            }
            if (key == VirtualKey.GamepadB || key == VirtualKey.Escape || key == VirtualKey.NavigationCancel || key == VirtualKey.GoBack || key == VirtualKey.Back)
            {
                try { dialog.Hide(); } catch (Exception) { }
                return true;
            }
            return false;
        }

        // Gamepad-style and Navigation keys arrive through the CoreWindow path
        // exactly as a real remote's do. Keyboard arrows and Enter normally reach
        // the routed player handler or native XAML focus first; approximate that.
        private async Task SimulateKeyDownAsync(VirtualKey key, bool repeat)
        {
            bool keyboard = key == VirtualKey.Up || key == VirtualKey.Down || key == VirtualKey.Left || key == VirtualKey.Right
                || key == VirtualKey.Enter;
            if (!keyboard || _dialogOpen) { await DispatchCoreKeyDownAsync(key, repeat, _ => { }); return; }
            if (_watching && !_creatorVisible && !_libraryVisible && HandlePlayerKey(key, repeat)) return;
            // Outside the player, keyboard-style keys move the app's own focus
            // the way its native XY navigation would.
            DispatchPlayerGamepadKey(key == VirtualKey.Enter ? VirtualKey.GamepadA : key, repeat);
            if (key == VirtualKey.Enter) _playerInput.ConfirmKeyUp(DateTimeOffset.UtcNow);
        }
    }
}
