namespace BiliXbox.Controls
{
    // Parent/layout replacement is verified by the native UWP application, not
    // this keyboard lifecycle fixture. No replacement behavior is simulated.
    internal static class InlineTextBoxFactory
    {
        internal static Windows.UI.Xaml.Controls.TextBox Replace(Windows.UI.Xaml.Controls.TextBox previous, int limit)
            => throw new global::System.NotSupportedException("Requires the real XAML visual tree.");
    }
}
namespace BiliXbox.InlineInputTests
{
    public static class Tests
    {
        private static int _count;
        private static void Check(bool value, string message) { if (!value) throw new Exception(message); ++_count; }
        private static Windows.UI.Xaml.Controls.TextBox NewEditor(string text = "中文草稿 🎮")
        {
            Windows.UI.ViewManagement.InputPane.Reset();
            Windows.UI.ViewManagement.Core.CoreInputView.Reset();
            Windows.Foundation.Metadata.ApiInformation.Available = true;
            return new Windows.UI.Xaml.Controls.TextBox { Text = text };
        }
        private static void PumpUntil(Windows.UI.Xaml.Controls.TextBox editor, Func<bool> condition)
        {
            var clock = global::System.Diagnostics.Stopwatch.StartNew();
            while (!condition() && clock.ElapsedMilliseconds < 1500)
            { editor.Dispatcher.Flush(); global::System.Threading.Thread.Sleep(1); }
            if (!condition()) throw new Exception("Timed out waiting for bounded native focus retry.");
        }
        public static void Run()
        {
            _count = 0;
            var editor = NewEditor();
            var session = new Controls.InlineNativeTextInputSession(editor, true);
            var result = session.ShowAsync();
            Check(!result.IsCompleted && session.IsActive, "session waits without a dialog");
            Check(Windows.UI.ViewManagement.Core.CoreInputView.GetForCurrentView().ShowCount == 0, "keyboard focus request deferred until layout");
            editor.Dispatcher.Flush();
            Check(editor.IsFocusEngaged && !editor.IsReadOnly, "same editor engaged for native input");
            Check(Windows.UI.ViewManagement.Core.CoreInputView.GetForCurrentView().ShowCount == 1, "exactly one native show");
            editor.Text = "new 中文 keyword";
            Check(editor.Key(Windows.System.VirtualKey.Enter) && result.Result, "explicit Enter searches");
            Check(!editor.IsFocusEngaged && !session.IsActive, "submit releases engagement");
            Check(Windows.UI.ViewManagement.InputPane.GetForCurrentView().SubscriberCount == 0, "submit removes pane subscriptions");
            session.RequestBack(); Check(result.Result, "duplicate close cannot change explicit submission");

            editor = NewEditor(); session = new Controls.InlineNativeTextInputSession(editor, true);
            result = session.ShowAsync(); editor.Dispatcher.Flush();
            Windows.UI.ViewManagement.InputPane.GetForCurrentView().RaiseShowing();
            editor.Text = "B后保留草稿";
            Check(editor.Key(Windows.System.VirtualKey.GamepadB) && !result.Result, "B closes editing without searching");
            Check(editor.Text == "B后保留草稿" && !editor.IsFocusEngaged, "B retains in-place text");
            var reopen = new Controls.InlineNativeTextInputSession(editor, true);
            var reopened = reopen.ShowAsync(); editor.Dispatcher.Flush();
            Check(!reopened.IsCompleted && Windows.UI.ViewManagement.Core.CoreInputView.GetForCurrentView().ShowCount == 2, "new session can request native keyboard after B");
            reopen.Cancel(); Check(!reopened.Result, "cancel stays non-submitting");

            editor = NewEditor(); session = new Controls.InlineNativeTextInputSession(editor, false);
            result = session.ShowAsync(); editor.Dispatcher.Flush();
            Check(!editor.Key(Windows.System.VirtualKey.Enter) && !result.IsCompleted, "comment Enter is left to native editor, never publishes");
            editor.Text = "第一行\n第二行";
            Windows.UI.ViewManagement.InputPane.GetForCurrentView().RaiseShowing();
            Windows.UI.ViewManagement.InputPane.GetForCurrentView().RaiseHiding();
            Check(!result.Result && editor.Text == "第一行\n第二行", "native Done/hide keeps multiline comment draft");
            Check(Windows.UI.ViewManagement.InputPane.GetForCurrentView().SubscriberCount == 0, "hide removes subscriptions");

            editor = NewEditor(); session = new Controls.InlineNativeTextInputSession(editor, true);
            result = session.ShowAsync(); editor.Dispatcher.Flush();
            editor.StartComposition();
            Check(!editor.Key(Windows.System.VirtualKey.Enter) && !result.IsCompleted, "IME composition Enter does not search");
            editor.EndComposition();
            Check(!editor.Key(Windows.System.VirtualKey.Enter) && !result.IsCompleted, "IME commit event cannot immediately double-submit");
            editor.Dispatcher.Flush();
            Check(editor.Key(Windows.System.VirtualKey.Enter) && result.Result, "next explicit Enter can search after composition");

            editor = NewEditor("   "); session = new Controls.InlineNativeTextInputSession(editor, true);
            result = session.ShowAsync(); editor.Dispatcher.Flush();
            Check(!editor.Key(Windows.System.VirtualKey.Enter) && !result.IsCompleted, "blank search cannot submit");
            session.Complete(true); Check(!result.Result, "explicit blank completion rejected");
            editor = NewEditor(); session = new Controls.InlineNativeTextInputSession(editor, true);
            result = session.ShowAsync(); session.Cancel(); editor.Dispatcher.Flush();
            Check(!result.Result && Windows.UI.ViewManagement.Core.CoreInputView.GetForCurrentView().ShowCount == 0, "navigation cancel prevents queued keyboard from appearing");
            session = new Controls.InlineNativeTextInputSession(editor, true); session.Dispose();
            Check(!session.ShowAsync().Result && !session.IsActive, "disposed session never starts queued work");
            editor = NewEditor(); session = new Controls.InlineNativeTextInputSession(editor, true);
            result = session.ShowAsync(); editor.Dispatcher.Flush(); editor.LoseFocus();
            Check(!result.Result && editor.Text == "中文草稿 🎮", "focus departure releases ownership and retains Unicode");
            editor = NewEditor(); Windows.UI.ViewManagement.Core.CoreInputView.GetForCurrentView().Accepted = false;
            session = new Controls.InlineNativeTextInputSession(editor, false); result = session.ShowAsync(); editor.Dispatcher.Flush();
            Check(Windows.UI.ViewManagement.InputPane.GetForCurrentView().ShowCount == 1, "rejected core view falls back to native InputPane");
            session.Cancel();
            editor = NewEditor(); Windows.UI.ViewManagement.Core.CoreInputView.GetForCurrentView().Throw = true;
            session = new Controls.InlineNativeTextInputSession(editor, false); result = session.ShowAsync(); editor.Dispatcher.Flush();
            Check(Windows.UI.ViewManagement.InputPane.GetForCurrentView().ShowCount == 1, "unsupported core view still uses native pane");
            session.Cancel();
            editor = NewEditor(); session = new Controls.InlineNativeTextInputSession(editor, true);
            result = session.ShowAsync();
            Windows.UI.ViewManagement.InputPane.GetForCurrentView().RaiseHiding();
            editor.Dispatcher.Flush();
            Check(!result.IsCompleted && editor.IsFocusEngaged, "orphan hide from previous session cannot close newly opened editor");
            session.Cancel();
            editor = NewEditor(); bool onSubmitButton = true;
            session = new Controls.InlineNativeTextInputSession(editor, true, () => onSubmitButton);
            result = session.ShowAsync(); editor.Dispatcher.Flush(); editor.LoseFocus();
            Windows.UI.ViewManagement.InputPane.GetForCurrentView().RaiseShowing();
            Windows.UI.ViewManagement.InputPane.GetForCurrentView().RaiseHiding();
            Check(!result.IsCompleted, "focus transfer to explicit search button does not restore hidden playback before its click");
            session.Complete(true); Check(result.Result, "explicit adjacent search button can submit after focus transfer");

            editor = NewEditor(); editor.FocusFailuresRemaining = 1;
            session = new Controls.InlineNativeTextInputSession(editor, true); result = session.ShowAsync();
            editor.Dispatcher.Flush();
            Check(!result.IsCompleted && editor.FocusAttempts == 1, "a not-yet-focusable editor is not canceled after one render pass");
            Check(Windows.UI.ViewManagement.Core.CoreInputView.GetForCurrentView().ShowCount == 0, "keyboard waits until editor actually receives focus");
            PumpUntil(editor, () => editor.IsFocusEngaged);
            Check(editor.FocusAttempts == 2 && editor.LayoutCalls == 2, "layout is updated before each bounded focus attempt");
            Check(Windows.UI.ViewManagement.Core.CoreInputView.GetForCurrentView().ShowCount == 1, "successful retry requests native keyboard only once");
            session.Cancel();

            editor = NewEditor(); editor.FocusFailuresRemaining = 10;
            session = new Controls.InlineNativeTextInputSession(editor, true); result = session.ShowAsync();
            PumpUntil(editor, () => result.IsCompleted);
            Check(!result.Result && editor.FocusAttempts == 3 && editor.LayoutCalls == 3, "permanently unfocusable editor stops after exactly three attempts");
            Check(!editor.IsFocusEngaged && Windows.UI.ViewManagement.InputPane.GetForCurrentView().SubscriberCount == 0, "exhausted focus attempts release native session ownership");
            Check(Windows.UI.ViewManagement.Core.CoreInputView.GetForCurrentView().ShowCount == 0, "exhausted focus never opens an unowned keyboard");

            editor = NewEditor(); editor.FocusFailuresRemaining = 10;
            session = new Controls.InlineNativeTextInputSession(editor, true); result = session.ShowAsync();
            editor.Dispatcher.Flush();
            Check(global::System.Threading.SpinWait.SpinUntil(() => editor.Dispatcher.PendingCount > 0, 1500), "a second focus callback is queued after the render interval");
            session.RequestBack(); editor.Dispatcher.Flush();
            Check(!result.Result && editor.FocusAttempts == 1 && editor.LayoutCalls == 1, "B between retry callbacks prevents further focus work");
            Check(Windows.UI.ViewManagement.Core.CoreInputView.GetForCurrentView().ShowCount == 0
                && Windows.UI.ViewManagement.InputPane.GetForCurrentView().SubscriberCount == 0, "cancel between focus attempts cannot reopen keyboard or retain subscriptions");
            Console.WriteLine("PASS: " + _count + " inline native input lifecycle assertions (B/reopen, IME, drafts, explicit search, cancellation, focus ownership).");
        }
    }
}
