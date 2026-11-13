using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BiliXbox.Controls;
using BiliXbox.Models;
using BiliXbox.Services;
using Windows.UI.Xaml;

namespace BiliXbox
{
    public sealed partial class MainPage
    {
        private readonly PlaybackRecoveryState _playbackRecovery = new PlaybackRecoveryState();
        private readonly PlaybackPipelineRecoveryState _pipelineRecovery = new PlaybackPipelineRecoveryState();
        private int _playbackRouteOffset;
        private bool _switchingPlaybackRoute;
        private double _bufferedPlaybackSeconds = -1;
        private DateTimeOffset _nextTransportSample;

        private static List<string> Mp4PlaybackUrls(PlaybackInfo playback)
            => new[] { playback.Url }.Concat(playback.BackupUrls ?? new string[0])
                .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().ToList();

        private bool HasAlternatePlaybackRoutes => _playback != null &&
            (!string.IsNullOrEmpty(_playback.DashManifest) ? _adaptivePlayback?.HasAlternateRoutes == true
                : Mp4PlaybackUrls(_playback).Count > 1);

        private void ResetPlaybackTransportDisplay()
        {
            _bufferedPlaybackSeconds = -1;
            _nextTransportSample = DateTimeOffset.MinValue;
        }

        private void ResetPlaybackRecovery(bool recovery)
        {
            if (recovery)
            {
                _playbackRecovery.ResetWaiting(DateTimeOffset.UtcNow);
                _pipelineRecovery.ResetWaiting(DateTimeOffset.UtcNow);
            }
            else { _playbackRouteOffset = 0; _playbackRecovery.Reset(); _pipelineRecovery.Reset(); }
        }

        private void UpdatePlaybackTransport(bool waiting) => UpdatePlaybackTransport(waiting, DateTimeOffset.UtcNow);

        private void UpdatePlaybackTransport(bool waiting, DateTimeOffset now)
        {
            var session = _media.PlaybackSession;
            // GetBufferedRanges crosses the native media boundary. Read twice a
            // second, not for every frame or every danmaku update.
            if (now >= _nextTransportSample)
            {
                _nextTransportSample = now.AddMilliseconds(500);
                _bufferedPlaybackSeconds = -1;
                if (_media.Source != null)
                {
                    try
                    {
                        var position = session.Position;
                        _bufferedPlaybackSeconds = 0;
                        foreach (var range in session.GetBufferedRanges())
                            if (range.Start.TotalSeconds <= position.TotalSeconds + 0.05 && range.End > position)
                                _bufferedPlaybackSeconds = Math.Max(_bufferedPlaybackSeconds, (range.End - position).TotalSeconds);
                    }
                    catch (Exception) { _bufferedPlaybackSeconds = -1; }
                }
                var speed = DownloadSpeedMeter.Format(_adaptivePlayback?.CurrentDownloadBytesPerSecond
                    ?? _activeDownloadSpeed?.CurrentDownloadBytesPerSecond ?? -1);
                var cache = _bufferedPlaybackSeconds < 0 ? "等待数据" : _bufferedPlaybackSeconds.ToString("0.0") + " 秒";
                BufferingSpeed.Text = "接收：" + speed + " · 可播缓存：" + cache;
                var required = _playback != null && _playback.VideoBitrate > 0
                    ? "\n此画质平均需要约 " + DownloadSpeedMeter.Format((_playback.VideoBitrate + (double)_playback.AudioBitrate) / 8) : "";
                PlaybackTransportHint.Text = "接收：" + speed + " · 可播缓存：" + cache + required;
            }
            bool active = _watching && _wantsPlayback && _media.Source != null && !_switchingPlaybackRoute
                && _partCts != null && !_partCts.IsCancellationRequested;
            bool blocked = _seeking || _dialogOpen || _inputPaneVisible || _creatorVisible || _libraryVisible;
            // A frozen clock with ample playable data is distinct from network
            // starvation. Rebuild the native source once at the same quality
            // and position; do not rotate CDN addresses for this condition.
            // Tiny clock ticks can briefly clear the spinner even while video
            // advances at slideshow speed. Observe native Playing as well; the
            // policy measures elapsed media time and leaves normal playback alone.
            bool observeClock = waiting || session.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing;
            if (_pipelineRecovery.ShouldRestart(now, session.Position, active && !_sourceLoading,
                observeClock, blocked, _bufferedPlaybackSeconds))
            {
                _ = RecoverPlaybackAsync(true);
                return;
            }
            if (_playbackRecovery.ShouldSwitch(now, session.Position,
                active, waiting, blocked,
                _bufferedPlaybackSeconds, HasAlternatePlaybackRoutes))
                _ = RecoverPlaybackAsync();
        }

        private static async Task PreparePlaybackRoutesAsync(PlaybackInfo playback, System.Threading.CancellationToken token)
        {
            if (!string.IsNullOrEmpty(playback.DashManifest))
            {
                var routes = await Task.WhenAll(PlaybackRouteSelector.SelectAsync(playback.DashVideoUrls, false, token),
                    PlaybackRouteSelector.SelectAsync(playback.DashAudioUrls, true, token));
                playback.DashVideoUrls = routes[0]; playback.DashAudioUrls = routes[1];
            }
            else
            {
                var routes = await PlaybackRouteSelector.SelectAsync(Mp4PlaybackUrls(playback), false, token);
                if (routes.Count > 0) { playback.Url = routes[0]; playback.BackupUrls = routes.Skip(1).ToArray(); }
            }
        }

        private async Task RecoverPlaybackAsync(bool restartPipeline = false)
        {
            if (_switchingPlaybackRoute || !_watching || _disposed || _video == null || _partCts == null
                || !_wantsPlayback || _playback == null || _partCts.IsCancellationRequested
                || (!restartPipeline && !HasAlternatePlaybackRoutes)) return;
            var playback = _playback;
            var watch = _watchCts;
            var part = _partCts;
            _switchingPlaybackRoute = true;
            if (!restartPipeline)
                _playbackRouteOffset = _adaptivePlayback?.NextRouteOffset ?? (_playbackRouteOffset + 1);
            try
            {
                SetStatus((restartPipeline ? "正在恢复播放 · " : "正在重新缓冲 · ") + playback.Quality);
                // Reuse this response's real audio/video alternatives. Fetching
                // a different quality or inventing CDN hostnames changes content.
                await LoadPartAsync(_currentCid, playback.QualityId, true, playback, true);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!_disposed && _watching && watch == _watchCts && _playback == playback
                    && !part.IsCancellationRequested) SetStatus("视频暂时无法继续：" + Friendly(ex));
            }
            finally
            {
                _switchingPlaybackRoute = false;
                _nextTransportSample = DateTimeOffset.MinValue;
            }
        }
    }
}
