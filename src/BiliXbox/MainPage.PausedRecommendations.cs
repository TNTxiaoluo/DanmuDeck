using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Controls;
using BiliXbox.Models;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace BiliXbox
{
    // Pull-down panel with three pages: 简介 (introduction), 合集 (only when
    // the video is in a collection) and 相关推荐. LB / RB or the tab row
    // switch pages; the panel remembers the last page within a session.
    public sealed partial class MainPage
    {
        private const int PausedIntroTab = 0, PausedCollectionTab = 1, PausedRelatedTab = 2;
        private bool _pausedRecommendationsVisible, _pausedRecommendationsLoading, _pausedHasCollection;
        private bool _pausedPullReady = true;
        private CancellationTokenSource _pausedRecommendationsCts;
        private string _pausedRecommendationsBvid;
        private long _pausedRecommendationsAccount = -1;
        private int _pausedRecommendationIndex = -1, _pausedCollectionIndex = -1, _pausedRecommendationFocus;
        private int _pausedTab = PausedIntroTab, _pausedPreferredTab = PausedIntroTab;
        private GridView _pausedFocusGrid;

        private bool CanOpenPausedRecommendations => !_disposed && _watching && !_creatorVisible && !_libraryVisible && !_liveVisible
            && !_sourceLoading && _media.Source != null && _video != null
            && (!_wantsPlayback || CurrentCollectionIndex() >= 0)
            && !_dialogOpen && !_inputPaneVisible && _playerUi.Drawer == PlayerDrawer.None;

        private int CurrentCollectionIndex()
        {
            var collection = _video?.CollectionEpisodes;
            if (collection == null) return -1;
            for (int index = 0; index < collection.Count; ++index)
                if (collection[index].Bvid == _video.Bvid) return index;
            return -1;
        }

        private int PreparePausedCollection()
        {
            int index = CurrentCollectionIndex();
            _pausedHasCollection = index >= 0;
            PausedCollectionGrid.Items.Clear();
            if (_pausedHasCollection)
                foreach (var episode in _video.CollectionEpisodes) PausedCollectionGrid.Items.Add(episode);
            PausedTabCollection.Visibility = _pausedHasCollection ? Visibility.Visible : Visibility.Collapsed;
            PausedCollectionTitle.Text = _pausedHasCollection ? "当前第 " + (index + 1) + " / " + _video.CollectionEpisodes.Count + " 集" : "";
            if (index >= 0) _pausedCollectionIndex = index;
            PreparePausedIntro();
            return index;
        }

        private async Task OpenPausedRecommendationsAsync(bool refresh = false)
        {
            if (!CanOpenPausedRecommendations) return;
            bool opening = !_pausedRecommendationsVisible;
            if (opening) PreparePausedCollection();
            _adjustingVolume = false;
            _pausedRecommendationsVisible = true;
            PausedRecommendationsPanel.Visibility = Visibility.Visible;
            ApplyPlayerUi();
            if (opening)
            {
                // While a collection video plays, the panel opens on its 合集 page.
                int tab = _wantsPlayback && _pausedHasCollection ? PausedCollectionTab : _pausedPreferredTab;
                if (tab == PausedCollectionTab && !_pausedHasCollection) tab = PausedRelatedTab;
                ShowPausedTab(tab, tab != PausedIntroTab);
                if (tab == PausedIntroTab) FocusPausedTabButton(tab);
            }
            if (!refresh && _pausedRecommendationsBvid == _video.Bvid && _pausedRecommendationsAccount == CurrentAccountMid
                && PausedRecommendationGrid.Items.Count > 0) return;
            if (_pausedRecommendationsLoading) return;
            Cancel(ref _pausedRecommendationsCts);
            var watch = _watchCts; string bvid = _video.Bvid; long account = CurrentAccountMid;
            var owner = _pausedRecommendationsCts = CancellationTokenSource.CreateLinkedTokenSource(watch.Token);
            _pausedRecommendationsLoading = true; _pausedRecommendationsBvid = null; _pausedRecommendationsAccount = -1;
            _pausedRecommendationIndex = -1;
            PausedRecommendationGrid.Items.Clear();
            PausedRecommendationNotice.Text = "正在加载与当前视频相关的推荐…";
            PausedRecommendationRefresh.IsEnabled = false;
            try
            {
                var videos = await _api.GetRelatedVideosAsync(bvid, owner.Token);
                if (owner != _pausedRecommendationsCts || owner.IsCancellationRequested || !_pausedRecommendationsVisible
                    || watch != _watchCts || bvid != _video?.Bvid || account != CurrentAccountMid || !CanOpenPausedRecommendations) return;
                foreach (var item in videos) PausedRecommendationGrid.Items.Add(item);
                _pausedRecommendationsBvid = bvid; _pausedRecommendationsAccount = account;
                PausedRecommendationNotice.Text = videos.Count == 0 ? "这个视频暂时没有相关推荐，可刷新重试。" : PausedHint();
                if (videos.Count > 0 && _pausedTab == PausedRelatedTab && _pausedFocusGrid == null && !PausedHeaderFocused())
                    FocusPausedRecommendation(0);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (owner == _pausedRecommendationsCts && _pausedRecommendationsVisible && !owner.IsCancellationRequested)
                    PausedRecommendationNotice.Text = "相关推荐加载失败：" + Friendly(ex);
            }
            finally
            {
                if (owner == _pausedRecommendationsCts) { _pausedRecommendationsLoading = false; PausedRecommendationRefresh.IsEnabled = true; }
            }
        }

        private static string PausedHint() => "LB / RB 切换分页 · 左右选择 · A 确认（标签上按 A 搜索）· ↑ 回到分页标签 · B 返回";

        // Show one page; focusContent moves focus into it (first tag, the
        // current collection episode or the first related card).
        private void ShowPausedTab(int tab, bool focusContent)
        {
            if (tab == PausedCollectionTab && !_pausedHasCollection) tab = PausedRelatedTab;
            _pausedTab = tab;
            _pausedPreferredTab = tab == PausedCollectionTab ? _pausedPreferredTab : tab;
            PausedIntroScroll.Visibility = tab == PausedIntroTab ? Visibility.Visible : Visibility.Collapsed;
            PausedCollectionPage.Visibility = PausedCollectionGrid.Visibility = tab == PausedCollectionTab ? Visibility.Visible : Visibility.Collapsed;
            PausedRecommendationGrid.Visibility = tab == PausedRelatedTab ? Visibility.Visible : Visibility.Collapsed;
            StylePausedTabs();
            if (PausedRecommendationGrid.Items.Count > 0 || !_pausedRecommendationsLoading) PausedRecommendationNotice.Text = PausedHint();
            if (!focusContent) { _pausedFocusGrid = null; return; }
            if (tab == PausedIntroTab)
            {
                PausedIntroScroll.ChangeView(null, 0, null, true);
                if (PausedTagsAvailable) FocusPausedTag(_pausedTagIndex);
                else FocusPausedAction(_pausedActionIndex);
            }
            else if (tab == PausedCollectionTab) FocusPausedCollection(Math.Max(0, _pausedCollectionIndex));
            else if (PausedRecommendationGrid.Items.Count > 0) FocusPausedRecommendation(Math.Max(0, _pausedRecommendationIndex));
            else FocusPausedTabButton(tab);
        }

        private void StylePausedTabs()
        {
            foreach (var pair in new[] { new KeyValuePair<Button, int>(PausedTabIntro, PausedIntroTab),
                new KeyValuePair<Button, int>(PausedTabCollection, PausedCollectionTab), new KeyValuePair<Button, int>(PausedTabRelated, PausedRelatedTab) })
            {
                bool selected = pair.Value == _pausedTab;
                pair.Key.Background = new SolidColorBrush(selected ? Color.FromArgb(255, 245, 245, 247) : Color.FromArgb(31, 255, 255, 255));
                pair.Key.Foreground = new SolidColorBrush(selected ? Color.FromArgb(255, 17, 18, 22) : Color.FromArgb(242, 255, 255, 255));
            }
        }

        private void SwitchPausedTab(int step)
        {
            if (!_pausedRecommendationsVisible || step == 0) return;
            var tabs = PausedTabs();
            int position = Math.Max(0, tabs.IndexOf(_pausedTab));
            int next = Math.Max(0, Math.Min(tabs.Count - 1, position + Math.Sign(step)));
            if (tabs[next] == _pausedTab) return;
            ShowPausedTab(tabs[next], true);
        }

        private List<int> PausedTabs()
        {
            var tabs = new List<int> { PausedIntroTab };
            if (_pausedHasCollection) tabs.Add(PausedCollectionTab);
            tabs.Add(PausedRelatedTab);
            return tabs;
        }

        private Button PausedTabButton(int tab) => tab == PausedIntroTab ? PausedTabIntro : tab == PausedCollectionTab ? PausedTabCollection : PausedTabRelated;

        private void FocusPausedTabButton(int tab)
        {
            ++_pausedRecommendationFocus;
            _pausedFocusGrid = null;
            PausedTabButton(tab).Focus(FocusState.Keyboard);
        }

        private bool PausedHeaderFocused()
        {
            var current = FocusManager.GetFocusedElement() as Control;
            return current == PausedTabIntro || current == PausedTabCollection || current == PausedTabRelated
                || current == PausedRecommendationRefresh || current == PausedRecommendationClose;
        }

        // Right stick on the 简介 page scrolls a long description.
        private void ScrollPausedIntro(double delta)
        {
            if (!_pausedRecommendationsVisible || _pausedTab != PausedIntroTab) return;
            PausedIntroScroll.ChangeView(null, Math.Max(0, PausedIntroScroll.VerticalOffset + delta), null, true);
        }

        private void ClosePausedRecommendations(bool restoreFocus = true, bool clear = false)
        {
            bool wasVisible = _pausedRecommendationsVisible;
            _pausedRecommendationsVisible = _pausedRecommendationsLoading = false;
            ++_pausedRecommendationFocus;
            Cancel(ref _pausedRecommendationsCts);
            PausedRecommendationsPanel.Visibility = Visibility.Collapsed;
            PausedCollectionGrid.Items.Clear();
            _pausedFocusGrid = null;
            _pausedCollectionIndex = -1;
            PausedRecommendationRefresh.IsEnabled = true;
            if (clear) { _pausedRecommendationsBvid = null; _pausedRecommendationsAccount = -1; _pausedRecommendationIndex = -1; PausedRecommendationGrid.Items.Clear(); }
            if (!wasVisible) return;
            ApplyPlayerUi();
            if (restoreFocus && !_disposed && _watching && !_creatorVisible && !_libraryVisible)
            { _playerInput.SelectButton(0, PlayerButtons.Length); ShowControls(); FocusPlayerInput(); }
        }
        private async void RefreshPausedRecommendations_Click(object sender, RoutedEventArgs args)
        { if (!IgnorePlayerClick()) await OpenPausedRecommendationsAsync(true); }
        private void ClosePausedRecommendations_Click(object sender, RoutedEventArgs args)
        { if (!IgnorePlayerClick()) ClosePausedRecommendations(); }
        private void PausedTab_Click(object sender, RoutedEventArgs args)
        {
            if (IgnorePlayerClick()) return;
            ShowPausedTab(sender == PausedTabIntro ? PausedIntroTab : sender == PausedTabCollection ? PausedCollectionTab : PausedRelatedTab, false);
        }
        private async void PausedRecommendation_ItemClick(object sender, ItemClickEventArgs args)
        { if (!IgnorePlayerClick() && args.ClickedItem is VideoItem item) await OpenPausedRecommendationVideoAsync(item); }
        private async void PausedCollection_ItemClick(object sender, ItemClickEventArgs args)
        { if (!IgnorePlayerClick() && args.ClickedItem is CollectionEpisode item) await OpenPausedCollectionEpisodeAsync(item); }

        private async Task OpenPausedCollectionEpisodeAsync(CollectionEpisode episode)
        {
            var collection = _video?.CollectionEpisodes;
            if (!_pausedRecommendationsVisible || !CanOpenPausedRecommendations || collection == null
                || episode == null || !collection.Contains(episode)) return;
            if (episode.Bvid == _video.Bvid)
            {
                ClosePausedRecommendations();
                if (!_wantsPlayback) PlayPause_Click(PlayPauseButton, new RoutedEventArgs());
                return;
            }
            ClosePausedRecommendations(false, true);
            await PlayCollectionEpisodeAsync(episode, collection);
        }

        private async Task OpenPausedRecommendationVideoAsync(VideoItem item)
        {
            if (!_pausedRecommendationsVisible || !CanOpenPausedRecommendations || item == null
                || _pausedRecommendationsAccount != CurrentAccountMid) return;
            ClosePausedRecommendations(false, true);
            // A related video is outside an uploader/account playlist. Do not let its end trigger that older queue.
            ClearCreatorNavigation(); ClearAccountLibraryNavigation();
            await OpenVideoAsync(item.Bvid, item.Title);
        }

        private bool HandlePausedRecommendationKey(VirtualKey key, bool repeat)
        {
            if (!_pausedRecommendationsVisible) return false;
            PlayerDirection direction;
            if (PlayerDirectionKey(key, out direction))
            {
                bool vertical = direction == PlayerDirection.Up || direction == PlayerDirection.Down;
                if (vertical && repeat) return true;
                var current = FocusManager.GetFocusedElement() as Control;
                var header = new List<Control>();
                foreach (int tab in PausedTabs()) header.Add(PausedTabButton(tab));
                header.Add(PausedRecommendationRefresh); header.Add(PausedRecommendationClose);
                int headerIndex = header.IndexOf(current);
                int tag = FocusedPausedTag();
                if (headerIndex >= 0)
                {
                    if (direction == PlayerDirection.Up) ClosePausedRecommendations();
                    else if (direction == PlayerDirection.Down)
                    {
                        ShowPausedTab(_pausedTab, true);
                    }
                    else
                    {
                        int next = Math.Max(0, Math.Min(header.Count - 1, headerIndex + (direction == PlayerDirection.Left ? -1 : 1)));
                        var target = header[next];
                        if (target == PausedRecommendationRefresh && !PausedRecommendationRefresh.IsEnabled)
                            next = Math.Max(0, Math.Min(header.Count - 1, next + (direction == PlayerDirection.Left ? -1 : 1)));
                        target = header[next];
                        ++_pausedRecommendationFocus; _pausedFocusGrid = null;
                        target.Focus(FocusState.Keyboard);
                        // Moving onto a tab shows its page, like a pivot header.
                        if (target == PausedTabIntro) ShowPausedTab(PausedIntroTab, false);
                        else if (target == PausedTabCollection) ShowPausedTab(PausedCollectionTab, false);
                        else if (target == PausedTabRelated) ShowPausedTab(PausedRelatedTab, false);
                        target.Focus(FocusState.Keyboard);
                    }
                    return true;
                }
                if (tag >= 0)
                {
                    if (direction == PlayerDirection.Up) { PausedIntroScroll.ChangeView(null, 0, null, true); FocusPausedTabButton(PausedIntroTab); }
                    else if (direction == PlayerDirection.Down) FocusPausedAction(_pausedActionIndex);
                    else FocusPausedTag(tag + (direction == PlayerDirection.Left ? -1 : 1));
                    return true;
                }
                int action = FocusedPausedAction();
                if (action >= 0)
                {
                    if (direction == PlayerDirection.Up)
                    {
                        if (PausedTagsAvailable) FocusPausedTag(_pausedTagIndex);
                        else { PausedIntroScroll.ChangeView(null, 0, null, true); FocusPausedTabButton(PausedIntroTab); }
                    }
                    else if (direction == PlayerDirection.Down) ScrollPausedIntro(160);
                    else FocusPausedAction(action + (direction == PlayerDirection.Left ? -1 : 1));
                    return true;
                }
                if (_pausedFocusGrid == PausedCollectionGrid)
                {
                    if (direction == PlayerDirection.Up) FocusPausedTabButton(PausedCollectionTab);
                    else if (!vertical)
                    {
                        int next = _pausedCollectionIndex + (direction == PlayerDirection.Left ? -1 : 1);
                        FocusPausedCollection(Math.Max(0, Math.Min(PausedCollectionGrid.Items.Count - 1, next)));
                    }
                    return true;
                }
                if (_pausedFocusGrid == PausedRecommendationGrid)
                {
                    int relatedCount = PausedRecommendationGrid.Items.Count;
                    if (direction == PlayerDirection.Up) FocusPausedTabButton(PausedRelatedTab);
                    else if (!vertical && relatedCount > 0)
                    {
                        int next = _pausedRecommendationIndex < 0 ? 0 : _pausedRecommendationIndex + (direction == PlayerDirection.Left ? -1 : 1);
                        FocusPausedRecommendation(Math.Max(0, Math.Min(relatedCount - 1, next)));
                    }
                    return true;
                }
                ShowPausedTab(_pausedTab, true);
                return true;
            }
            if (!PlayerConfirmKey(key)) return false;
            // 点赞 acts on release timing: a hold of 0.6 s becomes 一键三连.
            if (FocusedPausedAction() == 0) { if (_playerInput.ConfirmKeyDown(DateTimeOffset.UtcNow, repeat)) _ = PressPausedLikeAsync(); return true; }
            if (!_playerInput.ConfirmKeyDown(DateTimeOffset.UtcNow, repeat)) return true;
            _dispatchingPlayerAction = true;
            try
            {
                var focused = FocusManager.GetFocusedElement() as Control;
                int tagIndex = FocusedPausedTag();
                if (tagIndex >= 0) _ = SearchPausedTagAsync((PausedTagsPanel.Children[tagIndex] as Button)?.Content as string);
                else if (FocusedPausedAction() == 1) _ = ShowPausedCoinAsync();
                else if (FocusedPausedAction() == 2) _ = ShowPausedFavoriteAsync();
                else if (focused == PausedTabIntro) ShowPausedTab(PausedIntroTab, true);
                else if (focused == PausedTabCollection) ShowPausedTab(PausedCollectionTab, true);
                else if (focused == PausedTabRelated) ShowPausedTab(PausedRelatedTab, true);
                else if (focused == PausedRecommendationClose) ClosePausedRecommendations();
                else if (focused == PausedRecommendationRefresh)
                { if (PausedRecommendationRefresh.IsEnabled) _ = OpenPausedRecommendationsAsync(true); }
                else if (_pausedFocusGrid == PausedCollectionGrid && _pausedCollectionIndex >= 0 && _pausedCollectionIndex < PausedCollectionGrid.Items.Count)
                    _ = OpenPausedCollectionEpisodeAsync(PausedCollectionGrid.Items[_pausedCollectionIndex] as CollectionEpisode);
                else if (_pausedFocusGrid == PausedRecommendationGrid && _pausedRecommendationIndex >= 0
                    && _pausedRecommendationIndex < PausedRecommendationGrid.Items.Count)
                    _ = OpenPausedRecommendationVideoAsync(PausedRecommendationGrid.Items[_pausedRecommendationIndex] as VideoItem);
            }
            finally { _dispatchingPlayerAction = false; }
            return true;
        }

        private void FocusPausedRecommendation(int index) => FocusPausedGridItem(PausedRecommendationGrid, index);
        private void FocusPausedCollection(int index) => FocusPausedGridItem(PausedCollectionGrid, index);

        private async void FocusPausedGridItem(GridView grid, int index)
        {
            if (!_pausedRecommendationsVisible || index < 0 || index >= grid.Items.Count) return;
            int generation = ++_pausedRecommendationFocus;
            _pausedFocusGrid = grid;
            if (grid == PausedCollectionGrid) _pausedCollectionIndex = index;
            else _pausedRecommendationIndex = index;
            object item = grid.Items[index];
            try {
            grid.ScrollIntoView(item, ScrollIntoViewAlignment.Default);
            bool focused = false;
            for (int attempt = 0; attempt < 3 && !focused && generation == _pausedRecommendationFocus; ++attempt)
                await Dispatcher.RunAsync(CoreDispatcherPriority.Low, () => {
                    if (_disposed || !_pausedRecommendationsVisible || generation != _pausedRecommendationFocus
                        || index >= grid.Items.Count || !ReferenceEquals(item, grid.Items[index])) return;
                    grid.UpdateLayout();
                    var container = grid.ContainerFromIndex(index) as Control;
                    focused = container != null && container.Focus(FocusState.Keyboard);
                });
            } catch (Exception) { /* The player may close while a container is being realized. */ }
        }
    }
}
