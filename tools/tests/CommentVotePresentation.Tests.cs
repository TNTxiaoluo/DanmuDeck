using System;
using BiliXbox.Controls;
using BiliXbox.Models;

namespace BiliXbox.CommentVotePresentationTests
{
    public static class Tests
    {
        private static int _checks;
        public static string Run()
        {
            _checks = 0;
            var selected = Item(42, 100, 1, 300, CommentVote.None, false, 1000);
            var preview = Item(42, 100, 1, 300, CommentVote.None, false, 2);
            var grandchild = Item(42, 100, 1, 300, CommentVote.None, false, 1);
            var unrelated = Item(7, 100, 1, 300, CommentVote.Hate, true, 17);
            var root = Item(12, 100, 1, 300, CommentVote.Like, true, 90);
            preview.Replies = new[] { grandchild };
            root.Replies = new[] { preview, unrelated };
            var readback = Result(42, 100, 1, 300, CommentVote.Like, 1001);

            CommentVotePresentation.Apply(selected, readback);
            CommentVotePresentation.Apply(root, readback);
            foreach (var match in new[] { selected, preview, grandchild })
            {
                Check(match.VoteKnown && match.VoteAccountMid == 300 && match.Vote == CommentVote.Like, "Selected and nested preview copies reflect the same current account readback");
                Check(match.Likes == 1001, "Every matching copy uses the authoritative total rather than incrementing its local count");
            }
            Check(root.Vote == CommentVote.Like && root.Likes == 90 && unrelated.Vote == CommentVote.Hate && unrelated.Likes == 17, "Updating a reply does not change its root or adjacent rows");
            CommentVotePresentation.Apply(root, readback);
            Check(preview.Likes == 1001 && grandchild.Likes == 1001, "Reapplying a readback does not double count a like");

            var otherOid = Item(42, 101, 1, 300, CommentVote.Hate, true, 75);
            var otherType = Item(42, 100, 17, 300, CommentVote.None, true, 86);
            var otherId = Item(43, 100, 1, 300, CommentVote.None, true, 97);
            foreach (var item in new[] { otherOid, otherType, otherId })
            {
                long before = item.Likes; var vote = item.Vote;
                CommentVotePresentation.Apply(item, readback);
                Check(item.Likes == before && item.Vote == vote, "A colliding id in a different comment area or a different reply remains untouched");
            }

            readback.Vote = CommentVote.Hate; readback.Likes = 1000;
            CommentVotePresentation.Apply(selected, readback);
            Check(selected.Vote == CommentVote.Hate && selected.Likes == 1000 && selected.VoteKnown, "Switching from like to hate uses the server count and state");
            readback.Vote = CommentVote.None;
            CommentVotePresentation.Apply(selected, readback);
            Check(selected.Vote == CommentVote.None && selected.VoteKnown, "A confirmed cancellation is known neutral, not an unknown result");

            CommentVotePresentation.Invalidate(root, selected, 300);
            Check(!preview.VoteKnown && !grandchild.VoteKnown, "An unconfirmed write invalidates all matching nested preview copies");
            Check(root.VoteKnown && unrelated.VoteKnown, "An unconfirmed reply write does not invalidate unrelated comments");
            Check(preview.Likes == 1001 && preview.Vote == CommentVote.Like, "Unknown write state does not invent a new count or overwrite the last confirmed vote");
            var anotherAccount = Item(42, 100, 1, 301, CommentVote.Like, true, 55);
            CommentVotePresentation.Invalidate(anotherAccount, selected, 300);
            Check(anotherAccount.VoteKnown, "Late invalidation for account A cannot invalidate account B's confirmed readback");
            foreach (var item in new[] { otherOid, otherType, otherId })
            {
                CommentVotePresentation.Invalidate(item, selected, 300);
                Check(item.VoteKnown, "An unknown write is scoped to the complete area and reply identity");
            }

            CommentVotePresentation.Apply(root, readback);
            Check(preview.VoteKnown && preview.Vote == CommentVote.None && preview.Likes == 1000, "An explicit fresh read resolves the unknown result without repeating a write");

            var oldAccount = Item(1, 88, 1, 300, CommentVote.Like, true, 123);
            var sameAccount = Item(2, 88, 1, 301, CommentVote.Hate, true, 124);
            var unknownAccount = Item(3, 88, 1, 0, CommentVote.Like, true, 125);
            var accountTree = Item(4, 88, 1, 301, CommentVote.None, true, 126);
            accountTree.Replies = new[] { oldAccount, sameAccount, unknownAccount };
            CommentVotePresentation.ForAccount(accountTree, 301);
            Check(accountTree.VoteKnown && sameAccount.VoteKnown && sameAccount.Vote == CommentVote.Hate, "A current account keeps its confirmed state during refresh");
            Check(!oldAccount.VoteKnown && !unknownAccount.VoteKnown, "Switching accounts clears attribution of previous or anonymous vote state");
            Check(oldAccount.Likes == 123 && sameAccount.Likes == 124 && unknownAccount.Likes == 125, "Account switching preserves public like totals");
            CommentVotePresentation.ForAccount(accountTree, 0);
            Check(!accountTree.VoteKnown && !sameAccount.VoteKnown, "Logout removes personal vote attribution from roots and previews");
            Check(accountTree.Likes == 126 && sameAccount.Likes == 124, "Logout preserves public counts");

            CommentVotePresentation.Apply(null, readback);
            CommentVotePresentation.Apply(selected, null);
            CommentVotePresentation.Invalidate(null, selected, 300);
            CommentVotePresentation.Invalidate(selected, null, 300);
            CommentVotePresentation.ForAccount(null, 300);
            Check(selected.VoteKnown && selected.Likes == 1000, "Missing response or transiently absent view roots do not mutate confirmed state");
            return "Comment vote presentation: " + _checks + " checks passed.";
        }
        private static CommentItem Item(long id, long oid, int type, long account, CommentVote vote, bool known, long likes)
            => new CommentItem { Id = id, Oid = oid, Type = type, VoteAccountMid = account, Vote = vote, VoteKnown = known, Likes = likes };
        private static CommentVoteResult Result(long id, long oid, int type, long account, CommentVote vote, long likes)
            => new CommentVoteResult { CommentId = id, Oid = oid, Type = type, AccountMid = account, Vote = vote, Likes = likes };
        private static void Check(bool condition, string description)
        {
            _checks++;
            if (!condition) throw new InvalidOperationException(description);
        }
    }
}
