using System;

namespace BiliXbox.Controls
{
    // Repeated seeks in one direction grow their step so a long video can be
    // crossed in a few seconds. A pause or a direction change starts again at
    // the base step, which keeps single taps precise.
    internal sealed class SeekAccelerationState
    {
        public static readonly TimeSpan StreakWindow = TimeSpan.FromMilliseconds(650);
        public static readonly TimeSpan CommitDelay = TimeSpan.FromMilliseconds(350);
        private const int StepsPerLevel = 3;
        private int _direction;
        private DateTimeOffset _last;
        private bool _hasLast;

        public int Streak { get; private set; }

        public void Reset()
        {
            _direction = 0;
            _hasLast = false;
            Streak = 0;
        }

        public bool IsContinuing(int direction, DateTimeOffset now)
            => _hasLast && Math.Sign(direction) == _direction && now >= _last && now - _last <= StreakWindow;

        // Returns the signed number of seconds for this press.
        public double Step(int direction, double baseSeconds, double durationSeconds, DateTimeOffset now)
        {
            direction = Math.Sign(direction);
            if (direction == 0 || baseSeconds <= 0 || double.IsNaN(baseSeconds)) return 0;
            Streak = IsContinuing(direction, now) ? Math.Min(Streak + 1, 1000) : 0;
            _direction = direction;
            _last = now;
            _hasLast = true;
            return direction * StepSeconds(Streak, baseSeconds, durationSeconds);
        }

        public static double StepSeconds(int streak, double baseSeconds, double durationSeconds)
        {
            if (streak < 0) streak = 0;
            double duration = double.IsNaN(durationSeconds) || double.IsInfinity(durationSeconds) ? 0 : Math.Max(0, durationSeconds);
            double cap = Math.Max(baseSeconds, Math.Max(30, duration / 40));
            int level = Math.Min(10, streak / StepsPerLevel);
            return Math.Min(cap, baseSeconds * Math.Pow(2, level));
        }
    }
}
