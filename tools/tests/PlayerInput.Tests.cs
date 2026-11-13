namespace BiliXbox.PlayerInputTests
{
    public static class Tests
    {
        private static int _assertions;
        private static readonly DateTimeOffset Epoch =
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private static DateTimeOffset At(int milliseconds) => Epoch.AddMilliseconds(milliseconds);
        private static void Check(bool condition, string message)
        {
            ++_assertions;
            if (!condition) throw new InvalidOperationException(message);
        }
        private static void Throws<T>(Action action, string message) where T : Exception
        {
            ++_assertions;
            try { action(); }
            catch (T) { return; }
            throw new InvalidOperationException(message);
        }

        public static void Run()
        {
            _assertions = 0;
            FirstDirectionAndVisualOrder();
            SeekAndReturn();
            HeaderAndIndependentSelection();
            CreatorHeaderEntry();
            PlaybackCancelsSelection();
            ConfirmationDelivery();
            FastSeparatePressesAndHeldMotion();
            ReentryAndValidation();
            Console.WriteLine("All " + _assertions + " player input assertions passed. Xbox hardware and native event delivery require device testing.");
        }

        private static void FirstDirectionAndVisualOrder()
        {
            foreach (Controls.PlayerDirection direction in Enum.GetValues(typeof(Controls.PlayerDirection)))
            {
                var state = new Controls.PlayerInputState();
                Check(state.Mode == Controls.PlayerInputMode.Playback, "New playback started with stale controller selection.");
                var first = state.Navigate(direction, 9);
                Check(first.EnteredControls && first.Row == Controls.PlayerFocusRow.Buttons && first.ButtonIndex == 0 && first.SeekSeconds == 0,
                    "The first " + direction + " both woke controls and moved/seeked.");
                for (int i = 1; i < 9; ++i)
                {
                    var next = state.Navigate(Controls.PlayerDirection.Right, 9);
                    Check(!next.EnteredControls && next.ButtonIndex == i && next.Row == Controls.PlayerFocusRow.Buttons,
                        "Right did not follow the visual bottom-row order at index " + i + ".");
                }
                Check(state.Navigate(Controls.PlayerDirection.Right, 9).ButtonIndex == 8, "Right wrapped into an unrelated target.");
                for (int i = 7; i >= 0; --i)
                    Check(state.Navigate(Controls.PlayerDirection.Left, 9).ButtonIndex == i, "Left skipped a bottom-row action.");
                Check(state.Navigate(Controls.PlayerDirection.Left, 9).ButtonIndex == 0, "Left escaped the first action.");
                Check(state.Navigate(Controls.PlayerDirection.Down, 9).ButtonIndex == 0 && state.Row == Controls.PlayerFocusRow.Buttons,
                    "Down escaped the bottom row.");
            }
        }

        private static void SeekAndReturn()
        {
            var state = new Controls.PlayerInputState();
            state.Navigate(Controls.PlayerDirection.Right, 9);
            for (int i = 0; i < 6; ++i) state.Navigate(Controls.PlayerDirection.Right, 9);
            var up = state.Navigate(Controls.PlayerDirection.Up, 9);
            Check(up.Row == Controls.PlayerFocusRow.Seek && up.ButtonIndex == 6 && up.SeekSeconds == 0, "Up lost the selected settings action.");
            Check(state.Navigate(Controls.PlayerDirection.Right, 9).SeekSeconds == 10, "Seek right did not request ten seconds.");
            Check(state.Navigate(Controls.PlayerDirection.Right, 9).SeekSeconds == 10, "A repeated seek direction was dropped.");
            Check(state.Navigate(Controls.PlayerDirection.Left, 9).SeekSeconds == -10, "Seek left did not request minus ten seconds.");
            Check(state.Navigate(Controls.PlayerDirection.Up, 9).Row == Controls.PlayerFocusRow.Header, "Up from seek did not reach the header actions.");
            Check(state.Navigate(Controls.PlayerDirection.Down, 9).Row == Controls.PlayerFocusRow.Seek, "Down from header did not return to seek.");
            var down = state.Navigate(Controls.PlayerDirection.Down, 9);
            Check(down.Row == Controls.PlayerFocusRow.Buttons && down.ButtonIndex == 6 && down.SeekSeconds == 0,
                "Returning from seek did not restore the prior bottom action.");
            Check(state.Navigate(Controls.PlayerDirection.Right, 9).ButtonIndex == 7, "Navigation after seek resumed at the wrong action.");
            state.SelectButton(5, 9);
            Check(state.Row == Controls.PlayerFocusRow.Buttons && state.ButtonIndex == 5, "A closed drawer could not restore its invoking action.");
        }

        private static void HeaderAndIndependentSelection()
        {
            var state = new Controls.PlayerInputState();
            state.SelectButton(6, 9);
            state.Navigate(Controls.PlayerDirection.Up, 9);
            var header = state.Navigate(Controls.PlayerDirection.Up, 9);
            Check(header.Row == Controls.PlayerFocusRow.Header && header.HeaderIndex == 0 && state.HeaderIndex == 0,
                "The first header action was not the back button.");
            Check(header.ButtonIndex == 6 && header.SeekSeconds == 0, "Entering the header changed the remembered bottom action or seeked.");
            Check(state.Navigate(Controls.PlayerDirection.Left, 9).HeaderIndex == 0, "Header left escaped its first action.");
            Check(state.Navigate(Controls.PlayerDirection.Right, 9).HeaderIndex == 1, "Header search was not reachable with right.");
            Check(state.Navigate(Controls.PlayerDirection.Right, 9).HeaderIndex == 2, "Header account was not reachable with right.");
            Check(state.Navigate(Controls.PlayerDirection.Right, 9).HeaderIndex == 2, "Header right escaped its final action.");
            Check(state.Navigate(Controls.PlayerDirection.Up, 9).Row == Controls.PlayerFocusRow.Header, "Header up escaped the visible controls.");
            Check(state.ButtonIndex == 6, "Moving across header actions changed the remembered bottom action.");
            Check(state.Navigate(Controls.PlayerDirection.Left, 9).HeaderIndex == 1, "Header left skipped search.");
            Check(state.Navigate(Controls.PlayerDirection.Down, 9).Row == Controls.PlayerFocusRow.Seek, "Header down skipped the seek row.");
            var bottom = state.Navigate(Controls.PlayerDirection.Down, 9);
            Check(bottom.Row == Controls.PlayerFocusRow.Buttons && bottom.ButtonIndex == 6 && bottom.SeekSeconds == 0,
                "Returning from header lost the original settings action.");
            state.Navigate(Controls.PlayerDirection.Up, 9);
            Check(state.Navigate(Controls.PlayerDirection.Up, 9).HeaderIndex == 0, "Re-entering the header retained an invisible selection.");
            state.SelectButton(5, 9);
            Check(state.Row == Controls.PlayerFocusRow.Buttons && state.ButtonIndex == 5, "Selecting a drawer's invoking action left header mode active.");
            state.Navigate(Controls.PlayerDirection.Up, 9);
            state.Navigate(Controls.PlayerDirection.Up, 9);
            state.Reset();
            Check(state.Mode == Controls.PlayerInputMode.Playback && state.Row == Controls.PlayerFocusRow.Buttons && state.HeaderIndex == 0,
                "A new video retained a previous header focus.");
        }

        private static void CreatorHeaderEntry()
        {
            var state = new Controls.PlayerInputState();
            state.Navigate(Controls.PlayerDirection.Up, 10, 4);
            state.Navigate(Controls.PlayerDirection.Up, 10, 4);
            state.Navigate(Controls.PlayerDirection.Up, 10, 4);
            Check(state.Row == Controls.PlayerFocusRow.Header, "Creator header was unreachable from the player row.");
            Check(state.Navigate(Controls.PlayerDirection.Right, 10, 4).HeaderIndex == 1,
                "The new uploader entry was skipped in header navigation.");
            state.Navigate(Controls.PlayerDirection.Right, 10, 4);
            Check(state.Navigate(Controls.PlayerDirection.Right, 10, 4).HeaderIndex == 3,
                "The account action became unreachable after adding the uploader entry.");
            Check(state.Navigate(Controls.PlayerDirection.Right, 10, 4).HeaderIndex == 3,
                "Header navigation escaped its final control.");
            Check(state.Navigate(Controls.PlayerDirection.Up, 10, 2).HeaderIndex == 1,
                "Shrinking a header left a stale out-of-range focus index.");
            Throws<ArgumentOutOfRangeException>(() => state.Navigate(Controls.PlayerDirection.Right, 10, 0),
                "An empty header was accepted.");
        }

        private static void PlaybackCancelsSelection()
        {
            var state = new Controls.PlayerInputState();
            state.SelectButton(7, 9);
            state.UsePlayback();
            Check(state.Mode == Controls.PlayerInputMode.Playback, "Returning to playback left controller selection active.");
            Check(state.AcceptClick(At(0)), "A pointer click without a key press was suppressed.");
            Check(state.AcceptClick(At(20)), "Two independent mouse clicks were time-debounced.");
            var restart = state.Navigate(Controls.PlayerDirection.Left, 9);
            Check(restart.EnteredControls && restart.ButtonIndex == 0, "Direction after returning to playback retained a stale selected action.");
            state.Navigate(Controls.PlayerDirection.Up, 9);
            state.UsePlayback();
            var afterSeek = state.Navigate(Controls.PlayerDirection.Right, 9);
            Check(afterSeek.Row == Controls.PlayerFocusRow.Buttons && afterSeek.ButtonIndex == 0 && afterSeek.SeekSeconds == 0,
                "Returning from playback issued an invisible seek.");
        }

        private static void ConfirmationDelivery()
        {
            var state = new Controls.PlayerInputState();
            Check(state.ConfirmKeyDown(At(0), false), "Initial physical A did not activate.");
            Check(state.ConfirmHeld, "A manually dispatched confirmation did not expose its held state to a newly opened drawer.");
            Check(!state.ConfirmKeyDown(At(0), false), "Core and routed delivery activated the same A twice.");
            Check(!state.AcceptClick(At(1)), "A generated XAML Click repeated manual A dispatch.");
            Check(!state.ConfirmKeyDown(At(500), true), "Holding A repeated activation.");
            Check(!state.AcceptClick(At(1000)), "A late generated Click activated while A was still held.");
            state.ConfirmKeyUp(At(1000));
            Check(!state.ConfirmHeld, "A confirmation held through drawer opening was not released.");
            Check(!state.AcceptClick(At(1001)), "Release-generated Click repeated the confirmation.");
            Check(!state.AcceptClick(At(1119)), "Release-generated Click escaped the suppression tail.");
            Check(state.AcceptClick(At(1120)), "An independent pointer click remained disabled after release.");
            state.ConfirmKeyUp(At(1200));
            Check(state.AcceptClick(At(1200)), "Duplicate KeyUp extended click suppression.");
            state.Reset();
            Check(!state.ConfirmKeyDown(At(2000), true), "An orphan repeat from before playback activated a control.");
            Check(!state.ConfirmKeyDown(At(2001), false), "A second delivery after an orphan repeat activated a held key.");
            state.ConfirmKeyUp(At(2010));
            Check(state.ConfirmKeyDown(At(2020), false), "A new physical A did not recover after an orphan repeat.");
        }

        private static void FastSeparatePressesAndHeldMotion()
        {
            var state = new Controls.PlayerInputState();
            int actions = 0;
            if (state.ConfirmKeyDown(At(0), false)) ++actions;
            state.ConfirmKeyUp(At(20));
            if (state.ConfirmKeyDown(At(40), false)) ++actions;
            state.SelectButton(5, 9);
            state.UsePlayback();
            Check(!state.ConfirmKeyDown(At(45), false), "Returning to playback during one A converted it to another confirmation.");
            Check(state.Mode == Controls.PlayerInputMode.Playback, "The A latch forced controller mode back on.");
            state.ConfirmKeyUp(At(60));
            if (state.ConfirmKeyDown(At(80), false)) ++actions;
            Check(actions == 3, "Time debounce dropped distinct fast A presses.");
            state.Reset();
            Check(state.Mode == Controls.PlayerInputMode.Playback && state.ButtonIndex == 0 && state.Row == Controls.PlayerFocusRow.Buttons,
                "Opening another video retained prior input state.");
            Check(state.AcceptClick(At(81)), "Opening another video inherited click suppression.");
            Check(state.ConfirmKeyDown(At(82), false), "Opening another video inherited held A.");
        }

        private static void ReentryAndValidation()
        {
            var state = new Controls.PlayerInputState();
            state.SelectButton(8, 9);
            Check(state.Navigate(Controls.PlayerDirection.Left, 6).ButtonIndex == 4,
                "Shrinking the visible/enabled action list produced an invalid focus index.");
            state.SelectButton(0, 1);
            Check(state.Navigate(Controls.PlayerDirection.Right, 1).ButtonIndex == 0,
                "Single-action row lost its only control.");
            Throws<ArgumentOutOfRangeException>(() => state.Navigate(Controls.PlayerDirection.Left, 0), "An empty target list was accepted.");
            Throws<ArgumentOutOfRangeException>(() => state.Navigate((Controls.PlayerDirection)99, 9), "An invalid direction was accepted.");
            Throws<ArgumentOutOfRangeException>(() => state.SelectButton(-1, 9), "A negative focus index was accepted.");
            Throws<ArgumentOutOfRangeException>(() => state.SelectButton(9, 9), "An out-of-range focus index was accepted.");
        }
    }
}
