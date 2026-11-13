using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

// Executes the actual target selection, draft selection and publishing workflow
// against a controlled transport. No account or real write request is used.
namespace BiliXbox.CreatorThreadTests
{
    public sealed class CommentItem { public long Id, Oid, RootId; public int Type, ReplyCount; public string Author; public readonly List<object> Pictures = new List<object>(); }
    public sealed class DynamicCommentTarget { public long Oid; public int Type; public bool CanPost = true; public bool IsAvailable => Oid > 0 && Type > 0; }
    public sealed class User { public long Mid; }
    public sealed class CommentCard : Control
    {
        public CommentItem Item; public Control Primary; public Action Open;
        public int Activations, Entries;
        public void RequestPrimaryAction() { ++Entries; Open?.Invoke(); }
        public bool OwnsAction(Control control) => ReferenceEquals(control, Primary);
        public bool TryActivate(Control control) { if (!OwnsAction(control)) return false; ++Activations; return true; }
        public Control PrimaryActionControl(bool preferPictures) => Primary;
    }
    public sealed class ListViewBase
    {
        public readonly List<object> Items = new List<object>();
        public readonly Dictionary<object, Control> Rows = new Dictionary<object, Control>();
        public object ContainerFromItem(object item) { Control row; return Rows.TryGetValue(item, out row) ? row : null; }
    }
    public class Control { }
    public static class FocusManager { public static Control Focused; public static object GetFocusedElement() => Focused; }
    public sealed class TextBlock { public string Text; }
    public sealed class Client
    {
        public bool IsLoggedIn = true;
        public User CurrentUser = new User { Mid = 42 };
        public int Calls;
        public long Root, Parent;
        public Func<Task> Handler = () => Task.CompletedTask;
        public Task PostDynamicCommentAsync(DynamicCommentTarget target, string message, long root, long parent, CancellationToken token)
        { ++Calls; Root = root; Parent = parent; return Handler(); }
    }
    public sealed class CreatorHubView
    {
        private enum HubPane { Detail, Replies, Dynamics }
        private readonly Client _api = new Client();
        private HubPane _pane = HubPane.Replies;
        private bool IsDetail => _pane != HubPane.Dynamics;
        private readonly CancellationTokenSource _sessionCts = new CancellationTokenSource(), _commentCts = new CancellationTokenSource();
        private DynamicCommentTarget _target = new DynamicCommentTarget { Oid = 1000, Type = 17 };
        private CommentItem _replyRoot = Comment(100), _composeReply;
        private long _composeRoot, _composeParent;
        private string _draftKey = "", _draft = "";
        private CreatorCommentDraftState _draftState;
        private readonly Dictionary<string, CreatorCommentDraftState> _drafts = new Dictionary<string, CreatorCommentDraftState>();
        private bool _posting, _commentClosed = false;
        private readonly TextBlock _commentHint = new TextBlock();
        private readonly Control _write = new Control();
        private readonly ListViewBase _commentList = new ListViewBase(), _replyList = new ListViewBase();
        private ListViewBase ActiveList => _pane == HubPane.Replies ? _replyList : _commentList;
        private CommentCard _actionCard;
        private Control _actionRow;
        private int _commentLoads, _replyLoads;
        public event EventHandler LoginRequested;
        private void EnsureSession() { }
        private bool CurrentComment(CancellationTokenSource owner, DynamicCommentTarget target)
            => owner == _commentCts && !owner.IsCancellationRequested && ReferenceEquals(target, _target) && IsDetail;
        private void Focus(Control control) { FocusManager.Focused = control; }
        private static bool IsAvailable(Control control) => control != null;
        private void FocusItem(ListViewBase list, int index) { Focus(list.ContainerFromItem(list.Items[index]) as Control); }
        private void UpdateActions() { SelectDraft(); }
        private Task LoadCommentsAsync(bool reset) { ++_commentLoads; return Task.CompletedTask; }
        private Task LoadRepliesAsync(bool reset) { ++_replyLoads; return Task.CompletedTask; }
        private static string Friendly(Exception error) => error.Message;

        /* PRODUCTION_METHODS */

