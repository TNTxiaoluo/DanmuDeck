using System;
using BiliXbox.Controls;

namespace BiliXbox.SeekAndLiveWatchdogTests
{
    public static class Tests
    {
        private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }

        public static void Run()
        {
            SeekSingleTapsStayPrecise();
            SeekHoldAccelerates();
            SeekDirectionChangeResets();
            SeekShortVideoCap();
            WatchdogParsesExpiry();
            WatchdogRefreshesBeforeExpiry();
            WatchdogDetectsFrozenAndStalled();
            WatchdogIgnoresPausedAndNonAdvancingClock();
            WatchdogBacksOff();
            WatchdogRecoversLostPlayer();
            Console.WriteLine("Seek acceleration and live watchdog tests passed.");
        }

        private static readonly DateTimeOffset T0 = new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

        private static void SeekSingleTapsStayPrecise()
        {
            var s = new SeekAccelerationState();
            for (int i = 0; i < 5; ++i) Assert(s.Step(1, 10, 7200, T0.AddSeconds(i)) == 10, "spaced taps must stay at 10 s");
        }

        private static void SeekHoldAccelerates()
        {
            var s = new SeekAccelerationState(); double total = 0; var t = T0;
            for (int i = 0; i < 40; ++i) { total += s.Step(1, 10, 7200, t); t = t.AddMilliseconds(220); }
            Assert(SeekAccelerationState.StepSeconds(0, 10, 7200) == 10, "base step");
            Assert(SeekAccelerationState.StepSeconds(3, 10, 7200) == 20, "second level doubles");
            Assert(SeekAccelerationState.StepSeconds(100, 10, 7200) == 180, "cap is duration / 40 for long videos");
            Assert(total > 3600, "about 9 s of holding must cross an hour of a 2 h video, got " + total);
        }

        private static void SeekDirectionChangeResets()
        {
            var s = new SeekAccelerationState(); var t = T0;
            for (int i = 0; i < 12; ++i) { s.Step(1, 10, 7200, t); t = t.AddMilliseconds(200); }
            Assert(s.Step(-1, 10, 7200, t) == -10, "reverse direction restarts at the base step");
        }

        private static void SeekShortVideoCap()
        {
            Assert(SeekAccelerationState.StepSeconds(100, 10, 120) == 30, "short videos cap at 30 s");
            Assert(SeekAccelerationState.StepSeconds(100, 5, 0) == 30, "unknown duration still caps");
        }

        private static void WatchdogParsesExpiry()
        {
            var e = LivePlaybackWatchdog.ParseExpiry("https://cn-x.bilivideo.com/live-bvc/1/index.m3u8?expires=1764072000&len=0&oi=1");
            Assert(e.HasValue && e.Value.ToUnixTimeSeconds() == 1764072000, "expires parsed");
            Assert(!LivePlaybackWatchdog.ParseExpiry("https://x/index.m3u8?noexpires=1764072000x").HasValue, "no false match");
            Assert(!LivePlaybackWatchdog.ParseExpiry(null).HasValue, "null");
        }

        private static void WatchdogRefreshesBeforeExpiry()
        {
            var w = new LivePlaybackWatchdog();
            w.Attach(T0, T0.AddMinutes(60));
            Assert(w.RefreshAt == T0.AddMinutes(57), "refresh 3 min before expiry");
            double pos = 0; string reason = null; var t = T0;
            for (int i = 0; i < 60 * 58 && reason == null; ++i) { t = t.AddSeconds(1); pos += 1; reason = w.Sample(t, true, false, true, true, pos); }
            Assert(reason == "expiry" && t >= T0.AddMinutes(57) && t < T0.AddMinutes(57).AddSeconds(2), "expiry refresh fires on time: " + reason + " " + t);
            Assert(w.Attempts == 0, "expiry refresh is not a failure attempt");
            var near = new LivePlaybackWatchdog(); near.Attach(T0, T0.AddSeconds(30));
            Assert(near.RefreshAt == T0.AddMinutes(2), "never refresh sooner than 2 min");
            var none = new LivePlaybackWatchdog(); none.Attach(T0, null);
            Assert(none.RefreshAt == T0.AddMinutes(45), "fallback lifetime");
        }

        private static void WatchdogDetectsFrozenAndStalled()
        {
            var w = new LivePlaybackWatchdog(); w.Attach(T0, T0.AddHours(1)); var t = T0; double pos = 100;
            for (int i = 0; i < 5; ++i) { t = t.AddSeconds(1); pos += 1; Assert(w.Sample(t, true, false, true, true, pos) == null, "healthy"); }
            string reason = null; int seconds = 0;
            while (reason == null && seconds < 30) { t = t.AddSeconds(1); ++seconds; reason = w.Sample(t, true, false, true, true, pos); }
            Assert(reason == "frozen" && seconds == 12, "frozen after 12 s, got " + reason + " " + seconds);

            var b = new LivePlaybackWatchdog(); b.Attach(T0, T0.AddHours(1)); t = T0; reason = null; seconds = 0;
            while (reason == null && seconds < 30) { t = t.AddSeconds(1); ++seconds; reason = b.Sample(t, true, false, true, false, 0); }
            Assert(reason == "stalled" && seconds == 15, "buffering stall after 15 s, got " + reason + " " + seconds);
        }

        private static void WatchdogIgnoresPausedAndNonAdvancingClock()
        {
            var w = new LivePlaybackWatchdog(); w.Attach(T0, T0.AddHours(1)); var t = T0;
            for (int i = 0; i < 120; ++i) { t = t.AddSeconds(1); Assert(w.Sample(t, false, false, true, false, 5) == null, "paused by viewer"); }
            // A player whose live Position never advances is judged by state only.
            for (int i = 0; i < 120; ++i) { t = t.AddSeconds(1); Assert(w.Sample(t, true, false, true, true, 0) == null, "static live clock while Playing"); }
            for (int i = 0; i < 120; ++i) { t = t.AddSeconds(1); Assert(w.Sample(t, true, true, false, false, 0) == null, "reload in flight"); }
        }

        private static void WatchdogBacksOff()
        {
            var w = new LivePlaybackWatchdog(); w.Attach(T0, T0.AddHours(3)); var t = T0; int fired = 0;
            for (int i = 0; i < 300; ++i) { t = t.AddSeconds(1); if (w.Sample(t, true, false, true, false, 0) != null) ++fired; }
            // 15 s stall, then 20, 40, 80, 160 s back-off windows cap the retries.
            Assert(fired >= 3 && fired <= 5, "retries are backed off, fired " + fired);
        }

        private static void WatchdogRecoversLostPlayer()
        {
            var w = new LivePlaybackWatchdog(); w.Attach(T0, T0.AddHours(1)); var t = T0;
            Assert(w.Sample(t.AddSeconds(1), true, false, false, false, 0) == null, "wait briefly after failure");
            Assert(w.Sample(t.AddSeconds(4), true, false, false, false, 0) == "player-lost", "reload a failed player");
        }
    }
}
