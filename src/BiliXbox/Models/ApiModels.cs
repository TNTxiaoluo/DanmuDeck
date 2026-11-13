using System;
using System.Collections.Generic;

namespace BiliXbox.Models
{
    public class VideoItem
    {
        public string Bvid { get; set; } = "";
        public long Aid { get; set; }
        public string Title { get; set; } = "";
        public string CoverUrl { get; set; } = "";
        public string Owner { get; set; } = "";
        public long OwnerMid { get; set; }
        public string OwnerAvatarUrl { get; set; } = "";
        public string PlayText { get; set; } = "";
        public string DurationText { get; set; } = "";
    }

    public sealed class SearchPage
    {
        public IReadOnlyList<VideoItem> Videos { get; set; } = Array.Empty<VideoItem>();
        public string MatchedCreatorName { get; set; } = "";
        public bool HasMore { get; set; }
        public long Total { get; set; }
        // A partial uploader preview must not look like their complete archive.
        public string Notice { get; set; } = "";
    }

    public sealed class VideoDetail : VideoItem
    {
        public long Cid { get; set; }
        public long? ReplyCount { get; set; }
        public string Description { get; set; } = "";
        public IReadOnlyList<VideoPart> Parts { get; set; } = Array.Empty<VideoPart>();
    }

    public sealed class VideoPart
    {
        public long Cid { get; set; }
        public string Title { get; set; } = "";
        public int Page { get; set; }
    }

    public sealed class PlaybackInfo
    {
        public string Url { get; set; } = "";
        // DASH contains synchronized, separately downloaded video and audio.
        // Url alone is the video track and must never be played as a muxed MP4.
        public string DashManifest { get; set; } = "";
        // Each list belongs to exactly one representation, with identical bytes
        // and byte ranges on all URLs. Never substitute an audio URL for video.
        public IReadOnlyList<string> DashVideoUrls { get; set; } = Array.Empty<string>();
        public IReadOnlyList<string> DashAudioUrls { get; set; } = Array.Empty<string>();
        public string ContentType { get; set; } = "video/mp4";
        public IReadOnlyList<string> BackupUrls { get; set; } = Array.Empty<string>();
        public string Quality { get; set; } = "";
        public int QualityId { get; set; }
        public int RequestedQualityId { get; set; }
        // Response-level quality is advisory; QualityId comes from the chosen track.
        public int ServerQualityId { get; set; }
        public string VideoCodec { get; set; } = "";
        public int VideoWidth { get; set; }
        public int VideoHeight { get; set; }
        public long VideoBitrate { get; set; }
        public long AudioBitrate { get; set; }
        public double VideoFrameRate { get; set; }
        public bool HardwareHevcVerified { get; set; }
        public bool FourKDisplayVerified { get; set; }
        public string CapabilityDiagnostic { get; set; } = "";
        public bool Hdr10DisplayVerified { get; set; }
        public bool IsHdr10 { get; set; }
        public int VideoBitsPerChannel { get; set; }
        // One compatible source from this same authorized response. Never points
        // back to the primary source, so playback failure cannot retry forever.
        public PlaybackInfo CodecFallback { get; set; }
        public string QualityNotice { get; set; } = "";
        public IReadOnlyList<PlaybackQuality> AvailableQualities { get; set; } = Array.Empty<PlaybackQuality>();
        public bool RequiresHttpHeaders { get; set; } = true;
    }

    public sealed class PlaybackQuality
    {
        public int Id { get; set; }
        public string Label { get; set; } = "";
        public bool RequiresLogin { get; set; }
        public bool RequiresPremium { get; set; }
        public bool IsAvailable { get; set; } = true;
        // Capability-filtered options remain in the model for diagnostics, but
        // the UI must omit them from the public quality list.
        public bool IsVisible { get; set; } = true;
        public bool IsCapabilityPending { get; set; }
        // An advertised tier can be requested even when this response did not
        // include its track. IsAvailable describes the action, not a permission grant.
        public bool HasPlaybackSource { get; set; }
        public bool IsProbe { get; set; }
        public string Reason { get; set; } = "";
    }

