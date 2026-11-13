// Test-only HTTP and account fixtures; no real credentials, storage or POST.
namespace BiliXbox.AccountLibraryTests
{
    using BiliXbox.Services;
    using BiliXbox.VideoDetailTests;
    public static class Tests
    {
        private static int _checks;
        private static void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); ++_checks; }
        private static Dictionary<string, string> Query(HttpRequestMessage request)
            => request.RequestUri.Query.TrimStart('?').Split('&').Select(pair => pair.Split(new[] { '=' }, 2))
                .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]));
        private static JObject Ok(JObject data) => new JObject { ["code"] = 0, ["data"] = data };
        private static JObject Pgc(JObject data) => new JObject { ["code"] = 0, ["result"] = data };
        private static void Stub(Func<HttpRequestMessage, JObject> route)
        {
            Windows.System.Profile.AnalyticsInfo.VersionInfo.DeviceFamily = "Windows.Desktop";
            Windows.Graphics.Display.Core.HdmiDisplayInformation.Current = null;
            Windows.Graphics.Display.Core.HdmiDisplayInformation.ThrowView = false;
            Windows.Graphics.Display.Core.HdmiDisplayInformation.ThrowModes = false;
            Windows.Media.Protection.ProtectionCapabilities.Query = (contentType, keySystem) => Windows.Media.Protection.ProtectionCapabilityResult.Probably;
            Transport.Reset((request, token) =>
            {
                Check(request.Method.Method == "GET" && request.RequestUri.Scheme == "https" && request.RequestUri.Host == "api.bilibili.com", "library only calls official HTTPS read endpoints");
                if (request.RequestUri.AbsolutePath == "/x/web-interface/nav")
                {
                    var response = new HttpResponseMessage { Content = new HttpStringContent(Ok(new JObject
                    { ["isLogin"] = true, ["mid"] = 42, ["uname"] = "fixture account", ["vip"] = new JObject { ["status"] = 1 } }).ToString()) };
                    response.Headers["Set-Cookie"] = "SESSDATA=fixture-only; Domain=.bilibili.com; Path=/; Secure";
                    return Task.FromResult(response);
                }
                string cookie;
                Check(request.Headers.TryGetValue("Cookie", out cookie) && cookie.Contains("SESSDATA=fixture-only"), "library uses app authenticated session");
                return Transport.Json(route(request).ToString());
            });
        }
        private static async Task Login(BilibiliClient client)
        {
            var refresh = typeof(BilibiliClient).GetMethod("RefreshProfileAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)refresh.Invoke(client, new object[] { CancellationToken.None });
        }
        private static async Task Error(Func<Task> run, string name)
        {
            try { await run(); }
            catch (Exception error) when (error is BilibiliApiException || error is ArgumentException || error is OperationCanceledException)
            { Check(true, name); return; }
            throw new Exception("FAIL: expected rejection: " + name);
        }
        private static JObject HistoryRow(long aid, string business = "archive", long episode = 0) => new JObject
        {
            ["title"] = "历史 🎮 &amp; 视频", ["cover"] = "//i0.hdslb.com/history.jpg", ["author_name"] = "UP", ["author_mid"] = 84,
            ["author_face"] = "http://i0.hdslb.com/face.jpg", ["view_at"] = 1700000000L, ["progress"] = -1, ["duration"] = 97,
            ["history"] = new JObject { ["oid"] = aid, ["business"] = business, ["epid"] = episode, ["cid"] = 999,
                ["bvid"] = business == "archive" ? "BV1xx411c7mD" : "", ["page"] = 2 }
        };
        private static JObject HistoryPage(JArray rows, long max, long viewed = 1699999999, string business = "archive")
            => Ok(new JObject { ["list"] = rows, ["cursor"] = new JObject { ["max"] = max, ["view_at"] = viewed, ["business"] = business, ["ps"] = 30 } });
        private static JObject Folders(params long[] ids) => Ok(new JObject { ["count"] = ids.Length, ["list"] = new JArray(ids.Select(id => new JObject
            { ["id"] = id, ["mid"] = 42, ["title"] = "我的收藏 🎬 " + id, ["media_count"] = 31 })) });
        private static JObject Favorite(long id, bool dead = false) => new JObject { ["id"] = id, ["bvid"] = dead ? "" : "BV1xx411c7mD",
            ["type"] = 2, ["title"] = dead ? "已失效视频" : "我的视频", ["cover"] = "//i0.hdslb.com/fav.jpg", ["attr"] = dead ? 16 : 0,
            ["duration"] = 3723, ["upper"] = new JObject { ["mid"] = 84, ["name"] = "作者" }, ["cnt_info"] = new JObject { ["play"] = 12345 } };
        private static JObject FavoritePage(bool more, params JObject[] items) => Ok(new JObject { ["has_more"] = more,
            ["info"] = new JObject { ["id"] = 10, ["upper"] = new JObject { ["mid"] = 42 }, ["media_count"] = 31 }, ["medias"] = new JArray(items) });
        private static JObject SeasonRow(long id) => new JObject { ["season_id"] = id, ["title"] = "追番 &amp; 影视 🎬",
            ["cover"] = "http://i0.hdslb.com/season.jpg", ["season_type_name"] = "电影", ["is_play"] = 1,
            ["new_ep"] = new JObject { ["id"] = 501, ["index_show"] = "全12集" } };
        private static JObject Episode(long id, long aid = 100) => new JObject { ["id"] = id, ["aid"] = aid, ["cid"] = 200,
            ["bvid"] = "BV1xx411c7mD", ["title"] = "1", ["long_title"] = "分集 &amp; 🎬", ["duration"] = 97000, ["rights"] = null };
        private static JObject Season(params JObject[] episodes) => Pgc(new JObject { ["season_id"] = 11, ["title"] = "片名",
            ["cover"] = "//i0.hdslb.com/season.jpg", ["episodes"] = new JArray(episodes), ["section"] = null });
        private static JObject Track(int id, string codec, string kind = "video") => new JObject { ["id"] = id,
            ["codecs"] = codec, ["bandwidth"] = kind == "audio" ? 100000 : 2000000, ["width"] = kind == "video" && id == 120 ? 3840 : 1920, ["height"] = kind == "video" && id == 120 ? 2160 : 1080,
            ["mimeType"] = kind + "/mp4", ["frameRate"] = "30", ["baseUrl"] = "https://cdn.example.test/" + kind + id + ".m4s",
            ["SegmentBase"] = new JObject { ["Initialization"] = "0-927", ["indexRange"] = "928-1811" } };
        private static JObject Playback() => Pgc(new JObject { ["quality"] = 80, ["format"] = "flv", ["accept_quality"] = new JArray(120, 80),
            ["accept_description"] = new JArray("4K", "1080P"), ["dash"] = new JObject { ["duration"] = 97,
                ["video"] = new JArray(Track(120, "avc1.640032"), Track(80, "avc1.640032")),
                ["audio"] = new JArray(Track(30280, "mp4a.40.2", "audio")) } });
        public static async Task<string> Run()
        {
            Transport.Reset((request, token) => { throw new Exception("Anonymous account reads must stop before transport."); });
            using (var client = new BilibiliClient())
            {
                await Error(() => client.GetHistoryAsync(), "anonymous history denied");
                await Error(() => client.GetFavoriteFoldersAsync(), "anonymous folders denied");
                await Error(() => client.GetFavoriteItemsAsync(10), "anonymous folder items denied");
                await Error(() => client.GetFollowedSeasonsAsync(false), "anonymous follows denied");
                await Error(() => client.GetSeasonDetailAsync(11), "anonymous library episode catalog denied");
                await Error(() => client.GetEpisodePlaybackAsync(501), "anonymous PGC denied");
                Check(Transport.Requests.Count == 0, "no network or placeholder data on anonymous account reads");
            }
            Stub(request =>
            {
                var query = Query(request);
                Check(request.RequestUri.AbsolutePath == "/x/web-interface/history/cursor" && query["ps"] == "30" && query["type"] == "all", "history uses cursor API");
                if (query["max"] == "0") return HistoryPage(new JArray(HistoryRow(100), HistoryRow(200, "pgc", 501)), 4294967299L, 1700000001, "pgc");
                Check(query["max"] == "4294967299" && query["view_at"] == "1700000001" && query["business"] == "pgc", "all three opaque history cursor fields roundtrip without int truncation");
                return HistoryPage(new JArray(), 0, 0, "");
            });
            using (var client = new BilibiliClient())
            {
                await Login(client);
                var first = await client.GetHistoryAsync();
                Check(first.Items.Count == 2 && first.HasMore && first.NextCursor.Length > 0, "short history page does not invent end of list");
                var video = first.Items[0]; var episode = first.Items[1];
                Check(video.Title == "历史 🎮 & 视频" && video.CoverUrl.StartsWith("https:") && video.OwnerAvatarUrl.StartsWith("https:"), "history Unicode, text and secure images");
                Check(video.ProgressSeconds == -1 && video.BadgeText == "已看完" && video.Page == 2 && video.DurationText == "1:37", "history completed marker and part retained");
                Check(episode.IsSeason && episode.EpisodeId == 501 && episode.Aid == 200 && episode.Cid == 999 && episode.IsPlayable, "PGC history preserves true episode metadata");
                var end = await client.GetHistoryAsync(first.NextCursor);
                Check(end.Items.Count == 0 && !end.HasMore && end.NextCursor == "", "empty history response ends cursor pagination");
                await Error(() => client.GetHistoryAsync("{not-json}"), "malformed history cursor rejected");
                await Error(() => client.GetHistoryAsync("{\"max\":0,\"view_at\":0,\"business\":\"x&evil=1\"}"), "history cursor query injection rejected");
            }
            string same = "{\"max\":100,\"view_at\":1700000000,\"business\":\"archive\"}";
            Stub(request => HistoryPage(new JArray(HistoryRow(100)), 100, 1700000000));
            using (var client = new BilibiliClient()) { await Login(client); await Error(() => client.GetHistoryAsync(same), "repeated server cursor fails instead of loop or fake all-loaded"); }
            Stub(request => HistoryPage(new JArray(HistoryRow(100, "live")), 101));
            using (var client = new BilibiliClient())
            {
                await Login(client); var page = await client.GetHistoryAsync();
                Check(page.Items.Count == 0 && page.HasMore && page.Notice.Length > 0, "non-video-only page preserves continuation and explains omitted business");
            }
            Stub(request => Ok(new JObject { ["list"] = new JArray(HistoryRow(100)) }));
            using (var client = new BilibiliClient()) { await Login(client); await Error(() => client.GetHistoryAsync(), "missing history cursor cannot silently cap results"); }
            Stub(request => request.RequestUri.AbsolutePath.Contains("folder/created") ? Folders(10, 10, 11)
                : Query(request)["pn"] == "1" ? FavoritePage(true, Favorite(1), Favorite(2, true)) : FavoritePage(false));
            using (var client = new BilibiliClient())
            {
                await Login(client); var folders = await client.GetFavoriteFoldersAsync();
                Check(folders.Count == 2 && folders[0].Id == 10 && folders[0].Title.Contains("🎬"), "actual own folders deduplicated with Unicode");
                var page = await client.GetFavoriteItemsAsync(10);
                Check(page.Items.Count == 2 && page.HasMore && page.NextPage == 2 && page.Total == 31, "favorites honor server continuation beyond20");
                Check(page.Items[0].IsPlayable && page.Items[0].DurationText == "1:02:03" && page.Items[0].OwnerMid == 84, "favorite video map");
                Check(!page.Items[1].IsPlayable && page.Items[1].UnavailableReason.Length > 0, "dead favorite retained with unavailable state");
                Check(!(await client.GetFavoriteItemsAsync(10, 2)).HasMore, "favorite terminal page");
                await Error(() => client.GetFavoriteItemsAsync(999), "foreign or removed folder never read as own");
                Check(!Transport.Requests.Any(r => r.Uri.AbsolutePath.EndsWith("resource/list") && r.Uri.Query.Contains("media_id=999")), "unowned favorite blocked before contents request");
            }
            Stub(request => request.RequestUri.AbsolutePath.Contains("folder/created") ? Folders(10)
                : Ok(new JObject { ["has_more"] = true, ["medias"] = new JArray(), ["info"] = new JObject { ["id"] = 10 } }));
            using (var client = new BilibiliClient()) { await Login(client); await Error(() => client.GetFavoriteItemsAsync(10), "empty favorites with has_more rejected"); }
            Stub(request =>
            {
                var q = Query(request);
                Check(q["vmid"] == "42" && q["follow_status"] == "0" && q["ps"] == "20", "follows bind exact logged-in account and all follow states");
                return Ok(new JObject { ["list"] = new JArray(SeasonRow(q["type"] == "1" ? 11 : 12)), ["total"] = 21,
                    ["pn"] = int.Parse(q["pn"]), ["ps"] = 20 });
            });
            using (var client = new BilibiliClient())
            {
                await Login(client); var anime = await client.GetFollowedSeasonsAsync(false); var drama = await client.GetFollowedSeasonsAsync(true, 2);
                Check(anime.Items[0].IsSeason && anime.Items[0].EpisodeId == 0 && anime.Items[0].SeasonId == 11 && anime.NextPage == 2, "anime rows open season chooser with real continuation");
                Check(drama.Items[0].SeasonId == 12 && !drama.HasMore && drama.Items[0].Owner == "电影", "drama type2 includes films without mixing anime");
            }
            Stub(request =>
            {
                Check(request.RequestUri.AbsolutePath == "/pgc/view/web/season", "episode catalog uses PGC endpoint");
                var season = Season(Episode(501), Episode(502, 0));
                season["result"]["section"] = new JArray(new JObject { ["episodes"] = new JArray(Episode(501), Episode(503)) });
                return season;
            });
            using (var client = new BilibiliClient())
            {
                await Login(client); var season = await client.GetSeasonDetailAsync(11);
                Check(season.Episodes.Count == 3 && season.Title == "片名", "season catalog includes extra sections and deduplicates episode IDs");
                var ep = season.Episodes[0];
                Check(!ep.IsSeason && ep.EpisodeId == 501 && ep.Cid == 200 && ep.Aid == 100 && ep.IsPlayable && ep.Title == "1 分集 & 🎬" && ep.DurationText == "1:37", "episode metadata has separate PGC identity, true AV/CID and millisecond duration");
                Check(!season.Episodes[1].IsPlayable, "incomplete episode cannot falsely enable comments or playback");
                await client.GetSeasonDetailAsync(0, 501);
                Check(QueryForLast("/pgc/view/web/season")["ep_id"] == "501", "history resolves metadata by episode without guessing season from another ID");
                await Error(() => client.GetSeasonDetailAsync(12), "wrong returned season rejected");
                await Error(() => client.GetSeasonDetailAsync(11, 999), "wrong returned episode rejected");
            }
            Stub(request =>
            {
                Check(request.RequestUri.AbsolutePath == "/pgc/player/web/playurl", "all episode playback stays PGC, never UGC");
                var q = Query(request);
                Check(q["ep_id"] == "501" && q["qn"] == "120" && q["fnval"] == "2000" && q["fourk"] == "1", "PGC requests selected quality with authorized app session");
                return Playback();
            });
            using (var client = new BilibiliClient())
            {
                await Login(client); var playback = await client.GetEpisodePlaybackAsync(501, 120);
                Check(playback.QualityId == 120 && playback.RequestedQualityId == 120 && playback.DashManifest.Contains("video-120"), "PGC real4K track mapped without relabeling");
                Check(playback.DashVideoUrls.Count == 1 && playback.DashAudioUrls.Count == 1, "PGC keeps separate audio/video route identity");
                Check(!Transport.Requests.Any(r => r.Uri.AbsolutePath.Contains("/x/player/")), "PGC never falls back to UGC playback URL");
            }
            Stub(request => Playback());
            Windows.Media.Protection.ProtectionCapabilities.Query = (content, key) =>
                content.Contains("display-res-x=") ? Windows.Media.Protection.ProtectionCapabilityResult.NotSupported
                    : Windows.Media.Protection.ProtectionCapabilityResult.Probably;
            using (var client = new BilibiliClient())
            {
                await Login(client); var playback = await client.GetEpisodePlaybackAsync(501, 120);
                Check(playback.QualityId == 80 && !playback.AvailableQualities.First(item => item.Id == 120).IsVisible,
                    "PGC applies the same display capability filter and hides unavailable4K");
            }
            Stub(request =>
            {
                var response = Playback(); var data = response["result"];
                ((JArray)data["dash"]["video"]).RemoveAt(0);
                ((JArray)data["accept_quality"]).Insert(0, 125);
                ((JArray)data["accept_description"]).Insert(0, "HDR10");
                return response;
            });
            Windows.Media.Protection.ProtectionCapabilities.Query = (content, key) => Windows.Media.Protection.ProtectionCapabilityResult.Maybe;
            using (var client = new BilibiliClient())
            {
                await Login(client); var playback = await client.GetEpisodePlaybackAsync(501);
                Check(playback.QualityId == 80 && playback.AvailableQualities.First(item => item.Id == 120).IsVisible
                    && playback.AvailableQualities.First(item => item.Id == 125).IsVisible
                    && !playback.AvailableQualities.First(item => item.Id == 120).IsAvailable
                    && !playback.AvailableQualities.First(item => item.Id == 125).IsAvailable
                    && playback.AvailableQualities.First(item => item.Id == 120).IsCapabilityPending,
                    "PGC unconfirmed4K/HDR options stay visible but disabled without falsely approving playback");
            }
            Stub(request =>
            {
                var response = Playback(); response["result"]["dash"]["video"][0]["codecs"] = "hvc1.2.4.L153.B0";
                return response;
            });
            Windows.System.Profile.AnalyticsInfo.VersionInfo.DeviceFamily = "Windows.Xbox";
            Windows.Graphics.Display.Core.HdmiDisplayInformation.Current = new Windows.Graphics.Display.Core.HdmiDisplayInformation
            { Modes = new[] { new Windows.Graphics.Display.Core.HdmiDisplayMode
                { ResolutionWidthInRawPixels = 1920, ResolutionHeightInRawPixels = 1080, RefreshRate = 60, IsSdrLuminanceSupported = true } } };
            using (var client = new BilibiliClient())
            {
                await Login(client); var playback = await client.GetEpisodePlaybackAsync(501, 120);
                Check(playback.QualityId == 120 && playback.FourKDisplayVerified
                    && playback.AvailableQualities.First(item => item.Id == 120).IsAvailable
                    && playback.CapabilityDiagnostic.Contains("Probably"),
                    "PGC uses independent combined4K capability even with only1080 currently enumerable modes");
            }
            foreach (string restriction in new[] { "is_preview", "is_drm" })
            {
                Stub(request => { var playback = Playback(); playback["result"][restriction] = 1; return playback; });
                using (var client = new BilibiliClient()) { await Login(client); await Error(() => client.GetEpisodePlaybackAsync(501), "PGC rejects " + restriction + " instead of pretending full authorized playback"); }
            }
            Stub(request => new JObject { ["code"] = -10403, ["message"] = "大会员或地域观看权限不足" });
            using (var client = new BilibiliClient()) { await Login(client); await Error(() => client.GetEpisodePlaybackAsync(501), "platform rights error retained"); }
            Stub(request => Pgc(new JObject { ["code"] = -101, ["message"] = "login required" }));
            using (var client = new BilibiliClient())
            {
                await Login(client); await Error(() => client.GetEpisodePlaybackAsync(501), "nested PGC login rejection honored");
                Check(client.CurrentUser == null && !client.IsLoggedIn, "nested expired session invalidates app account consistently");
            }
            Stub(request => Pgc(new JObject { ["code"] = "changed-schema", ["dash"] = Playback()["result"]["dash"] }));
            using (var client = new BilibiliClient()) { await Login(client); await Error(() => client.GetEpisodePlaybackAsync(501), "malformed nested PGC status cannot be mistaken for success"); }
            Stub(request => Ok(new JObject()));
            using (var client = new BilibiliClient())
            {
                await Login(client); await Error(() => client.GetFavoriteFoldersAsync(), "missing folder schema is not an empty collection");
                await Error(() => client.GetFollowedSeasonsAsync(false), "missing followed-season schema is not an empty collection");
            }
            BilibiliClient switching = null;
            Stub(request => { switching.CurrentUser.Mid = 99; return HistoryPage(new JArray(HistoryRow(100)), 101); });
            using (var client = new BilibiliClient())
            {
                await Login(client); switching = client;
                await Error(() => client.GetHistoryAsync(), "in-flight account change discards former account data");
            }
            foreach (bool logout in new[] { false, true })
            {
                Stub(request => Playback());
                var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                int queries = 0;
                Windows.Media.Protection.ProtectionCapabilities.Query = (content, key) =>
                {
                    if (Interlocked.Increment(ref queries) == 1)
                    {
                        entered.TrySetResult(true);
                        if (!release.Task.Wait(TimeSpan.FromSeconds(5)))
                            throw new InvalidOperationException("capability test gate timed out");
                    }
                    return Windows.Media.Protection.ProtectionCapabilityResult.Probably;
                };
                using (var client = new BilibiliClient())
                {
                    await Login(client);
                    var pending = client.GetEpisodePlaybackAsync(501, 120);
                    try
                    {
                        var ready = await Task.WhenAny(entered.Task, Task.Delay(5000));
                        Check(ready == entered.Task && !pending.IsCompleted,
                            "PGC playback is suspended inside the actual background capability query");
                        if (logout) typeof(BilibiliClient).GetProperty("CurrentUser").SetValue(client, null);
                        else client.CurrentUser.Mid = 99;
                        release.TrySetResult(true);
                        await Error(() => pending, "account changes during capability checks discard former PGC playback: " + logout);
                        Check(logout ? !client.IsLoggedIn : client.CurrentUser.Mid == 99,
                            "late capability completion cannot restore the former app session");
                    }
                    finally { release.TrySetResult(true); }
                }
            }
            Check(Transport.Requests.All(r => r.Method == "GET"), "entire library test performs zero POST");
            return "PASS: " + _checks + " production authenticated library/history/favorites/follows/PGC assertions (synthetic account transport, no live account writes).";
        }
        private static Dictionary<string, string> QueryForLast(string path)
        {
            var uri = Transport.Requests.Last(r => r.Uri.AbsolutePath == path).Uri;
            return uri.Query.TrimStart('?').Split('&').Select(pair => pair.Split(new[] { '=' }, 2))
                .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]));
        }
        public static async Task<string> ValidatePublicSeason(string json)
        {
            // Parse a separately fetched public metadata response through the
            // real service with a synthetic login only. No live account cookie
            // is involved; this validates the parser, not PGC entitlements.
            var response = JObject.Parse(json);
            var result = response["result"] as JObject ?? response["data"] as JObject;
            if ((int?)response["code"] != 0 || result == null) throw new Exception("Public season fixture is not a successful metadata response.");
            long seasonId = (long)result["season_id"];
            Stub(request => response);
            using (var client = new BilibiliClient())
            {
                await Login(client);
                var season = await client.GetSeasonDetailAsync(seasonId);
                Check(season.SeasonId == seasonId && season.Episodes.Count > 0, "public PGC metadata uses real season/episode identity");
                Check(season.Episodes.All(item => item.EpisodeId > 0 && item.SeasonId == seasonId), "public PGC catalog fields parse");
                return "PASS: public PGC season " + seasonId + " parsed " + season.Episodes.Count + " episodes through production service (no real account or playback entitlement test).";
            }
        }
    }
}