        private static int _checks;
        private static void Check(bool ok, string message) { if (!ok) throw new Exception("FAIL: " + message); ++_checks; }
        private static CommentItem Comment(long id, long root = 0) => new CommentItem { Id = id, RootId = root, Oid = 1000, Type = 17, Author = "user" + id };
        private static void Edit(CreatorHubView view, string text) { view.SelectDraft(); view._draftState.Edit(text); view.SelectDraft(); }
        public static async Task<string> Run()
        {
            var view = new CreatorHubView();
            Check(view.SelectReplyTarget(view._replyRoot), "root is a valid reply recipient");
            Check(view._composeRoot == 100 && view._composeParent == 100, "replying to root uses root and parent equally");
            Edit(view, "root draft"); string rootKey = view._draftKey;
            var child = Comment(101, 100);
            Check(view.SelectReplyTarget(child), "child in current subject is replyable");
            Check(view._composeRoot == 100 && view._composeParent == 101, "replying to child preserves original floor and targets selected child");
            Edit(view, "child draft"); string childKey = view._draftKey;
            Check(childKey != rootKey && childKey.EndsWith(":100:101"), "draft includes floor and selected parent");
            view.SelectReplyTarget(view._replyRoot); view.SelectDraft();
            Check(view._draft == "root draft", "changing reply recipient preserves each draft");
            var wrong = Comment(202); wrong.Oid = 2000;
            Check(!view.SelectReplyTarget(wrong) && view._composeParent == 100, "foreign dynamic comment cannot replace current recipient");
            wrong.Oid = 1000; wrong.Type = 1;
            Check(!view.SelectReplyTarget(wrong), "same number with wrong resource type cannot be replied to");
            view.ResetReplyTarget(); view.SelectDraft();
            Check(view._draft == "" && view._draftKey.EndsWith(":0:0"), "top level composer has separate draft and no inherited reply target");
            view.SelectReplyTarget(child); view.SelectDraft();
            Check(view._draft == "child draft", "reply draft survives returning to top level composer");
            view._api.CurrentUser.Mid = 43; view.SelectDraft();
            Check(view._draft == "", "different account cannot inherit a reply draft");
            view._api.CurrentUser.Mid = 42; view.SelectDraft();
            Check(view._draft == "child draft", "original account restores its recipient-specific draft");
            await view.PostCommentAsync();
            Check(view._api.Calls == 1 && view._api.Root == 100 && view._api.Parent == 101, "actual publish workflow sends exact root and selected parent");
            Check(view._replyLoads == 1 && view._commentLoads == 0 && view._pane == HubPane.Replies, "reply publish refreshes existing thread without leaving it");
            Check(view._draft == "" && !view._draftState.InFlight, "confirmed reply clears only submitted draft");
            view.ResetReplyTarget(); Edit(view, "top level");
            await view.PostCommentAsync();
            Check(view._api.Root == 0 && view._api.Parent == 0 && view._commentLoads == 1, "new top-level publish does not inherit previous reply target");

            view = new CreatorHubView(); view.SelectReplyTarget(child); Edit(view, "uncertain reply");
            view._api.Handler = () => Task.FromException(new TimeoutException("controlled timeout"));
            await view.PostCommentAsync();
            Check(view._draftState.IsUncertain && view._draft == "uncertain reply", "unknown write outcome preserves reply draft");
            await view.PostCommentAsync();
            Check(view._api.Calls == 1 && view._draftState.RetryArmed, "first retry confirmation never emits duplicate write");
            view._api.Handler = () => Task.CompletedTask;
            await view.PostCommentAsync();
            Check(view._api.Calls == 2 && view._api.Root == 100 && view._api.Parent == 101, "deliberate second retry preserves exact reply target");

            view = new CreatorHubView(); view.SelectReplyTarget(child); Edit(view, "original pending");
            var pending = new TaskCompletionSource<bool>(); view._api.Handler = () => pending.Task;
            Task post = view.PostCommentAsync();
            await view.PostCommentAsync();
            Check(view._api.Calls == 1, "held confirmation cannot submit twice");
            view._api.CurrentUser.Mid = 43; Edit(view, "other account draft");
            pending.SetResult(true); await post;
            Check(view._draft == "other account draft" && view._replyLoads == 0, "late prior-account success neither clears new account draft nor changes thread");
            view._api.CurrentUser.Mid = 42; view.SelectDraft();
            Check(view._draft == "" && !view._draftState.InFlight, "late confirmed write clears only its original account state");

            var rows = new ListViewBase(); rows.Items.Add(new CommentCard { Item = Comment(100) });
            rows.Items.Add(new object()); rows.Items.Add(new CommentCard { Item = child });
            Check(view.NextCommentRow(rows, 1, 1) == 2 && view.NextCommentRow(rows, 1, -1) == 0, "divider is skipped in both navigation directions");
            Check(view.NextCommentRow(rows, -1, -1) == -1 && view.NextCommentRow(rows, 3, 1) == -1, "thread boundary cannot wrap to another floor");
            foreach (bool pictures in new[] { false, true })
            foreach (int replyCount in new[] { 0, 5 })
            {
                view = new CreatorHubView(); view._pane = HubPane.Detail;
                var summary = new CommentCard { Item = Comment(300), Primary = new Control() };
                summary.Item.ReplyCount = replyCount;
                if (pictures) summary.Item.Pictures.Add(new object());
                view._commentList.Items.Add(summary);
                var floor = new Control();
                summary.Open = () => { view._pane = HubPane.Replies; view._replyList.Items.Add(summary); view._replyList.Rows[summary] = floor; view.Focus(floor); };
                view.ActivateCommentCard(summary, summary.Primary);
                Check(view._pane == HubPane.Replies && summary.Entries == 1 && summary.Activations == 0, "Creator summary first A opens text/image floors even with no child replies and child-button focus.");
                view.ActivateCommentCard(summary, floor);
                Check(ReferenceEquals(FocusManager.Focused, summary.Primary) && summary.Activations == 0, "Creator thread second A selects picture/reaction without executing it.");
                view.ActivateCommentCard(summary, summary.Primary);
                Check(summary.Activations == 1 && summary.Entries == 1, "Creator thread third A executes selected action once without reopening the thread.");
                Check(view.LeaveCommentActions() && ReferenceEquals(FocusManager.Focused, floor), "Creator B restores the exact selected floor for continued reply browsing.");
            }
            return "PASS: " + _checks + " production creator thread reply/draft checks; no real writes.";
        }
    }
}
