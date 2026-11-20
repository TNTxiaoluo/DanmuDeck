using System;
using System.Collections.Generic;
using System.Globalization;
using BiliXbox.Models;
using BiliXbox.Services;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace BiliXbox.Controls
{
    public sealed partial class LiveHubView
    {
        private readonly DanmakuSettingsStore _liveSettingsStore = new DanmakuSettingsStore("live.danmaku.preferences.v1");
        private readonly Dictionary<string, Button> _liveSettingButtons = new Dictionary<string, Button>();
        private DanmakuSettings _liveSettings;

        private void InitializeLiveDanmakuSettings()
        {
            _liveSettings = _liveSettingsStore.Load().With(smartMask: false);
            _danmaku = _liveSettingsStore.LoadEnabled();
            _overlay.ApplySettings(_liveSettings);
            _overlay.Visibility = _danmaku ? Visibility.Visible : Visibility.Collapsed;
            _danmakuButton.Content = _danmaku ? "弹幕 开" : "弹幕 关";
        }

        private void RenderLiveDanmakuSettings()
        {
            _drawerNotice.Text = "A 切换选项，即时生效并自动保存 · B 返回。直播与普通视频的弹幕设置分别保存。";
            foreach (var option in new[] { "enabled", "area", "opacity", "font", "speed", "density", "overlap", "merge", "center", "scroll", "top", "bottom", "color", "reset" })
            {
                string key = option;
                _liveSettingButtons[key] = Row(LiveSettingLabel(key), () => ChangeLiveDanmakuSetting(key), "LiveDanmakuSetting_" + key);
            }
            FocusFirstRow();
        }

        private static double CycleLiveSetting(double value, params double[] options)
        { int at = Array.FindIndex(options, candidate => Math.Abs(candidate - value) < .005); return options[(at + 1) % options.Length]; }

        private void ChangeLiveDanmakuSetting(string option)
        {
            var s = _liveSettings;
            switch (option)
            {
                case "enabled": ToggleDanmaku(); return;
                case "area": s = s.With(areaFraction: CycleLiveSetting(s.AreaFraction, 1, .75, .5, .25)); break;
                case "opacity": s = s.With(opacity: CycleLiveSetting(s.Opacity, .9, .75, .5, .25, 1)); break;
                case "font": s = s.With(fontScale: CycleLiveSetting(s.FontScale, .75, 1, 1.25, 1.5)); break;
                case "speed": s = s.With(speedScale: CycleLiveSetting(s.SpeedScale, .75, 1, 1.25, 1.5, 2)); break;
                case "density": s = s.With(densityLimit: (int)CycleLiveSetting(s.DensityLimit, 12, 24, 36, 48, 64)); break;
                case "overlap": s = s.With(preventOverlap: !s.PreventOverlap); break;
                case "merge": s = s.With(mergeDuplicates: !s.MergeDuplicates); break;
                case "center": s = s.With(avoidCenter: !s.AvoidCenter); break;
                case "scroll": s = s.With(showScrolling: !s.ShowScrolling); break;
                case "top": s = s.With(showTop: !s.ShowTop); break;
                case "bottom": s = s.With(showBottom: !s.ShowBottom); break;
                case "color": s = s.With(showColor: !s.ShowColor); break;
                case "reset": s = DanmakuSettings.Default.With(smartMask: false); _danmaku = true; break;
                default: return;
            }
            _liveSettings = s;
            _overlay.ApplySettings(s);
            _overlay.Visibility = _danmaku ? Visibility.Visible : Visibility.Collapsed;
            _danmakuButton.Content = _danmaku ? "弹幕 开" : "弹幕 关";
            try { _liveSettingsStore.Save(s); _liveSettingsStore.SaveEnabled(_danmaku); }
            catch (Exception ex) { _drawerNotice.Text = "设置已生效，但暂时无法保存：" + Friendly(ex); }
            RefreshLiveDanmakuSettings();
        }

        private void RefreshLiveDanmakuSettings()
        {
            // Keep existing controls and focus when cycling a setting.
            foreach (var pair in _liveSettingButtons)
                if (pair.Value.Content is TextBlock text) text.Text = LiveSettingLabel(pair.Key);
        }
        private string LiveSettingLabel(string option)
        {
            var s = _liveSettings;
            Func<bool, string> state = enabled => enabled ? "开" : "关";
            switch (option)
            {
                case "enabled": return "直播弹幕：" + state(_danmaku);
                case "area": return "显示区域：" + (s.AreaFraction > .99 ? "全屏" : s.AreaFraction > .74 ? "3/4 屏" : s.AreaFraction > .49 ? "1/2 屏" : "1/4 屏");
                case "opacity": return "不透明度：" + Math.Round(s.Opacity * 100) + "%";
                case "font": return "字号：" + s.FontScale.ToString("0.##", CultureInfo.InvariantCulture) + " 倍";
                case "speed": return "滚动速度：" + s.SpeedScale.ToString("0.##", CultureInfo.InvariantCulture) + " 倍";
                case "density": return "同屏弹幕上限：" + s.DensityLimit;
                case "overlap": return "防止弹幕重叠：" + state(s.PreventOverlap);
                case "merge": return "过滤短时间重复弹幕：" + state(s.MergeDuplicates);
                case "center": return "避开画面中央：" + state(s.AvoidCenter);
                case "scroll": return "滚动弹幕：" + state(s.ShowScrolling);
                case "top": return "顶部弹幕：" + state(s.ShowTop);
                case "bottom": return "底部弹幕：" + state(s.ShowBottom);
                case "color": return "彩色弹幕：" + state(s.ShowColor);
                default: return "恢复直播弹幕默认设置";
            }
        }
    }
}
