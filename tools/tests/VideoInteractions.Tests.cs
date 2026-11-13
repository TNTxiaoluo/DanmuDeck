// Test only: production account jar, request bodies and interaction service.
// No real account or network is used and no remote POST is ever executed.
namespace BiliXbox.VideoInteractionTests
{
    using BiliXbox.Services;
    using BiliXbox.VideoDetailTests;
    public static class Tests
    {
        private const long Aid = 117160886206980;
        private static int _checks;
        private static void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); ++_checks; }
        private static JObject Ok(JToken data = null) => data == null ? new JObject { ["code"] = 0 }
            : new JObject { ["code"] = 0, ["data"] = data };
        private static JObject Folder(long id, long mid = 42, int selected = 0, long attr = 22)
            => new JObject { ["id"] = id, ["mid"] = mid, ["title"] = "收藏 &amp; 🎮 " + id,
                ["fav_state"] = selected, ["media_count"] = 7, ["attr"] = attr };
        private static JObject Folders(params JObject[] folders) => Ok(new JObject { ["count"] = folders.Length, ["list"] = new JArray(folders) });
        private static Dictionary<string, string> Query(HttpRequestMessage request)
            => request.RequestUri.Query.TrimStart('?').Split('&').Select(pair => pair.Split(new[] { '=' }, 2))
                .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]));
        private static Dictionary<string, string> Body(HttpRequestMessage request)
            => ((HttpFormUrlEncodedContent)request.Content).Values;
        private static void Stub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler,
            bool csrf = true, bool buvid = true)
        {
            Transport.Reset(async (request, token) =>
            {
                Check(request.RequestUri.Scheme == "https" && (request.RequestUri.Host == "api.bilibili.com"
                    || request.RequestUri.Host == "www.bilibili.com"), "interaction transport stays on official HTTPS hosts");
                if (request.RequestUri.AbsolutePath == "/x/web-interface/nav")
                {
                    var response = new HttpResponseMessage { Content = new HttpStringContent(Ok(new JObject
                    { ["mid"] = 42, ["uname"] = "fixture", ["isLogin"] = true }).ToString()) };
                    response.Headers["Set-Cookie"] = "SESSDATA=fixture-session; Domain=.bilibili.com; Path=/; Secure"
                        + (csrf ? ",bili_jct=fixture-csrf; Domain=.bilibili.com; Path=/; Secure" : "")
                        + (buvid ? ",buvid3=server-issued-fixture; Domain=.bilibili.com; Path=/; Secure" : "");
                    return response;
                }
                if (request.RequestUri.Host == "www.bilibili.com")
                {
                    Check(request.Method.Method == "GET" && request.RequestUri.AbsolutePath == "/", "missing website cookie uses ordinary read bootstrap");
                    var response = new HttpResponseMessage { Content = new HttpStringContent("discarded website") };
                    response.Headers["Set-Cookie"] = "buvid3=server-issued-fixture; Domain=.bilibili.com; Path=/; Secure";
                    return response;
                }
                if (request.Method.Method == "POST")
                {
                    string cookie;
                    Check(request.Headers.TryGetValue("Cookie", out cookie) && cookie.Contains("SESSDATA=fixture-session")
                        && cookie.Contains("buvid3=server-issued-fixture"), "writes reuse actual app account and website jar");
                    Check(Body(request)["csrf"] == "fixture-csrf" && request.Headers["Origin"] == "https://www.bilibili.com",
                        "write sends matching CSRF and origin only in form body");
                    Check(!request.RequestUri.Query.Contains("csrf"), "CSRF is never in request URL");
                }
                return await handler(request, token);
            });
        }
        private static void Stub(Func<HttpRequestMessage, JObject> handler, bool csrf = true, bool buvid = true)
            => Stub((request, token) => Transport.Json(handler(request).ToString()), csrf, buvid);
        private static async Task Login(BilibiliClient client)
        {
            var method = typeof(BilibiliClient).GetMethod("RefreshProfileAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)method.Invoke(client, new object[] { CancellationToken.None });
        }
        private static void ChangeAccount(BilibiliClient client, long? mid)
        {
            var property = typeof(BilibiliClient).GetProperty("CurrentUser");
            property.SetValue(client, mid.HasValue ? new UserProfile { Mid = mid.Value, Name = "changed fixture" } : null);
        }
        private static async Task Error(Func<Task> action, string name)
        {
            try { await action(); }
            catch (Exception ex) when (ex is BilibiliApiException || ex is ArgumentException || ex is OperationCanceledException || ex is ObjectDisposedException)
            { Check(true, name); return; }
            throw new Exception("FAIL: expected rejection: " + name);
        }
        private static int Posts => Transport.Requests.Count(item => item.Method == "POST");
        public static async Task<string> Run()
        {
            _checks = 0;
            Transport.Reset((request, token) => { throw new Exception("Anonymous interactions must fail before transport."); });
            using (var client = new BilibiliClient())
            {
                await Error(() => client.GetVideoInteractionsAsync(Aid), "anonymous state denied");
                await Error(() => client.GetVideoFavoriteFoldersAsync(Aid), "anonymous folder list denied");
                await Error(() => client.SetVideoLikeAsync(Aid, true), "anonymous like denied");
                await Error(() => client.AddVideoCoinsAsync(Aid, 1), "anonymous coin denied");
                await Error(() => client.UpdateVideoFavoritesAsync(Aid, new[] { 10L }, null), "anonymous favorites denied");
                await Error(() => client.TripleVideoAsync(Aid), "anonymous triple denied");
                Check(Transport.Requests.Count == 0, "no anonymous mutations or fabricated state");
            }
            Stub(request =>
            {
                Check(Query(request)["aid"] == Aid.ToString(), "64-bit video ID is preserved in state query");
                switch (request.RequestUri.AbsolutePath)
                {
                    case "/x/web-interface/archive/has/like": return Ok(1);
                    case "/x/web-interface/archive/coins": return Ok(new JObject { ["multiply"] = 1 });
                    case "/x/v2/fav/video/favoured": return Ok(new JObject { ["favoured"] = true });
                    default: throw new Exception("Unexpected state route");
                }
            });
            using (var client = new BilibiliClient())
            {
                await Login(client); var state = await client.GetVideoInteractionsAsync(Aid);
                Check(state.Aid == Aid && state.AccountMid == 42 && state.IsLiked && state.Coins == 1 && state.IsFavorited,
                    "three real state endpoints map independent user actions without optimistic values");
                Check(Posts == 0, "opening interaction state never posts");
            }
            Stub(request =>
            {
                var query = Query(request);
                Check(query["up_mid"] == "42" && query["type"] == "2" && query["rid"] == Aid.ToString(),
                    "folders scoped to account and target video");
                return Folders(Folder(10, 42, 1, 0), Folder(11), Folder(12, 42, 0, 1), Folder(13, 42, 0, 23));
            });
            using (var client = new BilibiliClient())
            {
                await Login(client); var folders = await client.GetVideoFavoriteFoldersAsync(Aid);
                Check(folders.Count == 4 && folders[0].IsSelected && folders[0].IsDefault && !folders[1].IsSelected
                    && !folders[1].IsDefault && folders[0].Title.Contains("& 🎮"), "folder state, default flag and Unicode mapped");
                Check(folders[2].IsDefault && !folders[3].IsDefault,
                    "default-folder bit is independent of public/private bit for attributes 0, 1, 22 and 23");
            }
            Stub(request =>
            {
                Check(request.RequestUri.AbsolutePath == "/x/web-interface/archive/like", "like uses official web operation");
                var form = Body(request);
                Check(form["aid"] == Aid.ToString() && (form["like"] == "1" || form["like"] == "2"), "like/unlike body uses web semantics");
                return Ok(); // Like success legitimately has no data object.
            }, buvid: false);
            using (var client = new BilibiliClient())
            {
                await Login(client); await client.SetVideoLikeAsync(Aid, true); await client.SetVideoLikeAsync(Aid, false);
                Check(Posts == 2 && Transport.Requests.Count(request => request.Uri.Host == "www.bilibili.com") == 1,
                    "like/unlike each send once and reuse server-issued bootstrap cookie");
            }
            Stub(request =>
            {
                if (request.Method.Method == "GET") return Ok(new JObject { ["multiply"] = 0 });
                var form = Body(request);
                Check(request.RequestUri.AbsolutePath == "/x/web-interface/coin/add" && form["multiply"] == "2"
                    && form["select_like"] == "0", "coin quantity explicit and does not silently add like");
                return Ok(new JObject { ["like"] = false });
            });
            using (var client = new BilibiliClient())
            {
                await Login(client); await client.AddVideoCoinsAsync(Aid, 2);
                Check(Posts == 1, "optional false like acknowledgement does not turn accepted coins into failure");
                await Error(() => client.AddVideoCoinsAsync(Aid, 0), "zero coin quantity rejected");
                await Error(() => client.AddVideoCoinsAsync(Aid, 3), "oversized coin quantity rejected");
                Check(Posts == 1, "invalid coin quantity cannot reach POST");
            }
            Stub(request => Ok(new JObject { ["multiply"] = 2 }));
            using (var client = new BilibiliClient())
            { await Login(client); await Error(() => client.AddVideoCoinsAsync(Aid, 1), "known coin limit prevents another submission"); Check(Posts == 0, "coin limit is checked before spending"); }
            Stub(request =>
            {
                if (request.Method.Method == "GET") return Folders(Folder(10), Folder(11, 42, 1));
                var body = Body(request);
                Check(request.RequestUri.AbsolutePath == "/x/v3/fav/resource/deal" && body["rid"] == Aid.ToString() && body["type"] == "2"
                    && body["add_media_ids"] == "10" && body["del_media_ids"] == "11", "favorite save sends only explicit add/remove delta");
                return Ok(new JObject { ["prompt"] = true });
            });
            using (var client = new BilibiliClient())
            {
                await Login(client); await client.UpdateVideoFavoritesAsync(Aid, new[] { 10L, 10L }, new[] { 11L });
                Check(Posts == 1, "duplicate selection does not duplicate mutation");
                await client.UpdateVideoFavoritesAsync(Aid, null, null); Check(Posts == 1, "empty delta is a no-op");
                await Error(() => client.UpdateVideoFavoritesAsync(Aid, new[] { 10L }, new[] { 10L }), "contradictory folder delta denied");
                await Error(() => client.UpdateVideoFavoritesAsync(Aid, new[] { 99L }, null), "stale or foreign folder ID denied");
                Check(Posts == 1, "foreign folder rejection never modifies any folder");
            }
            foreach (var invalid in new[] { Folders(Folder(10, 999)), Folders(Folder(10), Folder(10)),
                Ok(new JObject { ["count"] = 2, ["list"] = new JArray(Folder(10)) }),
                Folders(new JObject { ["id"] = 10, ["mid"] = 42, ["media_count"] = 0 }) })
            {
                Stub(request => invalid);
                using (var client = new BilibiliClient()) { await Login(client); await Error(() => client.GetVideoFavoriteFoldersAsync(Aid), "malformed or partial folder state cannot become unchecked selections"); }
            }
            Stub(request => Ok(new JObject { ["count"] = 0, ["list"] = null }));
            using (var client = new BilibiliClient()) { await Login(client); Check((await client.GetVideoFavoriteFoldersAsync(Aid)).Count == 0, "authoritative empty folder list stays empty"); }
            foreach (bool partial in new[] { false, true })
            {
                Stub(request =>
                {
                    Check(request.RequestUri.AbsolutePath == "/x/web-interface/archive/like/triple" && Body(request).Count == 2
                        && !Body(request).ContainsKey("multiply"), "triple is one official operation with no fabricated coin parameter");
                    return Ok(new JObject { ["like"] = true, ["coin"] = !partial, ["fav"] = true, ["multiply"] = 2 });
                });
                using (var client = new BilibiliClient())
                {
                    await Login(client); var result = await client.TripleVideoAsync(Aid);
                    Check(result.IsComplete == !partial && result.Like && result.Favorite && result.Coin == !partial && result.Coins == 2,
                        "triple preserves independently acknowledged partial results");
                    Check(Posts == 1, "triple never expands into three requests or automatically retries partial results");
                }
            }
            foreach (var response in new[] { new JObject { ["code"] = -104, ["message"] = "coin shortage" },
                Ok(), Ok(new JObject { ["like"] = true, ["coin"] = true, ["fav"] = true }),
                Ok(new JObject { ["like"] = true, ["coin"] = true, ["fav"] = true, ["multiply"] = 0 }),
                Ok(new JObject { ["like"] = 1, ["coin"] = true, ["fav"] = true, ["multiply"] = 2 }) })
            {
                Stub(request => response);
                using (var client = new BilibiliClient())
                { await Login(client); await Error(() => client.TripleVideoAsync(Aid), "failed or malformed triple acknowledgement is not success"); Check(Posts == 1, "uncertain mutation is never automatically repeated"); }
            }
            Stub(request => Ok(), csrf: false);
            using (var client = new BilibiliClient()) { await Login(client); await Error(() => client.SetVideoLikeAsync(Aid, true), "missing CSRF denies mutation before network"); Check(Posts == 0, "missing CSRF no POST"); }
            Stub((request, token) => Task.FromResult(new HttpResponseMessage { StatusCode = 412, Content = new HttpStringContent("restricted") }));
            using (var client = new BilibiliClient()) { await Login(client); await Error(() => client.SetVideoLikeAsync(Aid, true), "HTTP access restriction propagates"); Check(Posts == 1, "HTTP error cannot trigger another POST"); }
            await RunRaces();
            return "PASS: " + _checks + " production video-interaction API assertions (mock transport only; no remote actions).";
        }

        private static async Task RunRaces()
        {
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            Stub(async (request, token) =>
            {
                if (request.Method.Method == "POST") { entered.TrySetResult(true); return await release.Task; }
                return await Transport.Json(Ok(new JObject { ["multiply"] = 0 }).ToString());
            });
            using (var client = new BilibiliClient())
            {
                await Login(client);
                var first = client.SetVideoLikeAsync(Aid, true); await entered.Task;
                using (var cancel = new CancellationTokenSource())
                {
                    var queued = client.AddVideoCoinsAsync(Aid, 1, cancel.Token);
                    Check(Posts == 1 && Transport.Requests.All(request => request.Uri.AbsolutePath != "/x/web-interface/archive/coins"), "mutation gate prevents queued operation preflight while another write pending");
                    cancel.Cancel(); await Error(() => queued, "queued cancellation releases without spending");
                    release.TrySetResult(new HttpResponseMessage { Content = new HttpStringContent(Ok().ToString()) });
                    await first; Check(Posts == 1, "cancelled queued mutation never posts");
                }
            }
            foreach (long? changed in new long?[] { null, 99 })
            {
                var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                var reached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Stub(async (request, token) => { reached.TrySetResult(true); return await pending.Task; });
                using (var client = new BilibiliClient())
                {
                    await Login(client); var first = client.SetVideoLikeAsync(Aid, true); await reached.Task;
                    var queued = client.SetVideoLikeAsync(Aid, false);
                    ChangeAccount(client, changed);
                    pending.TrySetResult(new HttpResponseMessage { Content = new HttpStringContent(Ok().ToString()) });
                    await Error(() => first, "account change after POST invalidates acknowledgement for old account");
                    await Error(() => queued, "account change while queued prevents write with a new session");
                    Check(Posts == 1, "only in-flight old-account mutation may have reached service, never silently reassigned");
                    Check(changed.HasValue ? client.CurrentUser.Mid == changed.Value : client.CurrentUser == null, "late response never restores old account");
                }
            }
            BilibiliClient active = null;
            Stub(request => { ChangeAccount(active, 99); return Folders(Folder(10)); });
            using (var client = active = new BilibiliClient())
            {
                await Login(client); await Error(() => client.UpdateVideoFavoritesAsync(Aid, new[] { 10L }, null), "account change during folder ownership read prevents modification");
                Check(Posts == 0, "old folder ownership cannot authorize current account write");
            }
            foreach (var path in new[] { "/x/web-interface/archive/coins", "/x/v2/fav/video/favoured", "/x/v3/fav/folder/created/list-all" })
            {
                active = null;
                Stub(request =>
                {
                    if (request.RequestUri.AbsolutePath == path)
                    {
                        ChangeAccount(active, 99);
                        return new JObject { ["code"] = -101 };
                    }
                    if (request.RequestUri.AbsolutePath.EndsWith("/has/like", StringComparison.Ordinal)) return Ok(0);
                    return Ok(new JObject { ["multiply"] = 0 });
                });
                using (var client = active = new BilibiliClient())
                {
                    await Login(client);
                    if (path.Contains("folder")) await Error(() => client.GetVideoFavoriteFoldersAsync(Aid), "late old-account folder error cannot affect a new account");
                    else await Error(() => client.GetVideoInteractionsAsync(Aid), "late old-account state error cannot affect a new account");
                    Check(client.CurrentUser != null && client.CurrentUser.Mid == 99 && Posts == 0,
                        "ownership is checked before parsing late -101 so new account is preserved");
                }
            }
            active = null;
            Stub(request =>
            {
                var jar = typeof(BilibiliClient).GetField("_cookies", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(active);
                var changed = new HttpResponseMessage { Content = new HttpStringContent("") };
                changed.Headers["Set-Cookie"] = "SESSDATA=renewed-fixture-session; Domain=.bilibili.com; Path=/; Secure";
                jar.GetType().GetMethod("ApplyResponseCookies", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    .Invoke(jar, new object[] { changed, new Uri("https://api.bilibili.com/") });
                return Ok(0);
            });
            using (var client = active = new BilibiliClient())
            {
                await Login(client); await Error(() => client.GetVideoInteractionsAsync(Aid), "same MID with a new login session invalidates old state");
                Check(client.CurrentUser.Mid == 42 && Posts == 0, "session identity check does not sign out the renewed same-user profile");
            }
            Stub(request => Ok());
            using (var client = new BilibiliClient())
            {
                await Login(client);
                using (var cancel = new CancellationTokenSource())
                { cancel.Cancel(); await Error(() => client.TripleVideoAsync(Aid, cancel.Token), "pre-cancelled triple has no side effects"); Check(Posts == 0, "pre-cancelled write no network POST"); }
            }
        }
    }
}
