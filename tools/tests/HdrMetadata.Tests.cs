// Actual production DASH builder; no network or hardware capability claims.
namespace BiliXbox.HdrMetadataTests
{
    using BiliXbox.Services;
    public static class Tests
    {
        private static int _passed;
        private static readonly XNamespace Ns = "urn:mpeg:dash:schema:mpd:2011";
        private static void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("FAIL: " + name);
            _passed++;
        }
        private static JObject Video(int quality = 80) => new JObject
        {
            ["id"] = quality, ["codecs"] = "hvc1.2.4.L153.B0", ["bandwidth"] = 12000000,
            ["width"] = 3840, ["height"] = 2160, ["frameRate"] = "60000/1001",
            ["baseUrl"] = "https://media.example.test/video.m4s?token=public-fixture",
            ["SegmentBase"] = new JObject { ["Initialization"] = "0-999", ["indexRange"] = "1000-1999" }
        };
        private static JObject Audio() => new JObject
        {
            ["id"] = 30280, ["codecs"] = "mp4a.40.2", ["bandwidth"] = 128000,
            ["baseUrl"] = "https://media.example.test/audio.m4s",
            ["SegmentBase"] = new JObject { ["Initialization"] = "0-499", ["indexRange"] = "500-999" }
        };
        private static void RejectHdr(JObject video, string name)
        {
            Check(!DashManifestBuilder.IsHdr10(video), name + " is not relabelled HDR10");
            Check(!DashManifestBuilder.IsSupportedVideo(video), name + " is not selected as SDR");
            try { DashManifestBuilder.Build(video, Audio(), 97); throw new Exception("FAIL: unsupported HDR reached MPD"); }
            catch (BilibiliApiException) { Check(true, name + " cannot receive invented PQ metadata"); }
        }
        private static void AcceptHdr(JObject video, string name, int expectedColors = 3)
        {
            string before = video.ToString(Newtonsoft.Json.Formatting.None);
            Check(DashManifestBuilder.IsHdr10(video) && DashManifestBuilder.IsSupportedVideo(video), name + " retains confirmed HDR10");
            var xml = XDocument.Parse(DashManifestBuilder.Build(video, Audio(), 97));
            var representation = xml.Descendants(Ns + "Representation").First();
            var colors = representation.Elements(Ns + "SupplementalProperty").ToDictionary(
                item => (string)item.Attribute("schemeIdUri"), item => (string)item.Attribute("value"));
            Check(colors.Count == expectedColors && colors["urn:mpeg:mpegB:cicp:TransferCharacteristics"] == "16"
                && (expectedColors != 3 || colors["urn:mpeg:mpegB:cicp:ColourPrimaries"] == "9"
                    && colors["urn:mpeg:mpegB:cicp:MatrixCoefficients"] == "9")
                && (expectedColors != 1 || !colors.ContainsKey("urn:mpeg:mpegB:cicp:ColourPrimaries")
                    && !colors.ContainsKey("urn:mpeg:mpegB:cicp:MatrixCoefficients")), name + " advertises only established colorimetry");
            Check((string)representation.Attribute("codecs") == (string)video["codecs"]
                && (string)representation.Attribute("width") == "3840"
                && (string)representation.Element(Ns + "BaseURL") == (string)video["baseUrl"],
                name + " retains actual codec, pixels and media URL");
            Check(video.ToString(Newtonsoft.Json.Formatting.None) == before, name + " does not rewrite source metadata");
        }
        public static string Run()
        {
            _passed = 0;
            var video = Video(125); AcceptHdr(video, "platform HDR10 tier");
            foreach (var alias in new[] { "transfer_characteristics", "transferCharacteristics", "color_transfer", "colour_transfer", "colorTransfer" })
            {
                var pq = Video(); pq[alias] = "16"; pq["color_primaries"] = 9; pq["matrix_coefficients"] = 9;
                AcceptHdr(pq, "CICP PQ " + alias);
                var hlg = Video(125); hlg[alias] = 18; RejectHdr(hlg, "CICP HLG " + alias);
                var sdr = Video(125); sdr[alias] = 1; RejectHdr(sdr, "explicit SDR contradicts platform HDR tier " + alias);
            }
            foreach (var value in new[] { "pq", "smpte2084", "smpte-st-2084", "SMPTE ST 2084" })
            {
                var pq = Video(); pq["transfer_characteristics"] = value; AcceptHdr(pq, "PQ label without gamut assumptions " + value, 1);
            }
            foreach (var value in new[] { "HDR10", "hdr10 4k", "hdr10_4k" })
            {
                var hdr = Video(); hdr["hdr_type"] = value; AcceptHdr(hdr, "explicit format " + value);
            }
            foreach (var value in new[] { "hlg", "arib-std-b67", "dolby vision", "HDR10+" })
            {
                var bad = Video(125); bad["hdr_type"] = value; RejectHdr(bad, "unsupported format " + value);
            }
            var dvTier = Video(126); RejectHdr(dvTier, "Dolby Vision tier");
            var dvCodec = Video(125); dvCodec["codecs"] = "dvhe.05.06"; RejectHdr(dvCodec, "Dolby Vision bitstream");
            foreach (var field in new[] { "hdr", "hdr_type", "hdrType" })
            {
                foreach (var value in new[] { "1", "true", "hdr" })
                {
                    var generic = Video(); generic[field] = value; RejectHdr(generic, "unproved generic HDR " + field + "=" + value);
                }
            }
            foreach (var field in new[] { "color_primaries", "colour_primaries", "colorPrimaries", "color_space", "colour_space", "colorSpace" })
            {
                var wcg = Video(); wcg[field] = "bt2020";
                Check(!DashManifestBuilder.IsHdr10(wcg) && DashManifestBuilder.IsSupportedVideo(wcg), "wide gamut alone remains SDR " + field);
                Check(!XDocument.Parse(DashManifestBuilder.Build(wcg, Audio(), 97)).Descendants(Ns + "SupplementalProperty").Any(),
                    "wide gamut SDR does not gain PQ metadata " + field);
            }
            foreach (var field in new[] { "color_primaries", "colorPrimaries", "matrix_coefficients", "matrixCoefficients" })
            {
                var conflict = Video(125); conflict[field] = 1; RejectHdr(conflict, "BT709 contradicts BT2020 " + field);
            }
            foreach (var field in new[] { "bits_per_channel", "bitsPerChannel", "bit_depth", "bitDepth", "bpc" })
            {
                var main8 = Video(125); main8[field] = 8; RejectHdr(main8, "8-bit metadata " + field);
            }
            var contradictory = Video(125); contradictory["transfer_characteristics"] = 16;
            contradictory["colorTransfer"] = 18; RejectHdr(contradictory, "contradictory transfer aliases");
            var unspecified = Video(125); unspecified["transfer_characteristics"] = 2; AcceptHdr(unspecified, "unspecified CICP with authoritative HDR tier");
            Check(!DashManifestBuilder.IsHdr10(null) && !DashManifestBuilder.IsSupportedVideo(null), "absent video fails safely");
            return "PASS: " + _passed + " HDR metadata assertions using production DASH builder. Hardware HDR output is not established.";
        }
    }
}
