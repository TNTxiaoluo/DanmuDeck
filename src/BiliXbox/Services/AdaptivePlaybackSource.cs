using System;
using System.Collections.Generic;
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
            lock (_eventGate)
            {
                if (_disposed) return;
                var uri = _routes.ResolveRequest(args.ResourceUri, args.RequestId.ToString());
                if (uri != null && uri != args.ResourceUri)
                    args.Result.ResourceUri = uri;
                // Keep the native SegmentBase range and production HttpClient
                // headers. No full-track buffering or synthesized ranges.
            }
        }

        private void DownloadFailed(AdaptiveMediaSource sender, AdaptiveMediaSourceDownloadFailedEventArgs args)
        {
            lock (_eventGate)
            {
                if (!_disposed) _routes.ReportFailure(args.RequestId.ToString());
                // DownloadFailed is a notification, not a retry API. Native
                // retries use the new route; the UI can also rebuild boundedly.
            }
        }

        private void DownloadCompleted(AdaptiveMediaSource sender, AdaptiveMediaSourceDownloadCompletedEventArgs args)
        {
            lock (_eventGate)
            {
                if (_disposed) return;
                _routes.CompleteRequest(args.RequestId.ToString());
                // These native statistics are for telemetry only. Never use
                // them to change quality or infer that decoding is keeping up.
                try
                {
                    var statistics = args.Statistics;
                    if (statistics != null)
                    {
                        ulong received = statistics.ContentBytesReceivedCount;
                        _completedDownloadBytes += (long)Math.Min(received,
                            (ulong)(long.MaxValue - _completedDownloadBytes));
                    }
                }
                catch (Exception) { /* Missing optional statistics are harmless. */ }
            }
        }

        public void Dispose()
        {
            lock (_eventGate)
            {
                if (_disposed) return;
                _disposed = true;
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
