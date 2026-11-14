using System;
using System.Collections.Generic;
using System.Linq;
using BiliXbox.Models;

namespace BiliXbox.Controls
{
    // UI pages and their positions belong to one account and one feed batch.
    // The API recommendation cursor cache remains independent of these views.
    internal sealed class HomeFeedPageCache
    {
        internal sealed class Page
        {
            internal HomeSection Section;
            internal int Number;
            internal IReadOnlyList<VideoItem> Videos;
            internal bool HasMore;
            internal string Title, Notice;
            internal bool HasPosition;
            internal int FocusIndex;
            internal double ScrollOffset;
        }

        private readonly Dictionary<string, Page> _pages = new Dictionary<string, Page>(StringComparer.Ordinal);
        private readonly List<string> _order = new List<string>();
        internal long AccountMid { get; private set; } = -1;
        private static string Key(HomeSection section, int page) => ((int)section) + ":" + page;

        internal bool EnsureAccount(long accountMid)
        {
            if (accountMid < 0) throw new ArgumentOutOfRangeException(nameof(accountMid));
            if (AccountMid == accountMid) return false;
            AccountMid = accountMid; _pages.Clear(); _order.Clear();
            return true;
        }

        internal void Invalidate(HomeSection section)
        {
            foreach (var key in _pages.Where(pair => pair.Value.Section == section).Select(pair => pair.Key).ToList())
            { _pages.Remove(key); _order.Remove(key); }
        }

        internal Page Remember(HomeSection section, int page, IReadOnlyList<VideoItem> videos,
            bool hasMore, string title, string notice)
        {
            if (page <= 0 || section == HomeSection.Live || !Enum.IsDefined(typeof(HomeSection), section))
                throw new ArgumentOutOfRangeException(nameof(page));
            if (AccountMid < 0) throw new InvalidOperationException("A feed cache needs an account scope.");
            var snapshot = new Page { Section = section, Number = page, Videos = (videos ?? Array.Empty<VideoItem>()).ToArray(),
                HasMore = hasMore, Title = title ?? "", Notice = notice ?? "" };
            var key = Key(section, page); _pages[key] = snapshot;
            Touch(key);
            while (_order.Count > 64) { _pages.Remove(_order[0]); _order.RemoveAt(0); }
            return snapshot;
        }

        internal bool TryGet(HomeSection section, int page, out Page snapshot)
        {
            var key = Key(section, page);
            if (!_pages.TryGetValue(key, out snapshot)) return false;
            Touch(key); return true;
        }

        internal void RememberPosition(HomeSection section, int page, int index, double offset)
        {
            Page snapshot;
            if (!TryGet(section, page, out snapshot)) return;
            snapshot.HasPosition = true;
            snapshot.FocusIndex = Math.Max(0, Math.Min(index, Math.Max(0, snapshot.Videos.Count - 1)));
            snapshot.ScrollOffset = double.IsNaN(offset) || double.IsInfinity(offset) ? 0 : Math.Max(0, offset);
        }

        private void Touch(string key) { _order.Remove(key); _order.Add(key); }
    }
}
