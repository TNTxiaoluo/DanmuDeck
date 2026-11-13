using System;
using BiliXbox.Controls;
using Windows.System;
using Windows.UI.Xaml.Input;

namespace BiliXbox.GamepadRoutingTests
{
    public static class Tests
    {
        private static int _assertions;
        private static readonly object Device = new object();
        private static readonly DateTimeOffset Epoch = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        private static void Check(bool value, string message)
        {
            ++_assertions;
            if (!value) throw new InvalidOperationException(message);
        }

        private static KeyRoutedEventArgs Event(VirtualKey mapped, VirtualKey original)
            => new KeyRoutedEventArgs { Key = mapped, OriginalKey = original };

        private static GamepadInputFrame Read(GamepadInputState state, int milliseconds,
            GamepadInputButtons buttons, bool enabled = true)
            => state.Sample(TimeSpan.FromMilliseconds(milliseconds), Device, buttons, enabled);

        public static void Run()
        {
            _assertions = 0;
            MappedDirectionsRetainTheirOrigin();
            MappedConfirmationAndKeyboardAreDistinct();
            OnePressAcrossRawCoreAndXaml();
            EarlyNativeRepeatCannotAccelerateRawOwner();
            ConfirmationHasOneActionAcrossRepresentations();
            GatedSamplerLeavesNativeInputUnclaimed();
            Console.WriteLine("All " + _assertions + " mapped-gamepad routing assertions passed. " +
                "WinRT event types are stubs; UI gates, native delivery, and Xbox focus require device testing.");
        }

        private static void MappedDirectionsRetainTheirOrigin()
        {
            var arrows = new[] { VirtualKey.Left, VirtualKey.Right, VirtualKey.Up, VirtualKey.Down };
            var dpad = new[] { VirtualKey.GamepadDPadLeft, VirtualKey.GamepadDPadRight, VirtualKey.GamepadDPadUp, VirtualKey.GamepadDPadDown };
            var sticks = new[] { VirtualKey.GamepadLeftThumbstickLeft, VirtualKey.GamepadLeftThumbstickRight, VirtualKey.GamepadLeftThumbstickUp, VirtualKey.GamepadLeftThumbstickDown };
            var navigation = new[] { VirtualKey.NavigationLeft, VirtualKey.NavigationRight, VirtualKey.NavigationUp, VirtualKey.NavigationDown };
            for (int index = 0; index < arrows.Length; ++index)
            {
                foreach (var origin in new[] { dpad[index], sticks[index], navigation[index] })
                {
                    Check(GamepadKeyRouting.IsNavigationKey(origin), "Core key not recognized: " + origin);
                    Check(GamepadKeyRouting.IsNavigationEvent(Event(arrows[index], origin)),
                        "Mapped arrow leaked through the raw owner: " + origin);
                    Check(GamepadKeyRouting.IsNavigationEvent(Event(origin, origin)),
                        "An unmapped controller event was not recognized: " + origin);
                    Check(GamepadKeyRouting.IsNavigationEvent(Event(origin, VirtualKey.None)),
                        "Key-only controller event was not recognized: " + origin);
                }
                Check(!GamepadKeyRouting.IsNavigationKey(arrows[index]), "Physical arrow was classified as a Core gamepad key.");
                Check(!GamepadKeyRouting.IsNavigationEvent(Event(arrows[index], arrows[index])),
                    "Physical keyboard arrow was swallowed: " + arrows[index]);
            }
        }

        private static void MappedConfirmationAndKeyboardAreDistinct()
        {
            foreach (var origin in new[] { VirtualKey.GamepadA, VirtualKey.NavigationAccept })
            {
                Check(GamepadKeyRouting.IsNavigationKey(origin), "Core confirmation was not recognized.");
                foreach (var mapped in new[] { VirtualKey.Space, VirtualKey.Enter, origin })
                    Check(GamepadKeyRouting.IsNavigationEvent(Event(mapped, origin)),
                        "Mapped controller confirmation leaked: " + origin + " => " + mapped);
            }
            foreach (var keyboard in new[] { VirtualKey.Space, VirtualKey.Enter, VirtualKey.Escape, VirtualKey.A, VirtualKey.Y })
            {
                Check(!GamepadKeyRouting.IsNavigationKey(keyboard), "Physical keyboard key was classified as raw navigation.");
                Check(!GamepadKeyRouting.IsNavigationEvent(Event(keyboard, keyboard)),
                    "Physical keyboard key did not retain its existing handler: " + keyboard);
            }
            foreach (var separateShortcut in new[] { VirtualKey.GamepadB, VirtualKey.GamepadX, VirtualKey.GamepadY, VirtualKey.NavigationCancel })
                Check(!GamepadKeyRouting.IsNavigationEvent(Event(separateShortcut, separateShortcut)),
                    "A separate back/search shortcut was consumed by the raw navigation filter.");
        }

