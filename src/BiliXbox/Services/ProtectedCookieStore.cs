using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.DataProtection;
using Windows.Storage;
using Windows.Web.Http;

namespace BiliXbox.Services
{
    /// <summary>
    /// An in-memory cookie jar, persisted only as a user-bound encrypted file.
    /// The HTTP filter's automatic cookies must stay disabled to avoid a second disk cookie store.
    /// </summary>
    internal sealed class ProtectedCookieStore
    {
        private const string FileName = "bilibili-session.v1.bin";
        private readonly object _sync = new object();
        private readonly List<CookieRecord> _records = new List<CookieRecord>();

        internal async Task LoadAsync()
        {
            var file = await ApplicationData.Current.LocalFolder.TryGetItemAsync(FileName) as StorageFile;
            if (file == null) return;
            try
            {
                var protectedBytes = await FileIO.ReadBufferAsync(file);
                var plain = await new DataProtectionProvider().UnprotectAsync(protectedBytes);
                var json = CryptographicBuffer.ConvertBinaryToString(BinaryStringEncoding.Utf8, plain);
                var cookies = JsonConvert.DeserializeObject<List<CookieRecord>>(json);
                if (cookies == null) throw new FormatException();
                lock (_sync)
                {
                    _records.Clear();
                    foreach (var record in cookies)
                    {
                        if (!IsBilibiliDomain(record.Domain) || string.IsNullOrEmpty(record.Name)) continue;
                        if (record.Expires.HasValue && record.Expires <= DateTimeOffset.UtcNow) continue;
                        if (ContainsControl(record.Name) || ContainsControl(record.Value)) continue;
                        _records.Add(record);
                    }
                }
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                lock (_sync) _records.Clear();
                await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
                throw new BilibiliApiException(-1, "本机登录信息无法解密，已清除。请重新扫码登录。");
            }
        }

        internal async Task SaveAsync()
        {
            string json;
            lock (_sync) json = JsonConvert.SerializeObject(_records.Where(IsCurrent).ToList());
            var plain = CryptographicBuffer.ConvertStringToBinary(json, BinaryStringEncoding.Utf8);
            var encrypted = await new DataProtectionProvider("LOCAL=user").ProtectAsync(plain);
            var folder = ApplicationData.Current.LocalFolder;
            var temporary = await folder.CreateFileAsync(FileName + ".tmp", CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteBufferAsync(temporary, encrypted);
            var existing = await folder.TryGetItemAsync(FileName) as StorageFile;
            if (existing != null) await temporary.MoveAndReplaceAsync(existing);
            else await temporary.RenameAsync(FileName, NameCollisionOption.FailIfExists);
        }

        internal async Task ClearAsync()
        {
            lock (_sync) _records.Clear();
            var folder = ApplicationData.Current.LocalFolder;
            foreach (var name in new[] { FileName, FileName + ".tmp" })
            {
                var file = await folder.TryGetItemAsync(name) as StorageFile;
                if (file != null) await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
            }
        }

        // Login responses are staged separately until both passport status and
        // the account nav endpoint confirm the new session. A failed challenge
        // must never overwrite an already signed-in account's cookies.
        internal ProtectedCookieStore CloneForLogin()
        {
            var copy = new ProtectedCookieStore();
            lock (_sync)
                copy._records.AddRange(_records.Where(c => IsCurrent(c) && !IsAccountCookie(c.Name)).Select(CopyCookie));
            return copy;
        }

        internal void ReplaceWith(ProtectedCookieStore source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            List<CookieRecord> copy;
            lock (source._sync) copy = source._records.Where(IsCurrent).Select(CopyCookie).ToList();
            lock (_sync) { _records.Clear(); _records.AddRange(copy); }
        }

        internal void ImportLoginRedirectCookies(string url)
        {
            if (!string.IsNullOrEmpty(GetCookieValue("SESSDATA")) && !string.IsNullOrEmpty(GetCookieValue("bili_jct"))
                && !string.IsNullOrEmpty(GetCookieValue("DedeUserID"))) return;
            Uri uri;
            if (string.IsNullOrEmpty(url) || url.Length > 16384 || !Uri.TryCreate(url, UriKind.Absolute, out uri)
                || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0
                || !(uri.Host.Equals("passport.biligame.com", StringComparison.OrdinalIgnoreCase)
                    || uri.Host.Equals("passport.bilibili.com", StringComparison.OrdinalIgnoreCase))
                || !(uri.AbsolutePath == "/crossDomain" || uri.AbsolutePath == "/x/passport-login/web/crossDomain")) return;
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var item in uri.Query.TrimStart('?').Split('&'))
            {
                var equals = item.IndexOf('=');
                if (equals <= 0) continue;
                var name = Uri.UnescapeDataString(item.Substring(0, equals));
                if (name != "SESSDATA" && name != "bili_jct" && name != "DedeUserID") continue;
                if (values.ContainsKey(name)) return;
                var value = Uri.UnescapeDataString(item.Substring(equals + 1));
                if (value.Length == 0 || value.Length > 8192 || ContainsControl(value) || value.IndexOf(';') >= 0) return;
                values[name] = value;
            }
            long mid;
            if (values.Count != 3 || !long.TryParse(values["DedeUserID"], NumberStyles.None,
                CultureInfo.InvariantCulture, out mid) || mid <= 0) return;
            lock (_sync)
            {
                foreach (var pair in values)
                {
                    // Fill only absent credential cookies from this login
                    // response's official cross-domain URL. Never navigate it.
                    if (_records.Any(c => c.Name == pair.Key && IsCurrent(c) && DomainMatches("api.bilibili.com", c))) continue;
                    _records.Add(new CookieRecord { Name = pair.Key, Value = pair.Value, Domain = "bilibili.com",
                        Path = "/", Secure = true, HttpOnly = pair.Key == "SESSDATA", HostOnly = false });
                }
            }
        }

