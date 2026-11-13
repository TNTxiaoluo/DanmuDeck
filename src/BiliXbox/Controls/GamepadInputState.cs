using System;

namespace BiliXbox.Controls
{
    [Flags]
    internal enum GamepadInputButtons
    {
        None = 0,
        Up = 1,
        Down = 2,
        Left = 4,
        Right = 8,
        Confirm = 16
    }

    internal struct GamepadInputFrame
    {
        public PlayerDirection? Direction { get; }
        public bool IsRepeat { get; }
        public bool ConfirmPressed { get; }
        public bool ConfirmReleased { get; }

        public GamepadInputFrame(PlayerDirection? direction, bool isRepeat, bool confirmPressed, bool confirmReleased)
        {
            Direction = direction;
            IsRepeat = isRepeat;
            ConfirmPressed = confirmPressed;
            ConfirmReleased = confirmReleased;
        }
    }

    // Interpret actual Gamepad.GetCurrentReading snapshots, independent of XAML
    // focus and the Xbox mouse-mode virtual key routing. Use a monotonic clock.
    // The view selects one connected Gamepad, passes the same instance as identity,
    // and gates polling during deactivation and native dialogs, including keyboards.
    internal sealed class GamepadInputState
    {
        private const GamepadInputButtons DirectionMask = GamepadInputButtons.Up | GamepadInputButtons.Down |
            GamepadInputButtons.Left | GamepadInputButtons.Right;
        // TV focus needs a deliberate hold before repeating and enough time to
        // see each selected control. Separate taps still move immediately.
        private static readonly TimeSpan RepeatDelay = TimeSpan.FromMilliseconds(550);
        private static readonly TimeSpan RepeatInterval = TimeSpan.FromMilliseconds(220);
        private static readonly TimeSpan ClickTail = TimeSpan.FromMilliseconds(120);
        private object _deviceIdentity;
        private bool _baselinePending = true, _confirmHeld, _hasSample, _hasConfirmEvent;
        private GamepadInputButtons _blocked, _lastEffective;
        private PlayerDirection? _direction;
        private TimeSpan _lastSample, _nextRepeat, _lastConfirmEvent;

        public bool ConfirmHeld => _confirmHeld;

        // A view can reset its own confirmation state while opening another
        // screen. Keep raw A suppression here so its later native Click cannot
        // activate the new screen, including the release-generated click tail.
        public bool SuppressesClick(TimeSpan now)
        {
            if (now < TimeSpan.Zero || (_hasSample && now < _lastSample))
                throw new ArgumentOutOfRangeException(nameof(now), "Click suppression requires the sampler's monotonic clock.");
            return _confirmHeld || (_blocked & GamepadInputButtons.Confirm) != 0 ||
                (_hasConfirmEvent && now - _lastConfirmEvent < ClickTail);
        }

        public void Reset()
        {
            _deviceIdentity = null;
            _baselinePending = true;
            _confirmHeld = _hasSample = _hasConfirmEvent = false;
            _blocked = _lastEffective = GamepadInputButtons.None;
            _direction = null;
            _lastSample = _nextRepeat = _lastConfirmEvent = TimeSpan.Zero;
        }

