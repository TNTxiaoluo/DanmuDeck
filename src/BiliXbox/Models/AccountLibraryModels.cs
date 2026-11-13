using System;
using System.Collections.Generic;

namespace BiliXbox.Models
{
    public sealed class AccountLibraryItem : VideoItem
    {
        public string Key { get; set; } = "";
        public bool IsSeason { get; set; }
        public long SeasonId { get; set; }
        public long EpisodeId { get; set; }
        public long Cid { get; set; }
        public int Page { get; set; } = 1;
        // -1 is the service's completed marker; it must not become a seek time.
        public long ProgressSeconds { get; set; }
        public string BadgeText { get; set; } = "";
        public bool IsPlayable { get; set; } = true;
        public string UnavailableReason { get; set; } = "";
    }

    public sealed class AccountLibraryPage
    {
        public IReadOnlyList<AccountLibraryItem> Items { get; set; } = Array.Empty<AccountLibraryItem>();
        public bool HasMore { get; set; }
        public string NextCursor { get; set; } = "";
        public int NextPage { get; set; }
        public long Total { get; set; }
        public string Notice { get; set; } = "";
    }

    public sealed class FavoriteFolder
    {
        public long Id { get; set; }
        public string Title { get; set; } = "";
        public long Count { get; set; }
    }

    public sealed class AccountSeasonDetail
    {
        public long SeasonId { get; set; }
        public string Title { get; set; } = "";
        public string CoverUrl { get; set; } = "";
        public IReadOnlyList<AccountLibraryItem> Episodes { get; set; } = Array.Empty<AccountLibraryItem>();
        public string Notice { get; set; } = "";
    }
}
