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
        public async Task PostCommentReplyAsync(long aid, CommentItem target, string message,
            CancellationToken ct = default(CancellationToken))
        {
            CheckId(aid, nameof(aid));
            if (target == null || target.Id <= 0 || target.Oid != aid || target.Type != 1 || target.RootId < 0)
                throw new ArgumentException("回复对象与当前视频不一致，请重新选择评论。", nameof(target));
            if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("回复不能为空。", nameof(message));
            if (message.Length > 1000) throw new ArgumentException("回复最多1000个字符。", nameof(message));
            long parent = target.Id, root = target.RootId > 0 ? target.RootId : parent;
            long epoch = _loginSessionEpoch, account = CurrentUser?.Mid ?? 0;
            string session = _cookies.GetCookieValue("SESSDATA"), csrf = RequireSession();
            await _sessionGate.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                if (!SessionMatches(epoch, account, session) || RequireSession() != csrf)
                    throw new BilibiliApiException(-101, "账号已改变，请重新选择回复对象。");
                RequireData(await PostJsonAsync(Api + "/x/v2/reply/add", new Dictionary<string, string>
                {
                    ["oid"] = Invariant(aid), ["type"] = "1", ["message"] = message.Trim(),
                    ["root"] = Invariant(root), ["parent"] = Invariant(parent), ["plat"] = "1", ["csrf"] = csrf
                }, ct));
            }
            finally { _sessionGate.Release(); }
        }

        public async Task<CommentReplyPage> GetCommentRepliesAsync(long aid, long rootRpid, int page = 1,
            CancellationToken ct = default(CancellationToken))
        {
            CheckId(aid, nameof(aid));
            CheckId(rootRpid, nameof(rootRpid));
            CheckPage(page);
            ct.ThrowIfCancellationRequested();
            var account = CaptureCommentReadAccount();
            if (string.IsNullOrEmpty(_cookies.GetCookieValue("buvid3"))) await BootstrapWebsiteAsync(ct);
            const int pageSize = 20;
            var root = await GetJsonAsync(Api + "/x/v2/reply/reply?type=1&oid=" + Invariant(aid)
                + "&root=" + Invariant(rootRpid) + "&ps=" + Invariant(pageSize) + "&pn=" + Invariant(page), ct);
            var data = RequireData(root);
            var rootComment = data["root"];
            if (rootComment != null && rootComment.Type != JTokenType.Null
                && Number(rootComment, "rpid") != rootRpid)
                throw new BilibiliApiException(-1, "B站返回的楼中楼与当前评论不一致，请刷新重试。");
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
                || !(item["content"] is JObject) || (Number(item, "root") > 0 && Number(item, "root") != rootRpid)))
                throw new BilibiliApiException(-1, "B站楼中楼数据格式已变化，请更新客户端。");
            var items = raw.GroupBy(item => Number(item, "rpid")).Select(group => MapCommentItem(group.First(), false)).ToList();
            AttachCommentContext(items, aid, 1, account, Number(data["upper"], "mid"));
            var remaining = Math.Max(0L, count - (page - 1L) * size);
            if (items.Count == 0 && remaining > 0)
                throw new BilibiliApiException(-1, "B站暂未返回可显示的回复，请稍后重试；这不代表该评论没有回复。");
            if (items.Count > count || (!(data["replies"] is JArray) && data["replies"]?.Type != JTokenType.Null))
                throw new BilibiliApiException(-1, "B站楼中楼数量与列表不一致，请稍后重试。");
            // A short response before the authoritative total is a restricted
            // preview, not a complete list or evidence of a working next page.
            var partial = items.Count < Math.Min(size, remaining);
            return new CommentReplyPage
            {
                Items = items, Page = page, Total = count, TotalKnown = true,
                HasMore = !partial && page * size < count,
                IsPartial = partial,
                Notice = partial ? "B站本次仅提供部分回复，可稍后刷新，或在官方客户端查看。" : ""
            };
        }

        private static CommentItem MapCommentItem(JToken item, bool includePreview)
        {
            var content = item["content"] as JObject;
            var message = Str(content, "message");
            var member = item["member"] as JObject;
            var badge = (member?["user_sailing"] as JObject)?["cardbg"] as JObject;
            var fan = badge?["fan"] as JObject;
            int level, action;
            var levelKnown = int.TryParse(Str(member?["level_info"], "current_level"), NumberStyles.None,
                CultureInfo.InvariantCulture, out level) && level >= 0 && level <= 6;
            var actionKnown = int.TryParse(Str(item, "action"), NumberStyles.None,
                CultureInfo.InvariantCulture, out action) && action >= 0 && action <= 2;
            var previews = new List<JToken>();
            if (includePreview) AddCommentArray(previews, item["replies"]);
            // Replies are displayed only one level deep. A response cannot
            // create unbounded recursion through nested preview objects.
            if (previews.Any(reply => Number(reply, "rpid") <= 0 || !(reply["member"] is JObject)
                || !(reply["content"] is JObject)))
                throw new BilibiliApiException(-1, "B站回复预览格式已变化，请刷新重试。");
            return new CommentItem
            {
                Id = Number(item, "rpid"), Oid = Number(item, "oid"), Type = (int)Number(item, "type"),
                RootId = Number(item, "root"), AuthorMid = Number(member, "mid"), Author = Str(member, "uname"),
                AvatarUrl = ImageUrl(Str(member, "avatar")), Content = message,
                Level = levelKnown ? level : (int?)null,
                IsUploader = Flag(item["reply_control"], "is_up"),
                IsPinned = Flag(item["reply_control"], "is_top"),
                IpLocation = Str(item["reply_control"], "location"),
                FanNumber = CommentFanNumber(fan), FanBadgeImageUrl = CommentImageUrl(Str(badge, "image")),
                ContentParts = SplitCommentContent(message, content?["emote"] as JObject),
                Pictures = MapCommentPictures(content?["pictures"] as JArray),
                TimeText = FormatDate(Number(item, "ctime")), Likes = Math.Max(0L, Number(item, "like")),
                Vote = actionKnown ? (CommentVote)action : CommentVote.None, VoteKnown = actionKnown,
                ReplyCount = Math.Max(0L, Number(item, "rcount")),
                Replies = previews.GroupBy(reply => Number(reply, "rpid"))
                    .Select(group => MapCommentItem(group.First(), false)).ToList()
            };
        }

        private static IReadOnlyList<CommentContentPart> SplitCommentContent(string message, JObject emotes)
        {
            if (string.IsNullOrEmpty(message)) return Array.Empty<CommentContentPart>();
            var tokens = (emotes?.Properties() ?? Enumerable.Empty<JProperty>())
                .Where(entry => entry.Name.Length > 0 && entry.Value is JObject)
                .Where(entry => CommentImageUrl(Str(entry.Value, "url")).Length > 0)
                .OrderByDescending(entry => entry.Name.Length).ToList();
            var parts = new List<CommentContentPart>();
            var textStart = 0;
            var position = 0;
            while (position < message.Length)
            {
                var token = tokens.FirstOrDefault(entry => entry.Name.Length <= message.Length - position
                    && string.CompareOrdinal(message, position, entry.Name, 0, entry.Name.Length) == 0);
                if (token == null) { position++; continue; }
                if (position > textStart) parts.Add(new CommentContentPart { Text = message.Substring(textStart, position - textStart) });
                var metadata = token.Value["meta"] as JObject;
                var width = CommentDimension(token.Value, "width");
                var height = CommentDimension(token.Value, "height");
                if (width == 0) width = CommentDimension(metadata, "width");
                if (height == 0) height = CommentDimension(metadata, "height");
                double scale;
                if (!double.TryParse(Str(metadata, "size"), NumberStyles.Float, CultureInfo.InvariantCulture, out scale)
                    || double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0) scale = 1;
                parts.Add(new CommentContentPart
                {
                    ImageUrl = CommentImageUrl(Str(token.Value, "url")), AltText = token.Name,
                    Width = width, Height = height, Scale = Math.Max(1, Math.Min(4, scale))
                });
                position += token.Name.Length;
                textStart = position;
            }
            if (textStart < message.Length) parts.Add(new CommentContentPart { Text = message.Substring(textStart) });
            return parts;
        }

        private static IReadOnlyList<CommentPicture> MapCommentPictures(JArray pictures)
        {
            if (pictures == null) return Array.Empty<CommentPicture>();
            var result = new List<CommentPicture>();
            foreach (var picture in pictures.Take(20))
            {
                if (!(picture is JObject)) continue;
                var url = CommentImageUrl(Str(picture, "img_src"));
                if (url.Length == 0 || result.Any(item => item.ImageUrl == url)) continue;
                result.Add(new CommentPicture
                {
                    ImageUrl = url, Width = CommentDimension(picture, "img_width"),
                    Height = CommentDimension(picture, "img_height")
                });
            }
            return result;
        }

        private static int CommentDimension(JToken token, string name)
        {
            int value;
            return int.TryParse(Str(token, name), NumberStyles.None, CultureInfo.InvariantCulture, out value)
                && value > 0 && value <= 100000 ? value : 0;
        }

        private static string CommentFanNumber(JToken fan)
        {
            if (!(fan is JObject) || fan["is_fan"] != null && !Flag(fan, "is_fan")) return "";
            var text = Str(fan, "num_str");
            long value;
            if (text.Length > 0 && text.Length <= 24 && text.All(character => character >= '0' && character <= '9'))
                return text;
            return long.TryParse(Str(fan, "number"), NumberStyles.None, CultureInfo.InvariantCulture, out value)
                && value > 0 ? Invariant(value) : "";
        }

        private static string CommentImageUrl(string value)
        {
            var normalized = ImageUrl(value);
            Uri uri;
            if (!Uri.TryCreate(normalized, UriKind.Absolute, out uri) || uri.UserInfo.Length > 0 || uri.Port != 443) return "";
            var host = uri.Host;
            return new[] { "hdslb.com", "biliimg.com", "bilibili.com" }
                .Any(domain => host.Equals(domain, StringComparison.OrdinalIgnoreCase)
                    || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase)) ? normalized : "";
        }

        private sealed class CommentReadAccount
        {
            internal long Mid;
            internal string Session;
            internal string Csrf;
        }

        private CommentReadAccount CaptureCommentReadAccount()
            => new CommentReadAccount { Mid = IsLoggedIn ? CurrentUser.Mid : 0,
                Session = _cookies.GetCookieValue("SESSDATA") ?? "", Csrf = _cookies.GetCookieValue("bili_jct") ?? "" };

        private void AttachCommentContext(IReadOnlyList<CommentItem> items, long oid, int type,
            CommentReadAccount account, long ownerMid = 0)
        {
            var accountMatches = account != null && account.Mid > 0 && IsLoggedIn && CurrentUser.Mid == account.Mid
                && string.Equals(account.Session, _cookies.GetCookieValue("SESSDATA") ?? "", StringComparison.Ordinal)
                && string.Equals(account.Csrf, _cookies.GetCookieValue("bili_jct") ?? "", StringComparison.Ordinal);
            foreach (var item in items)
            {
                if (item.Oid > 0 && item.Oid != oid || item.Type > 0 && item.Type != type)
                    throw new BilibiliApiException(-1, "B站返回的评论与当前内容不一致，请刷新重试。");
                item.Oid = oid; item.Type = type;
                item.IsUploader = item.IsUploader || ownerMid > 0 && item.AuthorMid == ownerMid;
                item.VoteKnown = item.VoteKnown && accountMatches;
                item.VoteAccountMid = accountMatches ? account.Mid : 0;
                if (!accountMatches) item.Vote = CommentVote.None;
                AttachCommentContext(item.Replies, oid, type, account, ownerMid);
            }
        }
    }
}
