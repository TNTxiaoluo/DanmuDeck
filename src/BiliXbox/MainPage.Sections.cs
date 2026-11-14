using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Controls;
using BiliXbox.Models;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace BiliXbox
{
    public sealed partial class MainPage
    {
        private bool _liveVisible;
        private CancellationTokenSource _liveCts;
        private readonly HomeFeedPageCache _homeFeedPages = new HomeFeedPageCache();
        private FeedRequest _visibleHomeFeedRequest;
        private long _visibleHomeFeedAccountMid = -1;
        private HomeFeedPageCache.Page _pendingHomeFeedPosition;
        private Control _homeFeedFocusAnchor;
        private Control[] SectionButtons => new Control[] { LiveSectionButton, HomeButton,
            PopularSectionButton, AnimationSectionButton, FilmSectionButton };

        private void InitializeSections()
        {
            LiveHub.Initialize(_api);
            LiveHub.RequestTextInputAsync = ShowLiveInlineInputAsync;
            LiveHub.LoginRequested += async () =>
            {
                if (!_disposed && _liveVisible && !_dialogOpen) await ShowLoginAsync();
            };
            LiveHub.PlaybackChanged += (sender, args) =>
            {
                if (!_disposed && _liveVisible) ApplyLiveSectionSurface();
            };
            UpdateSectionButtons();
        }

        private async void Section_Click(object sender, RoutedEventArgs args)
        {
            if (IgnorePlayerClick() || _dialogOpen) return;
            HomeSection section;
            if (sender is Button button && Enum.TryParse(button.Tag as string, out section))
                await SelectSectionAsync(section);
        }

        private async Task SelectSectionAsync(HomeSection section)
        {
            if (_disposed || _dialogOpen) return;
            if (!_watching && !_creatorVisible && !_libraryVisible && !_feedNavigation.IsSearch
                && section == _feedNavigation.Section && _pendingSection == null) return;
            CaptureVisibleHomeFeedPosition();
            EnsureHomeFeedAccount();
            Cancel(ref _feedCts);
            CancelManualGamepadScroll();
            CancelGamepadHomeFocus();
            if (!await PrepareSectionTransitionAsync(section)) return;
            ClearAccountLibraryNavigation(); ClearCreatorNavigation();
            HideLiveSection(); Cancel(ref _feedCts); CancelGamepadListFocus();
            _homeFeedFocusAnchor = SectionButtons[(int)section];
            _gamepadHomeFeedReplacing = true;
            _pendingHomeFeedPosition = null;
            _feedNavigation.SwitchSection(section);
            SearchInput.Text = "";
            SearchQueryLabel.Text = "搜索视频或用户，支持 BV 号 / 视频链接";
            GoHome(false); UpdateSectionButtons();
            StartSectionTransition();
            if (section == HomeSection.Live) await LoadLiveSectionAsync();
            else await LoadFeedAsync();
        }

        private void EnsureHomeFeedAccount(bool refresh = false)
        {
            if (_homeFeedPages.EnsureAccount(CurrentAccountMid))
            {
                _feedNavigation.ResetHomePages();
                _visibleHomeFeedRequest = null; _visibleHomeFeedAccountMid = -1;
                _pendingHomeFeedPosition = null;
            }
            if (refresh && !_feedNavigation.IsSearch) InvalidateHomeFeedSection();
        }

        private void InvalidateHomeFeedSection()
        {
            if (_feedNavigation.IsSearch) return;
            _homeFeedPages.Invalidate(_feedNavigation.Section);
            _visibleHomeFeedRequest = null; _pendingHomeFeedPosition = null;
        }

        private void CaptureVisibleHomeFeedPosition()
        {
            var request = _visibleHomeFeedRequest;
            if (_watching || _creatorVisible || _libraryVisible || _liveVisible || _gamepadHomeFeedReplacing
                || request == null || request.IsSearch || _visibleHomeFeedAccountMid != CurrentAccountMid
                || _homeFeedPages.AccountMid != CurrentAccountMid) return;
            // Flush the latest right-stick position before cancelling its work.
            // Native focus may still refer to the card that was visible before scrolling.
            ReconcileHomeScrollBeforeNavigation();
            int index = GamepadHomeVideoIndex(FocusManager.GetFocusedElement() as Control);
            if (index < 0) index = _gamepadHomeIndex;
            var viewer = ViewportFocusScroll.FindScrollViewer(VideoGrid);
            _homeFeedPages.RememberPosition(request.Section, request.Page, index, viewer?.VerticalOffset ?? 0);
        }

        private bool TryRestoreCachedHomeFeed(FeedRequest request)
        {
            _pendingHomeFeedPosition = null;
            HomeFeedPageCache.Page page;
            if (request.IsSearch || _homeFeedPages.AccountMid != CurrentAccountMid
                || !_homeFeedPages.TryGet(request.Section, request.Page, out page)) return false;
            BeginHomeFeedReplacement();
            Videos.Clear(); Users.Clear();
            UserGrid.Visibility = Visibility.Collapsed; VideoGrid.Visibility = Visibility.Visible;
            foreach (var item in page.Videos) Videos.Add(item);
            _hasNextFeed = page.HasMore;
            FeedTitle.Text = page.Title; FeedNotice.Text = page.Notice;
            FeedNotice.Visibility = string.IsNullOrWhiteSpace(page.Notice) ? Visibility.Collapsed : Visibility.Visible;
            EmptyState.Text = "此页暂无视频，请稍后刷新或返回上一页。";
            EmptyState.Visibility = page.Videos.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            PageLabel.Text = "第 " + request.Page + " 页";
            PreviousPage.IsEnabled = request.Page > 1; NextPage.IsEnabled = page.HasMore;
            UpdateSectionButtons(); UpdateSearchModeUi(request);
            SetStatus("已加载 " + page.Videos.Count + " 个视频");
            _visibleHomeFeedRequest = request; _visibleHomeFeedAccountMid = CurrentAccountMid;
            _pendingHomeFeedPosition = page.HasPosition ? page : null;
            if (request.Section == HomeSection.Recommendations) _loadedFeedAccountMid = CurrentAccountMid;
            CompleteHomeFeedReplacement();
            return true;
        }

        private void RememberHomeFeedPage(FeedRequest request, long accountMid,
            IReadOnlyList<VideoItem> videos, bool hasMore, string notice)
        {
            if (accountMid != CurrentAccountMid || _homeFeedPages.AccountMid != accountMid
                || !_feedNavigation.IsCurrent(request)) return;
            _visibleHomeFeedRequest = request; _visibleHomeFeedAccountMid = accountMid;
            if (!request.IsSearch) _homeFeedPages.Remember(request.Section, request.Page, videos, hasMore, FeedTitle.Text, notice);
        }

        private bool RestoreVisibleHomeFeedPosition()
        {
            if (_homeFeedFocusAnchor != null)
            { FocusGamepadHomeControl(_homeFeedFocusAnchor); return true; }
            var request = _visibleHomeFeedRequest;
            HomeFeedPageCache.Page page;
            if (request == null || request.IsSearch || _feedNavigation.IsSearch || _visibleHomeFeedAccountMid != CurrentAccountMid
                || request.Section != _feedNavigation.Section || request.Page != _feedNavigation.Page
                || !_homeFeedPages.TryGet(request.Section, request.Page, out page) || !page.HasPosition) return false;
            _pendingHomeFeedPosition = page;
            return RestoreHomeFeedPageFocus();
        }

        private bool RestoreHomeFeedPageFocus()
        {
            var page = _pendingHomeFeedPosition;
            if (page == null || _watching || _creatorVisible || _libraryVisible || _liveVisible || _dialogOpen || _inputPaneVisible) return false;
            _pendingHomeFeedPosition = null;
            var grid = HomeResultGrid;
            if (grid.Items.Count == 0) return false;
            CancelGamepadHomeFocus();
            int revision = ++_manualScrollRevision;
            long accountMid = CurrentAccountMid;
            _gamepadHomeIndex = Math.Max(0, Math.Min(page.FocusIndex, grid.Items.Count - 1));
            _gamepadHomeInitialFocusDone = true;
            _homeScrollPending = _homeManuallyScrolled = true; _manuallyScrolledList = grid;
            bool restored = ViewportFocusScroll.Restore(grid, page.ScrollOffset, _gamepadHomeIndex, result =>
            {
                if (_disposed || revision != _manualScrollRevision || accountMid != CurrentAccountMid || _watching
                    || _creatorVisible || _libraryVisible || _liveVisible || _dialogOpen || !ReferenceEquals(grid, HomeResultGrid)) return;
                ApplyHomeScrollFocus(result); _homeScrollPending = false;
            });
            if (!restored)
            {
                CancelManualGamepadScroll();
                FocusGamepadHomeVideo(_gamepadHomeIndex);
            }
            return true;
        }

        private async Task LoadLiveSectionAsync()
        {
            if (_disposed || !_liveVisible) return;
            Cancel(ref _liveCts);
            var owner = _liveCts = new CancellationTokenSource();
            try
            {
                await LiveHub.ShowAsync(owner.Token);
                if (!_disposed && _liveVisible && _liveCts == owner) LiveHub.RestoreFocus();
            }
            catch (OperationCanceledException) when (owner.IsCancellationRequested) { }
            catch (Exception ex) { if (!_disposed && _liveVisible && _liveCts == owner) SetStatus("直播加载失败：" + Friendly(ex)); }
        }

        private async Task<bool> ShowLiveInlineInputAsync(TextBox editor, string title, int maxLength)
        {
            if (_disposed || !_liveVisible || editor == null) return false;
            editor.MaxLength = maxLength;
            await ShowInlineTextInputAsync(editor, false);
            return !_disposed && _liveVisible && !string.IsNullOrWhiteSpace(editor.Text);
        }

        private void ShowLiveSectionSurface()
        {
            _liveVisible = true;
            LiveHub.Visibility = Visibility.Visible;
            HomeView.Visibility = WatchView.Visibility = Visibility.Collapsed;
            ApplyLiveSectionSurface();
            LiveHub.RestoreFocus();
        }

        private void ApplyLiveSectionSurface()
        {
            bool playing = LiveHub.IsPlayingRoom;
            Grid.SetRow(LiveHub, playing ? 0 : 1); Grid.SetRowSpan(LiveHub, playing ? 3 : 1);
            Header.Visibility = Footer.Visibility = playing ? Visibility.Collapsed : Visibility.Visible;
            Root.Padding = playing ? new Thickness(0) : new Thickness(52, 28, 52, 20);
            GamepadFocusOutline.Visibility = Visibility.Collapsed;
        }

        private void HideLiveSection()
        {
            Cancel(ref _liveCts);
            _liveVisible = false;
            LiveHub.StopRequests(); LiveHub.Visibility = Visibility.Collapsed;
        }

        private void UpdateSectionButtons()
        {
            for (int index = 0; index < SectionButtons.Length; ++index)
            {
                var control = SectionButtons[index];
                bool selected = !_feedNavigation.IsSearch && (int)_feedNavigation.Section == index;
                control.Background = new SolidColorBrush(selected ? Color.FromArgb(255, 242, 242, 247) : Color.FromArgb(31, 255, 255, 255));
                control.Foreground = new SolidColorBrush(selected ? Color.FromArgb(255, 13, 13, 16) : Color.FromArgb(242, 255, 255, 255));
                AutomationProperties.SetHelpText(control, selected ? "当前分区" : "切换分区");
            }
        }

        private static string SectionTitle(HomeSection section)
            => section == HomeSection.Popular ? "热门" : section == HomeSection.Animation ? "动画分区"
                : section == HomeSection.Film ? "影视分区" : section == HomeSection.Live ? "直播" : "推荐";

        private bool DispatchLiveSectionKey(Windows.System.VirtualKey key, bool repeat)
        {
            LiveHub.ReconcileScrollFocus();
            var focused = FocusManager.GetFocusedElement() as Control;
            bool header = HomeHeaderControls.Contains(focused);
            PlayerDirection direction;
            if (PlayerDirectionKey(key, out direction))
            {
                if (header)
                {
                    if (direction == PlayerDirection.Down) LiveHub.RestoreFocus();
                    else if (direction == PlayerDirection.Left || direction == PlayerDirection.Right)
                    {
                        int current = Array.IndexOf(HomeHeaderControls, focused);
                        int next = Math.Max(0, Math.Min(HomeHeaderControls.Length - 1,
                            current + (direction == PlayerDirection.Left ? -1 : 1)));
                        FocusGamepadHomeControl(HomeHeaderControls[next]);
                    }
                }
                else if (!LiveHub.Navigate(direction)) FocusGamepadHomeControl(LiveSectionButton);
                return true;
            }
            if (!PlayerConfirmKey(key)) return false;
            if (header) ConfirmGamepadHome(repeat); else LiveHub.Confirm(repeat);
            return true;
        }
    }
}
