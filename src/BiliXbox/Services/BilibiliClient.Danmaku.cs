using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Models;
using Newtonsoft.Json.Linq;

namespace BiliXbox.Services
{
    public sealed partial class BilibiliClient
    {
        public async Task<DanmakuBlockRuleSet> GetDanmakuBlockRulesAsync(CancellationToken ct = default(CancellationToken))
        {
            ThrowIfDisposed();
            ct.ThrowIfCancellationRequested();
            if (!IsLoggedIn || CurrentUser.Mid <= 0) throw new BilibiliApiException(-101, "请先扫码登录，再同步账户的弹幕屏蔽列表。");
            long account = CurrentUser.Mid;
            string session = _cookies.GetCookieValue("SESSDATA");
            var data = RequireData(await GetJsonAsync(Api + "/x/dm/filter/user", ct));
            ct.ThrowIfCancellationRequested();
            if (!IsLoggedIn || CurrentUser.Mid != account || !string.Equals(session, _cookies.GetCookieValue("SESSDATA"), StringComparison.Ordinal))
                throw new OperationCanceledException("同步期间账户发生变化，已丢弃原账户的屏蔽列表。", ct);
            return ParseDanmakuBlockRules(data, account, DateTimeOffset.UtcNow);
        }

        // The website's HttpDmBlockList reads data.rule in a single authenticated
        // GET. Never call its add/add2/del endpoints while pulling account rules.
        internal static DanmakuBlockRuleSet ParseDanmakuBlockRules(JToken data, long account, DateTimeOffset syncedAt)
        {
            if (account <= 0) throw new ArgumentOutOfRangeException(nameof(account));
            var array = data as JArray ?? data?["rule"] as JArray ?? data?["rules"] as JArray ?? data?["list"] as JArray;
            if (array == null) throw new BilibiliApiException(-1, "B站未返回有效的账户弹幕屏蔽列表，原有列表已保留。");
            if (array.Count > 10000) throw new BilibiliApiException(-1, "账户弹幕屏蔽列表超出本客户端安全读取上限。");
            if (!(data is JArray) && (Flag(data, "has_more") || Flag(data, "hasMore") || Number(data, "total") > array.Count))
                throw new BilibiliApiException(-1, "B站返回了不完整的弹幕屏蔽列表，暂未替换原有列表。");
            var rules = new List<DanmakuBlockRule>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int unsupported = 0;
            foreach (var item in array)
            {
                long type;
                if (!(item is JObject) || !long.TryParse(Str(item, "type"), NumberStyles.Integer, CultureInfo.InvariantCulture, out type))
                    throw new BilibiliApiException(-1, "账户弹幕屏蔽规则格式已变化，原有列表已保留。");
                if (type < 0 || type > 2) { ++unsupported; continue; }
                var value = Str(item, "filter");
                if (value.Length == 0) value = Str(item, "value");
                if (value.Length == 0 || value.Length > 4096)
                    throw new BilibiliApiException(-1, "账户弹幕屏蔽规则内容无效，原有列表已保留。");
                bool enabled = !(item["opened"]?.Type == JTokenType.Boolean && !(bool)item["opened"])
                    && !(item["enabled"]?.Type == JTokenType.Boolean && !(bool)item["enabled"]);
                var key = type.ToString(CultureInfo.InvariantCulture) + ":" + (type == 0 || type == 2 ? value.ToUpperInvariant() : value);
                if (!seen.Add(key)) continue;
                rules.Add(new DanmakuBlockRule { Id = Number(item, "id"), Type = (DanmakuBlockRuleType)type, Value = value, Enabled = enabled });
            }
            return new DanmakuBlockRuleSet
            {
                Rules = rules, AccountMid = account, SyncedAt = syncedAt, SourceCount = array.Count, UnsupportedCount = unsupported,
                Notice = unsupported == 0 ? "" : "有 " + unsupported + " 条云端规则类型暂不受支持，其余规则已同步。"
            };
        }

        public async Task<DanmakuMaskInfo> GetDanmakuMaskInfoAsync(VideoDetail video, CancellationToken ct = default(CancellationToken))
        {
            if (video == null) throw new ArgumentNullException(nameof(video));
            CheckId(video.Cid, nameof(video.Cid));
            var query = new Dictionary<string, string> { ["cid"] = Invariant(video.Cid) };
            if (video.Aid > 0) query["aid"] = Invariant(video.Aid);
            else if (!string.IsNullOrWhiteSpace(video.Bvid)) query["bvid"] = video.Bvid;
            else throw new ArgumentException("人物遮罩需要有效的视频标识。", nameof(video));
            var data = RequireData(await GetSignedJsonAsync("/x/player/wbi/v2", query, ct));
            var mask = data["dm_mask"];
            if (mask == null || mask.Type == JTokenType.Null || string.IsNullOrWhiteSpace(Str(mask, "mask_url"))) return null;
            string url = MediaUrl(Str(mask, "mask_url"));
            if (url.Length == 0) throw new BilibiliApiException(-1, "B站返回的人物遮罩地址无效。");
            double fps;
            if (!double.TryParse(Str(mask, "fps"), NumberStyles.Float, CultureInfo.InvariantCulture, out fps) ||
                double.IsNaN(fps) || double.IsInfinity(fps) || fps <= 0 || fps > 120) fps = 24;
            return new DanmakuMaskInfo { Url = url, Fps = fps };
        }
    }
}
