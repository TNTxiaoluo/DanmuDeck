using System;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Models;
using BiliXbox.Services;
using FFmpegInteropX;
using Windows.Media.Core;

namespace BiliXbox
{
    public sealed partial class MainPage
    {
        // Dolby Vision Profile 8.4 (HLG base layer): the console's own HEVC
        // pipeline rejects HLG-signalled streams (0xC00D36B4, whatever the
        // source). It is decoded with FFmpegInteropX (FFmpeg
        // D3D11 hardware decoding), which hands uncompressed HLG frames to the
        // system under HDR output. Failure falls back to the SDR variant.
        private FFmpegMediaSource _ffmpegVideo;
        // FFmpeg's own playback item is given to the player directly, so the
        // library's item hooks see the same item that plays.
        private Windows.Media.Playback.MediaPlaybackItem _ffmpegItem;

        private async Task<MediaSource> CreateFfmpegVideoSourceAsync(Windows.Storage.Streams.IRandomAccessStream stream, PlaybackInfo playback, CancellationToken token)
        {
            try
            {
                var config = new MediaSourceConfig();
                config.Video.VideoDecoderMode = VideoDecoderMode.Automatic;
                config.Video.HdrSupport = HdrSupport.Enabled;
                config.Video.VideoOutputAllow10bit = true;
                config.General.ReadAheadBufferEnabled = true;
                config.General.FastSeek = true;
                stream.Seek(0);
                var ffmpeg = await FFmpegMediaSource.CreateFromStreamAsync(stream, config).AsTask(token);
                var video = ffmpeg.CurrentVideoStream;
                RecordPlaybackTrack("ffmpeg-video-open", playback, " codec=" + (video?.CodecName ?? "-")
                    + " decoder=" + (video?.DecoderEngine.ToString() ?? "-") + " hw=" + (video?.HardwareDecoderStatus.ToString() ?? "-")
                    + " bits=" + (video?.BitsPerSample.ToString() ?? "-") + " streams=" + ffmpeg.VideoStreams.Count);
                // 4K HEVC Main10 in software would not keep up: without the
                // GPU decoder use the SDR variant instead.
                if (video == null || video.HardwareDecoderStatus != HardwareDecoderStatus.Available)
                {
                    ffmpeg.Dispose();
                    RecordPlaybackTrack("ffmpeg-video-failed", playback, " reason=no-hardware-decoder");
                    return null;
                }
                // GetMediaStreamSource only returns a weak reference (null until a
                // source exists); the playback item creates and owns the source.
                var item = ffmpeg.CreateMediaPlaybackItem();
                _ffmpegVideo = ffmpeg;
                _ffmpegItem = item;
                RecordPlaybackTrack("ffmpeg-video-item", playback, " hdrSupport=" + config.Video.HdrSupport);
                return item.Source;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                RecordPlaybackTrack("ffmpeg-video-failed", playback, " hresult=" + ex.HResult.ToString("X8"));
                return null;
            }
        }

        // FFmpeg's D3D11 decoder allocates HEVC surfaces aligned to 128 pixels
        // (FFmpeg dxva2 surface_alignment), and FFmpegInteropX hands the whole
        // padded texture to the renderer: a 3840x1728 video arrives as
        // 3840x1792, squeezed vertically with green (zero YUV) rows at the
        // bottom.
        // The session's NormalizedSourceRect has no effect on these samples
        // (1.0.87.0), so the padding is removed in the view instead: the
        // player is scaled about the top-left of its letterboxed picture so
        // that the real rows fill it, and the padded rows are clipped off.
        private double _ffmpegPadScaleX = 1, _ffmpegPadScaleY = 1, _ffmpegPadAspect;
        private bool _ffmpegPadHooked;

