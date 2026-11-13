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
        private sealed class InteractionAccount
        {
            internal long Mid;
            internal string Session;
            internal string Csrf;
        }

        public async Task<VideoInteractionState> GetVideoInteractionsAsync(long aid,
            CancellationToken ct = default(CancellationToken))
        {
            CheckId(aid, nameof(aid));
            var account = CaptureInteractionAccount(false, ct);
            var likeRoot = await GetJsonAsync(Api + "/x/web-interface/archive/has/like?aid=" + Invariant(aid), ct);
            EnsureInteractionAccount(account, ct);
            var likeValue = InteractionData(likeRoot);
            int liked = InteractionInteger(likeValue, 0, 1, "点赞状态");
            int coins = await GetVideoCoinsCoreAsync(aid, account, ct);
            var favoriteRoot = await GetJsonAsync(Api + "/x/v2/fav/video/favoured?aid=" + Invariant(aid), ct);
            EnsureInteractionAccount(account, ct);
            var favorite = RequireData(favoriteRoot);
            return new VideoInteractionState
            {
                Aid = aid, AccountMid = account.Mid, IsLiked = liked == 1, Coins = coins,
                IsFavorited = InteractionBoolean(favorite["favoured"], "收藏状态")
            };
        }

        public async Task<IReadOnlyList<VideoFavoriteFolder>> GetVideoFavoriteFoldersAsync(long aid,
            CancellationToken ct = default(CancellationToken))
        {
            CheckId(aid, nameof(aid));
            var account = CaptureInteractionAccount(false, ct);
            return await GetVideoFavoriteFoldersCoreAsync(aid, account, ct);
        }

        public async Task SetVideoLikeAsync(long aid, bool like,
            CancellationToken ct = default(CancellationToken))
        {
            CheckId(aid, nameof(aid));
            var account = CaptureInteractionAccount(true, ct);
            await _videoInteractionGate.WaitAsync(ct);
            try
            {
                await PrepareInteractionMutationAsync(account, ct);
                var root = await PostJsonAsync(Api + "/x/web-interface/archive/like", new Dictionary<string, string>
                {
                    ["aid"] = Invariant(aid), ["like"] = like ? "1" : "2", ["csrf"] = account.Csrf
                }, ct);
                EnsureInteractionAccount(account, ct);
                RequireInteractionSuccess(root);
            }
            finally { _videoInteractionGate.Release(); }
        }

        public async Task AddVideoCoinsAsync(long aid, int count,
            CancellationToken ct = default(CancellationToken))
        {
            CheckId(aid, nameof(aid));
            if (count < 1 || count > 2) throw new ArgumentOutOfRangeException(nameof(count), "一次只能投 1 或 2 枚硬币。");
            var account = CaptureInteractionAccount(true, ct);
            await _videoInteractionGate.WaitAsync(ct);
            try
            {
                EnsureInteractionAccount(account, ct);
                int invested = await GetVideoCoinsCoreAsync(aid, account, ct);
                if (invested + count > 2)
                    throw new BilibiliApiException(-1, "此视频已投 " + Invariant(invested) + " 枚硬币，不能超过平台投币上限。");
                await PrepareInteractionMutationAsync(account, ct);
                var root = await PostJsonAsync(Api + "/x/web-interface/coin/add", new Dictionary<string, string>
                {
                    ["aid"] = Invariant(aid), ["multiply"] = Invariant(count),
                    ["select_like"] = "0", ["csrf"] = account.Csrf
                }, ct);
                EnsureInteractionAccount(account, ct);
                RequireInteractionSuccess(root);
                // select_like=0 means the optional like acknowledgement must
                // not be mistaken for whether the requested coins succeeded.
            }
            finally { _videoInteractionGate.Release(); }
        }

        public async Task UpdateVideoFavoritesAsync(long aid, IEnumerable<long> addIds, IEnumerable<long> removeIds,
            CancellationToken ct = default(CancellationToken))
        {
            CheckId(aid, nameof(aid));
            var added = InteractionFolderIds(addIds, nameof(addIds));
            var removed = InteractionFolderIds(removeIds, nameof(removeIds));
            if (added.Intersect(removed).Any()) throw new ArgumentException("同一收藏夹不能同时加入和移除。");
            var account = CaptureInteractionAccount(true, ct);
            await _videoInteractionGate.WaitAsync(ct);
            try
            {
                EnsureInteractionAccount(account, ct);
                if (added.Count == 0 && removed.Count == 0) return;
                // Verify fresh ownership with the same session immediately
                // before changing folders. Never trust IDs left in a UI cache.
                var folders = await GetVideoFavoriteFoldersCoreAsync(aid, account, ct);
                var ownIds = new HashSet<long>(folders.Select(folder => folder.Id));
                if (added.Concat(removed).Any(id => !ownIds.Contains(id)))
                    throw new BilibiliApiException(-1, "收藏夹已改变或不属于当前账户，请刷新后重新选择。");
                await PrepareInteractionMutationAsync(account, ct);
                var root = await PostJsonAsync(Api + "/x/v3/fav/resource/deal", new Dictionary<string, string>
                {
                    ["rid"] = Invariant(aid), ["type"] = "2",
                    ["add_media_ids"] = string.Join(",", added.Select(Invariant)),
                    ["del_media_ids"] = string.Join(",", removed.Select(Invariant)),
                    ["csrf"] = account.Csrf
                }, ct);
                EnsureInteractionAccount(account, ct);
                RequireInteractionSuccess(root);
            }
            finally { _videoInteractionGate.Release(); }
        }

        public async Task<VideoTripleResult> TripleVideoAsync(long aid,
            CancellationToken ct = default(CancellationToken))
        {
            CheckId(aid, nameof(aid));
            var account = CaptureInteractionAccount(true, ct);
            await _videoInteractionGate.WaitAsync(ct);
            try
            {
                await PrepareInteractionMutationAsync(account, ct);
                // The official operation chooses its own coin count (usually
                // two) and the default folder. Do not emulate it with multiple
                // POSTs or invent an unsupported multiply parameter.
                var root = await PostJsonAsync(Api + "/x/web-interface/archive/like/triple", new Dictionary<string, string>
                { ["aid"] = Invariant(aid), ["csrf"] = account.Csrf }, ct);
                EnsureInteractionAccount(account, ct);
                var data = RequireData(root);
                bool coin = InteractionBoolean(data["coin"], "三连投币结果");
                int coins = InteractionInteger(data["multiply"], 0, 2, "三连投币数量");
                if (coin && coins == 0) throw InteractionFormatError("三连投币数量");
                return new VideoTripleResult
                {
                    Like = InteractionBoolean(data["like"], "三连点赞结果"), Coin = coin,
                    Favorite = InteractionBoolean(data["fav"], "三连收藏结果"), Coins = coins
                };
            }
            finally { _videoInteractionGate.Release(); }
        }

        private async Task<int> GetVideoCoinsCoreAsync(long aid, InteractionAccount account, CancellationToken ct)
        {
            var root = await GetJsonAsync(Api + "/x/web-interface/archive/coins?aid=" + Invariant(aid), ct);
            EnsureInteractionAccount(account, ct);
            var data = RequireData(root);
            return InteractionInteger(data["multiply"], 0, 2, "已投硬币数");
        }

        private async Task<IReadOnlyList<VideoFavoriteFolder>> GetVideoFavoriteFoldersCoreAsync(long aid,
            InteractionAccount account, CancellationToken ct)
        {
            EnsureInteractionAccount(account, ct);
            var root = await GetJsonAsync(Api + "/x/v3/fav/folder/created/list-all?up_mid="
                + Invariant(account.Mid) + "&type=2&rid=" + Invariant(aid), ct);
            EnsureInteractionAccount(account, ct);
            var data = RequireData(root);
            int count = InteractionInteger(data["count"], 0, 10000, "收藏夹数量");
            var list = data["list"] as JArray;
            if (list == null)
            {
                if (count == 0 && data["list"]?.Type == JTokenType.Null) return Array.Empty<VideoFavoriteFolder>();
                throw InteractionFormatError("收藏夹列表");
            }
            var folders = new List<VideoFavoriteFolder>();
            foreach (var item in list)
            {
                if (!(item is JObject) || Number(item, "id") <= 0 || Number(item, "mid") != account.Mid
                    || folders.Any(folder => folder.Id == Number(item, "id"))) throw InteractionFormatError("收藏夹归属");
                int selected = InteractionInteger(item["fav_state"], 0, 1, "收藏夹状态");
                long mediaCount;
                if (!long.TryParse(Str(item, "media_count"), NumberStyles.None, CultureInfo.InvariantCulture, out mediaCount))
                    throw InteractionFormatError("收藏夹内容数量");
                long attr;
                bool attrKnown = long.TryParse(Str(item, "attr"), NumberStyles.None, CultureInfo.InvariantCulture, out attr);
                folders.Add(new VideoFavoriteFolder
                {
                    Id = Number(item, "id"), Title = PlainText(Str(item, "title")), Count = mediaCount,
                    IsSelected = selected == 1, IsDefault = attrKnown && (attr & 2L) == 0
                });
            }
            if (folders.Count != count) throw InteractionFormatError("完整收藏夹列表");
            return folders;
        }

        private InteractionAccount CaptureInteractionAccount(bool mutation, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); ThrowIfDisposed();
            var user = CurrentUser;
            if (!IsLoggedIn || user == null || user.Mid <= 0)
                throw new BilibiliApiException(-101, "请先扫码登录，再进行视频互动。");
            var account = new InteractionAccount
            { Mid = user.Mid, Session = _cookies.GetCookieValue("SESSDATA"), Csrf = _cookies.GetCookieValue("bili_jct") ?? "" };
            if (mutation) account.Csrf = RequireSession();
            EnsureInteractionAccount(account, ct);
            return account;
        }

        private void EnsureInteractionAccount(InteractionAccount account, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); ThrowIfDisposed();
            if (!IsLoggedIn || CurrentUser == null || CurrentUser.Mid != account.Mid
                || !string.Equals(_cookies.GetCookieValue("SESSDATA"), account.Session, StringComparison.Ordinal)
                || !string.Equals(_cookies.GetCookieValue("bili_jct") ?? "", account.Csrf, StringComparison.Ordinal))
                throw new BilibiliApiException(-101, "登录账户或会话已改变，请刷新视频互动状态后再操作。");
        }

        private async Task PrepareInteractionMutationAsync(InteractionAccount account, CancellationToken ct)
        {
            EnsureInteractionAccount(account, ct);
            if (string.IsNullOrEmpty(_cookies.GetCookieValue("buvid3"))) await BootstrapWebsiteAsync(ct);
            EnsureInteractionAccount(account, ct);
        }

        private void RequireInteractionSuccess(JObject root)
        {
            int code = ResponseCode(root);
            if (code == 0) return;
            if (code == -101) CurrentUser = null;
            throw ApiError(code, Str(root, "message"));
        }

        private JToken InteractionData(JObject root)
        {
            RequireInteractionSuccess(root);
            if (root["data"] == null || root["data"].Type == JTokenType.Null) throw InteractionFormatError("视频互动状态");
            return root["data"];
        }

        private static int InteractionInteger(JToken token, int minimum, int maximum, string name)
        {
            int value;
            if (token == null || token.Type != JTokenType.Integer || !int.TryParse(token.ToString(), NumberStyles.None,
                CultureInfo.InvariantCulture, out value) || value < minimum || value > maximum)
                throw InteractionFormatError(name);
            return value;
        }

        private static bool InteractionBoolean(JToken token, string name)
        {
            if (token == null || token.Type != JTokenType.Boolean) throw InteractionFormatError(name);
            return token.Value<bool>();
        }

        private static List<long> InteractionFolderIds(IEnumerable<long> ids, string name)
        {
            var result = new List<long>();
            if (ids == null) return result;
            foreach (long id in ids)
            {
                if (id <= 0 || result.Count >= 1000) throw new ArgumentException("收藏夹选择无效，请刷新后重试。", name);
                if (!result.Contains(id)) result.Add(id);
            }
            return result;
        }

        private static BilibiliApiException InteractionFormatError(string name)
            => new BilibiliApiException(-1, "B站未返回有效的" + name + "，请刷新后重试；不要重复提交尚未确认的操作。");
    }
}
