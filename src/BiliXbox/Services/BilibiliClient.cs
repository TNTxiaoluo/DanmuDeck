using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using System.IO;
using BiliXbox.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Windows.Web.Http;
using Windows.Web.Http.Filters;

namespace BiliXbox.Services
{
    /// <summary>
    /// A client for Bilibili's website APIs. These endpoints can change or reject third-party clients.
    /// No fallback returns mock account, playback, comment, or danmaku success.
    /// </summary>
    public sealed partial class BilibiliClient : IDisposable
    {
        public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";
        public const string Referer = "https://www.bilibili.com/";
        private const string Api = "https://api.bilibili.com";
        private const string Passport = "https://passport.bilibili.com";
        private readonly HttpClient _http;
        private readonly ProtectedCookieStore _cookies;
        private readonly SemaphoreSlim _sessionGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _videoInteractionGate = new SemaphoreSlim(1, 1);
        private long _loginSessionEpoch;
        private readonly Dictionary<string, PendingQrLogin> _pendingQrLogins = new Dictionary<string, PendingQrLogin>(StringComparer.Ordinal);
        private readonly SemaphoreSlim _wbiGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _websiteGate = new SemaphoreSlim(1, 1);
        private DateTimeOffset _websiteRetryAfter;
        private readonly Dictionary<long, Dictionary<int, CreatorMediaCursor>> _creatorMediaCursors
            = new Dictionary<long, Dictionary<int, CreatorMediaCursor>>();
        private readonly Dictionary<long, CreatorArchiveSource> _creatorArchiveSources
            = new Dictionary<long, CreatorArchiveSource>();
        private readonly Dictionary<string, VideoSearchSession> _videoSearchSessions
            = new Dictionary<string, VideoSearchSession>(StringComparer.Ordinal);
        private string _imageKey;
        private string _subKey;
        private DateTimeOffset _keysExpire;
        private bool _disposed;

        public UserProfile CurrentUser { get; private set; }
        public bool IsLoggedIn => CurrentUser != null && !string.IsNullOrEmpty(_cookies.GetCookieValue("SESSDATA"));
        public event Action<string> PlaybackDiagnostic;

        private void PublishPlaybackDiagnostic(string text)
        {
            try { PlaybackDiagnostic?.Invoke(text); } catch (Exception) { /* Diagnostics never break playback. */ }
        }

        public BilibiliClient()
        {
            var filter = new HttpBaseProtocolFilter
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = true,
                CookieUsageBehavior = HttpCookieUsageBehavior.NoCookies
            };
            filter.CacheControl.ReadBehavior = HttpCacheReadBehavior.MostRecent;
            filter.CacheControl.WriteBehavior = HttpCacheWriteBehavior.NoCache;
            _cookies = new ProtectedCookieStore();
            _http = new HttpClient(filter);
            _http.DefaultRequestHeaders.TryAppendWithoutValidation("User-Agent", UserAgent);
            _http.DefaultRequestHeaders.Referer = new Uri(Referer);
            _http.DefaultRequestHeaders.TryAppendWithoutValidation("Accept", "application/json, text/plain, */*");
        }

        public async Task InitializeAsync()
        {
            ThrowIfDisposed();
            await _sessionGate.WaitAsync();
            try
            {
                await _cookies.LoadAsync();
                await RefreshProfileAsync(CancellationToken.None);
                if (IsLoggedIn) await _cookies.SaveAsync();
            }
            finally { _sessionGate.Release(); }
        }

        public async Task<IReadOnlyList<VideoItem>> GetPopularAsync(int page = 1,
            CancellationToken ct = default(CancellationToken))
        {
            CheckPage(page);
            var root = await GetJsonAsync(Api + "/x/web-interface/popular?ps=20&pn=" + Invariant(page), ct);
            var data = RequireData(root);
            return (data["list"] as JArray ?? new JArray()).Select(MapVideo).ToList();
        }

        public async Task<IReadOnlyList<VideoItem>> SearchAsync(string keyword, int page = 1,
            CancellationToken ct = default(CancellationToken))
        {
            return (await SearchPageAsync(keyword, page, ct)).Videos;
        }

        public async Task<SearchPage> SearchPageAsync(string keyword, int page = 1,
            CancellationToken ct = default(CancellationToken))
        {
            CheckPage(page);
            keyword = NormalizeSearchKeyword(keyword);
            ct.ThrowIfCancellationRequested();
            VideoSearchSession session;
            if (page > 1 && _videoSearchSessions.TryGetValue(keyword, out session))
            {
                // A search keeps its original transport and real page size.
                // A mid-search switch between 42- and 20-item pages skips or
                // duplicates ranks, even when both pages contain genuine data.
                var later = session.Website ? await GetWebsiteSearchDataAsync(keyword, page, ct)
                    : await GetKeywordSearchDataAsync(keyword, page, session.PageSize, ct);
                return MapKeywordSearchPage(later, page, session.PageSize);
            }
            if (page != 1)
                throw new BilibiliApiException(-1, "请先从第1页重新搜索，再继续翻页。");
            JToken data;
            var website = true;
            try { data = await GetWebsiteSearchDataAsync(keyword, page, ct); }
            catch (BilibiliApiException)
            {
                // The normal public search webpage is an independent source
                // of server-rendered video results, not a verification bypass.
                // If unavailable, the normal typed API is a first-page fallback.
                website = false;
                data = await GetKeywordSearchDataAsync(keyword, page, 42, ct);
            }
            var result = MapKeywordSearchPage(data, page, 0);
            ct.ThrowIfCancellationRequested();
            _videoSearchSessions[keyword] = new VideoSearchSession
                { Website = website, PageSize = (int)Number(data, "pagesize") };
            return result;
        }

        public async Task<SearchPage> SearchCreatorPageAsync(string keyword, int page = 1,
            CancellationToken ct = default(CancellationToken))
        {
            CheckPage(page);
            keyword = NormalizeSearchKeyword(keyword);
            ct.ThrowIfCancellationRequested();
            await PrepareSearchApiAsync(ct);
            // Uploader lookup is explicitly requested by the user. Ordinary
            // video search never queries this endpoint or substitutes archives.
            var usersRoot = await GetSignedJsonAsync("/x/web-interface/wbi/search/type",
                new Dictionary<string, string>
                {
                    ["search_type"] = "bili_user", ["keyword"] = keyword,
                    ["page"] = "1", ["page_size"] = "20", ["order"] = "0"
                }, ct);
            var creator = SearchItems(RequireData(usersRoot)).FirstOrDefault(item => Number(item, "mid") > 0
                && string.Equals(PlainText(Str(item, "uname")).Trim(), keyword, StringComparison.OrdinalIgnoreCase));
            if (creator == null) return new SearchPage { Notice = "没有找到名称完全匹配的UP主，请检查名称，或切换到视频搜索。" };
            return await SearchCreatorVideosAsync(creator, page, ct);
        }

        private static string NormalizeSearchKeyword(string keyword)
        {
            if (string.IsNullOrWhiteSpace(keyword)) throw new ArgumentException("请输入搜索关键词。", nameof(keyword));
            if (keyword.Length > 100) throw new ArgumentException("搜索词最多100个字符。", nameof(keyword));
            return keyword.Trim();
        }

