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
    public sealed class AccountStats
    {
        public double Coins { get; set; } = -1;
        public double BCoins { get; set; } = -1;
        public long Following { get; set; } = -1;
        public long Followers { get; set; } = -1;
    }

    public sealed class RelationPage
    {
        public IReadOnlyList<CreatorProfile> Items { get; set; } = Array.Empty<CreatorProfile>();
        public long Total { get; set; }
        public bool HasMore { get; set; }
    }

    public sealed partial class BilibiliClient
    {
        // Coins and B-coins come from the signed-in nav; following and
        // follower counts from relation/stat. Either part may be missing.
        public async Task<AccountStats> GetAccountStatsAsync(CancellationToken ct = default(CancellationToken))
        {
            long owner = RequireLibraryAccount();
            var stats = new AccountStats();
            try
            {
                var nav = RequireData(await GetJsonAsync(Api + "/x/web-interface/nav", ct));
                if (nav["isLogin"]?.Value<bool>() == true && Number(nav, "mid") == owner)
                {
                    stats.Coins = Decimal(nav, "money");
                    stats.BCoins = Decimal(nav["wallet"], "bcoin_balance");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { /* Partial statistics are still shown. */ }
            try
            {
                var relation = RequireData(await GetJsonAsync(Api + "/x/relation/stat?vmid=" + Invariant(owner), ct));
                stats.Following = Number(relation, "following");
                stats.Followers = Number(relation, "follower");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { }
            EnsureLibraryAccount(owner, ct);
            return stats;
        }

        public async Task<RelationPage> GetRelationsAsync(bool followers, int page = 1,
            CancellationToken ct = default(CancellationToken))
        {
            CheckPage(page);
            long owner = RequireLibraryAccount();
            const int size = 50;
            var data = RequireData(await GetJsonAsync(Api + (followers ? "/x/relation/fans" : "/x/relation/followings")
                + "?vmid=" + Invariant(owner) + "&pn=" + Invariant(page) + "&ps=" + size + "&order=desc", ct));
            EnsureLibraryAccount(owner, ct);
            var items = (data["list"] as JArray ?? new JArray()).Where(item => Number(item, "mid") > 0)
                .Select(item => new CreatorProfile
                {
                    Mid = Number(item, "mid"), Name = Str(item, "uname"), AvatarUrl = ImageUrl(Str(item, "face")),
                    Signature = Str(item, "sign")
                }).ToList();
            long total = Number(data, "total");
            return new RelationPage { Items = items, Total = total, HasMore = items.Count >= size && (total <= 0 || page * size < total) };
        }

        private static double Decimal(JToken item, string name)
        {
            double value;
            return item != null && double.TryParse(Str(item, name), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value >= 0 ? value : -1;
        }
    }
}
