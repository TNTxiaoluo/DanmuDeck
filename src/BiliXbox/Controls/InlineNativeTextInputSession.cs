using System;
using System.Threading.Tasks;
using Windows.Foundation.Metadata;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.UI.ViewManagement.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace BiliXbox.Controls
{
    // Owns a native keyboard session for an editor already displayed in place.
    // No dialog, overlay editor, text copy or publication action is created.
    public sealed class InlineNativeTextInputSession : IDisposable
    {
        private readonly TextBox _editor;
        private readonly bool _submitOnEnter;
        private readonly Func<bool> _allowSubmitFocus;
        private readonly InputPane _pane;
        private readonly TaskCompletionSource<bool> _completion = new TaskCompletionSource<bool>();
        private bool _started, _active, _composing, _compositionJustEnded, _nativeShown;

        public bool IsActive => _active;
        public InlineNativeTextInputSession(TextBox editor, bool submitOnEnter, Func<bool> allowSubmitFocus = null)
        {
            _editor = editor ?? throw new ArgumentNullException(nameof(editor));
            _submitOnEnter = submitOnEnter;
            _allowSubmitFocus = allowSubmitFocus;
            _pane = InputPane.GetForCurrentView();
        }

        public static TextBox ReplaceEditor(TextBox previous, int maxLength) => InlineTextBoxFactory.Replace(previous, maxLength);

        public Task<bool> ShowAsync()
        {
            if (_started || _completion.Task.IsCompleted) return _completion.Task;
            _started = _active = true;
            _pane.Showing += Pane_Showing;
            _pane.Hiding += Pane_Hiding;
            _editor.KeyDown += Editor_KeyDown;
            _editor.LostFocus += Editor_LostFocus;
            _editor.TextCompositionStarted += Composition_Started;
            _editor.TextCompositionEnded += Composition_Ended;
            _editor.IsFocusEngagementEnabled = true;
            _editor.IsReadOnly = false;
            _ = ShowKeyboardAsync();
            return _completion.Task;
        }

        private async Task ShowKeyboardAsync()
        {
            try
            {
                // A renewed editor can be attached just as its parent becomes
                // visible. One dispatcher turn does not guarantee layout has
                // made it focusable. Allow a bounded number of render passes.
                for (int attempt = 0; attempt < 3 && _active; ++attempt)
                {
                    bool focused = false;
                    await _editor.Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                    {
                        if (!_active) return;
                        _editor.UpdateLayout();
                        if (!_active || !_editor.Focus(FocusState.Programmatic) || !_active) return;
                        focused = true;
                        _editor.IsFocusEngaged = true;
                        if (!_active) return;
                        _editor.Select(_editor.Text.Length, 0);
                        bool accepted = false;
                        try
                        {
                            if (ApiInformation.IsMethodPresent("Windows.UI.ViewManagement.Core.CoreInputView", "TryShow", 1))
                                accepted = CoreInputView.GetForCurrentView().TryShow(CoreInputViewKind.Keyboard);
                        }
                        catch (Exception) { }
                        if (_active && !accepted) _pane.TryShow();
                    });
                    if (!_active || focused) return;
                    if (attempt < 2) await Task.Delay(16);
                }
                if (_active) Complete(false);
            }
            catch (Exception) { Complete(false); }
        }

        private void Editor_KeyDown(object sender, KeyRoutedEventArgs args)
        {
            if (!_active) return;
            if (args.Key == VirtualKey.GamepadB || args.Key == VirtualKey.Escape)
            { args.Handled = true; RequestBack(); }
            else if (_submitOnEnter && !_composing && !_compositionJustEnded && args.Key == VirtualKey.Enter && !string.IsNullOrWhiteSpace(_editor.Text))
            { args.Handled = true; Complete(true); }
        }

        private void Composition_Started(TextBox sender, TextCompositionStartedEventArgs args) { _composing = true; }
        private async void Composition_Ended(TextBox sender, TextCompositionEndedEventArgs args)
        {
            _composing = false;
            _compositionJustEnded = true;
            try { await _editor.Dispatcher.RunAsync(CoreDispatcherPriority.Low, () => _compositionJustEnded = false); }
            catch (Exception) { }
        }
        private void Pane_Showing(InputPane sender, InputPaneVisibilityEventArgs args) { _nativeShown = true; }
        private void Pane_Hiding(InputPane sender, InputPaneVisibilityEventArgs args)
        {
            // A queued hide from the preceding B/reopen cycle is not a close
            // event for a keyboard this session has not yet seen appear.
            if (!_nativeShown || _allowSubmitFocus?.Invoke() == true) return;
            Complete(false);
        }
        private void Editor_LostFocus(object sender, RoutedEventArgs args)
        {
            if (_active && !ReferenceEquals(FocusManager.GetFocusedElement(), _editor) && _allowSubmitFocus?.Invoke() != true) Complete(false);
        }

        public void RequestBack() => Complete(false);
        public void Cancel() => Complete(false);
        public void Complete(bool submit)
        {
            if (_completion.Task.IsCompleted) return;
            _active = false;
            if (_started)
            {
                _pane.Showing -= Pane_Showing;
                _pane.Hiding -= Pane_Hiding;
                _editor.KeyDown -= Editor_KeyDown;
                _editor.LostFocus -= Editor_LostFocus;
                _editor.TextCompositionStarted -= Composition_Started;
                _editor.TextCompositionEnded -= Composition_Ended;
            }
            if (_editor.IsFocusEngaged) _editor.IsFocusEngaged = false;
            _pane.TryHide();
            // B/Done/hide keep the text in the same editor. Only the explicit
            // search action may submit; comments always remain a draft here.
            _completion.TrySetResult(submit && !string.IsNullOrWhiteSpace(_editor.Text));
        }
        public void Dispose() => Cancel();
    }
}
