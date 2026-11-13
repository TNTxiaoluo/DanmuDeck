using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Models;
using Newtonsoft.Json.Linq;
using Windows.Web.Http;

namespace BiliXbox.Services
{
    public sealed partial class BilibiliClient
    {
        private readonly Dictionary<string, UserSearchSession> _userSearchSessions
            = new Dictionary<string, UserSearchSession>(StringComparer.Ordinal);

        private sealed class UserSearchSession
        {
            public bool Website { get; set; }
            public int PageSize { get; set; }
        }

        public async Task<UserSearchPage> SearchUsersAsync(string keyword, int page = 1,
            CancellationToken ct = default(CancellationToken))
        {
            CheckPage(page);
            keyword = NormalizeSearchKeyword(keyword);
            ct.ThrowIfCancellationRequested();
            UserSearchSession session;
            if (page > 1 && _userSearchSessions.TryGetValue(keyword, out session))
            {
                var later = session.Website ? await GetWebsiteUserSearchDataAsync(keyword, page, ct)
                    : await GetUserSearchDataAsync(keyword, page, session.PageSize, ct);
                return MapUserSearchPage(later, page, session.PageSize);
            }
            if (page != 1)
                throw new BilibiliApiException(-1, "请先从第1页重新搜索用户，再继续翻页。");

            JToken data;
            bool website = true;
            try { data = await GetWebsiteUserSearchDataAsync(keyword, page, ct); }
            catch (BilibiliApiException)
            {
                website = false;
                data = await GetUserSearchDataAsync(keyword, page, 20, ct);
            }
            var result = MapUserSearchPage(data, page, 0);
            ct.ThrowIfCancellationRequested();
            _userSearchSessions[keyword] = new UserSearchSession
                { Website = website, PageSize = (int)Number(data, "pagesize") };
            return result;
        }

        private async Task<JToken> GetWebsiteUserSearchDataAsync(string keyword, int page, CancellationToken ct)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, new Uri("https://search.bilibili.com/upuser?keyword="
                + Uri.EscapeDataString(keyword) + "&page=" + Invariant(page)));
            request.Headers.TryAppendWithoutValidation("Accept", "text/html,application/xhtml+xml");
            return WebsiteSearchParser.ParseUsers(await SendTextAsync(request, ct), keyword, page);
        }

        private async Task<JToken> GetUserSearchDataAsync(string keyword, int page, int size, CancellationToken ct)
        {
            await PrepareSearchApiAsync(ct);
            return RequireData(await GetSignedJsonAsync("/x/web-interface/wbi/search/type",
                new Dictionary<string, string>
                {
                    ["search_type"] = "bili_user", ["keyword"] = keyword,
                    ["page"] = Invariant(page), ["page_size"] = Invariant(size), ["order"] = "0"
                }, ct));
        }

        private static UserSearchPage MapUserSearchPage(JToken token, int page, int expectedSize)
        {
            var data = token as JObject;
            long actualPage, size, total, pages;
            if (data == null || !UserSearchCount(data["page"], out actualPage) || actualPage != page
                || !UserSearchCount(data["pagesize"], out size) || size <= 0 || size > 100
                || expectedSize > 0 && size != expectedSize
                || !UserSearchCount(data["numResults"], out total)
                || !UserSearchCount(data["numPages"], out pages))
                throw new BilibiliApiException(-1, "用户搜索的页码或分页信息已变化，请从第1页重新搜索。");
            var raw = data["result"] as JArray;
            if (raw == null && (total != 0 || data["result"] == null || data["result"].Type != JTokenType.Null)
                || raw != null && (raw.Count > size || raw.Any(item => !(item is JObject)
                    || Str(item, "type") != "bili_user" || Number(item, "mid") <= 0
                    || string.IsNullOrWhiteSpace(PlainText(Str(item, "uname"))))))
                throw new BilibiliApiException(-1, "B站未返回有效的用户搜索列表，请刷新重试。");
            var users = (raw ?? new JArray()).Select(item => new UserSearchItem
            {
                Mid = Number(item, "mid"), Name = PlainText(Str(item, "uname")).Trim(),
                AvatarUrl = ImageUrl(Str(item, "upic")), Signature = PlainText(Str(item, "usign")),
                FollowersText = UserSearchCountText(item["fans"], "粉丝"),
                VideoCountText = UserSearchCountText(item["videos"], "个视频")
            }).GroupBy(item => item.Mid).Select(group => group.First()).ToList();
            if (users.Count > total || users.Count == 0 && total > 0 && page <= pages)
                throw new BilibiliApiException(-1, "B站未返回本页的用户结果，请刷新重试。");
            return new UserSearchPage
            {
                Users = users, Total = total,
                HasMore = users.Count > 0 && (pages > 0 ? page < pages : (long)page * size < total)
            };
        }

        private static bool UserSearchCount(JToken token, out long value)
        {
            value = 0;
            return token is JValue && token.Type != JTokenType.Null && long.TryParse(token.ToString(),
                NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 0;
        }

        private static string UserSearchCountText(JToken token, string suffix)
        {
            long count;
            if (!UserSearchCount(token, out count)) return "";
            var number = count >= 100000000 ? (count / 100000000d).ToString("0.#", CultureInfo.InvariantCulture) + "亿"
                : count >= 10000 ? (count / 10000d).ToString("0.#", CultureInfo.InvariantCulture) + "万"
                : count.ToString(CultureInfo.InvariantCulture);
            return number + suffix;
        }
    }
}
