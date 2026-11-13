namespace BiliXbox.SectionTests
{
    using BiliXbox.Services;
    using BiliXbox.VideoDetailTests;
    public static class Tests
    {
        private static int _checks;
        private static void Check(bool value, string message) { if (!value) throw new Exception(message); _checks++; }
        private static JObject Video(int id) => new JObject { ["aid"] = id, ["bvid"] = "BVfixture" + id,
            ["title"] = "动画 &amp; 影视🎮", ["pic"] = "https://i0.hdslb.com/fixture.jpg", ["duration"] = 97,
            ["owner"] = new JObject { ["mid"] = 42, ["name"] = "UP" }, ["stat"] = new JObject { ["view"] = 123 } };
        private static async Task Reject(Func<Task> action)
        { try { await action(); } catch (Exception ex) when (ex is ArgumentException || ex is BilibiliApiException || ex is OperationCanceledException) { _checks++; return; } throw new Exception("Expected invalid section response rejection"); }
        public static async Task<string> Run()
        {
            _checks = 0;
            foreach (int rid in new[] { 0, 1, 181 })
            {
                Transport.Reset((request, token) =>
                {
                    Check(request.Method.Method == "GET", "Sections are read-only");
                    Check(request.RequestUri.Host == "api.bilibili.com", "Sections stay on the actual official API");
                    Check(request.RequestUri.AbsolutePath == (rid == 0 ? "/x/web-interface/popular" : "/x/web-interface/newlist"), "Correct category source");
                    Check(request.RequestUri.Query.Contains("pn=2") && request.RequestUri.Query.Contains("ps=20"), "Real page size and page preserved");
                    if (rid > 0) Check(request.RequestUri.Query.Contains("rid=" + rid), "Animation and film use their distinct region IDs");
                    var list = new JArray(Enumerable.Range(1, 20).Select(id => Video(id)));
                    return Transport.Json(new JObject { ["code"] = 0, ["data"] = new JObject {
                        [rid == 0 ? "list" : "archives"] = list, ["page"] = new JObject { ["num"] = 2, ["count"] = 0, ["size"] = 20 } } }.ToString());
                });
                using (var api = new BilibiliClient())
                {
                    var page = await api.GetSectionVideosAsync(rid, 2);
                    Check(page.Videos.Count == 20 && page.HasMore, "A real full page with count=0 is not falsely terminal");
                    Check(page.Videos[0].Title == "动画 & 影视🎮" && page.Videos[0].DurationText == "1:37", "Shared card metadata stays consistent");
                }
            }
            Transport.Reset((request, token) => Transport.Json("{\"code\":0,\"data\":{\"list\":[],\"no_more\":true}}"));
            using (var api = new BilibiliClient()) { Check(!(await api.GetSectionVideosAsync(0)).HasMore, "Explicit popular end respected"); }
            Transport.Reset((request, token) => Transport.Json("{\"code\":0,\"data\":{\"archives\":[" + Video(1) + "," + Video(1) + "],\"page\":{\"num\":1,\"count\":2}}}"));
            using (var api = new BilibiliClient()) { var page = await api.GetSectionVideosAsync(1); Check(page.Videos.Count == 1 && !page.HasMore, "Duplicates removed and known total respected"); }
            Transport.Reset((request, token) => Transport.Json("{\"code\":0,\"data\":{\"archives\":[],\"page\":{\"num\":2}}}"));
            using (var api = new BilibiliClient()) { await Reject(() => api.GetSectionVideosAsync(1)); }
            Transport.Reset((request, token) => Transport.Json("{\"code\":0,\"data\":{}}"));
            using (var api = new BilibiliClient()) { await Reject(() => api.GetSectionVideosAsync(181)); }
            Transport.Reset((request, token) => { throw new Exception("Invalid input must not request network"); });
            using (var api = new BilibiliClient())
            {
                await Reject(() => api.GetSectionVideosAsync(99)); await Reject(() => api.GetSectionVideosAsync(1, 0));
                using (var stop = new CancellationTokenSource()) { stop.Cancel(); await Reject(() => api.GetSectionVideosAsync(1, 1, stop.Token)); }
            }
            return "PASS: " + _checks + " production section API assertions; no remote requests.";
        }
    }
}
