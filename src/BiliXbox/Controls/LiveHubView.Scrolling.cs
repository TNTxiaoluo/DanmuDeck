using System;
using Windows.Foundation;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace BiliXbox.Controls
{
    public sealed partial class LiveHubView
    {
        private ScrollViewer _chatScroll;
        private ScrollViewer _focusScroll;
        private ItemsControl _focusScrollList;
        private int _focusScrollGeneration, _focusScrollRoomGeneration, _focusScrollPaneGeneration, _queuedScrollFocusGeneration;
        private bool _scrollFocusQueued;
        private double _focusedScrollOffset = double.NaN;
        private ScrollViewer _manualScrollViewer;
        private double _manualScrollTarget;
        private int _manualScrollFocusGeneration, _manualScrollRoomGeneration, _manualScrollPaneGeneration;
        private Control _manualFocusControl;
        private int _manualFocusGeneration, _manualFocusRoomGeneration, _manualFocusPaneGeneration;
        private bool _synchronizingScrollFocus, _chatFollowLatest = true, _chatFollowScheduled;
        private int _chatScrollGeneration;

        // MainPage owns the right stick. Keeping scrolling and the selected room
        // together prevents the next directional press restoring an offscreen card.
        public bool ScrollBy(double verticalPixels)
        {
            if (_editing || Visibility != Visibility.Visible || double.IsNaN(verticalPixels) ||
                double.IsInfinity(verticalPixels) || Math.Abs(verticalPixels) < .01) return false;
            if (!IsPlayingRoom)
            {
                var scroll = FindChild<ScrollViewer>(_rooms);
                if (scroll == null) return false;
                ScrollViewport(scroll, verticalPixels);
                QueueViewportFocus(scroll, _rooms);
                return true;
            }
            if (_pane == Pane.None) return false;
            WakeChrome();
            if (_pane == Pane.Chat)
            {
                var scroll = EnsureChatScroll();
                if (scroll == null) return false;
                ++_chatScrollGeneration; _chatFollowScheduled = false;
                double target = ScrollViewport(scroll, verticalPixels);
                _chatFollowLatest = target >= Math.Max(0, scroll.ScrollableHeight - 2);
                QueueViewportFocus(scroll, _messages);
                return true;
            }
            ScrollViewport(_drawerScroll, verticalPixels);
            QueueViewportFocus(_drawerScroll, null);
            return true;
        }

        public void ReconcileScrollFocus()
        {
            // A direction/A event can arrive before the queued layout pass.
            // Resolve its starting card/message now, then retire delayed work
            // before navigation reads RoomIndex or confirms a room.
            if (_editing || _focusScroll == null || _focusScrollGeneration != _focusGeneration ||
                _focusScrollRoomGeneration != _roomGeneration || _focusScrollPaneGeneration != _paneGeneration ||
                Visibility != Visibility.Visible) return;
            if (ReferenceEquals(_focusScrollList, _rooms) ? IsPlayingRoom : !IsPlayingRoom) return;
            _focusScroll.UpdateLayout();
            FocusViewport(_focusScroll, _focusScrollList);
            ResetManualScrollState();
            ++_focusGeneration;
        }

        private double ScrollViewport(ScrollViewer scroll, double delta)
        {
            // Native ChangeView is asynchronous. Accumulate all 33 ms stick
            // samples against the last requested target while this same
            // viewport still owns the manual-scroll session.
            bool continuous = ReferenceEquals(scroll, _manualScrollViewer) && _manualScrollFocusGeneration == _focusGeneration &&
                _manualScrollRoomGeneration == _roomGeneration && _manualScrollPaneGeneration == _paneGeneration;
            double start = continuous ? _manualScrollTarget : scroll.VerticalOffset;
            if (!continuous) ResetManualScrollState();
            ++_focusGeneration;
            if (continuous && _manualFocusControl != null) _manualFocusGeneration = _focusGeneration;
            _manualScrollViewer = scroll; _manualScrollFocusGeneration = _focusGeneration;
            _manualScrollRoomGeneration = _roomGeneration; _manualScrollPaneGeneration = _paneGeneration;
            _manualScrollTarget = Math.Max(0, Math.Min(scroll.ScrollableHeight, start + delta));
            scroll.ChangeView(null, _manualScrollTarget, null, true);
            return _manualScrollTarget;
        }
        private void ResetManualScrollState()
        { _manualScrollViewer = null; _manualFocusControl = null; }

        private void QueueViewportFocus(ScrollViewer scroll, ItemsControl list)
        {
            int generation = _focusGeneration, paneGeneration = _paneGeneration, roomGeneration = _roomGeneration;
            if (!ReferenceEquals(_focusScroll, scroll))
            {
                if (_focusScroll != null) _focusScroll.ViewChanged -= FocusScroll_ViewChanged;
                _focusScroll = scroll; _focusScroll.ViewChanged += FocusScroll_ViewChanged;
            }
            _focusScrollList = list; _focusScrollGeneration = generation;
            _focusScrollRoomGeneration = roomGeneration; _focusScrollPaneGeneration = paneGeneration;
            if (_scrollFocusQueued && _queuedScrollFocusGeneration == generation) return;
            _scrollFocusQueued = true; _queuedScrollFocusGeneration = generation;
            try
            {
                var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                {
                    if (_queuedScrollFocusGeneration == generation) _scrollFocusQueued = false;
                    if (_editing || generation != _focusGeneration || roomGeneration != _roomGeneration ||
                        paneGeneration != _paneGeneration || Visibility != Visibility.Visible) return;
                    if (ReferenceEquals(list, _rooms) ? IsPlayingRoom : !IsPlayingRoom) return;
                    scroll.UpdateLayout();
                    _focusedScrollOffset = scroll.VerticalOffset;
                    FocusViewport(scroll, list);
                });
            }
            catch (Exception) { if (_queuedScrollFocusGeneration == generation) _scrollFocusQueued = false; }
        }
        private void FocusScroll_ViewChanged(object sender, ScrollViewerViewChangedEventArgs args)
        {
            // ChangeView may complete after the first dispatcher/layout pass.
            // Re-anchor only while that manual movement still owns focus; a
            // later D-pad press, room change or keyboard session retires it.
            if (args.IsIntermediate || _focusScroll == null || _focusScrollGeneration != _focusGeneration ||
                _focusScrollRoomGeneration != _roomGeneration || _focusScrollPaneGeneration != _paneGeneration) return;
            if (double.IsNaN(_focusedScrollOffset) || Math.Abs(_focusedScrollOffset - _focusScroll.VerticalOffset) > .5)
                QueueViewportFocus(_focusScroll, _focusScrollList);
        }

        private void FocusViewport(ScrollViewer scroll, ItemsControl list)
        {
            Control best = null; int bestIndex = -1; double bestScore = double.MaxValue;
            double height = scroll.ViewportHeight > 0 ? scroll.ViewportHeight : scroll.ActualHeight;
            int preferredColumn = ReferenceEquals(list, _rooms) ? _selectedIndex % RoomColumns() : -1;
            int count = list == null ? _drawerBody.Children.Count : list.Items.Count;
            for (int index = 0; index < count; ++index)
            {
                var row = (list == null ? _drawerBody.Children[index] : list.ContainerFromIndex(index)) as Control;
                if (row == null || !row.IsEnabled || row.Visibility != Visibility.Visible || row.ActualHeight <= 0) continue;
                try
                {
                    Rect bounds = row.TransformToVisual(scroll).TransformBounds(new Rect(0, 0, row.ActualWidth, row.ActualHeight));
                    if (bounds.Bottom <= 0 || bounds.Top >= height) continue;
                    // Prefer a fully visible row, then keep the previous column
                    // and choose the row nearest the center of the new viewport.
                    double score = Math.Abs(bounds.Top + bounds.Height / 2 - height / 2);
                    if (bounds.Top < 0 || bounds.Bottom > height) score += 100000;
                    if (preferredColumn >= 0) score += Math.Abs(index % RoomColumns() - preferredColumn) * 10000;
                    if (score < bestScore) { best = row; bestIndex = index; bestScore = score; }
                }
                catch (Exception) { /* A container can be recycled during layout. */ }
            }
            if (best == null) best = list as Control;
            if (best == null) return;
            if (ReferenceEquals(list, _rooms) && bestIndex >= 0) _selectedIndex = bestIndex;
            _manualFocusControl = best; _manualFocusGeneration = _focusGeneration;
            _manualFocusRoomGeneration = _roomGeneration; _manualFocusPaneGeneration = _paneGeneration;
            _synchronizingScrollFocus = true;
            try { if (best.Focus(FocusState.Keyboard)) _lastFocus = best; }
            finally { _synchronizingScrollFocus = false; }
        }

        private void SuppressManualFocusBringIntoView(object sender, BringIntoViewRequestedEventArgs args)
        {
            // Explicit focus follows a manually moved viewport. Its default
            // bring-into-view must not undo the right-stick scroll, including
            // for a single message taller than the whole chat viewport.
            bool ownedTarget = !_editing && _manualFocusControl != null && _manualFocusGeneration == _focusGeneration &&
                _manualFocusRoomGeneration == _roomGeneration && _manualFocusPaneGeneration == _paneGeneration &&
                IsWithin(args.TargetElement, _manualFocusControl);
            if (ownedTarget || (_synchronizingScrollFocus && args.TargetElement == null)) args.Handled = true;
        }

        private ScrollViewer EnsureChatScroll()
        {
            var scroll = FindChild<ScrollViewer>(_messages);
            if (ReferenceEquals(scroll, _chatScroll)) return scroll;
            if (_chatScroll != null) _chatScroll.ViewChanged -= ChatScroll_ViewChanged;
            _chatScroll = scroll;
            if (_chatScroll != null) _chatScroll.ViewChanged += ChatScroll_ViewChanged;
            return scroll;
        }
        private void ChatScroll_ViewChanged(object sender, ScrollViewerViewChangedEventArgs args)
        {
            if (args.IsIntermediate || _chatScroll == null || _chatFollowLatest) return;
            if (_chatScroll.VerticalOffset >= Math.Max(0, _chatScroll.ScrollableHeight - 2)) _chatFollowLatest = true;
        }
        private void FocusChatViewport()
        {
            var scroll = EnsureChatScroll();
            if (scroll == null) Focus(_messages);
            else { ++_focusGeneration; QueueViewportFocus(scroll, _messages); }
        }
        private void ResetChatScroll()
        { ++_chatScrollGeneration; _chatFollowLatest = true; _chatFollowScheduled = false; ResetManualScrollState(); }
        private void FollowLatestMessage()
        {
            if (!_chatFollowLatest || _chatFollowScheduled || _messages.Items.Count == 0 || _pane != Pane.Chat) return;
            int generation = _chatScrollGeneration, roomGeneration = _roomGeneration, paneGeneration = _paneGeneration;
            _chatFollowScheduled = true;
            try
            {
                var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                {
                    if (generation != _chatScrollGeneration) return;
                    _chatFollowScheduled = false;
                    if (!_chatFollowLatest || !RoomCurrent(roomGeneration) || paneGeneration != _paneGeneration ||
                        _pane != Pane.Chat || _messages.Items.Count == 0) return;
                    _manualScrollViewer = null;
                    _messages.ScrollIntoView(_messages.Items[_messages.Items.Count - 1]);
                });
            }
            catch (Exception) { _chatFollowScheduled = false; }
        }
    }
}
