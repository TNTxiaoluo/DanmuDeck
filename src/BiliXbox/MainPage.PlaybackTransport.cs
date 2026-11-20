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
        private readonly PlaybackAccelerationHealthState _accelerationHealth = new PlaybackAccelerationHealthState();
        private System.Threading.CancellationTokenSource _nativeAccelerationRecoveryWatch;
        private int _playbackRouteOffset;
        private bool _switchingPlaybackRoute;
        private double _bufferedPlaybackSeconds = -1;
        private double _downloadBytesPerSecond = -1;
        private double _bufferSamplePosition;
        private double? _lastBufferedWakePosition;
        private readonly PlaybackBufferingPolicy _bufferingPolicy = new PlaybackBufferingPolicy();
        private bool _bufferResumeInFlight;
        private int _bufferResumeRevision;
        private DateTimeOffset _nextTransportSample;
        private DateTimeOffset _nextWaitingDiagnostic;

        private bool IsWaitingForPlaybackBuffer => _wantsPlayback && _bufferingPolicy.Waiting;

        private static List<string> Mp4PlaybackUrls(PlaybackInfo playback)
            => new[] { playback.Url }.Concat(playback.BackupUrls ?? new string[0])
                .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().ToList();

        private bool HasAlternatePlaybackRoutes => _playback != null &&
            (!string.IsNullOrEmpty(_playback.DashManifest) ? _adaptivePlayback?.HasAlternateRoutes == true
                : Mp4PlaybackUrls(_playback).Count > 1);

        private bool HasUntriedPlaybackRoute => _playback != null &&
            (!string.IsNullOrEmpty(_playback.DashManifest) ? _adaptivePlayback?.HasUntriedRoute == true
                : _playbackRouteOffset + 1 < Mp4PlaybackUrls(_playback).Count);

        private void ResetPlaybackTransportDisplay()
        {
            ResetPlaybackBuffering();
            _bufferedPlaybackSeconds = -1;
            _downloadBytesPerSecond = -1;
            _bufferSamplePosition = 0;
            SetSeekBufferRanges(null, 0);
            _nextTransportSample = DateTimeOffset.MinValue;
            _nextWaitingDiagnostic = DateTimeOffset.MinValue;
        }

        private void ResetPlaybackBuffering()
        {
            ++_bufferResumeRevision;
            _bufferResumeInFlight = false;
            _lastBufferedWakePosition = null;
            _bufferingPolicy.Reset();
            _accelerationHealth.ResetWaiting();
        }

        private void InvalidatePlaybackBuffering()
        {
            bool waiting = _bufferingPolicy.Waiting && _wantsPlayback;
            if (_bufferResumeInFlight) CancelDisplayPlayRequest();
            ResetPlaybackBuffering();
            if (waiting) _bufferingPolicy.Rebuffer(_media.Source, _partGeneration, resumeAfterSeek: true);
            var now = DateTimeOffset.UtcNow;
            _playbackRecovery.ResetWaiting(now);
            _pipelineRecovery.ResetWaiting(now);
            _bufferedPlaybackSeconds = -1;
            SetSeekBufferRanges(null, 0);
            _nextTransportSample = DateTimeOffset.MinValue;
        }

        private void ApplyRestorePosition()
        {
            // Duration and CanSeek can become available while DASH is still
            // opening. Seeking then can interrupt the first video segment.
            if (_sourceLoading || !_restorePosition.HasValue || _media.Source == null) return;
            var session = _media.PlaybackSession;
            if (!session.CanSeek || session.NaturalDuration <= TimeSpan.Zero) return;
            var position = TimeSpan.FromSeconds(Math.Max(0, Math.Min(_restorePosition.Value.TotalSeconds, session.NaturalDuration.TotalSeconds)));
            SeekToPosition(position);
            RecordPlaybackTrack("restore-position-applied", _playback, " position=" + position.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        }

        private void SeekToPosition(TimeSpan position)
        {
            if (_sourceLoading)
            {
                _restorePosition = position;
                return;
            }
            _restorePosition = null;
            InvalidatePlaybackBuffering();
            _media.PlaybackSession.Position = position;
        }

        private void ResetPlaybackRecovery(bool recovery)
        {
            if (recovery)
            {
                _playbackRecovery.ResetWaiting(DateTimeOffset.UtcNow);
                _pipelineRecovery.ResetWaiting(DateTimeOffset.UtcNow);
            }
            else { _playbackRouteOffset = 0; _playbackRecovery.Reset(); _pipelineRecovery.Reset(); _indexCompatibilityAttempts.Clear(); }
        }

        private void UpdatePlaybackTransport(bool waiting, bool clockStalled = false)
            => UpdatePlaybackTransport(waiting, DateTimeOffset.UtcNow, clockStalled);

        private void UpdatePlaybackTransport(bool waiting, DateTimeOffset now, bool clockStalled = false)
        {
            var session = _media.PlaybackSession;
            double required = _playback != null && _playback.VideoBitrate > 0
                ? (_playback.VideoBitrate + (double)_playback.AudioBitrate) / 8 : 0;
            // Check for refill completion frequently. Poll less often when
            // the media clock is healthy.
            var sampleInterval = TimeSpan.FromMilliseconds(waiting ? 125 : 500);
            if (now >= _nextTransportSample || (waiting && _nextTransportSample - now > sampleInterval))
            {
                _nextTransportSample = now + sampleInterval;
                _downloadBytesPerSecond = _adaptivePlayback?.CurrentDownloadBytesPerSecond
                    ?? _activeDownloadSpeed?.CurrentDownloadBytesPerSecond ?? -1;
                _bufferedPlaybackSeconds = -1;
                PlaybackBufferRange[] ranges = null;
                double duration = 0;
                if (_media.Source != null)
                {
                    try
                    {
                        var position = session.Position;
                        duration = session.NaturalDuration.TotalSeconds;
                        ranges = session.GetBufferedRanges().Select(range =>
                            new PlaybackBufferRange(range.Start.TotalSeconds, range.End.TotalSeconds)).ToArray();
                        _bufferSamplePosition = position.TotalSeconds;
                        _bufferedPlaybackSeconds = PlaybackBufferRanges.ContiguousSeconds(position.TotalSeconds, ranges);
                    }
                    catch (Exception) { _bufferedPlaybackSeconds = -1; }
                }
                SetSeekBufferRanges(ranges, duration);
                var speed = DownloadSpeedMeter.Format(_downloadBytesPerSecond);
                var cache = _bufferedPlaybackSeconds < 0 ? "等待数据" : _bufferedPlaybackSeconds.ToString("0.0") + " 秒";
                BufferingSpeed.Text = "接收：" + speed + " · 可播缓存：" + cache;
                PlaybackTransportHint.Text = "接收：" + speed + " · 可播缓存：" + cache
                    + (required > 0 ? "\n此画质平均需要约 " + DownloadSpeedMeter.Format(required) : "");
            }
            double received = _downloadBytesPerSecond;
            bool active = _watching && _wantsPlayback && _media.Source != null && !_switchingPlaybackRoute
                && _partCts != null && !_partCts.IsCancellationRequested;
            bool blocked = _seeking || _dialogOpen || _inputPaneVisible || _creatorVisible || _libraryVisible || _displaySuspended;
            if (active && waiting && now >= _nextWaitingDiagnostic)
            {
                _nextWaitingDiagnostic = now.AddSeconds(10);
                RecordPlaybackTrack("transport-wait", _playback,
                    " generation=" + _partGeneration + " sourceLoading=" + _sourceLoading
                    + " nativeState=" + session.PlaybackState + " displaySuspended=" + _displaySuspended
                    + " refillPaused=" + _bufferingPolicy.PausedForBuffer + " resumeInFlight=" + _bufferResumeInFlight
                    + BufferDiagnostic(session.Position.TotalSeconds, _bufferedPlaybackSeconds)
                    + ThroughputDiagnostic(received, required)
                    + " " + (_adaptivePlayback?.TransportDiagnostic ?? "transport=mp4")
                    + " routeAttempts=" + _playbackRecovery.Attempts + " pipelineAttempts=" + _pipelineRecovery.Attempts);
            }
            bool pipelineBlocked = blocked;
            ApplyPlaybackBuffering(active && !_sourceLoading && !_displaySuspended, blocked);
            blocked = blocked || _bufferResumeInFlight;
            if (active && !blocked && _adaptivePlayback?.IndexFailurePending == true)
            {
                _ = RecoverPlaybackIndexAsync();
                return;
            }
            bool bodyFailed = _adaptivePlayback?.AccelerationBodyFailed == true;
            bool accelerationStalled = _accelerationHealth.Observe(_media.Source, _partGeneration, now,
                _adaptivePlayback?.AccelerationEnabled == true && _nativeAccelerationRecoveryWatch != _watchCts,
                active, waiting, blocked, session.Position.TotalSeconds, _bufferedPlaybackSeconds, bodyFailed);
            bool belowRequiredRate = received >= 0 && required > 0 && received < required * .5;
            if (!(accelerationStalled && bodyFailed) && _playbackRecovery.ShouldSwitch(now, session.Position, active, waiting, blocked,
                _bufferedPlaybackSeconds, HasUntriedPlaybackRoute, received > 0, belowRequiredRate))
            {
                string slowRefill = belowRequiredRate && received > 0 ? ThroughputDiagnostic(received, required) : null;
                _ = RecoverPlaybackAsync(slowRefill: slowRefill);
                return;
            }
            if (accelerationStalled)
            {
                _ = RecoverAccelerationNativelyAsync();
                return;
            }
            if (clockStalled && active && !blocked && !_sourceLoading && !_sessionBuffering
                && !_bufferingPolicy.Waiting && TryWakeBufferedPlayback()) return;
            // A frozen clock with ample playable data is distinct from network
            // starvation. Rebuild the native source once at the same quality
            // and position; do not rotate CDN addresses for this condition.
            // Tiny clock ticks can briefly clear the spinner even while video
            // advances at slideshow speed. Observe native Playing as well; the
            // policy measures elapsed media time and leaves normal playback alone.
            bool observeClock = waiting || session.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing;
            // A deliberate refill pause blocks clock recovery. A pending Play
            // request must not also block it: native resume can stall after the
            // contiguous buffer has already filled.
            bool stillRefilling = belowRequiredRate && waiting && _bufferedPlaybackSeconds >= 0
                && _bufferedPlaybackSeconds < 3;
            if (_pipelineRecovery.ShouldRestart(now, session.Position, active && !_sourceLoading,
                observeClock, pipelineBlocked || _bufferingPolicy.PausedForBuffer || stillRefilling, _bufferedPlaybackSeconds))
            {
                _ = RecoverPlaybackAsync(true);
                return;
            }
        }

        private bool TryWakeBufferedPlayback()
        {
            var session = _media.PlaybackSession;
            double position = session.Position.TotalSeconds;
            double buffered = _bufferedPlaybackSeconds - Math.Max(0, position - _bufferSamplePosition);
            if (session.PlaybackState != Windows.Media.Playback.MediaPlaybackState.Playing
                || position < _bufferSamplePosition || buffered < PlaybackBufferingPolicy.RefillTargetSeconds
                || (_lastBufferedWakePosition.HasValue && position <= _lastBufferedWakePosition.Value + 1)) return false;

            _lastBufferedWakePosition = position;
            try
            {
                _media.Pause();
                _media.Play();
                RecordPlaybackTrack("buffered-clock-wake", _playback, BufferDiagnostic(position, buffered));
                return true;
            }
            catch (Exception ex)
            {
                RecordPlaybackTrack("buffered-clock-wake-failed", _playback, " hresult=" + ex.HResult.ToString("X8"));
                if (session.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Paused)
                    _bufferingPolicy.Rebuffer(_media.Source, _partGeneration);
                return false;
            }
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

        private static string ThroughputDiagnostic(double received, double required)
            => " receiveBps=" + received.ToString("0", CultureInfo.InvariantCulture)
                + " requiredBps=" + required.ToString("0", CultureInfo.InvariantCulture);

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

        private async Task RecoverPlaybackAsync(bool restartPipeline = false, string slowRefill = null)
        {
            if (_switchingPlaybackRoute || !_watching || _disposed || _video == null || _partCts == null
                || !_wantsPlayback || _playback == null || _partCts.IsCancellationRequested
                || (!restartPipeline && !HasAlternatePlaybackRoutes)) return;
            var playback = _playback;
            var watch = _watchCts;
            var part = _partCts;
            _switchingPlaybackRoute = true;
            bool useNative = restartPipeline && _adaptivePlayback?.AccelerationEnabled == true;
            if (useNative) _nativeAccelerationRecoveryWatch = watch;
            if (!restartPipeline)
                _playbackRouteOffset = _adaptivePlayback?.NextRouteOffset ?? (_playbackRouteOffset + 1);
            try
            {
                SetStatus((restartPipeline ? "正在恢复播放 · " : slowRefill != null
                    ? "当前线路补缓冲过慢，正在切换备用线路 · " : "正在重新缓冲 · ") + playback.Quality);
                RecordPlaybackTrack(restartPipeline ? "pipeline-recovery" : "route-recovery", playback,
                    BufferDiagnostic(_media.PlaybackSession.Position.TotalSeconds, _bufferedPlaybackSeconds)
                    + (slowRefill == null ? "" : " reason=slow-refill" + slowRefill)
                    + " routeOffset=" + _playbackRouteOffset
                    + " resumeInFlight=" + _bufferResumeInFlight + " useNative=" + useNative
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

        private async Task RecoverAccelerationNativelyAsync()
        {
            if (_switchingPlaybackRoute || _disposed || !_watching || !_wantsPlayback || _playback == null
                || _video == null || _watchCts == null || _partCts == null || _partCts.IsCancellationRequested
                || _nativeAccelerationRecoveryWatch == _watchCts) return;
            var playback = _playback;
            _nativeAccelerationRecoveryWatch = _watchCts;
            _switchingPlaybackRoute = true;
            try
            {
                // DownloadFailed may already have selected a backup internally.
                // Recreating the filter must retain it rather than restore the
                // page's earlier route offset and retry the failed primary.
                _playbackRouteOffset = _adaptivePlayback?.SelectedRouteOffset ?? _playbackRouteOffset;
                playback.QualityNotice = "正在使用原生下载恢复 · " + playback.Quality;
                RecordPlaybackTrack("acceleration-native-recovery", playback,
                    BufferDiagnostic(_media.PlaybackSession.Position.TotalSeconds, _bufferedPlaybackSeconds)
                    + " routeOffset=" + _playbackRouteOffset
                    + " bodyFailed=" + (_adaptivePlayback?.AccelerationBodyFailed == true));
                await LoadPartAsync(_currentCid, playback.QualityId, true, playback, true);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!_disposed) SetStatus("原生线路暂时未能恢复：" + Friendly(ex)); }
            finally { _switchingPlaybackRoute = false; }
        }
    }
}
