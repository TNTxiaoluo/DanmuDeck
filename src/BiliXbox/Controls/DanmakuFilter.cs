using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using BiliXbox.Models;

namespace BiliXbox.Controls
{
    // Runs when a timeline/settings snapshot changes, never in the animation loop.
    internal sealed class DanmakuFilter
    {
        private readonly List<string> _keywords = new List<string>();
        private readonly HashSet<string> _senders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<Regex> _patterns = new List<Regex>();
        public int IgnoredRuleCount { get; private set; }

        public DanmakuFilter(IReadOnlyList<DanmakuBlockRule> rules)
        {
            if (rules == null) return;
            for (int i = 0; i < rules.Count; ++i)
            {
                var rule = rules[i];
                if (rule == null || !rule.Enabled || string.IsNullOrEmpty(rule.Value)) continue;
                if (i >= 2048 || rule.Value.Length > 512) { ++IgnoredRuleCount; continue; }
                if (rule.Type == DanmakuBlockRuleType.Keyword) _keywords.Add(rule.Value);
                else if (rule.Type == DanmakuBlockRuleType.UserHash) _senders.Add(rule.Value.Trim());
                else if (rule.Type == DanmakuBlockRuleType.Regex)
                {
                    try { _patterns.Add(new Regex(rule.Value, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(10))); }
                    catch (ArgumentException) { ++IgnoredRuleCount; }
                }
                else ++IgnoredRuleCount;
            }
        }

        public bool Blocks(string text, string senderHash)
        {
            if (!string.IsNullOrEmpty(senderHash) && _senders.Contains(senderHash.Trim())) return true;
            text = text ?? "";
            foreach (var word in _keywords) if (text.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            for (int i = _patterns.Count - 1; i >= 0; --i)
            {
                try { if (_patterns[i].IsMatch(text)) return true; }
                catch (RegexMatchTimeoutException)
                {
                    // A malicious rule cannot repeatedly consume frame time.
                    _patterns.RemoveAt(i);
                    ++IgnoredRuleCount;
                }
            }
            return false;
        }
    }

    internal sealed class DanmakuDuplicateTracker
    {
        private readonly Dictionary<string, double> _last = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        public bool Accept(string text, double seconds)
        {
            string key = Key(text);
            double previous;
            if (_last.TryGetValue(key, out previous) && seconds >= previous && seconds - previous <= 8) return false;
            _last[key] = seconds;
            return true;
        }
        public static string Key(string text)
        {
            text = text ?? "";
            try { text = text.Normalize(NormalizationForm.FormKC); }
            catch (ArgumentException) { /* Preserve malformed input without crashing the renderer. */ }
            var result = new StringBuilder(text.Length);
            bool space = false;
            foreach (char value in text)
            {
                if (char.IsWhiteSpace(value)) { space = result.Length > 0; continue; }
                if (space) result.Append(' ');
                result.Append(value);
                space = false;
            }
            return result.ToString();
        }
    }

    internal static class DanmakuLaneLayout
    {
        public static IReadOnlyList<double> AllowedRows(double height, double lineHeight, DanmakuSettings settings)
        {
            var rows = new List<double>();
            if (settings == null || double.IsNaN(height) || double.IsInfinity(height) || height <= 0 ||
                double.IsNaN(lineHeight) || double.IsInfinity(lineHeight) || lineHeight <= 0) return rows;
            double bottom = height * settings.AreaFraction;
            for (double y = 0; y + lineHeight <= bottom + 0.01 && rows.Count < 64; y += lineHeight)
            {
                if (settings.AvoidCenter && y < height * 0.68 && y + lineHeight > height * 0.32) continue;
                rows.Add(y);
            }
            return rows;
        }
    }
}
