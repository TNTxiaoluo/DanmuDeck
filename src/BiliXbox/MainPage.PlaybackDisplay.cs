using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Models;
using BiliXbox.Services;

namespace BiliXbox
{
    public sealed partial class MainPage
    {
        private PlaybackDisplayController _playbackDisplay;
        private CancellationTokenSource _displayPlayCts;
        private volatile bool _displaySuspended;

        // The page calls these methods on its UI dispatcher. Constructing a
        // controller is lazy and never queries the HDMI factory during startup.
        private PlaybackDisplayController PlaybackDisplay
            => _playbackDisplay ?? (_playbackDisplay = new PlaybackDisplayController());

        private async Task<PlaybackInfo> PrepareSourceDisplayAsync(PlaybackInfo playback, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (_displaySuspended) throw new OperationCanceledException(token);
            if (await PlaybackDisplay.PrepareAsync(playback, token)) return playback;
            token.ThrowIfCancellationRequested();
            string displayFailure = PlaybackDisplay.FailureReason;
            var fallback = playback.CodecFallback;
            if (fallback != null && !fallback.IsHdr10 && await PlaybackDisplay.PrepareAsync(fallback, token))
            {
                fallback.QualityNotice = (string.IsNullOrWhiteSpace(displayFailure) ? "当前显示模式无法启用。" : displayFailure)
                    + "已回退到实际 " + fallback.Quality + " SDR 播放源。";
                return fallback;
            }
            throw new InvalidOperationException("当前显示器无法启用此画质的输出模式，请选择较低画质或 SDR 视频。");
        }

        private void CancelDisplayPlayRequest() => Cancel(ref _displayPlayCts);

        private async Task ResetPlaybackDisplayAsync()
        {
            CancelDisplayPlayRequest();
            var display = _playbackDisplay;
            if (display == null) return;
            try { await display.ResetAsync(); }
            catch (Exception) { /* Playback is already paused; a later prepare retries the output mode. */ }
            if (!_disposed && _watching) UpdateQualities();
        }

        private async Task PlayWithDisplayAsync()
        {
            if (_disposed || !_watching || _displaySuspended || !_wantsPlayback || _libraryVisible || _creatorVisible
                || _media.Source == null || _playback == null || _watchCts == null) return;
            CancelDisplayPlayRequest();
            var source = _media.Source;
            var playback = _playback;
            var watch = _watchCts;
            if (_partCts == null) _partCts = CancellationTokenSource.CreateLinkedTokenSource(watch.Token);
            int generation = _partGeneration;
            var owner = _displayPlayCts = CancellationTokenSource.CreateLinkedTokenSource(watch.Token);
            var token = owner.Token;
            try
            {
                bool prepared = await PlaybackDisplay.PrepareAsync(playback, token);
                token.ThrowIfCancellationRequested();
                if (_displayPlayCts != owner || _disposed || !_watching || _displaySuspended || !_wantsPlayback
                    || _creatorVisible || _libraryVisible || generation != _partGeneration || _watchCts != watch
                    || _media.Source != source || _playback != playback) return;
                if (!prepared)
                {
                    var fallback = playback.CodecFallback;
                    if (fallback != null && !fallback.IsHdr10 && _video != null)
                    {
                        fallback.QualityNotice = (string.IsNullOrWhiteSpace(PlaybackDisplay.FailureReason) ? "当前显示模式无法恢复。" : PlaybackDisplay.FailureReason)
                            + "已回退到实际 " + fallback.Quality + " SDR 播放源。";
                        await LoadPartAsync(_currentCid, fallback.QualityId, true, fallback);
                        return;
                    }
                    throw new InvalidOperationException("当前显示器无法恢复此画质的输出模式，请选择其他画质。");
                }
                _media.Play();
                UpdateQualities();
                UpdatePlaybackUi();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested || _displaySuspended) { }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested && generation == _partGeneration && _media.Source == source
                    && _playback == playback && !_disposed)
                {
                    _wantsPlayback = _resumeAfterOpen = false;
                    _media.Pause();
                    SetStatus("显示模式设置失败：" + Friendly(ex));
                    await ResetPlaybackDisplayAsync();
                    if (!_disposed && generation == _partGeneration && _media.Source == source) UpdatePlaybackUi();
                }
            }
            finally { if (_displayPlayCts == owner) Cancel(ref _displayPlayCts); }
        }

        // Resuming does not autoplay. The next explicit play reapplies the
        // mode for the still-current source after the suspension reset.
        public void ResumeDisplayPlayback() { _displaySuspended = false; }

        public async Task SuspendPlaybackAsync()
        {
            _displaySuspended = true;
            ++_partGeneration;
            Cancel(ref _partCts);
            SuspendPlayback();
            await ResetPlaybackDisplayAsync();
        }

        private static void MergePlaybackQualities(PlaybackInfo current, PlaybackInfo latest)
        {
            current.CapabilityDiagnostic = latest.CapabilityDiagnostic;
            var qualities = latest.AvailableQualities.ToList();
            var playing = qualities.FirstOrDefault(item => item.Id == current.QualityId);
            if (playing == null)
            {
                playing = new PlaybackQuality { Id = current.QualityId, Label = current.Quality };
                qualities.Add(playing);
            }
            // A narrow catalogue cannot invalidate the actual stream, but an
            // explicit device block must stay hidden after a display change.
            if (playing.IsVisible && !playing.IsCapabilityPending)
            {
                playing.HasPlaybackSource = playing.IsAvailable = true;
                playing.IsProbe = false;
                playing.Reason = "";
            }
            current.AvailableQualities = qualities.OrderByDescending(item => item.Id).ToList();
        }
    }
}
