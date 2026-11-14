using System;

namespace BiliXbox.FeedNavigationTests
{
    public static class Tests
    {
        private static int _checks;

        public static string Run()
        {
            _checks = 0;
            InitialRecommendations();
            SearchWithoutResultsCanReturn();
            RecommendationResetsSearchAndPagination();
            RequestSnapshotsAndVideoReturn();
            NavigationInvalidatesPendingResponses();
            NewerRequestsReplaceOlderRequests();
            PaginationInvalidatesPendingResponses();
            InvalidSearchDoesNotCorruptNavigation();
            SearchDefaultsToRelatedVideos();
            ExplicitUserModeAndPagination();
            SwitchingModesInvalidatesDelayedResults();
            IgnoredModeActionsPreserveCurrentRequest();
            RecommendationsClearUserMode();
            InvalidModesDoNotCorruptNavigation();
            NewQueryCanRetainExplicitUserMode();
            SectionsKeepIndependentRequests();
            TabSwitchesRestorePagesAndInvalidateOldRequests();
            return _checks + " feed navigation checks passed.";
        }

        private static void InitialRecommendations()
        {
            var state = new Controls.FeedNavigationState();
            Check(state.Keyword == "" && state.Page == 1, "Startup must use recommendation page one.");
            Check(state.Mode == Controls.FeedSearchMode.Videos, "Startup must default to related videos rather than user search.");
            Check(!state.IsSearch && !state.CanReturnToRecommendations, "Startup must not claim to be a search route.");
            Check(!state.IsCurrent(null), "A missing response request cannot be current.");
            var request = state.BeginRequest();
            Check(!request.IsSearch && request.Keyword == "" && request.Page == 1, "The initial fetch must load popular videos.");
            Check(state.IsCurrent(request), "The latest initial fetch must be accepted.");
        }

        private static void SearchWithoutResultsCanReturn()
        {
            var state = new Controls.FeedNavigationState();
            state.Search(" TNTxiaoluo ");
            var search = state.BeginRequest();
            Check(state.IsSearch && state.Keyword == "TNTxiaoluo" && state.Page == 1, "Search must trim the query without changing its case.");
            Check(search.IsSearch && search.Keyword == "TNTxiaoluo", "The fetch snapshot must retain the uploader query.");
            Check(state.IsCurrent(search), "Even an empty successful search result belongs to its request.");
            Check(state.CanReturnToRecommendations, "An empty search result must still provide a route back.");
            state.Recommendations();
            Check(!state.IsSearch && state.Keyword == "" && state.Page == 1, "B from an empty search must restore recommendations.");
            Check(!state.IsCurrent(search), "Returning from an empty search must reject its delayed response.");
            Check(!state.CanReturnToRecommendations, "The restored initial recommendation route cannot return to itself.");
        }

        private static void RecommendationResetsSearchAndPagination()
        {
            var state = new Controls.FeedNavigationState();
            state.Search("TNTxiaoluo");
            state.NextPage();
            Check(state.Page == 2 && state.IsSearch, "Search pagination must retain its query.");
            var searchPageTwo = state.BeginRequest();
            state.Recommendations();
            Check(state.Keyword == "" && state.Page == 1 && !state.IsSearch, "The recommendation button must reset search page two completely.");
            Check(!state.IsCurrent(searchPageTwo), "Search page two cannot overwrite recommendations.");
            var popular = state.BeginRequest();
            Check(popular.Keyword == "" && popular.Page == 1 && !popular.IsSearch, "Recommendation fetch must not accidentally reuse a search query.");
            Check(state.IsCurrent(popular), "The recommendation response should be accepted.");

            state.NextPage();
            Check(state.CanReturnToRecommendations && !state.IsSearch, "Popular pagination can return to popular page one.");
            state.Recommendations();
            Check(state.Page == 1 && !state.CanReturnToRecommendations, "Returning from a later popular page must reset the page.");
        }

