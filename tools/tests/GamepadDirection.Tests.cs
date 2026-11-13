using System;
using BiliXbox.Controls;
using Buttons = BiliXbox.Controls.GamepadInputButtons;

namespace BiliXbox.GamepadDirectionTests
{
    public static class Tests
    {
        private static int _assertions;
        private static void Check(bool condition, string message)
        {
            ++_assertions;
            if (!condition) throw new InvalidOperationException(message);
        }

        public static void Run()
        {
            _assertions = 0;
            CardinalThresholds();
            CenterAndDrift();
            DiagonalsAndAxisLock();
            DpadPriorityAndFlagFiltering();
            InvalidReadingsAndReset();
            RepeatAndPlayerIntegration();
            GatedHeldStick();
            Console.WriteLine("All " + _assertions + " stick direction assertions passed. Physical Xbox input and XAML focus still require device testing.");
        }

        private static void CardinalThresholds()
        {
            foreach (var expected in new[] { Buttons.Up, Buttons.Down, Buttons.Left, Buttons.Right })
            {
                double x = expected == Buttons.Left ? -1 : expected == Buttons.Right ? 1 : 0;
                double y = expected == Buttons.Down ? -1 : expected == Buttons.Up ? 1 : 0;
                var mapper = new GamepadDirectionMapper();
                Check(mapper.Map(Buttons.None, x * .549, y * .549) == Buttons.None, "A fresh stick engaged below .55.");
                Check(mapper.Map(Buttons.None, x * .55, y * .55) == expected, "Stick did not engage " + expected + " at .55.");
                Check(mapper.Map(Buttons.None, x * .4, y * .4) == expected, "Stick jitter below activation prematurely released " + expected + ".");
                Check(mapper.Map(Buttons.None, x * .351, y * .351) == expected, "Stick released above its .35 stop threshold.");
                Check(mapper.Map(Buttons.None, x * .35, y * .35) == Buttons.None, "Stick did not release exactly at .35.");
                Check(mapper.Map(Buttons.None, x * .54, y * .54) == Buttons.None, "A released stick re-engaged without reaching .55.");
                Check(mapper.Map(Buttons.None, x * .55, y * .55) == expected, "A separately moved stick did not re-engage.");
                Check(mapper.Map(Buttons.None, -x * .4, -y * .4) == Buttons.None, "Crossing center carried the old direction into its opposite sign.");
                Check(mapper.Map(Buttons.None, -x * .55, -y * .55) == Opposite(expected), "A distinct opposite deflection was not recognized.");
                Check(mapper.Map(Buttons.None, 0, 0) == Buttons.None, "Centered stick retained a direction.");
            }
        }

        private static Buttons Opposite(Buttons direction)
        {
            switch (direction)
            {
                case Buttons.Up: return Buttons.Down;
                case Buttons.Down: return Buttons.Up;
                case Buttons.Left: return Buttons.Right;
                default: return Buttons.Left;
            }
        }

        private static void CenterAndDrift()
        {
            var mapper = new GamepadDirectionMapper();
            for (int x = -17; x <= 17; x++)
                for (int y = -17; y <= 17; y++)
                {
                    Check(mapper.Map(Buttons.None, x * .02, y * .02) == Buttons.None, "Centered stick drift caused navigation.");
                    mapper.Map(Buttons.None, 1, 0);
                    Check(mapper.Map(Buttons.None, x * .02, y * .02) == Buttons.None, "Returning into the center did not release a held axis.");
                }
            Check(mapper.Map(Buttons.None, .54, .54) == Buttons.None, "A diagonal with neither axis engaged bypassed the activation threshold.");
        }

