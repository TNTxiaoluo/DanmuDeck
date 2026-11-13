// TEST ONLY: external dependencies of unchanged MainPage.AccountLibrary.cs.
// No XAML loader, HTTP, account files, physical controller or actual logout.
namespace Windows.UI.Xaml
{
    public enum Visibility { Visible, Collapsed }
    public struct Thickness
    {
        public double Left, Top, Right, Bottom;
        public Thickness(double value) { Left = Top = Right = Bottom = value; }
    }
    public class FrameworkElement
    {
        public Visibility Visibility { get; set; } = Visibility.Visible;
        public Thickness Padding { get; set; }
    }
}
namespace Windows.UI.Xaml.Input
{
    public enum XYFocusKeyboardNavigationMode { Auto, Disabled, Enabled }
}
namespace Windows.UI.Xaml.Controls
{
    public enum ContentDialogButton { None, Primary, Secondary, Close }
    public enum ContentDialogResult { None, Primary, Secondary }
    public enum RequiresPointer { Never, WhenEngaged, WhenFocused }
    public sealed class ContentDialog
    {
        public object Title { get; set; }
        public object Content { get; set; }
        public string PrimaryButtonText { get; set; }
        public string CloseButtonText { get; set; }
        public ContentDialogButton DefaultButton { get; set; }
        public RequiresPointer RequiresPointer { get; set; }
        public Input.XYFocusKeyboardNavigationMode XYFocusKeyboardNavigation { get; set; }
        public static Func<ContentDialog, Task<ContentDialogResult>> ShowHandler;
        public static int ShowCount;
        public event EventHandler Closed;
        public async Task<ContentDialogResult> ShowAsync()
        {
            ++ShowCount;
            var result = ShowHandler == null ? ContentDialogResult.None : await ShowHandler(this);
            Closed?.Invoke(this, EventArgs.Empty);
            return result;
        }
        public static void Reset() { ShowHandler = null; ShowCount = 0; }
    }
}
namespace BiliXbox.Controls
{
    using BiliXbox.Models;
    using BiliXbox.Services;
    public sealed class AccountLibraryView : Windows.UI.Xaml.FrameworkElement
    {
        public int InitializeCount, ShowCount, StopCount, ClearCount, FocusCount;
        public object CachedView;
        public UserProfile LastUser;
        public Func<Task> ShowHandler;
        public event EventHandler CloseRequested;
        public event EventHandler SignOutRequested;
        public event Func<AccountLibraryItem, Task> OpenItemRequested;
        public void Initialize(BilibiliClient api) { ++InitializeCount; }
        public Task ShowAsync(UserProfile user)
        { ++ShowCount; LastUser = user; return ShowHandler == null ? Task.CompletedTask : ShowHandler(); }
        public void StopRequests() { ++StopCount; }
        public void RestoreFocus() { ++FocusCount; }
        public void ClearAccount() { ++ClearCount; CachedView = null; LastUser = null; }
        public void RaiseClose() { CloseRequested?.Invoke(this, EventArgs.Empty); }
        public void RaiseSignOut() { SignOutRequested?.Invoke(this, EventArgs.Empty); }
        public Task RaiseOpen(AccountLibraryItem item) => OpenItemRequested == null ? Task.CompletedTask : OpenItemRequested(item);
    }
    public sealed class CreatorDependency : Windows.UI.Xaml.FrameworkElement
    {
        public int StopCount;
        public void StopRequests() { ++StopCount; }
    }
}
namespace BiliXbox.Services
{
    using BiliXbox.Models;
    public sealed class BilibiliClient
    {
        public UserProfile CurrentUser { get; set; } = new UserProfile { Mid = 42, Name = "fixture account" };
        public bool IsLoggedIn => CurrentUser != null;
        public int LogoutCount, UgcCount, PgcCount;
        public long LastEpisodeId;
        public int LastQuality;
        public VideoDetail LastVideo;
        public CancellationToken LastToken;
        public Exception LogoutFailure;
        public Task LogoutAsync()
        {
            ++LogoutCount;
            if (LogoutFailure != null) return Task.FromException(LogoutFailure);
            CurrentUser = null;
            return Task.CompletedTask;
        }
        public Task<PlaybackInfo> GetEpisodePlaybackAsync(long episode, int quality, CancellationToken token)
        {
            ++PgcCount; LastEpisodeId = episode; LastQuality = quality; LastToken = token;
            return Task.FromResult(new PlaybackInfo { QualityId = quality });
        }
        public Task<PlaybackInfo> GetPlaybackAsync(VideoDetail video, int quality, CancellationToken token)
        {
            ++UgcCount; LastVideo = video; LastQuality = quality; LastToken = token;
            return Task.FromResult(new PlaybackInfo { QualityId = quality });
        }
    }
}
namespace BiliXbox.AccountNavigationTests
{
    public sealed class FakePlaybackSession { public TimeSpan Position { get; set; } }
    public sealed class FakeMediaPlayer
    {
        public FakePlaybackSession PlaybackSession { get; } = new FakePlaybackSession();
        public object Source { get; set; }
        public int PauseCount, PlayCount;
        public void Pause() { ++PauseCount; }
        public void Play() { ++PlayCount; }
    }
    public sealed class RecordedNavigation
    {
        public string Bvid, Title;
        public object Bookmark;
    }
}
namespace BiliXbox
{
    using BiliXbox.AccountNavigationTests;
    using BiliXbox.Models;
    using BiliXbox.Services;
    public sealed partial class MainPage
    {
        // Other MainPage partials and XAML are the dependencies of this unit.
        private readonly BilibiliClient _api = new BilibiliClient();
        private bool _disposed, _dialogOpen, _creatorVisible, _videoReturnsToCreator, _watching, _wantsPlayback, _resumeAfterOpen;
        private VideoDetail _video;
        private string _requestedBvid, _requestedTitle;
        private long _currentCid;
        private PlaybackInfo _playback;
        private int _preferredQuality = 80, _partGeneration;
        private TimeSpan? _restorePosition;
        private readonly FakeMediaPlayer _media = new FakeMediaPlayer();
        private ContentDialog _activeGeneralDialog;
        private DateTimeOffset _lastBack;
        private long CurrentAccountMid => _api.IsLoggedIn ? _api.CurrentUser?.Mid ?? 0 : 0;
        private readonly AccountLibraryView AccountLibrary = new AccountLibraryView();
        private readonly CreatorDependency CreatorHub = new CreatorDependency();
        private readonly FrameworkElement Header = new FrameworkElement(), Footer = new FrameworkElement(), HomeView = new FrameworkElement(),
            WatchView = new FrameworkElement(), Root = new FrameworkElement(), GamepadFocusOutline = new FrameworkElement();
        private sealed class CreatorPlaybackBookmark
        {
            public string Bvid, Title;
            public long Cid;
            public int Quality;
            public TimeSpan Position;
            public bool Playing;
            public AccountLibraryItem Episode;
        }
        public readonly System.Collections.Generic.List<RecordedNavigation> Navigations = new System.Collections.Generic.List<RecordedNavigation>();
        public int GoHomeCount, CreatorSurfaceCount, CreatorClearCount, AccountUpdateCount, HomeRefreshCount,
            PointerDisableCount, PlaybackUiCount, ShowControlsCount, NavigationFocusCount;
        public string Status;
        public MainPage() { InitializeAccountLibrary(); }
        private Task OpenVideoAsync(string bvid, string title = null, CreatorPlaybackBookmark bookmark = null)
        {
            // Deliberately record only; tests explicitly establish the separate
            // player lifecycle before invoking the production return methods.
            Navigations.Add(new RecordedNavigation { Bvid = bvid, Title = title, Bookmark = bookmark });
            return Task.CompletedTask;
        }
        private void HideLiveSection() { }
        private void CancelManualGamepadScroll() { }
        private void StopSectionMotion() { }
        private void GoHome()
        {
            // External page lifecycle substitute; contains no library policy.
            ++GoHomeCount; _watching = false; _playingEpisodeItem = null;
            _media.Source = null; _wantsPlayback = _resumeAfterOpen = false;
            Header.Visibility = Footer.Visibility = HomeView.Visibility = Visibility.Visible;
            WatchView.Visibility = Visibility.Collapsed;
        }
        private void ShowCreatorSurface() { ++CreatorSurfaceCount; }
        private void ClearCreatorNavigation() { ++CreatorClearCount; _creatorVisible = _videoReturnsToCreator = false; }
        private void UpdateAccount() { ++AccountUpdateCount; }
        private Task RefreshHomeForAccountAsync() { ++HomeRefreshCount; return Task.CompletedTask; }
        private Task PlayWithDisplayAsync() { _media.Play(); return Task.CompletedTask; }
        private Task ResetPlaybackDisplayAsync() { return Task.CompletedTask; }
        private void DisablePointerCursor() { ++PointerDisableCount; }
        private void UpdatePlaybackUi() { ++PlaybackUiCount; }
        private void ShowControls() { ++ShowControlsCount; }
        private void RestoreNavigationFocus() { ++NavigationFocusCount; }
        private void SetStatus(string text) { Status = text; }
        private static string Friendly(Exception error) => error.Message;
    }
}
