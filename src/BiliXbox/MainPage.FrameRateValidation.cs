using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Models;
using BiliXbox.Services;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.UI.Xaml;

namespace BiliXbox
{
    public sealed partial class MainPage
    {
        private MediaPlaybackItem _frameRateValidationItem;
        private MediaSource _frameRateValidationSource;
        private PlaybackInfo _frameRateValidationPlayback;
        private int _frameRateValidationGeneration;

        // MediaSource does not expose video tracks. Only the bounded above-60
        // compatibility path needs this wrapper; ordinary playback keeps its source.
        private IMediaPlaybackSource CreateFrameRateValidationPlaybackSource(MediaSource source,
            PlaybackInfo playback, int generation)
        {
            ClearNativeFrameRateValidationPlaybackItem();
            if (source == null || playback?.RequiresNativeFrameRateValidation != true) return source;
            var item = new MediaPlaybackItem(source);
            _frameRateValidationItem = item;
            _frameRateValidationSource = source;
            _frameRateValidationPlayback = playback;
            _frameRateValidationGeneration = generation;
            return item;
        }

        // The MediaSource/adaptive transport remains owned and disposed by
        // StopMedia. MediaPlaybackItem has no separate Dispose operation.
        private void ClearNativeFrameRateValidationPlaybackItem()
        {
            _frameRateValidationItem = null;
            _frameRateValidationSource = null;
            _frameRateValidationPlayback = null;
            _frameRateValidationGeneration = 0;
        }

        private bool OwnsNativeFrameRateValidation(MediaPlaybackItem item, MediaSource source,
            PlaybackInfo playback, int generation, CancellationTokenSource watch)
            => !_disposed && _watching && item != null && source != null && playback != null
                && ReferenceEquals(_frameRateValidationItem, item)
                && ReferenceEquals(_frameRateValidationSource, source)
                && ReferenceEquals(_frameRateValidationPlayback, playback)
                && generation == _frameRateValidationGeneration && generation == _partGeneration
                && ReferenceEquals(_source, source) && ReferenceEquals(_media.Source, item)
                && ReferenceEquals(_playback, playback) && ReferenceEquals(_watchCts, watch)
                && (watch == null || !watch.IsCancellationRequested);

