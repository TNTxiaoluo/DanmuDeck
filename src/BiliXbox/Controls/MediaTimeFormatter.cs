using System;
using System.Globalization;

namespace BiliXbox.Controls
{
    /// <summary>Formats media positions without wrapping hours after a day.</summary>
    public static class MediaTimeFormatter
    {
        private const long MaximumSeconds = long.MaxValue / TimeSpan.TicksPerSecond;

        public static string Format(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0) return "0:00";
            long wholeSeconds = seconds >= MaximumSeconds ? MaximumSeconds : (long)Math.Floor(seconds);
            return FormatSeconds(wholeSeconds);
        }

        public static string Format(TimeSpan position)
        {
            return FormatSeconds(Math.Max(0, position.Ticks / TimeSpan.TicksPerSecond));
        }

        private static string FormatSeconds(long seconds)
        {
            long hours = seconds / 3600;
            long minutes = (seconds / 60) % 60;
            long remainder = seconds % 60;
            return hours > 0
                ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", hours, minutes, remainder)
                : string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", minutes, remainder);
        }
    }
}
