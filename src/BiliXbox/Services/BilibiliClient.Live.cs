using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Models;
using Newtonsoft.Json.Linq;
using Windows.Web.Http;

namespace BiliXbox.Services
{
    public sealed partial class BilibiliClient
    {
        private const string LiveApi = "https://api.live.bilibili.com";

        public async Task<LiveRoomPage> GetLiveRoomsAsync(int page = 1, CancellationToken ct = default(CancellationToken))
        {
            CheckPage(page);
            var data = RequireData(await LiveGetAsync("/room/v3/area/getRoomList?platform=web&parent_area_id=0&area_id=0&sort_type=online&page_size=20&page=" + Invariant(page), 0, ct));
            var list = data["list"] as JArray;
            if (list == null) throw LiveFormatError("直播列表");
            var rooms = list.Where(item => Number(item, "roomid") > 0).Select(MapLiveRoom).GroupBy(room => room.Id).Select(group => group.First()).ToList();
            long total = Number(data, "count");
            return new LiveRoomPage { Page = page, Items = rooms, HasMore = rooms.Count > 0 && (total > 0 ? (long)page * 20 < total : rooms.Count >= 20) };
        }

        public async Task<LiveRoom> GetLiveRoomAsync(long roomId, CancellationToken ct = default(CancellationToken))
        {
            CheckId(roomId, nameof(roomId));
            var roomData = RequireData(await LiveGetAsync("/room/v1/Room/get_info?room_id=" + Invariant(roomId), roomId, ct));
            long realId = Number(roomData, "room_id"), owner = Number(roomData, "uid");
            if (realId <= 0 || owner <= 0) throw LiveFormatError("直播间信息");
            var info = RequireData(await LiveGetAsync("/live_user/v1/Master/info?uid=" + Invariant(owner), realId, ct))["info"];
            if (!(info is JObject) || Number(info, "uid") != owner) throw LiveFormatError("主播资料");
            return new LiveRoom
            {
                Id = realId, OwnerMid = owner, Title = PlainText(Str(roomData, "title")),
                OwnerName = PlainText(Str(info, "uname")), AvatarUrl = ImageUrl(Str(info, "face")),
                CoverUrl = ImageUrl(FirstLiveText(roomData, "user_cover", "keyframe")),
                Area = PlainText(Str(roomData, "area_name")), AreaId = Number(roomData, "area_id"),
                ParentAreaId = Number(roomData, "parent_area_id"), Online = Number(roomData, "online"),
                IsLive = Number(roomData, "live_status") == 1
            };
        }

