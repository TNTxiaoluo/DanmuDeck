using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Windows.Foundation;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace BiliXbox.Controls
{
    public sealed class ViewportScrollResult
    {
        public int Index { get; internal set; } = -1;
        public object Item { get; internal set; }
        public Control Container { get; internal set; }
        public double Offset { get; internal set; }
    }

    // Right-stick scrolling keeps native focus inside the viewport instead of
    // leaving a selected row off screen for the next D-pad event to restore.
    public static class ViewportFocusScroll
    {
        private sealed class State
        {
            internal ScrollViewer Viewer;
            internal ListViewBase List;
            internal Control FocusTarget;
            internal Action<ViewportScrollResult> Callback;
            internal bool Active, Queued, HasAnchor;
            internal int Generation, Count;
            internal object First, Last;
            internal double Target, AnchorX, AnchorContentY;
        }
        private static readonly ConditionalWeakTable<ScrollViewer, State> States = new ConditionalWeakTable<ScrollViewer, State>();

        public static ScrollViewer FindScrollViewer(DependencyObject root)
        {
            if (root is ScrollViewer direct) return direct;
            if (root == null) return null;
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; ++i)
            {
                var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
                if (found != null) return found;
            }
            return null;
        }

        public static bool Scroll(ListViewBase list, double delta, Action<ViewportScrollResult> reconciled = null, int preferredIndex = -1)
        {
            if (list == null || !IsVisible(list) || list.Items.Count == 0) return false;
            var viewer = FindScrollViewer(list);
            return ScrollCore(viewer, list, delta, reconciled, preferredIndex);
        }

        public static bool Scroll(ScrollViewer viewer, double delta, Action<ViewportScrollResult> reconciled = null)
        {
            return ScrollCore(viewer, null, delta, reconciled, -1);
        }

        private static bool ScrollCore(ScrollViewer viewer, ListViewBase list, double delta, Action<ViewportScrollResult> callback, int preferredIndex)
        {
            if (viewer == null || !IsVisible(viewer) || double.IsNaN(delta) || double.IsInfinity(delta) || Math.Abs(delta) < .01 || viewer.ScrollableHeight <= 0) return false;
            var state = States.GetValue(viewer, CreateState);
            if (!state.Active || !ReferenceEquals(state.List, list) || !SameItems(state))
            {
                state.Active = true; state.List = list; state.Target = viewer.VerticalOffset; state.HasAnchor = false; state.FocusTarget = null;
                state.Count = list?.Items.Count ?? 0;
                state.First = state.Count == 0 ? null : list.Items[0]; state.Last = state.Count == 0 ? null : list.Items[state.Count - 1];
                CaptureAnchor(state, preferredIndex);
            }
            state.Callback = callback;
            state.Target = ViewportScrollPolicy.ClampOffset(state.Target + delta, viewer.ScrollableHeight);
            viewer.ChangeView(null, state.Target, null, true);
            Queue(state);
            return true; // A boundary is still owned by this viewport.
        }

        private static State CreateState(ScrollViewer viewer)
        {
            var state = new State { Viewer = viewer };
            viewer.ViewChanged += (sender, args) => { if (state.Active) Queue(state); };
            viewer.Unloaded += (sender, args) => Stop(state);
            viewer.BringIntoViewRequested += (sender, args) =>
            {
                // UWP can dispatch this after Focus has returned. Retain the
                // most recent scroll-selected target until ordinary navigation
                // cancels ownership, without suppressing other controls.
                if (state.Active && state.FocusTarget != null && SameItems(state) &&
                    IsWithin(args.TargetElement, state.FocusTarget)) args.Handled = true;
            };
            return state;
        }

        private static void CaptureAnchor(State state, int preferredIndex)
        {
            var focused = FocusManager.GetFocusedElement() as DependencyObject;
            Control control = null;
            if (state.List != null)
            {
                int index = preferredIndex;
                for (var node = focused; node != null && node != state.List; node = VisualTreeHelper.GetParent(node))
                    if (node is Control row && state.List.IndexFromContainer(row) >= 0) { control = row; break; }
                if (control == null && index >= 0 && index < state.List.Items.Count) control = state.List.ContainerFromIndex(index) as Control;
            }
            else if (IsWithin(focused, state.Viewer)) control = focused as Control;
            Rect bounds;
            if (control != null && TryBounds(control, state.Viewer, out bounds))
            {
                state.AnchorX = bounds.X + bounds.Width / 2;
                state.AnchorContentY = bounds.Y + bounds.Height / 2 + state.Viewer.VerticalOffset;
                state.HasAnchor = true;
            }
        }

        private static void Queue(State state)
        {
            if (!state.Active || state.Queued) return;
            state.Queued = true; int generation = state.Generation;
            var ignored = state.Viewer.Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
            {
                if (generation != state.Generation) return;
                state.Queued = false;
                if (!state.Active || !IsVisible(state.Viewer) || !SameItems(state)) { Stop(state); return; }
                ReconcileState(state);
            });
        }

        // A caller can flush a pending scroll before interpreting D-pad input.
        public static ViewportScrollResult Reconcile(ListViewBase list)
        {
            var viewer = FindScrollViewer(list); State state;
            if (viewer == null || !States.TryGetValue(viewer, out state) || !state.Active || !ReferenceEquals(state.List, list) || !SameItems(state)) return null;
            return ReconcileState(state);
        }

        private static ViewportScrollResult ReconcileState(State state)
        {
            var viewer = state.Viewer;
            if (!IsVisible(viewer)) return null;
            viewer.UpdateLayout();
            var controls = new List<Control>(); var indices = new List<int>(); var candidates = new List<ViewportScrollCandidate>();
            if (state.List != null)
            {
                // Inspect realized rows only: a long comment history can have
                // thousands of logical items but only a handful of containers.
                CollectRows(state.List.ItemsPanelRoot, state.List, viewer, controls, indices, candidates);
            }
            else CollectControls(viewer.Content as DependencyObject, viewer, controls, indices, candidates);
            double height = viewer.ViewportHeight > 0 ? viewer.ViewportHeight : viewer.ActualHeight;
            double width = viewer.ViewportWidth > 0 ? viewer.ViewportWidth : viewer.ActualWidth;
            double x = state.HasAnchor ? state.AnchorX : width / 2;
            double y = state.HasAnchor ? state.AnchorContentY - viewer.VerticalOffset : height / 2;
            int chosen = ViewportScrollPolicy.Choose(candidates, height, width, x, y);
            var result = new ViewportScrollResult { Offset = viewer.VerticalOffset };
            if (chosen >= 0)
            {
                var control = controls[chosen]; var candidate = candidates[chosen];
                // Focusing an oversized comment must not make ScrollViewer
                // bring its distant top edge back into view.
                state.FocusTarget = control;
                if (!IsWithin(FocusManager.GetFocusedElement() as DependencyObject, control)) control.Focus(FocusState.Keyboard);
                result.Index = indices[chosen]; result.Container = control;
                result.Item = state.List != null && result.Index >= 0 ? state.List.Items[result.Index] : control;
                state.AnchorX = candidate.Left + candidate.Width / 2;
                state.AnchorContentY = candidate.Top + candidate.Height / 2 + viewer.VerticalOffset; state.HasAnchor = true;
            }
            state.Callback?.Invoke(result);
            return result;
        }

        private static void AddCandidate(Control control, int index, ScrollViewer viewer, List<Control> controls, List<int> indices, List<ViewportScrollCandidate> candidates)
        {
            Rect bounds;
            if (control == null || !control.IsEnabled || !control.IsTabStop || !IsVisible(control) || !TryBounds(control, viewer, out bounds)) return;
            controls.Add(control); indices.Add(index);
            candidates.Add(new ViewportScrollCandidate { Left = bounds.X, Top = bounds.Y, Width = bounds.Width, Height = bounds.Height });
        }

        private static void CollectControls(DependencyObject node, ScrollViewer viewer, List<Control> controls, List<int> indices, List<ViewportScrollCandidate> candidates)
        {
            if (node == null || node is ScrollViewer) return;
            var control = node as Control;
            if (control != null && control.IsTabStop && control.IsEnabled)
            { AddCandidate(control, -1, viewer, controls, indices, candidates); return; }
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; ++i) CollectControls(VisualTreeHelper.GetChild(node, i), viewer, controls, indices, candidates);
        }

        private static void CollectRows(DependencyObject node, ListViewBase list, ScrollViewer viewer, List<Control> controls, List<int> indices, List<ViewportScrollCandidate> candidates)
        {
            if (node == null) return;
            var row = node as Control; int index = row == null ? -1 : list.IndexFromContainer(row);
            if (index >= 0) { AddCandidate(row, index, viewer, controls, indices, candidates); return; }
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; ++i) CollectRows(VisualTreeHelper.GetChild(node, i), list, viewer, controls, indices, candidates);
        }

        private static bool TryBounds(FrameworkElement element, ScrollViewer viewer, out Rect bounds)
        {
            bounds = default(Rect);
            if (element.ActualWidth <= 0 || element.ActualHeight <= 0) return false;
            try { bounds = element.TransformToVisual(viewer).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight)); return true; }
            catch (Exception) { return false; } // A replaced virtualized container.
        }
        private static bool SameItems(State state)
        {
            var list = state.List;
            return list == null || (list.Items.Count == state.Count && (state.Count == 0 ||
                ReferenceEquals(state.First, list.Items[0]) && ReferenceEquals(state.Last, list.Items[state.Count - 1])));
        }
        private static bool IsWithin(DependencyObject node, DependencyObject root)
        {
            while (node != null) { if (ReferenceEquals(node, root)) return true; node = VisualTreeHelper.GetParent(node); }
            return false;
        }
        private static bool IsVisible(DependencyObject node)
        {
            while (node != null)
            {
                if (node is UIElement element && element.Visibility != Visibility.Visible) return false;
                node = VisualTreeHelper.GetParent(node);
            }
            return true;
        }
        public static void Cancel(DependencyObject scope)
        {
            if (scope == null) return;
            if (scope is ScrollViewer viewer) { State state; if (States.TryGetValue(viewer, out state)) Stop(state); }
            int count = VisualTreeHelper.GetChildrenCount(scope);
            for (int i = 0; i < count; ++i) Cancel(VisualTreeHelper.GetChild(scope, i));
        }
        private static void Stop(State state)
        { state.Active = state.Queued = state.HasAnchor = false; state.Callback = null; state.FocusTarget = null; ++state.Generation; }
    }

    internal sealed class ViewportScrollCandidate
    { internal double Left, Top, Width, Height; }

    internal static class ViewportScrollPolicy
    {
        internal static double ClampOffset(double offset, double maximum)
        { return Math.Max(0, Math.Min(Math.Max(0, maximum), offset)); }

        internal static int Choose(IReadOnlyList<ViewportScrollCandidate> candidates, double height, double width, double anchorX, double anchorY)
        {
            if (height <= 0 || width <= 0) return -1;
            int best = -1; double score = double.MaxValue;
            for (int i = 0; i < candidates.Count; ++i)
            {
                var row = candidates[i];
                double visible = Math.Min(height, row.Top + row.Height) - Math.Max(0, row.Top);
                double horizontal = Math.Min(width, row.Left + row.Width) - Math.Max(0, row.Left);
                if (row.Width <= 0 || row.Height <= 0 || visible <= 1 || horizontal <= 1) continue;
                bool full = row.Top >= -.5 && row.Top + row.Height <= height + .5;
                // Prefer complete cards, but oversized comments remain usable
                // anywhere in their visible body without snapping to their top.
                double partialPenalty = full || row.Height > height ? 0 : height * 4;
                double centerY = row.Top + row.Height / 2;
                double value = Math.Abs(centerY - Math.Max(0, Math.Min(height, anchorY))) +
                    Math.Abs(row.Left + row.Width / 2 - anchorX) * 2 + partialPenalty;
                if (value < score) { best = i; score = value; }
            }
            return best;
        }
    }
}
