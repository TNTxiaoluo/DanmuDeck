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
            var dvTier = Video(126); Check(!DashManifestBuilder.IsHdr10(dvTier), "Dolby Vision tier is not relabelled HDR10");
            Check(DashManifestBuilder.IsDolbyVisionProfile8(dvTier) && !DashManifestBuilder.IsDolbyVisionBaseLayer(dvTier)
                && !DashManifestBuilder.IsSupportedVideo(dvTier) && !DashManifestBuilder.IsDolbyVision(dvTier),
                "an unverified Profile 8 Dolby Vision tier stays hidden");
            var dvh108 = Video(126); dvh108["codecs"] = "dvh1.08.08";
            var dvh105 = Video(126); dvh105["codecs"] = "dvh1.05.06";
            Check(DashManifestBuilder.IsDolbyVisionProfile8(dvh108) && !DashManifestBuilder.IsDolbyVision(dvh108)
                && DashManifestBuilder.IsDolbyVision(dvh105) && !DashManifestBuilder.IsDolbyVisionProfile8(dvh105),
                "dvh1.08 is the Profile 8 base-layer path; dvh1.05 stays Profile 5");
            var hlgTier = Video(126); hlgTier.AddAnnotation(new DashManifestBuilder.DolbyVisionCompatibilityCheck { Compatibility = 4 });
            Check(!DashManifestBuilder.IsDolbyVisionBaseLayer(hlgTier) && !DashManifestBuilder.IsSupportedVideo(hlgTier),
                "a Profile 8.4 (HLG base) tier stays hidden");
            var hdr10Tier = Video(126); hdr10Tier.AddAnnotation(new DashManifestBuilder.DolbyVisionCompatibilityCheck { Compatibility = 1 });
            Check(DashManifestBuilder.IsDolbyVisionBaseLayer(hdr10Tier) && DashManifestBuilder.IsSupportedVideo(hdr10Tier),
                "a Profile 8.1 (HDR10 base) tier plays through the base-layer path");
            var dvvC = new byte[] { 0, 0, 0, 32, (byte)'d', (byte)'v', (byte)'v', (byte)'C', 1, 0, 16, 63, 0x40, 0, 0, 0 };
            Check(DashManifestBuilder.DolbyVisionCompatibility(dvvC) == 4 && DashManifestBuilder.DolbyVisionCompatibility(new byte[16]) == -1,
                "the dvvC base-layer compatibility id is read from the init segment");
            // Synthetic hvcC: one SPS whose VUI colour description (9, 18, 9)
            // starts 3 bits into its 6th byte.
            var sps = new byte[] { 0x42, 0x01, 0x01, 0x22, 0x40, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
            int colourBit = 5 * 8 + 3; int[] colour = { 9, 18, 9 };
            for (int c = 0; c < 3; ++c) for (int b = 0; b < 8; ++b)
                if ((colour[c] >> (7 - b) & 1) != 0) { int bit = colourBit + c * 8 + b; sps[bit / 8] |= (byte)(0x80 >> (bit % 8)); }
            sps[sps.Length - 1] = 0x80;
            var hvcc = new System.Collections.Generic.List<byte> { 0, 0, 0, 0, (byte)'h', (byte)'v', (byte)'c', (byte)'C' };
            hvcc.AddRange(new byte[22]); hvcc.Add(1); hvcc.Add(0xA1); hvcc.Add(0); hvcc.Add(1); hvcc.Add(0); hvcc.Add((byte)sps.Length); hvcc.AddRange(sps);
            hvcc[3] = (byte)hvcc.Count;
            var init = new byte[8].Concat(hvcc).ToArray();
            var patch = DashManifestBuilder.FindHlgTransferPatch(init);
            Check(patch.HasValue, "the HLG transfer byte is located in the SPS colour description");
            var patched = (byte[])init.Clone(); Buffer.BlockCopy(patch.Value.Value, 0, patched, (int)patch.Value.Key, 2);
            int spsStart = init.Length - sps.Length, tBit = colourBit + 8, transfer = 0;
            for (int b = 0; b < 8; ++b) transfer = transfer << 1 | (patched[spsStart + (tBit + b) / 8] >> (7 - (tBit + b) % 8) & 1);
            Check(transfer == 14 && patched.Where((v, i) => v != init[i]).Count() <= 2, "only the transfer field changes, from HLG (18) to BT.2020 SDR (14)");
            var noHlg = (byte[])init.Clone(); Buffer.BlockCopy(patch.Value.Value, 0, noHlg, (int)patch.Value.Key, 2);
            Check(!DashManifestBuilder.FindHlgTransferPatch(noHlg).HasValue, "a stream without an HLG transfer byte is never patched");
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
