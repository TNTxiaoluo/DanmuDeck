using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Controls;
using BiliXbox.Models;
using Windows.System;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace Windows.System { public enum VirtualKey { Left, Right, Up, Down, A, Other } }
namespace Windows.UI
{
    public struct Color { public byte A, R, G, B; public static Color FromArgb(byte a, byte r, byte g, byte b) { return new Color { A = a, R = r, G = g, B = b }; } }
}
namespace Windows.UI.Xaml
{
    public enum Visibility { Visible, Collapsed }
    public sealed class RoutedEventArgs : EventArgs { }
    public struct Thickness { public double Left, Top, Right, Bottom; public Thickness(double value) { Left = Top = Right = Bottom = value; } public Thickness(double left, double top, double right, double bottom) { Left = left; Top = top; Right = right; Bottom = bottom; } }
    public class DependencyObject { public DependencyObject Parent; }
}
namespace Windows.UI.Xaml.Controls
{
    public class Control : DependencyObject
    {
        public Visibility Visibility = Visibility.Visible;
        public object Background, Foreground, Tag;
        public string HelpText;
        public bool IsFocusEngaged;
        public bool IsEnabled = true;
    }
    public class Button : Control { }
    public class TextBox : Control { public string Text = ""; public int MaxLength; }
    public class PasswordBox : Control { }
    public class ComboBox : Control { public bool IsDropDownOpen; }
    public class TextBlock : Control { public string Text; }
    public class ScrollViewer : Control { public double VerticalOffset; }
    public class ListViewBase : Control
    {
        public readonly List<object> Items = new List<object>();
        public readonly ScrollViewer Viewer = new ScrollViewer();
    }
    public class Grid : Control
    {
        public Thickness Padding;
        public static void SetRow(Control control, int row) { }
        public static void SetRowSpan(Control control, int rows) { }
    }
    public static class FocusManager { public static object Current; public static object GetFocusedElement() { return Current; } }
}
namespace Windows.UI.Xaml.Media
{
    public sealed class SolidColorBrush { public Color Color; public SolidColorBrush(Color color) { Color = color; } }
    public static class VisualTreeHelper { public static DependencyObject GetParent(DependencyObject child) { return child.Parent; } }
}
namespace Windows.UI.Xaml.Automation { public static class AutomationProperties { public static void SetHelpText(Control control, string text) { control.HelpText = text; } } }
namespace Windows.UI.Xaml.Input { }
namespace BiliXbox.Controls
{
    public enum PlayerDirection { Left, Right, Up, Down }
    public sealed class ViewportScrollResult { public int Index; public object Item; public Control Container; }
    public static class ViewportFocusScroll
    {
        public static ScrollViewer FindScrollViewer(ListViewBase list) => list.Viewer;
        public static bool Restore(ListViewBase list, double offset, int index, Action<ViewportScrollResult> callback)
        {
            list.Viewer.VerticalOffset = offset;
            var control = new Control(); FocusManager.Current = control;
            callback(new ViewportScrollResult { Index = index, Item = list.Items[index], Container = control }); return true;
        }
    }
}
namespace BiliXbox
{
    public sealed class LiveNavigationView : Control
    {
        public event Func<Task> LoginRequested;
        public void ReconcileScrollFocus() { }
        public event EventHandler PlaybackChanged;
        public Func<TextBox, string, int, Task<bool>> RequestTextInputAsync;
        public bool IsEditing, IsPlayingRoom;
        public int Initializes, Shows, Stops, Restores, Navigations, Confirms, Activations, Writes;
        public bool NavigateResult = true;
        public PlayerDirection Direction;
        public readonly List<CancellationToken> Tokens = new List<CancellationToken>();
        public Func<CancellationToken, Task> Showing = token => Task.CompletedTask;
        public void Initialize(object api) { Initializes++; }
        public Task ShowAsync(CancellationToken token) { Shows++; Tokens.Add(token); return Showing(token); }
        public void StopRequests() { Stops++; IsPlayingRoom = false; }
        public void RestoreFocus() { Restores++; FocusManager.Current = this; }
        public bool Navigate(PlayerDirection direction) { Navigations++; Direction = direction; return NavigateResult; }
        public void Confirm(bool repeat) { Confirms++; if (!repeat) Activations++; }
        public Task RaiseLogin() { return LoginRequested == null ? Task.CompletedTask : LoginRequested(); }
        public void RaisePlayback(bool playing) { IsPlayingRoom = playing; PlaybackChanged?.Invoke(this, EventArgs.Empty); }
    }
    public sealed partial class MainPage
    {
        private readonly object _api = new object();
        private readonly LiveNavigationView LiveHub = new LiveNavigationView();
        private readonly Button LiveSectionButton = new Button { Tag = "Live" }, HomeButton = new Button(), PopularSectionButton = new Button { Tag = "Popular" },
            AnimationSectionButton = new Button { Tag = "Animation" }, FilmSectionButton = new Button { Tag = "Film" },
            HeaderSearchButton = new Button(), AccountButton = new Button(), HelpButton = new Button();
        private readonly FeedNavigationState _feedNavigation = new FeedNavigationState();
        private readonly TextBox SearchInput = new TextBox();
        private readonly TextBlock SearchQueryLabel = new TextBlock();
        private readonly Grid HomeView = new Grid(), WatchView = new Grid(), Root = new Grid(), Header = new Grid(), Footer = new Grid();
        private readonly Control GamepadFocusOutline = new Control();
        private readonly ComboBox PartSelector = new ComboBox();
        private CancellationTokenSource _feedCts;
        private bool _disposed, _dialogOpen, _ignore;
        private bool _watching = false;
        private bool _creatorVisible = false, _libraryVisible = false, _inputPaneVisible = false;
        private bool _gamepadHomeFeedReplacing, _gamepadHomeInitialFocusDone, _homeScrollPending, _homeManuallyScrolled;
        private int _gamepadHomeIndex, _manualScrollRevision;
        private ListViewBase _manuallyScrolledList;
        private HomeSection? _pendingSection = null;
        private readonly ListViewBase VideoGrid = new ListViewBase(), UserGrid = new ListViewBase();
        private ListViewBase HomeResultGrid => VideoGrid;
        private readonly List<VideoItem> Videos = new List<VideoItem>();
        private readonly List<object> Users = new List<object>();
        private readonly TextBlock FeedTitle = new TextBlock(), FeedNotice = new TextBlock(), EmptyState = new TextBlock(), PageLabel = new TextBlock();
        private readonly Button PreviousPage = new Button(), NextPage = new Button();
        private bool _hasNextFeed;
        private long _accountMid = 10, _loadedFeedAccountMid;
        private long CurrentAccountMid => _accountMid;
        private int _libraryClears, _creatorClears, _focusCancels, _feedLoads, _recommendationRefreshes, _homes, _loginShows, _homeConfirms, _homeActivations;
        private string _status = "";
        private Func<Task> _login = () => Task.CompletedTask;
        private Func<Task> _inline = () => Task.CompletedTask;
        private Control[] HomeHeaderControls => new Control[] { LiveSectionButton, HomeButton, PopularSectionButton, AnimationSectionButton, FilmSectionButton, HeaderSearchButton, AccountButton, HelpButton };
        private bool IgnorePlayerClick() { return _ignore; }
        private void ClearAccountLibraryNavigation() { _libraryClears++; }
        private void ClearCreatorNavigation() { _creatorClears++; }
        private void CancelGamepadListFocus() { _focusCancels++; }
        private void CancelGamepadHomeFocus() { }
        private int GamepadHomeVideoIndex(Control control) => _gamepadHomeIndex;
        private void ReconcileHomeScrollBeforeNavigation() { }
        private void ApplyHomeScrollFocus(ViewportScrollResult result) { _gamepadHomeIndex = result.Index; }
        private void FocusGamepadHomeVideo(int index) { _gamepadHomeIndex = index; }
        private void BeginHomeFeedReplacement() { _gamepadHomeFeedReplacing = true; }
        private void CompleteHomeFeedReplacement() { _gamepadHomeFeedReplacing = false; _homeFeedFocusAnchor = null; RestoreHomeFeedPageFocus(); }
        private void UpdateSearchModeUi(FeedRequest request) { }
        private void CancelManualGamepadScroll() { }
        private Task<bool> PrepareSectionTransitionAsync(HomeSection target) => Task.FromResult(true);
        private void StartSectionTransition() { }
        private static void Cancel(ref CancellationTokenSource source) { var owner = source; source = null; owner?.Cancel(); owner?.Dispose(); }
        private static string Friendly(Exception error) { return error.Message; }
        private void SetStatus(string text) { _status = text; }
        private Task ShowLoginAsync() { _loginShows++; return _login(); }
        private Task ShowInlineTextInputAsync(TextBox input, bool submit) { return _inline(); }
        // GoHome and feed network loading are external collaborators. The full
        // production Sections partial and editor guard remain unchanged.
        private void GoHome(bool loadLive = true)
        {
            _homes++;
            if (_feedNavigation.Section == HomeSection.Live && !_feedNavigation.IsSearch) ShowLiveSectionSurface();
            else { Header.Visibility = Footer.Visibility = HomeView.Visibility = Visibility.Visible; WatchView.Visibility = Visibility.Collapsed; }
        }
        private Task LoadFeedAsync(bool refresh = false)
        {
            EnsureHomeFeedAccount(refresh);
            var request = _feedNavigation.BeginRequest();
            if (TryRestoreCachedHomeFeed(request)) return Task.CompletedTask;
            _feedLoads++; if (refresh) _recommendationRefreshes++;
            Videos.Clear(); VideoGrid.Items.Clear();
            var video = new VideoItem { Bvid = request.Section + ":" + request.Page + ":" + _feedLoads };
            Videos.Add(video); VideoGrid.Items.Add(video); FeedTitle.Text = SectionTitle(request.Section);
            RememberHomeFeedPage(request, CurrentAccountMid, Videos, true, "");
            CompleteHomeFeedReplacement();
            return Task.CompletedTask;
        }
        private void FocusGamepadHomeControl(Control control) { FocusManager.Current = control; }
        private void ConfirmGamepadHome(bool repeat) { _homeConfirms++; if (!repeat) _homeActivations++; }
        private static bool PlayerConfirmKey(VirtualKey key) { return key == VirtualKey.A; }
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
        /* PRODUCTION_EDITOR_GUARD */

