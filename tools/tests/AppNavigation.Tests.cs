namespace BiliXbox.AppNavigationTests
{
    using System;
    using System.Linq;
    using System.Reflection;
    using BiliXbox.Controls;
    using BiliXbox.Services;
    using Windows.UI.Xaml;
    using Windows.UI.Xaml.Controls;
    using Windows.UI.Xaml.Input;
    using Windows.UI.ViewManagement;

    public static class Tests
    {
        private static int _passed;
        private static void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("FAIL: " + name);
            _passed++;
        }
        private static void Reset()
        {
            Window.Reset(); Frame.Reset(); ApplicationView.Reset(); MainPage.ConstructionCount = 0;
            StartupDiagnostics.Reset();
        }
        private static MainPage CachedMainPage(App app)
        {
            var field = typeof(App).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(item => item.FieldType == typeof(MainPage));
            return (MainPage)field.GetValue(app);
        }
        private static void CheckPolicy(App app)
        {
            Check(ReferenceEquals(Application.Current, app), "production App is the current Application");
            Check(app.RequiresPointerMode == ApplicationRequiresPointerMode.WhenRequested,
                "App constructor applies WhenRequested without relying on XAML attributes");
            Check(app.RequestedTheme == ApplicationTheme.Dark, "App constructor applies Dark before window activation");
        }
        private static void ObserveLaunch(App app)
        {
            Frame.NavigationObserver = (frame, pageType) =>
            {
                CheckPolicy(app);
                Check(frame.RequiresPointer == RequiresPointer.Never, "Frame mouse emulation is disabled before Navigate");
                Check(frame.XYFocusKeyboardNavigation == XYFocusKeyboardNavigationMode.Enabled, "Frame XY navigation is enabled before Navigate");
                Check(ReferenceEquals(Window.Current.Content, frame), "Frame is attached to Window before Navigate");
                Check(pageType == typeof(MainPage), "Navigate targets production MainPage type");
                var view = ApplicationView.GetForCurrentView();
                Check(view.SetBoundsCount > 0 && view.BoundsMode == ApplicationViewBoundsMode.UseCoreWindow,
                    "core window bounds are selected before Navigate");
                Check(Window.Current.ActivationCount == 0, "first Navigate occurs before activation");
            };
            Window.ActivationObserver = () =>
            {
                var frame = Window.Current.Content as Frame;
                Check(frame != null && frame.Content is MainPage, "window activates only after MainPage exists");
                Check(ReferenceEquals(CachedMainPage(app), frame.Content), "main page reference is set before window activation");
            };
        }
        private static void CheckResumeDelegation(App app, MainPage page)
        {
            int resumed = page.ResumeCount, suspended = page.SuspendCount, displayResumed = page.DisplayResumeCount;
            app.RaiseResume();
            Check(page.ResumeCount == resumed + 1 && page.SuspendCount == suspended,
                "Resuming delegates exactly once to the actual page gamepad recovery");
            Check(page.DisplayResumeCount == displayResumed + 1, "resuming permits an explicit play to prepare HDMI again without autoplay");
            app.RaiseSuspend();
            Check(page.SuspendCount == suspended + 1 && page.ResumeCount == resumed + 1,
                "Suspending delegates exactly once to actual page playback suspension");
        }
        private static void CheckDisplaySuspendDeferral()
        {
            Reset();
            var app = new App(); app.Launch();
            var page = (MainPage)((Frame)Window.Current.Content).Content;
            var pending = new System.Threading.Tasks.TaskCompletionSource<bool>();
            page.SuspendTask = pending.Task;
            var args = app.RaiseSuspend();
            Check(page.SuspendCount == 1 && args.SuspendingOperation.GetDeferralCount == 1,
                "suspension obtains one deferral before awaiting display cleanup");
            Check(args.SuspendingOperation.Deferral.CompleteCount == 0,
                "suspension deferral stays pending until native display cleanup finishes");
            pending.SetResult(true);
            Check(args.SuspendingOperation.Deferral.CompleteCount == 1,
                "display cleanup completion releases suspension deferral exactly once");
            page.SuspendTask = System.Threading.Tasks.Task.FromException(new InvalidOperationException("fixture HDMI reset failure"));
            var failed = app.RaiseSuspend();
            Check(failed.SuspendingOperation.Deferral.CompleteCount == 1 && StartupDiagnostics.FailureCount == 1,
                "failed display cleanup is recorded and still releases its deferral");
            Check(StartupDiagnostics.FailureStage == "挂起时恢复默认显示模式",
                "suspend display failure identifies its own lifecycle stage");
        }
        private static void CheckFailedNavigation(bool throws)
        {
            Reset();
            var app = new App();
            var navigationError = new NotSupportedException("test navigation failure");
            if (throws) Frame.NavigationException = navigationError;
            else Frame.NavigateSucceeds = false;
            int failureActivations = 0;
            Window.ActivationObserver = () =>
            {
                failureActivations++;
                Check(Window.Current.Content is StartupFailurePage, "failed navigation installs fallback before activating the window");
                Check(CachedMainPage(app) == null, "failure clears the cached MainPage before fallback activation");
            };
            app.Launch();
            var fallback = Window.Current.Content as StartupFailurePage;
            Check(fallback != null && Window.Current.ActivationCount == 1 && failureActivations == 1,
                "navigation failure activates one visible retry page");
            Check(StartupDiagnostics.FailureCount == 1 && !string.IsNullOrEmpty(StartupDiagnostics.FailureStage),
                "navigation failure records its startup stage exactly once");
            if (throws)
                Check(ReferenceEquals(StartupDiagnostics.Failure, navigationError), "thrown navigation exception is recorded without replacement");
            else
                Check(StartupDiagnostics.Failure is InvalidOperationException,
                    "Navigate returning false is treated as a failure instead of a successful blank page");
            Check(fallback.Details == StartupDiagnostics.LastFailureSummary && !string.IsNullOrEmpty(fallback.Details),
                "fallback receives the recorded failure summary");
            Check(MainPage.ConstructionCount == 0, "failed Navigate does not pretend a MainPage was constructed");
            app.RaiseResume(); app.RaiseSuspend();
            Check(StartupDiagnostics.FailureCount == 1 && Window.Current.ActivationCount == 1,
                "lifecycle events while showing fallback do not navigate or record unrelated errors");

            Frame.NavigationException = null; Frame.NavigateSucceeds = true;
            Window.ActivationObserver = () =>
            {
                var retryFrame = Window.Current.Content as Frame;
                Check(retryFrame != null && retryFrame.Content is MainPage,
                    "retry installs a working MainPage before activation");
                Check(ReferenceEquals(CachedMainPage(app), retryFrame.Content), "retry restores the MainPage reference before activation");
            };
            Frame.NavigationObserver = (frame, pageType) =>
            {
                Check(frame.RequiresPointer == RequiresPointer.Never && frame.XYFocusKeyboardNavigation == XYFocusKeyboardNavigationMode.Enabled,
                    "retry retains native focus navigation policy before navigation");
                Check(pageType == typeof(MainPage), "retry invokes the real App startup path for MainPage");
            };
            fallback.Retry();
            var recovered = Window.Current.Content as Frame;
            Check(recovered != null && recovered.Content is MainPage && recovered.NavigationCount == 1,
                "retry recovers from the failure page by creating and navigating a new Frame");
            Check(Frame.ConstructionCount == 2 && MainPage.ConstructionCount == 1 && Window.Current.ActivationCount == 2,
                "recovery constructs one MainPage and activates once more");
            Check(StartupDiagnostics.FailureCount == 1, "successful retry does not record a second failure");
            CheckResumeDelegation(app, (MainPage)recovered.Content);
        }
        private static void CheckUnhandledReporting()
        {
            Reset();
            var app = new App();
            var error = new InvalidOperationException("test unhandled exception");
            StartupDiagnostics.Mark("test active startup stage");
            var args = app.RaiseUnhandled(error);
            Check(StartupDiagnostics.FailureCount == 1 && ReferenceEquals(StartupDiagnostics.Failure, error),
                "production unhandled event records the original exception exactly once");
            Check(StartupDiagnostics.FailureStage == "test active startup stage", "unhandled exception retains the current startup stage");
            Check(!args.Handled, "diagnostic reporting does not swallow an unhandled exception");
            Check(Window.Current.Content == null && Window.Current.ActivationCount == 0,
                "unhandled reporting alone does not invent navigation or activate a window");
        }
        private static void CheckStalePageCleared()
        {
            Reset();
            var app = new App();
            app.Launch();
            var originalFrame = (Frame)Window.Current.Content;
            var originalPage = (MainPage)originalFrame.Content;
            Check(ReferenceEquals(CachedMainPage(app), originalPage), "test begins with a successfully cached MainPage");
            originalFrame.Content = null;
            Frame.NavigationException = new InvalidOperationException("test failed relaunch");
            app.Launch();
            Check(Window.Current.Content is StartupFailurePage && CachedMainPage(app) == null,
                "failed relaunch clears an existing cached page rather than retaining it behind fallback");
            app.RaiseResume(); app.RaiseSuspend();
            Check(originalPage.ResumeCount == 0 && originalPage.SuspendCount == 0,
                "lifecycle events after failed relaunch never reach the discarded MainPage");
        }
        public static string Run()
        {
            _passed = 0;
            Reset();
            var bare = new Application();
            Check(bare.RequiresPointerMode == ApplicationRequiresPointerMode.Auto && bare.RequestedTheme == ApplicationTheme.Light,
                "test Application starts with platform Auto pointer default and no XAML theme setup");
            var app = new App();
            CheckPolicy(app);
            Check(CachedMainPage(app) == null && Window.Current.Content == null, "constructor does not create a window page");
            app.RaiseResume(); app.RaiseSuspend();
            Check(MainPage.ConstructionCount == 0 && Window.Current.ActivationCount == 0,
                "lifecycle events before first launch neither fail nor create a page");
            ObserveLaunch(app);
            app.Launch();
            var created = Window.Current.Content as Frame;
            Check(created != null && created.NavigationCount == 1 && Frame.ConstructionCount == 1, "fresh launch creates one Frame and navigates once");
            Check(created.ApplicationModeAtConstruction == ApplicationRequiresPointerMode.WhenRequested
                && created.ApplicationThemeAtConstruction == ApplicationTheme.Dark,
                "constructor configuration precedes even Frame construction");
            Check(Window.Current.ActivationCount == 1 && MainPage.ConstructionCount == 1, "fresh launch activates once and constructs one MainPage");
            CheckResumeDelegation(app, (MainPage)created.Content);

            Reset(); app = new App();
            Window.Current.Content = new object();
            ObserveLaunch(app); app.Launch();
            Check(Window.Current.Content is Frame && Frame.ConstructionCount == 1 && MainPage.ConstructionCount == 1,
                "non-Frame window content is replaced with a properly configured Frame");

            Reset(); app = new App();
            var empty = new Frame { RequiresPointer = RequiresPointer.WhenEngaged, XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Disabled };
            Window.Current.Content = empty;
            ObserveLaunch(app); app.Launch();
            Check(ReferenceEquals(Window.Current.Content, empty) && Frame.ConstructionCount == 1 && empty.NavigationCount == 1,
                "empty existing Frame is reused and navigated once");
            Check(empty.RequiresPointer == RequiresPointer.Never && empty.XYFocusKeyboardNavigation == XYFocusKeyboardNavigationMode.Enabled,
                "launch repairs stale pointer and XY policy on a reused Frame");
            CheckResumeDelegation(app, (MainPage)empty.Content);

            Reset(); app = new App();
            var existingPage = new MainPage();
            var populated = new Frame { Content = existingPage, RequiresPointer = RequiresPointer.WhenEngaged,
                XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Disabled };
            Window.Current.Content = populated;
            ObserveLaunch(app); app.Launch();
            Check(ReferenceEquals(Window.Current.Content, populated) && ReferenceEquals(populated.Content, existingPage),
                "existing Frame and populated MainPage retain their identities");
            Check(populated.NavigationCount == 0 && MainPage.ConstructionCount == 1 && Frame.ConstructionCount == 1,
                "relaunch neither navigates nor creates a replacement populated page");
            Check(populated.RequiresPointer == RequiresPointer.Never && populated.XYFocusKeyboardNavigation == XYFocusKeyboardNavigationMode.Enabled,
                "populated reused Frame also receives current navigation policy");
            CheckResumeDelegation(app, existingPage);
            app.Launch();
            Check(Window.Current.ActivationCount == 2 && populated.NavigationCount == 0 && MainPage.ConstructionCount == 1,
                "repeated activation continues to reuse the same page without duplicate navigation");
            int previous = existingPage.ResumeCount; app.RaiseResume();
            Check(existingPage.ResumeCount == previous + 1, "relaunch does not register duplicate resume handlers");
            CheckFailedNavigation(true);
            CheckFailedNavigation(false);
            CheckUnhandledReporting();
            CheckStalePageCleared();
            CheckDisplaySuspendDeferral();
            return "PASS: " + _passed + " production App startup/lifecycle checks: pointer/theme policy, Frame reuse, pre-navigation configuration, lifecycle delegation, navigation failure reporting, retry recovery, and unhandled diagnostics. Native WinRT setter timing, Xbox shell rendering, and diagnostic file persistence are not established.";
        }
    }
}
