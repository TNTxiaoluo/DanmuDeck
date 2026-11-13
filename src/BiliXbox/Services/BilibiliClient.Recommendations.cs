using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Models;
using Newtonsoft.Json.Linq;

namespace BiliXbox.Services
{
    public sealed partial class BilibiliClient
    {
        private readonly SemaphoreSlim _recommendationGate = new SemaphoreSlim(1, 1);
        private readonly Dictionary<int, RecommendationPage> _recommendationPages
            = new Dictionary<int, RecommendationPage>();
        private readonly List<string> _recommendationShownBatches = new List<string>();
        private readonly HashSet<string> _recommendationShownVideos = new HashSet<string>(StringComparer.Ordinal);
        private long _recommendationAccount = -1;
        private int _recommendationFreshIndex;
        private int _recommendationFreshHourIndex;
        private int _recommendationPopularPage;
        private DateTimeOffset _recommendationHour = DateTimeOffset.UtcNow;

        /// <summary>
        /// Website home recommendations. Refresh starts a new visible page sequence;
        /// previously displayed server IDs remain in the normal last_showlist parameter.
        /// Going back returns the original page, rather than asking the server again.
        /// </summary>
        public async Task<RecommendationPage> GetRecommendationsAsync(int page = 1, bool refresh = false,
            CancellationToken ct = default(CancellationToken))
        {
            CheckPage(page);
            if (refresh && page != 1) throw new ArgumentException("刷新推荐时请从第1页开始。", nameof(page));
            await _recommendationGate.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                var account = IsLoggedIn ? CurrentUser.Mid : 0;
                var accountChanged = account != _recommendationAccount;
                if (accountChanged)
                {
                    // Never show one account's cached recommendations after a login change.
                    _recommendationPages.Clear();
                    _recommendationShownBatches.Clear();
                    _recommendationShownVideos.Clear();
                    _recommendationAccount = account;
                    _recommendationFreshIndex = 0;
                    _recommendationFreshHourIndex = 0;
                    _recommendationPopularPage = 0;
                }
                RecommendationPage cached;
                if (!refresh && _recommendationPages.TryGetValue(page, out cached)) return cached;
                if (page > 1 && !_recommendationPages.ContainsKey(page - 1))
                    throw new BilibiliApiException(-1, "请先从第1页加载推荐，再继续翻页。");

                var index = _recommendationFreshIndex;
                var hourIndex = DateTimeOffset.UtcNow - _recommendationHour >= TimeSpan.FromHours(1)
                    ? 0 : _recommendationFreshHourIndex;
                var parameters = new Dictionary<string, string>
                {
                    ["fresh_type"] = index == 0 ? "0" : refresh ? "5" : "4",
                    ["fresh_idx"] = Invariant(index), ["fresh_idx_1h"] = Invariant(hourIndex),
                    ["feed_version"] = "V8", ["ps"] = "20", ["y_num"] = "4",
                    ["fetch_row"] = Invariant(page), ["web_location"] = "1430650",
                    ["last_showlist"] = string.Join(";", _recommendationShownBatches)
                };
                RecommendationPage result;
                string shownBatch = "";
                var popularPage = _recommendationPopularPage;
                try
                {
                    JToken data;
                    try
                    {
                        data = RequireData(await GetSignedJsonAsync("/x/web-interface/wbi/index/top/feed/rcmd", parameters, ct));
                    }
                    catch (BilibiliApiException)
                    {
                        // This is Bilibili's public server-rendered homepage endpoint.
                        // It uses the same real session cookies; no fabricated device identity.
                        data = RequireData(await GetJsonAsync(Api + "/x/web-interface/index/top/feed/rcmd?"
                            + string.Join("&", parameters.Select(pair => Uri.EscapeDataString(pair.Key)
                                + "=" + Uri.EscapeDataString(pair.Value))), ct));
                    }
                    var items = data["item"] as JArray;
                    if (items == null) throw new BilibiliApiException(-1, "B站推荐数据格式已变化。");
                    var shown = new List<string>();
                    var videos = new List<VideoItem>();
                    var unique = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var item in items)
                    {
                        // Live rooms, login cards, and ads are not playable archive videos.
                        if (Str(item, "goto") != "av" || Number(item, "id") <= 0) continue;
                        var video = MapVideo(item);
                        video.Aid = Number(item, "id");
                        if (string.IsNullOrWhiteSpace(video.Bvid) || !unique.Add(video.Bvid)
                            || _recommendationShownVideos.Contains(video.Bvid)) continue;
                        videos.Add(video);
                        shown.Add("av_n_" + Invariant(video.Aid));
                    }
                    shownBatch = string.Join(",", shown);
                    var identified = account > 0 && Number(data, "mid") == account;
                    result = new RecommendationPage
                    {
                        Videos = videos, HasMore = videos.Count > 0,
                        IsPersonalized = identified,
                        Notice = account > 0 && !identified
                            ? "推荐服务未识别当前登录，现显示普通推荐；请重新扫码登录后刷新。" : ""
                    };
                    if (videos.Count == 0 && items.Count > 0)
                        result.Notice = "本批推荐暂无新视频，请稍后刷新。";
                }
                catch (BilibiliApiException ex)
                {
                    ct.ThrowIfCancellationRequested();
                    // A refresh advances the actual popular page as a visible fallback.
                    // It never shuffles the same fixed popular page or calls it personalized.
                    popularPage++;
                    var videos = await GetPopularAsync(popularPage, ct);
                    var unique = new HashSet<string>(StringComparer.Ordinal);
                    var fresh = videos.Where(video => !string.IsNullOrWhiteSpace(video.Bvid)
                        && unique.Add(video.Bvid) && !_recommendationShownVideos.Contains(video.Bvid)).ToList();
                    result = new RecommendationPage
                    {
                        Videos = fresh, HasMore = fresh.Count > 0 && videos.Count >= 20,
                        IsPopularFallback = true,
                        Notice = "推荐暂不可用，当前显示热门视频（非个性化推荐）。" + ex.Message
                    };
                }
                ct.ThrowIfCancellationRequested();
                var currentAccount = IsLoggedIn ? CurrentUser.Mid : 0;
                // Do not commit a response after another login completed while it was in flight.
                if (currentAccount != account)
                    throw new BilibiliApiException(-101, "登录状态已变化，请刷新推荐。");
                if (refresh) _recommendationPages.Clear();
                _recommendationPages[page] = result;
                _recommendationFreshIndex = checked(index + 1);
                _recommendationFreshHourIndex = checked(hourIndex + 1);
                if (hourIndex == 0) _recommendationHour = DateTimeOffset.UtcNow;
                _recommendationPopularPage = popularPage;
                if (shownBatch.Length > 0)
                {
                    _recommendationShownBatches.Add(shownBatch);
                    if (_recommendationShownBatches.Count > 10) _recommendationShownBatches.RemoveAt(0);
                }
                foreach (var video in result.Videos) _recommendationShownVideos.Add(video.Bvid);
                return result;
            }
            finally { _recommendationGate.Release(); }
        }
    }
}
