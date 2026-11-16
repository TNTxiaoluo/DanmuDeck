using System;
using System.Threading.Tasks;
using BiliXbox.Models;
using Windows.Media.Playback;

namespace BiliXbox
{
    public sealed partial class MainPage
    {
        private async Task<bool> TryRecoverFailedDownloadAsync(MediaPlayerError error, object source)
        {
            bool network = error == MediaPlayerError.NetworkError
                || (error == MediaPlayerError.Unknown && _adaptivePlayback?.DownloadFailurePending == true);
            if (!network || _disposed || !_watching || !_wantsPlayback || _video == null
                || _playback == null || _partCts == null || _partCts.IsCancellationRequested
                || !ReferenceEquals(source, _media.Source)) return false;
            if (_switchingPlaybackRoute) return true;
            RecordPlaybackTrack("media-download-failed", _playback,
                " nativeError=" + error + " " + (_adaptivePlayback?.TransportDiagnostic ?? "transport=mp4"));
            if (!_playbackRecovery.TrySwitchAfterFailure(DateTimeOffset.UtcNow, HasAlternatePlaybackRoutes)) return false;
            var playback = _playback;
            _playbackRouteOffset = _adaptivePlayback?.FailureRouteOffset ?? (_playbackRouteOffset + 1);
            _switchingPlaybackRoute = true;
            try
            {
                playback.QualityNotice = "播放线路下载失败，正在保留 " + playback.Quality + " 切换备用线路。";
                RecordPlaybackTrack("network-failure-route-retry", playback,
                    " routeOffset=" + _playbackRouteOffset + " attempts=" + _playbackRecovery.Attempts);
                await LoadPartAsync(_currentCid, playback.QualityId, true, playback, true);
            }
            catch (OperationCanceledException) { }
            finally { _switchingPlaybackRoute = false; }
            return true;
        }
    }
}
