using System;
using BiliXbox.Models;
using Windows.Storage;

namespace BiliXbox.Services
{
    // Subtitle appearance, saved on this device for every video.
    internal sealed class SubtitleSettings
    {
        internal static readonly double[] Scales = { 0.8, 1, 1.25, 1.5 };
        internal static readonly string[] ScaleLabels = { "小", "中", "大", "特大" };
        internal static readonly uint[] Colors = { 0xFFFFFF, 0xFFE14D, 0x7FE7FF, 0x8CFF8C };
        internal static readonly string[] ColorLabels = { "白色", "黄色", "青色", "绿色" };
        internal static readonly double[] Backgrounds = { 0, 0.25, 0.5, 0.75 };
        internal static readonly double[] Heights = { 40, 100, 160 };
        internal static readonly string[] HeightLabels = { "低", "中", "高" };

        internal int Scale = 1, Color, Background = 3, Height;
        internal DanmakuStrokeStyle Stroke = DanmakuStrokeStyle.Outline;
        internal bool Top;
        // Language (lan) of the last chosen track; empty means subtitles off.
        internal string LastLanguage = "";

        internal double FontScale => Scales[Clamp(Scale, Scales.Length)];
        internal uint Rgb => Colors[Clamp(Color, Colors.Length)];
        internal double BackgroundOpacity => Backgrounds[Clamp(Background, Backgrounds.Length)];
        internal double Offset => Heights[Clamp(Height, Heights.Length)];

        internal static int Clamp(int value, int count) => Math.Max(0, Math.Min(count - 1, value));
    }

    internal static class SubtitleSettingsStore
    {
        private const string Key = "subtitle.preferences.v1";

        internal static SubtitleSettings Load()
        {
            var settings = new SubtitleSettings();
            try
            {
                object stored;
                if (!ApplicationData.Current.LocalSettings.Values.TryGetValue(Key, out stored)) return settings;
                var values = stored as ApplicationDataCompositeValue;
                if (values == null) return settings;
                settings.Scale = Int(values, "scale", settings.Scale);
                settings.Color = Int(values, "color", settings.Color);
                settings.Background = Int(values, "background", settings.Background);
                settings.Height = Int(values, "height", settings.Height);
                int stroke = Int(values, "stroke", (int)settings.Stroke);
                settings.Stroke = Enum.IsDefined(typeof(DanmakuStrokeStyle), stroke) ? (DanmakuStrokeStyle)stroke : DanmakuStrokeStyle.Outline;
                settings.Top = values.TryGetValue("top", out var top) && top is bool flag && flag;
                settings.LastLanguage = values.TryGetValue("lastLanguage", out var language) ? language as string ?? "" : "";
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException)) { }
            return settings;
        }

        internal static void Save(SubtitleSettings settings)
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values[Key] = new ApplicationDataCompositeValue
                {
                    ["scale"] = settings.Scale, ["color"] = settings.Color, ["background"] = settings.Background,
                    ["height"] = settings.Height, ["stroke"] = (int)settings.Stroke, ["top"] = settings.Top,
                    ["lastLanguage"] = settings.LastLanguage ?? ""
                };
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException)) { }
        }

        private static int Int(ApplicationDataCompositeValue values, string key, int fallback)
            => values.TryGetValue(key, out var value) && value is int number ? number : fallback;
    }
}
