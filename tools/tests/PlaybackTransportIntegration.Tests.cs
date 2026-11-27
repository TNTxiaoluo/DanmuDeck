// Execute the production page transport orchestration with media/native I/O
// substituted. No requests, account writes, or display changes are performed.
namespace Windows.UI.Xaml { }
namespace Windows.Media.Playback {
    internal enum MediaPlaybackState { Playing, Paused, Buffering }
    internal enum MediaPlayerError { Unknown, Aborted, NetworkError, DecodingError, SourceNotSupported }
}
namespace BiliXbox.Services
{
    internal sealed class AdaptivePlaybackSource
    {
        public bool HasAlternateRoutes;
        public bool HasUntriedRoute;
        public int SelectedRouteOffset = 1;
        public int NextRouteOffset = 7;
        public int FailureRouteOffset = 6;
        public bool DownloadFailurePending;
        public bool AccelerationEnabled, AccelerationBodyFailed;
        public bool IndexFailurePending;
        public string TransportDiagnostic => "requested=3 failed=1 totalBytes=0";
        public double CurrentDownloadBytesPerSecond = 5 * 1024 * 1024;
    }
    internal static class PlaybackRouteSelector
    {
        internal static Task<IReadOnlyList<string>> SelectAsync(IReadOnlyList<string> urls, bool audio, CancellationToken token)
            => Task.FromResult(urls);
    }
    internal static class DashIndexCompatibility
    {
        internal static TaskCompletionSource<string> Hold;
        internal static bool Fail;
        internal static Task<string> ConvertAsync(string m, IReadOnlyList<string> v, IReadOnlyList<string> a, int r,
            CancellationToken token, Action<string> diagnostic)
            => Fail ? Task.FromException<string>(new Exception("invalid SIDX")) : Hold?.Task ?? Task.FromResult("compatible");
    }
    internal static class PlaybackDiagnosticsLog { internal static void Record(string value) { } }
}
namespace BiliXbox
{
    public sealed partial class MainPage
    {
        // The HDR split transport is not part of this DASH fixture.
        private bool TrySplitTransportBuffer(out double seconds) { seconds = -1; return false; }
        private bool _watching = true, _wantsPlayback = true, _disposed, _sourceLoading;
        private bool _seeking, _dialogOpen, _inputPaneVisible, _creatorVisible, _libraryVisible;
        private bool _sessionBuffering, _displaySuspended;
        private int _partGeneration;
        private TimeSpan? _restorePosition;
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
        internal void Sample(double time, double position = 0, double buffer = 4, bool waiting = true, bool clockStalled = false)
        {
            _media.PlaybackSession.Position = TimeSpan.FromSeconds(position);
            _media.PlaybackSession.Buffer = buffer;
            UpdatePlaybackTransport(waiting, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(time), clockStalled);
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
        internal void Bookmark(double position) { _restorePosition = TimeSpan.FromSeconds(position); }
        internal void ApplyBookmark() { ApplyRestorePosition(); }
        internal void Opened() { _sourceLoading = false; ApplyRestorePosition(); }
        internal TimeSpan Position => _media.PlaybackSession.Position;
        internal void MissingRanges() { _media.PlaybackSession.ThrowRanges = true; }
        internal int Pauses => _media.Pauses;
        internal int Plays => _media.Plays;
        internal bool BufferWait => IsWaitingForPlaybackBuffer;
        internal bool Wants => _wantsPlayback;
        internal void Block() { _seeking = true; }
        internal void Dialog() { _dialogOpen = true; }
        internal void SuspendDisplay() { _displaySuspended = true; }
        internal void Opening() { _sourceLoading = true; }
        internal void CancelPart() { _partCts.Cancel(); }
        internal void NewQuality() { ResetPlaybackRecovery(false); }
        internal void Alternates() { _adaptivePlayback.HasAlternateRoutes = _adaptivePlayback.HasUntriedRoute = true; }
        internal void SetReceiveRate(double value) { _adaptivePlayback.CurrentDownloadBytesPerSecond = value; }
        internal void Receiving(bool value) { _adaptivePlayback.CurrentDownloadBytesPerSecond = value ? 5 * 1024 * 1024 : 0; }
        internal void Acceleration(bool failed=false) { _adaptivePlayback.AccelerationEnabled=true; _adaptivePlayback.AccelerationBodyFailed=failed; }
        internal void InvalidIndex() { _adaptivePlayback.IndexFailurePending = true; }
        internal int IndexErrors;
        private void ShowPlaybackIndexError() { ++IndexErrors; _wantsPlayback = false; _media.Source = null; }
        internal Task<bool> Fail(Windows.Media.Playback.MediaPlayerError error, bool downloadFailed = false, int hresult = 0)
        { _adaptivePlayback.DownloadFailurePending = downloadFailed; return TryRecoverFailedDownloadAsync(error,_media.Source,hresult); }
        internal void Offset(int offset) { _playbackRouteOffset = offset; }
        internal string Speed => BufferingSpeed.Text;
        internal IReadOnlyList<PlaybackBufferRange> SeekBuffer { get; private set; } = Array.Empty<PlaybackBufferRange>();
        private void SetSeekBufferRanges(IEnumerable<PlaybackBufferRange> ranges, double duration)
        { SeekBuffer = PlaybackBufferRanges.TimelineFractions(duration, ranges); }
        internal PlaybackInfo Playback => _playback;
        internal int RestartAttempts => _pipelineRecovery.Attempts;
        internal bool RecoveringNatively => _nativeAccelerationRecoveryWatch == _watchCts;
    }
    internal sealed class FixtureText { internal string Text; }
    internal sealed class FixtureMedia
    {
        internal object Source = new object();
        internal FixtureSession PlaybackSession = new FixtureSession();
        internal int Pauses, Plays;
        internal void Pause()
        { ++Pauses; PlaybackSession.PlaybackState = Windows.Media.Playback.MediaPlaybackState.Paused; }
        internal void Play()
        { ++Plays; PlaybackSession.PlaybackState = Windows.Media.Playback.MediaPlaybackState.Playing; }
    }
    internal sealed class FixtureSession
    {
        internal bool CanSeek = true;
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
            var bufferedFreeze = new MainPage();
            bufferedFreeze.SetReceiveRate(2618022);
            bufferedFreeze.Sample(0, position: 192.277, buffer: 2.9, clockStalled: true);
            Check(bufferedFreeze.Pauses == 0 && bufferedFreeze.Plays == 0,
                "frozen clock does not interrupt an incompletely refilled source");
            bufferedFreeze.Sample(.125, position: 192.277, buffer: 5.331, clockStalled: true);
            Check(bufferedFreeze.Pauses == 1 && bufferedFreeze.Plays == 1 && bufferedFreeze.Loads == 0,
                "a frozen native Playing clock restarts in place on the first ready sample");
            bufferedFreeze.Sample(.4, position: 192.277, buffer: 6.206, clockStalled: true);
            Check(bufferedFreeze.Plays == 1 && Math.Abs(bufferedFreeze.Position.TotalSeconds - 192.277) < .001,
                "the same frozen frame is never nudged twice or sought");
            bufferedFreeze.Sample(.8, position: 193.8, buffer: 5, waiting: false);
            bufferedFreeze.Sample(1.4, position: 193.8, buffer: 5, clockStalled: true);
            Check(bufferedFreeze.Plays == 2 && bufferedFreeze.Loads == 0,
                "real progress permits recovery in a later freeze without replacing the source");
            foreach (Action<MainPage> block in new Action<MainPage>[] { player => player.Pause(), player => player.Opening(),
                player => player.Block(), player => player.SuspendDisplay(), player => player.CancelPart() })
            {
                var inactive = new MainPage();
                block(inactive);
                inactive.Sample(0, position: 192.277, buffer: 5.331, clockStalled: true);
                Check(inactive.Plays == 0, "in-place recovery respects playback intent and source ownership");
            }
            var downloading = new MainPage(); downloading.Alternates();
            var history = new MainPage(); history.Acceleration(); history.Opening(); history.Bookmark(150);
            history.ApplyBookmark();
            Check(history.Position == TimeSpan.Zero && !history.Diagnostics.Any(item => item.StartsWith("restore-position-applied")),
                "DASH can advertise duration and seek support before it opens without applying a history bookmark");
            history.Opened(); history.ApplyBookmark();
            Check(history.Position.TotalSeconds == 150 && history.Diagnostics.Count(item => item.StartsWith("restore-position-applied")) == 1,
                "the original history position is applied once when the same media actually opens");
            var timeline = new MainPage();
            timeline.Sample(0, position: 100, buffer: 40, waiting: false);
            Check(timeline.SeekBuffer.Count == 1 && timeline.SeekBuffer[0].Start == .1 && timeline.SeekBuffer[0].End == .14,
                "player forwards native playable ranges and timeline duration to the visual");
            timeline.SeekWhileWaiting();
            Check(timeline.SeekBuffer.Count == 0, "seek immediately clears stale buffer graphics");
            timeline.Sample(1, position: 220, buffer: 30, waiting: false);
            Check(timeline.SeekBuffer[0].Start == .22 && timeline.SeekBuffer[0].End == .25,
                "the next native sample displays the new actual range");
            timeline.NewSource();
            Check(timeline.SeekBuffer.Count == 0, "replacing the media source empties the old buffer track");
            var accelerated=new MainPage();accelerated.Acceleration();var hdr=accelerated.Playback;hdr.IsHdr10=true;hdr.QualityId=125;
            for(int second=0;second<=30;second++)accelerated.Sample(second,buffer:.3);
            Check(accelerated.Loads==1&&ReferenceEquals(accelerated.Loaded,hdr)&&hdr.IsHdr10&&hdr.QualityId==125
                &&accelerated.PreserveAtLoad&&accelerated.RecoveryAtLoad,
                "network throughput without playable progress triggers one native recovery retaining HDR/quality/position");
            var pausedAccelerator=new MainPage();pausedAccelerator.Acceleration();pausedAccelerator.Pause();
            pausedAccelerator.Sample(0,buffer:0);pausedAccelerator.Sample(30,buffer:0);
            Check(pausedAccelerator.Loads==0,"user pause cannot be undone by acceleration fallback");
            var partial=new MainPage();partial.Acceleration(true);var same=partial.Playback;
            Check(partial.Fail(Windows.Media.Playback.MediaPlayerError.Unknown).GetAwaiter().GetResult()&&partial.Loads==1&&ReferenceEquals(partial.Loaded,same),
                "a failed accelerated response body recovers at the same quality before codec fallback");
            Check(partial.OffsetAtLoad == 1, "terminal acceleration recovery keeps the native-selected backup");
            var decoding = new MainPage(); decoding.Acceleration(true);
            Check(!decoding.Fail(Windows.Media.Playback.MediaPlayerError.DecodingError).GetAwaiter().GetResult()
                && decoding.Loads == 0, "historical body failure does not override a terminal decoder error");

