// Test-only transport. No account files are loaded and no real POST is sent.
namespace BiliXbox.CreatorTests
{
    using BiliXbox.Services;
    using BiliXbox.VideoDetailTests;
    public static class Tests
    {
        private static int _checks;
        private static void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); _checks++; }
        private static Dictionary<string, string> Query(HttpRequestMessage request)
            => request.RequestUri.Query.TrimStart('?').Split('&').Select(pair => pair.Split(new[] { '=' }, 2))
                .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]));
        private static JObject Nav => JObject.Parse("{\"code\":-101,\"data\":{\"wbi_img\":{\"img_url\":\"https://i0.hdslb.com/bfs/wbi/7cd084941338484aae1ad9425b84077c.png\",\"sub_url\":\"https://i0.hdslb.com/bfs/wbi/4932caff0ff746eab6f01bf08b70ac45.png\"}}}");
        private static void Stub(Func<HttpRequestMessage, JObject> route)
        {
            Transport.Reset((request, token) =>
            {
                if (request.RequestUri.Host == "www.bilibili.com")
                {
                    var response = new HttpResponseMessage { Content = new HttpStringContent("discarded") };
                    response.Headers["Set-Cookie"] = "buvid3=creator-fixture; Domain=.bilibili.com; Path=/; Secure";
                    return Task.FromResult(response);
                }
                if (request.RequestUri.AbsolutePath == "/x/web-interface/nav") return Transport.Json(Nav.ToString());
                return Transport.Json(route(request).ToString());
            });
        }
        private static JObject Ok(JObject data) => new JObject { ["code"] = 0, ["data"] = data };
        private static JObject Dynamic(string id, long oid = 456, int type = 17) => new JObject
        {
            ["id_str"] = id, ["type"] = "DYNAMIC_TYPE_WORD",
            ["basic"] = new JObject { ["comment_id_str"] = oid.ToString(CultureInfo.InvariantCulture), ["comment_type"] = type },
            ["modules"] = new JObject
            {
                ["module_author"] = new JObject { ["mid"] = 42, ["name"] = "UP 🎮", ["face"] = "//i0.hdslb.com/avatar.jpg", ["pub_ts"] = 1700000000 },
                ["module_dynamic"] = new JObject { ["desc"] = new JObject { ["text"] = "测试[doge]", ["rich_text_nodes"] = new JArray(
                    new JObject { ["type"] = "RICH_TEXT_NODE_TYPE_TEXT", ["text"] = "测试" },
                    new JObject { ["type"] = "RICH_TEXT_NODE_TYPE_EMOJI", ["text"] = "[doge]", ["emoji"] = new JObject { ["icon_url"] = "//i0.hdslb.com/doge.png" } }) } },
                ["module_stat"] = new JObject
                {
                    ["comment"] = new JObject { ["count"] = 2 }, ["like"] = new JObject { ["count"] = 4294967296L },
                    ["forward"] = new JObject { ["count"] = 10 }
                }
            }
        };
        private static JObject Feed(bool more, string offset, params JObject[] items)
            => Ok(new JObject { ["items"] = new JArray(items), ["has_more"] = more, ["offset"] = offset });
        private static JObject Reply(long id) => new JObject
        {
            ["rpid"] = id, ["member"] = new JObject { ["uname"] = "回复用户", ["avatar"] = "//i0.hdslb.com/reply.jpg" },
            ["content"] = new JObject { ["message"] = "动态评论[doge]", ["emote"] = new JObject { ["[doge]"] = new JObject { ["url"] = "//i0.hdslb.com/doge.png" } } },
            ["ctime"] = 1700000000, ["like"] = 8
        };
        private static async Task Error(Func<Task> operation, string name)
        {
            try { await operation(); }
            catch (Exception ex) when (ex is BilibiliApiException || ex is ArgumentException) { Check(true, name); return; }
            throw new Exception("FAIL: invalid operation accepted: " + name);
        }
        public static async Task<string> Run()
        {
            Stub(request =>
            {
                Check(request.Method.Method == "GET" && request.RequestUri.AbsolutePath == "/x/space/wbi/acc/info", "profile uses official signed metadata endpoint");
                Check(Query(request)["mid"] == "42" && Query(request).ContainsKey("w_rid"), "profile binds selected numeric account and signs request");
                Check(request.Headers.ContainsKey("Cookie") && request.Headers["Cookie"].Contains("buvid3=creator-fixture"), "profile bootstraps a genuine anonymous cookie before signed space request");
                Check(request.Headers.Referer != null && request.Headers.Referer.Host == "space.bilibili.com"
                    && request.Headers.Referer.AbsolutePath.StartsWith("/42/", StringComparison.Ordinal), "profile request carries selected space referer");
                return Ok(new JObject { ["mid"] = 42, ["name"] = "UP 🎮", ["face"] = "http://i0.hdslb.com/profile.png", ["sign"] = "签名" });
            });
            using (var client = new BilibiliClient())
            {
                var profile = await client.GetCreatorProfileAsync(42);
                Check(profile.Mid == 42 && profile.Name == "UP 🎮" && profile.AvatarUrl == "https://i0.hdslb.com/profile.png" && profile.Signature == "签名", "profile preserves Unicode and secure avatar");
            }
            Stub(request => Ok(new JObject { ["mid"] = 43, ["name"] = "Wrong" }));
            using (var client = new BilibiliClient()) await Error(() => client.GetCreatorProfileAsync(42), "profile mismatch rejected");

            Stub(request =>
            {
                Check(request.RequestUri.AbsolutePath == "/x/v2/medialist/resource/list" && Query(request)["biz_id"] == "42", "creator videos use exact MID archive, no uploader-name search");
                return Ok(new JObject { ["total_count"] = 1, ["media_list"] = new JArray(new JObject
                {
                    ["id"] = 170001, ["bv_id"] = "BV1xx411c7mD", ["type"] = 2, ["title"] = "投稿", ["duration"] = 97,
                    ["upper"] = new JObject { ["mid"] = 42, ["name"] = "UP 🎮", ["face"] = "//i0.hdslb.com/profile.png" }
                }) });
            });
            using (var client = new BilibiliClient())
            {
                var videos = await client.GetCreatorVideosAsync(42);
                Check(videos.Videos.Count == 1 && videos.Videos[0].OwnerMid == 42 && videos.Videos[0].DurationText == "1:37", "exact creator archive maps real video");
                Check(Transport.Requests.All(item => item.Uri.AbsolutePath.IndexOf("search/type", StringComparison.Ordinal) < 0), "opening creator never silently searches account names");
            }

            var repost = Dynamic("111111111111111111111111111111");
            repost["orig"] = Dynamic("222", 333, 11);
            repost["orig"]["orig"] = Dynamic("444");
            var draw = Dynamic("777", 999, 11);
            draw["modules"]["module_dynamic"]["major"] = new JObject { ["draw"] = new JObject { ["items"] = new JArray(new JObject { ["src"] = "//i0.hdslb.com/draw.jpg" }, new JObject { ["src"] = "javascript:bad" }) } };
            var opus = Dynamic("888", 170001, 1);
            opus["modules"]["module_dynamic"]["desc"] = null;
            opus["modules"]["module_dynamic"]["major"] = new JObject
            {
                ["opus"] = new JObject { ["summary"] = new JObject { ["text"] = "图文摘要" }, ["pics"] = new JArray(new JObject { ["url"] = "http://i0.hdslb.com/opus.jpg" }) },
                ["archive"] = new JObject { ["aid"] = 170001, ["bvid"] = "BV1xx411c7mD", ["title"] = "动态视频", ["cover"] = "//i0.hdslb.com/cover.jpg", ["duration_text"] = "1:37", ["stat"] = new JObject { ["play"] = "1万" } }
            };
            const string nextOffset = "opaque+/=动态";
            Stub(request =>
            {
                Check(request.RequestUri.AbsolutePath == "/x/polymer/web-dynamic/v1/feed/space" && Query(request)["host_mid"] == "42", "public dynamic feed targets selected space");
                return Query(request)["offset"] == "" ? Feed(true, nextOffset, repost, draw, opus, draw) : Feed(false, "", Dynamic("999"));
            });
            DynamicCommentTarget target;
            using (var client = new BilibiliClient())
            {
                var page = await client.GetCreatorDynamicsAsync(42);
                Check(page.Items.Count == 3 && page.HasMore && page.NextOffset == nextOffset, "dynamic IDs deduplicate and server cursor remains opaque");
                var entry = page.Items[0];
                target = entry.CommentTarget;
                Check(entry.Id == "111111111111111111111111111111" && target.Oid == 456 && target.Type == 17, "dynamic ID never substitutes for distinct comment object");
                Check(entry.Original != null && entry.Original.Original == null && entry.Original.CommentTarget.Oid == 333 && entry.Original.CommentTarget.Type == 11, "repost original has separate safe target, bounded depth");
                Check(entry.ContentParts.Count == 2 && entry.ContentParts[1].IsImage && entry.ContentParts[1].AltText == "[doge]", "dynamic emoji retains image and fallback text");
                Check(entry.AuthorMid == 42 && entry.AvatarUrl == "https://i0.hdslb.com/avatar.jpg" && entry.LikeCount == 4294967296L, "dynamic avatar, account, 64-bit count retained");
                Check(page.Items[1].ImageUrls.SequenceEqual(new[] { "https://i0.hdslb.com/draw.jpg" }), "draw images normalized and unsafe schemes rejected");
                Check(page.Items[2].Content == "图文摘要" && page.Items[2].ImageUrls.Count == 1 && page.Items[2].Video.OwnerMid == 42 && page.Items[2].CommentTarget.Type == 1, "opus, archive and server video comment type supported");
                var later = await client.GetCreatorDynamicsAsync(42, page.NextOffset);
                Check(later.Items[0].Id == "999" && !later.HasMore, "next dynamic cursor returns distinct later page");
                Check(Transport.Requests.All(item => item.Method == "GET"), "browsing never posts content");
            }
            foreach (var invalidTarget in new[] { new JObject(), new JObject { ["comment_type"] = 17 },
                new JObject { ["comment_type"] = 17, ["comment_id_str"] = "9223372036854775808" },
                new JObject { ["comment_type"] = 0, ["comment_id_str"] = "456" },
                new JObject { ["comment_type"] = 17, ["comment_id_str"] = "-456" } })
            {
                var invalid = Dynamic("123456"); invalid["basic"] = invalidTarget;
                Stub(request => Feed(false, "", invalid));
                using (var client = new BilibiliClient())
                {
                    var entry = (await client.GetCreatorDynamicsAsync(42)).Items[0];
                    Check(!entry.CommentTarget.IsAvailable, "missing, overflow or invalid target cannot be inferred from dynamic ID");
                    var count = Transport.Requests.Count;
                    await Error(() => client.GetDynamicCommentsAsync(entry.CommentTarget), "invalid target has no comment request");
                    await Error(() => client.PostDynamicCommentAsync(entry.CommentTarget, "禁止误投"), "invalid target cannot post");
                    Check(Transport.Requests.Count == count, "invalid target touches no network");
                }
            }
            // A real native UI run hit JValue child access in a dynamic feed.
            // Explicit JSON null differs from an absent CLR reference. Reposts
            // may omit module_stat, while non-video cards omit major subtypes.
            foreach (var field in new[] { "modules.module_stat", "basic", "modules.module_author", "modules.module_dynamic",
                "modules.module_dynamic.desc", "modules.module_dynamic.major", "modules.module_stat.comment",
                "modules.module_stat.like", "modules.module_stat.forward" })
            {
                foreach (var missing in new JToken[] { JValue.CreateNull(), new JValue(0), new JValue("unavailable") })
                {
                    var entry = Dynamic("123456");
                    var segments = field.Split('.');
                    JToken parent = entry;
                    for (int index = 0; index < segments.Length - 1; index++) parent = parent[segments[index]];
                    parent[segments.Last()] = missing.DeepClone();
                    Stub(request => Feed(false, "", entry));
                    using (var client = new BilibiliClient())
                    {
                        var parsed = (await client.GetCreatorDynamicsAsync(42)).Items.Single();
                        Check(parsed.Id == "123456" && parsed.LikeCount >= 0, "optional object JValue never becomes child access: " + field);
                    }
                }
            }
            var nullRepost = Dynamic("555");
            nullRepost["orig"] = Dynamic("556");
            nullRepost["orig"]["modules"]["module_stat"] = JValue.CreateNull();
            nullRepost["orig"]["modules"]["module_dynamic"]["major"] = JValue.CreateNull();
            nullRepost["orig"]["basic"] = JValue.CreateNull();
            Stub(request => Feed(false, "", nullRepost));
            using (var client = new BilibiliClient())
            {
                var parsed = (await client.GetCreatorDynamicsAsync(42)).Items.Single();
                Check(parsed.Original != null && parsed.Original.Content == "测试[doge]" && !parsed.Original.CommentTarget.IsAvailable,
                    "original repost survives explicit null statistics, major and basic objects");
            }
            var nullMedia = Dynamic("557");
            nullMedia["modules"]["module_dynamic"]["major"] = new JObject
            {
                ["none"] = null, ["archive"] = null, ["draw"] = null,
                ["opus"] = new JObject { ["summary"] = null, ["pics"] = new JArray(JValue.CreateNull(), new JValue("unavailable")) }
            };
            nullMedia["modules"]["module_dynamic"]["desc"]["rich_text_nodes"] = new JArray(JValue.CreateNull(), new JValue(0),
                new JObject { ["type"] = "RICH_TEXT_NODE_TYPE_EMOJI", ["text"] = "[缺失]", ["emoji"] = null });
            Stub(request => Feed(false, "", nullMedia));
            using (var client = new BilibiliClient())
            {
                var parsed = (await client.GetCreatorDynamicsAsync(42)).Items.Single();
                Check(parsed.ContentParts.Count == 1 && parsed.ContentParts[0].Text == "[缺失]" && parsed.ImageUrls.Count == 0,
                    "null card media and malformed optional rich nodes preserve usable content without crashing");
            }
            Stub(request => Feed(true, "", Dynamic("1")));
            using (var client = new BilibiliClient()) await Error(() => client.GetCreatorDynamicsAsync(42), "missing server next offset does not claim complete list");
            var wrongAuthor = Dynamic("1"); wrongAuthor["modules"]["module_author"]["mid"] = 43;
            Stub(request => Feed(false, "", wrongAuthor));
            using (var client = new BilibiliClient()) await Error(() => client.GetCreatorDynamicsAsync(42), "another account's feed rejected");

            Stub(request =>
            {
                var query = Query(request);
                Check(request.RequestUri.AbsolutePath == "/x/v2/reply/wbi/main" && query["oid"] == "456" && query["type"] == "17", "dynamic comments use server target and signed shared parser");
                var cursor = JObject.Parse(query["pagination_str"])["offset"].ToString();
                Check(cursor == "" || cursor == nextOffset, "dynamic reply opaque cursor round-trips");
                return Ok(new JObject
                {
                    ["cursor"] = new JObject { ["all_count"] = 2, ["is_end"] = cursor.Length > 0, ["pagination_reply"] = new JObject { ["next_offset"] = cursor.Length == 0 ? nextOffset : "" } },
                    ["replies"] = new JArray(Reply(cursor.Length == 0 ? 10 : 11))
                });
            });
            using (var client = new BilibiliClient())
            {
                var first = await client.GetDynamicCommentsAsync(target);
                Check(first.Items[0].ContentParts[1].IsImage && first.Items[0].Likes == 8 && first.HasMore, "dynamic comments share proven rich emoji and pagination parser");
                var second = await client.GetDynamicCommentsAsync(target, first.NextCursor);
                Check(second.Items[0].Id == 11 && !second.HasMore, "dynamic comment second page preserves independent cursor");
            }
            Stub(request =>
            {
                var query = Query(request);
                Check(request.RequestUri.AbsolutePath == "/x/v2/reply/reply" && query["type"] == "17" && query["oid"] == "456" && query["root"] == "10", "dynamic nested replies carry independent exact target");
                return Ok(new JObject { ["root"] = new JObject { ["rpid"] = 10 }, ["page"] = new JObject { ["num"] = 1, ["size"] = 20, ["count"] = 1 }, ["replies"] = new JArray(Reply(12)) });
            });
            using (var client = new BilibiliClient()) Check((await client.GetDynamicRepliesAsync(target, 10)).Items[0].Id == 12, "dynamic nested reply parsed");

            Transport.Reset((request, token) => { throw new Exception("anonymous post must not reach transport"); });
            using (var client = new BilibiliClient()) await Error(() => client.PostDynamicCommentAsync(target, "test"), "dynamic post requires app account session");
            Check(Transport.Requests.Count == 0, "anonymous post prevented before network");
            Transport.Reset((request, token) =>
            {
                if (request.RequestUri.AbsolutePath == "/x/web-interface/nav")
                {
                    var login = new HttpResponseMessage { Content = new HttpStringContent("{\"code\":0,\"data\":{\"isLogin\":true,\"mid\":42,\"uname\":\"fixture\"}}") };
                    login.Headers["Set-Cookie"] = "SESSDATA=fixture-only; Domain=.bilibili.com; Path=/; Secure, bili_jct=fixture-csrf; Domain=.bilibili.com; Path=/; Secure";
                    return Task.FromResult(login);
                }
                Check(request.Method.Method == "POST" && request.RequestUri.AbsolutePath == "/x/v2/reply/add", "explicit dynamic publication uses official comment endpoint");
                var form = ((HttpFormUrlEncodedContent)request.Content).Values;
                Check(form["oid"] == "456" && form["type"] == "17" && form["csrf"] == "fixture-csrf" && form["message"] == "用户明确发布", "post exact dynamic target, app CSRF and trimmed Unicode");
                if (form.ContainsKey("root")) Check(form["root"] == "10" && form["parent"] == "12", "nested publication targets server comment IDs");
                return Transport.Json("{\"code\":0,\"data\":{}}");
            });
            using (var client = new BilibiliClient())
            {
                var refresh = typeof(BilibiliClient).GetMethod("RefreshProfileAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                await (Task)refresh.Invoke(client, new object[] { CancellationToken.None });
                await client.PostDynamicCommentAsync(target, "  用户明确发布  ");
                await client.PostDynamicCommentAsync(target, "用户明确发布", 10, 12);
                target.CanPost = false;
                await Error(() => client.PostDynamicCommentAsync(target, "closed"), "server comment forbidden prevents publication");
            }
            return "PASS: " + _checks + " production creator profile, videos, dynamics, comment target and fixture-only publication assertions.";
        }
    }
}
