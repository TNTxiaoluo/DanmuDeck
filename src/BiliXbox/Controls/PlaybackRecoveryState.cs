using System;

namespace BiliXbox.Controls
{
    // A decision consumes one attempt. The caller owns the actual source change
    // and must pass wantsPlayback=false after pause, stop or end of media.
    internal sealed class PlaybackRecoveryState
    {
        public const int MaximumAttempts = 2;
        public static readonly TimeSpan MinimumWait = TimeSpan.FromSeconds(8);
        public static readonly TimeSpan SwitchCooldown = TimeSpan.FromSeconds(20);

        private bool _hasSample;
        private TimeSpan _lastPosition;
        private DateTimeOffset _lastSampleAt;
        private DateTimeOffset? _waitingSince;
        private DateTimeOffset? _lastSwitchAt;

        public int Attempts { get; private set; }

        // New video, part or user-selected quality starts a new attempt budget.
        public void Reset()
        {
            Attempts = 0;
            _lastSwitchAt = null;
            _hasSample = false;
            _waitingSince = null;
        }

        // An automatic source change, seek or UI interruption receives a fresh
        // waiting window, but cannot replenish the budget or bypass cooldown.
        public void ResetWaiting(DateTimeOffset now)
        {
            _hasSample = false;
            _waitingSince = null;
            _lastSampleAt = now;
        }

        // Unknown buffer duration is represented by NaN or a negative number.
        // This is a consuming decision, so call it once per playback sample.
        public bool ShouldSwitch(DateTimeOffset now, TimeSpan mediaPosition,
            bool wantsPlayback, bool waiting, bool seekingOrModal,
            double bufferedSeconds, bool hasAlternates)
        {
            bool clockMoved = _hasSample &&
                Math.Abs((mediaPosition - _lastPosition).TotalSeconds) > 0.0001;
            bool wallClockRewound = _hasSample && now < _lastSampleAt;
            _hasSample = true;
            _lastPosition = mediaPosition;
            _lastSampleAt = now;

            bool starving = double.IsNaN(bufferedSeconds) || bufferedSeconds < 1;
            if (!wantsPlayback || !waiting || seekingOrModal || !starving || !hasAlternates)
            {
                _waitingSince = null;
                return false;
            }

            // Any media movement, including a rewind whose seek event was not
            // observed, breaks continuity. Progress never replenishes attempts.
            if (!_waitingSince.HasValue || clockMoved || wallClockRewound)
                _waitingSince = now;

            if (Attempts >= MaximumAttempts || now - _waitingSince.Value < MinimumWait ||
                (_lastSwitchAt.HasValue && now - _lastSwitchAt.Value < SwitchCooldown))
                return false;

            ++Attempts;
            _lastSwitchAt = now;
            // A long gap between samples grants one decision, not a backlog of
            // missed decisions. Even a failed source change gets a fresh wait.
            _waitingSince = now;
            return true;
        }
    }
}