        // A protocol simulation, not a copy of MainPage's UI gates: the fixture
        // starts with raw ownership already granted. The production classifier
        // decides whether a mapped event could add a second state transition.
        private static void DeliverRawDirection(GamepadInputFrame frame, PlayerInputState player)
        {
            if (frame.Direction.HasValue) player.Navigate(frame.Direction.Value, 64);
        }

        private static void ObserveOwnedCoreAndMapped(VirtualKey core, KeyRoutedEventArgs mapped,
            PlayerDirection direction, PlayerInputState player)
        {
            Check(GamepadKeyRouting.IsNavigationKey(core), "Owned Core event escaped classification.");
            bool consumed = GamepadKeyRouting.IsNavigationEvent(mapped);
            if (!consumed) player.Navigate(direction, 64);
            Check(consumed, "Mapped XAML event added a duplicate transition after raw dispatch.");
        }

        private static void OnePressAcrossRawCoreAndXaml()
        {
            var raw = new GamepadInputState();
            var player = new PlayerInputState();
            player.SelectButton(0, 64);
            Read(raw, 0, GamepadInputButtons.None);
            DeliverRawDirection(Read(raw, 10, GamepadInputButtons.Right), player);
            ObserveOwnedCoreAndMapped(VirtualKey.GamepadDPadRight,
                Event(VirtualKey.Right, VirtualKey.GamepadDPadRight), PlayerDirection.Right, player);
            foreach (int milliseconds in new[] { 20, 50, 100, 150, 200, 300, 400, 500, 559 })
            {
                DeliverRawDirection(Read(raw, milliseconds, GamepadInputButtons.Right), player);
                ObserveOwnedCoreAndMapped(VirtualKey.GamepadDPadRight,
                    Event(VirtualKey.Right, VirtualKey.GamepadDPadRight), PlayerDirection.Right, player);
                Check(player.ButtonIndex == 1, "One press or an early OS repeat advanced more than one target.");
            }
            var released = Read(raw, 560, GamepadInputButtons.None);
            DeliverRawDirection(released, player);
            Check(!released.Direction.HasValue && player.ButtonIndex == 1, "Releasing direction moved a target.");
        }

        private static void EarlyNativeRepeatCannotAccelerateRawOwner()
        {
            var raw = new GamepadInputState();
            var player = new PlayerInputState();
            player.SelectButton(0, 64);
            Read(raw, 0, GamepadInputButtons.None);
            int actions = 0;
            for (int milliseconds = 10; milliseconds <= 1100; ++milliseconds)
            {
                var frame = Read(raw, milliseconds, GamepadInputButtons.Right);
                if (frame.Direction.HasValue)
                {
                    ++actions;
                    DeliverRawDirection(frame, player);
                    int expectedTime = actions == 1 ? 10 : 560 + (actions - 2) * 220;
                    Check(milliseconds == expectedTime, "Raw movement did not respect the 550/220 ms hold schedule.");
                    Check(frame.IsRepeat == (actions > 1), "Initial press and held repeat were confused.");
                }
                if (milliseconds % 25 == 0)
                    ObserveOwnedCoreAndMapped(VirtualKey.GamepadDPadRight,
                        Event(VirtualKey.Right, VirtualKey.GamepadDPadRight), PlayerDirection.Right, player);
                if (milliseconds == 559) Check(actions == 1, "Movement repeated before its hold delay.");
                if (milliseconds == 779) Check(actions == 2, "Movement repeated before the 220 ms interval.");
            }
            Check(actions == 4 && player.ButtonIndex == 4, "Native repeats accelerated raw-owned selection.");
            Read(raw, 1101, GamepadInputButtons.None);
            foreach (int milliseconds in new[] { 1200, 2000, 10000 })
                Check(!Read(raw, milliseconds, GamepadInputButtons.None).Direction.HasValue,
                    "Direction kept repeating after release.");
        }