            var briefDip = new MainPage(); briefDip.Acceleration(true); briefDip.Alternates();
            briefDip.Sample(0, position: 0, buffer: 3.8, waiting: false);
            briefDip.Sample(5, position: 5, buffer: 2.7, waiting: false);
            briefDip.Sample(6, position: 5, buffer: 1.9);
            briefDip.Sample(7, position: 5, buffer: 2.2);
            briefDip.Sample(8, position: 6, buffer: 3.1, waiting: false);
            for (int second = 9; second <= 27; second++)
                briefDip.Sample(second, position: second - 2, buffer: 1.9, waiting: true);
            Check(briefDip.Loads == 0 && !briefDip.RecoveringNatively,
                "a brief buffering dip and later low cache while moving do not replace the source");

            var retrying = new MainPage(); retrying.Acceleration(true); var retainedQuality = retrying.Playback;
            retainedQuality.IsHdr10 = true; retainedQuality.QualityId = 125;
            retrying.Sample(0, position: 21.25, buffer: 1.717);
            retrying.Sample(4, position: 21.25, buffer: 1.91);
            retrying.Sample(7.5, position: 21.25, buffer: 2.01);
            Check(retrying.Loads == 0, "body failure receives a full same-source retry window");
            retrying.Sample(8, position: 21.25, buffer: 2.01);
            Check(retrying.Loads == 1 && ReferenceEquals(retrying.Loaded, retainedQuality)
                && retrying.Loaded.IsHdr10 && retrying.Loaded.QualityId == 125
                && retrying.PreserveAtLoad && retrying.RecoveryAtLoad && retrying.OffsetAtLoad == 1,
                "sustained nonterminal body failure recovers natively at the same HDR quality and selected route");
            retrying.Sample(9, position: 21.25, buffer: 2); retrying.Sample(19, position: 21.25, buffer: 2.3);
            Check(retrying.Loads == 1, "partial retry progress cannot repeat native acceleration recovery for the same watch");
            var healthyCache = new MainPage(); healthyCache.Acceleration(true);
            healthyCache.Sample(0, buffer: 40); healthyCache.Sample(5, position: 5, buffer: 35, waiting: false);
            Check(healthyCache.Loads == 0, "a bypass flag with healthy playable cache never discards that cache");
            var growingCache = new MainPage(); growingCache.Acceleration(true);
            growingCache.Sample(0, buffer: .7);
            growingCache.Sample(7, buffer: 1.6);
            growingCache.Sample(14, buffer: 2.5);
            growingCache.Sample(20, position: 1, buffer: 3.1, waiting: false);
            Check(growingCache.Loads == 0, "native retries making meaningful playable progress keep the source while refilling");
            foreach (string interruption in new[] { "pause", "seek", "dialog", "cancel", "display" })
            {
                var guarded = new MainPage(); guarded.Acceleration(true);
                if (interruption == "pause") guarded.Pause();
                if (interruption == "seek") guarded.Block();
                if (interruption == "dialog") guarded.Dialog();
                if (interruption == "cancel") guarded.CancelPart();
                if (interruption == "display") guarded.SuspendDisplay();
                guarded.Sample(0, buffer: 1.7);
                guarded.Sample(20, buffer: 1.7);
                Check(guarded.Loads == 0, interruption + " guards nonterminal acceleration recovery");
            }
            var unknownCache = new MainPage(); unknownCache.Acceleration(true); unknownCache.MissingRanges();
            unknownCache.Sample(0); unknownCache.Sample(20);
            Check(unknownCache.Loads == 0, "unknown native ranges cannot prove starvation for acceleration recovery");
            var refillFailure = new MainPage(); refillFailure.Acceleration();
            refillFailure.Sample(0, position: 21.24, buffer: 4, waiting: false);
            refillFailure.Starve(1, .394, position: 21.24);
            Check(refillFailure.Pauses == 1 && refillFailure.BufferWait && refillFailure.Loads == 0,
                "the recorded low-cache trace first enters the normal three-second refill wait");
            refillFailure.Acceleration(true);
            refillFailure.WaitingSample(11, 1.717, position: 21.25);
            refillFailure.WaitingSample(18.5, 1.9, position: 21.25);
            Check(refillFailure.Loads == 0, "an earlier refill wait does not count as waiting after accelerated body failure");
            refillFailure.WaitingSample(19, 1.9, position: 21.25);
            Check(refillFailure.Loads == 1 && refillFailure.PreserveAtLoad && refillFailure.OffsetAtLoad == 1,
                "a still-starved source can recover after bounded native retries");
            for (int i=0;i<=40;++i) downloading.Sample(i,buffer:0);
            Check(downloading.Loads==0, "active downloads with zero playable cache do not repeatedly discard the source");
            downloading.Receiving(false); downloading.Sample(41,buffer:0); downloading.Sample(49,buffer:0);
            Check(downloading.Loads==1, "eight seconds without byte progress still trigger bounded route recovery");
            foreach (bool accelerate in new[] { false, true })
            {
                var slow = new MainPage();
                slow.Alternates();
                if (accelerate) slow.Acceleration();
                slow.Playback.VideoBitrate = 12277180;
                slow.Playback.AudioBitrate = 120000;
                slow.SetReceiveRate(9.35 * 1024 * 1024);
                slow.Sample(0, buffer: 1.5);
                slow.SetReceiveRate(137.4 * 1024);
                slow.Sample(4, buffer: 1.6);
                slow.Sample(8, buffer: 1.8);
                slow.Sample(12, buffer: 1.9);
                slow.Sample(15.5, buffer: 2.0);
                Check(slow.Loads == 0, "startup burst and short low-throughput dips keep the current source");
                slow.Sample(16, buffer: 2.1);
                Check(slow.Loads == 1 && slow.PreserveAtLoad && slow.OffsetAtLoad == 7 && slow.Loaded.QualityId == 120
                    && slow.Diagnostics.Any(item => item.Contains("reason=slow-refill") && item.Contains("receiveBps=")),
                    "the recorded burst-to-trickle and low cache trigger one same-quality alternate, with or without acceleration");
            }
            var refilling = new MainPage();
            refilling.Alternates();
            refilling.Playback.VideoBitrate = 12277180;
            refilling.SetReceiveRate(150 * 1024);
            refilling.Sample(0, buffer: 1.5);
            refilling.Sample(11, buffer: 2.3);
            refilling.Sample(13, buffer: 2.5);
            refilling.Sample(24, buffer: 3);
            Check(refilling.Loads == 0, "advancing playable cache approaching resume threshold is preserved");
            var exhausted = new MainPage();
            exhausted.Playback.VideoBitrate = 12277180;
            exhausted.SetReceiveRate(140 * 1024);
            exhausted.Sample(0, buffer: 1.6);
            exhausted.Sample(30, buffer: 2.1);
            exhausted.Sample(50, buffer: 2.5);
            Check(exhausted.Loads == 0, "slow refills cannot invent an alternate");
            var network = new MainPage(); network.Alternates(); var original = network.Playback;
            Check(network.Fail(Windows.Media.Playback.MediaPlayerError.NetworkError).GetAwaiter().GetResult()
                && network.Loads==1 && ReferenceEquals(network.Loaded,original) && network.OffsetAtLoad==6
                && network.PreserveAtLoad && network.RecoveryAtLoad,
                "terminal network error preserves actual4K, position and replacement route rather than dropping quality");
            Check(network.Fail(Windows.Media.Playback.MediaPlayerError.NetworkError).GetAwaiter().GetResult()
                && !network.Fail(Windows.Media.Playback.MediaPlayerError.NetworkError).GetAwaiter().GetResult()
                && network.Loads==2, "repeated native network errors cannot replenish retry budget");
            var decode = new MainPage(); decode.Alternates();
            Check(!decode.Fail(Windows.Media.Playback.MediaPlayerError.DecodingError,true).GetAwaiter().GetResult()
                && decode.Loads==0, "known decoder errors remain eligible for existing codec/audio fallback");
            Check(decode.Fail(Windows.Media.Playback.MediaPlayerError.Unknown,true).GetAwaiter().GetResult(),
                "unknown error plus unresolved native download failure can try another real route");
            var paused = new MainPage(); paused.Alternates(); paused.Pause();
            var single = new MainPage(); single.Playback.IsHdr10=true; single.Playback.QualityId=125;
            Check(single.Fail(Windows.Media.Playback.MediaPlayerError.NetworkError).GetAwaiter().GetResult()
                && ReferenceEquals(single.Loaded,single.Playback) && single.Loaded.IsHdr10 && single.Loaded.QualityId==125
                && single.OffsetAtLoad==0 && !single.Fail(Windows.Media.Playback.MediaPlayerError.NetworkError).GetAwaiter().GetResult(),
                "one signed HDR URL receives one same-quality reconnection without an infinite retry loop");
            var generic = new MainPage(); generic.Alternates();
            Check(generic.Fail(Windows.Media.Playback.MediaPlayerError.Unknown,false,unchecked((int)0xC00D4278)).GetAwaiter().GetResult(),
                "native MF network timeout is recoverable even before DownloadFailed is delivered");
            var format = new MainPage(); format.Alternates();
            Check(!format.Fail(Windows.Media.Playback.MediaPlayerError.Unknown,false,unchecked((int)0xC00D36B4)).GetAwaiter().GetResult(),
                "unknown native format error is not falsely treated as a network timeout");
            Check(!paused.Fail(Windows.Media.Playback.MediaPlayerError.NetworkError).GetAwaiter().GetResult()
                && paused.Loads==0, "terminal error never resumes an explicitly paused video");
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

