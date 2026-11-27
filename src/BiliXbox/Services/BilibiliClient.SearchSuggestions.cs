using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace BiliXbox.Services
{
    public sealed partial class BilibiliClient
    {
        // The website's "bilibili 热搜" list shown under its search box.
        // Returns plain keywords; empty when the platform gives none.
        public async Task<IReadOnlyList<string>> GetTrendingSearchesAsync(int limit = 10,
            CancellationToken ct = default(CancellationToken))
        {
            limit = Math.Max(1, Math.Min(20, limit));
            var root = await GetSignedJsonAsync("/x/web-interface/wbi/search/square",
                new Dictionary<string, string> { ["limit"] = limit.ToString(System.Globalization.CultureInfo.InvariantCulture) }, ct);
            var data = RequireData(root);
            var list = data["trending"]?["list"] as JArray ?? new JArray();
            return list.Select(item => Str(item, "keyword").Trim())
                .Where(keyword => keyword.Length > 0 && keyword.Length <= 100)
                .Distinct(StringComparer.Ordinal).Take(limit).ToList();
        }
    }
}
