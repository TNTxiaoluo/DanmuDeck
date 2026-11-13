// Used only by Test-ApiLogic.ps1. Never include this file in the UWP project.
// Storage and DataProtectionProvider deliberately throw: this test does not verify DPAPI or disk I/O.
namespace Windows.Security.Cryptography
{
    public enum BinaryStringEncoding { Utf8 }
    public static class CryptographicBuffer
    {
        public static byte[] ConvertStringToBinary(string value, BinaryStringEncoding encoding)
            => global::System.Text.Encoding.UTF8.GetBytes(value);
        public static string ConvertBinaryToString(BinaryStringEncoding encoding, byte[] value)
            => global::System.Text.Encoding.UTF8.GetString(value);
        public static string EncodeToHexString(byte[] value)
            => global::System.BitConverter.ToString(value).Replace("-", "");
    }
}
namespace Windows.Security.Cryptography.Core
{
    public static class HashAlgorithmNames { public const string Md5 = "MD5"; }
    public sealed class HashAlgorithmProvider
    {
        public static HashAlgorithmProvider OpenAlgorithm(string name) => new HashAlgorithmProvider();
        public byte[] HashData(byte[] value)
        {
            using (var hash = global::System.Security.Cryptography.MD5.Create()) return hash.ComputeHash(value);
        }
    }
}
namespace Windows.Security.Cryptography.DataProtection
{
    public sealed class DataProtectionProvider
    {
        public DataProtectionProvider() { }
        public DataProtectionProvider(string descriptor) { }
        public global::System.Threading.Tasks.Task<byte[]> ProtectAsync(byte[] value) => throw new global::System.NotImplementedException();
        public global::System.Threading.Tasks.Task<byte[]> UnprotectAsync(byte[] value) => throw new global::System.NotImplementedException();
    }
}
namespace Windows.Storage
{
    public enum CreationCollisionOption { ReplaceExisting }
    public enum NameCollisionOption { FailIfExists }
    public enum StorageDeleteOption { PermanentDelete }
    public sealed class ApplicationData
    {
        public static ApplicationData Current { get; } = new ApplicationData();
        public StorageFolder LocalFolder { get; } = new StorageFolder();
    }
    public sealed class StorageFolder
    {
        public global::System.Threading.Tasks.Task<object> TryGetItemAsync(string name) => throw new global::System.NotImplementedException();
        public global::System.Threading.Tasks.Task<StorageFile> CreateFileAsync(string name, CreationCollisionOption option)
            => throw new global::System.NotImplementedException();
    }
    public sealed class StorageFile
    {
        public global::System.Threading.Tasks.Task DeleteAsync(StorageDeleteOption option) => throw new global::System.NotImplementedException();
        public global::System.Threading.Tasks.Task MoveAndReplaceAsync(StorageFile file) => throw new global::System.NotImplementedException();
        public global::System.Threading.Tasks.Task RenameAsync(string name, NameCollisionOption option) => throw new global::System.NotImplementedException();
    }
    public static class FileIO
    {
        public static global::System.Threading.Tasks.Task<byte[]> ReadBufferAsync(StorageFile file) => throw new global::System.NotImplementedException();
        public static global::System.Threading.Tasks.Task WriteBufferAsync(StorageFile file, byte[] value) => throw new global::System.NotImplementedException();
    }
}
namespace Windows.Web.Http
{
    public sealed class HttpResponseMessage
    {
        public global::System.Collections.Generic.Dictionary<string, string> Headers { get; }
            = new global::System.Collections.Generic.Dictionary<string, string>();
    }
}
namespace BiliXbox.Services
{
    public static class ApiLogicTests
    {
        private static void Check(bool value, string name)
        {
            if (!value) throw new System.Exception("FAIL: " + name);
        }

        private static void Set(ProtectedCookieStore jar, string header)
        {
            var response = new Windows.Web.Http.HttpResponseMessage();
            response.Headers["Set-Cookie"] = header;
            jar.ApplyResponseCookies(response, new System.Uri("https://passport.bilibili.com/"));
        }