        private static void RequestSnapshotsAndVideoReturn()
        {
            var state = new Controls.FeedNavigationState();
            state.Search("游戏");
            state.NextPage();
            var browsing = state.BeginRequest();
            // Playback has independent state: opening and closing its overlay must not
            // mutate this retained browsing route or the captured fetch parameters.
            Check(state.Keyword == "游戏" && state.Page == 2, "The browsing route must survive while a video is open.");
            Check(state.IsCurrent(browsing), "Restoring the same feed after playback must not invalidate its request.");
            state.Recommendations();
            Check(!state.IsCurrent(browsing), "The following feed-level B must invalidate the search route.");
            Check(browsing.Keyword == "游戏" && browsing.Page == 2 && browsing.IsSearch, "Navigation must never mutate an already issued request snapshot.");
        }

        private static void NavigationInvalidatesPendingResponses()
        {
            var state = new Controls.FeedNavigationState();
            state.Search("TNTxiaoluo");
            var slowSearch = state.BeginRequest();
            state.Recommendations();
            Check(!state.IsCurrent(slowSearch), "Clicking recommendations must reject a pending search before any new fetch begins.");
            var recommendations = state.BeginRequest();
            Check(state.IsCurrent(recommendations) && !state.IsCurrent(slowSearch), "Late search completion cannot replace the new recommendations response.");
            state.Search("音乐");
            Check(!state.IsCurrent(recommendations), "Changing search must invalidate a recommendation fetch immediately.");
            var music = state.BeginRequest();
            Check(state.IsCurrent(music), "The latest search response must be accepted.");
            state.Search("游戏");
            Check(!state.IsCurrent(music), "Replacing the query must reject the previous query's result.");
            Check(music.Keyword == "音乐", "Replacing the query must preserve the previous request snapshot.");
        }

        private static void NewerRequestsReplaceOlderRequests()
        {
            var state = new Controls.FeedNavigationState();
            var original = state.BeginRequest();
            var refreshed = state.BeginRequest();
            Check(!state.IsCurrent(original) && state.IsCurrent(refreshed), "Refresh must reject an earlier request for identical parameters.");
            state.Recommendations();
            Check(!state.IsCurrent(refreshed), "Explicitly reopening recommendations must invalidate an old recommendation fetch.");
            var latest = state.BeginRequest();
            Check(state.IsCurrent(latest) && !state.IsCurrent(original), "Only the latest request may update feed contents or status.");
        }

        private static void PaginationInvalidatesPendingResponses()
        {
            var state = new Controls.FeedNavigationState();
            state.Search("科学");
            var firstPage = state.BeginRequest();
            state.NextPage();
            Check(!state.IsCurrent(firstPage), "Going to the next page must invalidate page one's request before fetch startup.");
            var secondPage = state.BeginRequest();
            Check(secondPage.Page == 2 && secondPage.Keyword == "科学", "Page two must capture the same query.");
            Check(state.PreviousPage(), "Page two must allow moving to page one.");
            Check(!state.IsCurrent(secondPage) && state.Page == 1, "Moving back a page must reject the later page's response.");
            var backAtFirst = state.BeginRequest();
            Check(!state.PreviousPage(), "The page index must not go below one.");
            Check(state.IsCurrent(backAtFirst), "A refused previous-page action must not invalidate an active request.");
            Check(firstPage.Page == 1 && secondPage.Page == 2, "Pagination must not change issued page snapshots.");
        }

        private static void InvalidSearchDoesNotCorruptNavigation()
        {
            var state = new Controls.FeedNavigationState();
            state.Search("游戏");
            state.NextPage();
            var current = state.BeginRequest();
            foreach (var query in new[] { "", " ", "\t\r\n", null })
            {
                bool rejected = false;
                try { state.Search(query); }
                catch (ArgumentException) { rejected = true; }
                Check(rejected, "Blank input cannot become a search route.");
                Check(state.IsCurrent(current) && state.Keyword == "游戏" && state.Page == 2,
                    "Rejecting blank input must retain the existing query, page and active request.");
            }
        }

