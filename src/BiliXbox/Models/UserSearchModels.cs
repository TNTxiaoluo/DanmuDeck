using System;
using System.Collections.Generic;

namespace BiliXbox.Models
{
    public sealed class UserSearchItem
    {
        public long Mid { get; set; }
        public string Name { get; set; } = "";
        public string AvatarUrl { get; set; } = "";
        public string Signature { get; set; } = "";
        public string FollowersText { get; set; } = "";
        public string VideoCountText { get; set; } = "";
        // Virtualized item peers use the data item's display string, including
        // before its visual container is available to assistive technology.
        public override string ToString() => Name;
    }

    public sealed class UserSearchPage
    {
        public IReadOnlyList<UserSearchItem> Users { get; set; } = Array.Empty<UserSearchItem>();
        public bool HasMore { get; set; }
        public long Total { get; set; }
        public string Notice { get; set; } = "";
    }
}
