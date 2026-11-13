namespace Windows.Foundation.Metadata
{
    public static class ApiInformation
    {
        public static bool Available = true;
        public static bool IsMethodPresent(string type, string name, uint count) => Available;
    }
}
namespace Windows.System
{
    public enum VirtualKey { GamepadB, Escape, GamepadY, F3, GamepadA, Enter }
}
namespace Windows.UI.Core
{
    public enum CoreDispatcherPriority { Low }
    public sealed class CoreDispatcher
    {
        private readonly global::System.Collections.Generic.Queue<global::System.Action> _queue =
            new global::System.Collections.Generic.Queue<global::System.Action>();
        public global::System.Threading.Tasks.Task RunAsync(CoreDispatcherPriority priority, global::System.Action callback)
        {
            var completion = new global::System.Threading.Tasks.TaskCompletionSource<bool>();
            lock (_queue) _queue.Enqueue(() => { callback(); completion.SetResult(true); });
            return completion.Task;
        }
        public int PendingCount { get { lock (_queue) return _queue.Count; } }
        public void Flush()
        {
            while (true)
            {
                global::System.Action callback;
                lock (_queue) { if (_queue.Count == 0) return; callback = _queue.Dequeue(); }
                callback();
            }
        }
    }
}
namespace Windows.UI.ViewManagement
{
    public delegate void PaneEvent(InputPane sender, InputPaneVisibilityEventArgs args);
    public sealed class InputPaneVisibilityEventArgs { }
    public sealed class InputPane
    {
        private static InputPane _current = new InputPane();
        public static InputPane GetForCurrentView() => _current;
        public static void Reset() { _current = new InputPane(); }
        public int ShowCount, HideCount;
        public bool Visible;
        public event PaneEvent Showing;
        public event PaneEvent Hiding;
        public int SubscriberCount => (Showing?.GetInvocationList().Length ?? 0) + (Hiding?.GetInvocationList().Length ?? 0);
        public bool TryShow() { ++ShowCount; return true; }
        public bool TryHide()
        {
            ++HideCount;
            if (Visible) RaiseHiding();
            return true;
        }
        public void RaiseShowing() { Visible = true; Showing?.Invoke(this, new InputPaneVisibilityEventArgs()); }
        public void RaiseHiding() { Visible = false; Hiding?.Invoke(this, new InputPaneVisibilityEventArgs()); }
    }
}
namespace Windows.UI.ViewManagement.Core
{
    public enum CoreInputViewKind { Keyboard = 1 }
    public sealed class CoreInputView
    {
        private static CoreInputView _current = new CoreInputView();
        public static CoreInputView GetForCurrentView() => _current;
        public static void Reset() { _current = new CoreInputView(); }
        public bool Accepted = true, Throw;
        public int ShowCount;
        public CoreInputViewKind Kind;
        public bool TryShow(CoreInputViewKind kind)
        {
            ++ShowCount; Kind = kind;
            if (Throw) throw new global::System.NotSupportedException();
            return Accepted;
        }
    }
}
namespace Windows.UI.Xaml
{
    public sealed class RoutedEventArgs : global::System.EventArgs { }
    public enum ElementTheme { Dark }
    public enum RequiresPointer { Never }
    public enum XYFocusKeyboardNavigationMode { Enabled }
    public enum FocusState { Programmatic }
    public enum HorizontalAlignment { Stretch }
    public enum TextWrapping { NoWrap }
}
namespace Windows.UI.Xaml.Automation
{
    public static class AutomationProperties { public static void SetName(object control, string name) { } }
}
namespace Windows.UI.Xaml.Input
{
    public enum InputScopeNameValue { Search }
    public sealed class InputScopeName { public InputScopeNameValue NameValue { get; set; } }
    public sealed class InputScope
    {
        public global::System.Collections.Generic.List<InputScopeName> Names = new global::System.Collections.Generic.List<InputScopeName>();
    }
    public sealed class KeyRoutedEventArgs
    {
        public Windows.System.VirtualKey Key;
        public bool Handled;
    }
    public delegate void KeyEventHandler(object sender, KeyRoutedEventArgs args);
    public static class FocusManager
    {
        public static object Focused;
        public static object GetFocusedElement() => Focused;
    }
}
namespace Windows.UI.Xaml.Controls
{
    public sealed class TextCompositionStartedEventArgs { }
    public sealed class TextCompositionEndedEventArgs { }
    public delegate void CompositionStarted(TextBox sender, TextCompositionStartedEventArgs args);
    public delegate void CompositionEnded(TextBox sender, TextCompositionEndedEventArgs args);
    public enum ContentDialogButton { Primary }
    public enum ContentDialogResult { None, Primary }
    public sealed class ContentDialogOpenedEventArgs { }
    public sealed class ContentDialogClosedEventArgs { }
    public sealed class ContentDialogClosingEventArgs { public bool Cancel; public ContentDialogResult Result; }
    public sealed class ContentDialogButtonClickEventArgs { public bool Cancel; }
    public delegate void OpenEvent(ContentDialog sender, ContentDialogOpenedEventArgs args);
    public delegate void ClosedEvent(ContentDialog sender, ContentDialogClosedEventArgs args);
    public delegate void ClosingEvent(ContentDialog sender, ContentDialogClosingEventArgs args);
    public delegate void ButtonEvent(ContentDialog sender, ContentDialogButtonClickEventArgs args);
    public class ContentDialog
    {
        public string Title, PrimaryButtonText, CloseButtonText;
        public ContentDialogButton DefaultButton;
        public ElementTheme RequestedTheme;
        public RequiresPointer RequiresPointer;
        public XYFocusKeyboardNavigationMode XYFocusKeyboardNavigation;
        public bool IsPrimaryButtonEnabled;
        public bool ClosedState;
        public ContentDialogResult Result;
        public object Content;
        public global::System.Collections.Generic.Dictionary<string, object> Resources = new global::System.Collections.Generic.Dictionary<string, object>();
        public Windows.UI.Core.CoreDispatcher Dispatcher = new Windows.UI.Core.CoreDispatcher();
        public static object KeyDownEvent = new object();
        private Windows.UI.Xaml.Input.KeyEventHandler _key;
        public event OpenEvent Opened;
        public event ClosingEvent Closing;
        public event ClosedEvent Closed;
        public event ButtonEvent CloseButtonClick;
        public event ButtonEvent PrimaryButtonClick;
        public void AddHandler(object e, Windows.UI.Xaml.Input.KeyEventHandler handler, bool handled) { _key += handler; }
        public void Open() { Opened?.Invoke(this, new ContentDialogOpenedEventArgs()); }
        public void Hide() { CloseWithResult(ContentDialogResult.None); }
        private void CloseWithResult(ContentDialogResult result)
        {
            var args = new ContentDialogClosingEventArgs { Result = result };
            Closing?.Invoke(this, args);
            if (args.Cancel) return;
            Result = result; ClosedState = true; Closed?.Invoke(this, new ContentDialogClosedEventArgs());
        }
        public void PlatformBack() { CloseWithResult(ContentDialogResult.None); }
        public void Cancel()
        {
            var args = new ContentDialogButtonClickEventArgs();
            CloseButtonClick?.Invoke(this, args);
            if (!args.Cancel) CloseWithResult(ContentDialogResult.None);
        }
        public void Submit()
        {
            if (!IsPrimaryButtonEnabled) return;
            var args = new ContentDialogButtonClickEventArgs();
            PrimaryButtonClick?.Invoke(this, args);
            if (!args.Cancel) CloseWithResult(ContentDialogResult.Primary);
        }
        public bool Key(Windows.System.VirtualKey key)
        {
            var args = new Windows.UI.Xaml.Input.KeyRoutedEventArgs { Key = key };
            _key?.Invoke(this, args);
            return args.Handled;
        }
    }
    public sealed class TextBox
    {
        private string _text = "";
        public string Text
        {
            get => _text;
            set { _text = value; TextChanged?.Invoke(this, global::System.EventArgs.Empty); }
        }
        public int MaxLength, SelectionStart, FocusAttempts, LayoutCalls, FocusFailuresRemaining;
        public string PlaceholderText;
        public bool AcceptsReturn, IsFocusEngagementEnabled, IsFocusEngaged, IsSpellCheckEnabled, IsReadOnly;
        public Windows.UI.Core.CoreDispatcher Dispatcher = new Windows.UI.Core.CoreDispatcher();
        public TextWrapping TextWrapping;
        public double FontSize, MinHeight, MinWidth;
        public HorizontalAlignment HorizontalAlignment;
        public Windows.UI.Xaml.Input.InputScope InputScope;
        public event global::System.EventHandler TextChanged;
        public event Windows.UI.Xaml.Input.KeyEventHandler KeyDown;
        public event global::System.EventHandler<Windows.UI.Xaml.RoutedEventArgs> LostFocus;
        public event CompositionStarted TextCompositionStarted;
        public event CompositionEnded TextCompositionEnded;
        public void StartComposition() => TextCompositionStarted?.Invoke(this, new TextCompositionStartedEventArgs());
        public void EndComposition() => TextCompositionEnded?.Invoke(this, new TextCompositionEndedEventArgs());
        public void LoseFocus() { Windows.UI.Xaml.Input.FocusManager.Focused = new object(); LostFocus?.Invoke(this, new Windows.UI.Xaml.RoutedEventArgs()); }
        public bool Key(Windows.System.VirtualKey key) { var args = new Windows.UI.Xaml.Input.KeyRoutedEventArgs { Key = key }; KeyDown?.Invoke(this, args); return args.Handled; }
        public void Select(int start, int length) { SelectionStart = start; }
        public void UpdateLayout() { ++LayoutCalls; }
        public bool Focus(FocusState state)
        {
            ++FocusAttempts;
            if (FocusFailuresRemaining > 0) { --FocusFailuresRemaining; return false; }
            Windows.UI.Xaml.Input.FocusManager.Focused = this; return true;
        }
    }
}
namespace BiliXbox.NativeTextInputTests
{
    using BiliXbox.Controls;
    using Windows.UI.ViewManagement;
    using Windows.UI.ViewManagement.Core;
    using Windows.UI.Xaml.Controls;
    using Windows.System;
    public static class Tests
    {
        private static int _assertions;
        private static void Check(bool value, string message)
        {
            ++_assertions;
            if (!value) throw new global::System.Exception(message);
        }
        private static NativeTextInputDialog New(string text = "测试 BV1spz8B4EXv 🎮")
        {
            InputPane.Reset(); CoreInputView.Reset();
            Windows.Foundation.Metadata.ApiInformation.Available = true;
            return new NativeTextInputDialog(text);
        }
        private static void AgeBack(NativeTextInputDialog dialog)
        {
            foreach (string field in new[] { "_lastBackTimestamp", "_lastNativeHideTimestamp" })
                typeof(NativeTextInputDialog).GetField(field, global::System.Reflection.BindingFlags.NonPublic |
                    global::System.Reflection.BindingFlags.Instance).SetValue(dialog, global::System.Diagnostics.Stopwatch.GetTimestamp() -
                    global::System.Diagnostics.Stopwatch.Frequency);
        }
        public static void Run()
        {
            var dialog = New();
            var editor = dialog.Content as TextBox;
            Check(editor != null, "Native-only dialog content must be exactly one native TextBox.");
            Check(dialog.QueryText == "测试 BV1spz8B4EXv 🎮", "Native editor lost Unicode content.");
            Check(editor.SelectionStart == editor.Text.Length, "Initial caret is not at the end.");
            Check(editor.InputScope.Names[0].NameValue == Windows.UI.Xaml.Input.InputScopeNameValue.Search,
                "Native action must be a Search input scope.");
            Check(dialog.DefaultButton == ContentDialogButton.Primary && !editor.AcceptsReturn,
                "Native Enter must use the dialog default action, not a duplicate submission handler.");
            Check(CoreInputView.GetForCurrentView().ShowCount == 0, "Keyboard requested before the dialog opened.");
            dialog.Open();
            Check(CoreInputView.GetForCurrentView().ShowCount == 0, "Keyboard request ran before dialog focus settled.");
            dialog.Dispatcher.Flush();
            Check(CoreInputView.GetForCurrentView().ShowCount == 1 && CoreInputView.GetForCurrentView().Kind == CoreInputViewKind.Keyboard,
                "The native Keyboard view was not explicitly requested.");
            Check(InputPane.GetForCurrentView().ShowCount == 0, "Successful native request was duplicated.");
            Check(editor.IsFocusEngaged && ReferenceEquals(Windows.UI.Xaml.Input.FocusManager.Focused, editor), "Editor focus was not engaged.");
            editor.Text = "   ";
            Check(!dialog.IsPrimaryButtonEnabled, "Blank content can be submitted.");
            editor.Text = "中文搜索";
            Check(dialog.IsPrimaryButtonEnabled && dialog.QueryText == "中文搜索", "Native edits were not retained.");
            InputPane.GetForCurrentView().RaiseShowing();
            Check(!dialog.Key(VirtualKey.GamepadY), "Native keyboard Y/space was intercepted.");
            dialog.Submit();
            Check(dialog.ClosedState && dialog.Result == ContentDialogResult.Primary, "Explicit search did not return Primary.");
            Check(InputPane.GetForCurrentView().SubscriberCount == 0, "Closed dialog retained input pane subscriptions.");

            dialog = New(); dialog.Open(); dialog.Hide(); dialog.Dispatcher.Flush();
            Check(CoreInputView.GetForCurrentView().ShowCount == 0, "Cancelled queued request reopened the keyboard after closing.");

            dialog = New(); CoreInputView.GetForCurrentView().Accepted = false; dialog.Open(); dialog.Dispatcher.Flush();
            Check(InputPane.GetForCurrentView().ShowCount == 1, "A rejected CoreInputView request did not fall back to native InputPane.");
            dialog.Hide();
            dialog = New(); CoreInputView.GetForCurrentView().Throw = true; dialog.Open(); dialog.Dispatcher.Flush();
            Check(InputPane.GetForCurrentView().ShowCount == 1, "Unavailable CoreInputView implementation blocked native InputPane.");
            dialog.Hide();
            dialog = New(); Windows.Foundation.Metadata.ApiInformation.Available = false; dialog.Open(); dialog.Dispatcher.Flush();
            Check(CoreInputView.GetForCurrentView().ShowCount == 0 && InputPane.GetForCurrentView().ShowCount == 1,
                "An absent API was called rather than using the baseline native InputPane.");
            dialog.Hide();

            dialog = New(); dialog.Open(); dialog.Dispatcher.Flush(); InputPane.GetForCurrentView().RaiseShowing();
            Check(dialog.Key(VirtualKey.GamepadB), "Routed B was not consumed.");
            Check(!dialog.ClosedState && InputPane.GetForCurrentView().HideCount == 1, "First B must hide only the native keyboard.");
            dialog.RequestBack(); dialog.PlatformBack();
            Check(!dialog.ClosedState, "Routed B plus shell/platform B dismissed two UI layers.");
            AgeBack(dialog); dialog.RequestBack();
            Check(dialog.ClosedState, "A second distinct B did not close the editor.");

            dialog = New(); dialog.Open(); dialog.Dispatcher.Flush(); InputPane.GetForCurrentView().RaiseShowing();
            InputPane.GetForCurrentView().RaiseHiding(); dialog.PlatformBack();
            Check(!dialog.ClosedState, "Native Hiding before platform Back dismissed both keyboard and dialog.");
            editor = (TextBox)dialog.Content;
            Check(dialog.Key(VirtualKey.GamepadY), "Y did not reopen a cancelled native editor.");
            dialog.Dispatcher.Flush();
            Check(!ReferenceEquals(editor, dialog.Content) && dialog.QueryText == editor.Text,
                "Reopening reused the dismissed TextBox or discarded edited content.");
            Check(CoreInputView.GetForCurrentView().ShowCount == 2, "Reopening did not issue a fresh native keyboard request.");
            dialog.Hide();

            dialog = New(); dialog.Open(); dialog.Dispatcher.Flush(); InputPane.GetForCurrentView().RaiseShowing();
            dialog.RequestBack();
            editor = (TextBox)dialog.Content;
            Check(dialog.Key(VirtualKey.GamepadA), "A on the editor did not reopen the native keyboard.");
            dialog.Dispatcher.Flush();
            Check(!ReferenceEquals(editor, dialog.Content), "A reused a stale native editing session.");
            InputPane.GetForCurrentView().RaiseShowing(); dialog.Cancel();
            Check(dialog.ClosedState, "Explicit Cancel was blocked by keyboard visibility.");

            dialog = New(); dialog.Open(); dialog.Dispatcher.Flush(); InputPane.GetForCurrentView().RaiseShowing(); dialog.Hide();
            Check(dialog.ClosedState && InputPane.GetForCurrentView().SubscriberCount == 0,
                "Suspension/navigation Hide was incorrectly treated as native keyboard Back.");
            var reopened = New("保留文字");
            Check(!ReferenceEquals(dialog.Content, reopened.Content), "A fresh dialog retained the previous native TextBox.");
            reopened.Open(); reopened.Dispatcher.Flush();
            Check(CoreInputView.GetForCurrentView().ShowCount == 1 && reopened.QueryText == "保留文字",
                "A fresh search request failed to reopen the native keyboard.");
            reopened.Hide();
            global::System.Console.WriteLine("PASS: " + _assertions + " native text dialog lifecycle checks (UWP event stubs, no device keyboard).");
        }
    }
}
