using System;
using System.Collections.Generic;
using System.Diagnostics;
using BiliXbox.Controls;
using BiliXbox.Models;
using BiliXbox.Services;
using Windows.Storage;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace BiliXbox.DanmakuSettingsTests
{
    public static class Tests
    {
        private static int _assertions;
        private static void Check(bool condition, string message)
        {
            ++_assertions;
            if (!condition) throw new InvalidOperationException(message);
        }
        private static DanmakuItem Item(double time, string text, int mode = 1, int color = 0xFFFFFF, string hash = "")
            => new DanmakuItem { Time = time, Text = text, Mode = mode, Color = color, SenderHash = hash };
        private static DanmakuOverlay Surface(DanmakuSettings settings = null)
        {
            var surface = new DanmakuOverlay();
            surface.SetViewport(1280, 720);
            surface.RaiseLoaded();
            if (settings != null) surface.ApplySettings(settings);
            return surface;
        }
        public static void Run()
        {
            _assertions = 0;
            ImmutableSnapshotsAndCorruptStorage();
            CloudRuleTypesAndRegexBounds();
            UnicodeDuplicateKeys();
            DisplayAreaAndDensity();
            IndependentScrollingAndFixedLayers();
            ImmediateSettingsAndRuleReplacement();
            CollisionPolicyAndPoolBudget();
            SpeedAndPausedWorkBudget();
            Console.WriteLine("All " + _assertions + " danmaku settings/filter/render-budget assertions passed. These tests do not measure Xbox frame rate or person masks.");
        }
        private static void ImmutableSnapshotsAndCorruptStorage()
        {
            var original = DanmakuSettings.Default;
            var changed = original.With(areaFraction: 0.25, opacity: 0.4, fontScale: 1.5, densityLimit: 8,
                speedScale: 1.5, smartMask: false, showColor: false, showBottom: false, mergeDuplicates: false);
            Check(original.AreaFraction == 1 && original.Opacity == 0.9 && original.SmartMask, "Editing one snapshot mutated the default settings.");
            Check(changed.AreaFraction == 0.25 && changed.Opacity == 0.4 && changed.FontScale == 1.5 && changed.DensityLimit == 8 &&
                changed.SpeedScale == 1.5 && !changed.SmartMask && !changed.ShowColor && !changed.ShowBottom && !changed.MergeDuplicates,
                "With did not copy all selected fields.");
            foreach (var property in typeof(DanmakuSettings).GetProperties())
                Check(!property.CanWrite, "Settings exposed a mutable property: " + property.Name);
            var invalid = new DanmakuSettings(double.NaN, double.PositiveInfinity, -10, -1, 999);
            Check(invalid.AreaFraction == 1 && invalid.Opacity == 0.9 && invalid.FontScale == 0.65 && invalid.DensityLimit == 4 && invalid.SpeedScale == 2,
                "Invalid preferences escaped finite/bounded defaults.");
            var store = new DanmakuSettingsStore();
            ApplicationData.Current.LocalSettings.Values.Clear();
            Check(store.Load().DensityLimit == 24, "Missing preferences did not use defaults.");
            store.Save(changed);
            var loaded = store.Load();
            Check(loaded.AreaFraction == changed.AreaFraction && loaded.Opacity == changed.Opacity && loaded.FontScale == changed.FontScale &&
                loaded.DensityLimit == changed.DensityLimit && loaded.SpeedScale == changed.SpeedScale && loaded.SmartMask == changed.SmartMask &&
                loaded.ShowColor == changed.ShowColor && loaded.ShowBottom == changed.ShowBottom && loaded.MergeDuplicates == changed.MergeDuplicates,
                "Persisted preferences did not round trip.");
            Check(ApplicationData.Current.LocalSettings.Values.Count == 1, "One snapshot was split across storage keys.");
            ApplicationData.Current.LocalSettings.Values["danmaku.preferences.v1"] = new ApplicationDataCompositeValue
            {
                ["areaFraction"] = -10.0, ["opacity"] = "NaN", ["fontScale"] = new object(),
                ["densityLimit"] = double.PositiveInfinity, ["speedScale"] = 1000.0,
                ["showScrolling"] = "not a bool", ["smartMask"] = false
            };
            loaded = store.Load();
            Check(loaded.AreaFraction == 0.25 && loaded.Opacity == 0.9 && loaded.FontScale == 1 && loaded.DensityLimit == 24 &&
                loaded.SpeedScale == 2 && loaded.ShowScrolling && !loaded.SmartMask, "Corrupt fields were not clamped independently.");
            ApplicationData.Current.LocalSettings.Values["danmaku.preferences.v1"] = "corrupt";
            Check(store.Load().AreaFraction == 1, "A corrupt composite object crashed or retained invalid settings.");
        }
        private static void CloudRuleTypesAndRegexBounds()
        {
            var rules = new[]
            {
                new DanmakuBlockRule { Id = 1, Type = DanmakuBlockRuleType.Keyword, Value = "剧透" },
                new DanmakuBlockRule { Id = 2, Type = DanmakuBlockRuleType.Keyword, Value = "SPOILER" },
                new DanmakuBlockRule { Id = 3, Type = DanmakuBlockRuleType.Regex, Value = @"^第\d+名$" },
                new DanmakuBlockRule { Id = 4, Type = DanmakuBlockRuleType.UserHash, Value = "A1b2c3" },
                new DanmakuBlockRule { Id = 5, Type = DanmakuBlockRuleType.Keyword, Value = "保留", Enabled = false },
                new DanmakuBlockRule { Id = 6, Type = DanmakuBlockRuleType.Regex, Value = "[" }
            };
            var filter = new DanmakuFilter(rules);
            Check(filter.Blocks("这里有剧透", ""), "Unicode substring keyword did not block.");
            Check(filter.Blocks("some spoiler text", ""), "Keyword case folding was culture dependent.");
            Check(filter.Blocks("第123名", "") && !filter.Blocks("不是第123名", ""), "Regex rules were treated as plain keywords.");
            Check(filter.Blocks("普通文本", "a1B2C3"), "User hash equality did not block the sender.");
            Check(!filter.Blocks("A1b2c3 保留", "other"), "A disabled rule or user hash matched message contents.");
            Check(filter.IgnoredRuleCount == 1, "Invalid regex was not safely reported.");
            rules[0].Value = "changed";
            Check(filter.Blocks("剧透", ""), "A mutable cloud result changed a filter snapshot mid-playback.");
            var expensive = new DanmakuFilter(new[] { new DanmakuBlockRule { Type = DanmakuBlockRuleType.Regex, Value = "(a+)+$" } });
            var clock = Stopwatch.StartNew();
            Check(!expensive.Blocks(new string('a', 5000) + "!", ""), "Timed-out regex incorrectly blocked a message.");
            Check(expensive.IgnoredRuleCount == 1 && clock.Elapsed < TimeSpan.FromSeconds(2), "A catastrophic regex did not hit its bounded timeout.");
            Check(!expensive.Blocks("aaaa!", "") && expensive.IgnoredRuleCount == 1, "A timed-out regex ran again after disabling.");
        }
        private static void UnicodeDuplicateKeys()
        {
            var duplicates = new DanmakuDuplicateTracker();
            Check(duplicates.Accept("  ＡＩ　游戏 🎮  ", 0), "The first Unicode message was incorrectly merged.");
            Check(!duplicates.Accept("ai 游戏 🎮", 2), "Compatibility normalization/case/whitespace did not merge a duplicate.");
            Check(duplicates.Accept("AI 游戏 🎮", 8.1), "A duplicate remained suppressed beyond its eight second window.");
            Check(duplicates.Accept("AI 游戏 🎮🎮", 8.2), "Different Unicode text was collapsed into the same key.");
            Check(duplicates.Accept("这是甲", 10) && duplicates.Accept("这是乙", 10), "Distinct Chinese characters were merged.");
            Check(DanmakuDuplicateTracker.Key("e\u0301") == DanmakuDuplicateTracker.Key("é"), "Combining text elements were not normalized consistently.");
        }
        private static void DisplayAreaAndDensity()
        {
            foreach (double fraction in new[] { 0.25, 0.5, 0.75, 1.0 })
            {
                var settings = new DanmakuSettings(areaFraction: fraction);
                foreach (double y in DanmakuLaneLayout.AllowedRows(720, 44, settings))
                    Check(y >= 0 && y + 44 <= 720 * fraction + 0.01, "A lane escaped the configured display area.");
            }
            foreach (double y in DanmakuLaneLayout.AllowedRows(720, 44, new DanmakuSettings(avoidCenter: true)))
                Check(y + 44 <= 720 * 0.32 || y >= 720 * 0.68, "The optional fixed safe area covered the reserved center.");
            Check(DanmakuLaneLayout.AllowedRows(double.NaN, 44, DanmakuSettings.Default).Count == 0 &&
                DanmakuLaneLayout.AllowedRows(720, 0, DanmakuSettings.Default).Count == 0, "Invalid viewport values created unbounded lanes.");
            using (var overlay = Surface(new DanmakuSettings(areaFraction: 0.25, densityLimit: 4, preventOverlap: false)))
            {
                var items = new List<DanmakuItem>();
                for (int i = 0; i < 500; ++i) items.Add(Item(0, "密度 " + i));
                overlay.Load(items); overlay.Update(TimeSpan.Zero, false);
                Check(overlay.Children.Count == 4, "The configured maximum visible count was not enforced.");
                foreach (var block in overlay.Children) Check(block.Top + Math.Ceiling(block.FontSize * 1.5) <= 180.01,
                    "Rendered text escaped the quarter-screen area.");
            }
        }
        private static void IndependentScrollingAndFixedLayers()
        {
            // A real viewport must expose the first AND final configured row to
            // moving comments, even with both fixed categories already visible.
            foreach (double fraction in new[] { 0.25, 0.5, 0.75, 1.0 })
            foreach (double fontScale in new[] { 0.65, 1.0, 1.8 })
            foreach (bool avoidCenter in new[] { false, true })
            {
                var settings = new DanmakuSettings(areaFraction: fraction, fontScale: fontScale,
                    densityLimit: 64, avoidCenter: avoidCenter, mergeDuplicates: false);
                using (var overlay = Surface(settings))
                {
                    double lineHeight = Math.Ceiling(Math.Max(20, Math.Min(30, 720 / 25d)) * fontScale * 1.5);
                    var allowed = DanmakuLaneLayout.AllowedRows(720, lineHeight, settings);
                    var timeline = new List<DanmakuItem> { Item(0, "fixed-top", 5), Item(0, "fixed-bottom", 4) };
                    for (int i = 0; i < allowed.Count; ++i)
                        timeline.Add(Item((i / 8 + 1) / 10d, "move-" + i));
                    overlay.Load(timeline); overlay.Update(TimeSpan.Zero, false);
                    for (int batch = 1; batch <= (allowed.Count + 7) / 8; ++batch)
                        overlay.Update(TimeSpan.FromSeconds(batch / 10d), false);
                    var rows = new HashSet<double>();
                    TextBlock top = null, bottom = null;
                    foreach (var block in overlay.Children)
                    {
                        if (block.Text == "fixed-top") top = block;
                        else if (block.Text == "fixed-bottom") bottom = block;
                        else
                        {
                            Check(rows.Add(block.Top), "Moving comments collided within their own layer.");
                            Check(Canvas.GetZIndex(block) == 0, "A scrolling comment escaped the lower layer.");
                        }
                    }
                    Check(rows.Count == allowed.Count, "Fixed comments or the old 12-row cap removed scrolling rows.");
                    foreach (double y in allowed)
                        Check(rows.Contains(y), "A configured scrolling row was unavailable while fixed comments were visible.");
                    Check(top != null && bottom != null && rows.Contains(top.Top) && rows.Contains(bottom.Top),
                        "The first/final scrolling rows could not coexist with fixed comments.");
                    Check(Canvas.GetZIndex(top) > 0 && Canvas.GetZIndex(bottom) > 0,
                        "Earlier fixed comments were not explicitly above later scrolling comments.");
                }
            }

            // Verify the opposite admission order: a full scrolling layer must
            // not block fixed comments arriving later, while fixed-to-fixed
            // collision protection remains enabled.
            using (var overlay = Surface(new DanmakuSettings(densityLimit: 64, mergeDuplicates: false)))
            {
                var timeline = new List<DanmakuItem>();
                for (int i = 0; i < 16; ++i) timeline.Add(Item(i < 8 ? 0 : 0.1, "moving " + i));
                overlay.Load(timeline); overlay.Update(TimeSpan.Zero, false);
                overlay.Update(TimeSpan.FromSeconds(0.1), false);
                Check(overlay.Children.Count == 16, "The scrolling plane did not fill the full 720p region.");
                overlay.Add(Item(0.1, "late-top", 5)); overlay.Add(Item(0.1, "late-bottom", 4));
                overlay.Add(Item(0.1, "late-top-2", 5));
                var top = overlay.Children.Find(block => block.Text == "late-top");
                var bottom = overlay.Children.Find(block => block.Text == "late-bottom");
                var top2 = overlay.Children.Find(block => block.Text == "late-top-2");
                Check(top != null && bottom != null && top2 != null,
                    "Existing scrolling comments prevented later fixed messages from being admitted.");
                Check(top.Top == 0 && bottom.Top == 660 && top2.Top != top.Top,
                    "Fixed comments lost their edge positioning or same-layer collision protection.");
                Check(Canvas.GetZIndex(top) == 1 && Canvas.GetZIndex(bottom) == 1,
                    "Fixed messages arriving over scrolling messages were not on the higher plane.");

                // The text pool reuses the same controls across categories.
                foreach (int mode in new[] { 5, 1, 4, 1 })
                {
                    overlay.Reset(); overlay.Load(new[] { Item(0, "pooled", mode) });
                    overlay.Update(TimeSpan.Zero, false);
                    Check(overlay.Children.Count == 1 && Canvas.GetZIndex(overlay.Children[0]) == (mode == 1 ? 0 : 1),
                        "A pooled element retained another category's stacking order.");
                }
            }
        }

        private static void ImmediateSettingsAndRuleReplacement()
        {
            using (var overlay = Surface())
            {
                overlay.Load(new[] { Item(0, "blocked"), Item(0, "allowed"), Item(0, "sender", 1, 0xFFFFFF, "abcd") });
                overlay.Update(TimeSpan.Zero, false);
                overlay.ApplySettings(DanmakuSettings.Default.With(opacity: 0.4), new[]
                {
                    new DanmakuBlockRule { Type = DanmakuBlockRuleType.Keyword, Value = "blocked" },
                    new DanmakuBlockRule { Type = DanmakuBlockRuleType.UserHash, Value = "abcd" }
                });
                Check(overlay.Children.Count == 1 && overlay.Children[0].Text == "allowed" && overlay.Children[0].Opacity == 0.4,
                    "Applying rules/settings left blocked messages visible or skipped immediate opacity.");
                overlay.ApplySettings(overlay.Settings.With(fontScale: 1.5));
                overlay.Update(TimeSpan.FromSeconds(0.2), false);
                Check(overlay.Children.Count == 1 && overlay.Children[0].Text == "allowed", "A local setting change silently dropped account rules.");
                overlay.ApplySettings(DanmakuSettings.Default, Array.Empty<DanmakuBlockRule>());
                Check(overlay.Children.Count == 3, "Unblocking rules did not immediately rebuild eligible paused messages.");
                overlay.Update(TimeSpan.FromSeconds(1), false);
                overlay.Update(TimeSpan.Zero, false);
                Check(overlay.Children.Count == 3, "Clearing account rules did not restore the source timeline for a new seek.");
            }
            using (var overlay = Surface(new DanmakuSettings(showColor: false, showTop: false, showBottom: false)))
            {
                overlay.Load(new[] { Item(0, "white"), Item(0, "color", 1, 0xFF0000), Item(0, "top", 5), Item(0, "bottom", 4) });
                overlay.Update(TimeSpan.Zero, false);
                Check(overlay.Children.Count == 1 && overlay.Children[0].Text == "white", "Type/color switches did not filter the timeline.");
            }
            using (var overlay = Surface())
            {
                overlay.Load(new[] { Item(0, "same"), Item(0, "same"), Item(0, "same") });
                overlay.Update(TimeSpan.Zero, false);
                Check(overlay.Children.Count == 1, "Duplicate merging failed.");
                overlay.ApplySettings(overlay.Settings.With(mergeDuplicates: false));
                overlay.Update(TimeSpan.FromSeconds(1), false); overlay.Update(TimeSpan.Zero, false);
                Check(overlay.Children.Count == 3, "Disabling duplicate merging did not restore original entries.");
            }
        }
        private static void CollisionPolicyAndPoolBudget()
        {
            using (var overlay = Surface(new DanmakuSettings(mergeDuplicates: false)))
            {
                var burst = new List<DanmakuItem>();
                for (int i = 0; i < 2000; ++i) burst.Add(Item(0, "flood " + i));
                int created = TextBlock.Creations, measured = TextBlock.Measurements;
                overlay.Load(burst); overlay.Update(TimeSpan.Zero, false);
                Check(TextBlock.Creations - created <= 12 && TextBlock.Measurements - measured <= 12,
                    "A full lane flood allocated/measured text which could never be shown.");
                var occupied = new HashSet<double>();
                foreach (var block in overlay.Children) Check(occupied.Add(block.Top), "Simultaneous scrolling messages overlapped a row.");
                int firstCreations = TextBlock.Creations, firstMeasures = TextBlock.Measurements;
                for (int i = 0; i < 20; ++i)
                {
                    overlay.Update(TimeSpan.FromSeconds(20), false);
                    overlay.Update(TimeSpan.Zero, false);
                }
                Check(TextBlock.Creations == firstCreations, "Repeated seeks did not reuse the bounded text pool.");
                Check(TextBlock.Measurements == firstMeasures, "Repeated seeks remeasured unchanged text/font/viewport.");
            }
        }
        private static void SpeedAndPausedWorkBudget()
        {
            using (var normal = Surface())
            using (var fast = Surface(new DanmakuSettings(speedScale: 2)))
            {
                normal.Load(new[] { Item(0, "speed") }); fast.Load(new[] { Item(0, "speed") });
                normal.Update(TimeSpan.Zero, false); fast.Update(TimeSpan.Zero, false);
                normal.Update(TimeSpan.FromSeconds(0.5), false); fast.Update(TimeSpan.FromSeconds(0.5), false);
                Check(fast.Children[0].Left < normal.Children[0].Left, "Speed settings did not affect elapsed-time positioning.");
                int writes = TranslateTransform.Writes, creations = TextBlock.Creations, measures = TextBlock.Measurements;
                for (int i = 0; i < 1000; ++i) normal.Update(TimeSpan.FromSeconds(0.5), false);
                Check(TranslateTransform.Writes == writes && TextBlock.Creations == creations && TextBlock.Measurements == measures,
                    "Paused/stalled identical samples kept touching transforms or text layout.");
            }
        }
    }
}