    public sealed class CommentPage
    {
        public IReadOnlyList<CommentItem> Items { get; set; } = Array.Empty<CommentItem>();
        public bool HasMore { get; set; }
        public long Total { get; set; }
        // Opaque application state retaining the server's offset or explicitly
        // returned numeric next cursor; never incremented from a local page count.
        public string NextCursor { get; set; } = "";
        public bool TotalKnown { get; set; }
        public bool IsClosed { get; set; }
        public bool IsPartial { get; set; }
        public string Notice { get; set; } = "";
    }

    public sealed class CommentItem
    {
        public long Id { get; set; }
        // A reaction belongs to the exact comment subject, including dynamics.
        public long Oid { get; set; }
        public int Type { get; set; }
        public long RootId { get; set; }
        public long AuthorMid { get; set; }
        public string Author { get; set; } = "";
        public string AvatarUrl { get; set; } = "";
        public int? Level { get; set; }
        public bool IsUploader { get; set; }
        public bool IsPinned { get; set; }
        public string IpLocation { get; set; } = "";
        public string FanNumber { get; set; } = "";
        public string FanBadgeImageUrl { get; set; } = "";
        public string Content { get; set; } = "";
        public string TimeText { get; set; } = "";
        public long Likes { get; set; }
        public CommentVote Vote { get; set; }
        public bool VoteKnown { get; set; }
        public long VoteAccountMid { get; set; }
        public long ReplyCount { get; set; }
        public IReadOnlyList<CommentItem> Replies { get; set; } = Array.Empty<CommentItem>();
        public IReadOnlyList<CommentContentPart> ContentParts { get; set; } = Array.Empty<CommentContentPart>();
        public IReadOnlyList<CommentPicture> Pictures { get; set; } = Array.Empty<CommentPicture>();
    }

    public sealed class CommentContentPart
    {
        public string Text { get; set; } = "";
        public string ImageUrl { get; set; } = "";
        // Keep the original token so a missing emoji image never erases content.
        public string AltText { get; set; } = "";
        // Paid/collection emotes can be larger than an ordinary inline face.
        // Preserve source dimensions for aspect ratio; zero means unspecified.
        public double Width { get; set; }
        public double Height { get; set; }
        public double Scale { get; set; } = 1;
        public bool IsImage => !string.IsNullOrEmpty(ImageUrl);
    }

    public sealed class CommentPicture
    {
        // Full original image, never a thumbnail rewrite or a guessed CDN URL.
        public string ImageUrl { get; set; } = "";
        public int Width { get; set; }
        public int Height { get; set; }
    }

    public enum CommentVote { None = 0, Like = 1, Hate = 2 }

    public sealed class CommentVoteResult
    {
        public long Oid { get; set; }
        public int Type { get; set; }
        public long CommentId { get; set; }
        public long AccountMid { get; set; }
        public CommentVote Vote { get; set; }
        // Bilibili publishes a like count, but not a public dislike count.
        public long Likes { get; set; }
    }

    public sealed class CommentReplyPage
    {
        public IReadOnlyList<CommentItem> Items { get; set; } = Array.Empty<CommentItem>();
        public int Page { get; set; }
        public long Total { get; set; }
        public bool TotalKnown { get; set; }
        public bool HasMore { get; set; }
        public bool IsPartial { get; set; }
        public string Notice { get; set; } = "";
    }

    public sealed class DanmakuItem
    {
        public double Time { get; set; }
        public string SenderHash { get; set; } = "";
        public string Text { get; set; } = "";
        public int Color { get; set; } = 16777215;
        public int Mode { get; set; } = 1;
    }

    public sealed class QrLoginInfo
    {
        public string Url { get; set; } = "";
        public string Key { get; set; } = "";
    }

    public enum QrLoginStatus
    {
        WaitingForScan,
        WaitingForConfirmation,
        Expired,
        Success
    }

    public sealed class UserProfile
    {
        public long Mid { get; set; }
        public string Name { get; set; } = "";
        public string AvatarUrl { get; set; } = "";
        public bool IsPremium { get; set; }
    }
}
