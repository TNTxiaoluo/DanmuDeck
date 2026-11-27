using System;
using BiliXbox.Services;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace BiliXbox.Controls
{
    // The overview tile that used to open UP 动态 (still reachable from the
    // home bar's 动态 button) now opens the offline cache list.
    public sealed partial class AccountLibraryView
    {
        private OfflineCacheView _offlineView;
        public event Action<OfflineCacheEntry> OfflinePlayRequested;

        private void InitializeOfflinePane()
        {
            _offlineView = new OfflineCacheView { Visibility = Visibility.Collapsed, Margin = new Thickness(20, 8, 20, 0) };
            _offlineView.PlayRequested += entry => OfflinePlayRequested?.Invoke(entry);
            Add(_root, _offlineView, 1, 0);
        }

        private void OpenOffline() => Push(new ViewState { Pane = LibraryPane.Offline, Title = "离线缓存" });

        private void RenderOffline()
        {
            if (_offlineView == null) return;
            bool offline = _state.Pane == LibraryPane.Offline;
            bool wasVisible = _offlineView.Visibility == Visibility.Visible;
            _offlineView.Visibility = offline ? Visibility.Visible : Visibility.Collapsed;
            if (offline && !wasVisible) _offlineView.Show();
            else if (!offline && wasVisible) _offlineView.Hide();
        }

        private void FocusOffline()
        {
            if (_offlineView.HasRows) _offlineView.FocusCurrent();
            else Focus(_back);
        }

        private bool WithinOffline(DependencyObject node)
        {
            while (node != null)
            {
                if (ReferenceEquals(node, _offlineView)) return true;
                node = VisualTreeHelper.GetParent(node);
            }
            return false;
        }

        // true when the offline list handled the direction.
        private bool NavigateOffline(PlayerDirection direction)
        {
            var current = FocusManager.GetFocusedElement() as DependencyObject;
            if (WithinOffline(current))
            {
                if (!_offlineView.Navigate(direction)) Focus(_back);
                return true;
            }
            if (direction == PlayerDirection.Down && _offlineView.HasRows) { _offlineView.FocusCurrent(); return true; }
            return false;
        }
    }
}
