// TEST ONLY: real production comment service, signer and jar; projected transport.
namespace BiliXbox.CommentTests
{
    using BiliXbox.Services;
    using BiliXbox.VideoDetailTests;
    public static class Tests
    {
        private static int _passed;
        private static void Check(bool value, string name)
        {
            if (!value) throw new Exception("FAIL: " + name);
            _passed++;
        }
        private static JObject Nav => new JObject { ["code"] = -101, ["data"] = new JObject
        {
            ["wbi_img"] = new JObject
            {
                ["img_url"] = "https://i0.hdslb.com/bfs/wbi/7cd084941338484aae1ad9425b84077c.png",
                ["sub_url"] = "https://i0.hdslb.com/bfs/wbi/4932caff0ff746eab6f01bf08b70ac45.png"
            }
        }};
        private static JObject Reply(long id, long children = 0) => new JObject
        {
            ["rpid"] = id, ["rcount"] = children, ["member"] = new JObject
            { ["uname"] = "评论用户" + id, ["avatar"] = "//i0.hdslb.com/avatar.jpg" },
            ["content"] = new JObject { ["message"] = "中文评论 🎮 & 日本語 " + id },
            ["ctime"] = 1700000000, ["like"] = 25
        };
        private static JObject Page(long count, bool ended, string next, params JObject[] replies)
            => new JObject { ["code"] = 0, ["data"] = new JObject
            {
                ["cursor"] = new JObject
                {
                    ["all_count"] = count, ["is_end"] = ended,
                    ["pagination_reply"] = new JObject { ["next_offset"] = next }
                },
                ["replies"] = new JArray(replies)
            }};
        private static Dictionary<string, string> Query(HttpRequestMessage request)
            => request.RequestUri.Query.TrimStart('?').Split('&').Select(pair => pair.Split(new[] { '=' }, 2))
                .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]));
        private static void Stub(Func<HttpRequestMessage, JObject> result, int status = 200)
        {
            Transport.Reset((request, token) =>
            {
                Check(request.Method.Method == "GET", "comment loading is read-only");
                if (request.RequestUri.Host == "www.bilibili.com")
                {
                    var bootstrap = new HttpResponseMessage { Content = new HttpStringContent("discarded website") };
                    bootstrap.Headers["Set-Cookie"] = "buvid3=server-fixture; Domain=.bilibili.com; Path=/; Secure";
                    return Task.FromResult(bootstrap);
                }
                if (request.RequestUri.AbsolutePath == "/x/web-interface/nav") return Transport.Json(Nav.ToString());
                Check(request.RequestUri.Host == "api.bilibili.com"
                    && request.RequestUri.AbsolutePath == "/x/v2/reply/wbi/main", "uses current signed reply endpoint");
                var query = Query(request);
                Check(query["type"] == "1" && query["oid"] == "117449202600855"
                    && query["mode"] == "3" && query["plat"] == "1", "correct video archive and website mode");
                Check(query.ContainsKey("pagination_str") != query.ContainsKey("next")
                    && !query.ContainsKey("pn"), "one explicit server cursor format is signed, never a reconstructed local page");
                var canonical = string.Join("&", request.RequestUri.Query.TrimStart('?').Split('&')
                    .Where(pair => !pair.StartsWith("w_rid=", StringComparison.Ordinal)));
                string digest;
                using (var md5 = System.Security.Cryptography.MD5.Create())
                    digest = BitConverter.ToString(md5.ComputeHash(Encoding.UTF8.GetBytes(
                        canonical + "ea1db124af3c7062474693fa704f4ff8"))).Replace("-", "").ToLowerInvariant();
                Check(query["w_rid"] == digest, "wire signature includes exact JSON pagination");
                Check(request.Headers.ContainsKey("Cookie") && request.Headers["Cookie"].Contains("buvid3=server-fixture"),
                    "uses server-issued website cookie, no invented fingerprint");
                return Transport.Json(result(request).ToString(), status);
            });
        }
        private static async Task ExpectError(JObject response, int code = -1, int status = 200)
        {
            Stub(request => response, status);
            using (var client = new BilibiliClient())
            {
                try { await client.GetCommentsAsync(117449202600855L); }
                catch (BilibiliApiException ex)
                {
                    Check(ex.Code == code, "unavailable comments preserve diagnostic, never become zero");
                    Check(Transport.Requests.Count(item => item.Uri.AbsolutePath == "/x/v2/reply/wbi/main") == 1,
                        "failure does not retry or fall back to empty legacy route");
                    return;
                }
                throw new Exception("FAIL: unsuccessful response presented as empty comments");
            }
        }
        public static async Task<string> Run()
        {
            _passed = 0;
            const string opaque = "{\"type\":1,\"session_id\":\"opaque+/=\",\"data\":{\"rpid\":9876543210}}";
            var first = Page(25, false, opaque, Reply(1), Reply(4), Reply(4));
            var richRoot = (JObject)first["data"]["replies"][0];
            richRoot["rcount"] = 3;
            richRoot["like"] = 4294967296L;
            richRoot["content"] = new JObject
            {
                ["message"] = "中文🎮 [打call][打call] & [失效] [未知] 日本語",
                ["emote"] = new JObject
                {
                    ["[打call]"] = new JObject { ["url"] = "//i0.hdslb.com/bfs/emote/call.png" },
                    ["[失效]"] = new JObject { ["url"] = "javascript:alert(1)" }
                }
            };
            var preview = Reply(101);
            preview["content"] = new JObject
            {
                ["message"] = "回复[doge]",
                ["emote"] = new JObject { ["[doge]"] = new JObject { ["url"] = "http://i0.hdslb.com/doge.png" } }
            };
            // A nested preview must not recursively render an unbounded thread.
            preview["replies"] = new JArray(Reply(201));
            richRoot["replies"] = new JArray(preview, preview.DeepClone());
            first["data"]["top"] = new JObject { ["admin"] = richRoot.DeepClone(), ["upper"] = Reply(2), ["vote"] = null };
            first["data"]["top_replies"] = new JArray(Reply(2));
            first["data"]["hots"] = new JArray(Reply(3));
            Stub(request =>
            {
                var pagination = JObject.Parse(Query(request)["pagination_str"]);
                if ((string)pagination["offset"] == "") return first;
                Check((string)pagination["offset"] == opaque, "next_offset round-trips unchanged with escaped JSON");
                Check(!Query(request).ContainsKey("seek_rpid"), "subsequent page is not reset to first-comment seek");
                var second = Page(25, true, "", Reply(5), Reply(6));
                second["data"]["top"] = new JObject { ["upper"] = Reply(2) };
                second["data"]["hots"] = new JArray(Reply(3));
                return second;
            });
            using (var client = new BilibiliClient())
            {
                var result = await client.GetCommentsAsync(117449202600855L);
                Check(result.Items.Select(item => item.Id).SequenceEqual(new long[] { 1, 2, 3, 4 }),
                    "pins, hots and root replies mapped once in stable order");
                Check(result.TotalKnown && result.Total == 25 && result.HasMore && result.NextCursor == opaque,
                    "authoritative cursor count and actual next_offset retained");
                Check(result.Items[0].Content == "中文🎮 [打call][打call] & [失效] [未知] 日本語"
                    && result.Items[0].AvatarUrl == "https://i0.hdslb.com/avatar.jpg"
                    && result.Items[0].Likes == 4294967296L, "Unicode, secure avatar and 64-bit likes survive");
                var rich = result.Items[0];
                Check(rich.ContentParts.Count(part => part.IsImage) == 2
                    && rich.ContentParts.Where(part => part.IsImage).All(part => part.AltText == "[打call]"
                        && part.ImageUrl == "https://i0.hdslb.com/bfs/emote/call.png"),
                    "repeated Bilibili emoji become secure images with original text fallback");
                Check(string.Concat(rich.ContentParts.Select(part => part.IsImage ? part.AltText : part.Text)) == rich.Content,
                    "mixed Unicode, unknown and invalid emoji tokens retain exact original content");
                Check(rich.ReplyCount == 3 && rich.Replies.Count == 1 && rich.Replies[0].Id == 101
                    && rich.Replies[0].Replies.Count == 0 && rich.Replies[0].ContentParts[1].ImageUrl == "https://i0.hdslb.com/doge.png",
                    "unique child previews include emoji and stop after one level");
                var second = await client.GetCommentsAsync(117449202600855L, result.NextCursor);
                Check(second.Items.Select(item => item.Id).SequenceEqual(new long[] { 5, 6 })
                    && !second.HasMore && second.NextCursor.Length == 0 && !second.IsPartial,
                    "next cursor loads new comments without injecting first-page pins again");
                Check(Transport.Requests.Count(item => item.Uri.Host == "www.bilibili.com") == 1,
                    "normal bootstrap cookie is reused across pages");
            }
            Stub(request => Page(3050, true, "", Reply(10), Reply(11), Reply(12)));
            using (var client = new BilibiliClient())
            {
                var result = await client.GetCommentsAsync(117449202600855L);
                Check(result.Items.Count == 3 && result.Total == 3050 && result.TotalKnown
                    && result.IsPartial && !result.HasMore && result.Notice.Contains("登录"),
                    "anonymous three-comment preview never says no comments or all comments loaded");
            }
            Stub(request => Page(4, true, "", Reply(1, 1), Reply(2), Reply(3)));
            using (var client = new BilibiliClient())
                Check(!(await client.GetCommentsAsync(117449202600855L)).IsPartial,
                    "child replies can explain all_count exceeding root comment count");
            var empty = Page(0, true, ""); empty["data"]["replies"] = null;
            Stub(request => empty);
            using (var client = new BilibiliClient())
            {
                var result = await client.GetCommentsAsync(117449202600855L);
                Check(result.Items.Count == 0 && result.TotalKnown && result.Total == 0 && !result.IsClosed,
                    "valid authoritative empty reply cursor remains genuinely empty");
            }
            Stub(request => new JObject { ["code"] = 12002, ["message"] = "评论区已关闭" });
            using (var client = new BilibiliClient())
                Check((await client.GetCommentsAsync(117449202600855L)).IsClosed,
                    "closed comment section distinguished from no comments");
            var closed = new JObject { ["code"] = 0, ["data"] = new JObject
                { ["control"] = new JObject { ["show_text"] = "UP主已关闭评论区" } } };
            Stub(request => closed);
            using (var client = new BilibiliClient())
                Check((await client.GetCommentsAsync(117449202600855L)).IsClosed, "explicit server closure control handled");
            await ExpectError(new JObject { ["code"] = 0, ["data"] = new JObject
                { ["page"] = new JObject { ["count"] = 0 }, ["replies"] = new JArray() } });
            await ExpectError(Page(1000, true, ""));
            var unknownEmpty = Page(0, true, ""); ((JObject)unknownEmpty["data"]["cursor"]).Remove("all_count");
            await ExpectError(unknownEmpty);
            var missingEnd = Page(0, true, ""); ((JObject)missingEnd["data"]["cursor"]).Remove("is_end");
            await ExpectError(missingEnd);
            var malformed = Page(0, true, ""); malformed["data"]["replies"] = new JObject();
            await ExpectError(malformed);
            var malformedReply = Page(1, true, "", Reply(0)); await ExpectError(malformedReply);
            await ExpectError(Page(0, true, "", Reply(1)));
            await ExpectError(Page(3, false, "cursor"));
            await ExpectError(new JObject { ["code"] = -352 }, -352);
            await ExpectError(new JObject { ["code"] = -101 }, -101);
            await ExpectError(new JObject { ["code"] = 0 }, 412, 412);
            Stub(request => Page(10, false, "", Reply(1)));
            using (var client = new BilibiliClient())
            {
                var result = await client.GetCommentsAsync(117449202600855L);
                Check(!result.HasMore && result.IsPartial && result.NextCursor == "",
                    "missing actual next cursor is partial, never a numeric invented next page");
            }
            Stub(request => Page(1, false, "same", Reply(1)));
            using (var client = new BilibiliClient())
            {
                var result = await client.GetCommentsAsync(117449202600855L, "same");
                Check(result.HasMore && !result.IsPartial && result.NextCursor == "same",
                    "opaque session token equality alone does not indicate pagination ended");
                var requests = Transport.Requests.Count;
                try { await client.GetCommentsAsync(117449202600855L, 2); throw new Exception("int page accepted"); }
                catch (ArgumentException) { Check(Transport.Requests.Count == requests, "legacy page two rejected before I/O"); }
                using (var cancel = new CancellationTokenSource())
                {
                    cancel.Cancel();
                    try { await client.GetCommentsAsync(117449202600855L, "", cancel.Token); throw new Exception("cancel ignored"); }
                    catch (OperationCanceledException) { Check(Transport.Requests.Count == requests, "cancellation propagates without I/O"); }
                }
            }
            await RunServerCursorPages();
            RunPaginationGuard();
            await RunReplyPages();
            return "PASS: " + _passed + " current comment API, emoji, avatars, child previews, reply pages, cursor, Unicode, partial/closed/empty and error checks using production source.";
        }

        private static async Task RunServerCursorPages()
        {
            const string offset = "{\"type\":1,\"direction\":1,\"data\":{\"pn\":2}}";
            int pageCalls = 0;
            Stub(request =>
            {
                var query = Query(request); ++pageCalls;
                if (pageCalls == 1)
                {
                    Check(JObject.Parse(query["pagination_str"])["offset"].ToString() == "", "first page begins without a cursor");
                    var first = Page(62, false, offset, Enumerable.Range(1, 20).Select(id => Reply(id)).ToArray());
                    first["data"]["top"] = new JObject { ["upper"] = Reply(100) };
                    return first;
                }
                if (pageCalls == 2)
                {
                    Check(JObject.Parse(query["pagination_str"])["offset"].ToString() == offset, "preferred opaque cursor round trips exactly");
                    var second = Page(62, false, "", Enumerable.Range(21, 20).Select(id => Reply(id)).ToArray());
                    ((JObject)second["data"]["cursor"]).Remove("pagination_reply");
                    second["data"]["cursor"]["next"] = 73;
                    return second;
                }
                if (pageCalls == 3)
                {
                    Check(query["next"] == "73" && !query.ContainsKey("seek_rpid"), "missing offset continues with exact server numeric cursor, not page three");
                    var third = Page(62, false, "", Enumerable.Range(41, 20).Select(id => Reply(id)).ToArray());
                    third["data"]["cursor"]["next"] = 91;
                    return third;
                }
                Check(query["next"] == "91", "subsequent numeric cursor retains server jump rather than incrementing locally");
                return Page(62, true, "", Reply(61));
            });
            using (var client = new BilibiliClient())
            {
                var seen = new HashSet<long>(); string cursor = "";
                var displayed = new List<Models.CommentItem>();
                var guard = new Controls.CommentPageGuard();
                for (int index = 0; index < 4; index++)
                {
                    var page = await client.GetCommentsAsync(117449202600855L, cursor);
                    guard.Accept(page, cursor, index == 0, displayed);
                    displayed.AddRange(page.Items);
                    foreach (var item in page.Items) Check(seen.Add(item.Id), "additional comment pages return distinct IDs beyond forty");
                    Check(index == 3 ? !page.HasMore : page.HasMore && !page.IsPartial, "only explicit server end terminates valid pagination");
                    var presentation = Controls.CommentPresentation.Create(page, displayed.Count, 62);
                    Check(presentation.CanLoadMore == (index < 3) && !presentation.CanRetry,
                        "production collection guard and button presentation keep valid later pages available");
                    Check(presentation.Hint.Contains("已加载 " + displayed.Count + " 条"), "presentation uses accumulated count across actual service pages");
                    if (index == 1) Check(displayed.Count == 41 && presentation.MoreText == "加载更多评论",
                        "recorded twenty-one plus twenty boundary still exposes next action with numeric server cursor");
                    cursor = page.NextCursor;
                }
                Check(seen.Count == 62 && pageCalls == 4, "four-page regression crosses both twenty- and forty-comment boundaries including pinned comment");
            }
            pageCalls = 0;
            Stub(request =>
            {
                if (++pageCalls == 1)
                {
                    var first = Page(1049, false, offset, Enumerable.Range(1, 20).Select(id => Reply(id)).ToArray());
                    first["data"]["top"] = new JObject { ["upper"] = Reply(100) };
                    return first;
                }
                return Page(1049, false, "", Enumerable.Range(21, 20).Select(id => Reply(id)).ToArray());
            });
            using (var client = new BilibiliClient())
            {
                var guard = new Controls.CommentPageGuard(); var displayed = new List<Models.CommentItem>();
                var first = await client.GetCommentsAsync(117449202600855L);
                guard.Accept(first, "", true, displayed); displayed.AddRange(first.Items);
                var second = await client.GetCommentsAsync(117449202600855L, first.NextCursor);
                guard.Accept(second, first.NextCursor, false, displayed); displayed.AddRange(second.Items);
                var presentation = Controls.CommentPresentation.Create(second, displayed.Count, 1049);
                Check(displayed.Count == 41 && !presentation.CanLoadMore && presentation.CanRetry,
                    "recording-shaped missing-both-cursors response preserves forty-one rows and leaves recovery actionable");
                Check(presentation.MoreText == "重试加载评论" && presentation.Hint.Contains("已加载 41 条")
                    && !presentation.Hint.Contains("当前显示 20 条") && presentation.MoreText != "已加载全部评论",
                    "unavailable continuation never contradicts aggregate count or pretends to have fetched all 1049 comments");
            }
            var repeatedOpaque = Page(1000, false, offset, Reply(30));
            repeatedOpaque["data"]["cursor"]["next"] = 7;
            Stub(request => repeatedOpaque);
            using (var client = new BilibiliClient())
            {
                var page = await client.GetCommentsAsync(117449202600855L, offset);
                Check(page.HasMore && page.NextCursor == offset, "repeated opaque server session token takes priority over older numeric cursor");
            }
            await RunRepeatedOpaqueCursorPages();
            foreach (var invalid in new JToken[] { 0, -1, "not-a-cursor", "3.5", true })
            {
                var broken = Page(1000, false, "", Reply(1)); broken["data"]["cursor"]["next"] = invalid;
                Stub(request => broken);
                using (var client = new BilibiliClient())
                {
                    var page = await client.GetCommentsAsync(117449202600855L);
                    Check(!page.HasMore && page.IsPartial && !page.Notice.Contains("当前显示"), "invalid next remains partial without a contradictory per-page loaded count");
                }
            }
            var repeated = Page(1000, false, "", Reply(25)); repeated["data"]["cursor"]["next"] = 73;
            Stub(request => repeated);
            using (var client = new BilibiliClient())
            {
                var page = await client.GetCommentsAsync(117449202600855L, "bili-comment-next:73");
                Check(!page.HasMore && page.IsPartial, "same numeric server cursor cannot repeat indefinitely");
            }
            repeated["data"]["cursor"]["next"] = 2;
            Stub(request => repeated);
            using (var client = new BilibiliClient())
                Check(!(await client.GetCommentsAsync(117449202600855L, offset)).HasMore,
                    "opaque-to-numeric transition cannot rerequest the same recognized position");
        }

        private static async Task RunRepeatedOpaqueCursorPages()
        {
            // Modern reply sessions can reuse next_offset while moving forward.
            // The legacy numeric cursor may be absent, zero or unchanged.
            const string offset = "eyJzZXNzaW9uX2lkIjoic2VydmVyLW9wYXF1ZS10b2tlbiJ9+/=";
            foreach (var numeric in new JToken[] { null, 0, 1 })
            {
                int calls = 0;
                Stub(request =>
                {
                    var query = Query(request);
                    var sent = JObject.Parse(query["pagination_str"])["offset"].ToString();
                    Check(sent == (calls == 0 ? "" : offset) && !query.ContainsKey("next"),
                        "unchanged opaque offset is signed and sent exactly, without substituting a legacy page");
                    int index = calls++;
                    var replies = index < 3
                        ? Enumerable.Range(index * 20 + 1, 20).Select(id => Reply(id)).ToArray()
                        : new[] { Reply(61) };
                    var page = Page(62, index == 3, offset, replies);
                    if (numeric != null) page["data"]["cursor"]["next"] = numeric.DeepClone();
                    if (index == 0) page["data"]["top"] = new JObject { ["upper"] = Reply(100) };
                    return page;
                });
                using (var client = new BilibiliClient())
                {
                    var displayed = new List<Models.CommentItem>();
                    var guard = new Controls.CommentPageGuard();
                    string cursor = "";
                    for (int index = 0; index < 4; index++)
                    {
                        var page = await client.GetCommentsAsync(117449202600855L, cursor);
                        guard.Accept(page, cursor, index == 0, displayed);
                        displayed.AddRange(page.Items);
                        Check(displayed.Select(item => item.Id).Distinct().Count() == displayed.Count,
                            "reused session token still returns distinct comments");
                        Check(displayed.Count == (index < 3 ? 21 + index * 20 : 62),
                            "reused opaque token crosses the twenty, forty and sixty row boundaries");
                        var presentation = Controls.CommentPresentation.Create(page, displayed.Count, 62);
                        Check(page.HasMore == (index < 3) && !page.IsPartial && !presentation.CanRetry,
                            "server is_end controls completion while unchanged offset keeps new pages available");
                        Check(index == 3 ? !presentation.CanLoadMore : presentation.CanLoadMore && page.NextCursor == offset,
                            "load-more stays enabled through repeated opaque cursors until explicit server end");
                        cursor = page.NextCursor;
                    }
                    Check(calls == 4 && displayed.Count == 62, "all valid pages are requested despite a repeated server token");
                }
            }
        }

        private static void RunPaginationGuard()
        {
            var guard = new Controls.CommentPageGuard();
            var rows = new List<Models.CommentItem>();
            var first = new Models.CommentPage { Items = new[] { new Models.CommentItem { Id = 1 } }, HasMore = true, NextCursor = "A" };
            guard.Accept(first, "", true, rows); rows.AddRange(first.Items);
            var second = new Models.CommentPage { Items = new[] { new Models.CommentItem { Id = 2 } }, HasMore = true, NextCursor = "B" };
            guard.Accept(second, "A", false, rows); rows.AddRange(second.Items);
            var third = new Models.CommentPage { Items = new[] { new Models.CommentItem { Id = 3 } }, HasMore = true, NextCursor = "A" };
            guard.Accept(third, "B", false, rows); rows.AddRange(third.Items);
            Check(rows.Count == 3 && !third.IsPartial && third.HasMore && third.NextCursor == "A", "reused opaque token with new reply IDs does not terminate the session");
            var noProgress = new Models.CommentPage { Items = new[] { new Models.CommentItem { Id = 3 } }, HasMore = true, NextCursor = "A" };
            try { guard.Accept(noProgress, "A", false, rows); throw new Exception("FAIL: duplicate-only page accepted"); }
            catch (InvalidOperationException) { Check(rows.Count == 3 && noProgress.NextCursor == "A", "duplicate-only page retains existing rows and original session token for explicit retry"); }
            var recovered = new Models.CommentPage { Items = new[] { new Models.CommentItem { Id = 4 } }, HasMore = true, NextCursor = "A" };
            guard.Accept(recovered, "A", false, rows);
            Check(recovered.HasMore && !recovered.IsPartial, "retry with unchanged opaque cursor can recover when reply IDs advance");
            guard.Accept(first, "", true, rows);
            Check(first.HasMore, "successful refresh can start a fresh reply list");
        }

        private static async Task RunReplyPages()
        {
            Func<int, int, int, JObject[], JObject> response = (page, count, size, replies) => new JObject
            {
                ["code"] = 0, ["data"] = new JObject
                {
                    ["root"] = Reply(8, count),
                    ["page"] = new JObject { ["num"] = page, ["count"] = count, ["size"] = size },
                    ["replies"] = new JArray(replies)
                }
            };
            Action<Func<HttpRequestMessage, JObject>> configure = result => Transport.Reset((request, token) =>
            {
                Check(request.Method.Method == "GET", "thread fetches never write or like comments");
                if (request.RequestUri.Host == "www.bilibili.com")
                {
                    var cookie = new HttpResponseMessage { Content = new HttpStringContent("website") };
                    cookie.Headers["Set-Cookie"] = "buvid3=server-fixture; Domain=.bilibili.com; Path=/; Secure";
                    return Task.FromResult(cookie);
                }
                Check(request.RequestUri.Host == "api.bilibili.com"
                    && request.RequestUri.AbsolutePath == "/x/v2/reply/reply", "uses website child reply endpoint");
                var query = Query(request);
                Check(query["type"] == "1" && query["oid"] == "117449202600855" && query["root"] == "8"
                    && query["ps"] == "20", "child pagination is scoped to the video and root comment");
                return Transport.Json(result(request).ToString());
            });
            configure(request => Query(request)["pn"] == "1"
                ? response(1, 21, 20, Enumerable.Range(1, 20).Select(id => Reply(100 + id)).ToArray())
                : response(2, 21, 20, new[] { Reply(121) }));
            using (var client = new BilibiliClient())
            {
                var first = await client.GetCommentRepliesAsync(117449202600855L, 8);
                var second = await client.GetCommentRepliesAsync(117449202600855L, 8, 2);
                Check(first.Items.Count == 20 && first.HasMore && first.TotalKnown && first.Total == 21 && !first.IsPartial,
                    "full thread page preserves authoritative count and next page");
                Check(second.Page == 2 && second.Items.Single().Id == 121 && !second.HasMore && !second.IsPartial,
                    "next child page returns remaining reply without repeating previews");
                var requests = Transport.Requests.Count;
                try { await client.GetCommentRepliesAsync(117449202600855L, 0); throw new Exception("invalid root accepted"); }
                catch (ArgumentOutOfRangeException) { Check(Transport.Requests.Count == requests, "invalid root rejected before I/O"); }
                try { await client.GetCommentRepliesAsync(117449202600855L, 8, 0); throw new Exception("invalid page accepted"); }
                catch (ArgumentOutOfRangeException) { Check(Transport.Requests.Count == requests, "invalid child page rejected before I/O"); }
                using (var cancel = new CancellationTokenSource())
                {
                    cancel.Cancel();
                    try { await client.GetCommentRepliesAsync(117449202600855L, 8, 1, cancel.Token); throw new Exception("cancel ignored"); }
                    catch (OperationCanceledException) { Check(Transport.Requests.Count == requests, "child cancellation propagated before I/O"); }
                }
            }
            configure(request => response(1, 100, 20, new[] { Reply(101), Reply(101) }));
            using (var client = new BilibiliClient())
            {
                var partial = await client.GetCommentRepliesAsync(117449202600855L, 8);
                Check(partial.Items.Count == 1 && partial.IsPartial && !partial.HasMore && partial.Notice.Length > 0,
                    "short and duplicate restricted thread previews are never described as complete");
            }
            configure(request => response(1, 0, 20, new JObject[0]));
            using (var client = new BilibiliClient())
                Check((await client.GetCommentRepliesAsync(117449202600855L, 8)).Items.Count == 0,
                    "authoritative zero child count can be empty");
            var invalids = new List<JObject>
            {
                response(1, 3, 20, new JObject[0]), response(2, 1, 20, new[] { Reply(101) }),
                new JObject { ["code"] = -101 }, new JObject { ["code"] = -352 },
                new JObject { ["code"] = 0, ["data"] = new JObject { ["replies"] = new JArray() } }
            };
            var wrongRoot = response(1, 1, 20, new[] { Reply(101) }); wrongRoot["data"]["root"]["rpid"] = 999;
            invalids.Add(wrongRoot);
            var wrongChildRoot = response(1, 1, 20, new[] { Reply(101) }); wrongChildRoot["data"]["replies"][0]["root"] = 999;
            invalids.Add(wrongChildRoot);
            foreach (var invalid in invalids)
            {
                configure(request => invalid);
                using (var client = new BilibiliClient())
                {
                    try { await client.GetCommentRepliesAsync(117449202600855L, 8); throw new Exception("invalid child response accepted"); }
                    catch (BilibiliApiException) { Check(true, "unavailable or mismatched thread never becomes an empty list"); }
                }
            }
        }
        public static async Task<string> RunLive(string bvid)
        {
            using (var handler = new System.Net.Http.HttpClientHandler
            {
                UseCookies = false, AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
            })
            using (var http = new System.Net.Http.HttpClient(handler))
            {
                Transport.Reset(async (request, token) =>
                {
                    if (request.Method.Method != "GET" || request.RequestUri.Scheme != "https"
                        || (request.RequestUri.Host != "api.bilibili.com" && request.RequestUri.Host != "www.bilibili.com"))
                        throw new InvalidOperationException("Public comment probe permits only Bilibili HTTPS GET requests.");
                    var currentUri = request.RequestUri;
                    var bootstrap = currentUri.Host == "www.bilibili.com" && currentUri.AbsolutePath == "/";
                    var redirectCookies = new ProtectedCookieStore();
                    var receivedCookies = new List<string>();
                    for (int redirects = 0; ; redirects++)
                    {
                        using (var outgoing = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, currentUri))
                        {
                            outgoing.Headers.TryAddWithoutValidation("User-Agent", BilibiliClient.UserAgent);
                            outgoing.Headers.Referrer = new Uri(BilibiliClient.Referer);
                            foreach (var header in request.Headers) outgoing.Headers.TryAddWithoutValidation(header.Key, header.Value);
                            if (redirects > 0)
                            {
                                var cookies = redirectCookies.GetCookieHeader(currentUri);
                                if (cookies.Length > 0)
                                {
                                    outgoing.Headers.Remove("Cookie");
                                    outgoing.Headers.TryAddWithoutValidation("Cookie", cookies);
                                }
                            }
                            using (var response = await http.SendAsync(outgoing, token))
                            {
                                var body = await response.Content.ReadAsStringAsync();
                                var incoming = new HttpResponseMessage { StatusCode = (int)response.StatusCode,
                                    Content = new HttpStringContent(body) };
                                IEnumerable<string> cookies;
                                if (response.Headers.TryGetValues("Set-Cookie", out cookies))
                                {
                                    var realCookies = cookies.ToArray();
                                    incoming.Headers["Set-Cookie"] = string.Join(",", realCookies);
                                    receivedCookies.AddRange(realCookies);
                                    redirectCookies.ApplyResponseCookies(incoming, currentUri);
                                }
                                // LIVE TEST ADAPTER ONLY: the public landing page
                                // can redirect to /zh-Hans. Follow up to three
                                // HTTPS same-host bootstrap redirects and return
                                // its actual Set-Cookie headers to production's
                                // in-memory jar. No account store is loaded.
                                int status = (int)response.StatusCode;
                                if (bootstrap && (status == 301 || status == 302 || status == 303 || status == 307 || status == 308)
                                    && response.Headers.Location != null)
                                {
                                    var next = new Uri(currentUri, response.Headers.Location);
                                    if (redirects >= 3 || next.Scheme != "https" || next.Host != "www.bilibili.com"
                                        || next.Port != 443 || next.UserInfo.Length > 0)
                                        throw new InvalidOperationException("Public bootstrap redirect exceeded the same-host HTTPS probe limit.");
                                    currentUri = next;
                                    continue;
                                }
                                if (receivedCookies.Count > 0) incoming.Headers["Set-Cookie"] = string.Join(",", receivedCookies);
                                return incoming;
                            }
                        }
                    }
                });
                using (var client = new BilibiliClient())
                {
                    var detail = await client.GetVideoAsync(bvid);
                    var pages = new JArray();
                    var displayed = new List<Models.CommentItem>();
                    var seen = new HashSet<long>();
                    var guard = new Controls.CommentPageGuard();
                    Models.CommentPage result = null;
                    string cursor = "";
                    for (int index = 0; index < 6; index++)
                    {
                        var page = await client.GetCommentsAsync(detail.Aid, cursor);
                        if (page.Items.Count == 0 && (!page.TotalKnown || page.Total > 0 || detail.ReplyCount > 0))
                            throw new Exception("Live regression returned no visible comments despite a positive or unknown public count.");
                        int newCount = page.Items.Count(item => !seen.Contains(item.Id));
                        guard.Accept(page, cursor, index == 0, displayed);
                        foreach (var item in page.Items)
                            if (seen.Add(item.Id)) displayed.Add(item);
                        string hash;
                        using (var sha = System.Security.Cryptography.SHA256.Create())
                            hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(page.NextCursor ?? "")))
                                .Replace("-", "").ToLowerInvariant();
                        pages.Add(new JObject
                        {
                            ["Page"] = index + 1, ["Items"] = page.Items.Count, ["NewItems"] = newCount,
                            ["UniqueTotal"] = displayed.Count, ["HasMore"] = page.HasMore,
                            ["IsPartial"] = page.IsPartial, ["ServerTotal"] = page.Total,
                            ["NextCursorEqualsRequested"] = page.HasMore && page.NextCursor == cursor,
                            ["NextCursorSha256"] = hash
                        });
                        if (result == null) result = page;
                        cursor = page.NextCursor;
                        if (!page.HasMore) break;
                    }
                    var previewReplies = result.Items.Sum(item => item.Replies.Count);
                    var emojiImages = result.Items.Concat(result.Items.SelectMany(item => item.Replies))
                        .Sum(item => item.ContentParts.Count(part => part.IsImage));
                    var child = result.Items.Where(item => item.ReplyCount > 0).OrderByDescending(item => item.ReplyCount).FirstOrDefault();
                    var childCount = 0;
                    var childPartial = false;
                    var newChildSecondPage = 0;
                    if (child != null)
                    {
                        var children = await client.GetCommentRepliesAsync(detail.Aid, child.Id);
                        childCount = children.Items.Count;
                        childPartial = children.IsPartial;
                        emojiImages += children.Items.Sum(item => item.ContentParts.Count(part => part.IsImage));
                        if (children.HasMore)
                        {
                            var seenChild = new HashSet<long>(children.Items.Select(item => item.Id));
                            var childSecond = await client.GetCommentRepliesAsync(detail.Aid, child.Id, 2);
                            newChildSecondPage = childSecond.Items.Count(item => !seenChild.Contains(item.Id));
                            if (newChildSecondPage == 0) throw new Exception("Child reply page two repeated page one.");
                        }
                    }
                    return new JObject
                    {
                        ["Bvid"] = detail.Bvid, ["Title"] = detail.Title,
                        ["VisibleComments"] = result.Items.Count, ["ServerTotal"] = result.Total,
                        ["TotalKnown"] = result.TotalKnown, ["IsPartial"] = result.IsPartial,
                        ["HasMore"] = result.HasMore, ["RequestedRootPageLimit"] = 6,
                        ["RootPages"] = pages, ["UniqueRootComments"] = displayed.Count,
                        ["PaginationBeyond40Verified"] = pages.Count >= 3 && displayed.Count > 40,
                        ["NewSecondPageComments"] = pages.Count > 1 ? (int)pages[1]["NewItems"] : 0,
                        ["OwnerAvatarPresent"] = detail.OwnerAvatarUrl.Length > 0,
                        ["CommentAvatars"] = result.Items.Count(item => item.AvatarUrl.Length > 0),
                        ["LikesPresent"] = result.Items.Count(item => item.Likes > 0),
                        ["EmojiImages"] = emojiImages, ["ChildPreviews"] = previewReplies,
                        ["ThreadPageReplies"] = childCount, ["ThreadPagePartial"] = childPartial,
                        ["NewThreadSecondPageReplies"] = newChildSecondPage,
                        ["Notice"] = result.Notice
                    }.ToString();
                }
            }
        }
    }
}
