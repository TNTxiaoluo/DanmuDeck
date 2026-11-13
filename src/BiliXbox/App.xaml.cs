using System;
using BiliXbox.Controls;
using BiliXbox.Services;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace BiliXbox
{
    sealed partial class App : Application
    {
        private MainPage _mainPage;

        public App()
        {
            UnhandledException += (sender, args) => StartupDiagnostics.RecordFailure(StartupDiagnostics.Stage, args.Exception);
            StartupDiagnostics.Mark("初始化应用");
            InitializeComponent();
            // Application root attributes are not applied by UWP XAML codegen.
            // Set this before creating/focusing any page: Auto enables Xbox's
            // system gamepad mouse, which PointerCursor = null cannot disable.
            StartupDiagnostics.Mark("设置启动指针模式");
            RequiresPointerMode = ApplicationRequiresPointerMode.WhenRequested;
            RequestedTheme = ApplicationTheme.Dark;
            Suspending += OnSuspending;
            Resuming += OnResuming;
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            OpenMainPage();
        }

        private void OpenMainPage()
        {
            try
            {
                StartupDiagnostics.Mark("配置启动窗口");
                ApplicationView.GetForCurrentView().SetDesiredBoundsMode(ApplicationViewBoundsMode.UseCoreWindow);
                var frame = Window.Current.Content as Frame;
                if (frame == null) Window.Current.Content = frame = new Frame();
                frame.RequiresPointer = RequiresPointer.Never;
                frame.XYFocusKeyboardNavigation = Windows.UI.Xaml.Input.XYFocusKeyboardNavigationMode.Enabled;
                StartupDiagnostics.Mark("创建首页");
                if (frame.Content == null && !frame.Navigate(typeof(MainPage)))
                    throw new InvalidOperationException("Home navigation failed.");
                _mainPage = frame.Content as MainPage;
                StartupDiagnostics.Mark("显示首页");
                Window.Current.Activate();
                StartupDiagnostics.Mark("启动完成");
            }
            catch (Exception ex)
            {
                StartupDiagnostics.RecordFailure(StartupDiagnostics.Stage, ex);
                _mainPage = null;
                Window.Current.Content = new StartupFailurePage(StartupDiagnostics.LastFailureSummary, OpenMainPage);
                Window.Current.Activate();
            }
        }

        private void OnResuming(object sender, object e)
        {
            // Resuming may run outside the UI thread; the page marshals its own
            // window access and restores polling only while in the foreground.
            _mainPage?.ResumeDisplayPlayback();
            _mainPage?.ResumeGamepadInput();
        }

        private async void OnSuspending(object sender, SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();
            try
            {
                var page = (Window.Current.Content as Frame)?.Content as MainPage;
                if (page != null) await page.SuspendPlaybackAsync();
            }
            catch (Exception ex) { StartupDiagnostics.RecordFailure("挂起时恢复默认显示模式", ex); }
            finally { deferral.Complete(); }
        }
    }
}
