using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Controls;
using BiliXbox.Models;
using BiliXbox.Services;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace BiliXbox
{
    public sealed partial class MainPage
    {
        private readonly DanmakuSettingsStore _danmakuSettingsStore = new DanmakuSettingsStore();
        private DanmakuSettings _danmakuSettings;
        private IReadOnlyList<DanmakuBlockRule> _danmakuCloudRules = Array.Empty<DanmakuBlockRule>();
        private CancellationTokenSource _danmakuSyncCts, _danmakuMaskCts;
        private long _danmakuAccount = -1;
        private string _danmakuCloudNotice = "登录后同步关键词、正则和用户屏蔽规则";
        private string _danmakuMaskNotice = "此视频的人物遮罩尚未加载";
        private DanmakuMaskService _danmakuMaskService;
        private DanmakuMaskController _danmakuMaskController;
        private bool _danmakuMaskPending;
        private int _danmakuMaskVersion;

        private void InitializeDanmakuSettings()
        {
            _danmakuSettings = _danmakuSettingsStore.Load();
            _danmakuMaskController = new DanmakuMaskController(DanmakuMaskHost);
            Danmaku.ApplySettings(_danmakuSettings, _danmakuCloudRules);
            UpdateDanmakuSettingsUi();
        }

        private void DanmakuSettings_Click(object sender, RoutedEventArgs args)
        {
            if (IgnorePlayerClick()) return;
            _playerInput.SelectButton(Array.IndexOf(PlayerButtons, DanmakuSettingsButton), PlayerButtons.Length);
            _playerUi.Open(PlayerDrawer.DanmakuSettings, DateTimeOffset.UtcNow);
            UpdateDanmakuSettingsUi(); ApplyPlayerUi();
            DanmakuSettingsCloseButton.Focus(FocusState.Keyboard);
        }

        private static double CycleSetting(double current, params double[] values)
        {
            int index = Array.FindIndex(values, value => Math.Abs(value - current) < 0.005);
            return values[(index + 1) % values.Length];
        }

        private void DanmakuOption_Click(object sender, RoutedEventArgs args)
        {
            if (IgnorePlayerClick()) return;
            var settings = _danmakuSettings;
            switch ((sender as Button)?.Tag as string)
            {
                case "mask": settings = settings.With(smartMask: !settings.SmartMask); break;
                case "area": settings = settings.With(areaFraction: CycleSetting(settings.AreaFraction, 1, 0.75, 0.5, 0.25)); break;
                case "opacity": settings = settings.With(opacity: CycleSetting(settings.Opacity, 0.9, 0.75, 0.5, 0.25, 1)); break;
                case "density": settings = settings.With(densityLimit: (int)CycleSetting(settings.DensityLimit, 12, 24, 36, 48, 64)); break;
                case "font": settings = settings.With(fontScale: CycleSetting(settings.FontScale, 0.75, 1, 1.25, 1.5)); break;
                case "speed": settings = settings.With(speedScale: CycleSetting(settings.SpeedScale, 0.75, 1, 1.25, 1.5, 2)); break;
                case "overlap": settings = settings.With(preventOverlap: !settings.PreventOverlap); break;
                case "merge": settings = settings.With(mergeDuplicates: !settings.MergeDuplicates); break;
                case "scroll": settings = settings.With(showScrolling: !settings.ShowScrolling); break;
                case "top": settings = settings.With(showTop: !settings.ShowTop); break;
                case "bottom": settings = settings.With(showBottom: !settings.ShowBottom); break;
                case "color": settings = settings.With(showColor: !settings.ShowColor); break;
                default: return;
            }
            ApplyDanmakuPreferences(settings);
        }

        private void DanmakuReset_Click(object sender, RoutedEventArgs args)
        {
            if (!IgnorePlayerClick()) ApplyDanmakuPreferences(DanmakuSettings.Default);
        }

        private void ApplyDanmakuPreferences(DanmakuSettings settings)
        {
            bool maskChanged = _danmakuSettings.SmartMask != settings.SmartMask;
            _danmakuSettings = settings;
            Danmaku.ApplySettings(settings, _danmakuCloudRules);
            try { _danmakuSettingsStore.Save(settings); }
            catch (Exception ex) { SetStatus("弹幕设置已生效，但保存失败：" + Friendly(ex)); }
            if (maskChanged)
            {
                StopDanmakuMask();
                if (settings.SmartMask && _video != null && _partCts != null)
                    _ = StartDanmakuMaskAsync(new VideoDetail { Aid = _video.Aid, Bvid = _video.Bvid, Cid = _currentCid }, _partGeneration, _partCts.Token);
            }
            UpdateDanmakuSettingsUi();
        }

        private void UpdateDanmakuSettingsUi()
        {
            var settings = _danmakuSettings;
            if (settings == null) return;
            Func<bool, string> state = enabled => enabled ? "开" : "关";
            DanmakuMaskButton.Content = "智能人物防挡：" + state(settings.SmartMask);
            DanmakuMaskHint.Text = settings.SmartMask ? _danmakuMaskNotice : "智能防挡已关闭";
            string area = settings.AreaFraction > 0.99 ? "全屏" : settings.AreaFraction > 0.74 ? "3/4 屏" : settings.AreaFraction > 0.49 ? "1/2 屏" : "1/4 屏";
            DanmakuAreaButton.Content = "弹幕显示区域：" + area;
            DanmakuOpacityButton.Content = "不透明度：" + Math.Round(settings.Opacity * 100) + "%";
            DanmakuDensityButton.Content = "同屏弹幕上限：" + settings.DensityLimit;
            DanmakuFontButton.Content = "字号：" + settings.FontScale.ToString("0.##") + " 倍";
            DanmakuSpeedButton.Content = "滚动速度：" + settings.SpeedScale.ToString("0.##") + " 倍";
            DanmakuOverlapButton.Content = "防止弹幕重叠：" + state(settings.PreventOverlap);
            DanmakuMergeButton.Content = "过滤短时间重复弹幕：" + state(settings.MergeDuplicates);
            DanmakuScrollButton.Content = "滚动弹幕：" + state(settings.ShowScrolling);
            DanmakuTopButton.Content = "顶部弹幕：" + state(settings.ShowTop);
            DanmakuBottomButton.Content = "底部弹幕：" + state(settings.ShowBottom);
            DanmakuColorButton.Content = "彩色弹幕：" + state(settings.ShowColor);
            DanmakuCloudHint.Text = _danmakuCloudNotice + (Danmaku.IgnoredRuleCount > 0 ? "\n有 " + Danmaku.IgnoredRuleCount + " 条无效或复杂规则未启用。" : "");
            DanmakuCloudSyncButton.IsEnabled = _api.IsLoggedIn && _danmakuSyncCts == null;
            DanmakuCloudLoginButton.Visibility = _api.IsLoggedIn ? Visibility.Collapsed : Visibility.Visible;
        }

        private void UpdateDanmakuAccount()
        {
            long account = CurrentAccountMid;
            if (_danmakuAccount == account) { UpdateDanmakuSettingsUi(); return; }
            Cancel(ref _danmakuSyncCts);
            _danmakuAccount = account;
            _danmakuCloudRules = Array.Empty<DanmakuBlockRule>();
            Danmaku.ApplySettings(_danmakuSettings, _danmakuCloudRules);
            _danmakuCloudNotice = account > 0 ? "正在同步当前账户的屏蔽列表…" : "登录后同步关键词、正则和用户屏蔽规则";
            UpdateDanmakuSettingsUi();
            if (account > 0) _ = SyncDanmakuCloudRulesAsync();
        }

        private async void DanmakuCloudSync_Click(object sender, RoutedEventArgs args)
        {
            if (!IgnorePlayerClick()) await SyncDanmakuCloudRulesAsync();
        }

        private async Task SyncDanmakuCloudRulesAsync()
        {
            if (!_api.IsLoggedIn || _disposed) return;
            Cancel(ref _danmakuSyncCts);
            var owner = _danmakuSyncCts = new CancellationTokenSource();
            var token = owner.Token;
            long account = CurrentAccountMid;
            _danmakuCloudNotice = "正在同步当前账户的屏蔽列表…";
            UpdateDanmakuSettingsUi();
            try
            {
                var result = await _api.GetDanmakuBlockRulesAsync(token);
                if (_disposed || token.IsCancellationRequested || _danmakuSyncCts != owner || CurrentAccountMid != account || result.AccountMid != account) return;
                _danmakuCloudRules = result.Rules;
                Danmaku.ApplySettings(_danmakuSettings, _danmakuCloudRules);
                int keywords = result.Rules.Count(rule => rule.Type == DanmakuBlockRuleType.Keyword);
                int expressions = result.Rules.Count(rule => rule.Type == DanmakuBlockRuleType.Regex);
                int users = result.Rules.Count(rule => rule.Type == DanmakuBlockRuleType.UserHash);
                _danmakuCloudNotice = "已同步 " + result.Count + " 条：关键词 " + keywords + "、正则 " + expressions + "、用户 " + users
                    + "\n" + result.SyncedAt.ToLocalTime().ToString("HH:mm:ss") + (string.IsNullOrWhiteSpace(result.Notice) ? "" : "\n" + result.Notice);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested || CurrentAccountMid != account) { }
            catch (Exception ex)
            {
                if (_danmakuSyncCts == owner && !token.IsCancellationRequested)
                    _danmakuCloudNotice = "同步失败，保留当前账户已加载的 " + _danmakuCloudRules.Count + " 条规则。\n" + Friendly(ex);
            }
            finally
            {
                if (_danmakuSyncCts == owner) Cancel(ref _danmakuSyncCts);
                if (!_disposed) UpdateDanmakuSettingsUi();
            }
        }

        private void StopDanmakuMask()
        {
            ++_danmakuMaskVersion;
            Cancel(ref _danmakuMaskCts);
            _danmakuMaskService?.Dispose(); _danmakuMaskService = null;
            _danmakuMaskController?.Clear();
            _danmakuMaskPending = false;
        }

        private async Task StartDanmakuMaskAsync(VideoDetail video, int generation, CancellationToken parent)
        {
            StopDanmakuMask();
            if (!_danmakuSettings.SmartMask || !_watching || _disposed) return;
            var owner = _danmakuMaskCts = CancellationTokenSource.CreateLinkedTokenSource(parent);
            var token = owner.Token;
            _danmakuMaskNotice = "正在读取此视频的人物遮罩…";
            UpdateDanmakuSettingsUi();
            DanmakuMaskService pending = null;
            try
            {
                var info = await _api.GetDanmakuMaskInfoAsync(video, token);
                if (info != null) pending = await DanmakuMaskService.OpenAsync(info, token);
                token.ThrowIfCancellationRequested();
                if (_disposed || _danmakuMaskCts != owner || generation != _partGeneration || _currentCid != video.Cid) return;
                _danmakuMaskService = pending; pending = null;
                _danmakuMaskNotice = info == null ? "B站未给此视频提供人物遮罩，暂不能智能防挡。" : "人物遮罩已连接，随播放时间生效。";
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (_danmakuMaskCts == owner && !token.IsCancellationRequested) _danmakuMaskNotice = "人物遮罩加载失败，可关闭后重新开启重试。\n" + Friendly(ex);
            }
            finally { pending?.Dispose(); if (!_disposed) UpdateDanmakuSettingsUi(); }
        }

        private async void UpdateDanmakuMask(TimeSpan position)
        {
            var service = _danmakuMaskService;
            if (_disposed || !_watching || !_danmakuSettings.SmartMask || service == null || _danmakuMaskPending || _danmakuMaskCts == null) return;
            int version = _danmakuMaskVersion;
            var token = _danmakuMaskCts.Token;
            _danmakuMaskPending = true;
            try
            {
                var frame = await service.GetFrameAtAsync(position, token);
                if (_disposed || token.IsCancellationRequested || service != _danmakuMaskService || version != _danmakuMaskVersion) return;
                var current = _media.PlaybackSession.Position;
                if (frame == null || current < frame.Time || current >= frame.EndTime) { _danmakuMaskController.Clear(); return; }
                _danmakuMaskController.Apply(frame.Svg, _playback?.VideoWidth ?? 0, _playback?.VideoHeight ?? 0);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (service == _danmakuMaskService && !token.IsCancellationRequested)
                {
                    StopDanmakuMask();
                    _danmakuMaskNotice = "人物遮罩暂不可用，可关闭后重新开启重试。\n" + Friendly(ex);
                    UpdateDanmakuSettingsUi();
                }
            }
            finally { if (version == _danmakuMaskVersion) _danmakuMaskPending = false; }
        }
    }
}
