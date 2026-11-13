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
    public sealed partial class AccountLibraryView
    {
        public bool ScrollBy(double delta)
        {
            if (_busy || _state.Pane == LibraryPane.Overview || Visibility != Visibility.Visible) return false;
            ++_focusGeneration; _pendingList = null; _pendingIndex = -1;
            var list = ActiveList; var state = _state;
            return ViewportFocusScroll.Scroll(list, delta, result =>
            {
                if (ReferenceEquals(state, _state) && ReferenceEquals(list, ActiveList) && Visibility == Visibility.Visible && result.Index >= 0)
                { _state.SelectedIndex = result.Index; _lastFocus = result.Container; }
            }, _state.SelectedIndex);
        }

        public void RestoreFocus()
        {
            if (!EnsureSession()) { Focus(_back); return; }
            if (_state.NeedsRefresh)
            {
                _state.NeedsRefresh = false;
                Run(RefreshAsync);
                return;
            }
            Render();
            if (_state.Pane == LibraryPane.Overview)
            {
                var sections = new Control[] { _history, _favorites, _follows };
                Focus(IsAvailable(_lastFocus) ? _lastFocus : sections[Math.Max(0, Math.Min(2, _state.SelectedIndex))]); return;
            }
            if (ActiveList.Items.Count > 0) FocusItem(ActiveList, Math.Min(_state.SelectedIndex, ActiveList.Items.Count - 1));
            else FocusFirst();
        }

        internal void Navigate(PlayerDirection direction)
        {
            ViewportFocusScroll.Reconcile(ActiveList); ViewportFocusScroll.Cancel(this);
            var current = FocusManager.GetFocusedElement() as Control;
            var list = ActiveList; int index = ListIndex(list, current);
            if (index >= 0 && _state.Pane != LibraryPane.Overview)
            {
                int columns = Columns(list), target = index;
                if (direction == PlayerDirection.Left) target = Math.Max(index / columns * columns, index - 1);
                else if (direction == PlayerDirection.Right) target = Math.Min(Math.Min(list.Items.Count - 1, (index / columns + 1) * columns - 1), index + 1);
                else if (direction == PlayerDirection.Up)
                {
                    if (index < columns) { Focus(_state.Pane == LibraryPane.Follows ? _anime : _refresh); return; }
                    target = index - columns;
                }
                else if (index + columns >= list.Items.Count)
                { Focus(_next.IsEnabled ? _next : _previous.IsEnabled ? _previous : _refresh); return; }
                else target = index + columns;
                FocusItem(list, target); return;
            }
            var sections = new Control[] { _history, _favorites, _follows };
            int sectionIndex = Array.IndexOf(sections, current);
            if (sectionIndex >= 0 && _state.Pane == LibraryPane.Overview)
            {
                if (direction == PlayerDirection.Up) Focus(_back);
                else if (direction == PlayerDirection.Left || direction == PlayerDirection.Right)
                    Focus(sections[Math.Max(0, Math.Min(2, sectionIndex + (direction == PlayerDirection.Left ? -1 : 1)))]);
                return;
            }
            if (ReferenceEquals(current, _back) || ReferenceEquals(current, _signOut))
            {
                if (direction == PlayerDirection.Left) Focus(_back);
                else if (direction == PlayerDirection.Right) Focus(_signOut);
                else if (direction == PlayerDirection.Down)
                {
                    if (_state.Pane == LibraryPane.Overview) Focus(_history);
                    else if (_state.Pane == LibraryPane.Follows) Focus(_anime);
                    else if (!_busy) FocusFirst();
                }
                return;
            }
            var tabs = new List<Control>();
            if (_state.Pane == LibraryPane.Follows) { tabs.Add(_anime); tabs.Add(_drama); }
            tabs.Add(_refresh); tabs = tabs.Where(IsAvailable).ToList();
            int tabIndex = tabs.IndexOf(current);
            if (tabIndex >= 0)
            {
                if (direction == PlayerDirection.Up) Focus(_back);
                else if (direction == PlayerDirection.Down) FocusFirst();
                else Focus(tabs[Math.Max(0, Math.Min(tabs.Count - 1, tabIndex + (direction == PlayerDirection.Left ? -1 : 1)))]);
                return;
            }
            var pager = new Control[] { _previous, _next }.Where(IsAvailable).ToList();
            int pagerIndex = pager.IndexOf(current);
            if (pagerIndex >= 0)
            {
                if (direction == PlayerDirection.Up)
                {
                    if (list.Items.Count > 0) FocusItem(list, Math.Max(0, list.Items.Count - Columns(list)));
                    else Focus(_refresh);
                }
                else if (direction == PlayerDirection.Left || direction == PlayerDirection.Right)
                    Focus(pager[Math.Max(0, Math.Min(pager.Count - 1, pagerIndex + (direction == PlayerDirection.Left ? -1 : 1)))]);
                return;
            }
            if (_state.Pane == LibraryPane.Overview) Focus(_history); else FocusFirst();
        }

        internal void Confirm(bool repeat)
        {
            if (repeat || _openingItem) return;
            var current = FocusManager.GetFocusedElement() as Control;
            int index = ListIndex(ActiveList, current);
            if (_state.Pane != LibraryPane.Overview && index >= 0 && index < ActiveList.Items.Count)
            {
                if (_state.Pane == LibraryPane.Folders) OpenFolder(ActiveList.Items[index] as Models.FavoriteFolder);
                else Run(() => OpenItemAsync(ActiveList.Items[index] as Models.AccountLibraryItem));
                return;
            }
            if (ReferenceEquals(current, _back)) { if (!Back()) CloseRequested?.Invoke(this, EventArgs.Empty); }
            else if (ReferenceEquals(current, _signOut) && _signOut.IsEnabled) SignOutRequested?.Invoke(this, EventArgs.Empty);
            else if (ReferenceEquals(current, _history) && _history.IsEnabled) OpenSection(LibraryPane.History);
            else if (ReferenceEquals(current, _favorites) && _favorites.IsEnabled) OpenSection(LibraryPane.Folders);
            else if (ReferenceEquals(current, _follows) && _follows.IsEnabled) OpenSection(LibraryPane.Follows);
            else if (ReferenceEquals(current, _anime) && _anime.IsEnabled) SwitchFollows(false);
            else if (ReferenceEquals(current, _drama) && _drama.IsEnabled) SwitchFollows(true);
            else if (ReferenceEquals(current, _refresh) && _refresh.IsEnabled) Run(RefreshAsync);
            else if (ReferenceEquals(current, _previous) && _previous.IsEnabled) Run(() => ChangePageAsync(-1));
            else if (ReferenceEquals(current, _next) && _next.IsEnabled) Run(() => ChangePageAsync(1));
        }

        private void NativeKeyDown(object sender, KeyRoutedEventArgs args)
        {
            // MainPage's raw sampler owns mapped D-pad/Space events too.
            // Their keyboard aliases must not move or activate a second time.
            if (GamepadKeyRouting.IsNavigationEvent(args)) { args.Handled = true; return; }
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

        private void NativeKeyUp(object sender, KeyRoutedEventArgs args)
        {
            if (GamepadKeyRouting.IsNavigationEvent(args)) args.Handled = true;
        }

        private void RememberSelection()
        {
            if (_state.Pane == LibraryPane.Overview)
            {
                var current = FocusManager.GetFocusedElement() as Control;
                int section = Array.IndexOf(new Control[] { _history, _favorites, _follows }, current);
                if (section >= 0) _state.SelectedIndex = section;
                return;
            }
            int index = ListIndex(ActiveList, FocusManager.GetFocusedElement() as Control);
            if (index >= 0) _state.SelectedIndex = index;
        }

        private void FocusFirst()
        {
            if (!_busy && ActiveList.Items.Count > 0) FocusItem(ActiveList, 0);
            // A history page can contain only unsupported live/article entries
            // while still carrying a valid continuation. Keep Next reachable.
            else if (!_busy && _pager.Visibility == Visibility.Visible && _next.IsEnabled) Focus(_next);
            else Focus(_refresh.IsEnabled ? _refresh : _back);
        }

        private void Focus(Control target)
        {
            if (!IsAvailable(target)) { target = _back; if (!IsAvailable(target)) return; }
            ViewportFocusScroll.Cancel(this);
            ++_focusGeneration; _pendingList = null; _pendingIndex = -1;
            if (target.Focus(FocusState.Keyboard)) { _lastFocus = target; target.StartBringIntoView(); }
        }

        private async void FocusItem(ListViewBase list, int index)
        {
            if (index < 0 || index >= list.Items.Count || _busy || !ReferenceEquals(list, ActiveList)) return;
            ViewportFocusScroll.Cancel(this);
            int generation = ++_focusGeneration; _pendingList = list; _pendingIndex = index;
            var state = _state; object item = list.Items[index]; _state.SelectedIndex = index;
            try
            {
                list.ScrollIntoView(item, ScrollIntoViewAlignment.Default);
                for (int attempt = 0; attempt < 3; ++attempt)
                {
                    bool focused = false;
                    await Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                    {
                        if (generation != _focusGeneration || !ReferenceEquals(state, _state) || !ReferenceEquals(list, ActiveList)
                            || Visibility != Visibility.Visible || index >= list.Items.Count || !ReferenceEquals(item, list.Items[index])) return;
                        list.UpdateLayout(); var row = list.ContainerFromIndex(index) as Control;
                        if (row != null && row.Focus(FocusState.Keyboard)) { _lastFocus = row; focused = true; }
                    });
                    if (focused || generation != _focusGeneration) break;
                }
            }
            catch (Exception) { }
            finally { if (generation == _focusGeneration) { _pendingList = null; _pendingIndex = -1; } }
        }

        private int ListIndex(ListViewBase list, Control control)
        {
            if (_pendingList == list && _pendingIndex >= 0 && _pendingIndex < list.Items.Count) return _pendingIndex;
            DependencyObject child = control;
            while (child != null && child != list)
            {
                if (child is SelectorItem item) return list.IndexFromContainer(item);
                child = VisualTreeHelper.GetParent(child);
            }
            return -1;
        }

        private static int Columns(ListViewBase list)
        {
            var wrap = list.ItemsPanelRoot as ItemsWrapGrid; double width = wrap?.ItemWidth ?? 282;
            if (double.IsNaN(width) || width <= 0) width = 282;
            return Math.Max(1, (int)Math.Floor(Math.Max(width, list.ActualWidth - list.Padding.Left - list.Padding.Right) / width));
        }

        private bool IsAvailable(Control control)
        {
            if (control == null || !control.IsEnabled || !control.IsTabStop) return false;
            DependencyObject node = control;
            while (node != null)
            {
                if (node is UIElement element && element.Visibility != Visibility.Visible) return false;
                if (ReferenceEquals(node, this)) return true;
                node = VisualTreeHelper.GetParent(node);
            }
            return false;
        }
    }
}
