using System;

namespace BiliXbox.Services
{
    // Preserve the reported DASH metadata. Values above the documented 60fps
    // query range permit only a bounded native compatibility trial against a
    // 60fps baseline, never a claim that the reported high rate is supported.
    internal static class PlaybackFrameRatePolicy
    {
        internal const double MaximumBaselineFrameRate = 60;
        internal const double NearSixtyMetadataMaximum = 63;
        internal const double MaximumTrialReportedFrameRate = 120.001;

        internal static bool TryGetProbeRate(double reported, out double probe, out bool nativeTrialRequired)
        {
            probe = 0; nativeTrialRequired = false;
            if (double.IsNaN(reported) || double.IsInfinity(reported) || reported <= 0) return false;
            if (reported > 60.001)
            {
                if (reported > MaximumTrialReportedFrameRate) return false;
                probe = MaximumBaselineFrameRate; nativeTrialRequired = true;
                return true;
            }
            probe = reported <= 24.001 ? 24 : reported <= 25.001 ? 25
                : reported <= 29.971 ? 29.97 : reported <= 30.001 ? 30
                : reported <= 50.001 ? 50 : reported <= 59.941 ? 59.94 : 60;
            return true;
        }

        internal static bool TryGetOutputRate(double reported, double declaredProbe, bool nativeTrialRequired,
            out double output)
        {
            bool trial;
            if (!TryGetProbeRate(reported, out output, out trial)) return false;
            if (trial && (!nativeTrialRequired || double.IsNaN(declaredProbe) || double.IsInfinity(declaredProbe)
                || Math.Abs(declaredProbe - MaximumBaselineFrameRate) > .001))
            { output = 0; return false; }
            return true;
        }
    }
}