        private static void SearchDefaultsToRelatedVideos()
        {
            var state = new Controls.FeedNavigationState();
            foreach (var keyword in new[] { "TNTxiaoluo", "yuanshe", "游戏" })
            {
                state.Search(keyword);
                Check(state.Mode == Controls.FeedSearchMode.Videos, "Every submitted query, including an uploader name, must default to related videos.");
                var videos = state.BeginRequest();
                Check(videos.Mode == Controls.FeedSearchMode.Videos && videos.Keyword == keyword,
                    "A keyword fetch must capture related-video mode without rewriting the query.");
                Check(state.SelectSearchMode(Controls.FeedSearchMode.Users), "User search must require an explicit mode change.");
                state.NextPage();
                var uploads = state.BeginRequest();
                state.Search(keyword);
                Check(state.Mode == Controls.FeedSearchMode.Videos && state.Page == 1,
                    "Submitting even the same query after uploader browsing must restore related-video page one.");
                Check(!state.IsCurrent(uploads), "A pending user result cannot replace a freshly submitted keyword search.");
            }
        }

        private static void ExplicitUserModeAndPagination()
        {
            var state = new Controls.FeedNavigationState();
            state.Search("TNTxiaoluo");
            state.NextPage();
            Check(state.SelectSearchMode(Controls.FeedSearchMode.Users), "The explicit user action must change the mode.");
            Check(state.Keyword == "TNTxiaoluo" && state.Page == 1 && state.Mode == Controls.FeedSearchMode.Users,
                "Switching to user search must retain the exact query and reset pagination.");
            var first = state.BeginRequest();
            Check(first.IsSearch && first.Mode == Controls.FeedSearchMode.Users && first.Page == 1,
                "An user fetch must capture its explicit mode.");
            state.NextPage();
            var next = state.BeginRequest();
            Check(next.Mode == Controls.FeedSearchMode.Users && next.Page == 2 && next.Keyword == first.Keyword,
                "User pagination must retain its mode and query.");
            Check(state.PreviousPage(), "User page two must be able to go back.");
            Check(state.Mode == Controls.FeedSearchMode.Users && state.Page == 1,
                "Previous page must retain explicit user mode.");
            Check(state.SelectSearchMode(Controls.FeedSearchMode.Videos), "Related-video action must leave user mode.");
            Check(state.Mode == Controls.FeedSearchMode.Videos && state.Page == 1 && state.Keyword == "TNTxiaoluo",
                "Returning to related results must keep the query while restarting page one.");
        }

        private static void SwitchingModesInvalidatesDelayedResults()
        {
            var state = new Controls.FeedNavigationState();
            state.Search("TNTxiaoluo");
            var videos = state.BeginRequest();
            state.SelectSearchMode(Controls.FeedSearchMode.Users);
            Check(!state.IsCurrent(videos), "Switching modes must invalidate a pending video response before the next fetch starts.");
            var uploads = state.BeginRequest();
            state.SelectSearchMode(Controls.FeedSearchMode.Videos);
            Check(!state.IsCurrent(uploads), "A late user response cannot overwrite related-video mode.");
            var latestVideos = state.BeginRequest();
            Check(state.IsCurrent(latestVideos) && !state.IsCurrent(videos),
                "Returning to the same query and mode must not revive the original request.");
            Check(videos.Mode == Controls.FeedSearchMode.Videos && uploads.Mode == Controls.FeedSearchMode.Users,
                "Switching modes must not mutate issued request snapshots.");
            state.SelectSearchMode(Controls.FeedSearchMode.Users);
            var latestUploads = state.BeginRequest();
            state.Search("yuanshe");
            Check(!state.IsCurrent(latestUploads) && state.Mode == Controls.FeedSearchMode.Videos,
                "Changing the keyword after an user request must reject that response and restore related videos.");
        }

