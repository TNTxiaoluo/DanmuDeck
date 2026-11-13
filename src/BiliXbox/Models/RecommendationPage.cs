using System;
using System.Collections.Generic;

namespace BiliXbox.Models
{
    public sealed class RecommendationPage
    {
        public IReadOnlyList<VideoItem> Videos { get; set; } = Array.Empty<VideoItem>();
        public bool HasMore { get; set; }
        public bool IsPersonalized { get; set; }
        public bool IsPopularFallback { get; set; }
        public string Notice { get; set; } = "";
    }
}
