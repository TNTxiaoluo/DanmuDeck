using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BiliXbox.Services
{
    // The player asks for one media range at a time and only asks for the next
    // when it wants more, so bandwidth sits idle between requests. This cache
    // keeps fetching the bytes that follow the last request, one piece at a
    // time (each piece itself downloaded over many parallel connections), up
    // to a window ahead of playback. A later request inside fetched bytes is
    // answered from memory. A request elsewhere (a seek) restarts the window.
    internal sealed class SequentialPrefetchCache : IDisposable
    {
        internal const int PieceBytes = 8 * 1024 * 1024;
        internal const ulong WindowBytes = 64UL * 1024 * 1024;
        private const ulong KeepBehindBytes = 2UL * 1024 * 1024;
        private const int MaximumFiles = 3;

        internal delegate Task<byte[]> PieceFetcher(string key, ulong start, int length, CancellationToken token);

        private sealed class Chunk { internal ulong Start; internal byte[] Bytes; internal ulong End => Start + (ulong)Bytes.Length; }

        private sealed class FileState
        {
            internal string Key;
            internal ulong Total;
            internal readonly List<Chunk> Chunks = new List<Chunk>();
            internal ulong NextStart, Consumed;
            internal Task<byte[]> Inflight;
            internal ulong InflightStart, InflightEnd;
            internal CancellationTokenSource Cts = new CancellationTokenSource();
            internal long LastUse;
            internal int Hits, Misses, Pieces;
        }

        private readonly object _gate = new object();
        private readonly Dictionary<string, FileState> _files = new Dictionary<string, FileState>(StringComparer.Ordinal);
        private readonly PieceFetcher _fetch;
        private readonly Action<string> _trace;
        private bool _disposed;

        internal SequentialPrefetchCache(PieceFetcher fetch, Action<string> trace = null) { _fetch = fetch; _trace = trace; }

        internal string Diagnostic
        {
            get
            {
                lock (_gate)
                {
                    int hits = _files.Values.Sum(f => f.Hits), misses = _files.Values.Sum(f => f.Misses), pieces = _files.Values.Sum(f => f.Pieces);
                    long bytes = _files.Values.Sum(f => f.Chunks.Sum(c => (long)c.Bytes.Length));
                    return " prefetchHits=" + hits + " prefetchMisses=" + misses + " prefetchPieces=" + pieces + " prefetchBytes=" + bytes;
                }
            }
        }

        // Returns the requested bytes when they are (or are about to be) in
        // the cache; null means the caller downloads them itself.
        internal async Task<byte[]> TryGetAsync(string key, ulong start, int length, CancellationToken token)
        {
            ulong end = start + (ulong)length;
            Task<byte[]> wait = null;
            lock (_gate)
            {
                if (_disposed || !_files.TryGetValue(key, out var file)) return null;
                file.LastUse = Environment.TickCount;
                var bytes = Assemble(file, start, end);
                if (bytes != null) { file.Hits++; return bytes; }
                // The rest of the range is already being fetched: wait for it
                // instead of downloading the same bytes twice.
                bool prefixReady = start >= file.InflightStart || ContiguousFrom(file, start, file.InflightStart) >= file.InflightStart;
                if (file.Inflight != null && prefixReady && start < file.InflightEnd && end <= file.InflightEnd)
                    wait = file.Inflight;
                else { file.Misses++; return null; }
            }
            try { await wait.ConfigureAwait(false); }
            catch (Exception) { return null; }
            token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (!_files.TryGetValue(key, out var file)) return null;
                var bytes = Assemble(file, start, end);
                if (bytes != null) file.Hits++; else file.Misses++;
                return bytes;
            }
        }

        // Record that the player consumed up to consumedEnd and keep the
        // window ahead of it filled.
        internal void Advance(string key, ulong requestStart, ulong consumedEnd, ulong total)
        {
            if (total == 0) return;
            lock (_gate)
            {
                if (_disposed) return;
                if (!_files.TryGetValue(key, out var file))
                {
                    if (_files.Count >= MaximumFiles)
                    {
                        var oldest = _files.Values.OrderBy(f => f.LastUse).First();
                        oldest.Cts.Cancel(); _files.Remove(oldest.Key);
                    }
                    file = new FileState { Key = key, NextStart = consumedEnd };
                    _files[key] = file;
                }
                file.Total = total;
                file.LastUse = Environment.TickCount;
                bool known = Covered(file, requestStart, requestStart) || (requestStart >= file.Consumed && requestStart <= file.NextStart + (ulong)PieceBytes);
                if (!known)
                {
                    // A seek: drop the old window and start again from here.
                    file.Cts.Cancel(); file.Cts = new CancellationTokenSource();
                    file.Chunks.Clear(); file.Inflight = null;
                    file.NextStart = consumedEnd;
                    _trace?.Invoke("prefetch-restart key=" + Short(key) + " at=" + consumedEnd);
                }
                file.Consumed = Math.Max(file.Consumed, consumedEnd);
                file.Chunks.RemoveAll(c => c.End + KeepBehindBytes <= file.Consumed);
                if (file.NextStart < file.Consumed && !Covered(file, file.Consumed, file.Consumed)) file.NextStart = file.Consumed;
                Pump(file);
            }
        }

        private void Pump(FileState file)
        {
            if (file.Inflight != null || _disposed) return;
            if (file.NextStart >= file.Total || file.NextStart >= file.Consumed + WindowBytes) return;
            ulong start = file.NextStart;
            int length = (int)Math.Min((ulong)PieceBytes, file.Total - start);
            if (length < 64 * 1024) return;
            var cts = file.Cts;
            file.InflightStart = start; file.InflightEnd = start + (ulong)length;
            var task = _fetch(file.Key, start, length, cts.Token);
            file.Inflight = task;
            file.Pieces++;
            task.ContinueWith(done =>
            {
                lock (_gate)
                {
                    if (!ReferenceEquals(file.Inflight, task)) return;
                    file.Inflight = null;
                    if (done.Status == TaskStatus.RanToCompletion && done.Result != null && done.Result.Length == length && !cts.IsCancellationRequested)
                    {
                        file.Chunks.Add(new Chunk { Start = start, Bytes = done.Result });
                        file.Chunks.Sort((a, b) => a.Start.CompareTo(b.Start));
                        file.NextStart = start + (ulong)length;
                        Pump(file);
                    }
                    else _trace?.Invoke("prefetch-piece-failed key=" + Short(file.Key) + " start=" + start + " status=" + done.Status);
                }
            }, TaskScheduler.Default);
        }

        private static bool Covered(FileState file, ulong from, ulong to)
            => from >= to || ContiguousFrom(file, from, to) >= to;

        // Highest offset reachable from "from" through contiguous cached chunks (capped at limit).
        private static ulong ContiguousFrom(FileState file, ulong from, ulong limit)
        {
            ulong at = from;
            foreach (var chunk in file.Chunks)
            {
                if (chunk.End <= at) continue;
                if (chunk.Start > at) break;
                at = chunk.End;
                if (at >= limit) break;
            }
            return at;
        }

        private static byte[] Assemble(FileState file, ulong start, ulong end)
        {
            if (ContiguousFrom(file, start, end) < end) return null;
            var output = new byte[end - start];
            foreach (var chunk in file.Chunks)
            {
                if (chunk.End <= start || chunk.Start >= end) continue;
                ulong from = Math.Max(start, chunk.Start), to = Math.Min(end, chunk.End);
                Buffer.BlockCopy(chunk.Bytes, (int)(from - chunk.Start), output, (int)(from - start), (int)(to - from));
            }
            return output;
        }

        private static string Short(string key) => key.Length > 40 ? key.Substring(key.Length - 40) : key;

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                foreach (var file in _files.Values) file.Cts.Cancel();
                _files.Clear();
            }
        }
    }
}
