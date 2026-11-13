using System;
using System.Globalization;
using BiliXbox.Models;

namespace BiliXbox.Controls
{
    /// <summary>Formatting and image bounds shared by comment summaries and their reader.</summary>
    public static class CommentRichPresentation
    {
        public static bool TryImageUri(string value, out Uri uri)
        {
            uri = null;
            Uri address;
            if (!Uri.TryCreate(value, UriKind.Absolute, out address) || address.Scheme != "https" ||
                !string.IsNullOrEmpty(address.UserInfo) || !address.IsDefaultPort) return false;
            string host = address.DnsSafeHost;
            if (!BiliHost(host, "hdslb.com") && !BiliHost(host, "biliimg.com") && !BiliHost(host, "bilibili.com")) return false;
            uri = address;
            return true;
        }
        public static string LevelText(int? level)
            => level.HasValue && level.Value >= 0 && level.Value <= 6 ? "LV" + level.Value.ToString(CultureInfo.InvariantCulture) : "";
        public static string Metadata(CommentItem item, bool includeLikes)
        {
            if (item == null) return "";
            string value = (item.TimeText ?? "").Trim();
            string location = (item.IpLocation ?? "").Trim();
            if (location.Length > 0)
                value = Join(value, location.StartsWith("IP属地", StringComparison.Ordinal) || location.StartsWith("IP 属地", StringComparison.Ordinal)
                    ? location : "IP属地：" + location);
            if (includeLikes) value = Join(value, "点赞 " + LikeCount(item.Likes));
            return value;
        }
        public static string LikeCount(long count) => Math.Max(0, count).ToString("N0", CultureInfo.InvariantCulture);
        public static bool HasPictures(CommentItem item) => (item?.Pictures?.Count ?? 0) > 0;
        public static bool OpenDetailsOnPrimary(CommentItem item, bool isTextTrimmed, bool canViewReplies)
            => item != null && (isTextTrimmed || HasPictures(item) || !canViewReplies ||
                item.ReplyCount <= 0 && (item.Replies?.Count ?? 0) == 0);
        public static double EmoteHeight(CommentContentPart part)
            => part != null && Finite(part.Scale) && part.Scale > 1 ? 48 : 30;
        public static double EmoteWidth(CommentContentPart part)
        {
            double ratio = part != null && Finite(part.Width) && Finite(part.Height) && part.Width > 0 && part.Height > 0
                ? part.Width / part.Height : 1;
            return Math.Min(96, EmoteHeight(part) * Math.Max(0.5, Math.Min(2.5, ratio)));
        }
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        private static bool BiliHost(string host, string domain)
            => host.Equals(domain, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
        private static string Join(string first, string second) => first.Length == 0 ? second : first + "   ·   " + second;
    }
}
