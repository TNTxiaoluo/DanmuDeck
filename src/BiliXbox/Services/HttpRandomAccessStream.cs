using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.Web.Http;
using Windows.Web.Http.Filters;

namespace BiliXbox.Services
{
    /// <summary>
    /// Seekable, read-only MP4 transport with the headers required by Bilibili CDNs.
    /// Clones share one transport, in-flight requests and bounded range cache; sequential reads fetch one
    /// block ahead without waiting for a whole megabyte before first playback.
    /// </summary>
    public sealed class HttpRandomAccessStream : IRandomAccessStream
    {
        private const uint BlockSize = 256 * 1024;
        private readonly Uri _uri;
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private readonly SemaphoreSlim _readGate = new SemaphoreSlim(1, 1);
        private readonly object _stateGate = new object();
        private readonly BlockCache _blocks;
        private readonly DownloadSpeedMeter _downloadSpeed;
        private ulong _size;
        private ulong _position;
        private long _seekVersion;
        private ulong _prefetchStart;
        private Task<CachedBlock> _prefetchTask;
        private CancellationTokenSource _prefetchCancellation;
        private bool _disposed;

        private HttpRandomAccessStream(Uri uri, BlockCache blocks = null, DownloadSpeedMeter downloadSpeed = null)
        {
            _uri = uri;
            _downloadSpeed = downloadSpeed ?? new DownloadSpeedMeter();
            _blocks = blocks ?? new BlockCache(uri, _downloadSpeed);
            _blocks.AddReference();
        }

        public static Task<HttpRandomAccessStream> OpenAsync(Uri uri, CancellationToken ct = default(CancellationToken))
        {
            return OpenAsync(uri, new DownloadSpeedMeter(), ct);
        }

        internal static async Task<HttpRandomAccessStream> OpenAsync(Uri uri, DownloadSpeedMeter downloadSpeed,
            CancellationToken ct)
        {
            if (uri == null) throw new ArgumentNullException(nameof(uri));
            if (downloadSpeed == null) throw new ArgumentNullException(nameof(downloadSpeed));
            if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps)
                throw new ArgumentException("视频地址必须使用 HTTPS。", nameof(uri));

