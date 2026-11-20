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
            PlaybackDiagnosticsLog.Record("network-settings-open enabled=" + enabled);
            var panel = new StackPanel { Spacing = 12 };
            Button toggle = null;
            string ToggleLabel() => "网络加速 · 测试版 · " + (enabled ? "已启用" : "已关闭");
            toggle = CreateTvPanelRow(ToggleLabel(), () =>
            {
                enabled = !enabled;
                toggle.Content = ToggleLabel();
                PlaybackDiagnosticsLog.Record("network-settings-draft enabled=" + enabled + " inputArmed=True");
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
            panel.Children.Add(save);
            panel.Children.Add(cancel);
            try
            {
                if (!await ShowTvPanelAsync("设置", panel, new[] { toggle, save, cancel }, toggle) || _disposed) return;
                NetworkAccelerationSettings.Enabled = enabled;
                PlaybackDiagnosticsLog.Record("network-settings-save enabled=" + enabled);
                SetStatus(enabled ? "网络加速已启用，重新打开视频后生效。" : "网络加速已关闭，重新打开视频后使用原生下载。");
            }
            catch (Exception ex)
            {
                if (!_disposed) SetStatus("设置暂时无法保存：" + Friendly(ex));
            }
        }
    }
}
