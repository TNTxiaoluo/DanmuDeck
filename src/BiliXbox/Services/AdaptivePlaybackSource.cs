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
        private AdaptiveMediaSource _adaptive;
        private readonly object _eventGate = new object();
        private DashRoutePolicy _routes;
        private bool _disposed;
        private long _completedDownloadBytes;
        private long _requestedCount, _completedCount, _failedCount;
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
                    try { return _adaptive.InboundBitsPerSecond / 8.0; }
                    catch (Exception) { return -1; }
                }
            }
        }
        internal long TotalDownloadedBytes { get { lock (_eventGate) return _completedDownloadBytes; } }
        public string RouteSummary { get { return _routes == null ? "" : _routes.RouteSummary; } }
        public bool HasAlternateRoutes { get { return _routes != null && _routes.HasAlternateRoutes; } }
        public int RouteChangeCount { get { return _routes == null ? 0 : _routes.RouteChangeCount; } }
        internal int NextRouteOffset { get { return _routes == null ? 0 : _routes.NextRouteOffset; } }

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
            IReadOnlyList<string> audioUrls, DownloadSpeedMeter downloadSpeed, int routeOffset, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(manifest)) throw new ArgumentException("DASH 播放清单为空。", nameof(manifest));
            token.ThrowIfCancellationRequested();
            var owner = new AdaptivePlaybackSource(downloadSpeed);
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
                owner._http = new HttpClient(owner._filter);
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
                var uri = _routes.ResolveRequest(args.ResourceUri, args.RequestId.ToString());
                if (uri != null && uri != args.ResourceUri)
                    args.Result.ResourceUri = uri;
                // Keep the native SegmentBase range and production HttpClient
                // headers. No full-track buffering or synthesized ranges.
                string detail;
                try
                {
                    detail = "id=" + args.RequestId + " type=" + (int)args.ResourceType
                        + " rangeOffset=" + OptionalNumber(args.ResourceByteRangeOffset)
                        + " rangeLength=" + OptionalNumber(args.ResourceByteRangeLength);
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
                ++_failedCount;
                _routes.ReportFailure(args.RequestId.ToString());
                // DownloadFailed is a notification, not a retry API. Native
                // retries use the new route; the UI can also rebuild boundedly.
                string detail;
                try
                {
                    ulong ignored;
                    detail = "id=" + args.RequestId + " type=" + (int)args.ResourceType
                        + " httpStatus=" + (args.HttpResponseMessage == null ? 0 : (int)args.HttpResponseMessage.StatusCode)
                        + " hresult=0x" + (args.ExtendedError?.HResult ?? 0).ToString("X8", CultureInfo.InvariantCulture)
                        + " " + DownloadStatistics(args.Statistics, out ignored);
                }
                catch (Exception) { detail = "metadata=unavailable"; }
                diagnostic = CaptureDiagnostic("download-failed " + detail);
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
                _routes.CompleteRequest(args.RequestId.ToString());
                // These native statistics are for telemetry only. Never use
                // them to change quality or infer that decoding is keeping up.
                string detail;
                try
                {
                    ulong received;
                    detail = "id=" + args.RequestId + " type=" + (int)args.ResourceType
                        + " " + DownloadStatistics(args.Statistics, out received);
                    _completedDownloadBytes += (long)Math.Min(received,
                        (ulong)(long.MaxValue - _completedDownloadBytes));
                }
                catch (Exception) { detail = "metadata=unavailable"; }
                diagnostic = CaptureDiagnostic("download-completed " + detail);
            }
            PublishDiagnostic(diagnostic);
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
                result += " firstByteMs=" + Milliseconds(statistics.TimeToFirstByteReceived)
                    + " lastByteMs=" + Milliseconds(statistics.TimeToLastByteReceived);
            }
            catch (Exception) { result += " timing=unavailable"; }
            return result;
        }

        // Caller holds _eventGate. Fixed-size counters and a per-source event
        // budget keep telemetry bounded; it never controls native downloading.
        private string CaptureDiagnostic(string value)
        {
            if (DownloadDiagnostic == null || _diagnosticEvents >= MaximumDiagnosticEvents) return null;
            if (++_diagnosticEvents == MaximumDiagnosticEvents) value = "download-telemetry-limit";
            return value + " requested=" + _requestedCount + " completed=" + _completedCount
                + " failed=" + _failedCount + " totalBytes=" + _completedDownloadBytes
                + " routeChanges=" + _routes.RouteChangeCount;
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
            Source?.Dispose(); Source = null;
            _adaptive?.Dispose(); _adaptive = null;
            _manifest?.Dispose(); _manifest = null;
            _http?.Dispose(); _http = null;
            _filter?.Dispose(); _filter = null;
        }
    }
}