        private static void DiagonalsAndAxisLock()
        {
            var mapper = new GamepadDirectionMapper();
            Check(mapper.Map(Buttons.None, .8, .8) == Buttons.Up, "A first diagonal tie did not choose the vertical axis.");
            Check(mapper.Map(Buttons.None, .81, .79) == Buttons.Up, "Near-diagonal jitter changed the selected axis.");
            Check(mapper.Map(Buttons.None, .96, .6) == Buttons.Right, "A deliberate strong axis change was ignored.");
            Check(mapper.Map(Buttons.None, .79, .8) == Buttons.Right, "A diagonal retained no horizontal hysteresis.");
            Check(mapper.Map(Buttons.None, .65, .81) == Buttons.Up, "The new vertical axis did not win beyond the switch margin.");
            Check(mapper.Map(Buttons.None, .6, -.9) == Buttons.Down, "A deliberate vertical sign change was ignored.");
            Check(mapper.Map(Buttons.None, -.95, -.6) == Buttons.Left, "A deliberate horizontal sign/axis change was ignored.");
            mapper.Reset();
            Check(mapper.Map(Buttons.None, -.8, -.8) == Buttons.Down, "A negative diagonal tie selected the wrong sign or axis.");
            mapper.Reset();
            Check(mapper.Map(Buttons.None, .9, .6) == Buttons.Right, "Initial horizontal dominance was ignored.");
            Check(mapper.Map(Buttons.None, .4, .54) == Buttons.Right, "Another axis below .55 stole an engaged direction.");
        }

        private static void DpadPriorityAndFlagFiltering()
        {
            var mapper = new GamepadDirectionMapper();
            Check(mapper.Map(Buttons.None, 1, 0) == Buttons.Right, "Test setup did not engage the stick.");
            Check(mapper.Map(Buttons.Up | Buttons.Confirm | (Buttons)1024, -1, -1) == Buttons.Up,
                "D-pad was overridden or non-direction flags escaped into the mapped result.");
            Check(mapper.Map(Buttons.None, .4, 0) == Buttons.None, "D-pad release restored a stale stick direction below activation.");
            Check(mapper.Map(Buttons.Left | Buttons.Up, 1, -1) == (Buttons.Left | Buttons.Up), "D-pad diagonal flags were changed before sampler conflict resolution.");
            Check(mapper.Map(Buttons.Left | Buttons.Right, -1, 0) == (Buttons.Left | Buttons.Right), "Opposing D-pad flags bypassed the sampler's conflict rules.");
            Check(mapper.Map(Buttons.Down, double.NaN, double.PositiveInfinity) == Buttons.Down, "Invalid stick axes prevented an independent valid D-pad input.");
            Check(mapper.Map(Buttons.Confirm | (Buttons)1024, 0, 0) == Buttons.None, "Confirm or unknown flags became directions.");
        }

        private static void InvalidReadingsAndReset()
        {
            var mapper = new GamepadDirectionMapper();
            foreach (double invalid in new[] { double.NaN, double.NegativeInfinity, double.PositiveInfinity })
            {
                mapper.Map(Buttons.None, 1, 0);
                Check(mapper.Map(Buttons.None, invalid, 0) == Buttons.None, "An invalid X reading retained navigation.");
                Check(mapper.Map(Buttons.None, .4, 0) == Buttons.None, "An invalid X reading retained stick hysteresis.");
                mapper.Map(Buttons.None, 0, 1);
                Check(mapper.Map(Buttons.None, 0, invalid) == Buttons.None, "An invalid Y reading retained navigation.");
                Check(mapper.Map(Buttons.None, 0, .4) == Buttons.None, "An invalid Y reading retained stick hysteresis.");
            }
            Check(mapper.Map(Buttons.None, 2, .8) == Buttons.Right, "Out-of-range finite values were not clamped to valid axis bounds.");
            mapper.Reset();
            Check(mapper.Map(Buttons.None, .4, 0) == Buttons.None, "Reset retained an engaged direction.");
            Check(mapper.Map(Buttons.None, 0, -.8) == Buttons.Down, "Reset prevented a new deliberate engagement.");
        }

