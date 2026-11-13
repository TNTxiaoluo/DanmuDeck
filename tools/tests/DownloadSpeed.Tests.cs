namespace BiliXbox.DownloadSpeedTests
{
    using System;
    using System.Globalization;
    using System.Threading.Tasks;
    using BiliXbox.Services;

    public static class Tests
    {
        private static int _assertions;
        private static void Check(bool condition, string message)
        {
            _assertions++;
            if (!condition) throw new InvalidOperationException(message);
        }
        private static void Near(double actual, double expected, string message)
            => Check(Math.Abs(actual - expected) < 0.0001, message);

        public static void Run()
        {
            _assertions = 0;
            double now = 0;
            var meter = new DownloadSpeedMeter(() => now);
            Check(meter.CurrentDownloadBytesPerSecond == -1, "An untouched source advertised measured traffic.");
            meter.AddReceivedBytes(0);
            Check(meter.CurrentDownloadBytesPerSecond == -1, "An EOF read fabricated a traffic sample.");
            now = 0.01;
            meter.AddReceivedBytes(1024);
            Near(meter.CurrentDownloadBytesPerSecond, 4096, "The first received packet produced an infinite startup spike.");
            now = 0.5;
            Near(meter.CurrentDownloadBytesPerSecond, 2048, "Throughput did not account for elapsed real time.");
            meter.AddReceivedBytes(1024);
            Near(meter.CurrentDownloadBytesPerSecond, 4096, "Parallel track data was not aggregated.");
            now = 1;
            Near(meter.CurrentDownloadBytesPerSecond, 2048, "A quiet interval retained the original burst speed.");
            now = 2.01;
            Near(meter.CurrentDownloadBytesPerSecond, 512, "Expired packets remained in the recent speed window.");
            now = 2.51;
            Near(meter.CurrentDownloadBytesPerSecond, 0, "A stalled network did not fall to zero.");
            Check(meter.TotalDownloadedBytes == 2048, "Expiry discarded cumulative media byte accounting.");
            now = 500;
            Near(meter.CurrentDownloadBytesPerSecond, 0, "A prolonged stall fabricated network activity.");
            meter.AddReceivedBytes(8192);
            Near(meter.CurrentDownloadBytesPerSecond, 4096, "Traffic did not resume after a prolonged stall.");
            Check(new DownloadSpeedMeter(() => now).CurrentDownloadBytesPerSecond == -1,
                "A replacement playback source inherited the previous source's speed.");
            // Reading the UI property must not consume/reset the rolling rate.
            for (int i = 0; i < 10; i++)
                Near(meter.CurrentDownloadBytesPerSecond, 4096, "Independent UI samples changed download accounting.");

            now = 1000;
            var parallel = new DownloadSpeedMeter(() => now);
            Parallel.For(0, 5000, i => parallel.AddReceivedBytes(1024));
            Check(parallel.TotalDownloadedBytes == 5120000, "Concurrent audio/video response reads lost byte increments.");
            Near(parallel.CurrentDownloadBytesPerSecond, 20480000, "Concurrent reads corrupted the rolling rate.");
            now += 2.1;
            Near(parallel.CurrentDownloadBytesPerSecond, 0, "Concurrent download samples failed to expire.");

            var large = new DownloadSpeedMeter(() => now);
            large.AddReceivedBytes(uint.MaxValue);
            large.AddReceivedBytes(uint.MaxValue);
            Check(large.TotalDownloadedBytes == 2L * uint.MaxValue, "A large media session overflowed 32-bit accounting.");
            Check(large.CurrentDownloadBytesPerSecond > 0, "Large response accounting produced a negative speed.");
            Check(DownloadSpeedMeter.Format(-1) == "等待数据…", "Unsampled traffic was displayed as a measured zero.");
            Check(DownloadSpeedMeter.Format(0) == "0 KB/s", "A stalled network did not display zero KB/s.");
            Check(DownloadSpeedMeter.Format(1234) == "1.2 KB/s", "KB/s formatting is incorrect.");
            Check(DownloadSpeedMeter.Format(1048576) == "1.00 MB/s", "MB/s threshold formatting is incorrect.");
            Check(DownloadSpeedMeter.Format(1310720) == "1.25 MB/s", "MB/s precision is incorrect.");
            Check(DownloadSpeedMeter.Format(double.NaN) == "等待数据…", "Invalid speed was presented as measured throughput.");
            Check(DownloadSpeedMeter.Format(double.PositiveInfinity) == "等待数据…", "Infinite speed was presented as measured throughput.");
            var culture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
                Check(DownloadSpeedMeter.Format(1310720) == "1.25 MB/s", "UI speed decimals changed with desktop locale.");
            }
            finally { CultureInfo.CurrentCulture = culture; }
            Console.WriteLine("PASS: " + _assertions + " download speed assertions: actual bytes, expiry to zero, source isolation, concurrency, large counters, stable KB/s and MB/s.");
        }
    }
}
