namespace BiliXbox
{
    public sealed partial class MainPage
    {
        private VideoEndMode _endMode;
        private AccountLibraryItem _playingEpisodeItem;
        private IReadOnlyList<AccountLibraryItem> _episodeChoices = Array.Empty<AccountLibraryItem>();
        private VideoDetail _video;
        private long _currentCid;
        private int _preferredQuality = 120;
        private bool _watching = true, _videoReturnsToCreator;
        private readonly CancellationTokenSource _watchCts = new CancellationTokenSource();
        private readonly FakeMedia _media = new FakeMedia();
        private readonly FakeApi _api = new FakeApi();
        private readonly FakeCreator CreatorHub = new FakeCreator();
        private long selectedCid, selectedEpisode;
        private string openedBvid;
        private CreatorPlaybackBookmark openedBookmark;
        private sealed class CreatorPlaybackBookmark
        {
            public long Cid;
            public int Quality;
            public bool Playing;
            public IReadOnlyList<CollectionEpisode> CollectionEpisodes;
        }
        private Task LoadPartAsync(long cid) { selectedCid = cid; return Task.CompletedTask; }
        private Task PlaySelectedEpisodeAsync(AccountLibraryItem item) { selectedEpisode = item.EpisodeId; return Task.CompletedTask; }
        private Task OpenVideoAsync(string bvid, string title, CreatorPlaybackBookmark bookmark = null)
        { openedBvid = bvid; openedBookmark = bookmark; return Task.CompletedTask; }
        private sealed class FakeMedia { public object Source = new object(); }
        private sealed class FakeApi
        {
            public AccountSeasonDetail Detail;
            public TaskCompletionSource<AccountSeasonDetail> Pending;
            public Task<AccountSeasonDetail> GetPublicSeasonDetailAsync(long season, long episode, CancellationToken token) => Pending?.Task ?? Task.FromResult(Detail);
        }
        private sealed class FakeCreator
        {
            public bool ContinuousPlayback;
            public VideoItem Next;
            public TaskCompletionSource<VideoItem> Pending;
            public Task<VideoItem> NextPlaylistVideoAsync(string bvid, CancellationToken token) => Pending?.Task ?? Task.FromResult(Next);
        }
        /* END_METHOD */
        private static int checks;
        private static void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
        public static async Task<string> RunViewingPlaybackTests()
        {
            var page = new MainPage { _video = new VideoDetail { Bvid = "fixture", Parts = new [] { new VideoPart { Cid = 10 }, new VideoPart { Cid = 20 } } }, _currentCid = 10 };
            Check(!await page.AdvanceAfterEndAsync(page._media.Source) && page.selectedCid == 0, "Default stops without replay or navigation");
            page._endMode = VideoEndMode.Loop;
            Check(await page.AdvanceAfterEndAsync(page._media.Source) && page.selectedCid == 10, "Loop restarts exact current part");
            page._endMode = VideoEndMode.NextPart;
            Check(await page.AdvanceAfterEndAsync(page._media.Source) && page.selectedCid == 20, "Next part preserves service order");
            page._currentCid = 20; page.selectedCid = 0;
            Check(!await page.AdvanceAfterEndAsync(page._media.Source) && page.selectedCid == 0, "Final part stops without wrapping");
            var collection = new[] { new CollectionEpisode { Bvid = "before" }, new CollectionEpisode { Bvid = "fixture" },
                new CollectionEpisode { Bvid = "next", Cid = 30 } };
            page._video.CollectionEpisodes = collection;
            Check(await page.AdvanceAfterEndAsync(page._media.Source) && page.openedBvid == "next"
                && page.openedBookmark.Cid == 30 && page.openedBookmark.Playing && page.openedBookmark.Quality == 120
                && ReferenceEquals(page.openedBookmark.CollectionEpisodes, collection),
                "Last part advances to the next collection entry from its start and retains the manual 4K preference");
            page.openedBvid = null;
            page._video.Bvid = "next";
            Check(!await page.AdvanceAfterEndAsync(page._media.Source) && page.openedBvid == null, "Last collection entry stops without wrapping");
            var a = new AccountLibraryItem { SeasonId = 5, EpisodeId = 100 };
            var b = new AccountLibraryItem { SeasonId = 5, EpisodeId = 101 };
            page._playingEpisodeItem = a; page._api.Detail = new AccountSeasonDetail { Episodes = new [] { a,b } };
            Check(await page.AdvanceAfterEndAsync(page._media.Source) && page.selectedEpisode == 101, "Next episode resolves full season before advancing");
            page._playingEpisodeItem = b;
            Check(!await page.AdvanceAfterEndAsync(page._media.Source), "Final episode stops");
            page = new MainPage { _video = new VideoDetail { Bvid = "first" } };
            page.CreatorHub.ContinuousPlayback = true; page.CreatorHub.Next = new VideoItem { Bvid = "second" };
            Check(!await page.AdvanceAfterEndAsync(page._media.Source) && page.openedBvid == null, "Uploader playlist cannot leak into ordinary playback");
            page._videoReturnsToCreator = true;
            Check(await page.AdvanceAfterEndAsync(page._media.Source) && page.openedBvid == "second", "Explicit uploader playlist advances");
            page.openedBvid = null; page.CreatorHub.Pending = new TaskCompletionSource<VideoItem>();
            var pending = page.AdvanceAfterEndAsync(page._media.Source); page._media.Source = new object();
            page.CreatorHub.Pending.SetResult(new VideoItem { Bvid = "stale" }); await pending;
            Check(page.openedBvid == null, "Late playlist response cannot replace new playback");
            page = new MainPage { _endMode = VideoEndMode.NextPart, _playingEpisodeItem = a };
            page._api.Pending = new TaskCompletionSource<AccountSeasonDetail>();
            var seasonPending = page.AdvanceAfterEndAsync(page._media.Source); page._media.Source = new object();
            page._api.Pending.SetResult(new AccountSeasonDetail { Episodes = new [] { a,b } }); await seasonPending;
            Check(page.selectedEpisode == 0, "Late season response cannot switch a new source");
            var chord = new ShoulderChordState();
            Check(chord.Sample(false,false,true) == ShoulderAction.None, "Neutral input");
            Check(chord.Sample(true,false,true) == ShoulderAction.None, "LB is reserved on press");
            Check(chord.Sample(false,false,true) == ShoulderAction.Previous, "Solo LB is delivered on release");
            Check(chord.Sample(false,false,true) == ShoulderAction.None, "Solo LB never repeats on idle");
            chord.Sample(true,false,true);
            Check(chord.Sample(true,true,true) == ShoulderAction.Refresh, "LB plus Y refreshes");
            Check(chord.Sample(true,true,true) == ShoulderAction.None, "Holding shortcut does not refresh repeatedly");
            Check(chord.Sample(false,false,true) == ShoulderAction.None, "Shortcut release does not also seek or switch tabs");
            chord.Sample(true,false,true); chord.Sample(true,false,false);
            Check(chord.Sample(true,true,true) == ShoulderAction.None, "Disabled input requires a neutral barrier");
            chord.Sample(false,false,true); chord.Sample(true,false,true);
            Check(chord.Sample(false,false,true) == ShoulderAction.Previous, "Neutral input re-arms after background/keyboard");
            return "PASS: " + checks + " production end-of-video, source ownership and shoulder chord assertions.";
        }
    }
}
