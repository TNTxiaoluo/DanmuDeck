// TEST ONLY. Production tracker/queue/API, deterministic playback samples and
// a fake HTTP transport. No requests are sent to an account or external server.
namespace BiliXbox.PlaybackHistoryTests
{
    using BiliXbox.Services;
    using BiliXbox.VideoDetailTests;
    using System.Reflection;
    public static class Tests
    {
        private static int _checks;
        private static void Check(bool value, string name)
        { if (!value) throw new Exception("FAIL: " + name); ++_checks; }
        private static PlaybackHistoryAccount Account(long mid = 42, long epoch = 0)
            => new PlaybackHistoryAccount(mid, epoch, "fixture-session-" + mid);
        private static PlaybackHistoryTarget Target(PlaybackHistoryAccount account = null, long aid = 100, long cid = 200, long ep = 0)
            => new PlaybackHistoryTarget(account ?? Account(), aid, "BV1xx411c7mD", cid, ep, ep > 0 ? 11 : 0, 120);
        private static PlaybackHistoryReport Report(PlaybackHistoryTarget target = null, int position = 97, bool ended = false)
            => new PlaybackHistoryReport(target ?? Target(), 1700000000L, position, 300, 20, position, ended ? 4 : 0, ended);
        private static void Set(object instance, string name, object value)
            => instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(instance, value);
        private static void Seed(BilibiliClient api, long mid = 42, long epoch = 0)
        {
            var cookies = (ProtectedCookieStore)typeof(BilibiliClient).GetField("_cookies", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(api);
            var response = new HttpResponseMessage();
            response.Headers["Set-Cookie"] = "SESSDATA=fixture-session-" + mid + "; Domain=.bilibili.com; Path=/; Secure, bili_jct=fixture-csrf; Domain=.bilibili.com; Path=/; Secure";
            cookies.ApplyResponseCookies(response, new Uri("https://api.bilibili.com/"));
            typeof(BilibiliClient).GetProperty("CurrentUser").SetValue(api, new UserProfile { Mid = mid, Name = "fixture" });
            Set(api, "_loginSessionEpoch", epoch);
            Set(api, "_imageKey", "7cd084941338484aae1ad9425b84077c");
            Set(api, "_subKey", "4932caff0ff746eab6f01bf08b70ac45");
            Set(api, "_keysExpire", DateTimeOffset.UtcNow.AddHours(1));
        }
        private static Dictionary<string, string> Query(HttpRequestMessage request)
            => request.RequestUri.Query.TrimStart('?').Split('&').Select(pair => pair.Split(new[] { '=' }, 2))
                .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]));
        private static async Task Reject(Func<Task> action, string name)
        {
            try { await action(); }
            catch (Exception ex) when (ex is BilibiliApiException || ex is ArgumentException || ex is OperationCanceledException)
            { Check(true, name); return; }
            throw new Exception("FAIL: expected rejection " + name);
        }
        private static void Tracker()
        {
            var tracker = new PlaybackHistoryTracker(); var target = Target(); var source = new object();
            Check(tracker.Observe(target, source, 97, 300, false, 0, 1700000000) == null, "opening/restored but unplayed video creates no history");
            Check(tracker.Observe(target, source, 97, 300, false, 40, 1700000040) == null && tracker.Flush() == null,
                "time in paused player does not become viewing");
            tracker.Observe(target, source, 97, 300, true, 41, 1700000041);
            var first = tracker.Observe(target, source, 98, 300, true, 42, 1700000042);
            Check(first != null && first.PositionSeconds == 98 && first.WatchedSeconds == 1 && first.PlayType == 1,
                "first actual second reports the restored position");
            Check(tracker.Observe(target, source, 200, 300, true, 42.1, 1700000042) == null,
                "large seek cannot trigger history heartbeat");
            Check(tracker.Flush() == null, "seek without real advance is not uploaded");
            tracker.Observe(target, source, 201, 300, true, 43.1, 1700000043);
            var pause = tracker.Observe(target, source, 201.5, 300, false, 43.6, 1700000043);
            Check(pause != null && pause.PositionSeconds == 201 && pause.WatchedSeconds == 2 && pause.PlayType == 2,
                "pause flush includes only observed advancement");
            Check(tracker.Flush() == null, "same pause snapshot is consumed once");
            tracker.Observe(target, source, 201.5, 300, true, 44, 1700000044);
            for (int i = 1; i < 15; ++i)
                Check(tracker.Observe(target, source, 201.5 + i, 300, true, 44 + i, 1700000044 + i) == null,
                    "heartbeat stays throttled until 15 new played seconds");
            var periodic = tracker.Observe(target, source, 216.5, 300, true, 59, 1700000059);
            Check(periodic != null && periodic.PlayType == 0 && periodic.WatchedSeconds == 17,
                "15-second playback heartbeat carries cumulative real viewing time");
            tracker.Observe(target, source, 217.5, 300, true, 60, 1700000060);
            var switched = tracker.Observe(Target(cid: 201), new object(), 0, 50, true, 61, 1700000061);
            Check(switched != null && switched.Target.Cid == 200 && switched.PositionSeconds == 217 && switched.PlayType == 4,
                "part switch flushes the old target instead of attributing its position to new cid");
            Check(tracker.Flush(true) == null, "unplayed replacement part cannot be completed");
            tracker.Reset(); tracker.Observe(target, source, 0, 300, true, 0, 1700000000);
            tracker.Observe(target, source, 1, 300, true, 1, 1700000001);
            var recovery = new object();
            Check(tracker.Observe(Target(), recovery, 97, 300, true, 2, 1700000002) == null,
                "quality recovery restores source without counting jump or starting second watch session");
            tracker.Observe(Target(), recovery, 98, 300, true, 3, 1700000003);
            var complete = tracker.Flush(true);
            Check(complete != null && complete.Completed && complete.WatchedSeconds == 2 && complete.StartTimestamp == 1700000000,
                "true ended event completes a genuine preserved viewing session");
            Check(tracker.Flush(true) == null, "completion sent only once");
            tracker.Reset(); tracker.Observe(target, source, 0, 300, true, 0, 1700000000);
            tracker.Observe(target, source, 60, 300, true, 60, 1700000060);
            Check(tracker.Flush() == null, "suspend/time-gap advancement is not fabricated watch time");
        }
        private static async Task Api()
        {
            Transport.Reset((request, ct) => throw new Exception("invalid histories must never reach transport"));
            using (var api = new BilibiliClient())
            {
                Check(api.CapturePlaybackHistoryAccount() == null, "anonymous playback has no upload context");
                await Reject(() => api.ReportPlaybackHistoryAsync(Report()), "anonymous/stale session denied");
                Seed(api);
                Check(api.CapturePlaybackHistoryAccount() != null, "authenticated CSRF session can report history");
                await Reject(() => api.ReportPlaybackHistoryAsync(new PlaybackHistoryReport(Target(), 1700000000, 0, 30, 0, 0, 1, false)),
                    "unwatched report rejected before POST");
                await Reject(() => api.ReportPlaybackHistoryAsync(Report(Target(Account(42, 1)))), "same MID relogin epoch cannot replay stale report");
                Check(Transport.Requests.Count == 0, "validation and account mismatch cause no writes");
            }
            int posts = 0;
            Transport.Reset((request, ct) =>
            {
                ++posts;
                Check(request.Method.Method == "POST" && request.RequestUri.Scheme == "https"
                    && request.RequestUri.Host == "api.bilibili.com" && request.RequestUri.AbsolutePath == "/x/click-interface/web/heartbeat",
                    "history uses genuine official HTTPS heartbeat POST");
                var form = ((HttpFormUrlEncodedContent)request.Content).Values; var query = Query(request);
                Check(form["csrf"] == "fixture-csrf" && form["csrf_token"] == "fixture-csrf"
                    && form["mid"] == "42" && request.Headers["Cookie"].Contains("SESSDATA=fixture-session-42"),
                    "heartbeat reuses bound account cookie and CSRF");
                Check(query["w_aid"] == form["aid"] && query["w_mid"] == form["mid"]
                    && query["w_played_time"] == form["played_time"] && query.ContainsKey("wts") && query.ContainsKey("w_rid"),
                    "official player WBI query matches POST progress fields");
                var canonical = string.Join("&", request.RequestUri.Query.TrimStart('?').Split('&').Where(pair => !pair.StartsWith("w_rid=")));
                using (var md5 = System.Security.Cryptography.MD5.Create())
                    Check(query["w_rid"] == BitConverter.ToString(md5.ComputeHash(Encoding.UTF8.GetBytes(canonical + "ea1db124af3c7062474693fa704f4ff8"))).Replace("-", "").ToLowerInvariant(),
                        "independent known-mixin digest validates signed history wire request");
                Check(form["aid"] == "100" && form["cid"] == "200" && form["quality"] == "120"
                    && form["real_played_time"] == "20" && form["video_duration"] == "300" && !form.ContainsKey("no_history"),
                    "history retains real video/part, quality and progress without suppressing history");
                if (posts == 1)
                    Check(form["type"] == "3" && form["played_time"] == "97" && !form.ContainsKey("epid"), "ordinary video history does not masquerade as PGC");
                else
                    Check(form["type"] == "4" && form["epid"] == "501" && form["sid"] == "11" && form["played_time"] == "-1"
                        && form["last_play_progress_time"] == "300", "PGC completion includes episode/season plus real final progress");
                return Transport.Json("{\"code\":0,\"data\":null}");
            });
            using (var api = new BilibiliClient())
            {
                Seed(api);
                await api.ReportPlaybackHistoryAsync(Report());
                await api.ReportPlaybackHistoryAsync(Report(Target(ep: 501), 300, true));
                Check(posts == 2, "data:null success accepted for UGC and PGC writes");
            }
            posts = 0;
            Transport.Reset((request, ct) => { ++posts; return Transport.Json("{\"code\":-101,\"message\":\"expired\"}"); });
            using (var api = new BilibiliClient())
            {
                Seed(api); await Reject(() => api.ReportPlaybackHistoryAsync(Report()), "server failure never becomes fake success");
                Check(posts == 1, "API never automatically repeats uncertain history write");
            }
            Transport.Reset((request, ct) => throw new Exception("queued previous account must not POST"));
            using (var api = new BilibiliClient())
            {
                Seed(api); var old = api.CapturePlaybackHistoryAccount();
                Seed(api, 84, 1);
                await Reject(() => api.ReportPlaybackHistoryAsync(Report(Target(old))), "switching account rejects old pending history");
            }
        }
        private static async Task Queue()
        {
            var started = new TaskCompletionSource<bool>(); var release = new TaskCompletionSource<bool>();
            var writes = new List<PlaybackHistoryReport>(); int active = 0, maxActive = 0, successes = 0;
            using (var queue = new PlaybackHistoryUploadQueue(async (report, ct) =>
            {
                maxActive = Math.Max(maxActive, ++active); writes.Add(report);
                if (writes.Count == 1) { started.TrySetResult(true); await release.Task; }
                --active;
                if (report.PositionSeconds == 97) throw new InvalidOperationException("ambiguous response");
            }, report => ++successes))
            {
                queue.Enqueue(Report()); await started.Task;
                queue.Enqueue(Report(position: 112)); queue.Enqueue(Report(position: 127));
                queue.Enqueue(Report(Target(cid: 201), 1));
                release.SetResult(true); await queue.DrainAsync();
                Check(maxActive == 1 && writes.Count == 3 && writes[1].PositionSeconds == 127 && writes[2].Target.Cid == 201,
                    "single writer coalesces queued same-part progress, retains next part");
                Check(successes == 2 && writes.Count(report => report.PositionSeconds == 97) == 1,
                    "uncertain first POST is consumed once, only later real snapshots continue");
            }
            started = new TaskCompletionSource<bool>(); int cancellations = 0; writes.Clear();
            var cancelled = new PlaybackHistoryUploadQueue(async (report, ct) =>
            { writes.Add(report); started.TrySetResult(true); try { await Task.Delay(30000, ct); } catch (OperationCanceledException) { ++cancellations; throw; } }, null);
            cancelled.Enqueue(Report()); await started.Task; cancelled.Enqueue(Report(Target(cid: 201), 1));
            cancelled.Dispose(); await cancelled.DrainAsync();
            Check(writes.Count == 1 && cancellations == 1, "account/lifetime cancellation aborts active write and discards queued old-account reports");
            int attempts = 0;
            using (var queue = new PlaybackHistoryUploadQueue((report, ct) =>
            {
                if (++attempts == 1) throw new BilibiliApiException(-1, "fixture offline");
                return Task.CompletedTask;
            }, null))
            {
                queue.Enqueue(Report()); await queue.DrainAsync();
                Check(attempts == 1, "transient failure does not trigger a background retry loop");
                Check(queue.RetryFailed(), "final flush can recover failed first checkpoint even without further viewing");
                await queue.DrainAsync();
                Check(attempts == 2 && !queue.RetryFailed(), "successful retry clears failed progress");
            }
            attempts = 0;
            using (var queue = new PlaybackHistoryUploadQueue((report, ct) =>
            {
                ++attempts; throw new BilibiliApiException(412, "fixture restricted");
            }, null))
            {
                queue.Enqueue(Report()); await queue.DrainAsync();
                Check(attempts == 1 && !queue.RetryFailed(), "platform restriction is not retried by final flush");
            }
        }
        private static async Task Integration()
        {
            bool stored = false; int reads = 0, posts = 0;
            Transport.Reset((request, ct) =>
            {
                if (request.Method.Method == "POST")
                {
                    ++posts; stored = true;
                    var form = ((HttpFormUrlEncodedContent)request.Content).Values;
                    Check(form["cid"] == "200" && form["played_time"] == "98", "leaving captures old source position even after current cid/episode have changed");
                    return Transport.Json("{\"code\":0,\"data\":null}");
                }
                ++reads;
                Check(request.RequestUri.AbsolutePath == "/x/web-interface/history/cursor", "returning to cached history uses real history read endpoint");
                return Transport.Json(stored
                    ? "{\"code\":0,\"data\":{\"list\":[{\"title\":\"watched\",\"progress\":98,\"duration\":300,\"history\":{\"oid\":100,\"cid\":200,\"business\":\"archive\",\"bvid\":\"BV1xx411c7mD\"}}],\"cursor\":{\"max\":0,\"view_at\":0,\"business\":\"\"}}}"
                    : "{\"code\":0,\"data\":{\"list\":[],\"cursor\":{\"max\":0,\"view_at\":0,\"business\":\"\"}}}");
            });
            using (var api = new BilibiliClient())
            {
                Seed(api);
                var page = new BiliXbox.MainPage(api);
                await page.AccountLibrary.ReadEmptyHistory();
                Check(reads == 1 && page.AccountLibrary.HistoryCount == 0, "new account starts with genuinely empty cached history");
                page.StartHistoryFixture(); page.ObserveHistoryFixture();
                Check(posts == 0, "opening MainPage player alone writes nothing");
                await Task.Delay(1100);
                page.AdvanceHistoryFixture(98); // actual +1 second after restored position 97
                page.LeaveHistoryFixture(); // changes cid before StopMedia, as LoadPartAsync does
                await page.FlushHistoryFixture();
                Check(posts == 1, "MainPage leaving enqueues real playback once");
                page.AccountLibrary.RestoreFocus(); await page.AccountLibrary.PendingRefresh;
                Check(reads == 2 && page.AccountLibrary.HistoryCount == 1,
                    "successful upload invalidates old empty cache; actual library RestoreFocus reloads history on return");
                page.StopHistoryFixture();
            }
        }
        public static async Task<string> Run()
        {
            Tracker(); await Api(); await Queue(); await Integration();
            return "PASS: " + _checks + " playback history tracker, account API and upload queue checks (fixture transport only).";
        }
    }
}

