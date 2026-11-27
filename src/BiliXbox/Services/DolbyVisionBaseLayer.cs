using System;
using System.Collections.Generic;

namespace BiliXbox.Services
{
    // Dolby Vision Profile 8.x keeps a standard hvc1/hev1 sample entry and adds
    // a dvcC/dvvC/dvwC configuration box. Xbox's MP4 source fails such a track
    // (DecodingError 0xC00D36B4), but its base layer is ordinary HEVC Main10
    // (HDR10 for 8.1, HLG for 8.4) and the RPU NAL units are ignored by a
    // plain HEVC decoder. Renaming the configuration box to "free" (same size,
    // no offsets change) presents the base layer only.
    internal static class DolbyVisionBaseLayer
    {
        private static readonly string[] ConfigTypes = { "dvcC", "dvvC", "dvwC" };

        // Absolute offsets of the four type bytes of each Dolby Vision
        // configuration box inside the moov box of an initialization segment.
        internal static IReadOnlyList<long> FindConfigBoxTypes(byte[] data)
        {
            var result = new List<long>();
            if (data == null) return result;
            long moovStart = -1, moovEnd = -1;
            long at = 0;
            while (at + 8 <= data.Length)
            {
                long size = ReadUInt32(data, at);
                string type = FourCc(data, at + 4);
                if (size == 1 && at + 16 <= data.Length) size = (long)ReadUInt64(data, at + 8);
                if (size < 8) break;
                if (type == "moov") { moovStart = at + 8; moovEnd = Math.Min(data.Length, at + size); break; }
                at += size;
            }
            if (moovStart < 0) return result;
            for (long i = moovStart + 4; i + 4 <= moovEnd; ++i)
            {
                string type = FourCc(data, i);
                if (Array.IndexOf(ConfigTypes, type) < 0) continue;
                long size = ReadUInt32(data, i - 4);
                // A real configuration box is 24 bytes or slightly more and
                // must fit inside moov.
                if (size >= 24 && size <= 64 && i - 4 + size <= moovEnd) result.Add(i);
            }
            return result;
        }

        internal static readonly byte[] FreeType = { (byte)'f', (byte)'r', (byte)'e', (byte)'e' };

        private static uint ReadUInt32(byte[] data, long at)
            => (uint)(data[at] << 24 | data[at + 1] << 16 | data[at + 2] << 8 | data[at + 3]);
        private static ulong ReadUInt64(byte[] data, long at) => (ulong)ReadUInt32(data, at) << 32 | ReadUInt32(data, at + 4);
        private static string FourCc(byte[] data, long at)
            => new string(new[] { (char)data[at], (char)data[at + 1], (char)data[at + 2], (char)data[at + 3] });
    }
}
