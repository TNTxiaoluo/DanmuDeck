using System;
using BiliXbox.Models;

namespace BiliXbox.Controls
{
    // A reply id is scoped to its actual comment area. Never apply a stale
    // account's result or copy a dynamic's reaction into a video comment.
    internal static class CommentVotePresentation
    {
        internal static void Apply(CommentItem item, CommentVoteResult result)
        {
            if (item == null || result == null) return;
            if (item.Id == result.CommentId && item.Oid == result.Oid && item.Type == result.Type)
            {
                item.Vote = result.Vote;
                item.VoteKnown = true;
                item.VoteAccountMid = result.AccountMid;
                item.Likes = result.Likes;
            }
            foreach (var reply in item.Replies ?? Array.Empty<CommentItem>()) Apply(reply, result);
        }

        internal static void Invalidate(CommentItem item, CommentItem target, long account)
        {
            if (item == null || target == null) return;
            if (item.Id == target.Id && item.Oid == target.Oid && item.Type == target.Type && item.VoteAccountMid == account)
                item.VoteKnown = false;
            foreach (var reply in item.Replies ?? Array.Empty<CommentItem>()) Invalidate(reply, target, account);
        }

        internal static void ForAccount(CommentItem item, long account)
        {
            if (item == null) return;
            if (account <= 0 || item.VoteAccountMid != account) item.VoteKnown = false;
            foreach (var reply in item.Replies ?? Array.Empty<CommentItem>()) ForAccount(reply, account);
        }
    }
}