        // Call on the UI dispatcher after MediaOpened, before restoring autoplay.
        // true permits the caller to continue; false means stale media or a
        // rejected native track that already selected a fallback/error surface.
        private async Task<bool> ValidateNativeFrameRateAsync()
        {
            var playback = _playback;
            if (playback?.RequiresNativeFrameRateValidation != true) return true;
            var item = _frameRateValidationItem;
            var source = _frameRateValidationSource;
            int generation = _frameRateValidationGeneration;
            var watch = _watchCts;
            if (!OwnsNativeFrameRateValidation(item, source, playback, generation, watch)) return false;

            double nativeRate = 0;
            uint nativeWidth = 0, nativeHeight = 0;
            string nativeDetail = " track=unavailable";
            try
            {
                var tracks = item.VideoTracks;
                int selected = tracks.SelectedIndex;
                // A single video representation has no ambiguity if its default
                // selection has not been published yet. Never inspect another
                // track when multiple tracks exist but none is selected.
                if (selected < 0 && tracks.Count == 1) selected = 0;
                if (selected >= 0 && selected < tracks.Count)
                {
                    var properties = tracks[selected].GetEncodingProperties();
                    var rate = properties?.FrameRate;
                    if (rate != null && rate.Denominator != 0)
                        nativeRate = (double)rate.Numerator / rate.Denominator;
                    nativeWidth = properties?.Width ?? 0;
                    nativeHeight = properties?.Height ?? 0;
                    nativeDetail = " native-track=" + selected
                        + " native=" + properties?.Width + "x" + properties?.Height
                        + " native-fps-ratio=" + rate?.Numerator + "/" + rate?.Denominator;
                }
            }
            catch (Exception error)
            {
                // Missing native metadata is not an unsupported decoder. The
                // existing native error/watchdog path still owns trial recovery.
                nativeDetail = " native-properties-hresult=0x" + error.HResult.ToString("X8");
            }
            if (!OwnsNativeFrameRateValidation(item, source, playback, generation, watch)) return false;
            if (nativeWidth == 0 || nativeHeight == 0)
            {
                nativeWidth = _media.PlaybackSession.NaturalVideoWidth;
                nativeHeight = _media.PlaybackSession.NaturalVideoHeight;
                nativeDetail += " session-native=" + nativeWidth + "x" + nativeHeight;
            }
            if (double.IsNaN(nativeRate) || double.IsInfinity(nativeRate) || nativeRate <= 0) nativeRate = 0;
            bool expectsFourK = playback.QualityId == 120 || Math.Max(playback.VideoWidth, playback.VideoHeight) >= 3840
                || Math.Min(playback.VideoWidth, playback.VideoHeight) >= 2160;
            bool dimensionsKnown = nativeWidth > 0 && nativeHeight > 0;
            bool nativeFourK = dimensionsKnown && (Math.Max(nativeWidth, nativeHeight) >= 3840
                || Math.Min(nativeWidth, nativeHeight) >= 2160);
            bool wrongDimensions = expectsFourK && dimensionsKnown && !nativeFourK;
            playback.VideoFrameRateNative = nativeRate;
            playback.NativeFrameRateVerified = nativeRate > 0 && nativeRate <= 60.001
                && (!expectsFourK || nativeFourK);
            string rates = " api-fps=" + playback.VideoFrameRate.ToString("0.####", CultureInfo.InvariantCulture)
                + " query-fps=" + playback.VideoFrameRateProbe.ToString("0.####", CultureInfo.InvariantCulture)
                + " native-fps=" + nativeRate.ToString("0.####", CultureInfo.InvariantCulture)
                + " trial-kind=" + (playback.VideoFrameRate <= PlaybackFrameRatePolicy.NearSixtyMetadataMaximum
                    ? "near60-metadata" : "high-frame-rate-declaration") + nativeDetail;

            if (playback.NativeFrameRateVerified)
            {
                playback.FrameRateNotice = "原生媒体已确认 "
                    + nativeRate.ToString("0.##", CultureInfo.InvariantCulture)
                    + " 帧/秒；接口声明为 "
                    + playback.VideoFrameRate.ToString("0.##", CultureInfo.InvariantCulture) + " 帧/秒。";
                RecordPlaybackTrack("native-frame-rate-verified", playback, rates);
                UpdateQualities();
                return true;
            }

            if (!wrongDimensions && (nativeRate == 0 || nativeRate <= PlaybackFrameRatePolicy.MaximumTrialReportedFrameRate))
            {
                string trialName = playback.VideoFrameRate > PlaybackFrameRatePolicy.NearSixtyMetadataMaximum
                    ? "高帧率兼容性试播" : "兼容性试播";
                playback.FrameRateNotice = expectsFourK && !dimensionsKnown
                    ? "原生视频尺寸尚未确认；正在按已查询的 60fps 解码能力和 60Hz 输出进行" + trialName + "，尚未完成原生 4K 校验。"
                    : nativeRate == 0
                    ? "原生帧率尚未确认；正在按已查询的 60fps 解码能力和 60Hz 输出进行" + trialName + "。"
                    : nativeRate <= PlaybackFrameRatePolicy.NearSixtyMetadataMaximum
                    ? "原生媒体仍报告 " + nativeRate.ToString("0.##", CultureInfo.InvariantCulture)
                        + " 帧/秒；正在以 60Hz 输出兼容性试播，尚未确认此帧率的硬件支持。"
                    : "原生媒体报告高帧率 " + nativeRate.ToString("0.###", CultureInfo.InvariantCulture)
                        + " 帧/秒；正在以 60Hz 输出兼容性试播，尚未确认此高帧率的硬件解码能力。";
                RecordPlaybackTrack("native-frame-rate-trial", playback, rates + " verified=false output-hz=60");
                UpdateQualities();
                return true;
            }

            playback.QualityFailure = PlaybackQualityFailure.SourceUnsupported;
            playback.FrameRateNotice = wrongDimensions ? "原生视频尺寸与 4K 播放源声明不符。"
                : "原生媒体确认的帧率超过此播放路径的试播范围。";
            RecordPlaybackTrack("native-frame-rate-rejected", playback,
                rates + (wrongDimensions ? " reason=native-dimensions-not-4k" : " reason=native-frame-rate-above-120.001-trial-limit"));
            _media.Pause();
            // An audio fallback contains the same video track, so it cannot
            // resolve a rejected video frame rate. Try the next real video.
            var fallback = playback.CodecFallback;
            if (fallback != null && !ReferenceEquals(fallback, playback) && _video != null)
            {
                playback.CodecFallback = playback.AudioFallback = null;
                fallback.QualityFailure = PlaybackQualityFailure.SourceUnsupported;
                fallback.QualityNotice = (wrongDimensions ? "原轨道的实际尺寸与 4K 声明不符" : "原轨道的实际帧率不适用")
                    + "，正在尝试实际 " + fallback.Quality + " 的另一条播放源。";
                await LoadPartAsync(_currentCid, fallback.QualityId, true, fallback);
                return false;
            }

            _sourceLoading = _sessionBuffering = _wantsPlayback = _resumeAfterOpen = false;
            await ResetPlaybackDisplayAsync();
            if (!OwnsNativeFrameRateValidation(item, source, playback, generation, watch)) return false;
            PlaybackError.Text = (wrongDimensions ? "此播放源未提供实际 4K 视频" : "此轨道的实际帧率超过当前播放路径支持的范围")
                + "，请选择其他画质或重试。";
            ErrorPanel.Visibility = Visibility.Visible;
            OfficialVideoButton.Visibility = Visibility.Visible;
            ShowControls();
            SetStatus(playback.FrameRateNotice);
            UpdatePlaybackUi();
            return false;
        }
    }
}
