using System;

namespace BiliXbox.Models
{
    public sealed class VideoInteractionState
    {
        public long Aid { get; set; }
        public long AccountMid { get; set; }
        public bool IsLiked { get; set; }
        public int Coins { get; set; }
        public bool IsFavorited { get; set; }
    }

    public sealed class VideoFavoriteFolder
    {
        public long Id { get; set; }
        public string Title { get; set; } = "";
        public long Count { get; set; }
        public bool IsSelected { get; set; }
        public bool IsDefault { get; set; }
    }

    // A code-0 triple response can still report partial results. These fields
    // describe the service's acknowledgement, not an optimistic local update.
    public sealed class VideoTripleResult
    {
        public bool Like { get; set; }
        public bool Coin { get; set; }
        public bool Favorite { get; set; }
        public int Coins { get; set; }
        public bool IsComplete => Like && Coin && Favorite;
    }
}
