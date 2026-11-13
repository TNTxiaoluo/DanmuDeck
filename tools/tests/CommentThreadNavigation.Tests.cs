// The methods under test are extracted from MainPage.CommentsNavigation.cs.
// Only the external XAML tree, layout/focus, and editor-opening side effect are
// substitutes. No account, keyboard, network, or comment write is involved.
namespace Windows.Foundation
{
    public struct Rect
    {
        public double X, Y, Width, Height;
        public double Top => Y;
        public double Bottom => Y + Height;
        public Rect(double x, double y, double width, double height) { X = x; Y = y; Width = width; Height = height; }
    }
}
namespace Windows.UI.Xaml
{
    public enum Visibility { Visible, Collapsed }
    public enum FocusState { Keyboard }
    public class DependencyObject { public DependencyObject Parent; }
    public class RoutedEventArgs : EventArgs { }
    public class GeneralTransform
    {
        private readonly Func<Rect, Rect> _transform;
        public GeneralTransform(Func<Rect, Rect> transform) { _transform = transform; }
        public Rect TransformBounds(Rect rect) => _transform(rect);
    }
}
namespace Windows.UI.Xaml.Controls
{
    public class Control : DependencyObject
    {
        public bool IsEnabled = true, IsLoaded = true;
        public Visibility Visibility;
        public FontFamily FontFamily = new FontFamily();
        public double ActualWidth = 390, ActualHeight = 50, ContentTop;
        public ScrollViewer GeometryViewer;
        public GeneralTransform TransformToVisual(Control relative)
            => new GeneralTransform(rect => new Rect(0, ContentTop - (GeometryViewer?.VerticalOffset ?? 0), ActualWidth, ActualHeight));
    }
    public class FontFamily { public string Source = ""; }
    public class Button : Control { public object Tag; }
    public class ComboBox : Control { public bool IsDropDownOpen; public bool Focus(FocusState state) => true; }
    public class Grid : Control { public readonly List<Control> Children = new List<Control>(); }
    public class ItemClickEventArgs : EventArgs { public object ClickedItem; }
    public class TextBox : Control { public string Text = "", PlaceholderText = ""; }
    public class ListViewItem : Control { public object Content; public CommentCard Card; }
    public class ListView : Control
    {
        public readonly List<object> Items = new List<object>();
        public readonly List<ListViewItem> Rows = new List<ListViewItem>();
        public ScrollViewer Viewer;
        public object ContainerFromIndex(int index) => index >= 0 && index < Rows.Count ? Rows[index] : null;
        public int IndexFromContainer(Control row) => row is ListViewItem item ? Rows.IndexOf(item) : -1;
        public void UpdateLayout() { }
    }
    public class ScrollViewer : Control
    {
        public double VerticalOffset, ScrollableHeight;
        public int Changes;
        public void ChangeView(double? x, double? y, float? zoom, bool disableAnimation)
        { if (y.HasValue) VerticalOffset = y.Value; ++Changes; }
    }
}
namespace Windows.UI.Xaml.Input
{
    public static class FocusManager
    {
        public static Control Focused;
        public static object GetFocusedElement() => Focused;
    }
}
namespace BiliXbox.Models
{
    public sealed class CommentItem
    {
        public long Id, RootId; public string Author; public int ReplyCount;
        public readonly List<CommentItem> Replies = new List<CommentItem>();
        public readonly List<object> Pictures = new List<object>();
    }
    public sealed class VideoDetail { public long Aid; }
}
namespace BiliXbox.Controls
{
    public enum PlayerDirection { Left, Right, Up, Down }
    public enum PlayerDrawer { None, Comments }
    public sealed class PlayerUiState { public PlayerDrawer Drawer; }
    public sealed class CommentRepliesRequestedEventArgs : EventArgs
    {
        public CommentItem Comment;
        public CommentRepliesRequestedEventArgs(CommentItem item) { Comment = item; }
    }
    public sealed class CommentImageRequestedEventArgs : EventArgs { public CommentItem Comment; public int ImageIndex; }
    public sealed class CommentImageViewer
    {
        public bool IsOpen; public Control ReturnControl;
        public event EventHandler Closed;
        public bool Open(IList<object> pictures, int index, Control returnTo)
        { if (pictures.Count == 0) return false; IsOpen = true; ReturnControl = returnTo; return true; }
        public void Close() { IsOpen = false; FocusManager.Focused = ReturnControl; Closed?.Invoke(this, EventArgs.Empty); }
    }
    public sealed class CommentCard : Control
    {
        public CommentItem Item;
        public Control Primary;
        public Control Next;
        public int Activations;
        public Action OnActivate;
        public bool TryActivate(Control control) { if (!ReferenceEquals(control, Primary)) return false; ++Activations; OnActivate?.Invoke(); return true; }
        public Control PrimaryActionControl(bool preferPictures) => Primary;
        public Control NavigateAction(Control current, PlayerDirection direction) => Next ?? current;
    }
    public static class ViewportFocusScroll
    {
        public static int Cancelled;
        public static ScrollViewer FindScrollViewer(ListView list) => list.Viewer;
        public static void Cancel(DependencyObject root) { ++Cancelled; }
    }
}
namespace BiliXbox
{
    public sealed partial class MainPage
    {
        private bool _watching = true, _dialogOpen, _inputPaneVisible, _viewingReplies = true, _disposed;
        private bool _dispatchingPlayerAction, _gamepadListFocusPending;
        private readonly PlayerUiState _playerUi = new PlayerUiState { Drawer = PlayerDrawer.Comments };
        private readonly Control ReplySide = new Control(), CommentSide = new Control();
        private readonly ListView ReplyList = new ListView(), CommentList = new ListView();
        private readonly Control ReplyListViewport = new Control { ActualHeight = 400 };
        private readonly CommentCard ReplyRootCard = new CommentCard();
        private readonly Button RepliesBackButton = new Button(), RepliesCloseButton = new Button(), CommentsCloseButton = new Button();
        private readonly Button MoreRepliesButton = new Button(), SendReplyCommentButton = new Button(), SendCommentButton = new Button();
        private readonly TextBox ReplyCommentInput = new TextBox(), CommentInput = new TextBox();
        private readonly Button SettingsCloseButton = new Button(), DanmakuSettingsCloseButton = new Button(), DanmakuCloudSyncButton = new Button(), DanmakuCloudLoginButton = new Button(), DanmakuResetButton = new Button();
        private readonly Button QualityAccountButton = new Button(), CommentLoginButton = new Button(), RefreshCommentsButton = new Button(), MoreCommentsButton = new Button(), WriteReplyCommentButton = new Button();
        private readonly Control DanmakuSettingsSide = new Control();
        private readonly ComboBox PartSelector = new ComboBox();
        private readonly Grid QualityOptions = new Grid();
        private VideoDetail _video = new VideoDetail { Aid = 900 };
        private long CurrentAccountMid = 10;
        private CommentItem _replyRoot = new CommentItem { Id = 100, Author = "Root" };
        private CommentItem _commentComposerReturnItem, _replyComposerTarget;
        private bool _commentComposerReturnReplies;
        private long _commentComposerVideoAid, _replyComposerAid, _replyComposerAccount;
        private CommentCard _commentActionCard;
        private Control _commentActionReturn;
        private CommentImageViewer _commentImages;
        private DateTimeOffset _lastBack;
        private ListView _gamepadNavigationList, _manuallyScrolledList;
        private int _gamepadNavigationIndex = -1;
        private readonly Dictionary<long, string> _replyDrafts = new Dictionary<long, string>();
        private int _openedEditors, _openedThreads, _outlines;
        private bool _lastBringIntoView;
        private Control GamepadDrawerRoot => _viewingReplies ? ReplySide : CommentSide;
        private void SetStatus(string text) { }
        private void ApplyPlayerUi() { }
        private void UpdateGamepadFocusOutline() { ++_outlines; }
        private static bool IsGamepadDescendant(DependencyObject child, DependencyObject root)
        { while (child != null) { if (ReferenceEquals(child, root)) return true; child = child.Parent; } return false; }
        private static T FindGamepadAncestor<T>(DependencyObject child, DependencyObject root) where T : DependencyObject
        { while (child != null && child != root) { if (child is T match) return match; child = child.Parent; } return null; }
        private static T FindGamepadChild<T>(DependencyObject root) where T : DependencyObject => (root as ListViewItem)?.Card as T;
        private bool IgnorePlayerClick() => false;
        private void DisablePointerCursor() { }
        private void DrawerClose_Click(object sender, RoutedEventArgs args) { }
        private void DanmakuCloudSync_Click(object sender, RoutedEventArgs args) { }
        private void Account_Click(object sender, RoutedEventArgs args) { }
        private void DanmakuReset_Click(object sender, RoutedEventArgs args) { }
        private void DanmakuOption_Click(object sender, RoutedEventArgs args) { }
        private void RepliesBack_Click(object sender, RoutedEventArgs args) { }
        private void RefreshComments_Click(object sender, RoutedEventArgs args) { }
        private void MoreComments_Click(object sender, RoutedEventArgs args) { }
        private void SendReplyComment_Click(object sender, RoutedEventArgs args) { }
        private void SendComment_Click(object sender, RoutedEventArgs args) { }
        private void MoreReplies_Click(object sender, RoutedEventArgs args) { }
        private void Quality_Click(object sender, RoutedEventArgs args) { }
        private void FocusGamepadControl(Control control, bool bringIntoView = true)
        { if (control?.IsEnabled == true) { FocusManager.Focused = control; _lastBringIntoView = bringIntoView; } }
        private void FocusGamepadListItem(ListView list, int index, bool preserveViewport = false)
        { FocusGamepadControl(list.ContainerFromIndex(index) as Control, !preserveViewport); }
        private void CancelGamepadListFocus()
        { _gamepadListFocusPending = false; _gamepadNavigationList = null; _gamepadNavigationIndex = -1; }
        private void CommentReplies_Requested(object sender, CommentRepliesRequestedEventArgs args)
        { ++_openedThreads; _viewingReplies = true; _replyRoot = args.Comment; ReplyRootCard.Item = args.Comment; FocusGamepadControl(ReplyRootCard); }
        private void CommentEntry_Click(object sender, RoutedEventArgs args) { ++_openedEditors; }
        private static int _checks;
        private static void Check(bool value, string description)
        { ++_checks; if (!value) throw new InvalidOperationException("FAIL: " + description); }
        private static MainPage Fresh(double rootHeight = 1500)
        {
            var page = new MainPage();
            page.ReplyList.Parent = page.ReplySide; page.CommentList.Parent = page.CommentSide;
            page.ReplyRootCard.Parent = page.ReplyList; page.ReplyRootCard.Item = page._replyRoot;
            page.ReplyList.Viewer = new ScrollViewer { ScrollableHeight = Math.Max(0, rootHeight - 400) };
            page.ReplyRootCard.GeometryViewer = page.ReplyList.Viewer; page.ReplyRootCard.ActualHeight = rootHeight;
            foreach (var control in new Control[] { page.RepliesBackButton, page.RepliesCloseButton, page.MoreRepliesButton, page.ReplyCommentInput, page.SendReplyCommentButton }) control.Parent = page.ReplySide;
            page.MoreRepliesButton.IsEnabled = false;
            FocusManager.Focused = page.ReplyRootCard;
            return page;
        }
        private static ListViewItem AddReply(MainPage page, long id, double top = 1500, double height = 180)
        {
            var item = new CommentItem { Id = id, RootId = page._replyRoot.Id, Author = "Child " + id };
            var row = new ListViewItem { Content = item, Parent = page.ReplyList, GeometryViewer = page.ReplyList.Viewer, ContentTop = top, ActualHeight = height };
            page.ReplyList.Items.Add(item); page.ReplyList.Rows.Add(row); return row;
        }
        public static string RunThreadNavigationTests()
        {
            _checks = 0;
            foreach (bool withPicture in new[] { false, true })
            foreach (int replyCount in new[] { 0, 8 })
            {
                var summary = Fresh(150); summary._viewingReplies = false;
                var item = new CommentItem { Id = 200 + replyCount, Author = "Summary", ReplyCount = replyCount };
                if (withPicture) item.Pictures.Add(new object());
                var summaryRow = new ListViewItem { Content = item, Parent = summary.CommentList };
                var summaryAction = new Button();
                var summaryCard = new CommentCard { Item = item, Parent = summaryRow, Primary = summaryAction };
                summaryAction.Parent = summaryCard; summaryRow.Card = summaryCard;
                summary.CommentList.Items.Add(item); summary.CommentList.Rows.Add(summaryRow);
                summary.FocusGamepadControl(summaryRow);
                summary.ActivateGamepadDrawerControl(summaryRow);
                Check(summary._viewingReplies && summary._openedThreads == 1 && ReferenceEquals(summary._replyRoot, item), "Real drawer dispatcher first A opens every summary floor, including text-only and zero-reply comments.");
                Check(ReferenceEquals(FocusManager.Focused, summary.ReplyRootCard) && summaryCard.Activations == 0, "Summary A selects the thread root without voting or opening a picture.");
                var nestedAction = new Button { Parent = summary.ReplyRootCard }; summary.ReplyRootCard.Primary = nestedAction;
                summary.ActivateGamepadDrawerControl(summary.ReplyRootCard);
                Check(ReferenceEquals(FocusManager.Focused, nestedAction) && summary.ReplyRootCard.Activations == 0, "Second A on the thread root selects its picture or reaction without invoking it.");
                summary.ActivateGamepadDrawerControl(nestedAction);
                Check(summary.ReplyRootCard.Activations == 1 && summary._openedThreads == 1, "Third A confirms exactly the selected thread action without re-entering the floor.");
                Check(summary.ExitCommentActions() && ReferenceEquals(FocusManager.Focused, summary.ReplyRootCard), "B from root actions restores the same thread floor.");
                summary._viewingReplies = false;
                summary.ActivateGamepadDrawerControl(summaryAction);
                Check(summary._openedThreads == 2 && summaryCard.Activations == 0, "Native child-action focus in a summary still opens the thread before any action.");
            }
            var unrendered = Fresh(150); unrendered._viewingReplies = false;
            var plainItem = new CommentItem { Id = 300, Author = "No visual yet" };
            var plainRow = new ListViewItem { Content = plainItem, Parent = unrendered.CommentList };
            unrendered.ActivateGamepadDrawerControl(plainRow);
            Check(unrendered._openedThreads == 1 && ReferenceEquals(unrendered._replyRoot, plainItem), "An unrealized card with zero child replies still opens from its model row.");
            var clicked = Fresh(150); clicked._viewingReplies = false;
            clicked.CommentList_ItemClick(clicked.CommentList, new ItemClickEventArgs { ClickedItem = plainItem });
            Check(clicked._openedThreads == 1 && ReferenceEquals(clicked._replyRoot, plainItem), "Native click on a text-only summary opens the identical inline thread.");
            var pictured = Fresh(150); pictured._replyRoot.Pictures.Add(new object());
            var pictureAction = new Button { Parent = pictured.ReplyRootCard }; pictured.ReplyRootCard.Primary = pictureAction;
            pictured.ReplyRootCard.OnActivate = () => pictured.CommentImage_Requested(pictured.ReplyRootCard, new CommentImageRequestedEventArgs { Comment = pictured._replyRoot });
            pictured.ActivateGamepadDrawerControl(pictured.ReplyRootCard); pictured.ActivateGamepadDrawerControl(pictureAction);
            Check(pictured._dialogOpen && pictured._commentImages.IsOpen && ReferenceEquals(pictured._commentImages.ReturnControl, pictureAction), "Thread picture A passes the selected image action as the full-screen viewer's exact return focus.");
            pictured._commentImages.Close();
            Check(!pictured._dialogOpen && pictured._viewingReplies && ReferenceEquals(FocusManager.Focused, pictureAction), "Image B restores its image action inside the same thread instead of closing the floor.");
            Check(pictured.ExitCommentActions() && ReferenceEquals(FocusManager.Focused, pictured.ReplyRootCard), "The next B leaves image/reaction selection and resumes browsing the same floor.");
            var page = Fresh(); var viewer = page.ReplyList.Viewer;
            Check(page.ReplyList.Items.Count == 0, "Fixture has only a root header and no child rows.");
            Check(page.NavigateCommentDrawerControls(page.ReplyRootCard, PlayerDirection.Down), "D-pad owns an expanded root with no replies.");
            Check(viewer.VerticalOffset == 260 && ReferenceEquals(FocusManager.Focused, page.ReplyRootCard), "Down pages a tall empty-thread root without leaving its floor.");
            page.NavigateCommentDrawerControls(page.ReplyRootCard, PlayerDirection.Down);
            Check(viewer.VerticalOffset == 520, "Successive Down reads the next root page.");
            page.NavigateCommentDrawerControls(page.ReplyRootCard, PlayerDirection.Up);
            Check(viewer.VerticalOffset == 260, "Up reads the previous root page.");
            viewer.VerticalOffset = viewer.ScrollableHeight;
            page.NavigateCommentDrawerControls(page.ReplyRootCard, PlayerDirection.Down);
            Check(ReferenceEquals(FocusManager.Focused, page.ReplyCommentInput), "At the root bottom an empty thread reaches the fixed reply composer.");
            viewer.VerticalOffset = 0; FocusManager.Focused = page.ReplyRootCard;
            Check(page.ScrollReplyThread(75) && viewer.VerticalOffset == 75, "R scrolls a header-only thread despite zero child items.");
            Check(ReferenceEquals(FocusManager.Focused, page.ReplyRootCard) && !page._lastBringIntoView, "R preserves the visible root anchor without scrolling focus back to its top.");
            Check(ReferenceEquals(page._commentComposerReturnItem, page._replyRoot), "Header scrolling remembers the root as composer-return floor.");
            page.FocusGamepadControl(page.ReplyCommentInput); page.RestoreCommentComposerPosition();
            Check(ReferenceEquals(FocusManager.Focused, page.ReplyRootCard), "Returning from composer restores a root header, rather than looking for an item container.");
            page.NavigateCommentDrawerControls(page.RepliesBackButton, PlayerDirection.Down);
            Check(ReferenceEquals(FocusManager.Focused, page.ReplyRootCard), "Down from thread Back selects root before child replies.");
            page.NavigateCommentDrawerControls(page.RepliesCloseButton, PlayerDirection.Down);
            Check(ReferenceEquals(FocusManager.Focused, page.ReplyRootCard), "Down from thread Close selects root before child replies.");
            page = Fresh(150); viewer = page.ReplyList.Viewer; viewer.ScrollableHeight = 500;
            var row = AddReply(page, 101, 200);
            page.NavigateCommentDrawerControls(page.ReplyRootCard, PlayerDirection.Down);
            Check(ReferenceEquals(FocusManager.Focused, row), "A fully visible short root moves to its first child.");
            page.FocusGamepadControl(page.ReplyRootCard); page.ScrollReplyThread(350);
            Check(ReferenceEquals(FocusManager.Focused, row), "R reanchors to a visible child after the root leaves the viewport.");
            Check(page._gamepadNavigationIndex == 0 && ReferenceEquals(page._commentComposerReturnItem, row.Content), "R updates logical child index and composer-return floor together.");
            page = Fresh(); row = AddReply(page, 102); var child = (CommentItem)row.Content;
            page.FocusGamepadControl(row); Check(page.TryOpenCommentComposer(), "X opens reply entry on the selected child floor.");
            Check(ReferenceEquals(page._replyComposerTarget, child) && page._openedEditors == 1 && page._openedThreads == 0, "X targets the selected child without reopening or replacing its thread.");
            page.ReplyCommentInput.Text = "draft for child"; page.FocusGamepadControl(page.ReplyCommentInput); page.TryOpenCommentComposer();
            Check(ReferenceEquals(page._replyComposerTarget, child) && page.ReplyCommentInput.Text == "draft for child", "X on reply input preserves child target and draft.");
            page.FocusGamepadControl(page.SendReplyCommentButton); page.TryOpenCommentComposer();
            Check(ReferenceEquals(page._replyComposerTarget, child) && page.ReplyCommentInput.Text == "draft for child", "X on Publish preserves child target and draft.");
            page.FocusGamepadControl(page.ReplyRootCard); page.TryOpenCommentComposer();
            Check(ReferenceEquals(page._replyComposerTarget, page._replyRoot) && page.ReplyCommentInput.Text == "", "X on root deliberately selects its separate draft.");
            page.FocusGamepadControl(row); page.TryOpenCommentComposer();
            Check(ReferenceEquals(page._replyComposerTarget, child) && page.ReplyCommentInput.Text == "draft for child", "Reselecting child restores its own draft.");
            page.CurrentAccountMid = 11; page.TryOpenCommentComposer();
            Check(page.ReplyCommentInput.Text == "" && page._replyDrafts.Count == 0 && page._replyComposerAccount == 11, "Changing account clears old reply drafts before selecting a target.");
            var action = new Button(); var card = new CommentCard { Item = child, Parent = row, Primary = action };
            action.Parent = card; page.FocusGamepadControl(row); page.EnterCommentActions(card, row);
            Check(ReferenceEquals(FocusManager.Focused, action) && ReferenceEquals(page._commentActionCard, card), "Row A enters that row's actions without invoking a write.");
            Check(page.ExitCommentActions() && ReferenceEquals(FocusManager.Focused, row), "B leaves action mode and restores the exact child floor.");
            Check(!page.ExitCommentActions(), "A second B belongs to the enclosing thread, not a stale action group.");
            page.EnterCommentActions(card, row); row.IsLoaded = false; page.ExitCommentActions();
            Check(page._commentActionCard == null, "Unloaded action return target cannot retain stale action mode.");
            page._dialogOpen = true; int opened = page._openedEditors;
            Check(!page.TryOpenCommentComposer() && page._openedEditors == opened, "An image or modal owner prevents X from opening another editor.");
            page._dialogOpen = false; page._inputPaneVisible = true;
            Check(!page.TryOpenCommentComposer(), "Native keyboard retains ownership while visible.");
            return "Comment thread navigation: " + _checks + " production integration checks passed (offline XAML layout substitutes).";
        }
    }
}
