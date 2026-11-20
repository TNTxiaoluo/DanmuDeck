using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Media.Streaming.Adaptive;
using Windows.Storage.Streams;
using Windows.Web.Http;
using Windows.Web.Http.Filters;

namespace BiliXbox.Services
{
    // Keeps the manifest and its authenticated-by-URL CDN downloader alive for
    // Media Foundation. The MPD contains separate video and audio adaptations.
    internal sealed class AdaptivePlaybackSource : IDisposable
    {
        private InMemoryRandomAccessStream _manifest;
        private HttpBaseProtocolFilter _filter;
        private HttpClient _http;
        private RangeAccelerationFilter _acceleration;
        private AdaptiveMediaSource _adaptive;
        private readonly object _eventGate = new object();
        private DashRoutePolicy _routes;
        private bool _disposed;
        private long _completedDownloadBytes;
        private long _requestedCount, _completedCount, _failedCount, _cancelledCount;
        private readonly AdaptiveDownloadFailureState _downloadFailures = new AdaptiveDownloadFailureState();
        private int _lastHttpStatus;
        private int _lastDownloadError;
        private (TimeSpan? Duration, int Status, string Host) _lastMediaRequest;
        private (TimeSpan? Duration, int Status, string Host) _lastVideoRequest;
        private int _lastAudioHttpStatus;
        private bool _invalidIndex;
        private bool _traceRanges;
        private int _diagnosticEvents;
        internal const int MaximumDiagnosticEvents = 256;
        internal event Action<string> DownloadDiagnostic;
        public MediaSource Source { get; private set; }
        public double CurrentDownloadBytesPerSecond
        {
            get
            {
                lock (_eventGate)
                {
                    if (_disposed || _adaptive == null) return -1;
                    try { return Math.Max(_adaptive.InboundBitsPerSecond / 8.0, _acceleration?.CurrentDownloadBytesPerSecond ?? 0); }
                    catch (Exception) { return _acceleration?.CurrentDownloadBytesPerSecond ?? -1; }
                }
            }
        }
        internal (double NativeBps, double RangeBps) TransferRates
        {
            get
            {
                lock (_eventGate)
                {
                    double native = -1;
                    if (!_disposed && _adaptive != null)
                    {
                        try { native = _adaptive.InboundBitsPerSecond / 8.0; }
                        catch (Exception) { }
                    }
                    return (native, _acceleration?.CurrentDownloadBytesPerSecond ?? -1);
                }
            }
        }
        internal long TotalDownloadedBytes { get { lock (_eventGate) return _completedDownloadBytes; } }
        public string RouteSummary { get { return _routes == null ? "" : _routes.RouteSummary; } }
        public bool HasAlternateRoutes { get { return _routes != null && _routes.HasAlternateRoutes; } }
        internal bool HasUntriedRoute => _routes?.HasUntriedRoute == true;
        public int RouteChangeCount { get { return _routes == null ? 0 : _routes.RouteChangeCount; } }
        internal int SelectedRouteOffset { get { return _routes == null ? 0 : _routes.SelectedRouteOffset; } }
        internal int NextRouteOffset { get { return _routes == null ? 0 : _routes.NextRouteOffset; } }
        internal int FailureRouteOffset { get { return _routes == null ? 0 : _routes.FailureRouteOffset; } }
        internal bool DownloadFailurePending { get { lock (_eventGate) return _downloadFailures.Pending; } }
        internal bool AccelerationEnabled => _acceleration != null;
        internal bool AccelerationBodyFailed => _acceleration?.HasBodyFailure == true;
        internal (TimeSpan? Duration, int Status, string Host) LastMediaRequest
        {
            get
            {
                lock (_eventGate)
                {
                    var accelerated = _acceleration?.LastMediaRequest;
                    if (accelerated.HasValue && accelerated.Value.Duration.HasValue) return accelerated.Value;
                    return _lastVideoRequest.Host != null ? _lastVideoRequest : _lastMediaRequest;
                }
            }
        }
        internal string AccelerationWaitDiagnostic => _acceleration?.WaitDiagnostic;
        internal bool IndexFailurePending { get { lock (_eventGate) return _invalidIndex; } }
        internal string TransportDiagnostic
        {
            get
            {
                lock (_eventGate) return "requested=" + _requestedCount + " completed=" + _completedCount
                    + " failed=" + _failedCount + " cancelled=" + _cancelledCount + " totalBytes=" + _completedDownloadBytes
                    + " routeOffset=" + SelectedRouteOffset
                    + " lastHttp=" + _lastHttpStatus + " lastDownloadHresult=0x" + _lastDownloadError.ToString("X8")
                    + " unresolvedDownloadFailure=" + _downloadFailures.Pending + " pendingFailures=" + _downloadFailures.Count + " invalidIndex=" + _invalidIndex
                    + " audioHttp=" + _lastAudioHttpStatus
                    + " " + (_acceleration?.Diagnostic ?? "acceleration=off");
            }
        }

