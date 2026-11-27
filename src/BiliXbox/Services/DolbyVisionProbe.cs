using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Windows.Web.Http;

namespace BiliXbox.Services
{
    // Diagnostic only. When a playurl response offers Dolby Vision (qn 126),
    // record what is needed to choose a playback route: the track's codec and
    // dvcC configuration (profile, level, base-layer compatibility), the
    // platform decoder answers for that codec, and whether the current HDMI
    // connection offers Dolby Vision low-latency modes. Never affects playback.
    internal static class DolbyVisionProbe
    {
        private static int _running;

        internal static void Start(JToken data, Action<string> log)
        {
            if (data == null || log == null) return;
            var tracks = (data["dash"]?["video"] as JArray ?? new JArray()).Where(track => Number(track, "id") == 126).ToList();
            var accept = (data["accept_quality"] as JArray ?? new JArray()).Any(value => value?.ToString() == "126");
            if (tracks.Count == 0 && !accept) return;
            var sb = new StringBuilder("dolby-vision-probe accept126=" + accept + " tracks126=" + tracks.Count);
            AppendHdmi(sb);
            log(sb.ToString());
            if (tracks.Count == 0 || Interlocked.Exchange(ref _running, 1) == 1) return;
            var _ = RunAsync(tracks.Take(3).ToArray(), log);
        }

        private static void AppendHdmi(StringBuilder sb)
        {
            try
            {
                var display = Windows.Graphics.Display.Core.HdmiDisplayInformation.GetForCurrentView();
                if (display == null) { sb.Append(" hdmi=unavailable"); return; }
                var modes = display.GetSupportedDisplayModes();
                int dv = 0, pq = 0;
                var dvModes = new StringBuilder();
                foreach (var mode in modes)
                {
                    bool isDv = false, isPq = false;
                    try { isDv = mode.IsDolbyVisionLowLatencySupported; } catch (Exception) { }
                    try { isPq = mode.IsSmpte2084Supported; } catch (Exception) { }
                    if (isPq) ++pq;
                    if (!isDv) continue;
                    if (++dv <= 8)
                        dvModes.Append(dv == 1 ? "" : ",").Append(mode.ResolutionWidthInRawPixels).Append('x').Append(mode.ResolutionHeightInRawPixels)
                            .Append('@').Append(mode.RefreshRate.ToString("0.##", CultureInfo.InvariantCulture))
                            .Append('/').Append(mode.ColorSpace).Append('/').Append(mode.BitsPerPixel).Append("bpp");
                }
                sb.Append(" hdmiModes=").Append(modes.Count).Append(" pqModes=").Append(pq).Append(" dvLowLatencyModes=").Append(dv);
                if (dv > 0) sb.Append(" dv=[").Append(dvModes).Append(']');
                try
                {
                    var current = display.GetCurrentDisplayMode();
                    if (current != null) sb.Append(" current=").Append(current.ResolutionWidthInRawPixels).Append('x').Append(current.ResolutionHeightInRawPixels)
                        .Append('@').Append(current.RefreshRate.ToString("0.##", CultureInfo.InvariantCulture)).Append('/').Append(current.ColorSpace);
                }
                catch (Exception) { }
            }
            catch (Exception ex) { sb.Append(" hdmi=failed:").Append(ex.HResult.ToString("X8")); }
        }

        private static async Task RunAsync(JToken[] tracks, Action<string> log)
        {
            try
            {
                using (var http = new HttpClient())
                {
                    http.DefaultRequestHeaders.TryAppendWithoutValidation("User-Agent", BilibiliClient.UserAgent);
                    http.DefaultRequestHeaders.Referer = new Uri(BilibiliClient.Referer);
                    foreach (var track in tracks)
                    {
                        string codec = Safe(Str(track, "codecs"));
                        int width = (int)Number(track, "width"), height = (int)Number(track, "height");
                        var sb = new StringBuilder("dolby-vision-track codec=" + codec + " " + width + "x" + height
                            + " codecid=" + Number(track, "codecid") + " bps=" + Number(track, "bandwidth")
                            + " fps=" + Safe(Str(track, "frameRate") + Str(track, "frame_rate")));
                        foreach (string probe in new[] { codec, "dvh1.08.06", "dvhe.05.06", "dvhe.08.06", "hvc1.2.4.L153.90" }.Distinct())
                            foreach (string feature in new[] { "hdr=1", "hdr=dolbyvision", "dv=1" })
                                sb.Append(" ").Append(probe).Append('[').Append(feature).Append("]=").Append(Query(probe, width, height, feature));
                        log(sb.ToString());
                        var segment = track["SegmentBase"] ?? track["segment_base"];
                        string range = Str(segment, "Initialization") + Str(segment, "initialization");
                        var parts = range.Split('-');
                        ulong start, end;
                        string url = Str(track, "baseUrl") + Str(track, "base_url");
                        if (parts.Length != 2 || !ulong.TryParse(parts[0], out start) || !ulong.TryParse(parts[1], out end)
                            || end < start || end - start >= 262144 || !Uri.TryCreate(url, UriKind.Absolute, out Uri uri))
                        { log("dolby-vision-init codec=" + codec + " skipped=invalid-range"); continue; }
                        using (var request = new HttpRequestMessage(HttpMethod.Get, uri))
                        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                        {
                            request.Headers.TryAppendWithoutValidation("Range", "bytes=" + start + "-" + end);
                            using (var response = await http.SendRequestAsync(request).AsTask(timeout.Token))
                            {
                                var buffer = await response.Content.ReadAsBufferAsync().AsTask(timeout.Token);
                                var bytes = new byte[buffer.Length];
                                Windows.Storage.Streams.DataReader.FromBuffer(buffer).ReadBytes(bytes);
                                log("dolby-vision-init codec=" + codec + " http=" + (int)response.StatusCode + " bytes=" + bytes.Length
                                    + " " + Mp4InitDescriber.Describe(bytes));
                            }
                        }
                    }
                }
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            { log("dolby-vision-probe failed=" + ex.GetType().Name + " hresult=" + ex.HResult.ToString("X8")); }
            finally { Interlocked.Exchange(ref _running, 0); }
        }

        private static string Query(string codec, int width, int height, string hdrFeature)
        {
            try
            {
                string features = "decode-res-x=" + Math.Max(3840, width) + ",decode-res-y=" + Math.Max(2160, height)
                    + ",decode-bitrate=20000,decode-fps=60,decode-bpc=10,display-res-x=3840,display-res-y=2160,display-bpc=10," + hdrFeature;
                var type = "video/mp4;codecs=\"" + codec + ",mp4a\";features=\"" + features + "\"";
                return new Windows.Media.Protection.ProtectionCapabilities().IsTypeSupported(type, "com.microsoft.playready.hardware").ToString();
            }
            catch (Exception ex) { return "error:" + ex.HResult.ToString("X8"); }
        }

        private static string Str(JToken token, string name) => token?[name]?.ToString() ?? "";
        private static long Number(JToken token, string name)
        { long value; return long.TryParse(Str(token, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : 0; }
        private static string Safe(string value) => System.Text.RegularExpressions.Regex.Replace(value ?? "", "[^A-Za-z0-9._-]", "");
    }
}
