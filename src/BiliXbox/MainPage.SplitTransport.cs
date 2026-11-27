using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Models;
using BiliXbox.Services;

namespace BiliXbox
{
    public sealed partial class MainPage
    {
        // Xbox AdaptiveMediaSource fails every HEVC Main10 (HDR10) DASH video
        // with DecodingError 0x80004002 before the first frame, whatever the
        // MPD codec string or output mode. The same representation decodes
        // when opened as a plain fragmented MP4, so HDR10 sources (including
        // their SDR tone-mapped output variant) use separate video and audio
        // players. Every other source keeps the synchronized DASH transport.
        private static bool UsesSplitHdrTransport(PlaybackInfo playback, bool audioOnly)
            => !audioOnly && playback != null && (playback.IsHdr10 || playback.IsDolbyVision || playback.DolbyVisionBaseLayer) && !string.IsNullOrEmpty(playback.DashManifest)
                && playback.DashVideoUrls != null && playback.DashVideoUrls.Count > 0
                && playback.DashAudioUrls != null && playback.DashAudioUrls.Count > 0;

        // The HDR video stream's own cache is the only honest buffer measure:
        // bytes cached contiguously ahead of the decoder's latest read, at the
        // track's declared bitrate. A decoder read blocked on the network means
        // no playable data beyond what the native pipeline already holds.
        private bool TrySplitTransportBuffer(out double seconds)
        {
            seconds = -1;
            var stream = _stream;
            if (_splitAudio == null || stream == null || _playback == null || _playback.VideoBitrate <= 0) return false;
            try
            {
                stream.PumpReadAhead();
                seconds = stream.IsReadWaiting ? 0 : stream.ContiguousBytesAhead / (_playback.VideoBitrate / 8.0);
                return true;
            }
            catch (ObjectDisposedException) { return false; }
        }

        // Play was requested but the engine has not started (opening a seek
        // position or prerolling) while its byte stream waits for the network.
        // Native reports neither Buffering nor a moving clock here, so show the
        // buffering overlay explicitly.
        private bool IsSplitTransportStarved(Windows.Media.Playback.MediaPlaybackState state)
        {
            if (_splitAudio == null || _stream == null || !_wantsPlayback) return false;
            if (state == Windows.Media.Playback.MediaPlaybackState.Playing) return false;
            try { return _stream.IsReadWaiting; }
            catch (ObjectDisposedException) { return false; }
        }

