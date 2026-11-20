using System;
using System.Collections.Generic;
using System.IO;
using BiliXbox.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BiliXbox.Services
{
    internal static class LiveEmoteMetadata
    {
        internal const int MaximumEmotes = 16;
        internal static IReadOnlyList<LiveEmote> FromPacket(JArray properties, string text)
        {
            var result = new List<LiveEmote>();
            var mode = properties != null && properties.Count > 15 ? Object(properties[15]) : null;
            var extra = Object(mode?["extra"]);
            AddMap(result, extra?["emots"], text);
            if (properties != null && properties.Count > 13) Add(result, text, Object(properties[13]), text);
            return result;
        }
        internal static IReadOnlyList<LiveEmote> FromHistory(JToken message, string text)
        {
            var result = new List<LiveEmote>();
            AddMap(result, message?["emots"], text);
            var extra = Object(message?["extra"] ?? message?["mode_info"]?["extra"]);
            AddMap(result, extra?["emots"], text);
            Add(result, text, Object(message?["emoticon"]), text);
            return result;
        }
        private static void AddMap(List<LiveEmote> result, JToken value, string text)
        {
            var map = Object(value); if (map == null) return;
            int visited = 0;
            foreach (var property in map.Properties())
            { if (++visited > 64 || result.Count >= MaximumEmotes) break; Add(result, property.Name, Object(property.Value), text); }
        }
        private static void Add(List<LiveEmote> result, string token, JObject data, string text)
        {
            if (data == null || result.Count >= MaximumEmotes || string.IsNullOrEmpty(token) || token.Length > 512
                || (text ?? "").IndexOf(token, StringComparison.Ordinal) < 0) return;
            string url;
            if (data["url"]?.Type != JTokenType.String || !TryUrl((string)data["url"], out url)) return;
            foreach (var prior in result) if (prior.Text == token) return;
            result.Add(new LiveEmote { Text = token, ImageUrl = url, Width = Dimension(data["width"]), Height = Dimension(data["height"]) });
        }
        internal static bool TryUrl(string value, out string result)
        {
            result = ""; if (string.IsNullOrWhiteSpace(value) || value.Length > 2048) return false;
            if (value.StartsWith("//", StringComparison.Ordinal)) value = "https:" + value;
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || (uri.Scheme != "https" && uri.Scheme != "http")
                || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0) return false;
            var host = uri.DnsSafeHost;
            if (!host.EndsWith(".hdslb.com", StringComparison.OrdinalIgnoreCase)
                && !host.EndsWith(".biliimg.com", StringComparison.OrdinalIgnoreCase)) return false;
            result = uri.Scheme == "http" ? new UriBuilder(uri) { Scheme = "https", Port = -1 }.Uri.AbsoluteUri : uri.AbsoluteUri;
            return true;
        }
        private static int Dimension(JToken value)
        { int number; return value != null && int.TryParse(value.ToString(), out number) ? Math.Max(1, Math.Min(4096, number)) : 0; }
        private static JObject Object(JToken value)
        {
            if (value is JObject obj) return obj;
            if (value?.Type != JTokenType.String || ((string)value).Length > 65536) return null;
            try
            {
                using (var text = new StringReader((string)value))
                using (var reader = new JsonTextReader(text) { MaxDepth = 16, DateParseHandling = DateParseHandling.None })
                    return JToken.ReadFrom(reader) as JObject;
            }
            catch (JsonException) { return null; }
        }
    }
}