        internal static async Task RunTests()
        {
            Action<bool, string> check = LiveNavigationTests.Check;
            var page = new MainPage(); page.InitializeSections();
            check(page._feedNavigation.Section == HomeSection.Recommendations && !page._feedNavigation.IsSearch, "Fresh app defaults to recommendations");
            check(page.SectionButtons.SequenceEqual(new Control[] { page.LiveSectionButton, page.HomeButton, page.PopularSectionButton, page.AnimationSectionButton, page.FilmSectionButton }), "Five section controls have requested left-to-right order");
            check(page.HomeButton.HelpText == "当前分区" && page.LiveSectionButton.HelpText == "切换分区", "Default selection announces recommendations");
            check(page.LiveHub.Initializes == 1 && page.LiveHub.RequestTextInputAsync != null, "Section reuses page API/native input contract");
            foreach (HomeSection section in Enum.GetValues(typeof(HomeSection)))
            {
                page = new MainPage(); page.InitializeSections(); page.SearchInput.Text = "previous query"; page._feedNavigation.Search("previous query"); page._feedNavigation.NextPage();
                await page.SelectSectionAsync(section);
                check(page._feedNavigation.Section == section && !page._feedNavigation.IsSearch && page._feedNavigation.Page == 1 && page.SearchInput.Text == "", "Selecting each section clears search and page");
                check(page.SectionButtons.Select((control, index) => control.HelpText == "当前分区" ? index : -1).Single(index => index >= 0) == (int)section, "Selected section matches visual/help state");
                check(page._feedLoads == (section == HomeSection.Live ? 0 : 1) && page.LiveHub.Shows == (section == HomeSection.Live ? 1 : 0), "Live and video feeds have disjoint loading paths");
                check(page._recommendationRefreshes == 0, "Tab selection never explicitly refreshes personalized recommendations");
                check(page._libraryClears == 1 && page._creatorClears == 1 && page._focusCancels == 1, "Changing section retires unrelated navigation/focus requests");
                check(page.LiveHub.Writes == 0, "Changing tabs never submits chat/gifts");
            }
            page = new MainPage(); page.InitializeSections(); await page.LoadFeedAsync();
            page._feedNavigation.NextPage(); await page.LoadFeedAsync();
            page._gamepadHomeIndex = 0; page.VideoGrid.Viewer.VerticalOffset = 120;
            string oldVideo = page.Videos[0].Bvid;
            await page.SelectSectionAsync(HomeSection.Popular);
            int loads = page._feedLoads;
            await page.SelectSectionAsync(HomeSection.Recommendations);
            check(page._feedLoads == loads && page._feedNavigation.Page == 2 && page.Videos[0].Bvid == oldVideo,
                "Production section routing restores recommendation page two with identical cards and no network request");
            check(page.VideoGrid.Viewer.VerticalOffset == 120 && page._gamepadHomeIndex == 0 && page._gamepadHomeInitialFocusDone
                && !page._homeScrollPending && page._homeManuallyScrolled && page._manuallyScrolledList == page.VideoGrid
                && page._hasNextFeed && page._loadedFeedAccountMid == 10,
                "Returning to a tab restores its viewport and selected result");
            await page.SelectSectionAsync(HomeSection.Recommendations);
            check(page._feedLoads == loads && page._feedNavigation.Page == 2,
                "Selecting the current tab cannot reset its page or change the feed batch");
            page._accountMid = 11; await page.SelectSectionAsync(HomeSection.Popular);
            check(page._homeFeedPages.AccountMid == 11 && page._feedNavigation.Page == 1 && page._feedLoads == loads + 1,
                "Account switch clears previous user's views and tab pages before routing");
            var oldRequest = page._visibleHomeFeedRequest;
            HomeFeedPageCache.Page oldPage;
            page._homeFeedPages.TryGet(HomeSection.Popular, 1, out oldPage);
            page._accountMid = 12; page._gamepadHomeIndex = 99; page.VideoGrid.Viewer.VerticalOffset = 999;
            page.CaptureVisibleHomeFeedPosition();
            check(!oldPage.HasPosition, "An account change cannot capture old visible content using the new user's identity.");
            page.EnsureHomeFeedAccount();
            page.RememberHomeFeedPage(oldRequest, 11, new[] { new VideoItem { Bvid = "stale-private" } }, false, "");
            check(!page._homeFeedPages.TryGet(HomeSection.Popular, 1, out oldPage) && page._visibleHomeFeedRequest == null,
                "A late response from the old account cannot fill the new account's UI cache.");
            foreach (var target in new[] { HomeSection.Recommendations, HomeSection.Popular, HomeSection.Animation, HomeSection.Film })
            {
                page = new MainPage(); page.InitializeSections(); await page.SelectSectionAsync(HomeSection.Live);
                var live = page._liveCts; var token = live.Token; page._feedCts = new CancellationTokenSource(); var feed = page._feedCts.Token;
                await page.SelectSectionAsync(target);
                check(token.IsCancellationRequested && feed.IsCancellationRequested && !page._liveVisible && page.LiveHub.Visibility == Visibility.Collapsed, "Leaving live cancels live/feed ownership and hides it");
                check(page.LiveHub.Stops >= 2 && page.HomeView.Visibility == Visibility.Visible, "Leaving live closes view and restores feed");
            }
            page = new MainPage(); page.InitializeSections(); await page.SelectSectionAsync(HomeSection.Live);
            FocusManager.Current = page.LiveSectionButton;
            check(page.DispatchLiveSectionKey(VirtualKey.Right, false) && ReferenceEquals(FocusManager.Current, page.HomeButton), "Header right navigates to recommendation");
            page.DispatchLiveSectionKey(VirtualKey.Left, false); page.DispatchLiveSectionKey(VirtualKey.Left, false);
            check(ReferenceEquals(FocusManager.Current, page.LiveSectionButton), "Header left clamps at first tab");
            FocusManager.Current = page.HelpButton; page.DispatchLiveSectionKey(VirtualKey.Right, false);
            check(ReferenceEquals(FocusManager.Current, page.HelpButton), "Header right clamps at last action");
            page.DispatchLiveSectionKey(VirtualKey.Down, false);
            check(ReferenceEquals(FocusManager.Current, page.LiveHub), "Header down enters current live focus");
            page.DispatchLiveSectionKey(VirtualKey.Right, false);
            check(page.LiveHub.Navigations == 1 && page.LiveHub.Direction == PlayerDirection.Right, "Room direction routes exactly once to live view");
            page.LiveHub.NavigateResult = false; page.DispatchLiveSectionKey(VirtualKey.Up, false);
            check(ReferenceEquals(FocusManager.Current, page.LiveSectionButton), "Live browse upper boundary returns to section bar");
            page.DispatchLiveSectionKey(VirtualKey.A, false); page.DispatchLiveSectionKey(VirtualKey.A, true);
            check(page._homeConfirms == 2 && page._homeActivations == 1 && page.LiveHub.Confirms == 0, "Header A owns confirmation; repeat cannot activate twice");
            FocusManager.Current = page.LiveHub; page.DispatchLiveSectionKey(VirtualKey.A, false); page.DispatchLiveSectionKey(VirtualKey.A, true);
            check(page.LiveHub.Confirms == 2 && page.LiveHub.Activations == 1 && page._homeActivations == 1, "View A owns confirmation with no header/native duplicate");
            check(!page.DispatchLiveSectionKey(VirtualKey.Other, false), "Unrelated key stays available to root shortcuts");
            page.LiveHub.IsEditing = true; check(page.IsNativeGamepadEditorActive, "Live native editor releases raw sampler/navigation ownership");
            page.LiveHub.IsEditing = false; FocusManager.Current = page.LiveHub; check(!page.IsNativeGamepadEditorActive, "Closed native editor restores gamepad ownership");
            var engaged = new TextBox { IsFocusEngaged = true, Parent = page.Root }; FocusManager.Current = engaged;
            check(page.IsNativeGamepadEditorActive, "Engaged generic native editor also retains its input");
            engaged.IsFocusEngaged = false; check(!page.IsNativeGamepadEditorActive, "Read-only focused editor alone cannot swallow B/A");

            page = new MainPage(); page.InitializeSections();
            var pending = new TaskCompletionSource<bool>(); page.LiveHub.Showing = token => pending.Task;
            Task selection = page.SelectSectionAsync(HomeSection.Live); var oldToken = page._liveCts.Token;
            await page.SelectSectionAsync(HomeSection.Animation);
            pending.SetException(new Exception("stale live failure")); await selection;
            check(oldToken.IsCancellationRequested && page._status == "" && !page._liveVisible && page._feedNavigation.Section == HomeSection.Animation, "Late live failure cannot overwrite new tab notice");
            check(page.LiveHub.Restores == 1, "Late load cannot steal focus from new tab");
            page = new MainPage(); page.InitializeSections(); page.LiveHub.Showing = token => Task.FromException(new Exception("current failure"));
            await page.SelectSectionAsync(HomeSection.Live);
            check(page._status.Contains("current failure"), "Current live failure is shown to user");

            page = new MainPage(); page.InitializeSections(); await page.LiveHub.RaiseLogin();
            check(page._loginShows == 0, "Hidden live callback cannot open login");
            await page.SelectSectionAsync(HomeSection.Live); await page.LiveHub.RaiseLogin();
            check(page._loginShows == 1, "Visible live login uses shared account dialog");
            page._dialogOpen = true; await page.LiveHub.RaiseLogin(); page._dialogOpen = false; page._disposed = true; await page.LiveHub.RaiseLogin();
            check(page._loginShows == 1, "Dialog and disposed guards reject duplicate/stale login");
            page = new MainPage(); page.InitializeSections(); await page.SelectSectionAsync(HomeSection.Live);
            page.LiveHub.RaisePlayback(true);
            check(page.Header.Visibility == Visibility.Collapsed && page.Footer.Visibility == Visibility.Collapsed && page.Root.Padding.Left == 0, "Live playback uses full-screen player chrome");
            page.LiveHub.RaisePlayback(false);
            check(page.Header.Visibility == Visibility.Visible && page.Footer.Visibility == Visibility.Visible && page.Root.Padding.Left == 52, "Room exit restores shared browse chrome");
            int shows = page.LiveHub.Shows; var ownerToken = page._liveCts.Token; page.HideLiveSection();
            check(ownerToken.IsCancellationRequested && page.LiveHub.Visibility == Visibility.Collapsed, "Stop retires current live lifetime");
            page.ShowLiveSectionSurface(); await page.LoadLiveSectionAsync();
            check(page.LiveHub.Shows == shows + 1 && !page._liveCts.IsCancellationRequested && page.LiveHub.Writes == 0 && !page.LiveHub.IsPlayingRoom, "Reshow/restoration creates fresh browse scope without replaying playback or writes");

            var editor = new TextBox { Text = "input" }; bool accepted = await page.ShowLiveInlineInputAsync(editor, "room", 240);
            check(accepted && editor.MaxLength == 240, "Live input uses inline editor and specified limit");
            page._inline = () => { page.HideLiveSection(); return Task.CompletedTask; };
            check(!await page.ShowLiveInlineInputAsync(editor, "room", 240), "Late native input cannot submit after leaving live");
            page = new MainPage(); page._ignore = true; page.Section_Click(page.LiveSectionButton, new RoutedEventArgs());
            check(page._homes == 0, "Ignored duplicate native click cannot change section");
            page._ignore = false; page._dialogOpen = true; await page.SelectSectionAsync(HomeSection.Live);
            check(page._homes == 0 && !page._liveVisible, "Open native dialog blocks tab changes");
        }
    }
    public static class LiveNavigationTests
    {
        private static int _count;
        internal static void Check(bool value, string message) { if (!value) throw new Exception(message); _count++; }
        public static async Task Run() { await MainPage.RunTests(); Console.WriteLine("PASS: " + _count + " production live section, focus dispatch, async ownership, inline input, and lifecycle assertions. External XAML/network substitutes; no writes."); }
    }
}
