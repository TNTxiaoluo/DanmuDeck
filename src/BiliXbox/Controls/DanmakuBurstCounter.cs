using System;
using System.Collections.Generic;
using System.Linq;
using BiliXbox.Models;
using Windows.UI;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;

namespace BiliXbox.Controls
{
    // 高能弹幕计数: when many viewers send the same short comment within a
    // few seconds, a counter "？×475" is pinned near the top while the
    // ordinary comments keep scrolling. Bold italic white text with a black
    // outline; the number pops as it climbs, and the counter fades in and out.
    public sealed class DanmakuBurstCounter : Grid
    {
        private sealed class Burst
        {
            public string Text;
            public double[] Times;
            public double Start, End;
        }

        private const int Threshold = 8;          // occurrences before the counter shows
        private const double ClusterGap = 4;      // seconds between occurrences of one burst
        private const double Linger = 3;          // seconds the counter stays after the last one
        private const int MaximumTextLength = 12;

        private readonly List<Burst> _bursts = new List<Burst>();
        private readonly TextBlock[] _outline = new TextBlock[8];
        private readonly TextBlock _text;
        private readonly CompositeTransform _scale = new CompositeTransform();
        private Burst _shown;
        private int _shownCount;
        private bool _visible;

        public DanmakuBurstCounter()
        {
            IsHitTestVisible = false;
            Opacity = 0;
            RenderTransform = _scale;
            RenderTransformOrigin = new Windows.Foundation.Point(0, 0.5);
            var offsets = new[] { -2, 0, 2 };
            int index = 0;
            foreach (int x in offsets)
                foreach (int y in offsets)
                {
                    if (x == 0 && y == 0) continue;
                    var outline = MakeText(Colors.Black);
                    outline.RenderTransform = new TranslateTransform { X = x, Y = y };
                    _outline[index++] = outline;
                    Children.Add(outline);
                }
            _text = MakeText(Colors.White);
            Children.Add(_text);
        }

        private static TextBlock MakeText(Color color) => new TextBlock
        {
            FontSize = 40, FontWeight = FontWeights.Bold, FontStyle = FontStyle.Italic,
            Foreground = new SolidColorBrush(color), TextWrapping = TextWrapping.NoWrap
        };

        public void Load(IReadOnlyList<DanmakuItem> items)
        {
            _bursts.Clear();
            Hide(true);
            if (items == null) return;
            foreach (var group in items.Where(item => item != null && !string.IsNullOrWhiteSpace(item.Text)
                    && item.Text.Trim().Length <= MaximumTextLength)
                .GroupBy(item => DanmakuDuplicateTracker.Key(item.Text.Trim()), StringComparer.OrdinalIgnoreCase))
            {
                var times = group.Select(item => item.Time).Where(time => time >= 0).OrderBy(time => time).ToList();
                int start = 0;
                for (int i = 1; i <= times.Count; ++i)
                {
                    if (i < times.Count && times[i] - times[i - 1] <= ClusterGap) continue;
                    int count = i - start;
                    if (count >= Threshold)
                    {
                        var cluster = times.GetRange(start, count).ToArray();
                        _bursts.Add(new Burst
                        {
                            Text = group.First().Text.Trim(), Times = cluster,
                            Start = cluster[Threshold - 1], End = cluster[count - 1] + Linger
                        });
                    }
                    start = i;
                }
            }
            _bursts.Sort((left, right) => left.Start.CompareTo(right.Start));
        }

        public void Reset()
        {
            _bursts.Clear();
            Hide(true);
        }

        // Called with the playback position (several times a second).
        public void Update(TimeSpan position, bool enabled)
        {
            double seconds = position.TotalSeconds;
            Burst best = null;
            int bestCount = 0;
            if (enabled)
            {
                foreach (var burst in _bursts)
                {
                    if (burst.Start > seconds) break;
                    if (burst.End < seconds) continue;
                    int count = CountAt(burst.Times, seconds);
                    if (count > bestCount) { best = burst; bestCount = count; }
                }
            }
            if (best == null) { Hide(false); return; }
            if (!ReferenceEquals(best, _shown) || bestCount != _shownCount)
            {
                bool climbed = ReferenceEquals(best, _shown) && bestCount > _shownCount;
                _shown = best; _shownCount = bestCount;
                string value = best.Text + " ×" + bestCount;
                _text.Text = value;
                foreach (var outline in _outline) outline.Text = value;
                if (climbed) Pop();
            }
            if (!_visible) Fade(1, 0.3);
            _visible = true;
        }

        private static int CountAt(double[] times, double seconds)
        {
            int index = Array.BinarySearch(times, seconds);
            if (index < 0) index = ~index; else ++index;
            while (index < times.Length && times[index] <= seconds) ++index;
            return index;
        }

        private void Hide(bool immediate)
        {
            if (!_visible && !immediate) return;
            _visible = false; _shown = null; _shownCount = 0;
            if (immediate) Opacity = 0;
            else Fade(0, 0.5);
        }

        private void Fade(double to, double seconds)
        {
            var animation = new DoubleAnimation { To = to, Duration = TimeSpan.FromSeconds(seconds), EnableDependentAnimation = false };
            Storyboard.SetTarget(animation, this);
            Storyboard.SetTargetProperty(animation, "Opacity");
            var story = new Storyboard();
            story.Children.Add(animation);
            story.Begin();
        }

        // The number "pops" each time it climbs.
        private void Pop()
        {
            var story = new Storyboard();
            foreach (var property in new[] { "ScaleX", "ScaleY" })
            {
                var animation = new DoubleAnimation { From = 1.18, To = 1, Duration = TimeSpan.FromMilliseconds(220),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
                Storyboard.SetTarget(animation, _scale);
                Storyboard.SetTargetProperty(animation, property);
                story.Children.Add(animation);
            }
            story.Begin();
        }
    }
}
