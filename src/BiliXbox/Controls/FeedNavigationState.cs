using System;
using System.Collections.Generic;

namespace BiliXbox.Controls
{
    internal enum FeedSearchMode { Videos, Users }
    internal enum HomeSection { Live, Recommendations, Popular, Animation, Film }

    internal sealed class FeedNavigationState
    {
        public string Keyword { get; private set; } = "";
        public int Page { get; private set; } = 1;
        public FeedSearchMode Mode { get; private set; } = FeedSearchMode.Videos;
        public HomeSection Section { get; private set; } = HomeSection.Recommendations;
        public bool IsSearch => Keyword.Length > 0;
        public bool CanReturnToRecommendations => IsSearch || Page > 1 || Section != HomeSection.Recommendations;
        private long _revision;
        private readonly Dictionary<HomeSection, int> _sectionPages = new Dictionary<HomeSection, int>();

        public void Search(string keyword, FeedSearchMode mode = FeedSearchMode.Videos)
        {
            if (string.IsNullOrWhiteSpace(keyword)) throw new ArgumentException("搜索词不能为空。", nameof(keyword));
            ValidateMode(mode);
            RememberHomePage();
            Keyword = keyword.Trim(); Page = 1; Mode = mode; ++_revision;
        }
        public bool SelectSearchMode(FeedSearchMode mode)
        {
            ValidateMode(mode);
            if (!IsSearch || Mode == mode) return false;
            Mode = mode; Page = 1; ++_revision;
            return true;
        }
        public void Recommendations() => SelectSection(HomeSection.Recommendations);
        public void Refresh()
        {
            if (IsSearch) ++_revision;
            else SelectSection(Section);
        }
        public void SelectSection(HomeSection section)
        {
            if (!Enum.IsDefined(typeof(HomeSection), section)) throw new ArgumentOutOfRangeException(nameof(section));
            RememberHomePage();
            Section = section; Keyword = ""; Page = 1; Mode = FeedSearchMode.Videos; ++_revision;
            _sectionPages[section] = 1;
        }
        public void SwitchSection(HomeSection section)
        {
            if (!Enum.IsDefined(typeof(HomeSection), section)) throw new ArgumentOutOfRangeException(nameof(section));
            RememberHomePage();
            int page;
            Section = section; Keyword = ""; Mode = FeedSearchMode.Videos;
            Page = _sectionPages.TryGetValue(section, out page) ? page : 1;
            ++_revision;
        }
        public void ResetHomePages()
        {
            _sectionPages.Clear();
            if (!IsSearch) Page = 1;
            ++_revision;
        }
        private void RememberHomePage() { if (!IsSearch) _sectionPages[Section] = Page; }
        public void NextPage() { if (Page < int.MaxValue) { ++Page; ++_revision; } }
        public bool PreviousPage() { if (Page <= 1) return false; --Page; ++_revision; return true; }
        public FeedRequest BeginRequest() => new FeedRequest(Keyword, Page, Mode, ++_revision, Section);
        public bool IsCurrent(FeedRequest request) => request != null && request.Revision == _revision;
        private static void ValidateMode(FeedSearchMode mode)
        { if (mode != FeedSearchMode.Videos && mode != FeedSearchMode.Users) throw new ArgumentOutOfRangeException(nameof(mode)); }
    }

    internal sealed class FeedRequest
    {
        public string Keyword { get; }
        public int Page { get; }
        public FeedSearchMode Mode { get; }
        public HomeSection Section { get; }
        internal long Revision { get; }
        public bool IsSearch => Keyword.Length > 0;
        internal FeedRequest(string keyword, int page, FeedSearchMode mode, long revision, HomeSection section = HomeSection.Recommendations)
        { Keyword = keyword; Page = page; Mode = mode; Revision = revision; Section = section; }
    }
}
