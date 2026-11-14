using System;
using System.Collections.Generic;
using BiliXbox.Controls;
using BiliXbox.Models;

namespace BiliXbox.HomeFeedCacheTests
{
    public static class Tests
    {
        public static void Run()
        {
            int checks = 0;
            Action<bool, string> check = (value, name) => { ++checks; if (!value) throw new Exception(name); };
            var route = new FeedNavigationState(); var cache = new HomeFeedPageCache();
            check(cache.EnsureAccount(10), "Initial recommendation views receive an account scope.");
            var firstVideos = new List<VideoItem> { new VideoItem { Bvid = "first" }, new VideoItem { Bvid = "second" } };
            cache.Remember(route.Section, route.Page, firstVideos, true, "为你推荐", "notice");
            cache.RememberPosition(route.Section, route.Page, 1, 123.5);
            firstVideos.Clear();
            route.NextPage(); cache.Remember(route.Section, route.Page, new[] { new VideoItem { Bvid = "third" } }, false, "为你推荐", "");
            cache.RememberPosition(route.Section, route.Page, 0, 44);
            route.SwitchSection(HomeSection.Popular); cache.Remember(route.Section, route.Page, new[] { new VideoItem { Bvid = "popular" } }, true, "热门", "");
            route.SwitchSection(HomeSection.Recommendations);
            HomeFeedPageCache.Page saved;
            check(route.Page == 2 && cache.TryGet(route.Section, route.Page, out saved) && saved.Videos[0].Bvid == "third",
                "Returning from Popular restores recommendation page two's original batch.");
            cache.TryGet(route.Section, route.Page, out saved);
            check(saved.HasPosition && saved.FocusIndex == 0 && saved.ScrollOffset == 44 && !saved.HasMore,
                "The restored page retains scroll, selected card and pagination availability together.");
            route.PreviousPage(); cache.TryGet(route.Section, route.Page, out saved);
            check(saved.Videos.Count == 2 && saved.Videos[0].Bvid == "first" && saved.Videos[1].Bvid == "second",
                "Previous page returns identical ordered videos even if the original API list was reused.");
            check(saved.HasPosition && saved.FocusIndex == 1 && saved.ScrollOffset == 123.5 && saved.Title == "为你推荐" && saved.Notice == "notice",
                "Page one keeps its own selection, offset and personalized-feed metadata.");
            check(!cache.EnsureAccount(10) && cache.TryGet(HomeSection.Recommendations, 1, out saved),
                "Rechecking the same account cannot clear previously loaded pages.");
            cache.Invalidate(HomeSection.Recommendations); route.Refresh();
            check(!cache.TryGet(HomeSection.Recommendations, 1, out saved) && !cache.TryGet(HomeSection.Recommendations, 2, out saved),
                "Explicit refresh invalidates every old page of the recommendation batch.");
            check(cache.TryGet(HomeSection.Popular, 1, out saved), "Refreshing Recommendations preserves the independent Popular tab.");
            cache.EnsureAccount(11); route.ResetHomePages(); route.SwitchSection(HomeSection.Popular);
            check(route.Page == 1 && !cache.TryGet(HomeSection.Popular, 1, out saved), "A new account receives neither old user's content nor old user's page positions.");
            cache.EnsureAccount(10);
            check(!cache.TryGet(HomeSection.Recommendations, 1, out saved), "Returning to an earlier account cannot revive invalidated private views.");
            cache.Remember(HomeSection.Recommendations, 1, new[] { new VideoItem() }, false, null, null);
            cache.RememberPosition(HomeSection.Recommendations, 1, 500, double.NaN); cache.TryGet(HomeSection.Recommendations, 1, out saved);
            check(saved.FocusIndex == 0 && saved.ScrollOffset == 0, "Invalid saved geometry cannot select a recycled/out-of-range card.");
            for (int page = 2; page <= 65; ++page) cache.Remember(HomeSection.Recommendations, page, Array.Empty<VideoItem>(), false, "", "");
            check(!cache.TryGet(HomeSection.Recommendations, 1, out saved) && cache.TryGet(HomeSection.Recommendations, 65, out saved),
                "Long sessions bound view-cache memory while retaining newly visited pages.");
            Console.WriteLine("PASS: " + checks + " production home feed page-cache/navigation scenarios (offline).");
        }
    }
}