        private void ApplyFfmpegSurfaceCrop(Windows.Media.Playback.MediaPlayer player)
        {
            double sx = 1, sy = 1, aspect = 0;
            try
            {
                var video = _ffmpegVideo?.CurrentVideoStream;
                if (video != null && video.DecoderEngine == DecoderEngine.FFmpegD3D11HardwareDecoder
                    && video.PixelWidth > 0 && video.PixelHeight > 0)
                {
                    int paddedWidth = (video.PixelWidth + 127) / 128 * 128;
                    int paddedHeight = (video.PixelHeight + 127) / 128 * 128;
                    sx = (double)paddedWidth / video.PixelWidth;
                    sy = (double)paddedHeight / video.PixelHeight;
                    aspect = (double)video.PixelWidth / video.PixelHeight;
                    PlaybackDiagnosticsLog.Record("ffmpeg-surface-crop video=" + video.PixelWidth + "x" + video.PixelHeight
                        + " surface=" + paddedWidth + "x" + paddedHeight + " mode=view-transform");
                }
            }
            catch (Exception ex) { PlaybackDiagnosticsLog.Record("ffmpeg-surface-crop-failed hresult=" + ex.HResult.ToString("X8")); }
            var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                _ffmpegPadScaleX = sx; _ffmpegPadScaleY = sy; _ffmpegPadAspect = aspect;
                if (!_ffmpegPadHooked) { Player.SizeChanged += (s, e) => UpdateFfmpegPaddingView(); _ffmpegPadHooked = true; }
                UpdateFfmpegPaddingView();
            });
        }

        private void UpdateFfmpegPaddingView()
        {
            double width = Player.ActualWidth, height = Player.ActualHeight;
            if (_ffmpegPadAspect <= 0 || (_ffmpegPadScaleX == 1 && _ffmpegPadScaleY == 1) || width <= 0 || height <= 0)
            {
                Player.RenderTransform = null;
                Player.Clip = null;
                return;
            }
            // The renderer letterboxes the padded surface into the video's own
            // aspect ratio (Stretch="Uniform").
            double pictureWidth = width, pictureHeight = width / _ffmpegPadAspect;
            if (pictureHeight > height) { pictureHeight = height; pictureWidth = height * _ffmpegPadAspect; }
            double left = (width - pictureWidth) / 2, top = (height - pictureHeight) / 2;
            Player.RenderTransform = new Windows.UI.Xaml.Media.ScaleTransform
            {
                CenterX = left, CenterY = top, ScaleX = _ffmpegPadScaleX, ScaleY = _ffmpegPadScaleY
            };
            // Clip is in the element's own (pre-transform) coordinates.
            Player.Clip = new Windows.UI.Xaml.Media.RectangleGeometry
            {
                Rect = new Windows.Foundation.Rect(left, top, pictureWidth / _ffmpegPadScaleX, pictureHeight / _ffmpegPadScaleY)
            };
        }

        private void RecordFfmpegVideoState(string stage)
        {
            var ffmpeg = _ffmpegVideo;
            if (ffmpeg == null) return;
            try
            {
                var video = ffmpeg.CurrentVideoStream;
                PlaybackDiagnosticsLog.Record(stage + " decoder=" + (video?.DecoderEngine.ToString() ?? "-")
                    + " hw=" + (video?.HardwareDecoderStatus.ToString() ?? "-") + " hdrMetadata=" + (video?.HasHdrMetadata.ToString() ?? "-")
                    + " hdrActive=" + (video?.IsHdrActive.ToString() ?? "-"));
            }
            catch (Exception) { /* Diagnostics never break playback. */ }
        }

        // FFmpeg could not open the track: relabel it SDR in place and switch
        // to the SDR variant on the same stream.
        private async Task<PlaybackInfo> FallBackFromFfmpegAsync(HttpRandomAccessStream stream, PlaybackInfo playback, CancellationToken token)
        {
            var sdr = playback.CodecFallback;
            while (sdr != null && sdr.DolbyVisionHlgFfmpeg) sdr = sdr.CodecFallback;
            if (sdr == null) throw new DolbyVisionHlgPatchException();
            playback.CodecFallback = null;
            var prefix = await stream.ReadPrefixAsync(token);
            var hlg = DashManifestBuilder.FindHlgTransferPatch(prefix);
            if (!hlg.HasValue) throw new DolbyVisionHlgPatchException();
            stream.PatchBytes((ulong)hlg.Value.Key, hlg.Value.Value);
            RecordPlaybackTrack("ffmpeg-video-sdr-fallback", sdr, " transferPatched=True");
            return await PrepareSourceDisplayAsync(sdr, token);
        }
    }
}