namespace Windows.Media.Playback
{
    public enum MediaPlaybackState { None, Playing, Paused, Buffering }
}
namespace Windows.UI.Core
{
    public enum CoreDispatcherPriority { Normal }
    public sealed class HistoryDispatcher
    { public Task RunAsync(CoreDispatcherPriority priority, Action action) { action(); return Task.CompletedTask; } }
}
namespace Windows.UI.Xaml.Controls { public class Control { } }
namespace BiliXbox.Controls
{
    public sealed class AccountLibraryView
    {
        private enum LibraryPane { Overview, History }
        private sealed class ViewState { public LibraryPane Pane; public bool NeedsRefresh; public int SelectedIndex = 0; }
        private readonly ViewState _state = new ViewState { Pane = LibraryPane.History };
        private readonly Stack<ViewState> _trail = new Stack<ViewState>();
        private readonly Control _back = new Control(), _history = new Control(), _favorites = new Control(), _follows = new Control();
        private Control _lastFocus;
        private sealed class TestList { public List<object> Items = new List<object>(); }
        private readonly TestList ActiveList = new TestList();
        private readonly BilibiliClient _api;
        public int HistoryCount { get; private set; }
        public Task PendingRefresh { get; private set; } = Task.CompletedTask;
        public AccountLibraryView(BilibiliClient api) { _api = api; }
        public Task ReadEmptyHistory() => RefreshAsync();
        private async Task RefreshAsync() { HistoryCount = (await _api.GetHistoryAsync()).Items.Count; }
        private void Run(Func<Task> action) { PendingRefresh = action(); }
        private bool EnsureSession() => _api.IsLoggedIn;
        private void Focus(Control control) { _lastFocus = control; }
        private bool IsAvailable(Control control) => control != null;
        private void Render() { }
        private void FocusItem(TestList list, int index) { }
        private void FocusFirst() { }
        /* HISTORY_DIRTY_METHOD */
        /* HISTORY_RESTORE_METHOD */
    }
}
namespace BiliXbox
{
    using BiliXbox.Controls;
    public sealed partial class MainPage
    {
        internal readonly BilibiliClient _api;
        private bool _disposed, _watching, _wantsPlayback;
        private bool _creatorVisible = false, _libraryVisible = false, _sourceLoading = false, _sessionBuffering = false;
        private VideoDetail _video;
        private long _currentCid;
        private AccountLibraryItem _playingEpisodeItem;
        private PlaybackInfo _playback = null;
        private sealed class TestSession
        {
            public TimeSpan Position, NaturalDuration;
            public MediaPlaybackState PlaybackState;
        }
        private sealed class TestMedia { public object Source; public TestSession PlaybackSession = new TestSession(); }
        private readonly TestMedia _media = new TestMedia();
        private readonly HistoryDispatcher Dispatcher = new HistoryDispatcher();
        internal readonly AccountLibraryView AccountLibrary;
        internal MainPage(BilibiliClient api) { _api = api; AccountLibrary = new AccountLibraryView(api); }
        internal void StartHistoryFixture()
        {
            _watching = _wantsPlayback = true; _currentCid = 200;
            _video = new VideoDetail { Aid = 100, Bvid = "BV1xx411c7mD" };
            _media.Source = new object(); _media.PlaybackSession.Position = TimeSpan.FromSeconds(97);
            _media.PlaybackSession.NaturalDuration = TimeSpan.FromSeconds(300);
            _media.PlaybackSession.PlaybackState = MediaPlaybackState.Playing;
        }
        internal void ObserveHistoryFixture() => ObservePlaybackHistory();
        internal void AdvanceHistoryFixture(double position) { _media.PlaybackSession.Position = TimeSpan.FromSeconds(position); }
        internal void LeaveHistoryFixture() { _currentCid = 999; _playingEpisodeItem = new AccountLibraryItem { EpisodeId = 777 }; QueuePlaybackHistoryFlush(); }
        internal Task FlushHistoryFixture() => FlushPlaybackHistoryAsync();
        internal void StopHistoryFixture() { StopPlaybackHistory(); _disposed = true; }
    }
}
