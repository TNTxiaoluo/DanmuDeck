using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;

namespace BiliXbox.Services
{
    /// <summary>Builds an on-demand MPD from the server's real ISO-BMFF tracks.</summary>
    internal static class DashManifestBuilder
    {
        private static readonly XNamespace Ns = "urn:mpeg:dash:schema:mpd:2011";

        internal static bool IsSupportedVideo(JToken track)
            => VideoRejectionReason(track).Length == 0;

        internal static string VideoRejectionReason(JToken track)
        {
            var codec = Text(track, "codecs");
            if (!(codec.StartsWith("avc1.", StringComparison.OrdinalIgnoreCase)
                || codec.StartsWith("av01.", StringComparison.OrdinalIgnoreCase)
                || codec.StartsWith("hev1.", StringComparison.OrdinalIgnoreCase)
                || codec.StartsWith("hvc1.", StringComparison.OrdinalIgnoreCase))) return "未实现此视频编码";
            if (!HasMedia(track)) return "缺少有效媒体地址或初始化/索引分段";
            return HasUnsupportedHdr(track) ? "不支持此 HDR 格式或 HDR 元数据冲突" : "";
        }

        internal static bool IsSupportedAudio(JToken track)
            => (IsAacAudio(track) || IsDolbyAudio(track) || IsFlacAudio(track)) && HasMedia(track);

        internal static bool IsAacAudio(JToken track)
            => Text(track, "codecs").StartsWith("mp4a.40.", StringComparison.OrdinalIgnoreCase);

        internal static bool IsDolbyAudio(JToken track)
            => string.Equals(Text(track, "codecs"), "ec-3", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Text(track, "codecs"), "ac-3", StringComparison.OrdinalIgnoreCase);

        internal static bool IsFlacAudio(JToken track)
            => string.Equals(Text(track, "codecs"), "flac", StringComparison.OrdinalIgnoreCase);

        internal static bool IsHdr10(JToken track)
        {
            if (track == null) return false;
            // CICP transfer 16 is PQ; 18 is HLG and must never be relabelled
            // PQ. BT.2020 describes gamut, not dynamic range. The qn 125
            // contract can supply absent metadata, but cannot override an
            // explicit conflicting transfer, gamut, format or bit depth.
            if (HasHdr10Conflict(track)) return false;
            return Number(track, "id") == 125 || HasPqTransfer(track)
                || Values(track, HdrFields).Any(IsHdr10Label);
        }

        private static readonly string[] HdrFields = { "hdr_type", "hdrType", "hdr", "format" };
        private static readonly string[] TransferFields = { "transfer_characteristics", "transferCharacteristics",
            "color_transfer", "colour_transfer", "colorTransfer" };
        private static readonly string[] PrimariesFields = { "color_primaries", "colour_primaries", "colorPrimaries" };
        private static readonly string[] MatrixFields = { "matrix_coefficients", "matrixCoefficients",
            "color_space", "colour_space", "colorSpace" };
        private static readonly string[] DepthFields = { "bits_per_channel", "bitsPerChannel", "bit_depth", "bitDepth", "bpc" };

        private static bool HasHdr10Conflict(JToken track)
        {
            var all = Values(track, HdrFields.Concat(TransferFields).Concat(PrimariesFields)
                .Concat(MatrixFields).Concat(new[] { "codecs" }));
            if (Number(track, "id") == 126 || all.Any(value => ContainsAny(value, "dolby", "dvhe", "dvh1", "dva1", "dvav", "hlg", "arib", "hdr10+")))
                return true;
            // Unknown CICP value 2 / "unspecified" conveys no contradiction.
            if (Values(track, TransferFields).Any(value => !IsUnspecified(value) && !IsPq(value))) return true;
            if (Values(track, PrimariesFields).Any(value => !IsUnspecified(value) && !IsBt2020Primaries(value))) return true;
            if (Values(track, MatrixFields).Any(value => !IsUnspecified(value) && !IsBt2020Matrix(value))) return true;
            if (Values(track, DepthFields).Any(value => value != "10")) return true;
            return Values(track, HdrFields).Any(value => value == "0" || value == "false" || value == "sdr");
        }

