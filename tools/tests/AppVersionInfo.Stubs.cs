namespace Windows.ApplicationModel
{
    public sealed class Package
    {
        public static bool ThrowOnCurrent;
        private static readonly Package Instance = new Package();
        public static Package Current
        {
            get { if (ThrowOnCurrent) throw new System.InvalidOperationException(); return Instance; }
        }
        public PackageId Id { get; } = new PackageId();
    }
    public sealed class PackageId
    {
        public PackageVersion Version { get; set; } = new PackageVersion { Major = 1, Minor = 0, Build = 8, Revision = 0 };
    }
    public struct PackageVersion
    {
        public ushort Major, Minor, Build, Revision;
    }
}

namespace BiliXbox.VersionTests
{
    public static class Tests
    {
        private static int checks;
        private static void Equal(string actual, string expected)
        {
            if (actual != expected) throw new System.Exception("Expected " + expected + ", got " + actual);
            checks++;
        }
        public static int Run()
        {
            Equal(Services.AppVersionInfo.FormatDisplayVersion("1.0.2.0", false, ""), "1.0.2");
            Equal(Services.AppVersionInfo.FormatDisplayVersion("1.0.2.0", true, ""), "1.0.2b");
            Equal(Services.AppVersionInfo.FormatDisplayVersion("1.0.8.0", true, "1.0.2"), "1.0.2b");
            Equal(Services.AppVersionInfo.FormatDisplayVersion("1.0.8.0", false, "1.0.2"), "1.0.2");
            Equal(Services.AppVersionInfo.FormatDisplayVersion("2.3.4.5", true, "bad"), "2.3.4b");
            Equal(Services.AppVersionInfo.FormatDisplayVersion("1.0.8.0", true, "1.0.2b"), "1.0.8b");
            Equal(Services.AppVersionInfo.FormatDisplayVersion("1.0.8.0", true, "1.0.2.0"), "1.0.8b");
            Equal(Services.AppVersionInfo.FormatDisplayVersion("bad", false, ""), "未知版本");
            Equal(Services.AppVersionInfo.FormatDisplayVersion("unavailable", true, "1.0.2"), "1.0.2b");
            Equal(Services.AppVersionInfo.FormatDisplayVersion("1.0.8.0", false, "1.65536.2"), "1.0.8");
            Equal(Services.AppVersionInfo.PackageVersion, "1.0.8.0");
            Windows.ApplicationModel.Package.ThrowOnCurrent = true;
            Equal(Services.AppVersionInfo.PackageVersion, "unavailable");
            // Lookup failure must not throw again while constructing the startup failure page.
            Equal(Services.AppVersionInfo.DisplayVersion, "未知版本b");
            Windows.ApplicationModel.Package.ThrowOnCurrent = false;
            return checks;
        }
    }
}
