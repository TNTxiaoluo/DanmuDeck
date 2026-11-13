using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml;
using System.Xml.Linq;
using BiliXbox.Models;

namespace BiliXbox.Services
{
    internal sealed class WebMaskIndexEntry
    {
        public TimeSpan Time { get; set; }
        public long Offset { get; set; }
    }

    // Format read by the website's DmMask component: MASK header, big-endian
    // 64-bit time/offset index, then zlib/gzip blocks containing SVG data URLs.
    internal static class WebMaskParser
    {
        internal const int MaxEntries = 10000;
        internal const int MaxCompressedBlockBytes = 2 * 1024 * 1024;
        internal const int MaxInflatedBlockBytes = 8 * 1024 * 1024;
        internal const int MaxSvgBytes = 512 * 1024;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        public static int ReadHeader(byte[] bytes)
        {
            if (bytes == null || bytes.Length != 16 || bytes[0] != 'M' || bytes[1] != 'A' || bytes[2] != 'S' || bytes[3] != 'K' || bytes[8] != 2)
                throw new InvalidDataException("人物遮罩文件头不受支持。");
            uint count = U32(bytes, 12);
            if (count == 0 || count > MaxEntries) throw new InvalidDataException("人物遮罩索引数量无效。");
            return (int)count;
        }

        public static IReadOnlyList<WebMaskIndexEntry> ReadIndex(byte[] bytes, int count, long totalLength)
        {
            if (count <= 0 || count > MaxEntries || bytes == null || bytes.Length != count * 16 || totalLength < 16L + count * 16L)
                throw new InvalidDataException("人物遮罩索引不完整。");
            var entries = new List<WebMaskIndexEntry>(count);
            for (int index = 0; index < count; ++index)
            {
                long milliseconds = Checked64(bytes, index * 16);
                long offset = Checked64(bytes, index * 16 + 8);
                if (milliseconds > 7L * 24 * 60 * 60 * 1000 || offset < 16L + count * 16L || offset >= totalLength ||
                    (entries.Count > 0 && (milliseconds <= entries[entries.Count - 1].Time.TotalMilliseconds || offset <= entries[entries.Count - 1].Offset)))
                    throw new InvalidDataException("人物遮罩索引时间或字节位置无效。");
                entries.Add(new WebMaskIndexEntry { Time = TimeSpan.FromMilliseconds(milliseconds), Offset = offset });
            }
            return entries;
        }

