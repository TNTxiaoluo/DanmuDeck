using System;
using System.Diagnostics;
using System.Threading.Tasks;
using BiliXbox.Models;
using BiliXbox.Services;
using Windows.Media.Playback;
using Windows.UI.Core;

namespace BiliXbox
{
    public sealed partial class MainPage
    {
        private readonly PlaybackHistoryTracker _playbackHistory = new PlaybackHistoryTracker();
        private PlaybackHistoryUploadQueue _historyUploads;
        private PlaybackHistoryAccount _historyAccount;
        private object _historySource;

        private static double HistoryClock => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
        private bool HistoryPlaying => _watching && !_creatorVisible && !_libraryVisible && !_sourceLoading
            && !_sessionBuffering && _wantsPlayback && _media.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;

        private void AccountPlaybackHistoryChanged()
        {
            var account = _disposed ? null : _api.CapturePlaybackHistoryAccount();
            if (_historyAccount != null && _historyAccount.Matches(account)) return;
            if (_historyAccount == null && account == null) return;
            _historyUploads?.Dispose(); _historyUploads = null;
            _playbackHistory.Reset(); _historySource = null; _historyAccount = account;
            if (account != null)
                _historyUploads = new PlaybackHistoryUploadQueue(_api.ReportPlaybackHistoryAsync, HistoryReported);
        }

        private void HistoryReported(PlaybackHistoryReport report)
        {
            // The worker has no UI affinity. A completed old-account request
            // cannot dirty the newly signed-in account's library.
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (!_disposed && _api.IsPlaybackHistoryAccountCurrent(report.Target.Account))
                    AccountLibrary.InvalidateHistoryContent();
            });
        }

        private void ObservePlaybackHistory()
        {
            if (_disposed) return;
            AccountPlaybackHistoryChanged();
            var source = _media.Source;
            if (_historyAccount == null || !_watching || _video == null || _video.Aid <= 0 || _currentCid <= 0 || source == null) return;
            var episode = _playingEpisodeItem;
            var target = new PlaybackHistoryTarget(_historyAccount, _video.Aid, _video.Bvid, _currentCid,
                episode?.EpisodeId ?? 0, episode?.SeasonId ?? 0, _playback?.QualityId ?? 0);
            var session = _media.PlaybackSession;
            var report = _playbackHistory.Observe(target, source, session.Position.TotalSeconds,
                session.NaturalDuration.TotalSeconds, HistoryPlaying, HistoryClock, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            _historySource = source;
            _historyUploads?.Enqueue(report);
        }

        private void SampleCurrentHistoryPosition()
        {
            // StopMedia may run after _currentCid or _playingEpisodeItem has
            // changed. Sample only the old source; never retarget its progress.
            if (_disposed || _historySource == null || !ReferenceEquals(_historySource, _media.Source)) return;
            var session = _media.PlaybackSession;
            _playbackHistory.Sample(_historySource, session.Position.TotalSeconds,
                session.NaturalDuration.TotalSeconds, HistoryPlaying, HistoryClock);
        }

        private void QueuePlaybackHistoryFlush()
        {
            if (_disposed) return;
            SampleCurrentHistoryPosition();
            _historyUploads?.RetryFailed();
            _historyUploads?.Enqueue(_playbackHistory.Flush(false, true));
        }

        private async Task FlushPlaybackHistoryAsync(bool completed = false)
        {
            if (_disposed) return;
            SampleCurrentHistoryPosition();
            var queue = _historyUploads;
            queue?.Enqueue(_playbackHistory.Flush(completed));
            // Pausing, leaving or suspending must remain responsive even when
            // the account/network is slow. The bounded background write can
            // finish later without altering navigation or starting playback.
            if (queue != null)
            {
                var deadline = Task.Delay(1800);
                var drain = queue.DrainAsync();
                if (await Task.WhenAny(drain, deadline) == drain && queue.RetryFailed())
                    await Task.WhenAny(queue.DrainAsync(), deadline);
            }
            if (!_disposed && ReferenceEquals(queue, _historyUploads) && _api.IsPlaybackHistoryAccountCurrent(_historyAccount))
                AccountLibrary.InvalidateHistoryContent();
        }

        private void StopPlaybackHistory()
        {
            _historyUploads?.Dispose(); _historyUploads = null; _historyAccount = null;
            _historySource = null; _playbackHistory.Reset();
        }
    }
}
