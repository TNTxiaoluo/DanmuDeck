using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace BiliXbox.Services
{
    // Extra metadata for the video introduction panel.
    public sealed partial class BilibiliClient
    {
        public async Task<IReadOnlyList<string>> GetVideoTagsAsync(string bvid, CancellationToken ct = default(CancellationToken))
        {
            var root = await GetJsonAsync(Api + "/x/tag/archive/tags?bvid=" + Uri.EscapeDataString(bvid ?? ""), ct);
            if (ResponseCode(root) != 0) return Array.Empty<string>();
            return (root["data"] as JArray ?? new JArray())
                .Select(tag => Str(tag, "tag_name").Trim()).Where(name => name.Length > 0).Distinct().ToArray();
        }

        // "10" / "1000+" people watching this part now; empty when unknown.
        public async Task<string> GetOnlineCountAsync(string bvid, long cid, CancellationToken ct = default(CancellationToken))
        {
            var root = await GetJsonAsync(Api + "/x/player/online/total?bvid=" + Uri.EscapeDataString(bvid ?? "") + "&cid=" + Invariant(cid), ct);
            if (ResponseCode(root) != 0) return "";
            return Str(root["data"], "total");
        }
    }
}
