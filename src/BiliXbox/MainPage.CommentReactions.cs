using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Controls;
using BiliXbox.Models;
using BiliXbox.Services;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;

namespace BiliXbox
{
    public sealed partial class MainPage
    {
        private readonly HashSet<string> _commentVotesInFlight = new HashSet<string>(StringComparer.Ordinal);
        private CancellationTokenSource _commentReactionsCts = new CancellationTokenSource();

        private async void CommentVote_Requested(object sender, CommentVoteRequestedEventArgs args)
        {
            if (IgnorePlayerClick() || args.Comment == null || _disposed || _watchCts == null) return;
            var owner = _watchCts;
            long aid = _video?.Aid ?? 0;
            string notice = await SubmitCommentVoteAsync(args.Comment, args.Vote, owner.Token);
            if (!_disposed && owner == _watchCts && _video?.Aid == aid && !owner.IsCancellationRequested)
                SetStatus(notice);
        }

        private async Task<string> SubmitCommentVoteAsync(CommentItem item, CommentVote desired, CancellationToken cancellationToken)
        {
            if (_disposed || item == null || cancellationToken.IsCancellationRequested) return "";
            long account = CurrentAccountMid;
            if (account <= 0) return "请先登录，再为评论点赞或点踩。";
            if (item.Id <= 0 || item.Oid <= 0 || item.Type <= 0) return "此评论缺少互动信息，请刷新评论后重试。";
            string key = account + ":" + item.Type + ":" + item.Oid + ":" + item.Id;
            if (!_commentVotesInFlight.Add(key)) return "正在更新此评论，请稍候。";
            using (var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _commentReactionsCts.Token))
            {
                RefreshCommentReactionCards();
                try
                {
                    // A vote displayed for a previous account cannot determine
                    // whether this account intended to set or cancel its vote.
                    if (desired == CommentVote.None && (!item.VoteKnown || item.VoteAccountMid != account))
                    {
                        var current = await _api.GetCommentVoteAsync(item, request.Token);
                        request.Token.ThrowIfCancellationRequested();
                        if (!_disposed && CurrentAccountMid == account) ApplyCommentVote(current, item);
                        return "已更新当前账号的互动状态，请再次选择点赞或点踩。";
                    }
                    var result = await _api.SetCommentVoteAsync(item, desired, request.Token);
                    request.Token.ThrowIfCancellationRequested();
                    if (_disposed || CurrentAccountMid != account) return "";
                    ApplyCommentVote(result, item);
                    return result.Vote == CommentVote.Like ? "已点赞。" : result.Vote == CommentVote.Hate ? "已点踩。" : "已取消评价。";
                }
                catch (CommentVoteUnconfirmedException)
                {
                    InvalidateCommentVote(item, account);
                    // Never repeat a write after a lost response. A read may
                    // resolve it; otherwise the service retains its write guard.
                    try
                    {
                        var current = await _api.GetCommentVoteAsync(item, request.Token);
                        request.Token.ThrowIfCancellationRequested();
                        if (_disposed || CurrentAccountMid != account) return "";
                        ApplyCommentVote(current, item);
                        return "已重新读取评论，当前互动状态已更新。";
                    }
                    catch (OperationCanceledException) when (request.IsCancellationRequested) { return ""; }
                    catch (Exception) { return "本次评价结果尚未确认。请稍后再次选择以检查状态，不会自动重复提交。"; }
                }
                catch (OperationCanceledException) when (request.IsCancellationRequested) { return ""; }
                catch (Exception ex) { return "评论互动失败：" + Friendly(ex); }
                finally
                {
                    _commentVotesInFlight.Remove(key);
                    if (!_disposed) RefreshCommentReactionCards();
                }
            }
        }

        private void ApplyCommentVote(CommentVoteResult result, CommentItem requested)
        {
            if (result == null || result.AccountMid != CurrentAccountMid) return;
            CommentVotePresentation.Apply(requested, result);
            foreach (var item in Comments) CommentVotePresentation.Apply(item, result);
            foreach (var item in Replies) CommentVotePresentation.Apply(item, result);
            CommentVotePresentation.Apply(_replyRoot, result);
            CreatorHub.ApplyCommentVote(result);
            RefreshCommentReactionCards();
            (_activeGeneralDialog as CommentReaderDialog)?.RefreshReaction();
        }

        private void InvalidateCommentVote(CommentItem item, long account)
        {
            CommentVotePresentation.Invalidate(item, item, account);
            foreach (var root in Comments) CommentVotePresentation.Invalidate(root, item, account);
            foreach (var root in Replies) CommentVotePresentation.Invalidate(root, item, account);
            CommentVotePresentation.Invalidate(_replyRoot, item, account);
            CreatorHub.InvalidateCommentVote(item, account);
        }

        private void RefreshCommentReactionCards()
        {
            long account = CurrentAccountMid;
            foreach (var item in Comments) CommentVotePresentation.ForAccount(item, account);
            foreach (var item in Replies) CommentVotePresentation.ForAccount(item, account);
            CommentVotePresentation.ForAccount(_replyRoot, account);
            CreatorHub.SetCommentReactionAccount(account);
            RefreshCommentReactionCards(this);
            (_activeGeneralDialog as CommentReaderDialog)?.RefreshReaction();
        }

        private void RefreshCommentReactionCards(DependencyObject root)
        {
            var card = root as CommentCard;
            if (card != null)
            {
                card.RefreshReaction();
                var item = card.Item;
                card.SetReactionBusy(item != null && _commentVotesInFlight.Contains(
                    CurrentAccountMid + ":" + item.Type + ":" + item.Oid + ":" + item.Id));
                return;
            }
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
                RefreshCommentReactionCards(VisualTreeHelper.GetChild(root, index));
        }

        private void StopCommentReactions(bool disposing = false)
        {
            Cancel(ref _commentReactionsCts);
            if (!disposing) _commentReactionsCts = new CancellationTokenSource();
        }
    }
}
