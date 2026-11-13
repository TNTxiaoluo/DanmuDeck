// Production service with an in-memory transport. Never logs in or sends a remote POST.
namespace BiliXbox.LiveApiTests
{
    using BiliXbox.Services;
    using BiliXbox.VideoDetailTests;
    public static class Tests
    {
        private static int _checks;
        private static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); _checks++; }
        private static JObject Ok(JToken data = null) => data == null ? new JObject { ["code"] = 0 } : new JObject { ["code"] = 0, ["data"] = data };
        private static JObject Room() => Ok(new JObject { ["room_id"] = 12345678901L, ["uid"] = 424242, ["title"] = "真实 &amp; 直播", ["live_status"] = 1, ["area_id"] = 3, ["parent_area_id"] = 2 });
        private static JObject Master() => Ok(new JObject { ["info"] = new JObject { ["uid"] = 424242, ["uname"] = "测试主播", ["face"] = "https://i0.hdslb.com/face.png" } });
        private static JObject Catalog(long price = 100, int availability = 1, string currency = "gold") => Ok(new JObject
        {
            ["gift_data"] = new JObject { ["room_gift_list"] = new JObject { ["gold_list"] = new JArray(new JObject { ["gift_id"] = 77, ["special"] = new JObject { ["is_use"] = availability } }), ["silver_list"] = new JArray() } },
            ["gift_config"] = new JObject { ["base_config"] = new JObject { ["list"] = new JArray(new JObject { ["id"] = 77, ["name"] = "小花花", ["price"] = price, ["coin_type"] = currency, ["privilege_required"] = 0, ["bag_gift"] = 0 }) }, ["room_config"] = new JArray() }
        });
        private static JObject Bag(long quantity = 3) => Ok(new JObject { ["list"] = new JArray(new JObject { ["gift_id"] = 88, ["bag_id"] = 98765432101L, ["gift_name"] = "辣条", ["gift_num"] = quantity, ["expire_at"] = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds() }) });
        private static JObject Playback(string protocol = "http_hls", string format = "ts", string codecName = "avc", string host = "https://cdn.bilivideo.com", bool drm = false) => Ok(new JObject
        {
            ["room_id"] = 12345678901L, ["uid"] = 424242, ["live_status"] = 1,
            ["playurl_info"] = new JObject { ["playurl"] = new JObject
            {
                ["g_qn_desc"] = new JArray(new JObject { ["qn"] = 10000, ["desc"] = "原画" }, new JObject { ["qn"] = 250, ["desc"] = "超清" }, new JObject { ["qn"] = 20000, ["desc"] = "4K" }, new JObject { ["qn"] = 30000, ["desc"] = "杜比" }),
                ["stream"] = new JArray(new JObject { ["protocol_name"] = protocol, ["format"] = new JArray(new JObject { ["format_name"] = format, ["codec"] = new JArray(new JObject
                { ["codec_name"] = codecName, ["drm"] = drm, ["current_qn"] = 250, ["accept_qn"] = new JArray(10000, 250), ["base_url"] = "/live/index.m3u8?", ["url_info"] = new JArray(new JObject { ["host"] = host, ["extra"] = "token=opaque" }, new JObject { ["host"] = "https://backup.bilivideo.com", ["extra"] = "token=opaque" }) }) }) })
            } }
        });
        private static Dictionary<string, string> Body(HttpRequestMessage request) => ((HttpFormUrlEncodedContent)request.Content).Values;
        private static int Posts => Transport.Requests.Count(item => item.Method == "POST");
        private static void Stub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> run)
        {
            Transport.Reset(async (request, token) =>
            {
                if (request.RequestUri.AbsolutePath == "/x/web-interface/nav")
                {
                    var response = new HttpResponseMessage { Content = new HttpStringContent(Ok(new JObject { ["mid"] = 42, ["uname"] = "fixture", ["isLogin"] = true,
                        ["wbi_img"] = new JObject { ["img_url"] = "https://i0.hdslb.com/bfs/wbi/7cd084941338484aae1ad9425b84077c.png", ["sub_url"] = "https://i0.hdslb.com/bfs/wbi/4932caff0ff746eab6f01bf08b70ac45.png" } }).ToString()) };
                    response.Headers["Set-Cookie"] = "SESSDATA=fixture-session; Domain=.bilibili.com; Path=/; Secure,bili_jct=fixture-csrf; Domain=.bilibili.com; Path=/; Secure,buvid3=server-fixture; Domain=.bilibili.com; Path=/; Secure";
                    return response;
                }
                Check(request.RequestUri.Host == "api.live.bilibili.com" && request.RequestUri.Scheme == "https", "live transport official HTTPS only");
                Check(request.Headers.Referer != null && request.Headers.Referer.Host == "live.bilibili.com", "live-specific referer");
                if (request.Method.Method == "POST")
                {
                    Check(Body(request)["csrf"] == "fixture-csrf" && Body(request)["csrf_token"] == "fixture-csrf", "both matching CSRF fields sent");
                    Check(request.Headers["Origin"] == "https://live.bilibili.com", "live origin");
                    Check(!request.RequestUri.Query.Contains("csrf"), "credentials never in URL");
                }
                return await run(request, token);
            });
        }
        private static void Stub(Func<HttpRequestMessage, JObject> run) => Stub((request, token) => Transport.Json(run(request).ToString()));
        private static async Task Login(BilibiliClient client) => await (Task)typeof(BilibiliClient).GetMethod("RefreshProfileAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(client, new object[] { CancellationToken.None });
        private static void Switch(BilibiliClient client) => typeof(BilibiliClient).GetProperty("CurrentUser").SetValue(client, new UserProfile { Mid = 43, Name = "changed" });
        private static async Task Reject(Func<Task> run, string name)
        {
            try { await run(); } catch (Exception ex) when (ex is BilibiliApiException || ex is ArgumentException || ex is OperationCanceledException) { Check(true, name); return; }
            throw new Exception("FAIL: expected rejection: " + name);
        }
        private static JObject GiftRoute(HttpRequestMessage request, long price = 100, long quantity = 3)
        {
            switch (request.RequestUri.AbsolutePath)
            {
                case "/room/v1/Room/get_info": return Room();
                case "/live_user/v1/Master/info": return Master();
                case "/xlive/web-room/v1/giftPanel/roomGiftList": return Catalog(price);
                case "/xlive/web-room/v1/gift/bag_list": return Bag(quantity);
                default: if (request.Method.Method == "POST") return Ok(); throw new Exception("Unexpected live route");
            }
        }
        public static async Task<string> Run()
        {
            _checks = 0;
            Transport.Reset((request, token) => { throw new Exception("Must fail before transport"); });
            using (var client = new BilibiliClient())
            {
                await Reject(() => client.SendLiveDanmakuAsync(1, "hello"), "anonymous chat denied");
                await Reject(() => client.GetLiveGiftBagAsync(1), "anonymous bag denied");
                await Reject(() => client.PrepareLiveGiftAsync(1, new LiveGift { Id = 1 }, 1), "anonymous paid quote denied");
                await Reject(() => client.SendLiveGiftAsync(new LiveGiftQuote(), false), "unconfirmed gift denied");
                await Reject(() => client.SendLiveDanmakuAsync(1, "\n"), "control-only message denied");
                await Reject(() => client.GetLiveRoomsAsync(0), "invalid page denied");
                Check(Transport.Requests.Count == 0, "validation sends no anonymous writes");
            }
            Stub(request => Ok(new JObject { ["count"] = 42, ["list"] = new JArray(new JObject { ["roomid"] = 12345678901L, ["uid"] = 424242, ["title"] = "测试 &amp; 直播", ["uname"] = "主播", ["area_v2_id"] = 6, ["online"] = 12000 }) }));
            using (var client = new BilibiliClient())
            {
                var page = await client.GetLiveRoomsAsync(2);
                Check(page.Page == 2 && page.HasMore && page.Items.Count == 1 && page.Items[0].Id == 12345678901L && page.Items[0].Title == "测试 & 直播", "live page and 64-bit room ID map exactly");
                Check(Transport.Requests[0].Uri.Query.Contains("page=2") && Posts == 0, "real pagination query read-only");
            }
            Stub(request => request.RequestUri.AbsolutePath.Contains("get_info") ? Room() : Master());
            using (var client = new BilibiliClient()) { var room = await client.GetLiveRoomAsync(6); Check(room.Id == 12345678901L && room.OwnerMid == 424242 && room.OwnerName == "测试主播" && room.IsLive, "short room resolves to real ID and actual owner"); }
            Stub(request => Playback());
            using (var client = new BilibiliClient())
            {
                var play = await client.GetLivePlaybackAsync(12345678901L);
                Check(play.QualityId == 250 && play.QualityLabel == "超清" && play.Format == "ts" && play.Codec == "avc", "actual returned quality is not requested original quality");
                Check(play.Url.EndsWith("?token=opaque") && play.BackupUrls.Count == 1 && play.Qualities.Count == 2 && play.Qualities.All(item => item.Id != 20000), "only stream-accepted qualities and exact signed HLS URLs");
            }
            var multiFormat = Playback();
            var formats = (JArray)multiFormat["data"]["playurl_info"]["playurl"]["stream"][0]["format"];
            var fragmented = (JObject)formats[0].DeepClone(); fragmented["format_name"] = "fmp4";
            fragmented["codec"][0]["base_url"] = "/fragmented/index.m3u8?";
            fragmented["codec"][0]["url_info"] = new JArray(new JObject { ["host"] = "https://fragmented.bilivideo.com", ["extra"] = "signed=original" });
            formats.Add(fragmented);
            var otherQuality = (JObject)fragmented.DeepClone(); otherQuality["codec"][0]["current_qn"] = 10000;
            otherQuality["codec"][0]["url_info"] = new JArray(new JObject { ["host"] = "https://otherquality.bilivideo.com", ["extra"] = "signed=original" }); formats.Add(otherQuality);
            var drmFragmented = (JObject)fragmented.DeepClone(); drmFragmented["codec"][0]["drm"] = true;
            drmFragmented["codec"][0]["url_info"] = new JArray(new JObject { ["host"] = "https://drm.bilivideo.com", ["extra"] = "signed=original" }); formats.Add(drmFragmented);
            Stub(request => multiFormat);
            using (var client = new BilibiliClient())
            {
                var play = await client.GetLivePlaybackAsync(12345678901L, 250);
                Check(play.Format == "fmp4" && play.Url == "https://fragmented.bilivideo.com/fragmented/index.m3u8?signed=original", "same actual quality prefers original fMP4 HLS playlist");
                Check(play.BackupUrls.Count == 2 && play.BackupUrls[0] == "https://cdn.bilivideo.com/live/index.m3u8?token=opaque",
                    "same actual quality TS playlists remain unchanged as independent fallback");
                Check(play.QualityId == 250 && !play.BackupUrls.Any(value => value.Contains("otherquality") || value.Contains("drm")),
                    "backup formats cannot silently upgrade quality or admit DRM sources");
            }
            var explicitOriginal = Playback();
            var originalFormats = (JArray)explicitOriginal["data"]["playurl_info"]["playurl"]["stream"][0]["format"];
            originalFormats[0]["codec"][0]["current_qn"] = 10000;
            originalFormats.Add(fragmented.DeepClone());
            Stub(request => explicitOriginal);
            using (var client = new BilibiliClient())
            {
                var play = await client.GetLivePlaybackAsync(12345678901L, 10000);
                Check(play.Format == "ts" && play.QualityId == 10000 && play.QualityLabel == "原画", "explicit original quality TS wins over lower-quality fMP4");
                Check(play.BackupUrls.Count == 1 && !play.BackupUrls.Any(value => value.Contains("fragmented")), "lower quality format is never silently mixed into original quality fallback");
            }
            foreach (var fixture in new[] { Playback("http_stream", "flv"), Playback(codecName: "hevc"), Playback(drm: true) })
            { Stub(request => fixture); using (var client = new BilibiliClient()) await Reject(() => client.GetLivePlaybackAsync(1), "unsupported FLV/HEVC/DRM not passed to UWP as playback"); }
            Stub(request => Playback(host: "https://attacker.example"));
            using (var client = new BilibiliClient()) { var play = await client.GetLivePlaybackAsync(1); Check(play.Url.StartsWith("https://backup.bilivideo.com"), "untrusted source URL ignored"); }
            Stub(request => Ok(new JObject { ["room"] = new JArray(new JObject { ["uid"] = 7, ["nickname"] = "观众", ["text"] = "中文 🎮" }) }));
            using (var client = new BilibiliClient()) { var recent = await client.GetRecentLiveDanmakuAsync(1); Check(recent[0].Text == "中文 🎮" && recent[0].UserName == "观众", "recent chat preserves Unicode without synthetic messages"); }
            Stub(request => Catalog());
            using (var client = new BilibiliClient()) { var gifts = await client.GetLiveGiftCatalogAsync(1); Check(gifts.Items.Count == 1 && gifts.Items[0].UnitPrice == 100 && gifts.Items[0].PriceText == "1 电池", "exact server gift units translated to batteries"); }
            foreach (var fixture in new[] { Catalog(-1), Catalog(0), Catalog(100, 0), Catalog(100, 1, "unknown") })
            { Stub(request => fixture); using (var client = new BilibiliClient()) Check((await client.GetLiveGiftCatalogAsync(1)).Items.Count == 0, "invalid price, disabled gift or unknown currency hidden"); }
            var nullScene = Catalog(); nullScene["data"]["gift_data"]["room_gift_list"]["gold_list"][0]["gift_scene"] = JValue.CreateNull();
            Stub(request => nullScene);
            using (var client = new BilibiliClient()) Check((await client.GetLiveGiftCatalogAsync(1)).Items.Count == 1, "real endpoint null gift_scene is parsed as ordinary gift");
            Stub(request => Ok(new JObject { ["gift_data"] = new JObject { ["room_gift_list"] = JValue.CreateNull() }, ["gift_config"] = JValue.CreateNull() }));
            using (var client = new BilibiliClient()) Check((await client.GetLiveGiftCatalogAsync(1)).Items.Count == 0, "null restricted room catalogue stays empty without crash");
            Stub(request => Ok(new JObject { ["room_id"] = 1, ["live_status"] = 1, ["playurl_info"] = JValue.CreateNull() }));
            using (var client = new BilibiliClient()) await Reject(() => client.GetLivePlaybackAsync(1), "null restricted play metadata is explicit unavailable");
            Stub(request =>
            {
                if (request.Method.Method != "POST") throw new Exception("Send chat has no read mutation");
                Check(request.RequestUri.AbsolutePath == "/msg/send" && Body(request)["roomid"] == "12345678901" && Body(request)["msg"] == "中文 🎮", "one explicit chat POST with real room and Unicode");
                return Ok();
            });
            using (var client = new BilibiliClient()) { await Login(client); await client.SendLiveDanmakuAsync(12345678901L, "中文 🎮"); Check(Posts == 1, "chat not retried"); }
            Stub(request => new JObject { ["code"] = 0, ["message"] = "您发送弹幕的频率过快" });
            using (var client = new BilibiliClient()) { await Login(client); await Reject(() => client.SendLiveDanmakuAsync(1, "hello"), "code-zero moderation message not shown as sent"); Check(Posts == 1, "blocked chat not resent"); }
            Stub(request => GiftRoute(request));
            using (var client = new BilibiliClient())
            {
                await Login(client); var gift = (await client.GetLiveGiftCatalogAsync(12345678901L)).Items[0];
                var quote = await client.PrepareLiveGiftAsync(12345678901L, gift, 2);
                Check(quote.AccountMid == 42 && quote.OwnerMid == 424242 && quote.Count == 2 && quote.TotalPrice == 200 && quote.CostText == "2 电池" && Posts == 0, "quote reads current price and binds account, owner and quantity without spending");
                await Reject(() => client.SendLiveGiftAsync(quote, false), "explicit confirmation required");
                Check(Posts == 0, "quote and canceled confirmation never spend");
                await client.SendLiveGiftAsync(quote, true);
                Check(Posts == 1, "one gift confirmation performs exactly one write");
                await Reject(() => client.SendLiveGiftAsync(quote, true), "same confirmation cannot spend twice");
                Check(Posts == 1, "spent quote no automatic retry");
            }
            Stub(request =>
            {
                if (request.Method.Method == "POST") Check(request.RequestUri.AbsolutePath.EndsWith("/sendBag") && Body(request)["bag_id"] == "98765432101" && Body(request)["gift_num"] == "2" && Body(request)["price"] == "0", "backpack sends exact item and quantity with no paid fallback");
                return GiftRoute(request);
            });
            using (var client = new BilibiliClient())
            {
                await Login(client); var bag = (await client.GetLiveGiftBagAsync(12345678901L)).Items[0];
                await Reject(() => client.PrepareLiveGiftAsync(12345678901L, bag, 4), "insufficient backpack item denied");
                var quote = await client.PrepareLiveGiftAsync(12345678901L, bag, 2); Check(quote.CostText.Contains("背包") && quote.TotalPrice == 0, "bag quote states consumption without charging batteries");
                await client.SendLiveGiftAsync(quote, true); Check(Posts == 1, "backpack sends only once");
            }
            long changingPrice = 100;
            Stub(request => GiftRoute(request, changingPrice));
            using (var client = new BilibiliClient())
            {
                await Login(client); var gift = (await client.GetLiveGiftCatalogAsync(12345678901L)).Items[0]; var quote = await client.PrepareLiveGiftAsync(12345678901L, gift, 1);
                changingPrice = 200; await Reject(() => client.SendLiveGiftAsync(quote, true), "changed price requires fresh confirmation"); Check(Posts == 0, "changed price cannot silently spend more");
            }
            Stub(request => GiftRoute(request));
            using (var client = new BilibiliClient())
            {
                await Login(client); var gift = (await client.GetLiveGiftCatalogAsync(12345678901L)).Items[0]; var quote = await client.PrepareLiveGiftAsync(12345678901L, gift, 1);
                Switch(client); await Reject(() => client.SendLiveGiftAsync(quote, true), "new account cannot reuse old account confirmation"); Check(Posts == 0, "account change prevents remote gift");
            }
            Stub((request, token) => request.Method.Method == "POST" ? Task.FromException<HttpResponseMessage>(new OperationCanceledException()) : Transport.Json(GiftRoute(request).ToString()));
            using (var client = new BilibiliClient())
            {
                await Login(client); var gift = (await client.GetLiveGiftCatalogAsync(12345678901L)).Items[0]; var quote = await client.PrepareLiveGiftAsync(12345678901L, gift, 1);
                await Reject(() => client.SendLiveGiftAsync(quote, true), "gift timeout remains unknown"); await Reject(() => client.SendLiveGiftAsync(quote, true), "unknown gift outcome cannot reuse quote"); Check(Posts == 1, "timeout never doubles gift spend");
            }
            Stub(request =>
            {
                Check(request.RequestUri.AbsolutePath.EndsWith("/getDanmuInfo") && request.RequestUri.Query.Contains("w_rid=") && request.RequestUri.Query.Contains("wts="), "danmaku configuration uses ordinary WBI signature");
                return Ok(new JObject { ["token"] = "opaque-fixture", ["host_list"] = new JArray(new JObject { ["host"] = "tx.chat.bilibili.com", ["wss_port"] = 443 },
                    new JObject { ["host"] = "attacker.example", ["wss_port"] = 443 }, new JObject { ["host"] = "x.chat.bilibili.com.attacker.example", ["wss_port"] = 443 }, new JObject { ["host"] = "bad.chat.bilibili.com", ["wss_port"] = 65536 }) });
            });
            using (var client = new BilibiliClient())
            {
                await Login(client); var connection = await client.GetLiveDanmakuConnectionAsync(12345678901L);
                Check(connection.UserId == 42 && connection.RoomId == 12345678901L && connection.Token == "opaque-fixture" && connection.Buvid == "server-fixture"
                    && connection.Hosts.Count == 1 && connection.Hosts[0].WssPort == 443, "socket authentication binds original account and only official TLS hosts");
            }
            BilibiliClient changingClient = null;
            Stub(request =>
            {
                if (request.RequestUri.AbsolutePath.EndsWith("/bag_list")) { Switch(changingClient); return new JObject { ["code"] = -101, ["message"] = "stale" }; }
                return GiftRoute(request);
            });
            using (var client = new BilibiliClient())
            {
                changingClient = client; await Login(client);
                await Reject(() => client.GetLiveGiftBagAsync(12345678901L), "account change while bag request pending rejects stale response");
                Check(client.CurrentUser != null && client.CurrentUser.Mid == 43, "old account minus101 cannot clear newer account");
            }
            var pending = new TaskCompletionSource<bool>();
            var release = new TaskCompletionSource<bool>();
            Stub(async (request, token) =>
            {
                if (request.Method.Method == "POST") { pending.SetResult(true); await release.Task; return new HttpResponseMessage { Content = new HttpStringContent(new JObject { ["code"] = -101 }.ToString()) }; }
                return await Transport.Json(GiftRoute(request).ToString());
            });
            using (var client = new BilibiliClient())
            {
                await Login(client); var gift = (await client.GetLiveGiftCatalogAsync(12345678901L)).Items[0]; var quote = await client.PrepareLiveGiftAsync(12345678901L, gift, 1);
                var write = client.SendLiveGiftAsync(quote, true); await pending.Task;
                Switch(client); release.SetResult(true); await Reject(() => write, "late write response rejects changed account");
                Check(client.CurrentUser != null && client.CurrentUser.Mid == 43 && Posts == 1, "late old session never clears new login or retries spend");
            }
            return "PASS: " + _checks + " live API assertions; no network or account writes executed.";
        }
    }
}
