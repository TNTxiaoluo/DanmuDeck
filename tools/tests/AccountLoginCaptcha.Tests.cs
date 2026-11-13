namespace Windows.System
{
    public enum VirtualKey { GamepadB, NavigationCancel, Escape, GamepadA, Space, Up }
}
namespace Windows.UI.Core
{
    public enum CoreWindowActivationMode { None, ActivatedInForeground }
    public sealed class KeyEventArgs { public Windows.System.VirtualKey VirtualKey; public bool Handled; }
    public sealed class CoreWindow
    {
        public CoreWindowActivationMode ActivationMode = CoreWindowActivationMode.ActivatedInForeground;
        public event Action<CoreWindow, KeyEventArgs> KeyDown;
        public int Subscribers => KeyDown?.GetInvocationList().Length ?? 0;
        public void Raise(KeyEventArgs args) { KeyDown?.Invoke(this, args); }
    }
}
namespace Windows.UI.Xaml
{
    public enum Visibility { Visible, Collapsed }
    public enum FocusState { Keyboard }
    public sealed class Window
    {
        public static Window Current = new Window();
        public bool Visible = true;
        public CoreWindow CoreWindow = new CoreWindow();
    }
    public sealed class DispatcherTimer
    {
        public TimeSpan Interval;
        public bool Running;
        public event Action<object, object> Tick;
        public int Subscribers => Tick?.GetInvocationList().Length ?? 0;
        public void Start() { Running = true; }
        public void Stop() { Running = false; }
        public void Pulse() { if (Running) Tick?.Invoke(this, null); }
    }
}
namespace Windows.Gaming.Input
{
    [Flags] public enum GamepadButtons { None = 0, B = 1, A = 2 }
    public struct GamepadReading { public GamepadButtons Buttons; }
    public sealed class Gamepad
    {
        public static List<Gamepad> Gamepads = new List<Gamepad>();
        public GamepadButtons Buttons;
        public bool Removed;
        public GamepadReading GetCurrentReading() { if (Removed) throw new InvalidOperationException("removed"); return new GamepadReading { Buttons = Buttons }; }
    }
}
namespace Windows.UI.Xaml.Controls
{
    public class Control
    {
        public bool IsEnabled = true;
        public int Focuses;
        public Visibility Visibility;
        public bool Focus(FocusState state) { ++Focuses; return true; }
    }
    public sealed class TextBox : Control { public string Text = ""; }
    public sealed class PasswordBox : Control { public string Password = ""; }
    public sealed class TextBlock : Control { public string Text = ""; }
    public sealed class Button : Control { public object Content; }
    public sealed class StackPanel : Control { public List<object> Children = new List<object>(); }
    public sealed class ComboBox : Control { public bool IsDropDownOpen; public object SelectedItem; public List<object> Items = new List<object>(); }
    public sealed class ProgressRing : Control { public bool IsActive; }
    public class ContentDialog { }
    public sealed class ContentDialogClosingEventArgs { public bool Cancel; }
}
namespace BiliXbox.Controls
{
    public sealed class LoginCaptchaView : IDisposable
    {
        private TaskCompletionSource<LoginCaptchaProof> _proof = new TaskCompletionSource<LoginCaptchaProof>();
        private CancellationTokenRegistration _registration;
        public static LoginCaptchaView Last;
        public bool Disposed;
        public int Cancels;
        public event Action<string> StatusChanged;
        public event Action CancelRequested;
        public LoginCaptchaView() { Last = this; }
        public Task<LoginCaptchaProof> VerifyAsync(LoginCaptchaChallenge challenge, CancellationToken token)
        { _registration = token.Register(() => _proof.TrySetCanceled()); StatusChanged?.Invoke("verification"); return _proof.Task; }
        public void ReturnProof() { _proof.TrySetResult(new LoginCaptchaProof { Challenge = "offline challenge", Validate = "offline validation", Seccode = "offline seccode" }); }
        public void RequestNativeCancel() { CancelRequested?.Invoke(); }
        public void Cancel() { ++Cancels; _proof.TrySetCanceled(); }
        public void UpdateLayout() { }
        public bool FocusVerification() { return true; }
        public void Dispose() { Disposed = true; _registration.Dispose(); }
    }
    public sealed class FakeApi
    {
        public int Fetches, SmsPosts;
        public CancellationToken FetchToken;
        public Func<CancellationToken, Task<LoginCaptchaChallenge>> Challenge = token => Task.FromResult(new LoginCaptchaChallenge { Challenge = "offline challenge" });
        public Task<LoginCaptchaChallenge> GetLoginCaptchaAsync(CancellationToken token) { ++Fetches; FetchToken = token; return Challenge(token); }
        public Task<SmsLoginTicket> SendLoginSmsAsync(string phone, int country, LoginCaptchaChallenge challenge, LoginCaptchaProof proof, CancellationToken token)
        { token.ThrowIfCancellationRequested(); ++SmsPosts; return Task.FromResult(new SmsLoginTicket { Phone = phone, CountryId = country }); }
    }
    public sealed partial class AccountLoginDialog : ContentDialog
    {
        private readonly FakeApi _api = new FakeApi();
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private readonly TextBox _account = new TextBox(), _phone = new TextBox(), _code = new TextBox();
        private readonly PasswordBox _password = new PasswordBox();
        private readonly TextBlock _notice = new TextBlock(), _qrHint = new TextBlock();
        private readonly Button _verifySecondary = new Button(), _submitPassword = new Button(), _submitSms = new Button(), _switchMode = new Button(), _refreshQr = new Button(), _sendSms = new Button();
        private readonly ComboBox _country = new ComboBox();
        private readonly StackPanel _challengeHost = new StackPanel();
        private readonly ProgressRing _working = new ProgressRing();
        private readonly DispatcherTimer _cooldown = new DispatcherTimer();
        private CancellationTokenSource _challengeOwner;
        private CoreWindow _challengeWindow;
        private LoginCaptchaView _captcha;
        private FakeInput _input;
        private LoginCaptchaChallenge _pendingChallenge;
        private SmsLoginTicket _smsTicket;
        private Uri _secondaryUri;
        private int _operation, _hides, _qrRestarts, _endInputs, _challengeShows, _challengeHides;
        private DateTimeOffset _lastKeyboardBack, _resendAt;
        private bool _closed, _busy, _won, _smsMode = true, _passwordEditing, _challengeVisible;
        private void ShowChallenge(bool visible) { _challengeVisible = visible; if (visible) ++_challengeShows; else ++_challengeHides; }
        private void CaptchaStatus(string value) { if (!_closed) _notice.Text = value; }
        private void EndInput(bool focusButton) { ++_endInputs; _passwordEditing = false; _input = null; }
        private void CancelQr() { }
        private Task RunQrAsync() { ++_qrRestarts; return Task.CompletedTask; }
        private void Hide() { ++_hides; }
        private sealed class FakeInput { public bool IsActive; }
        private static AccountLoginDialog Ready()
        {
            LoginCaptchaView.Last = null;
            Gamepad.Gamepads.Clear(); Window.Current = new Window();
            var view = new AccountLoginDialog(); view._phone.Text = "1234567";
            var country = new LoginCountry { Id = 1, CountryCode = "86" }; view._country.Items.Add(country); view._country.SelectedItem = country;
            view._challengeWindow = new CoreWindow(); view._challengeWindow.KeyDown += view.CaptchaWindowKeyDown;
            return view;
        }
        public static async Task RunCaptchaTests()
        {
            int assertions = 0;
            Action<bool, string> check = (value, message) => { ++assertions; if (!value) throw new Exception(message); };
            var view = Ready(); var delayed = new TaskCompletionSource<LoginCaptchaChallenge>();
            view._api.Challenge = token => { token.Register(() => delayed.TrySetCanceled()); return delayed.Task; };
            Task pending = view.SendSmsAsync();
            check(view._busy && view._challengeOwner != null && view._challengeVisible, "Pending challenge owns cancellation before WebView exists and has a visible native return surface.");
            view.RequestBack(); await pending;
            check(view._api.FetchToken.IsCancellationRequested && view._api.SmsPosts == 0 && view._captcha == null, "B while fetching cancels the actual challenge request without sending SMS.");
            check(!view._busy && !view._challengeVisible && view._challengeOwner == null && view._sendSms.IsEnabled, "Cancelled fetch restores an enabled form and releases its owner.");
            check(view._sendSms.Focuses == 1 && view._qrRestarts == 1 && view._hides == 0, "Cancellation restores send-code focus and QR without dismissing login or auto-retrying.");

            view = Ready(); delayed = new TaskCompletionSource<LoginCaptchaChallenge>(); view._api.Challenge = token => delayed.Task;
            pending = view.SendSmsAsync(); view.RequestBack();
            var first = await Task.WhenAny(pending, Task.Delay(2000));
            check(ReferenceEquals(first, pending) && !delayed.Task.IsCompleted, "B releases the login operation before an uncooperative challenge fetch completes.");
            await pending;
            check(!view._challengeVisible && view._challengeOwner == null && view._sendSms.IsEnabled, "Delayed network cancellation does not keep verification on screen or resend disabled.");
            delayed.SetResult(new LoginCaptchaChallenge { Challenge = "late" });
            check(LoginCaptchaView.Last == null && view._api.SmsPosts == 0 && !view._busy, "A transport that returns after cancellation cannot create a late challenge or send SMS.");

            view = Ready(); delayed = new TaskCompletionSource<LoginCaptchaChallenge>(); view._api.Challenge = token => delayed.Task;
            pending = view.SendSmsAsync(); view.RequestBack(); first = await Task.WhenAny(pending, Task.Delay(2000));
            check(ReferenceEquals(first, pending) && !delayed.Task.IsCompleted, "Back also releases the UI before a delayed fetch failure.");
            await pending; delayed.SetException(new InvalidOperationException("offline late failure"));
            await ObserveCancelledChallengeAsync(delayed.Task);
            check(view._api.SmsPosts == 0 && view._captcha == null && !view._busy && view._hides == 0, "Late fetch errors are observed without reviving verification or dismissing login.");

            view = Ready(); pending = view.SendSmsAsync(); var browser = view._captcha;
            check(browser != null && view._challengeHost.Children.Count == 1, "Successful challenge fetch creates one owned browser.");
            var key = new KeyEventArgs { VirtualKey = VirtualKey.GamepadA }; view._challengeWindow.Raise(key);
            check(!key.Handled && view._captcha != null, "Window hook leaves verification confirmation and direction input intact.");
            key = new KeyEventArgs { VirtualKey = VirtualKey.GamepadB }; view._challengeWindow.Raise(key); await pending;
            check(key.Handled && browser.Disposed && view._challengeHost.Children.Count == 0, "CoreWindow B cancels WebView verification and releases browser/visuals.");
            check(view._api.SmsPosts == 0 && view._pendingChallenge == null && !view._busy, "WebView cancellation discards proof and never sends SMS.");
            view.RequestBack(); var closing = new ContentDialogClosingEventArgs(); view.OnClosing(view, closing);
            check(view._hides == 0 && closing.Cancel, "Duplicate shell/routed/closing delivery of one B cannot dismiss the restored login.");
            view._lastKeyboardBack = DateTimeOffset.UtcNow.AddSeconds(-1); view.RequestBack();
            check(view._hides == 1, "A later distinct B can leave login after verification is cancelled.");

            view = Ready(); pending = view.SendSmsAsync(); browser = view._captcha; browser.RequestNativeCancel(); await pending;
            check(browser.Disposed && view._api.SmsPosts == 0 && view._sendSms.IsEnabled, "Native return button cancels the proof task and permits an explicit retry.");
            pending = view.SendSmsAsync(); check(view._api.Fetches == 2 && view._busy, "Only another explicit send action starts a fresh challenge.");
            view._captcha.ReturnProof(); await pending;
            check(view._api.SmsPosts == 1 && view._smsTicket != null && !view._busy && view._resendAt > DateTimeOffset.UtcNow, "A completed retry sends one SMS and starts cooldown.");
            check(!view._sendSms.IsEnabled && view._sendSms.Focuses == 1, "Sent SMS keeps resend disabled and does not focus the disabled button.");

            view = Ready(); pending = view.SendSmsAsync(); view._country.IsDropDownOpen = true; view._passwordEditing = true;
            closing = new ContentDialogClosingEventArgs(); view.OnClosing(view, closing); await pending;
            check(closing.Cancel && view._api.SmsPosts == 0 && view._captcha == null, "Closing prioritizes the verification surface over stale native input/dropdown flags.");
            var window = view._challengeWindow; check(window.Subscribers == 1, "One challenge key observer is installed."); view.DetachCaptchaBackInput(); view.DetachCaptchaBackInput();
            check(window.Subscribers == 0 && view._challengeWindow == null, "Cleanup removes the window input observer idempotently.");
            key = new KeyEventArgs { VirtualKey = VirtualKey.Escape }; view.CaptchaWindowKeyDown(window, key);
            check(!key.Handled, "No active verification means window input remains untouched.");

            var gate = new LoginCaptchaBackState();
            check(!gate.Update(true, true) && !gate.Update(true, true), "Already held B cannot cancel a newly opened challenge.");
            check(!gate.Update(true, false) && gate.Update(true, true) && !gate.Update(true, true), "Raw B cancels once per released press, with no auto repeat.");
            gate.Update(true, false); gate.Update(false, false);
            check(!gate.Update(true, true), "Disconnected/hidden state requires another neutral release before cancel.");

            view = Ready(); var pad = new Gamepad { Buttons = GamepadButtons.B }; Gamepad.Gamepads.Add(pad); pending = view.SendSmsAsync();
            var timer = view._challengeBackTimer; check(timer != null && timer.Running && timer.Interval.TotalMilliseconds == 33, "Challenge owns a short-lived raw B poller.");
            timer.Pulse(); timer.Pulse(); check(view._busy, "Held B at challenge entry is ignored by the production poller.");
            pad.Buttons = GamepadButtons.None; timer.Pulse(); Window.Current.Visible = false; timer.Pulse();
            Window.Current.Visible = true; pad.Buttons = GamepadButtons.B; timer.Pulse(); check(view._busy, "Hidden-window polling resets the release barrier instead of queuing a cancellation.");
            pad.Buttons = GamepadButtons.None; timer.Pulse(); Window.Current.CoreWindow.ActivationMode = CoreWindowActivationMode.None; timer.Pulse();
            Window.Current.CoreWindow.ActivationMode = CoreWindowActivationMode.ActivatedInForeground; pad.Buttons = GamepadButtons.B; timer.Pulse(); check(view._busy, "Background window cannot queue a B cancellation for reactivation.");
            pad.Buttons = GamepadButtons.None; timer.Pulse(); pad.Buttons = GamepadButtons.B; timer.Pulse(); await pending;
            check(view._api.SmsPosts == 0 && !view._busy && view._challengeBackTimer == null && !timer.Running && timer.Subscribers == 0, "Raw B escapes a WebView input island and removes its timer/handler after cancellation.");
            timer.Pulse(); check(view._hides == 0 && view._api.Fetches == 1, "A stopped old poller cannot cancel the form or create another request.");
            Console.WriteLine("PASS: " + assertions + " production verification cancellation assertions (platform substitutes; no network, login or SMS).");
        }
    }
}