        private AdaptivePlaybackSource(DownloadSpeedMeter downloadSpeed)
        {
            // Preserve the existing construction contract; DASH reports native
            // telemetry instead of routing every body read through this meter.
            if (downloadSpeed == null) throw new ArgumentNullException(nameof(downloadSpeed));
        }

        public static Task<AdaptivePlaybackSource> OpenAsync(string manifest, CancellationToken token)
        {
            return OpenAsync(manifest, new DownloadSpeedMeter(), token);
        }

        internal static Task<AdaptivePlaybackSource> OpenAsync(string manifest, DownloadSpeedMeter downloadSpeed,
            CancellationToken token)
        {
            return OpenAsync(manifest, null, null, downloadSpeed, 0, token);
        }

        internal static async Task<AdaptivePlaybackSource> OpenAsync(string manifest, IReadOnlyList<string> videoUrls,
            IReadOnlyList<string> audioUrls, DownloadSpeedMeter downloadSpeed, int routeOffset, CancellationToken token,
            bool enableAcceleration = false, Action<string> rangeDiagnostic = null)
        {
            if (string.IsNullOrWhiteSpace(manifest)) throw new ArgumentException("DASH 播放清单为空。", nameof(manifest));
            token.ThrowIfCancellationRequested();
            var owner = new AdaptivePlaybackSource(downloadSpeed);
            owner._traceRanges = rangeDiagnostic != null;
            try
            {
                owner._routes = new DashRoutePolicy(manifest, videoUrls, audioUrls, routeOffset);
                owner._filter = new HttpBaseProtocolFilter
                {
                    AutomaticDecompression = false,
                    CookieUsageBehavior = HttpCookieUsageBehavior.NoCookies
                };
                owner._filter.CacheControl.ReadBehavior = HttpCacheReadBehavior.NoCache;
                owner._filter.CacheControl.WriteBehavior = HttpCacheWriteBehavior.NoCache;
                // Keep range-response streams native all the way to Media
                // Foundation. A managed IInputStream wrapper on every read adds
                // allocation/marshalling work to high-bitrate 4K playback merely
                // to collect a statistic that AdaptiveMediaSource already owns.
                if (enableAcceleration && videoUrls != null && videoUrls.Count > 0)
                    owner._acceleration = new RangeAccelerationFilter(owner._filter, owner._routes, downloadSpeed, rangeDiagnostic);
                owner._http = owner._acceleration == null ? new HttpClient(owner._filter) : new HttpClient(owner._acceleration);
                owner._http.DefaultRequestHeaders.Referer = new Uri(BilibiliClient.Referer);
                owner._http.DefaultRequestHeaders.TryAppendWithoutValidation("User-Agent", BilibiliClient.UserAgent);
                owner._http.DefaultRequestHeaders.TryAppendWithoutValidation("Accept-Encoding", "identity");
                owner._manifest = new InMemoryRandomAccessStream();
                await owner._manifest.WriteAsync(Encoding.UTF8.GetBytes(manifest).AsBuffer()).AsTask(token);
                owner._manifest.Seek(0);
                var result = await AdaptiveMediaSource.CreateFromStreamAsync(owner._manifest,
                    new Uri(BilibiliClient.Referer), "application/dash+xml", owner._http).AsTask(token);
                if (result.Status == AdaptiveMediaSourceCreationStatus.Success) owner._adaptive = result.MediaSource;
                token.ThrowIfCancellationRequested();
                if (result.Status != AdaptiveMediaSourceCreationStatus.Success)
                    throw new InvalidOperationException("无法加载 DASH 音视频清单（" + result.Status + "）。请重试或选择其他画质。");
                // The platform default is five minutes. Use a recent window for
                // the on-screen transfer rate without intercepting response data.
                try { owner._adaptive.InboundBitsPerSecondWindow = TimeSpan.FromSeconds(2); }
                catch (Exception) { /* Telemetry must never prevent playback. */ }
                owner._adaptive.DownloadRequested += owner.DownloadRequested;
                owner._adaptive.DownloadFailed += owner.DownloadFailed;
                owner._adaptive.DownloadCompleted += owner.DownloadCompleted;
                owner.Source = MediaSource.CreateFromAdaptiveMediaSource(owner._adaptive);
                return owner;
            }
            catch { owner.Dispose(); throw; }
        }

