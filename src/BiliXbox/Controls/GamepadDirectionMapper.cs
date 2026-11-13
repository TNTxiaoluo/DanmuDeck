using System;

namespace BiliXbox.Controls
{
    // Convert the left stick to one stable cardinal navigation command. This
    // only maps direction; GamepadInputState owns repeat timing and press edges.
    internal sealed class GamepadDirectionMapper
    {
        private const double StartThreshold = 0.55;
        private const double StopThreshold = 0.35;
        private const double AxisSwitchMargin = 0.15;
        private const GamepadInputButtons DirectionMask = GamepadInputButtons.Up | GamepadInputButtons.Down |
            GamepadInputButtons.Left | GamepadInputButtons.Right;
        private GamepadInputButtons _stickDirection;

        public void Reset() { _stickDirection = GamepadInputButtons.None; }

        public GamepadInputButtons Map(GamepadInputButtons dpad, double x, double y)
        {
            dpad &= DirectionMask;
            if (dpad != GamepadInputButtons.None)
            {
                // A deliberate D-pad command wins over a held or drifting stick.
                // Preserve its direction flags for the sampler's conflict rules.
                Reset();
                return dpad;
            }
            if (double.IsNaN(x) || double.IsNaN(y) || double.IsInfinity(x) || double.IsInfinity(y))
            {
                Reset();
                return GamepadInputButtons.None;
            }
            x = Math.Max(-1, Math.Min(1, x));
            y = Math.Max(-1, Math.Min(1, y));
            double horizontal = Math.Abs(x), vertical = Math.Abs(y);
            bool heldHorizontal = _stickDirection == GamepadInputButtons.Left || _stickDirection == GamepadInputButtons.Right;
            bool heldVertical = _stickDirection == GamepadInputButtons.Up || _stickDirection == GamepadInputButtons.Down;
            if (heldHorizontal && horizontal > StopThreshold && Horizontal(x) == _stickDirection)
            {
                if (vertical >= StartThreshold && vertical - horizontal > AxisSwitchMargin) _stickDirection = Vertical(y);
                return _stickDirection;
            }
            if (heldVertical && vertical > StopThreshold && Vertical(y) == _stickDirection)
            {
                if (horizontal >= StartThreshold && horizontal - vertical > AxisSwitchMargin) _stickDirection = Horizontal(x);
                return _stickDirection;
            }
            Reset();
            if (Math.Max(horizontal, vertical) < StartThreshold) return GamepadInputButtons.None;
            // At first diagonal engagement a tie resolves vertically; subsequent
            // near-diagonal jitter cannot repeatedly alternate between axes.
            _stickDirection = vertical >= horizontal ? Vertical(y) : Horizontal(x);
            return _stickDirection;
        }

        private static GamepadInputButtons Horizontal(double x) => x < 0 ? GamepadInputButtons.Left : GamepadInputButtons.Right;
        private static GamepadInputButtons Vertical(double y) => y < 0 ? GamepadInputButtons.Down : GamepadInputButtons.Up;
    }
}
