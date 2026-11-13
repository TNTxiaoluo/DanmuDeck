using System;

namespace BiliXbox.Controls
{
    // A stalled decoder can retain playable data while its media clock barely
    // advances. Rebuilding the same source is separate from CDN starvation
    // recovery: this class never selects a route or a different quality.
    internal sealed class PlaybackPipelineRecoveryState
    {
        public const int MaximumAttempts = 1;
        public const double MinimumPlayableBufferSeconds = 2;
        public const double MinimumHealthyClockRate = 0.2;
        public static readonly TimeSpan MinimumWait = TimeSpan.FromSeconds(12);
        public static readonly TimeSpan ClockObservationWindow = TimeSpan.FromSeconds(2);

        private bool _hasSample;
        private TimeSpan _lastPosition;
        private DateTimeOffset _lastSampleAt;
        private DateTimeOffset? _waitingSince;
        private DateTimeOffset _clockObservationAt;
        private TimeSpan _clockObservationPosition;

        public int Attempts { get; private set; }

        // Only a new video, part or explicit quality choice replenishes this
        // budget. Automatic rebuilding and temporary UI changes must not.
        public void Reset()
        {
            Attempts = 0;
            ResetWaiting(DateTimeOffset.MinValue);
        }

        public void ResetWaiting(DateTimeOffset now)
        {
            _hasSample = false;
            _waitingSince = null;
            _lastSampleAt = now;
        }

        // A true result consumes the one attempt, even if native rebuilding
        // subsequently fails. Call this once per playback observation.
        public bool ShouldRestart(DateTimeOffset now, TimeSpan mediaPosition,
            bool wantsPlayback, bool waiting, bool seekingOrModal,
            double bufferedSeconds)
        {
            var elapsed = _hasSample ? (now - _lastSampleAt).TotalSeconds : 0;
            var advanced = _hasSample ? (mediaPosition - _lastPosition).TotalSeconds : 0;
            // Explicit seeks arrive through seekingOrModal. Also reject media
            // discontinuities when a seek notification was missed. The forward
            // tolerance admits normal clock batching without accepting a jump.
            bool discontinuity = _hasSample && (elapsed < 0 || advanced < -0.01 ||
                advanced > Math.Max(2, elapsed * 1.5 + 0.5));
            _hasSample = true;
            _lastSampleAt = now;
            _lastPosition = mediaPosition;

            bool knownPlayableBuffer = !double.IsNaN(bufferedSeconds) &&
                !double.IsInfinity(bufferedSeconds) && bufferedSeconds >= MinimumPlayableBufferSeconds;
            if (!wantsPlayback || !waiting || seekingOrModal || !knownPlayableBuffer ||
                mediaPosition < TimeSpan.Zero || Attempts >= MaximumAttempts)
            {
                _waitingSince = null;
                return false;
            }

            if (!_waitingSince.HasValue || discontinuity)
            {
                BeginObservation(now, mediaPosition);
                return false;
            }

            var clockElapsed = now - _clockObservationAt;
            if (clockElapsed >= ClockObservationWindow)
            {
                var clockAdvance = (mediaPosition - _clockObservationPosition).TotalSeconds;
                // Compare over a real observation window. A single 0.1-second
                // tick in an otherwise frozen clock must not restart the timer.
                // Sustained progress at >=20% real time is outside this narrow
                // stall policy and receives a fresh waiting interval.
                if (clockAdvance >= clockElapsed.TotalSeconds * MinimumHealthyClockRate)
                {
                    BeginObservation(now, mediaPosition);
                    return false;
                }
                _clockObservationAt = now;
                _clockObservationPosition = mediaPosition;
            }

            if (now - _waitingSince.Value < MinimumWait) return false;

            ++Attempts;
            _waitingSince = null;
            return true;
        }

        private void BeginObservation(DateTimeOffset now, TimeSpan mediaPosition)
        {
            _waitingSince = now;
            _clockObservationAt = now;
            _clockObservationPosition = mediaPosition;
        }
    }
}
