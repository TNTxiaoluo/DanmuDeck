namespace Windows.UI.Xaml.Data
{
    public interface IValueConverter
    {
        object Convert(object value, System.Type targetType, object parameter, string language);
        object ConvertBack(object value, System.Type targetType, object parameter, string language);
    }
}

namespace BiliXbox.MediaTimeTests
{
    public static class Tests
    {
        private static int _checks;

        public static string Run()
        {
            _checks = 0;
            Check("0:00", Controls.MediaTimeFormatter.Format(0d));
            Check("0:00", Controls.MediaTimeFormatter.Format(-97d));
            Check("0:00", Controls.MediaTimeFormatter.Format(double.NaN));
            Check("0:00", Controls.MediaTimeFormatter.Format(double.PositiveInfinity));
            Check("0:00", Controls.MediaTimeFormatter.Format(double.NegativeInfinity));
            Check("0:00", Controls.MediaTimeFormatter.Format(0.999d));
            Check("0:01", Controls.MediaTimeFormatter.Format(1d));
            Check("0:09", Controls.MediaTimeFormatter.Format(9.75d));
            Check("0:59", Controls.MediaTimeFormatter.Format(59.999d));
            Check("1:00", Controls.MediaTimeFormatter.Format(60d));
            Check("1:37", Controls.MediaTimeFormatter.Format(97d));
            Check("1:37", Controls.MediaTimeFormatter.Format(97.999d));
            Check("59:59", Controls.MediaTimeFormatter.Format(3599.999d));
            Check("1:00:00", Controls.MediaTimeFormatter.Format(3600d));
            Check("1:02:03", Controls.MediaTimeFormatter.Format(3723d));
            Check("25:01:01", Controls.MediaTimeFormatter.Format(90061d));
            Check("256204778:48:05", Controls.MediaTimeFormatter.Format(double.MaxValue));
            Check("0:00", Controls.MediaTimeFormatter.Format(System.TimeSpan.MinValue));
            Check("1:37", Controls.MediaTimeFormatter.Format(System.TimeSpan.FromTicks(979999999)));
            Check("49:00:00", Controls.MediaTimeFormatter.Format(System.TimeSpan.FromHours(49)));
            Check("256204778:48:05", Controls.MediaTimeFormatter.Format(System.TimeSpan.MaxValue));

            var converter = new Controls.PlaybackTimeConverter();
            Check("1:37", (string)converter.Convert(97d, typeof(string), null, "zh-CN"));
            Check("1:37", (string)converter.Convert(97, typeof(string), null, "en-US"));
            Check("1:37", (string)converter.Convert(97m, typeof(string), null, "it-IT"));
            Check("1:37", (string)converter.Convert(System.TimeSpan.FromSeconds(97), typeof(string), null, "zh-CN"));
            Check("1:02:03", (string)converter.Convert(3723d, typeof(string), null, "zh-CN"));
            Check("0:00", (string)converter.Convert(null, typeof(string), null, "zh-CN"));
            Check("0:00", (string)converter.Convert(double.NaN, typeof(string), null, "zh-CN"));
            Check("0:00", (string)converter.Convert(new object(), typeof(string), null, "zh-CN"));
            Check("0:00", (string)converter.Convert("unavailable", typeof(string), null, "zh-CN"));

            var oldCulture = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("ar-SA");
                Check("1:02:03", Controls.MediaTimeFormatter.Format(3723d));
            }
            finally { System.Globalization.CultureInfo.CurrentCulture = oldCulture; }

            try { converter.ConvertBack("1:37", typeof(double), null, "zh-CN"); }
            catch (System.NotSupportedException) { _checks++; return _checks + " media timestamp checks passed."; }
            throw new System.Exception("A display-only tooltip converter must reject reverse conversion.");
        }

        private static void Check(string expected, string actual)
        {
            if (expected != actual) throw new System.Exception("Expected " + expected + "; actual " + actual);
            _checks++;
        }
    }
}
