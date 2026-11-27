using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.Web.Http;
using Windows.Web.Http.Filters;
using Windows.Web.Http.Headers;

namespace BiliXbox.Services
{
    // Returning a real 206 response lets AdaptiveMediaSource retain its native
    // SegmentBase interpretation, synchronisation and decoder management.
    internal sealed class RangeAccelerationFilter : IHttpFilter
    {
        private readonly IHttpFilter _inner;
        private readonly DashRoutePolicy _routes;
        private readonly DownloadSpeedMeter _meter;
        private readonly Action<string> _trace;
        private readonly ParallelRangeDownloader _downloader;
        private readonly SequentialPrefetchCache _prefetch;
        private readonly Dictionary<string, (string[] Urls, KeyValuePair<string, string>[] Headers, ulong Total)> _prefetchSources
            = new Dictionary<string, (string[], KeyValuePair<string, string>[], ulong)>(StringComparer.Ordinal);
        private readonly OrderedRangeReadStream.WaitStatistics _waits = new OrderedRangeReadStream.WaitStatistics();
        private readonly object _sampleGate = new object();
        private (TimeSpan? Duration, int Status, string Host) _lastMediaRequest;
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private int _nextRange;
        private int _disposed, _accelerated, _fallback, _bypass, _bodyFailed, _producerFailures;
        // Native fallback for the whole source only after repeated failures:
        // one transient overseas timeout must not disable acceleration.
        private const int BypassAfterFailures = 3;
        private bool Bypassed => Volatile.Read(ref _bypass) != 0;
        internal bool HasBodyFailure => Volatile.Read(ref _bodyFailed) != 0;
        internal (TimeSpan? Duration, int Status, string Host) LastMediaRequest
        { get { lock (_sampleGate) return Bypassed ? default((TimeSpan?, int, string)) : _lastMediaRequest; } }
        internal string WaitDiagnostic => (Bypassed ? "加速已转原生" : "并发加速")
            + " · 读流待数据 " + _waits.ReadWaiters + " · 预读等消费 " + _waits.WindowWaiters;
        internal double CurrentDownloadBytesPerSecond => _meter.CurrentDownloadBytesPerSecond;
        internal string Diagnostic => "acceleration=on parallel=" + ParallelRangeDownloader.Concurrency + " acceleratedRanges=" + Volatile.Read(ref _accelerated)
            + " nativeFallbacks=" + Volatile.Read(ref _fallback) + " reservedBytes=" + _downloader.ReservedBytes
            + " accelerationReceivedBytes=" + _meter.TotalDownloadedBytes + " bypass=" + Bypassed
            + " bodyFailed=" + HasBodyFailure + _downloader.PerformanceDiagnostic
            + " readWaiters=" + _waits.ReadWaiters + " readWaitMs=" + _waits.ReadMilliseconds
            + " windowWaiters=" + _waits.WindowWaiters + " windowWaitMs=" + _waits.WindowMilliseconds + _prefetch.Diagnostic;

        internal RangeAccelerationFilter(IHttpFilter inner, DashRoutePolicy routes, DownloadSpeedMeter meter,
            Action<string> trace = null)
        {
            _inner = inner; _routes = routes; _meter = meter; _trace = trace;
            _downloader = new ParallelRangeDownloader(FetchAsync);
            _prefetch = new SequentialPrefetchCache(FetchPieceAsync, trace == null ? null : new Action<string>(detail => trace(detail + " utc=" + DateTimeOffset.UtcNow.ToString("O"))));
        }