        private void DownloadRequested(AdaptiveMediaSource sender, AdaptiveMediaSourceDownloadRequestedEventArgs args)
        {
            string diagnostic;
            lock (_eventGate)
            {
                if (_disposed) return;
                ++_requestedCount;
                _downloadFailures.Requested(args.RequestId.ToString(), _routes.ResourceKey(args.ResourceUri),
                    (int)args.ResourceType, args.ResourceByteRangeOffset, args.ResourceByteRangeLength);
                var uri = _routes.ResolveRequest(args.ResourceUri, args.RequestId.ToString());
                if (uri != null && uri != args.ResourceUri)
                    args.Result.ResourceUri = uri;
                // Keep the native SegmentBase range and production HttpClient
                // headers. No full-track buffering or synthesized ranges.
                string detail;
                try
                {
                    detail = "id=" + args.RequestId + " type=" + (int)args.ResourceType
                        + " track=" + DiagnosticTrack(uri ?? args.ResourceUri)
                        + " rangeOffset=" + OptionalNumber(args.ResourceByteRangeOffset)
                        + " rangeLength=" + OptionalNumber(args.ResourceByteRangeLength)
                        + " host=" + DiagnosticHost(uri ?? args.ResourceUri);
                }
                catch (Exception) { detail = "metadata=unavailable"; }
                diagnostic = CaptureDiagnostic("download-requested " + detail);
            }
            PublishDiagnostic(diagnostic);
        }

        private void DownloadFailed(AdaptiveMediaSource sender, AdaptiveMediaSourceDownloadFailedEventArgs args)
        {
            string diagnostic;
            lock (_eventGate)
            {
                if (_disposed) return;
                int httpStatus = 0, error = 0;
                try
                {
                    httpStatus = args.HttpResponseMessage == null ? 0 : (int)args.HttpResponseMessage.StatusCode;
                    error = args.ExtendedError?.HResult ?? 0;
                }
                catch (Exception) { }
                string id = args.RequestId.ToString();
                // MF_E_OPERATION_CANCELLED is reported for native requests the
                // player abandons, such as ranges it no longer needs after a seek.
                bool cancelled = unchecked((uint)error) == 0xC00D36ED;
                if (cancelled)
                {
                    ++_cancelledCount;
                    _downloadFailures.Cancelled(id);
                    _routes.CompleteRequest(id);
                }
                else
                {
                    ++_failedCount;
                    _lastHttpStatus = httpStatus;
                    _lastDownloadError = error;
                    _downloadFailures.Failed(id);
                    // WinSDK: IndexSegment=5, MF_E_INVALID_FILE_FORMAT=0xC00D36BE.
                    // A completed index rejected by the parser is not a bad CDN.
                    if ((int)args.ResourceType == 5 && httpStatus == 206 && unchecked((uint)error) == 0xC00D36BE)
                    { _invalidIndex = true; _routes.CompleteRequest(id); }
                    else _routes.ReportFailure(id);
                }
                // DownloadFailed is a notification, not a retry API. Native
                // retries use the new route; the UI can also rebuild boundedly.
                string detail;
                try
                {
                    ulong ignored;
                    if (!cancelled) RememberMediaRequest(args.ResourceUri, httpStatus, args.Statistics);
                    detail = "id=" + id + " type=" + (int)args.ResourceType
                        + " track=" + DiagnosticTrack(args.ResourceUri)
                        + " httpStatus=" + httpStatus
                        + " host=" + DiagnosticHost(args.HttpResponseMessage?.RequestMessage?.RequestUri ?? args.ResourceUri)
                        + " hresult=0x" + error.ToString("X8", CultureInfo.InvariantCulture)
                        + " " + DownloadStatistics(args.Statistics, out ignored);
                }
                catch (Exception) { detail = "metadata=unavailable"; }
                diagnostic = CaptureDiagnostic((cancelled ? "download-cancelled " : "download-failed ") + detail);
            }
            PublishDiagnostic(diagnostic);
        }

