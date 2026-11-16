using System;
using System.Globalization;
using System.Threading.Tasks;
using BiliXbox.Controls;
using BiliXbox.Services;
using Windows.Gaming.Input;
using Windows.Storage;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace BiliXbox
{
    public sealed partial class MainPage
    {
        private readonly PlaybackSpeedState _playbackSpeed = new PlaybackSpeedState();
        private bool _mappedSpeedTrigger;
        private bool CanHoldPlaybackSpeed => !_disposed && _watching && !_creatorVisible && !_libraryVisible && !_liveVisible
            && !_inBackground && !_dialogOpen && !_inputPaneVisible && !_pausedRecommendationsVisible
            && _playerUi.Drawer == PlayerDrawer.None && _wantsPlayback && !_sourceLoading && _media.Source != null;
        private static string RateText(double rate) => rate.ToString("0.0#", CultureInfo.InvariantCulture) + "×";
        private void InitializePlaybackSpeed()
        {
            var saved = ApplicationData.Current.LocalSettings.Values["playbackRate"] as double?;
            if (saved.HasValue) { try { _playbackSpeed.Select(saved.Value); } catch (ArgumentOutOfRangeException) { } }
            UpdatePlaybackSpeedLabel();
        }
        private void UpdatePlaybackSpeedLabel()
        { PlaybackSpeedButton.Content = _playbackSpeed.Temporary ? "RT 2.0×" : RateText(_playbackSpeed.Selected); }
        private void ApplyPlaybackSpeed()
        {
            if (_disposed) return;
            try { if (_media.Source != null) _media.PlaybackSession.PlaybackRate = _playbackSpeed.Effective; }
            catch (Exception ex)
            {
                _playbackSpeed.Cancel();
                try { _media.PlaybackSession.PlaybackRate = _playbackSpeed.Selected; }
                catch (Exception) { _playbackSpeed.Select(1); try { _media.PlaybackSession.PlaybackRate = 1; } catch (Exception) { } }
                PlaybackDiagnosticsLog.Record("playback-rate-failed hresult=0x" + ex.HResult.ToString("X8"));
                SetStatus("播放器暂不支持此次倍速设置，已恢复 " + RateText(_playbackSpeed.Selected));
            }
            UpdatePlaybackSpeedLabel();
        }
        private void ResetTemporaryPlaybackSpeed()
        { _mappedSpeedTrigger = false; _playbackSpeed.Cancel(); ApplyPlaybackSpeed(); }
        private void PollPlaybackSpeed(GamepadReading reading, bool enabled)
        {
            double trigger = enabled ? reading.RightTrigger : _mappedSpeedTrigger ? 1 : 0;
            // Native dialogs own navigation, but a zeroed navigation sample is
            // not proof that a physically held RT has been released.
            if (!enabled && _activeGamepad != null)
            {
                try { trigger = _activeGamepad.GetCurrentReading().RightTrigger; }
                catch (Exception) { ResetTemporaryPlaybackSpeed(); return; }
            }
            if (_playbackSpeed.Sample(trigger, CanHoldPlaybackSpeed && _gamepadWindowActive && _gamepadWindowVisible)) ApplyPlaybackSpeed();
        }
        private bool HandlePlaybackSpeedKey(VirtualKey key, bool down)
        {
            if (key != VirtualKey.GamepadRightTrigger) return false;
            bool eligible = CanHoldPlaybackSpeed;
            if (down && !eligible) return false;
            if (!RawGamepadOwnsNavigation)
            {
                _mappedSpeedTrigger = down;
                if (_playbackSpeed.Sample(down ? 1 : 0, CanHoldPlaybackSpeed)) ApplyPlaybackSpeed();
            }
            return eligible;
        }
        private async void PlaybackSpeed_Click(object sender, RoutedEventArgs args)
        { if (!IgnorePlayerClick()) await ShowPlaybackSpeedAsync(); }
        private async Task ShowPlaybackSpeedAsync()
        {
            if (_disposed || _dialogOpen || !_watching) return;
            ResetTemporaryPlaybackSpeed();
            var rows = new StackPanel { Spacing = 6 }; Button selected = null;
            var dialog = new ContentDialog { Title = "播放倍速", Content = rows, CloseButtonText = "返回",
                RequestedTheme = ElementTheme.Dark, DefaultButton = ContentDialogButton.Close, RequiresPointer = RequiresPointer.Never };
            foreach (var rate in PlaybackSpeedState.Rates)
            {
                var button = new Button { Content = (rate == _playbackSpeed.Selected ? "✓  " : "") + RateText(rate),
                    HorizontalAlignment = HorizontalAlignment.Stretch, Style = (Style)Application.Current.Resources["TvRowButtonStyle"] };
                button.Click += (s, e) => {
                    _playbackSpeed.Select(rate); ApplyPlaybackSpeed();
                    ApplicationData.Current.LocalSettings.Values["playbackRate"] = _playbackSpeed.Selected;
                    dialog.Hide();
                };
                rows.Children.Add(button); if (rate == _playbackSpeed.Selected) selected = button;
            }
            dialog.Opened += async (s, e) => await Dispatcher.RunAsync(CoreDispatcherPriority.Low, () => selected?.Focus(FocusState.Keyboard));
            dialog.Closed += (s, e) => _lastBack = DateTimeOffset.UtcNow;
            _dialogOpen = true; _activeGeneralDialog = dialog; DisablePointerCursor();
            try { await dialog.ShowAsync(); }
            catch (Exception ex) { SetStatus("倍速选项未能打开：" + Friendly(ex)); }
            finally { _activeGeneralDialog = null; _dialogOpen = false; if (!_disposed) { ShowControls(); FocusGamepadControl(PlaybackSpeedButton); } }
        }
    }
}
