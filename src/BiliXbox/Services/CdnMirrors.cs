using System;
using System.Collections.Generic;
using System.Linq;

namespace BiliXbox.Services
{
    // Host lists and relay rules follow Bilibili-thread-ripper's cdn-resolver
    // (MIT). Bilibili's upos mirrors serve the same signed path and query, so
    // only the host is swapped. Every mirror is still verified per file by the
    // downloader (equal total length and byte-identical anchor) before use.
    internal static class CdnMirrors
    {
        // Overseas nodes first: the console is often outside mainland China.
        internal static readonly string[] Hosts =
        {
            "upos-sz-mirrorcosov.bilivideo.com", "upos-sz-mirroraliov.bilivideo.com",
            "cn-hk-eq-01-01.bilivideo.com", "cn-hk-eq-01-03.bilivideo.com",
            "upos-sz-mirrorali.bilivideo.com", "upos-sz-mirrorcos.bilivideo.com",
            "upos-sz-mirrorhw.bilivideo.com", "upos-sz-mirror08c.bilivideo.com"
        };

        internal static IReadOnlyList<Uri> Expand(IReadOnlyList<Uri> server)
        {
            if (server == null || server.Count == 0) return server ?? Array.Empty<Uri>();
            var donor = server.FirstOrDefault(IsUposBilivideo) ?? server.FirstOrDefault(IsUposAkamai);
            if (donor == null) return server;
            var result = new List<Uri>(server);
            var hosts = new HashSet<string>(server.Select(uri => uri.DnsSafeHost), StringComparer.OrdinalIgnoreCase);
            foreach (var host in Hosts)
            {
                if (!hosts.Add(host)) continue;
                var builder = new UriBuilder(donor) { Host = host, Port = -1 };
                result.Add(builder.Uri);
            }
            return result;
        }

        internal static bool IsRelay(Uri uri)
            => uri.DnsSafeHost.IndexOf("mcdn.", StringComparison.OrdinalIgnoreCase) >= 0
                || uri.AbsolutePath.StartsWith("/v1/resource/", StringComparison.OrdinalIgnoreCase)
                || uri.Query.IndexOf("os=mcdn", StringComparison.OrdinalIgnoreCase) >= 0
                || uri.DnsSafeHost.EndsWith(".szbdyd.com", StringComparison.OrdinalIgnoreCase);

        private static bool IsUposBilivideo(Uri uri)
            => uri.Scheme == Uri.UriSchemeHttps && !IsRelay(uri)
                && uri.DnsSafeHost.EndsWith(".bilivideo.com", StringComparison.OrdinalIgnoreCase)
                && uri.DnsSafeHost.StartsWith("upos-", StringComparison.OrdinalIgnoreCase);

        private static bool IsUposAkamai(Uri uri)
            => uri.Scheme == Uri.UriSchemeHttps && !IsRelay(uri)
                && uri.DnsSafeHost.StartsWith("upos-", StringComparison.OrdinalIgnoreCase)
                && uri.DnsSafeHost.EndsWith(".akamaized.net", StringComparison.OrdinalIgnoreCase);
    }
}
