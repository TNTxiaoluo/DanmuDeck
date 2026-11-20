namespace BiliXbox.ViewingApiTests
{
    using BiliXbox.Models;
    using BiliXbox.Services;
    using BiliXbox.VideoDetailTests;
    public static class Tests
    {
        private static int checks;
        private static void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
        private static JObject Ok(JObject data) => new JObject { ["code"] = 0, ["data"] = data };
        private static async Task Login(BilibiliClient api)
        {
            var method = typeof(BilibiliClient).GetMethod("RefreshProfileAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)method.Invoke(api, new object[] { CancellationToken.None });
        }
        private static void Route(Func<HttpRequestMessage,JObject> route)
        {
            Transport.Reset((request, token) => {
                Check(request.Method.Method == "GET", "Reads never submit interactions");
                Check(request.RequestUri.Scheme == "https", "HTTPS only");
                if (request.RequestUri.AbsolutePath == "/x/web-interface/nav") {
                    var response = new HttpResponseMessage { Content = new HttpStringContent(Ok(new JObject {
                        ["isLogin"] = true, ["mid"] = 42, ["uname"] = "fixture", ["wbi_img"] = new JObject { ["img_url"] = "https://i0.hdslb.com/bfs/wbi/7cd084941338484aae1ad9425b84077c.png", ["sub_url"] = "https://i0.hdslb.com/bfs/wbi/4932caff0ff746eab6f01bf08b70ac45.png" } }).ToString()) };
                    response.Headers["Set-Cookie"] = "SESSDATA=fixture-only; Domain=.bilibili.com; Path=/; Secure"; return Task.FromResult(response);
                }
                return Transport.Json(route(request).ToString());
            });
        }
        private static async Task Reject(Func<Task> action)
        {
            bool rejected = false;
            try { await action(); } catch (Exception e) when (e is BilibiliApiException || e is ArgumentException || e is OperationCanceledException) { rejected = true; }
            Check(rejected, "Invalid request must fail");
        }
        public static async Task<string> Run()
        {
            Route(request => {
                Check(request.RequestUri.AbsolutePath == "/x/web-interface/archive/related" && request.RequestUri.Query.Contains("bvid=BV1xx411c7mD"), "Recommendations tied to current video, not home feed");
                return new JObject { ["code"] = 0, ["data"] = new JArray(
                    new JObject { ["bvid"] = "BV1xx411c7mD", ["title"] = "self" },
                    new JObject { ["bvid"] = "BV1ct4y1n7t9", ["title"] = "related", ["pic"] = "https://i0.hdslb.com/r.jpg" },
                    new JObject { ["bvid"] = "BV1ct4y1n7t9", ["title"] = "duplicate" },
                    new JObject { ["bvid"] = "invalid", ["title"] = "invalid" }) };
            });
            using (var api = new BilibiliClient()) {
                var data = await api.GetRelatedVideosAsync("BV1xx411c7mD",CancellationToken.None);
                Check(data.Count == 1 && data[0].Bvid == "BV1ct4y1n7t9", "Related rows deduplicate and exclude current/invalid video");
                await Reject(() => api.GetRelatedVideosAsync("invalid",CancellationToken.None));
            }
            Route(request => new JObject { ["code"] = 0, ["data"] = new JArray() });
            using (var api = new BilibiliClient()) { Check((await api.GetRelatedVideosAsync("BV1xx411c7mD",CancellationToken.None)).Count == 0, "Real empty recommendations remain empty"); }
            Route(request => Ok(new JObject()));
            using (var api = new BilibiliClient()) { await Reject(() => api.GetRelatedVideosAsync("BV1xx411c7mD",CancellationToken.None)); }
            Route(request => { throw new Exception("Anonymous library must not request data"); });
            using (var api = new BilibiliClient()) { await Reject(() => api.GetWatchLaterAsync()); await Reject(() => api.GetFollowedCreatorsAsync()); await Reject(() => api.GetFollowingDynamicsAsync()); }
            Route(request => {
                Check(request.RequestUri.AbsolutePath == "/x/v2/history/toview", "Watch later endpoint");
                string cookie; Check(request.Headers.TryGetValue("Cookie", out cookie) && cookie.Contains("fixture-only"), "Session forwarded");
                return Ok(new JObject { ["count"] = 31, ["list"] = new JArray(Enumerable.Range(1,31).Select(i => new JObject {
                    ["aid"] = i, ["bvid"] = "BV1xx411c7mD", ["cid"] = i + 100, ["title"] = "待看 " + i,
                    ["pic"] = "https://i0.hdslb.com/a.jpg", ["duration"] = 97, ["progress"] = i,
                    ["owner"] = new JObject { ["mid"] = 7, ["name"] = "UP" } })) });
            });
            using (var api = new BilibiliClient()) {
                await Login(api); var first = await api.GetWatchLaterAsync(); var second = await api.GetWatchLaterAsync(2);
                Check(first.Items.Count == 30 && first.HasMore && first.NextPage == 2, "Watch later first page");
                Check(second.Items.Count == 1 && !second.HasMore && second.Items[0].Cid == 131, "Watch later second page");
                Check(second.Items[0].ProgressSeconds == 31, "Watch later keeps actual progress");
            }
            Route(request => {
                Check(request.RequestUri.AbsolutePath == "/x/relation/followings" && request.RequestUri.Query.Contains("vmid=42") && request.RequestUri.Query.Contains("pn=2"), "Following account and page");
                return Ok(new JObject { ["total"] = 101, ["list"] = new JArray(new JObject { ["mid"] = 7, ["uname"] = "UP", ["face"] = "https://i0.hdslb.com/u.jpg" }) });
            });
            using (var api = new BilibiliClient()) { await Login(api); var data = await api.GetFollowedCreatorsAsync(2); Check(data.Users.Count == 1 && data.Users[0].Mid == 7 && data.HasMore, "Following typed page"); }
            Route(request => {
                Check(request.RequestUri.AbsolutePath == "/x/polymer/web-dynamic/v1/feed/all" && request.RequestUri.Query.Contains("offset=next%2Fpage"), "All activity cursor encoded");
                return Ok(new JObject { ["items"] = new JArray(), ["has_more"] = false, ["offset"] = "" });
            });
            using (var api = new BilibiliClient()) { await Login(api); var data = await api.GetFollowingDynamicsAsync("next/page"); Check(data.Items.Count == 0 && !data.HasMore, "Empty followed activity"); }
            Route(request => {
                Check(request.RequestUri.AbsolutePath == "/x/polymer/web-dynamic/v1/portal", "Unread indicators use actual dynamic portal endpoint");
                return Ok(new JObject { ["up_list"] = new JArray(new JObject{["mid"]=7,["has_update"]=true},new JObject{["mid"]=8,["has_update"]=false},new JObject{["mid"]=7,["has_update"]=false},new JObject{["mid"]=0,["has_update"]=true}) });
            });
            using (var api = new BilibiliClient()) {
                await Reject(()=>api.GetDynamicAuthorUpdatesAsync(CancellationToken.None));
                await Login(api);var updates=await api.GetDynamicAuthorUpdatesAsync(CancellationToken.None);
                Check(updates.Count==2&&updates.Single(item=>item.Mid==7).HasUpdate&&!updates.Single(item=>item.Mid==8).HasUpdate,"Unread flags deduplicate per author and ignore invalid mid");
            }
            Route(request=>Ok(new JObject()));
            using(var api=new BilibiliClient()){await Login(api);await Reject(()=>api.GetDynamicAuthorUpdatesAsync(CancellationToken.None));}
            Route(request => new JObject { ["code"] = 0, ["result"] = new JObject { ["total"] = 21, ["list"] = new JArray(new JObject {
                ["season_id"] = 789, ["title"] = "正版番剧", ["cover"] = "https://i0.hdslb.com/c.jpg", ["index_show"] = "全12话" }) } });
            using (var api = new BilibiliClient()) {
                var data = await api.GetSeasonCatalogAsync(false,1,CancellationToken.None); var season = data.Videos[0] as AccountLibraryItem;
                Check(season != null && season.IsSeason && season.SeasonId == 789 && data.HasMore, "Catalog contains seasons, not UGC videos");
            }
            Route(request => {
                if (request.RequestUri.AbsolutePath == "/x/space/bangumi/follow/list")
                    return Ok(new JObject { ["total"] = 2, ["list"] = new JArray(
                        new JObject { ["season_id"] = 8, ["title"] = "追番" },
                        new JObject { ["season_id"] = 789, ["title"] = "已追番剧" }) });
                if (request.RequestUri.AbsolutePath == "/pgc/web/timeline/v2")
                    return Ok(new JObject { ["latest"] = new JArray(
                        new JObject { ["season_id"] = 10, ["published"] = 1, ["title"] = "近期更新", ["pub_ts"] = 300 },
                        new JObject { ["season_id"] = 11, ["published"] = 1, ["title"] = "新番首播", ["pub_index"] = "第1话", ["pub_ts"] = 200 },
                        new JObject { ["season_id"] = 12, ["published"] = 0, ["title"] = "预告", ["pub_ts"] = 400 },
                        new JObject { ["season_id"] = 8, ["published"] = 1, ["title"] = "追番重复", ["pub_ts"] = 100 }) });
                Check(request.RequestUri.Query.Contains("order=5&sort=0"), "Anime index is requested in premiere order");
                return new JObject { ["code"] = 0, ["result"] = new JObject { ["total"] = 21, ["list"] = new JArray(
                    new JObject { ["season_id"] = 9, ["title"] = "公开" },
                    new JObject { ["season_id"] = 789, ["title"] = "重复" }) } };
            });
            using (var api = new BilibiliClient()) {
                await Login(api);
                var data = await api.GetSeasonCatalogAsync(false, 1, CancellationToken.None);
                Check(data.Videos.Cast<AccountLibraryItem>().Select(item => item.SeasonId).SequenceEqual(new long[] { 8, 789, 11, 10, 9 })
                    && data.Videos[0].Owner.StartsWith("已追番") && data.HasMore,
                    "Followed anime leads, then recent premieres and updates, then the catalog. Exclude unaired and duplicate entries.");
            }
            Route(request => {
                if (request.RequestUri.Host.EndsWith("hdslb.com")) {
                    string cookie; Check(!request.Headers.TryGetValue("Cookie", out cookie) || !cookie.Contains("fixture-only"), "No account cookie on subtitle CDN");
                    return new JObject { ["body"] = new JArray(new JObject { ["from"] = 1, ["to"] = 2, ["content"] = "hello" },
                        new JObject { ["from"] = -1, ["to"] = 4, ["content"] = "invalid" }, new JObject { ["from"] = 5, ["to"] = 4, ["content"] = "invalid" }) };
                }
                Check(request.RequestUri.AbsolutePath == "/x/player/wbi/v2" && request.RequestUri.Query.Contains("aid=11") && request.RequestUri.Query.Contains("cid=22") && request.RequestUri.Query.Contains("w_rid="), "Signed subtitle request tied to part");
                return Ok(new JObject { ["subtitle"] = new JObject { ["subtitles"] = new JArray(new JObject { ["lan_doc"] = "中文", ["lan"] = "zh-CN", ["subtitle_url"] = "//aisubtitle.hdslb.com/a.json" },
                    new JObject { ["lan_doc"] = "bad", ["subtitle_url"] = "https://attacker.invalid/a.json" }) } });
            });
            using (var api = new BilibiliClient()) {
                await Login(api); var tracks = await api.GetSubtitlesAsync(new VideoDetail { Aid = 11 },22,CancellationToken.None);
                Check(tracks.Count == 1 && tracks[0].Language == "zh-CN", "Only real trusted subtitle tracks");
                var cues = await api.GetSubtitleCuesAsync(tracks[0],CancellationToken.None);
                Check(cues.Count == 1 && cues[0].Text == "hello", "Invalid subtitle timestamps discarded");
                await Reject(() => api.GetSubtitleCuesAsync(new SubtitleTrack { Url = "http://aisubtitle.hdslb.com/a" },CancellationToken.None));
            }
            Route(request => Ok(new JObject()));
            using (var api = new BilibiliClient()) {
                await Login(api);
                var cookies = (ProtectedCookieStore)typeof(BilibiliClient).GetField("_cookies", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(api);
                var cookieResponse = new HttpResponseMessage(); cookieResponse.Headers["Set-Cookie"] = "bili_jct=fixture-csrf; Domain=.bilibili.com; Path=/; Secure";
                cookies.ApplyResponseCookies(cookieResponse, new Uri("https://api.bilibili.com/"));
                int posts = 0;
                Transport.Reset((request, token) => {
                    Check(request.Method.Method == "POST" && request.RequestUri.AbsolutePath == "/x/v2/history/toview/add", "Explicit add uses official endpoint");
                    var form = request.Content as HttpFormUrlEncodedContent;
                    Check(form != null && form.Values["aid"] == "123" && form.Values["csrf"] == "fixture-csrf", "Add binds video and CSRF");
                    posts++; return Transport.Json("{\"code\":0}");
                });
                await api.AddWatchLaterAsync(123,CancellationToken.None);
                Check(posts == 1, "Add is not automatically repeated");
                var pending = new TaskCompletionSource<HttpResponseMessage>();
                Transport.Reset((request,token) => pending.Task);
                var writing = api.AddWatchLaterAsync(123,CancellationToken.None);
                var epoch = typeof(BilibiliClient).GetField("_loginSessionEpoch", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                epoch.SetValue(api,(long)epoch.GetValue(api) + 1);
                pending.SetResult(new HttpResponseMessage { Content = new HttpStringContent("{\"code\":0}") });
                await Reject(() => writing);
            }
            return "PASS: " + checks + " viewing API assertions (fixtures only; watch later, followings, activity, PGC and subtitles).";
        }
    }
}