        private static bool HasUnsupportedHdr(JToken track)
        {
            if (track == null || IsHdr10(track)) return false;
            // Do not select HLG, Dolby Vision or contradictory HDR streams as
            // if they were SDR. Plain BT.2020 SDR remains a valid SDR source.
            return Number(track, "id") == 125 || Number(track, "id") == 126
                || Values(track, HdrFields.Concat(TransferFields).Concat(new[] { "codecs" }))
                    .Any(value => IsHdr10Label(value) || IsPq(value)
                        || ContainsAny(value, "dolby", "dvhe", "dvh1", "dva1", "dvav", "hlg", "arib", "hdr")
                        || value == "18")
                || Values(track, HdrFields.Take(3)).Any(value => value == "1" || value == "true");
        }

        private static IEnumerable<string> Values(JToken track, IEnumerable<string> fields)
            => fields.Select(name => Text(track, name).Trim().ToLowerInvariant()).Where(value => value.Length > 0);

        private static bool HasPqTransfer(JToken track) => Values(track, TransferFields).Any(IsPq);
        private static bool IsPq(string value)
            => value == "16" || value == "pq" || value == "smpte2084" || value == "smpte-st-2084" || value == "smpte st 2084";
        private static bool IsHdr10Label(string value)
            => value == "hdr10" || value == "hdr10_4k" || value == "hdr10 4k";
        private static bool IsUnspecified(string value) => value == "2" || value == "unspecified" || value == "unknown";
        private static bool IsBt2020Primaries(string value) => value == "9" || value == "bt2020" || value == "bt.2020";
        private static bool IsBt2020Matrix(string value)
            => value == "9" || value == "bt2020" || value == "bt.2020" || value == "bt2020nc" || value == "bt2020_ncl";

        internal static string Url(JToken track)
            => NormalizeUrl(Text(track, "baseUrl", "base_url"));

        internal static IReadOnlyList<string> Urls(JToken track)
        {
            var urls = new List<string>();
            var primary = Url(track);
            if (primary.Length > 0) urls.Add(primary);
            foreach (var name in new[] { "backupUrl", "backup_url" })
                foreach (var value in track?[name] as JArray ?? new JArray())
                {
                    if (value.Type != JTokenType.String) continue;
                    var url = NormalizeUrl(value.Value<string>());
                    if (url.Length > 0 && !urls.Contains(url)) urls.Add(url);
                }
            return urls;
        }

        private static string NormalizeUrl(string value)
        {
            value = value ?? "";
            if (value.StartsWith("//", StringComparison.Ordinal)) value = "https:" + value;
            if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) value = "https://" + value.Substring(7);
            Uri uri;
            return Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Scheme == "https"
                && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 ? uri.AbsoluteUri : "";
        }

        internal static string Build(JToken video, JToken audio, double duration)
        {
            if (!IsSupportedVideo(video) || !IsSupportedAudio(audio) || double.IsNaN(duration)
                || double.IsInfinity(duration) || duration <= 0 || duration > 7 * 24 * 60 * 60)
                throw new BilibiliApiException(-1, "视频或音频分段信息不完整，无法建立同步播放源。");
            var length = "PT" + duration.ToString("0.###", CultureInfo.InvariantCulture) + "S";
            var manifest = new XDocument(new XElement(Ns + "MPD",
                new XAttribute("type", "static"),
                new XAttribute("profiles", "urn:mpeg:dash:profile:isoff-on-demand:2011"),
                new XAttribute("minBufferTime", "PT3S"),
                new XAttribute("mediaPresentationDuration", length),
                new XElement(Ns + "Period", new XAttribute("id", "0"), new XAttribute("start", "PT0S"),
                    new XAttribute("duration", length), Adaptation(video, "video", "0"), Adaptation(audio, "audio", "1"))));
            return manifest.ToString(SaveOptions.DisableFormatting);
        }

