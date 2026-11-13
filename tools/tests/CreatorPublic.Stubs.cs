// Explicit opt-in anonymous public integration check. Never loads account storage.
namespace BiliXbox.CreatorTests
{
    using BiliXbox.Services;
    using BiliXbox.VideoDetailTests;

    public static class PublicProbe
    {
        public static JArray LastRequests { get; private set; } = new JArray();

        public static async Task<string> Run(long mid, int pages)
        {
            LastRequests = new JArray();
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
                        || request.RequestUri.Host != "api.bilibili.com" && request.RequestUri.Host != "www.bilibili.com")
                        throw new InvalidOperationException("Public creator probe permits only normal Bilibili GET requests.");
                    using (var outgoing = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, request.RequestUri))
                    {
                        outgoing.Headers.TryAddWithoutValidation("User-Agent", BilibiliClient.UserAgent);
                        outgoing.Headers.Referrer = request.Headers.Referer ?? new Uri(BilibiliClient.Referer);
                        outgoing.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
                        foreach (var header in request.Headers)
                            outgoing.Headers.TryAddWithoutValidation(header.Key, header.Value);
                        using (var response = await http.SendAsync(outgoing, token))
                        {
                            var text = await response.Content.ReadAsStringAsync();
                            var requestSummary = new JObject
                            {
                                ["Path"] = request.RequestUri.AbsolutePath, ["Status"] = (int)response.StatusCode,
                                ["Referer"] = outgoing.Headers.Referrer?.AbsoluteUri ?? "",
                                ["CookieNames"] = request.Headers.ContainsKey("Cookie")
                                    ? string.Join(",", request.Headers["Cookie"].Split(';').Select(item => item.Trim().Split('=')[0])) : ""
                            };
                            try
                            {
                                var root = JObject.Parse(text);
                                requestSummary["Code"] = root["code"];
                                if (request.RequestUri.AbsolutePath == "/x/v2/medialist/resource/list")
                                {
                                    var media = root["data"]?["media_list"] as JArray ?? new JArray();
                                    requestSummary["Total"] = root["data"]?["total_count"];
                                    requestSummary["MediaCount"] = media.Count;
                                    requestSummary["MediaRows"] = new JArray(media.Select(item => new JObject
                                    {
                                        ["Id"] = item["id"], ["Type"] = item["type"],
                                        ["BvId"] = item["bv_id"], ["Bvid"] = item["bvid"],
                                        ["UpperMid"] = (item["upper"] as JObject)?["mid"]
                                    }));
                                }
                            }
                            catch (JsonException) { }
                            LastRequests.Add(requestSummary);
                            var incoming = new HttpResponseMessage
                            {
                                StatusCode = (int)response.StatusCode,
                                Content = new HttpStringContent(text)
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
                    // Reproduce the real hub opening profile and first page together.
                    var profileTask = client.GetCreatorProfileAsync(mid);
                    var firstTask = client.GetCreatorVideosAsync(mid);
                    await Task.WhenAll(profileTask, firstTask);
                    var profile = await profileTask;
                    var first = await firstTask;
                    var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var collaborations = 0;
                    var resultPages = new JArray();
                    var result = first;
                    for (var page = 1; page <= pages; page++)
                    {
                        if (page > 1) result = await client.GetCreatorVideosAsync(mid, page);
                        if (result.Videos.Any(item => item.OwnerMid <= 0 || !all.Add(item.Bvid)))
                            throw new InvalidOperationException("Creator pages repeat a video or omit its actual uploader.");
                        collaborations += result.Videos.Count(item => item.OwnerMid != mid);
                        resultPages.Add(new JObject
                        {
                            ["Page"] = page, ["Count"] = result.Videos.Count,
                            ["Total"] = result.Total, ["HasMore"] = result.HasMore,
                            ["FirstBvid"] = result.Videos.FirstOrDefault()?.Bvid ?? ""
                        });
                        if (!result.HasMore) break;
                    }
                    // Output only public metadata and counters: no cookies or signed URLs.
                    return new JObject
                    {
                        ["Mid"] = mid, ["Name"] = profile.Name, ["Pages"] = resultPages,
                        ["UniqueVideos"] = all.Count, ["HasMoreAfterLastRead"] = result.HasMore,
                        ["CollaborativePrimaryOwnersPreserved"] = collaborations,
                        ["Anonymous"] = !client.IsLoggedIn,
                        ["HomepageRequests"] = Transport.Requests.Count(item => item.Uri.Host == "www.bilibili.com"),
                        ["CardRequests"] = Transport.Requests.Count(item => item.Uri.AbsolutePath == "/x/web-interface/card"),
                        ["MediaListRequests"] = Transport.Requests.Count(item => item.Uri.AbsolutePath == "/x/v2/medialist/resource/list"),
                        ["WbiArchiveRequests"] = Transport.Requests.Count(item => item.Uri.AbsolutePath == "/x/space/wbi/arc/search"),
                        ["NativeXboxTransportVerified"] = false, ["Requests"] = LastRequests
                    }.ToString();
                }
            }
        }
    }
}
