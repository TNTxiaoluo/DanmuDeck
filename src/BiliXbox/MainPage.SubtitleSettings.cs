using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Controls;
using BiliXbox.Models;
using BiliXbox.Services;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace BiliXbox
{
    public sealed partial class MainPage
    {
        private SubtitleSettings _subtitleSettings;
        private SubtitleTrack _activeSubtitle;
        private FrameworkElement _subtitleStrokeHost;
        private readonly CompositionDanmakuStrokeRenderer _subtitleStrokeRenderer = new CompositionDanmakuStrokeRenderer();

        private SubtitleSettings SubtitleStyle => _subtitleSettings ?? (_subtitleSettings = SubtitleSettingsStore.Load());

        // Track list on top, appearance below; style rows apply live behind
        // the side panel so the viewer sees the change on the video.
        private async Task ShowSubtitlePanelAsync()
        {
            if (_dialogOpen || _video == null || !_watching) return;
            Cancel(ref _subtitleCts);
            var owner = _subtitleCts = CancellationTokenSource.CreateLinkedTokenSource(_watchCts.Token);
            long cid = _currentCid;
            var rows = new List<Button>();
            var panel = new StackPanel { Spacing = 8 };
            var notice = PanelText("正在读取字幕列表…", 15, 0.75);
            panel.Children.Add(PanelText("字幕轨道", 18, 1, true));
            panel.Children.Add(notice);
            SubtitleTrack chosen = null;
            bool choose = false;
            var trackRows = new List<Button>();
            void AddTrack(SubtitleTrack track)
            {
                bool current = track.Url.Length == 0 ? _activeSubtitle == null : _activeSubtitle?.Url == track.Url;
                var row = CreateTvPanelRow((current ? "✓ " : "") + track.Label, () => { chosen = track; choose = true; FinishTvPanel(true); });
                trackRows.Add(row); rows.Insert(trackRows.Count - 1, row);
                panel.Children.Insert(1 + trackRows.Count, row);
            }

            panel.Children.Add(PanelText("字幕样式", 18, 1, true, new Thickness(0, 16, 0, 0)));
            Button scale = null, color = null, background = null, stroke = null, position = null, height = null;
            void Refresh()
            {
                var s = SubtitleStyle;
                scale.Content = "字号：" + SubtitleSettings.ScaleLabels[SubtitleSettings.Clamp(s.Scale, SubtitleSettings.Scales.Length)];
                color.Content = "颜色：" + SubtitleSettings.ColorLabels[SubtitleSettings.Clamp(s.Color, SubtitleSettings.Colors.Length)];
                background.Content = "背景不透明度：" + Math.Round(s.BackgroundOpacity * 100) + "%";
                stroke.Content = "描边：" + DanmakuSettings.StrokeLabel(s.Stroke);
                position.Content = "位置：" + (s.Top ? "顶部" : "底部");
                height.Content = "离边缘距离：" + SubtitleSettings.HeightLabels[SubtitleSettings.Clamp(s.Height, SubtitleSettings.Heights.Length)];
            }
            void Change(Action<SubtitleSettings> edit) { edit(SubtitleStyle); SubtitleSettingsStore.Save(SubtitleStyle); ApplySubtitleStyle(); Refresh(); }
            scale = CreateTvPanelRow("", () => Change(s => s.Scale = (s.Scale + 1) % SubtitleSettings.Scales.Length));
            color = CreateTvPanelRow("", () => Change(s => s.Color = (s.Color + 1) % SubtitleSettings.Colors.Length));
            background = CreateTvPanelRow("", () => Change(s => s.Background = (s.Background + 1) % SubtitleSettings.Backgrounds.Length));
            stroke = CreateTvPanelRow("", () => Change(s => s.Stroke = new DanmakuSettings(strokeStyle: s.Stroke).NextStroke()));
            position = CreateTvPanelRow("", () => Change(s => s.Top = !s.Top));
            height = CreateTvPanelRow("", () => Change(s => s.Height = (s.Height + 1) % SubtitleSettings.Heights.Length));
            foreach (var row in new[] { scale, color, background, stroke, position, height }) { rows.Add(row); panel.Children.Add(row); }
            Refresh();

            var load = LoadTracksAsync();
            async Task LoadTracksAsync()
            {
                try
                {
                    var tracks = await _api.GetSubtitlesAsync(_video, cid, owner.Token);
                    if (owner.IsCancellationRequested) return;
                    notice.Text = tracks.Count == 0 ? (_api.IsLoggedIn ? "B站未提供此分 P 的可用字幕。" : "此视频没有可用字幕，登录后可重试。") : "";
                    notice.Visibility = notice.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
                    AddTrack(new SubtitleTrack { Label = "关闭字幕" });
                    foreach (var track in tracks) AddTrack(track);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { if (!owner.IsCancellationRequested) notice.Text = "字幕读取失败：" + Friendly(ex); }
            }

            bool accepted = await ShowTvPanelAsync("字幕", panel, rows);
            if (!accepted || !choose || owner.IsCancellationRequested || cid != _currentCid) return;
            await SelectSubtitleAsync(chosen, owner, cid, true);
        }

        private async Task SelectSubtitleAsync(SubtitleTrack track, CancellationTokenSource owner, long cid, bool remember)
        {
            try
            {
                var cues = track == null || string.IsNullOrEmpty(track.Url) ? Array.Empty<SubtitleCue>() : await _api.GetSubtitleCuesAsync(track, owner.Token);
                if (owner != _subtitleCts || cid != _currentCid || owner.IsCancellationRequested) return;
                _activeSubtitle = cues.Count > 0 ? track : null;
                _subtitleCues = cues;
                if (remember) { SubtitleStyle.LastLanguage = _activeSubtitle?.Language ?? ""; SubtitleSettingsStore.Save(SubtitleStyle); }
                ApplySubtitleStyle();
                RenderSubtitle();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { SetStatus("字幕读取失败：" + Friendly(ex)); }
        }

        // A part opened after the viewer chose a subtitle language turns the
        // same language on again; choosing "关闭字幕" stops this.
        private async void AutoEnableSubtitles(long cid)
        {
            string language = SubtitleStyle.LastLanguage;
            if (string.IsNullOrEmpty(language) || _video == null || _watchCts == null) return;
            Cancel(ref _subtitleCts);
            var owner = _subtitleCts = CancellationTokenSource.CreateLinkedTokenSource(_watchCts.Token);
            try
            {
                var tracks = await _api.GetSubtitlesAsync(_video, cid, owner.Token);
                var match = tracks.FirstOrDefault(track => track.Language == language)
                    ?? tracks.FirstOrDefault(track => SubtitleFamily(track.Language) == SubtitleFamily(language));
                if (match == null || owner != _subtitleCts || cid != _currentCid) return;
                await SelectSubtitleAsync(match, owner, cid, false);
            }
            catch (OperationCanceledException) { }
            catch (Exception) { /* Optional convenience; the menu still works. */ }
        }

        // "ai-zh" and "zh-CN" both count as Chinese for automatic re-enabling.
        private static string SubtitleFamily(string language)
        {
            language = (language ?? "").ToLowerInvariant();
            if (language.StartsWith("ai-")) language = language.Substring(3);
            int dash = language.IndexOf('-');
            return dash > 0 ? language.Substring(0, dash) : language;
        }

        private void ApplySubtitleStyle()
        {
            var s = SubtitleStyle;
            SubtitleText.FontSize = 25 * s.FontScale;
            uint rgb = s.Rgb;
            SubtitleText.Foreground = new SolidColorBrush(Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
            SubtitleLayer.Background = new SolidColorBrush(Color.FromArgb((byte)Math.Round(s.BackgroundOpacity * 255), 0, 0, 0));
            SubtitleLayer.VerticalAlignment = s.Top ? VerticalAlignment.Top : VerticalAlignment.Bottom;
            try
            {
                if (s.Stroke == DanmakuStrokeStyle.None)
                {
                    if (_subtitleStrokeHost != null) _subtitleStrokeHost.Visibility = Visibility.Collapsed;
                }
                else
                {
                    if (_subtitleStrokeHost == null)
                    {
                        _subtitleStrokeHost = _subtitleStrokeRenderer.Create(SubtitleText);
                        _subtitleStrokeHost.HorizontalAlignment = HorizontalAlignment.Left;
                        _subtitleStrokeHost.VerticalAlignment = VerticalAlignment.Top;
                        SubtitleTextHost.Children.Insert(0, _subtitleStrokeHost);
                    }
                    _subtitleStrokeRenderer.Apply(_subtitleStrokeHost, s.Stroke);
                    _subtitleStrokeHost.Visibility = Visibility.Visible;
                }
            }
            catch (Exception) { /* Plain text remains readable without an edge. */ }
            ApplyCommentLayout();
        }

        private double SubtitleEdgeOffset(bool controlsVisible)
            => SubtitleStyle.Top ? (controlsVisible ? 150 : 24) + SubtitleStyle.Offset - 40 : (controlsVisible ? 190 : SubtitleStyle.Offset);

        private static TextBlock PanelText(string text, double size, double opacity, bool bold = false, Thickness margin = default(Thickness))
            => new TextBlock { Text = text, FontSize = size, Opacity = opacity, TextWrapping = TextWrapping.Wrap, Margin = margin,
                FontWeight = bold ? Windows.UI.Text.FontWeights.SemiBold : Windows.UI.Text.FontWeights.Normal,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 0xF2, 0xF2, 0xF7)) };
    }
}
