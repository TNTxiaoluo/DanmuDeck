// TEST ONLY. The tested methods are inserted directly from CreatorHubView.cs.
// Dummy text controls and controlled API tasks expose ownership and completion
// races without real XAML, account persistence, requests, sleeps or gamepad input.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BiliXbox.CreatorProfileRefreshTests
{
    public enum Visibility { Visible, Collapsed }
    public sealed class Control
    {
        private bool _enabled;
        public int EnabledAssignments;
        public bool IsEnabled { get { return _enabled; } set { _enabled = value; EnabledAssignments++; } }
        public bool IsReadOnly;
        public string Text = "", Url = "", PlaceholderText = "";
        public object Content;
        public Visibility Visibility;
    }
    public sealed class CreatorProfile
    {
        public string Name = "", AvatarUrl = "", Signature = "", FollowersText = "", FollowingText = "", VideoCountText = "";
    }
    public sealed class Client
    {
        public sealed class Call { public long Mid; public CancellationToken Token; }
        public readonly List<Call> Calls = new List<Call>();
        public Func<long, CancellationToken, Task<CreatorProfile>> Provider;
        public bool IsLoggedIn = false;
        public Task<CreatorProfile> GetCreatorProfileAsync(long mid, CancellationToken token)
        { Calls.Add(new Call { Mid = mid, Token = token }); return Provider(mid, token); }
    }
    public sealed class Target { public bool IsAvailable = false, CanPost = false; }
    public sealed class DraftState { public bool RetryArmed = false, IsUncertain = false; }
    public sealed class Reply { public string Author = ""; }
    public sealed class CreatorHubView
    {
        private enum HubPane { Videos, Dynamics, Detail, Replies }
        private readonly Client _api = new Client();
        private CancellationTokenSource _sessionCts = new CancellationTokenSource(), _commentCts = null, _profileLoadOwner = null;
        private HubPane _pane = HubPane.Videos;
        private long _mid = 42;
        private int _videoPage = 9;
        private string _profileNotice = "", _draft = "";
        private bool _videosBusy = false, _dynamicsBusy = false, _commentsBusy = false, _repliesBusy = false;
        private bool _videoHasMore = false, _dynamicHasMore = false, _commentClosed = false, _posting = false, _writing = false;
        private Target _target = null;
        private DraftState _draftState = null;
        private Reply _composeReply = null;
        private bool _updatingDraftEditor = false;
        private readonly Control _name = new Control(), _avatar = new Control(), _signature = new Control(), _stats = new Control();
        private readonly Control _previous = new Control(), _next = new Control(), _moreDynamics = new Control(), _refresh = new Control();
        private readonly Control _refreshComments = new Control(), _write = new Control(), _send = new Control(), _draftEditor = new Control();
        private readonly Control _login = new Control(), _moreComments = new Control(), _moreReplies = new Control(), _draftLabel = new Control();
        private readonly List<int> _videoRequests = new List<int>();
        private readonly List<bool> _dynamicRequests = new List<bool>(), _commentRequests = new List<bool>(), _replyRequests = new List<bool>();
        // These collaborators are not under test here. Existing paging/thread
        // suites exercise the production content methods separately.
        private Task _contentResult = Task.CompletedTask;
        private Task LoadVideosAsync(int page) { _videoRequests.Add(page); return _contentResult; }
        private Task LoadDynamicsAsync(bool reset) { _dynamicRequests.Add(reset); return _contentResult; }
        private Task LoadCommentsAsync(bool reset) { _commentRequests.Add(reset); return _contentResult; }
        private Task LoadRepliesAsync(bool reset) { _replyRequests.Add(reset); return _contentResult; }
        private void SelectDraft() { }

        /* PRODUCTION_METHODS */

        private static int _checks;
        private static void Check(bool value, string message)
        { if (!value) throw new Exception("FAIL: " + message); _checks++; }
        private static CreatorProfile Profile(string name = "新的UP主") => new CreatorProfile
        {
            Name = name, AvatarUrl = "https://i0.hdslb.com/current.png", Signature = "当前签名",
            FollowersText = "138 粉丝", FollowingText = "12 关注", VideoCountText = "42 个视频"
        };
        private static CreatorHubView Pending(out TaskCompletionSource<CreatorProfile> response)
        {
            var view = new CreatorHubView();
            var pending = new TaskCompletionSource<CreatorProfile>();
            view._api.Provider = (mid, token) => pending.Task;
            response = pending;
            return view;
        }
        private static async Task SameOwnerSingleFlightAndSuccess()
        {
            TaskCompletionSource<CreatorProfile> response;
            var view = Pending(out response);
            view._profileNotice = "上次失败";
            var owner = view._sessionCts;
            var first = view.LoadProfileAsync(owner);
            Check(view._api.Calls.Count == 1 && view._api.Calls[0].Mid == 42
                && view._api.Calls[0].Token == owner.Token, "profile call retains selected MID and owner cancellation token");
            Check(view._profileLoadOwner == owner && !view._refresh.IsEnabled, "current profile request disables browse refresh");
            await view.LoadProfileAsync(owner);
            await view.LoadProfileAsync(owner);
            Check(view._api.Calls.Count == 1 && !first.IsCompleted, "same-owner duplicate profile loads do not issue parallel requests");
            response.SetResult(Profile());
            await first;
            Check(view._profileNotice == "" && view._name.Text == "新的UP主" && view._avatar.Url == "https://i0.hdslb.com/current.png"
                && view._signature.Text == "当前签名", "successful current profile replaces metadata and clears previous failure notice");
            Check(view._stats.Text == "138 粉丝    12 关注    42 个视频", "successful current profile preserves actual metadata statistics");
            Check(view._profileLoadOwner == null && view._refresh.IsEnabled && !view._updatingDraftEditor,
                "completed current profile releases ownership, enables refresh and leaves editor updates balanced");
            view._api.Provider = (mid, token) => Task.FromResult(Profile("再次刷新"));
            await view.LoadProfileAsync(owner);
            Check(view._api.Calls.Count == 2 && view._name.Text == "再次刷新", "completed owner may explicitly refresh again");
        }
        private static async Task FailureAndRetry()
        {
            var view = new CreatorHubView();
            view._api.Provider = (mid, token) => Task.FromException<CreatorProfile>(new Exception("fixture metadata failure"));
            await view.LoadProfileAsync(view._sessionCts);
            Check(view._profileNotice.Contains("fixture metadata failure") && view._stats.Text == view._profileNotice,
                "current profile failure remains visible for explicit retry");
            Check(view._profileLoadOwner == null && view._refresh.IsEnabled, "profile failure releases busy state");
            view._api.Provider = (mid, token) => Task.FromResult(Profile());
            await view.RefreshAsync();
            Check(view._api.Calls.Count == 2 && view._profileNotice == "", "browse refresh retries a failed profile and clears stale warning after success");
            Check(view._videoPage == 9 && view._videoRequests.Count == 1 && view._videoRequests[0] == 1,
                "refresh requests first content page instead of relying on old page number");
        }
        private static async Task ReplacedOwnerCompletion(string outcome)
        {
            var view = new CreatorHubView();
            var firstResponse = new TaskCompletionSource<CreatorProfile>();
            var currentResponse = new TaskCompletionSource<CreatorProfile>();
            view._api.Provider = (mid, token) => mid == 42 ? firstResponse.Task : currentResponse.Task;
            var oldOwner = view._sessionCts;
            var first = view.LoadProfileAsync(oldOwner);
            view._sessionCts = new CancellationTokenSource();
            view._mid = 84;
            view._name.Text = "当前主页名称";
            view._profileNotice = "当前主页已有失败提示";
            var current = view.LoadProfileAsync(view._sessionCts);
            Check(view._api.Calls.Count == 2 && view._profileLoadOwner == view._sessionCts && !view._refresh.IsEnabled,
                "new owner starts its own profile request while old request remains pending: " + outcome);
            var assignments = view._refresh.EnabledAssignments;
            var stats = view._stats.Text;
            if (outcome == "failure") firstResponse.SetException(new Exception("旧主页失败"));
            else if (outcome == "cancellation") { oldOwner.Cancel(); firstResponse.SetCanceled(); }
            else firstResponse.SetResult(Profile("旧主页迟到名称"));
            await first;
            Check(view._name.Text == "当前主页名称" && view._profileNotice == "当前主页已有失败提示" && view._stats.Text == stats,
                "replaced owner's late completion cannot overwrite current metadata or notice: " + outcome);
            Check(view._profileLoadOwner == view._sessionCts && !view._refresh.IsEnabled && !current.IsCompleted,
                "old completion cannot release new owner's profile busy state: " + outcome);
            Check(view._refresh.EnabledAssignments == assignments,
                "old completion performs no current-owner UI action update: " + outcome);
            currentResponse.SetResult(Profile("当前新主页"));
            await current;
            Check(view._name.Text == "当前新主页" && view._profileLoadOwner == null && view._profileNotice == "" && view._refresh.IsEnabled,
                "new owner independently completes and restores refresh: " + outcome);
        }
        private static async Task CurrentCancellationAndInvalidOwners()
        {
            TaskCompletionSource<CreatorProfile> response;
            var view = Pending(out response);
            await view.LoadProfileAsync(null);
            await view.LoadProfileAsync(new CancellationTokenSource());
            Check(view._api.Calls.Count == 0, "null or unrelated owner cannot start a current profile request");
            var owner = view._sessionCts;
            var pending = view.LoadProfileAsync(owner);
            view._name.Text = "保留当前名称";
            view._stats.Text = "保留当前统计";
            var assignments = view._refresh.EnabledAssignments;
            owner.Cancel();
            response.SetCanceled();
            await pending;
            Check(view._profileLoadOwner == null && view._name.Text == "保留当前名称" && view._stats.Text == "保留当前统计"
                && view._profileNotice == "", "current owner cancellation releases its own marker without manufacturing a failure notice");
            Check(view._refresh.EnabledAssignments == assignments, "canceled owner cannot update inactive UI actions");
            await view.LoadProfileAsync(owner);
            Check(view._api.Calls.Count == 1, "already canceled owner cannot restart profile API request");
        }
        private static async Task RefreshContentAndProfileJoin()
        {
            TaskCompletionSource<CreatorProfile> response;
            var view = Pending(out response);
            var content = new TaskCompletionSource<bool>();
            view._contentResult = content.Task;
            view._profileNotice = "资料需要重试";
            var refresh = view.RefreshAsync();
            Check(view._api.Calls.Count == 1 && view._videoRequests.Count == 1 && view._videoRequests[0] == 1
                && !refresh.IsCompleted, "failed profile retry runs alongside first content page refresh");
            content.SetResult(true);
            Check(!refresh.IsCompleted && !view._refresh.IsEnabled, "completed content cannot finish combined refresh while profile remains pending");
            response.SetResult(Profile());
            await refresh;
            Check(view._profileNotice == "" && view._refresh.IsEnabled, "combined refresh completes after profile and content both finish");

            view = new CreatorHubView();
            view._api.Provider = (mid, token) => { throw new Exception("healthy profile must not be requested again"); };
            await view.RefreshAsync();
            Check(view._api.Calls.Count == 0 && view._videoRequests.Count == 1 && view._videoRequests[0] == 1,
                "healthy profile refresh reloads content without unnecessary profile API request");
            view._pane = HubPane.Dynamics;
            await view.RefreshAsync();
            Check(view._dynamicRequests.Count == 1 && view._dynamicRequests[0] && view._api.Calls.Count == 0,
                "dynamic refresh resets its content cursor without unnecessary profile API request");
            view._profileNotice = "动态页资料也失败";
            view._api.Provider = (mid, token) => Task.FromResult(Profile());
            await view.RefreshAsync();
            Check(view._dynamicRequests.Count == 2 && view._dynamicRequests[1] && view._api.Calls.Count == 1 && view._profileNotice == "",
                "dynamic browse refresh also retries failed profile");

            view._profileNotice = "详情不触发资料刷新";
            view._api.Provider = (mid, token) => { throw new Exception("comment refresh must not refresh profile"); };
            view._pane = HubPane.Detail;
            await view.RefreshAsync();
            view._pane = HubPane.Replies;
            await view.RefreshAsync();
            Check(view._commentRequests.Count == 1 && view._commentRequests[0] && view._replyRequests.Count == 1 && view._replyRequests[0]
                && view._api.Calls.Count == 1, "detail refresh remains scoped to comments or replies instead of fetching metadata");

            view = new CreatorHubView();
            view._sessionCts = null;
            view._profileNotice = "重新创建会话后重试";
            view._api.Provider = (mid, token) => Task.FromResult(Profile());
            await view.RefreshAsync();
            Check(view._sessionCts != null && view._api.Calls.Count == 1 && view._api.Calls[0].Token == view._sessionCts.Token
                && view._profileNotice == "", "refresh establishes a current session before retrying failed profile");
        }
        public static async Task<string> Run()
        {
            await SameOwnerSingleFlightAndSuccess();
            await FailureAndRetry();
            await ReplacedOwnerCompletion("failure");
            await ReplacedOwnerCompletion("success");
            await ReplacedOwnerCompletion("cancellation");
            await CurrentCancellationAndInvalidOwners();
            await RefreshContentAndProfileJoin();
            return "PASS: " + _checks + " production creator profile ownership/refresh assertions (controlled tasks; no native UI, account persistence or live network).";
        }
    }
}
