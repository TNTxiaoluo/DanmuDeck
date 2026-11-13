using System;

namespace BiliXbox.GamepadInputTests
{
    public static class Tests
    {
        private static int _assertions;
        private static readonly object Pad = new object();
        private static readonly DateTimeOffset Epoch = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private static TimeSpan At(int milliseconds) => TimeSpan.FromMilliseconds(milliseconds);
        private static void Check(bool condition, string message)
        {
            ++_assertions;
            if (!condition) throw new InvalidOperationException(message);
        }
        private static void Quiet(Controls.GamepadInputFrame frame, string message)
        {
            Check(!frame.Direction.HasValue && !frame.IsRepeat && !frame.ConfirmPressed && !frame.ConfirmReleased, message);
        }
        private static Controls.GamepadInputFrame Read(Controls.GamepadInputState state, int milliseconds,
            Controls.GamepadInputButtons buttons, bool enabled = true, object device = null)
        {
            return state.Sample(At(milliseconds), device ?? Pad, buttons, enabled);
        }
        private static Controls.GamepadInputState Connected()
        {
            var state = new Controls.GamepadInputState();
            Quiet(Read(state, 0, Controls.GamepadInputButtons.None), "Connecting a neutral controller emitted input.");
            return state;
        }

        public static void Run()
        {
            _assertions = 0;
            RepeatAndSeparatePresses();
            ConfirmEdgesAndIndependentKeys();
            DirectionChangesAndConflicts();
            GatingAndReconnect();
            PlayerIntegration();
            ClickSuppressionAcrossScreens();
            ClockAndReset();
            Console.WriteLine("All " + _assertions + " physical gamepad input assertions passed. Xbox raw input delivery still requires device testing.");
        }

        private static void RepeatAndSeparatePresses()
        {
            var state = Connected();
            var button = Controls.GamepadInputButtons.Right;
            var first = Read(state, 10, button);
            Check(first.Direction == Controls.PlayerDirection.Right && !first.IsRepeat,
                "An initial physical direction did not activate immediately.");
            Quiet(Read(state, 10, button), "Sampling the same reading twice repeated direction.");
            Quiet(Read(state, 559, button), "D-pad repeated before the 550 ms hold delay.");
            var repeat = Read(state, 560, button);
            Check(repeat.Direction == first.Direction && repeat.IsRepeat, "D-pad did not repeat exactly at its hold delay.");
            Quiet(Read(state, 779, button), "D-pad repeated before its 220 ms interval.");
            Check(Read(state, 780, button).IsRepeat, "D-pad did not repeat after 220 ms.");
            Check(Read(state, 5000, button).IsRepeat, "Delayed frames lost a held direction.");
            Quiet(Read(state, 5000, button), "Delayed frames caused a catch-up input burst.");
            Quiet(Read(state, 5219, button), "Delayed-frame repeats resumed too early.");
            Check(Read(state, 5220, button).IsRepeat, "Delayed-frame repeats did not resume normally.");
            Quiet(Read(state, 5440, Controls.GamepadInputButtons.None),
                "Releasing at the next repeat deadline emitted an unwanted direction.");
            Quiet(Read(state, 6000, Controls.GamepadInputButtons.None), "A released direction resumed repeating later.");
            var fresh = Read(state, 6010, button);
            Check(fresh.Direction == first.Direction && !fresh.IsRepeat, "A fresh direction press was not immediate after release.");
            Quiet(Read(state, 6559, button), "A fresh press inherited the old short repeat interval.");
            Check(Read(state, 6560, button).IsRepeat, "A fresh press did not receive a complete new hold delay.");
        }

        private static void ConfirmEdgesAndIndependentKeys()
        {
            var state = Connected();
            var first = Read(state, 10, Controls.GamepadInputButtons.Confirm);
            Check(first.ConfirmPressed && !first.ConfirmReleased && state.ConfirmHeld, "A initial edge was not emitted.");
            for (int i = 11; i <= 10000; i += 17)
                Quiet(Read(state, i, Controls.GamepadInputButtons.Confirm), "A repeated while held.");
            var released = Read(state, 10020, Controls.GamepadInputButtons.None);
            Check(!released.ConfirmPressed && released.ConfirmReleased && !state.ConfirmHeld, "A release did not clear the confirm latch.");
            Quiet(Read(state, 10020, Controls.GamepadInputButtons.None), "An unchanged A release was emitted twice.");
            Check(Read(state, 10025, Controls.GamepadInputButtons.Confirm).ConfirmPressed, "Two distinct fast A presses were debounced.");
            var direction = Read(state, 10030, Controls.GamepadInputButtons.Confirm | Controls.GamepadInputButtons.Right);
            Check(direction.Direction == Controls.PlayerDirection.Right && !direction.ConfirmPressed,
                "Pressing direction while A was held activated A again.");
            var together = Read(state, 10040, Controls.GamepadInputButtons.Left);
            Check(together.Direction == Controls.PlayerDirection.Left && together.ConfirmReleased,
                "Simultaneous A release and direction change lost an edge.");
            var simultaneous = Read(state, 10050, Controls.GamepadInputButtons.Right | Controls.GamepadInputButtons.Confirm);
            Check(simultaneous.Direction == Controls.PlayerDirection.Right && simultaneous.ConfirmPressed,
                "Simultaneous direction and A edges lost one control.");
        }