        private void DownloadCompleted(AdaptiveMediaSource sender, AdaptiveMediaSourceDownloadCompletedEventArgs args)
        {
            string diagnostic;
            lock (_eventGate)
            {
                if (_disposed) return;
                ++_completedCount;
                _downloadFailures.Completed(args.RequestId.ToString());
                _routes.CompleteRequest(args.RequestId.ToString());
                // These native statistics are for telemetry only. Never use
                // them to change quality or infer that decoding is keeping up.
                string detail;
                try
                {
                    ulong received;
                    var status = (int?)args.HttpResponseMessage?.StatusCode ?? 0;
                    RememberMediaRequest(args.ResourceUri, status, args.Statistics);
                    detail = "id=" + args.RequestId + " type=" + (int)args.ResourceType + " track=" + DiagnosticTrack(args.ResourceUri)
                        + " httpStatus=" + status + " host=" + DiagnosticHost(args.HttpResponseMessage?.RequestMessage?.RequestUri ?? args.ResourceUri)
                        + " " + DownloadStatistics(args.Statistics, out received);
                    _completedDownloadBytes += (long)Math.Min(received,
                        (ulong)(long.MaxValue - _completedDownloadBytes));
                }
                catch (Exception) { detail = "metadata=unavailable"; }
                diagnostic = CaptureDiagnostic("download-completed " + detail);
            }
            PublishDiagnostic(diagnostic);
        }

        private void RememberMediaRequest(Uri uri, int status, AdaptiveMediaSourceDownloadStatistics statistics)
        {
            if (uri == null || !uri.IsAbsoluteUri) return;
            var resource = DiagnosticTrack(uri);
            if (resource == "unknown") return;
            TimeSpan? duration = null;
            try
            {
                var elapsed = statistics?.TimeToLastByteReceived;
                if (elapsed.HasValue && elapsed.Value > TimeSpan.Zero) duration = elapsed;
            }
            catch (Exception) { }
            var sample = (duration, status, uri.DnsSafeHost);
            _lastMediaRequest = sample;
            if (resource == "video") _lastVideoRequest = sample;
            else _lastAudioHttpStatus = status;
        }

        private string DiagnosticTrack(Uri uri)
        {
            var track = _routes.ResourceKey(uri);
            return track == "video" || track == "audio" ? track : "unknown";
        }

        private static string OptionalNumber(ulong? value)
            => value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "unknown";

        private static string Milliseconds(TimeSpan? value)
            => value.HasValue ? value.Value.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture) : "unknown";

        private static string DownloadStatistics(AdaptiveMediaSourceDownloadStatistics statistics, out ulong received)
        {
            received = 0;
            if (statistics == null) return "statistics=unavailable";
            received = statistics.ContentBytesReceivedCount;
            string result = "receivedBytes=" + received.ToString(CultureInfo.InvariantCulture);
            try
            {
                result += " headersMs=" + Milliseconds(statistics.TimeToHeadersReceived)
                    + " firstByteMs=" + Milliseconds(statistics.TimeToFirstByteReceived)
                    + " lastByteMs=" + Milliseconds(statistics.TimeToLastByteReceived);
            }
            catch (Exception) { result += " timing=unavailable"; }
            return result;
        }

        private static string DiagnosticHost(Uri uri) => uri?.IsAbsoluteUri == true ? uri.DnsSafeHost : "-";

        // Caller holds _eventGate. Fixed-size counters and a per-source event
        // budget keep telemetry bounded; it never controls native downloading.
        private string CaptureDiagnostic(string value)
        {
            if (DownloadDiagnostic == null) return null;
            if (!_traceRanges)
            {
                if (_diagnosticEvents >= MaximumDiagnosticEvents) return null;
                if (++_diagnosticEvents == MaximumDiagnosticEvents) value = "download-telemetry-limit";
            }
            else value += " utc=" + DateTimeOffset.UtcNow.ToString("O");
            return value + " requested=" + _requestedCount + " completed=" + _completedCount
                + " failed=" + _failedCount + " cancelled=" + _cancelledCount + " totalBytes=" + _completedDownloadBytes
                + " routeChanges=" + _routes.RouteChangeCount + " routeOffset=" + SelectedRouteOffset;
        }

        private void PublishDiagnostic(string value)
        {
            if (value == null) return;
            var handlers = DownloadDiagnostic;
            if (handlers == null) return;
            // Subscribers run after releasing the native event-state lock.
            // Never allow a failed diagnostic sink to interrupt media callbacks.
            foreach (Action<string> handler in handlers.GetInvocationList())
                try { handler(value); }
                catch (Exception) { }
        }

        public void Dispose()
        {
            lock (_eventGate)
            {
                if (_disposed) return;
                _disposed = true;
                DownloadDiagnostic = null;
                if (_adaptive != null)
                {
                    _adaptive.DownloadRequested -= DownloadRequested;
                    _adaptive.DownloadFailed -= DownloadFailed;
                    _adaptive.DownloadCompleted -= DownloadCompleted;
                }
            }
            _acceleration?.Dispose();
            Source?.Dispose(); Source = null;
            _adaptive?.Dispose(); _adaptive = null;
            _manifest?.Dispose(); _manifest = null;
            _http?.Dispose(); _http = null;
            _filter?.Dispose(); _filter = null;
        }
    }
}
