using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

// Run actual scrolling and drawer methods with external layout/input substitutes.
// No sockets, credentials, content submission or gift endpoints are involved.
namespace Windows.Foundation
{
    public struct Rect
    {
        public double X, Y, Width, Height;
        public double Top => Y;
        public double Bottom => Y + Height;
        public Rect(double x, double y, double width, double height) { X = x; Y = y; Width = width; Height = height; }
    }
}
namespace Windows.UI.Core { public enum CoreDispatcherPriority { Low } }
namespace Windows.UI.Xaml
{
    public enum Visibility { Visible, Collapsed }
    public enum FocusState { Keyboard }
    public class DependencyObject { public DependencyObject Parent; }
    public sealed class BringIntoViewRequestedEventArgs : EventArgs { public bool Handled; public DependencyObject TargetElement; }
}
namespace Windows.UI.Xaml.Input { }
namespace Windows.UI.Xaml.Controls
{
    public sealed class FakeTransform
    {
        private readonly Control _row; private readonly ScrollViewer _viewer;
        public FakeTransform(Control row, ScrollViewer viewer) { _row = row; _viewer = viewer; }
        public Rect TransformBounds(Rect bounds) => new Rect(bounds.X, _row.Top - _viewer.VerticalOffset, bounds.Width, bounds.Height);
    }
    public class Control : DependencyObject
    {
        public bool IsEnabled = true;
        public Visibility Visibility = Visibility.Visible;
        public double ActualWidth = 200, ActualHeight = 80, Top;
        public int FocusCount;
        public bool DelayBring;
        private readonly Queue<Action> _delayedBring = new Queue<Action>();
        public ScrollViewer ScrollingAncestor;
        public Action<BringIntoViewRequestedEventArgs> Bringing;
        public FakeTransform TransformToVisual(ScrollViewer scroll) => new FakeTransform(this, scroll);
        public bool Focus(FocusState state)
        {
            ++FocusCount; FocusManager.Current = this;
            if (ScrollingAncestor != null)
            {
                Action request = () =>
                {
                    var args = new BringIntoViewRequestedEventArgs { TargetElement = this }; Bringing?.Invoke(args);
                    if (!args.Handled) ScrollingAncestor.ChangeView(null, Top, null, true);
                };
                if (DelayBring) _delayedBring.Enqueue(request); else request();
            }
            return true;
        }
        public void DeliverBring() { while (_delayedBring.Count > 0) _delayedBring.Dequeue()(); }
    }
    public class Button : Control { }
    public class ItemsControl : Control
    {
        public readonly List<object> Items = new List<object>();
        public readonly List<Control> Containers = new List<Control>();
        public ScrollViewer Viewer;
        public object ContainerFromIndex(int index) => Containers[index];
    }
    public sealed class GridView : ItemsControl { }
    public sealed class ListView : ItemsControl
    {
        public int FollowCalls;
        public object LastFollowed;
        public void ScrollIntoView(object item) { ++FollowCalls; LastFollowed = item; Viewer.ChangeView(null, Viewer.ScrollableHeight, null, true); }
    }
    public sealed class StackPanel : Control { public readonly List<Control> Children = new List<Control>(); }
    public sealed class ScrollViewerViewChangedEventArgs : EventArgs { public bool IsIntermediate; }
    public sealed class ScrollViewer : Control
    {
        public double VerticalOffset, ScrollableHeight = 320, ViewportHeight = 160;
        public int Changes;
        public bool DeferChanges;
        private double? _deferredOffset;
        public event EventHandler<ScrollViewerViewChangedEventArgs> ViewChanged;
        public bool ChangeView(double? horizontal, double? vertical, float? zoom, bool noAnimation)
        {
            ++Changes; double target = Math.Max(0, Math.Min(ScrollableHeight, vertical ?? VerticalOffset));
            if (DeferChanges) { _deferredOffset = target; return true; }
            VerticalOffset = target; ViewChanged?.Invoke(this, new ScrollViewerViewChangedEventArgs()); return true;
        }
        public void Settle()
        { if (_deferredOffset.HasValue) { VerticalOffset = _deferredOffset.Value; _deferredOffset = null; ViewChanged?.Invoke(this, new ScrollViewerViewChangedEventArgs()); } }
        public void UpdateLayout() { }
        public void Intermediate() => ViewChanged?.Invoke(this, new ScrollViewerViewChangedEventArgs { IsIntermediate = true });
    }
    public static class FocusManager { public static Control Current; }
}
namespace BiliXbox.Controls
{
    public enum PlayerDirection { Left, Right, Up, Down }
    public sealed class FakeDispatcher
    {
        private readonly Queue<Action> _queue = new Queue<Action>();
        public int Pending => _queue.Count;
        public Task RunAsync(CoreDispatcherPriority priority, Action callback)
        { _queue.Enqueue(callback); return Task.CompletedTask; }
        public void Drain() { while (_queue.Count > 0) _queue.Dequeue()(); }
    }
    public sealed partial class LiveHubView
    {
        private enum Pane { None, Chat, Gifts, GiftAmount, GiftConfirm, Quality }
        private int _focusGeneration, _paneGeneration, _roomGeneration = 1, _selectedIndex;
        private bool _editing, IsPlayingRoom;
        private Visibility Visibility = Visibility.Visible;
        private Pane _pane;
        private Control _lastFocus;
        private readonly GridView _rooms = new GridView();
        private readonly ListView _messages = new ListView();
        private readonly ScrollViewer _drawerScroll = new ScrollViewer();
        private readonly StackPanel _drawerBody = new StackPanel();
        private readonly Control _closeDrawer = new Button(), _reconnectChat = new Button(), _chatInput = new Control(), _send = new Button();
        private readonly FakeDispatcher Dispatcher = new FakeDispatcher();
        private int RoomColumns() => 4;
        private bool RoomCurrent(int generation) => IsPlayingRoom && generation == _roomGeneration;
        private void WakeChrome() { }
        private void Focus(Control control) { ++_focusGeneration; _lastFocus = control; control.Focus(FocusState.Keyboard); }
        private static bool IsWithin(DependencyObject child, DependencyObject parent)
        { while (child != null) { if (ReferenceEquals(child, parent)) return true; child = child.Parent; } return false; }
        private static T FindChild<T>(DependencyObject root) where T : DependencyObject
        { return (root as ItemsControl)?.Viewer as T; }

