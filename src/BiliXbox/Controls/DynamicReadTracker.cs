using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BiliXbox.Models;

namespace BiliXbox.Controls
{
    // Real dynamic IDs form per-author read watermarks. A boolean portal flag
    // cannot identify a new post after a locally-read update; check real IDs.
    internal sealed class DynamicReadTracker
    {
        private sealed class Entry { internal string Read = "", Latest = ""; internal bool Portal; }
        private readonly Dictionary<long, Entry> _entries = new Dictionary<long, Entry>();
        internal long Account { get; private set; }
        internal void Reset(long account, IDictionary<string, string> saved = null)
        {
            Account = account; _entries.Clear();
            if (account <= 0 || saved == null) return;
            foreach (var pair in saved.Take(512))
            {
                long mid; string id = ValidId(pair.Value);
                if (long.TryParse(pair.Key, out mid) && mid > 0 && id != null) For(mid).Read = id;
            }
        }
        private Entry For(long mid)
        { Entry value; if (!_entries.TryGetValue(mid, out value)) _entries[mid] = value = new Entry(); return value; }
        internal void ApplyPortal(IEnumerable<DynamicAuthorUpdate> updates)
        { if (Account > 0) foreach (var update in updates) if (update.Mid > 0) For(update.Mid).Portal = update.HasUpdate; }
        internal void Observe(IEnumerable<CreatorDynamicItem> posts)
        {
            if (Account <= 0) return;
            foreach (var post in posts)
            {
                string id = ValidId(post.Id);
                if (post.AuthorMid <= 0 || id == null) continue;
                var entry = For(post.AuthorMid);
                if (Compare(id, entry.Latest) > 0) entry.Latest = id;
            }
        }
        internal bool IsUnread(long mid)
        {
            Entry entry;
            return Account > 0 && mid > 0 && _entries.TryGetValue(mid, out entry)
                && (Compare(entry.Latest, entry.Read) > 0 || (entry.Portal && entry.Read.Length == 0));
        }
        internal bool NeedsLatestCheck(long mid)
        { Entry entry; return _entries.TryGetValue(mid, out entry) && entry.Portal && entry.Read.Length > 0 && Compare(entry.Latest, entry.Read) <= 0; }
        internal void MarkViewed(long mid, string id = null)
        {
            if (Account <= 0 || mid <= 0) return;
            var entry = For(mid); string viewed = id == null ? entry.Latest : ValidId(id);
            if (viewed == null) return;
            if (viewed.Length == 0) viewed = "0";
            if (Compare(viewed, entry.Read) > 0) entry.Read = viewed;
        }
        internal void MarkPageViewed(long mid, IEnumerable<CreatorDynamicItem> posts)
        {
            string newest = "0";
            foreach (var post in posts.Where(item => item.AuthorMid == mid))
            { string id = ValidId(post.Id); if (id != null && Compare(id, newest) > 0) newest = id; }
            MarkViewed(mid, newest);
        }
        internal Dictionary<string, string> Export()
            => _entries.Where(pair => pair.Value.Read.Length > 0).Take(512)
                .ToDictionary(pair => pair.Key.ToString(CultureInfo.InvariantCulture), pair => pair.Value.Read);
        internal IReadOnlyList<long> OrderAuthors(IEnumerable<long> defaultOrder)
            => defaultOrder.OrderBy(mid => mid == 0 ? 0 : IsUnread(mid) ? 1 : 2).ToArray();
        private static string ValidId(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 64 || value.Any(ch => ch < '0' || ch > '9')) return null;
            string id = value.TrimStart('0'); return id.Length == 0 ? "0" : id;
        }
        private static int Compare(string a, string b)
        {
            if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
            return string.CompareOrdinal(a, b);
        }
    }
}
