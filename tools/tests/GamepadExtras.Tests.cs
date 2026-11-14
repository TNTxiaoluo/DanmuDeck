namespace Windows.Gaming.Input
{
    [Flags] public enum GamepadButtons { None = 0, LeftShoulder = 1, RightShoulder = 2 }
    public struct GamepadReading { public GamepadButtons Buttons; public double RightThumbstickY; }
}
namespace Windows.System
{
    public enum VirtualKey { Other, GamepadLeftShoulder, GamepadRightShoulder, GamepadRightThumbstickUp, GamepadRightThumbstickDown }
}
namespace Windows.UI.Xaml { public class DependencyObject { } }
namespace Windows.UI.Xaml.Controls
{
    public class Control : DependencyObject { }
    public sealed class ListViewBase : Control { }
    public sealed class ScrollViewer : Control { }
}
namespace Windows.UI.Xaml.Input
{
    public sealed class KeyRoutedEventArgs { public VirtualKey Key, OriginalKey; }
}
namespace BiliXbox.Controls
{
    public sealed class ViewportScrollResult
    { public int Index = -1; public object Item; public Control Container; public double Offset; }
    public static class ViewportFocusScroll
    {
        public sealed class Request
        { public DependencyObject Scope; public double Delta; public int Preferred; public Action<ViewportScrollResult> Callback; }
        public static readonly List<Request> Requests = new List<Request>();
        public static readonly List<DependencyObject> Reconciliations = new List<DependencyObject>(), Cancellations = new List<DependencyObject>();
        public static bool Handled = true;
        public static ViewportScrollResult ReconcileResult;
        public static bool Scroll(ListViewBase list, double delta, Action<ViewportScrollResult> callback = null, int preferredIndex = -1)
        { Requests.Add(new Request { Scope = list, Delta = delta, Callback = callback, Preferred = preferredIndex }); return Handled; }
        public static bool Scroll(ScrollViewer viewer, double delta, Action<ViewportScrollResult> callback = null)
        { Requests.Add(new Request { Scope = viewer, Delta = delta, Callback = callback }); return Handled; }
        public static ViewportScrollResult Reconcile(ListViewBase list) { Reconciliations.Add(list); return ReconcileResult; }
        public static void Cancel(DependencyObject scope) { Cancellations.Add(scope); }
        public static void Reset() { Requests.Clear(); Reconciliations.Clear(); Cancellations.Clear(); Handled = true; ReconcileResult = null; }
    }
    public sealed class ExtraView
    {
        public bool IsPlayingRoom, Handled = true;
        public int Scrolls; public double Delta;
        public bool ScrollBy(double delta) { ++Scrolls; Delta = delta; return Handled; }
    }
    public sealed class ExtraClock { public TimeSpan Elapsed; }
}
namespace BiliXbox
{
    public sealed partial class MainPage
    {
        private bool _disposed, _dialogOpen, _inputPaneVisible, IsNativeGamepadEditorActive, RawGamepadOwnsNavigation;
        private bool _watching, _creatorVisible, _libraryVisible, _liveVisible, _gamepadHomeFeedReplacing, _viewingReplies;
        private bool _gamepadHomeInitialFocusDone;
        private readonly FeedNavigationState _feedNavigation = new FeedNavigationState();
        private HomeSection? _pendingSection;
        private readonly ExtraView LiveHub = new ExtraView(), CreatorHub = new ExtraView(), AccountLibrary = new ExtraView();
        private readonly ExtraClock _gamepadClock = new ExtraClock();
        private object _activeGamepad = new object();
        private readonly Control[] SectionButtons = { new Control(), new Control(), new Control(), new Control(), new Control() };
        private readonly PlayerUiState _playerUi = new PlayerUiState();
        private int _gamepadHomeIndex = 1, _gamepadNavigationIndex = 2;
        private object _gamepadHomeItem;
        private Control _selectedGamepadControl;
        private Control _commentActionCard, _commentActionReturn;
        private ListViewBase _gamepadNavigationList;
        private ListViewBase HomeResultGrid = new ListViewBase();
        private readonly ListViewBase ReplyList = new ListViewBase(), CommentList = new ListViewBase();
        private readonly Control SettingsSide = new Control(), DanmakuSettingsSide = new Control(), Root = new Control();
        private readonly ScrollViewer _settingsScroll = new ScrollViewer(), _danmakuScroll = new ScrollViewer();
        private int _homeFocusCancelled, _listFocusCancelled, _remembered;
        private Control _rememberedControl;
        private string _status;
        private sealed class ExtraMedia
        {
            public object Source = new object();
            public ExtraSession PlaybackSession = new ExtraSession();
        }
        private sealed class ExtraSession
        {
            public bool CanSeek = true;
            public TimeSpan Position = TimeSpan.FromSeconds(30), NaturalDuration = TimeSpan.FromSeconds(100);
        }
        private readonly ExtraMedia _media = new ExtraMedia();
        private bool _wantsPlayback, _resumeAfterOpen;
        private int _controlsShown;
        private int _bufferInvalidations;
        private void InvalidatePlaybackBuffering() { ++_bufferInvalidations; }
        private void ShowControls() { ++_controlsShown; }
        /* PRODUCTION_SKIP */
        private readonly List<HomeSection> _sections = new List<HomeSection>();
        private readonly List<Task> _sectionTransitions = new List<Task>();
        private Task _sectionLoad = Task.CompletedTask;
        private Task SelectSectionAsync(HomeSection section)
        {
            // The real section loader owns its pending target before awaiting
            // transition/loading; this substitute exposes that external await.
            _sections.Add(section); _pendingSection = section;
            var transition = CompleteSectionLoadAsync(section, _sectionLoad);
            _sectionTransitions.Add(transition);
            return transition;
        }
        private async Task CompleteSectionLoadAsync(HomeSection section, Task load)
        {
            // This fixture has no native dispatcher. Do not rely on SetResult
            // running every continuation inline, or on the test caller pumping
            // an ambient PowerShell synchronization context.
            await load.ConfigureAwait(false);
            if (_pendingSection == section) { _feedNavigation.SelectSection(section); _pendingSection = null; }
        }
        private static string Friendly(Exception error) => error.Message;
        private void SetStatus(string text) { _status = text; }
        private void CancelGamepadHomeFocus() { ++_homeFocusCancelled; }
        private void CancelGamepadListFocus() { ++_listFocusCancelled; }
        private void RememberCommentComposerPosition(Control control) { ++_remembered; _rememberedControl = control; }
        private bool ScrollReplyThread(double delta) => ViewportFocusScroll.Scroll(ReplyList, delta);
        private T FindGamepadChild<T>(DependencyObject root) where T : DependencyObject
            => (ReferenceEquals(root, SettingsSide) ? _settingsScroll : _danmakuScroll) as T;

