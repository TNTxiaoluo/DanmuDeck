using System;

namespace BiliXbox.Controls
{
    // Uses movement of the media clock as well as native buffering events. Some
    // custom streams stop advancing without immediately reporting Buffering.
    internal sealed class PlaybackHealthState
    {
        private bool _hasSample, _wasPlaying;
        private TimeSpan _lastPosition;
        private DateTimeOffset _lastAdvance;
        public bool Waiting { get; private set; }
        public bool Stalled { get; private set; }

        public void Reset() { _hasSample = _wasPlaying = Waiting = Stalled = false; }
        public void Update(DateTimeOffset now, TimeSpan position, bool playing, bool loading, bool buffering)
        {
            // Pause, seek and a new source reset the watchdog; a static paused
            // position is expected and must never display a networking spinner.
            if (!_hasSample || !playing || !_wasPlaying || Math.Abs((position - _lastPosition).TotalSeconds) > 0.0001)
                _lastAdvance = now;
            _hasSample = true;
            _lastPosition = position;
            _wasPlaying = playing;
            Stalled = playing && !loading && !buffering && now - _lastAdvance >= TimeSpan.FromMilliseconds(800);
            Waiting = loading || buffering || Stalled;
        }
    }
}
