// TEST ONLY. Exercises the normal video search separately from explicit uploader mode.
namespace BiliXbox.SearchTests
{
    using BiliXbox.Services;
    using BiliXbox.VideoDetailTests;
    public static partial class Tests
    {
        private static JObject KeywordData(string keyword, int page, int size = 42, int total = 84)
        {
            return new JObject
            {
                ["page"] = page, ["pagesize"] = size, ["numResults"] = total,
                ["numPages"] = total == 0 ? 0 : (total + size - 1) / size,
                ["result"] = new JArray(Enumerable.Range(1, Math.Max(0, Math.Min(size, total - (page - 1) * size)))
                    .Select(i => new JObject
                    {
                        ["type"] = "video", ["aid"] = 100000000000L + (page - 1) * size + i,
                        ["bvid"] = "BV" + ((page - 1) * size + i).ToString("0000000000", CultureInfo.InvariantCulture),
                        ["title"] = keyword + " &amp; 相关视频 " + i, ["author"] = "Author " + (i % 2),
                        ["pic"] = "//i1.hdslb.com/bfs/archive/public.jpg", ["play"] = 1234, ["duration"] = "1:37"
                    }))
            };
        }

        private static string WebsiteHtml(string keyword, JObject data)
        {
            var root = new JObject
            {
                ["searchResponse"] = new JObject { ["searchAllResponse"] = new JObject
                {
                    ["result"] = new JArray(new JObject { ["result_type"] = "bili_user", ["data"] = new JArray(User()) })
                }},
                ["searchTypeResponse"] = new JObject { ["searchTypeResponse"] = data.DeepClone() }
            };
            var bvid = data["result"]?.First?["bvid"]?.Value<string>() ?? "BV1xx411c7mD";
            var author = data["result"]?.First?["author"]?.Value<string>() ?? "作者";
            var body = root.ToString(Newtonsoft.Json.Formatting.None)
                .Replace(JsonConvert.SerializeObject(bvid), "a").Replace(JsonConvert.SerializeObject(author), "A");
            return "<!doctype html><html><head><title>" + WebUtility.HtmlEncode(keyword)
                + "-哔哩哔哩_bilibili</title><link rel=\"canonical\" href=\""
                + WebUtility.HtmlEncode("https://search.bilibili.com/video?keyword=" + Uri.EscapeDataString(keyword))
                + "\"></head><script type=\"text/javascript\">window.__pinia=(function(a,A,z){return "
                + body + "}(" + JsonConvert.SerializeObject(bvid) + "," + JsonConvert.SerializeObject(author)
                + ",void 0))</script></html>";
        }

        private static void ParserError(string html, string keyword = "yuanshe", int page = 1)
        {
            try { WebsiteSearchParser.Parse(html, keyword, page); }
            catch (BilibiliApiException ex) { Check(ex.Code == -1, "invalid serialized search data is an explicit format error"); return; }
            throw new Exception("FAIL: invalid serialized search data accepted");
        }

