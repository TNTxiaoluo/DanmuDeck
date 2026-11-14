// Execute the real page validation orchestration with native-track metadata
// substituted. These tests do not claim to validate an Xbox decoder itself.
namespace Windows.UI.Xaml
{
    public enum Visibility { Visible, Collapsed }
}
namespace Windows.Media.Core
{
    public sealed class MediaSource : IMediaPlaybackSource { }
    public sealed class VideoTrack
    {
        internal FixtureVideoProperties Properties = new FixtureVideoProperties();
        internal Action Reading;
        public FixtureVideoProperties GetEncodingProperties() { Reading?.Invoke(); return Properties; }
    }
    public sealed class FixtureRatio { public uint Numerator, Denominator = 1; }
    public sealed class FixtureVideoProperties
    {
        public uint Width = 3840, Height = 2160;
        public FixtureRatio FrameRate = new FixtureRatio();
    }
}
namespace Windows.Media.Playback
{
    public interface IMediaPlaybackSource { }
    public sealed class MediaPlaybackItem : IMediaPlaybackSource
    {
        public MediaPlaybackItem(MediaSource source) { Source = source; }
        public MediaSource Source;
        public FixtureVideoTracks VideoTracks = new FixtureVideoTracks();
    }
    public sealed class FixtureVideoTracks : List<VideoTrack> { public int SelectedIndex = -1; }
}
namespace BiliXbox
{
    public sealed partial class MainPage
    {
        private bool _disposed, _watching = true, _sourceLoading = true, _sessionBuffering, _wantsPlayback = true, _resumeAfterOpen = true;
        private int _partGeneration = 1;
        private long _currentCid = 12;
        private CancellationTokenSource _watchCts = new CancellationTokenSource();
        private PlaybackInfo _playback;
        private VideoDetail _video = new VideoDetail();
        private MediaSource _source;
        private FixtureMedia _media = new FixtureMedia();
        private FixtureElement PlaybackError = new FixtureElement(), ErrorPanel = new FixtureElement(), OfficialVideoButton = new FixtureElement();
        internal readonly List<string> Logs = new List<string>();
        internal PlaybackInfo Loaded;
        internal int LoadCount, QualityUpdates;
        internal Action ResetAction;
        private void RecordPlaybackTrack(string phase, PlaybackInfo info, string detail = "") => Logs.Add(phase + detail);
        private void UpdateQualities() { ++QualityUpdates; }
        private void UpdatePlaybackUi() { }
        private void SetStatus(string text) { }
        private void ShowControls() { }
        private Task ResetPlaybackDisplayAsync() { ResetAction?.Invoke(); return Task.CompletedTask; }
        private Task LoadPartAsync(long cid, int? quality = null, bool preservePosition = false, PlaybackInfo preparedPlayback = null)
        {
            if (cid != _currentCid || !preservePosition) throw new Exception("fallback must preserve playback position and video");
            ++LoadCount; Loaded = preparedPlayback; return Task.CompletedTask;
        }
        internal IMediaPlaybackSource Attach(PlaybackInfo info, uint numerator = 60, uint denominator = 1)
        {
            _playback = info; _source = new MediaSource();
            _media.Source = CreateFrameRateValidationPlaybackSource(_source, info, _partGeneration);
            if (_frameRateValidationItem != null)
            {
                _frameRateValidationItem.VideoTracks.Add(new VideoTrack
                { Properties = new FixtureVideoProperties { FrameRate = new FixtureRatio { Numerator = numerator, Denominator = denominator } } });
                _frameRateValidationItem.VideoTracks.SelectedIndex = 0;
            }
            return _media.Source;
        }
        internal Task<bool> Validate() => ValidateNativeFrameRateAsync();
        internal void Invalidate() { ++_partGeneration; }
        internal void Clear() => ClearNativeFrameRateValidationPlaybackItem();
        internal FixtureVideoTracks Tracks => _frameRateValidationItem.VideoTracks;
        internal bool HasError => ErrorPanel.Visibility == Visibility.Visible;
        internal bool WantsPlayback => _wantsPlayback;
        internal int Pauses => _media.Pauses;
        internal void ReplaceMediaSource() { _media.Source = new MediaSource(); }
        internal void ReplaceWatch() { _watchCts = new CancellationTokenSource(); }
        internal void CancelWatch() { _watchCts.Cancel(); }
        internal void SetSessionDimensions(uint width, uint height)
        { _media.PlaybackSession.NaturalVideoWidth = width; _media.PlaybackSession.NaturalVideoHeight = height; }
    }
    internal sealed class FixtureMedia
    {
        internal IMediaPlaybackSource Source;
        internal FixtureSession PlaybackSession = new FixtureSession();
        internal int Pauses;
        internal void Pause() { ++Pauses; }
    }
    internal sealed class FixtureSession { internal uint NaturalVideoWidth, NaturalVideoHeight; }
    internal sealed class FixtureElement
    {
        internal Visibility Visibility = Visibility.Collapsed;
        internal string Text;
    }
}
namespace BiliXbox.FrameRateValidationTests
{
    public static class Tests
    {
        private static int _passed;
        private static void Check(bool value, string text) { if (!value) throw new Exception("FAIL: " + text); ++_passed; }
        private static PlaybackInfo Trial(double reported = 62.5) => new PlaybackInfo
        {
            QualityId = 120, Quality = "4K", VideoFrameRate = reported, VideoFrameRateProbe = 60,
            RequiresNativeFrameRateValidation = true
        };
        public static async Task<string> Run()
        {
            _passed = 0;
            var page = new MainPage(); var standard = new PlaybackInfo { VideoFrameRate = 30 };
            Check(page.Attach(standard) is MediaSource && await page.Validate(), "normal media keeps its original source and playback path");

            page = new MainPage(); var info = Trial();
            Check(page.Attach(info, 60000, 1001) is MediaPlaybackItem, "suspect metadata receives a native-track wrapper");
            Check(await page.Validate() && info.NativeFrameRateVerified && Math.Abs(info.VideoFrameRateNative - 59.94006) < .001,
                "native rational frame rate confirms actual supported cadence");
            Check(info.VideoFrameRate == 62.5 && info.VideoFrameRateProbe == 60 && info.RequiresNativeFrameRateValidation,
                "native verification preserves raw declaration, probe value and special-path marker");
            Check(page.Logs[0].Contains("native-frame-rate-verified") && page.Logs[0].Contains("60000/1001"), "native ratio is included in diagnostics");

            page = new MainPage(); info = Trial(); page.Attach(info, 125, 2);
            Check(await page.Validate() && !info.NativeFrameRateVerified && info.VideoFrameRateNative == 62.5,
                "native 62.5 remains a declared trial, not fabricated 60fps support");
            Check(info.FrameRateNotice.Contains("尚未确认") && page.Pauses == 0, "near-60 trial is explicitly unverified and continues to native recovery");
            page = new MainPage(); info = Trial(); page.Attach(info, 63);
            Check(await page.Validate() && !info.NativeFrameRateVerified, "bounded 63fps trial remains explicitly unverified");
            Check(page.Logs[0].Contains("trial-kind=near60-metadata"),
                "near60 metadata anomaly is classified independently of declared high-fps sources");
            foreach (double reported in new[] { 119.993, 119.994, 120.0, 120.001 })
            {
                page = new MainPage(); info = Trial(reported); page.Attach(info, 30);
                Check(await page.Validate() && info.NativeFrameRateVerified && info.VideoFrameRateNative == 30
                    && info.VideoFrameRate == reported && info.VideoFrameRateProbe == 60,
                    "native30fps metadata may verify a declared high-fps4K source without rewriting its API declaration: " + reported);
                Check(page.Logs[0].Contains("trial-kind=high-frame-rate-declaration")
                    && page.Logs[0].Contains("native-fps=30"), "high-fps declaration and native media metadata are logged separately");
            }
            foreach (var ratio in new[] { new FixtureRatio { Numerator = 119993, Denominator = 1000 },
                new FixtureRatio { Numerator = 119994, Denominator = 1000 },
                new FixtureRatio { Numerator = 120, Denominator = 1 },
                new FixtureRatio { Numerator = 120001, Denominator = 1000 } })
            {
                double rate = (double)ratio.Numerator / ratio.Denominator;
                page = new MainPage(); info = Trial(rate); page.Attach(info, ratio.Numerator, ratio.Denominator);
                info.CodecFallback = new PlaybackInfo { QualityId = 80, Quality = "1080P" };
                Check(await page.Validate() && !info.NativeFrameRateVerified && info.VideoFrameRateNative == rate
                    && info.VideoFrameRate == rate && page.LoadCount == 0 && page.Pauses == 0,
                    "native high-fps metadata stays a bounded unverified compatibility trial, not fake60fps or premature fallback: " + rate);
                Check(info.FrameRateNotice.Contains("高帧率") && info.FrameRateNotice.Contains("60Hz")
                    && info.FrameRateNotice.Contains("尚未确认") && page.Logs[0].Contains("verified=false output-hz=60"),
                    "high-fps native trial is explicitly unverified and only requests60Hz output");
            }
            page = new MainPage(); info = Trial(119.994); page.Attach(info, 0);
            Check(await page.Validate() && !info.NativeFrameRateVerified && info.VideoFrameRateNative == 0
                && info.FrameRateNotice.Contains("高帧率兼容性试播") && page.Pauses == 0,
                "missing native cadence retains the explicit high-fps trial without fabricating verification");

            page = new MainPage(); info = Trial(); page.Attach(info, 60, 0);
            Check(await page.Validate() && !info.NativeFrameRateVerified && info.VideoFrameRateNative == 0,
                "zero denominator is unknown, never native verification");
            page = new MainPage(); info = Trial(); page.Attach(info); page.Tracks.Clear();
            Check(await page.Validate() && !info.NativeFrameRateVerified && page.LoadCount == 0,
                "temporarily missing track metadata does not falsely reject the decoder");
            page = new MainPage(); info = Trial(); page.Attach(info); page.Tracks.SelectedIndex = -1;
            Check(await page.Validate() && info.NativeFrameRateVerified,
                "a single native track may be verified before its selection index is published");
            page = new MainPage(); info = Trial(); page.Attach(info, 120);
            page.Tracks.Add(new VideoTrack { Properties = new FixtureVideoProperties
                { FrameRate = new FixtureRatio { Numerator = 60, Denominator = 1 } } });
            page.Tracks.SelectedIndex = 1;
            Check(await page.Validate() && info.NativeFrameRateVerified && info.VideoFrameRateNative == 60,
                "multiple native tracks validate the selected track rather than an incompatible first track");
            page = new MainPage(); info = Trial(); page.Attach(info, 120);
            page.Tracks.Add(new VideoTrack()); page.Tracks.SelectedIndex = -1;
            Check(await page.Validate() && !info.NativeFrameRateVerified && info.VideoFrameRateNative == 0
                && page.LoadCount == 0,
                "multiple unselected native tracks remain pending without rejecting an arbitrary track");
            page = new MainPage(); info = Trial(); page.Attach(info); page.Tracks[0].Reading = () => { throw new InvalidOperationException(); };
            Check(await page.Validate() && page.Logs[0].Contains("native-properties-hresult"), "native metadata exception remains a logged unconfirmed trial");

            page = new MainPage(); info = Trial(120); page.Attach(info, 240);
            var videoFallback = new PlaybackInfo { QualityId = 120, Quality = "4K HEVC alternate" };
            info.CodecFallback = videoFallback; info.AudioFallback = new PlaybackInfo { Quality = "same 240fps video, AAC" };
            Check(!await page.Validate() && page.Loaded == videoFallback && page.LoadCount == 1 && page.Pauses == 1,
                "native240fps skips the identical-video audio fallback and preserves another real4K candidate");
            Check(info.CodecFallback == null && info.AudioFallback == null && info.QualityFailure == PlaybackQualityFailure.SourceUnsupported,
                "rejected candidate is consumed once and classified independently of hardware denial");
            Check(page.Logs[0].Contains("reason=native-frame-rate-above-120.001-trial-limit"), "native rejection records its concrete reason");

            page = new MainPage(); info = Trial(120); page.Attach(info, 120002, 1000);
            Check(!await page.Validate() && page.HasError && !page.WantsPlayback,
                "native rate above120.001 with no real fallback cannot autoplay");
            page = new MainPage(); info = Trial(); page.Attach(info);
            page.Tracks[0].Properties.Width = 1920; page.Tracks[0].Properties.Height = 1080;
            info.CodecFallback = videoFallback;
            Check(!await page.Validate() && page.Loaded == videoFallback && !info.NativeFrameRateVerified
                && page.Logs[0].Contains("reason=native-dimensions-not-4k"),
                "native 1080 dimensions cannot be advertised as verified 4K even at compatible 60fps");
            page = new MainPage(); info = Trial(); page.Attach(info);
            page.Tracks[0].Properties.Width = page.Tracks[0].Properties.Height = 0;
            Check(await page.Validate() && !info.NativeFrameRateVerified && info.FrameRateNotice.Contains("尺寸尚未确认"),
                "missing native dimensions keep genuine 4K verification pending");
            page = new MainPage(); info = Trial(); page.Attach(info);
            page.Tracks[0].Properties.Width = page.Tracks[0].Properties.Height = 0;
            page.SetSessionDimensions(3840, 2160);
            Check(await page.Validate() && info.NativeFrameRateVerified && page.Logs[0].Contains("session-native=3840x2160"),
                "current native playback-session dimensions may confirm a track with incomplete dimensions");
            page = new MainPage(); info = Trial(); page.Attach(info);
            page.Tracks[0].Properties.Width = 2880; page.Tracks[0].Properties.Height = 2160;
            Check(await page.Validate() && info.NativeFrameRateVerified,
                "native 4:3 UHD source retains a valid 2160 short edge");
            page = new MainPage(); info = Trial(); page.Attach(info);
            page.Tracks[0].Properties.Width = 2160; page.Tracks[0].Properties.Height = 3840;
            Check(await page.Validate() && info.NativeFrameRateVerified, "native portrait 4K dimensions are not rejected as landscape-only");
            page = new MainPage(); info = Trial(); page.Attach(info, 240); page.ResetAction = page.Invalidate;
            Check(!await page.Validate() && !page.HasError, "a source change during output reset suppresses the stale error surface");

            page = new MainPage(); info = Trial(); page.Attach(info); page.Invalidate();
            Check(!await page.Validate() && page.Logs.Count == 0, "old part generation cannot validate a newly selected video");
            page = new MainPage(); info = Trial(); page.Attach(info); page.ReplaceMediaSource();
            Check(!await page.Validate() && page.Logs.Count == 0, "replaced native wrapper cannot update current playback");
            page = new MainPage(); info = Trial(); page.Attach(info); page.Clear();
            Check(!await page.Validate() && page.Logs.Count == 0, "disposed wrapper references cannot update current playback");
            page = new MainPage(); info = Trial(); page.Attach(info); page.Tracks[0].Reading = page.Invalidate;
            Check(!await page.Validate() && page.Logs.Count == 0, "ownership is rechecked after reading native track metadata");
            page = new MainPage(); info = Trial(); page.Attach(info); page.Tracks[0].Reading = page.ReplaceWatch;
            Check(!await page.Validate() && page.Logs.Count == 0, "a cancelled/replaced video watch cannot update native verification");
            page = new MainPage(); info = Trial(); page.Attach(info); page.CancelWatch();
            Check(!await page.Validate() && page.Logs.Count == 0, "a cancelled current watch cannot verify or restart playback");
            page = new MainPage(); info = Trial(); page.Attach(info); page.Tracks[0].Reading = page.CancelWatch;
            Check(!await page.Validate() && page.Logs.Count == 0,
                "cancellation during native metadata access suppresses verification and fallback");
            return "PASS: " + _passed + " native frame-rate validation assertions.";
        }
    }
}