        // Append a synthesized 'mfra' built from the file's own 'sidx' so the
        // native MPEG-4 source can seek straight to a fragment instead of
        // parsing every earlier fragment (minutes of 4K HDR data) first.
        private async Task AttachRandomAccessIndexAsync(HttpRandomAccessStream stream, string track,
            PlaybackInfo playback, CancellationToken token)
        {
            try
            {
                var prefix = await stream.ReadPrefixAsync(token);
                if (track == "video" && playback.DolbyVisionBaseLayer)
                {
                    // Present only the HEVC base layer of Dolby Vision Profile 8.x.
                    var boxes = DolbyVisionBaseLayer.FindConfigBoxTypes(prefix);
                    foreach (var offset in boxes) stream.PatchBytes((ulong)offset, DolbyVisionBaseLayer.FreeType);
                    var hlg = playback.DolbyVisionHlgAsSdr ? DashManifestBuilder.FindHlgTransferPatch(prefix) : null;
                    if (hlg.HasValue) stream.PatchBytes((ulong)hlg.Value.Key, hlg.Value.Value);
                    RecordPlaybackTrack("dolby-vision-base-layer", playback, " configBoxesHidden=" + boxes.Count
                        + " hlgAsSdr=" + playback.DolbyVisionHlgAsSdr + " transferPatched=" + hlg.HasValue);
                    // Never hand an HLG-signalled stream to the Xbox pipeline:
                    // an unpatched 8.4 track can hang it.
                    if (playback.DolbyVisionHlgAsSdr && !hlg.HasValue)
                        throw new DolbyVisionHlgPatchException();
                }
                var tail = Mp4RandomAccessIndex.Build(prefix, stream.Size);
                if (tail != null) stream.AppendTail(tail);
                RecordPlaybackTrack("split-random-access", playback, " track=" + track + " mfraBytes=" + (tail?.Length ?? 0));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (!(ex is OutOfMemoryException) && !playback.DolbyVisionHlgAsSdr)
            { RecordPlaybackTrack("split-random-access-failed", playback, " track=" + track + " hresult=" + ex.HResult.ToString("X8")); }
        }

        // Try each verified route for this one representation, starting at the
        // current route offset so network recovery moves to the next CDN.
        private Task<HttpRandomAccessStream> OpenSplitStreamAsync(IReadOnlyList<string> urls,
            DownloadSpeedMeter downloadSpeed, CancellationToken token)
            => OpenSplitStreamAsync(urls, downloadSpeed, token, 256 * 1024, null);

        // The HDR video file uses 2 MiB blocks. With network acceleration on,
        // each block request passes through the same RangeAccelerationFilter
        // as DASH playback: parallel 512 KiB chunks across every verified CDN
        // route of this representation, with the per-route speed memory.
        private Task<HttpRandomAccessStream> OpenSplitVideoStreamAsync(PlaybackInfo playback,
            DownloadSpeedMeter downloadSpeed, int generation, CancellationToken token)
        {
            Func<Windows.Web.Http.Filters.IHttpFilter, Windows.Web.Http.Filters.IHttpFilter> wrap = null;
            bool accelerate = NetworkAccelerationSettings.Enabled && _nativeAccelerationRecoveryWatch != _watchCts;
            if (accelerate)
            {
                var routes = new DashRoutePolicy(playback.DashManifest, playback.DashVideoUrls, playback.DashAudioUrls, _playbackRouteOffset);
                Action<string> trace = AppVersionInfo.IsTestBuild
                    ? new Action<string>(detail => PlaybackDiagnosticsLog.RecordRange("split generation=" + generation + " " + detail))
                    : null;
                // The accelerator counts network bytes on its own meter; the
                // block cache reports the bytes it actually delivered.
                wrap = inner => new RangeAccelerationFilter(inner, routes, new DownloadSpeedMeter(), trace);
            }
            RecordPlaybackTrack("split-video-transport", playback, " acceleration=" + (accelerate ? "on" : "off") + " blockBytes=2097152");
            return OpenSplitStreamAsync(playback.DashVideoUrls, downloadSpeed, token, 2 * 1024 * 1024, wrap);
        }

        private async Task<HttpRandomAccessStream> OpenSplitStreamAsync(IReadOnlyList<string> urls,
            DownloadSpeedMeter downloadSpeed, CancellationToken token, uint blockBytes,
            Func<Windows.Web.Http.Filters.IHttpFilter, Windows.Web.Http.Filters.IHttpFilter> wrap)
        {
            Exception last = null;
            int start = Math.Abs(_playbackRouteOffset) % urls.Count;
            for (int index = 0; index < urls.Count; ++index)
            {
                string url = urls[(start + index) % urls.Count];
                try { return await HttpRandomAccessStream.OpenAsync(new Uri(url), downloadSpeed, token, blockBytes, wrap); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) when (!(ex is OutOfMemoryException)) { last = ex; }
            }
            throw last ?? new InvalidOperationException("没有可用的播放地址。");
        }
    }

    // The HLG transfer byte of a Dolby Vision Profile 8.4 track could not be
    // rewritten; the track must not reach the media pipeline.
    internal sealed class DolbyVisionHlgPatchException : InvalidOperationException
    {
        internal DolbyVisionHlgPatchException() : base("此杜比视界视频的 HLG 基础层未能转换，已停止播放这一档，请选择其他画质。") { }
    }
}
