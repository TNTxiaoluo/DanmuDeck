using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Storage;

namespace BiliXbox.Services
{
    // Recent search keywords on this device only, newest first. Ordinary
    // preferences: no account data, never uploaded.
    internal static class SearchHistoryStore
    {
        private const string Key = "search.recent.v1";
        internal const int Capacity = 12;
        private const char Separator = '\u001F';

        internal static IReadOnlyList<string> Load()
        {
            try
            {
                object value;
                if (!ApplicationData.Current.LocalSettings.Values.TryGetValue(Key, out value) || !(value is string text)) return Array.Empty<string>();
                return text.Split(Separator).Where(item => item.Length > 0).Take(Capacity).ToList();
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException)) { return Array.Empty<string>(); }
        }

        internal static IReadOnlyList<string> Add(IReadOnlyList<string> current, string keyword)
        {
            keyword = (keyword ?? "").Replace(Separator, ' ').Trim();
            var list = (current ?? Array.Empty<string>()).Where(item => !string.Equals(item, keyword, StringComparison.Ordinal)).ToList();
            if (keyword.Length > 0 && keyword.Length <= 200) list.Insert(0, keyword);
            return list.Take(Capacity).ToList();
        }

        internal static void Record(string keyword)
        {
            try { Save(Add(Load(), keyword)); }
            catch (Exception ex) when (!(ex is OutOfMemoryException)) { /* History is a convenience only. */ }
        }

        internal static void Clear()
        {
            try { ApplicationData.Current.LocalSettings.Values.Remove(Key); }
            catch (Exception ex) when (!(ex is OutOfMemoryException)) { }
        }

        private static void Save(IReadOnlyList<string> items)
            => ApplicationData.Current.LocalSettings.Values[Key] = string.Join(Separator.ToString(), items);
    }
}
