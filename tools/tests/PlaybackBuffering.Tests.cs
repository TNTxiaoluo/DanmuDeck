namespace BiliXbox.PlaybackBufferingTests
{
    using BiliXbox.Services;
    public static class Tests
    {
        private static int _checks;
        private static void Check(bool value, string name)
        { if (!value) throw new System.Exception("FAIL: " + name); ++_checks; }
        private static PlaybackBufferRange R(double start, double end) => new PlaybackBufferRange(start, end);
        public static string Run()
        {
            Check(PlaybackBufferRanges.ContiguousSeconds(10, new[] { R(0, 5), R(10, 11), R(11, 13), R(12, 14) }) == 4,
                "overlapping and contiguous native ranges are merged from current position");
            Check(PlaybackBufferRanges.ContiguousSeconds(10, new[] { R(0, 5), R(10, 11), R(12, 50) }) == 1,
                "a disconnected future range does not fill a media gap");
            Check(PlaybackBufferRanges.ContiguousSeconds(10, new[] { R(20, 100), R(10, 13) }) == 3,
                "native range order does not matter");
            Check(PlaybackBufferRanges.ContiguousSeconds(10, new[] { R(10.1, 20), R(0, 9) }) == 0,
                "buffer must cover current position rather than begin a frame later");
            Check(PlaybackBufferRanges.ContiguousSeconds(10, new[] { R(double.NaN, 10), R(12, 11), R(10, double.PositiveInfinity) }) == 0,
                "nonfinite and reversed ranges do not grant playback time");
            Check(PlaybackBufferRanges.ContiguousSeconds(double.NaN, new[] { R(0, 10) }) == -1
                && PlaybackBufferRanges.ContiguousSeconds(10, null) == -1, "invalid range query remains unknown");

            var policy = new PlaybackBufferingPolicy(); var source = new object();
            Check(policy.Observe(source, 1, true, true, false, false, true, 0, 100, 0) == PlaybackBufferAction.None && !policy.Waiting,
                "initial native Play is not paused before it can trigger downloads");
            Check(policy.Observe(source, 1, true, true, false, false, true, 0, 100, 2.999) == PlaybackBufferAction.None && !policy.Waiting,
                "initial buffering below three seconds keeps native downloading alive");
            Check(policy.Observe(source, 1, true, true, false, false, true, 0, 100, -1) == PlaybackBufferAction.None,
                "unknown initial ranges do not grant a speculative Play request");
            Check(policy.Observe(source, 1, true, true, false, false, true, 0, 100, 3) == PlaybackBufferAction.Resume && policy.Waiting,
                "first startup with exactly three real playable seconds can resume once");
            Check(policy.Observe(source, 1, true, true, false, false, true, 0, 100, 6) == PlaybackBufferAction.None,
                "more data while first startup resume is pending cannot issue another Play");
            Check(policy.Observe(source, 1, true, true, false, true, false, 10, 100, .1) == PlaybackBufferAction.None,
                "low cached amount alone cannot pause healthy native playback");
            Check(policy.Observe(source, 1, true, true, false, false, true, 10, 100, 2.999) == PlaybackBufferAction.Pause && policy.Waiting,
                "actual rebuffering below three seconds pauses while preserving play intent");
            Check(policy.PausedForBuffer, "an app-owned buffering pause is distinct from the later native resume handoff");
            for (int i = 0; i < 20; ++i)
                Check(policy.Observe(source, 1, true, true, false, false, true, 10, 100, 2.999) == PlaybackBufferAction.None,
                    "waiting episodes do not repeat Pause or Play");
            Check(policy.Observe(source, 1, true, true, false, false, true, 10, 100, 3) == PlaybackBufferAction.Resume,
                "exactly three seconds requests native resume");
            Check(!policy.PausedForBuffer && policy.Waiting,
                "a requested native resume no longer claims intentional app-pause ownership");
            for (int i = 0; i < 20; ++i)
                Check(policy.Observe(source, 1, true, true, false, false, true, 10, 100, 10) == PlaybackBufferAction.None,
                    "native decoder sync can remain pending without a Play loop");
            Check(policy.Observe(source, 1, true, true, false, true, false, 11, 100, 5) == PlaybackBufferAction.None && !policy.Waiting,
                "only actual native Playing ends the handoff wait");
            Check(policy.Observe(source, 1, true, true, false, false, true, 13, 100, .2) == PlaybackBufferAction.Pause,
                "a subsequent starvation episode can wait again");
            Check(policy.Observe(source, 1, true, false, false, false, true, 13, 100, 10) == PlaybackBufferAction.None && !policy.Waiting,
                "explicit user pause cancels automatic resume even with a filled cache");
            Check(!policy.PausedForBuffer, "an explicit user pause releases automatic pause ownership");
            Check(policy.Observe(source, 1, true, false, false, false, true, 13, 100, 10) == PlaybackBufferAction.None,
                "paused user remains paused on subsequent samples");
            policy.Rebuffer(source, 1);
            Check(policy.Observe(source, 1, true, true, true, false, true, 13, 100, 10) == PlaybackBufferAction.None && policy.Waiting,
                "seek/dialog/input blocks resume without discarding a pending buffer wait");
            Check(policy.Observe(source, 1, true, true, false, false, true, 13, 100, 3) == PlaybackBufferAction.Resume,
                "unblocking the same source can resume once ready");
            Check(policy.Observe(source, 2, true, true, false, false, true, 0, 100, 0) == PlaybackBufferAction.None && !policy.Waiting,
                "a new part generation discards old source wait and resume state without stopping initial download");
            policy.Rebuffer(source, 2);
            Check(policy.Observe(new object(), 2, true, true, false, false, true, 0, 100, 0) == PlaybackBufferAction.None,
                "a replacement native source does not inherit a previous resume");
            policy.Rebuffer(source, 3);
            Check(policy.Observe(source, 3, false, true, false, false, true, 10, 100, 10) == PlaybackBufferAction.None && !policy.Waiting,
                "suspend/unload/inactive playback cancels pending automatic action");
            policy.Rebuffer(source, 4);
            Check(policy.Observe(source, 4, true, true, false, false, true, 99.2, 100, .79) == PlaybackBufferAction.Resume,
                "a fully buffered tail shorter than three seconds finishes normally");
            policy.Rebuffer(source, 4);
            Check(policy.Observe(source, 4, true, true, false, false, true, 99.2, 100, .2) == PlaybackBufferAction.None,
                "an incompletely buffered short tail still waits");
            policy.Rebuffer(source, 4);
            Check(policy.Observe(source, 4, true, true, false, false, true, 100, 100, 0) == PlaybackBufferAction.Resume,
                "EOF never waits forever for three seconds past duration");
            policy.Rebuffer(source, 5);
            Check(policy.Observe(source, 5, true, true, false, false, true, 10, 0, 2.99) == PlaybackBufferAction.None,
                "unknown duration does not relax three second threshold");
            Check(policy.Observe(source, 5, true, true, false, false, true, 10, 0, -1) == PlaybackBufferAction.Resume,
                "lost native range query hands app-paused source back to native without claiming buffer readiness");
            Check(policy.Observe(source, 5, true, true, false, false, true, 10, 0, -1) == PlaybackBufferAction.None,
                "unknown-range handoff is also bounded to one Play request");
            policy.Reset();
            Check(!policy.Waiting, "lifecycle reset clears pending wait");
            Check(policy.Observe(source, 7, true, true, false, false, true, 0, 100, 6) == PlaybackBufferAction.Resume,
                "first startup with six playable seconds need not wait for a prior Playing sample");
            Check(policy.Observe(source, 7, true, true, false, false, true, 0, 100, 6) == PlaybackBufferAction.None,
                "six second first-start handoff remains a single native request");
            return "PASS: " + _checks + " three-second playback buffering and contiguous native-range assertions (no network or hardware).";
        }
    }
}
