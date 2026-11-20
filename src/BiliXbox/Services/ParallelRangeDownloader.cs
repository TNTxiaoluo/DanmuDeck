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
    internal sealed class ParallelRangeDownloader
    {
        internal const int Concurrency = 4, ChunkBytes = 512 * 1024, AnchorBytes = 16 * 1024;
        internal const int MaximumRangeBytes = 16 * 1024 * 1024, MaximumReservedBytes = 24 * 1024 * 1024;
        private const int MaximumRememberedRoutes = 16;
        private static readonly long FailureCooldown = 10 * Stopwatch.Frequency;
        private readonly Func<string, ulong, int, CancellationToken, Task<RangeChunk>> _fetch;
        private readonly SemaphoreSlim _connections = new SemaphoreSlim(Concurrency);
        private readonly object _gate = new object();
        private readonly Dictionary<string, Node> _performance = new Dictionary<string, Node>(StringComparer.Ordinal);
        private int _reserved;
        private int _chunkRetries;
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
                        + " videoChunkRetries=" + _chunkRetries;
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
                    var candidates = urls.Distinct(StringComparer.Ordinal).Take(3).ToArray();
                    RangeChunk anchor = null;
                    var verified = new List<Node>();
                    using (var probesLife = CancellationTokenSource.CreateLinkedTokenSource(job.Token))
                    {
                        var probes = candidates.Select(async url =>
                        {
                            try { return Tuple.Create(url, await FetchAsync(url, start, AnchorBytes, probesLife.Token, trace).ConfigureAwait(false)); }
                            catch (OperationCanceledException) when (job.IsCancellationRequested) { throw; }
                            catch (Exception ex) when (!(ex is OutOfMemoryException)) { return null; }
                        }).ToArray();
                        try
                        {
                        var pending = probes.ToList();
                        while (pending.Count > 0 && anchor == null)
                        {
                            var completed = await Task.WhenAny(pending).ConfigureAwait(false);
                            pending.Remove(completed);
                            var result = await completed.ConfigureAwait(false);
                            if (result != null && result.Item2.Total >= start + (ulong)length)
                            { anchor = result.Item2; }
                        }
                        // Do not wait eight seconds for a bad node after a good
                        // node has answered. A short grace discovers other fast
                        // nodes, then all unfinished probes are cancelled/joined.
                        if (anchor != null && pending.Count > 0)
                            await Task.WhenAny(Task.WhenAll(probes), Task.Delay(200, job.Token)).ConfigureAwait(false);
                        probesLife.Cancel();
                        var results = await Task.WhenAll(probes).ConfigureAwait(false);
                        job.Token.ThrowIfCancellationRequested();
                        // Prefer the API's current route when it also answered
                        // within the grace period, instead of an inconsistent
                        // fast backup deciding which bytes constitute this file.
                        var reference = results.FirstOrDefault(item => item != null && item.Item2.Total >= start + (ulong)length);
                        if (reference != null) anchor = reference.Item2;
                        if (anchor != null)
                            verified.AddRange(results.Where(item => item != null && item.Item2.Total == anchor.Total
                                && item.Item2.Bytes.SequenceEqual(anchor.Bytes)).Select(item => Route(item.Item1)));
                        }
                        finally
                        {
                            probesLife.Cancel();
                            try { await Task.WhenAll(probes).ConfigureAwait(false); }
                            catch (Exception ex) when (!(ex is OutOfMemoryException)) { }
                        }
                    }
                    if (anchor == null || anchor.Total < start + (ulong)length) throw new IOException("加速线路未提供完整媒体范围。");
                    // Only byte-identical anchors of this representation can
                    // share media ranges. Unverified nodes are not mixed in.
                    var nodes = verified;
                    var output = deliver == null ? new byte[length] : null;
                    if (deliver == null) Buffer.BlockCopy(anchor.Bytes, 0, output, 0, AnchorBytes);
                    else await deliver(0, anchor.Bytes, job.Token).ConfigureAwait(false);
                    ready?.Invoke(new RangeDownload { Total = anchor.Total, ContentType = anchor.ContentType });
                    int cursor = AnchorBytes;
                    var workers = Enumerable.Range(0, Concurrency).Select(async worker =>
                    {
                        try
                        {
                            while (true)
                            {
                                int offset = Interlocked.Add(ref cursor, ChunkBytes) - ChunkBytes;
                                if (offset >= length) return;
                                if (waitForWindow != null) await waitForWindow(offset, job.Token).ConfigureAwait(false);
                                int count = Math.Min(ChunkBytes, length - offset);
                                Exception last = null; var tried = new HashSet<Node>();
                                while (tried.Count < nodes.Count)
                                {
                                    var node = SelectNode(nodes, tried);
                                    tried.Add(node);
                                    try
                                    {
                                        var part = await FetchAsync(node.Url, start + (ulong)offset, count, job.Token, trace).ConfigureAwait(false);
                                        if (part.Total != anchor.Total)
                                        {
                                            trace?.Invoke("length-mismatch start=" + (start + (ulong)offset)
                                                + " expectedTotal=" + anchor.Total + " actualTotal=" + part.Total);
                                            throw new IOException("加速线路媒体文件长度变化。");
                                        }
                                        if (deliver == null) Buffer.BlockCopy(part.Bytes, 0, output, offset, count);
                                        else await deliver(offset, part.Bytes, job.Token).ConfigureAwait(false);
                                        RecordSuccess(node, count, part.TransferSeconds);
                                        last = null; break;
                                    }
                                    catch (OperationCanceledException) when (job.IsCancellationRequested) { throw; }
                                    catch (Exception ex) when (!(ex is OutOfMemoryException))
                                    {
                                        last = ex;
                                        if (!job.IsCancellationRequested)
                                            lock (_gate)
                                            {
                                                node.RetryAt = Stopwatch.GetTimestamp() + FailureCooldown;
                                                ++_chunkRetries;
                                            }
                                    }
                                    finally { lock (_gate) --node.Active; }
                                }
                                if (last != null) throw new IOException("加速线路分段下载失败。", last);
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
            // Probe hits and short tails are not representative of a media
            // chunk. Keep fast/slow decisions tied to completed media bytes.
            if (count < ChunkBytes / 2) return;
            lock (_gate)
            {
                double rate = count / Math.Max(.001, seconds);
                double previous = node.BytesPerSecond;
                // React to throttling after a fast initial burst. Give a brief
                // speed spike less influence than a newly slow media chunk.
                node.BytesPerSecond = previous <= 0 ? rate : rate < previous
                    ? .8 * rate + .2 * previous : .4 * rate + .6 * previous;
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
                    timeout.CancelAfter(TimeSpan.FromSeconds(8));
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
            internal int Active;
            internal long RetryAt;
            internal double BytesPerSecond;
            internal Node(string url) { Url = url; }
        }
    }
}
