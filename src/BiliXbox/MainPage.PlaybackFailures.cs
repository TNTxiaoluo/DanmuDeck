using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BiliXbox.Models;
using BiliXbox.Services;
using Windows.Media.Playback;

namespace BiliXbox
{
    public sealed partial class MainPage
    {
        private readonly HashSet<PlaybackInfo> _indexCompatibilityAttempts = new HashSet<PlaybackInfo>();

        private async Task RecoverPlaybackIndexAsync()
        {
            if (_switchingPlaybackRoute || _disposed || !_watching || !_wantsPlayback || _playback == null
                || _partCts == null || _partCts.IsCancellationRequested || _adaptivePlayback?.IndexFailurePending != true) return;
            var playback = _playback; var part = _partCts;
            if (!_indexCompatibilityAttempts.Add(playback)) { ShowPlaybackIndexError(); return; }
            _switchingPlaybackRoute = true;
            try
            {
                RecordPlaybackTrack("index-compat-start", playback);
                SetStatus("正在保留 " + playback.Quality + " 重新解析视频索引…");
                var manifest = await DashIndexCompatibility.ConvertAsync(playback.DashManifest, playback.DashVideoUrls,
                    playback.DashAudioUrls, _playbackRouteOffset, part.Token,
                    detail => PlaybackDiagnosticsLog.Record(detail));
                if (_disposed || !_watching || _partCts != part || part.IsCancellationRequested) return;
                playback.DashManifest = manifest;
                playback.QualityNotice = "已使用兼容索引加载 " + playback.Quality + "。";
                RecordPlaybackTrack("index-compat-ready", playback);
                await LoadPartAsync(_currentCid, playback.QualityId, true, playback, true);
            }
            catch (OperationCanceledException) when (part.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (!_disposed && _watching && _partCts == part && !part.IsCancellationRequested)
                {
                    RecordPlaybackTrack("index-compat-failed", playback, " hresult=" + ex.HResult.ToString("X8"));
                    ShowPlaybackIndexError();
                }
            }
            finally { _switchingPlaybackRoute = false; }
        }

        private async Task<bool> TryRecoverFailedDownloadAsync(MediaPlayerError error, object source, int hresult = 0)
        {
            if (_adaptivePlayback?.IndexFailurePending == true && !_disposed && _watching && _wantsPlayback
                && ReferenceEquals(source, _media.Source)) { await RecoverPlaybackIndexAsync(); return true; }
            if ((error == MediaPlayerError.Unknown || error == MediaPlayerError.NetworkError)
                && _adaptivePlayback?.AccelerationBodyFailed == true && _nativeAccelerationRecoveryWatch != _watchCts
                && !_disposed && _watching && _wantsPlayback && ReferenceEquals(source, _media.Source))
            { await RecoverAccelerationNativelyAsync(); return true; }
            bool network = error == MediaPlayerError.NetworkError
                || (error == MediaPlayerError.Unknown && (_adaptivePlayback?.DownloadFailurePending == true
                    || IsNativeNetworkFailure(hresult)));
            if (!network || _disposed || !_watching || !_wantsPlayback || _video == null
                || _playback == null || _partCts == null || _partCts.IsCancellationRequested
                || !ReferenceEquals(source, _media.Source)) return false;
            if (_switchingPlaybackRoute) return true;
            RecordPlaybackTrack("media-download-failed", _playback,
                " nativeError=" + error + " " + (_adaptivePlayback?.TransportDiagnostic ?? "transport=mp4"));
            bool alternate = HasAlternatePlaybackRoutes;
            // A transient failure is still recoverable when the API supplied
            // only one URL. Reopen it once, sharing the existing finite budget.
            if (!_playbackRecovery.TrySwitchAfterFailure(DateTimeOffset.UtcNow,
                alternate || _playbackRecovery.Attempts == 0)) return false;
            var playback = _playback;
            if (alternate) _playbackRouteOffset = _adaptivePlayback?.FailureRouteOffset ?? (_playbackRouteOffset + 1);
            _switchingPlaybackRoute = true;
            try
            {
                playback.QualityNotice = "播放线路下载失败，正在保留 " + playback.Quality
                    + (alternate ? " 切换备用线路。" : " 重连当前线路。");
                RecordPlaybackTrack("network-failure-route-retry", playback,
                    " routeOffset=" + _playbackRouteOffset + " attempts=" + _playbackRecovery.Attempts);
                await LoadPartAsync(_currentCid, playback.QualityId, true, playback, true);
            }
            catch (OperationCanceledException) { }
            finally { _switchingPlaybackRoute = false; }
            return true;
        }

        private static bool IsNativeNetworkFailure(int error)
        {
            // Microsoft WinSDK Mferror.h / WinINet HRESULTs. Do not classify
            // arbitrary Unknown, decoder, device or format errors as network.
            switch (unchecked((uint)error))
            {
                case 0xC00D4268: case 0xC00D426A: case 0xC00D4278:
                case 0xC00D4283: case 0xC00D4285: case 0xC00D4287: case 0xC00D428E:
                case 0x80072EE2: case 0x80072EE7: case 0x80072EFD: case 0x80072EFE: case 0x80072EFF:
                    return true;
                default: return false;
            }
        }
    }
}
