// TEST ONLY. No XAML loader, application registration, network or Xbox shell.
namespace Windows.ApplicationModel
{
    public sealed class SuspendingDeferral
    {
        public int CompleteCount { get; private set; }
        public void Complete() { ++CompleteCount; }
    }
    public sealed class SuspendingOperation
    {
        public int GetDeferralCount { get; private set; }
        public SuspendingDeferral Deferral { get; } = new SuspendingDeferral();
        public SuspendingDeferral GetDeferral() { ++GetDeferralCount; return Deferral; }
    }
    public sealed class SuspendingEventArgs : System.EventArgs
    {
        public SuspendingOperation SuspendingOperation { get; } = new SuspendingOperation();
    }
}
namespace Windows.ApplicationModel.Activation
{
    public sealed class LaunchActivatedEventArgs : System.EventArgs { }
}
namespace Windows.UI.Xaml
{
    public enum ApplicationRequiresPointerMode { Auto, WhenRequested }
    public enum ApplicationTheme { Light, Dark }
    public sealed class UnhandledExceptionEventArgs : System.EventArgs
    {
        public System.Exception Exception { get; }
        public bool Handled { get; set; }
        public UnhandledExceptionEventArgs(System.Exception error) { Exception = error; }
    }
    public class Application
    {
        public static Application Current { get; private set; }
        public ApplicationRequiresPointerMode RequiresPointerMode { get; set; } = ApplicationRequiresPointerMode.Auto;
        public ApplicationTheme RequestedTheme { get; set; } = ApplicationTheme.Light;
        public event System.EventHandler<Windows.ApplicationModel.SuspendingEventArgs> Suspending;
        public event System.EventHandler<object> Resuming;
        public event System.EventHandler<UnhandledExceptionEventArgs> UnhandledException;
        public Application() { Current = this; }
        protected virtual void OnLaunched(Windows.ApplicationModel.Activation.LaunchActivatedEventArgs args) { }
        public void Launch() { OnLaunched(new Windows.ApplicationModel.Activation.LaunchActivatedEventArgs()); }
        public void RaiseResume() { Resuming?.Invoke(this, System.EventArgs.Empty); }
        public Windows.ApplicationModel.SuspendingEventArgs RaiseSuspend()
        {
            var args = new Windows.ApplicationModel.SuspendingEventArgs();
            Suspending?.Invoke(this, args);
            return args;
        }
        public UnhandledExceptionEventArgs RaiseUnhandled(System.Exception error)
        {
            var args = new UnhandledExceptionEventArgs(error);
            UnhandledException?.Invoke(this, args);
            return args;
        }
    }
    public sealed class Window
    {
        public static Window Current { get; private set; } = new Window();
        public object Content { get; set; }
        public int ActivationCount { get; private set; }
        public static System.Action ActivationObserver;
        public static void Reset() { Current = new Window(); ActivationObserver = null; }
        public void Activate() { ActivationCount++; ActivationObserver?.Invoke(); }
    }
}
namespace Windows.UI.Xaml.Input
{
    public enum XYFocusKeyboardNavigationMode { Auto, Disabled, Enabled }
}
namespace Windows.UI.Xaml.Controls
{
    public enum RequiresPointer { Never, WhenEngaged, WhenFocused }
    public sealed class Frame
    {
        public static int ConstructionCount;
        public object Content { get; set; }
        public RequiresPointer RequiresPointer { get; set; } = RequiresPointer.Never;
        public Windows.UI.Xaml.Input.XYFocusKeyboardNavigationMode XYFocusKeyboardNavigation { get; set; }
        public int NavigationCount { get; private set; }
        public Windows.UI.Xaml.ApplicationRequiresPointerMode ApplicationModeAtConstruction { get; }
        public Windows.UI.Xaml.ApplicationTheme ApplicationThemeAtConstruction { get; }
        public static System.Action<Frame, System.Type> NavigationObserver;
        public static System.Exception NavigationException;
        public static bool NavigateSucceeds = true;
        public Frame()
        {
            ConstructionCount++;
            ApplicationModeAtConstruction = Windows.UI.Xaml.Application.Current.RequiresPointerMode;
            ApplicationThemeAtConstruction = Windows.UI.Xaml.Application.Current.RequestedTheme;
        }
        public bool Navigate(System.Type pageType)
        {
            NavigationCount++;
            NavigationObserver?.Invoke(this, pageType);
            if (NavigationException != null) throw NavigationException;
            if (!NavigateSucceeds) return false;
            Content = System.Activator.CreateInstance(pageType);
            return true;
        }
        public static void Reset()
        {
            ConstructionCount = 0; NavigationObserver = null;
            NavigationException = null; NavigateSucceeds = true;
        }
    }
}
namespace Windows.UI.ViewManagement
{
    public enum ApplicationViewBoundsMode { UseVisible, UseCoreWindow }
    public sealed class ApplicationView
    {
        private static readonly ApplicationView Current = new ApplicationView();
        public int SetBoundsCount { get; private set; }
        public ApplicationViewBoundsMode BoundsMode { get; private set; }
        public static ApplicationView GetForCurrentView() => Current;
        public bool SetDesiredBoundsMode(ApplicationViewBoundsMode value) { SetBoundsCount++; BoundsMode = value; return true; }
        public static void Reset() { Current.SetBoundsCount = 0; Current.BoundsMode = ApplicationViewBoundsMode.UseVisible; }
    }
}
namespace BiliXbox.Services
{
    // Records calls made by the unchanged production App. The production
    // diagnostics implementation and its storage behavior are outside this test.
    internal static class StartupDiagnostics
    {
        public static string Stage { get; private set; }
        public static string LastFailureSummary { get; private set; }
        public static int FailureCount { get; private set; }
        public static string FailureStage { get; private set; }
        public static System.Exception Failure { get; private set; }
        public static void Mark(string stage) { Stage = stage; }
        public static void RecordFailure(string stage, System.Exception error)
        {
            FailureCount++; FailureStage = stage; Failure = error;
            LastFailureSummary = stage + "\n" + error.GetType().Name + " · 0x" + error.HResult.ToString("X8");
        }
        public static void Reset()
        {
            Stage = "startup"; LastFailureSummary = ""; FailureCount = 0;
            FailureStage = null; Failure = null;
        }
    }
}
namespace BiliXbox.Controls
{
    internal sealed class StartupFailurePage
    {
        public string Details { get; }
        private readonly System.Action _retry;
        public StartupFailurePage(string details, System.Action retry) { Details = details; _retry = retry; }
        public void Retry() { _retry(); }
    }
}
namespace BiliXbox
{
    sealed partial class App
    {
        // Production generated InitializeComponent does not assign the pointer
        // mode. Do not make a fixture silently supply the missing startup code.
        private void InitializeComponent() { }
    }
    public sealed class MainPage
    {
        public static int ConstructionCount;
        public int ResumeCount { get; private set; }
        public int DisplayResumeCount { get; private set; }
        public int SuspendCount { get; private set; }
        public System.Threading.Tasks.Task SuspendTask;
        public MainPage() { ConstructionCount++; }
        public void ResumeGamepadInput() { ResumeCount++; }
        public void ResumeDisplayPlayback() { DisplayResumeCount++; }
        public void SuspendPlayback() { SuspendCount++; }
        public System.Threading.Tasks.Task SuspendPlaybackAsync()
        {
            SuspendPlayback();
            return SuspendTask ?? System.Threading.Tasks.Task.CompletedTask;
        }
    }
}
