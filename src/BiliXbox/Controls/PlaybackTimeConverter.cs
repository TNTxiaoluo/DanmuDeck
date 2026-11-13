using System;
using System.Globalization;
using Windows.UI.Xaml.Data;

namespace BiliXbox.Controls
{
    /// <summary>Shows a media timestamp in the native seek slider thumb tooltip.</summary>
    public sealed class PlaybackTimeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is TimeSpan position) return MediaTimeFormatter.Format(position);
            if (value == null) return "0:00";
            try { return MediaTimeFormatter.Format(System.Convert.ToDouble(value, CultureInfo.InvariantCulture)); }
            catch (FormatException) { return "0:00"; }
            catch (InvalidCastException) { return "0:00"; }
            catch (OverflowException) { return "0:00"; }
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotSupportedException("The seek tooltip displays a position; it does not edit one.");
        }
    }
}
