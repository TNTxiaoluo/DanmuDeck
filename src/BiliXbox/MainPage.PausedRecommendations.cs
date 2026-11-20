using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Controls;
using BiliXbox.Models;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace BiliXbox
{
    public sealed partial class MainPage
    {
        private bool _pausedRecommendationsVisible, _pausedRecommendationsLoading;
        private bool _pausedPullReady = true;
        private CancellationTokenSource _pausedRecommendationsCts;
        private string _pausedRecommendationsBvid;
        private long _pausedRecommendationsAccount = -1;
        private int _pausedRecommendationIndex = -1, _pausedCollectionIndex = -1, _pausedRecommendationFocus;
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
            bool hasCollection = index >= 0;
            PausedCollectionGrid.Items.Clear();
            if (hasCollection)
                foreach (var episode in _video.CollectionEpisodes) PausedCollectionGrid.Items.Add(episode);
            PausedCollectionGrid.Visibility = PausedRelatedTitle.Visibility = hasCollection ? Visibility.Visible : Visibility.Collapsed;
            PausedRecommendationsPanel.Height = hasCollection ? 564 : 324;
            PausedPanelTitle.Text = hasCollection
                ? "合集 · 当前第 " + (index + 1) + " / " + _video.CollectionEpisodes.Count + " 集" : "相关推荐";
            return index;
        }

        private async Task OpenPausedRecommendationsAsync(bool refresh = false)
        {
            if (!CanOpenPausedRecommendations) return;
            int initialCollectionIndex = _pausedRecommendationsVisible ? -1 : PreparePausedCollection();
            bool hasCollection = PausedCollectionGrid.Visibility == Visibility.Visible;
            _adjustingVolume = false;
            _pausedRecommendationsVisible = true;
            PausedRecommendationsPanel.Visibility = Visibility.Visible;
            ApplyPlayerUi();
            if (!refresh && _pausedRecommendationsBvid == _video.Bvid && _pausedRecommendationsAccount == CurrentAccountMid
                && PausedRecommendationGrid.Items.Count > 0)
            {
                if (initialCollectionIndex >= 0) FocusPausedCollection(initialCollectionIndex);
                else if (!hasCollection) FocusPausedRecommendation(Math.Max(0, _pausedRecommendationIndex));
                return;
            }
            if (_pausedRecommendationsLoading) return;
            Cancel(ref _pausedRecommendationsCts);
            var watch = _watchCts; string bvid = _video.Bvid; long account = CurrentAccountMid;
            var owner = _pausedRecommendationsCts = CancellationTokenSource.CreateLinkedTokenSource(watch.Token);
            _pausedRecommendationsLoading = true; _pausedRecommendationsBvid = null; _pausedRecommendationsAccount = -1;
            ++_pausedRecommendationFocus; _pausedRecommendationIndex = -1;
            PausedRecommendationGrid.Items.Clear();
            PausedRecommendationNotice.Text = "正在加载与当前视频相关的推荐…";
            PausedRecommendationRefresh.IsEnabled = false;
            if (initialCollectionIndex >= 0) FocusPausedCollection(initialCollectionIndex);
            else if (!hasCollection) PausedRecommendationClose.Focus(FocusState.Keyboard);
            try
            {
                var videos = await _api.GetRelatedVideosAsync(bvid, owner.Token);
                if (owner != _pausedRecommendationsCts || owner.IsCancellationRequested || !_pausedRecommendationsVisible
                    || watch != _watchCts || bvid != _video?.Bvid || account != CurrentAccountMid || !CanOpenPausedRecommendations) return;
                foreach (var item in videos) PausedRecommendationGrid.Items.Add(item);
                _pausedRecommendationsBvid = bvid; _pausedRecommendationsAccount = account;
                PausedRecommendationNotice.Text = videos.Count == 0 ? "这个视频暂时没有相关推荐，可刷新重试。"
                    : hasCollection ? "左右选择 · A 播放 · ↓ 相关推荐 · ↑ 合集 · B 收起"
                    : "左右选择 · A 播放 · ↓ 刷新 / 返回按钮 · ↑ 或 B 收起";
                if (videos.Count > 0 && !hasCollection) FocusPausedRecommendation(0);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (owner == _pausedRecommendationsCts && _pausedRecommendationsVisible && !owner.IsCancellationRequested)
                {
                    PausedRecommendationNotice.Text = "相关推荐加载失败：" + Friendly(ex);
                    if (!hasCollection) PausedRecommendationClose.Focus(FocusState.Keyboard);
                }
            }
            finally
            {
                if (owner == _pausedRecommendationsCts) { _pausedRecommendationsLoading = false; PausedRecommendationRefresh.IsEnabled = true; }
            }
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
                bool hasCollection = PausedCollectionGrid.Items.Count > 0;
                bool collectionFocused = _pausedFocusGrid == PausedCollectionGrid;
                var current = FocusManager.GetFocusedElement() as Control;
                bool toolbar = _pausedFocusGrid == null && (current == PausedRecommendationRefresh || current == PausedRecommendationClose);
                int relatedCount = PausedRecommendationGrid.Items.Count;
                if (direction == PlayerDirection.Up)
                {
                    if (hasCollection && !collectionFocused && !toolbar) FocusPausedCollection(Math.Max(0, _pausedCollectionIndex));
                    else ClosePausedRecommendations();
                }
                else if (direction == PlayerDirection.Down)
                {
                    if ((collectionFocused || toolbar) && relatedCount > 0) FocusPausedRecommendation(Math.Max(0, _pausedRecommendationIndex));
                    else if (hasCollection && toolbar) FocusPausedCollection(Math.Max(0, _pausedCollectionIndex));
                    else FocusPausedToolbar(true);
                }
                else if (toolbar) FocusPausedToolbar(direction == PlayerDirection.Left);
                else if (collectionFocused)
                {
                    int next = _pausedCollectionIndex + (direction == PlayerDirection.Left ? -1 : 1);
                    FocusPausedCollection(Math.Max(0, Math.Min(PausedCollectionGrid.Items.Count - 1, next)));
                }
                else if (relatedCount > 0)
                {
                    int next = _pausedRecommendationIndex < 0 ? 0 : _pausedRecommendationIndex + (direction == PlayerDirection.Left ? -1 : 1);
                    FocusPausedRecommendation(Math.Max(0, Math.Min(relatedCount - 1, next)));
                }
                else FocusPausedToolbar(true);
                return true;
            }
            if (!PlayerConfirmKey(key)) return false;
            if (!_playerInput.ConfirmKeyDown(DateTimeOffset.UtcNow, repeat)) return true;
            _dispatchingPlayerAction = true;
            try
            {
                var focused = FocusManager.GetFocusedElement() as Control;
                if (_pausedFocusGrid == null)
                {
                    if (focused == PausedRecommendationClose) ClosePausedRecommendations();
                    else if (focused == PausedRecommendationRefresh && PausedRecommendationRefresh.IsEnabled)
                        _ = OpenPausedRecommendationsAsync(true);
                }
                else if (_pausedFocusGrid == PausedCollectionGrid && _pausedCollectionIndex >= 0 && _pausedCollectionIndex < PausedCollectionGrid.Items.Count)
                    _ = OpenPausedCollectionEpisodeAsync(PausedCollectionGrid.Items[_pausedCollectionIndex] as CollectionEpisode);
                else if (_pausedFocusGrid == PausedRecommendationGrid && _pausedRecommendationIndex >= 0
                    && _pausedRecommendationIndex < PausedRecommendationGrid.Items.Count)
                    _ = OpenPausedRecommendationVideoAsync(PausedRecommendationGrid.Items[_pausedRecommendationIndex] as VideoItem);
            }
            finally { _dispatchingPlayerAction = false; }
            return true;
        }
        private void FocusPausedToolbar(bool preferRefresh)
        {
            ++_pausedRecommendationFocus;
            _pausedFocusGrid = null;
            (preferRefresh && PausedRecommendationRefresh.IsEnabled ? PausedRecommendationRefresh : PausedRecommendationClose).Focus(FocusState.Keyboard);
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