            var stream = new HttpRandomAccessStream(uri, downloadSpeed: downloadSpeed);
            try
            {
                await stream.GetBlockAsync(0, ct).ConfigureAwait(false);
                stream._size = stream._blocks.Size;
                return stream;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        public bool CanRead { get { lock (_stateGate) return !_disposed; } }
        public bool CanWrite { get { return false; } }
        public double CurrentDownloadBytesPerSecond { get { return _downloadSpeed.CurrentDownloadBytesPerSecond; } }
        internal long TotalDownloadedBytes { get { return _downloadSpeed.TotalDownloadedBytes; } }

        public ulong Size
        {
            get { lock (_stateGate) { ThrowIfDisposed(); return _size; } }
            set { throw new NotSupportedException("视频流为只读。"); }
        }

        public ulong Position
        {
            get { lock (_stateGate) { ThrowIfDisposed(); return _position; } }
        }

        public void Seek(ulong position)
        {
            CancellationTokenSource obsoletePrefetch = null;
            lock (_stateGate)
            {
                ThrowIfDisposed();
                _position = position;
                _seekVersion++;
                ulong targetBlock = position / BlockSize * BlockSize;
                if (_prefetchStart != targetBlock && _prefetchStart != targetBlock + BlockSize)
                    obsoletePrefetch = _prefetchCancellation;
            }
            // Release this stream's obsolete prefetch. A shared request is only
            // canceled when no other clone or foreground reader still needs it.
            try { obsoletePrefetch?.Cancel(); }
            // A concurrent read may have completed and disposed this old request.
            catch (ObjectDisposedException) { }
        }

        public IRandomAccessStream CloneStream()
        {
            return CreateCloneAt(0);
        }

        public IInputStream GetInputStreamAt(ulong position)
        {
            return CreateCloneAt(position);
        }

        private HttpRandomAccessStream CreateCloneAt(ulong position)
        {
            lock (_stateGate)
            {
                ThrowIfDisposed();
                return new HttpRandomAccessStream(_uri, _blocks, _downloadSpeed)
                {
                    _size = _size,
                    _position = position
                };
            }
        }

        public IOutputStream GetOutputStreamAt(ulong position)
        {
            throw new NotSupportedException("视频流为只读。");
        }

        public IAsyncOperationWithProgress<IBuffer, uint> ReadAsync(IBuffer buffer, uint count, InputStreamOptions options)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (count > buffer.Capacity) throw new ArgumentOutOfRangeException(nameof(count));

            return AsyncInfo.Run<IBuffer, uint>(async (ct, progress) =>
            {
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token))
                {
                    await _readGate.WaitAsync(linked.Token).ConfigureAwait(false);
                    try
                    {
                        ulong position;
                        long seekVersion;
                        lock (_stateGate)
                        {
                            ThrowIfDisposed();
                            position = _position;
                            seekVersion = _seekVersion;
                        }

                        buffer.Length = 0;
                        if (count == 0 || position >= _size) return buffer;
                        var wanted = (uint)Math.Min((ulong)count, _size - position);
                        uint written = 0;
                        while (written < wanted)
                        {
                            linked.Token.ThrowIfCancellationRequested();
                            ulong blockStart = position / BlockSize * BlockSize;
                            var block = await GetBlockAsync(blockStart, linked.Token).ConfigureAwait(false);
                            var offset = (uint)(position - block.Start);
                            var available = Math.Min(wanted - written, block.Bytes.Length - offset);
                            block.Bytes.CopyTo(offset, buffer, written, available);
                            written += available;
                            position += available;
                            buffer.Length = written;
                            progress.Report(written);
                            StartReadAhead(blockStart + BlockSize);
                            if ((options & InputStreamOptions.Partial) != 0) break;
                        }

                        lock (_stateGate)
                        {
                            ThrowIfDisposed();
                            if (_seekVersion != seekVersion)
                                throw new IOException("读取期间视频流位置已改变，请使用独立的输入流进行并发读取。");
                            _position = position;
                        }
                        return buffer;
                    }
                    finally { _readGate.Release(); }
                }
            });
        }

        private Task<CachedBlock> GetBlockAsync(ulong start, CancellationToken ct)
            => _blocks.GetAsync(start, ct);

        private void StartReadAhead(ulong start)
        {
            CachedBlock cached;
            if (_blocks.TryGet(start, out cached)) return;
            lock (_stateGate)
            {
                if (_disposed || start >= _size) return;
                if (_prefetchTask != null && !_prefetchTask.IsCompleted) return;
                _prefetchCancellation?.Dispose();
                _prefetchCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                _prefetchStart = start;
                _prefetchTask = PrefetchAsync(start, _prefetchCancellation.Token);
            }
        }

        private async Task<CachedBlock> PrefetchAsync(ulong start, CancellationToken ct)
        {
            try { return await GetBlockAsync(start, ct).ConfigureAwait(false); }
            // Failed speculative reads are retried only if the decoder actually
            // requests that range; never fail playback from a background task.
            catch (Exception) { return null; }
        }

        public IAsyncOperationWithProgress<uint, uint> WriteAsync(IBuffer buffer)
        {
            throw new NotSupportedException("视频流为只读。");
        }

        public IAsyncOperation<bool> FlushAsync()
        {
            lock (_stateGate) ThrowIfDisposed();
            return AsyncInfo.Run<bool>(ct => Task.FromResult(true));
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(HttpRandomAccessStream));
        }

        public void Dispose()
        {
            lock (_stateGate)
            {
                if (_disposed) return;
                _disposed = true;
            }
            // Cancel only this reader. The transport stays alive for other
            // clones and is released when the last stream has been disposed.
            _lifetime.Cancel();
            _prefetchCancellation?.Dispose();
            _lifetime.Dispose();
            _blocks.ReleaseReference();
        }

        private sealed class CachedBlock
        {
            public ulong Start;
            public IBuffer Bytes;
        }

        private sealed class BlockCache
        {
            // 32 x 256 KiB = at most 8 MiB, shared by all decoder clones.
            private const int MaximumBlocks = 32;
            private readonly object _gate = new object();
            private readonly Dictionary<ulong, CachedBlock> _items = new Dictionary<ulong, CachedBlock>();
            private readonly LinkedList<ulong> _recency = new LinkedList<ulong>();
            private readonly Dictionary<ulong, Flight> _flights = new Dictionary<ulong, Flight>();
            private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
            private readonly HttpBaseProtocolFilter _filter;
            private readonly HttpClient _http;
            private readonly Uri _uri;
            private readonly DownloadSpeedMeter _meter;
            private int _references;
            private bool _disposed;
            private ulong _size;

            internal BlockCache(Uri uri, DownloadSpeedMeter meter)
            {
                _uri = uri; _meter = meter;
                _filter = new HttpBaseProtocolFilter { AutomaticDecompression = false };
                _filter.CacheControl.ReadBehavior = HttpCacheReadBehavior.NoCache;
                _filter.CacheControl.WriteBehavior = HttpCacheWriteBehavior.NoCache;
                _http = new HttpClient(_filter);
                _http.DefaultRequestHeaders.Referer = new Uri("https://www.bilibili.com/");
                _http.DefaultRequestHeaders.TryAppendWithoutValidation("User-Agent", "Mozilla/5.0 BiliXbox/1.0");
                _http.DefaultRequestHeaders.TryAppendWithoutValidation("Accept-Encoding", "identity");
            }

            internal ulong Size { get { lock (_gate) return _size; } }

            internal void AddReference()
            {
                lock (_gate)
                {
                    if (_disposed) throw new ObjectDisposedException(nameof(BlockCache));
                    ++_references;
                }
            }

            internal void ReleaseReference()
            {
                lock (_gate)
                {
                    if (--_references != 0) return;
                    _disposed = true;
                    _items.Clear(); _recency.Clear();
                }
                _lifetime.Cancel();
                _http.Dispose(); _filter.Dispose(); _lifetime.Dispose();
            }

            internal async Task<CachedBlock> GetAsync(ulong start, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                Flight flight;
                bool startDownload = false;
                lock (_gate)
                {
                    if (_disposed) throw new ObjectDisposedException(nameof(BlockCache));
                    CachedBlock cached;
                    if (TryGet(start, out cached)) return cached;
                    if (!_flights.TryGetValue(start, out flight))
                    {
                        flight = new Flight(_lifetime.Token);
                        _flights.Add(start, flight);
                        startDownload = true;
                    }
                    ++flight.Readers;
                }
                if (startDownload) _ = FetchFlightAsync(start, flight);
                try
                {
                    // Cancel this wait independently. Another clone may still
                    // need the exact same range and must keep its download.
                    var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    using (token.Register(() => canceled.TrySetResult(true)))
                    {
                        if (await Task.WhenAny(flight.Completion.Task, canceled.Task).ConfigureAwait(false) != flight.Completion.Task)
                            token.ThrowIfCancellationRequested();
                        token.ThrowIfCancellationRequested();
                        return await flight.Completion.Task.ConfigureAwait(false);
                    }
                }
                finally
                {
                    bool cancel = false;
                    lock (_gate)
                    {
                        if (--flight.Readers == 0 && !flight.Completion.Task.IsCompleted)
                        {
                            Flight current;
                            if (_flights.TryGetValue(start, out current) && current == flight) _flights.Remove(start);
                            cancel = true;
                        }
                    }
                    if (cancel) { try { flight.Cancellation.Cancel(); } catch (ObjectDisposedException) { } }
                }
            }

            private async Task FetchFlightAsync(ulong start, Flight flight)
            {
                try
                {
                    using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(flight.Cancellation.Token))
                    {
                        timeout.CancelAfter(TimeSpan.FromSeconds(30));
                        var block = await FetchBlockAsync(start, timeout.Token).ConfigureAwait(false);
                        lock (_gate)
                        {
                            timeout.Token.ThrowIfCancellationRequested();
                            if (_disposed) throw new OperationCanceledException();
                            Add(block);
                            RemoveFlight(start, flight);
                            flight.Completion.TrySetResult(block);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    lock (_gate) { RemoveFlight(start, flight); flight.Completion.TrySetCanceled(); }
                }
                catch (Exception error)
                {
                    lock (_gate) { RemoveFlight(start, flight); flight.Completion.TrySetException(error); }
                }
                finally { flight.Cancellation.Dispose(); }
            }

            private void RemoveFlight(ulong start, Flight flight)
            {
                Flight current;
                if (_flights.TryGetValue(start, out current) && current == flight) _flights.Remove(start);
            }

            private async Task<CachedBlock> FetchBlockAsync(ulong start, CancellationToken token)
            {
                ulong end = checked(start + BlockSize - 1);
                using (var request = new HttpRequestMessage(HttpMethod.Get, _uri))
                {
                    request.Headers.TryAppendWithoutValidation("Range", "bytes=" + start + "-" + end);
                    using (var response = await _http.SendRequestAsync(request, HttpCompletionOption.ResponseHeadersRead).AsTask(token).ConfigureAwait(false))
                    {
                        response.EnsureSuccessStatusCode();
                        var headers = response.Content.Headers;
                        ulong total, expected;
                        if (response.StatusCode == HttpStatusCode.PartialContent)
                        {
                            var range = headers.ContentRange;
                            if (range == null || !string.Equals(range.Unit, "bytes", StringComparison.OrdinalIgnoreCase)
                                || !range.Length.HasValue || range.Length.Value == 0 || !range.FirstBytePosition.HasValue
                                || !range.LastBytePosition.HasValue || range.FirstBytePosition.Value != start
                                || start >= range.Length.Value || range.LastBytePosition.Value != Math.Min(end, range.Length.Value - 1))
                                throw new IOException("播放线路返回的字节范围无效。");
                            total = range.Length.Value;
                            expected = range.LastBytePosition.Value - start + 1;
                        }
                        else if (response.StatusCode == HttpStatusCode.Ok && start == 0 && headers.ContentLength.HasValue
                            && headers.ContentLength.Value > 0 && headers.ContentLength.Value <= BlockSize)
                            total = expected = headers.ContentLength.Value;
                        else throw new IOException("播放线路不支持分段读取。");
                        if (headers.ContentLength.HasValue && headers.ContentLength.Value != expected)
                            throw new IOException("播放线路返回的分段长度不一致。");
                        lock (_gate)
                        {
                            if (_size != 0 && _size != total) throw new IOException("播放文件长度已变化，请重新加载。");
                            _size = total;
                        }
                        var bytes = new Windows.Storage.Streams.Buffer((uint)expected);
                        using (var input = await response.Content.ReadAsInputStreamAsync().AsTask(token).ConfigureAwait(false))
                        {
                            uint read = 0;
                            while (read < expected)
                            {
                                var buffer = new Windows.Storage.Streams.Buffer((uint)Math.Min(65536UL, expected - read));
                                var part = await input.ReadAsync(buffer, buffer.Capacity, InputStreamOptions.Partial).AsTask(token).ConfigureAwait(false);
                                token.ThrowIfCancellationRequested();
                                if (part.Length == 0) throw new IOException("播放线路返回的媒体分段不完整。");
                                _meter.AddReceivedBytes(part.Length);
                                part.CopyTo(0, bytes, read, part.Length);
                                read += part.Length;
                            }
                            bytes.Length = read;
                        }
                        return new CachedBlock { Start = start, Bytes = bytes };
                    }
                }
            }

            private sealed class Flight
            {
                internal int Readers;
                internal readonly CancellationTokenSource Cancellation;
                internal readonly TaskCompletionSource<CachedBlock> Completion = new TaskCompletionSource<CachedBlock>(TaskCreationOptions.RunContinuationsAsynchronously);
                internal Flight(CancellationToken lifetime)
                {
                    Cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
                    // All readers may cancel before a failed request completes.
                    // Observe failures even in that case; live readers still get
                    // the original exception through the same task.
                    Completion.Task.ContinueWith(task => { var ignored = task.Exception; },
                        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
            }

            public bool TryGet(ulong start, out CachedBlock block)
            {
                lock (_gate)
                {
                    if (!_items.TryGetValue(start, out block)) return false;
                    _recency.Remove(start);
                    _recency.AddLast(start);
                    return true;
                }
            }

            public void Add(CachedBlock block)
            {
                lock (_gate)
                {
                    _items[block.Start] = block;
                    _recency.Remove(block.Start);
                    _recency.AddLast(block.Start);
                    while (_items.Count > MaximumBlocks)
                    {
                        ulong oldest = _recency.First.Value;
                        _recency.RemoveFirst();
                        _items.Remove(oldest);
                    }
                }
            }
        }
    }
}
