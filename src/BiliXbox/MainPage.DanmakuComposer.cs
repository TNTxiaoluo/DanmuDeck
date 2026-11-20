using System;
using System.Threading.Tasks;
using BiliXbox.Controls;
using BiliXbox.Models;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace BiliXbox
{
    public sealed partial class MainPage
    {
        private int _postDanmakuColor = 0xffffff, _postDanmakuMode = 1, _postDanmakuFontSize = 25;
        private bool _postingDanmaku;

        private async void DanmakuEntry_Click(object sender, RoutedEventArgs e)
        {
            if (IgnorePlayerClick() || _disposed || _dialogOpen || _postingDanmaku || _video == null || _watchCts == null) return;
            if (!_api.IsLoggedIn) { await ShowLoginAsync(); return; }
            if (_media.Source == null) { SetStatus("视频就绪后才能发送弹幕"); return; }

            var watch = _watchCts;
            long cid = _currentCid;
            long account = CurrentAccountMid;
            bool premium = _api.CurrentUser?.IsPremium == true;
            if (!premium) _postDanmakuColor = 0xffffff;
            _dialogOpen = true;
            string draft = null;
            try
            {
                DisablePointerCursor();
                var buttonStyle = (Style)Application.Current.Resources["TvButtonStyle"];
                var dialog = new NativeTextInputDialog("", "发送弹幕", "发送", 100, CreateDanmakuPostOptions(premium))
                {
                    Title = new TextBlock { Text = "发送弹幕", FontSize = 24, FontWeight = Windows.UI.Text.FontWeights.SemiBold },
                    DefaultButton = ContentDialogButton.None,
                    PrimaryButtonStyle = buttonStyle,
                    CloseButtonStyle = buttonStyle,
                    Background = new SolidColorBrush(Color.FromArgb(250, 22, 23, 29)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(38, 255, 255, 255)),
                    CornerRadius = new CornerRadius(24)
                };
                dialog.Resources["ContentDialogMaxWidth"] = 568d;
                dialog.Resources["ContentDialogPadding"] = new Thickness(28, 24, 28, 24);
                AutomationProperties.SetAutomationId(dialog, "SendDanmakuDialog");
                _activeGeneralDialog = dialog;
                if (await dialog.ShowAsync() == ContentDialogResult.Primary) draft = dialog.QueryText;
            }
            catch (Exception ex) { if (!_disposed) SetStatus("弹幕输入未能打开：" + Friendly(ex)); }
            finally
            {
                _activeGeneralDialog = null;
                _dialogOpen = false;
                _inputPaneVisible = false;
                _lastBack = DateTimeOffset.UtcNow;
                RestoreNavigationFocus();
            }
            if (draft != null && !_disposed && watch == _watchCts && cid == _currentCid && account == CurrentAccountMid)
                await SendDanmakuAsync(draft);
        }

        private FrameworkElement CreateDanmakuPostOptions(bool premium)
        {
            var panel = new StackPanel { Spacing = 8 };
            var row = new Grid { ColumnSpacing = 10 };
            var color = CreateDanmakuStyleButton("颜色",
                new[] { "白色", "红色", "橙色", "黄色", "绿色", "青色", "蓝色", "粉色" },
                new[] { 0xffffff, 0xff0000, 0xffa500, 0xffff00, 0x00cc66, 0x00ffff, 0x3399ff, 0xfb7299 },
                _postDanmakuColor, value => _postDanmakuColor = value);
            color.IsEnabled = premium;
            var type = CreateDanmakuStyleButton("类型", new[] { "滚动", "置顶", "置底" }, new[] { 1, 5, 4 },
                _postDanmakuMode, value => _postDanmakuMode = value);
            var size = CreateDanmakuStyleButton("字号", new[] { "默认", "较小", "大" }, new[] { 25, 18, 36 },
                _postDanmakuFontSize, value => _postDanmakuFontSize = value);
            foreach (var button in new[] { color, type, size })
            {
                Grid.SetColumn(button, row.ColumnDefinitions.Count);
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.Children.Add(button);
            }
            panel.Children.Add(row);
            if (!premium)
                panel.Children.Add(new TextBlock { Text = "彩色弹幕需要大会员", FontSize = 13, Opacity = .7 });
            return panel;
        }

        private static Button CreateDanmakuStyleButton(string title, string[] labels, int[] values, int selected, Action<int> change)
        {
            int index = Math.Max(0, Array.IndexOf(values, selected));
            var button = new Button
            {
                Style = (Style)Application.Current.Resources["TvButtonStyle"],
                Content = title + " · " + labels[index], FontSize = 15, Padding = new Thickness(8, 5, 8, 5),
                HorizontalAlignment = HorizontalAlignment.Stretch, IsFocusEngagementEnabled = false
            };
            button.Click += (sender, args) =>
            {
                index = (index + 1) % values.Length;
                change(values[index]);
                button.Content = title + " · " + labels[index];
            };
            return button;
        }

        private void CloseActiveGeneralDialog()
        {
            if (_activeGeneralDialog is NativeTextInputDialog composer) composer.Hide();
            else
            {
                (_activeGeneralDialog as CommentReaderDialog)?.Dispose();
                _activeGeneralDialog?.Hide();
            }
        }

        private async Task SendDanmakuAsync(string draft)
        {
            if (_postingDanmaku || _video == null || _currentCid == 0 || _watchCts == null) return;
            string text = draft.Trim();
            if (text.Length == 0 || !_api.IsLoggedIn) return;
            if (_media.Source == null) { SetStatus("视频就绪后才能发送弹幕"); return; }
            var owner = _watchCts;
            var token = owner.Token;
            long aid = _video.Aid, cid = _currentCid;
            var position = _media.PlaybackSession.Position;
            int color = _postDanmakuColor, mode = _postDanmakuMode, fontSize = _postDanmakuFontSize;
            _postingDanmaku = true;
            try
            {
                await _api.SendDanmakuAsync(aid, cid, text, position, color, mode, ct: token, fontSize: fontSize);
                if (_watchCts == owner && _currentCid == cid && !token.IsCancellationRequested)
                {
                    Danmaku.Add(new DanmakuItem { Time = position.TotalSeconds, Text = text, Color = color, Mode = mode, FontSize = fontSize });
                    SetStatus("弹幕已提交，展示以 Bilibili 的处理结果为准");
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { if (!token.IsCancellationRequested) SetStatus("弹幕发送未确认：" + Friendly(ex) + "；请确认后再试"); }
            finally { _postingDanmaku = false; if (!_disposed) UpdateAccount(); }
        }
    }
}