        private async Task PrepareSearchApiAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!string.IsNullOrEmpty(_cookies.GetCookieValue("buvid3"))) return;
            await _websiteGate.WaitAsync(ct);
            try
            {
                if (!string.IsNullOrEmpty(_cookies.GetCookieValue("buvid3"))
                    || _websiteRetryAfter > DateTimeOffset.UtcNow) return;
                try
                {
                    // Search and creator pages share the same genuine website
                    // session. Coalesce parallel profile/archive initialization.
                    await BootstrapWebsiteAsync(ct);
                }
                catch (BilibiliApiException)
                {
                    // The homepage may return 302 and issue buvid3 at the same
                    // time. SendAsync has already captured its Set-Cookie; no
                    // redirect, fabricated cookie or challenge retry is needed.
                }
                if (string.IsNullOrEmpty(_cookies.GetCookieValue("buvid3")))
                    _websiteRetryAfter = DateTimeOffset.UtcNow.AddMinutes(1);
            }
            finally { _websiteGate.Release(); }
        }

        private sealed class VideoSearchSession
        {
            public bool Website { get; set; }
            public int PageSize { get; set; }
        }

        private async Task<JToken> GetWebsiteSearchDataAsync(string keyword, int page, CancellationToken ct)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, new Uri("https://search.bilibili.com/video?keyword="
                + Uri.EscapeDataString(keyword) + "&page=" + Invariant(page)));
            request.Headers.TryAppendWithoutValidation("Accept", "text/html,application/xhtml+xml");
            return WebsiteSearchParser.Parse(await SendTextAsync(request, ct), keyword, page);
        }

        private async Task<JToken> GetKeywordSearchDataAsync(string keyword, int page, int size, CancellationToken ct)
        {
            await PrepareSearchApiAsync(ct);
            var parameters = new Dictionary<string, string>
            {
                ["search_type"] = "video", ["keyword"] = keyword,
                ["page"] = Invariant(page), ["page_size"] = Invariant(size), ["order"] = "totalrank"
            };
            return RequireData(await GetSignedJsonAsync("/x/web-interface/wbi/search/type", parameters, ct));
        }

        private static SearchPage MapKeywordSearchPage(JToken data, int page, int expectedSize)
        {
            var size = Number(data, "pagesize");
            if (Number(data, "page") != page || size <= 0 || size > 100 || expectedSize > 0 && size != expectedSize)
                throw new BilibiliApiException(-1, "搜索页码或每页数量已变化，请从第1页重新搜索。");
            var videos = SearchItems(data).Select(item => MapSearchVideo(item, ""))
                .Where(item => Regex.IsMatch(item.Bvid, "^BV[0-9A-Za-z]{10}$"))
                .GroupBy(item => item.Bvid, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToList();
            if (videos.Count == 0 && Number(data, "numResults") > 0 && page <= Number(data, "numPages"))
                throw new BilibiliApiException(-1, "B站未返回本页的视频结果，请重试搜索。");
            return new SearchPage
            {
                Videos = videos, Total = Number(data, "numResults"),
                HasMore = videos.Count > 0 && (Number(data, "numPages") > 0
                    ? page < Number(data, "numPages") : Number(data, "numResults") > 0
                        ? (long)page * size < Number(data, "numResults") : videos.Count >= size)
            };
        }

        private async Task<SearchPage> SearchCreatorVideosAsync(JToken creator, int page, CancellationToken ct)
        {
            await PrepareSearchApiAsync(ct);
            var mid = Number(creator, "mid");
            var name = PlainText(Str(creator, "uname"));
            if (page > 1)
            {
                CreatorArchiveSource source;
                if (!_creatorArchiveSources.TryGetValue(mid, out source))
                    throw new BilibiliApiException(-1, "请先从第1页重新读取投稿，再继续翻页。");
                // Keep the first page's transport, ordering and cursor. A
                // later failure must not silently splice two different lists.
                return source == CreatorArchiveSource.MediaList
                    ? await GetCreatorMediaPageAsync(creator, page, ct)
                    : await GetCreatorArchivePageAsync(creator, page, ct);
            }
            _creatorArchiveSources.Remove(mid);
            _creatorMediaCursors.Remove(mid);
            try
            {
                // Bilibili's public uploader playback list contains the full
                // public archive, rather than the three search-card previews.
                // It uses an AV cursor, not pn; retain cursors for Back/Next.
                var result = await GetCreatorMediaPageAsync(creator, page, ct);
                ct.ThrowIfCancellationRequested();
                _creatorArchiveSources[mid] = CreatorArchiveSource.MediaList;
                return result;
            }
            catch (BilibiliApiException)
            {
                // This is a separate normal public index, not a retry of a
                // verification challenge. The website archive remains useful
                // when playback-list availability changes for an uploader.
            }
            try
            {
                var result = await GetCreatorArchivePageAsync(creator, page, ct);
                ct.ThrowIfCancellationRequested();
                _creatorArchiveSources[mid] = CreatorArchiveSource.WbiArchive;
                return result;
            }
            catch (BilibiliApiException ex)
            {
                // User search carries genuine recent-upload previews, even when
                // the separate archive endpoint demands official verification.
                // Show only those real entries, labelled as a partial result.
                // Do not repeat the same three entries on subsequent pages.
                var previews = page == 1
                    ? (creator["res"] as JArray ?? new JArray()).Select(item => MapSearchVideo(item, name))
                        .Where(item => !string.IsNullOrEmpty(item.Bvid))
                        .GroupBy(item => item.Bvid, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToList()
                    : new List<VideoItem>();
                if (previews.Count == 0) throw;
                return new SearchPage
                {
                    Videos = previews, MatchedCreatorName = name, HasMore = false,
                    Notice = "完整投稿列表暂不可用，当前显示搜索提供的 " + Invariant(previews.Count)
                        + " 个近期视频。" + ex.Message
                };
            }
        }

        private enum CreatorArchiveSource { MediaList, WbiArchive }

        private static string CreatorReferer(long mid) => "https://space.bilibili.com/" + Invariant(mid) + "/video";

        private async Task<SearchPage> GetCreatorArchivePageAsync(JToken creator, int page, CancellationToken ct)
        {
            var mid = Number(creator, "mid");
            var name = PlainText(Str(creator, "uname"));
            var data = RequireData(await GetSignedJsonAsync("/x/space/wbi/arc/search",
                new Dictionary<string, string>
                {
                    ["mid"] = Invariant(mid), ["pn"] = Invariant(page), ["ps"] = "30",
                    ["order"] = "pubdate", ["platform"] = "web", ["tid"] = "0",
                    ["keyword"] = "", ["web_location"] = "1550101"
                }, ct, CreatorReferer(mid)));
            var archive = (data["list"] as JObject)?["vlist"] as JArray;
            if (archive == null || archive.Any(item => !(item is JObject)))
                throw new BilibiliApiException(-1, "B站投稿列表未返回该用户的有效数据，请刷新重试。");
            var videos = archive.Select(item => MapSearchVideo(item, name))
                .Where(item => Regex.IsMatch(item.Bvid, "^BV[0-9A-Za-z]{10}$"))
                .GroupBy(item => item.Bvid, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToList();
            var pagination = data["page"] as JObject;
            var count = Number(pagination, "count");
            var size = Number(pagination, "ps");
            if (pagination == null || size != 30 || count < 0
                || pagination["pn"] != null && Number(pagination, "pn") != page
                || videos.Count == 0 && ((long)(page - 1) * size < count || page == 1 && Number(creator, "videos") > 0))
                throw new BilibiliApiException(-1, "B站投稿的页码或列表不完整，请从第1页刷新重试。");
            return new SearchPage
            {
                Videos = videos, MatchedCreatorName = name, Total = count,
                HasMore = videos.Count > 0 && (count > 0 ? (long)page * size < count : videos.Count >= size)
            };
        }

        private sealed class CreatorMediaCursor
        {
            public long Oid { get; set; }
            public long Seen { get; set; }
        }

        private async Task<SearchPage> GetCreatorMediaPageAsync(JToken creator, int page, CancellationToken ct)
        {
            var mid = Number(creator, "mid");
            var name = PlainText(Str(creator, "uname"));
            Dictionary<int, CreatorMediaCursor> cursors;
            CreatorMediaCursor cursor;
            if (page == 1) cursor = new CreatorMediaCursor();
            else if (!_creatorMediaCursors.TryGetValue(mid, out cursors) || !cursors.TryGetValue(page, out cursor))
                throw new BilibiliApiException(-1, "投稿列表翻页需要上一页的位置，请先刷新搜索。");
            var url = Api + "/x/v2/medialist/resource/list?type=1&biz_id=" + Invariant(mid)
                + "&otype=2&ps=30&direction=false&desc=true&sort_field=1&tid=0"
                + (cursor.Oid == 0 ? "&with_current=true" : "&with_current=false&oid=" + Invariant(cursor.Oid));
            var data = RequireData(await GetJsonAsync(url, ct, CreatorReferer(mid)));
            ct.ThrowIfCancellationRequested();
            var media = data["media_list"] as JArray;
            if (media == null || media.Count > 30 || media.Any(item => !(item is JObject)
                || Number(item, "type") == 2 && (!(item["upper"] is JObject) || Number(item["upper"], "mid") <= 0)))
                throw new BilibiliApiException(-1, "B站投稿播放列表响应格式已变化。");
            // The UID-scoped archive also contains collaborative submissions.
            // Their primary uploader may be another account; retain the real
            // upper identity instead of rejecting the entire public list.
            if (cursor.Oid > 0 && media.Any(item => Number(item, "id") == cursor.Oid))
                throw new BilibiliApiException(-1, "B站投稿列表未正确翻页，请刷新搜索。");
            var videos = media.Where(item => Number(item, "type") == 2).Select(item => new VideoItem
            {
                Bvid = Str(item, "bv_id"), Aid = Number(item, "id"),
                Title = PlainText(Str(item, "title")), CoverUrl = ImageUrl(Str(item, "cover")),
                Owner = PlainText(Str(item["upper"], "name")), OwnerMid = Number(item["upper"], "mid"),
                OwnerAvatarUrl = ImageUrl(Str(item["upper"], "face")),
                PlayText = FormatCount(Number(item["cnt_info"] as JObject, "play")),
                DurationText = FormatDuration(Number(item, "duration"))
            }).Where(item => item.Aid > 0 && Regex.IsMatch(item.Bvid, "^BV[0-9A-Za-z]{10}$"))
                .GroupBy(item => item.Bvid, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToList();
            var total = Number(data, "total_count");
            if (videos.Count == 0 && page == 1 && (total > 0 || Number(creator, "videos") > 0))
                throw new BilibiliApiException(-1, "B站暂未提供该UP主的公开投稿列表。");
            var seen = cursor.Seen + media.Count;
            // For uploader lists has_more can remain true even after the final
            // public item. The server total takes precedence; without a total,
            // a short final batch must not produce endless empty pages.
            var hasMore = media.Count > 0 && (total > 0 ? seen < total
                : Flag(data, "has_more") && media.Count >= 30);
            var nextOid = media.Count > 0 ? Number(media.Last, "id") : 0;
            if (hasMore && (nextOid <= 0 || nextOid == cursor.Oid))
                throw new BilibiliApiException(-1, "B站没有返回有效的下一页投稿位置。");
            if (page == 1 || !_creatorMediaCursors.TryGetValue(mid, out cursors))
                _creatorMediaCursors[mid] = cursors = new Dictionary<int, CreatorMediaCursor>();
            cursors[page] = cursor;
            if (hasMore) cursors[page + 1] = new CreatorMediaCursor { Oid = nextOid, Seen = seen };
            else foreach (var later in cursors.Keys.Where(value => value > page).ToList()) cursors.Remove(later);
            return new SearchPage
            {
                Videos = videos, MatchedCreatorName = name, HasMore = hasMore, Total = total,
                Notice = videos.Count == media.Count ? "" : "部分投稿暂不可见，当前仅显示可读取的视频。"
            };
        }

        private static JArray SearchItems(JToken data)
        {
            var items = data["result"] as JArray;
            if (items == null && Number(data, "numResults") > 0)
                throw new BilibiliApiException(-1, "B站搜索响应格式已变化，请更新客户端。");
            return items ?? new JArray();
        }

        private static VideoItem MapSearchVideo(JToken item, string creator)
        {
            var owner = PlainText(Str(item, "author"));
            var duration = Str(item, "duration");
            if (string.IsNullOrEmpty(duration)) duration = Str(item, "length");
            return new VideoItem
            {
                Bvid = Str(item, "bvid"), Aid = Number(item, "aid"),
                Title = PlainText(Str(item, "title")), CoverUrl = ImageUrl(Str(item, "pic")),
                Owner = string.IsNullOrWhiteSpace(owner) ? creator : owner, OwnerMid = Number(item, "mid"),
                PlayText = FormatCount(Number(item, "play")), DurationText = duration
            };
        }

        public async Task<VideoDetail> GetVideoAsync(string bvid, CancellationToken ct = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(bvid) || !Regex.IsMatch(bvid, "^BV[0-9A-Za-z]{10}$"))
                throw new ArgumentException("请输入完整的 BV 号，例如 BV1xx411c7mD。", nameof(bvid));
            // The website uses the WBI detail endpoint. The older /view route can reject
            // otherwise valid public-video requests with HTTP 412 before metadata is read.
            var root = await GetSignedJsonAsync("/x/web-interface/wbi/view",
                new Dictionary<string, string> { ["bvid"] = bvid }, ct);
            var data = RequireData(root);
            var basic = MapVideo(data);
            var parts = (data["pages"] as JArray ?? new JArray()).Select(part => new VideoPart
            {
                Cid = Number(part, "cid"), Page = (int)Number(part, "page"), Title = Str(part, "part")
            }).ToList();
            var detail = new VideoDetail
            {
                Bvid = basic.Bvid, Aid = basic.Aid, Title = basic.Title, CoverUrl = basic.CoverUrl,
                Owner = basic.Owner, OwnerMid = basic.OwnerMid, OwnerAvatarUrl = basic.OwnerAvatarUrl,
                PlayText = basic.PlayText, DurationText = basic.DurationText,
                Description = Str(data, "desc"), Cid = Number(data, "cid"), Parts = parts,
                ReplyCount = OptionalCommentCount(data["stat"]?["reply"])
            };
            if (detail.Cid <= 0 && parts.Count > 0) detail.Cid = parts[0].Cid;
            if (detail.Aid <= 0 || detail.Cid <= 0)
                throw new BilibiliApiException(-1, "B站返回的视频信息不完整，无法播放。");
            return detail;
        }

        public Task<PlaybackInfo> GetPlaybackAsync(VideoDetail video,
            CancellationToken ct = default(CancellationToken))
        {
            return GetPlaybackAsync(video, 80, ct);
        }

        public async Task<PlaybackInfo> GetPlaybackAsync(VideoDetail video, int quality,
            CancellationToken ct = default(CancellationToken), PlaybackAudioMode audioMode = PlaybackAudioMode.Standard)
        {
            if (video == null || video.Cid <= 0) throw new ArgumentException("视频信息不完整。", nameof(video));
            if (quality <= 0) throw new ArgumentOutOfRangeException(nameof(quality));
            if (!Enum.IsDefined(typeof(PlaybackAudioMode), audioMode)) throw new ArgumentOutOfRangeException(nameof(audioMode));
            // Request the website's DASH catalogue rather than the HTML5 MP4
            // compatibility endpoint, which caps its catalogue at 720P. Send
            // the selected qn: a highest-tier request is not a substitute for
            // requesting 4K explicitly. Only returned tracks grant playback.
            // The official player uses fnval 2000 for HEVC with AV1 disabled;
            // 4048 advertises AV1. Start with AVC/HEVC so a server preference
            // cannot replace a working HEVC representation with AV1-only data.
            long playbackEpoch = _loginSessionEpoch;
            long playbackMid = CurrentUser?.Mid ?? 0;
            string playbackSession = _cookies.GetCookieValue("SESSDATA");
            PublishPlaybackDiagnostic("request endpoint=/x/player/wbi/playurl qn=" + quality + " fnval=2000 fourk=1"
                + " auth=" + IsLoggedIn + " sessionCookie=" + !string.IsNullOrEmpty(playbackSession)
                + " premium=" + (CurrentUser?.IsPremium == true));
            var root = await GetPlaybackJsonAsync(new Dictionary<string, string>
            {
                ["bvid"] = video.Bvid, ["cid"] = Invariant(video.Cid),
                ["qn"] = Invariant(quality), ["fnval"] = "2000", ["fnver"] = "0", ["fourk"] = "1"
            }, ct);
            var data = RequireData(root);
            if (!SessionMatches(playbackEpoch, playbackMid, playbackSession)) throw new OperationCanceledException("播放请求期间账号已改变。", ct);
            var dash = data["dash"];
            var rawVideos = dash?["video"] as JArray ?? new JArray();
            var videos = rawVideos
                .Where(track => PlaybackVideoRejectionReason(track).Length == 0).Where(track => Number(track, "id") > 0).ToList();
            var capabilities = await FilterPlaybackCapabilitiesAsync(data, videos, ct);
            if (!SessionMatches(playbackEpoch, playbackMid, playbackSession)) throw new OperationCanceledException("播放请求期间账号已改变。", ct);
            PublishTrackCatalogue(data, capabilities, quality);
            // Only probe AV1 when an explicit UHD request has no usable track
            // and the platform has advertised that tier. Never advertise AV1
            // on faith or repeatedly retry a denied playback request.
            if ((quality == 120 || quality == 127) && !capabilities.Allowed.Any(track => EffectivePlaybackQuality(track) == quality
                    && track.Annotation<SdrToneMappedPlaybackTrack>() == null)
                && !capabilities.DisplayBlocked.Contains(quality)
                && (rawVideos.Any(track => Number(track, "id") == quality)
                    || (data["accept_quality"] as JArray ?? new JArray()).Any(value => value.ToString() == Invariant(quality)))
                && await Task.Run(() => PlaybackCodecSupport.CanDecodeHardware("av01", quality == 127 ? 7680 : 3840,
                    quality == 127 ? 4320 : 2160, 30, 20000000, 8), ct))
            {
                try
                {
                    PublishPlaybackDiagnostic("request endpoint=/x/player/wbi/playurl qn=" + quality + " fnval=4048 fourk=1 av1HardwareProbe=passed");
                    var alternateData = RequireData(await GetPlaybackJsonAsync(new Dictionary<string, string>
                    {
                        ["bvid"] = video.Bvid, ["cid"] = Invariant(video.Cid), ["qn"] = Invariant(quality),
                        ["fnval"] = "4048", ["fnver"] = "0", ["fourk"] = "1"
                    }, ct));
                    alternateData.AddAnnotation(new PlaybackRequestMetadata { Fnval = 4048 });
                    if (!SessionMatches(playbackEpoch, playbackMid, playbackSession)) throw new OperationCanceledException("播放请求期间账号已改变。", ct);
                    var alternateRaw = alternateData["dash"]?["video"] as JArray ?? new JArray();
                    var alternateCapabilities = await FilterPlaybackCapabilitiesAsync(alternateData,
                        alternateRaw.Where(track => PlaybackVideoRejectionReason(track).Length == 0).Where(track => Number(track, "id") > 0).ToList(), ct);
                    PublishTrackCatalogue(alternateData, alternateCapabilities, quality);
                    if (alternateCapabilities.Allowed.Any(track => EffectivePlaybackQuality(track) == quality
                            && track.Annotation<SdrToneMappedPlaybackTrack>() == null)
                        && (alternateData["dash"]?["audio"] as JArray ?? new JArray()).Any(DashManifestBuilder.IsSupportedAudio))
                    {
                        data = alternateData; dash = data["dash"]; rawVideos = alternateRaw; capabilities = alternateCapabilities;
                    }
                }
                catch (BilibiliApiException error)
                { PublishPlaybackDiagnostic("av1-probe-failed apiCode=" + error.Code + " keepOriginalSource=true"); }
            }
            if (!SessionMatches(playbackEpoch, playbackMid, playbackSession)) throw new OperationCanceledException("播放请求期间账号已改变。", ct);
            var hardwareHevc = capabilities.HardwareHevc;
            var fourKDisplay = capabilities.FourKDisplay;
            var hdr10Display = capabilities.Hdr10Display;
            var blockedQualities = capabilities.BlockedQualities;
            var unknownQualities = capabilities.UnknownQualities;
            videos = capabilities.Allowed;
            var audioSelection = await SelectPlaybackAudioAsync(data, audioMode, ct);
            if (!SessionMatches(playbackEpoch, playbackMid, playbackSession)) throw new OperationCanceledException("播放请求期间账号已改变。", ct);
            var audio = audioSelection.Selected;
            if (videos.Count > 0 && audio != null)
            {
                var selected = OrderPlaybackVideoTracks(videos, quality, hardwareHevc).First();
                var actualQuality = EffectivePlaybackQuality(selected);
                var provided = new HashSet<int>(videos.Select(EffectivePlaybackQuality));
                // Anonymous DASH responses can stop at 480P, whereas the public
                // MP4 compatibility endpoint can still return a genuine 720P
                // muxed file. Preserve that working tier without calling it HD
                // DASH or pretending that 1080P/4K permissions were granted.
                if (!IsLoggedIn && actualQuality < 64 && quality >= 64)
                {
                    try
                    {
                        var mp4 = await GetMp4PlaybackAsync(video, quality, ct, blockedQualities);
                        if (mp4.QualityId > actualQuality)
                        {
                            mp4.AvailableQualities = MapPlaybackQualities(data, mp4.QualityId, provided, rawVideos, blockedQualities, unknownQualities);
                            mp4.CapabilityDiagnostic = capabilities.Diagnostic;
                            mp4.Quality = mp4.AvailableQualities.First(item => item.Id == mp4.QualityId).Label;
                            SetPlaybackDiagnostics(mp4, data, quality);
                            PublishSelectedPlayback(mp4);
                            return mp4;
                        }
                    }
                    catch (BilibiliApiException ex) when (ex.Code != 403 && ex.Code != 412 && ex.Code != 429)
                    {
                        // The already-returned DASH source remains usable when
                        // this video has no single-file compatibility source.
                    }
                }
                double duration;
                if (!double.TryParse(Str(dash, "duration"), NumberStyles.Float, CultureInfo.InvariantCulture, out duration)) duration = 0;
                var playback = CreateDashPlaybackChain(data, videos, audioSelection, duration, quality, rawVideos, capabilities);
                PublishSelectedPlayback(playback);
                return playback;
            }
            // Some older videos supply a real muxed MP4 even in this response.
            // Do not silently play a video-only track when audio is missing.
            var compatible = MapMp4Playback(data, blockedQualities, unknownQualities);
            compatible.CapabilityDiagnostic = capabilities.Diagnostic;
            SetPlaybackDiagnostics(compatible, data, quality);
            PublishSelectedPlayback(compatible);
            return compatible;
        }

        private static string PlaybackVideoRejectionReason(JToken track)
            => !ClearPlaybackAudio(track) ? "轨道需要当前播放器未实现的版权保护"
                : DashManifestBuilder.VideoRejectionReason(track);

        private static IEnumerable<JToken> OrderPlaybackVideoTracks(IEnumerable<JToken> tracks, int requestedQuality,
            ISet<JToken> hardwareHevc)
        {
            var all = tracks.ToList();
            var eligible = all.Where(track => EffectivePlaybackQuality(track) <= requestedQuality).ToList();
            if (eligible.Count == 0)
            {
                int minimum = all.Min(EffectivePlaybackQuality);
                eligible = all.Where(track => EffectivePlaybackQuality(track) == minimum).ToList();
            }
            return eligible.OrderByDescending(EffectivePlaybackQuality)
                .ThenBy(track => DashFrameRate(track) > 60.001 ? 1 : 0)
                .ThenBy(track => track.Annotation<SdrToneMappedPlaybackTrack>() != null ? 1 : 0)
                .ThenBy(track => hardwareHevc.Contains(track) && (IsTrueUhdVideo(track) || DashManifestBuilder.IsHdr10(track)) ? 0
                    : Str(track, "codecs").StartsWith("avc1.", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
                .ThenBy(track => DashManifestBuilder.IsHdr10(track) == (requestedQuality == 125) ? 0 : 1)
                .ThenByDescending(track => Number(track, "bandwidth"));
        }

        private sealed class SdrToneMappedPlaybackTrack
        {
            internal JToken Source;
        }

        private static int EffectivePlaybackQuality(JToken track)
            => track.Annotation<SdrToneMappedPlaybackTrack>() != null ? 120 : (int)Number(track, "id");

        private static JToken OriginalPlaybackTrack(JToken track)
            => track.Annotation<SdrToneMappedPlaybackTrack>()?.Source ?? track;

        private static bool IsBoundedFourKSource(JToken track)
        {
            long width = Number(track, "width"), height = Number(track, "height");
            return IsTrueUhdVideo(track) && width > 0 && height > 0
                && Math.Max(width, height) <= 4096 && Math.Min(width, height) <= 2160;
        }

        private static string PlaybackVideoSourceKey(JToken track)
        {
            Uri uri;
            string url = DashManifestBuilder.Url(track);
            string path = Uri.TryCreate(url, UriKind.Absolute, out uri) ? uri.AbsolutePath : url;
            var segment = track["SegmentBase"] ?? track["segment_base"];
            return path + "|" + Str(track, "codecs") + "|" + Number(track, "width") + "x" + Number(track, "height")
                + "|" + Number(track, "bandwidth") + "|" + (Str(segment, "Initialization") + Str(segment, "initialization"))
                + "|" + (Str(segment, "indexRange") + Str(segment, "index_range")) + "|" + DashManifestBuilder.IsHdr10(track);
        }

        private PlaybackInfo CreateDashPlaybackChain(JToken data, List<JToken> videos, PlaybackAudioSelection audio,
            double duration, int requestedQuality, JArray rawVideos, PlaybackTrackCapabilities capabilities)
        {
            var ordered = OrderPlaybackVideoTracks(videos, requestedQuality, capabilities.HardwareHevc).ToList();
            int actualQuality = EffectivePlaybackQuality(ordered[0]);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var sameTier = ordered.Where(track => EffectivePlaybackQuality(track) == actualQuality)
                .Where(track => seen.Add(PlaybackVideoSourceKey(track))).ToList();
            var candidates = sameTier.Take(12).ToList();
            var lower = ordered.Where(track => EffectivePlaybackQuality(track) < actualQuality).ToList();
            var compatible = lower.Where(track => Number(track, "id") <= 80
                && Str(track, "codecs").StartsWith("avc1.", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(track => Number(track, "id")).ThenBy(track => Number(track, "bandwidth")).FirstOrDefault()
                ?? lower.FirstOrDefault();
            if (compatible != null && seen.Add(PlaybackVideoSourceKey(compatible))) candidates.Add(compatible);
            PublishPlaybackDiagnostic("candidate-plan qn=" + requestedQuality + " sameTier=" + sameTier.Count
                + " sameTierBudget=12 attempts=" + candidates.Count + " omittedByBudget=" + Math.Max(0, sameTier.Count - 12));
            var provided = new HashSet<int>(videos.Select(EffectivePlaybackQuality));
            PlaybackInfo next = null;
            for (int index = candidates.Count - 1; index >= 0; --index)
            {
                var track = candidates[index];
                var selectedAudio = index == 0 ? audio.Selected : audio.Standard ?? audio.Selected;
                var candidate = CreateDashPlayback(data, track, selectedAudio, duration, requestedQuality, provided, rawVideos,
                    capabilities.HardwareHevc.Contains(track), capabilities.FourKDisplay.Contains(track), capabilities.Hdr10Display.Contains(track),
                    capabilities.BlockedQualities, capabilities.UnknownQualities, capabilities.Diagnostic);
                candidate.CodecFallback = next;
                if (index == 0 && candidate.AudioMode != PlaybackAudioMode.Standard && audio.Standard != null)
                {
                    candidate.AudioFallback = CreateDashPlayback(data, track, audio.Standard, duration, requestedQuality, provided,
                        rawVideos, capabilities.HardwareHevc.Contains(track), capabilities.FourKDisplay.Contains(track),
                        capabilities.Hdr10Display.Contains(track), capabilities.BlockedQualities, capabilities.UnknownQualities, capabilities.Diagnostic);
                    candidate.AudioFallback.CodecFallback = next;
                }
                next = candidate;
            }
            return next;
        }

        private void PublishSelectedPlayback(PlaybackInfo playback)
            => PublishPlaybackDiagnostic("selected qn=" + playback.QualityId + " trackIndex=" + playback.VideoTrackIndex
                + " " + playback.VideoWidth + "x" + playback.VideoHeight + " codec=" + SafePlaybackCodec(playback.VideoCodec)
                + " dynamicRange=" + (playback.IsHdr10 ? "HDR10" : "SDR") + " audio=" + playback.AudioMode
                + " sourceQn=" + playback.SourceQualityId + " outputRange=" + (playback.IsHdr10 && !playback.UsesSdrToneMapping ? "HDR10" : "SDR")
                + " nativeSdrToneMapping=" + playback.UsesSdrToneMapping);

        private static string SafePlaybackCodec(string value)
        {
            string codec = Regex.Replace(value ?? "", "[^A-Za-z0-9._-]", "");
            return codec.Length > 80 ? codec.Substring(0, 80) : codec;
        }

        private PlaybackInfo CreateDashPlayback(JToken data, JToken selected, JToken audio, double duration,
            int requestedQuality, ISet<int> provided, JArray rawVideos, bool hardwareHevc,
            bool fourKDisplayVerified = false, bool hdr10DisplayVerified = false,
            IDictionary<int, string> blockedQualities = null, IDictionary<int, string> unknownQualities = null,
            string capabilityDiagnostic = "")
        {
            int actualQuality = EffectivePlaybackQuality(selected);
            bool toneMapped = selected.Annotation<SdrToneMappedPlaybackTrack>() != null;
            var qualities = MapPlaybackQualities(data, actualQuality, provided, rawVideos, blockedQualities, unknownQualities);
            if (toneMapped)
            {
                var outputOption = qualities.First(item => item.Id == 120);
                outputOption.Label = "4K SDR（HDR源色调映射）";
                outputOption.RequiresLogin = outputOption.RequiresPremium = true;
            }
            var playback = new PlaybackInfo
            {
                Url = DashManifestBuilder.Url(selected), DashManifest = DashManifestBuilder.Build(selected, audio, duration),
                DashVideoUrls = DashManifestBuilder.Urls(selected), DashAudioUrls = DashManifestBuilder.Urls(audio),
                ContentType = "application/dash+xml", QualityId = actualQuality, SourceQualityId = (int)Number(selected, "id"),
                Quality = qualities.First(item => item.Id == actualQuality).Label,
                AvailableQualities = qualities, RequiresHttpHeaders = true, VideoCodec = Str(selected, "codecs"),
                VideoTrackIndex = rawVideos.IndexOf(OriginalPlaybackTrack(selected)),
                VideoWidth = (int)Number(selected, "width"), VideoHeight = (int)Number(selected, "height"),
                VideoBitrate = Number(selected, "bandwidth"), AudioBitrate = Number(audio, "bandwidth"),
                AudioCodec = Str(audio, "codecs"), AudioChannels = AudioChannelCount(audio),
                AudioSampleRate = AudioMetadataNumber(audio, 1, 1048575, "audioSamplingRate", "sample_rate", "sampleRate", "sampling_rate"),
                AudioBitsPerSample = AudioMetadataNumber(audio, 1, 32, "bits_per_sample", "bitsPerSample", "bit_depth", "bitDepth", "bits_per_channel", "bitsPerChannel"),
                AudioMode = DashManifestBuilder.IsFlacAudio(audio) ? PlaybackAudioMode.HiRes
                    : DashManifestBuilder.IsDolbyAudio(audio) ? PlaybackAudioMode.Dolby : PlaybackAudioMode.Standard,
                VideoFrameRate = DashFrameRate(selected), HardwareHevcVerified = hardwareHevc,
                FourKDisplayVerified = fourKDisplayVerified,
                Hdr10DisplayVerified = hdr10DisplayVerified,
                IsHdr10 = DashManifestBuilder.IsHdr10(selected),
                UsesSdrToneMapping = toneMapped,
                VideoBitsPerChannel = VideoBitsPerChannel(selected), CapabilityDiagnostic = capabilityDiagnostic
            };
            double probeRate;
            bool requiresNativeValidation;
            if (PlaybackFrameRatePolicy.TryGetProbeRate(playback.VideoFrameRate, out probeRate, out requiresNativeValidation))
            {
                playback.VideoFrameRateProbe = probeRate;
                playback.RequiresNativeFrameRateValidation = requiresNativeValidation;
                if (requiresNativeValidation) playback.FrameRateNotice = "接口声明 " + playback.VideoFrameRate.ToString("0.###", CultureInfo.InvariantCulture)
                    + (playback.VideoFrameRate > PlaybackFrameRatePolicy.NearSixtyMetadataMaximum
                        ? "fps；仅 60fps 基础能力已通过，正在准备兼容性试播，未确认此帧率的硬件支持。"
                        : "fps，帧率待系统验证；将尝试 60Hz 输出。");
            }
            if (requiresNativeValidation && playback.VideoFrameRate > PlaybackFrameRatePolicy.NearSixtyMetadataMaximum)
                qualities.First(item => item.Id == actualQuality).RequiresCompatibilityTrial = true;
            SetPlaybackDiagnostics(playback, data, requestedQuality);
            return playback;
        }

        private sealed class PlaybackAudioSelection
        {
            internal PlaybackAudioMode Requested;
            internal JToken Standard, Selected, Dolby, HiRes;
            internal string DolbyReason = "", HiResReason = "", Diagnostic = "";
            internal bool DolbyAvailable, HiResAvailable;
        }

        private async Task<PlaybackAudioSelection> SelectPlaybackAudioAsync(JToken data, PlaybackAudioMode requested, CancellationToken ct)
        {
            var selection = new PlaybackAudioSelection { Requested = requested };
            var dash = data["dash"] as JObject;
            var dolbyInfo = dash?["dolby"] as JObject;
            var flacInfo = dash?["flac"] as JObject;
            var standard = PlaybackAudioTracks(dash?["audio"]);
            var dolby = PlaybackAudioTracks(dolbyInfo?["audio"]);
            // display=true only advertises a service feature, not a source or
            // account entitlement. Only a real, clear FLAC track grants Hi-Res.
            var flac = PlaybackAudioTracks(flacInfo?["audio"]);
            selection.Standard = standard.Where(ClearPlaybackAudio).Where(DashManifestBuilder.IsAacAudio)
                .Where(DashManifestBuilder.IsSupportedAudio).OrderByDescending(track => Number(track, "bandwidth")).FirstOrDefault();
            var tracks = dolby.Concat(standard).Where(ClearPlaybackAudio).Where(DashManifestBuilder.IsDolbyAudio)
                .Where(DashManifestBuilder.IsSupportedAudio).OrderByDescending(track => Number(track, "bandwidth")).ToList();
            var decisions = new List<string>();
            var dolbyChoice = await SelectPremiumPlaybackAudioAsync(tracks, false, decisions, ct);
            var flacTracks = flac.Where(ClearPlaybackAudio).Where(DashManifestBuilder.IsFlacAudio)
                .Where(DashManifestBuilder.IsSupportedAudio).OrderByDescending(track => Number(track, "bandwidth")).ToList();
            var hiResChoice = await SelectPremiumPlaybackAudioAsync(flacTracks, true, decisions, ct);
            selection.Dolby = dolbyChoice.Track; selection.DolbyReason = dolbyChoice.Reason;
            selection.HiRes = hiResChoice.Track; selection.HiResReason = hiResChoice.Reason;
            selection.DolbyAvailable = selection.Dolby != null;
            selection.HiResAvailable = selection.HiRes != null;
            selection.Selected = requested == PlaybackAudioMode.Dolby && selection.DolbyAvailable ? selection.Dolby
                : requested == PlaybackAudioMode.HiRes && selection.HiResAvailable ? selection.HiRes : selection.Standard;
            var log = new System.Text.StringBuilder("audio requested=" + requested + " dolbyType=" + Number(dolbyInfo, "type")
                + " auth=" + IsLoggedIn + " premium=" + (CurrentUser?.IsPremium == true)
                + " dolbyAvailable=" + selection.DolbyAvailable + " dolbyReason=" + selection.DolbyReason
                + " hiResAvailable=" + selection.HiResAvailable + " hiResReason=" + selection.HiResReason
                + " flacDisplay=" + Flag(flacInfo, "display") + " query=" + string.Join(",", decisions));
            foreach (var track in standard.Concat(dolby).Concat(flac).Take(32))
            {
                string codec = Regex.Replace(Str(track, "codecs"), "[^A-Za-z0-9._-]", "");
                if (codec.Length > 80) codec = codec.Substring(0, 80);
                string decision = !ClearPlaybackAudio(track) ? "excluded: encrypted audio"
                    : !DashManifestBuilder.IsSupportedAudio(track) ? "excluded: unsupported codec or invalid segments"
                    : DashManifestBuilder.IsDolbyAudio(track) && !selection.DolbyAvailable ? "excluded: " + selection.DolbyReason
                    : DashManifestBuilder.IsFlacAudio(track) && !selection.HiResAvailable ? "excluded: " + selection.HiResReason : "eligible";
                log.Append("\naudio codec=").Append(codec).Append(" bps=").Append(Number(track, "bandwidth"))
                    .Append(" channels=").Append(AudioChannelCount(track))
                    .Append(" sampleRate=").Append(AudioMetadataNumber(track, 1, 1048575, "audioSamplingRate", "sample_rate", "sampleRate", "sampling_rate"))
                    .Append(" bitsPerSample=").Append(AudioMetadataNumber(track, 1, 32, "bits_per_sample", "bitsPerSample", "bit_depth", "bitDepth", "bits_per_channel", "bitsPerChannel"))
                    .Append(" decision=").Append(decision);
            }
            selection.Diagnostic = log.ToString();
            data.AddAnnotation(selection);
            PublishPlaybackDiagnostic(selection.Diagnostic);
            return selection;
        }

        private static List<JToken> PlaybackAudioTracks(JToken value)
            => value is JObject ? new List<JToken> { value }
                : (value as JArray ?? new JArray()).OfType<JObject>().Cast<JToken>().ToList();

        private sealed class PremiumPlaybackAudioChoice
        {
            internal JToken Track;
            internal string Reason = "";
        }

        private async Task<PremiumPlaybackAudioChoice> SelectPremiumPlaybackAudioAsync(List<JToken> tracks,
            bool hiRes, List<string> decisions, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var result = new PremiumPlaybackAudioChoice();
            string name = hiRes ? "Hi-Res 无损" : "杜比";
            result.Reason = !IsLoggedIn ? "需要登录" : CurrentUser?.IsPremium != true ? "需要大会员"
                : tracks.Count == 0 ? "B站本次未返回可用的" + name + "音轨" : "";
            if (result.Reason.Length > 0) return result;
            var probes = new Dictionary<string, PlaybackCodecSupport.Support>(StringComparer.OrdinalIgnoreCase);
            bool unknown = false;
            foreach (var track in tracks)
            {
                ct.ThrowIfCancellationRequested();
                string codec = Str(track, "codecs");
                PlaybackCodecSupport.Support support;
                if (!probes.TryGetValue(codec, out support))
                {
                    support = hiRes ? await PlaybackCodecSupport.CheckFlacDecoderAsync(codec, ct)
                        : await PlaybackCodecSupport.CheckDolbyDecoderAsync(codec, ct);
                    probes[codec] = support;
                }
                decisions.Add(SafePlaybackCodec(codec) + " decoder=" + support);
                if (support == PlaybackCodecSupport.Support.Supported) { result.Track = track; return result; }
                unknown |= support == PlaybackCodecSupport.Support.Unknown;
            }
            result.Reason = unknown ? "设备" + name + "解码能力尚未确认" : "当前设备未提供此" + name + "格式的解码器";
            return result;
        }

        private static bool ClearPlaybackAudio(JToken track) => !Flag(track, "is_drm") && Number(track, "drm_tech_type") == 0
            && Str(track, "widevine_pssh").Length == 0 && Str(track, "playready_pssh").Length == 0;

        private static int AudioChannelCount(JToken track)
        {
            foreach (var field in new[] { "channels", "channel_count", "channelCount" })
            {
                long value = Number(track, field);
                if (value > 0 && value <= 32) return (int)value;
            }
            return 0;
        }

        private static int AudioMetadataNumber(JToken track, int minimum, int maximum, params string[] fields)
        {
            foreach (string field in fields)
            {
                long value = Number(track, field);
                if (value >= minimum && value <= maximum) return (int)value;
            }
            return 0;
        }

        private void SetPlaybackAudioMetadata(PlaybackInfo playback, JToken data)
        {
            var selection = data.Annotation<PlaybackAudioSelection>();
            playback.RequestedAudioMode = selection?.Requested ?? PlaybackAudioMode.Standard;
            playback.AudioCapabilityDiagnostic = selection?.Diagnostic ?? "此兼容播放源未提供可选择的杜比或 Hi-Res 音轨";
            string dolbyReason = selection?.DolbyReason ?? (!IsLoggedIn ? "需要登录"
                : CurrentUser?.IsPremium != true ? "需要大会员" : "此兼容播放源未提供杜比音轨");
            bool dolbyAvailable = selection?.DolbyAvailable == true && playback.DashManifest.Length > 0;
            if (selection?.DolbyAvailable == true && !dolbyAvailable) dolbyReason = "此兼容播放源未提供可切换的杜比音轨";
            string hiResReason = selection?.HiResReason ?? (!IsLoggedIn ? "需要登录"
                : CurrentUser?.IsPremium != true ? "需要大会员" : "此兼容播放源未提供 Hi-Res 无损音轨");
            bool hiResAvailable = selection?.HiResAvailable == true && playback.DashManifest.Length > 0;
            if (selection?.HiResAvailable == true && !hiResAvailable) hiResReason = "此兼容播放源未提供可切换的 Hi-Res 无损音轨";
            playback.AvailableAudioModes = new[]
            {
                new PlaybackAudioOption { Mode = PlaybackAudioMode.Standard, Label = "标准音频", IsAvailable = selection?.Standard != null || playback.AudioMode == PlaybackAudioMode.Standard,
                    Reason = selection?.Standard == null && playback.AudioMode != PlaybackAudioMode.Standard ? "本次未返回标准音轨" : "" },
                new PlaybackAudioOption { Mode = PlaybackAudioMode.Dolby, Label = "杜比音效", IsAvailable = dolbyAvailable,
                    RequiresLogin = true, RequiresPremium = true, Reason = dolbyReason },
                new PlaybackAudioOption { Mode = PlaybackAudioMode.HiRes, Label = "Hi-Res 无损", IsAvailable = hiResAvailable,
                    RequiresLogin = true, RequiresPremium = true, Reason = hiResReason }
            };
            if (playback.RequestedAudioMode == PlaybackAudioMode.Dolby && playback.AudioMode != PlaybackAudioMode.Dolby)
                playback.AudioNotice = "未启用杜比音轨：" + (dolbyReason.Length == 0 ? "此播放源使用标准音轨" : dolbyReason) + "；当前使用标准音频。";
            else if (playback.RequestedAudioMode == PlaybackAudioMode.HiRes && playback.AudioMode != PlaybackAudioMode.HiRes)
                playback.AudioNotice = "未启用 Hi-Res 无损音轨：" + (hiResReason.Length == 0 ? "此播放源使用标准音轨" : hiResReason) + "；当前使用标准音频。";
        }


        private sealed class PlaybackTrackCapabilities
        {
            internal readonly List<JToken> Allowed = new List<JToken>();
            internal readonly HashSet<JToken> HardwareHevc = new HashSet<JToken>();
            internal readonly HashSet<JToken> FourKDisplay = new HashSet<JToken>();
            internal readonly HashSet<JToken> Hdr10Display = new HashSet<JToken>();
            internal readonly Dictionary<int, string> BlockedQualities = new Dictionary<int, string>();
            internal readonly Dictionary<int, string> UnknownQualities = new Dictionary<int, string>();
            internal string Diagnostic;
            internal readonly Dictionary<JToken, string> RejectedTracks = new Dictionary<JToken, string>();
            internal readonly Dictionary<JToken, string> TrackProbes = new Dictionary<JToken, string>();
            internal readonly Dictionary<JToken, string[]> TrackChecks = new Dictionary<JToken, string[]>();
            internal readonly HashSet<int> DeviceBlocked = new HashSet<int>();
            internal readonly HashSet<int> DisplayBlocked = new HashSet<int>();
        }

        private sealed class PlaybackRequestMetadata { internal int Fnval = 2000; }

        private async Task<JObject> GetPlaybackJsonAsync(Dictionary<string, string> parameters, CancellationToken ct)
        {
            try
            {
                var response = await GetSignedJsonAsync("/x/player/wbi/playurl", parameters, ct);
                if (ResponseCode(response) != 0) PublishPlaybackDiagnostic("api-response-failed qn=" + parameters["qn"]
                    + " fnval=" + parameters["fnval"] + " apiCode=" + ResponseCode(response));
                return response;
            }
            catch (BilibiliApiException error)
            {
                PublishPlaybackDiagnostic("api-request-failed qn=" + parameters["qn"] + " fnval=" + parameters["fnval"] + " apiCode=" + error.Code);
                throw;
            }
        }

        private void PublishTrackCatalogue(JToken data, PlaybackTrackCapabilities capabilities, int quality)
        {
            var report = new System.Text.StringBuilder("catalog qn=" + quality + " fnval=" + (data.Annotation<PlaybackRequestMetadata>()?.Fnval ?? 2000)
                + " fourk=1 auth=" + IsLoggedIn + " sessionCookie=" + !string.IsNullOrEmpty(_cookies.GetCookieValue("SESSDATA"))
                + " premium=" + (CurrentUser?.IsPremium == true) + " serverQuality=" + Number(data, "quality")
                + "\naccept_quality=" + string.Join(",", (data["accept_quality"] as JArray ?? new JArray()).Select(value => NumberValue(value))));
            foreach (var format in (data["support_formats"] as JArray ?? new JArray()).Take(32))
                report.Append("\nformat qn=").Append(Number(format, "quality")).Append(" need_login=").Append(Flag(format, "need_login"))
                    .Append(" need_vip=").Append(Flag(format, "need_vip")).Append(" reason=").Append(Number(format, "can_watch_qn_reason"));
            AppendPlaybackTrackDiagnostic(report, data, capabilities);
            PublishPlaybackDiagnostic(report.ToString());
        }

        private static long NumberValue(JToken value)
        { long number; return long.TryParse(value?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) ? number : 0; }

        private static void AppendPlaybackTrackDiagnostic(System.Text.StringBuilder report, JToken data, PlaybackTrackCapabilities capabilities)
        {
            var raw = data["dash"]?["video"] as JArray ?? new JArray();
            report.Append("\ntrackCount=").Append(raw.Count);
            for (int index = 0; index < raw.Count; ++index)
            {
                var track = raw[index];
                string reason;
                if (capabilities == null || !capabilities.RejectedTracks.TryGetValue(track, out reason))
                    reason = PlaybackVideoRejectionReason(track);
                if (Number(track, "id") <= 0) reason = "无有效画质编号";
                var checks = new[] { "not_requested", "not_requested", "not_requested" };
                string[] recorded;
                if (capabilities != null && capabilities.TrackChecks.TryGetValue(track, out recorded)) checks = recorded;
                bool toneMappedOutput = capabilities != null && capabilities.Allowed.Any(candidate =>
                    candidate.Annotation<SdrToneMappedPlaybackTrack>() != null && ReferenceEquals(OriginalPlaybackTrack(candidate), track));
                report.Append("\ntrack index=").Append(index).Append(" qn=").Append(Number(track, "id"))
                    .Append(" ").Append(Number(track, "width")).Append("x").Append(Number(track, "height"))
                    .Append(" codec=").Append(SafePlaybackCodec(Str(track, "codecs")))
                    .Append(" dynamicRange=").Append(DashManifestBuilder.IsHdr10(track) ? "HDR10"
                        : DashManifestBuilder.VideoRejectionReason(track).Contains("HDR") ? "unsupportedHdr" : "SDR")
                    .Append(" fps=").Append(DashFrameRate(track).ToString("0.###", CultureInfo.InvariantCulture))
                    .Append(" frameRateRaw=").Append(SafePlaybackFrameRateMetadata(Str(track, "frameRate")))
                    .Append(" frame_rateRaw=").Append(SafePlaybackFrameRateMetadata(Str(track, "frame_rate")))
                    .Append(" fpsField=").Append(Str(track, "frameRate").Length > 0 ? "frameRate"
                        : Str(track, "frame_rate").Length > 0 ? "frame_rate" : "missing")
                    .Append(" aliasConflict=").Append(Str(track, "frameRate").Length > 0 && Str(track, "frame_rate").Length > 0
                        && !string.Equals(Str(track, "frameRate"), Str(track, "frame_rate"), StringComparison.Ordinal) ? "true" : "false")
                    .Append(" bps=").Append(Number(track, "bandwidth")).Append(" bpc=").Append(VideoBitsPerChannel(track))
                    .Append(" decision=").Append(toneMappedOutput ? "SDR-tone-map-eligible; HDR-output-excluded: " + reason
                        : string.IsNullOrEmpty(reason) ? "eligible" : "excluded: " + reason)
                    .Append(" decode=").Append(checks[0]).Append(" sdrDisplay=").Append(checks[1]).Append(" hdrDisplay=").Append(checks[2]);
                string probes;
                if (capabilities != null && capabilities.TrackProbes.TryGetValue(track, out probes) && probes.Length > 0)
                    report.Append("\nprobe index=").Append(index).Append(" ").Append(probes);
            }
            report.Append("\n").Append(capabilities?.Diagnostic ?? "");
        }

        private static Task<PlaybackTrackCapabilities> FilterPlaybackCapabilitiesAsync(JToken data,
            List<JToken> videos, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            // Capture on the playback view before entering the worker. This
            // enumerates supported output modes, never the current dashboard
            // mode, and never requests an HDMI switch while building a menu.
            var outputModes = PlaybackCodecSupport.CaptureDisplayModes();
            // ProtectionCapabilities hardware queries can block. Run those off
            // the UI thread using the immutable output capability snapshot.
            return Task.Run(() =>
            {
                var result = new PlaybackTrackCapabilities();
                var log = new PlaybackCodecSupport.ProbeLog();
                var queried = new HashSet<JToken>();
                var deviceRejected = new HashSet<JToken>();
                var sdrDisplayRejected = new HashSet<JToken>();
                var unknownTracks = new HashSet<JToken>();
                foreach (var raw in data["dash"]?["video"] as JArray ?? new JArray())
                {
                    string reason = PlaybackVideoRejectionReason(raw);
                    if (Number(raw, "id") <= 0) reason = "无有效画质编号";
                    if (reason.Length > 0) result.RejectedTracks[raw] = reason;
                    else if (!videos.Contains(raw)) result.RejectedTracks[raw] = "轨道未通过当前播放场景预筛选";
                }
                foreach (var track in videos)
                {
                    ct.ThrowIfCancellationRequested();
                    var trackLog = new PlaybackCodecSupport.ProbeLog();
                    var checks = new[] { "not_requested", "not_requested", "not_requested" };
                    result.TrackChecks[track] = checks;
                    try
                    {
                    int id = (int)Number(track, "id");
                    bool uhd = IsTrueUhdVideo(track), hdr = DashManifestBuilder.IsHdr10(track);
                    bool highTier = id == 120 || id == 127;
                    if (highTier && !HasUhdDimensions(track, id == 127 ? 7680 : 3840, id == 127 ? 4320 : 2160))
                    {
                        result.BlockedQualities[id] = "此档播放源未提供可验证的超高清分辨率";
                        result.RejectedTracks[track] = result.BlockedQualities[id];
                        continue;
                    }
                    bool hevc = IsHevc(track);
                    bool av1 = Str(track, "codecs").StartsWith("av01.", StringComparison.OrdinalIgnoreCase);
                    int width = (int)Number(track, "width"), height = (int)Number(track, "height");
                    int bits = VideoBitsPerChannel(track);
                    double rate = DashFrameRate(track);
                    string codec = Str(track, "codecs");
                    long bitrate = Number(track, "bandwidth");
                    if (hdr && bits != 10)
                    {
                        result.RejectedTracks[track] = "HDR10轨道未提供符合要求的10位视频参数，未发起硬件能力查询";
                        result.BlockedQualities[id] = result.RejectedTracks[track];
                        trackLog.Add("HDR10位深参数无效", "validation=hdr-bit-depth; bpc=" + bits + "; query=not-issued");
                        continue;
                    }
                    queried.Add(track);
                    // Xbox's published AVC decoder ceiling is 1080P60. A
                    // generic protection query must not authorize software AVC
                    // 4K in place of a supported HEVC ultra-HD representation.
                    if (outputModes.IsXbox && Str(track, "codecs").StartsWith("avc1.", StringComparison.OrdinalIgnoreCase)
                        && (Math.Max(width, height) > 1920 || Math.Min(width, height) > 1080))
                    {
                        result.BlockedQualities[id] = "Xbox 的 H.264 播放上限为 1080P，需要可硬件解码的超高清播放源";
                        result.RejectedTracks[track] = result.BlockedQualities[id]; deviceRejected.Add(track); continue;
                    }
                    if (!uhd && !hdr && !hevc && !av1) { result.Allowed.Add(track); continue; }
                    var decode = PlaybackCodecSupport.CheckDecode(codec, width, height,
                        rate > 0 ? rate : 60, bitrate, bits, trackLog);
                    checks[0] = decode.ToString();
                    if (decode != PlaybackCodecSupport.Support.Supported && hevc && !uhd && !hdr && !outputModes.IsXbox)
                    {
                        // Desktop can use its installed software HEVC decoder
                        // for ordinary SDR; it still receives an AVC fallback.
                        result.Allowed.Add(track);
                        continue;
                    }
                    var display = !uhd || hdr ? PlaybackCodecSupport.Support.Supported
                        : PlaybackCodecSupport.CheckDisplay(codec, width, height,
                            rate > 0 ? rate : 60, bitrate, bits, false, outputModes, trackLog);
                    var displayHdr = !hdr ? PlaybackCodecSupport.Support.Supported
                        : bits != 10 ? PlaybackCodecSupport.Support.Unsupported
                        : PlaybackCodecSupport.CheckDisplay(codec, width, height,
                            rate > 0 ? rate : 60, bitrate, bits, true, outputModes, trackLog);
                    checks[1] = !uhd || hdr ? "not_requested" : display.ToString();
                    checks[2] = !hdr ? "not_requested" : displayHdr.ToString();
                    if (hdr && (id == 120 || id == 125) && IsBoundedFourKSource(track) && bits == 10
                        && decode == PlaybackCodecSupport.Support.Supported && displayHdr != PlaybackCodecSupport.Support.Supported)
                    {
                        // Microsoft documents native HDR10-to-SDR tone mapping.
                        // Keep the original HDR representation and PQ metadata;
                        // a separate output variant independently proves real
                        // 4K decoding and a usable 4K SDR display mode.
                        var sdr = PlaybackCodecSupport.CheckDisplay(codec, width, height,
                            rate > 0 ? rate : 60, bitrate, bits, false, outputModes, trackLog);
                        checks[1] = sdr.ToString();
                        if (sdr == PlaybackCodecSupport.Support.Supported)
                        {
                            var variant = track.DeepClone();
                            variant.AddAnnotation(new SdrToneMappedPlaybackTrack { Source = track });
                            result.Allowed.Add(variant);
                            if (hevc) result.HardwareHevc.Add(variant);
                            result.FourKDisplay.Add(variant);
                            trackLog.Add("HDR10→SDR 输出变体=Supported",
                                "output-variant sourceQn=" + id + " effectiveQn=120 source=HDR10 output=SDR toneMapping=native decode=Supported sdrDisplay=Supported");
                        }
                    }
                    if (decode != PlaybackCodecSupport.Support.Supported || display != PlaybackCodecSupport.Support.Supported
                        || displayHdr != PlaybackCodecSupport.Support.Supported)
                    {
                        bool unsupported = decode == PlaybackCodecSupport.Support.Unsupported
                            || display == PlaybackCodecSupport.Support.Unsupported || displayHdr == PlaybackCodecSupport.Support.Unsupported;
                        if (!unsupported) result.UnknownQualities[id] = "设备能力查询尚未确认，可重新打开播放设置重试";
                        else result.BlockedQualities[id] = decode == PlaybackCodecSupport.Support.Unsupported ? "当前设备不支持此档编码的硬件解码能力"
                            : display == PlaybackCodecSupport.Support.Unsupported ? "当前设备不支持此档 4K 显示能力" : "当前设备不支持此档 HDR10 显示能力";
                        result.RejectedTracks[track] = unsupported ? result.BlockedQualities[id] : result.UnknownQualities[id];
                        bool invalidMetadata = trackLog.Details.Contains("validation=");
                        if (invalidMetadata) result.RejectedTracks[track] = "轨道元数据或媒体参数无效，未向系统发起对应能力查询";
                        if (unsupported && !invalidMetadata) deviceRejected.Add(track);
                        if (!unsupported) unknownTracks.Add(track);
                        if (decode == PlaybackCodecSupport.Support.Supported
                            && !hdr && display == PlaybackCodecSupport.Support.Unsupported)
                            sdrDisplayRejected.Add(track);
                        continue;
                    }
                    result.Allowed.Add(track);
                    if (hevc) result.HardwareHevc.Add(track);
                    if (uhd) result.FourKDisplay.Add(track);
                    if (hdr) result.Hdr10Display.Add(track);
                    }
                    finally
                    {
                        result.TrackProbes[track] = trackLog.Details;
                        log.Merge(trackLog);
                    }
                }
                // A blocked codec must not hide another returned, usable codec
                // for the same tier. Absence of a track, however, is not a reason
                // to skip device checks on advertised 4K/HDR menu entries.
                foreach (var id in result.BlockedQualities.Keys.ToList())
                    if (result.Allowed.Any(track => EffectivePlaybackQuality(track) == id) || result.UnknownQualities.ContainsKey(id))
                        result.BlockedQualities.Remove(id);
                foreach (var id in result.UnknownQualities.Keys.ToList())
                    if (result.Allowed.Any(track => EffectivePlaybackQuality(track) == id)) result.UnknownQualities.Remove(id);
                foreach (var group in videos.GroupBy(track => (int)Number(track, "id")))
                {
                    int id = group.Key;
                    if (result.Allowed.Any(track => EffectivePlaybackQuality(track) == id)) continue;
                    var candidates = group.Where(queried.Contains).ToList();
                    if (unknownTracks.Any(track => Number(track, "id") == id))
                    { result.BlockedQualities.Remove(id); continue; }
                    if (candidates.Count > 0 && candidates.All(deviceRejected.Contains)) result.DeviceBlocked.Add(id);
                    // An HDR10 display rejection is no proof that the same
                    // quality cannot be delivered as SDR by another codec.
                    var sdrCandidates = candidates.Where(track => !DashManifestBuilder.IsHdr10(track)).ToList();
                    if (sdrCandidates.Count > 0 && sdrCandidates.All(sdrDisplayRejected.Contains)) result.DisplayBlocked.Add(id);
                    var reasons = group.Where(result.RejectedTracks.ContainsKey).Select(track => result.RejectedTracks[track]).Distinct().Take(3);
                    result.BlockedQualities[id] = string.Join("；", reasons);
                }
                foreach (var token in data["accept_quality"] as JArray ?? new JArray())
                {
                    ct.ThrowIfCancellationRequested();
                    int id;
                    if (!int.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out id)
                        || result.BlockedQualities.ContainsKey(id)
                        || result.UnknownQualities.ContainsKey(id)
                        || result.Allowed.Any(track => EffectivePlaybackQuality(track) == id)) continue;
                    var returned = data["dash"]?["video"] as JArray;
                    if (returned != null && returned.Any(track => Number(track, "id") == id)
                        && !videos.Any(track => Number(track, "id") == id))
                    {
                        if (id == 120 || id == 125 || id == 126 || id == 127)
                            result.BlockedQualities[id] = "此档视频编码或 HDR 格式暂不受支持";
                        continue;
                    }
                    if (id == 126)
                        result.BlockedQualities[id] = "当前播放器不支持 Dolby Vision";
                    else if (id == 120 || id == 127)
                    {
                        int width = id == 127 ? 7680 : 3840, height = id == 127 ? 4320 : 2160;
                        var support = PlaybackCodecSupport.CheckDisplay("hvc1", width, height, 30, 20000000, 10, false, outputModes, log);
                        if (support != PlaybackCodecSupport.Support.Supported)
                        {
                            var av1 = PlaybackCodecSupport.CheckDisplay("av01", width, height, 30, 20000000, 8, false, outputModes, log);
                            support = av1 == PlaybackCodecSupport.Support.Supported ? av1
                                : support == PlaybackCodecSupport.Support.Unknown || av1 == PlaybackCodecSupport.Support.Unknown
                                    ? PlaybackCodecSupport.Support.Unknown : PlaybackCodecSupport.Support.Unsupported;
                        }
                        if (!outputModes.IsXbox && support != PlaybackCodecSupport.Support.Supported)
                        {
                            var avc = PlaybackCodecSupport.CheckDisplay("avc1", width, height, 30, 20000000, 8, false, outputModes, log);
                            support = avc == PlaybackCodecSupport.Support.Supported ? avc
                                : support == PlaybackCodecSupport.Support.Unknown || avc == PlaybackCodecSupport.Support.Unknown
                                    ? PlaybackCodecSupport.Support.Unknown : PlaybackCodecSupport.Support.Unsupported;
                        }
                        if (support == PlaybackCodecSupport.Support.Unsupported) result.BlockedQualities[id] = "当前设备不支持超高清解码及显示能力";
                        else if (support == PlaybackCodecSupport.Support.Unknown) result.UnknownQualities[id] = "超高清能力查询尚未确认，可重新打开播放设置重试";
                    }
                    else if (id == 125)
                    {
                        var support = PlaybackCodecSupport.CheckDisplay("hvc1", 1920, 1080, 30, 20000000, 10, true, outputModes, log);
                        if (support == PlaybackCodecSupport.Support.Unsupported) result.BlockedQualities[id] = "当前设备不支持 HDR10 解码及显示能力";
                        else if (support == PlaybackCodecSupport.Support.Unknown) result.UnknownQualities[id] = "HDR10 能力查询尚未确认，可重新打开播放设置重试";
                    }
                }
                ct.ThrowIfCancellationRequested();
                result.Diagnostic = outputModes.Diagnostic + "；" + log;
                data.AddAnnotation(result);
                return result;
            }, ct);
        }
        private static bool IsUhdHevc(JToken track)
        {
            var codec = Str(track, "codecs");
            return (Number(track, "id") >= 120 || Math.Max(Number(track, "width"), Number(track, "height")) >= 3000)
                && (codec.StartsWith("hvc1.", StringComparison.OrdinalIgnoreCase) || codec.StartsWith("hev1.", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsHevc(JToken track)
        {
            var codec = Str(track, "codecs");
            return codec.StartsWith("hvc1.", StringComparison.OrdinalIgnoreCase)
                || codec.StartsWith("hev1.", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTrueUhdVideo(JToken track)
        {
            return HasUhdDimensions(track, 3840, 2160);
        }

        private static bool HasUhdDimensions(JToken track, int longEdge, int shortEdge)
        {
            long width = Number(track, "width"), height = Number(track, "height");
            // 2880x2160 (4:3), cropped cinema and portrait streams are real
            // platform UHD tiers too. Neither the title nor qn alone is proof.
            return width > 0 && height > 0 && (Math.Max(width, height) >= longEdge
                || Math.Min(width, height) >= shortEdge);
        }

        private static int VideoBitsPerChannel(JToken track)
        {
            foreach (var name in new[] { "bits_per_channel", "bitsPerChannel", "bit_depth", "bitDepth", "bpc" })
            {
                int value;
                if (int.TryParse(Str(track, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                    && value >= 8 && value <= 16) return value;
            }
            var codec = Str(track, "codecs");
            var av1 = codec.Split('.');
            int av1Depth;
            if (codec.StartsWith("av01.", StringComparison.OrdinalIgnoreCase) && av1.Length >= 4
                && int.TryParse(av1[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out av1Depth)
                && (av1Depth == 8 || av1Depth == 10 || av1Depth == 12)) return av1Depth;
            return codec.StartsWith("hvc1.2", StringComparison.OrdinalIgnoreCase)
                || Str(track, "codecs").StartsWith("hev1.2", StringComparison.OrdinalIgnoreCase) ? 10 : 8;
        }

        private static double DashFrameRate(JToken track)
        {
            var text = Str(track, "frameRate");
            if (text.Length == 0) text = Str(track, "frame_rate");
            var parts = text.Split('/');
            double numerator, denominator = 1;
            if (parts.Length < 1 || parts.Length > 2 ||
                !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out numerator) ||
                (parts.Length == 2 && !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out denominator)) ||
                numerator <= 0 || denominator <= 0 || double.IsInfinity(numerator) || double.IsNaN(numerator)) return 0;
            double rate = numerator / denominator;
            return !double.IsInfinity(rate) && !double.IsNaN(rate) ? rate : 0;
        }

        private static string SafePlaybackFrameRateMetadata(string value)
        {
            if (value.Length == 0) return "<missing>";
            // Do not echo arbitrary server strings, URLs, tokens or newlines
            // into a diagnostic report. Retain bounded numeric/rational syntax
            // only; parser priority and the original response stay unchanged.
            return value.Length <= 64 && Regex.IsMatch(value,
                @"\A[+]?(?:[0-9]+(?:\.[0-9]+)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?(?:/[+]?(?:[0-9]+(?:\.[0-9]+)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?)?\z")
                ? value : "<invalid>";
        }

        private async Task<PlaybackInfo> GetMp4PlaybackAsync(VideoDetail video, int quality, CancellationToken ct,
            IDictionary<int, string> blockedQualities = null)
        {
            var root = await GetJsonAsync(Api + "/x/player/playurl?bvid=" + Uri.EscapeDataString(video.Bvid)
                + "&cid=" + Invariant(video.Cid) + "&qn=" + Invariant(quality)
                + "&fnval=1&fnver=0&fourk=0&platform=html5", ct);
            var data = RequireData(root);
            var compatibleCapabilities = await FilterPlaybackCapabilitiesAsync(data, new List<JToken>(), ct);
            var combinedBlocks = blockedQualities == null ? new Dictionary<int, string>()
                : new Dictionary<int, string>(blockedQualities);
            foreach (var blocked in compatibleCapabilities.BlockedQualities) combinedBlocks[blocked.Key] = blocked.Value;
            var playback = MapMp4Playback(data, combinedBlocks, compatibleCapabilities.UnknownQualities);
            playback.CapabilityDiagnostic = compatibleCapabilities.Diagnostic;
            SetPlaybackDiagnostics(playback, data, quality);
            return playback;
        }

        private PlaybackInfo MapMp4Playback(JToken data, IDictionary<int, string> blockedQualities = null,
            IDictionary<int, string> unknownQualities = null)
        {
            var streams = data["durl"] as JArray;
            if (Str(data, "format").IndexOf("mp4", StringComparison.OrdinalIgnoreCase) < 0)
                throw new BilibiliApiException(-1, "该视频未提供本客户端支持的 MP4 格式，请选择其他视频或使用官方客户端。");
            if (streams == null || streams.Count == 0)
                throw new BilibiliApiException(-1, "该视频未提供本客户端支持的 MP4 播放源，可能需要登录、付费或官方客户端。");
            if (streams.Count != 1)
                throw new BilibiliApiException(-1, "该视频返回多个媒体分段，当前版本暂不支持，请选择其他视频。");
            var url = MediaUrl(Str(streams[0], "url"));
            if (string.IsNullOrEmpty(url)) throw new BilibiliApiException(-1, "B站没有返回有效的播放地址。");
            var qualityNumber = (int)Number(data, "quality");
            if (qualityNumber <= 0)
                throw new BilibiliApiException(-1, "B站没有返回有效的实际播放画质，请重试。");
            if (qualityNumber == 120 || qualityNumber == 125 || qualityNumber == 126 || qualityNumber == 127)
                throw new BilibiliApiException(-1, "此兼容播放源缺少可验证的超高清或 HDR 视频轨道，请选择普通画质。");
            var qualities = MapPlaybackQualities(data, qualityNumber, blockedQualities: blockedQualities,
                unknownQualities: unknownQualities);
            var actual = qualities.First(item => item.Id == qualityNumber);
            string blockedReason;
            if (blockedQualities != null && blockedQualities.TryGetValue(qualityNumber, out blockedReason))
                throw new BilibiliApiException(-1, blockedReason);
            var backups = (streams[0]["backup_url"] as JArray ?? new JArray())
                .Select(value => MediaUrl(value.Value<string>())).Where(value => value.Length > 0).ToList();
            // A requested tier may be downgraded by the server. Report only its
            // actual response, and never relabel the returned source as requested.
            return new PlaybackInfo
            {
                Url = url, BackupUrls = backups, Quality = actual.Label, QualityId = qualityNumber, SourceQualityId = qualityNumber,
                AvailableQualities = qualities, RequiresHttpHeaders = true
            };
        }

        private IReadOnlyList<PlaybackQuality> MapPlaybackQualities(JToken data, int actualQuality,
            ISet<int> dashQualities = null, JArray rawVideos = null,
            IDictionary<int, string> blockedQualities = null, IDictionary<int, string> unknownQualities = null)
        {
            var accepted = new JArray(data["accept_quality"] as JArray ?? new JArray());
            // The returned representations are stronger evidence than an
            // incomplete advertised menu. Keep every real returned tier.
            foreach (var id in dashQualities ?? new HashSet<int>())
                if (!accepted.Any(value => value.ToString() == Invariant(id))) accepted.Add(id);
            var descriptions = data["accept_description"] as JArray ?? new JArray();
            var formats = data["support_formats"] as JArray ?? new JArray();
            var result = new List<PlaybackQuality>();
            for (var i = 0; i < accepted.Count; i++)
            {
                int id;
                if (!int.TryParse(accepted[i].ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out id)
                    || id <= 0 || result.Any(item => item.Id == id)) continue;
                var support = formats.FirstOrDefault(item => Number(item, "quality") == id);
                var label = Str(support, "new_description");
                if (string.IsNullOrWhiteSpace(label) && i < descriptions.Count) label = descriptions[i].ToString();
                if (string.IsNullOrWhiteSpace(label)) label = Str(support, "display_desc");
                if (string.IsNullOrWhiteSpace(label)) label = id == 120 ? "4K 超高清" : id == 127 ? "8K 超高清" : "画质 " + Invariant(id);
                var provided = (dashQualities != null && dashQualities.Contains(id)) || id == actualQuality;
                var outputTracks = data.Annotation<PlaybackTrackCapabilities>()?.Allowed
                    .Where(track => EffectivePlaybackQuality(track) == id).ToList() ?? new List<JToken>();
                bool toneMappedOnly = id == 120 && outputTracks.Count > 0
                    && outputTracks.All(track => track.Annotation<SdrToneMappedPlaybackTrack>() != null);
                if (toneMappedOnly) label = "4K SDR（HDR源色调映射）";
                var needsLogin = Flag(support, "need_login") || (!provided && !IsLoggedIn && id >= 80);
                var needsPremium = Flag(support, "need_vip") || toneMappedOnly;
                var format = Str(support, "format");
                // Historical values such as flv/hdflv2 describe the quality
                // tier even when real DASH tracks use video/mp4. They are not
                // evidence that a DASH tier has an unsupported container.
                var unsupported = dashQualities == null && format.Length > 0
                    && format.IndexOf("mp4", StringComparison.OrdinalIgnoreCase) < 0;
                // The site's numeric reason fields are not documented entitlement
                // flags. Do not permanently disable advertised tiers from these
                // numbers, or from absence in a different qn's response. A click
                // must be able to fetch that tier using the real signed session.
                var returnedUnsupported = rawVideos != null && rawVideos.Any(item => Number(item, "id") == id)
                    && !provided;
                string blockedReason = "";
                var blocked = blockedQualities != null && blockedQualities.TryGetValue(id, out blockedReason);
                string unknownReason = "";
                var unknown = unknownQualities != null && unknownQualities.TryGetValue(id, out unknownReason);
                var reason = blocked ? blockedReason : unknown ? unknownReason
                    : unsupported ? "当前视频未提供此画质的 MP4 播放源"
                    : needsLogin && !IsLoggedIn ? "请先扫码登录"
                    : needsPremium && (!IsLoggedIn || CurrentUser?.IsPremium != true) ? "需要相应大会员权限"
                    : returnedUnsupported ? "本次返回的此档视频编码或分段格式暂不受支持" : "";
                result.Add(new PlaybackQuality
                {
                    Id = id, Label = label, RequiresLogin = needsLogin, RequiresPremium = needsPremium,
                    IsAvailable = (reason.Length == 0 || id == actualQuality) && !blocked && !unknown,
                    IsVisible = !blocked,
                    IsCapabilityPending = unknown,
                    HasPlaybackSource = provided,
                    RequiresCompatibilityTrial = outputTracks.Count > 0 && outputTracks.All(track =>
                        DashFrameRate(track) > PlaybackFrameRatePolicy.NearSixtyMetadataMaximum
                            && DashFrameRate(track) <= PlaybackFrameRatePolicy.MaximumTrialReportedFrameRate),
                    IsProbe = !provided && reason.Length == 0,
                    Reason = id == actualQuality ? "" : reason.Length > 0 ? reason
                        : !provided ? "选择后向B站请求此档画质" : ""
                });
            }
            // Keep playback usable if the response omits its advertised list.
            // The one source that was actually returned is the only inferred tier.
            if (!result.Any(item => item.Id == actualQuality))
                result.Add(new PlaybackQuality { Id = actualQuality, Label = "画质 " + Invariant(actualQuality), HasPlaybackSource = true });
            return result.OrderByDescending(item => item.Id).ToList();
        }

        private void SetPlaybackDiagnostics(PlaybackInfo playback, JToken data, int requestedQuality)
        {
            SetPlaybackAudioMetadata(playback, data);
            playback.RequestedQualityId = requestedQuality;
            playback.ServerQualityId = (int)Number(data, "quality");
            var raw = data["dash"]?["video"] as JArray ?? new JArray();
            var capabilities = data.Annotation<PlaybackTrackCapabilities>();
            var requested = playback.AvailableQualities.FirstOrDefault(item => item.Id == requestedQuality);
            var target = requested?.Label ?? ("画质 " + Invariant(requestedQuality));
            if (playback.QualityId != requestedQuality)
            {
                playback.QualityFailure = !raw.Any(track => Number(track, "id") == requestedQuality) ? PlaybackQualityFailure.ServerNotReturned
                    : requested?.IsCapabilityPending == true ? PlaybackQualityFailure.CapabilityUnconfirmed
                    : capabilities?.DeviceBlocked.Contains(requestedQuality) == true ? PlaybackQualityFailure.DeviceUnsupported
                    : PlaybackQualityFailure.SourceUnsupported;
                var cause = playback.QualityFailure == PlaybackQualityFailure.ServerNotReturned ? "服务端未返回该档视频流"
                    : playback.QualityFailure == PlaybackQualityFailure.DeviceUnsupported ? "设备不支持返回轨道的编码或显示要求"
                    : playback.QualityFailure == PlaybackQualityFailure.CapabilityUnconfirmed ? "设备能力尚未确认"
                    : "返回轨道的分辨率、格式或分段信息不符合播放要求";
                playback.QualityNotice = "已请求 " + target + "，" + cause + "，实际播放 " + playback.Quality + "。";
                if (requested != null && !requested.IsAvailable && requested.Reason.Length > 0)
                    playback.QualityNotice += requested.Reason + "。";
            }
            var report = new System.Text.StringBuilder("candidate requestedQn=" + requestedQuality + " fnval=" + (data.Annotation<PlaybackRequestMetadata>()?.Fnval ?? 2000) + " fourk=1"
                + " auth=" + IsLoggedIn + " sessionCookie=" + !string.IsNullOrEmpty(_cookies.GetCookieValue("SESSDATA"))
                + " premium=" + (CurrentUser?.IsPremium == true) + " serverQuality=" + playback.ServerQualityId
                + " candidateQn=" + playback.QualityId + " trackIndex=" + playback.VideoTrackIndex + " result=" + playback.QualityFailure
                + "\naccept_quality=" + string.Join(",", (data["accept_quality"] as JArray ?? new JArray()).Select(value => NumberValue(value))));
            AppendPlaybackTrackDiagnostic(report, data, capabilities);
            playback.PlaybackDiagnostic = report.ToString();
            PublishPlaybackDiagnostic("candidate qn=" + playback.QualityId + " trackIndex=" + playback.VideoTrackIndex
                + " " + playback.VideoWidth + "x" + playback.VideoHeight + " codec=" + SafePlaybackCodec(playback.VideoCodec)
                + " dynamicRange=" + (playback.IsHdr10 ? "HDR10" : "SDR") + " audio=" + playback.AudioMode
                + " sourceQn=" + playback.SourceQualityId + " outputRange=" + (playback.IsHdr10 && !playback.UsesSdrToneMapping ? "HDR10" : "SDR")
                + " nativeSdrToneMapping=" + playback.UsesSdrToneMapping);
        }

        private static bool Flag(JToken item, string name)
        {
            var value = Str(item, name);
            return value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static long? OptionalCommentCount(JToken value)
        {
            long count;
            return value != null && long.TryParse(value.ToString(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out count) && count >= 0 ? count : (long?)null;
        }

        public Task<CommentPage> GetCommentsAsync(long aid, int page = 1,
            CancellationToken ct = default(CancellationToken))
        {
            CheckId(aid, nameof(aid));
            CheckPage(page);
            if (page != 1)
                throw new ArgumentException("评论翻页需要上一页返回的游标，请先刷新评论。", nameof(page));
            return GetCommentsAsync(aid, "", ct);
        }

        public async Task<CommentPage> GetCommentsAsync(long aid, string cursor,
            CancellationToken ct = default(CancellationToken))
        {
            CheckId(aid, nameof(aid));
            var account = CaptureCommentReadAccount();
            cursor = cursor ?? "";
            if (cursor.Length > 16384) throw new ArgumentException("评论分页信息过长，请刷新评论。", nameof(cursor));
            if (string.IsNullOrEmpty(_cookies.GetCookieValue("buvid3")))
                await BootstrapWebsiteAsync(ct);
            // The legacy /x/v2/reply route can return code 0 with an empty list
            // and count 0 even for videos with thousands of public comments.
            // Prefer opaque next_offset; some otherwise valid pages expose only
            // the server's older numeric next cursor. Preserve its format too.
            var parameters = CommentRequestParameters(1, aid, cursor);
            var root = await GetSignedJsonAsync("/x/v2/reply/wbi/main", parameters, ct);
            if (ResponseCode(root) == 12002)
                return new CommentPage { IsClosed = true, Notice = "该视频的评论区已关闭。" };
            var data = RequireData(root);
            var result = MapCommentPage(data, cursor, IsLoggedIn);
            AttachCommentContext(result.Items, aid, 1, account, Number(data["upper"], "mid"));
            return result;
        }

        private static CommentPage MapCommentPage(JToken data, string requestedCursor, bool loggedIn)
        {
            var control = data["control"] as JObject;
            var message = Str(control, "show_text");
            if (string.IsNullOrWhiteSpace(message)) message = Str(control?["empty_page"] as JObject, "text");
            var closed = message.IndexOf("评论", StringComparison.Ordinal) >= 0
                && (message.IndexOf("已关闭", StringComparison.Ordinal) >= 0
                    || message.IndexOf("关闭了", StringComparison.Ordinal) >= 0);
            if (closed)
                return new CommentPage { IsClosed = true, Notice = "该视频的评论区已关闭。" };

            var cursor = data["cursor"] as JObject;
            bool ended;
            if (cursor == null || !bool.TryParse(Str(cursor, "is_end"), out ended))
                throw new BilibiliApiException(-1, "B站评论响应缺少分页信息，请更新客户端或稍后重试。");
            long total;
            var totalKnown = long.TryParse(Str(cursor, "all_count"), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out total) && total >= 0;
            if (!totalKnown) total = 0;
            var raw = new List<JToken>();
            if (requestedCursor.Length == 0)
            {
                // Some videos put their only visible comments in top/top_replies
                // or hots. Do not drop those, or repeat them on every cursor page.
                var top = data["top"] as JObject;
                if (top != null)
                    foreach (var name in new[] { "admin", "upper", "vote" })
                        if (top[name] is JObject) raw.Add(top[name]);
                AddCommentArray(raw, data["top_replies"]);
                AddCommentArray(raw, data["hots"]);
            }
            AddCommentArray(raw, data["replies"]);
            var unique = raw.GroupBy(item => Number(item, "rpid")).Select(group => group.First()).ToList();
            if (unique.Any(item => Number(item, "rpid") <= 0 || !(item["member"] is JObject)
                || !(item["content"] is JObject)))
                throw new BilibiliApiException(-1, "B站评论数据格式已变化，请更新客户端。");
            var items = unique.Select(item => MapCommentItem(item, true)).ToList();
            var pinned = new HashSet<long>();
            if (requestedCursor.Length == 0)
            {
                var top = data["top"] as JObject;
                if (top != null)
                    foreach (var property in top.Properties())
                        if (property.Value is JObject) pinned.Add(Number(property.Value, "rpid"));
                foreach (var item in data["top_replies"] as JArray ?? new JArray()) pinned.Add(Number(item, "rpid"));
            }
            foreach (var item in items) item.IsPinned = item.IsPinned || pinned.Contains(item.Id);
            var next = NextCommentCursor(cursor, requestedCursor);
            var hasMore = !ended && next.Length > 0 && items.Count > 0;
            if (items.Count == 0 && (!totalKnown || total > 0 || !ended))
                throw new BilibiliApiException(-1, "B站暂未返回可显示的评论，请稍后刷新；这不代表该视频没有评论。");
            if (items.Count > 0 && totalKnown && total == 0)
                throw new BilibiliApiException(-1, "B站返回的评论数量不一致，请稍后刷新。");
            if (items.Count == 0 && !(data["replies"] is JArray)
                && data["replies"]?.Type != JTokenType.Null)
                throw new BilibiliApiException(-1, "B站评论列表格式已变化，请更新客户端。");
            // all_count can include child replies. Count their coverage before
            // labelling an ended first page partial; don't mistake a 3-root,
            // 100-child complete root list for an anonymous 3-comment preview.
            var covered = unique.Sum(item => 1L + Math.Max(0L, Number(item, "rcount")));
            var partial = (!ended && !hasMore) || (requestedCursor.Length == 0
                && ended && totalKnown && total > covered);
            var notice = partial
                ? "B站未返回可继续翻页的有效游标，已保留加载的评论。"
                    + (loggedIn ? "可刷新重试，或在官方客户端查看。" : "登录后可刷新重试，或在官方客户端查看。")
                : "";
            return new CommentPage
            {
                Items = items, Total = total, TotalKnown = totalKnown,
                HasMore = hasMore, NextCursor = hasMore ? next : "", IsPartial = partial, Notice = notice
            };
        }

        // This tag is application state, not a reconstructed page number or a
        // wire offset. It distinguishes an explicit server next from next_offset.
        private const string NumericCommentCursorPrefix = "bili-comment-next:";

        private static Dictionary<string, string> CommentRequestParameters(int type, long oid, string cursor)
        {
            cursor = cursor ?? "";
            var parameters = new Dictionary<string, string>
            {
                ["type"] = Invariant(type), ["oid"] = Invariant(oid), ["mode"] = "3",
                ["plat"] = "1", ["web_location"] = "1315875"
            };
            long numeric;
            if (cursor.StartsWith(NumericCommentCursorPrefix, StringComparison.Ordinal))
            {
                if (!TryNumericCommentCursor(cursor, out numeric))
                    throw new ArgumentException("评论分页游标已失效，请刷新评论。", nameof(cursor));
                parameters["next"] = Invariant(numeric);
            }
            else parameters["pagination_str"] = new JObject { ["offset"] = cursor }.ToString(Newtonsoft.Json.Formatting.None);
            if (cursor.Length == 0) parameters["seek_rpid"] = "";
            return parameters;
        }

        private static bool TryNumericCommentCursor(string cursor, out long value)
        {
            value = 0;
            return cursor != null && cursor.StartsWith(NumericCommentCursorPrefix, StringComparison.Ordinal)
                && long.TryParse(cursor.Substring(NumericCommentCursorPrefix.Length), NumberStyles.None,
                    CultureInfo.InvariantCulture, out value) && value > 0;
        }

        private static string NextCommentCursor(JObject cursor, string requested)
        {
            var offset = (cursor["pagination_reply"] as JObject)?["next_offset"];
            string next = offset?.Type == JTokenType.String ? offset.ToString() : "";
            // next_offset is an opaque server session token, not a page ID.
            // The reply service can return the same token for consecutive pages
            // while advancing its session and returning new reply IDs. Preserve
            // it verbatim; token equality is not an end condition. The page
            // guard separately checks that the server returned new reply IDs.
            if (!string.IsNullOrWhiteSpace(next) && next.Length <= 16384) return next;
            long numeric, previous;
            if (!long.TryParse(Str(cursor, "next"), NumberStyles.None, CultureInfo.InvariantCulture, out numeric)
                || numeric <= 0 || TryNumericCommentCursor(requested, out previous) && previous == numeric) return "";
            // Compare recognized opaque page formats too, so a transition to the
            // older format cannot immediately request the page we just loaded.
            if (!string.IsNullOrWhiteSpace(requested) && requested[0] == '{')
            {
                try
                {
                    var prior = JObject.Parse(requested);
                    var position = Number(prior, "type") == 1 ? prior["data"]?["pn"]
                        : Number(prior, "type") == 3 ? prior["Data"]?["cursor"] : null;
                    if (position != null && long.TryParse(position.ToString(), NumberStyles.None,
                        CultureInfo.InvariantCulture, out previous) && previous == numeric) return "";
                }
                catch (Newtonsoft.Json.JsonException) { }
            }
            return NumericCommentCursorPrefix + Invariant(numeric);
        }

        private static void AddCommentArray(List<JToken> result, JToken value)
        {
            if (value == null || value.Type == JTokenType.Null) return;
            var array = value as JArray;
            if (array == null || array.Any(item => !(item is JObject)))
                throw new BilibiliApiException(-1, "B站评论列表格式已变化，请更新客户端。");
            result.AddRange(array);
        }

        public async Task PostCommentAsync(long aid, string message, CancellationToken ct = default(CancellationToken))
        {
            CheckId(aid, nameof(aid));
            var csrf = RequireSession();
            if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("评论不能为空。", nameof(message));
            if (message.Length > 1000) throw new ArgumentException("评论最多1000个字符。", nameof(message));
            var root = await PostJsonAsync(Api + "/x/v2/reply/add", new Dictionary<string, string>
            {
                ["oid"] = Invariant(aid), ["type"] = "1", ["message"] = message.Trim(),
                ["plat"] = "1", ["csrf"] = csrf
            }, ct);
            RequireData(root);
        }

        public async Task<IReadOnlyList<DanmakuItem>> GetDanmakuAsync(long cid,
            CancellationToken ct = default(CancellationToken))
        {
            CheckId(cid, nameof(cid));
            var xml = await SendBytesAsync(new HttpRequestMessage(HttpMethod.Get,
                new Uri(Api + "/x/v1/dm/list.so?oid=" + Invariant(cid))), ct);
            try
            {
                // Never resolve external entities from network-provided XML.
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
                XDocument document;
                // text/xml may omit its HTTP charset. Reading it as an HTTP string can
                // interpret UTF-8 bytes as Latin-1 on Xbox. Let XML read the original
                // bytes using its own declaration/BOM (UTF-8 when neither is present).
                using (var source = new MemoryStream(xml, false))
                using (var reader = XmlReader.Create(source, settings)) document = XDocument.Load(reader);
                if (document.Root == null || document.Root.Name.LocalName != "i")
                    throw new XmlException();
                var items = new List<DanmakuItem>();
                foreach (var element in document.Root.Elements("d"))
                {
                    ct.ThrowIfCancellationRequested();
                    var fields = ((string)element.Attribute("p") ?? "").Split(',');
                    double time;
                    int mode, color;
                    if (fields.Length < 4 || !double.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out time)
                        || !int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out mode)
                        || !int.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out color)
                        || double.IsNaN(time) || double.IsInfinity(time) || time < 0) continue;
                    // Advanced/BAS payloads are JSON/script data, not ordinary overlay text.
                    if (mode != 1 && mode != 4 && mode != 5 && mode != 6) continue;
                    items.Add(new DanmakuItem { Time = time, Mode = mode, Color = color & 0xffffff, Text = element.Value, SenderHash = fields.Length > 6 ? fields[6].Trim() : "" });
                }
                return items.OrderBy(item => item.Time).ToList();
            }
            catch (XmlException)
            {
                throw new BilibiliApiException(-1, "弹幕接口没有返回有效的 XML，可能受到网络或风控限制。");
            }
        }

        public async Task SendDanmakuAsync(long aid, long cid, string text, TimeSpan position,
            int color = 16777215, int mode = 1, CancellationToken ct = default(CancellationToken))
        {
            CheckId(aid, nameof(aid));
            CheckId(cid, nameof(cid));
            var csrf = RequireSession();
            if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("弹幕不能为空。", nameof(text));
            if (text.Length > 100) throw new ArgumentException("弹幕最多100个字符，实际限制由账号等级决定。", nameof(text));
            if (position < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(position));
            if (color < 0 || color > 0xffffff) throw new ArgumentOutOfRangeException(nameof(color));
            if (mode != 1 && mode != 4 && mode != 5) throw new ArgumentException("仅支持滚动、底部和顶部弹幕。", nameof(mode));
            var query = new Dictionary<string, string> { ["csrf"] = csrf, ["web_location"] = "1315873" };
            var signed = await SignedQueryAsync(query, ct);
            var root = await PostJsonAsync(Api + "/x/v2/dm/post?" + signed, new Dictionary<string, string>
            {
                ["type"] = "1", ["oid"] = Invariant(cid), ["aid"] = Invariant(aid), ["msg"] = text.Trim(),
                ["progress"] = Invariant((long)position.TotalMilliseconds), ["color"] = Invariant(color),
                ["fontsize"] = "25", ["pool"] = "0", ["mode"] = Invariant(mode),
                ["rnd"] = Invariant(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000), ["csrf"] = csrf
            }, ct);
            RequireData(root);
        }

        public async Task<QrLoginInfo> GenerateQrLoginAsync(CancellationToken ct = default(CancellationToken))
        {
            await _sessionGate.WaitAsync(ct);
            try
            {
                ThrowIfDisposed();
                var pending = new PendingQrLogin
                {
                    Cookies = _cookies.CloneForLogin(), Epoch = _loginSessionEpoch,
                    Mid = CurrentUser?.Mid ?? 0, Session = _cookies.GetCookieValue("SESSDATA"), Created = DateTimeOffset.UtcNow
                };
                var root = await GetSessionJsonAsync(Passport + "/x/passport-login/web/qrcode/generate", pending.Cookies, ct);
                var data = SessionData(root);
                var result = new QrLoginInfo { Url = Str(data, "url"), Key = Str(data, "qrcode_key") };
                Uri uri;
                if (string.IsNullOrEmpty(result.Key) || !Uri.TryCreate(result.Url, UriKind.Absolute, out uri)
                    || uri.Scheme != "https" || !(uri.Host.Equals("bilibili.com", StringComparison.OrdinalIgnoreCase)
                    || uri.Host.EndsWith(".bilibili.com", StringComparison.OrdinalIgnoreCase)))
                    throw new BilibiliApiException(-1, "B站没有返回有效的登录二维码。");
                ct.ThrowIfCancellationRequested();
                foreach (var key in _pendingQrLogins.Where(p => p.Value.Created.AddMinutes(3) < DateTimeOffset.UtcNow).Select(p => p.Key).ToList())
                    _pendingQrLogins.Remove(key);
                if (_pendingQrLogins.Count >= 4) _pendingQrLogins.Clear();
                _pendingQrLogins[result.Key] = pending;
                return result;
            }
            finally { _sessionGate.Release(); }
        }

        public async Task<QrLoginStatus> PollQrLoginAsync(string key, CancellationToken ct = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("登录二维码已失效。", nameof(key));
            await _sessionGate.WaitAsync(ct);
            try
            {
                PendingQrLogin pending;
                if (!_pendingQrLogins.TryGetValue(key, out pending) || pending.Created.AddMinutes(3) < DateTimeOffset.UtcNow
                    || !SessionMatches(pending.Epoch, pending.Mid, pending.Session)) return QrLoginStatus.Expired;
                var root = await GetSessionJsonAsync(Passport + "/x/passport-login/web/qrcode/poll?qrcode_key="
                    + Uri.EscapeDataString(key), pending.Cookies, ct);
                var data = SessionData(root);
                if (data["code"] == null) throw new BilibiliApiException(-1, "登录状态响应格式已变化。");
                var code = (int)Number(data, "code");
                switch (code)
                {
                    case 86101: return QrLoginStatus.WaitingForScan;
                    case 86090: return QrLoginStatus.WaitingForConfirmation;
                    case 86038: _pendingQrLogins.Remove(key); return QrLoginStatus.Expired;
                    case 0:
                        pending.Cookies.ImportLoginRedirectCookies(Str(data, "url"));
                        await CommitLoginSessionAsync(pending.Cookies, pending.Epoch, pending.Mid, pending.Session, ct);
                        return QrLoginStatus.Success;
                    default: throw ApiError(code, Str(data, "message"));
                }
            }
            finally { _sessionGate.Release(); }
        }

        /// <summary>Removes this app's session and protected jar; does not sign out other devices.</summary>
        public async Task LogoutAsync()
        {
            ThrowIfDisposed();
            await _sessionGate.WaitAsync();
            try
            {
                await _videoInteractionGate.WaitAsync();
                try
                {
                    ++_loginSessionEpoch; _pendingQrLogins.Clear();
                    CurrentUser = null;
                    await _cookies.ClearAsync();
                    _imageKey = _subKey = null;
                }
                finally { _videoInteractionGate.Release(); }
            }
            finally { _sessionGate.Release(); }
        }

        private sealed class PendingQrLogin
        {
            internal ProtectedCookieStore Cookies;
            internal long Epoch, Mid;
            internal string Session;
            internal DateTimeOffset Created;
        }

        private bool SessionMatches(long epoch, long mid, string session) => epoch == _loginSessionEpoch
            && mid == (CurrentUser?.Mid ?? 0) && string.Equals(session, _cookies.GetCookieValue("SESSDATA"), StringComparison.Ordinal);

        private Task<JObject> GetSessionJsonAsync(string url, ProtectedCookieStore cookies, CancellationToken ct)
            => SendSessionJsonAsync(new HttpRequestMessage(HttpMethod.Get, new Uri(url)), cookies, ct);

        private async Task<JObject> SendSessionJsonAsync(HttpRequestMessage request, ProtectedCookieStore cookies, CancellationToken ct)
            => ParseJson(await SendAsync(request, ct, (response, token) => response.Content.ReadAsStringAsync().AsTask(token), cookies));

        private static JToken SessionData(JObject root)
        {
            int code = ResponseCode(root);
            if (code != 0) throw ApiError(code, Str(root, "message"));
            var data = root["data"];
            if (data == null || data.Type != JTokenType.Object)
                throw new BilibiliApiException(-1, "B站登录响应格式已变化，请稍后重试。");
            return data;
        }

        // Called only with _sessionGate held. No account cookie is committed
        // until the isolated session passes a genuine authenticated nav check.
        private async Task CommitLoginSessionAsync(ProtectedCookieStore candidate, long epoch, long mid, string session, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!SessionMatches(epoch, mid, session)) throw new BilibiliApiException(-101, "账号已改变，请重新登录。");
            long cookieMid;
            if (string.IsNullOrEmpty(candidate.GetCookieValue("SESSDATA")) || string.IsNullOrEmpty(candidate.GetCookieValue("bili_jct"))
                || !long.TryParse(candidate.GetCookieValue("DedeUserID"), out cookieMid) || cookieMid <= 0)
                throw new BilibiliApiException(-101, "B站没有返回完整的登录会话，请重新登录。");
            var root = await GetSessionJsonAsync(Api + "/x/web-interface/nav", candidate, ct);
            var data = SessionData(root);
            if (data["isLogin"]?.Value<bool>() != true || Number(data, "mid") != cookieMid)
                throw new BilibiliApiException(-101, "登录会话未通过账号验证，请重新登录。");
            var profile = new UserProfile { Mid = cookieMid, Name = Str(data, "uname"), AvatarUrl = ImageUrl(Str(data, "face")),
                IsPremium = Number(data, "vipStatus") == 1 || Number(data["vip"], "status") == 1 };
            await _videoInteractionGate.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested(); ThrowIfDisposed();
                if (!SessionMatches(epoch, mid, session)) throw new BilibiliApiException(-101, "账号已改变，请重新登录。");
                // Saving atomically finishes an already confirmed login; after
                // this boundary cancellation cannot leave disk and memory split.
                try { await candidate.SaveAsync(); }
                catch (Exception ex) when (!(ex is OutOfMemoryException))
                { throw new BilibiliApiException(-1, "无法加密保存登录会话，请检查存储空间后重试。"); }
                _cookies.ReplaceWith(candidate); CurrentUser = profile;
                ++_loginSessionEpoch; _pendingQrLogins.Clear();
                CaptureWbiKeys(data);
            }
            finally { _videoInteractionGate.Release(); }
        }

        private async Task RefreshProfileAsync(CancellationToken ct)
        {
            var root = await GetJsonAsync(Api + "/x/web-interface/nav", ct);
            CaptureWbiKeys(root["data"]);
            var code = ResponseCode(root);
            if (code == -101 || (code == 0 && root["data"]?["isLogin"]?.Value<bool>() != true))
            {
                CurrentUser = null;
                return;
            }
            var data = RequireData(root);
            CurrentUser = new UserProfile
            {
                Mid = Number(data, "mid"), Name = Str(data, "uname"), AvatarUrl = ImageUrl(Str(data, "face")),
                IsPremium = Number(data, "vipStatus") == 1 || Number(data["vip"], "status") == 1
            };
            if (CurrentUser.Mid <= 0) { CurrentUser = null; throw new BilibiliApiException(-1, "账号信息不完整，请重新登录。"); }
        }

        private async Task<JObject> GetSignedJsonAsync(string path, IDictionary<string, string> parameters,
            CancellationToken ct, string referer = null)
        {
            return await GetJsonAsync(Api + path + "?" + await SignedQueryAsync(parameters, ct), ct, referer);
        }

        private async Task<string> SignedQueryAsync(IDictionary<string, string> parameters, CancellationToken ct)
        {
            await _wbiGate.WaitAsync(ct);
            try
            {
                if (string.IsNullOrEmpty(_imageKey) || _keysExpire <= DateTimeOffset.UtcNow)
                {
                    var navEpoch = _loginSessionEpoch;
                    var navSession = _cookies.GetCookieValue("SESSDATA");
                    var nav = await GetJsonAsync(Api + "/x/web-interface/nav", ct);
                    // Anonymous nav deliberately returns -101 but still includes the public WBI keys.
                    var code = ResponseCode(nav);
                    if (code != 0 && code != -101) throw ApiError(code, Str(nav, "message"));
                    if (code == -101 && navEpoch == _loginSessionEpoch
                        && string.Equals(navSession, _cookies.GetCookieValue("SESSDATA"), StringComparison.Ordinal)) CurrentUser = null;
                    CaptureWbiKeys(nav["data"]);
                }
                if (string.IsNullOrEmpty(_imageKey) || string.IsNullOrEmpty(_subKey))
                    throw new BilibiliApiException(-1, "无法取得B站请求签名，请稍后重试。");
                return WbiSigner.Sign(parameters, _imageKey, _subKey, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            }
            finally { _wbiGate.Release(); }
        }

        private void CaptureWbiKeys(JToken data)
        {
            var image = KeyFromUrl(Str(data?["wbi_img"], "img_url"));
            var sub = KeyFromUrl(Str(data?["wbi_img"], "sub_url"));
            if (image.Length == 32 && sub.Length == 32)
            {
                _imageKey = image; _subKey = sub; _keysExpire = DateTimeOffset.UtcNow.AddHours(1);
            }
        }

        private async Task BootstrapWebsiteAsync(CancellationToken ct)
        {
            // Discard the page. Only the server's Set-Cookie headers are needed for anonymous search.
            await SendTextAsync(new HttpRequestMessage(HttpMethod.Get, new Uri(Referer)), ct);
        }

        private async Task<JObject> GetJsonAsync(string url, CancellationToken ct, string referer = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url));
            if (referer != null) request.Headers.Referer = new Uri(referer);
            return ParseJson(await SendTextAsync(request, ct));
        }

        private async Task<JObject> PostJsonAsync(string url, IDictionary<string, string> form, CancellationToken ct)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, new Uri(url))
            {
                Content = new HttpFormUrlEncodedContent(form)
            };
            request.Headers.TryAppendWithoutValidation("Origin", "https://www.bilibili.com");
            return ParseJson(await SendTextAsync(request, ct));
        }

        private Task<string> SendTextAsync(HttpRequestMessage request, CancellationToken ct)
        {
            return SendAsync(request, ct, (response, token) => response.Content.ReadAsStringAsync().AsTask(token));
        }

        private Task<byte[]> SendBytesAsync(HttpRequestMessage request, CancellationToken ct)
        {
            return SendAsync(request, ct, async (response, token) =>
                (await response.Content.ReadAsBufferAsync().AsTask(token)).ToArray());
        }

        private async Task<T> SendAsync<T>(HttpRequestMessage request, CancellationToken ct,
            Func<HttpResponseMessage, CancellationToken, Task<T>> readContent, ProtectedCookieStore cookieStore = null)
        {
            ThrowIfDisposed();
            using (request)
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(25));
                try
                {
                    var jar = cookieStore ?? _cookies;
                    var cookieHeader = jar.GetCookieHeader(request.RequestUri);
                    if (cookieHeader.Length > 0) request.Headers.TryAppendWithoutValidation("Cookie", cookieHeader);
                    using (var response = await _http.SendRequestAsync(request, HttpCompletionOption.ResponseContentRead).AsTask(timeout.Token))
                    {
                        jar.ApplyResponseCookies(response, request.RequestUri);
                        if (!response.IsSuccessStatusCode)
                        {
                            var status = (int)response.StatusCode;
                            if (status == 412 || status == 403 || status == 429)
                                throw new BilibiliApiException(status, "B站限制了本次请求（HTTP " + status + "）。请稍后重试，或在官方客户端完成验证。");
                            throw new BilibiliApiException(status, "B站网络请求失败（HTTP " + status + "）。");
                        }
                        return await readContent(response, timeout.Token);
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new BilibiliApiException(-1, "连接B站超时，请检查网络后重试。");
                }
                catch (Exception ex) when (!(ex is BilibiliApiException) && !(ex is OperationCanceledException)
                    && !(ex is OutOfMemoryException) && !(ex is ObjectDisposedException))
                {
                    throw new BilibiliApiException(-1, "无法连接B站，请检查网络或代理设置后重试。");
                }
            }
        }

        private static JObject ParseJson(string text)
        {
            try { return JObject.Parse(text); }
            catch (JsonException) { throw new BilibiliApiException(-1, "B站没有返回有效的 API 数据，可能受到风控限制。"); }
        }

        private JToken RequireData(JObject root)
        {
            var code = ResponseCode(root);
            if (code != 0)
            {
                if (code == -101) CurrentUser = null;
                throw ApiError(code, Str(root, "message"));
            }
            var data = root["data"];
            if (data == null || data.Type != JTokenType.Object)
                throw new BilibiliApiException(-1, "B站 API 没有返回预期的数据，请稍后重试。");
            return data;
        }

        private string RequireSession()
        {
            if (!IsLoggedIn) throw new BilibiliApiException(-101, "请先扫码登录。");
            var csrf = _cookies.GetCookieValue("bili_jct");
            if (string.IsNullOrWhiteSpace(csrf)) throw new BilibiliApiException(-111, "登录会话缺少验证信息，请退出后重新登录。");
            return csrf;
        }

        private static int ResponseCode(JObject root)
        {
            int code;
            if (root["code"] == null || !int.TryParse(root["code"].ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out code))
                throw new BilibiliApiException(-1, "B站 API 响应格式已变化，请更新客户端。");
            return code;
        }

        private static BilibiliApiException ApiError(int code, string serverMessage)
        {
            string message;
            switch (code)
            {
                case -101: message = "登录已失效，请重新扫码登录。"; break;
                case -111: message = "登录验证失败，请退出后重新扫码。"; break;
                case -352:
                case -412:
                case -509: message = "B站触发了访问限制，请稍后重试，或在官方客户端完成验证。"; break;
                case -404: message = "视频或内容不存在，可能已被删除。"; break;
                default: message = string.IsNullOrWhiteSpace(serverMessage) || serverMessage == "0"
                        ? "B站拒绝了该操作。" : serverMessage; break;
            }
            return new BilibiliApiException(code, message + "（代码 " + Invariant(code) + "）");
        }

        private static VideoItem MapVideo(JToken item)
        {
            return new VideoItem
            {
                Aid = Number(item, "aid"), Bvid = Str(item, "bvid"), Title = PlainText(Str(item, "title")),
                CoverUrl = ImageUrl(Str(item, "pic")), Owner = Str(item?["owner"], "name"),
                OwnerMid = Number(item?["owner"], "mid"),
                OwnerAvatarUrl = ImageUrl(Str(item?["owner"], "face")),
                PlayText = FormatCount(Number(item?["stat"], "view")),
                DurationText = FormatDuration(Number(item, "duration"))
            };
        }

        private static string KeyFromUrl(string value)
        {
            Uri uri;
            return Uri.TryCreate(value, UriKind.Absolute, out uri) ? Path.GetFileNameWithoutExtension(uri.AbsolutePath) : "";
        }

        private static string Str(JToken item, string name) => item == null || item.Type == JTokenType.Null ? "" : item[name]?.ToString() ?? "";
        private static long Number(JToken item, string name)
        {
            long value;
            return long.TryParse(Str(item, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : 0;
        }
        private static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);
        private static string PlainText(string value) => WebUtility.HtmlDecode(Regex.Replace(value ?? "", "<[^>]+>", ""));
        private static string ImageUrl(string value)
        {
            if (value.StartsWith("//", StringComparison.Ordinal)) value = "https:" + value;
            if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) value = "https://" + value.Substring(7);
            Uri uri;
            return Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Scheme == "https" ? uri.AbsoluteUri : "";
        }
        private static string MediaUrl(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            if (value.StartsWith("//", StringComparison.Ordinal)) value = "https:" + value;
            if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) value = "https://" + value.Substring(7);
            Uri uri;
            return Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Scheme == "https" ? uri.AbsoluteUri : "";
        }
        private static string FormatCount(long count) => count >= 100000000
            ? (count / 100000000d).ToString("0.#", CultureInfo.InvariantCulture) + "亿次播放"
            : count >= 10000 ? (count / 10000d).ToString("0.#", CultureInfo.InvariantCulture) + "万次播放"
            : Invariant(count) + "次播放";
        private static string FormatDuration(long seconds)
        {
            var duration = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return seconds >= 3600 ? ((int)duration.TotalHours).ToString(CultureInfo.InvariantCulture)
                + ":" + duration.Minutes.ToString("00") + ":" + duration.Seconds.ToString("00")
                : ((int)duration.TotalMinutes).ToString(CultureInfo.InvariantCulture) + ":" + duration.Seconds.ToString("00");
        }
        private static string FormatDate(long timestamp)
        {
            try { return DateTimeOffset.FromUnixTimeSeconds(timestamp).ToLocalTime().ToString("yyyy-MM-dd HH:mm"); }
            catch (ArgumentOutOfRangeException) { return ""; }
        }
        private static void CheckPage(int page) { if (page < 1) throw new ArgumentOutOfRangeException(nameof(page)); }
        private static void CheckId(long id, string name) { if (id <= 0) throw new ArgumentOutOfRangeException(name); }
        private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(BilibiliClient)); }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _http.Dispose();
            // Gates remain usable for in-flight operations to release during cancellation/disposal.
        }
    }
}
