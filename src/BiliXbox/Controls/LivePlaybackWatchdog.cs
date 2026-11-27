using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace BiliXbox.Controls
{
    // Bilibili live HLS addresses carry an expires= timestamp, usually about an
    // hour ahead. After it passes, playlist refreshes are refused and the
    // native player freezes on its last frame or goes black. The watchdog asks
    // for a fresh address shortly before expiry and also when playback stops
    // advancing while the viewer still wants to watch.
    internal sealed class LivePlaybackWatchdog
    {
        public static readonly TimeSpan RefreshLead = TimeSpan.FromMinutes(3);
        public static readonly TimeSpan MinimumRefreshDelay = TimeSpan.FromMinutes(2);
        public static readonly TimeSpan FallbackLifetime = TimeSpan.FromMinutes(45);
        public static readonly TimeSpan PlayingStallLimit = TimeSpan.FromSeconds(12);
        public static readonly TimeSpan WaitingStallLimit = TimeSpan.FromSeconds(15);
        public static readonly TimeSpan LostPlayerDelay = TimeSpan.FromSeconds(2);
        public static readonly TimeSpan BaseBackoff = TimeSpan.FromSeconds(20);
        public static readonly TimeSpan HealthyReset = TimeSpan.FromSeconds(60);
        private static readonly Regex ExpiresPattern = new Regex(@"[?&]expires=(\d{9,11})(?:&|$)", RegexOptions.CultureInvariant);

        private bool _attached, _positionAdvances;
        private DateTimeOffset _refreshAt, _lastProgressAt, _nextAllowed, _healthySince, _lostAt;
        private double _lastPosition;
        private bool _lost;

        public int Attempts { get; private set; }
        public DateTimeOffset RefreshAt => _refreshAt;

        public static DateTimeOffset? ParseExpiry(string address)
        {
            if (string.IsNullOrEmpty(address)) return null;
            var match = ExpiresPattern.Match(address);
            long seconds;
            if (!match.Success || !long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out seconds)) return null;
            try { return DateTimeOffset.FromUnixTimeSeconds(seconds); }
            catch (ArgumentOutOfRangeException) { return null; }
        }

        public void Reset()
        {
            _attached = _positionAdvances = _lost = false;
            Attempts = 0;
            _nextAllowed = DateTimeOffset.MinValue;
        }

        public void Attach(DateTimeOffset now, DateTimeOffset? expires)
        {
            _attached = true;
            _positionAdvances = _lost = false;
            _lastPosition = 0;
            _lastProgressAt = _healthySince = now;
            var earliest = now + MinimumRefreshDelay;
            _refreshAt = expires.HasValue ? expires.Value - RefreshLead : now + FallbackLifetime;
            if (_refreshAt < earliest) _refreshAt = earliest;
        }

        // Returns a reason when a reload should start now, otherwise null.
        public string Sample(DateTimeOffset now, bool wantsPlay, bool reloading, bool hasPlayer, bool playing, double positionSeconds)
        {
            if (!_attached) return null;
            if (!wantsPlay || reloading)
            {
                _lastProgressAt = now; _lastPosition = positionSeconds; _healthySince = now;
                if (!hasPlayer) _lost = false;
                return null;
            }
            if (hasPlayer)
            {
                _lost = false;
                if (playing && positionSeconds > _lastPosition + 0.05)
                {
                    if (_lastPosition > 0) _positionAdvances = true;
                    _lastPosition = positionSeconds; _lastProgressAt = now;
                }
                else if (playing && !_positionAdvances) _lastProgressAt = now;
                else if (positionSeconds < _lastPosition) _lastPosition = positionSeconds;
                if (!playing) _healthySince = now;
                if (Attempts > 0 && now - _healthySince >= HealthyReset && now - _lastProgressAt < PlayingStallLimit) Attempts = 0;
            }
            else if (!_lost) { _lost = true; _lostAt = now; }
            if (now < _nextAllowed) return null;
            string reason = null;
            if (now >= _refreshAt) reason = "expiry";
            else if (!hasPlayer) { if (now - _lostAt >= LostPlayerDelay) reason = "player-lost"; }
            else if (now - _lastProgressAt >= (playing ? PlayingStallLimit : WaitingStallLimit)) reason = playing ? "frozen" : "stalled";
            if (reason == null) return null;
            double factor = Math.Pow(2, Math.Min(3, Attempts));
            _nextAllowed = now + TimeSpan.FromTicks((long)(BaseBackoff.Ticks * factor));
            if (reason != "expiry") ++Attempts;
            _lastProgressAt = _healthySince = now;
            // Until the new source attaches, push the expiry check past the
            // backoff so a failing refresh request is retried, not spammed.
            if (reason == "expiry") _refreshAt = _nextAllowed;
            return reason;
        }
    }
}
