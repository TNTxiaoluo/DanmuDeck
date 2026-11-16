using System;
using System.Collections.Generic;
namespace BiliXbox.Models
{
    public sealed class DynamicAuthorUpdate
    {
        public long Mid { get; set; }
        public bool HasUpdate { get; set; }
    }
    public enum VideoEndMode { Stop, NextPart, Loop }
    public sealed class SubtitleTrack
    {
        public string Language { get; set; } = "";
        public string Label { get; set; } = "";
        public string Url { get; set; } = "";
    }
    public sealed class SubtitleCue
    {
        public double Start { get; set; }
        public double End { get; set; }
        public string Text { get; set; } = "";
    }
    public sealed class FollowingPage
    {
        public IReadOnlyList<CreatorProfile> Users { get; set; } = Array.Empty<CreatorProfile>();
        public bool HasMore { get; set; }
    }
}
