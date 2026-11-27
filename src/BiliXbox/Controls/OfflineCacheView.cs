using System;
using System.Collections.Generic;
using System.Linq;
using BiliXbox.Services;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace BiliXbox.Controls
{
    // 离线缓存: stored and downloading parts, one row each with 播放,
    // 暂停/继续 and 删除, plus the console's free space. Navigation is driven
    // by AccountLibraryView (rows up/down, buttons left/right).
    public sealed class OfflineCacheView : UserControl
    {
        private sealed class Row
        {
            public OfflineCacheEntry Entry;
            public Grid Root;
            public Image Cover;
            public TextBlock Title, Detail;
            public ProgressBar Progress;
            public Button Play, Danmaku, Toggle, Delete;
            public DateTimeOffset DeleteArmed, NoticeUntil;
            public string Notice = "";
            public bool DanmakuBusy;
            public Button[] Buttons => new[] { Play, Danmaku, Toggle, Delete };
        }

        private readonly StackPanel _list = new StackPanel { Spacing = 12 };
        private readonly ScrollViewer _scroll;
        private readonly TextBlock _space, _empty, _summary;
        private readonly List<Row> _rows = new List<Row>();
        private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private int _row, _column;

        public event Action<OfflineCacheEntry> PlayRequested;

        public OfflineCacheView()
        {
            RequiresPointer = RequiresPointer.Never;
            var root = new Grid { RowSpacing = 10 };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _summary = Text("离线缓存", 22, true);
            root.Children.Add(_summary);
            _scroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, IsTabStop = false, Padding = new Thickness(0, 4, 0, 4) };
            Grid.SetRow(_scroll, 1); root.Children.Add(_scroll);
            _empty = Text("还没有缓存的视频。在播放页点击右上角的“缓存”，选择画质即可下载。", 20);
            _empty.Foreground = Secondary(); _empty.TextWrapping = TextWrapping.Wrap; _empty.HorizontalAlignment = HorizontalAlignment.Center;
            _empty.VerticalAlignment = VerticalAlignment.Center; _empty.MaxWidth = 760; _empty.TextAlignment = TextAlignment.Center;
            Grid.SetRow(_empty, 1); root.Children.Add(_empty);
            _space = Text("Xbox 剩余存储空间：读取中…", 15); _space.Foreground = Secondary();
            Grid.SetRow(_space, 2); root.Children.Add(_space);
            Content = root;
            _timer.Tick += (s, e) => Refresh();

            Loaded += (s, e) => { if (Visibility == Visibility.Visible) Show(); };
            Unloaded += (s, e) => _timer.Stop();
        }

        public void Show()
        {
            _ = OfflineCache.Instance.LoadAsync();
            Refresh();
            UpdateSpace();
            _timer.Start();
        }

        public void Hide() => _timer.Stop();

        private int _spaceTicks;

        private async void UpdateSpace()
        {
            var free = await OfflineCache.GetFreeSpaceAsync();
            _space.Text = "Xbox 剩余存储空间：" + (free.HasValue ? OfflineCache.FormatBytes(free.Value) : "未知");
        }

        private void Refresh()
        {
            if (++_spaceTicks % 10 == 0) UpdateSpace();
            var entries = OfflineCache.Instance.Entries;
            bool sameSet = entries.Count == _rows.Count && entries.Zip(_rows, (entry, row) => ReferenceEquals(entry, row.Entry)).All(x => x);
            if (!sameSet) Rebuild(entries);
            foreach (var row in _rows) UpdateRow(row);
            _empty.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            long total = entries.Where(entry => entry.State == OfflineCacheState.Completed).Sum(entry => entry.TotalBytes);
            int downloading = entries.Count(entry => entry.State != OfflineCacheState.Completed);
            _summary.Text = "离线缓存 · 已缓存 " + entries.Count(entry => entry.State == OfflineCacheState.Completed) + " 个 · 占用 " + OfflineCache.FormatBytes(total)
                + (downloading > 0 ? " · 未完成 " + downloading + " 个" : "");
        }

        private void Rebuild(IReadOnlyList<OfflineCacheEntry> entries)
        {
            var focused = FocusManager.GetFocusedElement() as Button;
            bool hadFocus = _rows.Any(row => row.Buttons.Contains(focused));
            _rows.Clear(); _list.Children.Clear();
            foreach (var entry in entries) { var row = BuildRow(entry); _rows.Add(row); _list.Children.Add(row.Root); }
            _row = Math.Max(0, Math.Min(_row, _rows.Count - 1));
            if (hadFocus) FocusCurrent();
        }

        private Row BuildRow(OfflineCacheEntry entry)
        {
            var row = new Row { Entry = entry };
            var grid = new Grid { ColumnSpacing = 18, Padding = new Thickness(14), CornerRadius = new CornerRadius(14), Background = Brush(255, 28, 28, 30) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(192) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Cover = new Image { Width = 192, Height = 108, Stretch = Stretch.UniformToFill };
            var coverBorder = new Border { CornerRadius = new CornerRadius(10), Background = Brush(255, 44, 44, 46), Child = row.Cover, Width = 192, Height = 108 };
            grid.Children.Add(coverBorder);
            LoadCover(row);
            var info = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            row.Title = Text(entry.Title + (string.IsNullOrWhiteSpace(entry.PartTitle) ? "" : " · " + entry.PartTitle), 20, true);
            row.Title.TextTrimming = TextTrimming.CharacterEllipsis; row.Title.MaxLines = 1;
            row.Detail = Text("", 15); row.Detail.Foreground = Secondary(); row.Detail.TextTrimming = TextTrimming.CharacterEllipsis;
            row.Progress = new ProgressBar { Minimum = 0, Maximum = 1, Height = 6, Foreground = Brush(255, 0, 161, 214) };
            info.Children.Add(row.Title); info.Children.Add(row.Detail); info.Children.Add(row.Progress);
            Grid.SetColumn(info, 1); grid.Children.Add(info);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
            row.Play = MakeButton("播放", () => { if (row.Entry.State == OfflineCacheState.Completed) PlayRequested?.Invoke(row.Entry); });
            row.Danmaku = MakeButton("更新弹幕", () => RefreshDanmaku(row));
            row.Toggle = MakeButton("暂停", () => Toggle(row));
            row.Delete = MakeButton("删除", () => Delete(row));
            buttons.Children.Add(row.Play); buttons.Children.Add(row.Danmaku); buttons.Children.Add(row.Toggle); buttons.Children.Add(row.Delete);
            Grid.SetColumn(buttons, 2); grid.Children.Add(buttons);
            row.Root = grid;
            return row;
        }

        private static async void LoadCover(Row row)
        {
            try
            {
                var folder = await OfflineCache.EntryFolderAsync(row.Entry);
                var file = await folder.TryGetItemAsync("cover.jpg") as Windows.Storage.StorageFile;
                if (file == null) return;
                using (var stream = await file.OpenReadAsync())
                {
                    var bitmap = new BitmapImage { DecodePixelWidth = 384 };
                    await bitmap.SetSourceAsync(stream);
                    row.Cover.Source = bitmap;
                }
            }
            catch (Exception) { }
        }

        private void UpdateRow(Row row)
        {
            var entry = row.Entry;
            string size = OfflineCache.FormatBytes(entry.TotalBytes);
            string detail = entry.QualityLabel + " · ";
            switch (entry.State)
            {
                case OfflineCacheState.Completed:
                    detail += size + " · 已完成"; break;
                case OfflineCacheState.Downloading:
                    detail += (entry.Progress * 100).ToString("0.0") + "% · " + OfflineCache.FormatBytes(entry.DoneBytes) + " / " + size;
                    if (entry.BytesPerSecond > 0)
                    {
                        detail += " · " + OfflineCache.FormatBytes(entry.BytesPerSecond) + "/s";
                        double seconds = Math.Max(0, entry.TotalBytes - entry.DoneBytes) / entry.BytesPerSecond;
                        detail += " · 剩余 " + FormatEta(seconds);
                    }
                    else detail += " · 连接中…";
                    break;
                case OfflineCacheState.Paused:
                    detail += "已暂停 · " + (entry.Progress * 100).ToString("0.0") + "% · " + OfflineCache.FormatBytes(entry.DoneBytes) + " / " + size; break;
                case OfflineCacheState.Failed:
                    detail += "下载失败" + (string.IsNullOrWhiteSpace(entry.Error) ? "" : "：" + entry.Error) + " · 选择“重试”继续"; break;
                default:
                    detail += "等待下载 · " + size; break;
            }
            if (DateTimeOffset.UtcNow < row.NoticeUntil) detail += " · " + row.Notice;
            row.Detail.Text = detail;
            row.Danmaku.Content = row.DanmakuBusy ? "更新中…" : "更新弹幕";
            row.Progress.Value = entry.Progress;
            row.Progress.Visibility = entry.State == OfflineCacheState.Completed ? Visibility.Collapsed : Visibility.Visible;
            row.Play.IsEnabled = entry.State == OfflineCacheState.Completed;
            row.Toggle.Visibility = entry.State == OfflineCacheState.Completed ? Visibility.Collapsed : Visibility.Visible;
            row.Toggle.Content = entry.State == OfflineCacheState.Downloading || entry.State == OfflineCacheState.Queued ? "暂停"
                : entry.State == OfflineCacheState.Failed ? "重试" : "继续";
            bool armed = (DateTimeOffset.UtcNow - row.DeleteArmed).TotalSeconds < 4;
            row.Delete.Content = armed ? "确认删除" : "删除";
        }

        private static string FormatEta(double seconds)
        {
            if (double.IsInfinity(seconds) || double.IsNaN(seconds)) return "--";
            var span = TimeSpan.FromSeconds(Math.Min(seconds, 359999));
            return span.TotalHours >= 1 ? (int)span.TotalHours + " 小时 " + span.Minutes + " 分" : span.TotalMinutes >= 1 ? span.Minutes + " 分 " + span.Seconds + " 秒" : span.Seconds + " 秒";
        }

        private async void RefreshDanmaku(Row row)
        {
            if (row.DanmakuBusy) return;
            row.DanmakuBusy = true;
            UpdateRow(row);
            try
            {
                int count = await OfflineCache.Instance.RefreshDanmakuAsync(row.Entry);
                row.Notice = "弹幕已更新，共 " + count + " 条";
            }
            catch (Exception ex) { row.Notice = "弹幕更新失败：" + ex.Message; }
            row.DanmakuBusy = false;
            row.NoticeUntil = DateTimeOffset.UtcNow.AddSeconds(6);
            UpdateRow(row);
            if (FocusManager.GetFocusedElement() == null) FocusCurrent();
        }

        private void Toggle(Row row)
        {
            var entry = row.Entry;
            if (entry.State == OfflineCacheState.Downloading || entry.State == OfflineCacheState.Queued) OfflineCache.Instance.Pause(entry);
            else OfflineCache.Instance.Resume(entry);
            UpdateRow(row);
        }

        private async void Delete(Row row)
        {
            if ((DateTimeOffset.UtcNow - row.DeleteArmed).TotalSeconds >= 4)
            {
                row.DeleteArmed = DateTimeOffset.UtcNow;
                UpdateRow(row);
                return;
            }
            int index = _rows.IndexOf(row);
            await OfflineCache.Instance.DeleteAsync(row.Entry);
            _row = Math.Max(0, index - 1 < 0 ? 0 : Math.Min(index, _rows.Count - 2));
            Refresh();
            if (_rows.Count > 0) FocusCurrent();
            UpdateSpace();
        }

        // Focus management, driven by AccountLibraryView.
        public bool HasRows => _rows.Count > 0;

        public void FocusCurrent()
        {
            if (_rows.Count == 0) return;
            _row = Math.Max(0, Math.Min(_row, _rows.Count - 1));
            var buttons = Available(_rows[_row]);
            if (buttons.Length == 0) return;
            _column = Math.Max(0, Math.Min(_column, buttons.Length - 1));
            var target = buttons[_column];
            target.Focus(FocusState.Keyboard);
            _rows[_row].Root.StartBringIntoView();
        }

        private static Button[] Available(Row row) => row.Buttons.Where(button => button.Visibility == Visibility.Visible && button.IsEnabled).ToArray();

        // false: leave the list upwards (to the page header).
        internal bool Navigate(PlayerDirection direction)
        {
            if (_rows.Count == 0) return false;
            SyncFromFocus();
            if (direction == PlayerDirection.Up) { if (_row == 0) return false; --_row; }
            else if (direction == PlayerDirection.Down) _row = Math.Min(_rows.Count - 1, _row + 1);
            else
            {
                var buttons = Available(_rows[_row]);
                _column = Math.Max(0, Math.Min(buttons.Length - 1, _column + (direction == PlayerDirection.Left ? -1 : 1)));
            }
            FocusCurrent();
            return true;
        }

        public bool Confirm()
        {
            var focused = FocusManager.GetFocusedElement() as Button;
            foreach (var row in _rows)
                if (row.Buttons.Contains(focused))
                {
                    if (focused == row.Play) { if (row.Entry.State == OfflineCacheState.Completed) PlayRequested?.Invoke(row.Entry); }
                    else if (focused == row.Danmaku) RefreshDanmaku(row);
                    else if (focused == row.Toggle) Toggle(row);
                    else if (focused == row.Delete) Delete(row);
                    return true;
                }
            return false;
        }

        private void SyncFromFocus()
        {
            var focused = FocusManager.GetFocusedElement() as Button;
            for (int i = 0; i < _rows.Count; ++i)
            {
                var buttons = Available(_rows[i]);
                int index = Array.IndexOf(buttons, focused);
                if (index >= 0) { _row = i; _column = index; return; }
            }
        }

        private static Button MakeButton(string text, Action action)
        {
            var button = new Button { Content = text, Style = (Style)Application.Current.Resources["TvButtonStyle"], RequiresPointer = RequiresPointer.Never, MinWidth = 96 };
            button.Click += (s, e) => action();
            return button;
        }

        private static TextBlock Text(string text, double size, bool bold = false)
            => new TextBlock { Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Foreground = Brush(255, 242, 242, 247) };
        private static SolidColorBrush Brush(byte a, byte r, byte g, byte b) => new SolidColorBrush(Color.FromArgb(a, r, g, b));
        private static SolidColorBrush Secondary() => Brush(255, 142, 142, 147);
    }
}
