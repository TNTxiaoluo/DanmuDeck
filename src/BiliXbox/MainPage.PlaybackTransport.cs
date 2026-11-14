using System;
using System.Collections.Generic;
using System.Globalization;
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
        private double _bufferSamplePosition;
        private readonly PlaybackBufferingPolicy _bufferingPolicy = new PlaybackBufferingPolicy();
        private bool _bufferResumeInFlight;
        private int _bufferResumeRevision;
        private DateTimeOffset _nextTransportSample;

        private bool IsWaitingForPlaybackBuffer => _wantsPlayback && _bufferingPolicy.Waiting;

        private static List<string> Mp4PlaybackUrls(PlaybackInfo playback)
            => new[] { playback.Url }.Concat(playback.BackupUrls ?? new string[0])
                .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().ToList();

        private bool HasAlternatePlaybackRoutes => _playback != null &&
            (!string.IsNullOrEmpty(_playback.DashManifest) ? _adaptivePlayback?.HasAlternateRoutes == true
                : Mp4PlaybackUrls(_playback).Count > 1);

        private void ResetPlaybackTransportDisplay()
        {
            ResetPlaybackBuffering();
            _bufferedPlaybackSeconds = -1;
            _bufferSamplePosition = 0;
            _nextTransportSample = DateTimeOffset.MinValue;
        }

        private void ResetPlaybackBuffering()
        {
            ++_bufferResumeRevision;
            _bufferResumeInFlight = false;
            _bufferingPolicy.Reset();
        }

        private void InvalidatePlaybackBuffering()
        {
            bool waiting = _bufferingPolicy.Waiting && _wantsPlayback;
            if (_bufferResumeInFlight) CancelDisplayPlayRequest();
            ResetPlaybackBuffering();
            if (waiting) _bufferingPolicy.Rebuffer(_media.Source, _partGeneration);
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
                        _bufferSamplePosition = position.TotalSeconds;
                        _bufferedPlaybackSeconds = PlaybackBufferRanges.ContiguousSeconds(position.TotalSeconds,
                            session.GetBufferedRanges().Select(range => new PlaybackBufferRange(range.Start.TotalSeconds, range.End.TotalSeconds)));
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
            ApplyPlaybackBuffering(active && !_sourceLoading && !_displaySuspended, blocked);
            blocked = blocked || _bufferResumeInFlight;
            // A frozen clock with ample playable data is distinct from network
            // starvation. Rebuild the native source once at the same quality
            // and position; do not rotate CDN addresses for this condition.
            // Tiny clock ticks can briefly clear the spinner even while video
            // advances at slideshow speed. Observe native Playing as well; the
            // policy measures elapsed media time and leaves normal playback alone.
            bool observeClock = waiting || session.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing;
            // A clock deliberately paused below the three-second refill target
            // is not a stuck decoder. Once a resume was requested, native stalls
            // remain eligible for the existing bounded pipeline recovery.
            if (_pipelineRecovery.ShouldRestart(now, session.Position, active && !_sourceLoading,
                observeClock, blocked || _bufferingPolicy.PausedForBuffer, _bufferedPlaybackSeconds))
            {
                _ = RecoverPlaybackAsync(true);
                return;
            }
            if (_playbackRecovery.ShouldSwitch(now, session.Position,
                active, waiting, blocked,
                _bufferedPlaybackSeconds, HasAlternatePlaybackRoutes))
                _ = RecoverPlaybackAsync();
        }

        private void ApplyPlaybackBuffering(bool active, bool blocked)
        {
            var session = _media.PlaybackSession;
            bool playing = session.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing;
            bool buffering = session.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Buffering
                || (_sessionBuffering && !playing);
            double position = session.Position.TotalSeconds;
            double buffered = position < _bufferSamplePosition ? -1
                : Math.Max(0, _bufferedPlaybackSeconds - (position - _bufferSamplePosition));
            if (_bufferedPlaybackSeconds < 0) buffered = -1;
            var action = _bufferingPolicy.Observe(_media.Source, _partGeneration, active, _wantsPlayback, blocked,
                playing, buffering, position, session.NaturalDuration.TotalSeconds, buffered);
            if (playing && !_bufferingPolicy.Waiting) _sessionBuffering = false;
            if (action == PlaybackBufferAction.Pause)
            {
                // Pause the engine while retaining the user's play intent.
                // TogglePlay still switches _wantsPlayback off on explicit pause.
                RecordPlaybackTrack("buffer-pause", _playback, BufferDiagnostic(position, buffered));
                _media.Pause();
            }
            else if (action == PlaybackBufferAction.Resume && !_bufferResumeInFlight)
            {
                RecordPlaybackTrack("buffer-resume-request", _playback, BufferDiagnostic(position, buffered));
                _ = ResumeBufferedPlaybackAsync();
            }
        }

        private static string BufferDiagnostic(double position, double buffered)
            => " position=" + position.ToString("0.###", CultureInfo.InvariantCulture)
                + " playableSeconds=" + buffered.ToString("0.###", CultureInfo.InvariantCulture);

        private async Task ResumeBufferedPlaybackAsync()
        {
            var source = _media.Source;
            int generation = _partGeneration, revision = _bufferResumeRevision;
            if (_disposed || !_watching || !_wantsPlayback || _displaySuspended || source == null
                || _creatorVisible || _libraryVisible || _seeking || _dialogOpen || _inputPaneVisible) return;
            _bufferResumeInFlight = true;
            try
            {
                // The refill policy already paused a starved engine. Initial
                // native buffering must keep its download alive; do not add a
                // second Pause/Play cycle when the real buffer reaches target.
                await PlayWithDisplayAsync();
                if (!_disposed && revision == _bufferResumeRevision && generation == _partGeneration
                    && ReferenceEquals(source, _media.Source)
                    && _media.PlaybackSession.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing)
                {
                    _sessionBuffering = false;
                    RecordPlaybackTrack("buffer-resumed", _playback,
                        " position=" + _media.PlaybackSession.Position.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!_disposed && revision == _bufferResumeRevision && generation == _partGeneration
                    && ReferenceEquals(source, _media.Source))
                {
                    RecordPlaybackTrack("buffer-resume-failed", _playback, " hresult=" + ex.HResult.ToString("X8"));
                    SetStatus("缓冲后暂时无法恢复播放：" + Friendly(ex));
                }
            }
            finally { if (revision == _bufferResumeRevision) _bufferResumeInFlight = false; }
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
                RecordPlaybackTrack(restartPipeline ? "pipeline-recovery" : "route-recovery", playback,
                    BufferDiagnostic(_media.PlaybackSession.Position.TotalSeconds, _bufferedPlaybackSeconds)
                    + " pipelineAttempts=" + _pipelineRecovery.Attempts + " routeAttempts=" + _playbackRecovery.Attempts);
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
