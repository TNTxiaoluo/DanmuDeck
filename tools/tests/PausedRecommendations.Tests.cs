using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
namespace BiliXbox
{
    public sealed partial class MainPage
    {
        private enum Visibility { Visible, Collapsed }
        private enum FocusState { Keyboard }
        private enum VirtualKey { Left, Right, Up, Down, A }
        private enum PlayerDirection { Left, Right, Up, Down }
        private class VideoItem { public string Bvid; public IReadOnlyList<CollectionEpisode> CollectionEpisodes = Array.Empty<CollectionEpisode>(); }
        private sealed class CollectionEpisode : VideoItem { }
        private sealed class GridView { public List<VideoItem> Items = new List<VideoItem>(); public Visibility Visibility; }
        private sealed class Panel { public Visibility Visibility; public double Height; }
        private sealed class Label { public string Text; public Visibility Visibility; }
        private class Control { }
        private static class FocusManager
        {
            internal static Control Current;
            internal static Control GetFocusedElement() => Current;
        }
        private sealed class Button : Control { public bool IsEnabled = true; public void Focus(FocusState state) { FocusManager.Current = this; } }
        private sealed class RoutedEventArgs { }
        private sealed class Input
        {
            public void SelectButton(int index,int count) { }
            public bool ConfirmKeyDown(DateTimeOffset now, bool repeat) => !repeat;
        }
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
        private bool _pausedRecommendationsVisible, _pausedRecommendationsLoading, _adjustingVolume, _dispatchingPlayerAction;
        private CancellationTokenSource _pausedRecommendationsCts, _watchCts = new CancellationTokenSource();
        private string _pausedRecommendationsBvid;
        private long _pausedRecommendationsAccount = -1, CurrentAccountMid = 42;
        private int _pausedRecommendationIndex = -1, _pausedCollectionIndex = -1, _pausedRecommendationFocus, focusIndex = -1, restored;
        private GridView _pausedFocusGrid;
        private VideoItem openedVideo;
        private IReadOnlyList<CollectionEpisode> openedCollection;
        private VideoItem _video = new VideoItem { Bvid = "current" };
        private readonly Api _api = new Api();
        private readonly GridView PausedRecommendationGrid = new GridView(), PausedCollectionGrid = new GridView();
        private readonly Panel PausedRecommendationsPanel = new Panel();
        private readonly Label PausedRecommendationNotice = new Label(), PausedPanelTitle = new Label(), PausedRelatedTitle = new Label();
        private readonly Button PausedRecommendationRefresh = new Button(), PausedRecommendationClose = new Button(), PlayPauseButton = new Button();
        private readonly Input _playerInput = new Input();
        private readonly Button[] PlayerButtons = { new Button() };
        /* CAN_OPEN */
        private static void Cancel(ref CancellationTokenSource source) { var old = source; source = null; if (old != null) { old.Cancel(); old.Dispose(); } }
        private void ApplyPlayerUi() { }
        private void ShowControls() { }
        private void FocusPlayerInput() { restored++; }
        private void FocusPausedRecommendation(int index) { _pausedFocusGrid = PausedRecommendationGrid; focusIndex = _pausedRecommendationIndex = index; }
        private void FocusPausedCollection(int index) { _pausedFocusGrid = PausedCollectionGrid; _pausedCollectionIndex = index; }
        private static bool PlayerDirectionKey(VirtualKey key, out PlayerDirection direction)
        { direction = (PlayerDirection)key; return key != VirtualKey.A; }
        private static bool PlayerConfirmKey(VirtualKey key) => key == VirtualKey.A;
        private void PlayPause_Click(object sender, RoutedEventArgs args) { _wantsPlayback = !_wantsPlayback; }
        private Task PlayCollectionEpisodeAsync(CollectionEpisode item, IReadOnlyList<CollectionEpisode> collection)
        { openedVideo = item; openedCollection = collection; return Task.CompletedTask; }
        private Task OpenPausedRecommendationVideoAsync(VideoItem item)
        { openedVideo = item; return Task.CompletedTask; }
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
            page = new MainPage { _wantsPlayback = true };
            var collection = new[] { new CollectionEpisode { Bvid = "prior" }, new CollectionEpisode { Bvid = "current" },
                new CollectionEpisode { Bvid = "next" } };
            page._video.CollectionEpisodes = collection;
            pending = new TaskCompletionSource<IReadOnlyList<VideoItem>>();
            page._api.Handler = token => pending.Task;
            loading = page.OpenPausedRecommendationsAsync();
            Check(page._wantsPlayback && page.PausedCollectionGrid.Items.SequenceEqual(collection)
                && page._pausedFocusGrid == page.PausedCollectionGrid && page._pausedCollectionIndex == 1,
                "Opening while watching builds the collection immediately and focuses its current episode");
            pending.SetResult(new[] { new VideoItem { Bvid = "related" } }); await loading;
            Check(page._pausedFocusGrid == page.PausedCollectionGrid && page.PausedRecommendationGrid.Items.Count == 1,
                "Loading related videos does not take collection focus");
            page.HandlePausedRecommendationKey(VirtualKey.Down, true);
            Check(page._pausedFocusGrid == page.PausedCollectionGrid, "Holding opening Down keeps the initial selection");
            page.HandlePausedRecommendationKey(VirtualKey.Down, false);
            Check(page._pausedFocusGrid == page.PausedRecommendationGrid && page._pausedRecommendationIndex == 0, "Down moves from collection to related videos");
            page.HandlePausedRecommendationKey(VirtualKey.Up, false);
            Check(page._pausedFocusGrid == page.PausedCollectionGrid && page._pausedCollectionIndex == 1, "Up returns to the same collection card");
            page.HandlePausedRecommendationKey(VirtualKey.Right, false);
            page.HandlePausedRecommendationKey(VirtualKey.A, false);
            Check(page.openedVideo == collection[2] && ReferenceEquals(page.openedCollection, collection)
                && !page._pausedRecommendationsVisible, "A opens the selected distinct video with its collection queue");
            page = new MainPage { _wantsPlayback = false };
            page._video.CollectionEpisodes = collection;
            await page.OpenPausedRecommendationsAsync();
            page.HandlePausedRecommendationKey(VirtualKey.A, false);
            Check(page._wantsPlayback && page.openedVideo == null && !page._pausedRecommendationsVisible,
                "Selecting the current episode resumes without opening a new source");
            return "PASS: " + checks + " production pause-recommendation cache, cancellation, ownership, retry and return assertions.";
        }
    }
}
