// Execute the production page transport orchestration with media/native I/O
// substituted. No requests, account writes, or display changes are performed.
namespace Windows.UI.Xaml { }
namespace Windows.Media.Playback { internal enum MediaPlaybackState { Playing, Paused, Buffering } }
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
        private bool _sessionBuffering, _displaySuspended;
        private int _partGeneration;
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
        internal readonly List<string> Diagnostics = new List<string>();
        internal TaskCompletionSource<bool> HoldLoad;
        internal TaskCompletionSource<bool> HoldPlay;
        internal int PlayRequests;
        private int _displayPlayRevision;
        private async Task PlayWithDisplayAsync()
        {
            int revision = _displayPlayRevision, generation = _partGeneration;
            var source = _media.Source;
            ++PlayRequests;
            if (HoldPlay != null) await HoldPlay.Task;
            if (_wantsPlayback && !_disposed && !_displaySuspended && generation == _partGeneration
                && revision == _displayPlayRevision && ReferenceEquals(source, _media.Source))
            { ++_media.Plays; _media.PlaybackSession.PlaybackState = Windows.Media.Playback.MediaPlaybackState.Playing; }
        }
        private void CancelDisplayPlayRequest() { ++_displayPlayRevision; }
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
        private void RecordPlaybackTrack(string phase, PlaybackInfo playback, string detail = "")
        { Diagnostics.Add(phase + detail); }
        private static string Friendly(Exception error) => error.Message;
        internal void Sample(double time, double position = 0, double buffer = 4, bool waiting = true)
        {
            _media.PlaybackSession.Position = TimeSpan.FromSeconds(position);
            _media.PlaybackSession.Buffer = buffer;
            UpdatePlaybackTransport(waiting, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(time));
        }
        internal void Pause() { _wantsPlayback = false; }
        internal void Starve(double time, double buffer, double duration = 100, double position = 10)
        {
            _sessionBuffering = true;
            _media.PlaybackSession.PlaybackState = Windows.Media.Playback.MediaPlaybackState.Buffering;
            _media.PlaybackSession.NaturalDuration = TimeSpan.FromSeconds(duration);
            Sample(time, position, buffer);
        }
        internal void WaitingSample(double time, double buffer, double duration = 100, double position = 10)
        {
            _media.PlaybackSession.NaturalDuration = TimeSpan.FromSeconds(duration);
            Sample(time, position, buffer);
        }
        internal void NewSource()
        { ++_partGeneration; _media.Source = new object(); ResetPlaybackTransportDisplay(); }
        internal void SeekWhileWaiting() { InvalidatePlaybackBuffering(); }
        internal void MissingRanges() { _media.PlaybackSession.ThrowRanges = true; }
        internal int Pauses => _media.Pauses;
        internal int Plays => _media.Plays;
        internal bool BufferWait => IsWaitingForPlaybackBuffer;
        internal bool Wants => _wantsPlayback;
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
        internal int Pauses, Plays;
        internal void Pause()
        { ++Pauses; PlaybackSession.PlaybackState = Windows.Media.Playback.MediaPlaybackState.Paused; }
    }
    internal sealed class FixtureSession
    {
        internal TimeSpan Position;
        internal TimeSpan NaturalDuration = TimeSpan.FromSeconds(1000);
        internal bool ThrowRanges;
        internal Windows.Media.Playback.MediaPlaybackState PlaybackState = Windows.Media.Playback.MediaPlaybackState.Playing;
        internal double Buffer;
        internal IEnumerable<FixtureRange> GetBufferedRanges()
        {
            if (ThrowRanges) throw new Exception("fixture native range unavailable");
            return new[] { new FixtureRange { Start = Position, End = Position + TimeSpan.FromSeconds(Buffer) } };
        }
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
            Check(page.Diagnostics.Count(value => value.StartsWith("pipeline-recovery")) == 1
                && page.Diagnostics.Any(value => value.Contains("position=0 playableSeconds=4") && value.Contains("pipelineAttempts=1")),
                "a decoder recovery records its real buffer, position and consumed attempt exactly once");
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
            Check(page.Diagnostics.Any(value => value.StartsWith("route-recovery") && value.Contains("playableSeconds=0")
                && value.Contains("routeAttempts=1")) && page.Diagnostics.All(value => !value.Contains("https://")),
                "starvation recovery logs its distinct cause without media URLs");
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

            page = new MainPage(); page.Starve(0, 0, position: 0);
            Check(page.Pauses == 0 && page.PlayRequests == 0 && !page.BufferWait,
                "first native buffering stays alive with no data rather than pausing sample acquisition");
            page.Starve(1, 2.99, position: 0);
            Check(page.Pauses == 0 && page.PlayRequests == 0,
                "first native buffering below three seconds does not interfere with downloads");
            page.HoldPlay = new TaskCompletionSource<bool>();
            page.Starve(2, 3, position: 0); page.Starve(3, 6, position: 0);
            Check(page.PlayRequests == 1 && page.Plays == 0 && page.BufferWait && page.Pauses == 0,
                "initial native buffering at three seconds requests one display-safe resume without pausing native acquisition");
            Check(page.Diagnostics.Count(value => value.StartsWith("buffer-resume-request")) == 1
                && page.Diagnostics.All(value => !value.StartsWith("buffer-pause")),
                "initial buffering logs only one resume action and no fabricated engine pause");
            page.HoldPlay.SetResult(true);
            page.WaitingSample(4, 6, position: 0);
            Check(page.PlayRequests == 1 && page.Plays == 1 && !page.BufferWait,
                "first resume completes without repeated Play or a stuck spinner");
            Check(page.Diagnostics.Count(value => value.StartsWith("buffer-resumed")) == 1,
                "completed native Playing resume is logged once rather than on every clock tick");

            page = new MainPage(); page.MissingRanges(); page.Starve(0, 6, position: 0);
            Check(page.PlayRequests == 0 && page.Pauses == 0,
                "unknown initial native ranges do not justify a speculative resume");

            page = new MainPage(); page.Sample(0, position: 10, buffer: 4, waiting: false);
            page.Starve(1, 2.99); int pauses = page.Pauses;
            Check(page.BufferWait && page.Wants && pauses == 1 && page.Plays == 0,
                "production starvation pauses native engine without changing user play intent");
            page.WaitingSample(2, 2.99);
            Check(page.Pauses == pauses && page.Plays == 0, "waiting below3seconds causes no repeated engine calls");
            page.WaitingSample(3, 3); page.WaitingSample(4, 4);
            Check(page.Plays == 1 && page.PlayRequests == 1 && !page.BufferWait && page.Pauses == pauses,
                "native playable3seconds resumes once without an extra Pause and restores playing state");
            page.Starve(5, 0); page.Pause(); page.WaitingSample(6, 5);
            Check(page.Plays == 1 && !page.BufferWait, "user pause cancels pending automatic resume");

            page = new MainPage(); page.Sample(0, position: 10, buffer: 4, waiting: false);
            page.Starve(1, 2.5);
            for (int i = 2; i <= 30; ++i) page.WaitingSample(i, 2.5);
            Check(page.Loads == 0 && page.RestartAttempts == 0 && page.Pauses == 1
                && page.PlayRequests == 0 && page.BufferWait,
                "deliberately paused2.5second buffer is retained beyond the12second decoder watchdog");
            Check(page.Diagnostics.Count == 1 && page.Diagnostics[0].StartsWith("buffer-pause")
                && page.Diagnostics[0].Contains("playableSeconds=2.5"),
                "a long intentional refill emits one pause diagnostic and no repeated actions or recovery");
            page.WaitingSample(31, 3); page.WaitingSample(32, 3);
            Check(page.Loads == 0 && page.Plays == 1 && !page.BufferWait,
                "the retained partial buffer resumes exactly once when it actually reaches3seconds (loads="
                    + page.Loads + ", plays=" + page.Plays + ", waiting=" + page.BufferWait + ")");

            page = new MainPage(); page.Sample(0, position: 10, buffer: 4, waiting: false);
            page.Starve(1, 2.5); page.WaitingSample(2, 3);
            page.Starve(3, 4); page.Starve(15, 4);
            Check(page.Loads == 1 && page.RestartAttempts == 1,
                "a native resume handoff that remains frozen with ample data can still recover its decoder");

            page = new MainPage(); page.Sample(0, position: 10); page.Starve(1, 0);
            page.SeekWhileWaiting(); page.WaitingSample(2, 1, position: 50);
            Check(page.BufferWait && page.Plays == 0, "seek invalidates old buffer but preserves app pause ownership");
            page.WaitingSample(3, 3, position: 50);
            Check(page.Plays == 1, "seeked app-paused source can resume from its new3second buffer");

            page = new MainPage(); page.Sample(0, position: 10); page.Starve(1, .2);
            page.HoldPlay = new TaskCompletionSource<bool>(); page.WaitingSample(2, 3); page.WaitingSample(3, 5);
            Check(page.PlayRequests == 1 && page.Plays == 0, "pending display preparation owns one resume request");
            page.NewSource(); page.HoldPlay.SetResult(true);
            Check(page.Plays == 0 && !page.BufferWait, "late resume completion cannot play a replacement source");
            Check(!page.Diagnostics.Any(value => value.StartsWith("buffer-resumed")),
                "an obsolete native generation cannot claim that a replacement video resumed");

            page = new MainPage(); page.Sample(0, position: 10); page.Starve(1, .2);
            page.HoldPlay = new TaskCompletionSource<bool>(); page.WaitingSample(2, 3);
            page.Pause(); page.HoldPlay.SetResult(true); page.WaitingSample(3, 5);
            Check(page.Plays == 0 && !page.BufferWait && !page.Wants,
                "manual pause during display-safe resume preparation cannot be undone by its completion");

            page = new MainPage(); page.Sample(0, position: 10); page.Starve(1, .2);
            page.HoldPlay = new TaskCompletionSource<bool>(); page.WaitingSample(2, 3);
            page.SeekWhileWaiting(); page.HoldPlay.SetResult(true); page.WaitingSample(3, 1, position: 50);
            Check(page.Plays == 0 && page.BufferWait,
                "seeking cancels the pending resume before it can use a stale pre-seek buffer");
            page.WaitingSample(4, 3, position: 50); page.WaitingSample(5, 3, position: 50);
            Check(page.Plays == 1 && !page.BufferWait,
                "a cancelled pre-seek handoff can resume once after the actual new-position buffer fills");

            page = new MainPage(); page.Sample(0, position: 99.2); page.Starve(1, .78, duration: 100, position: 99.2);
            Check(page.Plays == 1, "production transport resumes a complete tail shorter than3seconds");
            return "PASS: " + _checks + " production page transport integration assertions (mock I/O, no device playback).";
        }
    }
}
