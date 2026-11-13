// TEST ONLY. Test-VideoDetail.ps1 compiles the real production BilibiliClient.
// No real network, account, filesystem persistence, or Xbox runtime is involved.
// These doubles do not prove WinRT ABI/TLS support or playback on a real console.
namespace Windows.System.Profile
{
    public static class AnalyticsInfo
    {
        public static readonly AnalyticsVersionInfo VersionInfo = new AnalyticsVersionInfo();
    }
    public sealed class AnalyticsVersionInfo { public string DeviceFamily { get; set; } = "Windows.Desktop"; }
}
namespace Windows.Graphics.Display.Core
{
    public enum HdmiDisplayColorSpace { RgbLimited, RgbFull, BT2020, BT709 }
    public sealed class HdmiDisplayMode
    {
        public uint ResolutionWidthInRawPixels { get; set; }
        public uint ResolutionHeightInRawPixels { get; set; }
        public double RefreshRate { get; set; }
        public bool StereoEnabled { get; set; }
        public bool IsSmpte2084Supported { get; set; }
        public bool IsSdrLuminanceSupported { get; set; }
        public HdmiDisplayColorSpace ColorSpace { get; set; }
    }
    public sealed class HdmiDisplayInformation
    {
        public static HdmiDisplayInformation Current;
        public static int ViewQueries;
        public static bool ThrowView, ThrowModes;
        public static HdmiDisplayInformation GetForCurrentView()
        { ViewQueries++; if (ThrowView) throw new InvalidOperationException("fixture view unavailable"); return Current; }
        public IReadOnlyList<HdmiDisplayMode> Modes = new List<HdmiDisplayMode>();
        public int Queries;
        public IReadOnlyList<HdmiDisplayMode> GetSupportedDisplayModes()
        { Queries++; if (ThrowModes) throw new InvalidOperationException("fixture modes unavailable"); return Modes; }
        public HdmiDisplayMode CurrentMode;
        public int CurrentQueries;
        public HdmiDisplayMode GetCurrentDisplayMode() { CurrentQueries++; return CurrentMode; }
    }
}
namespace Windows.Media.Protection
{
    public enum ProtectionCapabilityResult { NotSupported, Maybe, Probably }
    public sealed class ProtectionCapabilities
    {
        public static Func<string, string, ProtectionCapabilityResult> Query { get; set; }
        public ProtectionCapabilityResult IsTypeSupported(string contentType, string keySystem)
            => Query == null ? ProtectionCapabilityResult.NotSupported : Query(contentType, keySystem);
    }
}
namespace Windows.Graphics.Display
{
    public enum AdvancedColorKind { StandardDynamicRange, WideColorGamut, Hdr10, DolbyVision }
    public sealed class AdvancedColorInfo
    {
        public static Func<AdvancedColorKind, bool> Query { get; set; }
        public bool IsAdvancedColorKindAvailable(AdvancedColorKind kind)
            => Query != null && Query(kind);
    }
    public sealed class DisplayInformation
    {
        public static AdvancedColorInfo Current { get; set; } = new AdvancedColorInfo();
        public static DisplayInformation GetForCurrentView() => new DisplayInformation();
        public AdvancedColorInfo GetAdvancedColorInfo() => Current;
    }
}
namespace System.Runtime.InteropServices.WindowsRuntime
{
    public static class WindowsRuntimeBufferExtensions
    {
        public static byte[] ToArray(this global::Windows.Web.Http.TestBuffer buffer) => buffer.Bytes.ToArray();
    }
}
namespace Windows.Web.Http.Filters
{
    public enum HttpCookieUsageBehavior { NoCookies }
    public enum HttpCacheReadBehavior { MostRecent }
    public enum HttpCacheWriteBehavior { NoCache }
    public sealed class HttpCacheControl
    {
        public HttpCacheReadBehavior ReadBehavior { get; set; }
        public HttpCacheWriteBehavior WriteBehavior { get; set; }
    }
    public sealed class HttpBaseProtocolFilter
    {
        public bool AllowAutoRedirect { get; set; }
        public bool AutomaticDecompression { get; set; }
        public HttpCookieUsageBehavior CookieUsageBehavior { get; set; }
        public HttpCacheControl CacheControl { get; } = new HttpCacheControl();
    }
}
namespace Windows.Web.Http
{
    public enum HttpCompletionOption { ResponseContentRead }
    public sealed class HttpMethod
    {
        private HttpMethod(string method) { Method = method; }
        public string Method { get; }
        public static HttpMethod Get { get; } = new HttpMethod("GET");
        public static HttpMethod Post { get; } = new HttpMethod("POST");
    }
    public sealed class RequestHeaders : Dictionary<string, string>
    {
        public Uri Referer { get; set; }
        public bool TryAppendWithoutValidation(string key, string value) { this[key] = value; return true; }
    }
    public sealed class TestOperation<T>
    {
        private readonly Func<CancellationToken, Task<T>> _run;
        public TestOperation(Func<CancellationToken, Task<T>> run) { _run = run; }
        public Task<T> AsTask(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return _run(token);
        }
    }
    public abstract class HttpContent
    {
        public abstract TestOperation<string> ReadAsStringAsync();
        public abstract TestOperation<TestBuffer> ReadAsBufferAsync();
    }
    public sealed class TestBuffer
    {
        public TestBuffer(byte[] bytes) { Bytes = bytes; }
        public byte[] Bytes { get; }
    }
    public sealed class HttpStringContent : HttpContent
    {
        private readonly string _value;
        public HttpStringContent(string value) { _value = value; }
        public override TestOperation<string> ReadAsStringAsync()
            => new TestOperation<string>(token => Task.FromResult(_value));
        public override TestOperation<TestBuffer> ReadAsBufferAsync()
            => new TestOperation<TestBuffer>(token => Task.FromResult(new TestBuffer(Encoding.UTF8.GetBytes(_value))));
    }
    public sealed class HttpByteContent : HttpContent
    {
        private readonly byte[] _bytes;
        private readonly string _httpCharset;
        public int StringReads { get; private set; }
        public int BufferReads { get; private set; }
        public HttpByteContent(byte[] bytes, string httpCharset)
        {
            _bytes = bytes;
            _httpCharset = httpCharset;
        }
        public override TestOperation<string> ReadAsStringAsync()
            => new TestOperation<string>(token =>
            {
                StringReads++;
                // Reproduce WinRT text/xml's Latin-1 default when HTTP has no
                // charset, and the same result when explicitly mislabeled.
                var charset = string.IsNullOrEmpty(_httpCharset) ? "iso-8859-1" : _httpCharset;
                return Task.FromResult(Encoding.GetEncoding(charset).GetString(_bytes));
            });
        public override TestOperation<TestBuffer> ReadAsBufferAsync()
            => new TestOperation<TestBuffer>(token =>
            {
                BufferReads++;
                return Task.FromResult(new TestBuffer(_bytes));
            });
    }
    public sealed class HttpFormUrlEncodedContent : HttpContent
    {
        public HttpFormUrlEncodedContent(IEnumerable<KeyValuePair<string, string>> form) { }
        public override TestOperation<string> ReadAsStringAsync() => throw new NotSupportedException();
        public override TestOperation<TestBuffer> ReadAsBufferAsync() => throw new NotSupportedException();
    }
    public sealed class HttpRequestMessage : IDisposable
    {
        public HttpRequestMessage(HttpMethod method, Uri uri) { Method = method; RequestUri = uri; }
        public HttpMethod Method { get; }
        public Uri RequestUri { get; }
        public RequestHeaders Headers { get; } = new RequestHeaders();
        public HttpContent Content { get; set; }
        public void Dispose() { }
    }
    public sealed class HttpResponseMessage : IDisposable
    {
        public Dictionary<string, string> Headers { get; } = new Dictionary<string, string>();
        public int StatusCode { get; set; } = 200;
        public bool IsSuccessStatusCode => StatusCode >= 200 && StatusCode < 300;
        public HttpContent Content { get; set; }
        public void Dispose() { }
    }
    public sealed class HttpClient : IDisposable
    {
        public HttpClient(Filters.HttpBaseProtocolFilter filter)
        {
            if (!filter.AutomaticDecompression)
                throw new InvalidOperationException("Production HTTP transport must decompress response bytes.");
        }
        public RequestHeaders DefaultRequestHeaders { get; } = new RequestHeaders();
        public TestOperation<HttpResponseMessage> SendRequestAsync(HttpRequestMessage request, HttpCompletionOption option)
        {
            return new TestOperation<HttpResponseMessage>(token =>
            {
                if (DefaultRequestHeaders.Referer?.AbsoluteUri != "https://www.bilibili.com/"
                    || !DefaultRequestHeaders.ContainsKey("User-Agent"))
                    throw new InvalidOperationException("Production request headers are missing.");
                // Capture only the public request URI/method. Never record cookies or headers.
                BiliXbox.VideoDetailTests.Transport.Requests.Add(
                    new BiliXbox.VideoDetailTests.Request(request.Method.Method, request.RequestUri));
                return BiliXbox.VideoDetailTests.Transport.Handler(request, token);
            });
        }
        public void Dispose() { }
    }
}
namespace BiliXbox.VideoDetailTests
{
    using BiliXbox.Services;

