using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using BiliXbox.Models;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace BiliXbox.Controls
{
    /// <summary>
    /// A bounded, non-interactive danmaku surface whose coordinates follow media time.
    /// Call Update on the UI thread whenever the player's position or state changes.
    /// </summary>
    public sealed class DanmakuOverlay : Canvas, IDisposable
    {
        private const int MaximumEntries = 100000;
        private const int MaximumDuePerFrame = 96;
        private const int MaximumAdmissionsPerFrame = 12;
        private const double FixedLifetime = 4.5;
        private const double SeekThreshold = 0.75;

        private readonly List<Entry> _sourceEntries = new List<Entry>();
        private readonly List<Entry> _entries = new List<Entry>();
        private readonly List<Visual> _visuals = new List<Visual>();
        private readonly Stack<Visual> _pool = new Stack<Visual>();
        private readonly Dictionary<int, SolidColorBrush> _brushes = new Dictionary<int, SolidColorBrush>();
        private readonly List<double> _scrollRows = new List<double>(), _topRows = new List<double>(), _bottomRows = new List<double>();
        private DanmakuSettings _settings = DanmakuSettings.Default;
        private DanmakuFilter _filter = new DanmakuFilter(null);
        private readonly Stopwatch _playbackClock = new Stopwatch();
        private int _nextEntry;
        private double _anchorSeconds;
        private double _lastRenderedSeconds;
        private double _lastSampleSeconds;
        private double _clockRate = 1;
        private double _lineHeight = 36;
        private double _fontSize = 24;
        private bool _hasPosition;
        private bool _isPlaying;
        private bool _isLoaded;
        private bool _rendering;
        private bool _disposed;

        public DanmakuOverlay()
        {
            IsHitTestVisible = false;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            SizeChanged += OnSizeChanged;
        }

        public DanmakuSettings Settings => _settings;
        public int IgnoredRuleCount => _filter.IgnoredRuleCount;

        // Null rules retain the current account rules; an empty list clears them.
        public void ApplySettings(DanmakuSettings settings, IReadOnlyList<DanmakuBlockRule> rules = null)
        {
            if (_disposed) return;
            var previous = _settings;
            _settings = settings ?? DanmakuSettings.Default;
            if (rules != null) _filter = new DanmakuFilter(rules);
            bool refilter = rules != null || previous.ShowScrolling != _settings.ShowScrolling || previous.ShowTop != _settings.ShowTop ||
                previous.ShowBottom != _settings.ShowBottom || previous.ShowColor != _settings.ShowColor || previous.MergeDuplicates != _settings.MergeDuplicates;
            bool relayout = previous.AreaFraction != _settings.AreaFraction || previous.FontScale != _settings.FontScale ||
                previous.AvoidCenter != _settings.AvoidCenter || previous.ShowScrolling != _settings.ShowScrolling ||
                previous.ShowTop != _settings.ShowTop || previous.ShowBottom != _settings.ShowBottom;
            bool rebuild = refilter || relayout || previous.SpeedScale != _settings.SpeedScale ||
                previous.PreventOverlap != _settings.PreventOverlap || previous.DensityLimit != _settings.DensityLimit;
            if (!rebuild)
            {
                // Opacity and person-mask preferences never re-run cloud regexes,
                // rebuild a timeline, or measure an otherwise unchanged message.
                foreach (var visual in _visuals) visual.Element.Opacity = _settings.Opacity;
                return;
            }
            var retained = new List<Entry>();
            foreach (var visual in _visuals) retained.Add(visual.Entry);
            if (refilter) RebuildEntries();
            if (relayout) RebuildLayout();
            ClearVisuals();
            double seconds = CurrentSeconds();
            foreach (var entry in retained) if (entry.Allowed) TryShow(entry, seconds);
            // Enabling a category/rule immediately restores eligible messages
            // still within their current lifetime, including during a pause.
            int end = UpperBound(seconds);
            int first = Math.Max(LowerBound(Math.Max(0, seconds - 32)), end - MaximumDuePerFrame);
            int admitted = 0;
            for (int i = first; i < end && admitted < MaximumAdmissionsPerFrame; ++i)
                if (TryShow(_entries[i], seconds)) ++admitted;
            _nextEntry = UpperBound(seconds);
            Render(seconds, true);
            UpdateRendering();
        }

        /// <summary>Copies the timeline, discarding invalid or excessively large input.</summary>
        public void Load(IReadOnlyList<DanmakuItem> items)
        {
            if (_disposed) return;
            _sourceEntries.Clear();
            if (items != null)
            {
                int count = Math.Min(items.Count, MaximumEntries);
                for (int i = 0; i < count; i++)
                {
                    Entry entry = CopyEntry(items[i]);
                    if (entry != null) _sourceEntries.Add(entry);
                }
                _sourceEntries.Sort((left, right) => left.Seconds.CompareTo(right.Seconds));
            }
            RebuildEntries();
            ClearVisuals();
            // Loading a timeline must not move the video or unpause playback.
            double seconds = CurrentSeconds();
            _nextEntry = LowerBound(seconds);
            _lastRenderedSeconds = seconds;
            Render(seconds, true);
            UpdateRendering();
        }

        /// <summary>Synchronizes both running and paused playback with the player.</summary>
        public void Update(TimeSpan position, bool isPlaying)
        {
            if (_disposed) return;
            double seconds = Math.Max(0, position.TotalSeconds);
            if (double.IsNaN(seconds) || double.IsInfinity(seconds)) return;
            double expected = CurrentSeconds();
            bool seek = !_hasPosition || Math.Abs(seconds - expected) > SeekThreshold ||
                seconds < _lastSampleSeconds - 0.25;

            // MediaPlaybackSession.Position can repeat between samples. Resetting
            // the animation to that stale value pulls every message backwards.
            // Keep the rendered clock monotonic and correct small drift through
            // its rate; real seeks and pause/buffer transitions still lock to media.
            bool smooth = !seek && _isPlaying && isPlaying;
            _anchorSeconds = smooth ? Math.Max(_lastRenderedSeconds, expected) : seconds;
            _clockRate = smooth ? Math.Max(0.85, Math.Min(1.15, 1 + (seconds - expected) * 0.5)) : 1;
            _lastSampleSeconds = seconds;
            _isPlaying = isPlaying;
            _hasPosition = true;
            if (isPlaying) _playbackClock.Restart();
            else _playbackClock.Reset();

            if (seek)
            {
                ClearVisuals();
                // Start at the destination instead of replaying the skipped interval.
                _nextEntry = LowerBound(seconds);
            }

            Render(CurrentSeconds(), seek);
            UpdateRendering();
        }

        /// <summary>Rewinds and stops the overlay without discarding its timeline.</summary>
        public void Reset()
        {
            if (_disposed) return;
            StopRendering();
            _playbackClock.Reset();
            _isPlaying = false;
            _hasPosition = false;
            _anchorSeconds = 0;
            _lastRenderedSeconds = 0;
            _lastSampleSeconds = 0;
            _clockRate = 1;
            _nextEntry = 0;
            ClearVisuals();
        }

        /// <summary>Adds a successfully posted message to this playback session.</summary>
        public void Add(DanmakuItem item)
        {
            if (_disposed || _sourceEntries.Count >= MaximumEntries) return;
            Entry entry = CopyEntry(item);
            if (entry == null) return;

            int sourceIndex = _sourceEntries.BinarySearch(entry, EntryTimeComparer.Instance);
            _sourceEntries.Insert(sourceIndex < 0 ? ~sourceIndex : sourceIndex, entry);
            if (!Allows(entry)) return;
            if (_settings.MergeDuplicates)
            {
                string key = DanmakuDuplicateTracker.Key(entry.Text);
                for (int i = LowerBound(Math.Max(0, entry.Seconds - 8)); i < _entries.Count && _entries[i].Seconds <= entry.Seconds + 8; ++i)
                    if (string.Equals(key, DanmakuDuplicateTracker.Key(_entries[i].Text), StringComparison.OrdinalIgnoreCase)) return;
            }
            entry.Allowed = true;

            int index = UpperBound(entry.Seconds);
            _entries.Insert(index, entry);
            double seconds = CurrentSeconds();
            bool alreadyDue = entry.Seconds <= seconds;
            if (index < _nextEntry || (index == _nextEntry && alreadyDue)) _nextEntry++;

            // Old timestamps remain in the timeline for a later seek, but should not
            // suddenly appear over the current frame.
            if (alreadyDue && seconds - entry.Seconds <= SeekThreshold &&
                ActualWidth > 0 && ActualHeight > 0 && Visibility == Visibility.Visible)
            {
                RemoveExpired(seconds);
                TryShow(entry, seconds);
            }
        }

        private void OnRendering(object sender, object args)
        {
            if (!_disposed && _isPlaying && _isLoaded) Render(CurrentSeconds());
        }

        private double CurrentSeconds()
        {
            if (!_isPlaying) return _anchorSeconds;
            // If the UI/player stops publishing position, do not let comments
            // keep moving across a frozen video indefinitely.
            return Math.Max(_anchorSeconds, Math.Min(_lastSampleSeconds + 0.25,
                _anchorSeconds + _playbackClock.Elapsed.TotalSeconds * _clockRate));
        }

        private void Render(double seconds, bool force = false)
        {
            // Repeated paused/stalled timestamps need no transforms, allocations,
            // lane scans, or layout work. Explicit settings/load operations force it.
            if (!force && Math.Abs(seconds - _lastRenderedSeconds) < 0.0005) return;
            _lastRenderedSeconds = seconds;
            if (ActualWidth <= 0 || ActualHeight <= 0) return;
            if (Visibility != Visibility.Visible)
            {
                ClearVisuals();
                _nextEntry = UpperBound(seconds);
                return;
            }

            RemoveExpired(seconds);
            int processed = 0, admitted = 0;
            while (_nextEntry < _entries.Count && _entries[_nextEntry].Seconds <= seconds &&
                processed < MaximumDuePerFrame)
            {
                Entry entry = _entries[_nextEntry++];
                processed++;
                // Do not turn a stalled UI frame into a burst of stale comments.
                if (seconds - entry.Seconds <= SeekThreshold && admitted < MaximumAdmissionsPerFrame && TryShow(entry, seconds)) ++admitted;
            }
            if (_nextEntry < _entries.Count && _entries[_nextEntry].Seconds <= seconds)
                _nextEntry = UpperBound(seconds);

            for (int i = 0; i < _visuals.Count; i++)
            {
                Visual visual = _visuals[i];
                if (visual.Mode == 1)
                {
                    double x = ActualWidth - visual.Speed * (seconds - visual.Start);
                    if (Math.Abs(x - visual.LastX) >= 0.25) { visual.Translation.X = x; visual.LastX = x; }
                }
            }
        }

        private void RemoveExpired(double seconds)
        {
            for (int i = _visuals.Count - 1; i >= 0; i--)
            {
                Visual visual = _visuals[i];
                if (seconds >= visual.End)
                {
                    Children.Remove(visual.Element);
                    _visuals.RemoveAt(i);
                    if (_pool.Count < 64) _pool.Push(visual);
                }
            }
        }

        private bool TryShow(Entry entry, double seconds)
        {
            if (_visuals.Count >= _settings.DensityLimit) return false;
            foreach (var current in _visuals) if (ReferenceEquals(current.Entry, entry)) return false;
            double speed = Math.Max(100, ActualWidth / 8.5) * _settings.SpeedScale;
            double x = entry.Mode == 1 ? ActualWidth - speed * (seconds - entry.Seconds) : 0;
            int lane = FindLane(entry.Mode, x, seconds);
            // Do not allocate or measure text which cannot enter a safe lane.
            if (lane < 0) return false;
            Visual visual;
            if (_pool.Count > 0) visual = _pool.Pop();
            else
            {
                var translation = new TranslateTransform();
                visual = new Visual { Element = new TextBlock { FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis,
                    IsHitTestVisible = false, RenderTransform = translation }, Translation = translation };
                SetLeft(visual.Element, 0);
            }
            var block = visual.Element;
            block.Text = entry.Text;
            block.FontSize = _fontSize;
            block.Foreground = ColorBrush(entry.Rgb);
            block.MaxWidth = Math.Max(1, ActualWidth * 0.8);
            block.Opacity = _settings.Opacity;
            if (entry.CachedFont != _fontSize || entry.CachedMaximum != block.MaxWidth || entry.CachedWidth <= 0)
            {
                block.Measure(new Size(block.MaxWidth, _lineHeight));
                entry.CachedWidth = Math.Min(block.MaxWidth, Math.Max(1, block.DesiredSize.Width));
                entry.CachedFont = _fontSize;
                entry.CachedMaximum = block.MaxWidth;
            }
            double width = entry.CachedWidth;
            double end = entry.Seconds + (entry.Mode == 1 ? (ActualWidth + width) / speed : FixedLifetime / _settings.SpeedScale);
            if (end <= seconds) { _pool.Push(visual); return false; }
            if (entry.Mode != 1) x = (ActualWidth - width) / 2;
            double y = Rows(entry.Mode)[lane];
            visual.Translation.X = x;
            SetTop(block, y);
            Children.Add(block);
            visual.Entry = entry; visual.Mode = entry.Mode; visual.Lane = lane; visual.Y = y;
            visual.Start = entry.Seconds; visual.End = end; visual.Width = width; visual.Speed = speed; visual.LastX = x;
            _visuals.Add(visual);
            return true;
        }

        private int FindLane(int mode, double newLeft, double seconds)
        {
            var rows = Rows(mode);
            double gap = _fontSize * 1.5;
            int leastOccupied = -1, leastCount = int.MaxValue;
            for (int lane = 0; lane < rows.Count; lane++)
            {
                bool available = true;
                int occupied = 0;
                for (int i = 0; i < _visuals.Count; i++)
                {
                    Visual existing = _visuals[i];
                    if (Math.Abs(existing.Y - rows[lane]) >= _lineHeight - 0.01) continue;
                    ++occupied;
                    // Every scrolling message shares one speed, so a safe entrance
                    // gap remains safe for the message's entire visible lifetime.
                    double right = ActualWidth - existing.Speed * (seconds - existing.Start) + existing.Width;
                    if (mode != 1 || existing.Mode != 1 || right + gap > newLeft)
                    {
                        available = false;
                        if (_settings.PreventOverlap) break;
                    }
                }
                if (available) return lane;
                if (occupied < leastCount) { leastCount = occupied; leastOccupied = lane; }
            }
            return _settings.PreventOverlap ? -1 : leastOccupied;
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs args)
        {
            if (_disposed) return;
            Clip = new RectangleGeometry { Rect = new Rect(0, 0, Math.Max(0, ActualWidth), Math.Max(0, ActualHeight)) };
            RebuildLayout();
            ClearVisuals();
            double seconds = CurrentSeconds();
            _nextEntry = LowerBound(seconds);
            Render(seconds, true);
        }

        private void RebuildLayout()
        {
            _fontSize = Math.Max(20, Math.Min(30, ActualHeight / 25)) * _settings.FontScale;
            _lineHeight = Math.Ceiling(_fontSize * 1.5);
            _scrollRows.Clear(); _topRows.Clear(); _bottomRows.Clear();
            var allowed = DanmakuLaneLayout.AllowedRows(ActualHeight, _lineHeight, _settings);
            int fixedCount = allowed.Count >= 5 ? Math.Min(3, allowed.Count / 5) : Math.Min(1, allowed.Count);
            if (_settings.ShowTop) for (int i = 0; i < fixedCount; ++i) _topRows.Add(allowed[i]);
            if (_settings.ShowBottom) for (int i = 0; i < fixedCount; ++i) _bottomRows.Add(allowed[allowed.Count - 1 - i]);
            int first = allowed.Count >= 3 && _settings.ShowTop ? fixedCount : 0;
            int last = allowed.Count - (allowed.Count >= 3 && _settings.ShowBottom ? fixedCount : 0);
            if (_settings.ShowScrolling) for (int i = first; i < last && _scrollRows.Count < 12; ++i) _scrollRows.Add(allowed[i]);
        }

        private List<double> Rows(int mode) => mode == 5 ? _topRows : mode == 4 ? _bottomRows : _scrollRows;

        private SolidColorBrush ColorBrush(int rgb)
        {
            SolidColorBrush brush;
            if (_brushes.TryGetValue(rgb, out brush)) return brush;
            brush = new SolidColorBrush(Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
            if (_brushes.Count < 256) _brushes[rgb] = brush;
            return brush;
        }

        private bool Allows(Entry entry) => (_settings.ShowColor || entry.Rgb == 0xFFFFFF) &&
            (entry.Mode == 1 ? _settings.ShowScrolling : entry.Mode == 5 ? _settings.ShowTop : _settings.ShowBottom) &&
            !_filter.Blocks(entry.Text, entry.SenderHash);

        private void RebuildEntries()
        {
            _entries.Clear();
            var duplicates = new DanmakuDuplicateTracker();
            foreach (var entry in _sourceEntries)
            {
                entry.Allowed = Allows(entry) && (!_settings.MergeDuplicates || duplicates.Accept(entry.Text, entry.Seconds));
                if (entry.Allowed) _entries.Add(entry);
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs args)
        {
            _isLoaded = true;
            UpdateRendering();
        }

        private void OnUnloaded(object sender, RoutedEventArgs args)
        {
            _isLoaded = false;
            StopRendering();
            ClearVisuals();
        }

        private void UpdateRendering()
        {
            bool animate = !_disposed && _isPlaying && _isLoaded && Visibility == Visibility.Visible;
            if (animate && !_rendering)
            {
                CompositionTarget.Rendering += OnRendering;
                _rendering = true;
            }
            else if (!animate) StopRendering();
        }

        private void StopRendering()
        {
            if (!_rendering) return;
            CompositionTarget.Rendering -= OnRendering;
            _rendering = false;
        }

        private void ClearVisuals()
        {
            foreach (var visual in _visuals) if (_pool.Count < 64) _pool.Push(visual);
            Children.Clear();
            _visuals.Clear();
        }

        private int LowerBound(double seconds)
        {
            int low = 0;
            int high = _entries.Count;
            while (low < high)
            {
                int mid = low + (high - low) / 2;
                if (_entries[mid].Seconds < seconds) low = mid + 1;
                else high = mid;
            }
            return low;
        }

        private int UpperBound(double seconds)
        {
            int low = 0;
            int high = _entries.Count;
            while (low < high)
            {
                int mid = low + (high - low) / 2;
                if (_entries[mid].Seconds <= seconds) low = mid + 1;
                else high = mid;
            }
            return low;
        }

        private static Entry CopyEntry(DanmakuItem item)
        {
            if (item == null || double.IsNaN(item.Time) || double.IsInfinity(item.Time) || item.Time < 0 ||
                string.IsNullOrWhiteSpace(item.Text)) return null;

            // Count Unicode text elements rather than splitting surrogate pairs or
            // combining sequences. A UTF-16 cap also bounds pathological input.
            var text = new StringBuilder();
            TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(item.Text);
            int count = 0;
            while (count < 120 && elements.MoveNext())
            {
                string element = elements.GetTextElement();
                if (text.Length + element.Length > 256) break;
                for (int i = 0; i < element.Length; i++)
                    text.Append(char.IsControl(element[i]) ? ' ' : element[i]);
                count++;
            }
            string value = text.ToString().Trim();
            if (value.Length == 0) return null;
            return new Entry
            {
                Seconds = item.Time,
                Text = value,
                Rgb = item.Color & 0xFFFFFF,
                Mode = item.Mode == 4 || item.Mode == 5 ? item.Mode : 1,
                SenderHash = item.SenderHash ?? ""
            };
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopRendering();
            _playbackClock.Reset();
            Loaded -= OnLoaded;
            Unloaded -= OnUnloaded;
            SizeChanged -= OnSizeChanged;
            ClearVisuals();
            _entries.Clear();
            _sourceEntries.Clear(); _pool.Clear(); _brushes.Clear();
        }

        private sealed class Entry
        {
            public double Seconds;
            public string Text;
            public int Rgb;
            public int Mode;
            public string SenderHash;
            public bool Allowed;
            public double CachedWidth, CachedFont, CachedMaximum;
        }

        private sealed class EntryTimeComparer : IComparer<Entry>
        {
            public static readonly EntryTimeComparer Instance = new EntryTimeComparer();
            public int Compare(Entry left, Entry right) => left.Seconds.CompareTo(right.Seconds);
        }

        private sealed class Visual
        {
            public TextBlock Element;
            public TranslateTransform Translation;
            public int Mode;
            public int Lane;
            public double Start;
            public double End;
            public double Width;
            public double Speed;
            public double Y, LastX;
            public Entry Entry;
        }
    }
}
