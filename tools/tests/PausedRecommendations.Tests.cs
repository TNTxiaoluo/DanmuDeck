using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
namespace BiliXbox
{
    public sealed partial class MainPage
    {
        private enum Visibility { Visible, Collapsed }
        private enum FocusState { Keyboard }
        private sealed class VideoItem { public string Bvid; }
        private sealed class Grid { public List<VideoItem> Items = new List<VideoItem>(); }
        private sealed class Panel { public Visibility Visibility; }
        private sealed class Label { public string Text; }
        private sealed class Button { public bool IsEnabled = true; public void Focus(FocusState state) { } }
        private sealed class Input { public void SelectButton(int index,int count) { } }
        private sealed class Api
        {
            public int Requests;
            public Func<CancellationToken,Task<IReadOnlyList<VideoItem>>> Handler = token => Task.FromResult<IReadOnlyList<VideoItem>>(new[] { new VideoItem { Bvid = "related" } });
            public Task<IReadOnlyList<VideoItem>> GetRelatedVideosAsync(string bvid,CancellationToken token) { Requests++; return Handler(token); }
        }
        private bool _disposed, _watching = true, _creatorVisible, _libraryVisible, _liveVisible, _wantsPlayback, _sourceLoading, _dialogOpen, _inputPaneVisible;
        private enum PlayerDrawer { None, Settings }
        private sealed class FakeUi { public PlayerDrawer Drawer; }
        private sealed class FakeMedia { public object Source = new object(); }
        private readonly FakeUi _playerUi = new FakeUi();
        private readonly FakeMedia _media = new FakeMedia();
        private bool _pausedRecommendationsVisible, _pausedRecommendationsLoading, _adjustingVolume;
        private CancellationTokenSource _pausedRecommendationsCts, _watchCts = new CancellationTokenSource();
        private string _pausedRecommendationsBvid;
        private long _pausedRecommendationsAccount = -1, CurrentAccountMid = 42;
        private int _pausedRecommendationIndex = -1, _pausedRecommendationFocus, focusIndex = -1, restored;
        private VideoItem _video = new VideoItem { Bvid = "current" };
        private readonly Api _api = new Api();
        private readonly Grid PausedRecommendationGrid = new Grid();
        private readonly Panel PausedRecommendationsPanel = new Panel();
        private readonly Label PausedRecommendationNotice = new Label();
        private readonly Button PausedRecommendationRefresh = new Button(), PausedRecommendationClose = new Button();
        private readonly Input _playerInput = new Input();
        private readonly Button[] PlayerButtons = { new Button() };
        /* CAN_OPEN */
        private static void Cancel(ref CancellationTokenSource source) { var old = source; source = null; if (old != null) { old.Cancel(); old.Dispose(); } }
        private void ApplyPlayerUi() { }
        private void ShowControls() { }
        private void FocusPlayerInput() { restored++; }
        private void FocusPausedRecommendation(int index) { focusIndex = _pausedRecommendationIndex = index; }
        private static string Friendly(Exception error) => error.Message;
        /* METHODS */
        private static int checks;
        private static void Check(bool value,string name) { if (!value) throw new Exception(name); checks++; }
        public static async Task<string> RunPausedTests()
        {
            var page = new MainPage { _wantsPlayback = true }; await page.OpenPausedRecommendationsAsync();
            Check(!page._pausedRecommendationsVisible && page._api.Requests == 0, "Playing state cannot open pause recommendations");
            page._wantsPlayback = false; await page.OpenPausedRecommendationsAsync();
            Check(page._pausedRecommendationsVisible && page.PausedRecommendationGrid.Items.Count == 1 && page.focusIndex == 0, "Pause loads real rows and focuses first card");
            page.ClosePausedRecommendations();
            Check(!page._pausedRecommendationsVisible && !page._wantsPlayback && page.restored == 1, "Back closes without resuming video");
            await page.OpenPausedRecommendationsAsync(); Check(page._api.Requests == 1, "Same video/account reopens cached rows");
            page.ClosePausedRecommendations(); page.CurrentAccountMid = 99; await page.OpenPausedRecommendationsAsync();
            Check(page._api.Requests == 2 && page._pausedRecommendationsAccount == 99, "Different account reloads recommendations");
            await page.OpenPausedRecommendationsAsync(true); Check(page._api.Requests == 3, "Explicit refresh reloads");
            page = new MainPage(); var pending = new TaskCompletionSource<IReadOnlyList<VideoItem>>();
            page._api.Handler = token => pending.Task;
            var loading = page.OpenPausedRecommendationsAsync(); page.ClosePausedRecommendations(false,true);
            pending.SetResult(new[] { new VideoItem { Bvid = "late" } }); await loading;
            Check(!page._pausedRecommendationsVisible && page.PausedRecommendationGrid.Items.Count == 0 && page.focusIndex == -1, "Late response after B cannot refill or steal focus");
            page = new MainPage(); pending = new TaskCompletionSource<IReadOnlyList<VideoItem>>(); page._api.Handler = token => pending.Task;
            loading = page.OpenPausedRecommendationsAsync(); page._video = new VideoItem { Bvid = "other" };
            pending.SetResult(new[] { new VideoItem { Bvid = "late" } }); await loading;
            Check(page.PausedRecommendationGrid.Items.Count == 0 && page.focusIndex == -1, "Old video response rejected");
            page = new MainPage(); pending = new TaskCompletionSource<IReadOnlyList<VideoItem>>(); page._api.Handler = token => pending.Task;
            loading = page.OpenPausedRecommendationsAsync(); page.CurrentAccountMid = 99;
            pending.SetResult(new[] { new VideoItem { Bvid = "late" } }); await loading;
            Check(page.PausedRecommendationGrid.Items.Count == 0 && page._pausedRecommendationsAccount == -1, "Old account response rejected");
            page = new MainPage(); page._api.Handler = token => throw new InvalidOperationException("fixture unavailable"); await page.OpenPausedRecommendationsAsync();
            Check(page.PausedRecommendationGrid.Items.Count == 0 && page.PausedRecommendationRefresh.IsEnabled && !page._pausedRecommendationsLoading, "Failure remains retryable without fake recommendations");
            return "PASS: " + checks + " production pause-recommendation cache, cancellation, ownership, retry and return assertions.";
        }
    }
}
