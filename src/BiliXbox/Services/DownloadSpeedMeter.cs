namespace BiliXbox.Services
{
    using System;
    using System.Diagnostics;
    using System.Globalization;

    /// <summary>
    /// Counts media response body bytes over a recent two-second window. This is
    /// application download throughput, not the media's encoded bitrate or a
    /// network connection speed test. A quiet connection naturally falls to zero.
    /// </summary>
    internal sealed class DownloadSpeedMeter
    {
        private const int BucketCount = 20;
        private const double BucketSeconds = 0.1;
        private const double WindowSeconds = BucketCount * BucketSeconds;
        private readonly object _gate = new object();
        private readonly Func<double> _seconds;
        private readonly long[] _bucketTimes = new long[BucketCount];
        private readonly long[] _bucketBytes = new long[BucketCount];
        private readonly double _started;
        private long _totalBytes;
        private bool _hasSample;

        public DownloadSpeedMeter() : this(() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency) { }

        // A monotonic clock can be supplied by deterministic tests.
        internal DownloadSpeedMeter(Func<double> seconds)
        {
            _seconds = seconds ?? throw new ArgumentNullException(nameof(seconds));
            _started = _seconds();
        }

        public void AddReceivedBytes(uint count)
        {
            if (count == 0) return;
            lock (_gate)
            {
                long tick = (long)Math.Floor(_seconds() / BucketSeconds);
                int bucket = (int)(tick % BucketCount);
                if (_bucketTimes[bucket] != tick)
                {
                    _bucketTimes[bucket] = tick;
                    _bucketBytes[bucket] = 0;
                }
                _bucketBytes[bucket] += count;
                _totalBytes += count;
                _hasSample = true;
            }
        }

        // -1 means no response body byte has been observed for this source yet.
        public double CurrentDownloadBytesPerSecond
        {
            get
            {
                lock (_gate)
                {
                    if (!_hasSample) return -1;
                    double now = _seconds();
                    long tick = (long)Math.Floor(now / BucketSeconds);
                    long recentBytes = 0;
                    for (int i = 0; i < BucketCount; i++)
                    {
                        long age = tick - _bucketTimes[i];
                        if (age >= 0 && age < BucketCount) recentBytes += _bucketBytes[i];
                    }
                    // Avoid an initial infinite spike when the first packet and
                    // the UI sample happen in the same clock tick.
                    double duration = Math.Max(0.25, Math.Min(WindowSeconds, now - _started));
                    return recentBytes / duration;
                }
            }
        }

        internal long TotalDownloadedBytes { get { lock (_gate) return _totalBytes; } }

        public static string Format(double bytesPerSecond)
        {
            if (bytesPerSecond < 0 || double.IsNaN(bytesPerSecond) || double.IsInfinity(bytesPerSecond))
                return "等待数据…";
            if (bytesPerSecond == 0) return "0 KB/s";
            if (bytesPerSecond >= 1024 * 1024)
                return (bytesPerSecond / (1024 * 1024)).ToString("0.00", CultureInfo.InvariantCulture) + " MB/s";
            return (bytesPerSecond / 1024).ToString("0.0", CultureInfo.InvariantCulture) + " KB/s";
        }
    }
}
