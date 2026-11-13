// Native WinRT, read-only network test. The unreachable control URL only exists
// here; production selection accepts complete URLs from the playback response.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using BiliXbox.Services;

internal static class RouteSelectorRuntime
{
    [MTAThread]
    private static int Main(string[] args)
    {
        try { return Run(args).GetAwaiter().GetResult(); }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL: native route selection {0}, HRESULT 0x{1:X8}; URLs omitted.", error.GetType().Name, error.HResult);
            return 1;
        }
    }

    private static async Task<int> Run(string[] args)
    {
        var xml = XDocument.Parse(File.ReadAllText(args[0]));
        var ns = xml.Root.Name.Namespace;
        string video = xml.Descendants(ns + "AdaptationSet").Single(element => (string)element.Attribute("contentType") == "video")
            .Descendants(ns + "BaseURL").Single().Value;
        string audio = xml.Descendants(ns + "AdaptationSet").Single(element => (string)element.Attribute("contentType") == "audio")
            .Descendants(ns + "BaseURL").Single().Value;
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            var clock = Stopwatch.StartNew();
            var videoTask = PlaybackRouteSelector.SelectAsync(new[] { "https://unavailable.example.test/video.m4s", video }, false, cancellation.Token);
            var audioTask = PlaybackRouteSelector.SelectAsync(new[] { "https://unavailable.example.test/audio.m4s", audio }, true, cancellation.Token);
            await Task.WhenAll(videoTask, audioTask).ConfigureAwait(false);
            bool selected = videoTask.Result[0] == video && audioTask.Result[0] == audio;
            Console.WriteLine("Native small-range selection: video {0}; audio {1}; total {2:F2}s; signed URLs omitted.",
                videoTask.Result[0] == video, audioTask.Result[0] == audio, clock.Elapsed.TotalSeconds);
            if (!selected || clock.Elapsed > TimeSpan.FromSeconds(2.8)) return 2;
            Console.WriteLine("PASS: actual Windows HttpClient selects valid public audio/video tracks over unavailable controls within a bounded concurrent startup sample. Xbox routing is not established.");
            return 0;
        }
    }
}

namespace BiliXbox.Services
{
    internal static class BilibiliClient
    {
        internal const string Referer = "https://www.bilibili.com/";
        internal const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";
    }
}
