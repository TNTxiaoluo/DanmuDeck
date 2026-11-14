using BiliXbox.Models;

namespace BiliXbox.Controls
{
    // Entitlement hints are independent of codec/display availability: a
    // hardware failure must not hide the user's missing login or membership.
    internal static class PlaybackQualityEntitlement
    {
        internal static string Badge(PlaybackQuality quality, bool loggedIn, bool premium)
        {
            bool needsPremium = quality.RequiresPremium || quality.Id == 112 || quality.Id == 116
                || quality.Id == 120 || quality.Id == 125 || quality.Id == 126 || quality.Id == 127;
            if (!loggedIn && (quality.RequiresLogin || needsPremium || quality.Id >= 80)) return "需要登录";
            return loggedIn && !premium && needsPremium ? "需要大会员" : "";
        }
    }
}