        private static XElement Adaptation(JToken track, string kind, string id)
        {
            var segment = track["SegmentBase"] ?? track["segment_base"];
            var representation = new XElement(Ns + "Representation",
                new XAttribute("id", kind + "-" + Text(track, "id")),
                new XAttribute("bandwidth", Text(track, "bandwidth")),
                new XAttribute("codecs", kind == "audio" && IsFlacAudio(track) ? "fLaC" : Text(track, "codecs")),
                new XElement(Ns + "BaseURL", Url(track)),
                new XElement(Ns + "SegmentBase", new XAttribute("indexRange", Text(segment, "indexRange", "index_range")),
                    new XAttribute("indexRangeExact", "true"),
                    new XElement(Ns + "Initialization", new XAttribute("range", Text(segment, "Initialization", "initialization")))));
            if (kind == "video")
            {
                AddNumber(representation, track, "width");
                AddNumber(representation, track, "height");
                var rate = Text(track, "frameRate", "frame_rate");
                if (ValidRate(rate)) representation.Add(new XAttribute("frameRate", rate));
                if (IsHdr10(track))
                {
                    // Describe only established colorimetry; a PQ transfer
                    // alone cannot establish the source primaries or matrix.
                    bool declaredHdr10 = Number(track, "id") == 125 || Values(track, HdrFields).Any(IsHdr10Label);
                    if (declaredHdr10 || Values(track, PrimariesFields).Any(IsBt2020Primaries))
                        AddColorProperty(representation, "ColourPrimaries", "9");
                    AddColorProperty(representation, "TransferCharacteristics", "16");
                    if (declaredHdr10 || Values(track, MatrixFields).Any(IsBt2020Matrix))
                        AddColorProperty(representation, "MatrixCoefficients", "9");
                }
            }
            var adaptation = new XElement(Ns + "AdaptationSet", new XAttribute("id", id),
                new XAttribute("contentType", kind), new XAttribute("mimeType", kind + "/mp4"),
                new XAttribute("segmentAlignment", "true"), new XAttribute("startWithSAP", "1"));
            if (kind == "audio")
            {
                adaptation.Add(new XElement(Ns + "Role", new XAttribute("schemeIdUri", "urn:mpeg:dash:role:2011"), new XAttribute("value", "main")));
                int sampleRate;
                if (int.TryParse(Text(track, "audioSamplingRate", "sample_rate", "sampleRate", "sampling_rate"), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out sampleRate) && sampleRate > 0 && sampleRate <= 1048575)
                    representation.Add(new XAttribute("audioSamplingRate", sampleRate));
                int channels;
                if (int.TryParse(Text(track, "channels", "channel_count", "channelCount"), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out channels) && channels > 0 && channels <= 32)
                    representation.Add(new XElement(Ns + "AudioChannelConfiguration",
                        new XAttribute("schemeIdUri", "urn:mpeg:dash:23003:3:audio_channel_configuration:2011"),
                        new XAttribute("value", channels)));
            }
            adaptation.Add(representation);
            return adaptation;
        }

        private static bool HasMedia(JToken track)
        {
            if (track == null || Url(track).Length == 0) return false;
            var segment = track["SegmentBase"] ?? track["segment_base"];
            long bandwidth;
            return long.TryParse(Text(track, "bandwidth"), NumberStyles.Integer, CultureInfo.InvariantCulture, out bandwidth)
                && bandwidth > 0 && Range(Text(segment, "Initialization", "initialization"))
                && Range(Text(segment, "indexRange", "index_range"));
        }

        private static bool Range(string text)
        {
            var parts = text.Split('-');
            long start, end;
            return parts.Length == 2 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out start)
                && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out end) && start >= 0 && end >= start;
        }

        private static bool ValidRate(string value)
        {
            var parts = value.Split('/');
            if (parts.Length < 1 || parts.Length > 2) return false;
            double numerator, denominator = 1;
            if (!double.TryParse(parts[0], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out numerator)
                || (parts.Length == 2 && !double.TryParse(parts[1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out denominator))
                || double.IsNaN(numerator) || double.IsInfinity(numerator) || numerator <= 0
                || double.IsNaN(denominator) || double.IsInfinity(denominator) || denominator <= 0) return false;
            // A suspicious API estimate is not a trustworthy MPD frame-rate
            // declaration. Leave it absent so native timestamps/codec metadata
            // can establish the rate; never replace it with a fabricated 60.
            return numerator / denominator <= 60.001;
        }

        private static void AddNumber(XElement element, JToken track, string name)
        {
            int number;
            if (int.TryParse(Text(track, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) && number > 0)
                element.Add(new XAttribute(name, number));
        }

        private static void AddColorProperty(XElement element, string name, string value)
        {
            element.Add(new XElement(Ns + "SupplementalProperty",
                new XAttribute("schemeIdUri", "urn:mpeg:mpegB:cicp:" + name),
                new XAttribute("value", value)));
        }

        private static bool ContainsAny(string value, params string[] markers)
        {
            return markers.Any(marker => value.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static long Number(JToken item, string name)
        {
            long value;
            return long.TryParse(Text(item, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : 0;
        }

        private static string Text(JToken item, params string[] names)
        {
            foreach (var name in names)
            {
                var text = item?[name]?.ToString();
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
            return "";
        }
    }
}
