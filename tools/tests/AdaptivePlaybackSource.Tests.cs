// Execute the production owner and route policy with only WinRT/media classes
// substituted. No request leaves this deterministic fixture.
namespace System.Runtime.InteropServices.WindowsRuntime
{
    internal static class FixtureExtensions
    {
        internal static byte[] AsBuffer(this byte[] value) => value;
        internal static Task<T> AsTask<T>(this Task<T> value, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return value; }
    }
}
namespace Windows.Storage.Streams
{
    internal sealed class InMemoryRandomAccessStream : IDisposable
    {
        internal static int Disposed;
        internal Task<uint> WriteAsync(byte[] value) => Task.FromResult((uint)value.Length);
        internal void Seek(ulong value) { }
        public void Dispose() { ++Disposed; }
    }
}
namespace Windows.Web.Http.Filters
{
    internal enum HttpCookieUsageBehavior { NoCookies }
    internal enum HttpCacheReadBehavior { NoCache }
    internal enum HttpCacheWriteBehavior { NoCache }
    internal sealed class FixtureCacheControl
    {
        internal HttpCacheReadBehavior ReadBehavior;
        internal HttpCacheWriteBehavior WriteBehavior;
    }
    internal sealed class HttpBaseProtocolFilter : IDisposable
    {
        internal static int Disposed;
        internal bool AutomaticDecompression;
        internal HttpCookieUsageBehavior CookieUsageBehavior;
        internal FixtureCacheControl CacheControl = new FixtureCacheControl();
        public void Dispose() { ++Disposed; }
    }
}
namespace Windows.Web.Http
{
    internal sealed class HttpResponseMessage { internal int StatusCode; }
    internal sealed class FixtureHeaders : Dictionary<string, string>
    {
        internal Uri Referer;
        internal bool TryAppendWithoutValidation(string name, string value) { this[name] = value; return true; }
    }
    internal sealed class HttpClient : IDisposable
    {
        internal static HttpClient Last;
        internal static int Constructed, Disposed;
        internal readonly object Filter;
        internal readonly FixtureHeaders DefaultRequestHeaders = new FixtureHeaders();
        internal HttpClient(object filter) { Filter = filter; Last = this; ++Constructed; }
        public void Dispose() { ++Disposed; }
    }
}
namespace Windows.Media.Core
{
    internal sealed class MediaSource : IDisposable
    {
        internal static int Disposed;
        internal static MediaSource CreateFromAdaptiveMediaSource(AdaptiveMediaSource value) => new MediaSource();
        public void Dispose() { ++Disposed; }
    }
}
namespace Windows.Media.Streaming.Adaptive
{
    internal enum AdaptiveMediaSourceCreationStatus { Success, UnknownFailure }
    internal sealed class AdaptiveMediaSourceCreationResult
    { internal AdaptiveMediaSourceCreationStatus Status; internal AdaptiveMediaSource MediaSource; }
    internal sealed class AdaptiveMediaSourceDownloadRequestedEventArgs
    {
        internal Uri ResourceUri;
        internal uint RequestId;
        internal int ResourceType;
        internal ulong? ResourceByteRangeOffset;
        internal ulong? ResourceByteRangeLength;
        internal readonly FixtureDownloadResult Result = new FixtureDownloadResult();
    }
    internal sealed class FixtureDownloadResult { internal Uri ResourceUri; }
    internal sealed class AdaptiveMediaSourceDownloadFailedEventArgs
    {
        internal uint RequestId;
        internal int ResourceType;
        internal HttpResponseMessage HttpResponseMessage;
        internal Exception ExtendedError;
        internal AdaptiveMediaSourceDownloadStatistics Statistics;
    }
    internal sealed class AdaptiveMediaSourceDownloadStatistics
    {
        internal ulong ContentBytesReceivedCount;
        internal bool ThrowTiming;
        internal TimeSpan FirstByte, LastByte;
        internal TimeSpan TimeToFirstByteReceived { get { if(ThrowTiming) throw new InvalidOperationException("timing fixture secret"); return FirstByte; } }
        internal TimeSpan TimeToLastByteReceived => LastByte;
    }
    internal sealed class AdaptiveMediaSourceDownloadCompletedEventArgs
    {
        internal uint RequestId;
        internal int ResourceType;
        internal AdaptiveMediaSourceDownloadStatistics Statistics;
        internal ulong? ResourceByteRangeLength;
    }
    internal sealed class AdaptiveMediaSource : IDisposable
    {
        internal static AdaptiveMediaSource Last;
        internal static AdaptiveMediaSourceCreationStatus Status;
        internal static bool ThrowWindow;
        internal static int Disposed;
        internal bool ThrowRate;
        internal ulong Rate;
        internal TimeSpan Window;
        internal ulong InboundBitsPerSecond { get { if (ThrowRate) throw new InvalidOperationException(); return Rate; } }
        internal TimeSpan InboundBitsPerSecondWindow
        { get { return Window; } set { if (ThrowWindow) throw new InvalidOperationException(); Window = value; } }
        internal event Action<AdaptiveMediaSource, AdaptiveMediaSourceDownloadRequestedEventArgs> DownloadRequested;
        internal event Action<AdaptiveMediaSource, AdaptiveMediaSourceDownloadFailedEventArgs> DownloadFailed;
        internal event Action<AdaptiveMediaSource, AdaptiveMediaSourceDownloadCompletedEventArgs> DownloadCompleted;
        internal int Handlers => (DownloadRequested?.GetInvocationList().Length ?? 0)
            + (DownloadFailed?.GetInvocationList().Length ?? 0) + (DownloadCompleted?.GetInvocationList().Length ?? 0);
        internal static Task<AdaptiveMediaSourceCreationResult> CreateFromStreamAsync(InMemoryRandomAccessStream stream,
            Uri baseUri, string contentType, HttpClient http)
        {
            Last = new AdaptiveMediaSource();
            return Task.FromResult(new AdaptiveMediaSourceCreationResult { Status = Status, MediaSource = Last });
        }
        internal void Request(AdaptiveMediaSourceDownloadRequestedEventArgs value) => DownloadRequested?.Invoke(this, value);
        internal void Fail(uint id) => DownloadFailed?.Invoke(this, new AdaptiveMediaSourceDownloadFailedEventArgs { RequestId = id });
        internal void Fail(AdaptiveMediaSourceDownloadFailedEventArgs value) => DownloadFailed?.Invoke(this, value);
        internal void Complete(AdaptiveMediaSourceDownloadCompletedEventArgs value) => DownloadCompleted?.Invoke(this, value);
        public void Dispose() { ++Disposed; }
    }
}
namespace BiliXbox.Services
{
    internal static class BilibiliClient
    {
        internal const string Referer = "https://www.bilibili.com/";
        internal const string UserAgent = "fixture-production-headers";
    }
}
namespace BiliXbox.AdaptiveSourceTests
{
    public static class Tests
    {
        private static int _passed;
        private static void Check(bool value, string name)
        { if (!value) throw new Exception("FAIL: " + name); ++_passed; }
        private const string Video = "https://media.example.test/video.m4s?token=signed";
        private const string Backup = "https://backup.example.test/video.m4s?token=signed";
        private const string Audio = "https://media.example.test/audio.m4s?token=signed";
        private const string Manifest = "<MPD xmlns='urn:mpeg:dash:schema:mpd:2011'><Period>"
            + "<AdaptationSet contentType='video'><Representation><BaseURL>" + Video + "</BaseURL></Representation></AdaptationSet>"
            + "<AdaptationSet contentType='audio'><Representation><BaseURL>" + Audio + "</BaseURL></Representation></AdaptationSet></Period></MPD>";
        public static async Task<string> Run()
        {
            _passed = 0;
            AdaptiveMediaSource.Status = AdaptiveMediaSourceCreationStatus.Success;
            var meter = new DownloadSpeedMeter(); meter.AddReceivedBytes(123);
            var owner = await AdaptivePlaybackSource.OpenAsync(Manifest, new[] { Video, Backup }, new[] { Audio }, meter, 1, CancellationToken.None);
            var native = AdaptiveMediaSource.Last;
            var client = HttpClient.Last;
            Check(client.Filter is HttpBaseProtocolFilter, "production HTTP client directly owns the native protocol filter");
            Check(native.Window == TimeSpan.FromSeconds(2), "native throughput uses a recent two-second statistic window");
            Check(client.DefaultRequestHeaders.Referer.AbsoluteUri == BilibiliClient.Referer
                && client.DefaultRequestHeaders["User-Agent"] == BilibiliClient.UserAgent
                && client.DefaultRequestHeaders["Accept-Encoding"] == "identity", "production CDN request headers remain native and intact");
            var filter = (HttpBaseProtocolFilter)client.Filter;
            Check(!filter.AutomaticDecompression && filter.CookieUsageBehavior == HttpCookieUsageBehavior.NoCookies,
                "native body bytes are neither decompressed nor authenticated with unrelated cookies");
            Check(owner.Source != null && native.Handlers == 3, "owner attaches the native source and range routing notifications");
            native.Rate = 8UL * 5 * 1024 * 1024;
            Check(owner.CurrentDownloadBytesPerSecond == 5 * 1024 * 1024, "native inbound bits convert exactly to bytes per second");
            native.Rate = 0;
            Check(owner.CurrentDownloadBytesPerSecond == 0, "idle native download reports zero instead of media encoding bitrate");
            native.ThrowRate = true;
            Check(owner.CurrentDownloadBytesPerSecond == -1, "unavailable native statistics do not stop playback");
            native.ThrowRate = false;
            var diagnostics = new List<string>();
            var eventGate = typeof(AdaptivePlaybackSource).GetField("_eventGate",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).GetValue(owner);
            bool diagnosticLockHeld = false;
            owner.DownloadDiagnostic += value => {throw new InvalidOperationException("subscriber URL https://private.invalid/?secret=private");};
            owner.DownloadDiagnostic += value => { diagnosticLockHeld |= Monitor.IsEntered(eventGate); diagnostics.Add(value); };
            var request = new AdaptiveMediaSourceDownloadRequestedEventArgs { ResourceUri = new Uri(Video), RequestId = 17, ResourceType = 3,
                ResourceByteRangeOffset = 2048, ResourceByteRangeLength = 1024 };
            native.Request(request);
            Check(request.Result.ResourceUri.AbsoluteUri == Backup && request.ResourceByteRangeLength == 1024,
                "native request retains its range while routing to the server-issued signed backup");
            native.Complete(new AdaptiveMediaSourceDownloadCompletedEventArgs { RequestId = 17,
                Statistics = new AdaptiveMediaSourceDownloadStatistics { ContentBytesReceivedCount = 400,
                    FirstByte=TimeSpan.FromMilliseconds(12),LastByte=TimeSpan.FromMilliseconds(48) }, ResourceByteRangeLength = 9000 });
            Check(owner.TotalDownloadedBytes == 400, "byte telemetry counts native received content, not requested range length");
            Check(diagnostics[0].Contains("id=17 type=3 rangeOffset=2048 rangeLength=1024")
                && diagnostics[1].Contains("receivedBytes=400 firstByteMs=12 lastByteMs=48"),
                "DASH diagnostics report native requested ranges and received bytes/timing as distinct facts");
            Check(!diagnosticLockHeld && diagnostics.Count==2,
                "diagnostics run outside the native event lock and a failed subscriber cannot suppress a healthy subscriber");
            native.Fail(17);
            Check(owner.RouteChangeCount == 0, "completed native request cannot later rotate a healthy backup");
            native.Complete(new AdaptiveMediaSourceDownloadCompletedEventArgs { RequestId = 18,
                Statistics = new AdaptiveMediaSourceDownloadStatistics { ContentBytesReceivedCount = 600 } });
            Check(owner.TotalDownloadedBytes == 1000, "successful audio and video downloads accumulate once per native completion");
            native.Complete(new AdaptiveMediaSourceDownloadCompletedEventArgs { RequestId = 19, ResourceByteRangeLength = 9000 });
            Check(owner.TotalDownloadedBytes == 1000, "absent native received-byte statistics are not fabricated from the byte range");
            Check(meter.TotalDownloadedBytes == 123, "DASH native telemetry leaves the unrelated MP4 read meter untouched");
            native.Complete(new AdaptiveMediaSourceDownloadCompletedEventArgs { RequestId = 20,
                Statistics = new AdaptiveMediaSourceDownloadStatistics { ContentBytesReceivedCount = ulong.MaxValue } });
            Check(owner.TotalDownloadedBytes == long.MaxValue, "untrusted or long-running download totals saturate without overflow");
            native.Fail(new AdaptiveMediaSourceDownloadFailedEventArgs {RequestId=21,ResourceType=3,
                HttpResponseMessage=new HttpResponseMessage {StatusCode=503},
                ExtendedError=new InvalidOperationException("https://private.invalid/?credential=fixture-diagnostic-secret"),
                Statistics=new AdaptiveMediaSourceDownloadStatistics{ContentBytesReceivedCount=99,ThrowTiming=true}});
            Check(diagnostics.Last().Contains("download-failed id=21 type=3 httpStatus=503 hresult=0x")
                && diagnostics.Last().Contains("receivedBytes=99 timing=unavailable")
                && owner.TotalDownloadedBytes==long.MaxValue, "failed requests expose status/partial-byte facts without changing completed-byte accounting");
            Check(diagnostics.All(value=>!value.Contains("https://")&&!value.Contains("token=")
                && !value.Contains("fixture-diagnostic-secret")&&!value.Contains("timing fixture secret")),
                "no media URLs, signed queries, native exception messages or subscriber messages enter request telemetry");
            int mediaDisposed = MediaSource.Disposed, adaptiveDisposed = AdaptiveMediaSource.Disposed,
                httpDisposed = HttpClient.Disposed, filterDisposed = HttpBaseProtocolFilter.Disposed;
            owner.Dispose(); owner.Dispose();
            Check(native.Handlers == 0 && owner.Source == null && owner.CurrentDownloadBytesPerSecond == -1,
                "disposal detaches callbacks and makes concurrent telemetry reads harmless");
            Check(MediaSource.Disposed == mediaDisposed + 1 && AdaptiveMediaSource.Disposed == adaptiveDisposed + 1
                && HttpClient.Disposed == httpDisposed + 1 && HttpBaseProtocolFilter.Disposed == filterDisposed + 1,
                "native source, client and filter each release once");
            int afterDispose=diagnostics.Count;
            native.Fail(22);native.Complete(new AdaptiveMediaSourceDownloadCompletedEventArgs{RequestId=22});
            Check(diagnostics.Count==afterDispose
                && typeof(AdaptivePlaybackSource).GetField("DownloadDiagnostic",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).GetValue(owner)==null,
                "disposal clears subscribers and prevents subsequent native callbacks from producing telemetry");
            using(var bounded=await AdaptivePlaybackSource.OpenAsync(Manifest,CancellationToken.None))
            {
                var boundedNative=AdaptiveMediaSource.Last;var boundedLog=new List<string>();bounded.DownloadDiagnostic+=boundedLog.Add;
                for(uint id=0;id<AdaptivePlaybackSource.MaximumDiagnosticEvents*2;++id)
                    boundedNative.Complete(new AdaptiveMediaSourceDownloadCompletedEventArgs{RequestId=id,
                        Statistics=new AdaptiveMediaSourceDownloadStatistics{ContentBytesReceivedCount=1}});
                Check(boundedLog.Count==AdaptivePlaybackSource.MaximumDiagnosticEvents
                    && boundedLog.Last().StartsWith("download-telemetry-limit"), "per-source native event telemetry has a fixed bound and explicit limit marker");
                Check(bounded.TotalDownloadedBytes==AdaptivePlaybackSource.MaximumDiagnosticEvents*2,
                    "telemetry limit never stops native completion processing or data accounting");
            }
            AdaptiveMediaSource.ThrowWindow = true;
            using (var telemetryUnavailable = await AdaptivePlaybackSource.OpenAsync(Manifest, CancellationToken.None))
                Check(telemetryUnavailable.Source != null, "optional statistic-window configuration cannot prevent opening media");
            AdaptiveMediaSource.ThrowWindow = false;
            var canceled = new CancellationToken(true); int before = HttpClient.Constructed;
            try { await AdaptivePlaybackSource.OpenAsync(Manifest, canceled); throw new Exception("FAIL: cancelled source accepted"); }
            catch (OperationCanceledException) { Check(HttpClient.Constructed == before, "canceled source allocates no native downloader"); }
            AdaptiveMediaSource.Status = AdaptiveMediaSourceCreationStatus.UnknownFailure; before = HttpClient.Disposed;
            try { await AdaptivePlaybackSource.OpenAsync(Manifest, CancellationToken.None); throw new Exception("FAIL: failed media creation accepted"); }
            catch (InvalidOperationException) { Check(HttpClient.Disposed == before + 1, "unsuccessful native creation releases its downloader"); }
            return "PASS: " + _passed + " native DASH ownership, range and telemetry checks.";
        }
    }
}
