using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Models;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace BiliXbox.Controls
{
    public sealed partial class AccountLoginDialog
    {
        private DateTimeOffset _lastKeyboardBack;
        private CoreWindow _challengeWindow;

        private async void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
        {
            // A WebView owns a separate input island. Routed XAML keys are not
            // guaranteed to escape it, so observe the window's cancel key too.
            _challengeWindow = CoreWindow.GetForCurrentThread();
            if (_challengeWindow != null) _challengeWindow.KeyDown += CaptchaWindowKeyDown;
            _refreshQr.Focus(FocusState.Keyboard);
            _ = LoadCountriesAsync();
            await RunQrAsync();
        }

        private async Task LoadCountriesAsync()
        {
            try
            {
                var countries = await _api.GetLoginCountriesAsync(_lifetime.Token);
                if (_closed) return;
                _country.ItemsSource = countries;
                _country.SelectedItem = countries.FirstOrDefault(country => country.CountryCode == "86") ?? countries.FirstOrDefault();
                _country.IsEnabled = !_busy && countries.Count > 0;
                UpdateCooldown();
            }
            catch (OperationCanceledException) { }
            catch (Exception) { if (!_closed && _smsMode) _notice.Text = "暂时无法读取手机区号，请关闭登录页后重试，或使用扫码登录。"; }
        }

        private void CancelQr()
        {
            ++_qrGeneration;
            if (_qrOwner == null) return;
            _qrOwner.Cancel(); _qrOwner.Dispose(); _qrOwner = null;
        }

        private async Task RunQrAsync()
        {
            if (_closed || _busy || _won) return;
            CancelQr();
            _qrOwner = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            var token = _qrOwner.Token;
            int generation = _qrGeneration;
            _qrImage.Source = null; _qrHint.Text = "正在获取二维码…";
            try
            {
                var qr = await _api.GenerateQrLoginAsync(token);
                token.ThrowIfCancellationRequested();
                var bitmap = await _renderQr(qr.Url);
                token.ThrowIfCancellationRequested();
                if (_closed || generation != _qrGeneration) return;
                _qrImage.Source = bitmap; _qrHint.Text = "打开哔哩哔哩手机 App，扫一扫";
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(1800, token);
                    var result = await _api.PollQrLoginAsync(qr.Key, token);
                    token.ThrowIfCancellationRequested();
                    if (_closed || generation != _qrGeneration) return;
                    if (result == QrLoginStatus.Success) { CompleteLogin(); return; }
                    if (result == QrLoginStatus.Expired) { _qrImage.Source = null; _qrHint.Text = "二维码已过期，请选择刷新二维码。"; return; }
                    _qrHint.Text = result == QrLoginStatus.WaitingForConfirmation ? "已扫码，请在手机上确认登录" : "等待扫码";
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { if (!_closed && generation == _qrGeneration) _qrHint.Text = "暂时无法获取登录状态，请刷新二维码重试。"; }
        }

        private bool BeginOperation()
        {
            if (_closed || _busy || _won) return false;
            EndInput(false); CancelQr();
            ++_operation; _busy = true;
            _qrHint.Text = "正在使用右侧登录。也可取消后重新扫码。";
            _secondaryUri = null; _verifySecondary.Visibility = Visibility.Collapsed;
            SetFormEnabled(false); _working.IsActive = true; _working.Visibility = Visibility.Visible;
            return true;
        }
        private Task EndOperationAsync(int operation)
        {
            if (_closed || operation != _operation || _won) return Task.CompletedTask;
            _busy = false; _working.IsActive = false; _working.Visibility = Visibility.Collapsed;
            SetFormEnabled(true); UpdateCooldown();
            (_smsMode ? _submitSms : _submitPassword).Focus(FocusState.Keyboard);
            _ = RunQrAsync();
            return Task.CompletedTask;
        }
        private void SetFormEnabled(bool enabled)
        {
            _account.IsEnabled = enabled; _password.IsEnabled = enabled; _phone.IsEnabled = enabled; _code.IsEnabled = enabled;
            _country.IsEnabled = enabled && _country.Items.Count > 0;
            _submitPassword.IsEnabled = _submitSms.IsEnabled = _switchMode.IsEnabled = _refreshQr.IsEnabled = enabled;
            _sendSms.IsEnabled = enabled && _country.SelectedItem != null && DateTimeOffset.UtcNow >= _resendAt;
        }

        private async Task<LoginCaptchaProof> VerifyCaptchaAsync(CancellationToken token)
        {
            _notice.Text = "正在获取安全验证…";
            // Own the request before fetching its challenge. B must work while
            // that request is waiting as well as while the browser is visible.
            var owner = CancellationTokenSource.CreateLinkedTokenSource(token);
            _challengeOwner = owner;
            LoginCaptchaView captcha = null;
            try
            {
                StartCaptchaBackPolling();
                ShowChallenge(true);
                var challenge = await AwaitChallengeAsync(_api.GetLoginCaptchaAsync(owner.Token), owner.Token);
                owner.Token.ThrowIfCancellationRequested();
                if (_closed || !ReferenceEquals(_challengeOwner, owner)) throw new OperationCanceledException(owner.Token);
                captcha = new LoginCaptchaView();
                _captcha = captcha;
                captcha.StatusChanged += CaptchaStatus;
                captcha.CancelRequested += CancelChallenge;
                _challengeHost.Children.Add(captcha);
                _notice.Text = "请先完成 B 站安全验证。";
                var verification = captcha.VerifyAsync(challenge, owner.Token);
                captcha.UpdateLayout(); captcha.FocusVerification();
                var proof = await verification;
                owner.Token.ThrowIfCancellationRequested();
                _pendingChallenge = challenge;
                return proof;
            }
            finally
            {
                if (captcha != null)
                {
                    captcha.StatusChanged -= CaptchaStatus;
                    captcha.CancelRequested -= CancelChallenge;
                    captcha.Dispose(); _challengeHost.Children.Remove(captcha);
                    if (ReferenceEquals(_captcha, captcha)) _captcha = null;
                }
                if (ReferenceEquals(_challengeOwner, owner))
                {
                    StopCaptchaBackPolling();
                    _challengeOwner = null;
                    if (!_closed) ShowChallenge(false);
                }
                owner.Dispose();
            }
        }
        private LoginCaptchaChallenge _pendingChallenge;

        private static async Task<LoginCaptchaChallenge> AwaitChallengeAsync(Task<LoginCaptchaChallenge> request, CancellationToken token)
        {
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (token.Register(() => cancelled.TrySetResult(true)))
            {
                var completed = await Task.WhenAny(request, cancelled.Task);
                if (!ReferenceEquals(completed, request) || token.IsCancellationRequested)
                {
                    // A transport can take time to honour cancellation. Release
                    // the UI now and observe any later failure without touching
                    // this dialog or reusing its challenge.
                    _ = ObserveCancelledChallengeAsync(request);
                    token.ThrowIfCancellationRequested();
                }
                return await request;
            }
        }

        private static async Task ObserveCancelledChallengeAsync(Task<LoginCaptchaChallenge> request)
        {
            try { await request.ConfigureAwait(false); }
            catch (Exception) { }
        }

        private void CaptchaStatus(string value) { if (!_closed) _notice.Text = value; }
        private void CancelChallenge()
        {
            _lastKeyboardBack = DateTimeOffset.UtcNow;
            _challengeOwner?.Cancel(); _captcha?.Cancel();
        }

        private void CaptchaWindowKeyDown(CoreWindow sender, KeyEventArgs args)
        {
            if (_closed || (_challengeOwner == null && _captcha == null)) return;
            if (args.VirtualKey != VirtualKey.GamepadB && args.VirtualKey != VirtualKey.NavigationCancel && args.VirtualKey != VirtualKey.Escape) return;
            args.Handled = true;
            CancelChallenge();
        }

        private void DetachCaptchaBackInput()
        {
            if (_challengeWindow == null) return;
            _challengeWindow.KeyDown -= CaptchaWindowKeyDown;
            _challengeWindow = null;
        }

        private async Task LoginPasswordAsync()
        {
            string account = _account.Text.Trim();
            if (string.IsNullOrWhiteSpace(account) || string.IsNullOrEmpty(_password.Password)) { _notice.Text = "请输入手机号或邮箱，以及密码。"; return; }
            if (!BeginOperation()) return;
            int operation = _operation; string password = _password.Password; _password.Password = string.Empty;
            try
            {
                var proof = await VerifyCaptchaAsync(_lifetime.Token);
                _notice.Text = "正在登录…";
                var result = await _api.LoginWithPasswordAsync(account, password, _pendingChallenge, proof, _lifetime.Token);
                if (!_closed && operation == _operation) ShowLoginResult(result);
            }
            catch (OperationCanceledException) { if (!_closed) _notice.Text = "已取消本次登录。"; }
            catch (Exception ex) { if (!_closed) _notice.Text = LoginError(ex, "登录未完成，请检查账号和密码后重试。", password); }
            finally { password = null; _pendingChallenge = null; if (!_closed) _password.Password = string.Empty; await EndOperationAsync(operation); }
        }

        private async Task SendSmsAsync()
        {
            if (_closed || _busy) return;
            var country = _country.SelectedItem as LoginCountry; string phone = _phone.Text.Trim();
            if (country == null) { _notice.Text = "请选择手机所在的国家或地区区号。"; return; }
            if (string.IsNullOrWhiteSpace(phone)) { _notice.Text = "请输入接收验证码的手机号。"; return; }
            if (DateTimeOffset.UtcNow < _resendAt) return;
            if (!BeginOperation()) return;
            int operation = _operation; bool submitted = false; Control recoveryFocus = null;
            try
            {
                var proof = await VerifyCaptchaAsync(_lifetime.Token);
                _notice.Text = "正在发送验证码…"; submitted = true;
                // A new send must not leave an earlier phone ticket or code
                // available when this attempt is rejected or its result is lost.
                _smsTicket = null; _code.Text = string.Empty;
                var ticket = await _api.SendLoginSmsAsync(phone, country.Id, _pendingChallenge, proof, _lifetime.Token);
                if (_closed || operation != _operation || _won) return;
                _smsTicket = ticket;
                _resendAt = DateTimeOffset.UtcNow + (ticket.ResendAfter > TimeSpan.Zero ? ticket.ResendAfter : TimeSpan.FromSeconds(60));
                _cooldown.Start(); _notice.Text = "验证码已发送。请在下方输入，并选择验证码登录。";
            }
            catch (OperationCanceledException)
            {
                if (!_closed && operation == _operation && !_won)
                {
                    _notice.Text = submitted ? "发送结果未确认，请稍后查看短信后再试。" : "已取消验证码验证。";
                    if (submitted) { _resendAt = DateTimeOffset.UtcNow.AddSeconds(60); _cooldown.Start(); }
                }
            }
            catch (BiliXbox.Services.BilibiliApiException ex) when (ex.IsDefiniteSmsRejection)
            {
                if (!_closed && operation == _operation && !_won)
                {
                    // Only a parsed SMS rejection is definite. HTTP failures
                    // and malformed replies can still follow a delivered SMS.
                    _smsTicket = null; _code.Text = string.Empty;
                    _notice.Text = LoginError(ex, "B站拒绝了此次验证码请求，未确认发送成功。请按提示处理后重新获取，或使用扫码登录。", phone);
                    _resendAt = DateTimeOffset.UtcNow + (ex.SmsRetryAfter > TimeSpan.Zero ? ex.SmsRetryAfter : TimeSpan.Zero);
                    if (ex.SmsRetryAfter > TimeSpan.Zero) _cooldown.Start(); else _cooldown.Stop();
                    recoveryFocus = ex.Code == 86103 ? (Control)_refreshQr : _sendSms;
                }
            }
            catch (Exception ex)
            {
                if (!_closed && operation == _operation && !_won)
                {
                    _notice.Text = LoginError(ex, submitted ? "验证码发送结果未确认，请查看短信，一分钟后再试。" : "安全验证未完成，请重试。", phone);
                    if (submitted) { _resendAt = DateTimeOffset.UtcNow.AddSeconds(60); _cooldown.Start(); }
                }
            }
            finally
            {
                if (operation == _operation) _pendingChallenge = null;
                await EndOperationAsync(operation);
                // Cancellation returns to the action that opened verification.
                // A later A is an explicit retry; nothing sends automatically.
                if (!_closed && operation == _operation && !_busy && !_won)
                {
                    if (recoveryFocus != null) recoveryFocus.Focus(FocusState.Keyboard);
                    else if (!submitted) _sendSms.Focus(FocusState.Keyboard);
                }
            }
        }

        private async Task LoginSmsAsync()
        {
            if (_smsTicket == null) { _notice.Text = "请先获取这个手机号的验证码。"; return; }
            string code = _code.Text.Trim();
            if (string.IsNullOrWhiteSpace(code)) { _notice.Text = "请输入短信验证码。"; return; }
            if (!BeginOperation()) return;
            int operation = _operation;
            try
            {
                _notice.Text = "正在验证并登录…";
                var result = await _api.LoginWithSmsAsync(_smsTicket, code, _lifetime.Token);
                if (!_closed && operation == _operation) ShowLoginResult(result);
            }
            catch (OperationCanceledException) { if (!_closed) _notice.Text = "已取消本次登录。"; }
            catch (Exception ex) { if (!_closed) _notice.Text = LoginError(ex, "登录未完成，请检查验证码后重试。", code); }
            finally { code = null; if (!_closed) _code.Text = string.Empty; await EndOperationAsync(operation); }
        }

        private void ShowLoginResult(AccountLoginResult result)
        {
            if (result == null) { _notice.Text = "B 站没有返回有效登录结果，请重试。"; return; }
            if (result.Status == AccountLoginStatus.Success) { CompleteLogin(); return; }
            _password.Password = string.Empty; _code.Text = string.Empty;
            _notice.Text = "此账号需要在 B 站完成额外验证。完成后请返回，重新登录或扫码。";
            Uri uri;
            if (Uri.TryCreate(result.VerificationUrl, UriKind.Absolute, out uri) && uri.Scheme == "https"
                && (uri.Host.Equals("bilibili.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".bilibili.com", StringComparison.OrdinalIgnoreCase)))
            { _secondaryUri = uri; _verifySecondary.Visibility = Visibility.Visible; }
        }
        private void CompleteLogin()
        {
            if (_closed || _won) return;
            _won = true; _password.Password = string.Empty; _code.Text = string.Empty;
            CancelQr(); _lifetime.Cancel(); Hide();
        }
        private async Task OpenSecondaryAsync()
        {
            if (_closed || _secondaryUri == null || _busy) return;
            try { await Launcher.LaunchUriAsync(_secondaryUri); }
            catch (Exception) { if (!_closed) _notice.Text = "无法打开官方验证，请在手机 App 完成验证后扫码登录。"; }
        }
        private void SwitchMode()
        {
            if (_closed || _busy) return;
            EndInput(false); _password.Password = string.Empty; _code.Text = string.Empty; _smsTicket = null;
            _smsMode = !_smsMode; _passwordForm.Visibility = _smsMode ? Visibility.Collapsed : Visibility.Visible;
            _smsForm.Visibility = _smsMode ? Visibility.Visible : Visibility.Collapsed;
            _modeTitle.Text = _smsMode ? "短信验证码登录" : "手机号 / 邮箱登录";
            _switchMode.Content = _smsMode ? "使用密码登录" : "使用短信验证码登录";
            _notice.Text = _smsMode && _country.Items.Count == 0 ? "正在读取手机区号；暂不可用时可扫码登录。" : string.Empty;
            _secondaryUri = null; _verifySecondary.Visibility = Visibility.Collapsed;
            UpdateCooldown(); (_smsMode ? (Control)_phone : _account).Focus(FocusState.Keyboard);
        }
        private void InvalidateSmsTicket() { _smsTicket = null; if (_code != null) _code.Text = string.Empty; }
        private void UpdateCooldown()
        {
            if (_closed) return;
            int remaining = Math.Max(0, (int)Math.Ceiling((_resendAt - DateTimeOffset.UtcNow).TotalSeconds));
            _sendSms.Content = remaining > 0 ? remaining + " 秒后重发" : "获取验证码";
            _sendSms.IsEnabled = !_busy && remaining == 0 && _country.SelectedItem != null;
            if (remaining == 0) _cooldown.Stop();
        }
        private static string LoginError(Exception error, string fallback, string sensitive)
        {
            // Never display an exception that might contain submitted material.
            // Client business messages are useful only when they do not echo it.
            var apiError = error as BiliXbox.Services.BilibiliApiException;
            string message = apiError?.Message;
            if (string.IsNullOrWhiteSpace(message) || message.Length > 160 || (!string.IsNullOrEmpty(sensitive) && message.IndexOf(sensitive, StringComparison.Ordinal) >= 0)) return fallback;
            return message;
        }
    }
}
