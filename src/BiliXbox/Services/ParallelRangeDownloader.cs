using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BiliXbox.Services
{
    // C# adaptation of the bounded Range/CDN scheduling in Bilibili-thread-ripper
    // (MIT, revision 6fd2ed3e). See THIRD-PARTY-NOTICES.txt. Browser hooks/MSE
    // are deliberately replaced by the Windows native HTTP filter adapter.
    //
    // Per range: a 16 KiB anchor is fetched from the best route already
    // verified for this file, while a few untried routes are probed alongside
    // it. Routes are verified once per file (same total length and the same
    // anchor bytes) and remembered, so later ranges start immediately. Body
    // chunks are spread over verified routes by measured speed; a chunk that is
    // much slower than its route's history is hedged on another route.
    internal sealed class ParallelRangeDownloader
    {
        internal const int Concurrency = 16, ChunkBytes = 512 * 1024, AnchorBytes = 16 * 1024;
        // Budget: two full-size player ranges plus one 8 MiB prefetch piece.
        internal const int MaximumRangeBytes = 16 * 1024 * 1024, MaximumReservedBytes = 40 * 1024 * 1024;
        private const int MaximumRememberedRoutes = 32, MaximumRememberedFiles = 8;
        private const int ProbesWithKnownRoutes = 2, ProbesWithoutKnownRoutes = 4;
        private static readonly long MinimumHedgeTicks = Stopwatch.Frequency * 9 / 10;
        private static readonly long MaximumHedgeTicks = Stopwatch.Frequency * 4;
        private readonly Func<string, ulong, int, CancellationToken, Task<RangeChunk>> _fetch;
        private readonly SemaphoreSlim _connections = new SemaphoreSlim(Concurrency);
        private readonly object _gate = new object();
        private readonly Dictionary<string, Node> _performance = new Dictionary<string, Node>(StringComparer.Ordinal);
        private readonly Dictionary<string, FileRoutes> _files = new Dictionary<string, FileRoutes>(StringComparer.Ordinal);
        private int _reserved;
        private int _chunkRetries, _hedges, _hedgeWins;
        internal int ReservedBytes { get { lock (_gate) return _reserved; } }

        internal string PerformanceDiagnostic
        {
            get
            {
                lock (_gate)
                {
                    long now = Stopwatch.GetTimestamp();
                    var rates = _performance.Values.Where(node => now >= node.RetryAt).Select(node => node.BytesPerSecond)
                        .Where(rate => rate > 0).ToArray();
                    return " sampledVideoRoutes=" + rates.Length + " bestVideoChunkBps="
                        + (rates.Length == 0 ? 0 : rates.Max()).ToString("0", CultureInfo.InvariantCulture)
                        + " videoChunkRetries=" + _chunkRetries + " hedges=" + _hedges + " hedgeWins=" + _hedgeWins
                        + " verifiedRoutes=" + _files.Values.Sum(file => file.Verified.Count);
                }
            }
        }

        internal ParallelRangeDownloader(Func<string, ulong, int, CancellationToken, Task<RangeChunk>> fetch)
        { _fetch = fetch ?? throw new ArgumentNullException(nameof(fetch)); }

        internal static bool TryParseRange(string value, out ulong start, out int length)
        {
            start = 0; length = 0;
            if (value == null || !value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return false;
            var parts = value.Substring(6).Split('-'); ulong end;
            if (parts.Length != 2 || !ulong.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out start)
                || !ulong.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out end)
                || end < start || end - start >= MaximumRangeBytes) return false;
            length = (int)(end - start + 1);
            return true;
        }

        internal async Task<RangeDownload> DownloadAsync(IReadOnlyList<string> urls, ulong start, int length, CancellationToken token,
            Action<RangeDownload> ready = null, Func<int, byte[], CancellationToken, Task> deliver = null,
            Func<int, CancellationToken, Task> waitForWindow = null, Action<string> trace = null)
        {
            token.ThrowIfCancellationRequested();
            if (urls == null || urls.Count == 0 || length < 64 * 1024 || length > MaximumRangeBytes
                || start > ulong.MaxValue - (ulong)length) return null;
            lock (_gate)
            {
                if (_reserved > MaximumReservedBytes - length) return null;
                _reserved += length;
            }
            try
            {
                using (var job = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    var candidates = urls.Distinct(StringComparer.Ordinal).ToArray();
                    var file = Routes(candidates[0]);
                    var verifiedNodes = new List<Node>();
                    RangeChunk anchor = await AnchorAsync(file, candidates, start, length, verifiedNodes, job.Token, trace).ConfigureAwait(false);
                    if (anchor == null || anchor.Total < start + (ulong)length) throw new IOException("加速线路未提供完整媒体范围。");
                    var nodes = verifiedNodes;
                    var output = deliver == null ? new byte[length] : null;
                    if (deliver == null) Buffer.BlockCopy(anchor.Bytes, 0, output, 0, AnchorBytes);
                    else await deliver(0, anchor.Bytes, job.Token).ConfigureAwait(false);
                    ready?.Invoke(new RangeDownload { Total = anchor.Total, ContentType = anchor.ContentType });
                    int cursor = AnchorBytes;
                    int workerCount = Math.Max(1, Math.Min(Concurrency, (length - AnchorBytes + ChunkBytes - 1) / ChunkBytes));
                    var workers = Enumerable.Range(0, workerCount).Select(async worker =>
                    {
                        try
                        {
                            while (true)
                            {
                                int offset = Interlocked.Add(ref cursor, ChunkBytes) - ChunkBytes;
                                if (offset >= length) return;
                                if (waitForWindow != null) await waitForWindow(offset, job.Token).ConfigureAwait(false);
                                int count = Math.Min(ChunkBytes, length - offset);
                                var part = await FetchChunkAsync(nodes, start + (ulong)offset, count, anchor.Total, job.Token, trace).ConfigureAwait(false);
                                if (deliver == null) Buffer.BlockCopy(part.Bytes, 0, output, offset, count);
                                else await deliver(offset, part.Bytes, job.Token).ConfigureAwait(false);
                            }
                        }
                        catch { job.Cancel(); throw; }
                    }).ToArray();
                    // Await all cancelled siblings before releasing their memory
                    // budget or falling back to the native HTTP request.
                    await Task.WhenAll(workers).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    return new RangeDownload { Bytes = output, Total = anchor.Total, ContentType = anchor.ContentType };
                }
            }
            finally { lock (_gate) _reserved -= length; }
        }

        // Fetch this range's first 16 KiB. Known-good routes answer at once;
        // untried routes are compared against the reference anchor so only
        // byte-identical copies of this exact file ever share media ranges.
        private async Task<RangeChunk> AnchorAsync(FileRoutes file, string[] candidates, ulong start, int length,
            List<Node> verifiedNodes, CancellationToken token, Action<string> trace)
        {
            List<string> known, untried;
            long now = Stopwatch.GetTimestamp();
            lock (_gate)
            {
                known = candidates.Where(url => file.Verified.Contains(url))
                    .OrderBy(url => now < Route(url).RetryAt).ThenByDescending(url => Route(url).BytesPerSecond).ToList();
                untried = candidates.Where(url => !file.Verified.Contains(url) && !file.Rejected.Contains(url)
                    && (!file.ProbeRetryAt.TryGetValue(url, out var retry) || now >= retry)).ToList();
            }
            var probeUrls = new List<string>();
            if (known.Count > 0) probeUrls.Add(known[0]);
            probeUrls.AddRange(untried.Take(known.Count > 0 ? ProbesWithKnownRoutes : ProbesWithoutKnownRoutes));
            if (probeUrls.Count == 0) probeUrls.AddRange(candidates.Take(1));
            string reference = known.Count > 0 ? known[0] : null;

            using (var probesLife = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                var probes = probeUrls.Select(async url =>
                {
                    try { return Tuple.Create(url, await FetchAsync(url, start, AnchorBytes, probesLife.Token, trace).ConfigureAwait(false)); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception ex) when (!(ex is OutOfMemoryException)) { return Tuple.Create(url, (RangeChunk)null); }
                }).ToArray();
                RangeChunk anchor = null;
                try
                {
                    var pending = probes.ToList();
                    while (pending.Count > 0)
                    {
                        var completed = await Task.WhenAny(pending).ConfigureAwait(false);
                        pending.Remove(completed);
                        var result = await completed.ConfigureAwait(false);
                        if (result.Item2 == null || result.Item2.Total < start + (ulong)length) continue;
                        // A known route defines the file; otherwise the first
                        // complete answer does, preferring the API's own order.
                        if (reference == null || result.Item1 == reference) { anchor = result.Item2; break; }
                    }
                    if (anchor == null && reference != null)
                    {
                        // The remembered route failed this time: fall back to
                        // any answer and let byte comparison decide again.
                        var answered = probes.Where(task => task.IsCompleted && task.Result.Item2 != null
                            && task.Result.Item2.Total >= start + (ulong)length).Select(task => task.Result).FirstOrDefault();
                        if (answered != null) anchor = answered.Item2;
                    }
                    // A short grace lets other fast untried routes finish their
                    // 16 KiB probe; slower ones are retried on a later range.
                    if (anchor != null && pending.Count > 0)
                        await Task.WhenAny(Task.WhenAll(probes), Task.Delay(known.Count > 0 ? 120 : 200, token)).ConfigureAwait(false);
                    probesLife.Cancel();
                    var results = await Task.WhenAll(probes).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (anchor == null) return null;
                    if (reference == null)
                    {
                        // Without a remembered route, prefer the first server
                        // URL among the answers so it decides the bytes.
                        var preferred = results.FirstOrDefault(item => item.Item2 != null && item.Item2.Total >= start + (ulong)length);
                        if (preferred != null) anchor = preferred.Item2;
                    }
                    lock (_gate)
                    {
                        if (file.Total != 0 && file.Total != anchor.Total) { file.Verified.Clear(); file.Rejected.Clear(); }
                        file.Total = anchor.Total;
                        long stamp = Stopwatch.GetTimestamp();
                        foreach (var item in results)
                        {
                            if (item.Item2 == null)
                            {
                                // Unanswered within the grace or failed: retry
                                // this route's probe later with backoff.
                                int misses;
                                file.ProbeMisses.TryGetValue(item.Item1, out misses);
                                file.ProbeMisses[item.Item1] = ++misses;
                                file.ProbeRetryAt[item.Item1] = stamp + Backoff(misses);
                                if (file.Verified.Contains(item.Item1) && misses >= 3) file.Verified.Remove(item.Item1);
                                continue;
                            }
                            if (item.Item2.Total == anchor.Total && item.Item2.Bytes.SequenceEqual(anchor.Bytes))
                            {
                                file.Verified.Add(item.Item1);
                                file.ProbeMisses.Remove(item.Item1);
                                file.ProbeRetryAt.Remove(item.Item1);
                            }
                            else { file.Verified.Remove(item.Item1); file.Rejected.Add(item.Item1); }
                        }
                        verifiedNodes.AddRange(candidates.Where(url => file.Verified.Contains(url)).Select(Route));
                    }
                    trace?.Invoke("anchor known=" + known.Count + " probed=" + probeUrls.Count + " verified=" + verifiedNodes.Count);
                    return verifiedNodes.Count == 0 ? null : anchor;
                }
                finally
                {
                    probesLife.Cancel();
                    try { await Task.WhenAll(probes).ConfigureAwait(false); }
                    catch (Exception ex) when (!(ex is OutOfMemoryException)) { }
                }
            }
        }

        private sealed class Attempt
        {
            internal Node Node;
            internal Task<RangeChunk> Task;
            internal CancellationTokenSource Life;
            internal bool Hedge;
        }

        // One body chunk. Routes are tried in speed order; if the first attempt
        // runs well past its route's measured speed, one hedge starts on the
        // next route and whichever finishes first wins.
        private async Task<RangeChunk> FetchChunkAsync(List<Node> nodes, ulong at, int count, ulong total,
            CancellationToken token, Action<string> trace)
        {
            var tried = new HashSet<Node>();
            var running = new List<Attempt>();
            Exception last = null;
            bool hedged = false;
            try
            {
                while (true)
                {
                    if (running.Count == 0)
                    {
                        var next = Start(nodes, tried, at, count, token, trace, false);
                        if (next == null) throw new IOException("加速线路分段下载失败。", last);
                        running.Add(next);
                    }
                    var waits = running.Select(item => (Task)item.Task).ToList();
                    Task timer = null;
                    if (!hedged && running.Count == 1 && tried.Count < nodes.Count)
                    {
                        timer = Task.Delay(TimeSpan.FromTicks(HedgeDelay(running[0].Node, count) * TimeSpan.TicksPerSecond / Stopwatch.Frequency), token);
                        waits.Add(timer);
                    }
                    var done = await Task.WhenAny(waits).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (done == timer)
                    {
                        hedged = true;
                        var hedge = Start(nodes, tried, at, count, token, trace, true);
                        if (hedge != null)
                        {
                            running.Add(hedge);
                            lock (_gate) ++_hedges;
                            trace?.Invoke("hedge start=" + at + " count=" + count);
                        }
                        continue;
                    }
                    var attempt = running.First(item => item.Task == done);
                    running.Remove(attempt);
                    try
                    {
                        var part = await attempt.Task.ConfigureAwait(false);
                        if (part.Total != total)
                        {
                            trace?.Invoke("length-mismatch start=" + at + " expectedTotal=" + total + " actualTotal=" + part.Total);
                            throw new IOException("加速线路媒体文件长度变化。");
                        }
                        RecordSuccess(attempt.Node, count, part.TransferSeconds);
                        if (attempt.Hedge) lock (_gate) ++_hedgeWins;
                        return part;
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception ex) when (!(ex is OutOfMemoryException))
                    {
                        last = ex;
                        RecordFailure(attempt.Node);
                    }
                }
            }
            finally
            {
                // Losing or abandoned attempts are cancelled; their own
                // continuation releases route state and observes the outcome.
                foreach (var item in running) { try { item.Life.Cancel(); } catch (ObjectDisposedException) { } }
            }
        }

        private Attempt Start(List<Node> nodes, HashSet<Node> tried, ulong at, int count, CancellationToken token,
            Action<string> trace, bool hedge)
        {
            var node = SelectNode(nodes, tried);
            if (node == null) return null;
            tried.Add(node);
            var life = CancellationTokenSource.CreateLinkedTokenSource(token);
            var task = RunAttemptAsync(node, at, count, life.Token, trace);
            _ = task.ContinueWith(completed =>
            {
                var observed = completed.Exception;
                life.Dispose();
            }, TaskScheduler.Default);
            return new Attempt { Node = node, Task = task, Life = life, Hedge = hedge };
        }

        private async Task<RangeChunk> RunAttemptAsync(Node node, ulong at, int count, CancellationToken token, Action<string> trace)
        {
            try { return await FetchAsync(node.Url, at, count, token, trace).ConfigureAwait(false); }
            finally { lock (_gate) --node.Active; }
        }

        private long HedgeDelay(Node node, int count)
        {
            lock (_gate)
            {
                double rate = node.BytesPerSecond;
                if (rate <= 0) return MinimumHedgeTicks * 2;
                long expected = (long)(count / rate * Stopwatch.Frequency);
                return Math.Max(MinimumHedgeTicks, Math.Min(MaximumHedgeTicks, expected * 5 / 2));
            }
        }

        private static long Backoff(int failures)
            => Math.Min(60L, 3L << Math.Min(failures - 1, 4)) * Stopwatch.Frequency;

        private FileRoutes Routes(string url)
        {
            Uri uri;
            string key = Uri.TryCreate(url, UriKind.Absolute, out uri) ? uri.AbsolutePath : "";
            lock (_gate)
            {
                FileRoutes file;
                if (_files.TryGetValue(key, out file)) return file;
                if (_files.Count >= MaximumRememberedFiles) _files.Remove(_files.Keys.First());
                file = new FileRoutes();
                _files.Add(key, file);
                return file;
            }
        }

        private Node Route(string url)
        {
            lock (_gate)
            {
                Node node;
                if (_performance.TryGetValue(url, out node)) return node;
                node = new Node(url);
                if (_performance.Count < MaximumRememberedRoutes) _performance.Add(url, node);
                return node;
            }
        }

        private Node SelectNode(IReadOnlyList<Node> nodes, HashSet<Node> tried)
        {
            lock (_gate)
            {
                long now = Stopwatch.GetTimestamp();
                var available = nodes.Where(node => !tried.Contains(node)).ToArray();
                if (available.Length == 0) return null;
                // Take a sample from unmeasured routes. After that, only split
                // work when a route can compete with the busy faster routes.
                double unmeasured = Math.Max(1, available.Max(node => node.BytesPerSecond));
                var chosen = available.OrderBy(node => now < node.RetryAt)
                    .ThenByDescending(node =>
                    {
                        double rate = node.BytesPerSecond;
                        // One media sample is enough to measure an unknown
                        // route. Keep other chunks on measured routes meanwhile.
                        double estimate = rate > 0 ? rate : node.Active == 0 ? unmeasured : 1;
                        return estimate / (1 + node.Active);
                    }).First();
                ++chosen.Active;
                return chosen;
            }
        }

        private void RecordSuccess(Node node, int count, double seconds)
        {
            lock (_gate)
            {
                node.Failures = 0;
                // Probe hits and short tails are not representative of a media
                // chunk. Keep fast/slow decisions tied to completed media bytes.
                if (count < ChunkBytes / 2) return;
                double rate = count / Math.Max(.001, seconds);
                double previous = node.BytesPerSecond;
                // React to throttling after a fast initial burst. Give a brief
                // speed spike less influence than a newly slow media chunk.
                node.BytesPerSecond = previous <= 0 ? rate : rate < previous
                    ? .8 * rate + .2 * previous : .4 * rate + .6 * previous;
            }
        }

        private void RecordFailure(Node node)
        {
            lock (_gate)
            {
                ++_chunkRetries;
                ++node.Failures;
                node.RetryAt = Stopwatch.GetTimestamp() + Backoff(node.Failures);
            }
        }

        private async Task<RangeChunk> FetchAsync(string url, ulong start, int count, CancellationToken token, Action<string> trace)
        {
            string range = trace == null ? null : " start=" + start + " end=" + (start + (ulong)count - 1)
                + " host=" + (Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.DnsSafeHost : "-");
            long queuedAt = trace == null ? 0 : Stopwatch.GetTimestamp();
            trace?.Invoke("slot-queued" + range);
            try { await _connections.WaitAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                trace?.Invoke("slot-cancelled" + range);
                throw;
            }
            try
            {
                trace?.Invoke("slot-acquired" + range + " queueMs=" + (Stopwatch.GetTimestamp() - queuedAt) * 1000 / Stopwatch.Frequency);
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(count <= AnchorBytes ? 6 : 12));
                    var clock = Stopwatch.StartNew();
                    var result = await _fetch(url, start, count, timeout.Token).ConfigureAwait(false);
                    if (result == null || result.Bytes == null || result.Bytes.Length != count || result.Offset != start
                        || start > ulong.MaxValue - (ulong)count || result.Total < start + (ulong)count)
                        throw new IOException("加速线路返回的媒体分段不完整或范围无效。");
                    result.TransferSeconds = clock.Elapsed.TotalSeconds;
                    return result;
                }
            }
            finally { _connections.Release(); }
        }

        internal sealed class RangeChunk
        {
            internal byte[] Bytes;
            internal ulong Offset, Total;
            internal string ContentType;
            internal double TransferSeconds;
        }
        internal sealed class RangeDownload { internal byte[] Bytes; internal ulong Total; internal string ContentType; }
        private sealed class Node
        {
            internal readonly string Url;
            internal int Active, Failures;
            internal long RetryAt;
            internal double BytesPerSecond;
            internal Node(string url) { Url = url; }
        }
        private sealed class FileRoutes
        {
            internal ulong Total;
            internal readonly HashSet<string> Verified = new HashSet<string>(StringComparer.Ordinal);
            internal readonly HashSet<string> Rejected = new HashSet<string>(StringComparer.Ordinal);
            internal readonly Dictionary<string, int> ProbeMisses = new Dictionary<string, int>(StringComparer.Ordinal);
            internal readonly Dictionary<string, long> ProbeRetryAt = new Dictionary<string, long>(StringComparer.Ordinal);
        }
    }
}
