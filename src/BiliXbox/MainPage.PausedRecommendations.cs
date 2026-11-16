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
        private int _pausedRecommendationIndex = -1, _pausedRecommendationFocus;

        private bool CanOpenPausedRecommendations => !_disposed && _watching && !_creatorVisible && !_libraryVisible && !_liveVisible
            && !_wantsPlayback && !_sourceLoading && _media.Source != null && _video != null
            && !_dialogOpen && !_inputPaneVisible && _playerUi.Drawer == PlayerDrawer.None;

        private async Task OpenPausedRecommendationsAsync(bool refresh = false)
        {
            if (!CanOpenPausedRecommendations) return;
            _adjustingVolume = false;
            _pausedRecommendationsVisible = true;
            PausedRecommendationsPanel.Visibility = Visibility.Visible;
            ApplyPlayerUi();
            if (!refresh && _pausedRecommendationsBvid == _video.Bvid && _pausedRecommendationsAccount == CurrentAccountMid
                && PausedRecommendationGrid.Items.Count > 0)
            { FocusPausedRecommendation(Math.Max(0, _pausedRecommendationIndex)); return; }
            if (_pausedRecommendationsLoading) return;
            Cancel(ref _pausedRecommendationsCts);
            var watch = _watchCts; string bvid = _video.Bvid; long account = CurrentAccountMid;
            var owner = _pausedRecommendationsCts = CancellationTokenSource.CreateLinkedTokenSource(watch.Token);
            _pausedRecommendationsLoading = true; _pausedRecommendationsBvid = null; _pausedRecommendationsAccount = -1;
            ++_pausedRecommendationFocus; _pausedRecommendationIndex = -1;
            PausedRecommendationGrid.Items.Clear();
            PausedRecommendationNotice.Text = "正在加载与当前视频相关的推荐…";
            PausedRecommendationRefresh.IsEnabled = false;
            PausedRecommendationClose.Focus(FocusState.Keyboard);
            try
            {
                var videos = await _api.GetRelatedVideosAsync(bvid, owner.Token);
                if (owner != _pausedRecommendationsCts || owner.IsCancellationRequested || !_pausedRecommendationsVisible
                    || watch != _watchCts || bvid != _video?.Bvid || account != CurrentAccountMid || !CanOpenPausedRecommendations) return;
                foreach (var item in videos) PausedRecommendationGrid.Items.Add(item);
                _pausedRecommendationsBvid = bvid; _pausedRecommendationsAccount = account;
                PausedRecommendationNotice.Text = videos.Count == 0 ? "这个视频暂时没有相关推荐，可刷新重试。" : "左右选择 · A 播放 · ↓ 刷新 / 返回按钮 · ↑ 或 B 收起";
                if (videos.Count > 0) FocusPausedRecommendation(0);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (owner == _pausedRecommendationsCts && _pausedRecommendationsVisible && !owner.IsCancellationRequested)
                { PausedRecommendationNotice.Text = "相关推荐加载失败：" + Friendly(ex); PausedRecommendationRefresh.Focus(FocusState.Keyboard); }
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
                if (direction == PlayerDirection.Up) { ClosePausedRecommendations(); return true; }
                var current = FocusManager.GetFocusedElement() as Control;
                bool toolbar = current == PausedRecommendationRefresh || current == PausedRecommendationClose;
                if (toolbar && (direction == PlayerDirection.Left || direction == PlayerDirection.Right))
                { ++_pausedRecommendationFocus; (direction == PlayerDirection.Left && PausedRecommendationRefresh.IsEnabled ? PausedRecommendationRefresh : PausedRecommendationClose).Focus(FocusState.Keyboard); return true; }
                int count = PausedRecommendationGrid.Items.Count;
                if (count == 0) { ++_pausedRecommendationFocus; (PausedRecommendationRefresh.IsEnabled ? PausedRecommendationRefresh : PausedRecommendationClose).Focus(FocusState.Keyboard); return true; }
                if (direction == PlayerDirection.Down && !toolbar)
                { ++_pausedRecommendationFocus; (PausedRecommendationRefresh.IsEnabled ? PausedRecommendationRefresh : PausedRecommendationClose).Focus(FocusState.Keyboard); return true; }
                int next = _pausedRecommendationIndex < 0 ? 0 : _pausedRecommendationIndex + (direction == PlayerDirection.Left ? -1 : direction == PlayerDirection.Right ? 1 : 0);
                FocusPausedRecommendation(Math.Max(0, Math.Min(count - 1, next))); return true;
            }
            if (!PlayerConfirmKey(key)) return false;
            if (!_playerInput.ConfirmKeyDown(DateTimeOffset.UtcNow, repeat)) return true;
            _dispatchingPlayerAction = true;
            try
            {
                var focused = FocusManager.GetFocusedElement() as Control;
                if (focused == PausedRecommendationClose) ClosePausedRecommendations();
                else if (focused == PausedRecommendationRefresh && PausedRecommendationRefresh.IsEnabled) _ = OpenPausedRecommendationsAsync(true);
                else if (_pausedRecommendationIndex >= 0 && _pausedRecommendationIndex < PausedRecommendationGrid.Items.Count)
                    _ = OpenPausedRecommendationVideoAsync(PausedRecommendationGrid.Items[_pausedRecommendationIndex] as VideoItem);
            }
            finally { _dispatchingPlayerAction = false; }
            return true;
        }
        private async void FocusPausedRecommendation(int index)
        {
            if (!_pausedRecommendationsVisible || index < 0 || index >= PausedRecommendationGrid.Items.Count) return;
            int generation = ++_pausedRecommendationFocus;
            _pausedRecommendationIndex = index; object item = PausedRecommendationGrid.Items[index];
            try {
            PausedRecommendationGrid.ScrollIntoView(item, ScrollIntoViewAlignment.Default);
            bool focused = false;
            for (int attempt = 0; attempt < 3 && !focused && generation == _pausedRecommendationFocus; ++attempt)
                await Dispatcher.RunAsync(CoreDispatcherPriority.Low, () => {
                    if (_disposed || !_pausedRecommendationsVisible || generation != _pausedRecommendationFocus
                        || index >= PausedRecommendationGrid.Items.Count || !ReferenceEquals(item, PausedRecommendationGrid.Items[index])) return;
                    PausedRecommendationGrid.UpdateLayout();
                    var container = PausedRecommendationGrid.ContainerFromIndex(index) as Control;
                    focused = container != null && container.Focus(FocusState.Keyboard);
                });
            } catch (Exception) { /* The player may close while a container is being realized. */ }
        }
    }
}
