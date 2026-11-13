using System;
using System.Collections.Generic;
using System.Linq;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace BiliXbox.Controls
{
    public sealed partial class LiveHubView
    {
        public bool Back()
        {
            if (_editing) return true; // Native input's owner consumes B.
            if (!IsPlayingRoom) return false;
            if (_pane != Pane.None)
            {
                if (_pane == Pane.GiftAmount || _pane == Pane.GiftConfirm)
                { _quote = null; OpenPane(Pane.Gifts); FocusFirstRow(); return true; }
                var prior = _pane; _pane = Pane.None; ++_paneGeneration; _drawer.Visibility = Visibility.Collapsed; WakeChrome();
                Focus(prior == Pane.Chat ? _chatButton : prior == Pane.Quality ? _qualityButton : _giftButton); return true;
            }
            if (_chrome.Visibility == Visibility.Visible) { _chrome.Visibility = Visibility.Collapsed; return true; }
            LeaveRoom(); return true;
        }
        public void RestoreFocus()
        {
            if (_editing) return;
            if (IsPlayingRoom)
            {
                WakeChrome();
                if (_pane == Pane.Chat) Focus(_chatInput);
                else if (_pane != Pane.None) FocusFirstRow();
                else Focus(_play);
            }
            else if (_rooms.Items.Count > 0) FocusRoom(Math.Max(0, Math.Min(_selectedIndex, _rooms.Items.Count - 1)));
            else Focus(_roomInput);
        }
        internal bool Navigate(PlayerDirection direction)
        {
            if (_editing) return true;
            ReconcileScrollFocus();
            var current = FocusManager.GetFocusedElement() as Control;
            if (!OwnsFocus) return false;
            if (IsPlayingRoom)
            {
                if (_chrome.Visibility != Visibility.Visible && _pane == Pane.None) { WakeChrome(); Focus(_play); return true; }
                WakeChrome();
                if (_pane != Pane.None) { NavigateDrawer(current, direction); return true; }
                var buttons = new List<Control> { _play, _danmakuButton, _chatButton, _giftButton, _qualityButton };
                int index = buttons.IndexOf(current);
                if (ReferenceEquals(current, _leave)) { Focus(direction == PlayerDirection.Down ? _play : _leave); return true; }
                if (direction == PlayerDirection.Up) { Focus(_leave); return true; }
                if (index < 0) index = 0;
                if (direction == PlayerDirection.Left) index = Math.Max(0, index - 1);
                else if (direction == PlayerDirection.Right) index = Math.Min(buttons.Count - 1, index + 1);
                Focus(buttons[index]); return true;
            }
            int roomIndex = RoomIndex(current);
            if (roomIndex >= 0)
            {
                int columns = RoomColumns(), next = roomIndex;
                if (direction == PlayerDirection.Left) next = Math.Max(roomIndex / columns * columns, roomIndex - 1);
                else if (direction == PlayerDirection.Right) next = Math.Min(Math.Min(_roomItems.Count - 1, (roomIndex / columns + 1) * columns - 1), roomIndex + 1);
                else if (direction == PlayerDirection.Up)
                { if (roomIndex < columns) { Focus(_roomInput); return true; } next = roomIndex - columns; }
                else if (roomIndex + columns >= _roomItems.Count) { Focus(_next.IsEnabled ? _next : _refresh); return true; }
                else next = roomIndex + columns;
                FocusRoom(next); return true;
            }
            var header = new List<Control> { _roomInput, _open, _refresh };
            int selected = header.IndexOf(current);
            if (selected >= 0)
            {
                if (direction == PlayerDirection.Up) return false;
                if (direction == PlayerDirection.Down) { if (_rooms.Items.Count > 0) FocusRoom(0); else Focus(_refresh); }
                else Focus(header[Math.Max(0, Math.Min(header.Count - 1, selected + (direction == PlayerDirection.Left ? -1 : 1)))]);
                return true;
            }
            if (ReferenceEquals(current, _previous) || ReferenceEquals(current, _next))
            {
                if (direction == PlayerDirection.Up && _rooms.Items.Count > 0) FocusRoom(_rooms.Items.Count - 1);
                else if (direction == PlayerDirection.Left && _previous.IsEnabled) Focus(_previous);
                else if (direction == PlayerDirection.Right && _next.IsEnabled) Focus(_next);
                else Focus(_refresh); return true;
            }
            RestoreFocus(); return true;
        }
        private void NavigateDrawer(Control current, PlayerDirection direction)
        {
            if (_pane == Pane.Chat)
            {
                if (ReferenceEquals(current, _closeDrawer)) { if (direction == PlayerDirection.Down) FocusChatViewport(); return; }
                if (IsWithin(current, _messages))
                {
                    var scroll = EnsureChatScroll();
                    if (direction == PlayerDirection.Up || direction == PlayerDirection.Down)
                    {
                        bool up = direction == PlayerDirection.Up;
                        if (scroll != null && (up ? scroll.VerticalOffset > 1 : scroll.VerticalOffset < scroll.ScrollableHeight - 1))
                            ScrollBy((up ? -1 : 1) * Math.Max(80, scroll.ViewportHeight * .55));
                        else Focus(up ? _closeDrawer : _reconnectChat);
                    }
                    else Focus(direction == PlayerDirection.Left ? (Control)_chatInput : _reconnectChat);
                    return;
                }
                if (ReferenceEquals(current, _reconnectChat))
                { if (direction == PlayerDirection.Up) FocusChatViewport(); else Focus(_chatInput); }
                else if (ReferenceEquals(current, _chatInput)) Focus(direction == PlayerDirection.Up ? (Control)_reconnectChat : _send);
                else if (ReferenceEquals(current, _send)) Focus(_chatInput);
                else Focus(_chatInput);
                return;
            }
            var rows = _drawerBody.Children.OfType<Button>().Where(button => button.IsEnabled).Cast<Control>().ToList();
            rows.Insert(0, _closeDrawer); int index = rows.IndexOf(current);
            if (index < 0) index = 0;
            if (direction == PlayerDirection.Up || direction == PlayerDirection.Left) index = Math.Max(0, index - 1);
            else index = Math.Min(rows.Count - 1, index + 1);
            Focus(rows[index]);
        }
        public void Confirm(bool repeat)
        {
            if (repeat || _editing || !OwnsFocus) return;
            ReconcileScrollFocus();
            WakeChrome(); var current = FocusManager.GetFocusedElement() as Control;
            int index = RoomIndex(current);
            if (!IsPlayingRoom && index >= 0) { Run(() => OpenRoomAsync(_roomItems[index].Id)); return; }
            if (ReferenceEquals(current, _roomInput)) { Run(() => EditAsync(true)); return; }
            if (ReferenceEquals(current, _chatInput)) { Run(() => EditAsync(false)); return; }
            if (current is Button button && button.IsEnabled && _actions.TryGetValue(button, out var action)) action();
        }
        private void Focus(Control control)
        {
            if (control == null || !control.IsEnabled || control.Visibility != Visibility.Visible) return;
            ++_focusGeneration;
            if (control.Focus(FocusState.Keyboard)) { _lastFocus = control; control.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false }); }
        }
        private async void FocusRoom(int index)
        {
            if (_editing || IsPlayingRoom || index < 0 || index >= _rooms.Items.Count) return;
            int generation = ++_focusGeneration; var item = _rooms.Items[index]; _selectedIndex = index;
            _rooms.ScrollIntoView(item, index == 0 ? ScrollIntoViewAlignment.Leading : ScrollIntoViewAlignment.Default);
            try
            {
                for (int attempt = 0; attempt < 3; ++attempt)
                {
                    bool focused = false;
                    await Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                    {
                        if (_editing || generation != _focusGeneration || IsPlayingRoom || index >= _rooms.Items.Count || !ReferenceEquals(_rooms.Items[index], item) || Visibility != Visibility.Visible) return;
                        _rooms.UpdateLayout(); var row = _rooms.ContainerFromIndex(index) as Control;
                        if (row != null) { focused = row.Focus(FocusState.Keyboard); if (focused) _lastFocus = row; }
                    });
                    if (focused || generation != _focusGeneration) return;
                }
            }
            catch (Exception) { }
        }
        private int RoomIndex(Control current)
        {
            var row = Ancestor<SelectorItem>(current, _rooms); return row == null ? -1 : _rooms.IndexFromContainer(row);
        }
        private int RoomColumns()
        {
            double width = (_rooms.ItemsPanelRoot as ItemsWrapGrid)?.ItemWidth ?? 282;
            if (double.IsNaN(width) || width <= 0) width = 282;
            return Math.Max(1, (int)Math.Floor(Math.Max(width, _rooms.ActualWidth - _rooms.Padding.Left - _rooms.Padding.Right) / width));
        }
        private static T Ancestor<T>(DependencyObject child, DependencyObject root) where T : DependencyObject
        { while (child != null && child != root) { if (child is T found) return found; child = VisualTreeHelper.GetParent(child); } return null; }
        private static bool IsWithin(DependencyObject child, DependencyObject root)
        { while (child != null) { if (child == root) return true; child = VisualTreeHelper.GetParent(child); } return false; }
        private static T FindChild<T>(DependencyObject root) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); ++i)
            { var child = VisualTreeHelper.GetChild(root, i); if (child is T found) return found; var nested = FindChild<T>(child); if (nested != null) return nested; }
            return null;
        }
        private void NativeKeyDown(object sender, KeyRoutedEventArgs args)
        {
            if (_editing) return;
            if (GamepadKeyRouting.IsNavigationEvent(args)) { args.Handled = true; return; }
            PlayerDirection direction;
            if (args.Key == VirtualKey.Up) direction = PlayerDirection.Up;
            else if (args.Key == VirtualKey.Down) direction = PlayerDirection.Down;
            else if (args.Key == VirtualKey.Left) direction = PlayerDirection.Left;
            else if (args.Key == VirtualKey.Right) direction = PlayerDirection.Right;
            else if (args.Key == VirtualKey.Enter || args.Key == VirtualKey.Space) { args.Handled = true; Confirm(args.KeyStatus.WasKeyDown); return; }
            else return;
            args.Handled = Navigate(direction);
        }
        private void NativeKeyUp(object sender, KeyRoutedEventArgs args)
        { if (!_editing && GamepadKeyRouting.IsNavigationEvent(args)) args.Handled = true; }
    }
}
