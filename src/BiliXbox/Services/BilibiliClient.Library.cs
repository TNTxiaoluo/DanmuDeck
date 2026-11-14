using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BiliXbox.Services
{
    public sealed partial class BilibiliClient
    {
        private long _libraryFolderOwner;
        private readonly HashSet<long> _libraryFolderIds = new HashSet<long>();

        public async Task<AccountLibraryPage> GetHistoryAsync(string cursor = "",
            CancellationToken ct = default(CancellationToken))
        {
            long owner = RequireLibraryAccount();
            var position = ParseHistoryCursor(cursor);
            var data = RequireData(await GetJsonAsync(Api + "/x/web-interface/history/cursor?ps=30&type=all&max="
                + Invariant(Number(position, "max")) + "&view_at=" + Invariant(Number(position, "view_at"))
                + "&business=" + Uri.EscapeDataString(Str(position, "business")), ct));
            EnsureLibraryAccount(owner, ct);
            return MapHistoryPage(data, position);
        }

        public async Task<IReadOnlyList<FavoriteFolder>> GetFavoriteFoldersAsync(
            CancellationToken ct = default(CancellationToken))
        {
            long owner = RequireLibraryAccount();
            var data = RequireData(await GetJsonAsync(Api + "/x/v3/fav/folder/created/list-all?up_mid="
                + Invariant(owner), ct));
            EnsureLibraryAccount(owner, ct);
            long count = LibraryCount(data, "count");
            if (data["list"] == null) throw LibraryFormatError("收藏夹列表");
            var list = LibraryArray(data, "list", true);
            if (list.Count == 0 && count > 0) throw LibraryFormatError("收藏夹列表");
            var folders = new List<FavoriteFolder>();
            foreach (var item in list)
            {
                long id = Number(item, "id"), mid = Number(item, "mid");
                if (id <= 0 || (mid > 0 && mid != owner))
                    throw LibraryFormatError("收藏夹");
                if (folders.Any(folder => folder.Id == id)) continue;
                folders.Add(new FavoriteFolder
                {
                    Id = id, Title = PlainText(Str(item, "title")), Count = Math.Max(0, Number(item, "media_count"))
                });
            }
            // Store only the current account's real folders. A stale folder ID
            // retained across sign-out must never open a different user's data.
            EnsureLibraryAccount(owner, ct);
            _libraryFolderOwner = owner;
            _libraryFolderIds.Clear();
            foreach (var folder in folders) _libraryFolderIds.Add(folder.Id);
            return folders;
        }

        public async Task<AccountLibraryPage> GetFavoriteItemsAsync(long folderId, int page = 1,
            CancellationToken ct = default(CancellationToken))
        {
            CheckId(folderId, nameof(folderId)); CheckPage(page);
            long owner = RequireLibraryAccount();
            if (_libraryFolderOwner != owner || !_libraryFolderIds.Contains(folderId))
            {
                await GetFavoriteFoldersAsync(ct);
                EnsureLibraryAccount(owner, ct);
                if (_libraryFolderOwner != owner || !_libraryFolderIds.Contains(folderId))
                    throw new BilibiliApiException(-1, "此收藏夹已移除或不属于当前账户，请刷新收藏列表。");
            }
            var data = RequireData(await GetJsonAsync(Api + "/x/v3/fav/resource/list?media_id="
                + Invariant(folderId) + "&pn=" + Invariant(page) + "&ps=20&order=mtime&platform=web", ct));
            EnsureLibraryAccount(owner, ct);
            var info = data["info"] as JObject;
            long responseFolder = Number(info, "id"), responseOwner = Number(info?["upper"] as JObject, "mid");
            if ((responseFolder > 0 && responseFolder != folderId) || (responseOwner > 0 && responseOwner != owner))
                throw LibraryFormatError("收藏夹");
            if (data["medias"] == null) throw LibraryFormatError("收藏内容");
            var raw = LibraryArray(data, "medias", true);
            if (page == 1 && raw.Count == 0 && Number(info, "media_count") > 0) throw LibraryFormatError("收藏内容");
            var items = new List<AccountLibraryItem>();
            foreach (var item in raw)
            {
                var upper = item["upper"] as JObject;
                long aid = Number(item, "id");
                string bvid = Str(item, "bvid");
                bool playable = Number(item, "type") == 2 && ValidLibraryBvid(bvid)
                    && Number(item, "attr") == 0 && Str(item, "title") != "已失效视频";
                items.Add(new AccountLibraryItem
                {
                    Key = "fav:" + Invariant(folderId) + ":" + Invariant(aid), Aid = aid, Bvid = bvid,
                    Title = PlainText(Str(item, "title")), CoverUrl = ImageUrl(Str(item, "cover")),
                    Owner = Str(upper, "name"), OwnerMid = Number(upper, "mid"), OwnerAvatarUrl = ImageUrl(Str(upper, "face")),
                    PlayText = FormatCount(Number(item["cnt_info"] as JObject, "play")),
                    DurationText = FormatDuration(Number(item, "duration")), BadgeText = playable ? "" : "暂不可播放",
                    IsPlayable = playable, UnavailableReason = playable ? "" : "此收藏内容已失效或暂不支持播放。"
                });
            }
            bool more = LibraryBoolean(data, "has_more");
            if (more && raw.Count == 0) throw LibraryFormatError("收藏分页");
            return new AccountLibraryPage { Items = items, HasMore = more, NextPage = more ? checked(page + 1) : 0,
                Total = Math.Max(0, Number(info, "media_count")) };
        }

        public async Task<AccountLibraryPage> GetFollowedSeasonsAsync(bool drama, int page = 1,
            CancellationToken ct = default(CancellationToken))
        {
            CheckPage(page);
            long owner = RequireLibraryAccount();
            var data = RequireData(await GetJsonAsync(Api + "/x/space/bangumi/follow/list?vmid=" + Invariant(owner)
                + "&type=" + (drama ? "2" : "1") + "&follow_status=0&pn=" + Invariant(page) + "&ps=20", ct));
            EnsureLibraryAccount(owner, ct);
            if (data["list"] == null) throw LibraryFormatError("追番追剧列表");
            var raw = LibraryArray(data, "list", true);
            long total = LibraryCount(data, "total");
            long responsePage = Number(data, "pn"), size = Number(data, "ps");
            if ((responsePage > 0 && responsePage != page) || size < 0 || size > 100)
                throw LibraryFormatError("追番追剧分页");
            if (size == 0) size = 20;
            var items = raw.Select(item => MapFollowedSeason(item, drama)).GroupBy(item => item.SeasonId)
                .Select(group => group.First()).ToList();
            bool more = checked((long)page * size) < total;
            if (raw.Count == 0 && (more || (page == 1 && total > 0))) throw LibraryFormatError("追番追剧分页");
            return new AccountLibraryPage { Items = items, Total = total, HasMore = more,
                NextPage = more ? checked(page + 1) : 0 };
        }

        public async Task<AccountSeasonDetail> GetSeasonDetailAsync(long seasonId, long episodeId = 0,
            CancellationToken ct = default(CancellationToken))
        {
            if (seasonId <= 0 && episodeId <= 0) throw new ArgumentOutOfRangeException(nameof(seasonId));
            long owner = RequireLibraryAccount();
            string query = episodeId > 0 ? "ep_id=" + Invariant(episodeId) : "season_id=" + Invariant(seasonId);
            var data = RequirePgcData(await GetJsonAsync(Api + "/pgc/view/web/season?" + query, ct));
            EnsureLibraryAccount(owner, ct);
            long returned = Number(data, "season_id");
            if (returned <= 0 || (seasonId > 0 && returned != seasonId)) throw LibraryFormatError("剧集");
            string title = PlainText(Str(data, "title"));
            string cover = ImageUrl(Str(data, "cover"));
            var raw = new List<JToken>();
            raw.AddRange(LibraryArray(data, "episodes", true));
            foreach (var section in LibraryArray(data, "section", true))
                raw.AddRange(LibraryArray(section, "episodes", true));
            var episodes = raw.Select(item => MapSeasonEpisode(item, returned, title, cover))
                .GroupBy(item => item.EpisodeId).Select(group => group.First()).ToList();
            if (episodeId > 0 && !episodes.Any(item => item.EpisodeId == episodeId))
                throw new BilibiliApiException(-1, "当前剧集没有返回所选分集，请刷新后重试。");
            return new AccountSeasonDetail { SeasonId = returned, Title = title, CoverUrl = cover, Episodes = episodes,
                Notice = episodes.Count == 0 ? "平台暂未提供可查看的分集。" : "" };
        }

        public async Task<PlaybackInfo> GetEpisodePlaybackAsync(long episodeId, int quality = 80,
            CancellationToken ct = default(CancellationToken), PlaybackAudioMode audioMode = PlaybackAudioMode.Standard)
        {
            CheckId(episodeId, nameof(episodeId));
            if (quality <= 0) throw new ArgumentOutOfRangeException(nameof(quality));
            if (!Enum.IsDefined(typeof(PlaybackAudioMode), audioMode)) throw new ArgumentOutOfRangeException(nameof(audioMode));
            long owner = RequireLibraryAccount();
            // PGC rights are evaluated by the official episode endpoint. Never
            // replace a rejected episode with its UGC BV playback endpoint.
            var data = RequirePgcData(await GetJsonAsync(Api + "/pgc/player/web/playurl?ep_id=" + Invariant(episodeId)
                + "&qn=" + Invariant(quality) + "&fnval=2000&fnver=0&fourk=1", ct));
            EnsureLibraryAccount(owner, ct);
            if (Flag(data, "is_preview"))
                throw new BilibiliApiException(-1, "平台仅返回试看内容，请在官方客户端确认此分集的观看权限。");
            if (Flag(data, "is_drm"))
                throw new BilibiliApiException(-1, "此分集需要本客户端暂不支持的版权保护播放，请使用官方客户端。");
            var dash = data["dash"] as JObject;
            var rawVideos = dash?["video"] as JArray ?? new JArray();
            var videos = rawVideos.Where(track => PlaybackVideoRejectionReason(track).Length == 0)
                .Where(track => Number(track, "id") > 0).ToList();
            var capabilities = await FilterPlaybackCapabilitiesAsync(data, videos, ct);
            PublishTrackCatalogue(data, capabilities, quality);
            EnsureLibraryAccount(owner, ct);
            var hardware = capabilities.HardwareHevc;
            var fourKDisplay = capabilities.FourKDisplay;
            var hdr10Display = capabilities.Hdr10Display;
            var blockedQualities = capabilities.BlockedQualities;
            var unknownQualities = capabilities.UnknownQualities;
            videos = capabilities.Allowed;
            var audioSelection = await SelectPlaybackAudioAsync(data, audioMode, ct);
            EnsureLibraryAccount(owner, ct);
            var audio = audioSelection.Selected;
            if (videos.Count == 0 || audio == null)
            {
                var compatible = MapMp4Playback(data, blockedQualities, unknownQualities);
                compatible.CapabilityDiagnostic = capabilities.Diagnostic;
                SetPlaybackDiagnostics(compatible, data, quality);
                PublishSelectedPlayback(compatible);
                return compatible;
            }
            double duration;
            if (!double.TryParse(Str(dash, "duration"), NumberStyles.Float, CultureInfo.InvariantCulture, out duration)) duration = 0;
            var playback = CreateDashPlaybackChain(data, videos, audioSelection, duration, quality, rawVideos, capabilities);
            PublishSelectedPlayback(playback);
            return playback;
        }

        private long RequireLibraryAccount()
        {
            ThrowIfDisposed();
            var user = CurrentUser;
            if (user == null || !IsLoggedIn || user.Mid <= 0) throw new BilibiliApiException(-101, "请先扫码登录，再查看自己的历史、收藏和追番追剧。");
            return user.Mid;
        }

        private void EnsureLibraryAccount(long owner, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); ThrowIfDisposed();
            var user = CurrentUser;
            if (user == null || !IsLoggedIn || user.Mid != owner)
                throw new BilibiliApiException(-101, "登录账户已变化，请重新打开个人片库。");
        }

        private JToken RequirePgcData(JObject root)
        {
            int code = ResponseCode(root);
            if (code != 0)
            {
                if (code == -101) CurrentUser = null;
                throw ApiError(code, Str(root, "message"));
            }
            var result = root["result"] as JObject ?? root["data"] as JObject;
            if (result == null) throw LibraryFormatError("剧集");
            if (result["code"] != null)
            {
                int nestedCode = ResponseCode(result);
                if (nestedCode != 0)
                {
                    if (nestedCode == -101) CurrentUser = null;
                    throw ApiError(nestedCode, Str(result, "message"));
                }
            }
            return result;
        }

        private static JObject ParseHistoryCursor(string value)
        {
            if (string.IsNullOrEmpty(value)) return new JObject { ["max"] = 0, ["view_at"] = 0, ["business"] = "" };
            if (value.Length > 2048) throw new ArgumentException("历史分页信息过长，请刷新列表。", nameof(value));
            JObject cursor;
            try { cursor = JObject.Parse(value); }
            catch (JsonException) { throw new ArgumentException("历史分页信息无效，请刷新列表。", nameof(value)); }
            long max, viewed;
            if (!long.TryParse(Str(cursor, "max"), NumberStyles.None, CultureInfo.InvariantCulture, out max)
                || !long.TryParse(Str(cursor, "view_at"), NumberStyles.None, CultureInfo.InvariantCulture, out viewed)
                || !Regex.IsMatch(Str(cursor, "business"), "^[a-z_-]{0,40}$"))
                throw new ArgumentException("历史分页信息无效，请刷新列表。", nameof(value));
            return new JObject { ["max"] = max, ["view_at"] = viewed, ["business"] = Str(cursor, "business") };
        }

        private static AccountLibraryPage MapHistoryPage(JToken data, JObject requested)
        {
            var raw = LibraryArray(data, "list", false);
            var items = new List<AccountLibraryItem>();
            int skipped = 0;
            foreach (var row in raw)
            {
                var history = row["history"] as JObject;
                string business = Str(history, "business");
                if (history == null || business.Length == 0) throw LibraryFormatError("历史记录");
                if (business != "archive" && business != "pgc") { ++skipped; continue; }
                long episode = Number(history, "epid"), aid = Number(history, "oid"), viewed = Number(row, "view_at");
                string bvid = Str(history, "bvid");
                bool pgc = business == "pgc";
                bool playable = pgc ? episode > 0 && aid > 0 && Number(history, "cid") > 0 : ValidLibraryBvid(bvid);
                long progress = Number(row, "progress");
                string badge = progress < 0 ? "已看完" : progress > 0 ? "看到 " + FormatDuration(progress) : "尚未播放";
                items.Add(new AccountLibraryItem
                {
                    Key = business + ":" + Invariant(pgc ? episode : aid) + ":" + Invariant(viewed),
                    Bvid = bvid, Aid = aid, Cid = Number(history, "cid"), EpisodeId = episode, IsSeason = pgc,
                    SeasonId = pgc ? Number(row, "season_id") : 0, Page = (int)Math.Max(1, Math.Min(int.MaxValue, Number(history, "page"))),
                    Title = PlainText(Str(row, "title")), CoverUrl = ImageUrl(Str(row, "cover")),
                    Owner = Str(row, "author_name"), OwnerMid = Number(row, "author_mid"), OwnerAvatarUrl = ImageUrl(Str(row, "author_face")),
                    PlayText = FormatDate(viewed), DurationText = FormatDuration(Number(row, "duration")),
                    ProgressSeconds = Math.Max(-1, progress), BadgeText = badge,
                    IsPlayable = playable, UnavailableReason = playable ? "" : "此历史内容已失效或暂未提供可播放的视频标识。"
                });
            }
            var nextRaw = data["cursor"] as JObject;
            if (raw.Count > 0 && nextRaw == null) throw LibraryFormatError("历史分页");
            bool more = raw.Count > 0 && Number(nextRaw, "max") > 0 && Number(nextRaw, "view_at") > 0;
            string next = "";
            if (more)
            {
                var valid = ParseHistoryCursor(nextRaw.ToString(Newtonsoft.Json.Formatting.None));
                if (JToken.DeepEquals(valid, requested)) throw LibraryFormatError("历史分页");
                next = valid.ToString(Newtonsoft.Json.Formatting.None);
            }
            return new AccountLibraryPage { Items = items, HasMore = more, NextCursor = next,
                Notice = skipped > 0 ? "本页的直播、专栏等非视频记录暂不支持播放，可继续加载后续历史。" : "" };
        }

        private static AccountLibraryItem MapFollowedSeason(JToken item, bool drama)
        {
            long id = Number(item, "season_id");
            if (id <= 0) throw LibraryFormatError("追番追剧");
            string type = Str(item, "season_type_name");
            if (type.Length == 0) type = drama ? "影视" : "番剧";
            var latest = item["new_ep"] as JObject;
            bool available = item["is_play"] == null || item["is_play"].Type == JTokenType.Null || Flag(item, "is_play");
            return new AccountLibraryItem
            {
                Key = "season:" + Invariant(id), IsSeason = true, SeasonId = id,
                Title = PlainText(Str(item, "title")), CoverUrl = ImageUrl(Str(item, "cover")), Owner = type,
                PlayText = FormatCount(Number(item["stat"] as JObject, "view")), DurationText = Str(latest, "index_show"),
                BadgeText = Str(item, "badge"), IsPlayable = available,
                UnavailableReason = available ? "" : "此剧集尚未上线或平台暂未开放播放。"
            };
        }

        private static AccountLibraryItem MapSeasonEpisode(JToken item, long season, string title, string cover)
        {
            long episode = Number(item, "id");
            if (episode <= 0) episode = Number(item, "ep_id");
            if (episode <= 0) throw LibraryFormatError("分集");
            string shortTitle = PlainText(Str(item, "title")), longTitle = PlainText(Str(item, "long_title"));
            string episodeTitle = (shortTitle + " " + longTitle).Trim();
            var rights = item["rights"] as JObject;
            bool playable = Number(item, "cid") > 0 && Number(item, "aid") > 0
                && (rights?["can_watch"] == null || rights["can_watch"].Type == JTokenType.Null || Flag(rights, "can_watch"));
            string episodeCover = ImageUrl(Str(item, "cover"));
            return new AccountLibraryItem
            {
                Key = "episode:" + Invariant(episode), IsSeason = false, SeasonId = season, EpisodeId = episode,
                Aid = Number(item, "aid"), Bvid = Str(item, "bvid"), Cid = Number(item, "cid"),
                Title = episodeTitle.Length > 0 ? episodeTitle : title, CoverUrl = episodeCover.Length > 0 ? episodeCover : cover,
                Owner = title, DurationText = FormatDuration(Number(item, "duration") / 1000), BadgeText = Str(item, "badge"),
                IsPlayable = playable, UnavailableReason = playable ? "" : "平台暂未开放此分集播放，请在官方客户端确认观看权限。"
            };
        }

        private static bool ClearLibraryTrack(JToken item) => !Flag(item, "is_drm") && Number(item, "drm_tech_type") == 0
            && string.IsNullOrEmpty(Str(item, "widevine_pssh")) && string.IsNullOrEmpty(Str(item, "playready_pssh"));
        private static bool ValidLibraryBvid(string value) => Regex.IsMatch(value ?? "", "^BV[0-9A-Za-z]{10}$");
        private static long LibraryCount(JToken data, string property)
        {
            long count;
            if (!long.TryParse(Str(data, property), NumberStyles.None, CultureInfo.InvariantCulture, out count))
                throw LibraryFormatError("列表计数");
            return count;
        }
        private static bool LibraryBoolean(JToken data, string property)
        {
            var value = Str(data, property);
            if (value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)) return true;
            if (value == "0" || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)) return false;
            throw LibraryFormatError("分页");
        }
        private static JArray LibraryArray(JToken data, string property, bool nullable)
        {
            var token = data?[property];
            if (nullable && (token == null || token.Type == JTokenType.Null)) return new JArray();
            var array = token as JArray;
            if (array == null || array.Any(item => !(item is JObject))) throw LibraryFormatError("列表");
            return array;
        }
        private static BilibiliApiException LibraryFormatError(string context)
            => new BilibiliApiException(-1, "B站返回的" + context + "信息不完整或格式已变化，请刷新重试。");
    }
}
