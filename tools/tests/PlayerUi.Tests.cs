namespace BiliXbox.PlayerUiTests
{
    public static class Tests
    {
        private static int _assertions;
        private static readonly DateTimeOffset Epoch =
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        private static DateTimeOffset At(double seconds) => Epoch.AddTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond));
        private static void Check(bool condition, string message)
        {
            ++_assertions;
            if (!condition) throw new InvalidOperationException(message);
        }

        public static void Run()
        {
            _assertions = 0;
            IdlePlayingAndActivity();
            PausedAndBlocked();
            DrawerAndBack();
            ReopenAndNewVideo();
            PointerMotionAndStationaryEvents();
            PointerTimeoutAndProtection();
            Console.WriteLine("All " + _assertions +
                " player UI state assertions passed. UWP rendering, native keyboard, and Xbox input require device testing.");
        }

        private static void IdlePlayingAndActivity()
        {
            var state = new Controls.PlayerUiState();
            state.Reset(At(0));
            Check(state.ControlsVisible && state.Drawer == Controls.PlayerDrawer.None,
                "Opening a video did not show its controls with no drawer.");
            Check(!state.CanAutoHide(At(4.999), true, false),
                "The player hid its controls before five seconds of inactivity.");
            Check(state.CanAutoHide(At(5), true, false),
                "The player did not allow hiding at the five-second threshold.");

            // Navigating to another transport action restarts the full idle period.
            state.Touch(At(4));
            Check(!state.CanAutoHide(At(5), true, false),
                "Recent controller activity did not postpone automatic hiding.");
            Check(!state.CanAutoHide(At(8.999), true, false),
                "Touch did not restart the full five-second idle period.");
            Check(state.CanAutoHide(At(9), true, false),
                "Controls never became eligible to hide after activity stopped.");
            state.Hide();
            Check(!state.ControlsVisible, "Eligible controls did not hide.");
            Check(!state.CanAutoHide(At(20), true, false),
                "The timer attempted to re-hide already hidden controls.");

            // Waking a hidden player must not inherit its previous timeout.
            state.Show(At(30));
            Check(state.ControlsVisible, "User input could not reveal hidden controls.");
            Check(!state.CanAutoHide(At(34.999), true, false),
                "Revealed controls inherited the old idle timeout.");
            Check(state.CanAutoHide(At(35), true, false),
                "Revealed controls did not resume normal automatic hiding.");
            Console.WriteLine("PASS: five-second boundary, activity, hidden state, and wake-up.");
        }

        private static void PausedAndBlocked()
        {
            var state = new Controls.PlayerUiState();
            state.Reset(At(0));
            Check(!state.CanAutoHide(At(60), false, false),
                "A paused player allowed its controls to disappear.");
            Check(!state.CanAutoHide(At(60), true, true),
                "Loading, dialog, or seek blocking did not keep controls visible.");
            Check(!state.CanAutoHide(At(60), false, true),
                "A paused and blocked player allowed automatic hiding.");
            Check(state.ControlsVisible,
                "Eligibility checks changed control visibility without an explicit hide.");

            // Returning from an interaction should be followed by Show/Touch.
            state.Touch(At(61));
            Check(!state.CanAutoHide(At(65.999), true, false),
                "Returning from a blocked interaction lost its activity grace period.");
            Check(state.CanAutoHide(At(66), true, false),
                "Automatic hiding did not resume after a blocking interaction ended.");
            Console.WriteLine("PASS: paused playback and blocked loading/dialog/seek eligibility.");
        }

        private static void DrawerAndBack()
        {
            foreach (Controls.PlayerDrawer drawer in new[] {
                Controls.PlayerDrawer.Settings, Controls.PlayerDrawer.Comments })
            {
                var state = new Controls.PlayerUiState();
                state.Reset(At(0));
                state.Hide();
                state.Open(drawer, At(10));
                Check(state.Drawer == drawer && state.ControlsVisible,
                    drawer + " did not open from hidden playback.");
                Check(!state.CanAutoHide(At(100), true, false),
                    drawer + " was hidden by the idle timeout.");
                state.Hide();
                Check(state.ControlsVisible && state.Drawer == drawer,
                    "A direct hide removed controls while " + drawer + " was open.");

                Check(state.Back(At(100)) == Controls.PlayerBackAction.CloseDrawer,
                    "The first B action did not close " + drawer + ".");
                Check(state.Drawer == Controls.PlayerDrawer.None && state.ControlsVisible,
                    "Closing " + drawer + " hid the controls or retained the drawer.");
                Check(!state.CanAutoHide(At(104.999), true, false),
                    "Closing " + drawer + " did not give time to use the transport controls.");
                Check(state.Back(At(101)) == Controls.PlayerBackAction.HideControls,
                    "The second B action did not hide the transport controls.");
                Check(!state.ControlsVisible,
                    "The second B action left the transport controls visible.");
                Check(state.Back(At(102)) == Controls.PlayerBackAction.LeaveVideo,
                    "The third B action did not return navigation to the caller.");
                Check(state.Drawer == Controls.PlayerDrawer.None && !state.ControlsVisible,
                    "Leaving a video reopened UI without user input.");
            }
            Console.WriteLine("PASS: settings/comments drawers, timeout protection, and ordered B actions.");
        }

        private static void ReopenAndNewVideo()
        {
            var state = new Controls.PlayerUiState();
            state.Reset(At(0));
            for (int i = 0; i < 10; ++i)
            {
                var drawer = i % 2 == 0 ? Controls.PlayerDrawer.Settings : Controls.PlayerDrawer.Comments;
                double opened = 10 + i * 20;
                state.Open(drawer, At(opened));
                Check(state.ControlsVisible && state.Drawer == drawer,
                    "Repeated opening lost the requested drawer.");
                state.Close(At(opened + 10));
                Check(state.ControlsVisible && state.Drawer == Controls.PlayerDrawer.None,
                    "Repeated cancellation left a drawer active.");
                Check(!state.CanAutoHide(At(opened + 14.999), true, false),
                    "Repeated cancellation retained a stale timeout.");
                state.Hide();
            }

            state.Open(Controls.PlayerDrawer.Comments, At(300));
            state.Reset(At(400));
            Check(state.ControlsVisible && state.Drawer == Controls.PlayerDrawer.None,
                "Opening the next video inherited the previous video's drawer.");
            Check(!state.CanAutoHide(At(404.999), true, false),
                "Opening the next video inherited a stale idle timeout.");
            Check(state.CanAutoHide(At(405), true, false),
                "The next video's controls did not resume normal idle behavior.");
            Console.WriteLine("PASS: repeated drawer reopen/cancel and video lifecycle reset.");
        }

        private static void PointerMotionAndStationaryEvents()
        {
            var state = new Controls.PlayerUiState();
            state.Reset(At(0));
            Check(state.PointerMoved(100, 100, At(0)), "The initial valid pointer position was ignored.");
            Check(!state.PointerMoved(100, 100, At(2)), "Stationary hit-test events were mistaken for pointer activity.");
            Check(!state.PointerMoved(102.999, 102.999, At(2.5)), "Subthreshold pointer jitter was treated as deliberate motion.");
            Check(state.ShouldHidePointer(At(3), false), "Synthetic pointer motion postponed cursor hiding.");
            Check(state.CanAutoHide(At(5), true, false), "Synthetic pointer motion postponed control hiding.");
            state.Hide();
            Check(!state.PointerMoved(100, 100, At(5.1)) && !state.ControlsVisible,
                "A stationary event after hiding immediately reopened the player controls.");
            Check(!state.PointerMoved(102, 101, At(5.2)) && !state.ControlsVisible,
                "Minor pointer jitter reopened hidden controls.");
            Check(state.PointerMoved(103, 100, At(5.3)) && state.ControlsVisible,
                "A deliberate three-pixel move did not reveal hidden controls.");
            Check(!state.CanAutoHide(At(10.299), true, false) && state.CanAutoHide(At(10.3), true, false),
                "A real pointer movement did not restart the full control idle timeout.");
            Check(!state.ShouldHidePointer(At(8.299), false) && state.ShouldHidePointer(At(8.3), false),
                "A real pointer movement did not restart the cursor idle timeout.");
            Check(state.PointerMoved(103, 97, At(11)), "The vertical three-pixel boundary was ignored.");
            Check(!state.PointerMoved(102, 97, At(11.1)) && !state.PointerMoved(101, 97, At(11.2)),
                "One-pixel drift was incorrectly classified as immediate user movement.");
            Check(state.PointerMoved(100, 97, At(11.3)), "Accumulated real movement never passed the movement threshold.");

            state.Reset(At(20));
            state.Hide();
            Check(!state.PointerMoved(double.NaN, 100, At(21)) && !state.PointerMoved(100, double.PositiveInfinity, At(22)),
                "Nonfinite pointer coordinates were accepted.");
            Check(!state.ControlsVisible && state.ShouldHidePointer(At(23), false),
                "Invalid pointer coordinates woke playback or postponed cursor hiding.");
            Check(state.PointerMoved(100, 97, At(24)), "Reset retained the previous video's pointer coordinates.");
            Console.WriteLine("PASS: stationary/synthetic pointer events, motion boundaries, accumulation, wake-up and reset.");
        }

        private static void PointerTimeoutAndProtection()
        {
            var state = new Controls.PlayerUiState();
            state.Reset(At(0));
            Check(!state.ShouldHidePointer(At(2.999), false), "Cursor hid before three idle seconds.");
            Check(state.ShouldHidePointer(At(3), false), "Cursor did not hide at its three-second deadline.");
            Check(!state.ShouldHidePointer(At(100), true), "An input dialog/keyboard did not protect the cursor.");
            Check(state.ShouldHidePointer(At(100), false), "Cursor timeout remained disabled after modal input ended.");
            state.PointerPressed(At(101));
            Check(!state.ShouldHidePointer(At(103.999), false) && state.ShouldHidePointer(At(104), false),
                "Clicking did not restart the cursor timeout.");
            Check(!state.CanAutoHide(At(105.999), true, false) && state.CanAutoHide(At(106), true, false),
                "Clicking did not postpone control hiding.");
            foreach (Controls.PlayerDrawer drawer in new[] { Controls.PlayerDrawer.Settings, Controls.PlayerDrawer.Comments })
            {
                state.Open(drawer, At(200));
                Check(!state.ShouldHidePointer(At(500), false), drawer + " did not protect the cursor.");
                state.Close(At(501));
                Check(state.ShouldHidePointer(At(501), false), "Closing a drawer permanently disabled cursor hiding.");
            }
            state.Reset(At(600));
            Check(!state.ShouldHidePointer(At(602.999), false) && state.ShouldHidePointer(At(603), false),
                "A new video retained the old cursor deadline.");
            Console.WriteLine("PASS: three-second cursor timeout, click activity, modal/drawer protection and lifecycle reset.");
        }
    }
}
