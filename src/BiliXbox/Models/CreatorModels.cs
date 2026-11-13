using System;
using System.Collections.Generic;

namespace BiliXbox.Models
{
    public sealed class CreatorProfile
    {
        public long Mid { get; set; }
        public string Name { get; set; } = "";
        public string AvatarUrl { get; set; } = "";
        public string Signature { get; set; } = "";
        public string FollowersText { get; set; } = "";
        public string FollowingText { get; set; } = "";
        public string VideoCountText { get; set; } = "";
    }

    public sealed class CreatorDynamicPage
    {
        public IReadOnlyList<CreatorDynamicItem> Items { get; set; } = Array.Empty<CreatorDynamicItem>();
        public string NextOffset { get; set; } = "";
        public bool HasMore { get; set; }
        public string Notice { get; set; } = "";
    }

    public sealed class CreatorDynamicItem
    {
        public string Id { get; set; } = "";
        public long AuthorMid { get; set; }
        public string Author { get; set; } = "";
        public string AvatarUrl { get; set; } = "";
        public string TimeText { get; set; } = "";
        public string Type { get; set; } = "";
        public string Content { get; set; } = "";
        public IReadOnlyList<CommentContentPart> ContentParts { get; set; } = Array.Empty<CommentContentPart>();
        public IReadOnlyList<string> ImageUrls { get; set; } = Array.Empty<string>();
        public VideoItem Video { get; set; }
        // A repost includes only one original level to bound rendering depth.
        public CreatorDynamicItem Original { get; set; }
        public DynamicCommentTarget CommentTarget { get; set; } = new DynamicCommentTarget();
        public long CommentCount { get; set; }
        public long RepostCount { get; set; }
        public long LikeCount { get; set; }
    }

    public sealed class DynamicCommentTarget
    {
        // These values come from basic.comment_id_str and comment_type. An
        // activity ID is not necessarily the ID of its comment resource.
        public long Oid { get; set; }
        public int Type { get; set; }
        public bool CanPost { get; set; } = true;
        public bool IsAvailable => Oid > 0 && Type > 0;
    }
}