        private static void DirectionChangesAndConflicts()
        {
            var state = Connected();
            Read(state, 10, Controls.GamepadInputButtons.Right);
            var left = Read(state, 20, Controls.GamepadInputButtons.Left);
            Check(left.Direction == Controls.PlayerDirection.Left && !left.IsRepeat, "Switching direction waited for an old repeat deadline.");
            Quiet(Read(state, 30, Controls.GamepadInputButtons.Left | Controls.GamepadInputButtons.Right),
                "Opposing horizontal directions moved the selection.");
            Check(Read(state, 40, Controls.GamepadInputButtons.Right).Direction == Controls.PlayerDirection.Right,
                "Releasing a direction conflict did not recover navigation.");
            Check(Read(state, 50, Controls.GamepadInputButtons.Up).Direction == Controls.PlayerDirection.Up,
                "Vertical direction was not interpreted.");
            Quiet(Read(state, 60, Controls.GamepadInputButtons.Up | Controls.GamepadInputButtons.Down),
                "Opposing vertical directions moved the selection.");
            Check(Read(state, 70, Controls.GamepadInputButtons.Up).Direction == Controls.PlayerDirection.Up,
                "Releasing a vertical conflict did not recover navigation.");
            Check(Read(state, 80, Controls.GamepadInputButtons.Up | Controls.GamepadInputButtons.Right).Direction == Controls.PlayerDirection.Right,
                "A newly pressed diagonal direction was hidden behind the held axis.");
            Quiet(Read(state, 90, Controls.GamepadInputButtons.Up | Controls.GamepadInputButtons.Right),
                "Stable diagonal input oscillated between focus directions.");
            Check(Read(state, 100, Controls.GamepadInputButtons.Up).Direction == Controls.PlayerDirection.Up,
                "The remaining diagonal axis was lost after release.");
            Quiet(Read(state, 110, (Controls.GamepadInputButtons)128), "Unmapped controller buttons generated navigation.");

            var repeating = Connected();
            Read(repeating, 10, Controls.GamepadInputButtons.Right);
            Check(Read(repeating, 560, Controls.GamepadInputButtons.Right).IsRepeat, "Held-direction change setup did not enter repeat mode.");
            var changed = Read(repeating, 770, Controls.GamepadInputButtons.Left);
            Check(changed.Direction == Controls.PlayerDirection.Left && !changed.IsRepeat,
                "Changing a repeating direction did not produce an immediate fresh step.");
            Quiet(Read(repeating, 780, Controls.GamepadInputButtons.Left), "Changing direction inherited the old repeat deadline.");
            Quiet(Read(repeating, 1319, Controls.GamepadInputButtons.Left), "Changed direction repeated before its own full hold delay.");
            var newRepeat = Read(repeating, 1320, Controls.GamepadInputButtons.Left);
            Check(newRepeat.Direction == Controls.PlayerDirection.Left && newRepeat.IsRepeat,
                "Changed direction did not restart repeating after its own hold delay.");
        }

