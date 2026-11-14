using System;
using System.Globalization;
using Windows.ApplicationModel;

namespace BiliXbox.Services
{
    // Ordinary local builds are test builds. Release scripts override these
    // two constants only in isolated snapshots, never in the working source.
    internal static class AppVersionInfo
    {
        internal const bool IsTestBuild = true;
        internal const string ReleaseVersionOverride = "";

        internal static string PackageVersion
        {
            get
            {
                try
                {
                    var version = Package.Current.Id.Version;
                    return string.Format(CultureInfo.InvariantCulture, "{0}.{1}.{2}.{3}",
                        version.Major, version.Minor, version.Build, version.Revision);
                }
                catch (Exception) { return "unavailable"; }
            }
        }

        internal static string DisplayVersion => FormatDisplayVersion(PackageVersion, IsTestBuild, ReleaseVersionOverride);
        internal static string ApplicationLabel => "弹幕舱 · v" + DisplayVersion;

        internal static string FormatDisplayVersion(string packageVersion, bool testBuild, string releaseOverride)
        {
            Version parsed;
            string release;
            if (!string.IsNullOrEmpty(releaseOverride) && releaseOverride.Split('.').Length == 3
                && Version.TryParse(releaseOverride + ".0", out parsed) && ValidVersion(parsed))
                release = string.Format(CultureInfo.InvariantCulture, "{0}.{1}.{2}", parsed.Major, parsed.Minor, parsed.Build);
            else if (Version.TryParse(packageVersion, out parsed) && ValidVersion(parsed))
                release = string.Format(CultureInfo.InvariantCulture, "{0}.{1}.{2}", parsed.Major, parsed.Minor, parsed.Build);
            else release = "未知版本";
            return release + (testBuild ? "b" : "");
        }

        private static bool ValidVersion(Version version)
            => version.Major >= 0 && version.Major <= 65535 && version.Minor >= 0 && version.Minor <= 65535
                && version.Build >= 0 && version.Build <= 65535 && version.Revision >= 0 && version.Revision <= 65535;
    }
}
