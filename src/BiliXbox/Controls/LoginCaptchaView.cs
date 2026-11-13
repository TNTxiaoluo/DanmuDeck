using System;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Models;
using BiliXbox.Services;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace BiliXbox.Controls
{
    /// <summary>Hosts only the provider's human verification, never account credentials.</summary>
    public sealed class LoginCaptchaView : Grid, IDisposable
    {
        private WebView _browser;
        private TaskCompletionSource<LoginCaptchaProof> _pending;
        private LoginCaptchaBridge _bridge;
        private bool _disposed;
        public event Action<string> StatusChanged;
        public event Action CancelRequested;

        public LoginCaptchaView()
        {
            // The dialog owns the bounded verification viewport. A fixed height
            // here used to crop the provider's instructions and confirm button.
            HorizontalAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Stretch;
            PreviewKeyDown += VerificationKeyDown;
            AddHandler(KeyDownEvent, new KeyEventHandler(VerificationKeyDown), true);
        }

        public async Task<LoginCaptchaProof> VerifyAsync(LoginCaptchaChallenge challenge, CancellationToken ct)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(LoginCaptchaView));
            if (challenge == null) throw new ArgumentNullException(nameof(challenge));
            ct.ThrowIfCancellationRequested();
            Cancel(); ReleaseBrowser();
            var completion = new TaskCompletionSource<LoginCaptchaProof>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending = completion;
            string nonce = Guid.NewGuid().ToString("N");
            _bridge = new LoginCaptchaBridge(nonce, challenge.Challenge);
            _browser = new WebView(WebViewExecutionMode.SeparateProcess)
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            _browser.ScriptNotify += Browser_Notify;
            _browser.NavigationStarting += Browser_NavigationStarting;
            _browser.NavigationCompleted += Browser_NavigationCompleted;
            _browser.NewWindowRequested += Browser_NewWindowRequested;
            Children.Add(_browser);
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromMinutes(3));
                using (timeout.Token.Register(() => completion.TrySetCanceled()))
                {
                    try
                    {
                        _browser.NavigateToString(LoginCaptchaBridge.BuildPage(challenge.Gt, challenge.Challenge, nonce));
                        return await completion.Task;
                    }
                    finally
                    {
                        if (ReferenceEquals(_pending, completion)) { _pending = null; _bridge = null; ReleaseBrowser(); }
                    }
                }
            }
        }

        private void Browser_Notify(object sender, NotifyEventArgs args)
        {
            if (!ReferenceEquals(sender, _browser) || _pending == null || _bridge == null) return;
            // NavigateToString has about:blank (or null) as its caller. Never trust
            // notifications from a remote page reached through an SDK link.
            if (args.CallingUri != null && !string.Equals(args.CallingUri.AbsoluteUri, "about:blank", StringComparison.OrdinalIgnoreCase)) return;
            string state; LoginCaptchaProof proof;
            if (!_bridge.TryRead(args.Value, out state, out proof)) return;
            if (state == "success") _pending.TrySetResult(proof);
            else if (state == "ready") StatusChanged?.Invoke("请完成 B 站安全验证");
            else if (state == "cancel") RequestBack();
            else if (state == "error") _pending.TrySetException(new InvalidOperationException("无法加载 B 站安全验证，请稍后重试或扫码登录。"));
        }

        private void Browser_NavigationStarting(WebView sender, WebViewNavigationStartingEventArgs args)
        {
            if (args.Uri != null && !string.Equals(args.Uri.AbsoluteUri, "about:blank", StringComparison.OrdinalIgnoreCase)) args.Cancel = true;
        }
        private void Browser_NavigationCompleted(WebView sender, WebViewNavigationCompletedEventArgs args)
        {
            if (ReferenceEquals(sender, _browser) && _pending != null && !args.IsSuccess)
                _pending.TrySetException(new InvalidOperationException("无法加载 B 站安全验证，请稍后重试。"));
            else if (ReferenceEquals(sender, _browser) && _pending != null) FocusVerification();
        }
        public bool FocusVerification() => _browser != null && _browser.Focus(FocusState.Programmatic);
        private void VerificationKeyDown(object sender, KeyRoutedEventArgs args)
        {
            if (args.Handled) return;
            if (args.Key != VirtualKey.GamepadB && args.Key != VirtualKey.NavigationCancel && args.Key != VirtualKey.Escape
                && args.OriginalKey != VirtualKey.GamepadB && args.OriginalKey != VirtualKey.NavigationCancel) return;
            args.Handled = true;
            RequestBack();
        }
        public bool RequestBack()
        {
            if (_pending == null || _pending.Task.IsCompleted) return false;
            Cancel();
            CancelRequested?.Invoke();
            return true;
        }
        private void Browser_NewWindowRequested(WebView sender, WebViewNewWindowRequestedEventArgs args) { args.Handled = true; }
        public void Cancel() { _pending?.TrySetCanceled(); }
        private void ReleaseBrowser()
        {
            if (_browser == null) return;
            _browser.ScriptNotify -= Browser_Notify;
            _browser.NavigationStarting -= Browser_NavigationStarting;
            _browser.NavigationCompleted -= Browser_NavigationCompleted;
            _browser.NewWindowRequested -= Browser_NewWindowRequested;
            try { _browser.Stop(); _browser.NavigateToString(""); } catch (Exception) { }
            Children.Remove(_browser); _browser = null;
        }
        public void Dispose() { if (_disposed) return; _disposed = true; Cancel(); _bridge = null; ReleaseBrowser(); }
    }
}
