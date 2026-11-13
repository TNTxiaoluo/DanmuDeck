using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BiliXbox.Models;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace BiliXbox.Controls
{
    public sealed partial class CreatorHubView
    {
        public bool ScrollBy(double delta)
        {
            if (_writing || IsImageOpen || Visibility != Visibility.Visible) return false;
            ClearCommentActions();
            ++_focusGeneration; _pendingList = null; _pendingIndex = -1;
            var current = FocusManager.GetFocusedElement() as Control;
            if (IsDetail && (ReferenceEquals(current, _detailScroll) || FindAncestor<ScrollViewer>(current, _detail) == _detailScroll))
                return ViewportFocusScroll.Scroll(_detailScroll, delta, result => { if (result.Container != null) _lastFocus = result.Container; });
            var list = ActiveList; var pane = _pane;
            return ViewportFocusScroll.Scroll(list, delta, result =>
            {
                if (_pane == pane && ReferenceEquals(list, ActiveList) && Visibility == Visibility.Visible && result.Container != null)
                    _lastFocus = result.Container;
            });
        }

        internal void Navigate(PlayerDirection direction)
        {
            ViewportFocusScroll.Reconcile(ActiveList); ViewportFocusScroll.Cancel(this);
            var current = FocusManager.GetFocusedElement() as Control;
            var list = ActiveList;
            if (IsDetail && NavigateCommentActions(current, direction)) return;
            if (ReferenceEquals(current, _draftEditor))
            {
                if (direction == PlayerDirection.Up) Focus(_write.IsEnabled ? _write : _refreshComments);
                else if (direction == PlayerDirection.Down) FocusFirstOr(list, ActiveMore);
                else if (direction == PlayerDirection.Left) Focus(_detailScroll);
                else Focus(_send.IsEnabled ? _send : _write);
                return;
            }
            int index = ListIndex(list, current);
            if (index >= 0)
            {
                if (_pane == HubPane.Videos)
                {
                    int columns = VideoColumns(), next = index;
                    if (direction == PlayerDirection.Left) next = Math.Max(index / columns * columns, index - 1);
                    else if (direction == PlayerDirection.Right) next = Math.Min(Math.Min(list.Items.Count - 1, (index / columns + 1) * columns - 1), index + 1);
                    else if (direction == PlayerDirection.Up)
                    { if (index < columns) { Focus(_videosTab); return; } next = index - columns; }
                    else if (index + columns >= list.Items.Count) { Focus(_next.IsEnabled ? _next : _refresh); return; }
                    else next = index + columns;
                    FocusItem(list, next); return;
                }
                if (direction == PlayerDirection.Left || direction == PlayerDirection.Right)
                {
                    if (_pane == HubPane.Replies && direction == PlayerDirection.Right && list.Items[index] is CommentCard card)
                        EnterCommentActions(card, true);
                    else Focus(IsDetail ? (Control)_detailScroll : _dynamicsTab);
                    return;
                }
                if (IsDetail && ScrollCommentRow(list, index, direction == PlayerDirection.Down)) return;
                int target = index + (direction == PlayerDirection.Up ? -1 : 1);
                if (IsDetail)
                {
                    int next = NextCommentRow(list, target, direction == PlayerDirection.Up ? -1 : 1);
                    target = next >= 0 ? next : direction == PlayerDirection.Up ? -1 : list.Items.Count;
                }
                if (target < 0) Focus(IsDetail ? (_write.IsEnabled ? _write : _refreshComments) : _dynamicsTab);
                else if (target >= list.Items.Count) Focus(IsDetail ? ActiveMore : _moreDynamics.IsEnabled ? _moreDynamics : _refresh);
                else FocusItem(list, target);
                return;
            }
            if (IsDetail && ReferenceEquals(current, _detailScroll))
            {
                if (direction == PlayerDirection.Up || direction == PlayerDirection.Down)
                {
                    double offset = Math.Max(0, Math.Min(_detailScroll.ScrollableHeight,
                        _detailScroll.VerticalOffset + (direction == PlayerDirection.Up ? -1 : 1) * Math.Max(70, _detailScroll.ViewportHeight * .25)));
                    if (offset <= 0 && direction == PlayerDirection.Up) Focus(IsAvailable(_detailVideo) ? _detailVideo : _back);
                    else _detailScroll.ChangeView(null, offset, null, true);
                }
                else if (direction == PlayerDirection.Right) FocusFirstOr(list, _write.IsEnabled ? _write : _refreshComments);
                else Focus(_back);
                return;
            }
            if (IsDetail && ReferenceEquals(current, _detailVideo))
            {
                if (direction == PlayerDirection.Down) Focus(_detailScroll);
                else if (direction == PlayerDirection.Up || direction == PlayerDirection.Left) Focus(_back);
                else Focus(_write.IsEnabled ? _write : _refreshComments);
                return;
            }
            var group = HeaderActions();
            int selected = group.IndexOf(current);
            if (selected >= 0)
            {
                if (direction == PlayerDirection.Left || direction == PlayerDirection.Right)
                    Focus(group[Math.Max(0, Math.Min(group.Count - 1, selected + (direction == PlayerDirection.Left ? -1 : 1)))]);
                else if (direction == PlayerDirection.Down && IsDetail &&
                    (ReferenceEquals(current, _write) || ReferenceEquals(current, _send) || ReferenceEquals(current, _refreshComments)))
                    Focus(_draftEditor.IsEnabled ? (Control)_draftEditor : _detailScroll);
                else if (direction == PlayerDirection.Down)
                    FocusFirstOr(list, IsDetail ? (Control)_detailScroll : _refresh);
                else Focus(_back);
                return;
            }
            if (ReferenceEquals(current, ActiveMore) || ReferenceEquals(current, _previous) || ReferenceEquals(current, _next) || ReferenceEquals(current, _moreDynamics))
            {
                if (direction == PlayerDirection.Up && list.Items.Count > 0) FocusItem(list, list.Items.Count - 1);
                else if (IsDetail) Focus(_write.IsEnabled ? _write : _refreshComments);
                else if (_pane == HubPane.Videos && (direction == PlayerDirection.Left || direction == PlayerDirection.Right))
                { if (direction == PlayerDirection.Left && _previous.IsEnabled) Focus(_previous); else if (_next.IsEnabled) Focus(_next); else Focus(_refresh); }
                else Focus(_refresh);
                return;
            }
            FocusFirstOr(list, IsDetail ? (_write.IsEnabled ? _write : _refreshComments) : _videosTab);
        }

        public void Confirm(bool repeat)
        {
            if (repeat) return;
            var current = FocusManager.GetFocusedElement() as Control;
            int index = ListIndex(ActiveList, current);
            if (index >= 0)
            {
                if (_pane == HubPane.Videos) RequestVideo(_videos[index]);
                else if (_pane == HubPane.Dynamics) Run(() => OpenDynamicAsync(_dynamics[index]));
                else
                {
                    var card = FindAncestor<CommentCard>(current, ActiveList) ?? ActiveList.Items[index] as CommentCard;
                    ActivateCommentCard(card, current);
                }
                return;
            }
            if (ReferenceEquals(current, _back)) { if (!Back()) CloseRequested?.Invoke(this, EventArgs.Empty); }
            else if (ReferenceEquals(current, _videosTab)) SwitchTab(HubPane.Videos);
            else if (ReferenceEquals(current, _dynamicsTab)) SwitchTab(HubPane.Dynamics);
            else if (ReferenceEquals(current, _refresh)) Run(RefreshAsync);
            else if (ReferenceEquals(current, _previous)) { if (_previous.IsEnabled) Run(() => LoadVideosAsync(_videoPage - 1)); }
            else if (ReferenceEquals(current, _next)) { if (_next.IsEnabled) Run(() => LoadVideosAsync(_videoPage + 1)); }
            else if (ReferenceEquals(current, _moreDynamics)) { if (_moreDynamics.IsEnabled) Run(() => LoadDynamicsAsync(false)); }
            else if (ReferenceEquals(current, _refreshComments)) { if (_refreshComments.IsEnabled) Run(() => _pane == HubPane.Replies ? LoadRepliesAsync(true) : LoadCommentsAsync(true)); }
            else if (ReferenceEquals(current, _write)) { if (_write.IsEnabled) Run(WriteNewCommentAsync); }
            else if (ReferenceEquals(current, _draftEditor)) { if (_draftEditor.IsEnabled) Run(WriteCommentAsync); }
            else if (ReferenceEquals(current, _send)) { if (_send.IsEnabled) Run(PostCommentAsync); }
            else if (ReferenceEquals(current, _login)) LoginRequested?.Invoke(this, EventArgs.Empty);
            else if (ReferenceEquals(current, _detailVideo)) RequestVideo(FindDynamicVideo(_dynamic));
            else if (ReferenceEquals(current, _moreComments)) { if (_moreComments.IsEnabled) Run(() => LoadCommentsAsync(!_commentHasMore)); }
            else if (ReferenceEquals(current, _moreReplies)) { if (_moreReplies.IsEnabled) Run(() => LoadRepliesAsync(!_replyHasMore)); }
        }

        private List<Control> HeaderActions()
        {
            var controls = new List<Control> { _back, _videosTab, _dynamicsTab, _refresh };
            if (IsDetail) { controls.Add(_refreshComments); controls.Add(_write); controls.Add(_send); controls.Add(_login); }
            return controls.Where(IsAvailable).ToList();
        }

        private void NativeKeyDown(object sender, KeyRoutedEventArgs args)
        {
            // Native text editing owns ordinary arrows, spaces and Enter as
            // well as gamepad keys. They must not navigate or submit a comment.
            if (_writing || _draftEditor.IsFocusEngaged) return;
            // Gamepad and Navigation keys are owned by MainPage's single raw
            // dispatcher. Only a physical keyboard's ordinary arrows run here.
            PlayerDirection direction;
            if (args.Key == VirtualKey.Left) direction = PlayerDirection.Left;
            else if (args.Key == VirtualKey.Right) direction = PlayerDirection.Right;
            else if (args.Key == VirtualKey.Up) direction = PlayerDirection.Up;
            else if (args.Key == VirtualKey.Down) direction = PlayerDirection.Down;
            else if (args.Key == VirtualKey.Enter || args.Key == VirtualKey.Space)
            { args.Handled = true; Confirm(args.KeyStatus.WasKeyDown); return; }
            else return;
            args.Handled = true; Navigate(direction);
        }

        private void FocusFirstOr(ListViewBase list, Control fallback)
        { if (list.Items.Count > 0) FocusItem(list, 0); else Focus(fallback); }
        private void Focus(Control control)
        {
            if (!IsAvailable(control)) { control = _back; if (!IsAvailable(control)) return; }
            ViewportFocusScroll.Cancel(this);
            ++_focusGeneration; _pendingList = null; _pendingIndex = -1;
            if (control.Focus(FocusState.Keyboard)) { _lastFocus = control; control.StartBringIntoView(); }
        }
        private async void FocusItem(ListViewBase list, int index, bool alignStart = false)
        {
            if (index < 0 || index >= list.Items.Count || !ReferenceEquals(list, ActiveList)) return;
            if (IsDetail && !(list.Items[index] is CommentCard))
            {
                int next = NextCommentRow(list, index, 1);
                if (next < 0) next = NextCommentRow(list, index, -1);
                if (next < 0) return;
                index = next;
            }
            ClearCommentActions();
            ViewportFocusScroll.Cancel(this);
            int generation = ++_focusGeneration;
            _pendingList = list; _pendingIndex = index;
            object item = list.Items[index];
            try
            {
                list.ScrollIntoView(item, alignStart ? ScrollIntoViewAlignment.Leading : ScrollIntoViewAlignment.Default);
                for (int attempt = 0; attempt < 3; ++attempt)
                {
                    bool focused = false;
                    await Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                    {
                        if (generation != _focusGeneration || !ReferenceEquals(list, ActiveList) || Visibility != Visibility.Visible ||
                            index >= list.Items.Count || !ReferenceEquals(item, list.Items[index])) return;
                        list.UpdateLayout();
                        var row = list.ContainerFromIndex(index) as Control;
                        if (row != null && row.Focus(FocusState.Keyboard)) { _lastFocus = row; focused = true; }
                    });
                    if (focused || generation != _focusGeneration) break;
                }
            }
            catch (Exception) { /* A tab or creator replacement invalidated the container. */ }
            finally { if (generation == _focusGeneration) { _pendingList = null; _pendingIndex = -1; } }
        }
        private int ListIndex(ListViewBase list, Control current)
        {
            if (_pendingList == list && _pendingIndex >= 0 && _pendingIndex < list.Items.Count) return _pendingIndex;
            var row = FindAncestor<SelectorItem>(current, list);
            return row == null ? -1 : list.IndexFromContainer(row);
        }
        private int VideoColumns()
        {
            var wrap = _videoList.ItemsPanelRoot as ItemsWrapGrid;
            double width = wrap?.ItemWidth ?? 270;
            if (double.IsNaN(width) || width <= 0) width = 270;
            return Math.Max(1, (int)Math.Floor(Math.Max(width, _videoList.ActualWidth - _videoList.Padding.Left - _videoList.Padding.Right) / width));
        }
        private bool IsAvailable(Control control)
        {
            if (control == null || !control.IsEnabled || !control.IsTabStop) return false;
            DependencyObject node = control;
            while (node != null)
            {
                var element = node as UIElement;
                if (element != null && element.Visibility != Visibility.Visible) return false;
                if (ReferenceEquals(node, this)) return true;
                node = VisualTreeHelper.GetParent(node);
            }
            return false;
        }
        private static T FindAncestor<T>(DependencyObject child, DependencyObject root) where T : DependencyObject
        {
            while (child != null && child != root)
            { var match = child as T; if (match != null) return match; child = VisualTreeHelper.GetParent(child); }
            return null;
        }
    }
}
