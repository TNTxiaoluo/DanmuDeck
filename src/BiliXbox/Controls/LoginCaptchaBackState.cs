namespace BiliXbox.Controls
{
    // A provider WebView can swallow routed B. This gate handles raw B once,
    // only after release, and forgets the gesture across inactive windows.
    internal sealed class LoginCaptchaBackState
    {
        private bool _released;
        internal bool Update(bool available, bool pressed)
        {
            if (!available) { Reset(); return false; }
            if (!pressed) { _released = true; return false; }
            bool cancel = _released;
            _released = false;
            return cancel;
        }
        internal void Reset() { _released = false; }
    }
}