        private static LiveHubView Rooms()
        {
            var view = new LiveHubView(); view._rooms.Viewer = new ScrollViewer();
            for (int i = 0; i < 24; ++i)
            {
                var row = new Control { Top = i / 4 * 80, Parent = view._rooms, ScrollingAncestor = view._rooms.Viewer };
                row.Bringing = args => view.SuppressManualFocusBringIntoView(row, args);
                view._rooms.Items.Add(new object()); view._rooms.Containers.Add(row);
            }
            return view;
        }
        private static LiveHubView Chat()
        {
            var view = new LiveHubView { IsPlayingRoom = true, _pane = Pane.Chat };
            view._messages.Viewer = new ScrollViewer { ScrollableHeight = 320, VerticalOffset = 320 };
            for (int i = 0; i < 6; ++i)
            {
                var row = new Control { Top = i * 80, Parent = view._messages, ScrollingAncestor = view._messages.Viewer };
                row.Bringing = args => view.SuppressManualFocusBringIntoView(row, args);
                view._messages.Items.Add(new object()); view._messages.Containers.Add(row);
            }
            view.EnsureChatScroll(); return view;
        }

        public static void RunScrollingTests()
        {
            int checks = 0;
            Action<bool, string> check = (value, name) => { ++checks; if (!value) throw new Exception(name); };
            var view = Rooms();
            foreach (double value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, 0, .001 })
                check(!view.ScrollBy(value) && view._rooms.Viewer.Changes == 0, "Invalid/noise movement leaves viewport alone");
            view._editing = true; check(!view.ScrollBy(80), "Native editor owns all stick input");
            view._editing = false; view.Visibility = Visibility.Collapsed; check(!view.ScrollBy(80), "Hidden live view does not scroll");
            view = Rooms(); check(view.ScrollBy(80), "Browse accepts positive right-stick movement");
            view.Dispatcher.Drain();
            check(view._rooms.Viewer.VerticalOffset == 80 && view._selectedIndex == 4, "Scrolled room focus and selected index move to a visible row");
            check(ReferenceEquals(view._lastFocus, view._rooms.Containers[4]) && ReferenceEquals(FocusManager.Current, view._lastFocus), "Actual native focus follows selected visible room");
            view.ScrollBy(80); view.Dispatcher.Drain();
            check(view._selectedIndex == 8 && view._rooms.Viewer.VerticalOffset == 160, "A second scroll starts from the new viewport rather than the old card");
            view._selectedIndex = 11; view.ScrollBy(80); view.Dispatcher.Drain();
            check(view._selectedIndex == 15, "Viewport anchoring preserves the horizontal card column");
            view.ScrollBy(9000); view.Dispatcher.Drain();
            check(view._rooms.Viewer.VerticalOffset == 320 && view._selectedIndex == 19, "Bottom is clamped and its visible room becomes navigation anchor");
            view.ScrollBy(-9000); view.Dispatcher.Drain();
            check(view._rooms.Viewer.VerticalOffset == 0 && view._selectedIndex == 3, "Top clamps without stale previous-page focus");
            view = Rooms(); view.ScrollBy(80); view.ScrollBy(80); view.Dispatcher.Drain();
            check(view._rooms.Containers.Sum(row => row.FocusCount) == 1 && view._selectedIndex == 8, "New manual movement invalidates stale queued viewport focus");
            view = Rooms(); view.ScrollBy(80); int pendingGeneration = view._focusGeneration; view.ReconcileScrollFocus();
            check(view._selectedIndex == 4 && view._focusGeneration > pendingGeneration, "Immediate D-pad/A reconciles the new visible room before using its index");
            view.Dispatcher.Drain(); view.ReconcileScrollFocus();
            check(view._rooms.Containers.Sum(row => row.FocusCount) == 1, "Immediate navigation retires delayed layout focus instead of restoring its old selection");
            view = Rooms(); view._rooms.Viewer.DeferChanges = true; view.ScrollBy(80); view.Dispatcher.Drain();
            view._rooms.Viewer.Settle(); view.Dispatcher.Drain();
            check(view._selectedIndex == 4 && view._rooms.Viewer.VerticalOffset == 80, "A late native ChangeView completion reanchors focus to the final viewport");
            view = Rooms(); view._rooms.Viewer.DeferChanges = true;
            view.ScrollBy(40); view.ScrollBy(40); view.ScrollBy(40); view.Dispatcher.Drain();
            view._rooms.Viewer.Settle(); view.Dispatcher.Drain();
            check(view._rooms.Viewer.VerticalOffset == 120 && view._selectedIndex == 8, "Continuous stick samples accumulate their full requested distance before native offsets settle");
            view = Rooms(); view._rooms.Viewer.DeferChanges = true;
            view.ScrollBy(80); view.ScrollBy(-20); view.ScrollBy(-20); view._rooms.Viewer.Settle(); view.Dispatcher.Drain();
            check(view._rooms.Viewer.VerticalOffset == 40, "Reversing the stick subtracts from the pending target rather than an old native offset");
            view.ScrollBy(9000); view.ScrollBy(-10); view._rooms.Viewer.Settle(); view.Dispatcher.Drain();
            check(view._rooms.Viewer.VerticalOffset == 310, "Accumulated targets stay bounded and reverse immediately from the boundary");
            view.ReconcileScrollFocus(); view._rooms.Viewer.VerticalOffset = 80; view.ScrollBy(20); view._rooms.Viewer.Settle(); view.Dispatcher.Drain();
            check(view._rooms.Viewer.VerticalOffset == 100, "Navigation reconciliation resets the pending target for a new manual session");
            view = Rooms(); view._rooms.Viewer.DeferChanges = true; view.ScrollBy(80); ++view._roomGeneration;
            view.ScrollBy(20); view._rooms.Viewer.Settle(); view.Dispatcher.Drain();
            check(view._rooms.Viewer.VerticalOffset == 20, "A new room generation cannot inherit a previous viewport's pending target");
            view = Rooms(); foreach (var row in view._rooms.Containers) row.DelayBring = true;
            view.ScrollBy(120); view.Dispatcher.Drain(); var manuallyFocused = view._lastFocus;
            manuallyFocused.DeliverBring();
            check(view._rooms.Viewer.VerticalOffset == 120, "Late native bring-into-view from the manually focused row cannot undo scrolling");
            var childTarget = new Control { Parent = manuallyFocused };
            var childBring = new BringIntoViewRequestedEventArgs { TargetElement = childTarget };
            view.SuppressManualFocusBringIntoView(view._rooms, childBring);
            check(childBring.Handled, "A delayed bring from the focused card's child is scoped to the owned card");
            var unrelatedBring = new BringIntoViewRequestedEventArgs { TargetElement = new Control() };
            view.SuppressManualFocusBringIntoView(view._rooms, unrelatedBring);
            check(!unrelatedBring.Handled, "An unrelated control's bring-into-view remains available");
            view.ScrollBy(20); manuallyFocused.Focus(FocusState.Keyboard); manuallyFocused.DeliverBring();
            check(view._rooms.Viewer.VerticalOffset == 140, "Another right-stick sample preserves suppression until its new viewport focus is installed");
            view.Dispatcher.Drain(); view.ReconcileScrollFocus();
            var retiredBring = new BringIntoViewRequestedEventArgs { TargetElement = view._lastFocus };
            view.SuppressManualFocusBringIntoView(view._rooms, retiredBring);
            check(!retiredBring.Handled, "Directional navigation retires manual-focus suppression");
            view = Rooms(); view.ScrollBy(120); view.Dispatcher.Drain(); view._editing = true;
            var editorBring = new BringIntoViewRequestedEventArgs { TargetElement = view._lastFocus };
            view.SuppressManualFocusBringIntoView(view._rooms, editorBring);
            check(!editorBring.Handled, "Native editing owns bring-into-view after leaving manual scroll focus");
            view = Rooms(); view._rooms.Viewer.DeferChanges = true; view.ScrollBy(80); view.Dispatcher.Drain();
            ++view._focusGeneration; view._rooms.Viewer.Settle(); view.Dispatcher.Drain();
            check(view._selectedIndex == 0 && view._rooms.Containers.Sum(row => row.FocusCount) == 1, "A later direction/selection owns focus ahead of delayed scroll completion");
            foreach (int invalidation in Enumerable.Range(0, 4))
            {
                view = Rooms(); view.ScrollBy(80);
                if (invalidation == 0) view._editing = true;
                else if (invalidation == 1) ++view._roomGeneration;
                else if (invalidation == 2) ++view._paneGeneration;
                else view.Visibility = Visibility.Collapsed;
                view.Dispatcher.Drain();
                check(view._rooms.Containers.Sum(row => row.FocusCount) == 0, "Changed owner/editor/page rejects delayed focus");
            }
            view = Rooms(); view.IsPlayingRoom = true;
            check(!view.ScrollBy(80), "Playing without a drawer leaves video controls alone");
            view = Chat(); view.FollowLatestMessage();
            check(view.Dispatcher.Pending == 1, "Following latest batches one UI callback");
            view.ScrollBy(-80); view.Dispatcher.Drain();
            check(!view._chatFollowLatest && view._messages.FollowCalls == 0 && view._messages.Viewer.VerticalOffset == 240, "Manual upward scroll cancels a previously queued jump to latest");
            view._messages.Viewer.ScrollableHeight += 80; view.FollowLatestMessage(); view.Dispatcher.Drain();
            check(view._messages.Viewer.VerticalOffset == 240 && view._messages.FollowCalls == 0, "Incoming messages preserve older-reading viewport");
            check(IsWithin(view._lastFocus, view._messages), "Chat viewport gets native message focus after scrolling");
            view.ScrollBy(80); view.Dispatcher.Drain(); check(!view._chatFollowLatest, "Partial downward scrolling keeps older-reading mode");
            view.ScrollBy(80); view.Dispatcher.Drain(); check(view._chatFollowLatest, "Returning to the bottom restores live following");
            view._messages.Viewer.ScrollableHeight += 80;
            for (int i = 0; i < 48; ++i) view.FollowLatestMessage();
            check(view.Dispatcher.Pending == 1, "A large message batch schedules a single latest-message scroll");
            view.Dispatcher.Drain();
            check(view._messages.Viewer.VerticalOffset == 480 && view._messages.FollowCalls == 1, "New message follows only after reaching bottom again");
            check(ReferenceEquals(view._messages.LastFollowed, view._messages.Items.Last()), "Following targets latest server message");
            view = Chat(); view.ScrollBy(-80); view.Dispatcher.Drain();
            view._messages.Viewer.VerticalOffset = view._messages.Viewer.ScrollableHeight; view._messages.Viewer.Intermediate();
            check(!view._chatFollowLatest, "Intermediate view updates cannot prematurely restore auto-follow");
            view._messages.Viewer.ChangeView(null, 320, null, true);
            check(view._chatFollowLatest, "Completed scrollbar movement to bottom also restores auto-follow");
            view = Chat(); view.FollowLatestMessage(); view.ResetChatScroll(); ++view._roomGeneration; view.Dispatcher.Drain();
            check(view._messages.FollowCalls == 0 && view._chatFollowLatest, "A departed room cannot run an old latest-message callback");
            view = Chat(); view.NavigateDrawer(view._messages, PlayerDirection.Up); view.Dispatcher.Drain();
            check(view._messages.Viewer.VerticalOffset == 232 && !view._chatFollowLatest, "Direction up reads earlier messages instead of immediately leaving chat");
            view._messages.Viewer.VerticalOffset = 0; view.NavigateDrawer(view._lastFocus, PlayerDirection.Up);
            check(ReferenceEquals(FocusManager.Current, view._closeDrawer), "Chat top boundary reaches close control");
            view.NavigateDrawer(view._closeDrawer, PlayerDirection.Down); view.Dispatcher.Drain();
            check(IsWithin(FocusManager.Current, view._messages) && view._messages.Viewer.VerticalOffset == 0, "Returning from close preserves chat viewport");
            view.NavigateDrawer(FocusManager.Current, PlayerDirection.Left);
            check(ReferenceEquals(FocusManager.Current, view._chatInput), "Left provides immediate existing inline composer access");
            view._messages.Viewer.VerticalOffset = 320; view.NavigateDrawer(view._messages, PlayerDirection.Down);
            check(ReferenceEquals(FocusManager.Current, view._reconnectChat), "Chat bottom boundary reaches composer controls");
            view = Chat(); view._pane = Pane.Gifts;
            var longRow = new Button { Top = 0, ActualHeight = 400, ScrollingAncestor = view._drawerScroll };
            longRow.Bringing = args => view.SuppressManualFocusBringIntoView(longRow, args); view._drawerBody.Children.Add(longRow);
            view.ScrollBy(100); view.Dispatcher.Drain();
            check(view._drawerScroll.VerticalOffset == 100 && ReferenceEquals(FocusManager.Current, longRow), "Tall drawer focus cannot undo manual right-stick scroll");
            check(!view._synchronizingScrollFocus, "Focus-scroll suppression always releases after focus updates");
            Console.WriteLine("PASS: " + checks + " production live right-stick/chat scrolling and viewport focus assertions (offline layout substitutes).");
        }
    }
}
