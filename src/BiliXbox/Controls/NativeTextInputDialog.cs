using System;
using System.Diagnostics;
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
    /// <summary>
    /// A native TextBox hosted in a small dialog. All keys, IME and candidate UI belong to Xbox.
    /// Create a new dialog for every request so a cancelled native editing session is not reused.
    /// </summary>
    public sealed class NativeTextInputDialog : ContentDialog
    {
        private readonly InputPane _inputPane;
        private readonly int _maxLength;
        private readonly string _editorName;
        private readonly string _placeholder;
        private TextBox _editor;
        private bool _isOpen, _nativeInputVisible, _closeRequested, _openingKeyboard;
        private long _lastBackTimestamp, _lastNativeHideTimestamp;
        private int _showGeneration;

        public string QueryText => _editor.Text;

        public NativeTextInputDialog(string initialText, string title = "搜索视频", string primaryButtonText = "搜索", int maxLength = 200)
        {
            if (maxLength < 1) throw new ArgumentOutOfRangeException(nameof(maxLength));
            _maxLength = maxLength;
            _editorName = title + "输入内容";
            _placeholder = primaryButtonText == "搜索" ? (title == "搜索用户" ? "输入用户昵称" : "搜索视频或用户、BV 号或视频链接") : "输入内容";
            Title = title;
            PrimaryButtonText = primaryButtonText;
            CloseButtonText = "取消";
            DefaultButton = ContentDialogButton.Primary;
            RequestedTheme = ElementTheme.Dark;
            Resources["ContentDialogMaxWidth"] = 780d;
            RequiresPointer = RequiresPointer.Never;
            XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Enabled;
            _inputPane = InputPane.GetForCurrentView();
            string initial = initialText ?? "";
            ReplaceEditor(initial, initial.Length);
            Opened += Dialog_Opened;
            Closing += Dialog_Closing;
            Closed += Dialog_Closed;
            CloseButtonClick += (sender, args) => _closeRequested = true;
            // Enter / the native Search action is handled by ContentDialog's default button.
            // Do not synthesize another submission from the native keyboard's Enter event.
            PrimaryButtonClick += (sender, args) =>
            {
                if (string.IsNullOrWhiteSpace(QueryText)) args.Cancel = true;
                else _closeRequested = true;
            };
            AddHandler(KeyDownEvent, new KeyEventHandler(Dialog_KeyDown), true);
        }

        private void ReplaceEditor(string text, int selection)
        {
            if (_editor != null && _editor.IsFocusEngaged) _editor.IsFocusEngaged = false;
            _editor = new TextBox
            {
                Text = text,
                MaxLength = _maxLength,
                PlaceholderText = _placeholder,
                AcceptsReturn = false,
                TextWrapping = TextWrapping.NoWrap,
                FontSize = 22,
                MinHeight = 48,
                MinWidth = 280,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                IsFocusEngagementEnabled = true,
                IsSpellCheckEnabled = false
            };
            var scope = new InputScope();
            scope.Names.Add(new InputScopeName { NameValue = InputScopeNameValue.Search });
            _editor.InputScope = scope;
            AutomationProperties.SetName(_editor, _editorName);
            _editor.Select(Math.Max(0, Math.Min(selection, text.Length)), 0);
            _editor.TextChanged += (sender, args) => IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(QueryText);
            Content = _editor;
            IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(text);
        }

        private async void Dialog_Opened(ContentDialog sender, ContentDialogOpenedEventArgs args)
        {
            _isOpen = true;
            _closeRequested = false;
            _inputPane.Showing += InputPane_Showing;
            _inputPane.Hiding += InputPane_Hiding;
            await ShowKeyboardAsync(false);
        }

        private async Task ShowKeyboardAsync(bool replaceEditor)
        {
            if (!_isOpen || _openingKeyboard) return;
            _openingKeyboard = true;
            int generation = ++_showGeneration;
            try
            {
                if (replaceEditor) ReplaceEditor(_editor.Text, _editor.SelectionStart);
                // Opened is followed by the dialog's focus setup. Wait one dispatcher pass so
                // that setup cannot take focus away immediately after the OS keyboard request.
                await Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                {
                    if (!_isOpen || generation != _showGeneration) return;
                    if (!_editor.Focus(FocusState.Programmatic)) return;
                    _editor.IsFocusEngaged = true;
                    bool accepted = false;
                    try
                    {
                        if (ApiInformation.IsMethodPresent("Windows.UI.ViewManagement.Core.CoreInputView", "TryShow", 1))
                            accepted = CoreInputView.GetForCurrentView().TryShow(CoreInputViewKind.Keyboard);
                    }
                    catch (Exception)
                    {
                        // A device family may expose the API without implementing this input view.
                    }
                    if (!accepted) _inputPane.TryShow();
                });
            }
            finally { _openingKeyboard = false; }
        }

        private void InputPane_Showing(InputPane sender, InputPaneVisibilityEventArgs args)
        {
            _nativeInputVisible = true;
            _lastNativeHideTimestamp = 0;
        }

        private void InputPane_Hiding(InputPane sender, InputPaneVisibilityEventArgs args)
        {
            _nativeInputVisible = false;
            _lastNativeHideTimestamp = Stopwatch.GetTimestamp();
            if (_editor.IsFocusEngaged) _editor.IsFocusEngaged = false;
        }

        private async void Dialog_KeyDown(object sender, KeyRoutedEventArgs args)
        {
            if (args.Key == VirtualKey.GamepadB || args.Key == VirtualKey.Escape)
            {
                args.Handled = true;
                RequestBack();
            }
            else if (!_nativeInputVisible && (args.Key == VirtualKey.GamepadY || args.Key == VirtualKey.F3 ||
                (args.Key == VirtualKey.GamepadA && ReferenceEquals(FocusManager.GetFocusedElement(), _editor))))
            {
                // After B dismisses the keyboard, A on the editor or Y starts a fresh native
                // input session; the dismissed editor does not retain stale engagement state.
                args.Handled = true;
                await ShowKeyboardAsync(true);
            }
        }

        /// <summary>Consumes one back press across routed keys, shell BackRequested and Closing.</summary>
        public void RequestBack()
        {
            if (!_isOpen) return;
            long now = Stopwatch.GetTimestamp();
            if (WasRecent(_lastBackTimestamp, now)) return;
            _lastBackTimestamp = now;
            ++_showGeneration;
            // Xbox may report the keyboard Hiding before forwarding the same B to the app.
            if (_nativeInputVisible || WasRecent(_lastNativeHideTimestamp, now))
            {
                _inputPane.TryHide();
                _nativeInputVisible = false;
                if (_editor.IsFocusEngaged) _editor.IsFocusEngaged = false;
                return;
            }
            _closeRequested = true;
            Hide();
        }

        // Suspension and navigation may explicitly close the dialog while Xbox's keyboard is
        // visible. They must not be mistaken for a back press by the Closing guard below.
        public new void Hide()
        {
            _closeRequested = true;
            base.Hide();
        }

        private void Dialog_Closing(ContentDialog sender, ContentDialogClosingEventArgs args)
        {
            if (_closeRequested || args.Result != ContentDialogResult.None) return;
            long now = Stopwatch.GetTimestamp();
            if (_nativeInputVisible || WasRecent(_lastNativeHideTimestamp, now))
            {
                // ContentDialog's own B handler can run before the routed key handler.
                args.Cancel = true;
                RequestBack();
            }
        }

        private void Dialog_Closed(ContentDialog sender, ContentDialogClosedEventArgs args)
        {
            _isOpen = false;
            ++_showGeneration;
            _inputPane.Showing -= InputPane_Showing;
            _inputPane.Hiding -= InputPane_Hiding;
            if (_editor.IsFocusEngaged) _editor.IsFocusEngaged = false;
            _inputPane.TryHide();
        }

        private static bool WasRecent(long previous, long now)
        {
            return previous != 0 && (now - previous) * 1000d / Stopwatch.Frequency < 250;
        }
    }
}
