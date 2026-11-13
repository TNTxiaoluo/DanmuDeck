namespace BiliXbox.InteractionViewTests
{
    public static class Tests
    {
        private static int _passed;
        private static void Check(bool value, string message) { if (!value) throw new Exception("FAIL: " + message); _passed++; }
        private static T Field<T>(Controls.VideoInteractionView view, string name)
            => (T)typeof(Controls.VideoInteractionView).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(view);
        private static Task Call(Controls.VideoInteractionView view, string name, params object[] arguments)
            => (Task)typeof(Controls.VideoInteractionView).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(view, arguments);
        private static Button Row(Controls.VideoInteractionView view, string id)
            => Field<List<Button>>(view, "_rows").Single(row => AutomationProperties.GetAutomationId(row) == id);
        private static Controls.VideoInteractionView View(BilibiliClient api)
        { var view = new Controls.VideoInteractionView(); view.Initialize(api); return view; }
        public static async Task<string> Run()
        {
            _passed = 0;
            var expiredApi = new BilibiliClient();
            expiredApi.Read = (aid, token) => { expiredApi.CurrentUser = null; throw new InvalidOperationException("expired"); };
            var expired = View(expiredApi);
            await expired.ShowAsync(200, 40, "Owner");
            Check(!Field<bool>(expired, "_busy") && Row(expired, "InteractionLoginButton").IsEnabled,
                "Expired-auth read clears spinner and exposes login instead of sticking disabled");
            Check(Field<VideoInteractionState>(expired, "_state") == null && !Row(expired, "InteractionLikeButton").IsEnabled,
                "Expired account's interaction state is discarded");

            var switchedApi = new BilibiliClient();
            var pendingRead = new TaskCompletionSource<VideoInteractionState>();
            switchedApi.Read = (aid, token) => pendingRead.Task;
            var switched = View(switchedApi);
            var opening = switched.ShowAsync(200, 40, "Owner");
            switchedApi.CurrentUser = new UserProfile { Mid = 31 };
            pendingRead.SetResult(new VideoInteractionState { Aid = 200, AccountMid = 30, IsLiked = true });
            await opening;
            Check(!Field<bool>(switched, "_busy") && Row(switched, "InteractionRefreshButton").IsEnabled,
                "Account switch during read unlocks new-account refresh");
            Check(Field<VideoInteractionState>(switched, "_state") == null && !Row(switched, "InteractionLikeButton").IsEnabled,
                "Old account read does not display its liked state under new account");
            switchedApi.Read = null;
            await Call(switched, "RefreshAsync", true);
            Check(Row(switched, "InteractionLikeButton").IsEnabled,
                "New account can recover without closing the drawer");

            var api = new BilibiliClient(); var view = View(api);
            await view.ShowAsync(200, 40, "Owner");
            Check(api.LikePosts + api.CoinPosts + api.FavoritePosts + api.TriplePosts == 0,
                "Opening interaction drawer only reads state");
            var pendingWrite = new TaskCompletionSource<bool>();
            api.WriteLike = () => pendingWrite.Task;
            var writing = Call(view, "LikeAsync");
            await Call(view, "LikeAsync");
            Check(api.LikePosts == 1 && Field<bool>(view, "_busy"), "Repeated action during pending POST is coalesced");
            api.State.IsLiked = true; pendingWrite.SetResult(true); await writing;
            Check(!Field<bool>(view, "_busy") && Field<VideoInteractionState>(view, "_state").IsLiked,
                "Like UI updates only after reading actual acknowledged state");
            api.WriteLike = () => { throw new TimeoutException("ambiguous"); };
            await Call(view, "LikeAsync");
            Check(!Row(view, "InteractionLikeButton").IsEnabled && Row(view, "InteractionRefreshButton").IsEnabled,
                "Ambiguous POST blocks another write but keeps explicit refresh usable");
            Check(api.LikePosts == 2, "Write failure never triggers an automatic POST retry");
            view.StopRequests(); await view.ShowAsync(200, 40, "Owner");
            Check(!Row(view, "InteractionLikeButton").IsEnabled, "Reopening retains unknown mutation until explicit refresh");
            await Call(view, "RefreshAsync", true);
            Check(Row(view, "InteractionLikeButton").IsEnabled && api.LikePosts == 2,
                "Explicit successful refresh observes state and rearms without writing");

            var authWriteApi = new BilibiliClient(); var authWrite = View(authWriteApi);
            await authWrite.ShowAsync(200, 40, "Owner");
            authWriteApi.WriteLike = () => { authWriteApi.CurrentUser = null; throw new InvalidOperationException("expired during write"); };
            await Call(authWrite, "LikeAsync");
            Check(!Field<bool>(authWrite, "_busy") && Row(authWrite, "InteractionLoginButton").IsEnabled,
                "Auth expiration during POST also clears spinner and exposes login");

            var folderApi = new BilibiliClient(); var folders = View(folderApi);
            await folders.ShowAsync(200, 40, "Owner"); await Call(folders, "OpenFavoritesAsync");
            var draft = Field<Controls.VideoFavoriteSelection>(folders, "_selection"); draft.Toggle(2);
            Check(folders.Back() && folderApi.FavoritePosts == 0, "B discards checkbox changes without submitting favorites");
            await Call(folders, "OpenFavoritesAsync");
            Check(!draft.Changed && !draft.Contains(2), "Reopening favorite selector uses server state, not abandoned draft");
            draft.Toggle(2); await Call(folders, "SaveFavoritesAsync");
            Check(folderApi.FavoritePosts == 1 && folderApi.Folders.Single(folder => folder.Id == 2).IsSelected,
                "Explicit save submits selected folder delta once");
            Check(Field<VideoInteractionState>(folders, "_state").IsFavorited && !Field<Controls.VideoInteractionSession>(folders, "_session").IsUncertain,
                "Favorite success is confirmed by both account state and actual folder membership");

            var coinApi = new BilibiliClient(); coinApi.State.Coins = 1; var coin = View(coinApi);
            await coin.ShowAsync(200, 40, "Owner");
            Check(!Row(coin, "InteractionTripleButton").IsEnabled, "Already-one-coin state never promises a restricted triple cost the API cannot request");
            await Call(coin, "TripleAsync"); Check(coinApi.TriplePosts == 0, "One-coin triple cannot be forced through its handler");
            await Call(coin, "CoinAsync", 2); Check(coinApi.CoinPosts == 0, "Invalid over-quota two-coin action rejected before API");
            await Call(coin, "CoinAsync", 1);
            Check(coinApi.CoinPosts == 1 && coinApi.State.Coins == 2 && !Row(coin, "InteractionCoinButton").IsEnabled,
                "Explicit one-coin action reaches quota then disables duplicate coin action");

            var partialApi = new BilibiliClient(); var partial = View(partialApi);
            await partial.ShowAsync(200, 40, "Owner");
            partialApi.WriteTriple = () => { partialApi.State.IsLiked = true; return Task.FromResult(new VideoTripleResult { Like = true }); };
            await Call(partial, "TripleAsync");
            Check(partialApi.TriplePosts == 1 && !Row(partial, "InteractionLikeButton").IsEnabled,
                "Partial triple acknowledgement never becomes optimistic all-three success or automatic retry");
            Check(Field<string>(partial, "_message").Contains("未确认") && !Field<string>(partial, "_message").Contains("三连结果已确认"),
                "Partial triple describes only reported fields");

            var staleApi = new BilibiliClient(); var stale = View(staleApi);
            await stale.ShowAsync(200, 40, "Owner");
            int cacheInvalidations = 0;
            stale.InteractionChanged += (sender, args) => ++cacheInvalidations;
            var stalePost = new TaskCompletionSource<bool>(); staleApi.WriteLike = () => stalePost.Task;
            var old = Call(stale, "LikeAsync");
            Check(cacheInvalidations == 1 && staleApi.LikePosts == 1 && !old.IsCompleted,
                "Pending mutation invalidates cached favorites before awaiting its remote result");
            stale.StopRequests();
            await stale.ShowAsync(201, 41, "New owner");
            staleApi.State.IsLiked = true; stalePost.SetResult(true); await old;
            Check(cacheInvalidations == 2 && staleApi.LikePosts == 1,
                "Closed-resource mutation completion invalidates again without repeating the POST");
            Check(Field<VideoInteractionState>(stale, "_state").Aid == 201 && !Field<VideoInteractionState>(stale, "_state").IsLiked,
                "Late old-video POST cannot mark new video liked");
            var oldButton = Row(stale, "InteractionLikeButton");
            var oldClick = Field<Dictionary<Button, Action>>(stale, "_actions")[oldButton];
            await stale.ShowAsync(202, 42, "Third owner");
            int beforeOldClick = staleApi.LikePosts;
            oldClick();
            Check(staleApi.LikePosts == beforeOldClick && Field<VideoInteractionState>(stale, "_state").Aid == 202,
                "Queued button action from old resource never POSTs against newly opened video");
            return "PASS: " + _passed + " actual production interaction-view/data orchestration assertions (UWP/API substitutes, no remote writes).";
        }
    }
}