        private static void ConfirmationHasOneActionAcrossRepresentations()
        {
            var raw = new GamepadInputState();
            var player = new PlayerInputState();
            Read(raw, 0, GamepadInputButtons.None);
            int activations = 0;
            foreach (int milliseconds in new[] { 10, 20, 50, 100, 200, 600 })
            {
                var frame = Read(raw, milliseconds, GamepadInputButtons.Confirm);
                if (frame.ConfirmPressed && player.ConfirmKeyDown(Epoch.AddMilliseconds(milliseconds), false)) ++activations;
                bool consumed = GamepadKeyRouting.IsNavigationEvent(Event(VirtualKey.Space, VirtualKey.GamepadA));
                if (!consumed && player.ConfirmKeyDown(Epoch.AddMilliseconds(milliseconds), false)) ++activations;
                Check(consumed && activations == 1, "Mapped Space confirmed a raw-owned A twice.");
                Check(!player.AcceptClick(Epoch.AddMilliseconds(milliseconds)) && raw.SuppressesClick(TimeSpan.FromMilliseconds(milliseconds)),
                    "A held confirmation leaked its native Click.");
            }
            var release = Read(raw, 601, GamepadInputButtons.None);
            if (release.ConfirmReleased) player.ConfirmKeyUp(Epoch.AddMilliseconds(601));
            Check(release.ConfirmReleased && raw.SuppressesClick(TimeSpan.FromMilliseconds(720)),
                "Release-generated native Click escaped its suppression tail.");
            Check(player.AcceptClick(Epoch.AddMilliseconds(721)) && !raw.SuppressesClick(TimeSpan.FromMilliseconds(721)),
                "An independent click stayed suppressed past the release tail.");
            Check(activations == 1, "One physical A performed more than one action.");
        }

        private static void GatedSamplerLeavesNativeInputUnclaimed()
        {
            var raw = new GamepadInputState();
            Read(raw, 0, GamepadInputButtons.None);
            var pressed = Read(raw, 10, GamepadInputButtons.Right | GamepadInputButtons.Confirm);
            Check(pressed.Direction.HasValue && pressed.ConfirmPressed, "Fixture did not start an owned direction and A.");
            var gated = Read(raw, 20, GamepadInputButtons.Right | GamepadInputButtons.Confirm, false);
            Check(!gated.Direction.HasValue && !gated.ConfirmPressed && gated.ConfirmReleased,
                "Entering a native-input gate did not release owned confirmation and stop movement.");
            foreach (int milliseconds in new[] { 30, 100, 1000 })
            {
                var frame = Read(raw, milliseconds, GamepadInputButtons.Right | GamepadInputButtons.Confirm, false);
                Check(!frame.Direction.HasValue && !frame.ConfirmPressed && !frame.ConfirmReleased,
                    "Disabled sampling dispatched an action that belongs to native input.");
            }
            var resumedHeld = Read(raw, 1001, GamepadInputButtons.Right | GamepadInputButtons.Confirm);
            Check(!resumedHeld.Direction.HasValue && !resumedHeld.ConfirmPressed,
                "Closing native input turned its held button into a new application action.");
            Check(!Read(raw, 2000, GamepadInputButtons.Right | GamepadInputButtons.Confirm).Direction.HasValue,
                "A held native-input direction repeated after resuming without release.");
            Read(raw, 2001, GamepadInputButtons.None);
            var newPress = Read(raw, 2002, GamepadInputButtons.Right);
            Check(newPress.Direction == PlayerDirection.Right && !newPress.IsRepeat,
                "Fresh navigation did not resume after native input and neutral release.");
            // The classifier intentionally has no UI state. MainPage must return
            // before calling it when a dialog/editor owns input. This test does
            // not claim to execute those UI handlers or verify OS keyboard focus.
        }
    }
}
