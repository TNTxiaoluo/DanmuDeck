// Actual Windows WinRT runtime probe, compiled for the installed .NET Framework.
// Unlike logic-test fixtures, this uses the real SDK and operating-system media stack.
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.Streaming.Adaptive;
using BiliXbox.Services;

internal static class DashRuntime
{
    [MTAThread]
    private static int Main(string[] args)
    {
        try { return Run(args).GetAwaiter().GetResult(); }
        catch (Exception error)
        {
            // Error text and request URLs may include expiring signed CDN tokens.
            Console.Error.WriteLine("FAIL: runtime {0}, HRESULT 0x{1:X8}", error.GetType().Name, error.HResult);
            return 1;
        }
    }

    private static async Task<int> Run(string[] args)
    {
        if (args.Length < 1) throw new ArgumentException("A local MPD path is required.");
        byte[] bytes = File.ReadAllBytes(args[0]);
        int timeoutSeconds = args.Length > 1 ? int.Parse(args[1]) : 30;
        bool decode = args.Length > 2 && args[2] == "decode";
        var xml = XDocument.Parse(System.Text.Encoding.UTF8.GetString(bytes));
        var ns = xml.Root.Name.Namespace;
        var representations = xml.Descendants(ns + "Representation").ToArray();
        await DashTransportRuntime.Run().ConfigureAwait(false);
        Console.WriteLine("Manifest: {0} representations; maximum video height {1}.", representations.Length,
            representations.Select(item => (int?)item.Attribute("height") ?? 0).DefaultIfEmpty().Max());
        Console.WriteLine("Runtime4K HEVC60 hardware capability query: {0}. This is a capability result, not a decode guarantee.",
            PlaybackCodecSupport.CanDecodeHevcHardware(3840, 2160, 59.94, 20000000, 10));
        if (!AdaptiveMediaSource.IsContentTypeSupported("application/dash+xml"))
            throw new InvalidOperationException("This Windows media stack does not support DASH.");

        using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
        {
            // Prove that the production native callback rewrites BOTH tracks to
            // their supplied backup while preserving native SegmentBase ranges.
            // Invalid primary hosts exist only in this controlled test MPD.
            string[] videoRoutes = null, audioRoutes = null;
            foreach (var adaptation in xml.Descendants(ns + "AdaptationSet"))
            {
                var kind = (string)adaptation.Attribute("contentType");
                var baseUrl = adaptation.Descendants(ns + "BaseURL").Single();
                string actual = baseUrl.Value;
                baseUrl.Value = "https://unavailable.example.test/" + kind + ".m4s";
                if (kind == "video") videoRoutes = new[] { baseUrl.Value, actual };
                else if (kind == "audio") audioRoutes = new[] { baseUrl.Value, actual };
            }
            var creationTask = AdaptivePlaybackSource.OpenAsync(xml.ToString(SaveOptions.DisableFormatting), videoRoutes,
                audioRoutes, new DownloadSpeedMeter(), 1, cancellation.Token);
            if (await Task.WhenAny(creationTask, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds))).ConfigureAwait(false) != creationTask)
                throw new TimeoutException("AdaptiveMediaSource manifest creation timed out.");
            using (var owner = await creationTask.ConfigureAwait(false))
            {
                Console.WriteLine("Production AdaptivePlaybackSource.OpenAsync: Success.");
                var adaptive = (AdaptiveMediaSource)typeof(AdaptivePlaybackSource).GetField("_adaptive",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(owner);
                if (adaptive.InboundBitsPerSecondWindow != TimeSpan.FromSeconds(2))
                    throw new InvalidOperationException("Production DASH source did not configure its native telemetry window.");
                Console.WriteLine("Native download telemetry window: {0:F1} seconds.", adaptive.InboundBitsPerSecondWindow.TotalSeconds);
                Console.WriteLine("Available bitrates: {0}.", string.Join(", ", adaptive.AvailableBitrates));
                if (!decode) return 0;
                int downloads = 0, failures = 0, rewrites = 0, ranged = 0;
                adaptive.DownloadRequested += (sender, request) =>
                {
                    Interlocked.Increment(ref downloads);
                    if (request.Result.ResourceUri != null && request.Result.ResourceUri.Host != "unavailable.example.test") Interlocked.Increment(ref rewrites);
                    if (request.ResourceByteRangeLength.GetValueOrDefault() > 0) Interlocked.Increment(ref ranged);
                };
                adaptive.DownloadFailed += (sender, failure) => Interlocked.Increment(ref failures);
                var opened = new TaskCompletionSource<bool>();
                using (var player = new MediaPlayer { AutoPlay = false, IsMuted = true })
                {
                    player.MediaOpened += (sender, ignored) => opened.TrySetResult(true);
                    player.MediaFailed += (sender, error) =>
                    {
                        Console.Error.WriteLine("MediaFailed: {0}; HRESULT 0x{1:X8}.", error.Error,
                            error.ExtendedErrorCode == null ? 0 : error.ExtendedErrorCode.HResult);
                        opened.TrySetResult(false);
                    };
                    player.Source = owner.Source;
                    player.Play();
                    if (await Task.WhenAny(opened.Task, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds))).ConfigureAwait(false) != opened.Task)
                    {
                        Console.Error.WriteLine("MediaOpened timed out; requests {0}, failed {1}.", downloads, failures);
                        return 3;
                    }
                    if (!await opened.Task.ConfigureAwait(false))
                    {
                        Console.Error.WriteLine("Failed playback: requests {0}; failures {1}; rewrites {2}; ranged {3}.", downloads, failures, rewrites, ranged);
                        return 4;
                    }
                    var session = player.PlaybackSession;
                    Console.WriteLine("MediaOpened: duration {0:F2} seconds, video {1}x{2}.",
                        session.NaturalDuration.TotalSeconds, session.NaturalVideoWidth, session.NaturalVideoHeight);
                    await Task.Delay(2200).ConfigureAwait(false);
                    Console.WriteLine("Playback: {0}; position {1:F2} seconds; requests {2}; failures {3}.",
                        session.PlaybackState, session.Position.TotalSeconds, downloads, failures);
                    Console.WriteLine("Native completed media body bytes: {0}; current native throughput {1:F2} KB/s.",
                        owner.TotalDownloadedBytes, Math.Max(0, owner.CurrentDownloadBytesPerSecond) / 1024);
                    bool advancing = session.Position.TotalSeconds > 0.3;
                    bool counted = owner.TotalDownloadedBytes > 0;
                    bool routed = rewrites == downloads && ranged > 0;
                    Console.WriteLine("Backup rewrite: {0}/{1} requests; {2} native byte ranges; buffered ranges {3}.",
                        rewrites, downloads, ranged, session.GetBufferedRanges().Count);
                    player.Pause();
                    player.Source = null;
                    return !advancing ? 5 : !counted ? 6 : !routed ? 7 : 0;
                }
            }
        }
    }
}

namespace BiliXbox.Services
{
    // Constants-only dependency for the production helper. All media, HTTP,
    // WinRT streams, and player APIs above use real Windows implementations.
    internal static class BilibiliClient
    {
        internal const string Referer = "https://www.bilibili.com/";
        internal const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";
    }
}