        private static void RepeatAndPlayerIntegration()
        {
            var mapper = new GamepadDirectionMapper();
            var sampler = new GamepadInputState();
            var player = new PlayerInputState();
            var pad = new object();
            sampler.Sample(TimeSpan.Zero, pad, mapper.Map(Buttons.None, 0, 0), true);
            var frame = sampler.Sample(TimeSpan.FromMilliseconds(10), pad, mapper.Map(Buttons.None, .8, 0), true);
            Check(frame.Direction == PlayerDirection.Right && !frame.IsRepeat, "The initial stick engagement emitted no fresh direction.");
            var navigation = player.Navigate(frame.Direction.Value, 9);
            Check(navigation.ButtonIndex == 0 && navigation.EnteredControls, "Initial stick navigation skipped the first visible player action.");
            frame = sampler.Sample(TimeSpan.FromMilliseconds(100), pad, mapper.Map(Buttons.None, .4, 0), true);
            Check(!frame.Direction.HasValue, "Axis hysteresis generated a second press before the hold delay.");
            frame = sampler.Sample(TimeSpan.FromMilliseconds(560), pad, mapper.Map(Buttons.None, .4, 0), true);
            Check(frame.Direction == PlayerDirection.Right && frame.IsRepeat, "A held stick did not use the physical D-pad repeat cadence.");
            player.Navigate(frame.Direction.Value, 9);
            Check(player.ButtonIndex == 1, "A stick repeat did not advance exactly one player action.");
            frame = sampler.Sample(TimeSpan.FromMilliseconds(570), pad, mapper.Map(Buttons.Up, 1, 0), true);
            Check(frame.Direction == PlayerDirection.Up && !frame.IsRepeat, "D-pad did not take over from a held stick immediately.");
            Check(player.Navigate(frame.Direction.Value, 9).Row == PlayerFocusRow.Seek, "D-pad takeover did not enter the seek row.");
            frame = sampler.Sample(TimeSpan.FromMilliseconds(580), pad, mapper.Map(Buttons.None, .4, 0), true);
            Check(!frame.Direction.HasValue, "D-pad release revived a below-threshold stick.");
            frame = sampler.Sample(TimeSpan.FromMilliseconds(590), pad, mapper.Map(Buttons.None, .8, 0) | Buttons.Confirm, true);
            Check(frame.Direction == PlayerDirection.Right && frame.ConfirmPressed, "Mapping directions lost an independent A press.");
        }

        private static void GatedHeldStick()
        {
            var mapper = new GamepadDirectionMapper();
            var sampler = new GamepadInputState();
            var pad = new object();
            sampler.Sample(TimeSpan.Zero, pad, Buttons.None, true);
            sampler.Sample(TimeSpan.FromMilliseconds(10), pad, mapper.Map(Buttons.None, 1, 0) | Buttons.Confirm, true);
            mapper.Reset();
            var frame = sampler.Sample(TimeSpan.FromMilliseconds(20), pad, Buttons.None, false);
            Check(frame.ConfirmReleased && !frame.Direction.HasValue, "Native-input ownership did not release the existing A latch.");
            frame = sampler.Sample(TimeSpan.FromMilliseconds(30), pad, mapper.Map(Buttons.None, 1, 0) | Buttons.Confirm, true);
            Check(!frame.Direction.HasValue && !frame.ConfirmPressed, "A held stick/A fired while returning from native input.");
            frame = sampler.Sample(TimeSpan.FromMilliseconds(40), pad, mapper.Map(Buttons.None, .4, 0) | Buttons.Confirm, true);
            Check(!frame.Direction.HasValue && !frame.ConfirmPressed, "A stick still outside its center rearmed after native input.");
            sampler.Sample(TimeSpan.FromMilliseconds(50), pad, mapper.Map(Buttons.None, .35, 0), true);
            frame = sampler.Sample(TimeSpan.FromMilliseconds(60), pad, mapper.Map(Buttons.None, .8, 0) | Buttons.Confirm, true);
            Check(frame.Direction == PlayerDirection.Right && frame.ConfirmPressed, "A new stick/A press failed to rearm after returning to neutral.");
        }
    }
}