        private static void GatingAndReconnect()
        {
            foreach (bool disconnected in new[] { false, true })
            {
                var state = Connected();
                var held = Controls.GamepadInputButtons.Confirm | Controls.GamepadInputButtons.Right;
                Check(Read(state, 10, held).ConfirmPressed, "Initial confirm setup failed.");
                var gate = state.Sample(At(20), disconnected ? null : Pad, held, !disconnected ? false : true);
                Check(gate.ConfirmReleased && !gate.ConfirmPressed && !gate.Direction.HasValue,
                    "Keyboard/deactivation/disconnection did not release the existing A latch.");
                Quiet(state.Sample(At(30), disconnected ? null : Pad, held, !disconnected ? false : true),
                    "Repeated disabled/disconnected polling repeated a release.");
                Quiet(Read(state, 40, held), "Resuming with held buttons activated playback or navigation.");
                Quiet(Read(state, 1000, held), "Held pre-resume direction started repeating without a new press.");
                Quiet(Read(state, 1010, Controls.GamepadInputButtons.None), "Releasing the resumed baseline generated an action.");
                var fresh = Read(state, 1020, held);
                Check(fresh.ConfirmPressed && fresh.Direction == Controls.PlayerDirection.Right,
                    "Fresh input did not recover after a keyboard/deactivation/disconnection transition.");
            }

            var replacement = Connected();
            Read(replacement, 10, Controls.GamepadInputButtons.Confirm);
            var secondPad = new object();
            var changed = Read(replacement, 20, Controls.GamepadInputButtons.Confirm, true, secondPad);
            Check(changed.ConfirmReleased && !changed.ConfirmPressed, "Replacing the controller transferred held A into a new action.");
            Quiet(Read(replacement, 30, Controls.GamepadInputButtons.None, true, secondPad), "Replacement controller release generated an action.");
            Check(Read(replacement, 40, Controls.GamepadInputButtons.Confirm, true, secondPad).ConfirmPressed,
                "Replacement controller could not confirm after release.");

            var initiallyHeld = new Controls.GamepadInputState();
            Quiet(Read(initiallyHeld, 0, Controls.GamepadInputButtons.Confirm | Controls.GamepadInputButtons.Down),
                "Opening playback while A/D-pad were held immediately activated a control.");
            Quiet(Read(initiallyHeld, 1000, Controls.GamepadInputButtons.Confirm | Controls.GamepadInputButtons.Down),
                "Initial held input started repeating without release.");
            Quiet(Read(initiallyHeld, 1010, Controls.GamepadInputButtons.Down), "Releasing one blocked input emitted an A-up without a prior down.");
            Check(Read(initiallyHeld, 1020, Controls.GamepadInputButtons.Down | Controls.GamepadInputButtons.Confirm).ConfirmPressed,
                "Releasing A did not individually rearm it while direction stayed held.");
            Check(Read(initiallyHeld, 1030, Controls.GamepadInputButtons.Left | Controls.GamepadInputButtons.Confirm).Direction == Controls.PlayerDirection.Left,
                "An unrelated held baseline direction blocked a fresh direction.");
        }

        private static void PlayerIntegration()
        {
            var raw = Connected();
            var player = new Controls.PlayerInputState();
            int activated = 0;
            Action<int, Controls.GamepadInputButtons> deliver = (milliseconds, buttons) =>
            {
                var frame = Read(raw, milliseconds, buttons);
                if (frame.ConfirmReleased) player.ConfirmKeyUp(Epoch.AddMilliseconds(milliseconds));
                if (frame.Direction.HasValue) player.Navigate(frame.Direction.Value, 9);
                if (frame.ConfirmPressed && player.ConfirmKeyDown(Epoch.AddMilliseconds(milliseconds), false)) ++activated;
            };
            deliver(10, Controls.GamepadInputButtons.Right);
            Check(player.Mode == Controls.PlayerInputMode.Controls && player.ButtonIndex == 0,
                "Raw D-pad did not enter bottom-row mode at its first action.");
            deliver(20, Controls.GamepadInputButtons.None);
            deliver(30, Controls.GamepadInputButtons.Right);
            Check(player.ButtonIndex == 1, "A second raw direction did not select the next bottom action.");
            deliver(40, Controls.GamepadInputButtons.Right | Controls.GamepadInputButtons.Confirm);
            Check(activated == 1, "Raw A did not confirm the selected action exactly once.");
            Check(!player.AcceptClick(Epoch.AddMilliseconds(41)), "An OS/XAML click duplicated the raw A dispatch.");
            player.UsePlayback();
            deliver(50, Controls.GamepadInputButtons.Right | Controls.GamepadInputButtons.Confirm);
            Check(activated == 1 && player.Mode == Controls.PlayerInputMode.Playback,
                "Pointer motion while A remained held replayed the confirmation.");
            deliver(60, Controls.GamepadInputButtons.None);
            deliver(70, Controls.GamepadInputButtons.Left);
            Check(player.ButtonIndex == 0 && player.Mode == Controls.PlayerInputMode.Controls,
                "A fresh direction after pointer movement failed to reenter bottom controls.");
            deliver(80, Controls.GamepadInputButtons.Confirm);
            Check(activated == 2, "A distinct fast raw confirmation was suppressed by click-tail logic.");
            var disabled = raw.Sample(At(90), Pad, Controls.GamepadInputButtons.Confirm, false);
            if (disabled.ConfirmReleased) player.ConfirmKeyUp(Epoch.AddMilliseconds(90));
            Check(!player.ConfirmHeld, "Opening a native keyboard left the view's A latch stuck.");
        }

