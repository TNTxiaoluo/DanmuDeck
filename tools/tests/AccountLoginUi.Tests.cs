using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Models;

namespace BiliXbox.Controls
{
    public enum Visibility { Visible, Collapsed }
    public enum FocusState { Keyboard }
    public class Control
    {
        public bool IsEnabled = true;
        public int FocusCalls;
        public Visibility Visibility = Visibility.Visible;
        public bool Focus(FocusState state) { ++FocusCalls; return true; }
    }
    public sealed class TextBox : Control { public string Text = ""; }
    public sealed class PasswordBox : Control { public string Password = ""; }
    public sealed class TextBlock : Control { public string Text = ""; }
    public sealed class Button : Control { public object Content; }
    public sealed class StackPanel : Control { }
    public sealed class ProgressRing : Control { public bool IsActive; }
    public sealed class ComboBox : Control { public object SelectedItem; public List<object> Items = new List<object>(); }
    public sealed class DispatcherTimer { public bool Running; public void Start() { Running = true; } public void Stop() { Running = false; } }
    public sealed class FakeApi
    {
        public int PasswordPosts, SmsPosts, SmsLoginPosts;
        public string PasswordCaptured;
        public Func<Task<AccountLoginResult>> PasswordResult = () => Task.FromResult(new AccountLoginResult { Status = AccountLoginStatus.RequiresSecondaryVerification });
        public Func<Task<SmsLoginTicket>> SmsResult = () => Task.FromResult(new SmsLoginTicket { Phone = "1234567", CountryId = 1, ResendAfter = TimeSpan.FromSeconds(75) });
        public Func<Task<AccountLoginResult>> SmsLoginResult = () => Task.FromResult(new AccountLoginResult { Status = AccountLoginStatus.Success });
        public Task<AccountLoginResult> LoginWithPasswordAsync(string account, string password, LoginCaptchaChallenge challenge, LoginCaptchaProof proof, CancellationToken ct)
        { ++PasswordPosts; PasswordCaptured = password; return PasswordResult(); }
        public Task<SmsLoginTicket> SendLoginSmsAsync(string phone, int country, LoginCaptchaChallenge challenge, LoginCaptchaProof proof, CancellationToken ct)
        { ++SmsPosts; return SmsResult(); }
        public Task<AccountLoginResult> LoginWithSmsAsync(SmsLoginTicket ticket, string code, CancellationToken ct)
        { ++SmsLoginPosts; return SmsLoginResult(); }
    }
    public sealed partial class AccountLoginDialog
    {
        private readonly FakeApi _api = new FakeApi();
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private TextBox _account = new TextBox(), _phone = new TextBox(), _code = new TextBox();
        private PasswordBox _password = new PasswordBox();
        private readonly TextBlock _notice = new TextBlock(), _qrHint = new TextBlock(), _modeTitle = new TextBlock();
        private readonly Button _verifySecondary = new Button(), _submitPassword = new Button(), _submitSms = new Button(), _switchMode = new Button(), _refreshQr = new Button(), _sendSms = new Button();
        private readonly StackPanel _passwordForm = new StackPanel(), _smsForm = new StackPanel();
        private readonly ComboBox _country = new ComboBox();
        private readonly ProgressRing _working = new ProgressRing();
        private readonly DispatcherTimer _cooldown = new DispatcherTimer();
        private SmsLoginTicket _smsTicket;
        private LoginCaptchaChallenge _pendingChallenge;
        private Uri _secondaryUri;
        private DateTimeOffset _resendAt;
        private int _operation, QrCancels, QrStarts, Captchas, Hides, InputEnds;
        private bool _closed, _busy, _won, _smsMode;
        private Func<Task<LoginCaptchaProof>> Proof = () => Task.FromResult(new LoginCaptchaProof { Challenge = "challenge", Validate = "validation", Seccode = "code" });
        private void CancelQr() { ++QrCancels; }
        private Task RunQrAsync() { ++QrStarts; return Task.CompletedTask; }
        private void EndInput(bool focus) { ++InputEnds; }
        private void Hide() { ++Hides; }
        private Task<LoginCaptchaProof> VerifyCaptchaAsync(CancellationToken token) { ++Captchas; _pendingChallenge = new LoginCaptchaChallenge(); return Proof(); }

