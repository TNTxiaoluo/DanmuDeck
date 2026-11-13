// TEST ONLY. Real user-search source, with read-only deterministic HTTP doubles.
namespace BiliXbox.UserSearchTests
{
    using BiliXbox.Services;
    using BiliXbox.VideoDetailTests;
    public static class Tests
    {
        private static int _passed;
        private static void Check(bool condition, string message)
        { if (!condition) throw new Exception("FAIL: " + message); ++_passed; }
        private static JObject Envelope(JObject data) => new JObject { ["code"] = 0, ["data"] = data };
        private static JObject Nav => new JObject { ["code"] = -101, ["data"] = new JObject
        { ["wbi_img"] = new JObject
        {
            ["img_url"] = "https://i0.hdslb.com/bfs/wbi/7cd084941338484aae1ad9425b84077c.png",
            ["sub_url"] = "https://i0.hdslb.com/bfs/wbi/4932caff0ff746eab6f01bf08b70ac45.png"
        } } };
        private static JObject User(long mid = 123, string name = "<em class=\"keyword\">小</em>明 &amp; Xbox")
            => new JObject
            {
                ["type"] = "bili_user", ["mid"] = mid, ["uname"] = name,
                ["usign"] = "介绍 &amp; 签名", ["upic"] = "//i0.hdslb.com/bfs/face/public.webp",
                ["fans"] = 12000, ["videos"] = 0,
                // Embedded videos must not become the search results.
                ["res"] = new JArray(new JObject { ["bvid"] = "BV1spz8B4EXv" })
            };
        private static JObject Data(int page = 1, int size = 20, int total = 21)
            => new JObject
            {
                ["page"] = page, ["pagesize"] = size, ["numResults"] = total,
                ["numPages"] = total == 0 ? 0 : (total + size - 1) / size,
                ["result"] = new JArray(Enumerable.Range(1, Math.Max(0, Math.Min(size, total - (page - 1) * size)))
                    .Select(i => User(1000 + (page - 1) * size + i, "小明相关用户 " + i)))
            };
        private static string Html(string keyword, JObject data)
        {
            var body = new JObject { ["searchTypeResponse"] = new JObject { ["searchTypeResponse"] = data } }
                .ToString(Newtonsoft.Json.Formatting.None).Replace(JsonConvert.SerializeObject("bili_user"), "u");
            return "<html><head><title>" + WebUtility.HtmlEncode(keyword)
                + "-哔哩哔哩_bilibili</title><link rel=\"canonical\" href=\""
                + WebUtility.HtmlEncode("https://search.bilibili.com/upuser?keyword=" + Uri.EscapeDataString(keyword))
                + "\"></head><script>window.__pinia=(function(u){return " + body + "}(\"bili_user\"))</script></html>";
        }
        private static Dictionary<string, string> Query(HttpRequestMessage request)
            => request.RequestUri.Query.TrimStart('?').Split('&').Select(pair => pair.Split(new[] { '=' }, 2))
                .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]));
        private static void Stub(Func<Dictionary<string, string>, string> website,
            Func<Dictionary<string, string>, JObject> api = null, int websiteStatus = 200, int apiStatus = 200)
        {
            Transport.Reset((request, token) =>
            {
                Check(request.Method.Method == "GET", "every user search request is read-only");
                if (request.RequestUri.Host == "search.bilibili.com")
                {
                    Check(request.RequestUri.AbsolutePath == "/upuser", "user website uses official upuser route");
                    return Task.FromResult(new HttpResponseMessage { StatusCode = websiteStatus,
                        Content = new HttpStringContent(website == null ? "unavailable" : website(Query(request))) });
                }
                if (request.RequestUri.Host == "www.bilibili.com")
                {
                    var bootstrap = new HttpResponseMessage { Content = new HttpStringContent("discarded") };
                    bootstrap.Headers["Set-Cookie"] = "buvid3=server-issued-fixture; Domain=.bilibili.com; Path=/; Secure";
                    return Task.FromResult(bootstrap);
                }
                if (request.RequestUri.AbsolutePath == "/x/web-interface/nav") return Transport.Json(Nav.ToString());
                Check(request.RequestUri.Host == "api.bilibili.com"
                    && request.RequestUri.AbsolutePath == "/x/web-interface/wbi/search/type", "no user search requests an uploader archive or video endpoint");
                var query = Query(request);
                Check(query["search_type"] == "bili_user" && query["order"] == "0", "typed API requests relevant users");
                Check(query.ContainsKey("w_rid") && query.ContainsKey("wts"), "API requests are signed by production WBI signer");
                return Transport.Json((api == null ? new JObject { ["code"] = -352 } : api(query)).ToString(), apiStatus);
            });
        }
        private static async Task Error(Func<Task> operation, int code)
        {
            try { await operation(); }
            catch (BilibiliApiException ex) { Check(ex.Code == code, "an explicit user-search failure retains the service error code"); return; }
            throw new Exception("FAIL: expected user-search error");
        }
        public static async Task<string> Run()
        {
            foreach (var keyword in new[] { "小", "TNTxiaoluo", "hello & user" })
            {
                Stub(query => { Check(query["keyword"] == keyword, "keyword preserved without exact-name conversion"); return Html(keyword, Data(int.Parse(query["page"]))); });
                using (var client = new BilibiliClient())
                {
                    var first = await client.SearchUsersAsync(" " + keyword + " ");
                    var second = await client.SearchUsersAsync(keyword, 2);
                    Check(first.Users.Count == 20 && first.HasMore && second.Users.Count == 1 && !second.HasMore
                        && first.Total == 21, "real user ranks page independently of uploader videos");
                    Check(first.Users.Concat(second.Users).Select(user => user.Mid).Distinct().Count() == 21,
                        "page two contains distinct actual user IDs");
                    Check(first.Users.All(user => user.Name.StartsWith("小明相关用户")), "related users are retained even when their names do not exactly match the input");
                    Check(Transport.Requests.All(request => request.Uri.Host == "search.bilibili.com"),
                        "successful user website needs no account lookup or API request");
                }
            }
            Stub(query => { var data = Data(total: 1); data["result"] = new JArray(User()); return Html("小", data); });
            using (var client = new BilibiliClient())
            {
                var user = (await client.SearchUsersAsync("小")).Users.Single();
                Check(user.Mid == 123 && user.Name == "小明 & Xbox" && user.Signature == "介绍 & 签名", "plain user name, signature and exact MID preserved");
                Check(user.AvatarUrl == "https://i0.hdslb.com/bfs/face/public.webp"
                    && user.FollowersText == "1.2万粉丝" && user.VideoCountText == "0个视频", "avatar, followers and zero uploads render as users, not play counts");
            }
            Stub(query => Html("无用户", Data(total: 0)));
            using (var client = new BilibiliClient())
            {
                var result = await client.SearchUsersAsync("无用户");
                Check(result.Users.Count == 0 && result.Total == 0 && !result.HasMore && result.Notice.Length == 0,
                    "verified empty user results are distinct from failed requests");
            }
            Stub(null, query => Envelope(Data(int.Parse(query["page"]), 10, 11)), websiteStatus: 412);
            using (var client = new BilibiliClient())
            {
                var first = await client.SearchUsersAsync("小");
                var second = await client.SearchUsersAsync("小", 2);
                Check(first.Users.Count == 10 && first.HasMore && second.Users.Count == 1 && !second.HasMore,
                    "first-page fallback keeps the API's actual page size");
                Check(Transport.Requests.Count(item => item.Uri.Host == "search.bilibili.com") == 1,
                    "later API user pages do not splice website ranking");
                Check(Transport.Requests.Where(item => item.Uri.Query.Contains("search_type=bili_user"))
                    .Last().Uri.Query.Contains("page_size=10"), "later page requests preserve real page size");
            }
            Stub(query => query["page"] == "1" ? Html("小", Data()) : "unavailable");
            using (var client = new BilibiliClient())
            {
                await client.SearchUsersAsync("小");
                await Error(() => client.SearchUsersAsync("小", 2), -1);
                Check(Transport.Requests.All(item => item.Uri.Host == "search.bilibili.com"),
                    "failed website page two never silently switches result order");
            }
            Stub(query => Html("小", Data()));
            using (var client = new BilibiliClient())
            {
                await Error(() => client.SearchUsersAsync("小", 2), -1);
                Check(Transport.Requests.Count == 0, "starting at page two requires a real search session");
                await client.SearchUsersAsync("小");
                await Error(() => client.SearchUsersAsync("小", 2), -1);
            }
            Stub(null, websiteStatus: 412, apiStatus: 412);
            using (var client = new BilibiliClient()) await Error(() => client.SearchUsersAsync("小"), 412);
            Stub(null, websiteStatus: 503);
            using (var client = new BilibiliClient()) await Error(() => client.SearchUsersAsync("小"), -352);

            foreach (var mutation in new Action<JObject>[]
            {
                data => data["page"] = 2, data => data["pagesize"] = 0, data => data["pagesize"] = 1000,
                data => data["numResults"] = -1, data => data["numPages"] = JValue.CreateNull(),
                data => data["result"] = JValue.CreateNull(), data => data["result"] = new JArray(),
                data => data["result"] = new JArray("invalid"),
                data => data["result"][0]["mid"] = 0, data => data["result"][0]["uname"] = " ",
                data => data["result"][0]["type"] = "video"
            })
            {
                Stub(null, query => { var data = Data(); mutation(data); return Envelope(data); }, websiteStatus: 503);
                using (var client = new BilibiliClient()) await Error(() => client.SearchUsersAsync("小"), -1);
            }
            Stub(null, query => Envelope(Data(int.Parse(query["page"]), query["page"] == "1" ? 10 : 20, 21)), websiteStatus: 503);
            using (var client = new BilibiliClient())
            { await client.SearchUsersAsync("小"); await Error(() => client.SearchUsersAsync("小", 2), -1); }

            var original = Html("小", Data());
            foreach (var html in new[] { original.Replace("/upuser?", "/video?"), original.Replace("keyword=%E5%B0%8F", "keyword=other"),
                original.Replace("return {", "return alert(1);{"), original.Replace("}(\"bili_user\"))", "}(alert(1)))") })
            {
                try { WebsiteSearchParser.ParseUsers(html, "小", 1); throw new Exception("FAIL: invalid user SSR accepted"); }
                catch (BilibiliApiException ex) { Check(ex.Code == -1, "serialized user page is never evaluated as JavaScript or accepted for another route/query"); }
            }
            using (var client = new BilibiliClient())
            {
                var canceled = new CancellationTokenSource(); canceled.Cancel();
                int requests = Transport.Requests.Count;
                try { await client.SearchUsersAsync("小", ct: canceled.Token); throw new Exception("FAIL: canceled user search executed"); }
                catch (OperationCanceledException) { Check(Transport.Requests.Count == requests, "pre-canceled search performs no HTTP"); }
                foreach (var keyword in new[] { "", " ", new string('x', 101) })
                {
                    try { await client.SearchUsersAsync(keyword); throw new Exception("FAIL: invalid input accepted"); }
                    catch (ArgumentException) { Check(Transport.Requests.Count == requests, "invalid keyword performs no HTTP"); }
                }
            }
            return "PASS: " + _passed + " production user search, server pagination, real MID, optional metadata and safe website parsing assertions.";
        }

        public static string ParseOfficial(string html, string keyword, string secondHtml)
        {
            // Parse the previously captured public website through the production
            // transport method, not a second implementation of its data mapping.
            Stub(query => query["page"] == "2" && secondHtml != null ? secondHtml : html);
            using (var client = new BilibiliClient())
            {
                var result = client.SearchUsersAsync(keyword).GetAwaiter().GetResult();
                var second = secondHtml == null ? null : client.SearchUsersAsync(keyword, 2).GetAwaiter().GetResult();
                return new JObject { ["Source"] = "Official public upuser SSR", ["Count"] = result.Users.Count,
                    ["Total"] = result.Total, ["HasMore"] = result.HasMore,
                    ["FirstUserMid"] = result.Users.FirstOrDefault()?.Mid ?? 0,
                    ["FirstUserName"] = result.Users.FirstOrDefault()?.Name ?? "",
                    ["SecondPageCount"] = second?.Users.Count ?? 0,
                    ["UniqueAcrossPages"] = result.Users.Concat(second?.Users ?? Array.Empty<UserSearchItem>()).Select(item => item.Mid).Distinct().Count()
                }.ToString();
            }
        }
    }
}
