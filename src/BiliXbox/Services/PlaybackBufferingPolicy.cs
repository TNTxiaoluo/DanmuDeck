using System;
using System.Collections.Generic;
using System.Linq;

namespace BiliXbox.Services
{
    internal enum PlaybackBufferAction { None, Pause, Resume }

    internal struct PlaybackBufferRange
    {
        internal double Start, End;
        internal PlaybackBufferRange(double start, double end) { Start = start; End = end; }
    }

    // Uses the native playback session's playable ranges, never downloaded byte
    // counts or bitrate estimates. Disjoint future ranges cannot fill a gap.
    internal static class PlaybackBufferRanges
    {
        internal static double ContiguousSeconds(double position, IEnumerable<PlaybackBufferRange> ranges)
        {
            if (!Finite(position) || position < 0 || ranges == null) return -1;
            double end = position;
            foreach (var range in ranges.Where(value => Finite(value.Start) && Finite(value.End)
                && value.Start >= 0 && value.End > value.Start).OrderBy(value => value.Start))
            {
                if (range.End <= end) continue;
                if (range.Start > end + .001) break;
                end = range.End;
            }
            return Math.Max(0, end - position);
        }
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }

    // Initial Play remains native so download/decode can actually start. Once
    // native Buffering has three playable seconds it can resume once, even on
    // first startup. Only later starvation episodes pause below that target.
    internal sealed class PlaybackBufferingPolicy
    {
        private object _source;
        private int _generation;
        private bool _played, _waiting, _resumeRequested;
        internal bool Waiting => _waiting || _resumeRequested;
        // Only this state owns a deliberate engine Pause. A requested resume
        // can still hang inside the native decoder and must remain recoverable.
        internal bool PausedForBuffer => _waiting;

        internal PlaybackBufferAction Observe(object source, int generation, bool active, bool wantsPlayback,
            bool blocked, bool nativePlaying, bool nativeBuffering, double position, double duration, double bufferedSeconds)
        {
            if (!ReferenceEquals(source, _source) || generation != _generation)
            {
                Reset(); _source = source; _generation = generation;
            }
            if (source == null || !active || !wantsPlayback)
            { _waiting = _resumeRequested = false; return PlaybackBufferAction.None; }
            if (nativePlaying && !_waiting)
            { _played = true; _resumeRequested = false; }
            if (blocked) return PlaybackBufferAction.None;
            if (_resumeRequested) return PlaybackBufferAction.None;
            if (_waiting)
            {
                // A failed native range query must not leave an app-paused
                // source permanently stuck. Hand it back to native buffering.
                if (bufferedSeconds < 0 || double.IsNaN(bufferedSeconds) || double.IsInfinity(bufferedSeconds)
                    || Ready(position, duration, bufferedSeconds)) return RequestResume();
                return PlaybackBufferAction.None;
            }
            if (!nativeBuffering) return PlaybackBufferAction.None;
            if (bufferedSeconds < 0 || double.IsNaN(bufferedSeconds) || double.IsInfinity(bufferedSeconds))
                return PlaybackBufferAction.None;
            if (Ready(position, duration, bufferedSeconds)) return RequestResume();
            // Before first Playing, the native Play must keep running to drive
            // metadata and sample acquisition; do not app-pause that download.
            if (!_played) return PlaybackBufferAction.None;
            _waiting = true;
            return PlaybackBufferAction.Pause;
        }

        private PlaybackBufferAction RequestResume()
        { _waiting = false; _resumeRequested = true; return PlaybackBufferAction.Resume; }

        private static bool Ready(double position, double duration, double buffered)
        {
            double target = 3;
            if (!double.IsNaN(duration) && !double.IsInfinity(duration) && duration > 0
                && !double.IsNaN(position) && !double.IsInfinity(position) && position >= 0)
            {
                double remaining = Math.Max(0, duration - position);
                // Media duration and the final range can differ by a frame.
                // Let native finish a fully buffered short tail or EOF.
                if (remaining <= .05) return true;
                if (remaining < target) target = Math.Max(.001, remaining - .05);
            }
            return buffered >= target;
        }

        internal void Reset()
        { _source = null; _generation = 0; _played = _waiting = _resumeRequested = false; }

        internal void Rebuffer(object source, int generation)
        {
            Reset(); _source = source; _generation = generation;
            _played = _waiting = source != null;
        }
    }
}