        public async Task<LivePlayback> GetLivePlaybackAsync(long roomId, int qualityId = 10000, CancellationToken ct = default(CancellationToken))
        {
            CheckId(roomId, nameof(roomId));
            if (qualityId < 0 || qualityId > 100000) throw new ArgumentOutOfRangeException(nameof(qualityId));
            var data = RequireData(await LiveGetAsync("/xlive/web-room/v2/index/getRoomPlayInfo?room_id=" + Invariant(roomId)
                + "&protocol=1&format=1,2&codec=0&platform=web&ptype=16&qn=" + Invariant(qualityId), roomId, ct));
            if (Number(data, "live_status") != 1) throw new BilibiliApiException(-1, "主播当前未开播。");
            if (data.Value<bool?>("is_hidden") == true || data.Value<bool?>("is_locked") == true
                || data.Value<bool?>("encrypted") == true && data.Value<bool?>("pwd_verified") != true)
                throw new BilibiliApiException(-1, "此直播间受限，无法播放。");
            long actualRoom = Number(data, "room_id");
            if (actualRoom <= 0) throw LiveFormatError("直播间真实房间号");
            var playInfo = data["playurl_info"] as JObject;
            var play = playInfo?["playurl"] as JObject;
            var streams = play?["stream"] as JArray;
            if (streams == null) throw LiveFormatError("直播播放源");
            var candidates = new List<Tuple<int, JToken, string>>();
            foreach (var stream in streams.Where(item => Str(item, "protocol_name") == "http_hls"))
                foreach (var format in stream["format"] as JArray ?? new JArray())
                {
                    string formatName = Str(format, "format_name");
                    if (formatName != "ts" && formatName != "fmp4") continue;
                    foreach (var codec in format["codec"] as JArray ?? new JArray())
                        if (Str(codec, "codec_name") == "avc" && codec.Value<bool?>("drm") != true
                            && Number(codec, "hdr_type") == 0 && Number(codec, "dolby_type") == 0)
                            candidates.Add(Tuple.Create(formatName == "fmp4" ? 0 : 1, codec, formatName));
                }
            // Honour the requested quality before choosing a container. At the
            // same quality prefer modern fMP4 HLS, then retain TS as backup.
            foreach (var candidate in candidates.OrderBy(value => Number(value.Item2, "current_qn") == qualityId ? 0 : 1)
                .ThenBy(value => value.Item1))
            {
                var codec = candidate.Item2;
                int current = (int)Number(codec, "current_qn");
                if (current <= 0) continue;
                var urls = new List<string>();
                // Each HLS URL is a complete independently consumable playlist. Keep
                // server-issued playlists at the same actual quality as backup
                // candidates; a CDN outage must not discard another valid format.
                foreach (var alternate in candidates.OrderBy(value => value.Item1)
                    .Where(value => Number(value.Item2, "current_qn") == current))
                foreach (var host in alternate.Item2["url_info"] as JArray ?? new JArray())
                {
                    string address = Str(host, "host") + Str(alternate.Item2, "base_url") + Str(host, "extra");
                    Uri parsed;
                    if (Uri.TryCreate(address, UriKind.Absolute, out parsed) && parsed.Scheme == "https"
                        && (parsed.Host.EndsWith(".bilivideo.com", StringComparison.OrdinalIgnoreCase)
                            || parsed.Host.EndsWith(".bilivideo.cn", StringComparison.OrdinalIgnoreCase)
                            || parsed.Host.EndsWith(".bilibili.com", StringComparison.OrdinalIgnoreCase))
                        && parsed.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)) urls.Add(address);
                }
                if (urls.Count == 0) continue;
                var accepted = new HashSet<int>((codec["accept_qn"] as JArray ?? new JArray()).Where(value => value.Type == JTokenType.Integer)
                    .Select(value => value.Value<int>()).Where(value => value > 0));
                accepted.Add(current);
                // Only show quality IDs supported by this actual SDR AVC stream, never the global catalogue alone.
                var qualities = (play["g_qn_desc"] as JArray ?? new JArray()).Where(value => accepted.Contains((int)Number(value, "qn"))
                    && Number(value, "hdr_type") == 0 && Number(value, "eotf") == 0 && (int)Number(value, "qn") != 30000)
                    .Select(value => new LiveQuality { Id = (int)Number(value, "qn"), Label = PlainText(Str(value, "desc")) })
                    .GroupBy(value => value.Id).Select(group => group.First()).OrderByDescending(value => value.Id).ToList();
                string label = qualities.FirstOrDefault(value => value.Id == current)?.Label;
                if (string.IsNullOrWhiteSpace(label)) label = "画质 " + Invariant(current);
                return new LivePlayback { RoomId = actualRoom, Url = urls[0], BackupUrls = urls.Skip(1).Distinct().ToList(),
                    QualityId = current, QualityLabel = label, Codec = "avc", Format = candidate.Item3, Qualities = qualities };
            }
            throw new BilibiliApiException(-1, "B站未提供本机可播放的 HLS 直播源，请稍后重试。");
        }

