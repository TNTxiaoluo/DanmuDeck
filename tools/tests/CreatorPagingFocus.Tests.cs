using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BiliXbox.CreatorPagingTests
{
    public enum Visibility { Visible, Collapsed }
    public enum FocusState { Keyboard }
    public enum CoreDispatcherPriority { Low }
    public enum ScrollIntoViewAlignment { Default, Leading }
    // Viewport right-stick ownership is covered by Test-ViewportFocusScroll;
    // this fixture only supplies the external cancellation collaborator.
    public static class ViewportFocusScroll { public static void Cancel(object scope) { } }
    public sealed class Control
    {
        public static Control Focused;
        public readonly int Index;
        public Control(int index) { Index = index; }
        public bool Focus(FocusState state) { Focused = this; return true; }
    }
    public sealed class VideoItem { public readonly int Id; public VideoItem(int id) { Id = id; } }
    public sealed class CommentCard { }
    public sealed class SearchPage
    {
        public List<VideoItem> Videos = new List<VideoItem>();
        public bool HasMore;
        public string Notice = "";
    }
    public sealed class TextBlock { public string Text; }
    public sealed class Items : List<object>
    {
        public Action BeforeClear;
        public new void Clear() { BeforeClear?.Invoke(); base.Clear(); }
    }
    public sealed class ListViewBase
    {
        public readonly Items Items = new Items();
        private readonly Dictionary<object, Control> _rows = new Dictionary<object, Control>();
        public object LastScrollItem;
        public ScrollIntoViewAlignment LastAlignment;
        public void ScrollIntoView(object item, ScrollIntoViewAlignment alignment)
        { LastScrollItem = item; LastAlignment = alignment; }
        public void UpdateLayout() { }
        public object ContainerFromIndex(int index)
        {
            var item = Items[index];
            Control row;
            if (!_rows.TryGetValue(item, out row)) { row = new Control(index); _rows.Add(item, row); }
            return row;
        }
    }
    public sealed class Dispatcher
    {
        private readonly Queue<Action> _callbacks = new Queue<Action>();
        public int Pending => _callbacks.Count;
        public Task RunAsync(CoreDispatcherPriority priority, Action callback)
        {
            var completion = new TaskCompletionSource<bool>();
            _callbacks.Enqueue(() => { callback(); completion.SetResult(true); });
            return completion.Task;
        }
        public void Drain() { while (_callbacks.Count > 0) _callbacks.Dequeue()(); }
    }
    public sealed class Client
    {
        public Func<int, CancellationToken, Task<SearchPage>> Handler;
        public int Calls;
        public Task<SearchPage> GetCreatorVideosAsync(long mid, int page, CancellationToken token)
        { Calls++; return Handler(page, token); }
    }
    public sealed class CreatorHubView
    {
        private enum HubPane { Videos, Dynamics }
        private readonly Client _api = new Client();
        private readonly ListViewBase _videoList = new ListViewBase(), _dynamicList = new ListViewBase();
        private readonly List<VideoItem> _videos = new List<VideoItem>();
        private readonly TextBlock _pageLabel = new TextBlock();
        private readonly Control _videosTab = new Control(-1), _write = new Control(-2);
        private readonly Dispatcher Dispatcher = new Dispatcher();
        private CancellationTokenSource _sessionCts = new CancellationTokenSource();
        private long _mid = 42;
        private HubPane _pane = HubPane.Videos;
        private int _focusGeneration, _pendingIndex = -1, _videoPage = 1;
        private bool _videosBusy, _videoHasMore, _videosLoaded;
        private ListViewBase _pendingList;
        private Control _lastFocus;
        private string _notice;
        private Visibility Visibility = Visibility.Visible;
        private ListViewBase ActiveList => _pane == HubPane.Videos ? _videoList : _dynamicList;
        private bool IsDetail => false;
        private void ClearCommentActions() { }
        private int NextCommentRow(ListViewBase list, int start, int step) => -1;
        private void EnsureSession() { }
        private void SelectDraft() { }
        private void ApplyPane() { }
        private void UpdateActions() { }
        private bool Current(CancellationTokenSource owner) => owner == _sessionCts && !owner.IsCancellationRequested;
        private void SetBrowseNotice(HubPane pane, string notice) { _notice = notice; }
        private static string Friendly(Exception error) => error.Message;
        private bool IsAvailable(Control control) => control != null;
        private void Focus(Control control) { _lastFocus = control; control.Focus(FocusState.Keyboard); }

        /* PRODUCTION_METHODS */

        private static int _passed;
        private static void Check(bool value, string message)
        { if (!value) throw new Exception("FAIL: " + message); _passed++; }
        private static SearchPage Page(int start, int count) => new SearchPage
        { Videos = Enumerable.Range(start, count).Select(id => new VideoItem(id)).ToList(), HasMore = count > 0 };
        private static CreatorHubView ExistingPage()
        {
            var view = new CreatorHubView();
            foreach (var item in Page(1, 8).Videos) { view._videos.Add(item); view._videoList.Items.Add(item); }
            view._lastFocus = new Control(-3); Control.Focused = view._lastFocus;
            return view;
        }
        public static async Task<string> Run()
        {
            _passed = 0;
            var view = ExistingPage();
            view._focusGeneration = 20; view._pendingList = view._videoList; view._pendingIndex = 7;
            bool invalidatedBeforeClear = false;
            view._videoList.Items.BeforeClear = () => invalidatedBeforeClear = view._focusGeneration > 20
                && view._pendingList == null && view._pendingIndex == -1;
            view._api.Handler = (page, token) => Task.FromResult(Page(100, 8));
            await view.LoadVideosAsync(2);
            Check(invalidatedBeforeClear, "old queued focus invalidated before old containers are cleared");
            Check(view._videoPage == 2 && view._pageLabel.Text == "第 2 页" && view._videosLoaded && view._videoHasMore,
                "successful page metadata advances with actual replacement");
            Check(view._pendingIndex == 0 && view._pendingList == view._videoList, "new page owns a first-row focus request");
            Check(ReferenceEquals(view._videoList.LastScrollItem, view._videoList.Items[0])
                && view._videoList.LastAlignment == ScrollIntoViewAlignment.Leading, "first row aligns with start of new page");
            view.Dispatcher.Drain();
            Check(Control.Focused.Index == 0 && ReferenceEquals(view._lastFocus, Control.Focused), "first item actually receives replacement focus");
            Check(!view._videosBusy && view._pendingList == null && view._pendingIndex == -1, "replacement and pending focus complete");
            view.FocusItem(view._videoList, 7); view.Dispatcher.Drain();
            Check(Control.Focused.Index == 7 && view._videoList.LastAlignment == ScrollIntoViewAlignment.Default,
                "ordinary selection retains normal scrolling rather than forcing page start");
            var selected = Control.Focused; var requests = view._api.Calls;
            view.RestoreFocus();
            Check(ReferenceEquals(selected, Control.Focused) && view._api.Calls == requests,
                "return restores saved selection without reloading or resetting the page");

            view = ExistingPage();
            view.FocusItem(view._videoList, 7); // old page callback remains queued
            view._api.Handler = (page, token) => Task.FromResult(Page(200, 4));
            await view.LoadVideosAsync(3); view.Dispatcher.Drain();
            Check(Control.Focused.Index == 0 && ((VideoItem)view._videoList.Items[0]).Id == 200,
                "queued old-page focus cannot overwrite the replacement's first focus");

            view = ExistingPage();
            var pending = new TaskCompletionSource<SearchPage>();
            view._api.Handler = (page, token) => pending.Task;
            var loading = view.LoadVideosAsync(2);
            await view.LoadVideosAsync(3);
            Check(view._api.Calls == 1, "busy load does not queue a second conflicting page mutation");
            view._pane = HubPane.Dynamics; view._focusGeneration = 55;
            var dynamicControl = new Control(900); view._lastFocus = Control.Focused = dynamicControl;
            view._pendingList = view._dynamicList; view._pendingIndex = 5;
            pending.SetResult(Page(300, 6)); await loading;
            Check(view._videoPage == 2 && view._videoList.Items.Count == 6, "hidden video response still updates its own list");
            Check(ReferenceEquals(Control.Focused, dynamicControl) && view._lastFocus == dynamicControl
                && view._focusGeneration == 55 && view._pendingList == view._dynamicList && view._pendingIndex == 5,
                "video response cannot steal another tab's focus or pending container work");
            Check(view.Dispatcher.Pending == 0 && view._videoList.LastScrollItem == null, "hidden tab does not schedule visual navigation");

            view = ExistingPage(); var original = view._videoList.Items[0]; selected = Control.Focused;
            view._api.Handler = (page, token) => Task.FromException<SearchPage>(new Exception("fixture failure"));
            await view.LoadVideosAsync(2);
            Check(ReferenceEquals(view._videoList.Items[0], original) && Control.Focused == selected && view._videoPage == 1,
                "failed page retains original list, page number and selection");
            Check(!view._videosBusy && view._notice.Contains("fixture failure"), "failure permits explicit retry");

            view = ExistingPage(); pending = new TaskCompletionSource<SearchPage>();
            view._api.Handler = (page, token) => pending.Task;
            loading = view.LoadVideosAsync(2); original = view._videoList.Items[0];
            view._sessionCts.Cancel(); view._sessionCts = new CancellationTokenSource();
            pending.SetResult(Page(400, 9)); await loading;
            Check(view._videoPage == 1 && ReferenceEquals(original, view._videoList.Items[0]) && view.Dispatcher.Pending == 0,
                "response for a replaced creator session cannot mutate the current page or focus");

            view = ExistingPage(); view._api.Handler = (page, token) => Task.FromResult(Page(500, 0));
            await view.LoadVideosAsync(1);
            Check(Control.Focused == view._videosTab && view._videoList.Items.Count == 0 && !view._videoHasMore,
                "empty valid page chooses the visible video tab instead of stale container");
            return "PASS: " + _passed + " production creator paging/focus assertions (controlled layout and network, no Xbox rendering).";
        }
    }
}
