using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Models;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace BiliXbox.Controls
{
    public sealed partial class LiveHubView
    {
        private Grid _categoryPanel;
        private StackPanel _categoryGroups, _categoryAreas;
        private TextBlock _categoryNotice;
        private Button _categoryClose, _categoryRetry;
        private readonly List<Button> _categoryGroupButtons = new List<Button>(), _categoryAreaButtons = new List<Button>();
        private IReadOnlyList<LiveArea> _liveAreas = Array.Empty<LiveArea>();
        private CancellationTokenSource _categoryCts;
        private bool _categoryOpen;
        private long _parentAreaId, _areaId, _categoryParentId;
        private string _areaLabel = "推荐";

        private void InitializeLiveCategories()
        {
            _categoryPanel = new Grid { Background = Brush(255, 20, 20, 24), Padding = new Thickness(28, 20, 28, 16),
                RowSpacing = 12, Visibility = Visibility.Collapsed };
            _categoryPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _categoryPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _categoryPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _categoryPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var header = new Grid(); header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(Text("直播分类", 26, true));
            _categoryClose = MakeButton("返回", () => CloseLiveCategories(), "LiveCategoryCloseButton"); Add(header, _categoryClose, 0, 1);
            _categoryPanel.Children.Add(header);
            _categoryNotice = Text("正在读取分类…", 15); _categoryNotice.TextWrapping = TextWrapping.Wrap; Add(_categoryPanel, _categoryNotice, 1, 0);
            var columns = new Grid { ColumnSpacing = 24 };
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _categoryGroups = new StackPanel { Spacing = 8 }; _categoryAreas = new StackPanel { Spacing = 8 };
            columns.Children.Add(CategoryScroll(_categoryGroups)); Add(columns, CategoryScroll(_categoryAreas), 0, 1);
            Add(_categoryPanel, columns, 2, 0);
            var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18 };
            _categoryRetry = MakeButton("重试读取分类", () => Run(OpenLiveCategoriesAsync), "LiveCategoryRetryButton");
            _categoryRetry.Visibility = Visibility.Collapsed; footer.Children.Add(_categoryRetry);
            footer.Children.Add(Text("上下选择 · A 查看分区 / 确认分类 · 左右切换 · B 返回", 13)); Add(_categoryPanel, footer, 3, 0);
            _root.Children.Add(_categoryPanel);
        }
        private static ScrollViewer CategoryScroll(UIElement content)
            => new ScrollViewer { Content = content, Padding = new Thickness(5), HorizontalScrollMode = ScrollMode.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        private async Task OpenLiveCategoriesAsync()
        {
            if (_api == null || IsPlayingRoom) return;
            Cancel(ref _categoryCts);
            var owner = _categoryCts = CancellationTokenSource.CreateLinkedTokenSource(_external);
            _categoryOpen = true; ++_focusGeneration; _browse.Visibility = Visibility.Collapsed;
            _categoryPanel.Visibility = Visibility.Visible; _categoryRetry.Visibility = Visibility.Collapsed;
            _categoryNotice.Text = "正在读取分类…"; RenderCategoryGroups();
            if (_liveAreas.Count > 0) { _categoryNotice.Text = "选择分区后，直播列表将从第一页刷新。"; return; }
            try
            {
                var areas = await _api.GetLiveAreasAsync(owner.Token);
                if (!_categoryOpen || owner != _categoryCts || owner.IsCancellationRequested) return;
                _liveAreas = areas; RenderCategoryGroups();
                _categoryNotice.Text = areas.Count == 0 ? "当前没有可选分区，可返回推荐直播。" : "选择分区后，直播列表将从第一页刷新。";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!_categoryOpen || owner != _categoryCts) return;
                _categoryNotice.Text = "分类读取失败：" + Friendly(ex) + "。可重试或选择推荐。";
                _categoryRetry.Visibility = Visibility.Visible;
            }
        }
        private void ClearCategoryButtons(StackPanel panel, List<Button> buttons)
        { foreach (var button in buttons) _actions.Remove(button); buttons.Clear(); panel.Children.Clear(); }
        private Button CategoryButton(string label, Action action, string id)
        {
            Button button = null;
            button = MakeButton(label, () =>
            {
                if (_categoryOpen && (_categoryGroupButtons.Contains(button) || _categoryAreaButtons.Contains(button))) action();
            }, id);
            button.Style = (Style)Application.Current.Resources["TvRowButtonStyle"];
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap };
            return button;
        }
        private void RenderCategoryGroups()
        {
            ClearCategoryButtons(_categoryGroups, _categoryGroupButtons);
            ClearCategoryButtons(_categoryAreas, _categoryAreaButtons);
            var recommended = CategoryButton("推荐直播", () => Run(() => SelectLiveAreaAsync(null, null)), "LiveCategoryRecommended");
            _categoryGroupButtons.Add(recommended); _categoryGroups.Children.Add(recommended);
            foreach (var parent in _liveAreas)
            {
                var button = CategoryButton(parent.Name, () => { RenderCategoryAreas(parent); Focus(_categoryAreaButtons[0]); }, "LiveCategoryParent" + parent.Id);
                button.Tag = parent; _categoryGroupButtons.Add(button); _categoryGroups.Children.Add(button);
            }
            var selected = _liveAreas.FirstOrDefault(parent => parent.Id == _parentAreaId);
            if (selected != null) RenderCategoryAreas(selected);
            Focus(selected == null ? recommended : _categoryGroupButtons.First(button => (button.Tag as LiveArea)?.Id == selected.Id));
        }
        private void RenderCategoryAreas(LiveArea parent)
        {
            _categoryParentId = parent.Id;
            ClearCategoryButtons(_categoryAreas, _categoryAreaButtons);
            var all = CategoryButton("全部" + parent.Name, () => Run(() => SelectLiveAreaAsync(parent, null)), "LiveCategoryAll" + parent.Id);
            _categoryAreaButtons.Add(all); _categoryAreas.Children.Add(all);
            foreach (var area in parent.Children)
            {
                var button = CategoryButton(area.Name, () => Run(() => SelectLiveAreaAsync(parent, area)), "LiveCategoryArea" + area.Id);
                _categoryAreaButtons.Add(button); _categoryAreas.Children.Add(button);
            }
            _categoryNotice.Text = parent.Name + " · 选择具体分类，或查看该分区全部直播";
        }
        private async Task SelectLiveAreaAsync(LiveArea parent, LiveArea area)
        {
            if (!_categoryOpen) return;
            _parentAreaId = parent?.Id ?? 0; _areaId = area?.Id ?? 0;
            _areaLabel = area == null ? parent?.Name ?? "推荐" : parent.Name + " / " + area.Name;
            _category.Content = _parentAreaId == 0 ? "分类" : "分类：" + (area?.Name ?? parent.Name);
            // A new filter owns a new request. Late pages from the old filter
            // must neither replace its rooms nor move focus back to a stale row.
            Cancel(ref _browseCts); _browseCts = CancellationTokenSource.CreateLinkedTokenSource(_external); _browseBusy = false;
            _roomItems.Clear(); _rooms.Items.Clear(); _selectedIndex = 0; _page = 1; _hasMore = false; _pageText.Text = "第 1 页";
            CloseLiveCategories();
            await LoadRoomsAsync(1);
        }
        private void CloseLiveCategories(bool restoreFocus = true)
        {
            if (!_categoryOpen) return;
            _categoryOpen = false; Cancel(ref _categoryCts); ++_focusGeneration;
            _categoryPanel.Visibility = Visibility.Collapsed; _browse.Visibility = Visibility.Visible;
            if (restoreFocus && Visibility == Visibility.Visible) Focus(_category);
        }
        private void NavigateLiveCategories(PlayerDirection direction)
        {
            var current = FocusManager.GetFocusedElement() as Button;
            int group = _categoryGroupButtons.IndexOf(current), area = _categoryAreaButtons.IndexOf(current);
            if (direction == PlayerDirection.Left && area >= 0)
            { Focus(_categoryGroupButtons.FirstOrDefault(button => (button.Tag as LiveArea)?.Id == _categoryParentId) ?? _categoryGroupButtons[0]); return; }
            if (direction == PlayerDirection.Right && group > 0)
            { RenderCategoryAreas((LiveArea)current.Tag); Focus(_categoryAreaButtons[0]); return; }
            var rows = area >= 0 ? _categoryAreaButtons : _categoryGroupButtons; int index = area >= 0 ? area : group;
            if (ReferenceEquals(current, _categoryRetry))
            { if (direction == PlayerDirection.Up && _categoryGroupButtons.Count > 0) Focus(_categoryGroupButtons.Last()); else Focus(_categoryClose); return; }
            if (index < 0) { if (direction == PlayerDirection.Down && rows.Count > 0) Focus(rows[0]); return; }
            if (direction == PlayerDirection.Up && index == 0) { Focus(_categoryClose); return; }
            if (direction == PlayerDirection.Down && index == rows.Count - 1 && _categoryRetry.Visibility == Visibility.Visible)
            { Focus(_categoryRetry); return; }
            if (direction == PlayerDirection.Up || direction == PlayerDirection.Down)
            { int next = index + (direction == PlayerDirection.Up ? -1 : 1); Focus(rows[Math.Max(0, Math.Min(rows.Count - 1, next))]); }
            else if (direction == PlayerDirection.Left) Focus(_categoryClose);
        }
    }
}
