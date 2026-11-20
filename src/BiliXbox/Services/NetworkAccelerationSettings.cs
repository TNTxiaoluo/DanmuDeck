using System;
using Windows.Storage;

namespace BiliXbox.Services
{
    internal static class NetworkAccelerationSettings
    {
        private const string Key = "NetworkAcceleration.Enabled";
        internal static bool Enabled
        {
            get
            {
                try { object value; return ApplicationData.Current.LocalSettings.Values.TryGetValue(Key, out value) && value is bool && (bool)value; }
                catch (Exception ex) when (!(ex is OutOfMemoryException)) { return false; }
            }
            set { ApplicationData.Current.LocalSettings.Values[Key] = value; }
        }
    }
}
