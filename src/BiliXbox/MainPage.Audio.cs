using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Controls;
using BiliXbox.Models;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace BiliXbox
{
    public sealed partial class MainPage
    {
        private PlaybackAudioMode _preferredAudioMode = PlaybackAudioMode.Standard;

        private void UpdateAudioOptions()
        {
            var previous = (FocusManager.GetFocusedElement() as Button)?.Tag as PlaybackAudioMode?;
            AudioOptions.Children.Clear();
            if (_playback == null) return;
            string actual = AudioModeLabel(_playback.AudioMode);
            AudioHint.Text = "当前：" + actual + (string.IsNullOrEmpty(_playback.AudioCodec) ? "" : " · " + _playback.AudioCodec)
                + (_playback.AudioSampleRate > 0 ? " · " + (_playback.AudioSampleRate / 1000d).ToString("0.###", CultureInfo.InvariantCulture) + " kHz" : "")
                + (_playback.AudioBitsPerSample > 0 ? " · " + _playback.AudioBitsPerSample + " bit" : "")
                + (_playback.AudioChannels > 0 ? " · " + _playback.AudioChannels + " 声道" : "")
                + (string.IsNullOrWhiteSpace(_playback.AudioNotice) ? "" : "\n" + _playback.AudioNotice)
                + (_playback.AudioMode != PlaybackAudioMode.Standard ? "\n输出格式由 Xbox 音频设置和连接设备决定。" : "");
            foreach (var option in _playback.AvailableAudioModes)
            {
                string badge = !_api.IsLoggedIn && (option.RequiresLogin || option.RequiresPremium) ? "需要登录"
                    : _api.IsLoggedIn && _api.CurrentUser?.IsPremium != true && option.RequiresPremium ? "需要大会员" : "";
                var button = new Button
                {
                    Style = (Style)Application.Current.Resources["TvRowButtonStyle"],
                    Content = new TextBlock { Text = (_playback.AudioMode == option.Mode ? "✓  " : "") + option.Label
                        + (badge.Length > 0 ? " · " + badge : !option.IsAvailable && !string.IsNullOrWhiteSpace(option.Reason) ? " · " + option.Reason : ""),
                        TextWrapping = TextWrapping.Wrap },
                    Tag = option.Mode, IsEnabled = option.IsAvailable && badge.Length == 0,
                    HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
                    FontSize = 18, Padding = new Thickness(16, 12, 16, 12)
                };
                button.Click += Audio_Click;
                AudioOptions.Children.Add(button);
            }
            if (previous.HasValue && _playerUi.Drawer == PlayerDrawer.Settings && _playerInput.Mode == PlayerInputMode.Controls
                && !_inputPaneVisible && !IsNativeGamepadEditorActive)
            {
                var available = AudioOptions.Children.OfType<Button>().Where(button => button.IsEnabled).ToList();
                var target = available.FirstOrDefault(button => (PlaybackAudioMode)button.Tag == previous.Value)
                    ?? available.FirstOrDefault(button => (PlaybackAudioMode)button.Tag == _playback.AudioMode)
                    ?? available.FirstOrDefault();
                if (target == null || !target.Focus(FocusState.Keyboard)) SettingsCloseButton.Focus(FocusState.Keyboard);
            }
        }

        private async void Audio_Click(object sender, RoutedEventArgs args)
        {
            if (IgnorePlayerClick() || _video == null || _watchCts == null || !(sender is Button button)
                || !(button.Tag is PlaybackAudioMode mode) || _playerUi.Drawer != PlayerDrawer.Settings
                || !_watching || _creatorVisible || _libraryVisible || _dialogOpen || _inputPaneVisible || IsNativeGamepadEditorActive) return;
            var current = _playback;
            var option = current?.AvailableAudioModes.FirstOrDefault(item => item.Mode == mode);
            if (option == null || !option.IsAvailable || (option.RequiresLogin && !_api.IsLoggedIn)
                || (option.RequiresPremium && (!_api.IsLoggedIn || _api.CurrentUser?.IsPremium != true))) return;
            if (current.AudioMode == mode) { Cancel(ref _qualityCts); _preferredAudioMode = mode; CloseDrawer(); return; }
            Cancel(ref _qualityCts);
            var watchOwner = _watchCts;
            var owner = _qualityCts = CancellationTokenSource.CreateLinkedTokenSource(watchOwner.Token);
            var token = owner.Token;
            var video = new VideoDetail { Bvid = _video.Bvid, Aid = _video.Aid, Cid = _currentCid };
            int generation = _partGeneration;
            long accountMid = AudioAccountMid;
            AudioHint.Text = "正在请求 " + option.Label + "，当前视频继续播放…";
            SetBusy(true);
            try
            {
                var received = await GetCurrentPlaybackAsync(video, current.QualityId, token, mode);
                token.ThrowIfCancellationRequested();
                if (_disposed || _qualityCts != owner || generation != _partGeneration || _playback != current
                    || _video?.Aid != video.Aid || _currentCid != video.Cid || _watchCts != watchOwner
                    || !_watching || _creatorVisible || _libraryVisible || _dialogOpen || _inputPaneVisible
                    || IsNativeGamepadEditorActive || _playerUi.Drawer != PlayerDrawer.Settings
                    || AudioAccountMid != accountMid || !AudioOptionAuthorized(option)) return;
                if (received.AudioMode != mode || received.QualityId != current.QualityId)
                {
                    current.AvailableAudioModes = received.AvailableAudioModes;
                    current.AudioNotice = "音频未切换，继续播放当前音轨。" + (string.IsNullOrWhiteSpace(received.AudioNotice)
                        ? received.QualityId != current.QualityId ? "平台返回的画质与当前画质不同。" : "平台未返回可用的所选音轨。" : received.AudioNotice);
                    UpdateAudioOptions(); SetStatus(current.AudioNotice);
                    return;
                }
                _preferredAudioMode = mode;
                CloseDrawer();
                await LoadPartAsync(video.Cid, current.QualityId, true, received);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error)
            {
                if (!_disposed && _qualityCts == owner && generation == _partGeneration && _playback == current
                    && _watchCts == watchOwner && AudioAccountMid == accountMid && AudioOptionAuthorized(option)
                    && _watching && !_creatorVisible && !_libraryVisible && !_dialogOpen && !_inputPaneVisible
                    && !IsNativeGamepadEditorActive && _playerUi.Drawer == PlayerDrawer.Settings)
                { current.AudioNotice = "音频请求失败，当前播放保持不变：" + Friendly(error); UpdateAudioOptions(); SetStatus(current.AudioNotice); }
            }
            finally { if (_qualityCts == owner) Cancel(ref _qualityCts); SetBusy(false); }
        }

        private long AudioAccountMid => _api.IsLoggedIn ? _api.CurrentUser?.Mid ?? 0 : 0;

        private bool AudioOptionAuthorized(PlaybackAudioOption option)
            => (!option.RequiresLogin || _api.IsLoggedIn)
                && (!option.RequiresPremium || _api.IsLoggedIn && _api.CurrentUser?.IsPremium == true);

        private static string AudioModeLabel(PlaybackAudioMode mode)
            => mode == PlaybackAudioMode.HiRes ? "Hi-Res 无损音轨"
                : mode == PlaybackAudioMode.Dolby ? "杜比音轨" : "标准音轨";

        private static string EnhancedAudioFallbackNotice(PlaybackInfo failed, bool opening)
            => AudioModeLabel(failed?.AudioMode ?? PlaybackAudioMode.Standard)
                + (opening ? "无法打开" : "播放失败") + "，正在尝试同画质的标准音轨。";

        private static PlaybackInfo NativePlaybackFallback(PlaybackInfo playback) => playback?.AudioFallback ?? playback?.CodecFallback;
    }
}