        public static async Task RunTests()
        {
            int count = 0;
            Action<bool, string> check = (value, message) => { ++count; if (!value) throw new Exception(message); };
            Func<AccountLoginDialog> ready = () =>
            {
                var view = new AccountLoginDialog(); view._account.Text = "test@example.invalid"; view._password.Password = "offline-test-password";
                var country = new LoginCountry { Id = 1, CountryCode = "86" }; view._country.Items.Add(country); view._country.SelectedItem = country; view._phone.Text = "1234567"; return view;
            };
            var item = ready(); item._account.Text = ""; await item.LoginPasswordAsync();
            check(item._api.PasswordPosts == 0 && item.Captchas == 0 && item.QrCancels == 0, "Empty username performs no login or captcha and does not cancel QR");
            item = ready(); item._password.Password = ""; await item.LoginPasswordAsync();
            check(item._api.PasswordPosts == 0 && !item._busy, "Empty password cannot start login");

            item = ready(); var proof = new TaskCompletionSource<LoginCaptchaProof>(); item.Proof = () => proof.Task;
            var pending = item.LoginPasswordAsync();
            check(item._busy && item.QrCancels == 1 && item._password.Password == "", "Password is cleared before waiting for human verification and QR is paused");
            check(!item._switchMode.IsEnabled && !item._refreshQr.IsEnabled && !item._sendSms.IsEnabled, "Single busy operation disables alternate login and SMS actions");
            await item.LoginPasswordAsync(); await item.SendSmsAsync();
            check(item.Captchas == 1 && item._api.PasswordPosts == 0 && item._api.SmsPosts == 0, "Repeated confirmation cannot duplicate captcha or send a request");
            item.SwitchMode(); check(!item._smsMode, "Mode cannot change during login");
            proof.SetCanceled(); await pending;
            check(item._api.PasswordPosts == 0 && !item._busy && item._password.Password == "", "Cancelled human check never submits password and re-enables the form");
            check(item.QrStarts == 1 && item._pendingChallenge == null, "Failed attempt resumes QR once and discards challenge");

            item = ready(); item._api.PasswordResult = () => Task.FromResult(new AccountLoginResult { Status = AccountLoginStatus.Success }); await item.LoginPasswordAsync();
            check(item._api.PasswordPosts == 1 && item._api.PasswordCaptured == "offline-test-password", "Explicit password action sends only after verification");
            check(item._won && item.Hides == 1 && item._password.Password == "" && item._lifetime.IsCancellationRequested, "Successful login closes once, clears password and cancels other work");
            check(item.QrStarts == 0, "Successful login never restarts QR"); item.CompleteLogin(); check(item.Hides == 1, "Repeated completion cannot close or authenticate twice");

            item = ready(); item._api.PasswordResult = () => Task.FromResult(new AccountLoginResult { Status = AccountLoginStatus.RequiresSecondaryVerification, VerificationUrl = "https://passport.bilibili.com/verify" }); await item.LoginPasswordAsync();
            check(!item._won && item.Hides == 0 && item._secondaryUri != null && item._verifySecondary.Visibility == Visibility.Visible, "Additional verification stays explicit on the official host");
            check(item._password.Password == "" && !item._busy && item.QrStarts == 1, "Secondary verification does not retain password or block QR");
            item = ready(); item._secondaryUri = null; item._verifySecondary.Visibility = Visibility.Collapsed; item.ShowLoginResult(new AccountLoginResult { Status = AccountLoginStatus.RequiresSecondaryVerification, VerificationUrl = "https://bilibili.com.attacker.invalid/verify" });
            check(item._secondaryUri == null && item._verifySecondary.Visibility == Visibility.Collapsed, "Lookalike verification URLs are not offered");
            check(LoginError(new BiliXbox.Services.BilibiliApiException(-1, "bad offline-test-password"), "safe", "offline-test-password") == "safe", "Submitted secrets cannot be echoed by business error text");
            check(LoginError(new InvalidOperationException("any request details"), "safe", null) == "safe", "Transport exceptions do not leak request details");

            item = ready(); item._smsTicket = new SmsLoginTicket(); item._code.Text = "123456"; item.SwitchMode();
            check(item._smsMode && item._password.Password == "" && item._code.Text == "" && item._smsTicket == null, "Mode switch clears password, code, and old phone ticket");
            check(item._smsForm.Visibility == Visibility.Visible && item._passwordForm.Visibility == Visibility.Collapsed && item._api.SmsPosts == 0, "Selecting SMS only changes the form and never sends a message");
            item.SwitchMode(); check(!item._smsMode && item._passwordForm.Visibility == Visibility.Visible, "Can return to native password form");
            item = ready(); item._country.SelectedItem = null; await item.SendSmsAsync(); check(item._api.SmsPosts == 0 && item.Captchas == 0, "SMS requires an actual country selection");
            item = ready(); item._phone.Text = ""; await item.SendSmsAsync(); check(item._api.SmsPosts == 0, "SMS requires a phone number");
            item = ready(); await item.SendSmsAsync();
            check(item._api.SmsPosts == 1 && item.Captchas == 1 && item._smsTicket != null, "Explicit send performs one verified SMS request");
            check(item._resendAt > DateTimeOffset.UtcNow.AddSeconds(70) && item._cooldown.Running && !item._sendSms.IsEnabled, "Server resend cooldown is respected");
            await item.SendSmsAsync(); check(item._api.SmsPosts == 1 && item.Captchas == 1, "Cooldown prevents duplicate SMS or new captcha");
            item._resendAt = DateTimeOffset.UtcNow.AddSeconds(-1); item.UpdateCooldown(); check(item._sendSms.IsEnabled && !item._cooldown.Running, "Cooldown expiry only enables an explicit send");
            item.InvalidateSmsTicket(); check(item._smsTicket == null && item._code.Text == "", "Editing phone or country invalidates old SMS credentials");

            item = ready(); item._api.SmsResult = () => { throw new OperationCanceledException(); }; await item.SendSmsAsync();
            check(item._api.SmsPosts == 1 && item._smsTicket == null && item._resendAt > DateTimeOffset.UtcNow.AddSeconds(55), "Uncertain SMS outcome enforces local cooldown without retry");

            item = ready(); item.SwitchMode(); item._smsTicket = new SmsLoginTicket(); item._code.Text = "123456";
            item._api.SmsResult = () => { throw new BiliXbox.Services.BilibiliApiException(86103,
                "B站拒绝了本次短信请求（代码86103）。请先在最新版官方App完成注册或安全验证，再使用扫码登录或密码登录。", true, TimeSpan.Zero); };
            await item.SendSmsAsync();
            check(item._api.SmsPosts == 1 && item.Captchas == 1, "A definite 86103 response is not retried or submitted through another channel");
            check(item._smsTicket == null && item._code.Text == "", "A rejected send cannot reuse a previous phone ticket or SMS code");
            check(item._sendSms.IsEnabled && !item._cooldown.Running && item._sendSms.Content.ToString() == "获取验证码", "A definite 86103 rejection shows no fake resend countdown");
            check(item._notice.Text.Contains("86103") && item._notice.Text.Contains("官方App") && !item._notice.Text.Contains("已发送") && !item._notice.Text.Contains("结果未确认"), "86103 explains the official App requirement without claiming success or an unknown send result");
            check(item._refreshQr.FocusCalls == 1 && item._smsMode && item._smsForm.Visibility == Visibility.Visible, "86103 offers QR recovery focus while keeping the user-selected login mode");
            check(item._api.PasswordPosts == 0 && item._api.SmsLoginPosts == 0 && !item._won && item.Hides == 0 && !item._busy && item.QrStarts == 1, "A rejected send leaves QR/password choices available and does not authenticate or close the page");
            await item.LoginSmsAsync(); check(item._api.SmsLoginPosts == 0, "Submitting after 86103 cannot log in with a stale SMS ticket");
            item.SwitchMode(); check(!item._smsMode && item._passwordForm.Visibility == Visibility.Visible, "Password login remains explicitly selectable after 86103");

            item = ready(); item._api.SmsResult = () => { throw new BiliXbox.Services.BilibiliApiException(1003,
                "请求频繁，请稍后重试。", true, TimeSpan.FromSeconds(42)); }; await item.SendSmsAsync();
            check(item._resendAt > DateTimeOffset.UtcNow.AddSeconds(38) && item._resendAt < DateTimeOffset.UtcNow.AddSeconds(44)
                && item._cooldown.Running && !item._sendSms.IsEnabled, "A definite rate-limit response retains its actual cooldown instead of clearing it or inventing sixty seconds");
            await item.SendSmsAsync(); check(item._api.SmsPosts == 1 && item.Captchas == 1, "A known server cooldown still blocks duplicate sends and captcha requests");

            item = ready(); item._api.SmsResult = () => { throw new BiliXbox.Services.BilibiliApiException(-105,
                "请重新完成安全验证。", true, TimeSpan.Zero); }; await item.SendSmsAsync();
            check(item._sendSms.IsEnabled && !item._cooldown.Running && item._sendSms.FocusCalls == 1, "A definite rejected captcha permits a new explicit attempt without a send countdown");
            check(item._notice.Text == "请重新完成安全验证。" && item._api.SmsPosts == 1, "Definite rejection preserves the useful service instruction without retrying");

            item = ready(); item._api.SmsResult = () => { throw new BiliXbox.Services.BilibiliApiException(1002,
                "blocked 1234567", true, TimeSpan.Zero); }; await item.SendSmsAsync();
            check(!item._notice.Text.Contains("1234567") && item._sendSms.IsEnabled, "An explicit service rejection cannot echo the submitted phone number");

            item = ready(); item.Proof = () => { throw new OperationCanceledException(); }; await item.SendSmsAsync();
            check(item._api.SmsPosts == 0 && !item._cooldown.Running && item._sendSms.IsEnabled && item._sendSms.FocusCalls == 1,
                "Cancelling human verification before sending returns to the send action without a countdown or SMS");

            item = ready(); item._api.SmsResult = () => { throw new BiliXbox.Services.BilibiliApiException(86103, "Unknown transport result"); }; await item.SendSmsAsync();
            check(!item._sendSms.IsEnabled && item._cooldown.Running && item._resendAt > DateTimeOffset.UtcNow.AddSeconds(55), "An unclassified API exception cannot be mistaken for a definite rejection based only on its code");
            item = ready(); item._smsTicket = new SmsLoginTicket(); item._code.Text = "123456";
            item._api.SmsResult = () => { throw new InvalidOperationException("transport details"); }; await item.SendSmsAsync();
            check(item._smsTicket == null && item._code.Text == "" && item._cooldown.Running, "A lost send result clears stale credentials and retains conservative cooldown");
            check(item._notice.Text.Contains("结果未确认") && !item._notice.Text.Contains("transport details") && item._api.SmsPosts == 1, "Unknown network errors stay accurate, private and single-submit");

            item = ready(); var sms = new TaskCompletionSource<SmsLoginTicket>(); item._api.SmsResult = () => sms.Task;
            pending = item.SendSmsAsync(); item._closed = true; item._notice.Text = "closed";
            sms.SetException(new BiliXbox.Services.BilibiliApiException(86103, "rejected", true, TimeSpan.Zero)); await pending;
            check(item._notice.Text == "closed" && !item._cooldown.Running && item._refreshQr.FocusCalls == 0 && item.QrStarts == 0, "A late 86103 rejection after dismissal cannot restart cooldown, QR or focus");
            item = ready(); sms = new TaskCompletionSource<SmsLoginTicket>(); item._api.SmsResult = () => sms.Task;
            pending = item.SendSmsAsync(); ++item._operation; item._notice.Text = "new owner";
            sms.SetException(new InvalidOperationException("late transport")); await pending;
            check(item._notice.Text == "new owner" && !item._cooldown.Running && item.QrStarts == 0 && item._busy, "A late transport failure cannot alter another operation's state or cooldown");

            item = ready(); await item.LoginSmsAsync(); check(item._api.SmsLoginPosts == 0, "No SMS ticket means no login");
            item._smsTicket = new SmsLoginTicket(); await item.LoginSmsAsync(); check(item._api.SmsLoginPosts == 0, "Empty SMS code cannot login");
            item._code.Text = "123456"; await item.LoginSmsAsync(); check(item._api.SmsLoginPosts == 1 && item.Hides == 1 && item._code.Text == "", "Explicit SMS login clears code on success");

            item = ready(); var login = new TaskCompletionSource<AccountLoginResult>(); item._api.PasswordResult = () => login.Task; pending = item.LoginPasswordAsync();
            check(item._busy && item._api.PasswordPosts == 1, "Password POST has one owner while awaiting its result");
            item._closed = true; item._notice.Text = "closed"; login.SetResult(new AccountLoginResult { Status = AccountLoginStatus.Success }); await pending;
            check(item.Hides == 0 && item._notice.Text == "closed" && item.QrStarts == 0, "Late response after dismissal cannot resurrect the dialog or start QR");
            item = ready(); item._closed = true; check(!item.BeginOperation(), "Closed login surface rejects new operations");
            item = ready(); check(item.BeginOperation() && !item.BeginOperation(), "Gate admits exactly one operation");
            int generation = item._operation; await item.EndOperationAsync(generation + 1); check(item._busy && item.QrStarts == 0, "Late finally from another generation cannot reset an active operation");
            Console.WriteLine("PASS: " + count + " production account-login UI workflow assertions (offline substitutes; no real login or SMS).");
        }
    }
}
