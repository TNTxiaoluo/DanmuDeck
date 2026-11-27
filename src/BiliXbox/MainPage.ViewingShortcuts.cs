using System;
using BiliXbox.Controls;
using Windows.Gaming.Input;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
namespace BiliXbox
{
    public sealed partial class MainPage
    {
        private bool _virtualLb, _rawLb, _lbChordUsed, _accountChordUsed;
        private readonly ShoulderChordState _viewingChord = new ShoulderChordState();
        private DateTimeOffset _lastViewingChord = DateTimeOffset.MinValue;
        private DateTimeOffset _lastRemoteSeek = DateTimeOffset.MinValue;
        private bool _remoteControlsSelected;
        private bool _replyShortcutBusy;
        private DateTimeOffset _lastReplyShortcut = DateTimeOffset.MinValue;
        private bool IsCommentReplySurface => _libraryVisible ? AccountLibrary.IsActivityDiscussionOpen
            : _creatorVisible ? CreatorHub.IsDiscussionOpen
            : _watching && _playerUi.Drawer == Controls.PlayerDrawer.Comments;
        private async void RunTargetedCommentReply()
        {
            var now = DateTimeOffset.UtcNow;
            if (_replyShortcutBusy || _disposed || _dialogOpen || _inputPaneVisible
                || !IsCommentReplySurface || (now - _lastReplyShortcut).TotalMilliseconds < 450) return;
            _lastReplyShortcut = now; _replyShortcutBusy = true;
            try
            {
                if (_libraryVisible) await AccountLibrary.WriteActivityCommentAsync(true);
                else if (_creatorVisible) await CreatorHub.WriteCommentAsync(true);
                else TryOpenCommentComposer(true);
            }
            catch (Exception ex) { SetStatus("回复输入未能打开：" + Friendly(ex)); }
            finally { _replyShortcutBusy = false; }
        }
        private int _filmCatalogType = 2;
        private string _lastUiDisplayDiagnostic;
        private void RecordUiDisplayDiagnostic()
        {
            try
            {
                var bounds = Window.Current.Bounds;
                var display = Windows.Graphics.Display.DisplayInformation.GetForCurrentView();
                double scale = display.RawPixelsPerViewPixel;
                string value = "ui-display logicalView=" + bounds.Width.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                    + "x" + bounds.Height.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                    + " pixelScale=" + scale.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                    + " viewPixelsEstimate=" + Math.Round(bounds.Width * scale) + "x" + Math.Round(bounds.Height * scale)
                    + " layoutUnits=" + Math.Round(Root.ActualWidth) + "x" + Math.Round(Root.ActualHeight);
                if (value == _lastUiDisplayDiagnostic) return;
                _lastUiDisplayDiagnostic = value;
                BiliXbox.Services.PlaybackDiagnosticsLog.Record(value);
            }
            catch (Exception) { /* Optional display telemetry must not block startup. */ }
        }
        private void PageViewport_SizeChanged(object sender, SizeChangedEventArgs args)
        {
            if (Root == null || args.NewSize.Width <= 0 || args.NewSize.Height <= 0) return;
            // Use full-size XAML layout when the viewport can hold the TV
            // controls. The Viewbox only scales smaller effective viewports.
            double scale = Math.Max(1, Math.Min(args.NewSize.Width / 1280, args.NewSize.Height / 720));
            Root.Width = Math.Round(1280 * scale);
            Root.Height = Math.Round(720 * scale);
            RecordUiDisplayDiagnostic();
        }
        private async void FilmGenre_Click(object sender, RoutedEventArgs args)
        {
            if (IgnorePlayerClick() || _dialogOpen || _watching || _feedNavigation.IsSearch || _feedNavigation.Section != HomeSection.Film) return;
            int[] types = { 2, 5, 3, 7 }; string[] labels = { "电影", "电视剧", "纪录片", "综艺" };
            int index = (Array.IndexOf(types, _filmCatalogType) + 1) % types.Length;
            _filmCatalogType = types[index]; FilmGenreButton.Content = "类型：" + labels[index];
            _feedNavigation.SwitchSection(HomeSection.Film); InvalidateHomeFeedSection();
            try { await LoadFeedAsync(); } catch (Exception ex) { SetStatus("影视加载失败：" + Friendly(ex)); }
        }
        private bool IsRemoteDirection(VirtualKey key) => key == VirtualKey.Left || key == VirtualKey.Right || key == VirtualKey.Up || key == VirtualKey.Down
            || key == VirtualKey.NavigationLeft || key == VirtualKey.NavigationRight || key == VirtualKey.NavigationUp || key == VirtualKey.NavigationDown;
        // Media remotes and HDMI-CEC TV remotes can arrive as mapped gamepad
        // D-pad keys without any Windows.Gaming.Input device. These keys are
        // only remote candidates here; IsRemoteDirection stays narrow so the
        // controller's own mapped aliases are still swallowed elsewhere.
        private static bool IsRemoteSeekKey(VirtualKey key, out bool horizontal, out bool backward)
        {
            backward = key == VirtualKey.Left || key == VirtualKey.NavigationLeft || key == VirtualKey.GamepadDPadLeft;
            horizontal = backward || key == VirtualKey.Right || key == VirtualKey.NavigationRight || key == VirtualKey.GamepadDPadRight;
            return horizontal || key == VirtualKey.Up || key == VirtualKey.Down || key == VirtualKey.NavigationUp || key == VirtualKey.NavigationDown
                || key == VirtualKey.GamepadDPadUp || key == VirtualKey.GamepadDPadDown;
        }
        private TimeSpan _lastRawGamepadDirectionAt = TimeSpan.MinValue, _lastRawGamepadConfirmAt = TimeSpan.MinValue;

