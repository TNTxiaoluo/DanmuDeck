// TEST ONLY. Compiles production search logic with deterministic public-API transport.
namespace BiliXbox.SearchTests
{
    using BiliXbox.Services;
    using BiliXbox.VideoDetailTests;
    public static partial class Tests
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
        private static JObject Envelope(JObject data) => new JObject { ["code"] = 0, ["data"] = data };
        private static JObject Upload(string bvid = "BV1YdhH6AEF5") => new JObject
        {
            ["bvid"] = bvid, ["aid"] = 117316897475933L, ["title"] = "<em class=\"keyword\">iOS27</em> &amp; Xbox",
            ["pic"] = "//i1.hdslb.com/bfs/archive/public-preview.jpg", ["play"] = "3458", ["duration"] = "4:48"
        };
        private static JObject User(string name = "TNTxiaoluo", bool previews = true) => new JObject
        {
            ["mid"] = 396323748, ["uname"] = name, ["videos"] = 21,
            ["res"] = previews ? new JArray(Upload(), Upload("BV1ES8b64EM3"), Upload("BV1m7836sE21")) : new JArray()
        };
        private static Dictionary<string, string> Query(HttpRequestMessage request)
            => request.RequestUri.Query.TrimStart('?').Split('&').Select(pair => pair.Split(new[] { '=' }, 2))
                .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]));
        private static void Stub(JObject user, JObject videos = null, JObject space = null,
            int spaceStatus = 200, int userCode = 0, JObject playlist = null,
            Func<Dictionary<string, string>, JObject> playlistResponder = null,
            Func<Dictionary<string, string>, string> websiteResponder = null, int websiteStatus = 503,
            Func<Dictionary<string, string>, JObject> videoResponder = null,
            Func<Dictionary<string, string>, JObject> spaceResponder = null)
        {
            Transport.Reset((request, token) =>
            {
                Check(request.Method.Method == "GET", "all search requests are read-only");
                if (request.RequestUri.Host == "search.bilibili.com")
                    return Task.FromResult(new HttpResponseMessage
                    {
                        StatusCode = websiteStatus,
                        Content = new HttpStringContent(websiteResponder == null ? "Unavailable" : websiteResponder(Query(request)))
                    });
                if (request.RequestUri.Host == "www.bilibili.com")
                {
                    var bootstrap = new HttpResponseMessage { Content = new HttpStringContent("discarded page") };
                    bootstrap.Headers["Set-Cookie"] = "buvid3=server-issued-fixture; Domain=.bilibili.com; Path=/; Secure";
                    return Task.FromResult(bootstrap);
                }
                if (request.RequestUri.AbsolutePath == "/x/web-interface/nav") return Transport.Json(Nav.ToString());
                Check(request.RequestUri.Host == "api.bilibili.com", "search uses Bilibili's own public endpoint");
                var query = Query(request);
                if (request.RequestUri.AbsolutePath == "/x/v2/medialist/resource/list")
                {
                    Check(request.Headers.Referer != null && request.Headers.Referer.AbsoluteUri == "https://space.bilibili.com/396323748/video",
                        "public archive carries the selected uploader video-page referer");
                    Check(query["type"] == "1" && query["biz_id"] == "396323748" && query["ps"] == "30"
                        && query["desc"] == "true" && query["direction"] == "false",
                        "normal public uploader playback list requests descending uploader archive");
                    Check(!query.ContainsKey("appkey") && !query.ContainsKey("sign"),
                        "public uploader playlist requires no app secret or impersonated client key");
                    return Transport.Json((playlistResponder != null ? playlistResponder(query)
                        : playlist ?? new JObject { ["code"] = -404 }).ToString());
                }
                Check(query.ContainsKey("wts") && query.ContainsKey("w_rid"), "search and archives carry real WBI signatures");
                if (request.RequestUri.AbsolutePath == "/x/space/wbi/arc/search")
                {
                    Check(query["mid"] == "396323748" && query["ps"] == "30"
                        && query["order"] == "pubdate", "resolved account ID and newest archive order");
                    Check(request.Headers.Referer != null && request.Headers.Referer.AbsoluteUri == "https://space.bilibili.com/396323748/video",
                        "signed archive carries the selected uploader video-page referer");
                    return Transport.Json((spaceResponder != null ? spaceResponder(query) : space ?? new JObject { ["code"] = -352 }).ToString(), spaceStatus);
                }
                Check(request.RequestUri.AbsolutePath == "/x/web-interface/wbi/search/type", "typed website search route");
                if (query["search_type"] == "bili_user")
                {
                    Check(query["page"] == "1", "uploader resolution always uses first user-result page");
                    if (userCode != 0) return Transport.Json(new JObject { ["code"] = userCode }.ToString());
                    return Transport.Json(Envelope(new JObject
                    {
                        ["numResults"] = user == null ? 0 : 1,
                        ["result"] = user == null ? new JArray() : new JArray(user)
                    }).ToString());
                }
                Check(query["search_type"] == "video", "ordinary keyword search stays video typed");
                return Transport.Json((videoResponder != null ? videoResponder(query) : videos
                    ?? Envelope(new JObject { ["page"] = int.Parse(query["page"]), ["pagesize"] = int.Parse(query["page_size"]),
                        ["numResults"] = 0, ["numPages"] = 0, ["result"] = new JArray() })).ToString());
            });
        }
        private static JObject Archive(int count, params JObject[] uploads) => Envelope(new JObject
        {
            ["list"] = new JObject { ["vlist"] = new JArray(uploads) },
            ["page"] = new JObject { ["ps"] = 30, ["count"] = count }
        });
        private static JObject MediaUpload(int index, long mid = 396323748) => new JObject
        {
            ["id"] = 110000000000000L + index, ["bv_id"] = "BV" + index.ToString("0000000000", CultureInfo.InvariantCulture),
            ["title"] = "Upload " + index + " &amp; Xbox", ["type"] = 2,
            ["cover"] = "http://i1.hdslb.com/bfs/archive/public-media.jpg", ["duration"] = 97,
            ["upper"] = new JObject { ["mid"] = mid, ["name"] = "TNTxiaoluo" },
            ["cnt_info"] = new JObject { ["play"] = 12345 }
        };
        private static JObject MediaPage(long count, JArray uploads, bool more = true) => Envelope(new JObject
        {
            ["media_list"] = uploads, ["has_more"] = more, ["total_count"] = count
        });
        private static async Task ExpectError(Func<Task> run, int code)
        {
            try { await run(); }
            catch (BilibiliApiException ex) { Check(ex.Code == code, "restricted response preserves diagnostic code"); return; }
            throw new Exception("FAIL: restricted response disguised as no results");
        }
        // Explicitly invoked read-only integration probe. The service, signer,
        // parsing and in-memory cookie jar are production code. Only WinRT's
        // HTTP projection is replaced with .NET HTTP, so this does not prove
        // console TLS or system input behavior. It loads no account storage.
        public static async Task<string> RunLivePublicSearch(string keyword, bool creatorMode = false,
            int pages = 1, bool summary = false)
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
                        || (request.RequestUri.Host != "api.bilibili.com" && request.RequestUri.Host != "www.bilibili.com"
                            && request.RequestUri.Host != "search.bilibili.com"))
                        throw new InvalidOperationException("The public search probe permits only Bilibili GET requests.");
                    using (var outgoing = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, request.RequestUri))
                    {
                        outgoing.Headers.TryAddWithoutValidation("User-Agent", BilibiliClient.UserAgent);
                        outgoing.Headers.Referrer = new Uri(BilibiliClient.Referer);
                        outgoing.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
                        foreach (var header in request.Headers)
                            outgoing.Headers.TryAddWithoutValidation(header.Key, header.Value);
                        using (var response = await http.SendAsync(outgoing, token))
                        {
                            var incoming = new HttpResponseMessage
                            {
                                StatusCode = (int)response.StatusCode,
                                Content = new HttpStringContent(await response.Content.ReadAsStringAsync())
                            };
                            IEnumerable<string> cookies;
                            if (response.Headers.TryGetValues("Set-Cookie", out cookies))
                                incoming.Headers["Set-Cookie"] = string.Join(",", cookies);
                            return incoming;
                        }
                    }
                });
                using (var client = new BilibiliClient())
                {
                    var output = new JArray();
                    var unique = new HashSet<string>(StringComparer.Ordinal);
                    var received = 0;
                    for (var page = 1; page <= pages; page++)
                    {
                        var result = creatorMode ? await client.SearchCreatorPageAsync(keyword, page)
                            : await client.SearchPageAsync(keyword, page);
                        received += result.Videos.Count;
                        foreach (var video in result.Videos) unique.Add(video.Bvid);
                        output.Add(new JObject
                        {
                            ["Page"] = page, ["Count"] = result.Videos.Count,
                            ["MatchedCreatorName"] = result.MatchedCreatorName,
                            ["HasMore"] = result.HasMore, ["Total"] = result.Total, ["Notice"] = result.Notice,
                            ["Videos"] = new JArray((summary ? result.Videos.Take(3) : result.Videos).Select(item => new JObject
                            {
                                ["Bvid"] = item.Bvid, ["Title"] = item.Title,
                                ["Owner"] = item.Owner, ["DurationText"] = item.DurationText
                            }))
                        });
                        if (!result.HasMore) break;
                    }
                    // Public metadata only. Never print cookies, login keys or
                    // signed URLs. Summary limits displayed samples, not counts.
                    return new JObject
                    {
                        ["Mode"] = creatorMode ? "Creator" : "Video", ["Keyword"] = keyword,
                        ["Received"] = received, ["UniqueAcrossPages"] = unique.Count,
                        ["WebsiteRequests"] = Transport.Requests.Count(item => item.Uri.Host == "search.bilibili.com"),
                        ["UserLookupRequests"] = Transport.Requests.Count(item => item.Uri.Query.Contains("search_type=bili_user")),
                        ["Pages"] = output
                    }.ToString();
                }
            }
        }
        public static async Task<string> Run()
        {
            _passed = 0;
            await RunKeywordSearch();
            Stub(User(), playlist: MediaPage(21, new JArray(Enumerable.Range(1, 21).Select(i => MediaUpload(i)))));
            using (var client = new BilibiliClient())
            {
                var result = await client.SearchCreatorPageAsync("TNTxiaoluo");
                Check(result.Videos.Count == 21 && result.Total == 21 && !result.HasMore && result.Notice.Length == 0,
                    "full public archive supersedes three preview videos and ends at actual total despite stale has_more");
                Check(result.Videos[0].Title == "Upload 1 & Xbox" && result.Videos[0].DurationText == "1:37"
                    && result.Videos[0].PlayText == "1.2万次播放" && result.Videos[0].CoverUrl.StartsWith("https://", StringComparison.Ordinal)
                    && result.Videos.All(item => item.Owner == "TNTxiaoluo" && item.Aid > 0),
                    "playlist canonical BV/id, owner, cover, count, entity-decoded title and seconds map correctly");
                Check(!Transport.Requests.Any(item => item.Uri.AbsolutePath == "/x/space/wbi/arc/search"),
                    "working public playlist does not request the separately restricted archive");
            }
            Stub(User(), playlistResponder: query =>
            {
                if (!query.ContainsKey("oid"))
                {
                    Check(query["with_current"] == "true", "initial playlist starts at newest upload");
                    return MediaPage(61, new JArray(Enumerable.Range(1, 30).Select(i => MediaUpload(i))));
                }
                Check(query["with_current"] == "false", "later playlist pages exclude their boundary upload");
                if (query["oid"] == "110000000000030")
                    return MediaPage(61, new JArray(Enumerable.Range(31, 30).Select(i => MediaUpload(i))));
                Check(query["oid"] == "110000000000060", "third page uses second page's genuine final AV id");
                return MediaPage(61, new JArray(MediaUpload(61)));
            });
            using (var client = new BilibiliClient())
            {
                var first = await client.SearchCreatorPageAsync("TNTxiaoluo");
                var second = await client.SearchCreatorPageAsync("TNTxiaoluo", 2);
                var third = await client.SearchCreatorPageAsync("TNTxiaoluo", 3);
                Check(first.HasMore && second.HasMore && !third.HasMore && third.Videos.Count == 1,
                    "61 genuine uploads expose exactly three cursor pages, with no fourth empty page");
                Check(first.Videos.Concat(second.Videos).Concat(third.Videos).Select(item => item.Bvid).Distinct().Count() == 61,
                    "cursor pages cover every upload exactly once");
                var back = await client.SearchCreatorPageAsync("TNTxiaoluo", 2);
                Check(back.Videos.Select(item => item.Bvid).SequenceEqual(second.Videos.Select(item => item.Bvid)),
                    "going to previous page reuses that page's cursor rather than repeating newest uploads");
            }
            var foreign = MediaUpload(1, 12345);
            Stub(User(), spaceStatus: 412, playlist: MediaPage(1, new JArray(foreign)));
            using (var client = new BilibiliClient())
            {
                var result = await client.SearchCreatorPageAsync("TNTxiaoluo");
                Check(result.Videos.Count == 1 && result.Notice.Length == 0
                    && result.Videos.Single().Bvid == foreign["bv_id"].ToString()
                    && result.Videos.Single().OwnerMid == 12345,
                    "MID-scoped collaborative archive retains a video's different primary uploader without changing attribution");
                Check(!Transport.Requests.Any(item => item.Uri.AbsolutePath == "/x/space/wbi/arc/search"),
                    "valid collaborative item does not discard the selected account archive or trigger fallback");
            }
            Stub(User(), playlist: MediaPage(0, new JArray(MediaUpload(1))));
            using (var client = new BilibiliClient())
                Check(!(await client.SearchCreatorPageAsync("TNTxiaoluo")).HasMore,
                    "short final page without a positive total does not loop on stale has_more");
            Stub(User(), spaceStatus: 412, playlist: MediaPage(31,
                new JArray(Enumerable.Range(1, 30).Select(i => MediaUpload(i)))));
            using (var client = new BilibiliClient())
            {
                Check((await client.SearchCreatorPageAsync("TNTxiaoluo")).HasMore,
                    "a genuine total beyond the first batch exposes the next page");
                await ExpectError(() => client.SearchCreatorPageAsync("TNTxiaoluo", 2), -1);
                Check(Transport.Requests.Count(item => item.Uri.AbsolutePath == "/x/v2/medialist/resource/list") == 2,
                    "a repeated first batch is rejected once rather than looping or duplicating uploads");
                Check(!Transport.Requests.Any(item => item.Uri.AbsolutePath == "/x/space/wbi/arc/search"),
                    "malformed second media page cannot switch archive source and silently skip uploads");
            }
            foreach (var status in new[] { 200, 412 })
            {
                Stub(User(), spaceStatus: status);
                using (var client = new BilibiliClient())
                {
                    var result = await client.SearchCreatorPageAsync("TNTxiaoluo");
                    Check(result.MatchedCreatorName == "TNTxiaoluo" && result.Videos.Count == 3,
                        "UP-name search displays real embedded recent uploads despite restricted archive");
                    Check(result.Videos.Select(item => item.Bvid).SequenceEqual(new[]
                        { "BV1YdhH6AEF5", "BV1ES8b64EM3", "BV1m7836sE21" }), "real preview IDs survive mapping in server order");
                    Check(result.Videos.All(item => item.Owner == "TNTxiaoluo")
                        && result.Videos[0].Title == "iOS27 & Xbox"
                        && result.Videos[0].CoverUrl.StartsWith("https://", StringComparison.Ordinal)
                        && result.Videos[0].DurationText == "4:48", "owner fallback, HTML decoding, secure images and duration survive");
                    Check(!result.HasMore && result.Notice.Contains("3") && result.Notice.Contains("完整投稿列表"),
                        "partial previews do not imply complete archive or repeated pagination");
                    Check(!Transport.Requests.Any(item => item.Uri.Query.Contains("search_type=video"))
                        && Transport.Requests.Count(item => item.Uri.AbsolutePath == "/x/space/wbi/arc/search") == 1,
                        "exact uploader never replaced by unrelated title recommendations or automatic retry");
                }
            }
            Stub(User(), space: Archive(31, Upload()));
            using (var client = new BilibiliClient())
            {
                var first = await client.SearchCreatorPageAsync(" tntXIAOLUO ");
                Check(first.MatchedCreatorName == "TNTxiaoluo" && first.HasMore && first.Notice.Length == 0,
                    "case-insensitive exact name uses complete archive and server total");
                var second = await client.SearchCreatorPageAsync("TNTxiaoluo", 2);
                Check(second.Videos.Count == 1 && !second.HasMore, "last archive page uses page count");
                Check(Transport.Requests.Any(item => item.Uri.Query.Contains("pn=2")), "page two transmitted to uploader archive");
                Check(Transport.Requests.Count(item => item.Uri.AbsolutePath == "/x/v2/medialist/resource/list") == 1,
                    "a successful WBI archive first page fixes later pages to that source");
            }
            var sourcePhase = 0;
            Stub(User(), playlistResponder: query => sourcePhase == 0
                ? MediaPage(31, new JArray(Enumerable.Range(1, 30).Select(i => MediaUpload(i))))
                : sourcePhase == 4 ? MediaPage(1, new JArray(MediaUpload(80)), false)
                : new JObject { ["code"] = sourcePhase == 1 ? -352 : -404 },
                spaceResponder: query => Archive(31, Upload(query["pn"] == "1" ? "BV1YdhH6AEF5" : "BV1ES8b64EM3")));
            using (var client = new BilibiliClient())
            {
                Check((await client.SearchCreatorPageAsync("TNTxiaoluo")).HasMore, "first media page establishes a genuine continuation");
                sourcePhase = 1;
                await ExpectError(() => client.SearchCreatorPageAsync("TNTxiaoluo", 2), -352);
                Check(!Transport.Requests.Any(item => item.Uri.AbsolutePath == "/x/space/wbi/arc/search"),
                    "second media page verification failure never switches to page-number archive");
                sourcePhase = 2;
                var refreshed = await client.SearchCreatorPageAsync("TNTxiaoluo");
                Check(refreshed.Videos.Single().Bvid == "BV1YdhH6AEF5", "explicit first-page refresh may select the currently usable WBI source");
                var mediaBefore = Transport.Requests.Count(item => item.Uri.AbsolutePath == "/x/v2/medialist/resource/list");
                sourcePhase = 3;
                var later = await client.SearchCreatorPageAsync("TNTxiaoluo", 2);
                Check(later.Videos.Single().Bvid == "BV1ES8b64EM3" && !later.HasMore,
                    "new WBI continuation preserves page number and different server video");
                Check(Transport.Requests.Count(item => item.Uri.AbsolutePath == "/x/v2/medialist/resource/list") == mediaBefore,
                    "later WBI page does not probe the previously failed media source");
                var archiveBefore = Transport.Requests.Count(item => item.Uri.AbsolutePath == "/x/space/wbi/arc/search");
                sourcePhase = 4;
                var refreshedAgain = await client.SearchCreatorPageAsync("TNTxiaoluo");
                Check(refreshedAgain.Videos.Single().Aid == 110000000000080L
                    && Transport.Requests.Count(item => item.Uri.AbsolutePath == "/x/space/wbi/arc/search") == archiveBefore,
                    "another explicit first-page refresh may return to recovered media source");
            }
            var secondUpload = Upload("BV1ES8b64EM3"); secondUpload.Remove("duration"); secondUpload["length"] = "4:59";
            Stub(User(), space: Archive(2, Upload(), secondUpload));
            using (var client = new BilibiliClient())
                Check((await client.SearchCreatorPageAsync("TNTxiaoluo")).Videos[1].DurationText == "4:59", "archive length string supported");
            Stub(User("TNTxiaoluoFan"));
            using (var client = new BilibiliClient())
            {
                var result = await client.SearchCreatorPageAsync("TNTxiaoluo");
                Check(result.MatchedCreatorName.Length == 0 && result.Videos.Count == 0 && result.Notice.Length > 0,
                    "explicit uploader mode never substitutes a similarly named account");
                Check(!Transport.Requests.Any(item => item.Uri.AbsolutePath == "/x/space/wbi/arc/search"), "non-exact users never trigger unrelated archives");
            }
            Stub(User(), space: Archive(0));
            using (var client = new BilibiliClient())
            {
                var result = await client.SearchCreatorPageAsync("TNTxiaoluo");
                Check(result.Videos.Count == 3 && result.Notice.Length > 0, "contradictory empty archive does not erase real preview uploads");
            }
            Stub(User(previews: false), spaceStatus: 412);
            using (var client = new BilibiliClient())
                await ExpectError(() => client.SearchCreatorPageAsync("TNTxiaoluo"), 412);
            Stub(User(), spaceStatus: 412);
            using (var client = new BilibiliClient())
            {
                await ExpectError(() => client.SearchCreatorPageAsync("TNTxiaoluo", 2), -1);
                Check(!Transport.Requests.Any(item => item.Uri.AbsolutePath == "/x/v2/medialist/resource/list"
                    || item.Uri.AbsolutePath == "/x/space/wbi/arc/search"),
                    "page greater than one without established source cannot fabricate or skip a first page");
            }
            Stub(null, userCode: -352);
            using (var client = new BilibiliClient())
                await ExpectError(() => client.SearchCreatorPageAsync("TNTxiaoluo"), -352);
            Stub(null);
            using (var client = new BilibiliClient())
            {
                var result = await client.SearchCreatorPageAsync("no matching videos");
                Check(result.Videos.Count == 0 && result.Notice.Length > 0 && !result.HasMore,
                    "explicit uploader search reports when no exact account exists");
            }
            Stub(User(), space: Archive(3, Upload(), Upload(), Upload("")));
            using (var client = new BilibiliClient())
                Check((await client.SearchCreatorPageAsync("TNTxiaoluo")).Videos.Count == 1,
                    "explicit creator mode deduplicates real archive BV IDs");
            Stub(null);
            using (var client = new BilibiliClient())
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel();
                try { await client.SearchCreatorPageAsync("TNTxiaoluo", ct: cancel.Token); throw new Exception("FAIL: canceled search continued"); }
                catch (OperationCanceledException) { Check(Transport.Requests.Count == 0, "canceled request never renders stale search results"); }
            }
            return "PASS: " + _passed + " production related-video / explicit-uploader search checks, including website data parsing, source-stable paging, explicit UP lookup, error reporting and cancellation.";
        }
    }
}