        private static bool IsAccountCookie(string name) => name == "SESSDATA" || name == "bili_jct"
            || name == "DedeUserID" || name == "DedeUserID__ckMd5" || name == "sid";
        private static CookieRecord CopyCookie(CookieRecord value) => new CookieRecord
        {
            Name = value.Name, Value = value.Value, Domain = value.Domain, Path = value.Path,
            HostOnly = value.HostOnly, HttpOnly = value.HttpOnly, Secure = value.Secure, Expires = value.Expires
        };

        internal string GetCookieValue(string name)
        {
            lock (_sync)
                return _records.FirstOrDefault(c => c.Name == name && IsCurrent(c)
                    && DomainMatches("api.bilibili.com", c))?.Value;
        }

        internal string GetCookieHeader(Uri uri)
        {
            if (uri.Scheme != "https" || !IsBilibiliDomain(uri.Host)) return "";
            lock (_sync)
            {
                return string.Join("; ", _records.Where(c => IsCurrent(c) && DomainMatches(uri.Host, c)
                    && PathMatches(uri.AbsolutePath, c.Path)).OrderByDescending(c => (c.Path ?? "/").Length)
                    .Select(c => c.Name + "=" + c.Value));
            }
        }

        internal void ApplyResponseCookies(HttpResponseMessage response, Uri requestUri)
        {
            if (requestUri.Scheme != "https" || !IsBilibiliDomain(requestUri.Host)) return;
            string header;
            if (!response.Headers.TryGetValue("Set-Cookie", out header) || string.IsNullOrWhiteSpace(header)) return;
            // Headers may be coalesced. Split only before a new cookie name, not at Expires' comma.
            foreach (var text in Regex.Split(header, @",(?=\s*[^;,\s=]+=)"))
            {
                var attributes = text.Split(';');
                var equals = attributes[0].IndexOf('=');
                if (equals <= 0) continue;
                var record = new CookieRecord
                {
                    Name = attributes[0].Substring(0, equals).Trim(),
                    Value = attributes[0].Substring(equals + 1).Trim(),
                    Domain = requestUri.Host.ToLowerInvariant(), HostOnly = true,
                    Path = DefaultPath(requestUri.AbsolutePath)
                };
                if (!Regex.IsMatch(record.Name, @"^[!#$%&'*+.^_`|~0-9A-Za-z-]+$") || ContainsControl(record.Value)) continue;
                long? maximumAge = null;
                foreach (var attribute in attributes.Skip(1))
                {
                    var separator = attribute.IndexOf('=');
                    var name = (separator < 0 ? attribute : attribute.Substring(0, separator)).Trim();
                    var value = separator < 0 ? "" : attribute.Substring(separator + 1).Trim();
                    switch (name.ToLowerInvariant())
                    {
                        case "domain": record.Domain = value.TrimStart('.').ToLowerInvariant(); record.HostOnly = false; break;
                        case "path": if (value.StartsWith("/", StringComparison.Ordinal)) record.Path = value; break;
                        case "secure": record.Secure = true; break;
                        case "httponly": record.HttpOnly = true; break;
                        case "expires":
                            DateTimeOffset expires;
                            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces
                                | DateTimeStyles.AssumeUniversal, out expires)) record.Expires = expires;
                            break;
                        case "max-age":
                            long age;
                            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out age)) maximumAge = age;
                            break;
                    }
                }
                if (!IsBilibiliDomain(record.Domain) || !DomainMatches(requestUri.Host, record)) continue;
                if (maximumAge.HasValue)
                    record.Expires = maximumAge <= 0 ? DateTimeOffset.MinValue
                        : DateTimeOffset.UtcNow.AddSeconds(Math.Min(maximumAge.Value, 315360000));
                lock (_sync)
                {
                    _records.RemoveAll(c => c.Name == record.Name && c.Domain == record.Domain && c.Path == record.Path);
                    if (IsCurrent(record)) _records.Add(record);
                }
            }
        }

        private static bool IsCurrent(CookieRecord record) => !record.Expires.HasValue || record.Expires > DateTimeOffset.UtcNow;
        private static bool DomainMatches(string host, CookieRecord record) => host.Equals(record.Domain, StringComparison.OrdinalIgnoreCase)
            || (!record.HostOnly && host.EndsWith("." + record.Domain, StringComparison.OrdinalIgnoreCase));
        private static bool PathMatches(string requestPath, string cookiePath)
        {
            cookiePath = string.IsNullOrEmpty(cookiePath) ? "/" : cookiePath;
            return requestPath == cookiePath || (requestPath.StartsWith(cookiePath, StringComparison.Ordinal)
                && (cookiePath.EndsWith("/", StringComparison.Ordinal)
                || (requestPath.Length > cookiePath.Length && requestPath[cookiePath.Length] == '/')));
        }
        private static string DefaultPath(string requestPath)
        {
            var lastSlash = requestPath.LastIndexOf('/');
            return lastSlash <= 0 ? "/" : requestPath.Substring(0, lastSlash);
        }
        private static bool ContainsControl(string text) => text != null && text.Any(c => c < 0x20 || c == 0x7f);
        private static bool IsBilibiliDomain(string domain) => !string.IsNullOrEmpty(domain)
            && (domain.Equals("bilibili.com", StringComparison.OrdinalIgnoreCase)
            || domain.EndsWith(".bilibili.com", StringComparison.OrdinalIgnoreCase));

        private sealed class CookieRecord
        {
            public string Name { get; set; }
            public string Domain { get; set; }
            public string Path { get; set; }
            public string Value { get; set; }
            public bool HostOnly { get; set; }
            public bool HttpOnly { get; set; }
            public bool Secure { get; set; }
            public DateTimeOffset? Expires { get; set; }
        }
    }
}
