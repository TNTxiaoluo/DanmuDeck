using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace BiliXbox.Services
{
    // A bounded ISO-BMFF SIDX reader for the native DASH index compatibility
    // path. Offsets remain 64-bit and refer to the unchanged CDN resource.
    internal sealed class DashSegmentIndex
    {
        internal const int MaximumIndexBytes = 1024 * 1024, MaximumSegments = 8192;
        internal uint Timescale, ReferenceId;
        internal byte Version;
        internal ulong EarliestTime;
        internal readonly List<Segment> Segments = new List<Segment>();
        internal sealed class Segment { internal ulong Start, End; internal uint Duration; }

        internal static DashSegmentIndex Parse(byte[] bytes, ulong absoluteStart, ulong totalLength)
        {
            if (bytes == null || bytes.Length > MaximumIndexBytes || bytes.Length < 8)
                throw new InvalidDataException("媒体索引大小无效。");
            int at = 0;
            while (at <= bytes.Length - 8)
            {
                ulong size = U32(bytes, at); int header = 8;
                if (size == 1) { if (at > bytes.Length - 16) throw Invalid(); size = U64(bytes, at + 8); header = 16; }
                if (size == 0) size = (ulong)(bytes.Length - at);
                if (size < (ulong)header || size > (ulong)(bytes.Length - at)) throw Invalid();
                int end = at + (int)size;
                if (bytes[at + 4] == 's' && bytes[at + 5] == 'i' && bytes[at + 6] == 'd' && bytes[at + 7] == 'x')
                {
                    int p = at + header;
                    if (p > end - 12) throw Invalid();
                    var index = new DashSegmentIndex { Version = bytes[p], ReferenceId = U32(bytes, p + 4), Timescale = U32(bytes, p + 8) };
                    p += 12;
                    ulong first;
                    if (index.Version == 0)
                    { if (p > end - 8) throw Invalid(); index.EarliestTime = U32(bytes, p); first = U32(bytes, p + 4); p += 8; }
                    else if (index.Version == 1)
                    { if (p > end - 16) throw Invalid(); index.EarliestTime = U64(bytes, p); first = U64(bytes, p + 8); p += 16; }
                    else throw Invalid();
                    if (index.Timescale == 0 || p > end - 4) throw Invalid();
                    int count = (bytes[p + 2] << 8) | bytes[p + 3]; p += 4;
                    if (count < 1 || count > MaximumSegments || count > (end - p) / 12) throw Invalid();
                    ulong cursor = checked(absoluteStart + (ulong)end + first), time = index.EarliestTime;
                    for (int i = 0; i < count; i++, p += 12)
                    {
                        uint reference = U32(bytes, p), duration = U32(bytes, p + 4);
                        // Hierarchical indexes need another bounded fetch; never
                        // treat a child index as a playable media fragment.
                        if ((reference & 0x80000000) != 0 || reference == 0 || duration == 0) throw Invalid();
                        ulong next = checked(cursor + reference);
                        if (next > totalLength) throw Invalid();
                        index.Segments.Add(new Segment { Start = cursor, End = next - 1, Duration = duration });
                        cursor = next; time = checked(time + duration);
                    }
                    return index;
                }
                at = end;
            }
            throw Invalid();
        }

        internal void Replace(XElement segmentBase)
        {
            var ns = segmentBase.Name.Namespace;
            var initialization = segmentBase.Element(ns + "Initialization");
            if (initialization == null) throw Invalid();
            var timeline = new XElement(ns + "SegmentTimeline");
            for (int i = 0; i < Segments.Count; i++)
            {
                var s = new XElement(ns + "S", new XAttribute("d", Number(Segments[i].Duration)));
                if (i == 0) s.Add(new XAttribute("t", Number(EarliestTime)));
                timeline.Add(s);
            }
            var list = new XElement(ns + "SegmentList", new XAttribute("timescale", Number(Timescale)));
            var offset = segmentBase.Attribute("presentationTimeOffset");
            if (offset != null) list.Add(new XAttribute(offset));
            list.Add(new XElement(initialization), timeline);
            foreach (var s in Segments) list.Add(new XElement(ns + "SegmentURL", new XAttribute("mediaRange", Number(s.Start) + "-" + Number(s.End))));
            segmentBase.ReplaceWith(list);
        }
        private static string Number(ulong value) => value.ToString(CultureInfo.InvariantCulture);
        private static uint U32(byte[] b, int p) => ((uint)b[p] << 24) | ((uint)b[p + 1] << 16) | ((uint)b[p + 2] << 8) | b[p + 3];
        private static ulong U64(byte[] b, int p) => ((ulong)U32(b, p) << 32) | U32(b, p + 4);
        private static InvalidDataException Invalid() => new InvalidDataException("媒体 SIDX 索引无效或暂不兼容。");
    }
}
