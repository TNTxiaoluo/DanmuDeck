using System;
using System.Collections.Generic;
using System.Threading.Tasks;
namespace Windows.Foundation
{
    public struct Rect
    {
        public double X, Y, Width, Height;
        public Rect(double x, double y, double width, double height) { X = x; Y = y; Width = width; Height = height; }
    }
}
namespace Windows.UI.Core
{
    public enum CoreDispatcherPriority { Low }
    public sealed class FakeDispatcher
    {
        private readonly Queue<Action> _queue = new Queue<Action>();
        public int Pending => _queue.Count;
        public Task RunAsync(CoreDispatcherPriority priority, Action action) { _queue.Enqueue(action); return Task.CompletedTask; }
        public void Drain() { int count = 0; while (_queue.Count > 0) { if (++count > 100) throw new Exception("Unbounded dispatcher queue"); _queue.Dequeue()(); } }
    }
}
namespace Windows.UI.Xaml
{
    public enum Visibility { Visible, Collapsed }
    public enum FocusState { Keyboard }
    public class DependencyObject
    {
        public readonly List<DependencyObject> Children = new List<DependencyObject>();
        public DependencyObject Parent;
        public void Add(DependencyObject value) { Children.Add(value); value.Parent = this; }
    }
    public class UIElement : DependencyObject { public Visibility Visibility; }
    public class FrameworkElement : UIElement
    {
        public double ActualWidth = 100, ActualHeight = 100, X, Y;
        public Windows.UI.Core.FakeDispatcher Dispatcher = new Windows.UI.Core.FakeDispatcher();
        public event EventHandler Unloaded;
        public void RaiseUnloaded() => Unloaded?.Invoke(this, EventArgs.Empty);
        public void UpdateLayout() { }
        public Windows.UI.Xaml.Media.Transform TransformToVisual(FrameworkElement viewer) => new Windows.UI.Xaml.Media.Transform(this, viewer);
    }
    public sealed class BringIntoViewRequestedEventArgs : EventArgs { public bool Handled; public UIElement TargetElement; }
}
namespace Windows.UI.Xaml.Input
{
    public static class FocusManager
    {
        public static object Current;
        public static object GetFocusedElement() => Current;
    }
}
namespace Windows.UI.Xaml.Controls
{
    public class Control : FrameworkElement
    {
        public bool IsEnabled = true, IsTabStop = true;
        public int FocusCount;
        public bool Focus(FocusState state)
        {
            ++FocusCount; Input.FocusManager.Current = this;
            for (DependencyObject node = Parent; node != null; node = node.Parent)
                if (node is ScrollViewer viewer) { viewer.RequestAutoBring(this); break; }
            return true;
        }
    }
    public sealed class ScrollViewer : Control
    {
        public object Content;
        public double VerticalOffset, ScrollableHeight = 700, ViewportHeight = 300, ViewportWidth = 200, Requested;
        public bool Defer, DeferBring;
        private readonly Queue<Control> _pendingBring = new Queue<Control>();
        public int PendingBring => _pendingBring.Count;
        public int ChangeCalls, AutoBringCalls;
        public event EventHandler ViewChanged;
        public event EventHandler<BringIntoViewRequestedEventArgs> BringIntoViewRequested;
        public void ChangeView(double? x, double? y, float? zoom, bool disableAnimation)
        {
            ++ChangeCalls; Requested = y.Value;
            if (!Defer) { VerticalOffset = y.Value; ViewChanged?.Invoke(this, EventArgs.Empty); }
        }
        public void Apply() { VerticalOffset = Requested; ViewChanged?.Invoke(this, EventArgs.Empty); }
        public void RequestAutoBring(Control control) { if (DeferBring) _pendingBring.Enqueue(control); else AutoBring(control); }
        public void FlushBring() { while (_pendingBring.Count > 0) AutoBring(_pendingBring.Dequeue()); }
        public void AutoBring(Control control)
        {
            ++AutoBringCalls; var args = new BringIntoViewRequestedEventArgs { TargetElement = control }; BringIntoViewRequested?.Invoke(this, args);
            if (!args.Handled) ChangeView(null, Math.Max(0, Math.Min(ScrollableHeight, control.Y)), null, true);
        }
    }
    public sealed class ListViewBase : Control
    {
        public readonly List<object> Items = new List<object>();
        public readonly List<Control> Rows = new List<Control>();
        public FrameworkElement ItemsPanelRoot;
        public object ContainerFromIndex(int index) => Rows[index];
        public int IndexFromContainer(Control row) => Rows.IndexOf(row);
    }
}
namespace Windows.UI.Xaml.Media
{
    public static class VisualTreeHelper
    {
        public static DependencyObject GetParent(DependencyObject node) => node.Parent;
        public static int GetChildrenCount(DependencyObject node) => node.Children.Count;
        public static DependencyObject GetChild(DependencyObject node, int index) => node.Children[index];
    }
    public sealed class Transform
    {
        private readonly FrameworkElement _element, _viewer;
        public Transform(FrameworkElement element, FrameworkElement viewer) { _element = element; _viewer = viewer; }
        public Windows.Foundation.Rect TransformBounds(Windows.Foundation.Rect rect)
        {
            var viewer = _viewer as Controls.ScrollViewer;
            return new Windows.Foundation.Rect(_element.X, _element.Y - (viewer?.VerticalOffset ?? 0), rect.Width, rect.Height);
        }
    }
}
namespace BiliXbox.ViewportScrollTests
{
    public static class Tests
    {
        private sealed class Fixture
        {
            public readonly Windows.UI.Xaml.Controls.ListViewBase List = new Windows.UI.Xaml.Controls.ListViewBase();
            public readonly Windows.UI.Xaml.Controls.ScrollViewer Viewer = new Windows.UI.Xaml.Controls.ScrollViewer();
            public Fixture(int count = 20, int columns = 2, double rowHeight = 100)
            {
                List.Add(Viewer); var panel = new Windows.UI.Xaml.FrameworkElement(); Viewer.Add(panel); Viewer.Content = panel; List.ItemsPanelRoot = panel;
                for (int i = 0; i < count; ++i)
                {
                    List.Items.Add(new object()); var control = new Windows.UI.Xaml.Controls.Control { X = i % columns * 100, Y = i / columns * rowHeight, ActualHeight = rowHeight };
                    List.Rows.Add(control); panel.Add(control);
                }
                Windows.UI.Xaml.Input.FocusManager.Current = List.Rows.Count == 0 ? null : List.Rows[0];
            }
        }
        public static void Run()
        {
            int count = 0;
            Action<bool, string> check = (value, name) => { ++count; if (!value) throw new Exception(name); };
            var f = new Fixture(); Controls.ViewportScrollResult result = null;
            check(Controls.ViewportFocusScroll.Scroll(f.List, 160, value => result = value), "Scrollable grid owns wheel input");
            check(f.Viewer.Dispatcher.Pending == 1 && result == null, "Scroll reconciles after layout with a bounded dispatch");
            f.Viewer.Dispatcher.Drain();
            check(f.Viewer.VerticalOffset == 160 && result.Index == 4, "Scrolled viewport moves focus to visible row in the same column");
            check(ReferenceEquals(result.Item, f.List.Items[4]) && ReferenceEquals(result.Container, f.List.Rows[4]), "Callback returns current item and container for navigation bookmarks");
            check(f.Viewer.AutoBringCalls == 1 && f.Viewer.ChangeCalls == 1, "Focus bring-into-view cannot reset scroll position");
            Controls.ViewportFocusScroll.Scroll(f.List, 5); f.Viewer.Dispatcher.Drain();
            check(f.List.Rows[4].FocusCount == 1, "Continuous scrolling does not refocus the same row or restart focus animation");
            result = Controls.ViewportFocusScroll.Reconcile(f.List);
            check(result.Index == 4 && result.Offset == 165, "D-pad can synchronously flush the latest viewport selection");
            Controls.ViewportFocusScroll.Scroll(f.List, 200); Controls.ViewportFocusScroll.Cancel(f.List); f.Viewer.Dispatcher.Drain();
            check(f.List.Rows[4].FocusCount == 1, "Cancelled scroll cannot restore a stale focus after D-pad navigation");
            f = new Fixture(); Windows.UI.Xaml.Input.FocusManager.Current = f.List.Rows[1];
            Controls.ViewportFocusScroll.Scroll(f.List, 160, value => result = value); f.Viewer.Dispatcher.Drain();
            check(result.Index == 5, "Right-hand column is preserved while scrolling a virtualized grid");
            f = new Fixture();
            for (int i = 0; i < 50; ++i) Controls.ViewportFocusScroll.Scroll(f.List, 2);
            check(f.Viewer.Dispatcher.Pending == 1, "Rapid sampling coalesces all pending focus work");
            f.Viewer.Dispatcher.Drain(); check(f.Viewer.VerticalOffset == 100, "Continuous deltas accumulate once each");
            Controls.ViewportFocusScroll.Scroll(f.List, -10000); f.Viewer.Dispatcher.Drain();
            check(f.Viewer.VerticalOffset == 0, "Upward scroll clamps to top");
            check(Controls.ViewportFocusScroll.Scroll(f.List, -20), "Top boundary still handles right-stick input"); f.Viewer.Dispatcher.Drain();
            Controls.ViewportFocusScroll.Scroll(f.List, 10000); f.Viewer.Dispatcher.Drain();
            check(f.Viewer.VerticalOffset == 700, "Downward scroll clamps to bottom");
            f = new Fixture(); f.Viewer.Defer = true;
            Controls.ViewportFocusScroll.Scroll(f.List, 50); Controls.ViewportFocusScroll.Scroll(f.List, 50);
            check(f.Viewer.VerticalOffset == 0 && f.Viewer.Requested == 100, "Delayed native scrolling accumulates from requested target instead of stale offset");
            f.Viewer.Apply(); f.Viewer.Dispatcher.Drain();
            check(f.Viewer.VerticalOffset == 100, "ViewChanged reconciles the actual final native position");
            f = new Fixture(); for (int i = 0; i < 5000; ++i) f.List.Items.Add(new object());
            Controls.ViewportFocusScroll.Scroll(f.List, 160, value => result = value); f.Viewer.Dispatcher.Drain();
            check(result.Index == 4, "Thousands of unrealized comments are not queried for unavailable containers on each sample");
            f = new Fixture(); int callbacks = 0; Controls.ViewportFocusScroll.Scroll(f.List, 100, value => ++callbacks);
            f.List.Items[0] = new object(); f.Viewer.Dispatcher.Drain();
            check(callbacks == 0 && f.List.Rows[0].FocusCount == 0, "Page replacement rejects pending old-page focus");
            f = new Fixture(); Controls.ViewportFocusScroll.Scroll(f.List, 100, value => ++callbacks);
            f.List.Visibility = Windows.UI.Xaml.Visibility.Collapsed; f.Viewer.Dispatcher.Drain();
            check(callbacks == 0, "Hidden section cannot take focus after transition");
            f = new Fixture(); Controls.ViewportFocusScroll.Scroll(f.List, 100, value => ++callbacks); f.Viewer.RaiseUnloaded(); f.Viewer.Dispatcher.Drain();
            check(callbacks == 0, "Unloaded viewport invalidates delayed focus");
            f = new Fixture(2, 1, 1400); f.Viewer.ScrollableHeight = 2500;
            Controls.ViewportFocusScroll.Scroll(f.List, 600, value => result = value); f.Viewer.Dispatcher.Drain();
            check(result.Index == 0 && f.Viewer.VerticalOffset == 600, "Oversized comment stays scrollable through its body without snapping to its top");
            f = new Fixture(2, 1, 1400); f.Viewer.ScrollableHeight = 2500; f.Viewer.DeferBring = true;
            Windows.UI.Xaml.Input.FocusManager.Current = null;
            Controls.ViewportFocusScroll.Scroll(f.List, 600, value => result = value); f.Viewer.Dispatcher.Drain();
            check(f.Viewer.PendingBring == 1 && f.Viewer.VerticalOffset == 600, "Native focus can defer its bring-into-view request until after focus callback returns");
            f.Viewer.FlushBring();
            check(f.Viewer.VerticalOffset == 600 && f.Viewer.ChangeCalls == 1, "Delayed native bring-into-view cannot snap a scroll-selected long comment back to top");
            var child = new Windows.UI.Xaml.Controls.Control(); f.List.Rows[0].Add(child); f.Viewer.AutoBring(child);
            check(f.Viewer.VerticalOffset == 600, "Delayed bring-into-view for a descendant of the scroll-selected target is also suppressed");
            f.Viewer.AutoBring(f.List.Rows[1]);
            check(f.Viewer.VerticalOffset == 1400, "Scroll ownership does not suppress bring-into-view for unrelated controls");
            Controls.ViewportFocusScroll.Cancel(f.List); f.Viewer.AutoBring(f.List.Rows[0]);
            check(f.Viewer.VerticalOffset == 0, "Direction cancellation releases suppression before ordinary native focus movement");
            f = new Fixture(); f.List.Rows[4].IsEnabled = false;
            Controls.ViewportFocusScroll.Scroll(f.List, 160, value => result = value); f.Viewer.Dispatcher.Drain();
            check(result.Index != 4 && result.Index >= 0, "Disabled rows cannot receive scrolling focus");
            check(!Controls.ViewportFocusScroll.Scroll(f.List, double.NaN) && !Controls.ViewportFocusScroll.Scroll(f.List, double.PositiveInfinity), "Invalid analog deltas are rejected");
            f.Viewer.ScrollableHeight = 0; check(!Controls.ViewportFocusScroll.Scroll(f.List, 20), "Non-scrollable view is not falsely consumed");
            f = new Fixture(); var button = f.List.Rows[0]; Windows.UI.Xaml.Input.FocusManager.Current = button;
            Controls.ViewportFocusScroll.Scroll(f.Viewer, 160, value => result = value); f.Viewer.Dispatcher.Drain();
            check(result.Index == -1 && result.Container != null && f.Viewer.VerticalOffset == 160, "General settings viewport chooses visible controls without a list index");
            f = new Fixture(); result = null;
            check(Controls.ViewportFocusScroll.Restore(f.List, 240, 9, value => result = value), "Cached page can request its exact scroll offset and selected column");
            f.Viewer.Dispatcher.Drain();
            check(f.Viewer.VerticalOffset == 240 && result.Index == 9, "Cached page restores its selected card without snapping the viewport to its top edge");
            check(f.Viewer.ChangeCalls == 1, "Native focus cannot issue a second scroll while restoring a cached page");
            int restores = 0; f = new Fixture();
            Controls.ViewportFocusScroll.Restore(f.List, 240, 9, value => ++restores);
            Controls.ViewportFocusScroll.Cancel(f.List); f.Viewer.Dispatcher.Drain();
            check(restores == 0, "Switching away before cached layout completes rejects stale focus restoration");
            f = new Fixture(); Controls.ViewportFocusScroll.Restore(f.List, 240, 9, value => ++restores);
            f.List.Items[0] = new object(); f.Viewer.Dispatcher.Drain();
            check(restores == 0, "An asynchronously replaced page cannot receive cached-page focus");
            f = new Fixture(); Controls.ViewportFocusScroll.Restore(f.List, 240, 0, value => result = value); f.Viewer.Dispatcher.Drain();
            check(result.Index >= 6 && result.Index <= 9 && f.Viewer.VerticalOffset == 240,
                "An offscreen saved selection is reconciled to a visible row without moving the saved viewport");
            var choices = new List<Controls.ViewportScrollCandidate> {
                new Controls.ViewportScrollCandidate { Left = 0, Top = -50, Width = 100, Height = 100 },
                new Controls.ViewportScrollCandidate { Left = 0, Top = 50, Width = 100, Height = 100 } };
            check(Controls.ViewportScrollPolicy.Choose(choices, 300, 200, 50, 0) == 1, "Complete card is preferred to clipped card at the viewport edge");
            check(Controls.ViewportScrollPolicy.Choose(choices, 0, 200, 50, 0) == -1, "Unmeasured viewport cannot select a container");
            Console.WriteLine("PASS: " + count + " production viewport scrolling/focus assertions (offline UWP substitutes).");
        }
    }
}
