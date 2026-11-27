using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Controls;
using BiliXbox.Models;
using BiliXbox.Services;
using Windows.Media.Core;
using Windows.Storage.Streams;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace BiliXbox
{
    // Offline cache: the player's 缓存 button stores the current part at a
    // chosen quality (OfflineCache downloads it in LocalCacheFolder), and a
    // stored part plays from its local files through the same split
    // transport (video file + audio follower, FFmpeg for Profile 8.4).
    public sealed partial class MainPage
    {
        private IRandomAccessStream _offlineVideoStream;

        private void InitializeOfflineCache()
        {
            var cache = OfflineCache.Instance;
            cache.Resolver = async (entry, token) =>
            {
                var options = await _api.GetOfflineOptionsAsync(new VideoDetail { Bvid = entry.Bvid, Aid = entry.Aid, Cid = entry.Cid }, token);
                return options.FirstOrDefault(option => option.QualityId == entry.QualityId && option.Kind == entry.Kind)
                    ?? options.FirstOrDefault(option => option.QualityId == entry.QualityId);
            };
            cache.DanmakuFetcher = (cid, token) => _api.GetDanmakuAsync(cid, token);
            Application.Current.Suspending += (s, e) => cache.Suspend();
            Application.Current.Resuming += (s, e) => cache.ResumeAll();
            _ = cache.LoadAsync();
            AccountLibrary.OfflinePlayRequested += async entry => await OpenOfflineEntryAsync(entry);
        }

        // A stored part replaces the network source when the part is opened
        // (initial load, part change) or the stored quality is chosen again.
        private OfflineCacheEntry OfflinePlaybackEntry(string bvid, long cid, int? quality, bool preservePosition)
        {
            var entry = OfflineCache.Instance.FindCompleted(bvid ?? "", cid);
            if (entry == null) return null;
            return !preservePosition || !quality.HasValue || quality.Value == entry.QualityId ? entry : null;
        }

        private async Task<PlaybackInfo> GetOnlineOrOfflinePlaybackAsync(VideoDetail video, int quality, CancellationToken token)
        {
            try { return await GetPreferredPlaybackAsync(video, quality, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) when (OfflineCache.Instance.FindCompleted(video.Bvid ?? "", video.Cid) != null)
            {
                return CreateOfflinePlayback(OfflineCache.Instance.FindCompleted(video.Bvid ?? "", video.Cid));
            }
        }

        private async Task<VideoDetail> GetVideoOrOfflineAsync(string bvid, CancellationToken token)
        {
            await OfflineCache.Instance.LoadAsync();
            try { return await _api.GetVideoAsync(bvid, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) when (OfflineCache.Instance.CompletedParts(bvid).Count > 0)
            {
                var parts = OfflineCache.Instance.CompletedParts(bvid);
                var first = parts[0];
                return new VideoDetail
                {
                    Bvid = bvid, Aid = first.Aid, Cid = first.Cid, Title = first.Title, Owner = first.Owner, OwnerMid = first.OwnerMid,
                    CoverUrl = first.CoverUrl, PlayText = "离线缓存",
                    Parts = parts.Select((entry, index) => new VideoPart
                    {
                        Cid = entry.Cid, Page = index + 1,
                        Title = string.IsNullOrWhiteSpace(entry.PartTitle) ? entry.Title : entry.PartTitle
                    }).ToArray()
                };
            }
        }

        private static PlaybackInfo CreateOfflinePlayback(OfflineCacheEntry entry)
        {
            bool hdr = entry.Kind == "hdr10" || entry.Kind == "dv81" || entry.Kind == "dv84";
            string label = entry.QualityLabel + "（离线）";
            return new PlaybackInfo
            {
                OfflineEntryId = entry.Id, ContentType = "video/mp4", RequiresHttpHeaders = false,
                QualityId = entry.QualityId, SourceQualityId = entry.QualityId, RequestedQualityId = entry.QualityId, ServerQualityId = entry.QualityId,
                Quality = label, VideoCodec = entry.VideoCodec, VideoWidth = entry.Width, VideoHeight = entry.Height,
                VideoBitrate = entry.VideoBandwidth, AudioBitrate = entry.AudioBandwidth, VideoFrameRate = entry.FrameRate,
                VideoBitsPerChannel = entry.BitsPerChannel, HardwareHevcVerified = true, FourKDisplayVerified = true,
                Hdr10DisplayVerified = hdr, AllowsScaledSdrOutput = true, IsHdr10 = hdr,
                IsDolbyVision = entry.Kind == "dv5", DolbyVisionBaseLayer = entry.Kind == "dv81" || entry.Kind == "dv84",
                DolbyVisionHlgFfmpeg = entry.Kind == "dv84",
                QualityNotice = "正在播放离线缓存 · " + entry.QualityLabel,
                AvailableQualities = new[] { new PlaybackQuality { Id = entry.QualityId, Label = label, HasPlaybackSource = true } }
            };
        }

        private async Task OpenOfflineSourceAsync(PlaybackInfo playback, CancellationToken token)
        {
            var entry = OfflineCache.Instance.Entries.FirstOrDefault(item => item.Id == playback.OfflineEntryId);
            if (entry == null || entry.State != OfflineCacheState.Completed) throw new InvalidOperationException("离线缓存已被删除，请重新缓存。");
            var folder = await OfflineCache.EntryFolderAsync(entry);
            var videoFile = await folder.GetFileAsync("video.mp4");
            var audioFile = await folder.GetFileAsync("audio.m4a");
            IRandomAccessStream videoStream = await videoFile.OpenReadAsync();
            IRandomAccessStream audioStream = await audioFile.OpenReadAsync();
            try
            {
                token.ThrowIfCancellationRequested();
                RecordPlaybackTrack("offline-source-open", playback, " kind=" + entry.Kind + " videoBytes=" + videoStream.Size + " audioBytes=" + audioStream.Size);
                MediaSource source;
                if (playback.DolbyVisionHlgFfmpeg)
                {
                    source = await CreateFfmpegVideoSourceAsync(videoStream, playback, token);
                    if (source == null) throw new InvalidOperationException("此离线视频需要 GPU 硬件解码（杜比视界 Profile 8.4），当前无法打开。");
                }
                else source = MediaSource.CreateFromStream(videoStream, "video/mp4");
                _offlineVideoStream = videoStream; videoStream = null;
                _source = source;
                _splitAudio = new SplitAudioFollower(_media, audioStream); audioStream = null;
                _splitAudio.Failed += detail => PlaybackDiagnosticsLog.Record("offline-audio-failed error=" + detail);
            }
            finally { videoStream?.Dispose(); audioStream?.Dispose(); }
        }

        private void DisposeOfflineSource()
        {
            var stream = _offlineVideoStream;
            _offlineVideoStream = null;
            stream?.Dispose();
        }

        private async Task OpenOfflineEntryAsync(OfflineCacheEntry entry)
        {
            if (_disposed || entry == null || entry.State != OfflineCacheState.Completed) return;
            AccountLibrary.StopRequests();
            _libraryVisible = false;
            AccountLibrary.Visibility = Visibility.Collapsed;
            _videoReturnsToCreator = false;
            _videoReturnsToLibrary = true;
            var bookmark = new CreatorPlaybackBookmark
            {
                Bvid = entry.Bvid, Title = entry.Title, Cid = entry.Cid, Quality = entry.QualityId,
                Position = TimeSpan.Zero, Playing = true
            };
            await OpenVideoAsync(entry.Bvid, entry.Title, bookmark);
        }

        private async void WatchCache_Click(object sender, RoutedEventArgs args)
        { if (!IgnorePlayerClick()) await ShowOfflineCacheDialogAsync(); }

        private static string KindTag(string kind)
            => kind == "dv5" || kind == "dv81" || kind == "dv84" ? " · 杜比视界" : kind == "hdr10" ? " · HDR" : "";

        private async Task ShowOfflineCacheDialogAsync()
        {
            if (_disposed || _dialogOpen || !_watching) return;
            var video = _video;
            long cid = _currentCid;
            if (video == null || cid <= 0) { SetStatus("视频信息尚未就绪，稍后再缓存。"); return; }
            var rows = new StackPanel { Spacing = 6, MinWidth = 520 };
            var status = new TextBlock { Text = "正在读取可缓存的画质…", FontSize = 16, TextWrapping = TextWrapping.Wrap, Opacity = .8 };
            var space = new TextBlock { Text = "Xbox 剩余存储空间：读取中…", FontSize = 15, Opacity = .7, Margin = new Thickness(0, 10, 0, 0) };
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(status); panel.Children.Add(rows); panel.Children.Add(space);
            var dialog = new ContentDialog
            {
                Title = (video.Parts.Count > 1 ? "缓存当前分 P" : "缓存视频") + " · 选择画质", Content = new ScrollViewer { Content = panel, MaxHeight = 560 },
                CloseButtonText = "返回", RequestedTheme = ElementTheme.Dark, DefaultButton = ContentDialogButton.Close,
                RequiresPointer = RequiresPointer.Never
            };
            var cts = new CancellationTokenSource();
            dialog.Closed += (s, e) => { cts.Cancel(); _lastBack = DateTimeOffset.UtcNow; };
            dialog.Opened += async (s, e) =>
            {
                try
                {
                    var freeTask = OfflineCache.GetFreeSpaceAsync();
                    var options = await _api.GetOfflineOptionsAsync(new VideoDetail { Bvid = video.Bvid, Aid = video.Aid, Cid = cid }, cts.Token);
                    var free = await freeTask;
                    if (cts.IsCancellationRequested) return;
                    space.Text = "Xbox 剩余存储空间：" + (free.HasValue ? OfflineCache.FormatBytes(free.Value) : "未知");
                    var existing = OfflineCache.Instance.Find(video.Bvid, cid);
                    status.Text = options.Count == 0 ? "此视频没有可缓存的画质。"
                        : existing != null ? "已缓存：" + existing.QualityLabel + "（" + StateText(existing) + "）。选择画质会替换现有缓存。"
                        : "选择要缓存的画质（大小为估算值）：";
                    Button first = null;
                    foreach (var option in options)
                    {
                        long size = option.EstimatedBytes;
                        bool fits = !free.HasValue || (ulong)size + 200UL * 1024 * 1024 < free.Value;
                        var button = new Button
                        {
                            Content = option.Label + KindTag(option.Kind) + "  ·  约 " + OfflineCache.FormatBytes(size) + (fits ? "" : "  ·  空间不足"),
                            HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = fits,
                            Style = (Style)Application.Current.Resources["TvRowButtonStyle"]
                        };
                        var chosen = option;
                        button.Click += async (bs, be) =>
                        {
                            dialog.Hide();
                            await StartOfflineDownloadAsync(video, cid, chosen);
                        };
                        rows.Children.Add(button);
                        if (first == null && fits) first = button;
                    }
                    await Dispatcher.RunAsync(CoreDispatcherPriority.Low, () => first?.Focus(FocusState.Keyboard));
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { status.Text = "画质列表读取失败：" + Friendly(ex); }
            };
            _dialogOpen = true; _activeGeneralDialog = dialog; DisablePointerCursor();
            try { await dialog.ShowAsync(); }
            catch (Exception ex) { SetStatus("缓存选项未能打开：" + Friendly(ex)); }
            finally { _activeGeneralDialog = null; _dialogOpen = false; cts.Dispose(); if (!_disposed && _watching) { ShowControls(); FocusGamepadControl(WatchCacheButton); } }
        }

        private async Task StartOfflineDownloadAsync(VideoDetail video, long cid, OfflineQualityOption option)
        {
            try
            {
                IReadOnlyList<DanmakuItem> danmaku = null;
                try { danmaku = await _api.GetDanmakuAsync(cid); } catch (Exception) { }
                await OfflineCache.Instance.EnqueueAsync(video, cid, option, danmaku);
                SetStatus("已开始缓存 " + option.Label + "，可在 个人中心 → 离线缓存 查看进度。");
            }
            catch (Exception ex) { SetStatus("缓存未能开始：" + Friendly(ex)); }
        }

        internal static string StateText(OfflineCacheEntry entry)
        {
            switch (entry.State)
            {
                case OfflineCacheState.Completed: return "已完成";
                case OfflineCacheState.Downloading: return "下载中 " + (entry.Progress * 100).ToString("0") + "%";
                case OfflineCacheState.Paused: return "已暂停 " + (entry.Progress * 100).ToString("0") + "%";
                case OfflineCacheState.Failed: return "下载失败";
                default: return "等待下载";
            }
        }
    }
}
