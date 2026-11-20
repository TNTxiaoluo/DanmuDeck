using System;

namespace BiliXbox.Controls
{
    // Opening A/Enter/Space belongs to the previous surface. Only a fresh
    // press after neutral may activate a newly focused dialog control.
    internal sealed class DialogConfirmReleaseGate
    {
        private TimeSpan? _neutralAt;
        private bool _pressed;
        internal bool Armed { get; private set; }
        internal void Observe(TimeSpan now, bool held)
        {
            if (Armed) return;
            if (held) { _neutralAt = null; return; }
            if (!_neutralAt.HasValue || now < _neutralAt.Value) _neutralAt = now;
            if (now - _neutralAt.Value >= TimeSpan.FromMilliseconds(100)) Armed = true;
        }
        internal bool KeyDown(bool repeat)
        {
            if (!Armed || repeat || _pressed) return false;
            return _pressed = true;
        }
        internal bool KeyUp()
        { bool allowed = Armed && _pressed; _pressed = false; return allowed; }
    }
}
