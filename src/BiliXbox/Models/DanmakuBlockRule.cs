using System;
using System.Collections.Generic;

namespace BiliXbox.Models
{
    public enum DanmakuBlockRuleType { Keyword = 0, Regex = 1, UserHash = 2 }

    public sealed class DanmakuBlockRule
    {
        public long Id { get; set; }
        public DanmakuBlockRuleType Type { get; set; }
        public string Value { get; set; } = "";
        public bool Enabled { get; set; } = true;
    }

    public sealed class DanmakuBlockRuleSet
    {
        public IReadOnlyList<DanmakuBlockRule> Rules { get; set; } = Array.Empty<DanmakuBlockRule>();
        public long AccountMid { get; set; }
        public DateTimeOffset SyncedAt { get; set; }
        public int Count => Rules?.Count ?? 0;
        public int SourceCount { get; set; }
        public int UnsupportedCount { get; set; }
        public string Notice { get; set; } = "";
    }
}