        public GamepadInputFrame Sample(TimeSpan now, object deviceIdentity, GamepadInputButtons buttons, bool enabled)
        {
            if (now < TimeSpan.Zero || (_hasSample && now < _lastSample))
                throw new ArgumentOutOfRangeException(nameof(now), "Gamepad samples require a nondecreasing monotonic clock.");
            _hasSample = true;
            _lastSample = now;
            buttons &= DirectionMask | GamepadInputButtons.Confirm;

            bool release = _confirmHeld;
            bool blockedConfirm = (_blocked & GamepadInputButtons.Confirm) != 0;
            if (!enabled || deviceIdentity == null)
            {
                // A window/keyboard transition must also release the view's latch.
                // Further gated polls emit nothing, even when the device stays held.
                _deviceIdentity = deviceIdentity;
                _baselinePending = true;
                if (release || blockedConfirm) RecordConfirmEvent(now);
                ClearActiveInput();
                return new GamepadInputFrame(null, false, false, release);
            }

            if (_baselinePending || !ReferenceEquals(deviceIdentity, _deviceIdentity))
            {
                _deviceIdentity = deviceIdentity;
                _baselinePending = false;
                if (release || blockedConfirm) RecordConfirmEvent(now);
                ClearActiveInput();
                // Opening playback, reconnecting, or dismissing the keyboard cannot
                // turn an already held A/D-pad into a fresh action. Release rearms it.
                _blocked = buttons;
                return new GamepadInputFrame(null, false, false, release);
            }

            _blocked &= buttons;
            if (blockedConfirm && (_blocked & GamepadInputButtons.Confirm) == 0) RecordConfirmEvent(now);
            GamepadInputButtons effective = buttons & ~_blocked;
            bool confirm = (effective & GamepadInputButtons.Confirm) != 0;
            bool pressed = confirm && !_confirmHeld;
            bool released = !confirm && _confirmHeld;
            _confirmHeld = confirm;
            if (pressed || released) RecordConfirmEvent(now);

            PlayerDirection? nextDirection = ResolveDirection(effective, effective & ~_lastEffective, _direction);
            _lastEffective = effective;
            if (nextDirection != _direction)
            {
                _direction = nextDirection;
                _nextRepeat = now + RepeatDelay;
                return new GamepadInputFrame(nextDirection, false, pressed, released);
            }

            if (_direction.HasValue && now >= _nextRepeat)
            {
                // A delayed UI frame produces one action, never a catch-up burst.
                _nextRepeat = now + RepeatInterval;
                return new GamepadInputFrame(_direction, true, pressed, released);
            }
            return new GamepadInputFrame(null, false, pressed, released);
        }

        private void ClearActiveInput()
        {
            _confirmHeld = false;
            _blocked = _lastEffective = GamepadInputButtons.None;
            _direction = null;
            _nextRepeat = TimeSpan.Zero;
        }

        private void RecordConfirmEvent(TimeSpan now)
        {
            _hasConfirmEvent = true;
            _lastConfirmEvent = now;
        }

        private static PlayerDirection? ResolveDirection(GamepadInputButtons effective,
            GamepadInputButtons newlyPressed, PlayerDirection? previous)
        {
            GamepadInputButtons directions = effective & DirectionMask;
            if ((directions & (GamepadInputButtons.Up | GamepadInputButtons.Down)) ==
                (GamepadInputButtons.Up | GamepadInputButtons.Down))
                directions &= ~(GamepadInputButtons.Up | GamepadInputButtons.Down);
            if ((directions & (GamepadInputButtons.Left | GamepadInputButtons.Right)) ==
                (GamepadInputButtons.Left | GamepadInputButtons.Right))
                directions &= ~(GamepadInputButtons.Left | GamepadInputButtons.Right);

            GamepadInputButtons fresh = newlyPressed & directions;
            if (fresh != GamepadInputButtons.None) return FirstDirection(fresh);
            if (previous.HasValue && (directions & DirectionButton(previous.Value)) != 0) return previous;
            return FirstDirection(directions);
        }

        private static PlayerDirection? FirstDirection(GamepadInputButtons directions)
        {
            if ((directions & GamepadInputButtons.Up) != 0) return PlayerDirection.Up;
            if ((directions & GamepadInputButtons.Down) != 0) return PlayerDirection.Down;
            if ((directions & GamepadInputButtons.Left) != 0) return PlayerDirection.Left;
            if ((directions & GamepadInputButtons.Right) != 0) return PlayerDirection.Right;
            return null;
        }

        private static GamepadInputButtons DirectionButton(PlayerDirection direction)
        {
            switch (direction)
            {
                case PlayerDirection.Up: return GamepadInputButtons.Up;
                case PlayerDirection.Down: return GamepadInputButtons.Down;
                case PlayerDirection.Left: return GamepadInputButtons.Left;
                default: return GamepadInputButtons.Right;
            }
        }
    }
}
