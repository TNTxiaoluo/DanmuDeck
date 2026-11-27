using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace BiliXbox.Services
{
    /// <summary>
    /// Describes the box structure of an ISO-BMFF initialization segment for
    /// diagnostics only: box types, sizes, sample-entry children and the
    /// decoder-relevant fields of hvcC/colr/dvcC. Never records payload data
    /// beyond these structural fields, nor any URL.
    /// </summary>
    internal static class Mp4InitDescriber
    {
        private const int MaxDepth = 10;
        private const int MaxEntries = 160;

        private static readonly HashSet<string> Containers = new HashSet<string>(StringComparer.Ordinal)
        {
            "moov", "trak", "mdia", "minf", "stbl", "mvex", "edts", "dinf", "udta", "sinf", "schi", "moof", "traf"
        };

        private static readonly HashSet<string> SampleEntries = new HashSet<string>(StringComparer.Ordinal)
        {
            "hvc1", "hev1", "dvh1", "dvhe", "avc1", "avc3", "av01", "encv", "mp4a", "ec-3", "ac-3", "fLaC", "enca"
        };

        internal static string Describe(byte[] data)
        {
            var output = new StringBuilder();
            int entries = 0;
            try { Walk(data, 0, data?.Length ?? 0, 0, "", output, ref entries); }
            catch (Exception ex) { output.Append(" parse-error=").Append(ex.GetType().Name); }
            if (entries >= MaxEntries) output.Append(" truncated=True");
            return output.ToString().Trim();
        }

        private static void Walk(byte[] data, int start, int end, int depth, string path, StringBuilder output, ref int entries)
        {
            int offset = start;
            while (offset + 8 <= end && entries < MaxEntries)
            {
                long size = ReadUInt32(data, offset);
                string type = FourCc(data, offset + 4);
                int header = 8;
                if (size == 1)
                {
                    if (offset + 16 > end) break;
                    size = (long)((ulong)ReadUInt32(data, offset + 8) << 32 | ReadUInt32(data, offset + 12));
                    header = 16;
                }
                else if (size == 0) size = end - offset;
                if (size < header || offset + size > end)
                {
                    output.Append(' ').Append(path).Append(type).Append("@").Append(offset).Append(":badSize=").Append(size);
                    ++entries;
                    return;
                }
                int body = offset + header, next = (int)(offset + size);
                string name = path + type;
                output.Append(' ').Append(name).Append(':').Append(size);
                ++entries;
                if (depth < MaxDepth)
                {
                    if (Containers.Contains(type)) Walk(data, body, next, depth + 1, name + "/", output, ref entries);
                    else if (type == "stsd" && body + 8 <= next) Walk(data, body + 8, next, depth + 1, name + "/", output, ref entries);
                    else if (SampleEntries.Contains(type)) SampleEntry(data, type, body, next, depth, name, output, ref entries);
                    else if (type == "hvcC") Hvcc(data, body, next, output);
                    else if (type == "colr") Colr(data, body, next, output);
                    else if (type == "dvcC" || type == "dvvC" || type == "dvwC") DolbyConfig(data, body, next, output);
                    else if (type == "tkhd" || type == "mdhd" || type == "hdlr") FullBox(data, type, body, next, output);
                    else if (type == "frma" && body + 4 <= next) output.Append("[").Append(FourCc(data, body)).Append(']');
                    else if (type == "schm" && body + 8 <= next) output.Append("[").Append(FourCc(data, body + 4)).Append(']');
                }
                offset = next;
            }
            if (offset != end && offset < end) output.Append(' ').Append(path).Append("trailing=").Append(end - offset);
        }

        private static void SampleEntry(byte[] data, string type, int body, int end, int depth, string name,
            StringBuilder output, ref int entries)
        {
            bool audio = type == "mp4a" || type == "ec-3" || type == "ac-3" || type == "fLaC" || type == "enca";
            int fixedBytes = audio ? 28 : 78;
            if (body + fixedBytes > end) { output.Append("[short]"); return; }
            if (!audio)
                output.Append('[').Append(ReadUInt16(data, body + 24)).Append('x').Append(ReadUInt16(data, body + 26))
                    .Append(" depth=").Append(ReadUInt16(data, body + 74)).Append(']');
            Walk(data, body + fixedBytes, end, depth + 1, name + "/", output, ref entries);
        }

        private static void Hvcc(byte[] data, int body, int end, StringBuilder output)
        {
            if (body + 23 > end) { output.Append("[short]"); return; }
            int profileByte = data[body + 1];
            output.Append("[v=").Append(data[body])
                .Append(" space=").Append(profileByte >> 6).Append(" tier=").Append((profileByte >> 5) & 1)
                .Append(" profile=").Append(profileByte & 0x1F)
                .Append(" compat=").Append(ReadUInt32(data, body + 2).ToString("X8", CultureInfo.InvariantCulture))
                .Append(" level=").Append(data[body + 12])
                .Append(" chroma=").Append(data[body + 16] & 3)
                .Append(" lumaBits=").Append((data[body + 17] & 7) + 8)
                .Append(" chromaBits=").Append((data[body + 18] & 7) + 8)
                .Append(" nalLen=").Append((data[body + 21] & 3) + 1);
            int arrays = data[body + 22], offset = body + 23;
            output.Append(" arrays=").Append(arrays);
            for (int index = 0; index < arrays && offset + 3 <= end; ++index)
            {
                int nalType = data[offset] & 0x3F, count = ReadUInt16(data, offset + 1);
                offset += 3;
                output.Append(" nal").Append(nalType).Append('x').Append(count).Append('(');
                for (int item = 0; item < count && offset + 2 <= end; ++item)
                {
                    int length = ReadUInt16(data, offset);
                    if (item > 0) output.Append(',');
                    output.Append(length);
                    offset += 2 + length;
                }
                output.Append(')');
            }
            if (offset > end) output.Append(" overrun=").Append(offset - end);
            output.Append(']');
        }

        private static void Colr(byte[] data, int body, int end, StringBuilder output)
        {
            if (body + 4 > end) { output.Append("[short]"); return; }
            string kind = FourCc(data, body);
            output.Append('[').Append(kind);
            if (kind == "nclx" && body + 11 <= end)
                output.Append(" primaries=").Append(ReadUInt16(data, body + 4))
                    .Append(" transfer=").Append(ReadUInt16(data, body + 6))
                    .Append(" matrix=").Append(ReadUInt16(data, body + 8))
                    .Append(" full=").Append(data[body + 10] >> 7);
            output.Append(']');
        }

        private static void DolbyConfig(byte[] data, int body, int end, StringBuilder output)
        {
            if (body + 5 > end) { output.Append("[short]"); return; }
            int profile = data[body + 2] >> 1;
            int level = ((data[body + 2] & 1) << 5) | (data[body + 3] >> 3);
            output.Append("[ver=").Append(data[body]).Append('.').Append(data[body + 1])
                .Append(" profile=").Append(profile).Append(" level=").Append(level)
                .Append(" rpu=").Append((data[body + 3] >> 2) & 1)
                .Append(" el=").Append((data[body + 3] >> 1) & 1)
                .Append(" bl=").Append(data[body + 3] & 1)
                .Append(" compat=").Append(data[body + 4] >> 4).Append(']');
        }

        private static void FullBox(byte[] data, string type, int body, int end, StringBuilder output)
        {
            if (type == "hdlr" && body + 12 <= end) output.Append('[').Append(FourCc(data, body + 8)).Append(']');
            else if (type == "mdhd" && body + 4 <= end)
            {
                int version = data[body];
                int at = body + (version == 1 ? 20 : 12);
                if (at + 4 <= end) output.Append("[timescale=").Append(ReadUInt32(data, at)).Append(']');
            }
            else if (type == "tkhd" && body + 4 <= end)
            {
                int version = data[body];
                int at = body + (version == 1 ? 88 : 76);
                if (at + 8 <= end)
                    output.Append('[').Append(ReadUInt32(data, at) >> 16).Append('x').Append(ReadUInt32(data, at + 4) >> 16).Append(']');
            }
        }

        private static uint ReadUInt32(byte[] data, int offset)
            => (uint)(data[offset] << 24 | data[offset + 1] << 16 | data[offset + 2] << 8 | data[offset + 3]);

        private static int ReadUInt16(byte[] data, int offset) => data[offset] << 8 | data[offset + 1];

        private static string FourCc(byte[] data, int offset)
        {
            var chars = new char[4];
            for (int index = 0; index < 4; ++index)
            {
                byte value = data[offset + index];
                chars[index] = value >= 0x20 && value < 0x7F ? (char)value : '?';
            }
            return new string(chars);
        }
    }
}
