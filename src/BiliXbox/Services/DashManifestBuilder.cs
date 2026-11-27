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

        // Set from the playback view: true while the Xbox HDMI connection has
        // offered a Dolby Vision low-latency output mode in this session.
        internal static bool DolbyVisionOutputAvailable { get; set; }

        // Bilibili qn 126 is Dolby Vision. Only tracks whose codec string names
        // Dolby Vision itself are offered: Profile 5 (dvh1/dvhe, verified on
        // Xbox Series S), and the AVC/AV1 profiles (dva1/dvav/dav1) if they
        // appear. Profile 8.x (plain hvc1/hev1 with a dvvC box) is handled by
        // IsDolbyVisionBaseLayer: Xbox rejects it as Dolby Vision (DecodingError
        // 0xC00D36B4, "星云" demo, 2026-11-26), so its base layer is played.
        internal static bool IsDolbyVision(JToken track)
        {
            if (track == null || Number(track, "id") != 126) return false;
            var codec = Text(track, "codecs");
            if (IsProfile8DolbyVisionCodec(codec)) return false;
            return codec.StartsWith("dvh1.", StringComparison.OrdinalIgnoreCase)
                || codec.StartsWith("dvhe.", StringComparison.OrdinalIgnoreCase)
                || codec.StartsWith("dva1.", StringComparison.OrdinalIgnoreCase)
                || codec.StartsWith("dvav.", StringComparison.OrdinalIgnoreCase)
                || codec.StartsWith("dav1.", StringComparison.OrdinalIgnoreCase);
        }

        // Bilibili qn 126 delivered as a plain hvc1/hev1 string is Dolby Vision
        // Profile 8.x (dvvC box in the init segment).
        internal static bool IsDolbyVisionProfile8(JToken track)
        {
            if (track == null || Number(track, "id") != 126) return false;
            var codec = Text(track, "codecs");
            return codec.StartsWith("hvc1.", StringComparison.OrdinalIgnoreCase)
                || codec.StartsWith("hev1.", StringComparison.OrdinalIgnoreCase)
                || IsProfile8DolbyVisionCodec(codec);
        }

        // Bilibili also labels Profile 8 tracks "dvh1.08.xx"/"dvhe.08.xx" while
        // the file is an hvc1 sample entry with a dvvC box (BV1nu411A7pz:
        // compat=4), so the HEVC base-layer path applies to them too.
        internal static bool IsProfile8DolbyVisionCodec(string codec)
            => codec != null && (codec.StartsWith("dvh1.08", StringComparison.OrdinalIgnoreCase)
                || codec.StartsWith("dvhe.08", StringComparison.OrdinalIgnoreCase));

        // Result of reading a Profile 8 track's initialization segment: the
        // dvvC/dvcC base-layer compatibility id (1 = HDR10, 2 = SDR, 4 = HLG).
        internal sealed class DolbyVisionCompatibilityCheck { internal int Compatibility = -1; internal bool HlgPatchable { get; set; } }

        // Profile 8.1 (HDR10 base layer) plays its base layer as HDR10. Xbox
        // cannot decode an HLG-signalled stream (8.4): DecodingError 0xC00D36B4
        // under HDR output, and SDR output hung the media pipeline ("星云"
        // demo, 2026-11-26). HLG is designed to be viewable as SDR, so when the
        // SPS transfer_characteristics byte can be located exactly, it is
        // rewritten to BT.2020 SDR in memory and the base layer plays in SDR.
        internal static bool IsDolbyVisionBaseLayer(JToken track)
        {
            var check = IsDolbyVisionProfile8(track) ? track.Annotation<DolbyVisionCompatibilityCheck>() : null;
            return check != null && (check.Compatibility == 1 || (check.Compatibility == 4 && check.HlgPatchable));
        }

        internal static bool IsDolbyVisionHlgBaseLayer(JToken track)
            => IsDolbyVisionBaseLayer(track) && track.Annotation<DolbyVisionCompatibilityCheck>().Compatibility == 4;

        // A 2-byte in-place patch that turns the SPS VUI transfer_characteristics
        // of an HLG stream (18) into BT.2020 10-bit (14). The colour description
        // must read primaries 9, transfer 18, matrix 9 at exactly one bit
        // position of exactly one SPS in hvcC, and the change must not create an
        // emulation-prevention start-code pattern. Otherwise null.
        internal static KeyValuePair<long, byte[]>? FindHlgTransferPatch(byte[] init)
        {
            if (init == null) return null;
            KeyValuePair<long, byte[]>? found = null;
            for (int box = 4; box + 4 <= init.Length; ++box)
            {
                if (init[box] != (byte)'h' || init[box + 1] != (byte)'v' || init[box + 2] != (byte)'c' || init[box + 3] != (byte)'C') continue;
                long boxSize = (long)init[box - 4] << 24 | (long)init[box - 3] << 16 | (long)init[box - 2] << 8 | init[box - 1];
                long body = box + 4, end = Math.Min(init.Length, box - 4 + boxSize);
                if (boxSize < 31 || body + 23 > end) continue;
                long at = body + 23;
                int arrays = init[body + 22];
                for (int a = 0; a < arrays && at + 3 <= end; ++a)
                {
                    int type = init[at] & 0x3F;
                    int count = init[at + 1] << 8 | init[at + 2];
                    at += 3;
                    for (int n = 0; n < count && at + 2 <= end; ++n)
                    {
                        int length = init[at] << 8 | init[at + 1];
                        at += 2;
                        if (at + length > end) return null;
                        if (type == 33)
                        {
                            var patch = FindTransferInSps(init, at, length);
                            if (patch == null) return null;
                            if (found != null) return null;
                            found = patch;
                        }
                        at += length;
                    }
                }
            }
            return found;
        }

        private static KeyValuePair<long, byte[]>? FindTransferInSps(byte[] data, long start, int length)
        {
            KeyValuePair<long, byte[]>? found = null;
            long bits = (long)length * 8;
            for (long p = 16; p + 24 <= bits; ++p)
            {
                if (ReadBits(data, start, p, 8) != 9 || ReadBits(data, start, p + 8, 8) != 18 || ReadBits(data, start, p + 16, 8) != 9) continue;
                if (found != null) return null;
                long first = start + (p + 8) / 8;
                if (first + 2 > start + length) return null;
                int shift = (int)((p + 8) % 8);
                int window = data[first] << 8 | data[first + 1];
                int replaced = window & ~(0xFF << (8 - shift)) | (14 << (8 - shift));
                var bytes = new[] { (byte)(replaced >> 8), (byte)replaced };
                // Refuse when the bytes around the change could form or break
                // a 0x000000-0x000003 emulation-prevention pattern.
                for (long i = Math.Max(start, first - 2); i <= Math.Min(start + length - 3, first + 1); ++i)
                {
                    byte b0 = i == first ? bytes[0] : i == first + 1 ? bytes[1] : data[i];
                    byte b1 = i + 1 == first ? bytes[0] : i + 1 == first + 1 ? bytes[1] : data[i + 1];
                    byte b2 = i + 2 == first ? bytes[0] : i + 2 == first + 1 ? bytes[1] : data[i + 2];
                    byte o0 = data[i], o1 = data[i + 1], o2 = data[i + 2];
                    if ((b0 == 0 && b1 == 0 && b2 <= 3) || (o0 == 0 && o1 == 0 && o2 <= 3)) return null;
                }
                found = new KeyValuePair<long, byte[]>(first, bytes);
            }
            return found;
        }

        private static int ReadBits(byte[] data, long start, long bit, int count)
        {
            int value = 0;
            for (int i = 0; i < count; ++i)
            {
                long b = bit + i;
                value = value << 1 | (data[start + b / 8] >> (7 - (int)(b % 8)) & 1);
            }
            return value;
        }

        // Base-layer compatibility id from the first Dolby Vision configuration
        // box inside moov, or -1 when none is found.
        internal static int DolbyVisionCompatibility(byte[] init)
        {
            if (init == null) return -1;
            for (int i = 4; i + 9 <= init.Length; ++i)
            {
                if (init[i] != (byte)'d' || init[i + 1] != (byte)'v' || (init[i + 2] != (byte)'v' && init[i + 2] != (byte)'c' && init[i + 2] != (byte)'w')
                    || init[i + 3] != (byte)'C') continue;
                long size = (long)init[i - 4] << 24 | (long)init[i - 3] << 16 | (long)init[i - 2] << 8 | init[i - 1];
                if (size < 24 || size > 64) continue;
                return init[i + 8] >> 4;
            }
            return -1;
        }

        internal static string VideoRejectionReason(JToken track)
        {
            if (IsDolbyVision(track))
                return !HasMedia(track) ? "缺少有效媒体地址或初始化/索引分段"
                    : DolbyVisionOutputAvailable ? "" : "电视或 Xbox 当前未提供杜比视界输出模式";
            // Profile 8.1: played as its HDR10 base layer with the Dolby Vision
            // configuration box hidden. Other Profile 8 base layers are hidden.
            if (IsDolbyVisionBaseLayer(track))
                return HasMedia(track) ? "" : "缺少有效媒体地址或初始化/索引分段";
            if (IsDolbyVisionProfile8(track))
                return "此杜比视界视频的基础层为 HLG 或未确认，Xbox 无法解码";
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

        // Retain the exact server-provided audio representation and SegmentBase.
        // A background audio source must not keep a 4K video decoder alive.
        internal static string AudioOnly(string manifest)
        {
            XDocument document;
            using (var text = new System.IO.StringReader(manifest ?? ""))
            using (var reader = System.Xml.XmlReader.Create(text, new System.Xml.XmlReaderSettings
                { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null }))
                document = XDocument.Load(reader);
            var period = document.Root?.Element(Ns + "Period");
            var audio = period?.Elements(Ns + "AdaptationSet")
                .Where(item => (string)item.Attribute("contentType") == "audio").ToArray();
            if (audio == null || audio.Length != 1 || audio[0].Elements(Ns + "Representation").Count() != 1)
                throw new InvalidOperationException("没有可用于后台播放的独立音轨。");
            foreach (var item in period.Elements(Ns + "AdaptationSet").Where(item => item != audio[0]).ToArray()) item.Remove();
            return document.ToString(SaveOptions.DisableFormatting);
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
