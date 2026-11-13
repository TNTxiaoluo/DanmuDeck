// TEST ONLY. Production recommendation state and API mapping, synthetic account cookies.
// The optional live probe uses anonymous GET requests and never reads account files.
namespace BiliXbox.RecommendationTests
{
    using BiliXbox.Services;
    using BiliXbox.VideoDetailTests;
    public static class Tests
    {
        private static int _passed;
        private static void Check(bool value, string name)
        { if (!value) throw new Exception("FAIL: " + name); _passed++; }
        private static Dictionary<string, string> Query(HttpRequestMessage request)
            => request.RequestUri.Query.TrimStart('?').Split('&').Select(pair => pair.Split(new[] { '=' }, 2))
                .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]));
        private static JObject Nav(long mid) => new JObject { ["code"] = mid > 0 ? 0 : -101,
            ["data"] = new JObject { ["wbi_img"] = new JObject {
                ["img_url"] = "https://i0.hdslb.com/bfs/wbi/7cd084941338484aae1ad9425b84077c.png",
                ["sub_url"] = "https://i0.hdslb.com/bfs/wbi/4932caff0ff746eab6f01bf08b70ac45.png" } } };
        private static JObject Video(int n, string kind = "av") => new JObject
        {
            ["id"] = 117400000000000L + n, ["bvid"] = "BV" + n.ToString("0000000000"),
            ["title"] = "推荐 &amp; Xbox " + n, ["goto"] = kind, ["duration"] = 97,
            ["pic"] = "//i1.hdslb.com/bfs/archive/test.jpg", ["owner"] = new JObject
                { ["name"] = "UP " + n, ["face"] = "//i1.hdslb.com/bfs/face/test.jpg" },
            ["stat"] = new JObject { ["view"] = 12345 }
        };
        private static JObject Envelope(long mid, params JObject[] items) => new JObject
            { ["code"] = 0, ["data"] = new JObject { ["mid"] = mid, ["item"] = new JArray(items) } };
        private static void Stub(Func<HttpRequestMessage, JObject> respond, long mid = 0)
        {
            Transport.Reset((request, token) =>
            {
                Check(request.Method.Method == "GET" && request.RequestUri.Scheme == "https"
                    && request.RequestUri.Host == "api.bilibili.com", "recommendations only use Bilibili HTTPS GET");
                if (request.RequestUri.AbsolutePath == "/x/web-interface/nav") return Transport.Json(Nav(mid).ToString());
                return Transport.Json(respond(request).ToString());
            });
        }
        private static void Login(BilibiliClient client, long mid)
        {
            var profile = new UserProfile { Mid = mid, Name = "test account" };
            typeof(BilibiliClient).GetProperty("CurrentUser").SetValue(client, profile);
            var jar = (ProtectedCookieStore)typeof(BilibiliClient).GetField("_cookies",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(client);
            var response = new HttpResponseMessage();
            response.Headers["Set-Cookie"] = "SESSDATA=synthetic-only; Domain=.bilibili.com; Path=/; Secure";
            jar.ApplyResponseCookies(response, new Uri("https://passport.bilibili.com/"));
        }
        public static async Task<string> Run()
        {
            _passed = 0;
            var calls = 0;
            Stub(request =>
            {
                Check(request.RequestUri.AbsolutePath == "/x/web-interface/wbi/index/top/feed/rcmd", "homepage WBI feed rather than fixed popular page");
                var query = Query(request);
                Check(query.ContainsKey("w_rid") && query.ContainsKey("wts"), "normal website recommendation signature");
                Check(query["fresh_idx"] == calls.ToString() && query["fresh_idx_1h"] == calls.ToString(), "refresh indices advance only for committed batches");
                Check(query["fresh_type"] == (calls == 0 ? "0" : calls == 2 ? "5" : "4"), "initial, scroll, refresh use website semantics");
                Check(calls == 0 ? query["last_showlist"] == "" : query["last_showlist"].Contains("av_n_117400000000001"), "server receives real previously displayed archive IDs");
                var n = calls++ * 3 + 1;
                return Envelope(0, Video(n), Video(n), Video(n + 1), Video(n + 2, "live"));
            });
            using (var client = new BilibiliClient())
            {
                var first = await client.GetRecommendationsAsync();
                Check(first.Videos.Count == 2 && first.HasMore && !first.IsPersonalized && !first.IsPopularFallback, "deduplicate archives and exclude nonvideo cards");
                var video = first.Videos[0];
                Check(video.Aid == 117400000000001L && video.Title == "推荐 & Xbox 1"
                    && video.DurationText == "1:37" && video.OwnerAvatarUrl.StartsWith("https://")
                    && video.CoverUrl.StartsWith("https://"), "recommendation IDs, duration, text and UP avatars map correctly");
                Check(ReferenceEquals(first, await client.GetRecommendationsAsync()) && calls == 1, "returning to first page uses original cached items");
                await client.GetRecommendationsAsync(2);
                Check(ReferenceEquals(first, await client.GetRecommendationsAsync()) && calls == 2, "previous page never advances the server cursor");
                var refreshed = await client.GetRecommendationsAsync(1, true);
                Check(!refreshed.Videos.Select(item => item.Bvid).Intersect(first.Videos.Select(item => item.Bvid)).Any(), "refresh fetches different server batch");
                await client.GetRecommendationsAsync(2);
                Check(calls == 4, "refresh invalidates old forward-page cache");
                try { await client.GetRecommendationsAsync(3, true); Check(false, "invalid refresh rejected"); }
                catch (ArgumentException) { Check(calls == 4, "invalid refresh never requests data"); }
            }

            foreach (var serverMid in new[] { 42L, 0L, 99L })
            {
                Stub(request => Envelope(serverMid, Video(1)), 42);
                using (var client = new BilibiliClient())
                {
                    Login(client, 42);
                    var result = await client.GetRecommendationsAsync();
                    Check(result.IsPersonalized == (serverMid == 42), "only server-confirmed current account is personalized");
                    Check(serverMid == 42 ? result.Notice == "" : result.Notice.Contains("未识别"), "unrecognized sessions disclose ordinary recommendations");
                }
            }
            calls = 0;
            Stub(request =>
            {
                if (request.RequestUri.AbsolutePath.EndsWith("/feed/rcmd", StringComparison.Ordinal))
                    return new JObject { ["code"] = -352 };
                Check(request.RequestUri.AbsolutePath == "/x/web-interface/popular", "restricted recommendation falls back only to real popular API");
                var page = int.Parse(Query(request)["pn"]);
                Check(page == ++calls, "fallback refresh advances real popular page rather than shuffling");
                return new JObject { ["code"] = 0, ["data"] = new JObject {
                    ["list"] = new JArray(Enumerable.Range(page * 20, 20).Select(n => { var item = Video(n); item["aid"] = item["id"]; return item; })) } };
            }, 42);
            using (var client = new BilibiliClient())
            {
                Login(client, 42);
                var first = await client.GetRecommendationsAsync();
                var next = await client.GetRecommendationsAsync(1, true);
                Check(first.IsPopularFallback && !first.IsPersonalized && first.Notice.Contains("非个性化"), "fallback is never presented as personalized");
                Check(next.Videos.Count == 20 && !next.Videos.Select(item => item.Bvid).Intersect(first.Videos.Select(item => item.Bvid)).Any(), "fallback refresh displays another actual page");
            }

            Stub(request => Envelope(0, Video(1)));
            using (var client = new BilibiliClient())
            {
                try { await client.GetRecommendationsAsync(2); Check(false, "skip-page rejected"); }
                catch (BilibiliApiException) { Check(Transport.Requests.Count == 0, "missing cursor cannot silently skip feed pages"); }
                var cts = new CancellationTokenSource(); cts.Cancel();
                try { await client.GetRecommendationsAsync(ct: cts.Token); Check(false, "canceled request rejected"); }
                catch (OperationCanceledException) { Check(Transport.Requests.Count == 0, "cancellation performs no network or cursor mutation"); }
            }
            // A login change cannot reuse anonymous or another account's cached first page.
            calls = 0;
            Stub(request => { calls++; return Envelope(42, Video(calls)); }, 42);
            using (var client = new BilibiliClient())
            {
                var anonymous = await client.GetRecommendationsAsync();
                Login(client, 42);
                var signedIn = await client.GetRecommendationsAsync();
                Check(calls == 2 && signedIn.IsPersonalized && !ReferenceEquals(anonymous, signedIn), "login invalidates recommendation cache automatically");
            }

            var inFlight = new CancellationTokenSource();
            calls = 0;
            Stub(request =>
            {
                Check(Query(request)["fresh_idx"] == "0", "canceled in-flight batch does not consume refresh index");
                if (calls++ == 0) inFlight.Cancel();
                return Envelope(0, Video(1));
            });
            using (var client = new BilibiliClient())
            {
                try { await client.GetRecommendationsAsync(ct: inFlight.Token); Check(false, "in-flight cancellation propagated"); }
                catch (OperationCanceledException) { Check(true, "in-flight cancellation propagated"); }
                var retry = await client.GetRecommendationsAsync();
                Check(calls == 2 && retry.Videos.Count == 1, "canceled batch neither caches items nor marks video as seen");
            }

            var failRefresh = false;
            Stub(request => failRefresh ? new JObject { ["code"] = -352 } : Envelope(0, Video(1)));
            using (var client = new BilibiliClient())
            {
                var first = await client.GetRecommendationsAsync();
                failRefresh = true;
                try { await client.GetRecommendationsAsync(1, true); Check(false, "all transports failing propagates error"); }
                catch (BilibiliApiException) { Check(true, "all transports failing propagates error"); }
                var before = Transport.Requests.Count;
                Check(ReferenceEquals(first, await client.GetRecommendationsAsync())
                    && before == Transport.Requests.Count, "failed refresh preserves previous page cache");
            }

            calls = 0;
            Stub(request => Envelope(0, Video(1), Video(++calls + 1)));
            using (var client = new BilibiliClient())
            {
                await client.GetRecommendationsAsync();
                var next = await client.GetRecommendationsAsync(2);
                Check(next.Videos.Count == 1 && next.Videos[0].Bvid == "BV0000000003", "cross-batch duplicates are removed without discarding genuinely new server items");
            }

            calls = 0;
            BilibiliClient changingClient = null;
            Stub(request =>
            {
                if (calls++ == 0) Login(changingClient, 99);
                return Envelope(42, Video(1));
            }, 42);
            using (var client = new BilibiliClient())
            {
                changingClient = client;
                Login(client, 42);
                try { await client.GetRecommendationsAsync(); Check(false, "account changed during response rejected"); }
                catch (BilibiliApiException ex) { Check(ex.Code == -101, "account changed during response rejected"); }
            }
            return "PASS: " + _passed + " production recommendation checks. Live account personalization and Xbox UI need device validation.";
        }

        public static async Task<string> RunLive()
        {
            using (var handler = new System.Net.Http.HttpClientHandler
            { UseCookies = false, AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli })
            using (var http = new System.Net.Http.HttpClient(handler))
            {
                Transport.Reset(async (request, token) =>
                {
                    if (request.Method.Method != "GET" || request.RequestUri.Scheme != "https"
                        || request.RequestUri.Host != "api.bilibili.com")
                        throw new InvalidOperationException("The anonymous recommendation probe permits only Bilibili API GET requests.");
                    using (var outgoing = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, request.RequestUri))
                    {
                        outgoing.Headers.TryAddWithoutValidation("User-Agent", BilibiliClient.UserAgent);
                        outgoing.Headers.Referrer = new Uri(BilibiliClient.Referer);
                        foreach (var header in request.Headers) outgoing.Headers.TryAddWithoutValidation(header.Key, header.Value);
                        using (var response = await http.SendAsync(outgoing, token))
                        {
                            var incoming = new HttpResponseMessage { StatusCode = (int)response.StatusCode,
                                Content = new HttpStringContent(await response.Content.ReadAsStringAsync()) };
                            IEnumerable<string> cookies;
                            if (response.Headers.TryGetValues("Set-Cookie", out cookies)) incoming.Headers["Set-Cookie"] = string.Join(",", cookies);
                            return incoming;
                        }
                    }
                });
                using (var client = new BilibiliClient())
                {
                    var first = await client.GetRecommendationsAsync();
                    var second = await client.GetRecommendationsAsync(2);
                    var refresh = await client.GetRecommendationsAsync(1, true);
                    var countBeforeBack = Transport.Requests.Count;
                    var back = await client.GetRecommendationsAsync();
                    return new JObject
                    {
                        ["Anonymous"] = true, ["FirstCount"] = first.Videos.Count, ["SecondCount"] = second.Videos.Count,
                        ["RefreshedCount"] = refresh.Videos.Count,
                        ["UniqueAcrossBatches"] = first.Videos.Concat(second.Videos).Concat(refresh.Videos).Select(item => item.Bvid).Distinct().Count(),
                        ["UsesPopularFallback"] = first.IsPopularFallback || second.IsPopularFallback || refresh.IsPopularFallback,
                        ["BackUsesCache"] = ReferenceEquals(back, refresh) && countBeforeBack == Transport.Requests.Count,
                        ["Notice"] = refresh.Notice,
                        ["ApiPaths"] = new JArray(Transport.Requests.Select(item => item.Uri.AbsolutePath).Distinct())
                    }.ToString();
                }
            }
        }
    }
}