    public sealed class Request
    {
        public Request(string method, Uri uri) { Method = method; Uri = uri; }
        public string Method { get; }
        public Uri Uri { get; }
    }
    public static class Transport
    {
        public static readonly List<Request> Requests = new List<Request>();
        public static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Handler;
        public static void Reset(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            Requests.Clear();
            Handler = handler;
        }
        public static Task<HttpResponseMessage> Json(string text, int status = 200)
            => Task.FromResult(new HttpResponseMessage { StatusCode = status, Content = new HttpStringContent(text) });
    }
    public static class Tests
    {
        // Published WBI fixture: public image keys and their fixed expected mixin.
        // Separate Test-ApiLogic.ps1 verifies the signer against a published full digest.
        private const string PublicImageKey = "7cd084941338484aae1ad9425b84077c";
        private const string PublicSubKey = "4932caff0ff746eab6f01bf08b70ac45";
        private const string PublicMixin = "ea1db124af3c7062474693fa704f4ff8";
        private const string Bvid = "BV1xx411c7mD";
        private const string Detail = "{\"code\":0,\"data\":{\"bvid\":\"BV1xx411c7mD\",\"aid\":170001,\"cid\":279786,"
            + "\"title\":\"<b>测试</b> &amp; Xbox\",\"pic\":\"//i0.hdslb.com/test.jpg\",\"desc\":\"fixture description\","
            + "\"owner\":{\"name\":\"fixture author\"},\"stat\":{\"view\":12345},\"duration\":66,"
            + "\"pages\":[{\"cid\":279786,\"page\":1,\"part\":\"First\"},{\"cid\":279787,\"page\":2,\"part\":\"Second\"}]}}";
        private static readonly string Nav = "{\"code\":-101,\"message\":\"not logged in\",\"data\":{\"isLogin\":false,"
            + "\"wbi_img\":{\"img_url\":\"https://i0.hdslb.com/bfs/wbi/" + PublicImageKey
            + ".png\",\"sub_url\":\"https://i0.hdslb.com/bfs/wbi/" + PublicSubKey + ".png\"}}}";
        private static int _passed;

