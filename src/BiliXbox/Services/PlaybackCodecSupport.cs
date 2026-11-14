using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BiliXbox.Services
{
    // Decode capability and available HDMI output modes are separate. A console
    // whose dashboard is 1080P may still offer a real 4K SDR mode for playback.
    // Only Probably establishes hardware support; unknown results fail closed.
    internal static class PlaybackCodecSupport
    {
        internal enum Support { Unsupported, Supported, Unknown }

        internal static async Task<Support> CheckDolbyDecoderAsync(string codec, CancellationToken token)
            => await CheckAudioDecoderAsync(codec, false, token);

        internal static async Task<Support> CheckFlacDecoderAsync(string codec, CancellationToken token)
            => await CheckAudioDecoderAsync(codec, true, token);

        private static async Task<Support> CheckAudioDecoderAsync(string codec, bool flac, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                string subtype = flac ? string.Equals(codec, "flac", StringComparison.OrdinalIgnoreCase)
                        ? Windows.Media.Core.CodecSubtypes.AudioFormatFlac : null
                    : string.Equals(codec, "ec-3", StringComparison.OrdinalIgnoreCase)
                    ? Windows.Media.Core.CodecSubtypes.AudioFormatDolbyDDPlus
                    : string.Equals(codec, "ac-3", StringComparison.OrdinalIgnoreCase)
                        ? Windows.Media.Core.CodecSubtypes.AudioFormatDolbyAC3 : null;
                if (subtype == null) return Support.Unsupported;
                var codecs = await new Windows.Media.Core.CodecQuery().FindAllAsync(Windows.Media.Core.CodecKind.Audio,
                    Windows.Media.Core.CodecCategory.Decoder, subtype).AsTask(token);
                return codecs != null && codecs.Count > 0 ? Support.Supported : Support.Unsupported;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { return Support.Unknown; }
        }

        internal sealed class ProbeLog
        {
            private readonly List<string> _results = new List<string>();
            private readonly List<string> _details = new List<string>();
            internal void Add(string value) => Add(value, value);
            internal void Add(string summary, string details)
            {
                if (!_results.Contains(summary)) _results.Add(summary);
                if (!_details.Contains(details)) _details.Add(details);
            }
            internal string Details => string.Join("；", _details);
            internal void Merge(ProbeLog other)
            {
                if (other == null || ReferenceEquals(this, other)) return;
                foreach (var value in other._results) if (!_results.Contains(value)) _results.Add(value);
                foreach (var value in other._details) if (!_details.Contains(value)) _details.Add(value);
            }
            public override string ToString() => string.Join("；", _results.Take(6))
                + (_results.Count > 6 ? "；另有 " + (_results.Count - 6) + " 项查询" : "");
        }

        internal sealed class DisplayModes
        {
            internal bool IsXbox { get; }
            internal bool EnumerationSucceeded { get; }
            internal string Diagnostic { get; }
            private readonly IReadOnlyList<OutputMode> _modes;
            internal DisplayModes(bool isXbox, IReadOnlyList<OutputMode> modes, bool enumerated = false,
                string failure = "")
            {
                IsXbox = isXbox; EnumerationSucceeded = enumerated;
                _modes = modes ?? Array.Empty<OutputMode>();
                var largest = _modes.OrderByDescending(mode => (long)mode.Width * mode.Height).FirstOrDefault();
                Diagnostic = !isXbox ? "桌面能力查询" : !enumerated ? "HDMI 模式未确认" + failure
                    : "HDMI 模式 " + _modes.Count.ToString(CultureInfo.InvariantCulture)
                        + (largest == null ? "" : "，最高 " + largest.Width + "×" + largest.Height);
            }

            internal bool CanOutput(int width, int height, double fps, bool hdr)
            {
                if (width <= 0 || height <= 0 || fps <= 0 || double.IsNaN(fps) || double.IsInfinity(fps)) return false;
                double outputRate; bool trial;
                if (!PlaybackFrameRatePolicy.TryGetProbeRate(fps, out outputRate, out trial)) return false;
                int landscapeWidth, landscapeHeight;
                if (!PlaybackOutputSizePolicy.TryGetOutputSize(width, height, out landscapeWidth, out landscapeHeight)) return false;
                return _modes.Any(mode => mode.Width >= landscapeWidth && mode.Height >= landscapeHeight
                    && mode.RefreshRate > 0 && mode.RefreshRate <= 60.001 && mode.RefreshRate + 0.1 >= outputRate
                    && !mode.Stereo && (hdr ? mode.Hdr10 : mode.Sdr));
            }
        }

        internal sealed class OutputMode
        {
            internal uint Width, Height;
            internal double RefreshRate;
            internal bool Stereo, Sdr, Hdr10;
        }

        // Call lazily on the playback view's UI thread before Task.Run. Copy
        // values rather than carrying view-scoped WinRT objects to the worker.
        // Never query or change HDMI modes from application startup.
        internal static DisplayModes CaptureDisplayModes()
        {
            bool xbox;
            try { xbox = Windows.System.Profile.AnalyticsInfo.VersionInfo.DeviceFamily == "Windows.Xbox"; }
            catch (Exception) { return new DisplayModes(true, null); }
            if (!xbox) return new DisplayModes(false, null);
            try
            {
                var information = Windows.Graphics.Display.Core.HdmiDisplayInformation.GetForCurrentView();
                var modes = information?.GetSupportedDisplayModes();
                return new DisplayModes(true, modes?.Select(mode => new OutputMode
                {
                    Width = mode.ResolutionWidthInRawPixels, Height = mode.ResolutionHeightInRawPixels,
                    RefreshRate = mode.RefreshRate, Stereo = mode.StereoEnabled,
                    Sdr = mode.IsSdrLuminanceSupported,
                    Hdr10 = mode.IsSmpte2084Supported
                        && mode.ColorSpace == Windows.Graphics.Display.Core.HdmiDisplayColorSpace.BT2020
                }).ToList(), modes != null);
            }
            catch (Exception ex) { return new DisplayModes(true, null, false,
                " (0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture) + ")"); }
        }

        internal static bool CanDecodeHevcHardware(int width, int height, double framesPerSecond,
            long bitsPerSecond, int bitsPerChannel)
            => CanDecodeHardware("hvc1", width, height, framesPerSecond, bitsPerSecond, bitsPerChannel);

        internal static bool CanDisplay4KHevc(int width, int height, double framesPerSecond,
            long bitsPerSecond, int bitsPerChannel)
            => CanDisplay("hvc1", width, height, framesPerSecond, bitsPerSecond, bitsPerChannel, false);

        internal static bool CanDisplayHdr10(int width, int height, double framesPerSecond, long bitsPerSecond)
            => CanDisplay("hvc1", width, height, framesPerSecond, bitsPerSecond, 10, true);

        internal static bool CanDecodeHardware(string codec, int width, int height, double framesPerSecond,
            long bitsPerSecond, int bitsPerChannel)
            => CheckDecode(codec, width, height, framesPerSecond, bitsPerSecond, bitsPerChannel) == Support.Supported;

        internal static Support CheckDecode(string codec, int width, int height, double framesPerSecond,
            long bitsPerSecond, int bitsPerChannel, ProbeLog log = null)
            => Query(codec, width, height, framesPerSecond, bitsPerSecond, bitsPerChannel, false, false, log);

        internal static bool CanDisplay(string codec, int width, int height, double framesPerSecond,
            long bitsPerSecond, int bitsPerChannel, bool hdr, DisplayModes modes = null)
            => CheckDisplay(codec, width, height, framesPerSecond, bitsPerSecond, bitsPerChannel, hdr, modes) == Support.Supported;

        internal static Support CheckDisplay(string codec, int width, int height, double framesPerSecond,
            long bitsPerSecond, int bitsPerChannel, bool hdr, DisplayModes modes = null, ProbeLog log = null)
        {
            if (modes != null && modes.IsXbox)
            {
                // The documented decode-only query does not depend on the
                // active dashboard resolution. HDMI enumeration establishes
                // the available SDR8-bit / HDR10 output independently.
                var decode = CheckDecode(codec, width, height, framesPerSecond, bitsPerSecond, bitsPerChannel, log);
                if (decode != Support.Supported) return decode;
                if (hdr && bitsPerChannel != 10)
                {
                    log?.Add("HDR10 显示 validation=HDR10 轨道位深必须为 10bit; query=not-issued");
                    return Support.Unsupported;
                }
                if (modes.CanOutput(width, height, framesPerSecond, hdr))
                {
                    double outputRate; bool trial;
                    PlaybackFrameRatePolicy.TryGetProbeRate(framesPerSecond, out outputRate, out trial);
                    int outputWidth, outputHeight;
                    PlaybackOutputSizePolicy.TryGetOutputSize(width, height, out outputWidth, out outputHeight);
                    log?.Add((hdr ? "HDR10" : "SDR") + " 输出模式=Supported " + width + "×" + height
                        + " source=" + width + "x" + height + " output=" + outputWidth + "x" + outputHeight
                        + " output-fps=" + outputRate.ToString("0.###", CultureInfo.InvariantCulture)
                        + " reported-fps=" + framesPerSecond.ToString("0.###", CultureInfo.InvariantCulture)
                        + " nativeTrialRequired=" + trial.ToString());
                    return Support.Supported;
                }
                // Enumeration is constrained by Xbox settings. A limited/empty
                // list is not a replacement for the documented decode/display
                // capability query. Only Probably supplies this second proof;
                // the player must still request a real output mode successfully.
            }
            var display = Query(codec, width, height, framesPerSecond, bitsPerSecond, bitsPerChannel, true, hdr, log);
            // A failed mode query cannot supply the independent negative proof.
            // Keep this tier pending unless hardware decoding itself was denied.
            return display == Support.Unsupported && modes != null && modes.IsXbox && !modes.EnumerationSucceeded
                ? Support.Unknown : display;
        }

        private static Support Query(string codec, int width, int height, double framesPerSecond,
            long bitsPerSecond, int bitsPerChannel, bool display, bool hdr, ProbeLog log)
        {
            codec = codec ?? "";
            string token = codec.StartsWith("avc1", StringComparison.OrdinalIgnoreCase) ? "avc1"
                : codec.StartsWith("hvc1", StringComparison.OrdinalIgnoreCase)
                    || codec.StartsWith("hev1", StringComparison.OrdinalIgnoreCase) ? "hvc1"
                : codec.StartsWith("av01", StringComparison.OrdinalIgnoreCase) ? "av01" : "";
            string input = (token.Length == 0 ? "未知编码" : token) + " " + width + "×" + height
                + " fps=" + framesPerSecond.ToString("0.###", CultureInfo.InvariantCulture)
                + " bps=" + bitsPerSecond.ToString(CultureInfo.InvariantCulture) + " bpc=" + bitsPerChannel
                + " " + (!display ? "解码" : hdr ? "HDR10 显示" : "SDR 显示");
            string invalid = token.Length == 0 ? "未实现此编码的能力查询"
                : width <= 0 || height <= 0 ? "缺少有效视频尺寸"
                : bitsPerSecond <= 0 ? "缺少有效视频码率"
                : bitsPerChannel != 8 && bitsPerChannel != 10 ? "此查询未实现该视频位深"
                : hdr && bitsPerChannel != 10 ? "HDR10 轨道位深必须为 10bit"
                : double.IsNaN(framesPerSecond) || double.IsInfinity(framesPerSecond) || framesPerSecond <= 0
                    ? "缺少有效视频帧率" : "";
            if (invalid.Length > 0)
            {
                log?.Add(input + " validation=" + invalid + "; query=not-issued");
                // Preserve fail-closed selection. The validation annotation
                // distinguishes this policy result from a native refusal.
                return Support.Unsupported;
            }
            double probeRate; bool nativeTrialRequired;
            if (!PlaybackFrameRatePolicy.TryGetProbeRate(framesPerSecond, out probeRate, out nativeTrialRequired))
            {
                log?.Add(input + " validation=媒体能力查询仅能验证 60fps 基准，接口帧率超过 120.001fps 兼容性试播上限; query=not-issued; hardware=unconfirmed");
                return Support.Unknown;
            }
            var rate = probeRate.ToString("0.##", CultureInfo.InvariantCulture);
            if (nativeTrialRequired)
                log?.Add(input + " reported-fps=" + framesPerSecond.ToString("0.###", CultureInfo.InvariantCulture)
                    + " probe-fps=" + rate + " nativeTrialRequired=true trial-kind="
                    + (framesPerSecond <= PlaybackFrameRatePolicy.NearSixtyMetadataMaximum
                        ? "near60-metadata" : "high-frame-rate-declaration") + "; raw-frame-rate-not-verified");
            long kilobits = Math.Max(20000, bitsPerSecond / 1000 + (bitsPerSecond % 1000 == 0 ? 0 : 1));
            string features = "decode-res-x=" + Math.Max(width, height).ToString(CultureInfo.InvariantCulture)
                + ",decode-res-y=" + Math.Min(width, height).ToString(CultureInfo.InvariantCulture)
                + ",decode-bitrate=" + kilobits.ToString(CultureInfo.InvariantCulture)
                + ",decode-fps=" + rate + ",decode-bpc=" + bitsPerChannel.ToString(CultureInfo.InvariantCulture);
            if (display)
            {
                int outputWidth, outputHeight;
                PlaybackOutputSizePolicy.TryGetOutputSize(width, height, out outputWidth, out outputHeight);
                features += ",display-res-x=" + outputWidth.ToString(CultureInfo.InvariantCulture)
                    + ",display-res-y=" + outputHeight.ToString(CultureInfo.InvariantCulture)
                    + ",display-bpc=" + (hdr ? "10" : "8");
                if (hdr) features += ",hdr=1";
            }
            var contentType = "video/mp4;codecs=\"" + token + ",mp4a\";features=\"" + features + "\"";
            string name = token + " " + Math.Max(width, height) + "×" + Math.Min(width, height)
                + " " + rate + "fps " + bitsPerChannel + "bit " + (!display ? "解码" : hdr ? "HDR10 显示" : "SDR 显示");
            try
            {
                var result = new Windows.Media.Protection.ProtectionCapabilities().IsTypeSupported(contentType,
                    "com.microsoft.playready.hardware");
                log?.Add(name + "=" + result, name + "=" + result + " [" + features + "]");
                return result == Windows.Media.Protection.ProtectionCapabilityResult.Probably ? Support.Supported
                    : result == Windows.Media.Protection.ProtectionCapabilityResult.NotSupported ? Support.Unsupported : Support.Unknown;
            }
            catch (Exception ex)
            {
                log?.Add(name + "=未确认 (0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture) + ")");
                return Support.Unknown;
            }
        }
    }
}
