using System;
using System.Collections.Generic;
using System.Globalization;

namespace BiliXbox.Models
{
    public sealed class LiveRoom
    {
        public long Id { get; set; }
        public long OwnerMid { get; set; }
        public string Title { get; set; } = "";
        public string OwnerName { get; set; } = "";
        public string AvatarUrl { get; set; } = "";
        public string CoverUrl { get; set; } = "";
        public string Area { get; set; } = "";
        public long AreaId { get; set; }
        public long ParentAreaId { get; set; }
        public long Online { get; set; }
        public bool IsLive { get; set; }
        public string OnlineText => Online >= 10000 ? (Online / 10000.0).ToString("0.#", CultureInfo.InvariantCulture) + "万热度" : Online.ToString(CultureInfo.InvariantCulture) + "热度";
    }

    public sealed class LiveRoomPage
    {
        public IReadOnlyList<LiveRoom> Items { get; set; } = Array.Empty<LiveRoom>();
        public int Page { get; set; }
        public bool HasMore { get; set; }
    }

    public sealed class LiveQuality
    {
        public int Id { get; set; }
        public string Label { get; set; } = "";
    }

    public sealed class LivePlayback
    {
        public long RoomId { get; set; }
        public string Url { get; set; } = "";
        public IReadOnlyList<string> BackupUrls { get; set; } = Array.Empty<string>();
        public int QualityId { get; set; }
        public string QualityLabel { get; set; } = "";
        public string Codec { get; set; } = "";
        public string Format { get; set; } = "";
        public IReadOnlyList<LiveQuality> Qualities { get; set; } = Array.Empty<LiveQuality>();
    }

    public sealed class LiveDanmakuHost
    {
        public string Host { get; set; } = "";
        public int WssPort { get; set; } = 443;
    }

    public sealed class LiveDanmakuConnection
    {
        public long RoomId { get; set; }
        public long UserId { get; set; }
        public string Token { get; set; } = "";
        public string Buvid { get; set; } = "";
        public IReadOnlyList<LiveDanmakuHost> Hosts { get; set; } = Array.Empty<LiveDanmakuHost>();
    }

    public enum LiveChatMessageKind { Danmaku, Gift, SuperChat, Live, Preparing, Popularity }

    public sealed class LiveChatMessage : EventArgs
    {
        public LiveChatMessageKind Kind { get; set; }
        public string UserName { get; set; } = "";
        public string Text { get; set; } = "";
        public long UserId { get; set; }
        public uint Color { get; set; } = 0xFFFFFF;
        public string GiftName { get; set; } = "";
        public long GiftCount { get; set; }
        public long Popularity { get; set; }
        public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    }

    public sealed class LiveGift
    {
        public long Id { get; set; }
        public long BagId { get; set; }
        public string Name { get; set; } = "";
        public string ImageUrl { get; set; } = "";
        public long Quantity { get; set; }
        public int MaxCount { get; set; } = 999;
        // Exact server units: 100 gold units are one battery. Never invent prices.
        public long UnitPrice { get; set; }
        public string Currency { get; set; } = "";
        public DateTimeOffset? ExpireAt { get; set; }
        public bool IsBag => BagId > 0;
        public string PriceText => IsBag ? "背包 · 剩余 " + Quantity.ToString(CultureInfo.InvariantCulture)
            : Currency == "gold" ? (UnitPrice / 100m).ToString("0.##", CultureInfo.InvariantCulture) + " 电池"
            : UnitPrice.ToString(CultureInfo.InvariantCulture) + " 银瓜子";
    }

    public sealed class LiveGiftCatalog
    {
        public long RoomId { get; set; }
        public long AccountMid { get; set; }
        public IReadOnlyList<LiveGift> Items { get; set; } = Array.Empty<LiveGift>();
    }

    public sealed class LiveGiftQuote
    {
        public long RoomId { get; internal set; }
        public long OwnerMid { get; internal set; }
        public long AccountMid { get; internal set; }
        public long GiftId { get; internal set; }
        public long BagId { get; internal set; }
        public string GiftName { get; internal set; }
        public int Count { get; internal set; }
        public long UnitPrice { get; internal set; }
        public string Currency { get; internal set; }
        public long TotalPrice => checked(UnitPrice * Count);
        public string CostText => BagId > 0 ? "消耗 " + Count.ToString(CultureInfo.InvariantCulture) + " 个背包礼物"
            : Currency == "gold" ? (TotalPrice / 100m).ToString("0.##", CultureInfo.InvariantCulture) + " 电池"
            : TotalPrice.ToString(CultureInfo.InvariantCulture) + " 银瓜子";
        internal string Session;
        internal string Csrf;
        internal DateTimeOffset CreatedAt;
        internal bool Consumed;
    }
}
