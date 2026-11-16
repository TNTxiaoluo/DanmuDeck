// Compile the production composer against external editor, account and view substitutes.
namespace BiliXbox.Controls
{
    using System;
    using System.Threading.Tasks;
    public sealed partial class CreatorHubView
    {
        private class Control { }
        private sealed class TextBox : Control { public string Text=""; public bool IsFocusEngaged, IsReadOnly; }
        private sealed class Label { public string Text; }
        private sealed class CommentItem { public long Id; public string Author; }
        private sealed class Card { public CommentItem Item; }
        private sealed class Target { public bool IsAvailable=true, CanPost=true; }
        private sealed class Api { public bool IsLoggedIn=true; }
        private enum HubPane { Dynamics, Detail, Replies }
        private static class InlineNativeTextInputSession
        { public static TextBox ReplaceEditor(TextBox old, int max) => new TextBox { Text=old.Text }; }
        private bool _writing, _posting, _commentClosed;
        private HubPane _pane=HubPane.Detail;
        private bool IsDetail => _pane==HubPane.Detail || _pane==HubPane.Replies;
        private readonly Api _api=new Api();
        private readonly Target _target=new Target();
        private readonly object _commentCts=new object();
        private readonly CreatorCommentDraftState _draftState=new CreatorCommentDraftState();
        private string _draft="", _draftKey="subject", _draftEditKey;
        private object _draftEditOwner;
        private Target _draftEditTarget;
        private TextBox _draftEditor=new TextBox();
        private readonly Control _send=new Control(), _write=new Control();
        private readonly Label _commentHint=new Label();
        private Control _focused;
        private Card _focusedCard;
        private CommentItem _composeReply, _replyRoot;
        private Func<TextBox,string,int,Task<bool>> RequestTextInputAsync;
        private event EventHandler LoginRequested;
        private void EnsureSession() { }
        private Card FocusedCommentCard() => _focusedCard;
        private bool SelectReplyTarget(CommentItem item) { _composeReply=item; return item!=null; }
        private void ResetReplyTarget() { _composeReply=null; }
        private void ClearCommentActions() { }
        private void SelectDraft() { _draft=_draftState.Draft; }
        private void AttachDraftEditor() { }
        private void UpdateActions() { }
        private bool CurrentComment(object owner,Target target) => ReferenceEquals(owner,_commentCts) && ReferenceEquals(target,_target);
        private string TargetKey(Target target) => _draftKey;
        private void Focus(Control control) { _focused=control; }
        private static int checks;
        private static void Check(bool value,string label) { checks++; if(!value) throw new Exception(label); }
        public static async Task RunComposerTests()
        {
            var one=new CommentItem { Id=1,Author="one" };
            var two=new CommentItem { Id=2,Author="two" };
            var page=new CreatorHubView { _composeReply=two, _focusedCard=new Card { Item=one } };
            string title=null;
            page.RequestTextInputAsync=(editor,label,max)=> { title=label; return Task.FromResult(false); };
            await page.WriteCommentAsync();
            Check(page._composeReply==null && title=="写动态评论","Root X clears previous reply and writes to subject even with a focused user");
            Check(page._focused==page._write && !page._writing,"Cancelled empty draft returns to visible write button");
            await page.WriteCommentAsync(true);
            Check(page._composeReply==one && title=="回复 one","LB X replies to focused user");
            page._pane=HubPane.Replies; page._focusedCard=new Card { Item=two };
            await page.WriteCommentAsync();
            Check(page._composeReply==two && title=="回复 two","Nested X replies to focused user");
            page._focusedCard=null;page._composeReply=null;page._replyRoot=one;
            await page.WriteCommentAsync();
            Check(page._composeReply==one,"Nested composer can target root when no row has focus");
            page._pane=HubPane.Detail;page._replyRoot=null;page._composeReply=null;title=null;
            await page.WriteCommentAsync(true);
            Check(title==null && page._commentHint.Text.Contains("选择"),"Targeted reply without a user does not become a subject comment");
            page.RequestTextInputAsync=(editor,label,max)=> { editor.Text="draft only"; return Task.FromResult(true); };
            await page.WriteCommentAsync();
            Check(page._draftState.Draft=="draft only" && page._focused==page._send,"Completed text remains a draft and focuses publish");
            Check(!page._draftState.InFlight,"Editing does not publish or start submission");
            page._api.IsLoggedIn=false;bool login=false;page.LoginRequested+=(s,e)=>login=true;
            await page.WriteCommentAsync();
            Check(login,"Anonymous comment invokes login before editing");
            Console.WriteLine("PASS: "+checks+" production dynamic subject/reply target, editor cancellation, draft and login assertions (external UI substitutes).");
        }
    }
}
