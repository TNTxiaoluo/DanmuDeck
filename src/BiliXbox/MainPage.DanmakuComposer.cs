using System;
using BiliXbox.Models;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace BiliXbox
{
    public sealed partial class MainPage
    {
        private int _postDanmakuColor = 0xffffff, _postDanmakuMode = 1, _postDanmakuFontSize = 25;
        private FrameworkElement CreateDanmakuPostOptions()
        {
            bool premium = _api.IsLoggedIn && _api.CurrentUser?.IsPremium == true;
            if (!premium) _postDanmakuColor = 0xffffff;
            var panel = new StackPanel { Spacing = 10 };
            var row = new Grid { ColumnSpacing = 12 };
            for (int i = 0; i < 3; i++) row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var colors = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 150 };
            string[] names = { "白色", "红色", "橙色", "黄色", "绿色", "青色", "蓝色", "粉色" };
            int[] values = { 0xffffff, 0xff0000, 0xffa500, 0xffff00, 0x00cc66, 0x00ffff, 0x3399ff, 0xfb7299 };
            for (int i = 0; i < names.Length; i++)
            {
                int value = values[i];
                var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                label.Children.Add(new Border { Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center,
                    Background = new SolidColorBrush(Color.FromArgb(255, (byte)(value >> 16), (byte)(value >> 8), (byte)value)) });
                label.Children.Add(new TextBlock { Text = names[i] + (i > 0 && !premium ? "（需要大会员）" : ""), VerticalAlignment = VerticalAlignment.Center });
                var item = new ComboBoxItem { Content = label, Tag = value, IsEnabled = i == 0 || premium };
                colors.Items.Add(item); if (value == _postDanmakuColor) colors.SelectedItem = item;
            }
            colors.SelectionChanged += (s, e) => { if (colors.SelectedItem is ComboBoxItem item) _postDanmakuColor = (int)item.Tag; };
            var modes = PostOptionCombo(new[] { "滚动", "置顶", "置底" }, new[] { 1, 5, 4 }, _postDanmakuMode, value => _postDanmakuMode = value);
            var sizes = PostOptionCombo(new[] { "默认", "较小", "大" }, new[] { 25, 18, 36 }, _postDanmakuFontSize, value => _postDanmakuFontSize = value);
            ComboBox[] fields = { colors, modes, sizes }; string[] titles = { "弹幕颜色", "弹幕类型", "字号" };
            for (int i = 0; i < fields.Length; i++)
            {
                var field = new StackPanel { Spacing = 6 };
                field.Children.Add(new TextBlock { Text = titles[i], FontSize = 15 }); field.Children.Add(fields[i]);
                Windows.UI.Xaml.Automation.AutomationProperties.SetName(fields[i], titles[i]);
                Grid.SetColumn(field, i); row.Children.Add(field);
            }
            panel.Children.Add(row);
            panel.Children.Add(new TextBlock { Text = "B 收起键盘后可选择样式；彩色弹幕需要大会员。", FontSize = 13, Opacity = .7, TextWrapping = TextWrapping.Wrap });
            return panel;
        }
        private static ComboBox PostOptionCombo(string[] labels, int[] values, int selected, Action<int> changed)
        {
            var box = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 115 };
            for (int i = 0; i < labels.Length; i++)
            { var item = new ComboBoxItem { Content = labels[i], Tag = values[i] }; box.Items.Add(item); if (values[i] == selected) box.SelectedItem = item; }
            box.SelectionChanged += (s, e) => { if (box.SelectedItem is ComboBoxItem item) changed((int)item.Tag); };
            return box;
        }
    }
}
