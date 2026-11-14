using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Models;

namespace BiliXbox.Services
{
    // One writer; retain only the newest queued progress for a viewing session.
    // A failed checkpoint never triggers a retry loop. An explicit final flush
    // can retry a transient failure once; a heartbeat replaces progress and is
    // safe to repeat, unlike a coin or comment submission.
    public sealed class PlaybackHistoryUploadQueue : IDisposable
    {
        private readonly object _gate = new object();
        private readonly LinkedList<PlaybackHistoryReport> _pending = new LinkedList<PlaybackHistoryReport>();
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly Func<PlaybackHistoryReport, CancellationToken, Task> _upload;
        private readonly Action<PlaybackHistoryReport> _success;
        private Task _worker = Task.CompletedTask;
        private PlaybackHistoryReport _failed;
        private bool _running, _stopped;
        public PlaybackHistoryUploadQueue(Func<PlaybackHistoryReport, CancellationToken, Task> upload,
            Action<PlaybackHistoryReport> success)
        { _upload = upload ?? throw new ArgumentNullException(nameof(upload)); _success = success; }

        public void Enqueue(PlaybackHistoryReport report)
        {
            if (report == null) return;
            lock (_gate)
            {
                if (_stopped) return;
                for (var node = _pending.First; node != null; node = node.Next)
                    if (node.Value.StartTimestamp == report.StartTimestamp && node.Value.Target.Matches(report.Target))
                    { node.Value = report; return; }
                if (_pending.Count >= 8) _pending.RemoveFirst();
                _pending.AddLast(report);
                if (!_running) { _running = true; _worker = Task.Run(ProcessAsync); }
            }
        }
        public Task DrainAsync() { lock (_gate) return _worker; }
        public bool RetryFailed()
        {
            lock (_gate)
            {
                var report = _failed;
                if (_stopped || report == null) return false;
                _failed = null;
                // A newer checkpoint for the same viewing session supersedes it.
                foreach (var pending in _pending)
                    if (pending.StartTimestamp == report.StartTimestamp && pending.Target.Matches(report.Target)) return false;
                Enqueue(report);
                return true;
            }
        }
        private async Task ProcessAsync()
        {
            while (true)
            {
                PlaybackHistoryReport report;
                lock (_gate)
                {
                    if (_stopped || _pending.Count == 0) { _running = false; return; }
                    report = _pending.First.Value; _pending.RemoveFirst();
                }
                try
                {
                    await _upload(report, _stop.Token).ConfigureAwait(false);
                    lock (_gate)
                        if (_failed != null && _failed.StartTimestamp == report.StartTimestamp
                            && _failed.Target.Matches(report.Target)) _failed = null;
                    if (!_stop.IsCancellationRequested) _success?.Invoke(report);
                }
                catch (Exception error) when (!(error is OutOfMemoryException))
                {
                    var apiError = error as BilibiliApiException;
                    bool transient = error is OperationCanceledException || apiError?.Code == -1 || apiError?.Code >= 500;
                    lock (_gate) if (!_stopped && transient) _failed = report;
                }
            }
        }
        public void Dispose()
        {
            Task worker;
            lock (_gate) { if (_stopped) return; _stopped = true; _pending.Clear(); _failed = null; worker = _worker; }
            _stop.Cancel();
            _ = worker.ContinueWith(task => _stop.Dispose(), TaskScheduler.Default);
        }
    }
}
