using System;
using System.Threading.Tasks;
using BiliXbox.Services;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace BiliXbox
{
    public sealed partial class MainPage
    {
        private async Task ShowNetworkSettingsAsync()
        {
            if (_disposed || _dialogOpen) return;
            bool enabled = NetworkAccelerationSettings.Enabled;
            bool followSystemHdmi = HdmiOutputSettings.FollowSystem;
            PlaybackDiagnosticsLog.Record("network-settings-open enabled=" + enabled + " followSystemHdmi=" + followSystemHdmi);
            var panel = new StackPanel { Spacing = 12 };
            Button toggle = null;
            string ToggleLabel() => "网络加速 · 测试版 · " + (enabled ? "已启用" : "已关闭");
            toggle = CreateTvPanelRow(ToggleLabel(), () =>
            {
                enabled = !enabled;
                toggle.Content = ToggleLabel();
                PlaybackDiagnosticsLog.Record("network-settings-draft enabled=" + enabled + " inputArmed=True");
            });
            Button hdmi = null;
            string HdmiLabel() => "HDMI 输出 · " + (followSystemHdmi ? "跟随系统设置（推荐）" : "按片源切换");
            hdmi = CreateTvPanelRow(HdmiLabel(), () =>
            {
                followSystemHdmi = !followSystemHdmi;
                hdmi.Content = HdmiLabel();
            });
            var save = CreateTvPanelRow("保存", () => FinishTvPanel(true));
            var cancel = CreateTvPanelRow("取消", () => FinishTvPanel(false));
            panel.Children.Add(toggle);
            panel.Children.Add(new TextBlock
            {
                Text = "并发下载视频分段，尝试改善海外或慢速线路的缓冲。保存后重新打开视频生效。加速异常时会切回原生下载。",
                FontSize = 16, Opacity = .75, TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(new TextBlock
            {
                Text = "基于 Bilibili-thread-ripper 的 Range 下载方案 · MIT 许可",
                FontSize = 13, Opacity = .6, TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(hdmi);
            panel.Children.Add(new TextBlock
            {
                Text = "跟随系统设置：界面和所有视频都使用 Xbox 设置里的分辨率和刷新率，不同清晰度、帧率的视频由播放器缩放，不再切换 HDMI 模式；只有播放 HDR 视频时切到同分辨率的 HDR10，离开 HDR 视频约 8 秒后回到 SDR。按片源切换：每个视频切到最匹配的分辨率和帧率，每次切换电视会短暂黑屏。下一个视频起生效。",
                FontSize = 16, Opacity = .75, TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(save);
            panel.Children.Add(cancel);
            try
            {
                if (!await ShowTvPanelAsync("设置", panel, new[] { toggle, hdmi, save, cancel }, toggle) || _disposed) return;
                NetworkAccelerationSettings.Enabled = enabled;
                bool hdmiChanged = HdmiOutputSettings.FollowSystem != followSystemHdmi;
                HdmiOutputSettings.FollowSystem = followSystemHdmi;
                PlaybackDiagnosticsLog.Record("network-settings-save enabled=" + enabled + " followSystemHdmi=" + followSystemHdmi);
                // Changing the policy outside playback hands the display
                // back to the system now instead of at the next video.
                if (hdmiChanged && !_watching) _ = ResetPlaybackDisplayAsync(true);
                SetStatus((enabled ? "网络加速已启用" : "网络加速已关闭") + "，HDMI 输出："
                    + (followSystemHdmi ? "跟随系统设置" : "按片源切换") + "。重新打开视频后生效。");
            }
            catch (Exception ex)
            {
                if (!_disposed) SetStatus("设置暂时无法保存：" + Friendly(ex));
            }
        }
    }
}
