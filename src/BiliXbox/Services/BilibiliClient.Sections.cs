using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Models;
using Newtonsoft.Json.Linq;

namespace BiliXbox.Services
{
    public sealed partial class BilibiliClient
    {
        // These are the legacy rid values used by newlist, not tid_v2 IDs.
        public async Task<SearchPage> GetSectionVideosAsync(int regionId, int page = 1,
            CancellationToken ct = default(CancellationToken))
        {
            CheckPage(page);
            if (regionId != 0 && regionId != 1 && regionId != 181)
                throw new ArgumentOutOfRangeException(nameof(regionId));
            var endpoint = regionId == 0 ? "/x/web-interface/popular?ps=20&pn="
                : "/x/web-interface/newlist?rid=" + Invariant(regionId) + "&ps=20&pn=";
            var data = RequireData(await GetJsonAsync(Api + endpoint + Invariant(page), ct));
            ct.ThrowIfCancellationRequested();
            var raw = data[regionId == 0 ? "list" : "archives"] as JArray;
            if (raw == null) throw new BilibiliApiException(-1, "B站未返回有效的分区视频列表，请稍后刷新。");
            long responsePage = Number(data["page"], "num");
            if (responsePage > 0 && responsePage != page)
                throw new BilibiliApiException(-1, "分区页码与请求不符，请返回第1页刷新。");
            long total = Number(data["page"], "count");
            // newlist can return count=0 together with a full, valid page.
            // Zero is unknown in that response, rather than proof of no next page.
            bool more = total > 0 ? (long)page * 20 < total : raw.Count >= 20;
            if (data["no_more"]?.Type == JTokenType.Boolean && data["no_more"].Value<bool>()) more = false;
            var items = raw.Select(MapVideo).Where(item => !string.IsNullOrWhiteSpace(item.Bvid))
                .GroupBy(item => item.Bvid, StringComparer.Ordinal).Select(group => group.First()).ToArray();
            return new SearchPage { Videos = items, Total = total, HasMore = more && items.Length > 0 };
        }
    }
}
