// Execute the production page transport orchestration with media/native I/O
// substituted. No requests, account writes, or display changes are performed.
namespace Windows.UI.Xaml { }
namespace Windows.Media.Playback { internal enum MediaPlaybackState { Playing, Paused } }
namespace BiliXbox.Services
{
    internal sealed class AdaptivePlaybackSource
    {
        public bool HasAlternateRoutes;
        public int NextRouteOffset = 7;
        public double CurrentDownloadBytesPerSecond = 5 * 1024 * 1024;
    }
    internal static class PlaybackRouteSelector
    {
        internal static Task<IReadOnlyList<string>> SelectAsync(IReadOnlyList<string> urls, bool audio, CancellationToken token)
            => Task.FromResult(urls);
    }
}
namespace BiliXbox
{
    public sealed partial class MainPage
    {
        private bool _watching = true, _wantsPlayback = true, _disposed, _sourceLoading;
        private bool _seeking, _dialogOpen, _inputPaneVisible, _creatorVisible, _libraryVisible;
        private VideoDetail _video = new VideoDetail();
        private long _currentCid = 17;
        private CancellationTokenSource _partCts = new CancellationTokenSource();
        private CancellationTokenSource _watchCts = new CancellationTokenSource();
        private PlaybackInfo _playback = new PlaybackInfo { QualityId = 120, Quality = "4K", DashManifest = "fixture" };
        private AdaptivePlaybackSource _adaptivePlayback = new AdaptivePlaybackSource();
        private DownloadSpeedMeter _activeDownloadSpeed = new DownloadSpeedMeter();
        private readonly FixtureMedia _media = new FixtureMedia();
        private readonly FixtureText BufferingSpeed = new FixtureText(), PlaybackTransportHint = new FixtureText();
        internal int Loads, OffsetAtLoad;
        internal bool PreserveAtLoad, RecoveryAtLoad;
        internal PlaybackInfo Loaded;
        internal string Status;
        internal TaskCompletionSource<bool> HoldLoad;
        private async Task LoadPartAsync(long cid, int? quality = null, bool preservePosition = false,
            PlaybackInfo preparedPlayback = null, bool routeRecovery = false)
        {
            Loads++; Loaded = preparedPlayback; PreserveAtLoad = preservePosition;
            RecoveryAtLoad = routeRecovery; OffsetAtLoad = _playbackRouteOffset;
            if (cid != _currentCid || quality != preparedPlayback.QualityId) throw new Exception("Changed playback identity");
            ResetPlaybackRecovery(routeRecovery);
            if (HoldLoad != null) await HoldLoad.Task;
        }
        private void SetStatus(string value) { Status = value; }
        private static string Friendly(Exception error) => error.Message;
        internal void Sample(double time, double position = 0, double buffer = 4, bool waiting = true)
        {
            _media.PlaybackSession.Position = TimeSpan.FromSeconds(position);
            _media.PlaybackSession.Buffer = buffer;
            UpdatePlaybackTransport(waiting, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(time));
        }
        internal void Pause() { _wantsPlayback = false; }
        internal void Block() { _seeking = true; }
        internal void Opening() { _sourceLoading = true; }
        internal void CancelPart() { _partCts.Cancel(); }
        internal void NewQuality() { ResetPlaybackRecovery(false); }
        internal void Alternates() { _adaptivePlayback.HasAlternateRoutes = true; }
        internal void Offset(int offset) { _playbackRouteOffset = offset; }
        internal string Speed => BufferingSpeed.Text;
        internal PlaybackInfo Playback => _playback;
        internal int RestartAttempts => _pipelineRecovery.Attempts;
    }
    internal sealed class FixtureText { internal string Text; }
    internal sealed class FixtureMedia
    {
        internal object Source = new object();
        internal FixtureSession PlaybackSession = new FixtureSession();
    }
    internal sealed class FixtureSession
    {
        internal TimeSpan Position;
        internal Windows.Media.Playback.MediaPlaybackState PlaybackState = Windows.Media.Playback.MediaPlaybackState.Playing;
        internal double Buffer;
        internal IEnumerable<FixtureRange> GetBufferedRanges()
        { return new[] { new FixtureRange { Start = Position, End = Position + TimeSpan.FromSeconds(Buffer) } }; }
    }
    internal sealed class FixtureRange { internal TimeSpan Start, End; }
}
namespace BiliXbox.PlaybackTransportTests
{
    public static class Tests
    {
        private static int _checks;
        private static void Check(bool value, string message)
        { ++_checks; if (!value) throw new Exception("FAIL: " + message); }
        public static string Run()
        {
            var page = new MainPage(); page.Offset(2);
            page.Sample(0); page.Sample(11.999);
            Check(page.Loads == 0, "a short buffered wait must not rebuild");
            page.Sample(12);
            Check(page.Loads == 1 && page.Loaded == page.Playback && page.Loaded.QualityId == 120,
                "buffered stall rebuilds the same actual4K track even without alternate routes");
            Check(page.PreserveAtLoad && page.RecoveryAtLoad && page.OffsetAtLoad == 2,
                "pipeline recovery keeps position/route and existing budget");
            Check(page.Status.Contains("恢复播放") && !page.Status.Contains("路线"), "status describes playback recovery");
            Check(page.Speed.Contains("5.00 MB/s"), "DASH UI uses native throughput instead of an unused managed meter");
            page.Sample(13); page.Sample(200);
            Check(page.Loads == 1 && page.RestartAttempts == 1, "automatic rebuild cannot rearm itself forever");
            page.NewQuality(); page.Sample(201); page.Sample(213);
            Check(page.Loads == 2 && page.OffsetAtLoad == 0, "new user quality rearms once and resets route offset");

            foreach (string interruption in new[] { "pause", "seek", "opening", "cancel" })
            {
                page = new MainPage(); page.Sample(0);
                if (interruption == "pause") page.Pause();
                if (interruption == "seek") page.Block();
                if (interruption == "opening") page.Opening();
                if (interruption == "cancel") page.CancelPart();
                page.Sample(30);
                Check(page.Loads == 0, interruption + " cannot trigger an unsolicited source rebuild");
            }

            page = new MainPage(); page.HoldLoad = new TaskCompletionSource<bool>();
            page.Sample(0); page.Sample(12); page.Sample(100);
            Check(page.Loads == 1, "pending rebuild serializes later watchdog samples");
            page.HoldLoad.SetResult(true);

            page = new MainPage(); page.Alternates(); page.Sample(0, buffer: 0); page.Sample(8, buffer: 0);
            Check(page.Loads == 1 && page.OffsetAtLoad == 7, "actual starvation still uses alternate-route recovery");
            Check(page.RestartAttempts == 0 && page.Status.Contains("重新缓冲"), "starvation does not consume buffered-stall restart");
            page = new MainPage(); page.Sample(0, buffer: 0); page.Sample(100, buffer: 0);
            Check(page.Loads == 0, "empty cache cannot be misclassified as decoder pressure");
            page = new MainPage(); page.Sample(0, waiting: false); page.Sample(100, position: 100, waiting: false);
            Check(page.Loads == 0, "healthy4K playback is untouched");
            page = new MainPage();
            for (int i = 0; i <= 12; ++i) page.Sample(i, position: i * 0.1, waiting: false);
            Check(page.Loads == 1, "slideshow clock progress cannot postpone recovery by briefly clearing the spinner");
            page = new MainPage();
            for (int i = 0; i <= 60; ++i) page.Sample(i, position: i, waiting: false);
            Check(page.Loads == 0, "continuous normal nativePlaying clock never rebuilds the source");
            return "PASS: " + _checks + " production page transport integration assertions (mock I/O, no device playback).";
        }
    }
}
