using System;
using System.Collections.Generic;
using System.IO;

namespace BiliXbox.Services
{
    /// <summary>
    /// Builds a movie-fragment random access box ('mfra' with one 'tfra' and a
    /// trailing 'mfro') from the top-level 'sidx' of a Bilibili DASH file.
    /// The Windows MPEG-4 source otherwise seeks in a fragmented file by
    /// parsing every 'moof' from the start: restoring a position of two
    /// minutes in a 4K HDR file read ~280 MB first. Appended virtually after
    /// the real file, the index lets the source jump straight to a fragment.
    /// Each DASH subsegment starts with a stream access point (startWithSAP).
    /// </summary>
    internal static class Mp4RandomAccessIndex
    {
        internal static byte[] Build(byte[] prefix, ulong totalLength)
        {
            if (prefix == null || prefix.Length < 16) return null;
            uint trackId = 0, mediaTimescale = 0;
            DashSegmentIndex index = null;
            long offset = 0;
            while (offset + 8 <= prefix.Length)
            {
                ulong size = ReadUInt32(prefix, (int)offset);
                string type = FourCc(prefix, (int)offset + 4);
                int header = 8;
                if (size == 1)
                {
                    if (offset + 16 > prefix.Length) break;
                    size = ReadUInt64(prefix, (int)offset + 8);
                    header = 16;
                }
                if (size < (ulong)header || size == 0) return null;
                if (type == "mfra") return null;
                if (type == "moov")
                {
                    if (offset + (long)size > prefix.Length) return null;
                    ReadTrack(prefix, (int)offset + header, (int)(offset + (long)size), ref trackId, ref mediaTimescale);
                }
                else if (type == "sidx")
                {
                    if (offset + (long)size > prefix.Length || size > (ulong)DashSegmentIndex.MaximumIndexBytes) return null;
                    var bytes = new byte[size];
                    Buffer.BlockCopy(prefix, (int)offset, bytes, 0, (int)size);
                    try { index = DashSegmentIndex.Parse(bytes, (ulong)offset, totalLength); }
                    catch (InvalidDataException) { return null; }
                    break;
                }
                else if (type == "moof" || type == "mdat") break;
                offset += (long)size;
            }
            if (index == null || trackId == 0 || index.Segments.Count == 0 || index.Timescale == 0) return null;
            if (mediaTimescale == 0) mediaTimescale = index.Timescale;

            var entries = new List<Tuple<ulong, ulong>>(index.Segments.Count);
            ulong time = index.EarliestTime;
            foreach (var segment in index.Segments)
            {
                ulong media = mediaTimescale == index.Timescale ? time
                    : (ulong)((double)time * mediaTimescale / index.Timescale);
                entries.Add(Tuple.Create(media, segment.Start));
                time += segment.Duration;
            }
            int tfraSize = 12 + 4 + 4 + 4 + entries.Count * 19;
            int mfraSize = 8 + tfraSize + 16;
            var output = new byte[mfraSize];
            int at = 0;
            WriteUInt32(output, ref at, (uint)mfraSize); WriteFourCc(output, ref at, "mfra");
            WriteUInt32(output, ref at, (uint)tfraSize); WriteFourCc(output, ref at, "tfra");
            WriteUInt32(output, ref at, 0x01000000); // version 1, flags 0
            WriteUInt32(output, ref at, trackId);
            WriteUInt32(output, ref at, 0); // 1-byte traf/trun/sample numbers
            WriteUInt32(output, ref at, (uint)entries.Count);
            foreach (var entry in entries)
            {
                WriteUInt64(output, ref at, entry.Item1);
                WriteUInt64(output, ref at, entry.Item2);
                output[at++] = 1; output[at++] = 1; output[at++] = 1;
            }
            WriteUInt32(output, ref at, 16); WriteFourCc(output, ref at, "mfro");
            WriteUInt32(output, ref at, 0);
            WriteUInt32(output, ref at, (uint)mfraSize);
            return output;
        }

        private static void ReadTrack(byte[] data, int start, int end, ref uint trackId, ref uint timescale)
        {
            int offset = start;
            while (offset + 8 <= end)
            {
                uint size = ReadUInt32(data, offset);
                string type = FourCc(data, offset + 4);
                if (size < 8 || offset + size > end) return;
                int body = offset + 8;
                if (type == "trak" || type == "mdia") ReadTrack(data, body, (int)(offset + size), ref trackId, ref timescale);
                else if (type == "tkhd" && trackId == 0 && body + 24 <= offset + size)
                    trackId = ReadUInt32(data, body + (data[body] == 1 ? 20 : 12));
                else if (type == "mdhd" && timescale == 0 && body + 24 <= offset + size)
                    timescale = ReadUInt32(data, body + (data[body] == 1 ? 20 : 12));
                offset += (int)size;
            }
        }

        private static uint ReadUInt32(byte[] data, int offset)
            => (uint)(data[offset] << 24 | data[offset + 1] << 16 | data[offset + 2] << 8 | data[offset + 3]);
        private static ulong ReadUInt64(byte[] data, int offset)
            => (ulong)ReadUInt32(data, offset) << 32 | ReadUInt32(data, offset + 4);
        private static string FourCc(byte[] data, int offset)
            => new string(new[] { (char)data[offset], (char)data[offset + 1], (char)data[offset + 2], (char)data[offset + 3] });
        private static void WriteUInt32(byte[] data, ref int at, uint value)
        { data[at++] = (byte)(value >> 24); data[at++] = (byte)(value >> 16); data[at++] = (byte)(value >> 8); data[at++] = (byte)value; }
        private static void WriteUInt64(byte[] data, ref int at, ulong value)
        { WriteUInt32(data, ref at, (uint)(value >> 32)); WriteUInt32(data, ref at, (uint)value); }
        private static void WriteFourCc(byte[] data, ref int at, string type)
        { foreach (char c in type) data[at++] = (byte)c; }
    }
}