        public async Task<LiveDanmakuConnection> GetLiveDanmakuConnectionAsync(long roomId, CancellationToken ct = default(CancellationToken))
        {
            CheckId(roomId, nameof(roomId));
            var account = IsLoggedIn ? CaptureInteractionAccount(false, ct) : null;
            string anonymousSession = _cookies.GetCookieValue("SESSDATA") ?? "";
            if (string.IsNullOrEmpty(_cookies.GetCookieValue("buvid3")))
            {
                try { await BootstrapWebsiteAsync(ct); }
                catch (BilibiliApiException)
                {
                    // A regional homepage redirect or block does not establish whether the
                    // separate signed live endpoint is available. Never fabricate a buvid.
                }
            }
            if (account != null) EnsureInteractionAccount(account, ct);
            string query = await SignedQueryAsync(new Dictionary<string, string>
                { ["id"] = Invariant(roomId), ["type"] = "0", ["web_location"] = "444.8" }, ct);
            if (account != null) EnsureInteractionAccount(account, ct);
            var root = await LiveGetAsync("/xlive/web-room/v1/index/getDanmuInfo?" + query, roomId, ct);
            if (account != null) EnsureInteractionAccount(account, ct);
            var data = RequireData(root);
            if (account == null && (IsLoggedIn || !string.Equals(anonymousSession, _cookies.GetCookieValue("SESSDATA") ?? "", StringComparison.Ordinal)))
                throw new BilibiliApiException(-101, "登录状态已改变，请重新连接直播弹幕。");
            string token = Str(data, "token");
            var hosts = new List<LiveDanmakuHost>();
            foreach (var host in data["host_list"] as JArray ?? new JArray())
            {
                string name = Str(host, "host");
                long port = Number(host, "wss_port");
                if (name.EndsWith(".chat.bilibili.com", StringComparison.OrdinalIgnoreCase)
                    && Uri.CheckHostName(name) == UriHostNameType.Dns && port > 0 && port <= 65535)
                    hosts.Add(new LiveDanmakuHost { Host = name, WssPort = (int)port });
            }
            if (string.IsNullOrWhiteSpace(token) || hosts.Count == 0) throw LiveFormatError("直播弹幕连接信息");
            return new LiveDanmakuConnection { RoomId = roomId, UserId = account?.Mid ?? 0, Token = token,
                Buvid = _cookies.GetCookieValue("buvid3") ?? "", Hosts = hosts };
        }

        public async Task<IReadOnlyList<LiveChatMessage>> GetRecentLiveDanmakuAsync(long roomId, CancellationToken ct = default(CancellationToken))
        {
            CheckId(roomId, nameof(roomId));
            var data = RequireData(await LiveGetAsync("/xlive/web-room/v1/dM/gethistory?roomid=" + Invariant(roomId), roomId, ct));
            var messages = data["room"] as JArray;
            if (messages == null) throw LiveFormatError("最近直播弹幕");
            return messages.Where(item => !string.IsNullOrWhiteSpace(Str(item, "text"))).Select(item => new LiveChatMessage
            { Kind = LiveChatMessageKind.Danmaku, Text = PlainText(Str(item, "text")), UserName = PlainText(Str(item, "nickname")), UserId = Number(item, "uid") }).ToList();
        }

