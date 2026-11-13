using System;

namespace BiliXbox.Models
{
    // Immutable snapshots keep UI edits from changing a renderer mid-frame.
    public sealed class DanmakuSettings
    {
        public static DanmakuSettings Default { get; } = new DanmakuSettings();
        public double AreaFraction { get; }
        public double Opacity { get; }
        public double FontScale { get; }
        public int DensityLimit { get; }
        public double SpeedScale { get; }
        public bool SmartMask { get; }
        // An optional fixed safe area, independent of a server-supplied person mask.
        public bool AvoidCenter { get; }
        public bool PreventOverlap { get; }
        public bool ShowScrolling { get; }
        public bool ShowTop { get; }
        public bool ShowBottom { get; }
        public bool ShowColor { get; }
        public bool MergeDuplicates { get; }

        public DanmakuSettings(double areaFraction = 1, double opacity = 0.9, double fontScale = 1,
            int densityLimit = 24, double speedScale = 1, bool smartMask = true, bool avoidCenter = false,
            bool preventOverlap = true, bool showScrolling = true, bool showTop = true,
            bool showBottom = true, bool showColor = true, bool mergeDuplicates = true)
        {
            AreaFraction = Bound(areaFraction, 0.25, 1, 1);
            Opacity = Bound(opacity, 0.1, 1, 0.9);
            FontScale = Bound(fontScale, 0.65, 1.8, 1);
            DensityLimit = Math.Max(4, Math.Min(64, densityLimit));
            SpeedScale = Bound(speedScale, 0.5, 2, 1);
            SmartMask = smartMask;
            AvoidCenter = avoidCenter;
            PreventOverlap = preventOverlap;
            ShowScrolling = showScrolling;
            ShowTop = showTop;
            ShowBottom = showBottom;
            ShowColor = showColor;
            MergeDuplicates = mergeDuplicates;
        }

        public DanmakuSettings With(double? areaFraction = null, double? opacity = null, double? fontScale = null,
            int? densityLimit = null, double? speedScale = null, bool? smartMask = null, bool? avoidCenter = null,
            bool? preventOverlap = null, bool? showScrolling = null, bool? showTop = null,
            bool? showBottom = null, bool? showColor = null, bool? mergeDuplicates = null)
        {
            return new DanmakuSettings(areaFraction ?? AreaFraction, opacity ?? Opacity, fontScale ?? FontScale,
                densityLimit ?? DensityLimit, speedScale ?? SpeedScale, smartMask ?? SmartMask, avoidCenter ?? AvoidCenter,
                preventOverlap ?? PreventOverlap, showScrolling ?? ShowScrolling, showTop ?? ShowTop,
                showBottom ?? ShowBottom, showColor ?? ShowColor, mergeDuplicates ?? MergeDuplicates);
        }

        private static double Bound(double value, double minimum, double maximum, double fallback)
            => double.IsNaN(value) || double.IsInfinity(value) ? fallback : Math.Max(minimum, Math.Min(maximum, value));
    }
}
