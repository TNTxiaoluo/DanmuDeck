namespace BiliXbox.PlaybackPipelineRecoveryTests
{
    public static class Tests
    {
        private static int _passed;
        private static readonly System.DateTimeOffset Start = new System.DateTimeOffset(2026, 10, 24, 12, 0, 0, System.TimeSpan.Zero);
        private static void Check(bool value, string name)
        { if (!value) throw new System.Exception("FAIL: " + name); ++_passed; }

        private static bool Sample(Controls.PlaybackPipelineRecoveryState state, double wall, double media = 10,
            bool wants = true, bool waiting = true, bool blocked = false, double buffer = 4)
            => state.ShouldRestart(Start.AddSeconds(wall), System.TimeSpan.FromSeconds(media), wants, waiting, blocked, buffer);

        public static string Run()
        {
            _passed = 0;
            var state = new Controls.PlaybackPipelineRecoveryState();
            Check(!Sample(state, 0), "first ample-buffer wait starts observation");
            Check(!Sample(state, 11.999), "minimum twelve seconds is enforced");
            Check(Sample(state, 12), "frozen clock with playable buffer allows a single rebuild");
            Check(state.Attempts == 1, "decision consumes its attempt before native work");
            Check(!Sample(state, 1000), "long wait cannot grant a second rebuild");
            state.ResetWaiting(Start.AddSeconds(1001));
            Check(!Sample(state, 1001) && !Sample(state, 1013), "clearing waiting cannot replenish the budget");
            state.Reset();
            Check(state.Attempts == 0 && !Sample(state, 1014) && Sample(state, 1026), "new video part or user quality rearms once");

            foreach (double buffer in new[] { -1.0, 0, 1.999, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            {
                state.Reset();
                Check(!Sample(state, 0, buffer: buffer) && !Sample(state, 100, buffer: buffer),
                    "unknown insufficient or nonfinite buffer never rebuilds: " + buffer);
                Check(state.Attempts == 0, "invalid buffer does not consume budget: " + buffer);
            }
            state.Reset();
            Check(!Sample(state, 0, buffer: 2) && Sample(state, 12, buffer: 2), "two seconds of playable buffer is sufficient");
            state.Reset();
            Check(!Sample(state,0,blocked:true,buffer:2.5)
                && !Sample(state,12,blocked:true,buffer:2.5)
                && !Sample(state,30,blocked:true,buffer:2.5) && state.Attempts==0,
                "deliberate app-pause below three-second resume target cannot consume native-rebuild budget");
            Check(!Sample(state,31,buffer:3) && !Sample(state,42.999,buffer:3)
                && Sample(state,43,buffer:3) && state.Attempts==1,
                "hung native resume handoff after intentional wait receives a fresh full decoder-recovery interval");
            state.ResetWaiting(Start.AddSeconds(44));
            Check(!Sample(state,44,blocked:true,buffer:2.5)
                && !Sample(state,70,blocked:true,buffer:2.5)
                && !Sample(state,71,buffer:3) && !Sample(state,100,buffer:3)
                && state.Attempts==1,
                "repeated app-buffer pauses and handoffs cannot replenish consumed native-rebuild budget");
            state.Reset();
            Sample(state, 0); Sample(state, 11, buffer: 1.9);
            Check(!Sample(state, 12) && !Sample(state, 23.999) && Sample(state, 24), "buffer starvation interrupts continuity");

            foreach (string interruption in new[] { "pause", "not waiting", "seek or modal" })
            {
                state.Reset(); Sample(state, 0);
                Check(!Sample(state, 11, wants: interruption != "pause", waiting: interruption != "not waiting",
                    blocked: interruption == "seek or modal"), interruption + " interrupts waiting");
                Check(!Sample(state, 12) && !Sample(state, 23.999) && Sample(state, 24),
                    interruption + " needs a fresh twelve-second interval");
                state.ResetWaiting(Start.AddSeconds(25));
                Check(!Sample(state, 25) && !Sample(state, 100), interruption + " cannot replenish used budget");
            }

            state.Reset(); Sample(state, 0);
            bool fired = false;
            for (int i = 1; i < 12; ++i) fired |= Sample(state, i, 10 + i * 0.1);
            Check(!fired, "tiny clock advances do not trigger prematurely");
            Check(Sample(state, 12, 11.2), "0.1-second ticks cannot indefinitely defer sustained stall recovery");

            state.Reset(); Sample(state, 0);
            fired = false;
            for (int i = 1; i <= 60; ++i) fired |= Sample(state, i, 10 + i);
            Check(!fired && state.Attempts == 0, "healthy real-time clock never rebuilds despite stale waiting flag");

            state.Reset(); Sample(state, 0);
            fired = false;
            for (int i = 1; i <= 60; ++i) fired |= Sample(state, i, 10 + i * 0.25);
            Check(!fired && state.Attempts == 0, "sustained clock above twenty percent clears stall interval");
            state.Reset(); Sample(state, 0);
            fired = false;
            for (int i = 1; i <= 30; ++i) fired |= Sample(state, i, 10 + i * 0.2);
            Check(!fired && state.Attempts == 0, "twenty-percent threshold includes its healthy boundary");

            state.Reset(); Sample(state, 0);
            for (int i = 1; i <= 10; ++i) Sample(state, i, 10 + i * 0.1);
            Check(!Sample(state, 11, 12) && !Sample(state, 12, 13), "two seconds of resumed healthy clock cancels old stall");
            Check(!Sample(state, 23.999, 13) && Sample(state, 24, 13), "fresh stall after healthy clock gets complete new interval");

            state.Reset(); Sample(state, 0); Sample(state, 11);
            Check(!Sample(state, 12, 9), "unobserved backward seek clears old interval");
            Check(!Sample(state, 23.999, 9) && Sample(state, 24, 9), "backward seek receives fresh wait without new budget");
            state.Reset(); Sample(state, 0); Sample(state, 11);
            Check(!Sample(state, 12, 30), "unobserved forward seek clears old interval");
            Check(!Sample(state, 23.999, 30) && Sample(state, 24, 30), "forward seek receives fresh wait");
            state.Reset(); Sample(state, 0); Sample(state, 11);
            Check(!Sample(state, 5), "backward wall clock cannot use future elapsed time");
            Check(!Sample(state, 16.999) && Sample(state, 17), "wall clock rollback starts fresh observation");

            state.Reset(); Sample(state, 0); Sample(state, 11);
            state.ResetWaiting(Start.AddSeconds(12));
            Check(!Sample(state, 12) && !Sample(state, 23.999) && Sample(state, 24), "explicit waiting reset clears prior age");
            state.Reset();
            Check(!Sample(state, 0, -1) && !Sample(state, 100, -1), "invalid negative media position never restarts");
            Check(!Sample(state, 101, 10) && Sample(state, 113, 10), "valid media position begins new interval after invalid state");
            state.Reset(); Sample(state, 0);
            Check(!Sample(state, 0) && !Sample(state, 0), "duplicate timestamps cannot accumulate duration");
            Check(Sample(state, 12), "duplicate samples do not lose subsequent real duration");

            return "Playback pipeline recovery checks passed: " + _passed;
        }
    }
}
