using System;
using System.Globalization;
using System.Threading;
using BiliXbox.Services;
using Windows.Media.Playback;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Text;

namespace BiliXbox
{
    public sealed partial class MainPage
    {
        private TextBlock _diagnosticText;
        private CancellationTokenSource _diagnosticWatch;
        private long _diagnosticCid;
        private bool _diagnosticHasPlayed;
        private string _diagnosticBufferEvent;
        private string _diagnosticAudio, _diagnosticSoundLevel = "—";
        private int _diagnosticAudioGeneration = -1;
        private TimeSpan _nextAudioDiagnostic;
        private DateTimeOffset? _lastStallAt;
        private TimeSpan? _stallStarted;
        private TimeSpan _lastStallDuration, _nextDiagnosticRefresh, _nextClockDiagnostic;

        private void InitializePlaybackDiagnostics()
        {
            var layout = new StackPanel { Spacing = 10 };
            layout.Children.Add(new TextBlock
            {
                Text = "弹幕舱 - 播放诊断", FontSize = 18,
                FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Colors.White)
            });
            layout.Children.Add(_diagnosticText = new TextBlock
            {
                FontSize = 14, LineHeight = 20, TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromArgb(219, 242, 242, 247))
            });
            PlaybackDiagnosticHost.Content = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(32, 255, 255, 255)),
                Padding = new Thickness(14), CornerRadius = new CornerRadius(14), Child = layout
            };
            PlaybackDiagnosticHost.Visibility = Visibility.Visible;
        }

        private void ObservePlaybackDiagnostics(MediaPlaybackSession session, bool playing, bool waiting)
        {
            var now = _gamepadClock.Elapsed;
            if (_diagnosticWatch != _watchCts || _diagnosticCid != _currentCid)
            {
                _diagnosticWatch = _watchCts;
                _diagnosticCid = _currentCid;
                _diagnosticHasPlayed = false;
                _stallStarted = null;
                _lastStallAt = null;
                _lastStallDuration = TimeSpan.Zero;
                _nextClockDiagnostic = TimeSpan.Zero;
            }
            if (playing && !waiting) _diagnosticHasPlayed = true;
            bool stalled = _diagnosticHasPlayed && _wantsPlayback && waiting && !_seeking;
            if (stalled && !_stallStarted.HasValue)
            {
                _stallStarted = now;
                _lastStallAt = DateTimeOffset.Now;
                _nextClockDiagnostic = TimeSpan.Zero;
                RecordPlaybackTrack("diagnostic-stall-start", _playback,
                    BufferDiagnostic(session.Position.TotalSeconds, _bufferedPlaybackSeconds)
                    + " " + (_adaptivePlayback?.TransportDiagnostic ?? "transport=mp4"));
            }
            if (_media.Source != null && _wantsPlayback && now >= _nextClockDiagnostic)
            {
                _nextClockDiagnostic = now + TimeSpan.FromSeconds(waiting ? 1 : 5);
                var rates = _adaptivePlayback?.TransferRates ?? (-1.0, -1.0);
                var detail = BufferDiagnostic(session.Position.TotalSeconds, _bufferedPlaybackSeconds)
                    + " bufferedSamplePosition=" + _bufferSamplePosition.ToString("0.###", CultureInfo.InvariantCulture)
                    + " nativeState=" + session.PlaybackState + " waiting=" + waiting
                    + " clockStalled=" + _playbackHealth.Stalled + " refillPaused=" + _bufferingPolicy.PausedForBuffer
                    + " nativeBuffering=" + _sessionBuffering + " bufferEvent=" + (_diagnosticBufferEvent ?? "-")
                    + " nativeInboundBps=" + rates.Item1.ToString("0", CultureInfo.InvariantCulture)
                    + " rangeBodyBps=" + rates.Item2.ToString("0", CultureInfo.InvariantCulture)
                    + " displayedBps=" + _downloadBytesPerSecond.ToString("0", CultureInfo.InvariantCulture)
                    + " requestHost=" + (_adaptivePlayback?.LastMediaRequest.Host ?? _stream?.LastMediaRequest.Host ?? "-")
                    + " utc=" + DateTimeOffset.UtcNow.ToString("O");
                RecordPlaybackTrack("diagnostic-clock", _playback, detail);
            }
            else if (!stalled && _stallStarted.HasValue)
            {
                _lastStallDuration = now - _stallStarted.Value;
                _stallStarted = null;
                RecordPlaybackTrack("diagnostic-stall-end", _playback,
                    " stallMs=" + _lastStallDuration.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)
                    + " " + (_adaptivePlayback?.TransportDiagnostic ?? "transport=mp4"));
            }
            if (_media.Source != null && now >= _nextAudioDiagnostic)
            {
                _nextAudioDiagnostic = now + TimeSpan.FromMilliseconds(500);
                ObservePlaybackAudioDiagnostic();
            }
            if (SettingsSide.Visibility == Visibility.Visible && now >= _nextDiagnosticRefresh)
            {
                _nextDiagnosticRefresh = now + TimeSpan.FromMilliseconds(500);
                RefreshPlaybackDiagnosticPanel(session, now);
            }
        }

        private void RecordBufferingDiagnostic(MediaPlaybackSession session, string eventName)
        {
            _diagnosticBufferEvent = eventName;
            _nextClockDiagnostic = TimeSpan.Zero;
            RecordPlaybackTrack("diagnostic-buffer-event", _playback,
                " event=" + eventName + " nativeState=" + session.PlaybackState
                + BufferDiagnostic(session.Position.TotalSeconds, _bufferedPlaybackSeconds)
                + " utc=" + DateTimeOffset.UtcNow.ToString("O"));
        }

        private void RefreshPlaybackDiagnosticPanel(MediaPlaybackSession session, TimeSpan now)
        {
            if (_diagnosticText == null) return;
            var request = _adaptivePlayback?.LastMediaRequest ?? _stream?.LastMediaRequest ?? default((TimeSpan? Duration, int Status, string Host));
            string duration = request.Duration.HasValue ? request.Duration.Value.TotalMilliseconds.ToString("0") + " ms" : "—";
            string status = request.Status > 0 ? request.Status.ToString(CultureInfo.InvariantCulture) : "—";
            string progress = "—";
            if (_media.Source != null)
            {
                try
                {
                    double value = session.BufferingProgress;
                    if (value >= 0 && value <= 1) progress = value.ToString("P0", CultureInfo.InvariantCulture);
                }
                catch (Exception) { }
            }
            var elapsed = _stallStarted.HasValue ? now - _stallStarted.Value : _lastStallDuration;
            string stall = _lastStallAt.HasValue ? elapsed.TotalSeconds.ToString("0.0") + " s"
                + (_stallStarted.HasValue ? "（进行中）" : "") : "—";
            uint width = _media.Source == null ? 0 : session.NaturalVideoWidth;
            uint height = _media.Source == null ? 0 : session.NaturalVideoHeight;
            var text = "视频画质：" + (_playback?.Quality ?? "—")
                + "\n视频编码：" + DiagnosticCodec(_playback?.VideoCodec)
                + (width > 0 && height > 0 ? "\n原生报告尺寸：" + width + "×" + height : "")
                + "\n播放状态：" + session.PlaybackState
                + "\n\n缓冲事件：" + (_diagnosticBufferEvent ?? "—")
                + "\n缓冲进度：" + progress
                + "\n\nCDN 到末字节：" + duration
                + "\nHTTP 状态：" + status
                + "\n媒体接收速率：" + DownloadSpeedMeter.Format(_downloadBytesPerSecond)
                + "\n\n最近一次卡顿：" + (_lastStallAt?.ToString("HH:mm:ss", CultureInfo.InvariantCulture) ?? "—")
                + "\n卡顿持续时间：" + stall
                + "\n\n片源音轨：" + AudioModeLabel(_playback?.AudioMode ?? Models.PlaybackAudioMode.Standard)
                + (string.IsNullOrWhiteSpace(_playback?.AudioCodec) ? "" : " · " + _playback.AudioCodec)
                + "\n应用音量：" + _media.Volume.ToString("P0", CultureInfo.InvariantCulture)
                + " · " + (_media.IsMuted ? "静音" : "未静音")
                + "\n系统音频级别：" + (_media.Source == null ? "—" : _diagnosticSoundLevel);
            if (request.Host != null) text += "\n\nCDN：" + request.Host;
            if (_adaptivePlayback?.AccelerationWaitDiagnostic is string waits) text += "\n" + waits;
            if (_diagnosticText.Text != text) _diagnosticText.Text = text;
        }

        private void ObservePlaybackAudioDiagnostic()
        {
            try { _diagnosticSoundLevel = _media.AudioStateMonitor?.SoundLevel.ToString() ?? "—"; }
            catch (Exception) { _diagnosticSoundLevel = "—"; }
            var detail = " volumePercent=" + (_media.Volume * 100).ToString("0", CultureInfo.InvariantCulture)
                + " muted=" + _media.IsMuted + " soundLevel=" + _diagnosticSoundLevel;
            if (_diagnosticAudioGeneration != _partGeneration || _diagnosticAudio != detail)
            {
                _diagnosticAudioGeneration = _partGeneration;
                _diagnosticAudio = detail;
                RecordPlaybackTrack("diagnostic-audio-state", _playback, detail);
            }
        }

        private static string DiagnosticCodec(string codec)
        {
            if (string.IsNullOrWhiteSpace(codec)) return "—";
            if (codec.StartsWith("hvc1", StringComparison.OrdinalIgnoreCase)
                || codec.StartsWith("hev1", StringComparison.OrdinalIgnoreCase)) return "HEVC";
            if (codec.StartsWith("avc", StringComparison.OrdinalIgnoreCase)) return "H.264";
            if (codec.StartsWith("av01", StringComparison.OrdinalIgnoreCase)) return "AV1";
            return codec;
        }
    }
}
