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
        public async Task<CreatorProfile> GetCreatorProfileAsync(long mid,
            CancellationToken ct = default(CancellationToken))
        {
            CheckId(mid, nameof(mid));
            await PrepareSearchApiAsync(ct);
            JToken data;
            JToken stats = null;
            try
            {
                data = RequireData(await GetSignedJsonAsync("/x/space/wbi/acc/info",
                    new Dictionary<string, string>
                    {
                        ["mid"] = Invariant(mid), ["platform"] = "web", ["web_location"] = "1550101"
                    }, ct, CreatorReferer(mid)));
            }
            catch (BilibiliApiException ex) when (ex.Code == -352 || ex.Code == -412
                || ex.Code == 403 || ex.Code == 412)
            {
                // The public user card is a separate, normal read endpoint.
                // It exposes basic public metadata without a verification retry
                // or substituting a similarly-named account from search.
                stats = RequireData(await GetJsonAsync(Api + "/x/web-interface/card?mid="
                    + Invariant(mid) + "&photo=false", ct, CreatorReferer(mid)));
                data = stats["card"] as JObject;
            }
            if (!(data is JObject) || Number(data, "mid") != mid || string.IsNullOrWhiteSpace(Str(data, "name")))
                throw new BilibiliApiException(-1, "B站返回的UP主资料与当前主页不一致，请刷新重试。");
            return new CreatorProfile
            {
                Mid = mid, Name = PlainText(Str(data, "name")), AvatarUrl = ImageUrl(Str(data, "face")),
                Signature = PlainText(Str(data, "sign")),
                FollowersText = CreatorCountText(stats?["follower"] ?? data["fans"], "粉丝"),
                FollowingText = CreatorCountText(data["attention"], "关注"),
                VideoCountText = CreatorCountText(stats?["archive_count"], "个视频")
            };
        }

        private static string CreatorCountText(JToken token, string suffix)
        {
            long count;
            if (!(token is JValue) || !long.TryParse(token.ToString(), NumberStyles.None,
                CultureInfo.InvariantCulture, out count) || count < 0) return "";
            var text = count >= 100000000 ? (count / 100000000d).ToString("0.#", CultureInfo.InvariantCulture) + "亿"
                : count >= 10000 ? (count / 10000d).ToString("0.#", CultureInfo.InvariantCulture) + "万" : Invariant(count);
            return text + " " + suffix;
        }

        public async Task<SearchPage> GetCreatorVideosAsync(long mid, int page = 1,
            CancellationToken ct = default(CancellationToken))
        {
            CheckId(mid, nameof(mid));
            CheckPage(page);
            // Use this exact account ID; a renamed uploader must not open the
            // archive of a different account found by name search.
            var result = await SearchCreatorVideosAsync(new JObject { ["mid"] = mid }, page, ct);
            foreach (var video in result.Videos)
            {
                // A scoped space list includes collaborative submissions, so
                // the video's actual primary uploader must remain unchanged.
                if (video.OwnerMid <= 0) video.OwnerMid = mid;
            }
            if (string.IsNullOrEmpty(result.MatchedCreatorName))
                result.MatchedCreatorName = result.Videos.FirstOrDefault()?.Owner ?? "";
            return result;
        }

        public async Task<CreatorDynamicPage> GetCreatorDynamicsAsync(long mid, string offset = "",
            CancellationToken ct = default(CancellationToken))
        {
            CheckId(mid, nameof(mid));
            offset = offset ?? "";
            if (offset.Length > 16384) throw new ArgumentException("动态分页信息过长，请刷新主页。", nameof(offset));
            ct.ThrowIfCancellationRequested();
            await PrepareSearchApiAsync(ct);
            var data = RequireData(await GetJsonAsync(Api + "/x/polymer/web-dynamic/v1/feed/space?host_mid="
                + Invariant(mid) + "&offset=" + Uri.EscapeDataString(offset)
                + "&platform=web&timezone_offset=" + Invariant(-(long)DateTimeOffset.Now.Offset.TotalMinutes)
                + "&features=itemOpusStyle", ct, CreatorReferer(mid)));
            var raw = data["items"] as JArray;
            var moreValue = Str(data, "has_more");
            if (raw == null || raw.Any(item => !(item is JObject))
                || !(moreValue == "0" || moreValue == "1" || string.Equals(moreValue, "true", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(moreValue, "false", StringComparison.OrdinalIgnoreCase)))
                throw new BilibiliApiException(-1, "B站动态列表格式已变化，请稍后刷新。");
            var items = raw.Select(item => MapCreatorDynamic(item, true)).GroupBy(item => item.Id,
                StringComparer.Ordinal).Select(group => group.First()).ToList();
            // A pinned repost may contain somebody else's original, but its
            // outer author must still be the account whose space was opened.
            if (items.Any(item => item.AuthorMid > 0 && item.AuthorMid != mid))
                throw new BilibiliApiException(-1, "B站返回了不属于该UP主的动态，请刷新重试。");
            var more = Flag(data, "has_more");
            var next = Str(data, "offset");
            if (more && (items.Count == 0 || next.Length == 0 || next.Length > 16384 || next == offset))
                throw new BilibiliApiException(-1, "B站未返回有效的下一页动态位置，请刷新重试。");
            return new CreatorDynamicPage { Items = items, HasMore = more, NextOffset = more ? next : "" };
        }

        private static CreatorDynamicItem MapCreatorDynamic(JToken item, bool includeOriginal)
        {
            var id = Str(item, "id_str");
            if (!Regex.IsMatch(id, "^[0-9]{1,40}$") || id.TrimStart('0').Length == 0)
                throw new BilibiliApiException(-1, "B站返回了无效的动态标识，请刷新重试。");
            var modules = item["modules"] as JObject;
            var author = modules?["module_author"] as JObject;
            var dynamic = modules?["module_dynamic"] as JObject;
            var desc = dynamic?["desc"] as JObject;
            var major = dynamic?["major"] as JObject;
            var opus = major?["opus"] as JObject;
            var summary = opus?["summary"] as JObject;
            var rich = (desc?["rich_text_nodes"] as JArray) ?? (summary?["rich_text_nodes"] as JArray);
            var content = Str(desc, "text");
            if (content.Length == 0) content = Str(summary, "text");
            var parts = MapDynamicContent(rich, content);
            if (content.Length == 0) content = string.Concat(parts.Select(part => part.IsImage ? part.AltText : part.Text));
            if (content.Length == 0) content = Str(opus, "title");
            if (content.Length == 0) content = Str(major?["none"] as JObject, "tips");
            if (parts.Count == 0 && content.Length > 0)
                parts = new List<CommentContentPart> { new CommentContentPart { Text = content } };
            var images = new List<string>();
            foreach (var picture in (major?["draw"] as JObject)?["items"] as JArray ?? new JArray())
            {
                var url = ImageUrl(Str(picture as JObject, "src"));
                if (url.Length > 0) images.Add(url);
            }
            foreach (var picture in opus?["pics"] as JArray ?? new JArray())
            {
                var url = ImageUrl(Str(picture as JObject, "url"));
                if (url.Length > 0) images.Add(url);
            }
            // JSON null is a non-null JValue instance. C# ?. only guards CLR
            // null, so every optional object must be projected to JObject
            // before accessing children (notably absent repost statistics).
            var stat = modules?["module_stat"] as JObject;
            var commentStat = stat?["comment"] as JObject;
            var likeStat = stat?["like"] as JObject;
            var forwardStat = stat?["forward"] as JObject;
            var basic = item["basic"] as JObject;
            long commentOid;
            int commentType;
            var target = new DynamicCommentTarget();
            if (long.TryParse(Str(basic, "comment_id_str"), NumberStyles.None, CultureInfo.InvariantCulture, out commentOid)
                && commentOid > 0 && int.TryParse(Str(basic, "comment_type"), NumberStyles.None,
                    CultureInfo.InvariantCulture, out commentType) && commentType > 0)
            {
                target.Oid = commentOid;
                target.Type = commentType;
                target.CanPost = !Flag(commentStat, "forbidden")
                    && !Flag(commentStat, "disabled") && !Flag(commentStat, "hidden");
            }
            var result = new CreatorDynamicItem
            {
                Id = id, Type = Str(item, "type"), AuthorMid = Number(author, "mid"),
                Author = Str(author, "name"), AvatarUrl = ImageUrl(Str(author, "face")),
                TimeText = Number(author, "pub_ts") > 0 ? FormatDate(Number(author, "pub_ts")) : Str(author, "pub_time"),
                Content = content, ContentParts = parts, ImageUrls = images.Distinct(StringComparer.Ordinal).ToList(),
                CommentTarget = target, CommentCount = Math.Max(0, Number(commentStat, "count")),
                LikeCount = Math.Max(0, Number(likeStat, "count")),
                RepostCount = Math.Max(0, Number(forwardStat, "count"))
            };
            var archive = major?["archive"];
            if (archive is JObject && Regex.IsMatch(Str(archive, "bvid"), "^BV[0-9A-Za-z]{10}$"))
                result.Video = new VideoItem
                {
                    Aid = Number(archive, "aid"), Bvid = Str(archive, "bvid"), Title = Str(archive, "title"),
                    CoverUrl = ImageUrl(Str(archive, "cover")), DurationText = Str(archive, "duration_text"),
                    Owner = result.Author, OwnerMid = result.AuthorMid, OwnerAvatarUrl = result.AvatarUrl,
                    PlayText = Str(archive["stat"] as JObject, "play")
                };
            if (includeOriginal && item["orig"] is JObject && Str(item["orig"], "id_str").Length > 0)
                result.Original = MapCreatorDynamic(item["orig"], false);
            return result;
        }

        private static IReadOnlyList<CommentContentPart> MapDynamicContent(JArray nodes, string fallback)
        {
            if (nodes == null || nodes.Count == 0)
                return fallback.Length == 0 ? Array.Empty<CommentContentPart>()
                    : new[] { new CommentContentPart { Text = fallback } };
            var result = new List<CommentContentPart>();
            foreach (var node in nodes)
            {
                var richNode = node as JObject;
                if (richNode == null) continue;
                var text = Str(richNode, "text");
                if (text.Length == 0) text = Str(richNode, "orig_text");
                var emoji = Str(richNode, "type") == "RICH_TEXT_NODE_TYPE_EMOJI"
                    ? ImageUrl(Str(richNode["emoji"] as JObject, "icon_url")) : "";
                if (emoji.Length > 0) result.Add(new CommentContentPart { ImageUrl = emoji, AltText = text });
                else if (text.Length > 0) result.Add(new CommentContentPart { Text = text });
            }
            return result.Count == 0 && fallback.Length > 0
                ? new[] { new CommentContentPart { Text = fallback } } : (IReadOnlyList<CommentContentPart>)result;
        }

        public async Task<CommentPage> GetDynamicCommentsAsync(DynamicCommentTarget target, string cursor = "",
            CancellationToken ct = default(CancellationToken))
        {
            ValidateDynamicTarget(target);
            var account = CaptureCommentReadAccount();
            // Snapshot before awaiting. Changing the selected dynamic while a
            // request runs cannot redirect that request to the new activity.
            var oid = target.Oid;
            var type = target.Type;
            cursor = cursor ?? "";
            if (cursor.Length > 16384) throw new ArgumentException("评论分页信息过长，请刷新评论。", nameof(cursor));
            if (string.IsNullOrEmpty(_cookies.GetCookieValue("buvid3"))) await BootstrapWebsiteAsync(ct);
            var parameters = CommentRequestParameters(type, oid, cursor);
            var root = await GetSignedJsonAsync("/x/v2/reply/wbi/main", parameters, ct);
            if (ResponseCode(root) == 12002)
                return new CommentPage { IsClosed = true, Notice = "该动态的评论区已关闭。" };
            var result = MapCommentPage(RequireData(root), cursor, IsLoggedIn);
            AttachCommentContext(result.Items, oid, type, account, Number(root["data"]?["upper"], "mid"));
            if (result.IsClosed) result.Notice = "该动态的评论区已关闭。";
            return result;
        }

        public async Task<CommentReplyPage> GetDynamicRepliesAsync(DynamicCommentTarget target,
            long rootRpid, int page = 1, CancellationToken ct = default(CancellationToken))
        {
            ValidateDynamicTarget(target);
            var account = CaptureCommentReadAccount();
            var oid = target.Oid;
            var type = target.Type;
            CheckId(rootRpid, nameof(rootRpid));
            CheckPage(page);
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(_cookies.GetCookieValue("buvid3"))) await BootstrapWebsiteAsync(ct);
            var data = RequireData(await GetJsonAsync(Api + "/x/v2/reply/reply?type=" + Invariant(type)
                + "&oid=" + Invariant(oid) + "&root=" + Invariant(rootRpid) + "&ps=20&pn=" + Invariant(page), ct));
            if (data["root"] != null && data["root"].Type != JTokenType.Null && Number(data["root"], "rpid") != rootRpid)
                throw new BilibiliApiException(-1, "B站返回的楼中楼与当前动态评论不一致，请刷新重试。");
            var pagination = data["page"] as JObject;
            long count, size, number;
            if (pagination == null || !long.TryParse(Str(pagination, "count"), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out count) || count < 0
                || !long.TryParse(Str(pagination, "size"), out size) || size <= 0
                || !long.TryParse(Str(pagination, "num"), out number) || number != page)
                throw new BilibiliApiException(-1, "B站楼中楼响应缺少有效分页信息，请稍后重试。");
            var raw = new List<JToken>();
            AddCommentArray(raw, data["replies"]);
            if (raw.Any(item => Number(item, "rpid") <= 0 || !(item["member"] is JObject)
                || !(item["content"] is JObject) || Number(item, "root") > 0 && Number(item, "root") != rootRpid))
                throw new BilibiliApiException(-1, "B站楼中楼数据格式已变化，请更新客户端。");
            var items = raw.GroupBy(item => Number(item, "rpid")).Select(group => MapCommentItem(group.First(), false)).ToList();
            AttachCommentContext(items, oid, type, account, Number(data["upper"], "mid"));
            var remaining = Math.Max(0L, count - (page - 1L) * size);
            if (items.Count == 0 && remaining > 0)
                throw new BilibiliApiException(-1, "B站暂未返回可显示的回复，请稍后重试。");
            if (items.Count > count || (!(data["replies"] is JArray) && data["replies"]?.Type != JTokenType.Null))
                throw new BilibiliApiException(-1, "B站楼中楼数量与列表不一致，请稍后重试。");
            var partial = items.Count < Math.Min(size, remaining);
            return new CommentReplyPage
            {
                Items = items, Page = page, Total = count, TotalKnown = true, HasMore = !partial && page * size < count,
                IsPartial = partial, Notice = partial ? "B站本次仅提供部分回复，可稍后刷新，或在官方客户端查看。" : ""
            };
        }

        public Task PostDynamicCommentAsync(DynamicCommentTarget target, string message,
            CancellationToken ct = default(CancellationToken))
            => PostDynamicCommentAsync(target, message, 0, 0, ct);

        public async Task PostDynamicCommentAsync(DynamicCommentTarget target, string message,
            long rootRpid, long parentRpid, CancellationToken ct = default(CancellationToken))
        {
            ValidateDynamicTarget(target);
            if (!target.CanPost) throw new BilibiliApiException(-1, "该动态当前不允许发表评论。");
            var oid = target.Oid;
            var type = target.Type;
            var csrf = RequireSession();
            if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("评论不能为空。", nameof(message));
            if (message.Length > 1000) throw new ArgumentException("评论最多1000个字符。", nameof(message));
            if (rootRpid < 0 || parentRpid < 0 || (rootRpid == 0) != (parentRpid == 0))
                throw new ArgumentException("回复目标不完整，请重新选择评论。");
            var form = new Dictionary<string, string>
            {
                ["oid"] = Invariant(oid), ["type"] = Invariant(type), ["message"] = message.Trim(),
                ["plat"] = "1", ["csrf"] = csrf
            };
            if (rootRpid > 0) { form["root"] = Invariant(rootRpid); form["parent"] = Invariant(parentRpid); }
            RequireData(await PostJsonAsync(Api + "/x/v2/reply/add", form, ct));
        }

        private static void ValidateDynamicTarget(DynamicCommentTarget target)
        {
            if (target == null || !target.IsAvailable)
                throw new ArgumentException("B站未提供这条动态的评论入口，无法读取或发表评论。", nameof(target));
        }
    }
}
