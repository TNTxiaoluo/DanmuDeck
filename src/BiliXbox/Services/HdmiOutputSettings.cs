using System;
using Windows.Storage;

namespace BiliXbox.Services
{
    // FollowSystem (default): the UI and every video use the resolution and
    // refresh rate chosen in the Xbox system settings; the player scales each
    // source into that mode. Only an HDR10 source switches the output to the
    // matching HDR10 descriptor. PerSource keeps the original behaviour of
    // matching each source's resolution and frame rate and restoring the
    // system default after playback.
    internal static class HdmiOutputSettings
    {
        // A new key: the earlier "unified 4K HDR10 for the whole session"
        // choice does not carry over, so every install starts following the system.
        private const string Key = "HdmiOutput.FollowSystem";
        internal static bool FollowSystem
        {
            get
            {
                try
                {
                    object value;
                    return !ApplicationData.Current.LocalSettings.Values.TryGetValue(Key, out value) || !(value is bool) || (bool)value;
                }
                catch (Exception ex) when (!(ex is OutOfMemoryException)) { return true; }
            }
            set { ApplicationData.Current.LocalSettings.Values[Key] = value; }
        }
    }
}
