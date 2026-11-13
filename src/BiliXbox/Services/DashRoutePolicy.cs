using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace BiliXbox.Services
{
    // A route is a complete server-issued URL for one exact ISO-BMFF track.
    // Never construct a CDN URL or combine one track's path/query with another.
    // This policy deliberately does not probe/download media or alter byte ranges.
    internal sealed class DashRoutePolicy
    {
        private const int MaximumPendingRequests = 1024;
        private readonly object _gate = new object();
        private readonly TrackRoutes _video;
        private readonly TrackRoutes _audio;
        private readonly Dictionary<string, PendingRequest> _pending = new Dictionary<string, PendingRequest>(StringComparer.Ordinal);
        private int _changes;

        internal DashRoutePolicy(string manifest, IReadOnlyList<string> videoUrls, IReadOnlyList<string> audioUrls, int routeOffset)
        {
            if (routeOffset < 0) throw new ArgumentOutOfRangeException(nameof(routeOffset));
            var document = ReadManifest(manifest);
            _video = CreateTrack(document, "video", videoUrls, routeOffset);
            _audio = CreateTrack(document, "audio", audioUrls, routeOffset);
        }

        internal bool HasAlternateRoutes
        {
            get { return (_video != null && _video.Urls.Length > 1) || (_audio != null && _audio.Urls.Length > 1); }
        }

        internal int RouteChangeCount { get { lock (_gate) return _changes; } }
        internal int NextRouteOffset
        {
            get
            {
                lock (_gate)
                {
                    var track = _video != null && _video.Urls.Length > 1 ? _video : _audio;
                    return track == null ? 0 : track.Selected + 1;
                }
            }
        }

        internal string RouteSummary
        {
            get
            {
                lock (_gate)
                {
                    var parts = new List<string>();
                    if (_video != null) parts.Add(Summary("视频", _video));
                    if (_audio != null) parts.Add(Summary("音频", _audio));
                    return string.Join("；", parts);
                }
            }
        }

        // The native request keeps its Range and headers. Only its resource URI
        // changes. Unknown resources (manifests, keys, other tracks) stay untouched.
        internal Uri ResolveRequest(Uri originalUri, string requestId)
        {
            if (originalUri == null || !originalUri.IsAbsoluteUri) return originalUri;
            lock (_gate)
            {
                var video = Matches(_video, originalUri);
                var audio = Matches(_audio, originalUri);
                // Ambiguous metadata must never route audio bytes into video.
                if (video == audio) return originalUri;
                var track = video ? _video : _audio;
                if (!string.IsNullOrEmpty(requestId))
                {
                    // Normally DownloadCompleted removes entries immediately.
                    // Cap retained native request metadata if callbacks are lost.
                    if (_pending.Count >= MaximumPendingRequests) _pending.Clear();
                    _pending[requestId] = new PendingRequest(track, track.Selected);
                }
                return track.Urls[track.Selected];
            }
        }

        internal bool ReportFailure(string requestId)
        {
            if (string.IsNullOrEmpty(requestId)) return false;
            lock (_gate)
            {
                PendingRequest request;
                if (!_pending.TryGetValue(requestId, out request)) return false;
                _pending.Remove(requestId);
                var track = request.Track;
                // Several native byte-range requests can fail together. A stale
                // failure must not rotate a replacement it never downloaded from.
                if (track.Selected != request.Selected) return false;
                track.Unavailable.Add(request.Selected);
                for (int step = 1; step < track.Urls.Length; step++)
                {
                    int next = (track.Selected + step) % track.Urls.Length;
                    if (track.Unavailable.Contains(next)) continue;
                    track.Selected = next;
                    _changes++;
                    return true;
                }
                return false;
            }
        }

        internal void CompleteRequest(string requestId)
        {
            if (string.IsNullOrEmpty(requestId)) return;
            lock (_gate) _pending.Remove(requestId);
        }

        private static bool Matches(TrackRoutes track, Uri uri)
        {
            return track != null && track.ResourceKeys.Contains(uri.AbsoluteUri);
        }

        private static string Summary(string name, TrackRoutes track)
        {
            // Host only: signed query parameters and paths never reach the UI.
            return name + "线路 " + (track.Selected + 1).ToString(CultureInfo.InvariantCulture) + "/"
                + track.Urls.Length.ToString(CultureInfo.InvariantCulture) + " · " + track.Urls[track.Selected].DnsSafeHost;
        }

        private static XDocument ReadManifest(string manifest)
        {
            if (string.IsNullOrWhiteSpace(manifest)) throw new ArgumentException("DASH 播放清单为空。", nameof(manifest));
            try
            {
                using (var text = new StringReader(manifest))
                using (var reader = XmlReader.Create(text, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                    return XDocument.Load(reader);
            }
            catch (XmlException) { throw new ArgumentException("DASH 播放清单无效。", nameof(manifest)); }
        }

        private static TrackRoutes CreateTrack(XDocument document, string kind, IReadOnlyList<string> supplied, int routeOffset)
        {
            if (supplied == null || supplied.Count == 0 || document.Root == null) return null;
            var ns = document.Root.Name.Namespace;
            var originals = document.Descendants(ns + "AdaptationSet")
                .Where(item => string.Equals((string)item.Attribute("contentType"), kind, StringComparison.OrdinalIgnoreCase)
                    || ((string)item.Attribute("mimeType") ?? "").StartsWith(kind + "/", StringComparison.OrdinalIgnoreCase))
                .SelectMany(item => item.Elements(ns + "Representation"))
                .SelectMany(item => item.Elements(ns + "BaseURL"))
                .Select(item => HttpsUri(item.Value)).Where(item => item != null).ToArray();
            // The current MPD has exactly one representation per track. Refuse
            // to map a future multi-quality manifest onto one representation.
            if (originals.Length != 1) return null;
            var urls = new List<Uri>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in supplied)
            {
                var uri = HttpsUri(value);
                if (uri != null && keys.Add(uri.AbsoluteUri)) urls.Add(uri);
            }
            // Mapping requires that this exact manifest track is present in the
            // server-supplied list. A mismatched list is ignored, never guessed.
            if (urls.Count == 0 || !keys.Contains(originals[0].AbsoluteUri)) return null;
            return new TrackRoutes(urls.ToArray(), keys, routeOffset);
        }

        private static Uri HttpsUri(string value)
        {
            Uri uri;
            return Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Scheme == Uri.UriSchemeHttps
                && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment) ? uri : null;
        }

        private sealed class TrackRoutes
        {
            internal readonly Uri[] Urls;
            internal readonly HashSet<string> ResourceKeys;
            internal readonly HashSet<int> Unavailable = new HashSet<int>();
            internal int Selected;
            internal TrackRoutes(Uri[] urls, HashSet<string> resourceKeys, int offset)
            {
                Urls = urls;
                ResourceKeys = resourceKeys;
                Selected = offset % urls.Length;
                // A rebuild with an explicit offset must not immediately pick
                // one of the earlier routes again after a download failure.
                for (int index = 0; index < Math.Min(offset, urls.Length); index++)
                    if (index != Selected) Unavailable.Add(index);
            }
        }

        private sealed class PendingRequest
        {
            internal readonly TrackRoutes Track;
            internal readonly int Selected;
            internal PendingRequest(TrackRoutes track, int selected) { Track = track; Selected = selected; }
        }
    }
}
