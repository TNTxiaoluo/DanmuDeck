using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Models;
using Newtonsoft.Json.Linq;

namespace BiliXbox.Services
{
    // One quality that can be stored for offline playback: the exact video
    // representation the player would use plus the best standard audio track.
    public sealed class OfflineQualityOption
    {
        public int QualityId { get; set; }
        public string Label { get; set; } = "";
        // sdr, hdr10, dv5 (Dolby Vision output), dv81 (HDR10 base layer),
        // dv84 (HLG base layer, FFmpeg decoding).
        public string Kind { get; set; } = "sdr";
        public string VideoCodec { get; set; } = "";
        public int Width { get; set; }
        public int Height { get; set; }
        public double FrameRate { get; set; }
        public int BitsPerChannel { get; set; }
        public long VideoBandwidth { get; set; }
        public long AudioBandwidth { get; set; }
        public double DurationSeconds { get; set; }
        public IReadOnlyList<string> VideoUrls { get; set; } = Array.Empty<string>();
        public IReadOnlyList<string> AudioUrls { get; set; } = Array.Empty<string>();
        public long EstimatedBytes => (long)((VideoBandwidth + AudioBandwidth) / 8.0 * Math.Max(0, DurationSeconds));
    }

    public sealed partial class BilibiliClient
    {
        // The whole DASH catalogue of one part, highest tier requested, so
        // every quality this account may watch is listed with its own track.
        public async Task<IReadOnlyList<OfflineQualityOption>> GetOfflineOptionsAsync(VideoDetail video,
            CancellationToken ct = default(CancellationToken))
        {
            if (video == null || video.Cid <= 0) throw new ArgumentException("视频信息不完整。", nameof(video));
            var root = await GetPlaybackJsonAsync(new Dictionary<string, string>
            {
                ["bvid"] = video.Bvid, ["cid"] = Invariant(video.Cid),
                ["qn"] = "127", ["fnval"] = "2000", ["fnver"] = "0", ["fourk"] = "1"
            }, ct);
            var data = RequireData(root);
            var dash = data["dash"];
            var rawVideos = dash?["video"] as JArray ?? new JArray();
            if (!DashManifestBuilder.DolbyVisionOutputAvailable && rawVideos.Any(DashManifestBuilder.IsDolbyVision)
                && PlaybackCodecSupport.CaptureDisplayModes().HasDolbyVisionMode)
                DashManifestBuilder.DolbyVisionOutputAvailable = true;
            await CheckDolbyVisionProfile8Async(rawVideos, ct);
            double duration = 0;
            double.TryParse(Str(dash, "duration"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out duration);
            if (duration <= 0) duration = Number(data, "timelength") / 1000.0;
            var audio = (dash?["audio"] as JArray ?? new JArray())
                .Where(track => DashManifestBuilder.Urls(track).Count > 0)
                .OrderByDescending(track => Number(track, "bandwidth")).FirstOrDefault();
            if (audio == null) return Array.Empty<OfflineQualityOption>();
            var labels = new Dictionary<int, string>();
            var ids = data["accept_quality"] as JArray; var names = data["accept_description"] as JArray;
            if (ids != null && names != null)
                for (int i = 0; i < Math.Min(ids.Count, names.Count); ++i)
                    labels[(int)NumberValue(ids[i])] = names[i]?.ToString() ?? "";
            var options = new List<OfflineQualityOption>();
            foreach (var group in rawVideos.Where(track => Number(track, "id") > 0 && PlaybackVideoRejectionReason(track).Length == 0
                    && DashManifestBuilder.Urls(track).Count > 0)
                .GroupBy(track => (int)Number(track, "id")))
            {
                // Smallest stream for the tier: HEVC, then AVC, then AV1.
                var track = group.OrderBy(item => CodecRank(Str(item, "codecs"))).ThenBy(item => Number(item, "bandwidth")).First();
                string kind = DashManifestBuilder.IsDolbyVision(track) ? "dv5"
                    : DashManifestBuilder.IsDolbyVisionBaseLayer(track) ? (DashManifestBuilder.IsDolbyVisionHlgBaseLayer(track) ? "dv84" : "dv81")
                    : DashManifestBuilder.IsHdr10(track) ? "hdr10" : "sdr";
                string label;
                labels.TryGetValue(group.Key, out label);
                options.Add(new OfflineQualityOption
                {
                    QualityId = group.Key, Label = string.IsNullOrWhiteSpace(label) ? "画质 " + group.Key : label, Kind = kind,
                    VideoCodec = Str(track, "codecs"), Width = (int)Number(track, "width"), Height = (int)Number(track, "height"),
                    FrameRate = DashFrameRate(track), BitsPerChannel = VideoBitsPerChannel(track),
                    VideoBandwidth = Number(track, "bandwidth"), AudioBandwidth = Number(audio, "bandwidth"),
                    DurationSeconds = duration, VideoUrls = DashManifestBuilder.Urls(track), AudioUrls = DashManifestBuilder.Urls(audio)
                });
            }
            return options.OrderByDescending(option => option.QualityId).ToArray();
        }

        private static int CodecRank(string codec)
            => codec.StartsWith("hev1.", StringComparison.OrdinalIgnoreCase) || codec.StartsWith("hvc1.", StringComparison.OrdinalIgnoreCase)
                || codec.StartsWith("dvh", StringComparison.OrdinalIgnoreCase) ? 0
                : codec.StartsWith("avc1.", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
    }
}