        private static void IgnoredModeActionsPreserveCurrentRequest()
        {
            var state = new Controls.FeedNavigationState();
            var popular = state.BeginRequest();
            Check(!state.SelectSearchMode(Controls.FeedSearchMode.Users), "An user action without a query must be ignored.");
            Check(state.IsCurrent(popular) && state.Keyword == "" && state.Mode == Controls.FeedSearchMode.Videos,
                "An ignored mode action must leave recommendations and their request intact.");
            state.Search("科学");
            state.NextPage();
            var videos = state.BeginRequest();
            Check(!state.SelectSearchMode(Controls.FeedSearchMode.Videos), "Selecting the already active video mode must be ignored.");
            Check(state.IsCurrent(videos) && state.Page == 2, "Repeated mode selection must retain the active request and page.");
            state.SelectSearchMode(Controls.FeedSearchMode.Users);
            state.NextPage();
            var uploads = state.BeginRequest();
            Check(!state.SelectSearchMode(Controls.FeedSearchMode.Users), "Repeated user mode selection must be ignored.");
            Check(state.IsCurrent(uploads) && state.Page == 2, "Repeated user selection must not invalidate archive pagination.");
        }

        private static void RecommendationsClearUserMode()
        {
            var state = new Controls.FeedNavigationState();
            state.Search("TNTxiaoluo");
            state.SelectSearchMode(Controls.FeedSearchMode.Users);
            state.NextPage();
            var uploads = state.BeginRequest();
            state.Recommendations();
            Check(state.Keyword == "" && state.Page == 1 && state.Mode == Controls.FeedSearchMode.Videos,
                "Recommendation navigation must clear the query, user mode and pagination.");
            Check(!state.IsCurrent(uploads), "An user response cannot overwrite recommendations.");
            var popular = state.BeginRequest();
            Check(!popular.IsSearch && popular.Mode == Controls.FeedSearchMode.Videos,
                "A recommendation request must never retain user mode.");
        }

        private static void InvalidModesDoNotCorruptNavigation()
        {
            var state = new Controls.FeedNavigationState();
            state.Search("TNTxiaoluo");
            state.SelectSearchMode(Controls.FeedSearchMode.Users);
            state.NextPage();
            var current = state.BeginRequest();
            foreach (int invalid in new[] { -1, 2, int.MaxValue })
            {
                bool rejected = false;
                try { state.SelectSearchMode((Controls.FeedSearchMode)invalid); }
                catch (ArgumentOutOfRangeException) { rejected = true; }
                Check(rejected, "An unknown search mode must be rejected.");
                Check(state.IsCurrent(current) && state.Keyword == "TNTxiaoluo" && state.Page == 2
                    && state.Mode == Controls.FeedSearchMode.Users,
                    "Rejecting an invalid mode must preserve the active user route and request.");
            }
        }