        public async Task SendLiveDanmakuAsync(long roomId, string text, CancellationToken ct = default(CancellationToken))
        {
            CheckId(roomId, nameof(roomId));
            if (string.IsNullOrWhiteSpace(text) || text.Length > 100 || text.Any(character => char.IsControl(character)))
                throw new ArgumentException("请输入 1 至 100 字的直播弹幕，直播间的实际字数上限由B站决定。", nameof(text));
            var account = CaptureInteractionAccount(true, ct);
            await _videoInteractionGate.WaitAsync(ct);
            try
            {
                await PrepareInteractionMutationAsync(account, ct);
                var root = await LivePostAsync("/msg/send", roomId, new Dictionary<string, string>
                {
                    ["roomid"] = Invariant(roomId), ["msg"] = text.Trim(), ["mode"] = "1", ["fontsize"] = "25", ["color"] = "16777215",
                    ["bubble"] = "0", ["rnd"] = Invariant(DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                    ["csrf"] = account.Csrf, ["csrf_token"] = account.Csrf
                }, ct);
                EnsureInteractionAccount(account, ct);
                RequireInteractionSuccess(root);
                // code 0 with a non-empty message may mean moderation or rate limiting, not publication.
                string message = Str(root, "message");
                if (!string.IsNullOrWhiteSpace(message) && message != "0" && !string.Equals(message, "OK", StringComparison.OrdinalIgnoreCase))
                    throw new BilibiliApiException(-1, "直播弹幕未确认发送成功：" + PlainText(message));
            }
            finally { _videoInteractionGate.Release(); }
        }

        public async Task<LiveGiftCatalog> GetLiveGiftCatalogAsync(long roomId, CancellationToken ct = default(CancellationToken))
        {
            CheckId(roomId, nameof(roomId));
            var account = IsLoggedIn ? CaptureInteractionAccount(false, ct) : null;
            var root = await LiveGetAsync("/xlive/web-room/v1/giftPanel/roomGiftList?platform=pc&room_id=" + Invariant(roomId), roomId, ct);
            if (account != null) EnsureInteractionAccount(account, ct);
            var data = RequireData(root);
            var giftData = data["gift_data"] as JObject;
            var panel = giftData?["room_gift_list"] as JObject;
            var available = new Dictionary<long, JToken>();
            foreach (var item in (panel?["gold_list"] as JArray ?? new JArray()).Concat(panel?["silver_list"] as JArray ?? new JArray()))
            {
                long id = Number(item, "gift_id");
                if (id <= 0) continue;
                if (item["special"] is JObject && Number(item["special"], "is_use") != 1) continue;
                if (item["gift_scene"] is JObject && Str(item["gift_scene"], "pay_type") != "send_gift") continue;
                var giftScene = item["gift_scene"] as JObject;
                if (giftScene?.Value<bool?>("hide_price") == true) continue;
                available[id] = item;
            }
            var giftConfig = data["gift_config"] as JObject;
            var baseConfig = giftConfig?["base_config"] as JObject;
            var configured = (baseConfig?["list"] as JArray ?? new JArray())
                .Concat(giftConfig?["room_config"] as JArray ?? new JArray());
            var mapped = new Dictionary<long, LiveGift>();
            foreach (var item in configured)
            {
                long id = Number(item, "id"), price;
                string currency = Str(item, "coin_type");
                if (!available.ContainsKey(id) || Number(item, "privilege_required") != 0 || Number(item, "bag_gift") != 0
                    || (currency != "gold" && currency != "silver") || !LiveNonnegativeInteger(item["price"], out price) || price <= 0
                    || Number(item, "bind_roomid") > 0 && Number(item, "bind_roomid") != roomId) continue;
                long itemLimit = Number(item, "max_send_limit"), panelLimit = Number(giftData, "max_send_gift");
                int maximum = (int)Math.Min(999, Math.Min(itemLimit > 0 ? itemLimit : 999, panelLimit > 0 ? panelLimit : 999));
                mapped[id] = new LiveGift { Id = id, Name = PlainText(Str(item, "name")), UnitPrice = price,
                    Currency = currency, MaxCount = maximum, ImageUrl = ImageUrl(Str(item, "img_basic")) };
            }
            return new LiveGiftCatalog { RoomId = roomId, AccountMid = account?.Mid ?? 0, Items = mapped.Values.OrderBy(item => item.UnitPrice).ToList() };
        }

        public async Task<LiveGiftCatalog> GetLiveGiftBagAsync(long roomId, CancellationToken ct = default(CancellationToken))
        {
            CheckId(roomId, nameof(roomId));
            var account = CaptureInteractionAccount(false, ct);
            var root = await LiveGetAsync("/xlive/web-room/v1/gift/bag_list?room_id=" + Invariant(roomId), roomId, ct);
            EnsureInteractionAccount(account, ct);
            var data = RequireData(root);
            var list = data["list"] as JArray;
            if (list == null && data["list"]?.Type != JTokenType.Null) throw LiveFormatError("背包礼物");
            var result = new List<LiveGift>();
            foreach (var item in list ?? new JArray())
            {
                long id = Number(item, "gift_id"), bag = Number(item, "bag_id"), quantity = Number(item, "gift_num"), expiry = Number(item, "expire_at");
                if (id <= 0 || bag <= 0 || quantity <= 0 || expiry > 0 && expiry <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) continue;
                if (expiry > 253402300799L) continue;
                result.Add(new LiveGift { Id = id, BagId = bag, Name = PlainText(Str(item, "gift_name")), Quantity = quantity,
                    Currency = "bag", UnitPrice = 0, MaxCount = (int)Math.Min(999, quantity), ImageUrl = ImageUrl(FirstLiveText(item, "gift_img", "img_basic")),
                    ExpireAt = expiry > 0 ? (DateTimeOffset?)DateTimeOffset.FromUnixTimeSeconds(expiry) : null });
            }
            return new LiveGiftCatalog { RoomId = roomId, AccountMid = account.Mid, Items = result };
        }

        public async Task<LiveGiftQuote> PrepareLiveGiftAsync(long roomId, LiveGift gift, int count, CancellationToken ct = default(CancellationToken))
        {
            CheckId(roomId, nameof(roomId));
            if (gift == null || gift.Id <= 0 || gift.BagId < 0) throw new ArgumentException("请选择有效礼物。", nameof(gift));
            if (count < 1 || count > 999) throw new ArgumentOutOfRangeException(nameof(count), "每次赠送 1 至 999 个礼物。");
            var account = CaptureInteractionAccount(true, ct);
            var room = await GetLiveRoomAsync(roomId, ct);
            EnsureInteractionAccount(account, ct);
            if (room.Id != roomId || !room.IsLive) throw new BilibiliApiException(-1, "直播间已变化或未开播，请重新打开直播间。");
            var catalog = gift.IsBag ? await GetLiveGiftBagAsync(roomId, ct) : await GetLiveGiftCatalogAsync(roomId, ct);
            EnsureInteractionAccount(account, ct);
            var fresh = catalog.Items.FirstOrDefault(item => item.Id == gift.Id && item.BagId == gift.BagId);
            if (fresh == null || fresh.Name != gift.Name || fresh.UnitPrice != gift.UnitPrice || fresh.Currency != gift.Currency
                || fresh.IsBag && fresh.Quantity < count || count > fresh.MaxCount) throw new BilibiliApiException(-1, "礼物价格、库存、赠送数量上限或可赠送状态已改变，请刷新后重新选择。");
            if (fresh.UnitPrice > long.MaxValue / count) throw LiveFormatError("礼物总价");
            return new LiveGiftQuote { RoomId = roomId, OwnerMid = room.OwnerMid, AccountMid = account.Mid, GiftId = fresh.Id,
                BagId = fresh.BagId, GiftName = fresh.Name, Count = count, UnitPrice = fresh.UnitPrice, Currency = fresh.Currency,
                Session = account.Session, Csrf = account.Csrf, CreatedAt = DateTimeOffset.UtcNow };
        }

        public async Task SendLiveGiftAsync(LiveGiftQuote quote, bool confirmed, CancellationToken ct = default(CancellationToken))
        {
            if (!confirmed) throw new ArgumentException("请先确认直播间、礼物数量及费用。", nameof(confirmed));
            if (quote == null || quote.RoomId <= 0 || quote.GiftId <= 0 || quote.OwnerMid <= 0 || quote.Count < 1 || quote.Count > 999
                || string.IsNullOrWhiteSpace(quote.Session)) throw new ArgumentException("礼物确认信息无效，请重新选择。", nameof(quote));
            var account = CaptureInteractionAccount(true, ct);
            await _videoInteractionGate.WaitAsync(ct);
            try
            {
                EnsureInteractionAccount(account, ct);
                if (account.Mid != quote.AccountMid || account.Session != quote.Session || account.Csrf != quote.Csrf
                    || DateTimeOffset.UtcNow - quote.CreatedAt > TimeSpan.FromMinutes(2) || quote.Consumed)
                    throw new BilibiliApiException(-1, "礼物确认已失效或已经提交，请检查赠送记录后重新选择。");
                // Claim the quote before awaiting. Even timeouts never allow an automatic second spend.
                quote.Consumed = true;
                var fresh = await PrepareLiveGiftAsync(quote.RoomId, new LiveGift { Id = quote.GiftId, BagId = quote.BagId,
                    Name = quote.GiftName, UnitPrice = quote.UnitPrice, Currency = quote.Currency }, quote.Count, ct);
                EnsureInteractionAccount(account, ct);
                if (fresh.OwnerMid != quote.OwnerMid) throw new BilibiliApiException(-1, "主播信息已改变，请重新确认礼物。");
                await PrepareInteractionMutationAsync(account, ct);
                var form = new Dictionary<string, string>
                {
                    ["uid"] = Invariant(account.Mid), ["ruid"] = Invariant(quote.OwnerMid), ["gift_id"] = Invariant(quote.GiftId),
                    ["gift_num"] = Invariant(quote.Count), ["bag_id"] = Invariant(quote.BagId), ["price"] = Invariant(quote.UnitPrice),
                    ["platform"] = "pc", ["send_ruid"] = "0", ["storm_beat_id"] = "0", ["biz_code"] = "live", ["biz_id"] = Invariant(quote.RoomId),
                    ["roomid"] = Invariant(quote.RoomId), ["rnd"] = Invariant(DateTimeOffset.UtcNow.ToUnixTimeSeconds()), ["visit_id"] = "",
                    ["csrf"] = account.Csrf, ["csrf_token"] = account.Csrf
                };
                if (quote.BagId == 0) form["coin_type"] = quote.Currency;
                string path = quote.BagId > 0 ? "/xlive/revenue/v2/gift/sendBag" : quote.Currency == "gold"
                    ? "/xlive/revenue/v1/gift/sendGold" : quote.Currency == "silver" ? "/xlive/revenue/v1/gift/sendSilver" : null;
                if (path == null) throw new BilibiliApiException(-1, "礼物支付方式已变化，请重新选择。");
                var root = await LivePostAsync(path, quote.RoomId, form, ct);
                EnsureInteractionAccount(account, ct);
                RequireInteractionSuccess(root);
            }
            finally { _videoInteractionGate.Release(); }
        }

        private Task<JObject> LiveGetAsync(string path, long roomId, CancellationToken ct)
        {
            return LiveRequestAsync(HttpMethod.Get, path, roomId, null, ct);
        }

        private Task<JObject> LivePostAsync(string path, long roomId, IDictionary<string, string> form, CancellationToken ct)
        {
            return LiveRequestAsync(HttpMethod.Post, path, roomId, form, ct);
        }

        private async Task<JObject> LiveRequestAsync(HttpMethod method, string path, long roomId, IDictionary<string, string> form, CancellationToken ct)
        {
            var request = new HttpRequestMessage(method, new Uri(LiveApi + path));
            request.Headers.Referer = new Uri("https://live.bilibili.com/" + (roomId > 0 ? Invariant(roomId) : ""));
            if (form != null)
            {
                request.Content = new HttpFormUrlEncodedContent(form);
                request.Headers.TryAppendWithoutValidation("Origin", "https://live.bilibili.com");
            }
            return ParseJson(await SendTextAsync(request, ct));
        }

        private static LiveRoom MapLiveRoom(JToken item)
        {
            return new LiveRoom { Id = Number(item, "roomid"), OwnerMid = Number(item, "uid"), Title = PlainText(Str(item, "title")),
                OwnerName = PlainText(Str(item, "uname")), AvatarUrl = ImageUrl(Str(item, "face")), CoverUrl = ImageUrl(FirstLiveText(item, "cover", "user_cover", "system_cover")),
                Area = PlainText(FirstLiveText(item, "area_v2_name", "area_name")), AreaId = Number(item, "area_v2_id"),
                ParentAreaId = Number(item, "area_v2_parent_id"), Online = Number(item, "online"), IsLive = true };
        }

        private static string FirstLiveText(JToken item, params string[] names)
        {
            foreach (string name in names) if (!string.IsNullOrWhiteSpace(Str(item, name))) return Str(item, name);
            return "";
        }

        private static bool LiveNonnegativeInteger(JToken token, out long value)
        {
            value = 0;
            return token != null && token.Type == JTokenType.Integer && long.TryParse(token.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 0;
        }

        private static BilibiliApiException LiveFormatError(string name) => new BilibiliApiException(-1, "B站未返回有效的" + name + "，请稍后重试。");
    }
}