        private static int _checks;
        private static void Check(bool value, string message)
        { ++_checks; if (!value) throw new Exception("FAIL: " + message); }
        private static MainPage Fresh()
        { ViewportFocusScroll.Reset(); return new MainPage(); }
        private static ViewportScrollResult Result(int index) => new ViewportScrollResult { Index = index, Item = new object(), Container = new Control(), Offset = 300 };
        private static ViewportFocusScroll.Request LastRequest => ViewportFocusScroll.Requests[ViewportFocusScroll.Requests.Count - 1];

        public static void RunExtrasTests()
        {
            _checks = 0;
            var page = Fresh();
            Check(page._feedNavigation.Section == HomeSection.Recommendations, "Real feed policy starts on Recommendations");
            page.StepHomeSection(1);
            Check(page._sections.Count == 1 && page._sections[0] == HomeSection.Popular, "RB moves default Recommendations to Popular");
            page = Fresh(); page.StepHomeSection(-1);
            Check(page._sections[0] == HomeSection.Live, "LB moves Recommendations to Live");
            page.StepHomeSection(-1); Check(page._sections.Count == 1, "LB clamps at leftmost Live");
            page._feedNavigation.SelectSection(HomeSection.Film); page.StepHomeSection(1);
            Check(page._sections.Count == 1, "RB clamps at rightmost Film");
            page._feedNavigation.Search("fixture"); page.StepHomeSection(1);
            Check(page._sections.Count == 2 && page._sections[1] == HomeSection.Film && !page._feedNavigation.IsSearch,
                "Bumper at an edge still exits search into the selected section");
            page = Fresh(); var wait = new TaskCompletionSource<bool>(); page._sectionLoad = wait.Task;
            page.StepHomeSection(1); page.StepHomeSection(1); page.StepHomeSection(1);
            Check(page._sections.Count == 3 && page._sections[0] == HomeSection.Popular && page._sections[1] == HomeSection.Animation && page._sections[2] == HomeSection.Film,
                "Rapid presses advance from queued pending target rather than stale rendered section");
            Check(page._feedNavigation.Section == HomeSection.Recommendations, "Pending loading does not fake rendered section state");
            page.StepHomeSection(1); Check(page._sections.Count == 3, "Pending rightmost section also clamps while transition awaits");
            page.StepHomeSection(-1); Check(page._sections[3] == HomeSection.Animation, "Opposite bumper reverses from pending target");
            wait.SetResult(true);
            Check(Task.WhenAll(page._sectionTransitions).Wait(TimeSpan.FromSeconds(5)),
                "Every queued external section transition completes before inspecting final navigation state");
            Check(page._feedNavigation.Section == HomeSection.Animation, "Latest pending section wins when external transition completes");
            foreach (Action<MainPage> guard in new Action<MainPage>[] {
                p => p._watching = true, p => p._creatorVisible = true, p => p._libraryVisible = true,
                p => { p._liveVisible = true; p.LiveHub.IsPlayingRoom = true; },
                p => p._disposed = true, p => p._dialogOpen = true, p => p._inputPaneVisible = true, p => p.IsNativeGamepadEditorActive = true })
            {
                page = Fresh(); guard(page); page.StepHomeSection(1);
                Check(page._sections.Count == 0, "Bumper cannot change content while player, nested view, dialog or native keyboard owns navigation");
            }
            page = Fresh(); page._liveVisible = true; page._feedNavigation.SelectSection(HomeSection.Live); page.StepHomeSection(1);
            Check(page._sections[0] == HomeSection.Recommendations, "Live room browser still participates in section bumpers");
            page = Fresh(); page.RawGamepadOwnsNavigation = true;
            Check(page.HandleAuxiliaryGamepadKey(VirtualKey.GamepadRightShoulder, false) && page._sections.Count == 0,
                "Core/virtual shoulder alias is consumed without duplicating raw polling");
            Check(page.HandleAuxiliaryGamepadKey(VirtualKey.GamepadRightThumbstickDown, false) && ViewportFocusScroll.Requests.Count == 0,
                "Core/virtual right-stick alias cannot duplicate raw scrolling");
            page.RawGamepadOwnsNavigation = false; page.HandleAuxiliaryGamepadKey(VirtualKey.GamepadRightShoulder, true);
            Check(page._sections.Count == 0, "Fallback shoulder ignores key autorepeat");
            page.HandleAuxiliaryGamepadKey(VirtualKey.GamepadRightShoulder, false);
            Check(page._sections.Count == 1 && page._sections[0] == HomeSection.Popular, "Fallback shoulder performs one real section action");
            page.HandleAuxiliaryGamepadKey(VirtualKey.GamepadRightThumbstickUp, false);
            Check(LastRequest.Delta == -84, "Fallback right-stick up routes a bounded upward scroll");
            page.HandleAuxiliaryGamepadKey(VirtualKey.GamepadRightThumbstickDown, true);
            Check(LastRequest.Delta == 84, "Fallback right-stick down supports native repeat as wheel input");
            Check(!page.HandleAuxiliaryGamepadKey(VirtualKey.Other, false), "Unrelated key is not consumed by auxiliary router");
            Check(IsAuxiliaryGamepadEvent(new KeyRoutedEventArgs { Key = VirtualKey.Other, OriginalKey = VirtualKey.GamepadLeftShoulder }),
                "Routed shoulder is recognized through OriginalKey");
            Check(IsAuxiliaryGamepadEvent(new KeyRoutedEventArgs { Key = VirtualKey.GamepadRightThumbstickDown, OriginalKey = VirtualKey.Other }),
                "Mapped right-stick is recognized through Key");
            Check(!IsAuxiliaryGamepadEvent(new KeyRoutedEventArgs { Key = VirtualKey.Other, OriginalKey = VirtualKey.Other }),
                "Ordinary keyboard event is not mistaken for gamepad auxiliary input");

            page = Fresh(); page.PollAuxiliaryGamepad(new GamepadReading(), true);
            page._gamepadClock.Elapsed = TimeSpan.FromMilliseconds(33); page.PollAuxiliaryGamepad(new GamepadReading(), true);
            page._gamepadClock.Elapsed = TimeSpan.FromMilliseconds(66); page.PollAuxiliaryGamepad(new GamepadReading { Buttons = GamepadButtons.RightShoulder }, true);
            page._gamepadClock.Elapsed = TimeSpan.FromMilliseconds(99); page.PollAuxiliaryGamepad(new GamepadReading { Buttons = GamepadButtons.RightShoulder }, true);
            Check(page._sections.Count == 1, "Actual auxiliary sampler and root router keep held bumper to one action");
            page._gamepadClock.Elapsed = TimeSpan.FromMilliseconds(132); page.PollAuxiliaryGamepad(new GamepadReading { RightThumbstickY = 1 }, true);
            Check(LastRequest.Delta < 0 && LastRequest.Delta >= -46, "Actual raw right stick uses elapsed-time bounded upward scrolling");
            int samples = ViewportFocusScroll.Requests.Count;
            page._gamepadClock.Elapsed = TimeSpan.FromMilliseconds(165); page.PollAuxiliaryGamepad(new GamepadReading { Buttons = GamepadButtons.LeftShoulder, RightThumbstickY = -1 }, false);
            Check(ViewportFocusScroll.Requests.Count == samples && page._sections.Count == 1, "Disabled raw sampling cannot scroll or change sections");

            foreach (bool playing in new[] { false, true })
            {
                page = Fresh(); page._watching = true;
                page._wantsPlayback = page._resumeAfterOpen = playing;
                page.PollAuxiliaryGamepad(new GamepadReading(), true);
                page._gamepadClock.Elapsed = TimeSpan.FromMilliseconds(33);
                page.PollAuxiliaryGamepad(new GamepadReading { Buttons = GamepadButtons.RightShoulder }, true);
                Check(page._media.PlaybackSession.Position == TimeSpan.FromSeconds(35) && page._controlsShown == 1,
                    "Raw RB seeks exactly five seconds while playing or paused");
                Check(page._bufferInvalidations == 1, "shoulder seek invalidates cached ranges before changing position");
                page.RawGamepadOwnsNavigation = true;
                Check(page.HandleAuxiliaryGamepadKey(VirtualKey.GamepadRightShoulder, false), "Mapped raw-owned bumper is consumed");
                for (int frame = 2; frame < 30; ++frame)
                {
                    page._gamepadClock.Elapsed = TimeSpan.FromMilliseconds(frame * 33);
                    page.PollAuxiliaryGamepad(new GamepadReading { Buttons = GamepadButtons.RightShoulder }, true);
                    page.HandleAuxiliaryGamepadKey(VirtualKey.GamepadRightShoulder, true);
                }
                Check(page._media.PlaybackSession.Position == TimeSpan.FromSeconds(35) && page._controlsShown == 1,
                    "Held raw bumper and mapped aliases cannot multiply one seek edge");
                page._gamepadClock.Elapsed = TimeSpan.FromMilliseconds(1000);
                page.PollAuxiliaryGamepad(new GamepadReading(), true);
                page._gamepadClock.Elapsed = TimeSpan.FromMilliseconds(1033);
                page.PollAuxiliaryGamepad(new GamepadReading { Buttons = GamepadButtons.LeftShoulder }, true);
                Check(page._media.PlaybackSession.Position == TimeSpan.FromSeconds(30), "Released raw LB seeks back exactly five seconds");
                Check(page._wantsPlayback == playing && page._resumeAfterOpen == playing && page._sections.Count == 0,
                    "Shoulder seeks preserve playing intent and never change home sections in player");
                page.RawGamepadOwnsNavigation = false;
                page.HandleAuxiliaryGamepadKey(VirtualKey.GamepadLeftShoulder, false);
                page.HandleAuxiliaryGamepadKey(VirtualKey.GamepadLeftShoulder, true);
                Check(page._media.PlaybackSession.Position == TimeSpan.FromSeconds(25), "Virtual LB fallback seeks once and ignores autorepeat");
                page._media.PlaybackSession.Position = TimeSpan.FromSeconds(2);
                page.HandleAuxiliaryGamepadKey(VirtualKey.GamepadLeftShoulder, false);
                Check(page._media.PlaybackSession.Position == TimeSpan.Zero, "Five-second reverse seek clamps at video start");
                page._media.PlaybackSession.Position = TimeSpan.FromSeconds(99);
                page.HandleAuxiliaryGamepadKey(VirtualKey.GamepadRightShoulder, false);
                Check(page._media.PlaybackSession.Position == TimeSpan.FromSeconds(100), "Five-second forward seek clamps at video end");
            }
            foreach (Action<MainPage> guard in new Action<MainPage>[] {
                p => p._creatorVisible = true, p => p._libraryVisible = true, p => p._liveVisible = true,
                p => p._dialogOpen = true, p => p._inputPaneVisible = true, p => p._disposed = true,
                p => p.IsNativeGamepadEditorActive = true,
                p => p._playerUi.Open(PlayerDrawer.Comments, DateTimeOffset.UtcNow),
                p => p._playerUi.Open(PlayerDrawer.Settings, DateTimeOffset.UtcNow) })
            {
                page = Fresh(); page._watching = true; guard(page);
                page.SeekVideoShoulder(1);
                Check(page._media.PlaybackSession.Position == TimeSpan.FromSeconds(30), "Foreground nested view/editor/drawer ownership blocks video bumper seek");
            }

            page = Fresh(); Check(page.ScrollGamepadSurface(55), "Home viewport accepts scrolling");
            var request = LastRequest;
            Check(page._homeFocusCancelled == 1 && page._homeScrollPending && page._homeManuallyScrolled,
                "Home scroll invalidates old pending focus before requesting a fresh viewport position");
            Check(ReferenceEquals(request.Scope, page.HomeResultGrid) && request.Preferred == 1 && request.Delta == 55,
                "Home router supplies active result grid, previous column/index and requested delta");
            var result = Result(6); request.Callback(result);
            Check(!page._homeScrollPending && page._gamepadHomeInitialFocusDone && page._gamepadHomeIndex == 6,
                "Completed home scroll clears pending flag and records visible item index");
            Check(ReferenceEquals(page._gamepadHomeItem, result.Item) && ReferenceEquals(page._selectedGamepadControl, result.Container),
                "Home scroll records matching item and native focus bookmark");
            page.ScrollGamepadSurface(50); var stale = LastRequest.Callback;
            page.ScrollGamepadSurface(50); var current = LastRequest.Callback;
            stale(Result(8)); Check(page._gamepadHomeIndex == 6 && page._homeScrollPending, "Old manual-scroll callback cannot replace current bookmark or pending ownership");
            current(Result(10)); Check(page._gamepadHomeIndex == 10 && !page._homeScrollPending, "Latest manual-scroll callback owns final focus");
            page.ScrollGamepadSurface(50); var replaced = LastRequest.Callback; page.HomeResultGrid = new ListViewBase(); replaced(Result(14));
            Check(page._gamepadHomeIndex == 10, "Replaced home grid rejects old viewport callback");
            page = Fresh(); ViewportFocusScroll.Handled = false; Check(!page.ScrollGamepadSurface(50) && !page._homeScrollPending,
                "Non-scrollable home grid does not retain a pending autofocus suppression");
            page = Fresh(); page._gamepadHomeFeedReplacing = true;
            Check(!page.ScrollGamepadSurface(50) && ViewportFocusScroll.Requests.Count == 0 && page._homeFocusCancelled == 0,
                "Feed replacement cannot scroll old containers");
            foreach (Action<MainPage> guard in new Action<MainPage>[] { p => p._disposed = true, p => p._dialogOpen = true,
                p => p._inputPaneVisible = true, p => p.IsNativeGamepadEditorActive = true })
            {
                page = Fresh(); guard(page);
                Check(!page.ScrollGamepadSurface(50) && ViewportFocusScroll.Requests.Count == 0,
                    "Native editor/dialog/lifetime ownership blocks right-stick scrolling");
            }

            foreach (int view in new[] { 0, 1, 2 })
            {
                page = Fresh(); page._liveVisible = view == 0; page._creatorVisible = view == 1; page._libraryVisible = view == 2;
                var target = view == 0 ? page.LiveHub : view == 1 ? page.CreatorHub : page.AccountLibrary;
                Check(page.ScrollGamepadSurface(40) && target.Scrolls == 1 && target.Delta == 40,
                    "Nested view receives right-stick input through its own scroll/focus owner");
                Check(page._homeFocusCancelled == 0 && page._listFocusCancelled == 0 && ViewportFocusScroll.Requests.Count == 0,
                    "Nested-view scrolling bypasses root focus cancellation and viewport mutations");
                page._homeManuallyScrolled = page._homeScrollPending = true; page._manualScrollRevision = 7;
                page.ReconcileHomeScrollBeforeNavigation();
                Check(ViewportFocusScroll.Cancellations.Count == 0 && ViewportFocusScroll.Reconciliations.Count == 0 && page._manualScrollRevision == 7 && page._homeScrollPending,
                    "Root handoff leaves nested view pending reconciliation intact for its native navigation");
            }

            page = Fresh(); page._watching = true; page._playerUi.Open(PlayerDrawer.Comments, DateTimeOffset.UtcNow);
            page.ReconcileHomeScrollBeforeNavigation();
            Check(ViewportFocusScroll.Reconciliations.Count == 0 && page._remembered == 0,
                "Opening comments without manual scrolling cannot unexpectedly reconcile focus into the list");
            Check(ViewportFocusScroll.Cancellations.Count == 1, "Ordinary root handoff can cancel inert scroll state without moving focus");
            Check(page.ScrollGamepadSurface(30) && page._listFocusCancelled == 1 && ReferenceEquals(LastRequest.Scope, page.CommentList),
                "Video-comment scroll targets current list after cancelling old list focus");
            result = Result(7); LastRequest.Callback(result);
            Check(ReferenceEquals(page._gamepadNavigationList, page.CommentList) && page._gamepadNavigationIndex == 7 && ReferenceEquals(page._rememberedControl, result.Container),
                "Comment scroll synchronizes list index, selected control and composer-return bookmark");
            ViewportFocusScroll.ReconcileResult = Result(9); page.ReconcileHomeScrollBeforeNavigation();
            Check(ViewportFocusScroll.Reconciliations.Count == 1 && ReferenceEquals(ViewportFocusScroll.Reconciliations[0], page.CommentList) && page._gamepadNavigationIndex == 9,
                "D-pad handoff flushes only the comment list that manual scrolling owned");
            Check(page._manuallyScrolledList == null && !page._homeScrollPending && !page._homeManuallyScrolled,
                "Root navigation releases manual-scroll owner and flags after handoff");
            page = Fresh(); page._watching = true; page._playerUi.Open(PlayerDrawer.Comments, DateTimeOffset.UtcNow);
            page.ScrollGamepadSurface(30); request = LastRequest; page._viewingReplies = true; request.Callback(Result(8));
            Check(page._remembered == 0, "Opening replies rejects stale parent-comment scroll callback");
            page.ReconcileHomeScrollBeforeNavigation();
            Check(ViewportFocusScroll.Reconciliations.Count == 0, "Parent-list scroll ownership cannot steal focus in a newly opened reply list");
            page.ScrollGamepadSurface(30); Check(ReferenceEquals(LastRequest.Scope, page.ReplyList), "Reply drawer scroll targets reply list");
            foreach (PlayerDrawer drawer in new[] { PlayerDrawer.Settings, PlayerDrawer.DanmakuSettings })
            {
                page = Fresh(); page._watching = true; page._playerUi.Open(drawer, DateTimeOffset.UtcNow);
                Check(page.ScrollGamepadSurface(30) && ReferenceEquals(LastRequest.Scope, drawer == PlayerDrawer.Settings ? page._settingsScroll : page._danmakuScroll),
                    "Settings drawer scroll uses the matching visible scroll viewer");
            }
            page = Fresh(); page._watching = true;
            Check(!page.ScrollGamepadSurface(30) && ViewportFocusScroll.Requests.Count == 0, "Bare video playback does not turn right stick into unintended seeking");
            page = Fresh(); page.ScrollGamepadSurface(50); ViewportFocusScroll.ReconcileResult = Result(12);
            page.ReconcileHomeScrollBeforeNavigation();
            Check(page._gamepadHomeIndex == 12 && ViewportFocusScroll.Reconciliations.Count == 1 && ReferenceEquals(ViewportFocusScroll.Cancellations[0], page.Root),
                "Home direction handoff flushes visible bookmark then cancels all root scroll work");
            Console.WriteLine("PASS: " + _checks + " production MainPage auxiliary gamepad routing assertions (offline view/viewport substitutes).");
        }
    }
}