        private static void NewQueryCanRetainExplicitUserMode()
        {
            var state = new Controls.FeedNavigationState();
            state.Search("TNTxiaoluo", Controls.FeedSearchMode.Users);
            state.NextPage();
            var oldUsers = state.BeginRequest();
            state.Search("TNT", Controls.FeedSearchMode.Users);
            Check(state.Mode == Controls.FeedSearchMode.Users && state.Page == 1 && state.Keyword == "TNT",
                "Editing a query in the selected user tab must stay in user search and reset pagination.");
            Check(!state.IsCurrent(oldUsers), "A previous keyword's user results cannot overwrite the edited query.");
            var users = state.BeginRequest();
            bool rejected = false;
            try { state.Search("other", (Controls.FeedSearchMode)99); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            Check(rejected && state.IsCurrent(users) && state.Keyword == "TNT",
                "Invalid query mode must leave the active request unchanged.");
            state.Search("music");
            Check(state.Mode == Controls.FeedSearchMode.Videos && state.Page == 1,
                "An ordinary new search defaults to related videos.");
        }

        private static void SectionsKeepIndependentRequests()
        {
            var state = new Controls.FeedNavigationState();
            Check(state.Section == Controls.HomeSection.Recommendations, "Startup selects recommendations, not the first live tab.");
            foreach (Controls.HomeSection section in Enum.GetValues(typeof(Controls.HomeSection)))
            {
                state.Search("query", Controls.FeedSearchMode.Users); state.NextPage();
                var old = state.BeginRequest();
                state.SelectSection(section);
                var request = state.BeginRequest();
                Check(request.Section == section && state.Section == section, "Section request snapshots retain their exact channel.");
                Check(!state.IsSearch && state.Page == 1 && state.Mode == Controls.FeedSearchMode.Videos,
                    "Selecting a section clears previous user search and pagination.");
                Check(!state.IsCurrent(old) && state.IsCurrent(request), "Old section/search cannot overwrite replacement content.");
                state.NextPage(); var second = state.BeginRequest();
                Check(second.Section == section && second.Page == 2, "Paging stays in the selected section.");
                state.Refresh();
                Check(state.Section == section && state.Page == 1 && !state.IsCurrent(second),
                    "Refresh starts a new first page in the current section rather than switching to recommendation.");
                state.Search("new");
                Check(!state.IsCurrent(second), "Global search invalidates the old category page.");
            }
            state.SelectSection(Controls.HomeSection.Live);
            Check(state.CanReturnToRecommendations, "B from the live root can return to recommendations.");
            var live = state.BeginRequest(); bool rejected = false;
            try { state.SelectSection((Controls.HomeSection)99); } catch (ArgumentOutOfRangeException) { rejected = true; }
            Check(rejected && state.IsCurrent(live), "Invalid section cannot corrupt live navigation.");
            state.Recommendations();
            Check(state.Section == Controls.HomeSection.Recommendations && !state.CanReturnToRecommendations,
                "Recommendation button and B restore the default section.");
            state.Search("keyword", Controls.FeedSearchMode.Users); state.NextPage();
            var search = state.BeginRequest(); state.Refresh();
            Check(state.Keyword == "keyword" && state.Page == 2 && state.Mode == Controls.FeedSearchMode.Users && !state.IsCurrent(search),
                "Refreshing user search preserves the selected query, mode and page.");
        }

        private static void TabSwitchesRestorePagesAndInvalidateOldRequests()
        {
            var state = new Controls.FeedNavigationState();
            state.NextPage(); state.NextPage();
            var recommendations = state.BeginRequest();
            state.SwitchSection(Controls.HomeSection.Popular);
            Check(state.Page == 1 && !state.IsCurrent(recommendations), "A first tab visit starts at page one and rejects the outgoing response.");
            state.NextPage(); var popular = state.BeginRequest();
            state.SwitchSection(Controls.HomeSection.Recommendations);
            Check(state.Page == 3 && !state.IsCurrent(popular), "Returning to recommendations preserves its page instead of fetching a new first batch.");
            state.SwitchSection(Controls.HomeSection.Popular);
            Check(state.Page == 2, "Each tab remembers its own independently browsed page.");
            state.Search("science"); state.NextPage();
            state.SwitchSection(Controls.HomeSection.Popular);
            Check(state.Page == 2 && !state.IsSearch, "Search pagination cannot overwrite the underlying section page.");
            state.Refresh(); state.SwitchSection(Controls.HomeSection.Recommendations); state.SwitchSection(Controls.HomeSection.Popular);
            Check(state.Page == 1, "An explicit refresh resets only the refreshed section's page.");
            var pending = state.BeginRequest(); state.ResetHomePages();
            Check(!state.IsCurrent(pending), "Changing account scope invalidates an outgoing response before a new request.");
            state.SwitchSection(Controls.HomeSection.Recommendations);
            Check(state.Page == 1, "A new account cannot inherit the previous account's recommendation cursor page.");
            var current = state.BeginRequest(); bool rejected = false;
            try { state.SwitchSection((Controls.HomeSection)99); } catch (ArgumentOutOfRangeException) { rejected = true; }
            Check(rejected && state.IsCurrent(current), "Invalid tab input preserves the current page and request.");
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            _checks++;
        }
    }
}
