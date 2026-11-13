using System;
using System.Threading.Tasks;
using Windows.Foundation.Metadata;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.UI.ViewManagement.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace BiliXbox.Controls
{
    public sealed partial class AccountLoginDialog
    {
        private int _passwordInputGeneration;

        private static bool IsAcceptKey(KeyRoutedEventArgs args)
        {
            // Xbox maps A to Space; OriginalKey retains GamepadA. Never treat
            // a physical Space in an active native editor as another command.
            return args.Key == VirtualKey.GamepadA || args.Key == VirtualKey.NavigationAccept || args.Key == VirtualKey.Enter
                || args.OriginalKey == VirtualKey.GamepadA || args.OriginalKey == VirtualKey.NavigationAccept;
        }

        private static bool IsBackKey(KeyRoutedEventArgs args)
        {
            return args.Key == VirtualKey.GamepadB || args.Key == VirtualKey.NavigationCancel || args.Key == VirtualKey.Escape
                || args.OriginalKey == VirtualKey.GamepadB || args.OriginalKey == VirtualKey.NavigationCancel;
        }

        private void CountryKeyDown(object sender, KeyRoutedEventArgs args)
        {
            if (_closed || _busy || !_country.IsEnabled || _country.IsDropDownOpen || !IsAcceptKey(args)) return;
            args.Handled = true;
            // The country selector opens on the first press, without Xbox's
            // green focus-engagement step. Its open list retains native keys.
            _country.IsDropDownOpen = true;
        }

        private async Task EditTextAsync(TextBox previous)
        {
            if (_closed || _busy || _input?.IsActive == true || _passwordEditing || previous == null) return;
            TextBox editor = null;
            try
            {
                bool account = ReferenceEquals(previous, _account), phone = ReferenceEquals(previous, _phone), code = ReferenceEquals(previous, _code);
                if (!account && !phone && !code) return;
                editor = InlineNativeTextInputSession.ReplaceEditor(previous, previous.MaxLength);
                AttachEditor(editor);
                editor.IsTextPredictionEnabled = false;
                if (account) _account = editor;
                else if (phone) { _phone = editor; editor.TextChanged += (s, e) => InvalidateSmsTicket(); }
                else _code = editor;
                using (var session = new InlineNativeTextInputSession(editor, false))
                {
                    _input = session;
                    await session.ShowAsync();
                    if (ReferenceEquals(_input, session)) _input = null;
                }
                if (!_closed) { editor.IsReadOnly = true; editor.IsFocusEngagementEnabled = false; }
            }
            catch (Exception) { if (!_closed) _notice.Text = "无法打开系统键盘，请重新选择输入框。"; }
            finally
            {
                _input = null;
                if (!_closed && editor != null) { editor.IsReadOnly = true; editor.IsFocusEngagementEnabled = false; }
                _lastKeyboardBack = DateTimeOffset.UtcNow;
            }
        }

        private void EditPassword()
        {
            if (_closed || _busy || _passwordEditing || _input?.IsActive == true) return;
            try
            {
                // A renewed native PasswordBox lets Xbox reopen its keyboard
                // after B without ever putting the secret into a TextBox.
                string value = _password.Password;
                var previous = _password;
                var replacement = PasswordEditor(); replacement.Password = value; value = null;
                _passwordHost.Children.Clear(); _passwordHost.Children.Add(replacement); _password = replacement;
                previous.Password = string.Empty;
                _passwordEditing = true; _nativePaneShown = false;
                replacement.IsFocusEngagementEnabled = true;
                _ = ShowPasswordKeyboardAsync(replacement, ++_passwordInputGeneration);
            }
            catch (Exception) { _passwordEditing = false; if (!_closed) _notice.Text = "无法打开密码键盘，请重新选择密码框。"; }
        }

        private async Task ShowPasswordKeyboardAsync(PasswordBox editor, int generation)
        {
            try
            {
                // Replacing the PasswordBox during PreviewKeyDown must finish
                // layout before its native focus / keyboard request is made.
                for (int attempt = 0; attempt < 3 && !_closed && _passwordEditing && generation == _passwordInputGeneration; ++attempt)
                {
                    bool focused = false;
                    await editor.Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                    {
                        if (_closed || !_passwordEditing || generation != _passwordInputGeneration || !ReferenceEquals(editor, _password)) return;
                        editor.UpdateLayout();
                        if (!editor.Focus(FocusState.Programmatic)) return;
                        focused = true; editor.IsFocusEngaged = true;
                        bool shown = false;
                        try
                        {
                            if (ApiInformation.IsMethodPresent("Windows.UI.ViewManagement.Core.CoreInputView", "TryShow", 1))
                                shown = CoreInputView.GetForCurrentView().TryShow(CoreInputViewKind.Keyboard);
                        }
                        catch (Exception) { }
                        if (!shown) _inputPane.TryShow();
                    });
                    if (focused || _closed || !_passwordEditing || generation != _passwordInputGeneration) return;
                    if (attempt < 2) await Task.Delay(16);
                }
                if (!_closed && _passwordEditing && generation == _passwordInputGeneration)
                { EndPassword(false); _notice.Text = "无法聚焦密码框，请重新选择密码框。"; }
            }
            catch (Exception)
            {
                if (!_closed && generation == _passwordInputGeneration)
                { EndPassword(false); _notice.Text = "无法打开密码键盘，请重新选择密码框。"; }
            }
        }

        private void PaneShowing(InputPane sender, InputPaneVisibilityEventArgs args)
        {
            if (_passwordEditing || ReferenceEquals(FocusManager.GetFocusedElement(), _password)) { _passwordEditing = true; _nativePaneShown = true; }
        }
        private void PaneHiding(InputPane sender, InputPaneVisibilityEventArgs args)
        {
            if (_passwordEditing && _nativePaneShown) EndPassword(true);
        }
        private void EndPassword(bool focusButton)
        {
            if (!_passwordEditing) return;
            ++_passwordInputGeneration;
            _passwordEditing = false; _nativePaneShown = false; _lastKeyboardBack = DateTimeOffset.UtcNow;
            if (_password.IsFocusEngaged) _password.IsFocusEngaged = false;
            _password.IsFocusEngagementEnabled = false;
            _inputPane.TryHide();
            if (focusButton && !_closed) _submitPassword.Focus(FocusState.Keyboard);
        }
        private void EndInput(bool focusButton)
        {
            _input?.Cancel(); EndPassword(focusButton);
            if (_account.IsFocusEngaged) _account.IsFocusEngaged = false;
            if (_phone.IsFocusEngaged) _phone.IsFocusEngaged = false;
            if (_code.IsFocusEngaged) _code.IsFocusEngaged = false;
        }
        public bool RequestBack()
        {
            if (_closed) return true;
            // Verification is the foremost surface, even if an older native
            // editor/drop-down still reports focus while the WebView is active.
            if (_challengeOwner != null || _captcha != null) { CancelChallenge(); return true; }
            if (_input?.IsActive == true || _passwordEditing)
            {
                EndInput(true); _lastKeyboardBack = DateTimeOffset.UtcNow; return true;
            }
            if (_country.IsDropDownOpen) { _country.IsDropDownOpen = false; _lastKeyboardBack = DateTimeOffset.UtcNow; return true; }
            // CoreWindow and routed B can describe the same press. Let the
            // first one close the native keyboard without dismissing login.
            if (DateTimeOffset.UtcNow - _lastKeyboardBack < TimeSpan.FromMilliseconds(300)) return true;
            Hide(); return true;
        }
        private void DialogKeyDown(object sender, KeyRoutedEventArgs args)
        {
            if (IsBackKey(args))
            {
                args.Handled = true;
                if (!args.KeyStatus.WasKeyDown) RequestBack();
            }
        }
        private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
        {
            if (_closed || _won) return;
            if (_challengeOwner != null || _captcha != null) { args.Cancel = true; CancelChallenge(); }
            else if (_input?.IsActive == true || _passwordEditing) { args.Cancel = true; EndInput(true); _lastKeyboardBack = DateTimeOffset.UtcNow; }
            else if (_country.IsDropDownOpen) { args.Cancel = true; _country.IsDropDownOpen = false; _lastKeyboardBack = DateTimeOffset.UtcNow; }
            else if (DateTimeOffset.UtcNow - _lastKeyboardBack < TimeSpan.FromMilliseconds(300)) args.Cancel = true;
        }

        public void Dispose()
        {
            if (_closed) return;
            _closed = true; ++_operation; _won = true;
            StopCaptchaBackPolling();
            DetachCaptchaBackInput();
            EndInput(false); _input?.Dispose(); _input = null;
            CancelQr(); _challengeOwner?.Cancel(); _captcha?.Cancel();
            _lifetime.Cancel(); _lifetime.Dispose();
            _cooldown.Stop(); _inputPane.Showing -= PaneShowing; _inputPane.Hiding -= PaneHiding;
            _password.Password = string.Empty; _account.Text = _phone.Text = _code.Text = string.Empty;
            _smsTicket = null; _pendingChallenge = null; _secondaryUri = null; _qrImage.Source = null;
        }
    }
}