        // While a controller is paired, the raw sampler owns its D-pad and A, and
        // the platform's mapped GamepadDPad*/GamepadA aliases of those presses are
        // swallowed. HDMI-CEC and media remotes send the very same key names
        // without any Windows.Gaming.Input device, so a mapped key is a remote's
        // unless the physical controller is holding (or just pressed) that input.
        private bool IsRemoteAlias(VirtualKey key)
        {
            bool confirm = key == VirtualKey.GamepadA || key == VirtualKey.NavigationAccept;
            bool direction = key == VirtualKey.GamepadDPadUp || key == VirtualKey.GamepadDPadDown
                || key == VirtualKey.GamepadDPadLeft || key == VirtualKey.GamepadDPadRight;
            if (!confirm && !direction) return false;
            var recent = confirm ? _lastRawGamepadConfirmAt : _lastRawGamepadDirectionAt;
            if (recent != TimeSpan.MinValue && _gamepadClock.Elapsed - recent < TimeSpan.FromMilliseconds(300)) return false;
            try
            {
                if (_activeGamepad != null)
                {
                    var reading = _activeGamepad.GetCurrentReading();
                    if (confirm && (reading.Buttons & GamepadButtons.A) != 0) return false;
                    if (direction && ((reading.Buttons & (GamepadButtons.DPadUp | GamepadButtons.DPadDown | GamepadButtons.DPadLeft | GamepadButtons.DPadRight)) != 0
                        || Math.Abs(reading.LeftThumbstickX) > .35 || Math.Abs(reading.LeftThumbstickY) > .35)) return false;
                }
            }
            catch (Exception) { }
            return true;
        }
        private readonly System.Collections.Generic.HashSet<string> _loggedRemoteKeys = new System.Collections.Generic.HashSet<string>();
        private void RecordRemoteKey(VirtualKey key, string outcome)
        {
            string entry = key + ":" + outcome;
            if (_loggedRemoteKeys.Count > 64 || !_loggedRemoteKeys.Add(entry)) return;
            BiliXbox.Services.PlaybackDiagnosticsLog.Record("remote-key key=" + key + " outcome=" + outcome
                + " gamepads=" + _gamepadCount + " mode=" + _playerInput.Mode + " row=" + _playerInput.Row);
        }
        private bool TryRemoteSeek(VirtualKey key)
        {
            if (_pausedRecommendationsVisible) return false;
            if (_adjustingVolume) return false;
            bool horizontal, backward;
            if (!IsRemoteSeekKey(key, out horizontal, out backward) || !_watching || _creatorVisible || _libraryVisible || _liveVisible || _playerUi.Drawer != PlayerDrawer.None
                || _dialogOpen || _inputPaneVisible || IsNativeGamepadEditorActive) return false;
            if (!horizontal) { _remoteControlsSelected = true; return false; }
            // A controller may remain paired while an infrared remote is used. Only suppress a real physical direction currently held,
            // or one the raw sampler has just reported (a short tap can be released before its mapped key event is handled).
            if (_lastRawGamepadDirectionAt != TimeSpan.MinValue && _gamepadClock.Elapsed - _lastRawGamepadDirectionAt < TimeSpan.FromMilliseconds(300))
            { RecordRemoteKey(key, "controller-recent"); return false; }
            try { if (_activeGamepad != null) { var reading = _activeGamepad.GetCurrentReading();
                if ((reading.Buttons & (GamepadButtons.DPadLeft | GamepadButtons.DPadRight)) != 0 || Math.Abs(reading.LeftThumbstickX) > .35)
                { RecordRemoteKey(key, "controller-held"); return false; } } } catch (Exception) { }
            // Controls hidden (playback mode): left/right seek at once. Once the
            // viewer has entered the control rows (OK, up or down), left/right
            // move between buttons such as quality, subtitles and danmaku
            // settings; only the progress-bar row keeps seeking. Hiding the
            // controls returns to playback mode.
            if (_playerInput.Mode == PlayerInputMode.Playback) _remoteControlsSelected = false;
            if ((_remoteControlsSelected || _playerInput.Mode == PlayerInputMode.Controls) && _playerInput.Row != PlayerFocusRow.Seek)
            { RecordRemoteKey(key, "controls-navigation"); return false; }
            RecordRemoteKey(key, "seek");
            var now = DateTimeOffset.UtcNow;
            if ((now - _lastRemoteSeek).TotalMilliseconds >= 200)
            { _lastRemoteSeek = now; ShowControls(); Skip(backward ? -10 : 10); }
            return true;
        }
        private bool HandleViewingChord(VirtualKey key, bool repeat)
        {
            if (_disposed || _dialogOpen || _inputPaneVisible || IsNativeGamepadEditorActive) return false;
            if (key == VirtualKey.GamepadLeftShoulder)
            {
                if (!repeat) { _virtualLb = true; if (!_rawLb) _lbChordUsed = _accountChordUsed = false; }
                return true;
            }
            bool lb = _virtualLb || _rawLb;
            try { if (_activeGamepad != null) lb |= (_activeGamepad.GetCurrentReading().Buttons & GamepadButtons.LeftShoulder) != 0; } catch (Exception) { }
            if (lb && key == VirtualKey.GamepadY)
            {
                _lbChordUsed = true;
                if (!repeat) RunAccountChord();
                return true;
            }
            bool targetX = key == VirtualKey.GamepadX && IsCommentReplySurface;
            bool refreshX = key == VirtualKey.GamepadX && IsSectionBrowseSurface;
            if (!lb || (!refreshX && !targetX)) return false;
            _lbChordUsed = true;
            if (!repeat) { if (targetX) RunTargetedCommentReply(); else RunViewingChord(true); }
            return true;
        }
        private void ReleaseViewingChord(VirtualKey key)
        {
            if (key != VirtualKey.GamepadLeftShoulder) return;
            _virtualLb = false;
            if (!_rawLb) _accountChordUsed = false;
            if (!RawGamepadOwnsNavigation && !_lbChordUsed && !_dialogOpen && !_inputPaneVisible)
            { if (IsSectionBrowseSurface) StepHomeSection(-1); else SeekVideoShoulder(-1); }
        }
        private void PollViewingChord(GamepadReading reading, bool enabled)
        {
            bool lb = enabled && (reading.Buttons & GamepadButtons.LeftShoulder) != 0;
            bool x = (IsSectionBrowseSurface || IsCommentReplySurface) && (reading.Buttons & GamepadButtons.X) != 0;
            bool alreadyUsed = _lbChordUsed;
            bool y = (reading.Buttons & GamepadButtons.Y) != 0;
            var action = _viewingChord.Sample(lb, x || y, enabled);
            // A mapped X tap may occur entirely between raw polling frames.
            // Keep its consumed-LB latch until release, including when a remote
            // provides mapped keys while the raw device sampler is disabled.
            _lbChordUsed = _viewingChord.Consumed || (alreadyUsed && (_virtualLb || _rawLb || lb));
            if (action == ShoulderAction.Refresh)
            {
                if (y) RunAccountChord();
                else if (IsCommentReplySurface) RunTargetedCommentReply();
                else RunViewingChord(true);
            }
            else if (action == ShoulderAction.Previous && !alreadyUsed) { if (IsSectionBrowseSurface) StepHomeSection(-1); else SeekVideoShoulder(-1); }
            _rawLb = lb;
            if (!lb && !_virtualLb) _lbChordUsed = _accountChordUsed = false;
        }
        private async void RunAccountChord()
        {
            if (_accountChordUsed || _disposed || _dialogOpen || _inputPaneVisible) return;
            _accountChordUsed = true;
            try { await OpenAccountAsync(); }
            catch (Exception ex) { if (!_disposed) SetStatus("个人中心未能打开：" + Friendly(ex)); }
        }

        private async void RunViewingChord(bool refresh)
        {
            var now = DateTimeOffset.UtcNow;
            if (_disposed || _dialogOpen || _inputPaneVisible || (now - _lastViewingChord).TotalMilliseconds < 450) return;
            _lastViewingChord = now;
            try
            {
                if (refresh && IsSectionBrowseSurface) await RefreshFeedAsync();
            }
            catch (Exception ex) { SetStatus("快捷操作失败：" + Friendly(ex)); }
        }
        private void Watch_SizeChanged(object sender, SizeChangedEventArgs args) { ApplyCommentLayout(); RecordUiDisplayDiagnostic(); }
        private void RefreshFeed_GotFocus(object sender, RoutedEventArgs args) => RefreshFeedFocus.BorderBrush = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Colors.White);
        private void RefreshFeed_LostFocus(object sender, RoutedEventArgs args) => RefreshFeedFocus.BorderBrush = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Colors.Transparent);
    }
}
