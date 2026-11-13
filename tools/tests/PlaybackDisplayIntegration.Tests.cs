// Compile the real MainPage display orchestration; only external native media,
// HDMI controller and unrelated page/navigation methods are substituted.
namespace BiliXbox.Services
{
    internal sealed class PlaybackDisplayController
    {
        internal static int Constructed, PrepareCount, ResetCount;
        internal static Func<PlaybackInfo, CancellationToken, Task<bool>> Prepare;
        internal static Func<Task> Reset;
        internal static string Failure;
        internal string FailureReason => Failure;
        internal PlaybackDisplayController() { ++Constructed; }
        internal Task<bool> PrepareAsync(PlaybackInfo value, CancellationToken token)
        { ++PrepareCount; return Prepare == null ? Task.FromResult(true) : Prepare(value, token); }
        internal Task ResetAsync() { ++ResetCount; return Reset == null ? Task.CompletedTask : Reset(); }
        internal static void Clear() { Constructed = PrepareCount = ResetCount = 0; Prepare = null; Reset = null; Failure = null; }
    }
}
namespace BiliXbox
{
    public sealed partial class MainPage
    {
        private bool _disposed, _libraryVisible, _creatorVisible, _watching = true, _wantsPlayback = true, _resumeAfterOpen = true;
        private int _partGeneration = 1;
        private long _currentCid = 7;
        private CancellationTokenSource _watchCts = new CancellationTokenSource(), _partCts = new CancellationTokenSource();
        private VideoDetail _video = new VideoDetail { Bvid = "fixture", Cid = 7 };
        private PlaybackInfo _playback = new PlaybackInfo { QualityId = 125, Quality = "HDR10", IsHdr10 = true };
        private readonly FixtureMedia _media = new FixtureMedia { Source = new object() };
        internal int LoadCount, SuspendCount, UiCount;
        internal PlaybackInfo Loaded;
        internal string Status;
        private void UpdateQualities() { ++UiCount; }
        private void UpdatePlaybackUi() { ++UiCount; }
        private void SetStatus(string value) { Status = value; }
        private static string Friendly(Exception ex) => ex.Message;
        private static void Cancel(ref CancellationTokenSource source)
        { var old = source; source = null; if (old != null) { old.Cancel(); old.Dispose(); } }
        private Task LoadPartAsync(long cid, int? quality = null, bool preservePosition = false, PlaybackInfo preparedPlayback = null)
        { ++LoadCount; Loaded = preparedPlayback; if (!preservePosition) throw new Exception("resume target was not preserved"); return Task.CompletedTask; }
        public void SuspendPlayback() { ++SuspendCount; _wantsPlayback = _resumeAfterOpen = false; _media.Pause(); }
        internal Task<PlaybackInfo> PrepareForTest(PlaybackInfo value, CancellationToken ct) => PrepareSourceDisplayAsync(value, ct);
        internal Task PlayForTest() => PlayWithDisplayAsync();
        internal Task ResetForTest() => ResetPlaybackDisplayAsync();
        internal int Plays => _media.PlayCount;
        internal int Pauses => _media.PauseCount;
        internal bool WantsPlay => _wantsPlayback;
        internal bool Suspended => _displaySuspended;
        internal int Generation => _partGeneration;
        internal CancellationToken PartToken => _partCts.Token;
        internal void PauseForTest() { _wantsPlayback = false; CancelDisplayPlayRequest(); _media.Pause(); }
        internal void WantsPlayForTest() { _wantsPlayback = _resumeAfterOpen = true; }
        internal void ChangeGenerationForTest() { ++_partGeneration; }
        internal void ChangeSourceForTest() { _media.Source = new object(); }
        internal void ChangeWatchForTest() { _watchCts = new CancellationTokenSource(); }
        internal void CreatorForTest() { _creatorVisible = true; }
        internal void DisposeForTest() { _disposed = true; }
        internal void FallbackForTest(PlaybackInfo fallback) { _playback.CodecFallback = fallback; }
        internal static void MergeForTest(PlaybackInfo current, PlaybackInfo latest) => MergePlaybackQualities(current, latest);
    }
    internal sealed class FixtureMedia
    {
        internal object Source;
        internal int PlayCount, PauseCount;
        internal void Play() { ++PlayCount; }
        internal void Pause() { ++PauseCount; }
    }
}
namespace BiliXbox.DisplayIntegrationTests
{
    public static class Tests
    {
        private static int _passed;
        private static void Check(bool value, string name)
        { if (!value) throw new Exception("FAIL: " + name); ++_passed; }
        public static async Task<string> Run()
        {
            _passed = 0; PlaybackDisplayController.Clear();
            var page = new MainPage();
            Check(PlaybackDisplayController.Constructed == 0, "page does not initialize HDMI capability during startup");
            var original = new PlaybackInfo { QualityId = 125, Quality = "HDR10", IsHdr10 = true };
            Check(await page.PrepareForTest(original, CancellationToken.None) == original,
                "successful output preparation retains the actual selected source");
            Check(PlaybackDisplayController.PrepareCount == 1, "source preparation queries output before media attachment");
            var sdr = new PlaybackInfo { QualityId = 80, Quality = "1080P" }; original.CodecFallback = sdr;
            PlaybackDisplayController.Prepare = (value, token) => Task.FromResult(value == sdr);
            Check(await page.PrepareForTest(original, CancellationToken.None) == sdr && sdr.QualityNotice.Contains("1080P"),
                "failed HDR output can only fall back to the provided real SDR source");
            PlaybackDisplayController.Prepare = (value, token) =>
            {
                PlaybackDisplayController.Failure = value == original ? "请检查允许 4K。" : "";
                return Task.FromResult(value == sdr);
            };
            await page.PrepareForTest(original, CancellationToken.None);
            Check(sdr.QualityNotice.Contains("允许 4K") && sdr.QualityNotice.Contains("1080P"),
                "successful fallback retains the primary output failure instead of erasing it");
            original.CodecFallback = new PlaybackInfo { IsHdr10 = true };
            int calls = PlaybackDisplayController.PrepareCount;
            try { await page.PrepareForTest(original, CancellationToken.None); throw new Exception("FAIL: unprepared HDR accepted"); }
            catch (InvalidOperationException) { Check(PlaybackDisplayController.PrepareCount == calls + 1, "HDR fallback does not masquerade as SDR"); }

            PlaybackDisplayController.Clear(); page = new MainPage();
            var pending = new TaskCompletionSource<bool>();
            PlaybackDisplayController.Prepare = (value, token) => pending.Task;
            var play = page.PlayForTest();
            Check(page.Plays == 0 && !play.IsCompleted, "play waits for asynchronous native mode acceptance");
            pending.SetResult(true); await play;
            Check(page.Plays == 1, "accepted current mode permits one playback start");
            foreach (var change in new Action<MainPage>[] { item => item.PauseForTest(), item => item.ChangeGenerationForTest(),
                item => item.ChangeSourceForTest(), item => item.ChangeWatchForTest(), item => item.CreatorForTest(), item => item.DisposeForTest() })
            {
                PlaybackDisplayController.Clear(); page = new MainPage(); pending = new TaskCompletionSource<bool>();
                PlaybackDisplayController.Prepare = (value, token) => pending.Task;
                play = page.PlayForTest(); change(page); pending.SetResult(true); await play;
                Check(page.Plays == 0, "late display acceptance cannot play a paused, replaced, overlaid or disposed source");
            }
            PlaybackDisplayController.Clear(); page = new MainPage(); pending = new TaskCompletionSource<bool>();
            PlaybackDisplayController.Prepare = (value, token) => pending.Task;
            play = page.PlayForTest(); await page.ResetForTest(); pending.SetResult(true); await play;
            Check(page.Plays == 0 && PlaybackDisplayController.ResetCount == 1,
                "stop/reset cancels a pending mode preparation even if the external completion ignores cancellation");
            PlaybackDisplayController.Clear(); page = new MainPage(); pending = new TaskCompletionSource<bool>();
            PlaybackDisplayController.Prepare = (value, token) => pending.Task;
            play = page.PlayForTest(); await page.SuspendPlaybackAsync(); pending.SetResult(true); await play;
            Check(page.Plays == 0 && page.Suspended && !page.WantsPlay,
                "suspension during pending mode preparation never restarts the old source");
            PlaybackDisplayController.Clear(); page = new MainPage();
            page.FallbackForTest(sdr); PlaybackDisplayController.Prepare = (value, token) => Task.FromResult(false);
            await page.PlayForTest();
            Check(page.Plays == 0 && page.LoadCount == 1 && page.Loaded == sdr,
                "failed mode restore delegates an authorized SDR fallback with preserved playback position");
            PlaybackDisplayController.Clear(); page = new MainPage();
            PlaybackDisplayController.Prepare = (value, token) => Task.FromException<bool>(new InvalidOperationException("fixture failure"));
            await page.PlayForTest();
            Check(page.Plays == 0 && !page.WantsPlay && page.Pauses > 0 && PlaybackDisplayController.ResetCount == 1,
                "display exception pauses playback and attempts default mode cleanup");
            Check(page.Status.Contains("fixture failure"), "display failure remains actionable without an async-void crash");
            PlaybackDisplayController.Clear(); page = new MainPage();
            await page.PrepareForTest(original, CancellationToken.None);
            var resetPending = new TaskCompletionSource<bool>(); PlaybackDisplayController.Reset = () => resetPending.Task;
            var partToken = page.PartToken; int generation = page.Generation;
            var suspend = page.SuspendPlaybackAsync();
            Check(page.Suspended && partToken.IsCancellationRequested && page.Generation == generation + 1,
                "suspension invalidates unfinished source attachment before mode reset");
            Check(page.SuspendCount == 1 && !suspend.IsCompleted && !page.WantsPlay,
                "suspend completion waits for native display reset while playback remains paused");
            resetPending.SetResult(true); await suspend;
            page.ResumeDisplayPlayback();
            Check(!page.Suspended && page.Plays == 0 && !page.WantsPlay, "resume enables later explicit play without autoplay");
            page.WantsPlayForTest(); await page.PlayForTest();
            Check(PlaybackDisplayController.PrepareCount == 2 && page.Plays == 1 && !page.PartToken.IsCancellationRequested,
                "first explicit play after suspension prepares the retained source display again");
            Check(partToken.IsCancellationRequested && page.PartToken != partToken,
                "resuming restores a fresh part lifetime for network recovery without reusing the cancelled suspension token");
            var current = new PlaybackInfo { QualityId = 120, Quality = "4K" };
            var blocked = new PlaybackQuality { Id = 120, Label = "4K", IsVisible = false, IsAvailable = false, Reason = "device block" };
            MainPage.MergeForTest(current, new PlaybackInfo { CapabilityDiagnostic = "HDMI query unavailable", AvailableQualities = new[] { blocked } });
            Check(current.CapabilityDiagnostic == "HDMI query unavailable", "quality refresh replaces stale display diagnostic with latest probe");
            Check(!current.AvailableQualities[0].IsVisible && !current.AvailableQualities[0].IsAvailable
                && current.AvailableQualities[0].Reason == "device block", "fresh device block is not erased because the old source was playing");
            var pendingQuality = new PlaybackQuality { Id = 120, IsVisible = true, IsAvailable = false,
                IsCapabilityPending = true, Reason = "capability pending" };
            MainPage.MergeForTest(current, new PlaybackInfo { AvailableQualities = new[] { pendingQuality } });
            Check(current.AvailableQualities[0].IsVisible && !current.AvailableQualities[0].IsAvailable
                && current.AvailableQualities[0].IsCapabilityPending && current.AvailableQualities[0].Reason == "capability pending",
                "refresh cannot re-enable an unconfirmed tier just because that source was previously playing");
            MainPage.MergeForTest(current, new PlaybackInfo { AvailableQualities = new PlaybackQuality[0] });
            Check(current.AvailableQualities.Count == 1 && current.AvailableQualities[0].IsVisible
                && current.AvailableQualities[0].HasPlaybackSource, "narrow response without a device block preserves the actual playing source");
            return "PASS: " + _passed + " production display playback orchestration assertions. Native HDMI output is not established.";
        }
    }
}