        private static void Check(bool value, string name)
        {
            if (!value) throw new Exception("FAIL: " + name);
            _passed++;
        }
        private static bool IsNav(HttpRequestMessage request) => request.RequestUri.AbsolutePath == "/x/web-interface/nav";
        private static void SignedDetailRequest(HttpRequestMessage request)
        {
            Check(request.Method.Method == "GET", "video detail uses GET");
            Check(request.RequestUri.Host == "api.bilibili.com"
                && request.RequestUri.AbsolutePath == "/x/web-interface/wbi/view", "current WBI detail endpoint");
            var pairs = request.RequestUri.Query.TrimStart('?').Split('&').Select(pair => pair.Split(new[] { '=' }, 2))
                .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]));
            long timestamp;
            Check(pairs.ContainsKey("bvid") && pairs["bvid"] == Bvid, "selected BV ID is transmitted");
            Check(pairs.ContainsKey("wts") && long.TryParse(pairs["wts"], out timestamp)
                && Math.Abs(timestamp - DateTimeOffset.UtcNow.ToUnixTimeSeconds()) <= 60, "fresh WBI timestamp");
            Check(pairs.ContainsKey("w_rid") && Regex.IsMatch(pairs["w_rid"], "^[0-9a-f]{32}$"), "WBI digest on wire");
            var canonical = string.Join("&", request.RequestUri.Query.TrimStart('?').Split('&')
                .Where(pair => !pair.StartsWith("w_rid=", StringComparison.Ordinal)));
            string digest;
            using (var md5 = System.Security.Cryptography.MD5.Create())
                digest = BitConverter.ToString(md5.ComputeHash(Encoding.UTF8.GetBytes(canonical + PublicMixin)))
                    .Replace("-", "").ToLowerInvariant();
            Check(digest == pairs["w_rid"], "wire digest matches independent fixed-mixin MD5");
        }
        private static void StubDetail(string text, int status = 200)
        {
            Transport.Reset((request, token) =>
            {
                if (IsNav(request)) return Transport.Json(Nav);
                SignedDetailRequest(request);
                return Transport.Json(text, status);
            });
        }
        private static async Task ExpectApiError(Func<Task> action, int code, string name)
        {
            try { await action(); }
            catch (BilibiliApiException ex) { Check(ex.Code == code, name + " preserves error code"); return; }
            throw new Exception("FAIL: expected API error: " + name);
        }
        private static async Task TestDanmakuBytes()
        {
            const long cid = 279786;
            const string declaration = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>";
            const string text = "前方高能 中文 🎮 & > 日本語 かな";
            const string body = "<i>"
                + "<d p=\"3.25,1,25,16711808,0,0,0,0\">前方高能 中文 🎮 &amp; &gt; 日本語 かな</d>"
                + "<d p=\"0.125,4,25,65280,0,0,0,0\">底部弹幕</d>"
                + "<d p=\"2.75,5,25,255,0,0,0,0\">顶部弹幕</d>"
                + "<d p=\"1.5,6,25,-1,0,0,0,0\">逆向滚动</d>"
                + "<d p=\"4,1,25,0,0,0,0,0\">ASCII [OHHHH] 000000</d>"
                + "<d p=\"0,7,25,16777215,0,0,0,0\">[\"advanced\",\"payload\"]</d>"
                + "<d p=\"NaN,1,25,16777215,0,0,0,0\">invalid time</d>"
                + "<d p=\"-1,1,25,16777215,0,0,0,0\">negative time</d></i>";
            // Every fixture begins with HTTP response bytes, so a regression to
            // ReadAsStringAsync reproduces mojibake instead of passing a predecoded string.
            foreach (var charset in new[] { null, "iso-8859-1", "utf-8" })
            foreach (var prefix in new[] { "", declaration, "\ufeff" + declaration })
            {
                var content = new HttpByteContent(Encoding.UTF8.GetBytes(prefix + body), charset);
                Transport.Reset((request, token) =>
                {
                    Check(request.Method.Method == "GET" && request.RequestUri.Host == "api.bilibili.com"
                        && request.RequestUri.PathAndQuery == "/x/v1/dm/list.so?oid=" + cid,
                        "danmaku uses the selected CID and unchanged read-only endpoint");
                    return Task.FromResult(new HttpResponseMessage { Content = content });
                });
                using (var client = new BilibiliClient())
                {
                    var items = await client.GetDanmakuAsync(cid);
                    Check(items.Count == 5 && items[3].Text == text,
                        "raw UTF-8 Chinese, emoji, Japanese and XML entities survive HTTP charset " + (charset ?? "missing"));
                    Check(items[0].Time == 0.125 && items[0].Mode == 4 && items[0].Color == 65280
                        && items[1].Time == 1.5 && items[1].Mode == 6 && items[1].Color == 0xffffff
                        && items[2].Time == 2.75 && items[2].Mode == 5 && items[2].Color == 255
                        && items[3].Time == 3.25 && items[3].Mode == 1 && items[3].Color == 16711808,
                        "danmaku sort order, times, colors and supported modes preserved");
                    Check(items[4].Text == "ASCII [OHHHH] 000000" && items[4].Color == 0,
                        "ASCII content and zero color preserved");
                    Check(content.BufferReads == 1 && content.StringReads == 0 && Transport.Requests.Count == 1,
                        "danmaku consumes original bytes exactly once without an HTTP string conversion");
                }
            }

            foreach (var invalid in new[] {
                "<html>not danmaku</html>", "<i><d>truncated",
                "<!DOCTYPE i [<!ENTITY denied SYSTEM 'file:///never-read-danmaku-fixture'>]><i><d>&denied;</d></i>" })
            {
                var content = new HttpByteContent(Encoding.UTF8.GetBytes(invalid), null);
                Transport.Reset((request, token) => Task.FromResult(new HttpResponseMessage { Content = content }));
                using (var client = new BilibiliClient())
                    await ExpectApiError(async () => { await client.GetDanmakuAsync(cid); }, -1,
                        "non-XML, wrong XML root, truncated XML and external DTD rejected");
            }

            var blocked = new HttpByteContent(Encoding.UTF8.GetBytes(body), null);
            Transport.Reset((request, token) => Task.FromResult(new HttpResponseMessage { StatusCode = 412, Content = blocked }));
            using (var client = new BilibiliClient())
                await ExpectApiError(async () => { await client.GetDanmakuAsync(cid); }, 412, "danmaku HTTP 412");
            Check(blocked.BufferReads == 0 && blocked.StringReads == 0 && Transport.Requests.Count == 1,
                "HTTP failure is handled before decoding, without retries");

            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Transport.Reset(async (request, token) =>
            {
                started.SetResult(true);
                await Task.Delay(Timeout.Infinite, token);
                throw new InvalidOperationException("Canceled danmaku fixture unexpectedly completed.");
            });
            using (var client = new BilibiliClient())
            using (var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                var pending = client.GetDanmakuAsync(cid, cancel.Token);
                if (await Task.WhenAny(started.Task, pending) == pending)
                {
                    await pending;
                    throw new Exception("FAIL: danmaku request completed before cancellation.");
                }
                cancel.Cancel();
                var canceled = false;
                try { await pending; }
                catch (OperationCanceledException) { canceled = true; }
                Check(canceled && Transport.Requests.Count == 1, "danmaku caller cancellation preserved without retry");
            }
            Transport.Reset((request, token) => throw new InvalidOperationException("Must not send pre-canceled request."));
            using (var client = new BilibiliClient())
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel();
                var canceled = false;
                try { await client.GetDanmakuAsync(cid, cancel.Token); }
                catch (OperationCanceledException) { canceled = true; }
                Check(canceled && Transport.Requests.Count == 0, "pre-canceled danmaku performs no network operation");
            }
        }
        public static async Task<string> Run()
        {
            _passed = 0;
            StubDetail(Detail);
            using (var client = new BilibiliClient())
            {
                var video = await client.GetVideoAsync(Bvid);
                Check(!client.IsLoggedIn && client.CurrentUser == null, "anonymous nav does not create a logged-in user");
                Check(video.Aid == 170001 && video.Cid == 279786 && video.Bvid == Bvid, "playback/comment IDs mapped");
                Check(video.Parts.Count == 2 && video.Parts[1].Cid == 279787 && video.Parts[1].Page == 2
                    && video.Parts[1].Title == "Second", "multiple parts mapped");
                Check(video.Title == "测试 & Xbox" && video.Owner == "fixture author" && video.Description == "fixture description"
                    && video.CoverUrl == "https://i0.hdslb.com/test.jpg" && video.DurationText == "1:06", "detail metadata mapped");
                await client.GetVideoAsync(Bvid);
                Check(Transport.Requests.Count == 3 && Transport.Requests.Count(r => r.Uri.AbsolutePath == "/x/web-interface/nav") == 1,
                    "anonymous WBI keys cached across detail requests");
            }

            StubDetail(Detail.Replace("\"cid\":279786,\"title\"", "\"cid\":0,\"title\""));
            using (var client = new BilibiliClient())
                Check((await client.GetVideoAsync(Bvid)).Cid == 279786, "first part supplies missing top-level CID");

            StubDetail("<html>request blocked</html>", 412);
            using (var client = new BilibiliClient())
                await ExpectApiError(async () => { await client.GetVideoAsync(Bvid); }, 412, "HTTP 412");
            Check(Transport.Requests.Count == 2 && Transport.Requests.All(r => r.Method == "GET")
                && Transport.Requests.All(r => r.Uri.AbsolutePath != "/x/web-interface/view"),
                "HTTP 412 performs no automatic retry, legacy fallback, or POST");

            StubDetail("not JSON");
            using (var client = new BilibiliClient())
                await ExpectApiError(async () => { await client.GetVideoAsync(Bvid); }, -1, "malformed JSON");
            StubDetail("{\"code\":0,\"data\":{\"bvid\":\"BV1xx411c7mD\",\"aid\":170001,\"pages\":[]}}");
            using (var client = new BilibiliClient())
                await ExpectApiError(async () => { await client.GetVideoAsync(Bvid); }, -1, "missing playback CID");
            StubDetail("{\"code\":0,\"data\":null}");
            using (var client = new BilibiliClient())
                await ExpectApiError(async () => { await client.GetVideoAsync(Bvid); }, -1, "missing detail object");

            var detailStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Transport.Reset(async (request, token) =>
            {
                if (IsNav(request)) return await Transport.Json(Nav);
                SignedDetailRequest(request);
                detailStarted.SetResult(true);
                await Task.Delay(Timeout.Infinite, token);
                throw new InvalidOperationException("Canceled fixture unexpectedly completed.");
            });
            using (var client = new BilibiliClient())
            using (var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                var pending = client.GetVideoAsync(Bvid, cancel.Token);
                // Fail promptly if a future regression faults before reaching detail;
                // never leave the test waiting on an event its failed request cannot set.
                if (await Task.WhenAny(detailStarted.Task, pending) == pending)
                {
                    await pending;
                    throw new Exception("FAIL: detail request completed before cancellation.");
                }
                cancel.Cancel();
                var canceled = false;
                try { await pending; }
                catch (OperationCanceledException) { canceled = true; }
                Check(canceled, "in-flight caller cancellation remains cancellation");
                Check(Transport.Requests.Count == 2, "cancellation triggers no retry");
            }
            StubDetail(Detail);
            using (var client = new BilibiliClient())
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel();
                var canceled = false;
                try { await client.GetVideoAsync(Bvid, cancel.Token); }
                catch (OperationCanceledException) { canceled = true; }
                Check(canceled && Transport.Requests.Count == 0, "pre-canceled request performs no network operation");
                var rejected = false;
                try { await client.GetVideoAsync("not-a-BV-id"); }
                catch (ArgumentException) { rejected = true; }
                Check(rejected && Transport.Requests.Count == 0, "invalid BV ID rejected before network operation");
            }
            await TestDanmakuBytes();
            return "PASS: " + _passed + " assertions using production BilibiliClient: signed video details, WBI cache/mapping, raw-byte UTF-8 danmaku with absent/mislabeled charset, XML entities/BOM/DTD rejection, metadata, HTTP errors, and cancellation. Fake WinRT transport only; no network/account actions or Xbox runtime validation.";
        }
    }
}
