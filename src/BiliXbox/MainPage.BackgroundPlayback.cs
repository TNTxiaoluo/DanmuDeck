using System;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Services;
using Windows.ApplicationModel;
using Windows.System;
using Windows.UI.Core;

namespace BiliXbox
{
    public sealed partial class MainPage
    {
        private readonly SemaphoreSlim _backgroundGate = new SemaphoreSlim(1, 1);
        private int _backgroundRevision;
        private bool _audioOnlyPlayback, _backgroundSourceSwitch, _backgroundViewsDetached;
        private object _backgroundVideoItems, _backgroundUserItems, _backgroundCommentItems, _backgroundReplyItems;

        private async void Viewing_EnteredBackground(object sender, EnteredBackgroundEventArgs args)
        {
            var deferral = args.GetDeferral();
            try { await TransitionBackgroundAsync(true); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { RecordBackgroundFailure("enter", ex); }
            finally { deferral.Complete(); }
        }
        private async void Viewing_LeavingBackground(object sender, LeavingBackgroundEventArgs args)
        {
            var deferral = args.GetDeferral();
            try { await TransitionBackgroundAsync(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { RecordBackgroundFailure("leave", ex); }
            finally { deferral.Complete(); }
        }
        private async Task TransitionBackgroundAsync(bool entering)
        {
            int revision = ++_backgroundRevision;
            _inBackground = entering;
            if (!entering && _backgroundSourceSwitch) Cancel(ref _partCts);
            await _backgroundGate.WaitAsync();
            try
            {
                if (_disposed || revision != _backgroundRevision) return;
                RecordBackgroundState(entering ? "enter-start" : "leave-start");
                _clock.Stop();
                if (entering)
                {
                    SuspendGamepadInput(); CancelDisplayPlayRequest();
                    Player.SetMediaPlayer(null);
                    if (!_backgroundAudio || !_watching)
                    { _media.Pause(); _wantsPlayback = _resumeAfterOpen = false; }
                    _adjustingVolume = false; UpdateVolumeLabel();
                    TrimBackgroundViews();
                    if (_backgroundAudio && _watching && _playback != null && _video != null
                        && !string.IsNullOrEmpty(_playback.DashManifest))
                    {
                        _backgroundSourceSwitch = true;
                        try
                        {
                            await LoadPartAsync(_currentCid, _playback.QualityId, true, _playback, true);
                            await WaitForBackgroundAudioAsync(revision);
                        }
                        finally { _backgroundSourceSwitch = false; }
                    }
                    if (_disposed || revision != _backgroundRevision) return;
                    await ResetPlaybackDisplayAsync();
                }
                else
                {
                    RestoreBackgroundViews();
                    Player.SetMediaPlayer(_media);
                    if (_watching && _playback != null && (_audioOnlyPlayback || _media.Source == null))
                        await LoadPartAsync(_currentCid, _playback.QualityId, true, _playback, true);
                    else if (_wantsPlayback) await PlayWithDisplayAsync();
                }
                if (_disposed || revision != _backgroundRevision) return;
                if (entering) { _clock.Interval = TimeSpan.FromSeconds(1); _clock.Start(); }
                else UpdateVolumeLabel();
                RecordBackgroundState(entering ? "enter-ready" : "leave-ready");
            }
            finally
            {
                if (!_disposed && revision == _backgroundRevision && !entering)
                { _clock.Interval = TimeSpan.FromMilliseconds(100); _clock.Start(); ResumeGamepadInput(); }
                _backgroundGate.Release();
            }
        }
        private async Task WaitForBackgroundAudioAsync(int revision)
        {
            // Keep the transition deferral while MediaOpened applies the old
            // position and starts audio. Do not hand the OS a stopped player in
            // the small source-switch gap; bound this wait and allow returning.
            var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
            while (!_disposed && revision == _backgroundRevision && _wantsPlayback
                && _sourceLoading && _media.Source != null && DateTimeOffset.UtcNow < deadline)
                await Task.Delay(50);
            if (revision == _backgroundRevision) RecordBackgroundState(_sourceLoading ? "audio-open-pending" : "audio-opened");
        }

        private void InitializeBackgroundMemoryHandling()
        {
            MemoryManager.AppMemoryUsageLimitChanging += BackgroundMemoryLimitChanging;
            MemoryManager.AppMemoryUsageIncreased += BackgroundMemoryIncreased;
        }
        private void DisposeBackgroundMemoryHandling()
        {
            ++_backgroundRevision;
            MemoryManager.AppMemoryUsageLimitChanging -= BackgroundMemoryLimitChanging;
            MemoryManager.AppMemoryUsageIncreased -= BackgroundMemoryIncreased;
        }
        private void BackgroundMemoryLimitChanging(object sender, AppMemoryUsageLimitChangingEventArgs args)
        { if (_inBackground && MemoryManager.AppMemoryUsage >= args.NewLimit) QueueBackgroundTrim(); }
        private void BackgroundMemoryIncreased(object sender, object args)
        { if (_inBackground && MemoryManager.AppMemoryUsage >= MemoryManager.AppMemoryUsageLimit * .8) QueueBackgroundTrim(); }
        private void QueueBackgroundTrim()
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.High, () =>
            {
                if (!_inBackground || _disposed) return;
                try { TrimBackgroundViews(); }
                catch (Exception ex) { RecordBackgroundFailure("memory-trim", ex); }
            });
        }
        private void TrimBackgroundViews()
        {
            if (!_inBackground || !_backgroundAudio || !_watching) return;
            if (!_backgroundViewsDetached)
            {
                _backgroundViewsDetached = true;
                _backgroundVideoItems = VideoGrid.ItemsSource; _backgroundUserItems = UserGrid.ItemsSource;
                _backgroundCommentItems = CommentList.ItemsSource; _backgroundReplyItems = ReplyList.ItemsSource;
                VideoGrid.ItemsSource = UserGrid.ItemsSource = CommentList.ItemsSource = ReplyList.ItemsSource = null;
            }
            DisposeCommentImages(); StopDanmakuMask();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized);
            RecordBackgroundState("memory-trim");
        }
        private void RestoreBackgroundViews()
        {
            if (!_backgroundViewsDetached) return;
            VideoGrid.ItemsSource = _backgroundVideoItems; UserGrid.ItemsSource = _backgroundUserItems;
            CommentList.ItemsSource = _backgroundCommentItems; ReplyList.ItemsSource = _backgroundReplyItems;
            _backgroundVideoItems = _backgroundUserItems = _backgroundCommentItems = _backgroundReplyItems = null;
            _backgroundViewsDetached = false;
        }
        private void RecordBackgroundState(string phase)
        {
            PlaybackDiagnosticsLog.Record("background phase=" + phase + " revision=" + _backgroundRevision
                + " inBackground=" + _inBackground + " audioOnly=" + _audioOnlyPlayback
                + " wantsPlayback=" + _wantsPlayback + " memoryBytes=" + MemoryManager.AppMemoryUsage
                + " memoryLimitBytes=" + MemoryManager.AppMemoryUsageLimit);
        }
        private void RecordBackgroundFailure(string phase, Exception ex)
        { PlaybackDiagnosticsLog.Record("background-failed phase=" + phase + " hresult=0x" + ex.HResult.ToString("X8")); }
    }
}
