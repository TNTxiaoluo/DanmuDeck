namespace BiliXbox.PlaybackRecoveryTests
{
    public static class Tests
    {
        private static int _assertions;
        private static readonly DateTimeOffset Epoch = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private static DateTimeOffset At(double seconds) => Epoch.AddTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond));
        private static TimeSpan Position(double seconds) => TimeSpan.FromTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond));
        private static void Check(bool condition, string message)
        {
            ++_assertions;
            if (!condition) throw new InvalidOperationException(message);
        }
        private static bool Sample(Controls.PlaybackRecoveryState state, double wall, double media = 0,
            bool playing = true, bool waiting = true, bool blocked = false,
            double buffered = double.NaN, bool alternates = true)
            => state.ShouldSwitch(At(wall), Position(media), playing, waiting, blocked, buffered, alternates);

        public static void Run()
        {
            _assertions = 0;
            ShortWaitsAndProgress();
            EligibilityAndUnknownBuffer();
            PauseAndResume();
            SeekAndModal();
            CooldownBudgetAndSourceReset();
            LargeTimeJump();
            NewPlaybackGeneration();
            ClockRewind();
            Console.WriteLine("PASS: " + _assertions + " deterministic playback-recovery assertions (no network or device media).");
        }

        private static void ShortWaitsAndProgress()
        {
            var state = new Controls.PlaybackRecoveryState();
            Check(!Sample(state, 0), "First waiting sample switched immediately.");
            Check(!Sample(state, 7.999), "A brief wait consumed a source.");
            Check(!Sample(state, 8, waiting: false), "Recovered playback switched a source.");
            Check(!Sample(state, 9), "A new wait inherited the previous deadline.");
            Check(!Sample(state, 16.999), "A second brief wait switched prematurely.");
            Check(!Sample(state, 17, media: 0.01), "A small media advance failed to reset the wait.");
            Check(!Sample(state, 24.999, media: 0.01), "Progress did not grant a full waiting window.");
            Check(Sample(state, 25, media: 0.01), "An eight-second frozen media clock did not switch.");
            Check(state.Attempts == 1, "A switch did not consume exactly one attempt.");
        }

        private static void EligibilityAndUnknownBuffer()
        {
            var state = new Controls.PlaybackRecoveryState();
            Check(!Sample(state, 0, waiting: false), "No waiting signal triggered recovery.");
            Check(!Sample(state, 100, waiting: false), "A frozen but nonwaiting clock triggered recovery.");
            Check(!Sample(state, 101, buffered: 3), "A substantial buffer triggered recovery.");
            Check(!Sample(state, 200, buffered: 3), "A long buffered wait triggered recovery.");
            Check(!Sample(state, 201, buffered: 1), "Exactly one buffered second was considered starving.");
            Check(!Sample(state, 202, buffered: 0.999), "A low buffer inherited an ineligible wait.");
            Check(!Sample(state, 209.999, buffered: 0), "A low buffer switched before eight seconds.");
            Check(Sample(state, 210, buffered: 0), "A genuinely empty buffer never triggered recovery.");

            state.Reset();
            Check(!Sample(state, 0, alternates: false), "A stream without alternates triggered recovery.");
            Check(!Sample(state, 100, alternates: false), "A long wait consumed an unavailable alternate.");
            Check(state.Attempts == 0, "An unavailable alternate consumed the budget.");
            Check(!Sample(state, 101, buffered: -1), "A newly available alternate inherited a stale deadline.");
            Check(Sample(state, 109, buffered: -1), "An unknown negative buffer duration prevented recovery.");

            state.Reset();
            Check(!Sample(state, 0), "Unknown buffer switched immediately.");
            Check(Sample(state, 8), "NaN buffer duration prevented a sustained wait from recovering.");
        }

        private static void PauseAndResume()
        {
            var state = new Controls.PlaybackRecoveryState();
            Check(!Sample(state, 0), "Pause setup switched immediately.");
            Check(!Sample(state, 7, playing: false), "Pause triggered recovery.");
            Check(!Sample(state, 100, playing: false), "A long pause triggered recovery.");
            Check(!Sample(state, 101), "Resume inherited a pre-pause deadline.");
            Check(!Sample(state, 108.999), "Resume did not receive eight seconds of grace.");
            Check(Sample(state, 109), "A sustained waiting resume never recovered.");
            Check(!Sample(state, 1000, playing: false), "Ended/stopped playback consumed another alternate.");
            Check(state.Attempts == 1, "Pause or media end replenished or consumed the budget.");
        }

        private static void SeekAndModal()
        {
            var state = new Controls.PlaybackRecoveryState();
            Check(!Sample(state, 0, media: 10), "Seek setup switched immediately.");
            Check(!Sample(state, 7, media: 100, blocked: true), "An active seek triggered recovery.");
            Check(!Sample(state, 100, media: 100, blocked: true), "A long seek/modal triggered recovery.");
            Check(!Sample(state, 101, media: 100), "Seek completion inherited the old wait.");
            Check(!Sample(state, 108.999, media: 100), "Seek completion lacked a fresh waiting window.");
            Check(Sample(state, 109, media: 100), "Waiting after a completed seek never recovered.");

            Check(!Sample(state, 130, media: 5), "A backward seek with a missed event immediately switched.");
            Check(!Sample(state, 137.999, media: 5), "A backward seek did not reset the waiting window.");
            Check(Sample(state, 138, media: 5), "A stuck backward seek never recovered.");
            Check(state.Attempts == 2, "Seek movement incorrectly replenished the total attempt budget.");
        }

        private static void CooldownBudgetAndSourceReset()
        {
            var state = new Controls.PlaybackRecoveryState();
            Check(!Sample(state, 0), "Budget setup switched immediately.");
            Check(Sample(state, 8), "First eligible recovery was skipped.");
            state.ResetWaiting(At(8));
            Check(state.Attempts == 1, "ResetWaiting replenished the budget.");
            Check(!Sample(state, 8), "A replacement source switched immediately.");
            Check(!Sample(state, 16), "ResetWaiting cleared the source-switch cooldown.");
            Check(!Sample(state, 27.999), "A recovery occurred before twenty seconds of cooldown.");
            Check(Sample(state, 28), "A second sustained wait never recovered after cooldown.");
            Check(state.Attempts == Controls.PlaybackRecoveryState.MaximumAttempts, "The maximum attempt count is incorrect.");
            Check(!Sample(state, 50, media: 1), "Advancing playback switched immediately.");
            state.ResetWaiting(At(51));
            Check(!Sample(state, 51, media: 1), "Exhausted replacement source switched immediately.");
            Check(!Sample(state, 1000, media: 1), "Source reset or playback progress replenished exhausted attempts.");
            Check(state.Attempts == 2, "Budget exceeded two automatic source switches.");
        }

        private static void LargeTimeJump()
        {
            var state = new Controls.PlaybackRecoveryState();
            Check(!Sample(state, 0), "Time jump setup switched immediately.");
            Check(Sample(state, 10000), "A long continuous wait never recovered.");
            Check(!Sample(state, 10000), "A time jump emitted a backlog of source switches.");
            Check(!Sample(state, 10008), "A time jump bypassed the cooldown.");
            Check(state.Attempts == 1, "A single delayed sample consumed several attempts.");
            Check(Sample(state, 10020), "The next recovery did not wait twenty seconds.");
        }

        private static void NewPlaybackGeneration()
        {
            var state = new Controls.PlaybackRecoveryState();
            Sample(state, 0);
            Check(Sample(state, 8), "Generation setup failed its first switch.");
            Check(Sample(state, 28), "Generation setup failed its second switch.");
            state.Reset();
            Check(state.Attempts == 0, "A new video/part/quality retained the exhausted budget.");
            Check(!Sample(state, 29, media: 500), "A new generation inherited the old waiting deadline.");
            Check(!Sample(state, 36.999, media: 500), "A new generation did not get eight seconds to load.");
            Check(Sample(state, 37, media: 500), "A new generation inherited the old source cooldown.");
            Check(state.Attempts == 1, "A new generation consumed an incorrect number of attempts.");
            state.Reset();
            state.Reset();
            Check(state.Attempts == 0, "Repeated reset changed the new generation state.");
        }

        private static void ClockRewind()
        {
            var state = new Controls.PlaybackRecoveryState();
            Check(!Sample(state, 100), "Clock rewind setup switched immediately.");
            Check(!Sample(state, 107), "Clock rewind setup did not honor eight seconds.");
            Check(!Sample(state, 50), "A backwards wall clock triggered recovery.");
            Check(!Sample(state, 57.999), "A backwards wall clock retained an unrelated deadline.");
            Check(Sample(state, 58), "Recovery never resumed after the wall clock stabilized.");
            Check(!Sample(state, 0), "Clock rewind bypassed cooldown.");
            Check(!Sample(state, 77.999), "Wall clock rewind cleared the most recent cooldown.");
            Check(Sample(state, 78), "Recovery did not honor the original switch timestamp after a wall clock rewind.");
        }
    }
}
