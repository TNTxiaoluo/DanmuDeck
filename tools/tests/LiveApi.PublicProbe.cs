// Optional genuine anonymous GET-only probe. No local credentials are loaded.
namespace BiliXbox.LiveApiTests
{
    using BiliXbox.Services;
    using BiliXbox.VideoDetailTests;
    public static class PublicProbe
    {
        public static async Task<string> Run()
        {
            var report = new JObject { ["network"] = "anonymous GET only", ["loadedAccount"] = false, ["sentWrites"] = false };
            using (var handler = new System.Net.Http.HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
            using (var http = new System.Net.Http.HttpClient(handler) { Timeout = TimeSpan.FromSeconds(18) })
            using (var client = new BilibiliClient())
            {
                Transport.Reset(async (request, token) =>
                {
                    if (request.Method.Method != "GET" || request.RequestUri.Scheme != "https"
                        || (request.RequestUri.Host != "api.live.bilibili.com" && request.RequestUri.Host != "api.bilibili.com" && request.RequestUri.Host != "www.bilibili.com"))
                        throw new InvalidOperationException("Public probe blocks writes, redirects and non-official hosts.");
                    using (var actual = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, request.RequestUri))
                    {
                        actual.Headers.TryAddWithoutValidation("User-Agent", BilibiliClient.UserAgent);
                        actual.Headers.Referrer = request.Headers.Referer ?? new Uri(BilibiliClient.Referer);
                        string cookie;
                        if (request.Headers.TryGetValue("Cookie", out cookie))
                        {
                            if (cookie.IndexOf("SESSDATA", StringComparison.OrdinalIgnoreCase) >= 0 || cookie.IndexOf("bili_jct", StringComparison.OrdinalIgnoreCase) >= 0)
                                throw new InvalidOperationException("Public probe refuses authenticated credentials.");
                            actual.Headers.TryAddWithoutValidation("Cookie", cookie);
                        }
                        using (var response = await http.SendAsync(actual, token))
                        {
                            var adapted = new HttpResponseMessage { StatusCode = (int)response.StatusCode, Content = new HttpStringContent(await response.Content.ReadAsStringAsync()) };
                            IEnumerable<string> values;
                            if (response.Headers.TryGetValues("Set-Cookie", out values)) adapted.Headers["Set-Cookie"] = string.Join(",", values);
                            return adapted;
                        }
                    }
                });
                var rooms = await client.GetLiveRoomsAsync(1);
                report["firstPageCount"] = rooms.Items.Count; report["hasMore"] = rooms.HasMore;
                if (rooms.Items.Count > 0)
                {
                    long id = rooms.Items[0].Id;
                    var room = await client.GetLiveRoomAsync(id);
                    report["resolvedRoom"] = room.Id; report["ownerResolved"] = !string.IsNullOrWhiteSpace(room.OwnerName);
                    try
                    {
                        var playback = await client.GetLivePlaybackAsync(room.Id);
                        report["playback"] = new JObject { ["actualQualityId"] = playback.QualityId, ["label"] = playback.QualityLabel, ["format"] = playback.Format,
                            ["codec"] = playback.Codec, ["qualityCount"] = playback.Qualities.Count, ["backups"] = playback.BackupUrls.Count,
                            ["officialHttps"] = playback.Url.StartsWith("https://", StringComparison.Ordinal) };
                    }
                    catch (BilibiliApiException ex) { report["playbackUnavailable"] = ex.Message; }
                    try { report["catalogCount"] = (await client.GetLiveGiftCatalogAsync(room.Id)).Items.Count; }
                    catch (BilibiliApiException ex) { report["catalogUnavailable"] = ex.Message; }
                    try { report["recentChatCount"] = (await client.GetRecentLiveDanmakuAsync(room.Id)).Count; }
                    catch (BilibiliApiException ex) { report["recentChatUnavailable"] = ex.Message; }
                    try
                    {
                        var connection = await client.GetLiveDanmakuConnectionAsync(room.Id);
                        report["socketConfig"] = new JObject { ["validToken"] = !string.IsNullOrWhiteSpace(connection.Token), ["officialHostCount"] = connection.Hosts.Count,
                            ["authenticatedUserId"] = connection.UserId, ["realBuvid"] = !string.IsNullOrWhiteSpace(connection.Buvid) };
                    }
                    catch (BilibiliApiException ex) { report["socketConfigUnavailable"] = ex.Message; }
                }
            }
            return report.ToString(Newtonsoft.Json.Formatting.Indented);
        }
    }
}
