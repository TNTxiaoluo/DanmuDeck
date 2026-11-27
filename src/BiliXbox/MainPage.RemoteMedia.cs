using System;
using System.Linq;
using System.Threading.Tasks;
using BiliXbox.Services;
using Windows.Media.Playback;
using Windows.System;
using Windows.UI.Core;

namespace BiliXbox
{
    public sealed partial class MainPage
    {
        // Media remotes (Xbox media remote, HDMI-CEC TV remotes) deliver their
        // transport buttons through the system media controls, which reach the
        // app as MediaPlayer command events; some remotes send them as keys.
        // Both routes, and the test-build remote simulator, end here.
        private void InitializeRemoteMedia()
        {
            var commands = _media.CommandManager;
            commands.NextBehavior.EnablingRule = MediaCommandEnablingRule.Always;
            commands.PreviousBehavior.EnablingRule = MediaCommandEnablingRule.Always;
            commands.FastForwardBehavior.EnablingRule = MediaCommandEnablingRule.Always;
            commands.RewindBehavior.EnablingRule = MediaCommandEnablingRule.Always;
            commands.NextReceived += (s, e) => { e.Handled = true; QueueRemoteMedia("Next"); };
            commands.PreviousReceived += (s, e) => { e.Handled = true; QueueRemoteMedia("Previous"); };
            commands.FastForwardReceived += (s, e) => { e.Handled = true; QueueRemoteMedia("FastForward"); };
            commands.RewindReceived += (s, e) => { e.Handled = true; QueueRemoteMedia("Rewind"); };
        }

        private void QueueRemoteMedia(string command)
            => _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () => await HandleRemoteMediaAsync(command));

        private bool RemoteMediaSurface => _watching && !_creatorVisible && !_libraryVisible && !_disposed && _media.Source != null;

        internal async Task<bool> HandleRemoteMediaAsync(string command)
        {
            PlaybackDiagnosticsLog.Record("remote-media command=" + command + " watching=" + _watching);
            if (!RemoteMediaSurface) return false;
            switch (command)
            {
                case "PlayPause":
                    if (_wantsPlayback) PauseFromRemote(); else await PlayFromRemoteAsync();
                    break;
                case "Play": await PlayFromRemoteAsync(); break;
                case "Pause": case "Stop": PauseFromRemote(); break;
                case "FastForward": ShowControls(); Skip(10); break;
                case "Rewind": ShowControls(); Skip(-10); break;
                case "Next": await StepPartAsync(1); break;
                case "Previous": await StepPartAsync(-1); break;
                default: return false;
            }
            UpdatePlaybackUi();
            return true;
        }

        private void PauseFromRemote()
        {
            _wantsPlayback = _resumeAfterOpen = false;
            _media.Pause();
            ResetPlaybackBuffering();
            ShowControls();
        }

        private async Task PlayFromRemoteAsync()
        {
            _wantsPlayback = _resumeAfterOpen = true;
            ShowControls();
            if (_inBackground) _media.Play(); else await PlayWithDisplayAsync();
        }

        // Previous/next part of a multi-part video, or episode of a series.
        private async Task StepPartAsync(int step)
        {
            if (_playingEpisodeItem != null && _episodeChoices.Count > 0)
            {
                int index = _episodeChoices.ToList().FindIndex(item => item.EpisodeId == _playingEpisodeItem.EpisodeId);
                int target = index + step;
                if (index >= 0 && target >= 0 && target < _episodeChoices.Count) { await PlaySelectedEpisodeAsync(_episodeChoices[target]); return; }
            }
            else if (_video != null && _video.Parts.Count > 1)
            {
                int index = _video.Parts.ToList().FindIndex(item => item.Cid == _currentCid);
                int target = index + step;
                if (index >= 0 && target >= 0 && target < _video.Parts.Count) { await LoadPartAsync(_video.Parts[target].Cid); return; }
            }
            SetStatus(step > 0 ? "已经是最后一个分 P / 剧集" : "已经是第一个分 P / 剧集");
        }

        // Remote keys that arrive as plain key events instead of media commands.
        private const VirtualKey VolumeMuteKey = (VirtualKey)173, VolumeDownKey = (VirtualKey)174, VolumeUpKey = (VirtualKey)175;
        private const VirtualKey MediaNextKey = (VirtualKey)176, MediaPreviousKey = (VirtualKey)177, MediaStopKey = (VirtualKey)178, MediaPlayPauseKey = (VirtualKey)179;

        private async Task<bool> HandleRemoteMediaKeyAsync(VirtualKey key, bool repeat)
        {
            string command = key == MediaPlayPauseKey ? "PlayPause" : key == MediaStopKey ? "Stop"
                : key == MediaNextKey ? "Next" : key == MediaPreviousKey ? "Previous" : null;
            if (command != null) { if (!repeat) await HandleRemoteMediaAsync(command); return true; }
            if (key == VirtualKey.PageUp || key == VirtualKey.PageDown)
            {
                // Channel up/down: previous/next part while watching.
                if (!RemoteMediaSurface) return false;
                if (!repeat) await StepPartAsync(key == VirtualKey.PageDown ? 1 : -1);
                return true;
            }
            if (key != VolumeUpKey && key != VolumeDownKey && key != VolumeMuteKey) return false;
            if (!_watching || _disposed) return false;
            if (key == VolumeMuteKey) { if (!repeat) _media.IsMuted = !_media.IsMuted; }
            else
            {
                _media.IsMuted = false;
                VolumeSlider.Value = Math.Max(0, Math.Min(100, VolumeSlider.Value + (key == VolumeUpKey ? 5 : -5)));
            }
            ShowControls();
            UpdateVolumeLabel();
            return true;
        }
    }
}
