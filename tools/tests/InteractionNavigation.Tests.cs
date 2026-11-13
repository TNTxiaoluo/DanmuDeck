// External collaborators only; MainPage.Interactions.cs and its gamepad
// dispatcher are compiled unchanged by Test-InteractionNavigation.ps1.
namespace Windows.UI.Xaml { public sealed class RoutedEventArgs : EventArgs { } }
namespace Windows.System { public enum VirtualKey { Left, Right, Up, Down, A, Other } }
namespace BiliXbox.Controls { public enum PlayerDirection { Left, Right, Up, Down } }
namespace BiliXbox
{
    public sealed class InteractionVideo
    {
        public long Aid = 123, OwnerMid = 456;
        public string Owner = "fixture author";
    }
    public sealed class NavigationView
    {
        public int Navigations, Confirmations, Activations, Invalidations;
        public PlayerDirection LastDirection;
        public bool LastRepeat;
        public void Navigate(PlayerDirection direction) { LastDirection = direction; Navigations++; }
        public void Confirm(bool repeat) { Confirmations++; LastRepeat = repeat; if (!repeat) Activations++; }
        public void InvalidateFavoriteContent() { Invalidations++; }
    }
    public sealed class InteractionView
    {
        public event EventHandler CloseRequested, CreatorRequested, InteractionChanged;
        public event Func<Task> LoginRequested;
        public int Initializes, Shows, Stops, Navigations, Confirmations, Activations;
        public long Aid, Mid;
        public string Name;
        public CancellationToken Token;
        public PlayerDirection LastDirection;
        public bool LastRepeat;
        public Func<Task> Show = () => Task.CompletedTask;
        public void Initialize(object api) { Initializes++; }
        public Task ShowAsync(long aid, long mid, string name, CancellationToken token)
        { Shows++; Aid = aid; Mid = mid; Name = name; Token = token; return Show(); }
        public void StopRequests() { Stops++; }
        public void Navigate(PlayerDirection direction) { LastDirection = direction; Navigations++; }
        public void Confirm(bool repeat) { Confirmations++; LastRepeat = repeat; if (!repeat) Activations++; }
        public void RaiseClose() { CloseRequested?.Invoke(this, EventArgs.Empty); }
        public void RaiseCreator() { CreatorRequested?.Invoke(this, EventArgs.Empty); }
        public void RaiseChanged() { InteractionChanged?.Invoke(this, EventArgs.Empty); }
        public Task RaiseLogin() => LoginRequested == null ? Task.CompletedTask : LoginRequested();
    }
    public sealed class NavigationInput
    {
        public int Index = -1, Count;
        public void SelectButton(int index, int count) { Index = index; Count = count; }
    }
    public sealed partial class MainPage
    {
        private void ReconcileHomeScrollBeforeNavigation() { }
        private readonly object _api = new object();
        private readonly InteractionView InteractionSide = new InteractionView();
        private readonly NavigationView AccountLibrary = new NavigationView(), CreatorHub = new NavigationView();
        private readonly NavigationInput _playerInput = new NavigationInput();
        private readonly PlayerUiState _playerUi = new PlayerUiState();
        private readonly object InteractionButton = new object(), VideoOwnerButton = new object();
        private readonly object _play = new object(), _comments = new object();
        private object[] PlayerButtons => new[] { _play, InteractionButton, _comments };
        private InteractionVideo _video = new InteractionVideo();
        private CancellationTokenSource _watchCts = new CancellationTokenSource();
        private bool _disposed, _dialogOpen, _watching = true, _creatorVisible, _libraryVisible, _liveVisible = false, _dispatchingPlayerAction, _ignore;
        private bool DispatchLiveSectionKey(VirtualKey key, bool repeat) { return true; }
        private int _focusCancellations, _uiApplications, _closes, _creatorRequests, _loginRequests;
        private bool _creatorDispatchFlag, _creatorSawClosed;
        private object _creatorSender;
        private int _homeDirections, _homeConfirms, _playerKeys, _drawerDirections, _drawerConfirms;
        private string _status = "";
        private Func<Task> _login = () => Task.CompletedTask;
        private bool IgnorePlayerClick() => _ignore;
        private void CancelGamepadListFocus() { _focusCancellations++; }
        private void ApplyPlayerUi()
        { _uiApplications++; if (_playerUi.Drawer != PlayerDrawer.Interactions) InteractionSide.StopRequests(); }
        private void CloseDrawer() { _closes++; _playerUi.Close(DateTimeOffset.UtcNow); ApplyPlayerUi(); }
        private Task ShowLoginAsync() { _loginRequests++; return _login(); }
        private void VideoCreator_Click(object sender, RoutedEventArgs args)
        {
            _creatorRequests++; _creatorDispatchFlag = _dispatchingPlayerAction;
            _creatorSawClosed = _playerUi.Drawer == PlayerDrawer.None; _creatorSender = sender;
        }
        private static string Friendly(Exception error) => error.Message;
        private void SetStatus(string text) { _status = text; }
        private static bool PlayerDirectionKey(VirtualKey key, out PlayerDirection direction)
        {
            switch (key)
            {
                case VirtualKey.Left: direction = PlayerDirection.Left; return true;
                case VirtualKey.Right: direction = PlayerDirection.Right; return true;
                case VirtualKey.Up: direction = PlayerDirection.Up; return true;
                case VirtualKey.Down: direction = PlayerDirection.Down; return true;
                default: direction = PlayerDirection.Up; return false;
            }
        }
        private static bool PlayerConfirmKey(VirtualKey key) => key == VirtualKey.A;
        private void NavigateGamepadHome(PlayerDirection direction) { _homeDirections++; }
        private void ConfirmGamepadHome(bool repeat) { _homeConfirms++; }
        private bool HandlePlayerKey(VirtualKey key, bool repeat) { _playerKeys++; return true; }
        private void NavigateGamepadDrawer(PlayerDirection direction) { _drawerDirections++; }
        private void ConfirmGamepadDrawer(bool repeat) { _drawerConfirms++; }