        private static async Task RunKeywordSearch()
        {
            foreach (var keyword in new[] { "yuanshe", "原神", "TNTxiaoluo" })
            {
                Stub(User(), websiteStatus: 200, websiteResponder: query =>
                {
                    Check(query["keyword"] == keyword, "normal keyword is transmitted unchanged, including Chinese and uploader-like names");
                    return WebsiteHtml(keyword, KeywordData(keyword, int.Parse(query["page"])));
                });
                using (var client = new BilibiliClient())
                {
                    var first = await client.SearchPageAsync(keyword);
                    var second = await client.SearchPageAsync(keyword, 2);
                    Check(first.Videos.Count == 42 && first.HasMore && second.Videos.Count == 42 && !second.HasMore,
                        "normal video results keep real desktop page size and server page count");
                    Check(first.MatchedCreatorName.Length == 0 && first.Videos.Select(item => item.Owner).Distinct().Count() == 2,
                        "normal video search never silently becomes an exact-account archive");
                    Check(first.Videos.Concat(second.Videos).Select(item => item.Bvid).Distinct().Count() == 84,
                        "two website pages contain 84 distinct server-provided video ranks");
                    Check(Transport.Requests.All(item => item.Uri.Host == "search.bilibili.com"),
                        "working website results need no homepage bootstrap, WBI nav, user lookup or archive call");
                    Check(first.Videos[0].Title.Contains("& 相关视频") && first.Videos[0].CoverUrl.StartsWith("https://")
                        && first.Videos[0].Bvid == "BV0000000001" && first.Videos[0].Owner == "Author 1",
                        "case-sensitive serialized aliases, Unicode entities and actual metadata survive parsing");
                }
            }
            Stub(User(), websiteStatus: 503, videoResponder: query =>
            {
                var page = int.Parse(query["page"]);
                Check(query["page_size"] == (page == 1 ? "42" : "20"),
                    "API compatibility retains its actual first-page size if the server ignores requested 42");
                return Envelope(KeywordData("yuanshe", page, 20, 40));
            });
            using (var client = new BilibiliClient())
            {
                var first = await client.SearchPageAsync("yuanshe");
                var second = await client.SearchPageAsync("yuanshe", 2);
                Check(first.Videos.Count == 20 && second.Videos.Count == 20 && !second.HasMore
                    && first.Videos.Concat(second.Videos).Select(item => item.Bvid).Distinct().Count() == 40,
                    "website-to-API first-page compatibility keeps all ranks exactly once");
                Check(Transport.Requests.Count(item => item.Uri.Host == "search.bilibili.com") == 1
                    && !Transport.Requests.Any(item => item.Uri.Query.Contains("search_type=bili_user")),
                    "subsequent API pages stay on API and never query an uploader");
            }
            Stub(User(), websiteStatus: 200, websiteResponder: query => query["page"] == "1"
                ? WebsiteHtml("yuanshe", KeywordData("yuanshe", 1)) : "unavailable");
            using (var client = new BilibiliClient())
            {
                await client.SearchPageAsync("yuanshe");
                await ExpectError(() => client.SearchPageAsync("yuanshe", 2), -1);
                Check(!Transport.Requests.Any(item => item.Uri.Host == "api.bilibili.com"),
                    "a failed second website page is not spliced with incompatible API ranks");
            }
            Stub(User(), websiteStatus: 200, websiteResponder: query => WebsiteHtml("yuanshe", KeywordData("yuanshe", 1)));
            using (var client = new BilibiliClient())
            {
                await client.SearchPageAsync("yuanshe");
                await ExpectError(() => client.SearchPageAsync("yuanshe", 2), -1);
                Check(Transport.Requests.Count == 2, "stale page one returned for page two is rejected without retry or fallback");
            }
            Stub(User(), websiteStatus: 503, videoResponder: query => Envelope(KeywordData("yuanshe", int.Parse(query["page"]),
                query["page"] == "1" ? 20 : 42)));
            using (var client = new BilibiliClient())
            {
                await client.SearchPageAsync("yuanshe");
                await ExpectError(() => client.SearchPageAsync("yuanshe", 2), -1);
            }
            Stub(User(), websiteStatus: 200, websiteResponder: query => WebsiteHtml("no videos", KeywordData("no videos", 1, total: 0)));
            using (var client = new BilibiliClient())
            {
                var empty = await client.SearchPageAsync("no videos");
                Check(empty.Videos.Count == 0 && empty.Total == 0 && !empty.HasMore && empty.Notice.Length == 0,
                    "a verified genuine empty keyword response remains a normal empty state");
            }
            Stub(User(), websiteStatus: 200, websiteResponder: query => WebsiteHtml("TNTxiaoluo", KeywordData("TNTxiaoluo", 1)));
            using (var client = new BilibiliClient())
                Check((await client.SearchAsync("TNTxiaoluo")).Count == 42,
                    "legacy video-search wrapper keeps normal relevance semantics");

            var html = WebsiteHtml("yuanshe", KeywordData("yuanshe", 1));
            ParserError(html, "原神");
            ParserError(html, page: 2);
            ParserError(html.Replace("https://search.bilibili.com/video?keyword=yuanshe", "https://example.com/video?keyword=yuanshe"));
            ParserError(html.Replace("keyword=yuanshe", "keyword=other"));
            ParserError(html.Replace("\"page\":1", "\"page\":unknown"));
            ParserError(html.Replace("\"page\":1", "\"page\":alert(1)"));
            ParserError(html.Replace("void 0))", "{}))"));
            ParserError(html.Replace("void 0))", "alert(1)))"));
            ParserError(html.Replace("))</script>", "));alert(1)</script>"));
            ParserError(html.Replace("{\"searchResponse\":", "{\"dup\":0,\"dup\":1,\"searchResponse\":"));
            ParserError(html.Replace("{\"searchResponse\":", "{\"deep\":" + new string('[', 65) + "0" + new string(']', 65) + ",\"searchResponse\":"));
            ParserError(WebsiteHtml("yuanshe", KeywordData("yuanshe", 1)).Replace("\"searchTypeResponse\":{\"searchTypeResponse\":", "\"searchTypeResponse\":0,\"removed\":{\"searchTypeResponse\":"));
            ParserError(html.Replace("\"pagesize\":42", "\"pagesize\":1000"));
            ParserError(new string(' ', 6 * 1024 * 1024 + 1));
            var aliasOverflow = string.Join(",", Enumerable.Range(1, 5001).Select(i => "v" + i));
            ParserError(html.Replace("function(a,A,z)", "function(" + aliasOverflow + ")"));
        }
    }
}