        public static IReadOnlyList<DanmakuMaskFrame> ReadBlock(byte[] compressed, TimeSpan segmentEnd, double fps,
            CancellationToken ct = default(CancellationToken))
        {
            if (compressed == null || compressed.Length < 6 || compressed.Length > MaxCompressedBlockBytes)
                throw new InvalidDataException("人物遮罩压缩分段大小无效。");
            ct.ThrowIfCancellationRequested();
            byte[] inflated;
            bool gzip = compressed[0] == 0x1f && compressed[1] == 0x8b;
            bool zlib = (compressed[0] & 15) == 8 && (((int)compressed[0] << 8) + compressed[1]) % 31 == 0 && (compressed[1] & 32) == 0;
            if (!gzip && !zlib) throw new InvalidDataException("人物遮罩压缩格式不受支持。");
            using (var input = new MemoryStream(compressed, gzip ? 0 : 2, compressed.Length - (gzip ? 0 : 6), false))
            using (Stream decoder = gzip ? (Stream)new GZipStream(input, CompressionMode.Decompress) : new DeflateStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                var buffer = new byte[8192];
                int read;
                while ((read = decoder.Read(buffer, 0, buffer.Length)) != 0)
                {
                    ct.ThrowIfCancellationRequested();
                    if (output.Length + read > MaxInflatedBlockBytes) throw new InvalidDataException("人物遮罩解压后超出大小上限。");
                    output.Write(buffer, 0, read);
                }
                inflated = output.ToArray();
            }
            if (zlib && Adler32(inflated) != U32(compressed, compressed.Length - 4))
                throw new InvalidDataException("人物遮罩压缩校验失败。");
            if (inflated.Length == 0) throw new InvalidDataException("人物遮罩分段为空。");
            var frames = new List<DanmakuMaskFrame>();
            int position = 0;
            while (position < inflated.Length)
            {
                ct.ThrowIfCancellationRequested();
                if (inflated.Length - position < 12 || frames.Count >= 4096) throw new InvalidDataException("人物遮罩帧索引无效。");
                uint length = U32(inflated, position);
                long milliseconds = Checked64(inflated, position + 4);
                if (length == 0 || length > MaxSvgBytes * 2 || length > inflated.Length - position - 12 ||
                    milliseconds > 7L * 24 * 60 * 60 * 1000 || (frames.Count > 0 && milliseconds < frames[frames.Count - 1].Time.TotalMilliseconds))
                    throw new InvalidDataException("人物遮罩帧长度或时间无效。");
                string payload = Utf8.GetString(inflated, position + 12, (int)length);
                const string prefix = "data:image/svg+xml;base64,";
                if (!payload.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("人物遮罩帧没有提供 SVG 数据。");
                byte[] svgBytes = Convert.FromBase64String(payload.Substring(prefix.Length));
                if (svgBytes.Length == 0 || svgBytes.Length > MaxSvgBytes) throw new InvalidDataException("人物遮罩 SVG 大小无效。");
                string svg = Utf8.GetString(svgBytes, 0, svgBytes.Length);
                ValidateSvg(svg);
                var time = TimeSpan.FromMilliseconds(milliseconds);
                // Official files can contain several warm-up shapes at time 0.
                // They must not invalidate an otherwise ordered segment. Keep
                // the last shape at a timestamp, then resume the real timeline.
                if (frames.Count > 0 && frames[frames.Count - 1].Time == time)
                {
                    frames[frames.Count - 1].Svg = svg;
                    position = checked(position + 12 + (int)length);
                    continue;
                }
                if (frames.Count > 0) frames[frames.Count - 1].EndTime = time;
                frames.Add(new DanmakuMaskFrame { Time = time, Svg = svg });
                position = checked(position + 12 + (int)length);
            }
            if (double.IsNaN(fps) || double.IsInfinity(fps) || fps <= 0 || fps > 120) fps = 24;
            var last = frames[frames.Count - 1];
            last.EndTime = segmentEnd > last.Time && segmentEnd != TimeSpan.MaxValue ? segmentEnd : last.Time + TimeSpan.FromSeconds(1 / fps);
            return frames;
        }

        private static void ValidateSvg(string svg)
        {
            using (var reader = XmlReader.Create(new StringReader(svg), new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxSvgBytes }))
            {
                var document = XDocument.Load(reader);
                if (document.Root == null || document.Root.Name.LocalName != "svg") throw new InvalidDataException("人物遮罩 SVG 根节点无效。");
                foreach (var element in document.Root.DescendantsAndSelf())
                {
                    if (element.Name.LocalName.Equals("script", StringComparison.OrdinalIgnoreCase) || element.Name.LocalName.Equals("foreignObject", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("人物遮罩 SVG 包含不支持的内容。");
                    foreach (var attribute in element.Attributes())
                        if ((attribute.Name.LocalName.Equals("href", StringComparison.OrdinalIgnoreCase) && !attribute.Value.StartsWith("#", StringComparison.Ordinal)) ||
                            attribute.Name.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase) || attribute.Value.IndexOf("url(", StringComparison.OrdinalIgnoreCase) >= 0)
                            throw new InvalidDataException("人物遮罩 SVG 不允许引用外部资源。");
                }
            }
        }

        private static long Checked64(byte[] bytes, int position)
        {
            ulong value = ((ulong)U32(bytes, position) << 32) | U32(bytes, position + 4);
            if (value > long.MaxValue) throw new InvalidDataException("人物遮罩数值超出支持范围。");
            return (long)value;
        }

        private static uint U32(byte[] bytes, int position)
            => ((uint)bytes[position] << 24) | ((uint)bytes[position + 1] << 16) | ((uint)bytes[position + 2] << 8) | bytes[position + 3];

        private static uint Adler32(byte[] bytes)
        {
            uint a = 1, b = 0;
            foreach (byte value in bytes) { a = (a + value) % 65521; b = (b + a) % 65521; }
            return (b << 16) | a;
        }
    }
}
