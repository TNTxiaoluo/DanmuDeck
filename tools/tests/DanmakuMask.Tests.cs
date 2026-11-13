namespace BiliXbox.MaskTests
{
    public static class Tests
    {
        private static int _checks;
        private static void Check(bool value, string text) { if (!value) throw new Exception(text); ++_checks; }
        private static void Reject(Action action) { try { action(); throw new Exception("invalid mask accepted"); } catch (Exception ex) when (ex is InvalidDataException || ex is XmlException || ex is FormatException) { ++_checks; } }
        private static void U32(byte[] bytes, int position, uint value) { bytes[position] = (byte)(value >> 24); bytes[position + 1] = (byte)(value >> 16); bytes[position + 2] = (byte)(value >> 8); bytes[position + 3] = (byte)value; }
        private static byte[] Frame(string svg, uint milliseconds)
        {
            byte[] text = Encoding.UTF8.GetBytes("data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg)));
            var bytes = new byte[12 + text.Length]; U32(bytes, 0, (uint)text.Length); U32(bytes, 8, milliseconds); Array.Copy(text, 0, bytes, 12, text.Length); return bytes;
        }
        private static byte[] Compress(byte[] input)
        {
            using (var output = new MemoryStream())
            {
                using (var zip = new GZipStream(output, CompressionLevel.Fastest, true)) zip.Write(input, 0, input.Length);
                return output.ToArray();
            }
        }
        public static string Run()
        {
            var header = new byte[16]; Array.Copy(Encoding.ASCII.GetBytes("MASK"), header, 4); header[8] = 2; U32(header, 12, 2);
            Check(Services.WebMaskParser.ReadHeader(header) == 2, "MASK header");
            var wrong = (byte[])header.Clone(); wrong[0] = 0; Reject(() => Services.WebMaskParser.ReadHeader(wrong));
            Reject(() => Services.WebMaskParser.ReadHeader(new byte[12]));
            var index = new byte[32]; U32(index, 12, 48); U32(index, 20, 1000); U32(index, 28, 100);
            var entries = Services.WebMaskParser.ReadIndex(index, 2, 300);
            Check(entries[0].Offset == 48 && entries[1].Time == TimeSpan.FromSeconds(1), "64-bit big endian index");
            var badIndex = (byte[])index.Clone(); U32(badIndex, 28, 40); Reject(() => Services.WebMaskParser.ReadIndex(badIndex, 2, 300));
            const string svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 320 180\"><g transform=\"translate(0,180) scale(.1,-.1)\"><path d=\"M0 0 H3200 V1800 H0 Z M100 100 V300 H300 V100 Z\" fill-rule=\"evenodd\"/></g></svg>";
            byte[] raw = Frame(svg, 0).Concat(Frame(svg, 500)).ToArray();
            var frames = Services.WebMaskParser.ReadBlock(Compress(raw), TimeSpan.FromSeconds(1), 24);
            Check(frames.Count == 2 && frames[0].Svg == svg, "gzip SVG timeline");
            Check(frames[0].EndTime == TimeSpan.FromMilliseconds(500) && frames[1].EndTime == TimeSpan.FromSeconds(1), "frame lifetime boundaries");
            var warmup = Services.WebMaskParser.ReadBlock(Compress(Frame("<svg/>",0).Concat(Frame(svg,0)).Concat(Frame(svg,33)).ToArray()), TimeSpan.FromSeconds(1), 30);
            Check(warmup.Count == 2 && warmup[0].Svg == svg && warmup[0].EndTime == TimeSpan.FromMilliseconds(33), "official duplicate warm-up timestamps keep last shape");
            Reject(() => Services.WebMaskParser.ReadBlock(Compress(Frame("<!DOCTYPE svg [<!ENTITY x SYSTEM 'file:///secret'>]><svg>&x;</svg>", 0)), TimeSpan.FromSeconds(1), 24));
            Reject(() => Services.WebMaskParser.ReadBlock(Compress(Frame("<svg><script>x</script></svg>", 0)), TimeSpan.FromSeconds(1), 24));
            Reject(() => Services.WebMaskParser.ReadBlock(Compress(Frame("<svg><image href='https://example.com'/></svg>", 0)), TimeSpan.FromSeconds(1), 24));
            Reject(() => Services.WebMaskParser.ReadBlock(Compress(Frame(svg, 500).Concat(Frame(svg, 100)).ToArray()), TimeSpan.FromSeconds(1), 24));
            Reject(() => Services.WebMaskParser.ReadBlock(new byte[8], TimeSpan.FromSeconds(1), 24));
            var document = Controls.SvgMaskDocument.Parse(svg);
            Check(document.Width == 320 && document.Height == 180 && document.Paths.Count == 1, "native clipping viewport");
            Check(document.Paths[0].EvenOdd && document.Paths[0].TransformIds.Length == 1, "mask holes and ancestor transforms preserved");
            var sanitized = Controls.SvgMaskDocument.Parse("<svg viewBox='0 0 1 1' onload='x()'><path d='M0 0 H1 V1 Z' style='fill:url(https://example.com)'/></svg>");
            Check(!sanitized.Xml.Contains("onload") && !sanitized.Xml.Contains("url("), "native SVG input excludes external resources");
            Reject(() => Controls.SvgMaskDocument.Parse("<svg viewBox='0 0 0 1'/>"));
            Reject(() => Controls.SvgMaskDocument.Parse("<svg><foreignObject/></svg>"));
            Reject(() => Controls.SvgMaskDocument.Parse("<!DOCTYPE svg SYSTEM 'file:///secret'><svg/>"));
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                try { Services.WebMaskParser.ReadBlock(Compress(raw), TimeSpan.FromSeconds(1), 24, cts.Token); throw new Exception("canceled mask decode ran"); }
                catch (OperationCanceledException) { ++_checks; }
            }
            return "PASS: " + _checks + " mask binary, gzip, timeline, SVG geometry and input-boundary assertions; GPU clipping still needs Xbox validation.";
        }
        public static string CheckPublicSvg(string svg, byte[] compressed)
        {
            var parsed = Controls.SvgMaskDocument.Parse(svg);
            var frames = Services.WebMaskParser.ReadBlock(compressed, TimeSpan.FromSeconds(10), 24);
            Check(parsed.Paths.Count > 0 && parsed.Width > 0 && parsed.Height > 0, "real SVG paths and viewport");
            Check(frames.Count > 0 && frames[0].Time == TimeSpan.Zero && frames[0].Svg.StartsWith("<svg"), "production parser decodes public SVG timeline after warm-up deduplication");
            Check(frames[0].Time <= frames[frames.Count-1].Time, "public timeline ordered");
            foreach(var frame in frames) Check(Controls.SvgMaskDocument.Parse(frame.Svg).Paths.Count <= 128,"real SVG supported by clip parser");
            return "PASS: production parser decoded " + frames.Count + " real public SVG frames; each frame passes native clip input parsing. No Xbox GPU validation inferred.";
        }
    }
}
