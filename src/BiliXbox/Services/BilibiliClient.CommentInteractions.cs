using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Models;
using Newtonsoft.Json.Linq;

namespace BiliXbox.Services
{
    // A transport failure after starting a write cannot be called a rejection.
    // The caller must read current state before offering another submission.
    public sealed class CommentVoteUnconfirmedException : Exception
    {
        public long Oid { get; }
        public int Type { get; }
        public long CommentId { get; }
        public long AccountMid { get; }

        internal CommentVoteUnconfirmedException(long oid, int type, long commentId, long accountMid, Exception inner)
            : base("尚未确认本次评论互动结果，请先刷新评论状态，不要重复提交。", inner)
        { Oid = oid; Type = type; CommentId = commentId; AccountMid = accountMid; }
    }

    public sealed partial class BilibiliClient
    {
        // Accessed only while holding the shared account mutation gate. A new
        // pane, CommentItem instance, or cancelled request cannot bypass this.
        private readonly HashSet<string> _unconfirmedCommentVotes = new HashSet<string>(StringComparer.Ordinal);

        public async Task<CommentVoteResult> GetCommentVoteAsync(CommentItem target,
            CancellationToken ct = default(CancellationToken))
        {
            var identity = CommentVoteIdentity(target);
            var account = CaptureInteractionAccount(false, ct);
            await _videoInteractionGate.WaitAsync(ct);
            try
            {
                var result = await GetCommentVoteCoreAsync(identity, account, ct);
                _unconfirmedCommentVotes.Remove(CommentVoteKey(identity, account.Mid));
                return result;
            }
            finally { _videoInteractionGate.Release(); }
        }

        public async Task<CommentVoteResult> SetCommentVoteAsync(CommentItem target, CommentVote desired,
            CancellationToken ct = default(CancellationToken))
        {
            var identity = CommentVoteIdentity(target);
            if (desired != CommentVote.None && desired != CommentVote.Like && desired != CommentVote.Hate)
                throw new ArgumentOutOfRangeException(nameof(desired));
            var account = CaptureInteractionAccount(true, ct);
            var key = CommentVoteKey(identity, account.Mid);
            await _videoInteractionGate.WaitAsync(ct);
            try
            {
                EnsureInteractionAccount(account, ct);
                if (_unconfirmedCommentVotes.Contains(key))
                    throw new CommentVoteUnconfirmedException(identity.Oid, identity.Type, identity.Id, account.Mid, null);
                // The on-screen comment may be stale or from another account.
                // Read the exact resource before choosing a cancellation route.
                var before = await GetCommentVoteCoreAsync(identity, account, ct);
                if (before.Vote == desired) return before;
                await PrepareInteractionMutationAsync(account, ct);
                var endpoint = desired == CommentVote.Hate || desired == CommentVote.None && before.Vote == CommentVote.Hate
                    ? "/x/v2/reply/hate" : "/x/v2/reply/action";
                var rejected = false;
                try
                {
                    // Each explicit action submits exactly one POST. Bilibili's
                    // operation replaces the opposite reaction; never emulate
                    // that with a second implicit unlike/dislike request.
                    var root = await PostJsonAsync(Api + endpoint, new Dictionary<string, string>
                    {
                        ["oid"] = Invariant(identity.Oid), ["type"] = Invariant(identity.Type),
                        ["rpid"] = Invariant(identity.Id), ["action"] = desired == CommentVote.None ? "0" : "1",
                        ["csrf"] = account.Csrf
                    }, ct);
                    rejected = ResponseCode(root) != 0;
                    RequireInteractionSuccess(root);
                    EnsureInteractionAccount(account, ct);
                    var after = await GetCommentVoteCoreAsync(identity, account, ct);
                    if (after.Vote != desired)
                        throw new BilibiliApiException(-1, "B站尚未返回与本次操作一致的评论状态，请刷新确认。");
                    return after;
                }
                catch (Exception exception) when (!rejected && !(exception is OutOfMemoryException))
                {
                    _unconfirmedCommentVotes.Add(key);
                    throw new CommentVoteUnconfirmedException(identity.Oid, identity.Type, identity.Id, account.Mid, exception);
                }
            }
            finally { _videoInteractionGate.Release(); }
        }

        private async Task<CommentVoteResult> GetCommentVoteCoreAsync(CommentItem identity,
            InteractionAccount account, CancellationToken ct)
        {
            EnsureInteractionAccount(account, ct);
            var data = RequireData(await GetJsonAsync(Api + "/x/v2/reply/detail?type=" + Invariant(identity.Type)
                + "&oid=" + Invariant(identity.Oid) + "&root=" + Invariant(identity.Id) + "&ps=1&pn=1", ct));
            EnsureInteractionAccount(account, ct);
            var reply = data["root"] as JObject;
            long likes;
            int action;
            if (reply == null || Number(reply, "rpid") != identity.Id || Number(reply, "oid") != identity.Oid
                || Number(reply, "type") != identity.Type
                || reply["action"]?.Type != JTokenType.Integer
                || !int.TryParse(Str(reply, "action"), NumberStyles.None, CultureInfo.InvariantCulture, out action)
                || action < 0 || action > 2 || reply["like"]?.Type != JTokenType.Integer
                || !long.TryParse(Str(reply, "like"), NumberStyles.None, CultureInfo.InvariantCulture, out likes) || likes < 0)
                throw new BilibiliApiException(-1, "B站未返回这条评论的有效互动状态，请刷新后重试。");
            return new CommentVoteResult
            {
                Oid = identity.Oid, Type = identity.Type, CommentId = identity.Id, AccountMid = account.Mid,
                Vote = (CommentVote)action, Likes = likes
            };
        }

        private static CommentItem CommentVoteIdentity(CommentItem target)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            CheckId(target.Id, nameof(target.Id)); CheckId(target.Oid, nameof(target.Oid));
            if (target.Type <= 0 || target.Type > 10000) throw new ArgumentException("评论所属内容无效，请刷新评论。", nameof(target));
            // Snapshot primitive IDs before any await; a UI model is mutable.
            return new CommentItem { Id = target.Id, Oid = target.Oid, Type = target.Type };
        }

        private static string CommentVoteKey(CommentItem identity, long mid)
            => Invariant(mid) + ":" + Invariant(identity.Type) + ":" + Invariant(identity.Oid) + ":" + Invariant(identity.Id);
    }
}
