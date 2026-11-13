using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Controls;
using BiliXbox.Models;
using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace BiliXbox
{
    public sealed partial class MainPage
    {
        private CommentItem _commentComposerReturnItem;
        private bool _commentComposerReturnReplies;
        private long _commentComposerVideoAid;
        private CommentCard _commentActionCard;
        private Control _commentActionReturn;
        private CommentImageViewer _commentImages;
        private CommentItem _replyComposerTarget;
        private long _replyComposerAid, _replyComposerAccount;
        private readonly Dictionary<long, string> _replyDrafts = new Dictionary<long, string>();

        private void CommentViewport_SizeChanged(object sender, SizeChangedEventArgs args)
        {
            var viewport = sender as FrameworkElement;
            if (viewport != null)
                viewport.Clip = new RectangleGeometry { Rect = new Rect(0, 0, Math.Max(0, args.NewSize.Width), Math.Max(0, args.NewSize.Height)) };
        }

        // Called by X independently of the current comment's length or index.
        // Writing fills a draft; only the separate Publish action posts it.
        private bool TryOpenCommentComposer()
        {
            if (!_watching || _playerUi.Drawer != PlayerDrawer.Comments || _dialogOpen || _inputPaneVisible) return false;
            if (!CommentInput.IsEnabled)
            { SetStatus("当前评论区暂不可写"); return true; }
            var current = FocusManager.GetFocusedElement() as Control;
            RememberCommentComposerPosition(current);
            var card = FindGamepadAncestor<CommentCard>(current, GamepadDrawerRoot);
            var row = FindGamepadAncestor<ListViewItem>(current, _viewingReplies ? ReplyList : CommentList);
            var item = card?.Item ?? row?.Content as CommentItem;
            if (item == null && _gamepadListFocusPending && _gamepadNavigationList != null
                && _gamepadNavigationIndex >= 0 && _gamepadNavigationIndex < _gamepadNavigationList.Items.Count)
                item = _gamepadNavigationList.Items[_gamepadNavigationIndex] as CommentItem;
            if (item == null && _viewingReplies)
                item = current == ReplyCommentInput || current == SendReplyCommentButton
                    ? _replyComposerTarget ?? _replyRoot : _replyRoot;
            if (item != null)
            {
                if (!_viewingReplies)
                {
                    _dispatchingPlayerAction = true;
                    try { CommentReplies_Requested(this, new CommentRepliesRequestedEventArgs(item)); }
                    finally { _dispatchingPlayerAction = false; }
                }
                SelectReplyComposerTarget(item);
            }
            _dispatchingPlayerAction = true;
            try { CommentEntry_Click(CommentInput, new RoutedEventArgs()); }
            finally { _dispatchingPlayerAction = false; }
            return true;
        }

        private void RememberCommentComposerPosition(Control control)
        {
            long aid = _video?.Aid ?? 0;
            if (_commentComposerVideoAid != aid)
            {
                _commentComposerVideoAid = aid;
                _commentComposerReturnItem = null;
                _commentComposerReturnReplies = _viewingReplies;
            }
            var list = _viewingReplies ? ReplyList : CommentList;
            var row = FindGamepadAncestor<ListViewItem>(control, list);
            var item = row?.Content as CommentItem;
            if (_viewingReplies && (ReferenceEquals(control, ReplyRootCard) || IsGamepadDescendant(control, ReplyRootCard))) item = _replyRoot;
            if (_gamepadListFocusPending && ReferenceEquals(_gamepadNavigationList, list) &&
                _gamepadNavigationIndex >= 0 && _gamepadNavigationIndex < list.Items.Count)
                item = list.Items[_gamepadNavigationIndex] as CommentItem;
            if (item == null) return;
            _commentComposerReturnItem = item;
            _commentComposerReturnReplies = _viewingReplies;
        }

        private void RestoreCommentComposerPosition()
        {
            if (_commentComposerVideoAid != (_video?.Aid ?? 0))
            {
                _commentComposerReturnItem = null;
                _commentComposerReturnReplies = _viewingReplies;
            }
            if (_commentComposerReturnReplies != _viewingReplies)
            {
                _viewingReplies = _commentComposerReturnReplies && _replyRoot != null;
                ApplyPlayerUi();
            }
            var list = _viewingReplies ? ReplyList : CommentList;
            if (_viewingReplies && ReferenceEquals(_commentComposerReturnItem, _replyRoot))
            { FocusGamepadControl(ReplyRootCard); return; }
            int index = _commentComposerReturnItem == null ? -1 : list.Items.IndexOf(_commentComposerReturnItem);
            if (index >= 0) FocusGamepadListItem(list, index, true);
            else if (list.Items.Count > 0) FocusGamepadListItem(list, 0, true);
            else FocusGamepadControl(_viewingReplies ? RepliesBackButton : CommentsCloseButton);
        }

        private bool NavigateCommentDrawerControls(Control current, PlayerDirection direction)
        {
            if (_playerUi.Drawer != PlayerDrawer.Comments) return false;
            if (_commentActionCard != null)
            {
                if (_commentActionCard.IsLoaded && IsGamepadDescendant(current, _commentActionCard))
                {
                    var action = _commentActionCard.NavigateAction(current, direction);
                    if (action != null) FocusGamepadControl(action);
                    return true;
                }
                _commentActionCard = null; _commentActionReturn = null;
            }
            if (_viewingReplies && (ReferenceEquals(current, ReplyRootCard) || IsGamepadDescendant(current, ReplyRootCard)))
            {
                if (ReadExpandedComment(ReplyRootCard, direction)) return true;
                if (direction == PlayerDirection.Down)
                {
                    if (ReplyList.Items.Count > 0) FocusGamepadListItem(ReplyList, 0, true);
                    else FocusGamepadControl(MoreRepliesButton.IsEnabled ? (Control)MoreRepliesButton : ReplyCommentInput);
                }
                else if (direction == PlayerDirection.Up) FocusGamepadControl(RepliesBackButton);
                else if (direction == PlayerDirection.Right) FocusGamepadControl(ReplyCommentInput);
                return true;
            }
            if (_viewingReplies && direction == PlayerDirection.Down && (current == RepliesBackButton || current == RepliesCloseButton))
            { FocusGamepadControl(ReplyRootCard); return true; }
            var list = _viewingReplies ? ReplyList : CommentList;
            bool inList = ReferenceEquals(current, list) || IsGamepadDescendant(current, list);
            if (direction == PlayerDirection.Right && inList)
            {
                RememberCommentComposerPosition(current);
                CancelGamepadListFocus();
                FocusGamepadControl(_viewingReplies ? ReplyCommentInput :
                    string.IsNullOrWhiteSpace(CommentInput.Text) ? (Control)CommentInput : SendCommentButton);
                return true;
            }
            if (direction == PlayerDirection.Left && !inList)
            {
                RestoreCommentComposerPosition();
                return true;
            }
            if (!_viewingReplies && current == CommentInput && direction == PlayerDirection.Right)
            { FocusGamepadControl(SendCommentButton.IsEnabled ? (Control)SendCommentButton : CommentInput); return true; }
            if (!_viewingReplies && current == SendCommentButton && direction == PlayerDirection.Up)
            { FocusGamepadControl(CommentInput.IsEnabled ? (Control)CommentInput : CommentsCloseButton); return true; }
            if (_viewingReplies && current == ReplyCommentInput && direction == PlayerDirection.Right)
            { FocusGamepadControl(SendReplyCommentButton); return true; }
            if (_viewingReplies && current == SendReplyCommentButton && direction == PlayerDirection.Up)
            { FocusGamepadControl(ReplyCommentInput); return true; }
            return false;
        }

        private bool ReadExpandedComment(Control row, PlayerDirection direction)
        {
            if (!_viewingReplies || row == null || (direction != PlayerDirection.Up && direction != PlayerDirection.Down)) return false;
            var viewer = ViewportFocusScroll.FindScrollViewer(ReplyList);
            if (viewer == null) return false;
            var bounds = row.TransformToVisual(ReplyListViewport).TransformBounds(new Rect(0, 0, row.ActualWidth, row.ActualHeight));
            double delta = CommentReadingScroll.GetDelta(bounds.Top, bounds.Bottom, ReplyListViewport.ActualHeight, direction == PlayerDirection.Down);
            if (Math.Abs(delta) < .5) return false;
            double offset = Math.Max(0, Math.Min(viewer.ScrollableHeight, viewer.VerticalOffset + delta));
            if (Math.Abs(offset - viewer.VerticalOffset) < .5) return false;
            viewer.ChangeView(null, offset, null, true);
            UpdateGamepadFocusOutline();
            return true;
        }

        // The root is a ListView header, not an item. Native list reconciliation
        // cannot see it, especially when the thread has no children yet.
        private bool ScrollReplyThread(double delta)
        {
            var viewer = ViewportFocusScroll.FindScrollViewer(ReplyList);
            if (viewer == null || viewer.ScrollableHeight <= 0) return false;
            ViewportFocusScroll.Cancel(ReplyList);
            _manuallyScrolledList = null;
            viewer.ChangeView(null, Math.Max(0, Math.Min(viewer.ScrollableHeight, viewer.VerticalOffset + delta)), null, true);
            ReplyList.UpdateLayout();
            var focused = FocusManager.GetFocusedElement() as Control;
            Control anchor = IsGamepadDescendant(focused, ReplyRootCard) ? (Control)ReplyRootCard
                : FindGamepadAncestor<ListViewItem>(focused, ReplyList);
            Func<Control, bool> visible = control =>
            {
                if (control == null || !control.IsLoaded) return false;
                var bounds = control.TransformToVisual(ReplyListViewport).TransformBounds(new Rect(0, 0, control.ActualWidth, control.ActualHeight));
                return bounds.Bottom > 1 && bounds.Top < ReplyListViewport.ActualHeight - 1;
            };
            if (!visible(anchor))
            {
                anchor = visible(ReplyRootCard) ? (Control)ReplyRootCard : null;
                for (int index = 0; anchor == null && index < ReplyList.Items.Count; index++)
                {
                    var row = ReplyList.ContainerFromIndex(index) as Control;
                    if (visible(row)) anchor = row;
                }
            }
            if (anchor != null)
            {
                FocusGamepadControl(anchor, false);
                _gamepadNavigationList = ReplyList; _gamepadNavigationIndex = ReplyList.IndexFromContainer(anchor);
                RememberCommentComposerPosition(anchor);
            }
            UpdateGamepadFocusOutline();
            return true;
        }

        private void DisposeCommentImages()
        {
            bool open = _commentImages?.IsOpen == true;
            _commentImages?.Dispose(); _commentImages = null;
            if (open) _dialogOpen = false;
        }

        private void CommentDetails_Requested(object sender, CommentRepliesRequestedEventArgs args)
        {
            if (IgnorePlayerClick() || _dialogOpen || args.Comment == null || _disposed) return;
            if (!_viewingReplies) CommentReplies_Requested(sender, args);
        }

        private void CommentList_ItemClick(object sender, ItemClickEventArgs args)
        {
            var item = args.ClickedItem as CommentItem;
            if (IgnorePlayerClick() || _dialogOpen || _disposed || _viewingReplies || item == null) return;
            CommentReplies_Requested(sender, new CommentRepliesRequestedEventArgs(item));
        }

        // Summary rows always open their floor first. Pictures, reply counts,
        // and native child-button focus must not change that first A action.
        // Once inside the thread, A selects actions; another A confirms one.
        private bool TryActivateCommentControl(Control control)
        {
            if (_playerUi.Drawer != PlayerDrawer.Comments || control == null) return false;
            var list = _viewingReplies ? ReplyList : CommentList;
            var row = FindGamepadAncestor<ListViewItem>(control, list);
            var card = FindGamepadAncestor<CommentCard>(control, GamepadDrawerRoot)
                ?? (row == null ? null : FindGamepadChild<CommentCard>(row));
            var item = card?.Item ?? row?.Content as CommentItem;
            if (item == null) return false;
            if (!_viewingReplies)
            {
                RememberCommentComposerPosition(row ?? control);
                CommentReplies_Requested(control, new CommentRepliesRequestedEventArgs(item));
                return true;
            }
            if (card != null && !card.TryActivate(control)) EnterCommentActions(card, row ?? control);
            return true;
        }

        private void EnterCommentActions(CommentCard card, Control returnTo)
        {
            if (card?.Item == null) return;
            var action = card.PrimaryActionControl(_viewingReplies);
            if (action == null) return;
            _commentActionCard = card; _commentActionReturn = returnTo;
            RememberCommentComposerPosition(returnTo);
            CancelGamepadListFocus();
            FocusGamepadControl(action);
        }

        private bool ExitCommentActions()
        {
            if (_commentActionCard == null) return false;
            var card = _commentActionCard; var previous = _commentActionReturn;
            _commentActionCard = null; _commentActionReturn = null;
            if (previous?.IsLoaded == true && IsGamepadDescendant(previous, GamepadDrawerRoot)) FocusGamepadControl(previous);
            else if (ReferenceEquals(card, ReplyRootCard)) FocusGamepadControl(ReplyRootCard);
            else RestoreCommentComposerPosition();
            return true;
        }

        private void CommentImage_Requested(object sender, CommentImageRequestedEventArgs args)
        {
            if (_dialogOpen || _disposed || !_viewingReplies || args.Comment == null) return;
            if (_commentImages == null)
            {
                _commentImages = new CommentImageViewer();
                _commentImages.Closed += (s, e) =>
                {
                    _dialogOpen = false; _lastBack = DateTimeOffset.UtcNow;
                    if (!_disposed) DisablePointerCursor();
                };
            }
            if (_commentImages.Open(args.Comment.Pictures, args.ImageIndex, FocusManager.GetFocusedElement() as Control))
                _dialogOpen = true;
        }

        private void SelectReplyComposerTarget(CommentItem item)
        {
            if (item == null || _video == null) return;
            if (_replyComposerAid != _video.Aid || _replyComposerAccount != CurrentAccountMid)
            {
                _replyDrafts.Clear(); _replyComposerTarget = null;
                ReplyCommentInput.Text = "";
                _replyComposerAid = _video.Aid; _replyComposerAccount = CurrentAccountMid;
            }
            if (_replyComposerTarget != null) _replyDrafts[_replyComposerTarget.Id] = ReplyCommentInput.Text;
            _replyComposerTarget = item;
            string draft;
            ReplyCommentInput.Text = _replyDrafts.TryGetValue(item.Id, out draft) ? draft : "";
            ReplyCommentInput.PlaceholderText = "回复 @" + item.Author + " · X 输入";
        }

        private async void SendReplyComment_Click(object sender, RoutedEventArgs args)
        {
            if (IgnorePlayerClick() || _postingComment || _replyComposerTarget == null || _video == null || _watchCts == null) return;
            if (!_api.IsLoggedIn) { await ShowLoginAsync(); return; }
            if (_replyComposerAid != _video.Aid || _replyComposerAccount != CurrentAccountMid)
            { SetStatus("账号或视频已改变，请重新选择回复对象。"); return; }
            string text = ReplyCommentInput.Text.Trim();
            if (text.Length == 0) { SetStatus("请输入回复内容"); return; }
            var target = _replyComposerTarget; var owner = _watchCts; var token = owner.Token;
            long aid = _video.Aid, account = CurrentAccountMid;
            _postingComment = true; SendReplyCommentButton.IsEnabled = false;
            try
            {
                await _api.PostCommentReplyAsync(aid, target, text, token);
                if (owner != _watchCts || token.IsCancellationRequested || CurrentAccountMid != account) return;
                _replyDrafts.Remove(target.Id);
                if (ReferenceEquals(target, _replyComposerTarget)) ReplyCommentInput.Text = "";
                SetStatus("回复已提交，展示以 Bilibili 的处理结果为准");
                if (_viewingReplies && _replyRoot?.Id == (target.RootId > 0 ? target.RootId : target.Id))
                { _replyPage = 0; _replyHasMore = true; await LoadRepliesAsync(); }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { if (owner == _watchCts) SetStatus("回复发布未确认：" + Friendly(ex) + "；草稿已保留，请刷新确认后再试。"); }
            finally
            {
                if (owner == _watchCts) { _postingComment = false; SendReplyCommentButton.IsEnabled = _lastCommentResult?.IsClosed != true; }
            }
        }
    }
}