        // One prefetch piece, downloaded over the same verified parallel routes
        // and media headers as the request that started the window.
        private async Task<byte[]> FetchPieceAsync(string key, ulong start, int length, CancellationToken token)
        {
            (string[] Urls, KeyValuePair<string, string>[] Headers, ulong Total) source;
            lock (_prefetchSources) if (!_prefetchSources.TryGetValue(key, out source)) return null;
            if (Bypassed) return null;
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token))
            {
                var trace = TraceRange(start, length);
                _context.Value = new RequestContext { Headers = source.Headers, Trace = trace };
                trace?.Invoke("prefetch-piece-start");
                var result = await _downloader.DownloadAsync(source.Urls, start, length, linked.Token, trace: trace).ConfigureAwait(false);
                return result?.Bytes;
            }
        }

        private static HttpResponseMessage CachedResponse(HttpRequestMessage request, byte[] bytes, ulong start, ulong total)
        {
            var content = new HttpBufferContent(bytes.AsBuffer());
            content.Headers.ContentLength = (ulong)bytes.Length;
            content.Headers.ContentRange = new HttpContentRangeHeaderValue(start, start + (ulong)bytes.Length - 1, total);
            content.Headers.TryAppendWithoutValidation("Content-Type", "video/mp4");
            return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content, RequestMessage = request };
        }

        public IAsyncOperationWithProgress<HttpResponseMessage, HttpProgress> SendRequestAsync(HttpRequestMessage request)
            => AsyncInfo.Run<HttpResponseMessage, HttpProgress>(async (token, progress) =>
            {
                using (var job = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token))
                {
                    var ct = job.Token; ct.ThrowIfCancellationRequested();
                    string range; ulong start; int length;
                    var candidates = _routes.CandidateUris(request.RequestUri);
                    // Add the same signed path on Bilibili's other upos mirrors.
                    // The downloader verifies each mirror's bytes before use.
                    if (candidates.Count > 0 && _routes.ResourceKey(request.RequestUri) == "video")
                        candidates = CdnMirrors.Expand(candidates);
                    if (string.Equals(request.Method?.Method, "GET", StringComparison.OrdinalIgnoreCase)
                        && !Bypassed && candidates.Count > 0 && _routes.ResourceKey(request.RequestUri) == "video"
                        && !request.Headers.ContainsKey("Authorization") && !request.Headers.ContainsKey("Cookie")
                        && request.Headers.TryGetValue("Range", out range)
                        && ParallelRangeDownloader.TryParseRange(range, out start, out length))
                    {
                        // Clone only the existing media headers, retaining signed
                        // URLs verbatim. API/account credentials never enter this path.
                        var headers = request.Headers.Where(pair => !string.Equals(pair.Key, "Range", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(pair.Key, "Host", StringComparison.OrdinalIgnoreCase)).ToArray();
                        var context = new RequestContext { Headers = headers, Trace = TraceRange(start, length) };
                        string fileKey = request.RequestUri.AbsolutePath;
                        var urls = candidates.Select(uri => uri.AbsoluteUri).ToArray();
                        ulong knownTotal = 0;
                        lock (_prefetchSources)
                        {
                            if (_prefetchSources.TryGetValue(fileKey, out var known)) knownTotal = known.Total;
                            _prefetchSources[fileKey] = (urls, headers, knownTotal);
                        }
                        if (knownTotal > 0)
                        {
                            var cached = await _prefetch.TryGetAsync(fileKey, start, length, ct).ConfigureAwait(false);
                            if (cached != null)
                            {
                                context.Trace?.Invoke("prefetch-hit bytes=" + cached.Length);
                                Interlocked.Increment(ref _accelerated);
                                _prefetch.Advance(fileKey, start, start + (ulong)length, knownTotal);
                                return CachedResponse(request, cached, start, knownTotal);
                            }
                        }
                        _context.Value = context;
                        try
                        {
                            var stream = new OrderedRangeReadStream(length, token, _lifetime.Token, _waits, context.Trace);
                            var ready = new TaskCompletionSource<ParallelRangeDownloader.RangeDownload>(TaskCreationOptions.RunContinuationsAsynchronously);
                            // ProduceAsync catches and publishes body errors even
                            // after headers return; no abandoned task owns a range.
                            _ = ProduceAsync(candidates.Select(uri => uri.AbsoluteUri).ToArray(), start, length, stream, ready, context.Trace);
                            var result = await ready.Task.ConfigureAwait(false);
                            if (result != null)
                            {
                                var content = new HttpStreamContent(stream.AsInputStream());
                                content.Headers.ContentLength = (ulong)length;
                                // Total length is retained per request by the fetch
                                // context, not shared between simultaneous audio/video.
                                content.Headers.ContentRange = new HttpContentRangeHeaderValue(start, start + (ulong)length - 1, result.Total);
                                content.Headers.TryAppendWithoutValidation("Content-Type", result.ContentType ?? "video/mp4");
                                Interlocked.Increment(ref _accelerated);
                                lock (_prefetchSources) _prefetchSources[fileKey] = (urls, headers, result.Total);
                                _prefetch.Advance(fileKey, start, start + (ulong)length, result.Total);
                                return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content, RequestMessage = request };
                            }
                            stream.Dispose();
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (Exception ex) when (!(ex is OutOfMemoryException)) { }
                        finally { _context.Value = null; }
                        Interlocked.Increment(ref _fallback);
                        context.Trace?.Invoke("native-fallback host=" + request.RequestUri.DnsSafeHost);
                    }
                    ct.ThrowIfCancellationRequested();
                    return await _inner.SendRequestAsync(request).AsTask(ct, progress).ConfigureAwait(false);
                }
            });

        private readonly AsyncLocal<RequestContext> _context = new AsyncLocal<RequestContext>();
        private Action<string> TraceRange(ulong start, int length)
        {
            if (_trace == null) return null;
            string range = "range job=" + Interlocked.Increment(ref _nextRange) + " parentStart=" + start
                + " parentEnd=" + (start + (ulong)length - 1) + " ";
            return detail => _trace(range + detail + " utc=" + DateTimeOffset.UtcNow.ToString("O"));
        }

        private async Task ProduceAsync(IReadOnlyList<string> urls, ulong start, int length, OrderedRangeReadStream stream,
            TaskCompletionSource<ParallelRangeDownloader.RangeDownload> ready, Action<string> trace)
        {
            try
            {
                var result = await _downloader.DownloadAsync(urls, start, length, stream.ProducerToken,
                    value => ready.TrySetResult(value), stream.AddAsync, stream.WaitForWindowAsync, trace).ConfigureAwait(false);
                ready.TrySetResult(result);
                stream.Complete();
            }
            catch (Exception ex)
            {
                if (!stream.ProducerToken.IsCancellationRequested)
                {
                    // Before committing the response, the same request can be
                    // retried natively without replacing the playback source,
                    // so only repeated failures disable acceleration. A failure
                    // after the 206 headers broke a delivered body: stop at once.
                    bool committed = ready.Task.Status == TaskStatus.RanToCompletion && ready.Task.Result != null;
                    if (committed || Interlocked.Increment(ref _producerFailures) >= BypassAfterFailures)
                        Interlocked.Exchange(ref _bypass, 1);
                    if (committed) Interlocked.Exchange(ref _bodyFailed, 1);
                }
                stream.Complete(ex);
                if (ready.TrySetException(ex)) stream.Dispose();
            }
        }
        private sealed class RequestContext
        {
            internal KeyValuePair<string, string>[] Headers;
            internal Action<string> Trace;
            internal int NextAttempt;
        }

        private async Task<ParallelRangeDownloader.RangeChunk> FetchAsync(string url, ulong start, int count, CancellationToken token)
        {
            var uri = new Uri(url);
            var context = _context.Value;
            if (context == null) throw new InvalidOperationException("加速下载上下文已失效。");
            var trace = context.Trace;
            var clock = Stopwatch.StartNew();
            long headersMs = -1, firstBodyMs = -1;
            int status = 0, received = 0, error = 0;
            string range = trace == null ? null : " attempt=" + Interlocked.Increment(ref context.NextAttempt)
                + " start=" + start + " end=" + (start + (ulong)count - 1) + " host=" + uri.DnsSafeHost;
            trace?.Invoke("fetch-start" + range);
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, uri))
                {
                    foreach (var pair in context.Headers) request.Headers.TryAppendWithoutValidation(pair.Key, pair.Value);
                    request.Headers.TryAppendWithoutValidation("Range", "bytes=" + start + "-" + (start + (ulong)count - 1));
                    using (var response = await _inner.SendRequestAsync(request).AsTask(token).ConfigureAwait(false))
                    {
                        headersMs = clock.ElapsedMilliseconds;
                        status = (int)response.StatusCode;
                        var contentRange = response.Content?.Headers.ContentRange;
                        if (response.StatusCode != HttpStatusCode.PartialContent || contentRange == null
                            || !string.Equals(contentRange.Unit, "bytes", StringComparison.OrdinalIgnoreCase)
                            || contentRange.FirstBytePosition != start || contentRange.LastBytePosition != start + (ulong)count - 1
                            || !contentRange.Length.HasValue || contentRange.Length.Value < start + (ulong)count
                            || (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength != (ulong)count))
                            throw new IOException("加速线路返回的 HTTP Range 无效。");
                        var output = new byte[count];
                        using (var input = await response.Content.ReadAsInputStreamAsync().AsTask(token).ConfigureAwait(false))
                        {
                            while (received < count)
                            {
                                var buffer = new Windows.Storage.Streams.Buffer((uint)Math.Min(65536, count - received));
                                var part = await input.ReadAsync(buffer, buffer.Capacity, InputStreamOptions.Partial).AsTask(token).ConfigureAwait(false);
                                if (part.Length == 0) throw new IOException("加速线路返回的媒体分段不完整。");
                                if (firstBodyMs < 0) firstBodyMs = clock.ElapsedMilliseconds;
                                _meter.AddReceivedBytes(part.Length);
                                part.CopyTo(0, output, received, (int)part.Length);
                                received += (int)part.Length;
                            }
                        }
                        return new ParallelRangeDownloader.RangeChunk { Bytes = output, Offset = start,
                            Total = contentRange.Length.Value, ContentType = response.Content.Headers.ContentType?.ToString() };
                    }
                }
            }
            catch (Exception ex) { error = ex.HResult; throw; }
            finally
            {
                var elapsed = clock.Elapsed;
                if (count > ParallelRangeDownloader.AnchorBytes && !token.IsCancellationRequested)
                    lock (_sampleGate) _lastMediaRequest = (elapsed, status, uri.DnsSafeHost);
                trace?.Invoke("fetch-end" + range + " http=" + status + " headersMs=" + headersMs
                    + " firstBodyReadMs=" + firstBodyMs + " elapsedMs=" + (long)elapsed.TotalMilliseconds
                    + " received=" + received + " cancelled=" + token.IsCancellationRequested + " hresult=0x" + error.ToString("X8"));
            }
        }

        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) { _lifetime.Cancel(); _prefetch.Dispose(); } }
    }
}