        /* PRODUCTION_DISPATCH */

        internal static async Task RunTests()
        {
            var view = new MainPage();
            view.InitializeVideoInteractions();
            InteractionNavigationTests.Check(view.InteractionSide.Initializes == 1, "new drawer reuses the page API client");
            await view.ShowVideoInteractionsAsync();
            InteractionNavigationTests.Check(view.InteractionSide.Shows == 1 && view.InteractionSide.Aid == 123
                && view.InteractionSide.Mid == 456 && view.InteractionSide.Name == "fixture author"
                && view.InteractionSide.Token == view._watchCts.Token, "drawer receives exact current video, creator and watch scope");
            InteractionNavigationTests.Check(view._playerUi.Drawer == PlayerDrawer.Interactions && view._focusCancellations == 1
                && view._playerInput.Index == 1 && view._playerInput.Count == 3 && view._uiApplications == 1,
                "interaction entry owns the drawer and selects its return button");
            view.InteractionSide.RaiseChanged();
            InteractionNavigationTests.Check(view.AccountLibrary.Invalidations == 1, "successful interaction invalidates cached favorite content");
            view.InteractionSide.RaiseCreator();
            InteractionNavigationTests.Check(view._creatorRequests == 1 && view._creatorDispatchFlag && view._creatorSawClosed
                && ReferenceEquals(view._creatorSender, view.VideoOwnerButton) && !view._dispatchingPlayerAction,
                "creator entry closes drawer and reuses the guarded player avatar route exactly once");
            view.InteractionSide.RaiseCreator();
            InteractionNavigationTests.Check(view._creatorRequests == 1, "stale creator callback after drawer closes does not navigate");

            var blockers = new Action<MainPage>[]
            {
                item => item._disposed = true, item => item._watching = false, item => item._video = null,
                item => item._video.Aid = 0, item => item._watchCts = null, item => item._watchCts.Cancel(),
                item => item._creatorVisible = true, item => item._libraryVisible = true
            };
            foreach (var blocker in blockers)
            {
                view = new MainPage(); blocker(view); await view.ShowVideoInteractionsAsync();
                InteractionNavigationTests.Check(view.InteractionSide.Shows == 0 && view._focusCancellations == 0
                    && view._playerUi.Drawer == PlayerDrawer.None, "invalid resource/lifecycle context cannot open interaction drawer");
            }
            view = new MainPage(); view._ignore = true; view.Interactions_Click(null, new RoutedEventArgs());
            InteractionNavigationTests.Check(view.InteractionSide.Shows == 0, "ignored native click cannot bypass page input owner");

            foreach (int stale in Enumerable.Range(0, 5))
            {
                view = new MainPage(); var pending = new TaskCompletionSource<bool>();
                view.InteractionSide.Show = () => pending.Task;
                var show = view.ShowVideoInteractionsAsync();
                if (stale == 1) view._watchCts = new CancellationTokenSource();
                if (stale == 2) view._video = new InteractionVideo();
                if (stale == 3) view._playerUi.Close(DateTimeOffset.UtcNow);
                if (stale == 4) view._disposed = true;
                pending.SetException(new Exception("fixture read failure")); await show;
                InteractionNavigationTests.Check((view._status.Length > 0) == (stale == 0),
                    "late read failure reports only for its still-current watch/video/drawer");
            }
            view = new MainPage(); view.InteractionSide.Show = () => Task.FromCanceled(new CancellationToken(true));
            await view.ShowVideoInteractionsAsync();
            InteractionNavigationTests.Check(view._status.Length == 0, "canceled resource request does not overwrite page status");

            foreach (int stale in Enumerable.Range(0, 6))
            {
                view = new MainPage(); view.InitializeVideoInteractions();
                view._playerUi.Open(PlayerDrawer.Interactions, DateTimeOffset.UtcNow);
                var pending = new TaskCompletionSource<bool>(); view._login = () => pending.Task;
                var login = view.InteractionSide.RaiseLogin();
                InteractionNavigationTests.Check(view._loginRequests == 1 && view.InteractionSide.Shows == 0,
                    "login callback waits for existing account flow before refreshing");
                if (stale == 1) view._watchCts = new CancellationTokenSource();
                if (stale == 2) view._video = new InteractionVideo { Aid = 999 };
                if (stale == 3) view._playerUi.Close(DateTimeOffset.UtcNow);
                if (stale == 4) view._watching = false;
                if (stale == 5) view._disposed = true;
                pending.SetResult(true); await login;
                InteractionNavigationTests.Check(view.InteractionSide.Shows == (stale == 0 ? 1 : 0),
                    "login result cannot reopen stale interaction resource or closed navigation");
            }
            view = new MainPage(); view.InitializeVideoInteractions(); view._dialogOpen = true;
            await view.InteractionSide.RaiseLogin();
            InteractionNavigationTests.Check(view._loginRequests == 0, "existing dialog prevents a second login flow");

            view = new MainPage(); view.InitializeVideoInteractions();
            view._playerUi.Open(PlayerDrawer.Comments, DateTimeOffset.UtcNow); view.InteractionSide.RaiseClose();
            InteractionNavigationTests.Check(view._closes == 0 && view._playerUi.Drawer == PlayerDrawer.Comments,
                "stale interaction close event cannot close another drawer");
            view._playerUi.Open(PlayerDrawer.Interactions, DateTimeOffset.UtcNow); view.InteractionSide.RaiseClose();
            InteractionNavigationTests.Check(view._closes == 1 && view._playerUi.Drawer == PlayerDrawer.None,
                "interaction close delegates to existing drawer return flow");
            view._playerUi.Open(PlayerDrawer.Interactions, DateTimeOffset.UtcNow); view.SuspendVideoInteractions();
            InteractionNavigationTests.Check(view.InteractionSide.Stops > 0 && view._playerUi.Drawer == PlayerDrawer.None,
                "suspension stops interaction requests and releases its drawer");
            view._playerUi.Open(PlayerDrawer.Settings, DateTimeOffset.UtcNow); int applications = view._uiApplications;
            view.SuspendVideoInteractions();
            InteractionNavigationTests.Check(view._playerUi.Drawer == PlayerDrawer.Settings && view._uiApplications == applications,
                "interaction suspension does not reset unrelated drawer navigation");

            view = new MainPage(); view._playerUi.Open(PlayerDrawer.Interactions, DateTimeOffset.UtcNow);
            foreach (var key in new[] { VirtualKey.Left, VirtualKey.Right, VirtualKey.Up, VirtualKey.Down })
                InteractionNavigationTests.Check(view.DispatchPlayerGamepadKey(key, false), "direction is consumed by interaction owner");
            InteractionNavigationTests.Check(view.InteractionSide.Navigations == 4 && view._drawerDirections == 0 && view._playerKeys == 0,
                "directions are not also dispatched to covered player/comment navigation");
            view.DispatchPlayerGamepadKey(VirtualKey.A, false); view.DispatchPlayerGamepadKey(VirtualKey.A, true);
            InteractionNavigationTests.Check(view.InteractionSide.Confirmations == 2 && view.InteractionSide.Activations == 1
                && view.InteractionSide.LastRepeat && view._drawerConfirms == 0,
                "A has one interaction dispatch and preserves repeat suppression for mutation view");
            InteractionNavigationTests.Check(!view.DispatchPlayerGamepadKey(VirtualKey.Other, false), "unrelated key is not consumed as an action");
            view._libraryVisible = true; view.DispatchPlayerGamepadKey(VirtualKey.A, false);
            InteractionNavigationTests.Check(view.AccountLibrary.Confirmations == 1 && view.InteractionSide.Activations == 1,
                "account overlay retains navigation ownership above player drawer");
            view._libraryVisible = false; view._creatorVisible = true; view.DispatchPlayerGamepadKey(VirtualKey.Up, false);
            InteractionNavigationTests.Check(view.CreatorHub.Navigations == 1 && view.InteractionSide.Navigations == 4,
                "creator overlay retains navigation ownership above player drawer");
        }
    }
    public static class InteractionNavigationTests
    {
        private static int _passed;
        internal static void Check(bool value, string message)
        { if (!value) throw new Exception("FAIL: " + message); _passed++; }
        public static async Task<string> Run()
        {
            _passed = 0; await MainPage.RunTests();
            return "PASS: " + _passed + " production interaction orchestration/key-routing assertions (external UI/account providers; no live writes).";
        }
    }
}