        public static string Run()
        {
            // Historical, published WBI test vector; these are public image keys, not credentials.
            // https://gitea.s1f.ren/shiran/bilibili-API-collect/src/commit/4234084026b19caba4b593fda80e830cf89c8147/docs/misc/sign/wbi.md
            const string image = "7cd084941338484aae1ad9425b84077c";
            const string sub = "4932caff0ff746eab6f01bf08b70ac45";
            var parameters = new System.Collections.Generic.Dictionary<string, string>
                { { "foo", "114" }, { "bar", "514" }, { "zab", "1919810" } };
            var query = WbiSigner.Sign(parameters, image, sub, 1702204169);
            Check(query == "bar=514&foo=114&wts=1702204169&zab=1919810&w_rid=8f6f2b5b3d485fe1886cec6a0be8c5d4", "Published WBI signature vector");
            Check(!parameters.ContainsKey("wts"), "Signer preserves caller input");
            var unicode = WbiSigner.Sign(new System.Collections.Generic.Dictionary<string, string>
                { { "keyword", "五一四 a!()*'" } }, image, sub, 1702204169);
            Check(unicode.StartsWith("keyword=%E4%BA%94%E4%B8%80%E5%9B%9B%20a&wts=", System.StringComparison.Ordinal),
                "WBI UTF-8 percent encoding, spaces, and filtered punctuation");
            var badKeyRejected = false;
            try { WbiSigner.Sign(parameters, "invalid", sub, 1702204169); }
            catch (BilibiliApiException) { badKeyRejected = true; }
            Check(badKeyRejected, "Changed WBI key format fails explicitly");

            // Only synthetic cookies are used. No network or actual account changes occur.
            var jar = new ProtectedCookieStore();
            Set(jar, "SESSDATA=test-only; Domain=.bilibili.com; Path=/; Secure; HttpOnly; Expires=Thu, 01 Jan 2037 00:00:00 GMT, bili_jct=csrf-only; Domain=.bilibili.com; Path=/; Secure");
            Check(jar.GetCookieValue("SESSDATA") == "test-only", "Combined Set-Cookie and Expires comma");
            Check(jar.GetCookieHeader(new System.Uri("https://api.bilibili.com/x")).Contains("bili_jct=csrf-only"), "Parent domain cookie reaches API");
            Check(jar.GetCookieHeader(new System.Uri("https://evilbilibili.com/")).Length == 0, "Domain suffix boundary");
            Check(jar.GetCookieHeader(new System.Uri("http://api.bilibili.com/x")).Length == 0, "Never send session over HTTP");
            Set(jar, "host=test; Path=/");
            Check(!jar.GetCookieHeader(new System.Uri("https://api.bilibili.com/x")).Contains("host="), "Host-only cookie isolation");
            Set(jar, "attack=test; Domain=evil.example; Path=/");
            Check(!jar.GetCookieHeader(new System.Uri("https://api.bilibili.com/x")).Contains("attack="), "Reject foreign cookie domain");
            Set(jar, "path=test; Domain=.bilibili.com; Path=/foo");
            Check(jar.GetCookieHeader(new System.Uri("https://api.bilibili.com/foo/bar")).Contains("path=test"), "Cookie path child");
            Check(!jar.GetCookieHeader(new System.Uri("https://api.bilibili.com/foobar")).Contains("path=test"), "Cookie path segment boundary");
            Set(jar, "SESSDATA=deleted; Domain=.bilibili.com; Path=/; Max-Age=0");
            Check(jar.GetCookieValue("SESSDATA") == null, "Max-Age deletion");
            Set(jar, "expired=test; Domain=.bilibili.com; Path=/; Expires=Sat, 01 Jan 2000 00:00:00 GMT");
            Check(!jar.GetCookieHeader(new System.Uri("https://api.bilibili.com/x")).Contains("expired="), "Expired cookie is discarded");
            return "PASS: 4 WBI checks and 10 cookie checks using production source. WinRT, DPAPI, and disk persistence are not tested.";
        }
    }
}
