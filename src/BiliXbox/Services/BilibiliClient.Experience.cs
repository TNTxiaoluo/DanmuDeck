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
        public async Task<IReadOnlyList<VideoItem>> GetRelatedVideosAsync(string bvid, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(bvid) || !System.Text.RegularExpressions.Regex.IsMatch(bvid, "^BV[0-9A-Za-z]{10}$"))
                throw new ArgumentException("无效的视频 BV 号", nameof(bvid));
            var root = await GetJsonAsync(Api + "/x/web-interface/archive/related?bvid=" + Uri.EscapeDataString(bvid), ct);
            ct.ThrowIfCancellationRequested();
            int code = ResponseCode(root); if (code != 0) throw ApiError(code, Str(root, "message"));
            var items = root["data"] as JArray;
            if (items == null) throw new BilibiliApiException(-1, "B站未返回有效的相关推荐列表，请重试。");
            return items.Where(item => item is JObject).Select(MapVideo)
                .Where(item => System.Text.RegularExpressions.Regex.IsMatch(item.Bvid ?? "", "^BV[0-9A-Za-z]{10}$") && item.Bvid != bvid)
                .GroupBy(item => item.Bvid, StringComparer.Ordinal).Select(group => group.First()).Take(40).ToArray();
        }
        public async Task AddWatchLaterAsync(long aid, CancellationToken ct)
        {
            CheckId(aid, nameof(aid)); long account = RequireLibraryAccount();
            long epoch = _loginSessionEpoch;
            var sessionCookies = _cookies.CloneForLogin();
            var form = new Dictionary<string,string> { ["aid"] = Invariant(aid), ["csrf"] = RequireSession() };
            var request = new Windows.Web.Http.HttpRequestMessage(Windows.Web.Http.HttpMethod.Post, new Uri(Api + "/x/v2/history/toview/add"))
                { Content = new Windows.Web.Http.HttpFormUrlEncodedContent(form) };
            request.Headers.TryAppendWithoutValidation("Origin", "https://www.bilibili.com");
            // Bind the mutation to the exact session selected at A/confirm, never a later account's cookie jar.
            var root = await SendSessionJsonAsync(request, sessionCookies, ct);
            int code = ResponseCode(root); if (code != 0) throw ApiError(code, Str(root, "message"));
            EnsureLibraryAccount(account, ct);
            if (epoch != _loginSessionEpoch) throw new OperationCanceledException("登录会话已变更");
        }
        public async Task<AccountLibraryPage> GetWatchLaterAsync(int page = 1, CancellationToken ct = default(CancellationToken))
        {
            CheckPage(page); long account = RequireLibraryAccount();
            var data = RequireData(await GetJsonAsync(Api + "/x/v2/history/toview", ct));
            EnsureLibraryAccount(account, ct);
            var raw = data["list"] as JArray;
            if (raw == null && Number(data, "count") != 0) throw LibraryFormatError("稍后再看");
            var all = (raw ?? new JArray()).Select(item =>
            {
                var video = MapVideo(item);
                return new AccountLibraryItem { Key = "watchlater:" + video.Bvid, Bvid = video.Bvid,
                    Aid = video.Aid, Title = video.Title, CoverUrl = video.CoverUrl, Owner = video.Owner,
                    OwnerMid = video.OwnerMid, DurationText = video.DurationText, PlayText = video.PlayText,
                    Cid = Number(item, "cid"), ProgressSeconds = Math.Max(0, Number(item, "progress")) };
            }).Where(item => !string.IsNullOrWhiteSpace(item.Bvid)).ToArray();
            return new AccountLibraryPage { Items = all.Skip((page - 1) * 30).Take(30).ToArray(),
                Total = all.Length, HasMore = (long)page * 30 < all.Length, NextPage = page + 1 };
        }

        public async Task<FollowingPage> GetFollowedCreatorsAsync(int page = 1, CancellationToken ct = default(CancellationToken))
        {
            CheckPage(page); long account = RequireLibraryAccount();
            var data = RequireData(await GetJsonAsync(Api + "/x/relation/followings?vmid=" + Invariant(account)
                + "&pn=" + Invariant(page) + "&ps=50&order=desc", ct));
            EnsureLibraryAccount(account, ct);
            var raw = data["list"] as JArray;
            if (raw == null) throw LibraryFormatError("关注列表");
            return new FollowingPage { Users = raw.Where(item => Number(item, "mid") > 0).Select(item => new CreatorProfile
                { Mid = Number(item, "mid"), Name = PlainText(Str(item, "uname")), AvatarUrl = ImageUrl(Str(item, "face")) }).ToArray(),
                HasMore = (long)page * 50 < Number(data, "total") };
        }

        public async Task<IReadOnlyList<DynamicAuthorUpdate>> GetDynamicAuthorUpdatesAsync(CancellationToken ct)
        {
            long account = RequireLibraryAccount();
            var data = RequireData(await GetJsonAsync(Api + "/x/polymer/web-dynamic/v1/portal", ct));
            EnsureLibraryAccount(account, ct);
            var raw = data["up_list"] as JArray;
            if (raw == null) throw LibraryFormatError("动态更新列表");
            return raw.Where(item => Number(item, "mid") > 0).GroupBy(item => Number(item, "mid"))
                .Select(group => new DynamicAuthorUpdate { Mid = group.Key, HasUpdate = group.Any(item => Flag(item, "has_update")) }).ToArray();
        }

        public async Task<CreatorDynamicPage> GetFollowingDynamicsAsync(string offset = "", CancellationToken ct = default(CancellationToken))
        {
            long account = RequireLibraryAccount();
            if ((offset ?? "").Length > 16384) throw new ArgumentException("无效动态游标");
            var data = RequireData(await GetJsonAsync(Api + "/x/polymer/web-dynamic/v1/feed/all?type=all&platform=web&features=itemOpusStyle&offset="
                + Uri.EscapeDataString(offset ?? ""), ct));
            EnsureLibraryAccount(account, ct);
            var raw = data["items"] as JArray;
            if (raw == null) throw LibraryFormatError("关注动态");
            bool more = Flag(data, "has_more"); string next = Str(data, "offset");
            if (more && (next.Length == 0 || next.Length > 16384 || next == offset)) throw LibraryFormatError("动态分页");
            return new CreatorDynamicPage { Items = raw.Select(item => MapCreatorDynamic(item, true)).ToArray(), HasMore = more, NextOffset = next };
        }

        public async Task<SearchPage> GetSeasonCatalogAsync(bool drama, int page, CancellationToken ct, int seasonType = 0)
        {
            CheckPage(page);
            if (seasonType == 0) seasonType = drama ? 2 : 1;
            if (seasonType != 1 && seasonType != 2 && seasonType != 3 && seasonType != 4 && seasonType != 5 && seasonType != 7) throw new ArgumentOutOfRangeException(nameof(seasonType));
            var data = RequirePgcData(await GetJsonAsync(Api + "/pgc/season/index/result?season_type=" + Invariant(seasonType)
                + "&type=1&order=" + (seasonType == 1 ? "5" : "3") + "&sort=0&pagesize=20&page=" + Invariant(page), ct));
            var raw = data["list"] as JArray;
            if (raw == null) throw LibraryFormatError("番剧影视索引");
            var seasons = raw.Where(item => Number(item, "season_id") > 0).Select(item => new AccountLibraryItem
            {
                Key = "season:" + Invariant(Number(item, "season_id")), SeasonId = Number(item, "season_id"), IsSeason = true,
                Title = PlainText(Str(item, "title")), CoverUrl = ImageUrl(Str(item, "cover")),
                Owner = PlainText(Str(item, "index_show")), DurationText = Str(item, "badge"),
                PlayText = PlainText(Str(item, "order"))
            }).ToArray();
            IReadOnlyList<VideoItem> cards = seasons;
            if (page == 1 && seasonType == 1)
                cards = await PrioritizeAnimeCatalogAsync(seasons, ct);
            ct.ThrowIfCancellationRequested();
            return new SearchPage { Videos = cards, Total = Number(data, "total"), HasMore = (long)page * 20 < Number(data, "total") };
        }

        private async Task<IReadOnlyList<VideoItem>> PrioritizeAnimeCatalogAsync(AccountLibraryItem[] seasons, CancellationToken ct)
        {
            var recent = GetRecentAnimeAsync(ct);
            IReadOnlyList<AccountLibraryItem> followed = Array.Empty<AccountLibraryItem>();
            if (IsLoggedIn)
            {
                long account = CurrentUser?.Mid ?? 0;
                using (var request = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    request.CancelAfter(TimeSpan.FromSeconds(4));
                    try
                    {
                        var following = await GetFollowedSeasonsAsync(false, 1, request.Token);
                        if (account == CurrentUser?.Mid && IsLoggedIn)
                        {
                            foreach (var item in following.Items) item.Owner = "已追番 · " + item.Owner;
                            followed = following.Items;
                        }
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested && !(ex is OutOfMemoryException))
                    { /* The public catalogue is still available. */ }
                }
            }
            return followed.Concat(await recent).Concat(seasons)
                .GroupBy(item => item.SeasonId).Select(group => group.First()).ToArray();
        }

        private async Task<IReadOnlyList<AccountLibraryItem>> GetRecentAnimeAsync(CancellationToken ct)
        {
            using (var request = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                request.CancelAfter(TimeSpan.FromSeconds(4));
                try
                {
                    var data = RequirePgcData(await GetJsonAsync(Api
                        + "/pgc/web/timeline/v2?season_type=1&day_before=7&day_after=0", request.Token));
                    var latest = data["latest"] as JArray ?? new JArray();
                    return latest.Where(item => Number(item, "published") == 1 && Number(item, "season_id") > 0)
                        .OrderByDescending(item => Str(item, "pub_index") == "第1话")
                        .ThenByDescending(item => Number(item, "pub_ts"))
                        .GroupBy(item => Number(item, "season_id")).Take(12).Select(group =>
                        {
                            var item = group.First();
                            string cover = Str(item, "ep_cover");
                            return new AccountLibraryItem
                            {
                                Key = "season:" + Invariant(group.Key), SeasonId = group.Key, IsSeason = true,
                                Title = PlainText(Str(item, "title")), CoverUrl = ImageUrl(cover.Length > 0 ? cover : Str(item, "cover")),
                                Owner = "最近更新 · " + PlainText(Str(item, "pub_index")), PlayText = PlainText(Str(item, "follows"))
                            };
                        }).ToArray();
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && !(ex is OutOfMemoryException))
                { return Array.Empty<AccountLibraryItem>(); }
            }
        }

        public async Task<IReadOnlyList<SubtitleTrack>> GetSubtitlesAsync(VideoDetail video, long cid, CancellationToken ct)
        {
            if (video == null || video.Aid <= 0 || cid <= 0) return Array.Empty<SubtitleTrack>();
            var data = RequireData(await GetSignedJsonAsync("/x/player/wbi/v2", new Dictionary<string,string>
                { ["aid"] = Invariant(video.Aid), ["cid"] = Invariant(cid) }, ct));
            return (data["subtitle"]?["subtitles"] as JArray ?? new JArray()).Select(item => new SubtitleTrack
                { Language = Str(item, "lan"), Label = PlainText(Str(item, "lan_doc")), Url = ImageUrl(Str(item, "subtitle_url")) })
                .Where(item => TrustedSubtitleUrl(item.Url)).Take(40).ToArray();
        }

        private static bool TrustedSubtitleUrl(string url)
        {
            Uri uri;
            return Uri.TryCreate(url, UriKind.Absolute, out uri) && uri.Scheme == "https" &&
                (uri.Host.EndsWith(".hdslb.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".bilibili.com", StringComparison.OrdinalIgnoreCase));
        }
        public async Task<IReadOnlyList<SubtitleCue>> GetSubtitleCuesAsync(SubtitleTrack track, CancellationToken ct)
        {
            if (track == null || !TrustedSubtitleUrl(track.Url)) throw new ArgumentException("无效字幕地址");
            // The shared transport scopes cookies to their domains; account cookies are never sent to hdslb.com.
            var request = new Windows.Web.Http.HttpRequestMessage(Windows.Web.Http.HttpMethod.Get, new Uri(track.Url));
            var data = ParseJson(await SendTextAsync(request, ct));
            var body = data["body"] as JArray;
            if (body == null || body.Count > 50000) throw LibraryFormatError("字幕");
            return body.Select(item => new SubtitleCue { Start = (double?)item["from"] ?? -1,
                End = (double?)item["to"] ?? -1, Text = PlainText(Str(item, "content")) })
                .Where(cue => !double.IsNaN(cue.Start) && !double.IsInfinity(cue.Start) && cue.Start >= 0 && cue.End > cue.Start && cue.End <= 604800)
                .OrderBy(cue => cue.Start).ToArray();
        }
    }
}
