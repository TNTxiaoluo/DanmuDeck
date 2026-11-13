using System;

namespace BiliXbox.Controls
{
    internal struct GamepadAuxiliaryFrame
    {
        public int SectionStep { get; }
        public double ScrollPixels { get; }
        public GamepadAuxiliaryFrame(int step, double pixels) { SectionStep = step; ScrollPixels = pixels; }
    }

    // Bumpers are one action per press. Right-stick scroll uses elapsed time,
    // a deadzone and a bounded frame delta, independent of focus-key repeat.
    internal sealed class GamepadAuxiliaryState
    {
        private object _device;
        private bool _baseline = true, _left, _right, _stickBlocked = true, _sampled;
        private TimeSpan _last;
        public void Reset() { _device = null; _baseline = _stickBlocked = true; _left = _right = _sampled = false; _last = TimeSpan.Zero; }
        public GamepadAuxiliaryFrame Sample(TimeSpan now, object device, bool left, bool right, double stickY, bool enabled)
        {
            if (now < TimeSpan.Zero || (_sampled && now < _last)) throw new ArgumentOutOfRangeException(nameof(now));
            double seconds = _sampled ? Math.Min(.05, (now - _last).TotalSeconds) : 0;
            _sampled = true; _last = now;
            if (!enabled || device == null)
            { _device = device; _baseline = _stickBlocked = true; _left = left; _right = right; return default(GamepadAuxiliaryFrame); }
            if (_baseline || !ReferenceEquals(device, _device))
            { _device = device; _baseline = false; _left = left; _right = right; _stickBlocked = true; return default(GamepadAuxiliaryFrame); }
            int step = left == right ? 0 : left && !_left ? -1 : right && !_right ? 1 : 0;
            _left = left; _right = right;
            if (double.IsNaN(stickY) || double.IsInfinity(stickY)) { _stickBlocked = true; return new GamepadAuxiliaryFrame(step, 0); }
            double magnitude = Math.Min(1, Math.Abs(stickY));
            if (magnitude <= .22) _stickBlocked = false;
            double pixels = 0;
            if (!_stickBlocked && magnitude > .22)
            {
                double strength = (magnitude - .22) / .78;
                pixels = -Math.Sign(stickY) * 920 * strength * strength * seconds;
            }
            return new GamepadAuxiliaryFrame(step, pixels);
        }
    }
}