        private static void ClickSuppressionAcrossScreens()
        {
            var raw = Connected();
            var player = new Controls.PlayerInputState();
            Check(!raw.SuppressesClick(At(0)), "Neutral raw input suppressed an independent native Click.");
            Check(Read(raw, 10, Controls.GamepadInputButtons.Confirm).ConfirmPressed, "Home confirmation setup failed.");
            player.ConfirmKeyDown(Epoch.AddMilliseconds(10), false);
            player.Reset(); // Opening a video resets its view state immediately.
            Check(player.AcceptClick(Epoch.AddMilliseconds(11)), "Cross-screen setup retained the view's old A suppression.");
            Check(raw.SuppressesClick(At(11)), "Opening a video lost raw suppression while the home A was still held.");
            var release = Read(raw, 20, Controls.GamepadInputButtons.None);
            Check(release.ConfirmReleased, "The original home A did not produce its raw release edge.");
            player.ConfirmKeyUp(Epoch.AddMilliseconds(20)); // It has no view-held A now.
            Check(player.AcceptClick(Epoch.AddMilliseconds(21)) && raw.SuppressesClick(At(21)),
                "A late native Click could pause the new video after its view state was reset.");
            Check(raw.SuppressesClick(At(139)), "A release-generated native Click escaped before 120 ms.");
            Check(!raw.SuppressesClick(At(140)), "An independent Click remained suppressed at the exact 120 ms boundary.");
            Read(raw, 150, Controls.GamepadInputButtons.None);
            Check(!raw.SuppressesClick(At(150)), "Duplicate released samples extended raw click suppression.");

            foreach (bool disconnected in new[] { false, true })
            {
                var state = Connected();
                Read(state, 10, Controls.GamepadInputButtons.Confirm);
                var gated = state.Sample(At(20), disconnected ? null : Pad, Controls.GamepadInputButtons.Confirm, disconnected);
                Check(gated.ConfirmReleased && state.SuppressesClick(At(21)), "Gating/disconnecting held A discarded its release tail.");
                state.Sample(At(30), disconnected ? null : Pad, Controls.GamepadInputButtons.Confirm, disconnected);
                Check(state.SuppressesClick(At(139)), "Repeated gated samples cleared the existing click tail.");
                Check(!state.SuppressesClick(At(140)), "Repeated gated samples kept extending the release tail.");
            }

            var held = Connected();
            Read(held, 10, Controls.GamepadInputButtons.Confirm);
            Read(held, 2000, Controls.GamepadInputButtons.Confirm);
            Check(held.SuppressesClick(At(2000)), "A long held A outlived raw click suppression.");
            Read(held, 2010, Controls.GamepadInputButtons.None);
            Check(held.SuppressesClick(At(2129)) && !held.SuppressesClick(At(2130)), "A long held A did not start a fresh tail on release.");

            var baseline = new Controls.GamepadInputState();
            Read(baseline, 0, Controls.GamepadInputButtons.Confirm);
            Check(baseline.SuppressesClick(At(1000)), "An initially held baseline A leaked a native Click before release.");
            Quiet(Read(baseline, 1010, Controls.GamepadInputButtons.None), "Releasing a baseline A invented an accepted raw action.");
            Check(baseline.SuppressesClick(At(1129)) && !baseline.SuppressesClick(At(1130)), "A baseline A release leaked its delayed native Click.");
            baseline.Reset();
            Check(!baseline.SuppressesClick(At(0)), "Reset retained raw click suppression.");

            ++_assertions;
            try { baseline.SuppressesClick(At(-1)); throw new InvalidOperationException("Negative click clock was accepted."); }
            catch (ArgumentOutOfRangeException) { }
        }

        private static void ClockAndReset()
        {
            var state = Connected();
            Read(state, 10, Controls.GamepadInputButtons.Confirm);
            ++_assertions;
            try { Read(state, 9, Controls.GamepadInputButtons.None); throw new InvalidOperationException("A clock regression was accepted."); }
            catch (ArgumentOutOfRangeException) { }
            state.Reset();
            Check(!state.ConfirmHeld, "Reset retained held A.");
            Quiet(Read(state, 0, Controls.GamepadInputButtons.None), "Reset did not allow a fresh monotonic clock origin.");
            Check(Read(state, 10, Controls.GamepadInputButtons.Confirm).ConfirmPressed, "Reset retained a stale baseline or confirm state.");
            ++_assertions;
            try { Read(new Controls.GamepadInputState(), -1, Controls.GamepadInputButtons.None); throw new InvalidOperationException("Negative elapsed time was accepted."); }
            catch (ArgumentOutOfRangeException) { }
        }
    }
}
