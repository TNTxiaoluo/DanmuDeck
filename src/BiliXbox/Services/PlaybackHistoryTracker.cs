using System;
using BiliXbox.Models;

namespace BiliXbox.Services
{
    // Samples actual media advancement against a monotonic clock. Seeking,
    // buffering, opening a video and time spent behind a drawer are not viewing.
    public sealed class PlaybackHistoryTracker
    {
        private PlaybackHistoryTarget _target;
        private object _source;
        private double _time, _position, _duration, _watched, _maxPosition, _lastReportWatched;
        private long _start;
        private bool _sampled, _playing, _reported, _completed;
        public PlaybackHistoryAccount Account => _target?.Account;

        public PlaybackHistoryReport Observe(PlaybackHistoryTarget target, object source, double position,
            double duration, bool playing, double monotonicSeconds, long unixSeconds)
        {
            if (target == null || target.Account == null || target.Aid <= 0 || target.Cid <= 0 || source == null) return null;
            PlaybackHistoryReport previous = null;
            if (_target == null || !_target.Matches(target))
            {
                previous = Flush(false, true);
                Reset(); _target = target; _start = unixSeconds;
            }
            // Replacing a source for quality/recovery must keep the same viewing
            // session, but its restore-position jump cannot count as playback.
            _target = target;
            Sample(source, position, duration, playing, monotonicSeconds);
            if (previous != null) return previous;
            if (!playing) return Flush(false, false);
            if (_watched >= 1 && (!_reported || _watched - _lastReportWatched >= 15))
                return Report(_reported ? 0 : 1, false);
            return null;
        }

        public void Sample(object source, double position, double duration, bool playing, double monotonicSeconds)
        {
            if (_target == null || source == null || !Finite(position) || position < 0
                || !Finite(monotonicSeconds)) return;
            if (Finite(duration) && duration > 0) _duration = duration;
            if (_duration > 0) position = Math.Min(position, _duration);
            double elapsed = monotonicSeconds - _time, advanced = position - _position;
            if (_sampled && ReferenceEquals(source, _source) && _playing && elapsed > 0 && elapsed <= 3
                && advanced > 0 && advanced <= elapsed * 1.5 + .15)
                _watched += Math.Min(advanced, elapsed * 1.1);
            _source = source; _position = position; _time = monotonicSeconds; _sampled = true; _playing = playing;
            _maxPosition = Math.Max(_maxPosition, position);
        }

        public PlaybackHistoryReport Flush(bool completed = false, bool quitting = false)
        {
            if (_target == null || _watched < 1 || _completed) return null;
            if (!completed && _reported && _watched - _lastReportWatched < .05) return null;
            return Report(completed || quitting ? 4 : 2, completed);
        }

        private PlaybackHistoryReport Report(int playType, bool completed)
        {
            _reported = true; _completed = completed; _lastReportWatched = _watched;
            return new PlaybackHistoryReport(_target, _start, Seconds(_position), Seconds(_duration),
                Seconds(_watched), Seconds(_maxPosition), playType, completed);
        }

        public void Reset()
        {
            _target = null; _source = null; _time = _position = _duration = _watched = _maxPosition = _lastReportWatched = 0;
            _start = 0; _sampled = _playing = _reported = _completed = false;
        }
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        private static int Seconds(double value) => (int)Math.Min(604800, Math.Max(0, Math.Floor(value)));
    }
}
