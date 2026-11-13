using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Controls;
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
            Cancel(ref _feedCts);
            CancelManualGamepadScroll();
            if (!await PrepareSectionTransitionAsync(section)) return;
            ClearAccountLibraryNavigation(); ClearCreatorNavigation();
            HideLiveSection(); Cancel(ref _feedCts); CancelGamepadListFocus();
            _feedNavigation.SelectSection(section);
            SearchInput.Text = "";
            SearchQueryLabel.Text = "搜索视频或用户，支持 BV 号 / 视频链接";
            GoHome(false); UpdateSectionButtons();
            StartSectionTransition();
            if (section == HomeSection.Live) await LoadLiveSectionAsync();
            else await LoadFeedAsync(section == HomeSection.Recommendations);
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
