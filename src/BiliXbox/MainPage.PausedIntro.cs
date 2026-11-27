using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Models;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace BiliXbox
{
    // The pull-down panel's first section: the video introduction (stats
    // with icons, statement, BV id, description and tags; A on a tag searches
    // it). Collection and related recommendations follow below.
    public sealed partial class MainPage
    {
        private string _pausedIntroBvid;
        private long _pausedOnlineCid;
        private int _pausedTagIndex;

        private void PreparePausedIntro()
        {
            var video = _video;
            if (video == null) return;
            PausedIntroTitle.Text = video.Title ?? "";
            PreparePausedActions();
            PausedViewCount.Text = video.ViewCount.HasValue ? IntroCount(video.ViewCount.Value) : "--";
            PausedDanmakuCount.Text = video.DanmakuCount.HasValue ? IntroCount(video.DanmakuCount.Value) : "--";
            PausedPublishTime.Text = video.PublishTime.HasValue
                ? video.PublishTime.Value.ToLocalTime().ToString("yyyy年M月d日 HH:mm", CultureInfo.InvariantCulture) : "";
            PausedStatement.Text = video.Statement ?? "";
            PausedStatementPanel.Visibility = string.IsNullOrWhiteSpace(video.Statement) ? Visibility.Collapsed : Visibility.Visible;
            PausedBvid.Text = video.Bvid;
            PausedNoReprint.Visibility = video.NoReprint ? Visibility.Visible : Visibility.Collapsed;
            PausedDescription.Text = string.IsNullOrWhiteSpace(video.Description) || video.Description == "-" ? "（暂无简介）" : video.Description.Trim();
            if (_pausedIntroBvid != video.Bvid)
            {
                _pausedIntroBvid = video.Bvid;
                _pausedTagIndex = 0;
                PausedTagsPanel.Children.Clear();
                PausedTagsScroll.Visibility = Visibility.Collapsed;
                _ = LoadPausedTagsAsync(video.Bvid);
            }
            _ = LoadPausedOnlineAsync(video.Bvid, _currentCid);
        }

        private static string IntroCount(long value)
            => value >= 100000000 ? (value / 100000000.0).ToString("0.#", CultureInfo.InvariantCulture) + "亿"
                : value >= 10000 ? (value / 10000.0).ToString("0.#", CultureInfo.InvariantCulture) + "万"
                : value.ToString(CultureInfo.InvariantCulture);

        private async Task LoadPausedTagsAsync(string bvid)
        {
            try
            {
                var tags = await _api.GetVideoTagsAsync(bvid);
                if (_pausedIntroBvid != bvid || _video?.Bvid != bvid) return;
                PausedTagsPanel.Children.Clear();
                foreach (var tag in tags.Take(20))
                {
                    var button = new Button
                    {
                        Content = tag, Padding = new Thickness(20, 8, 20, 8), MinHeight = 42, FontSize = 17,
                        CornerRadius = new CornerRadius(21), RequiresPointer = RequiresPointer.Never
                    };
                    var keyword = tag;
                    button.Click += async (s, e) => { if (!IgnorePlayerClick()) await SearchPausedTagAsync(keyword); };
                    Windows.UI.Xaml.Automation.AutomationProperties.SetName(button, "搜索标签 " + tag);
                    PausedTagsPanel.Children.Add(button);
                }
                PausedTagsScroll.Visibility = PausedTagsPanel.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception) { /* Tags are optional. */ }
        }

        private async Task LoadPausedOnlineAsync(string bvid, long cid)
        {
            _pausedOnlineCid = cid;
            try
            {
                var total = await _api.GetOnlineCountAsync(bvid, cid);
                if (_video?.Bvid != bvid || _currentCid != cid) return;
                PausedOnlineCount.Text = total;
                PausedOnlinePanel.Visibility = string.IsNullOrWhiteSpace(total) ? Visibility.Collapsed : Visibility.Visible;
            }
            catch (Exception) { PausedOnlinePanel.Visibility = Visibility.Collapsed; }
        }

        private async Task SearchPausedTagAsync(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag) || _disposed) return;
            ClosePausedRecommendations(false);
            SearchInput.Text = tag;
            await SearchAsync();
        }

        private bool PausedTagsAvailable => PausedTagsScroll.Visibility == Visibility.Visible && PausedTagsPanel.Children.Count > 0;

        private int FocusedPausedTag()
        {
            var focused = FocusManager.GetFocusedElement() as Button;
            return focused == null ? -1 : PausedTagsPanel.Children.IndexOf(focused);
        }

        private void FocusPausedTag(int index)
        {
            if (!PausedTagsAvailable) return;
            ++_pausedRecommendationFocus;
            _pausedFocusGrid = null;
            _pausedTagIndex = Math.Max(0, Math.Min(PausedTagsPanel.Children.Count - 1, index));
            var button = PausedTagsPanel.Children[_pausedTagIndex] as Button;
            if (button != null && button.Focus(FocusState.Keyboard)) button.StartBringIntoView();
        }
    }
}
