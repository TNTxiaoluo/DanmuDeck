using System;
using BiliXbox.Models;
using Windows.Storage;

namespace BiliXbox.Services
{
    // The last confirmed account profile, so a start without network keeps
    // the saved session signed in (offline cache, history of this device).
    public sealed partial class BilibiliClient
    {
        private const string OfflineProfileKey = "offlineProfile";

        partial void LoadOfflineProfile(ref UserProfile profile)
        {
            try
            {
                var values = ApplicationData.Current.LocalSettings.Values[OfflineProfileKey] as ApplicationDataCompositeValue;
                if (values == null) return;
                long mid = Convert.ToInt64(values["mid"]);
                if (mid <= 0) return;
                profile = new UserProfile
                {
                    Mid = mid, Name = values["name"] as string ?? "", AvatarUrl = values["avatar"] as string ?? "",
                    IsPremium = values["premium"] is bool premium && premium
                };
            }
            catch (Exception) { profile = null; }
        }


        private const string LoginChannelKey = "loginChannelMid";

        partial void LoadLoginChannel(ref long mid)
        {
            try
            {
                var value = ApplicationData.Current.LocalSettings.Values[LoginChannelKey];
                mid = value == null ? 0 : Convert.ToInt64(value);
            }
            catch (Exception) { mid = 0; }
        }

        partial void SaveLoginChannel(long mid)
        {
            try { ApplicationData.Current.LocalSettings.Values[LoginChannelKey] = mid; }
            catch (Exception) { }
        }

        partial void SaveOfflineProfile(UserProfile profile)
        {
            if (profile == null || profile.Mid <= 0) return;
            try
            {
                ApplicationData.Current.LocalSettings.Values[OfflineProfileKey] = new ApplicationDataCompositeValue
                {
                    ["mid"] = profile.Mid, ["name"] = profile.Name ?? "", ["avatar"] = profile.AvatarUrl ?? "", ["premium"] = profile.IsPremium
                };
            }
            catch (Exception) { }
        }
    }
}
