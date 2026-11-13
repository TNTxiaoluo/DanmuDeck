using System;

namespace BiliXbox.Controls
{
    internal enum PlayerInputMode { Playback, Controls }
    internal enum PlayerFocusRow { Buttons, Seek, Header }
    internal enum PlayerDirection { Left, Right, Up, Down }

    internal struct PlayerNavigation
    {
        public PlayerFocusRow Row { get; }
        public int ButtonIndex { get; }
        public int HeaderIndex { get; }
        public int SeekSeconds { get; }
        public bool EnteredControls { get; }

        public PlayerNavigation(PlayerFocusRow row, int buttonIndex, int seekSeconds, bool enteredControls, int headerIndex = 0)
        {
            Row = row;
            ButtonIndex = buttonIndex;
            HeaderIndex = headerIndex;
            SeekSeconds = seekSeconds;
            EnteredControls = enteredControls;
        }
    }

    // The view maps these logical targets to visible controls.
    // It must bypass this state while a drawer or native text input owns focus.
    internal sealed class PlayerInputState
    {
        private static readonly TimeSpan ClickTail = TimeSpan.FromMilliseconds(120);
        private bool _confirmHeld, _hasConfirm;
        private DateTimeOffset _lastConfirm;

        public PlayerInputMode Mode { get; private set; } = PlayerInputMode.Playback;
        public PlayerFocusRow Row { get; private set; } = PlayerFocusRow.Buttons;
        public int ButtonIndex { get; private set; }
        public int HeaderIndex { get; private set; }
        public bool ConfirmHeld => _confirmHeld;

        public void Reset()
        {
            Mode = PlayerInputMode.Playback;
            Row = PlayerFocusRow.Buttons;
            ButtonIndex = 0;
            HeaderIndex = 0;
            _confirmHeld = _hasConfirm = false;
            _lastConfirm = default(DateTimeOffset);
        }

        // With controls hidden, A belongs to playback. Keep its latch: changing
        // modes cannot turn an already held confirmation into a second press.
        public void UsePlayback() { Mode = PlayerInputMode.Playback; }

        public PlayerNavigation Navigate(PlayerDirection direction, int buttonCount, int headerCount = 3)
        {
            ValidateCount(buttonCount);
            ValidateCount(headerCount);
            if (!Enum.IsDefined(typeof(PlayerDirection), direction))
                throw new ArgumentOutOfRangeException(nameof(direction));
            bool entered = Mode != PlayerInputMode.Controls;
            Mode = PlayerInputMode.Controls;
            ButtonIndex = Math.Max(0, Math.Min(ButtonIndex, buttonCount - 1));
            HeaderIndex = Math.Max(0, Math.Min(HeaderIndex, headerCount - 1));
            int seek = 0;
            if (entered)
            {
                // The first direction wakes the row at its leftmost action. It must
                // not also skip to the next action or seek without a visible focus.
                Row = PlayerFocusRow.Buttons;
                ButtonIndex = 0;
                HeaderIndex = 0;
            }
            else if (Row == PlayerFocusRow.Header)
            {
                if (direction == PlayerDirection.Down) Row = PlayerFocusRow.Seek;
                else if (direction == PlayerDirection.Left) HeaderIndex = Math.Max(0, HeaderIndex - 1);
                else if (direction == PlayerDirection.Right) HeaderIndex = Math.Min(headerCount - 1, HeaderIndex + 1);
            }
            else if (Row == PlayerFocusRow.Seek)
            {
                if (direction == PlayerDirection.Down) Row = PlayerFocusRow.Buttons;
                else if (direction == PlayerDirection.Up) { Row = PlayerFocusRow.Header; HeaderIndex = 0; }
                else if (direction == PlayerDirection.Left) seek = -10;
                else if (direction == PlayerDirection.Right) seek = 10;
            }
            else
            {
                if (direction == PlayerDirection.Up) Row = PlayerFocusRow.Seek;
                else if (direction == PlayerDirection.Left) ButtonIndex = Math.Max(0, ButtonIndex - 1);
                else if (direction == PlayerDirection.Right) ButtonIndex = Math.Min(buttonCount - 1, ButtonIndex + 1);
            }
            return new PlayerNavigation(Row, ButtonIndex, seek, entered, HeaderIndex);
        }

        public void SelectButton(int index, int buttonCount)
        {
            ValidateCount(buttonCount);
            if (index < 0 || index >= buttonCount) throw new ArgumentOutOfRangeException(nameof(index));
            Mode = PlayerInputMode.Controls;
            Row = PlayerFocusRow.Buttons;
            ButtonIndex = index;
        }

        // Both CoreWindow.KeyDown and routed KeyDown may observe one physical A.
        // A repeat or second delivery consumes no second action. The caller must
        // subscribe to KeyUp, including while drawers/dialogs are open.
        public bool ConfirmKeyDown(DateTimeOffset now, bool isRepeat)
        {
            if (_confirmHeld) return false;
            _confirmHeld = true;
            if (isRepeat) return false;
            _hasConfirm = true;
            _lastConfirm = now;
            return true;
        }

        public void ConfirmKeyUp(DateTimeOffset now)
        {
            if (!_confirmHeld) return;
            _confirmHeld = false;
            if (_hasConfirm) _lastConfirm = now;
        }

        // Explicit key dispatch executes its action directly. A subsequent XAML
        // Click is the same confirmation and must not execute the action again.
        // Pointer clicks without a preceding manual dispatch are accepted normally.
        public bool AcceptClick(DateTimeOffset now)
        {
            return !_confirmHeld && (!_hasConfirm || now - _lastConfirm >= ClickTail);
        }

        private static void ValidateCount(int buttonCount)
        {
            if (buttonCount < 1) throw new ArgumentOutOfRangeException(nameof(buttonCount));
        }
    }
}
