using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Controls;
using BiliXbox.Services;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace BiliXbox
{
    public sealed partial class MainPage
    {
        // Set while a two-column TV panel (the search page) owns direction keys.
        private Func<PlayerDirection, Button, bool> _tvPanelGridNavigation;
        private CancellationTokenSource _searchPageCts;

        // Search opens a full-screen page: the search bar on top, trending
        // searches on the left and this device's recent searches on the right.
        // Choosing the bar opens the native keyboard inside the page.
        private async Task ShowSearchPageAsync()
        {
            if (_disposed || _dialogOpen) return;
            string chosen = null;
            var rows = new List<Button>();
            var trending = new List<Button>();
            var recent = new List<Button>();
            int lastColumn = 0;

            const string placeholder = "搜索视频或用户，支持 BV 号 / 视频链接";
            string current = _feedNavigation.IsSearch ? SearchInput.Text?.Trim() ?? "" : "";
            // The bar shows a real search glyph (the row style's text font has
            // none) and opens the native keyboard on an editor inside this page,
            // so the page stays visible while typing.
            var barLabel = new TextBlock { Text = current.Length > 0 ? current : placeholder, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis };
            var barContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
            barContent.Children.Add(new FontIcon { Glyph = "", FontFamily = new Windows.UI.Xaml.Media.FontFamily("Segoe MDL2 Assets"), FontSize = 20 });
            barContent.Children.Add(barLabel);
            var barCell = new Grid();
            Button bar = null;
            bar = CreateTvPanelRow("", () => { var _ = EditSearchAsync(); });
            bar.Content = barContent;
            bar.HorizontalAlignment = HorizontalAlignment.Stretch;
            bar.HorizontalContentAlignment = HorizontalAlignment.Left;
            bar.FontSize = 20;
            Windows.UI.Xaml.Automation.AutomationProperties.SetName(bar, "搜索");
            barCell.Children.Add(bar);
            rows.Add(bar);

            async Task EditSearchAsync()
            {
                if (_activeInlineTextInput != null) return;
                var editor = new TextBox { Text = current, PlaceholderText = placeholder, MaxLength = 200, FontSize = 20,
                    MinHeight = 56, Padding = new Thickness(18, 12, 18, 10), IsSpellCheckEnabled = false };
                barCell.Children.Add(editor);
                bar.Visibility = Visibility.Collapsed;
                bool submitted = false;
                try { submitted = await ShowInlineTextInputAsync(editor, true); }
                finally
                {
                    barCell.Children.Remove(editor);
                    bar.Visibility = Visibility.Visible;
                }
                string text = editor.Text?.Trim() ?? "";
                if (text.Length > 0) { current = text; barLabel.Text = text; }
                if (submitted && text.Length > 0 && IsTvPanelOpen) { chosen = text; FinishTvPanel(true); }
                else if (IsTvPanelOpen) FocusGamepadControl(bar);
            }

            var trendingPanel = new StackPanel { Spacing = 8 };
            var recentPanel = new StackPanel { Spacing = 8 };
            var trendingNotice = Notice("正在读取 B 站热搜…");
            trendingPanel.Children.Add(Header("推荐搜索"));
            trendingPanel.Children.Add(trendingNotice);
            recentPanel.Children.Add(Header("最近搜索"));

            void Choose(string keyword) { chosen = keyword; FinishTvPanel(true); }
            Button Keyword(string text, string keyword)
            {
                var button = CreateTvPanelRow(text, () => Choose(keyword));
                button.HorizontalAlignment = HorizontalAlignment.Stretch;
                return button;
            }
            void BuildRecent()
            {
                foreach (var button in recent) rows.Remove(button);
                recent.Clear();
                while (recentPanel.Children.Count > 1) recentPanel.Children.RemoveAt(1);
                var history = SearchHistoryStore.Load();
                if (history.Count == 0) { recentPanel.Children.Add(Notice("还没有搜索记录。")); return; }
                foreach (var keyword in history) recent.Add(Keyword(keyword, keyword));
                var clear = CreateTvPanelRow("清空最近搜索", () =>
                {
                    SearchHistoryStore.Clear();
                    BuildRecent();
                    FocusGamepadControl(trending.Count > 0 ? trending[0] : bar);
                });
                clear.HorizontalAlignment = HorizontalAlignment.Stretch;
                clear.Opacity = 0.8;
                recent.Add(clear);
                foreach (var button in recent) { recentPanel.Children.Add(button); rows.Add(button); }
            }
            BuildRecent();

            var columns = new Grid { ColumnSpacing = 40, Margin = new Thickness(0, 24, 0, 0) };
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(recentPanel, 1);
            columns.Children.Add(trendingPanel);
            columns.Children.Add(recentPanel);
            var content = new StackPanel();
            content.Children.Add(barCell);
            content.Children.Add(columns);

            _tvPanelGridNavigation = (direction, focused) =>
            {
                var lists = new[] { trending, recent };
                int column = trending.Contains(focused) ? 0 : recent.Contains(focused) ? 1 : -1;
                Button target = null;
                if (column < 0)
                {
                    if (direction == PlayerDirection.Down)
                    {
                        if (focused == TvPanelCloseButton) target = bar;
                        else if (focused == bar) target = lists[lastColumn].FirstOrDefault() ?? lists[1 - lastColumn].FirstOrDefault();
                    }
                    else if (direction == PlayerDirection.Up && focused == bar) target = TvPanelCloseButton;
                    else if (focused == null) target = bar;
                }
                else
                {
                    var list = lists[column];
                    int index = list.IndexOf(focused);
                    lastColumn = column;
                    if (direction == PlayerDirection.Up) target = index == 0 ? bar : list[index - 1];
                    else if (direction == PlayerDirection.Down) target = list[Math.Min(list.Count - 1, index + 1)];
                    else
                    {
                        var other = lists[1 - column];
                        if (other.Count > 0) { target = other[Math.Min(other.Count - 1, index)]; lastColumn = 1 - column; }
                    }
                }
                if (target != null) FocusGamepadControl(target);
                return true;
            };

            Cancel(ref _searchPageCts);
            var owner = _searchPageCts = new CancellationTokenSource();
            _ = LoadTrendingAsync();
            async Task LoadTrendingAsync()
            {
                try
                {
                    var words = await _api.GetTrendingSearchesAsync(10, owner.Token);
                    if (owner.IsCancellationRequested) return;
                    trendingPanel.Children.Remove(trendingNotice);
                    if (words.Count == 0) { trendingPanel.Children.Add(Notice("B 站暂未提供热搜。")); return; }
                    int rank = 0;
                    foreach (var word in words)
                    {
                        var button = Keyword((++rank).ToString() + "   " + word, word);
                        trending.Add(button); rows.Add(button); trendingPanel.Children.Add(button);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    if (!owner.IsCancellationRequested) trendingNotice.Text = "热搜暂时无法读取：" + Friendly(ex);
                }
            }

            double width = TvPanelBox.Width;
            var alignment = TvPanelBox.HorizontalAlignment;
            TvPanelBox.Width = double.NaN;
            TvPanelBox.HorizontalAlignment = HorizontalAlignment.Stretch;
            bool accepted;
            try { accepted = await ShowTvPanelAsync("搜索", content, rows, bar); }
            finally
            {
                _tvPanelGridNavigation = null;
                Cancel(ref _searchPageCts);
                TvPanelBox.Width = width;
                TvPanelBox.HorizontalAlignment = alignment;
            }
            if (!accepted || _disposed) return;
            if (string.IsNullOrWhiteSpace(chosen)) return;
            SearchInput.Text = chosen;
            // A search started from this page lists videos; the results page
            // still offers the 用户 tab.
            _feedNavigation.SelectSearchMode(FeedSearchMode.Videos);
            await SearchAsync();

            TextBlock Header(string text) => new TextBlock { Text = text, FontSize = 20, FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xF2, 0xF2, 0xF7)), Margin = new Thickness(0, 0, 0, 4) };
            TextBlock Notice(string text) => new TextBlock { Text = text, FontSize = 16, Opacity = 0.7, TextWrapping = TextWrapping.Wrap,
                Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xF2, 0xF2, 0xF7)) };
        }
    }
}
