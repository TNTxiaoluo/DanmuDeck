// Production mapper, cookie/session checks, HTTP forms, and reaction service.
// This fixture transport cannot access a real account or execute real writes.
namespace BiliXbox.RichCommentTests
{
    using System.Reflection;
    using BiliXbox.Services;
    using BiliXbox.VideoDetailTests;

    public static class Tests
    {
        private const long Oid = 117160886206980;
        private const long Rpid = 311835469425;
        private static int _checks;
        private static void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); _checks++; }
        private static JObject Ok(JToken data = null) => data == null ? new JObject { ["code"] = 0 }
            : new JObject { ["code"] = 0, ["data"] = data };
        private static JObject Raw(int action = 0, long likes = 678) => new JObject
        {
            ["rpid"] = Rpid, ["oid"] = Oid, ["type"] = 1, ["root"] = 0,
            ["ctime"] = 1787735485, ["like"] = likes, ["action"] = action, ["rcount"] = 2,
            ["member"] = new JObject
            {
                ["mid"] = "1949411764", ["uname"] = "资料来自公开字段",
                ["avatar"] = "https://i1.hdslb.com/bfs/face/fixture.jpg",
                ["level_info"] = new JObject { ["current_level"] = 6 },
                ["user_sailing"] = new JObject { ["cardbg"] = new JObject
                    { ["image"] = "https://i0.hdslb.com/bfs/garb/cardbg.png",
                        ["fan"] = new JObject { ["is_fan"] = 1, ["num_str"] = "000933", ["number"] = 933 } } }
            },
            ["content"] = new JObject
            {
                ["message"] = "长表情[收藏_打call] [doge] [未知] [收藏_打call] 🎮",
                ["emote"] = new JObject
                {
                    ["[收藏_打call]"] = new JObject { ["type"] = 2, ["url"] = "//i0.hdslb.com/bfs/garb/paid.png",
                        ["meta"] = new JObject { ["size"] = 2, ["width"] = 160, ["height"] = 100 } },
                    ["[doge]"] = new JObject { ["url"] = "http://i0.hdslb.com/bfs/emote/doge.png", ["meta"] = new JObject { ["size"] = 1 } }
                },
                ["pictures"] = new JArray(
                    new JObject { ["img_src"] = "http://i0.hdslb.com/bfs/new_dyn/picture.png", ["img_width"] = 1336, ["img_height"] = 2432 },
                    new JObject { ["img_src"] = "https://i0.hdslb.com/bfs/new_dyn/picture.png", ["img_width"] = 1336, ["img_height"] = 2432 },
                    new JObject { ["img_src"] = "https://i0.hdslb.com.evil.invalid/leak.png", ["img_width"] = 1 },
                    new JObject { ["img_src"] = "https://i0.hdslb.com:444/not-standard.png" },
                    new JObject { ["img_src"] = "https://user@i0.hdslb.com/not-standard.png" },
                    new JObject { ["img_src"] = "data:image/png;base64,AA==" },
                    new JObject { ["img_src"] = "//i1.hdslb.com/second.webp", ["img_width"] = -2, ["img_height"] = 999999999 })
            },
            ["reply_control"] = new JObject { ["location"] = "IP属地：上海", ["is_up"] = true, ["is_top"] = true }
        };
        private static CommentItem Map(JObject raw) => (CommentItem)typeof(BilibiliClient)
            .GetMethod("MapCommentItem", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { raw, true });
        private static CommentItem Target(int type = 1) => new CommentItem { Id = Rpid, Oid = Oid, Type = type };
        private static Dictionary<string, string> Query(HttpRequestMessage request) => request.RequestUri.Query.TrimStart('?')
            .Split('&').Select(pair => pair.Split(new[] { '=' }, 2))
            .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]));
        private static Dictionary<string, string> Body(HttpRequestMessage request) => ((HttpFormUrlEncodedContent)request.Content).Values;
        private static int Posts => Transport.Requests.Count(request => request.Method == "POST");
        private static void Stub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler, bool csrf = true)
        {
            Transport.Reset(async (request, token) =>
            {
                Check(request.RequestUri.Scheme == "https" && request.RequestUri.Host == "api.bilibili.com", "official HTTPS only");
                if (request.RequestUri.AbsolutePath == "/x/web-interface/nav")
                {
                    var response = new HttpResponseMessage { Content = new HttpStringContent(Ok(new JObject
                        { ["mid"] = 42, ["uname"] = "fixture", ["isLogin"] = true }).ToString()) };
                    response.Headers["Set-Cookie"] = "SESSDATA=fixture-session; Domain=.bilibili.com; Path=/; Secure,buvid3=fixture-device; Domain=.bilibili.com; Path=/; Secure"
                        + (csrf ? ",bili_jct=fixture-csrf; Domain=.bilibili.com; Path=/; Secure" : "");
                    return response;
                }
                if (request.Method.Method == "POST")
                {
                    Check(Body(request)["csrf"] == "fixture-csrf" && request.Headers["Origin"] == "https://www.bilibili.com", "matching body CSRF and origin");
                    Check(!request.RequestUri.Query.Contains("csrf") && request.Headers["Cookie"].Contains("SESSDATA=fixture-session"), "reuse account jar without URL secrets");
                }
                return await handler(request, token);
            });
        }
        private static void Stub(Func<HttpRequestMessage, JObject> handler, bool csrf = true)
            => Stub((request, token) => Transport.Json(handler(request).ToString()), csrf);
        private static async Task Login(BilibiliClient client) => await (Task)typeof(BilibiliClient)
            .GetMethod("RefreshProfileAsync", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(client, new object[] { CancellationToken.None });
        private static void ChangeAccount(BilibiliClient client, long mid) => typeof(BilibiliClient).GetProperty("CurrentUser")
            .SetValue(client, new UserProfile { Mid = mid, Name = "changed" });
        private static async Task Reject(Func<Task> action, string name)
        {
            try { await action(); }
            catch (Exception error) when (error is BilibiliApiException || error is ArgumentException || error is OperationCanceledException || error is ObjectDisposedException)
            { Check(true, name); return; }
            throw new Exception("FAIL: expected rejection: " + name);
        }
        private static async Task Unconfirmed(Func<Task> action, string name)
        {
            try { await action(); }
            catch (CommentVoteUnconfirmedException error)
            { Check(error.Oid == Oid && error.Type == 1 && error.CommentId == Rpid && error.AccountMid == 42, name); return; }
            throw new Exception("FAIL: expected uncertain result: " + name);
        }
        private static JObject Detail(int action, long likes = 678, long oid = Oid, long rpid = Rpid, int type = 1)
        {
            var raw = Raw(action, likes); raw["oid"] = oid; raw["rpid"] = rpid; raw["type"] = type;
            return Ok(new JObject { ["root"] = raw });
        }

        public static async Task<string> Run()
        {
            _checks = 0;
            var mapped = Map(Raw());
            Check(mapped.Id == Rpid && mapped.Oid == Oid && mapped.Type == 1 && mapped.AuthorMid == 1949411764, "64-bit comment identities retained");
            Check(mapped.Level == 6 && mapped.IsUploader && mapped.IsPinned && mapped.IpLocation == "IP属地：上海", "LV, uploader, pinned and real IP metadata mapped");
            Check(mapped.FanNumber == "000933" && mapped.FanBadgeImageUrl.EndsWith("/cardbg.png"), "CD number preserves leading zeros and original decoration");
            Check(mapped.Pictures.Count == 2 && mapped.Pictures[0].ImageUrl == "https://i0.hdslb.com/bfs/new_dyn/picture.png"
                && mapped.Pictures[0].Width == 1336 && mapped.Pictures[0].Height == 2432, "original image aspect and HTTPS preserved, duplicate removed");
            Check(mapped.Pictures[1].Width == 0 && mapped.Pictures[1].Height == 0, "invalid dimensions omitted without inventing aspect");
            Check(mapped.ContentParts.Count(part => part.IsImage) == 3 && mapped.ContentParts.Count(part => part.Scale == 2) == 2, "repeated paid/collection emotes retain size");
            Check(mapped.ContentParts.First(part => part.Scale == 2).Width == 160 && mapped.ContentParts.First(part => part.Scale == 2).Height == 100, "wide paid emote keeps aspect metadata");
            Check(string.Concat(mapped.ContentParts.Select(part => part.IsImage ? part.AltText : part.Text)) == mapped.Content, "unknown tokens, whitespace and Unicode survive exactly");
            var absent = new JObject { ["rpid"] = 2, ["member"] = new JObject(), ["content"] = new JObject { ["message"] = "无可选字段" } };
            var plain = Map(absent);
            Check(plain.Level == null && plain.IpLocation == "" && plain.FanNumber == "" && plain.FanBadgeImageUrl == ""
                && !plain.VoteKnown && plain.Pictures.Count == 0, "missing metadata stays absent");
            foreach (var level in new JToken[] { -1, 7, "invalid", JValue.CreateNull() })
            { var raw = Raw(); raw["member"]["level_info"]["current_level"] = level; Check(Map(raw).Level == null, "invalid level hidden"); }
            var levelZero = Raw(); levelZero["member"]["level_info"]["current_level"] = 0;
            Check(Map(levelZero).Level == 0, "genuine LV0 retained");
            var nonFan = Raw(); nonFan["member"]["user_sailing"]["cardbg"]["fan"]["is_fan"] = 0;
            Check(Map(nonFan).FanNumber == "", "non-owner fan number is not fabricated");
            var numericFan = Raw(); ((JObject)numericFan["member"]["user_sailing"]["cardbg"]["fan"]).Remove("num_str");
            Check(Map(numericFan).FanNumber == "933", "numeric fan field used only when explicitly present");
            var attack = Raw(); attack["content"]["emote"]["[收藏_打call]"]["url"] = "https://hdslb.com.evil.invalid/a.png";
            Check(Map(attack).ContentParts.Count(part => part.IsImage) == 1
                && string.Concat(Map(attack).ContentParts.Select(part => part.IsImage ? part.AltText : part.Text)) == mapped.Content, "untrusted emote URL remains text");
            var nested = Raw(); nested["replies"] = new JArray(Raw());
            Check(Map(nested).Replies[0].Pictures.Count == 2 && Map(nested).Replies[0].Level == 6, "child previews retain rich content and metadata");

            Transport.Reset((request, token) => { throw new Exception("Anonymous operation reached transport"); });
            using (var client = new BilibiliClient())
            {
                await Reject(() => client.GetCommentVoteAsync(Target()), "anonymous state denied before HTTP");
                await Reject(() => client.SetCommentVoteAsync(Target(), CommentVote.Like), "anonymous vote denied before HTTP");
                await Reject(() => client.SetCommentVoteAsync(new CommentItem { Id = Rpid }, CommentVote.Like), "missing resource context denied");
                await Reject(() => client.SetCommentVoteAsync(Target(), (CommentVote)10), "invalid desired action denied");
                Check(Transport.Requests.Count == 0, "invalid/anonymous requests never post");
            }
            var state = 0;
            var likes = 700L;
            Stub(request =>
            {
                if (request.Method.Method == "GET")
                {
                    var query = Query(request);
                    Check(request.RequestUri.AbsolutePath == "/x/v2/reply/detail" && query["oid"] == Oid.ToString()
                        && query["root"] == Rpid.ToString() && query["type"] == "1", "read exact comment subject and 64-bit rpid");
                    return Detail(state, likes);
                }
                var body = Body(request);
                Check(body["oid"] == Oid.ToString() && body["type"] == "1" && body["rpid"] == Rpid.ToString(), "vote body bound to exact subject/comment");
                Check(body.Count == 5 && (body["action"] == "1" || body["action"] == "0"), "form uses one documented action and no inferred counts");
                var hate = request.RequestUri.AbsolutePath == "/x/v2/reply/hate";
                // Independently verified against Bilibili's official
                // commentpc/bili-comments.js POST declarations, not /reply/like.
                Check(hate || request.RequestUri.AbsolutePath == "/x/v2/reply/action", "only genuine comment mutation route");
                state = body["action"] == "0" ? 0 : hate ? 2 : 1;
                likes = state == 1 ? 701 : 700;
                return Ok();
            });
            using (var client = new BilibiliClient())
            {
                await Login(client);
                var target = Target(); target.Likes = 3; target.Vote = CommentVote.Hate;
                var result = await client.SetCommentVoteAsync(target, CommentVote.Like);
                Check(result.Vote == CommentVote.Like && result.Likes == 701 && result.AccountMid == 42 && result.CommentId == Rpid, "like result comes from authoritative detail readback");
                Check(target.Likes == 3 && target.Vote == CommentVote.Hate, "service never mutates UI target while awaiting");
                var postCount = Posts; await client.SetCommentVoteAsync(target, CommentVote.Like);
                Check(Posts == postCount, "already-active like is idempotent after fresh read");
                result = await client.SetCommentVoteAsync(target, CommentVote.Hate);
                Check(result.Vote == CommentVote.Hate && result.Likes == 700 && Posts == postCount + 1, "like-to-hate uses one replacement POST with fresh count");
                result = await client.SetCommentVoteAsync(target, CommentVote.None);
                Check(result.Vote == CommentVote.None && Posts == postCount + 2, "hate cancellation uses hate action0 once");
                var lastPost = Transport.Requests.Last(request => request.Method == "POST");
                Check(lastPost.Uri.AbsolutePath == "/x/v2/reply/hate", "None cancels actual server reaction instead of stale UI reaction");
                await client.SetCommentVoteAsync(target, CommentVote.Like);
                await client.SetCommentVoteAsync(target, CommentVote.None);
                Check(Transport.Requests.Last(request => request.Method == "POST").Uri.AbsolutePath == "/x/v2/reply/action", "like cancellation uses action route with action0");
            }
            Stub(request => Detail(2, type: 17));
            using (var client = new BilibiliClient())
            {
                await Login(client); var result = await client.GetCommentVoteAsync(Target(17));
                Check(result.Type == 17 && result.Vote == CommentVote.Hate && Posts == 0, "dynamic reaction reads preserve actual type without writes");
            }
            foreach (var invalid in new[] { Detail(0, oid: 123), Detail(0, rpid: 123), Detail(0, type: 17), Detail(3), Detail(0, -1) })
            {
                Stub(request => invalid);
                using (var client = new BilibiliClient())
                { await Login(client); await Reject(() => client.SetCommentVoteAsync(Target(), CommentVote.Like), "invalid/mismatched detail rejects before mutation"); Check(Posts == 0, "bad detail cannot target a POST"); }
            }
            Stub(request => Detail(0), csrf: false);
            using (var client = new BilibiliClient())
            { await Login(client); await Reject(() => client.SetCommentVoteAsync(Target(), CommentVote.Like), "missing CSRF denied"); Check(Posts == 0, "missing CSRF cannot mutate"); }

            state = 0; var fail = true;
            Stub((request, token) =>
            {
                if (request.Method.Method == "GET") return Transport.Json(Detail(state).ToString());
                state = 1;
                if (fail) throw new Exception("Synthetic connection lost after server accepted action");
                return Transport.Json(Ok().ToString());
            });
            using (var client = new BilibiliClient())
            {
                await Login(client);
                await Unconfirmed(() => client.SetCommentVoteAsync(Target(), CommentVote.Like), "transport loss reported as unconfirmed with exact target");
                Check(Posts == 1, "unknown transport response never auto retries");
                await Unconfirmed(() => client.SetCommentVoteAsync(Target(), CommentVote.Like), "new target model cannot bypass unknown-write guard");
                Check(Posts == 1, "unreconciled repeated click does not POST");
                var result = await client.GetCommentVoteAsync(Target());
                Check(result.Vote == CommentVote.Like, "explicit GET reconciles real accepted server state");
                fail = false;
                await client.SetCommentVoteAsync(Target(), CommentVote.Like);
                Check(Posts == 1, "reconciled accepted like stays idempotent");
            }
            Stub(request => request.Method.Method == "GET" ? Detail(0) : Ok());
            using (var client = new BilibiliClient())
            {
                await Login(client); await Unconfirmed(() => client.SetCommentVoteAsync(Target(), CommentVote.Like), "accepted POST with inconsistent readback stays unconfirmed");
                Check(Posts == 1, "inconsistent readback never causes automatic correction POST");
            }
            Stub(request => request.Method.Method == "GET" ? Detail(0) : new JObject { ["code"] = -400, ["message"] = "fixture denied" });
            using (var client = new BilibiliClient())
            {
                await Login(client); await Reject(() => client.SetCommentVoteAsync(Target(), CommentVote.Like), "explicit rejection remains rejection");
                await Reject(() => client.SetCommentVoteAsync(Target(), CommentVote.Like), "explicit rejection does not create permanent unknown state");
                Check(Posts == 2, "each later explicit action can retry a known rejection once");
            }
            BilibiliClient changedClient = null;
            Stub(request => { ChangeAccount(changedClient, 99); return Detail(0); });
            using (var client = new BilibiliClient())
            {
                changedClient = client; await Login(client);
                await Reject(() => client.SetCommentVoteAsync(Target(), CommentVote.Like), "account change during preflight cannot post to stale account");
                Check(Posts == 0, "stale account rejected before mutation");
            }
            var entered = new TaskCompletionSource<bool>(); var release = new TaskCompletionSource<bool>(); state = 0;
            Stub(async (request, token) =>
            {
                if (request.Method.Method == "GET") return await Transport.Json(Detail(state).ToString());
                entered.TrySetResult(true); await release.Task; state = 1;
                return await Transport.Json(Ok().ToString());
            });
            using (var client = new BilibiliClient())
            {
                await Login(client);
                var first = client.SetCommentVoteAsync(Target(), CommentVote.Like); await entered.Task;
                var second = client.SetCommentVoteAsync(Target(), CommentVote.Like);
                Check(Posts == 1, "shared gate serializes concurrent clicks");
                release.TrySetResult(true); await first; await second;
                Check(Posts == 1, "queued duplicate becomes read-only after first result");
            }
            Stub(request => new JObject());
            using (var client = new BilibiliClient())
            {
                await Login(client); var cancelled = new CancellationToken(true);
                await Reject(() => client.SetCommentVoteAsync(Target(), CommentVote.Like, cancelled), "cancelled mutation rejected before transport");
                Check(Posts == 0, "cancellation cannot cause a POST");
            }
            Dictionary<string, string> replyForm = null;
            Stub(request => { if (request.Method.Method == "POST") replyForm = Body(request); return Ok(new JObject { ["rpid"] = "777" }); });
            using (var client = new BilibiliClient())
            {
                await Login(client);
                var target = Target();
                await client.PostCommentReplyAsync(Oid, target, "  fixture root reply  ");
                var post = Transport.Requests.Last(request => request.Method == "POST");
                Check(post.Uri.AbsolutePath == "/x/v2/reply/add", "reply uses official comment endpoint");
                Check(replyForm["root"] == Rpid.ToString() && replyForm["parent"] == Rpid.ToString(), "reply to original targets root and parent original");
                Check(replyForm["oid"] == Oid.ToString() && replyForm["type"] == "1" && replyForm["message"] == "fixture root reply", "reply preserves subject and actual message");
                target.RootId = Rpid; target.Id = Rpid + 1;
                await client.PostCommentReplyAsync(Oid, target, "fixture child reply");
                post = Transport.Requests.Last(request => request.Method == "POST");
                Check(replyForm["root"] == Rpid.ToString() && replyForm["parent"] == (Rpid + 1).ToString(), "reply to child keeps original root and selected parent");
                int count = Posts;
                await Reject(() => client.PostCommentReplyAsync(Oid + 1, target, "bad subject"), "cross-video reply rejected");
                target.Type = 17;
                await Reject(() => client.PostCommentReplyAsync(Oid, target, "bad type"), "dynamic target cannot post as video");
                target.Type = 1; target.RootId = -1;
                await Reject(() => client.PostCommentReplyAsync(Oid, target, "bad root"), "negative thread rejected");
                target.RootId = Rpid;
                await Reject(() => client.PostCommentReplyAsync(Oid, target, " "), "empty reply rejected");
                await Reject(() => client.PostCommentReplyAsync(Oid, target, new string('a', 1001)), "oversized reply rejected");
                await Reject(() => client.PostCommentReplyAsync(Oid, target, "cancelled", new CancellationToken(true)), "cancelled reply never posts");
                Check(Posts == count, "all invalid reply targets and cancellation leave transport untouched");
                var sessionGate = (SemaphoreSlim)typeof(BilibiliClient).GetField("_sessionGate", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(client);
                await sessionGate.WaitAsync();
                var queued = client.PostCommentReplyAsync(Oid, target, "queued reply from original account");
                ChangeAccount(client, 99);
                sessionGate.Release();
                await Reject(() => queued, "account changed while reply waited for session gate");
                Check(Posts == count, "queued draft cannot publish through a newly logged-in account");
            }
            return "PASS: " + _checks + " rich comment mapping, metadata, paid emoji, pictures, reactions and root/parent reply checks; all writes mocked.";
        }
    }
}
