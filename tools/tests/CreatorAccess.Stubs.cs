// TEST ONLY. Deterministic GET-only transport, no real endpoint or saved account.
// Production cookie storage persistence stubs throw if any account I/O is attempted.
namespace BiliXbox.CreatorAccessTests
{
    using BiliXbox.Services;
    using BiliXbox.VideoDetailTests;
    public static class Tests
    {
        private const long ScreenshotMid = 321173469;
        private static int _checks;
        private static void Check(bool value, string name)
        {
            if (!value) throw new Exception("FAIL: " + name);
            Interlocked.Increment(ref _checks);
        }
        private static Dictionary<string, string> Query(HttpRequestMessage request)
            => request.RequestUri.Query.TrimStart('?').Split('&').Where(value => value.Length > 0)
                .Select(pair => pair.Split(new[] { '=' }, 2))
                .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]));
        private static JObject Ok(JObject data) => new JObject { ["code"] = 0, ["data"] = data };
        private static JObject Nav => JObject.Parse("{\"code\":-101,\"data\":{\"wbi_img\":{\"img_url\":\"https://i0.hdslb.com/bfs/wbi/7cd084941338484aae1ad9425b84077c.png\",\"sub_url\":\"https://i0.hdslb.com/bfs/wbi/4932caff0ff746eab6f01bf08b70ac45.png\"}}}");
        private static JObject Profile(long mid) => Ok(new JObject
        {
            ["mid"] = mid, ["name"] = "哔哩哔哩会员购", ["face"] = "//i0.hdslb.com/avatar.png", ["sign"] = "官方账号 🎮"
        });
        private static JObject Card(long mid) => Ok(new JObject
        {
            ["card"] = new JObject
            {
                ["mid"] = mid.ToString(CultureInfo.InvariantCulture), ["name"] = "哔哩哔哩会员购",
                ["face"] = "http://i0.hdslb.com/avatar.png", ["sign"] = "官方账号 🎮", ["attention"] = 11
            },
            ["follower"] = 12345, ["archive_count"] = 138
        });
        private static JObject EmptyVideos => Ok(new JObject
        { ["media_list"] = new JArray(), ["total_count"] = 0, ["has_more"] = false });
        private static JObject DynamicFeed(long mid) => Ok(new JObject
        {
            ["has_more"] = false, ["offset"] = "", ["items"] = new JArray(new JObject
            {
                ["id_str"] = "123456789", ["type"] = "DYNAMIC_TYPE_WORD",
                ["modules"] = new JObject
                {
                    ["module_author"] = new JObject { ["mid"] = mid, ["name"] = "哔哩哔哩会员购" },
                    ["module_dynamic"] = new JObject { ["desc"] = new JObject { ["text"] = "真实动态内容" } }
                }
            })
        });
        private static Task<HttpResponseMessage> Response(JObject value, int status = 200)
            => Transport.Json(value.ToString(), status);
        private static HttpResponseMessage Bootstrap(int status = 302, bool cookie = true)
        {
            var response = new HttpResponseMessage { StatusCode = status, Content = new HttpStringContent("discarded homepage") };
            if (cookie) response.Headers["Set-Cookie"] = "buvid3=server-issued-only; Domain=.bilibili.com; Path=/; Secure, b_nut=fixture-time; Domain=.bilibili.com; Path=/; Secure";
            return response;
        }
        private static string Cookie(HttpRequestMessage request) => request.Headers.ContainsKey("Cookie") ? request.Headers["Cookie"] : "";
        private static void SpaceRequest(HttpRequestMessage request, long mid, bool videos = false)
        {
            Check(request.Method.Method == "GET" && request.RequestUri.Scheme == "https"
                && request.RequestUri.Host == "api.bilibili.com", "creator browsing is an official read-only HTTPS API request");
            var referer = request.Headers.Referer;
            Check(referer != null && referer.Scheme == "https" && referer.Host == "space.bilibili.com"
                && referer.AbsolutePath.StartsWith("/" + mid.ToString(CultureInfo.InvariantCulture) + "/", StringComparison.Ordinal),
                "request-specific space referer follows selected MID");
            if (videos) Check(referer.AbsolutePath == "/" + mid.ToString(CultureInfo.InvariantCulture) + "/video", "archive request uses selected video page referer");
        }
        private static void SeedCookies(BilibiliClient client, string value)
        {
            var jar = (ProtectedCookieStore)typeof(BilibiliClient).GetField("_cookies",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(client);
            var response = new HttpResponseMessage();
            response.Headers["Set-Cookie"] = value;
            jar.ApplyResponseCookies(response, new Uri("https://www.bilibili.com/"));
        }
        private static async Task ExpectApiError(Func<Task> run, string name)
        {
            try { await run(); }
            catch (BilibiliApiException) { Check(true, name); return; }
            throw new Exception("FAIL: invalid profile accepted: " + name);
        }
        private static async Task ExpectCanceled(Task operation, string name)
        {
            try { await operation; }
            catch (OperationCanceledException) { Check(true, name); return; }
            throw new Exception("FAIL: cancellation lost: " + name);
        }
        private static void Stub(Func<HttpRequestMessage, Task<HttpResponseMessage>> route, bool bootstrapCookie = true)
        {
            Transport.Reset((request, token) =>
            {
                token.ThrowIfCancellationRequested();
                Check(request.Method.Method == "GET", "fixture rejects any publication");
                if (request.RequestUri.Host == "www.bilibili.com") return Task.FromResult(Bootstrap(302, bootstrapCookie));
                if (request.RequestUri.AbsolutePath == "/x/web-interface/nav") return Response(Nav);
                return route(request);
            });
        }
        private static async Task RestrictedProfileFallback()
        {
            foreach (var failure in new[] { -352, -412, 403, 412 })
            {
                var primary = 0;
                var cards = 0;
                Stub(request =>
                {
                    SpaceRequest(request, ScreenshotMid);
                    Check(Query(request)["mid"] == ScreenshotMid.ToString(CultureInfo.InvariantCulture), "profile and fallback retain selected account");
                    Check(Cookie(request).Contains("buvid3=server-issued-only") && Cookie(request).Contains("b_nut=fixture-time"), "302 bootstrap response cookies retained on profile and fallback");
                    Check(!Cookie(request).Contains("SESSDATA="), "anonymous access creates no synthetic login cookie");
                    if (request.RequestUri.AbsolutePath == "/x/space/wbi/acc/info")
                    {
                        primary++;
                        Check(Query(request).ContainsKey("w_rid") && Query(request).ContainsKey("wts"), "primary profile carries real WBI signature");
                        return failure > 0 ? Response(new JObject(), failure) : Response(new JObject { ["code"] = failure });
                    }
                    cards++;
                    Check(request.RequestUri.AbsolutePath == "/x/web-interface/card" && Query(request)["photo"] == "false", "restricted profile has one independent card fallback");
                    return Response(Card(ScreenshotMid));
                });
                using (var client = new BilibiliClient())
                {
                    var profile = await client.GetCreatorProfileAsync(ScreenshotMid);
                    Check(profile.Mid == ScreenshotMid && profile.Name == "哔哩哔哩会员购" && profile.AvatarUrl == "https://i0.hdslb.com/avatar.png"
                        && profile.Signature == "官方账号 🎮", "restricted profile maps verified card identity and normalizes avatar");
                    Check(!string.IsNullOrWhiteSpace(profile.FollowersText) && !string.IsNullOrWhiteSpace(profile.FollowingText)
                        && !string.IsNullOrWhiteSpace(profile.VideoCountText), "fallback card retains follower, following and archive statistics");
                }
                Check(primary == 1 && cards == 1 && Transport.Requests.Count(value => value.Uri.Host == "www.bilibili.com") == 1,
                    "restriction performs one bootstrap and one request per profile route without retry loop");
            }
        }
        private static async Task ProfileValidation()
        {
            var cards = 0;
            Stub(request =>
            {
                if (request.RequestUri.AbsolutePath == "/x/web-interface/card") cards++;
                return Response(Profile(ScreenshotMid + 1));
            });
            using (var client = new BilibiliClient()) await ExpectApiError(() => client.GetCreatorProfileAsync(ScreenshotMid), "primary wrong MID rejected");
            Check(cards == 0, "primary identity mismatch never triggers fallback");

            foreach (var invalid in new[] { Card(ScreenshotMid + 1), Ok(new JObject()), Ok(new JObject { ["card"] = JValue.CreateNull() }),
                Ok(new JObject { ["card"] = new JObject { ["mid"] = ScreenshotMid, ["name"] = "  " } }),
                new JObject { ["code"] = -352 } })
            {
                cards = 0;
                Stub(request =>
                {
                    if (request.RequestUri.AbsolutePath == "/x/space/wbi/acc/info") return Response(new JObject { ["code"] = -352 });
                    cards++;
                    return Response(invalid);
                });
                using (var client = new BilibiliClient()) await ExpectApiError(() => client.GetCreatorProfileAsync(ScreenshotMid), "fallback wrong MID, bad schema or restricted response never becomes success");
                Check(cards == 1, "invalid fallback is not retried");
            }
            foreach (var code in new[] { -404, -101, -1, 429 })
            {
                cards = 0;
                Stub(request =>
                {
                    if (request.RequestUri.AbsolutePath == "/x/web-interface/card") cards++;
                    return code == 429 ? Response(new JObject(), 429) : Response(new JObject { ["code"] = code });
                });
                using (var client = new BilibiliClient()) await ExpectApiError(() => client.GetCreatorProfileAsync(ScreenshotMid), "non-verification error remains explicit");
                Check(cards == 0, "non-verification error does not broaden fallback requests");
            }
        }
        private static async Task ExistingCookiesAndIndependentReferers()
        {
            var bootstrap = 0;
            Transport.Reset((request, token) =>
            {
                if (request.RequestUri.Host == "www.bilibili.com") { bootstrap++; return Task.FromResult(Bootstrap()); }
                if (request.RequestUri.AbsolutePath == "/x/web-interface/nav") return Response(Nav);
                Check(Cookie(request).Contains("buvid3=existing-issued") && Cookie(request).Contains("SESSDATA=account-fixture")
                    && Cookie(request).Contains("bili_jct=csrf-fixture"), "existing anonymous and account cookies remain intact");
                var query = Query(request);
                var media = request.RequestUri.AbsolutePath == "/x/v2/medialist/resource/list";
                var mid = long.Parse(query[media ? "biz_id" : "mid"], CultureInfo.InvariantCulture);
                SpaceRequest(request, mid, media);
                return Response(media ? EmptyVideos : Profile(mid));
            });
            using (var client = new BilibiliClient())
            {
                SeedCookies(client, "buvid3=existing-issued; Domain=.bilibili.com; Path=/; Secure, SESSDATA=account-fixture; Domain=.bilibili.com; Path=/; Secure, bili_jct=csrf-fixture; Domain=.bilibili.com; Path=/; Secure");
                var first = client.GetCreatorProfileAsync(ScreenshotMid);
                var second = client.GetCreatorProfileAsync(ScreenshotMid + 1);
                var videos = client.GetCreatorVideosAsync(ScreenshotMid + 1);
                await Task.WhenAll(first, second, videos);
                Check(first.Result.Mid == ScreenshotMid && second.Result.Mid == ScreenshotMid + 1 && videos.Result.Videos.Count == 0,
                    "two selected MIDs never share a stale referer or profile result");
            }
            Check(bootstrap == 0, "existing genuine buvid skips homepage bootstrap");
        }
        private static async Task CollaborativeArchiveOwners()
        {
            var archives = 0;
            Stub(request =>
            {
                if (request.RequestUri.AbsolutePath != "/x/v2/medialist/resource/list")
                {
                    archives++;
                    return Response(new JObject { ["code"] = -352 });
                }
                SpaceRequest(request, ScreenshotMid, true);
                Check(Query(request)["biz_id"] == ScreenshotMid.ToString(CultureInfo.InvariantCulture),
                    "collaborative archive request remains scoped to selected account");
                var media = new JArray();
                var ownerMids = new[] { ScreenshotMid, 2306780L };
                var ids = new[] { "BV1xx411c7mD", "BV1DXHZ6BEzG" };
                for (var index = 0; index < ownerMids.Length; index++) media.Add(new JObject
                {
                    ["id"] = 170001 + index, ["bv_id"] = ids[index], ["type"] = 2, ["title"] = "合作投稿 " + index,
                    ["duration"] = 97, ["upper"] = new JObject { ["mid"] = ownerMids[index], ["name"] = "原作者 " + index }
                });
                return Response(Ok(new JObject { ["media_list"] = media, ["total_count"] = 2, ["has_more"] = false }));
            });
            using (var client = new BilibiliClient())
            {
                var result = await client.GetCreatorVideosAsync(ScreenshotMid);
                Check(result.Videos.Count == 2 && result.Notice.Length == 0 && !result.HasMore,
                    "mixed primary uploader archive stays valid and retains all collaborative videos");
                Check(result.Videos[0].OwnerMid == ScreenshotMid && result.Videos[1].OwnerMid == 2306780
                    && result.Videos[1].Bvid == "BV1DXHZ6BEzG" && result.Videos[1].Owner == "原作者 1",
                    "collaborative video preserves server primary author MID and name");
                Check(archives == 0, "valid collaboration list never requests separately restricted fallback archive");
            }
            Stub(request =>
            {
                SpaceRequest(request, ScreenshotMid, true);
                if (request.RequestUri.AbsolutePath == "/x/v2/medialist/resource/list") return Response(new JObject { ["code"] = -404 });
                Check(request.RequestUri.AbsolutePath == "/x/space/wbi/arc/search"
                    && Query(request)["mid"] == ScreenshotMid.ToString(CultureInfo.InvariantCulture),
                    "collaborative WBI archive remains scoped to selected account");
                return Response(Ok(new JObject
                {
                    ["list"] = new JObject { ["vlist"] = new JArray(
                        new JObject { ["bvid"] = "BV1DXHZ6BEzG", ["aid"] = 170001, ["mid"] = 2306780,
                            ["title"] = "合作投稿", ["author"] = "原作者" },
                        new JObject { ["bvid"] = "BV1xx411c7mD", ["aid"] = 170002, ["title"] = "未提供主作者MID" }) },
                    ["page"] = new JObject { ["count"] = 2, ["ps"] = 30, ["pn"] = 1 }
                }));
            });
            using (var client = new BilibiliClient())
            {
                var result = await client.GetCreatorVideosAsync(ScreenshotMid);
                Check(result.Videos.Count == 2 && result.Videos[0].OwnerMid == 2306780 && result.Videos[0].Owner == "原作者",
                    "collaborative WBI response retains different primary author instead of rejecting archive");
                Check(result.Videos[1].OwnerMid == ScreenshotMid,
                    "scoped creator identity fills only an omitted primary author MID");
            }
        }
        private static async Task BootstrapSingleFlight(bool success)
        {
            var bootstrap = 0;
            var issued = new TaskCompletionSource<HttpResponseMessage>();
            Transport.Reset((request, token) =>
            {
                if (request.RequestUri.Host == "www.bilibili.com")
                {
                    Interlocked.Increment(ref bootstrap);
                    return issued.Task;
                }
                if (request.RequestUri.AbsolutePath == "/x/web-interface/nav") return Response(Nav);
                Check(success ? Cookie(request).Contains("buvid3=server-issued-only") : Cookie(request).Length == 0,
                    "bootstrap waiters use only genuine returned cookies");
                if (request.RequestUri.AbsolutePath == "/x/polymer/web-dynamic/v1/feed/space")
                {
                    SpaceRequest(request, ScreenshotMid);
                    return Response(DynamicFeed(ScreenshotMid));
                }
                if (request.RequestUri.AbsolutePath == "/x/v2/medialist/resource/list")
                {
                    SpaceRequest(request, ScreenshotMid, true);
                    return Response(EmptyVideos);
                }
                SpaceRequest(request, ScreenshotMid);
                return Response(Profile(ScreenshotMid));
            });
            using (var client = new BilibiliClient())
            {
                var requests = new List<Task>();
                for (var index = 0; index < 6; index++) requests.Add(client.GetCreatorProfileAsync(ScreenshotMid));
                requests.Add(client.GetCreatorVideosAsync(ScreenshotMid));
                var dynamic = client.GetCreatorDynamicsAsync(ScreenshotMid);
                requests.Add(dynamic);
                Check(bootstrap == 1, "concurrent profile, archive and dynamic requests coalesce one pending bootstrap");
                issued.SetResult(Bootstrap(success ? 302 : 412, success));
                await Task.WhenAll(requests);
                Check(dynamic.Result.Items.Single().Content == "真实动态内容", "302 bootstrap or unavailable homepage never prevents usable dynamic feed");
                await client.GetCreatorProfileAsync(ScreenshotMid);
                Check(bootstrap == 1, success ? "redirect cookie prevents repeated bootstrap" : "failed bootstrap has a short cooldown instead of serial retries");
            }
        }
        private static async Task OptionalArchiveStatistics()
        {
            var fallbackRequests = 0;
            Stub(request =>
            {
                if (request.RequestUri.AbsolutePath != "/x/v2/medialist/resource/list")
                {
                    fallbackRequests++;
                    return Response(new JObject { ["code"] = -352 });
                }
                var media = new JArray();
                for (var index = 0; index < 4; index++)
                {
                    var item = new JObject
                    {
                        ["id"] = 170001 + index, ["bv_id"] = "BV" + (170001 + index).ToString("0000000000", CultureInfo.InvariantCulture),
                        ["type"] = 2, ["title"] = "可选统计缺失合作投稿 " + index,
                        ["upper"] = new JObject { ["mid"] = index % 2 == 0 ? ScreenshotMid : 2306780L, ["name"] = "真实原作者" }
                    };
                    if (index == 1) item["cnt_info"] = JValue.CreateNull();
                    if (index == 2) item["cnt_info"] = new JValue(0);
                    if (index == 3) item["cnt_info"] = new JValue("unavailable");
                    media.Add(item);
                }
                return Response(Ok(new JObject { ["media_list"] = media, ["total_count"] = media.Count, ["has_more"] = false }));
            });
            using (var client = new BilibiliClient())
            {
                var result = await client.GetCreatorVideosAsync(ScreenshotMid);
                Check(result.Videos.Count == 4 && result.Notice.Length == 0 && !result.HasMore,
                    "missing, JSON-null and scalar optional cnt_info cannot discard genuine collaborative videos");
                Check(result.Videos[0].OwnerMid == ScreenshotMid && result.Videos[1].OwnerMid == 2306780
                    && result.Videos[2].OwnerMid == ScreenshotMid && result.Videos[3].OwnerMid == 2306780,
                    "malformed optional statistics never change valid cooperative author attribution");
                Check(fallbackRequests == 0, "unavailable optional statistics never cause a restricted archive fallback");
            }
        }
        private static async Task CanceledBootstrapWaiter()
        {
            var bootstrap = 0;
            var issued = new TaskCompletionSource<HttpResponseMessage>();
            Transport.Reset((request, token) =>
            {
                if (request.RequestUri.Host == "www.bilibili.com") { bootstrap++; return issued.Task; }
                if (request.RequestUri.AbsolutePath == "/x/web-interface/nav") return Response(Nav);
                return Response(Profile(ScreenshotMid));
            });
            using (var client = new BilibiliClient())
            using (var cancel = new CancellationTokenSource())
            {
                var active = client.GetCreatorProfileAsync(ScreenshotMid);
                var waiter = client.GetCreatorProfileAsync(ScreenshotMid, cancel.Token);
                cancel.Cancel();
                await ExpectCanceled(waiter, "canceling a bootstrap waiter preserves its own cancellation");
                Check(!active.IsCompleted && bootstrap == 1, "canceling waiter cannot cancel another active bootstrap");
                issued.SetResult(Bootstrap());
                Check((await active).Mid == ScreenshotMid, "remaining bootstrap owner completes normally");
            }
        }
        private static async Task NoCookieAndCancellation()
        {
            Stub(request =>
            {
                Check(Cookie(request).Length == 0, "homepage without cookie never synthesizes buvid or session");
                return Response(Profile(ScreenshotMid));
            }, false);
            using (var client = new BilibiliClient()) Check((await client.GetCreatorProfileAsync(ScreenshotMid)).Mid == ScreenshotMid,
                "missing bootstrap cookie does not block a usable separate profile route");

            Transport.Reset((request, token) => { throw new Exception("pre-canceled access reached network"); });
            using (var client = new BilibiliClient())
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel();
                await ExpectCanceled(client.GetCreatorProfileAsync(ScreenshotMid, cancel.Token), "pre-canceled profile preserves cancellation");
                await ExpectCanceled(client.GetCreatorVideosAsync(ScreenshotMid, 1, cancel.Token), "pre-canceled archive preserves cancellation");
                Check(Transport.Requests.Count == 0, "pre-canceled operations issue no request");
            }
            foreach (var cancelBootstrap in new[] { true, false })
            {
                var pending = new TaskCompletionSource<HttpResponseMessage>();
                var cards = 0;
                Transport.Reset((request, token) =>
                {
                    if (request.RequestUri.AbsolutePath == "/x/web-interface/nav") return Response(Nav);
                    if (request.RequestUri.AbsolutePath == "/x/web-interface/card") cards++;
                    token.Register(() => pending.TrySetCanceled());
                    return pending.Task;
                });
                using (var client = new BilibiliClient())
                using (var cancel = new CancellationTokenSource())
                {
                    if (!cancelBootstrap) SeedCookies(client, "buvid3=existing-issued; Domain=.bilibili.com; Path=/; Secure");
                    var operation = client.GetCreatorProfileAsync(ScreenshotMid, cancel.Token);
                    cancel.Cancel();
                    await ExpectCanceled(operation, cancelBootstrap ? "bootstrap cancellation remains cancellation" : "primary profile cancellation remains cancellation");
                    Check(cards == 0, "cancellation never triggers a profile fallback");
                }
            }
        }
        public static async Task<string> Run()
        {
            await RestrictedProfileFallback();
            await ProfileValidation();
            await ExistingCookiesAndIndependentReferers();
            await CollaborativeArchiveOwners();
            await OptionalArchiveStatistics();
            await BootstrapSingleFlight(true);
            await BootstrapSingleFlight(false);
            await CanceledBootstrapWaiter();
            await NoCookieAndCancellation();
            return "PASS: " + _checks + " production creator access, genuine cookie, referer, fallback, cancellation and single-flight assertions (no live network or account persistence).";
        }
    }
}
