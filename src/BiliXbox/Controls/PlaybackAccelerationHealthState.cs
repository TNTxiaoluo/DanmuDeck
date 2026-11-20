using System;

namespace BiliXbox.Controls
{
    // Network bytes (including probes/retries) cannot prove playable progress.
    // One stalled accelerated source may restart natively at the same quality.
    internal sealed class PlaybackAccelerationHealthState
    {
        internal static readonly TimeSpan MaximumWait = TimeSpan.FromSeconds(12);
        private static readonly TimeSpan BodyFailureWait = TimeSpan.FromSeconds(8);
        private object _source;
        private int _generation;
        private DateTimeOffset? _waitingSince;
        private double _position, _buffer;
        private bool _triggered, _bodyFailed;

        internal void ResetWaiting() => _waitingSince = null;

        internal bool Observe(object source, int generation, DateTimeOffset now, bool enabled,
            bool active, bool waiting, bool blocked, double position, double buffer, bool bodyFailed = false)
        {
            if (!ReferenceEquals(source, _source) || generation != _generation)
            {
                _source = source;
                _generation = generation;
                _triggered = false;
                ResetWaiting();
            }
            if (_triggered) return false;
            if (!enabled || !active || !waiting || blocked || buffer < 0 || double.IsNaN(buffer) || buffer >= 3)
            {
                ResetWaiting();
                return false;
            }

            // Once an accelerated body fails, the filter downloads subsequent
            // ranges natively. Give those retries time to refill the same source.
            double refillProgress = bodyFailed ? .75 : .25;
            if (!_waitingSince.HasValue || now < _waitingSince.Value || bodyFailed != _bodyFailed
                || position < _position || position - _position > .25 || buffer - _buffer >= refillProgress)
            {
                _waitingSince = now;
                _position = position;
                _buffer = buffer;
                _bodyFailed = bodyFailed;
            }
            if (now - _waitingSince.Value < (bodyFailed ? BodyFailureWait : MaximumWait)) return false;
            return _triggered = true;
        }
    }
}
