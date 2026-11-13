namespace BiliXbox.DanmakuCloudTests
{
    public static class Tests
    {
        private static int _checks;
        private static void Check(bool passed, string name) { if (!passed) throw new Exception(name); ++_checks; }
        private static JObject Rules(string json) => JObject.Parse("{\"rule\":" + json + "}");
        private static void Invalid(JToken input)
        {
            try { Services.BilibiliClient.ParseDanmakuBlockRules(input, 42, DateTimeOffset.UtcNow); throw new Exception("invalid cloud rules accepted"); }
            catch (Services.BilibiliApiException) { ++_checks; }
        }
        public static async Task<string> Run()
        {
            var now = DateTimeOffset.UtcNow;
            var parsed = Services.BilibiliClient.ParseDanmakuBlockRules(Rules("[{\"id\":1,\"type\":0,\"filter\":\"剧透😀\"},{\"id\":2,\"type\":1,\"filter\":\"^重复+$\"},{\"id\":3,\"type\":2,\"filter\":\"DEADbeef\"}]"), 42, now);
            Check(parsed.AccountMid == 42 && parsed.SyncedAt == now && parsed.Count == 3, "cloud provenance and count");
            Check(parsed.Rules[0].Value == "剧透😀" && parsed.Rules[0].Type == DanmakuBlockRuleType.Keyword, "Unicode keyword");
            Check(parsed.Rules[1].Type == DanmakuBlockRuleType.Regex && parsed.Rules[2].Type == DanmakuBlockRuleType.UserHash, "rule types");
            Check(parsed.Rules.All(rule => rule.Enabled), "website defaults enabled");
            var empty = Services.BilibiliClient.ParseDanmakuBlockRules(Rules("[]"), 42, now);
            Check(empty.Count == 0 && empty.SourceCount == 0 && empty.Notice.Length == 0, "real empty cloud list");
            var duplicate = Services.BilibiliClient.ParseDanmakuBlockRules(Rules("[{\"type\":0,\"filter\":\"Test\"},{\"type\":0,\"filter\":\"test\"},{\"type\":2,\"filter\":\"abCD\"},{\"type\":2,\"filter\":\"ABcd\"},{\"type\":1,\"filter\":\"Test\"},{\"type\":1,\"filter\":\"test\"}]"), 42, now);
            Check(duplicate.Count == 4 && duplicate.SourceCount == 6, "type-specific deduplication");
            var disabled = Services.BilibiliClient.ParseDanmakuBlockRules(Rules("[{\"type\":0,\"filter\":\"text\",\"opened\":false},{\"type\":1,\"filter\":\".*\",\"enabled\":false},{\"type\":9,\"filter\":\"future\"}]"), 42, now);
            Check(disabled.Count == 2 && disabled.Rules.All(rule => !rule.Enabled), "disabled rules preserved");
            Check(disabled.UnsupportedCount == 1 && disabled.Notice.Length > 0, "unsupported rules explicitly reported");
            Invalid(new JObject()); Invalid(Rules("[{}]")); Invalid(Rules("[{\"type\":0,\"filter\":\"\"}]"));
            Invalid(new JObject { ["rule"] = new JArray(), ["has_more"] = true });
            Invalid(new JObject { ["rule"] = new JArray(), ["total"] = 3 });
            Invalid(new JObject { ["rule"] = new JArray(new JObject { ["type"] = 0, ["filter"] = new string('x', 4097) }) });
            VideoDetailTests.Transport.Reset((request, token) => { throw new Exception("anonymous cloud request attempted network"); });
            using (var client = new Services.BilibiliClient())
            {
                try { await client.GetDanmakuBlockRulesAsync(); throw new Exception("anonymous cloud list accepted"); }
                catch (Services.BilibiliApiException ex) { Check(ex.Code == -101, "cloud needs login"); }
            }
            Check(VideoDetailTests.Transport.Requests.Count == 0, "anonymous call never reads cloud");
            VideoDetailTests.Transport.Reset((request, token) =>
            {
                if (request.RequestUri.AbsolutePath == "/x/web-interface/nav")
                {
                    var response = new Windows.Web.Http.HttpResponseMessage { Content = new Windows.Web.Http.HttpStringContent("{\"code\":0,\"data\":{\"isLogin\":true,\"mid\":42,\"uname\":\"fixture\",\"vipStatus\":1}}") };
                    response.Headers["Set-Cookie"] = "SESSDATA=fixture-only; Domain=.bilibili.com; Path=/; Secure";
                    return Task.FromResult(response);
                }
                Check(request.Method.Method == "GET" && request.RequestUri.AbsolutePath == "/x/dm/filter/user", "cloud sync is read-only official GET");
                string cookie;
                Check(request.Headers.TryGetValue("Cookie", out cookie) && cookie.Contains("SESSDATA=fixture-only"), "cloud uses only current app session");
                return Task.FromResult(new Windows.Web.Http.HttpResponseMessage { Content = new Windows.Web.Http.HttpStringContent("{\"code\":0,\"data\":{\"rule\":[{\"type\":0,\"filter\":\"剧透\"}]}}") });
            });
            using (var client = new Services.BilibiliClient())
            {
                var refresh = typeof(Services.BilibiliClient).GetMethod("RefreshProfileAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                await (Task)refresh.Invoke(client, new object[] { CancellationToken.None });
                var result = await client.GetDanmakuBlockRulesAsync();
                Check(result.AccountMid == 42 && result.Count == 1, "authenticated account cloud rules");
            }
            return "PASS: " + _checks + " production cloud block-list assertions; real account cloud sync requires signed-in device validation.";
        }
    }
}
