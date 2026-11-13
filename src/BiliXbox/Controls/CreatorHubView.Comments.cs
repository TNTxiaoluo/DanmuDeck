using System;
using System.Linq;
using System.Threading.Tasks;
using BiliXbox.Models;
using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace BiliXbox.Controls
{
    public sealed partial class CreatorHubView
    {
        private void ResetReplyTarget()
        {
            _draftState?.DisarmRetry();
            _composeReply = null; _composeRoot = _composeParent = 0;
        }

        private bool SelectReplyTarget(CommentItem item)
        {
            // The displayed comment must belong to this exact dynamic subject.
            // The selected child supplies parent; root always remains the floor.
            if (item == null || item.Id <= 0 || _target == null || !IsDetail
                || item.Oid != _target.Oid || item.Type != _target.Type) return false;
            long root = item.RootId > 0 ? item.RootId : item.Id;
            if (_pane == HubPane.Replies && _replyRoot != null) root = _replyRoot.Id;
            _draftState?.DisarmRetry();
            _composeReply = item; _composeRoot = root; _composeParent = item.Id;
            return true;
        }

        private Task WriteNewCommentAsync()
        {
            if (_writing || _posting) return Task.CompletedTask;
            ResetReplyTarget(); ClearCommentActions(); SelectDraft(); UpdateActions();
            Focus(_draftEditor);
            return WriteCommentAsync();
        }

        private void RebuildReplyRows()
        {
            _replyList.Items.Clear();
            if (_replyRoot == null) return;
            _replyList.Items.Add(BuildCommentCard(_replyRoot, false, true));
            var heading = new StackPanel { Spacing = 12, Margin = new Thickness(0, 8, 0, 14) };
            heading.Children.Add(new Border { Height = 1, Background = Brush(65, 255, 255, 255) });
            AutomationProperties.SetName(heading, "以下是这条评论的回复");
            _replyList.Items.Add(heading);
            foreach (var reply in _replies) _replyList.Items.Add(BuildCommentCard(reply, false, true));
        }

        private CommentCard FocusedCommentCard()
        {
            if (!IsDetail) return null;
            var current = FocusManager.GetFocusedElement() as Control;
            if (_actionCard != null && _actionCard.OwnsAction(current)) return _actionCard;
            var card = FindAncestor<CommentCard>(current, ActiveList);
            if (card != null) return card;
            int index = ListIndex(ActiveList, current);
            return index >= 0 ? ActiveList.Items[index] as CommentCard : null;
        }

        private void ActivateCommentCard(CommentCard card, Control current = null)
        {
            if (card?.Item == null || !IsDetail || !ActiveList.Items.Contains(card)) return;
            if (_pane != HubPane.Replies)
            {
                card.RequestPrimaryAction();
                return;
            }
            if (current != null && card.TryActivate(current)) return;
            EnterCommentActions(card, true);
        }

        private void EnterCommentActions(CommentCard card, bool preferPictures)
        {
            if (card == null || !IsDetail || !ActiveList.Items.Contains(card)) return;
            var first = card.PrimaryActionControl(preferPictures);
            if (first == null) return;
            _actionCard = card;
            _actionRow = ActiveList.ContainerFromItem(card) as Control;
            Focus(first);
        }

        private void ClearCommentActions() { _actionCard = null; _actionRow = null; }

        private bool LeaveCommentActions()
        {
            var current = FocusManager.GetFocusedElement() as Control;
            if (_actionCard == null) return false;
            var card = _actionCard;
            var row = _actionRow;
            bool wasAction = card.OwnsAction(current);
            ClearCommentActions();
            if (!wasAction) return false;
            int index = ActiveList.Items.IndexOf(card);
            if (index >= 0) FocusItem(ActiveList, index);
            else if (IsAvailable(row)) Focus(row);
            return true;
        }

        private bool NavigateCommentActions(Control current, PlayerDirection direction)
        {
            var card = FindAncestor<CommentCard>(current, ActiveList);
            if (card == null || !card.OwnsAction(current)) return false;
            _actionCard = card;
            _actionRow = ActiveList.ContainerFromItem(card) as Control;
            var next = card.NavigateAction(current, direction);
            if (next != null) Focus(next);
            return true;
        }

        private void OpenCommentImage(CommentCard card, CommentImageRequestedEventArgs args)
        {
            if (_pane != HubPane.Replies || card == null || !card.IsExpanded || args?.Comment == null
                || !ActiveList.Items.Contains(card) || _commentCts == null || _commentCts.IsCancellationRequested) return;
            if (_commentImages == null)
            {
                _commentImages = new CommentImageViewer();
                _commentImages.Closed += (sender, closed) => ImageViewerClosed?.Invoke(this, EventArgs.Empty);
            }
            var restore = FocusManager.GetFocusedElement() as Control ?? card.PrimaryActionControl(true);
            if (_commentImages.Open(args.Comment.Pictures, args.ImageIndex, restore))
                ImageViewerOpened?.Invoke(this, EventArgs.Empty);
        }

        private int NextCommentRow(ListViewBase list, int start, int step)
        {
            for (int index = start; index >= 0 && index < list.Items.Count; index += step)
                if (list.Items[index] is CommentCard) return index;
            return -1;
        }

        private bool ScrollCommentRow(ListViewBase list, int index, bool down)
        {
            var row = list.ContainerFromIndex(index) as FrameworkElement;
            var viewer = ViewportFocusScroll.FindScrollViewer(list);
            if (row == null || viewer == null || viewer.ViewportHeight <= 0) return false;
            try
            {
                var bounds = row.TransformToVisual(viewer).TransformBounds(new Rect(0, 0, row.ActualWidth, row.ActualHeight));
                double delta = CommentReadingScroll.GetDelta(bounds.Top, bounds.Bottom, viewer.ViewportHeight, down);
                double offset = Math.Max(0, Math.Min(viewer.ScrollableHeight, viewer.VerticalOffset + delta));
                if (Math.Abs(offset - viewer.VerticalOffset) < .5) return false;
                // Preserve the selected floor until all of its text is visible.
                viewer.ChangeView(null, offset, null, true);
                return true;
            }
            catch (InvalidOperationException) { return false; }
            catch (System.Runtime.InteropServices.COMException) { return false; }
        }
    }
}
