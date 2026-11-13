using System;

namespace BiliXbox.Controls
{
    internal enum PlayerDrawer { None, Settings, Comments, DanmakuSettings, Interactions }
    internal enum PlayerBackAction { CloseDrawer, HideControls, LeaveVideo }

    // Pure state used by the actual player and by lifecycle tests; time is supplied by the caller.
    internal sealed class PlayerUiState
    {
        public bool ControlsVisible { get; private set; } = true;
        public PlayerDrawer Drawer { get; private set; }
        private DateTimeOffset _lastActivity;
        private bool _hasPointerPosition;
        private double _pointerX, _pointerY;
        private DateTimeOffset _lastPointerActivity;

        public void Show(DateTimeOffset now) { ControlsVisible = true; _lastActivity = now; }
        public void Touch(DateTimeOffset now) { _lastActivity = now; }
        public void Hide() { if (Drawer == PlayerDrawer.None) ControlsVisible = false; }
        public void Open(PlayerDrawer drawer, DateTimeOffset now) { Drawer = drawer; Show(now); }
        public void Close(DateTimeOffset now) { Drawer = PlayerDrawer.None; Show(now); }
        public void Reset(DateTimeOffset now) { Drawer = PlayerDrawer.None; _hasPointerPosition = false; _lastPointerActivity = now; Show(now); }
        public bool PointerMoved(double x, double y, DateTimeOffset now)
        {
            if (double.IsNaN(x) || double.IsNaN(y) || double.IsInfinity(x) || double.IsInfinity(y)) return false;
            // Layout/hit-test changes can synthesize PointerMoved at unchanged
            // coordinates. They must not wake controls that just disappeared.
            if (_hasPointerPosition && Math.Abs(x - _pointerX) < 3 && Math.Abs(y - _pointerY) < 3) return false;
            _hasPointerPosition = true; _pointerX = x; _pointerY = y;
            _lastPointerActivity = now; Show(now); return true;
        }
        public void PointerPressed(DateTimeOffset now) { _lastPointerActivity = now; Touch(now); }
        public bool ShouldHidePointer(DateTimeOffset now, bool blocked)
            => Drawer == PlayerDrawer.None && !blocked && now - _lastPointerActivity >= TimeSpan.FromSeconds(3);
        public bool CanAutoHide(DateTimeOffset now, bool playing, bool blocked)
            => ControlsVisible && Drawer == PlayerDrawer.None && playing && !blocked && now - _lastActivity >= TimeSpan.FromSeconds(5);
        public PlayerBackAction Back(DateTimeOffset now)
        {
            if (Drawer != PlayerDrawer.None) { Close(now); return PlayerBackAction.CloseDrawer; }
            if (ControlsVisible) { Hide(); return PlayerBackAction.HideControls; }
            return PlayerBackAction.LeaveVideo;
        }
    }
}
