using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace BiliXbox.LiveFocusTests
{
    public enum FocusState { Keyboard }
    public enum Visibility { Visible, Collapsed }
    public enum ScrollIntoViewAlignment { Default, Leading }
    public enum CoreDispatcherPriority { Low }
    public class Control
    {
        internal static Control Focused;
        internal int FocusCount;
        public bool Focus(FocusState state) { ++FocusCount; Focused = this; return true; }
    }
    public sealed class TextBox : Control
    {
        internal string Text = "";
        internal bool IsFocusEngaged, IsReadOnly = true;
    }
    public sealed class TextBlock { internal string Text; }
    public sealed class GridView
    {
        internal readonly List<object> Items = new List<object>();
        internal readonly Control Row = new Control();
        internal int ScrollCalls;
        internal void ScrollIntoView(object item, ScrollIntoViewAlignment alignment) { ++ScrollCalls; }
        internal void UpdateLayout() { }
        internal object ContainerFromIndex(int index) => Row;
    }
    public sealed class Dispatcher
    {
        private readonly Queue<Action> _callbacks = new Queue<Action>();
        internal int Pending => _callbacks.Count;
        internal Task RunAsync(CoreDispatcherPriority priority, Action callback)
        {
            var completion = new TaskCompletionSource<bool>();
            _callbacks.Enqueue(() => { callback(); completion.SetResult(true); }); return completion.Task;
        }
        internal void Drain() { while (_callbacks.Count > 0) _callbacks.Dequeue()(); }
    }
    public static class InlineNativeTextInputSession
    {
        public static TextBox ReplaceEditor(TextBox editor, int limit) => new TextBox { Text = editor.Text };
    }
    public sealed class LiveHubView
    {
        private int _focusGeneration, _roomGeneration = 1, _selectedIndex;
        private bool _editing, _sending, IsPlayingRoom;
        private readonly GridView _rooms = new GridView();
        private readonly Dispatcher Dispatcher = new Dispatcher();
        private readonly TextBlock _notice = new TextBlock(), _drawerNotice = new TextBlock();
        private TextBox _roomInput = new TextBox(), _chatInput = new TextBox();
        private readonly Control _open = new Control(), _send = new Control();
        private long CurrentMid = 7;
        private string _uncertainChat = "";
        private Control _lastFocus;
        private Visibility Visibility = Visibility.Visible;
        private Func<TextBox, string, int, Task<bool>> RequestTextInputAsync;
        private void EnsureChatAccount() { }
        private Task LoginAsync() => Task.CompletedTask;
        private void AttachEditor(TextBox editor, bool roomInput) { }
        private void Focus(Control control) { _lastFocus = control; control.Focus(FocusState.Keyboard); }

        /* PRODUCTION_METHODS */

        public static async Task Run()
        {
            int count = 0;
            Action<bool, string> check = (value, name) => { ++count; if (!value) throw new Exception(name); };
            Func<LiveHubView> ready = () => { var value = new LiveHubView(); value._rooms.Items.Add(new object()); return value; };
            var view = ready(); view.FocusRoom(0); view.Dispatcher.Drain();
            check(view._rooms.Row.FocusCount == 1, "Normal room focus still reaches its container");
            view = ready(); view._editing = true; view.FocusRoom(0);
            check(view.Dispatcher.Pending == 0 && view._rooms.ScrollCalls == 0, "Starting list restore during native editing does not queue or scroll");
            view = ready(); view.FocusRoom(0); view._editing = true; view.Dispatcher.Drain();
            check(view._rooms.Row.FocusCount == 0, "Already queued room callback cannot steal active editor focus");
            view = ready(); view.FocusRoom(0);
            var keyboard = new TaskCompletionSource<bool>(); TextBox edited = null;
            view.RequestTextInputAsync = (editor, title, limit) => { edited = editor; editor.IsReadOnly = false; editor.IsFocusEngaged = true; editor.Focus(FocusState.Keyboard); return keyboard.Task; };
            int oldGeneration = view._focusGeneration; var edit = view.EditAsync(true);
            check(view._editing && view._focusGeneration > oldGeneration && ReferenceEquals(edited, view._roomInput), "Actual editing invalidates pending grid focus before installing renewed inline editor");
            view.Dispatcher.Drain();
            check(ReferenceEquals(Control.Focused, edited) && !edited.IsReadOnly && view._rooms.Row.FocusCount == 0, "Pending grid layout cannot dismiss or replace the actual writable native editor");
            keyboard.SetResult(false); await edit;
            check(!view._editing && edited.IsReadOnly, "Cancelled native input releases editing and renews readonly composer");
            view = ready(); view.FocusRoom(0); keyboard = new TaskCompletionSource<bool>();
            view.RequestTextInputAsync = (editor, title, limit) => keyboard.Task;
            edit = view.EditAsync(true); keyboard.SetResult(false); await edit; view.Dispatcher.Drain();
            check(view._rooms.Row.FocusCount == 0, "Grid callback still cannot steal focus after keyboard closes because its generation is stale");
            view = ready(); view.FocusRoom(0); view._rooms.Items[0] = new object(); view.Dispatcher.Drain();
            check(view._rooms.Row.FocusCount == 0, "Replaced room page rejects old container focus");
            view = ready(); view.FocusRoom(0); view.IsPlayingRoom = true; view.Dispatcher.Drain();
            check(view._rooms.Row.FocusCount == 0, "Entering room rejects delayed browse focus");
            view = ready(); view.FocusRoom(0); view.Visibility = Visibility.Collapsed; view.Dispatcher.Drain();
            check(view._rooms.Row.FocusCount == 0, "Hidden live section rejects delayed grid focus");
            Console.WriteLine("PASS: " + count + " production live focus/native inline editor ownership assertions (offline fake dispatcher).");
        }
    }
}
