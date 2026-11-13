namespace BiliXbox.PlaybackHealthTests
{
    public static class Tests
    {
        private static int _assertions;
        private static readonly DateTimeOffset Epoch = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private static long Ticks(double seconds) => (long)Math.Round(seconds * TimeSpan.TicksPerSecond);
        private static DateTimeOffset At(double seconds) => Epoch.AddTicks(Ticks(seconds));
        private static void Check(bool condition, string message)
        {
            ++_assertions;
            if (!condition) throw new InvalidOperationException(message);
        }
        private static void Sample(Controls.PlaybackHealthState state, double wallSeconds, double mediaSeconds,
            bool playing = true, bool loading = false, bool buffering = false)
            => state.Update(At(wallSeconds), TimeSpan.FromTicks(Ticks(mediaSeconds)), playing, loading, buffering);

        public static void Run()
        {
            _assertions = 0;
            FrozenClockAndRecovery();
            PausedAndResume();
            NativeBufferingAndOpening();
            SeekAndSourceReset();
            NormalProgress();
            Console.WriteLine("PASS: " + _assertions + " deterministic playback-health assertions (no network or device media).");
        }
        private static void FrozenClockAndRecovery()
        {
            var state = new Controls.PlaybackHealthState();
            Check(!state.Waiting && !state.Stalled, "A new player starts in a waiting state.");
            Sample(state, 0, 10);
            Check(!state.Waiting, "First playing sample was incorrectly marked stalled.");
            Sample(state, 0.799, 10);
            Check(!state.Waiting && !state.Stalled, "Short clock repetition showed a premature spinner.");
            Sample(state, 0.8, 10);
            Check(state.Waiting && state.Stalled, "A frozen playing clock did not show buffering at 800ms.");
            Sample(state, 10, 10);
            Check(state.Waiting && state.Stalled, "A prolonged stalled clock cleared the buffering indicator.");
            Sample(state, 10.1, 10.1);
            Check(!state.Waiting && !state.Stalled, "Advancing media did not clear the fallback buffering indicator.");
            Sample(state, 10.899, 10.1);
            Check(!state.Stalled, "Recovery inherited the prior freeze deadline.");
            Sample(state, 10.9, 10.1);
            Check(state.Waiting && state.Stalled, "A second freeze did not start a fresh 800ms watchdog.");
        }
        private static void PausedAndResume()
        {
            var state = new Controls.PlaybackHealthState();
            Sample(state, 0, 0);
            Sample(state, 1, 0);
            Check(state.Waiting, "Setup did not reach a playing stall.");
            Sample(state, 2, 0, playing: false);
            Check(!state.Waiting && !state.Stalled, "Pausing retained a stale network spinner.");
            Sample(state, 500, 0, playing: false);
            Check(!state.Waiting && !state.Stalled, "A long pause was classified as slow networking.");
            Sample(state, 501, 0);
            Check(!state.Waiting, "Resuming a static paused frame immediately declared a stall.");
            Sample(state, 501.799, 0);
            Check(!state.Waiting, "Resume lost its full watchdog grace period.");
            Sample(state, 501.8, 0);
            Check(state.Waiting && state.Stalled, "An actually stalled resume did not show buffering.");
            Sample(state, 502, 0, playing: false);
            Check(!state.Waiting, "Pausing a second stalled session left buffering visible.");
        }
        private static void NativeBufferingAndOpening()
        {
            var state = new Controls.PlaybackHealthState();
            Sample(state, 0, 0, playing: false, loading: true);
            Check(state.Waiting && !state.Stalled, "Opening a source did not show loading immediately.");
            Sample(state, 200, 0, playing: false, loading: true);
            Check(state.Waiting && !state.Stalled, "Prolonged native opening was mistaken for the playing watchdog.");
            Sample(state, 201, 0);
            Check(!state.Waiting, "Opened media inherited a stale opening spinner.");
            Sample(state, 201.01, 0, playing: false, buffering: true);
            Check(state.Waiting && !state.Stalled, "Native Buffering did not show a spinner immediately.");
            Sample(state, 400, 0, playing: false, buffering: true);
            Check(state.Waiting && !state.Stalled, "Native Buffering disappeared before the buffer recovered.");
            Sample(state, 400.1, 0);
            Check(!state.Waiting && !state.Stalled, "Native buffering recovery did not clear the spinner.");
            Sample(state, 400.899, 0);
            Check(!state.Waiting, "Leaving native Buffering lost the recovery grace period.");
            Sample(state, 400.9, 0);
            Check(state.Stalled && state.Waiting, "Post-buffering clock freeze did not activate the fallback watchdog.");
            Sample(state, 401, 0, loading: true, buffering: true);
            Check(state.Waiting && !state.Stalled, "Native loading/buffering did not take precedence over a clock stall.");
        }
        private static void SeekAndSourceReset()
        {
            var state = new Controls.PlaybackHealthState();
            Sample(state, 0, 10); Sample(state, 1, 10);
            Sample(state, 2, 100);
            Check(!state.Waiting && !state.Stalled, "A forward seek retained a stale freeze deadline.");
            Sample(state, 2.799, 100);
            Check(!state.Waiting, "Seek target was declared stalled before its recovery deadline.");
            Sample(state, 2.8, 100);
            Check(state.Waiting, "No progress after a seek was never recognized as buffering.");
            Sample(state, 3, 5);
            Check(!state.Waiting, "Rewinding did not reset the stalled-clock watchdog.");
            Sample(state, 3.8, 5);
            Check(state.Waiting, "No progress after rewind did not become a stall.");
            state.Reset();
            Check(!state.Waiting && !state.Stalled, "Reset left a visible buffering state behind.");
            Sample(state, 1000, 0);
            Check(!state.Waiting, "A new source inherited the prior source's elapsed freeze time.");
            Sample(state, 1000.799, 0);
            Check(!state.Waiting, "A new source did not receive a full watchdog grace period.");
            Sample(state, 1000.8, 0);
            Check(state.Waiting && state.Stalled, "Watchdog did not resume after a new source reset.");
            state.Reset(); state.Reset();
            Check(!state.Waiting && !state.Stalled, "Repeated cleanup changed reset state.");
        }
        private static void NormalProgress()
        {
            var state = new Controls.PlaybackHealthState();
            for (int i = 0; i <= 100; ++i)
            {
                Sample(state, i * 0.1, i * 0.1);
                Check(!state.Waiting && !state.Stalled, "Ordinary advancing playback showed a network spinner at sample" + i);
            }
            // Repeated native samples between valid clock advances should not flicker the spinner.
            state.Reset();
            for (int i = 0; i <= 50; ++i)
            {
                Sample(state, i * 0.1, Math.Floor(i / 3d) * 0.3);
                Check(!state.Waiting, "Coarse but advancing native media time was classified as a stall.");
            }
            state.Reset();
            for (int i = 0; i <= 100; ++i)
            {
                Sample(state, i * 0.1, i * 0.005);
                Check(!state.Waiting && !state.Stalled, "Small valid media-clock advances were classified as a frozen video.");
            }
        }
    }
}
