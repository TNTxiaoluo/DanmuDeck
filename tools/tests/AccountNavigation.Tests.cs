namespace BiliXbox.AccountNavigationTests
{
    using System.Reflection;
    using BiliXbox.Controls;
    using BiliXbox.Models;
    using BiliXbox.Services;
    using Windows.UI.Xaml;
    using Windows.UI.Xaml.Controls;
    public static class Tests
    {
        private static int _checks;
        private static readonly BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
        private static void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); ++_checks; }
        private static object Field(object target, string name)
        { return target.GetType().GetField(name, PrivateInstance | BindingFlags.Public).GetValue(target); }
        private static T Field<T>(object target, string name) => (T)Field(target, name);
        private static void Set(object target, string name, object value)
        { target.GetType().GetField(name, PrivateInstance | BindingFlags.Public).SetValue(target, value); }
        private static object Invoke(object target, string name, params object[] args)
        {
            try { return target.GetType().GetMethod(name, PrivateInstance | BindingFlags.Static).Invoke(target, args); }
            catch (TargetInvocationException error) { throw error.InnerException; }
        }
        private static Task Call(MainPage page, string name, params object[] args) => (Task)Invoke(page, name, args);
        private static AccountLibraryView View(MainPage page) => Field<AccountLibraryView>(page, "AccountLibrary");
        private static BilibiliClient Api(MainPage page) => Field<BilibiliClient>(page, "_api");
        private static FakeMediaPlayer Media(MainPage page) => Field<FakeMediaPlayer>(page, "_media");
        private static VideoDetail Video(string bvid = "BV1xx411c7mD", long cid = 100) => new VideoDetail { Bvid = bvid, Aid = 10, Cid = cid, Title = "原视频" };
        private static AccountLibraryItem Item(long episode = 0) => new AccountLibraryItem
        {
            Bvid = episode > 0 ? "" : "BV1spz8B4EXv", Aid = 20, Cid = 200, Title = "片库 🎮", EpisodeId = episode,
            SeasonId = episode > 0 ? 11 : 0, ProgressSeconds = 97, IsPlayable = true
        };
        private static void SeedPlayback(MainPage page, VideoDetail video, bool playing, double position, AccountLibraryItem episode = null)
        {
            Set(page, "_watching", true); Set(page, "_video", video); Set(page, "_currentCid", video.Cid);
            Set(page, "_wantsPlayback", playing); Set(page, "_resumeAfterOpen", playing); Set(page, "_playingEpisodeItem", episode);
            Set(page, "_requestedBvid", video.Bvid); Set(page, "_requestedTitle", video.Title);
            Set(page, "_playback", new PlaybackInfo { QualityId = 120 });
            Set(page, "_restorePosition", null); Media(page).PlaybackSession.Position = TimeSpan.FromSeconds(position); Media(page).Source = new object();
        }
        private static object OriginBookmark(MainPage page) => Field(Field(page, "_libraryReturn"), "Playback");
        private static void CheckBookmark(object bookmark, string bvid, long cid, bool playing, double position, AccountLibraryItem episode, string context)
        {
            Check(bookmark != null, context + " captures a bookmark");
            Check(Field<string>(bookmark, "Bvid") == bvid && Field<long>(bookmark, "Cid") == cid, context + " preserves BV and selected part");
            Check(Field<bool>(bookmark, "Playing") == playing && Field<TimeSpan>(bookmark, "Position") == TimeSpan.FromSeconds(position), context + " preserves pause/play and position");
            Check(ReferenceEquals(Field(bookmark, "Episode"), episode), context + " preserves exact PGC identity");
        }
        public static async Task<string> Run()
        {
            ContentDialog.Reset();
            var page = new MainPage();
            var cache = new object(); View(page).CachedView = cache;
            Check(View(page).InitializeCount == 1, "production initialization wires the existing account control once");
            await Call(page, "ShowAccountLibraryAsync");
            Check(Field<bool>(page, "_libraryVisible") && View(page).ShowCount == 1 && View(page).LastUser.Mid == 42, "home opens authenticated library");
            Check(OriginBookmark(page) == null, "home origin has no invented playback bookmark");
            Check(Field<FrameworkElement>(page, "HomeView").Visibility == Visibility.Collapsed && View(page).Visibility == Visibility.Visible, "library owns page surface");
            var history = Item();
            await View(page).RaiseOpen(history);
            Check(page.Navigations.Count == 1 && Field<bool>(page, "_videoReturnsToLibrary") && !Field<bool>(page, "_libraryVisible"), "real event handler opens selected history video with library return path");
            CheckBookmark(page.Navigations[0].Bookmark, history.Bvid, history.Cid, true, 97, null, "history opening");
            SeedPlayback(page, Video(history.Bvid, history.Cid), true, 101);
            Invoke(page, "ReturnToAccountLibraryFromVideo");
            Check(Field<bool>(page, "_libraryVisible") && !Field<bool>(page, "_videoReturnsToLibrary"), "production return target used by B restores library");
            Check(View(page).ShowCount == 1 && ReferenceEquals(View(page).CachedView, cache), "return does not reload library or discard its cached view");
            await Call(page, "CloseAccountLibraryAsync");
            Check(!Field<bool>(page, "_libraryVisible") && page.GoHomeCount == 2 && page.Navigations.Count == 1, "closing home-origin library restores home instead of replaying a video");
            Check(View(page).ClearCount == 0 && Field(page, "_libraryReturn") == null, "ordinary close removes navigation bookmark but retains same-account cache");

            foreach (bool playing in new[] { false, true })
            {
                page = new MainPage();
                var origin = Video(); SeedPlayback(page, origin, playing, 100);
                int beforePause = Media(page).PauseCount;
                await Call(page, "ShowAccountLibraryAsync");
                CheckBookmark(OriginBookmark(page), origin.Bvid, origin.Cid, playing, 100, null, playing ? "playing origin" : "paused origin");
                Check(Media(page).PauseCount == beforePause + 1 && !Field<bool>(page, "_wantsPlayback"), "opening library pauses source after capturing its prior intent");
                await Call(page, "CloseAccountLibraryAsync");
                Check(page.Navigations.Count == 0 && Field<FrameworkElement>(page, "WatchView").Visibility == Visibility.Visible, "unchanged live origin reuses its original media source");
                Check(Field<bool>(page, "_wantsPlayback") == playing && Field<bool>(page, "_resumeAfterOpen") == playing, "unchanged origin restores exact playing intent");
                Check(Media(page).PlayCount == (playing ? 1 : 0) && Media(page).PauseCount == beforePause + (playing ? 1 : 2), "unchanged origin resumes only when originally playing");
            }
            foreach (bool playing in new[] { false, true })
            {
                page = new MainPage();
                var originalEpisode = Item(501); originalEpisode.Cid = 100;
                var origin = Video("", 100); SeedPlayback(page, origin, playing, 100, originalEpisode);
                await Call(page, "ShowAccountLibraryAsync");
                CheckBookmark(OriginBookmark(page), "", 100, playing, 100, originalEpisode, "PGC origin");
                await Call(page, "OpenLibraryItemAsync", Item());
                SeedPlayback(page, Video("BV1spz8B4EXv", 200), true, 3);
                Invoke(page, "ReturnToAccountLibraryFromVideo");
                await Call(page, "CloseAccountLibraryAsync");
                Check(page.Navigations.Count == 2, "different library video requires reopening original PGC source");
                CheckBookmark(page.Navigations[1].Bookmark, "", 100, playing, 100, originalEpisode, "PGC restore after other video");
                Check(Field<int>(page.Navigations[1].Bookmark, "Quality") == 120, "PGC restore preserves selected quality");
            }

            page = new MainPage();
            Set(page, "_watching", true); Set(page, "_video", null); Set(page, "_requestedBvid", "BV1xx411c7mD");
            Set(page, "_requestedTitle", "待加载标题"); Set(page, "_currentCid", 0L); Set(page, "_preferredQuality", 112);
            Set(page, "_restorePosition", TimeSpan.FromSeconds(123)); Set(page, "_wantsPlayback", false);
            Media(page).PlaybackSession.Position = TimeSpan.FromSeconds(2);
            await Call(page, "ShowAccountLibraryAsync");
            var pending = OriginBookmark(page);
            CheckBookmark(pending, "BV1xx411c7mD", 0, false, 123, null, "pending metadata origin");
            Check(Field<string>(pending, "Title") == "待加载标题" && Field<int>(pending, "Quality") == 112, "pending capture keeps requested title and preferred quality");
            await Call(page, "CloseAccountLibraryAsync");
            Check(page.Navigations.Count == 1 && page.Navigations[0].Bvid == "BV1xx411c7mD", "pending metadata origin can be reopened without a VideoDetail");
            CheckBookmark(page.Navigations[0].Bookmark, "BV1xx411c7mD", 0, false, 123, null, "pending origin restored");

            page = new MainPage();
            await Call(page, "ShowAccountLibraryAsync");
            var completed = Item(); completed.ProgressSeconds = -1;
            await Call(page, "OpenLibraryItemAsync", completed);
            Check(Field<TimeSpan>(page.Navigations[0].Bookmark, "Position") == TimeSpan.Zero, "completed history marker never becomes a negative seek");
            page = new MainPage(); await Call(page, "ShowAccountLibraryAsync");
            var hugeProgress = Item(); hugeProgress.ProgressSeconds = long.MaxValue;
            await Call(page, "OpenLibraryItemAsync", hugeProgress);
            Check(Field<TimeSpan>(page.Navigations[0].Bookmark, "Position") == TimeSpan.FromDays(7), "oversized history progress is bounded before TimeSpan conversion");
            page = new MainPage(); await Call(page, "ShowAccountLibraryAsync");
            var selectedEpisode = Item(999); await Call(page, "OpenLibraryItemAsync", selectedEpisode);
            Check(ReferenceEquals(Field(page.Navigations[0].Bookmark, "Episode"), selectedEpisode), "selected episode opens with PGC bookmark identity");

            page = new MainPage(); var token = new CancellationTokenSource().Token; var ugc = Video();
            Set(page, "_playingEpisodeItem", Item(777));
            await (Task<PlaybackInfo>)Invoke(page, "GetCurrentPlaybackAsync", ugc, 120, token);
            Check(Api(page).PgcCount == 1 && Api(page).UgcCount == 0 && Api(page).LastEpisodeId == 777, "positive episode exclusively dispatches PGC playback endpoint");
            Check(Api(page).LastQuality == 120 && Api(page).LastToken == token, "PGC dispatch preserves requested quality and cancellation token");
            Set(page, "_playingEpisodeItem", null);
            await (Task<PlaybackInfo>)Invoke(page, "GetCurrentPlaybackAsync", ugc, 80, token);
            Check(Api(page).UgcCount == 1 && Api(page).PgcCount == 1 && ReferenceEquals(Api(page).LastVideo, ugc), "ordinary video exclusively dispatches UGC endpoint");
            Set(page, "_playingEpisodeItem", Item(0));
            await (Task<PlaybackInfo>)Invoke(page, "GetCurrentPlaybackAsync", ugc, 64, token);
            Check(Api(page).UgcCount == 2 && Api(page).PgcCount == 1 && Api(page).LastQuality == 64, "non-episode row cannot accidentally route to PGC");

            page = new MainPage(); await Call(page, "ShowAccountLibraryAsync"); cache = new object(); View(page).CachedView = cache;
            var confirmation = new TaskCompletionSource<ContentDialogResult>(); ContentDialog.ShowHandler = dialog => confirmation.Task;
            var signOut = Call(page, "SignOutAccountAsync");
            Check(Field<bool>(page, "_dialogOpen") && Field(page, "_activeGeneralDialog") != null && Api(page).LogoutCount == 0, "logout waits for explicit dialog confirmation before account mutation");
            confirmation.SetResult(ContentDialogResult.None); await signOut;
            Check(Api(page).IsLoggedIn && Api(page).LogoutCount == 0 && View(page).ClearCount == 0 && ReferenceEquals(View(page).CachedView, cache), "cancel logout preserves account and cached library without writes");
            Check(Field<bool>(page, "_libraryVisible") && !Field<bool>(page, "_dialogOpen") && Field(page, "_activeGeneralDialog") == null, "cancel logout restores library navigation ownership");
            Check(page.GoHomeCount == 0 && page.HomeRefreshCount == 0, "cancel logout does not navigate away or refresh account feed");
            ContentDialog.ShowHandler = dialog => Task.FromResult(ContentDialogResult.Primary);
            await Call(page, "SignOutAccountAsync");
            Check(Api(page).LogoutCount == 1 && !Api(page).IsLoggedIn, "confirmed logout calls account removal exactly once");
            Check(View(page).ClearCount == 1 && View(page).CachedView == null && Field<long>(page, "_libraryAccountMid") == 0, "confirmed logout clears former account library and identity");
            Check(!Field<bool>(page, "_libraryVisible") && !Field<bool>(page, "_videoReturnsToLibrary") && Field(page, "_libraryReturn") == null, "confirmed logout clears all library navigation targets");
            Check(page.GoHomeCount == 1 && page.CreatorClearCount == 1 && page.AccountUpdateCount == 1 && page.HomeRefreshCount == 1, "confirmed logout returns home and refreshes account-dependent surfaces");
            Check(Field(page, "_activeGeneralDialog") == null && !Field<bool>(page, "_dialogOpen"), "confirmed logout always releases dialog input owner");
            ContentDialog.Reset();

            page = new MainPage(); await Call(page, "ShowAccountLibraryAsync");
            Api(page).CurrentUser = new UserProfile { Mid = 99, Name = "different fixture account" };
            await Call(page, "OpenLibraryItemAsync", Item());
            Check(page.Navigations.Count == 0 && !Field<bool>(page, "_videoReturnsToLibrary"), "account swap blocks stale library item before navigation");
            Set(page, "_videoReturnsToLibrary", true); Invoke(page, "ReturnToAccountLibraryFromVideo");
            Check(!Field<bool>(page, "_libraryVisible") && View(page).ClearCount == 1 && Field<long>(page, "_libraryAccountMid") == 0, "return after account swap clears former account cache instead of exposing it");
            Check(View(page).ShowCount == 1, "account swap never reloads old account via return path");

            page = new MainPage(); await Call(page, "ShowAccountLibraryAsync");
            int showBefore = View(page).ShowCount, focusBefore = View(page).FocusCount;
            await Call(page, "ShowAccountLibraryAsync");
            Check(View(page).ShowCount == showBefore && View(page).FocusCount == focusBefore + 1, "reenter existing library only restores focus and preserves origin state");
            var unavailable = Item(); unavailable.IsPlayable = false; unavailable.UnavailableReason = "fixture unavailable";
            int stopBefore = View(page).StopCount; await Call(page, "OpenLibraryItemAsync", unavailable);
            Check(page.Navigations.Count == 0 && View(page).StopCount == stopBefore && page.Status == "fixture unavailable", "unavailable item keeps library visible and explains failure without navigation");
            page = new MainPage(); Api(page).CurrentUser = null; await Call(page, "ShowAccountLibraryAsync");
            Check(!Field<bool>(page, "_libraryVisible") && View(page).ShowCount == 0, "anonymous library open is ignored without fake content");
            page = new MainPage(); Set(page, "_dialogOpen", true); await Call(page, "ShowAccountLibraryAsync");
            Check(View(page).ShowCount == 0, "active dialog owns input and blocks library opening");
            page = new MainPage(); Set(page, "_disposed", true); await Call(page, "ShowAccountLibraryAsync");
            Check(View(page).ShowCount == 0, "disposed page cannot open library");

            var detailItem = Item(501); detailItem.CoverUrl = "https://i0.hdslb.com/cover.jpg"; detailItem.Owner = "片名";
            detailItem.OwnerAvatarUrl = "https://i0.hdslb.com/avatar.jpg"; detailItem.OwnerMid = 84;
            var detail = (VideoDetail)Invoke(new MainPage(), "EpisodeVideoDetail", detailItem);
            Check(detail.Aid == detailItem.Aid && detail.Cid == detailItem.Cid && detail.Bvid == "" && detail.Parts.Count == 1 && detail.Parts[0].Cid == detailItem.Cid, "PGC episode detail preserves true AV/CID without requiring a fabricated BV");
            Check(detail.Owner == detailItem.Owner && detail.CoverUrl == detailItem.CoverUrl && detail.OwnerAvatarUrl == detailItem.OwnerAvatarUrl && detail.OwnerMid == 84, "PGC detail preserves shared presentation metadata");
            detailItem.Aid = 0;
            bool rejected = false;
            try { Invoke(new MainPage(), "EpisodeVideoDetail", detailItem); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "PGC detail rejects incomplete episode metadata instead of using UGC lookup");
            return "PASS: " + _checks + " unchanged MainPage account-library navigation/bookmark/endpoint/logout assertions (external lifecycle substitutes; no native XAML, raw B input or real account writes).";
        }
    }
}
