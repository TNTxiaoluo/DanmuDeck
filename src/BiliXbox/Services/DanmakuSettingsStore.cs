using System;
using System.Globalization;
using BiliXbox.Models;
using Windows.Storage;

namespace BiliXbox.Services
{
    // Ordinary device preferences only: no session cookies or account credentials.
    public sealed class DanmakuSettingsStore
    {
        private const string Key = "danmaku.preferences.v1";
        public DanmakuSettings Load()
        {
            try
            {
                object stored;
                if (!ApplicationData.Current.LocalSettings.Values.TryGetValue(Key, out stored)) return DanmakuSettings.Default;
                var values = stored as ApplicationDataCompositeValue;
                if (values == null) return DanmakuSettings.Default;
                return new DanmakuSettings(Number(values, "areaFraction", 1), Number(values, "opacity", 0.9),
                    Number(values, "fontScale", 1), Integer(values, "densityLimit", 24), Number(values, "speedScale", 1),
                    Flag(values, "smartMask", true), Flag(values, "avoidCenter", false), Flag(values, "preventOverlap", true),
                    Flag(values, "showScrolling", true), Flag(values, "showTop", true), Flag(values, "showBottom", true),
                    Flag(values, "showColor", true), Flag(values, "mergeDuplicates", true));
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException)) { return DanmakuSettings.Default; }
        }

        public void Save(DanmakuSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            // One composite assignment avoids a half-written settings snapshot.
            ApplicationData.Current.LocalSettings.Values[Key] = new ApplicationDataCompositeValue
            {
                ["areaFraction"] = settings.AreaFraction, ["opacity"] = settings.Opacity,
                ["fontScale"] = settings.FontScale, ["densityLimit"] = settings.DensityLimit,
                ["speedScale"] = settings.SpeedScale, ["smartMask"] = settings.SmartMask,
                ["avoidCenter"] = settings.AvoidCenter, ["preventOverlap"] = settings.PreventOverlap,
                ["showScrolling"] = settings.ShowScrolling, ["showTop"] = settings.ShowTop,
                ["showBottom"] = settings.ShowBottom, ["showColor"] = settings.ShowColor,
                ["mergeDuplicates"] = settings.MergeDuplicates
            };
        }

        private static double Number(ApplicationDataCompositeValue values, string key, double fallback)
        {
            object value;
            if (!values.TryGetValue(key, out value) || value == null) return fallback;
            try { return Convert.ToDouble(value, CultureInfo.InvariantCulture); }
            catch (Exception ex) when (ex is FormatException || ex is InvalidCastException || ex is OverflowException) { return fallback; }
        }
        private static int Integer(ApplicationDataCompositeValue values, string key, int fallback)
        {
            double number = Number(values, key, fallback);
            if (double.IsNaN(number) || double.IsInfinity(number)) return fallback;
            return (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, number));
        }
        private static bool Flag(ApplicationDataCompositeValue values, string key, bool fallback)
        {
            object value;
            bool parsed;
            return values.TryGetValue(key, out value) && value != null && bool.TryParse(value.ToString(), out parsed) ? parsed : fallback;
        }
    }
}
