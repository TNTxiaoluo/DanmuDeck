namespace BiliXbox.Controls
{
    internal enum ShoulderAction { None, Previous, Refresh }
    // Reserve LB until release so a refresh chord cannot first switch or seek.
    internal sealed class ShoulderChordState
    {
        private bool _lb, _used, _held, _blocked;
        public bool Consumed => _used;
        public ShoulderAction Sample(bool lb, bool y, bool enabled)
        {
            if (!enabled) { _lb = false; _used = _held = false; _blocked = true; return ShoulderAction.None; }
            if (_blocked) { if (!lb && !y) _blocked = false; return ShoulderAction.None; }
            if (lb && !_lb) _used = false;
            bool chord = lb && y;
            ShoulderAction action = ShoulderAction.None;
            if (chord) { _used = true; if (!_held) action = ShoulderAction.Refresh; }
            if (!lb && _lb && !_used) action = ShoulderAction.Previous;
            _lb = lb; _held = chord;
            return action;
        }
    }
}
