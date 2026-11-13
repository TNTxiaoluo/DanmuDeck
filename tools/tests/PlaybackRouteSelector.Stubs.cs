// TEST ONLY: controlled native HTTP/stream doubles. Production selector is
// compiled unchanged. No account, network, signed live URLs or device are used.
namespace Windows.Foundation
{
    public interface IAsyncOperation<T> { System.Threading.Tasks.Task<T> Run(System.Threading.CancellationToken token); }
    public sealed class Operation<T> : IAsyncOperation<T>
    {
        private readonly System.Func<System.Threading.CancellationToken, System.Threading.Tasks.Task<T>> _run;
        public Operation(System.Func<System.Threading.CancellationToken, System.Threading.Tasks.Task<T>> run) { _run = run; }
        public System.Threading.Tasks.Task<T> Run(System.Threading.CancellationToken token) { return _run(token); }
    }
}
namespace System.Runtime.InteropServices.WindowsRuntime
{
    public static class Extensions
    {
        public static System.Threading.Tasks.Task<T> AsTask<T>(this global::Windows.Foundation.IAsyncOperation<T> operation,
            System.Threading.CancellationToken token) { token.ThrowIfCancellationRequested(); return operation.Run(token); }
    }
}
namespace Windows.Storage.Streams
{
    public enum InputStreamOptions { Partial }
    public interface IBuffer { uint Capacity { get; } uint Length { get; set; } }
    public sealed class Buffer : IBuffer { public Buffer(uint capacity) { Capacity = capacity; } public uint Capacity { get; private set; } public uint Length { get; set; } }
    public interface IInputStream : System.IDisposable
    { Windows.Foundation.IAsyncOperation<IBuffer> ReadAsync(IBuffer buffer, uint count, InputStreamOptions options); }
    public sealed class FakeInput : IInputStream
    {
        private readonly BiliXbox.RouteSelectorTests.Plan _plan;
        private readonly ulong _expected;
        private ulong _position;
        public FakeInput(BiliXbox.RouteSelectorTests.Plan plan, ulong expected) { _plan = plan; _expected = expected; }
        public Windows.Foundation.IAsyncOperation<IBuffer> ReadAsync(IBuffer buffer, uint count, InputStreamOptions options)
            => new Windows.Foundation.Operation<IBuffer>(async token =>
            {
                if (_plan.BodyDelay > 0) await System.Threading.Tasks.Task.Delay(_plan.BodyDelay, token);
                token.ThrowIfCancellationRequested();
                var available = _plan.Truncated ? System.Math.Min(_expected, 17UL) : _expected;
                buffer.Length = (uint)System.Math.Min(count, available - _position);
                _position += buffer.Length;
                System.Threading.Interlocked.Add(ref _plan.Bytes, buffer.Length);
                return buffer;
            });
        public void Dispose() { }
    }
}
namespace Windows.Web.Http.Headers
{
    public sealed class HttpRequestHeaderCollection
    {
        public System.Uri Referer { get; set; }
        public readonly System.Collections.Generic.Dictionary<string, string> Values = new System.Collections.Generic.Dictionary<string, string>();
        public bool TryAppendWithoutValidation(string key, string value) { Values[key] = value; return true; }
    }
    public sealed class HttpContentRangeHeaderValue
    { public string Unit = "bytes"; public ulong? Length; public ulong? FirstBytePosition; public ulong? LastBytePosition; }
    public sealed class HttpContentHeaderCollection { public ulong? ContentLength; public HttpContentRangeHeaderValue ContentRange; }
}
namespace Windows.Web.Http.Filters
{
    public enum HttpCookieUsageBehavior { NoCookies }
    public enum HttpCacheReadBehavior { NoCache }
    public enum HttpCacheWriteBehavior { NoCache }
    public sealed class HttpCacheControl { public HttpCacheReadBehavior ReadBehavior; public HttpCacheWriteBehavior WriteBehavior; }
    public sealed class HttpBaseProtocolFilter : System.IDisposable
    {
        public bool AutomaticDecompression;
        public HttpCookieUsageBehavior CookieUsageBehavior;
        public readonly HttpCacheControl CacheControl = new HttpCacheControl();
        public void Dispose() { }
    }
}
namespace Windows.Web.Http
{
    public enum HttpStatusCode { Ok = 200, PartialContent = 206, Forbidden = 403 }
    public enum HttpCompletionOption { ResponseHeadersRead }
    public sealed class HttpMethod { public static readonly HttpMethod Get = new HttpMethod(); }
    public sealed class HttpRequestMessage : System.IDisposable
    {
        public readonly System.Uri RequestUri;
        public readonly Headers.HttpRequestHeaderCollection Headers = new Headers.HttpRequestHeaderCollection();
        public HttpRequestMessage(HttpMethod method, System.Uri uri) { RequestUri = uri; }
        public void Dispose() { }
    }
    public sealed class HttpContent
    {
        public readonly Headers.HttpContentHeaderCollection Headers = new Headers.HttpContentHeaderCollection();
        public BiliXbox.RouteSelectorTests.Plan Plan;
        public ulong Expected;
        public Windows.Foundation.IAsyncOperation<Windows.Storage.Streams.IInputStream> ReadAsInputStreamAsync()
            => new Windows.Foundation.Operation<Windows.Storage.Streams.IInputStream>(token =>
            {
                System.Threading.Interlocked.Increment(ref Plan.BodyOpens);
                return System.Threading.Tasks.Task.FromResult<Windows.Storage.Streams.IInputStream>(new Windows.Storage.Streams.FakeInput(Plan, Expected));
            });
    }
    public sealed class HttpResponseMessage : System.IDisposable
    {
        private bool _disposed;
        public HttpStatusCode StatusCode;
        public readonly HttpContent Content = new HttpContent();
        public HttpResponseMessage() { System.Threading.Interlocked.Increment(ref BiliXbox.RouteSelectorTests.Transport.LiveResponses); }
        public void Dispose() { if (!_disposed) { _disposed = true; System.Threading.Interlocked.Decrement(ref BiliXbox.RouteSelectorTests.Transport.LiveResponses); } }
    }
    public sealed class HttpClient : System.IDisposable
    {
        private int _disposed;
        private readonly Filters.HttpBaseProtocolFilter _filter;
        public readonly Headers.HttpRequestHeaderCollection DefaultRequestHeaders = new Headers.HttpRequestHeaderCollection();
        public HttpClient(Filters.HttpBaseProtocolFilter filter) { _filter = filter; System.Threading.Interlocked.Increment(ref BiliXbox.RouteSelectorTests.Transport.LiveClients); }
        public Windows.Foundation.IAsyncOperation<HttpResponseMessage> SendRequestAsync(HttpRequestMessage request, HttpCompletionOption option)
            => new Windows.Foundation.Operation<HttpResponseMessage>(token =>
            {
                if (_filter.AutomaticDecompression || _filter.CookieUsageBehavior != Filters.HttpCookieUsageBehavior.NoCookies
                    || DefaultRequestHeaders.Referer.AbsoluteUri != BiliXbox.Services.BilibiliClient.Referer
                    || DefaultRequestHeaders.Values["User-Agent"] != BiliXbox.Services.BilibiliClient.UserAgent
                    || DefaultRequestHeaders.Values["Accept-Encoding"] != "identity" || DefaultRequestHeaders.Values.ContainsKey("Cookie"))
                    throw new System.Exception("Probe must preserve production headers without account cookies");
                return BiliXbox.RouteSelectorTests.Transport.Send(request, token);
            });
        public void Dispose() { if (System.Threading.Interlocked.Exchange(ref _disposed, 1) == 0) System.Threading.Interlocked.Decrement(ref BiliXbox.RouteSelectorTests.Transport.LiveClients); }
    }
}
namespace BiliXbox.Services
{
    internal static class BilibiliClient
    {
        internal const string Referer = "https://www.bilibili.com/";
        internal const string UserAgent = "Production header fixture";
    }
}
namespace BiliXbox.RouteSelectorTests
{
    public sealed class Plan
    {
        public int HeaderDelay, BodyDelay, Status = 206, BodyOpens;
        public long Bytes;
        public ulong Length = 1024 * 1024;
        public bool BadFirst, BadLast, BadLength, Fail, Truncated;
    }
    public static class Transport
    {
        private static readonly object Gate = new object();
        public static readonly Dictionary<string, Plan> Plans = new Dictionary<string, Plan>();
        public static readonly List<string> Requests = new List<string>();
        public static readonly List<string> Ranges = new List<string>();
        public static int LiveClients, LiveResponses;
        public static void Reset() { Plans.Clear(); Requests.Clear(); Ranges.Clear(); }
        public static async Task<Windows.Web.Http.HttpResponseMessage> Send(Windows.Web.Http.HttpRequestMessage request, CancellationToken token)
        {
            Plan plan;
            string range = request.Headers.Values["Range"];
            lock (Gate) { Requests.Add(request.RequestUri.AbsoluteUri); Ranges.Add(range); plan = Plans[request.RequestUri.Host]; }
            if (plan.HeaderDelay > 0) await Task.Delay(plan.HeaderDelay, token);
            token.ThrowIfCancellationRequested();
            if (plan.Fail) throw new System.IO.IOException("controlled connection failure");
            var requested = ulong.Parse(range.Substring("bytes=0-".Length)) + 1;
            var expected = Math.Min(requested, plan.Length);
            var response = new Windows.Web.Http.HttpResponseMessage { StatusCode = (Windows.Web.Http.HttpStatusCode)plan.Status };
            response.Content.Plan = plan; response.Content.Expected = expected;
            response.Content.Headers.ContentLength = plan.BadLength ? 1 : expected;
            response.Content.Headers.ContentRange = new Windows.Web.Http.Headers.HttpContentRangeHeaderValue
            { Length = plan.Length, FirstBytePosition = plan.BadFirst ? 1UL : 0, LastBytePosition = plan.BadLast ? requested + 1 : expected - 1 };
            return response;
        }
    }
    public static class Tests
    {
        private static int _count;
        private static string Url(string host) => "https://" + host + "/media.m4s?token=Exact%2fsigned&other=1";
        private static void Check(bool value, string name) { if (!value) throw new Exception(name); ++_count; }
        private static void Clean() => Check(Transport.LiveClients == 0 && Transport.LiveResponses == 0, "all probe clients and responses disposed");
        public static async Task<string> Run()
        {
            _count = 0;
            Transport.Reset();
            var none = await Services.PlaybackRouteSelector.SelectAsync(null, false, CancellationToken.None);
            Check(none.Count == 0 && Transport.Requests.Count == 0, "empty catalogue does not probe");
            var single = await Services.PlaybackRouteSelector.SelectAsync(new[] { Url("only.test"), Url("only.test"), "file:///private", "http://insecure.test/v", "https://user:secret@private.test/v", "https://fragment.test/v#x" }, false, CancellationToken.None);
            Check(single.Count == 1 && single[0] == Url("only.test") && Transport.Requests.Count == 0, "single valid unique URL has zero probe overhead");
            Clean();

            Transport.Reset();
            Transport.Plans["slow.test"] = new Plan { HeaderDelay = 250 };
            Transport.Plans["fast.test"] = new Plan { HeaderDelay = 15 };
            Transport.Plans["middle.test"] = new Plan { HeaderDelay = 110 };
            var urls = new[] { Url("slow.test"), Url("fast.test"), Url("middle.test"), Url("unprobed.test") };
            var ranked = await Services.PlaybackRouteSelector.SelectAsync(urls, false, CancellationToken.None);
            Check(ranked.SequenceEqual(new[] { urls[1], urls[2], urls[0], urls[3] }), "completed real byte/time samples rank and preserve all unprobed candidates");
            Check(Transport.Requests.Count == 3 && Transport.Ranges.All(range => range == "bytes=0-65535"), "at most three bounded 64KiB video range requests");
            Check(Transport.Plans.Values.Sum(plan => plan.Bytes) == 3 * 65536L, "video probe traffic never exceeds bounded sample total");
            Clean();

            Transport.Reset();
            Transport.Plans["body-slow.test"] = new Plan { BodyDelay = 100 };
            Transport.Plans["body-fast.test"] = new Plan { HeaderDelay = 50 };
            ranked = await Services.PlaybackRouteSelector.SelectAsync(new[] { Url("body-slow.test"), Url("body-fast.test") }, false, CancellationToken.None);
            Check(ranked[0] == Url("body-fast.test"), "ranking includes full body transfer rather than headers latency alone");
            Clean();

            foreach (var invalid in new[] { "status", "first", "last", "length", "truncated", "network" })
            {
                Transport.Reset();
                var bad = new Plan { Status = invalid == "status" ? 200 : 206, BadFirst = invalid == "first", BadLast = invalid == "last", BadLength = invalid == "length", Truncated = invalid == "truncated", Fail = invalid == "network" };
                Transport.Plans["bad.test"] = bad; Transport.Plans["good.test"] = new Plan { HeaderDelay = 10 };
                ranked = await Services.PlaybackRouteSelector.SelectAsync(new[] { Url("bad.test"), Url("good.test") }, true, CancellationToken.None);
                Check(ranked[0] == Url("good.test") && ranked.Count == 2, "invalid candidate ignored, retained for real downloader: " + invalid);
                Check(Transport.Ranges.All(range => range == "bytes=0-32767") && Transport.Plans.Values.Sum(plan => plan.Bytes) <= 2 * 32768L, "audio traffic bounded: " + invalid);
                if (invalid != "truncated" && invalid != "network") Check(bad.BodyOpens == 0, "invalid response rejected before body read: " + invalid);
                Clean();
            }

            Transport.Reset();
            Transport.Plans["one.test"] = new Plan { Fail = true }; Transport.Plans["two.test"] = new Plan { BadLast = true };
            urls = new[] { Url("one.test"), Url("two.test") };
            ranked = await Services.PlaybackRouteSelector.SelectAsync(urls, false, CancellationToken.None);
            Check(ranked.SequenceEqual(urls), "no valid sample preserves original source order");
            Clean();

            Transport.Reset();
            foreach (var host in new[] { "hung1.test", "hung2.test", "hung3.test" }) Transport.Plans[host] = new Plan { HeaderDelay = 10000 };
            urls = new[] { Url("hung1.test"), Url("hung2.test"), Url("hung3.test"), Url("unprobed.test") };
            var watch = Stopwatch.StartNew();
            ranked = await Services.PlaybackRouteSelector.SelectAsync(urls, false, CancellationToken.None);
            Check(watch.ElapsedMilliseconds < 2300 && ranked.SequenceEqual(urls), "one global deadline releases playback rather than waiting on hung candidates");
            Check(Transport.Requests.Count == 3, "hung selection remains bounded to three candidates");
            await Task.Delay(20); Clean();

            Transport.Reset();
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel(); bool caught = false;
                try { await Services.PlaybackRouteSelector.SelectAsync(urls, false, canceled.Token); } catch (OperationCanceledException) { caught = true; }
                Check(caught && Transport.Requests.Count == 0, "pre-canceled selection cannot start requests");
            }
            foreach (var host in new[] { "hung1.test", "hung2.test", "hung3.test" }) Transport.Plans[host] = new Plan { HeaderDelay = 10000 };
            using (var canceled = new CancellationTokenSource(40))
            {
                watch.Restart(); bool caught = false;
                try { await Services.PlaybackRouteSelector.SelectAsync(urls, false, canceled.Token); } catch (OperationCanceledException) { caught = true; }
                Check(caught && watch.ElapsedMilliseconds < 600, "navigation cancellation exits immediately instead of returning stale rankings");
            }
            await Task.Delay(20); Clean();
            return "PASS: " + _count + " bounded device route-selector assertions (sample speed, exact signed URLs, separate audio/video budgets, cancellation and disposal).";
        }
    }
}