            var frozen = new MainPage(); frozen.Acceleration(); frozen.Alternates(); frozen.Offset(1);
            frozen.HoldPlay = new TaskCompletionSource<bool>();
            frozen.Starve(0, 69.7, duration: 414, position: 20);
            frozen.WaitingSample(5.9, 94.7, duration: 414, position: 20);
            Check(frozen.PlayRequests == 1 && frozen.Plays == 0 && frozen.Loads == 0,
                "ample buffer gets a short recovery window while one native resume is outstanding");
            frozen.WaitingSample(6, 123.2, duration: 414, position: 20);
            frozen.WaitingSample(18, 123.2, duration: 414, position: 20);
            Check(frozen.Loads == 1 && frozen.PreserveAtLoad && frozen.OffsetAtLoad == 1 && frozen.Loaded.QualityId == 120
                && frozen.RecoveringNatively && frozen.Diagnostics.Any(value => value.Contains("resumeInFlight=True useNative=True")),
                "the 4K frozen trace reopens the same quality and position natively once despite a pending resume");
            frozen.NewSource(); frozen.HoldPlay.SetResult(true);

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

            page = new MainPage(); page.Alternates(); page.Receiving(false); page.Sample(0, buffer: 0); page.Sample(8, buffer: 0);
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
            var refillActions = page.Diagnostics.Where(value => !value.StartsWith("transport-wait")).ToList();
            Check(refillActions.Count == 1 && refillActions[0].StartsWith("buffer-pause")
                && refillActions[0].Contains("playableSeconds=2.5"),
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
            page.SeekWhileWaiting(); page.WaitingSample(2, 0, position: 50);
            Check(page.Plays == 1 && page.Loads == 0, "seeking while app-paused hands back to the same native source to fill the new position");
            page.WaitingSample(3, 0, position: 50);
            Check(page.Plays == 1, "the seek restart is not repeated while the native source refills");

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
            page.SeekWhileWaiting(); page.HoldPlay.SetResult(true);
            Check(page.Plays == 0 && page.BufferWait,
                "seeking cancels the pending resume before it can use a stale pre-seek buffer");
            page.WaitingSample(3, 0, position: 50); page.WaitingSample(4, 3, position: 50);
            Check(page.Plays == 1 && !page.BufferWait,
                "only the new-position handoff resumes playback");

            page = new MainPage(); page.Sample(0, position: 99.2); page.Starve(1, .78, duration: 100, position: 99.2);
            Check(page.Plays == 1, "production transport resumes a complete tail shorter than3seconds");
            var index = new MainPage(); index.Acceleration(); index.InvalidIndex();
            var retained = index.Playback;
            Check(index.Fail(Windows.Media.Playback.MediaPlayerError.Unknown).GetAwaiter().GetResult()
                && index.Loads == 1 && ReferenceEquals(index.Loaded, retained) && index.Loaded.DashManifest == "compatible"
                && index.PreserveAtLoad && index.RecoveryAtLoad,
                "index format failure tries same-quality segment timeline before acceleration or codec fallback");
            index.Fail(Windows.Media.Playback.MediaPlayerError.Unknown).GetAwaiter().GetResult();
            Check(index.Loads == 1 && index.IndexErrors == 1 && !index.Wants,
                "same source index compatibility cannot repeat forever");
            index = new MainPage(); index.InvalidIndex(); DashIndexCompatibility.Fail = true;
            index.Sample(0, buffer: 0);
            Check(index.Loads == 0 && index.IndexErrors == 1 && !index.Wants,
                "failed compatibility validation stops runaway native downloads");
            DashIndexCompatibility.Fail = false;
            index = new MainPage(); index.InvalidIndex(); index.Block(); index.Sample(0, buffer: 0);
            Check(index.Loads == 0 && index.IndexErrors == 0,"modal/seek blocks automatic index recovery");
            index = new MainPage(); index.InvalidIndex(); index.Pause(); index.Sample(0, buffer: 0);
            Check(index.Loads == 0,"user pause cannot start index recovery");
            index = new MainPage(); index.InvalidIndex();
            DashIndexCompatibility.Hold = new TaskCompletionSource<string>();
            var pending = index.Fail(Windows.Media.Playback.MediaPlayerError.Unknown);
            index.CancelPart(); DashIndexCompatibility.Hold.SetResult("stale"); pending.GetAwaiter().GetResult();
            Check(index.Loads == 0 && index.Playback.DashManifest == "fixture" && index.IndexErrors == 0,
                "cancelled part ignores late compatibility completion");
            DashIndexCompatibility.Hold = null;
            return "PASS: " + _checks + " production page transport integration assertions (mock I/O, no device playback).";
        }
    }
}
