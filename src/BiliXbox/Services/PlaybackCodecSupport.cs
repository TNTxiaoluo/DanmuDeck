using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace BiliXbox.Services
{
    // Decode capability and available HDMI output modes are separate. A console
    // whose dashboard is 1080P may still offer a real 4K SDR mode for playback.
    // Only Probably establishes hardware support; unknown results fail closed.
    internal static class PlaybackCodecSupport
    {
        internal enum Support { Unsupported, Supported, Unknown }

        internal sealed class ProbeLog
        {
            private readonly List<string> _results = new List<string>();
            internal void Add(string value) { if (!_results.Contains(value)) _results.Add(value); }
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
                int landscapeWidth = Math.Max(width, height), landscapeHeight = Math.Min(width, height);
                return _modes.Any(mode => mode.Width >= landscapeWidth && mode.Height >= landscapeHeight
                    && mode.RefreshRate > 0 && mode.RefreshRate <= 60.001 && mode.RefreshRate + 0.1 >= fps
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
                if (hdr && bitsPerChannel != 10) return Support.Unsupported;
                if (modes.CanOutput(width, height, framesPerSecond, hdr)) return Support.Supported;
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
                    || codec.StartsWith("hev1", StringComparison.OrdinalIgnoreCase) ? "hvc1" : "";
            if (token.Length == 0 || width <= 0 || height <= 0 || bitsPerSecond <= 0
                || (bitsPerChannel != 8 && bitsPerChannel != 10) || (hdr && bitsPerChannel != 10)
                || double.IsNaN(framesPerSecond) || double.IsInfinity(framesPerSecond) || framesPerSecond <= 0)
                return Support.Unsupported;
            var rate = framesPerSecond <= 24.001 ? "24" : framesPerSecond <= 25.001 ? "25"
                : framesPerSecond <= 29.971 ? "29.97" : framesPerSecond <= 30.001 ? "30"
                : framesPerSecond <= 50.001 ? "50" : framesPerSecond <= 59.941 ? "59.94" : framesPerSecond <= 60.001 ? "60" : "";
            if (rate.Length == 0) return Support.Unsupported;
            long kilobits = Math.Max(20000, bitsPerSecond / 1000 + (bitsPerSecond % 1000 == 0 ? 0 : 1));
            string features = "decode-res-x=" + Math.Max(width, height).ToString(CultureInfo.InvariantCulture)
                + ",decode-res-y=" + Math.Min(width, height).ToString(CultureInfo.InvariantCulture)
                + ",decode-bitrate=" + kilobits.ToString(CultureInfo.InvariantCulture)
                + ",decode-fps=" + rate + ",decode-bpc=" + bitsPerChannel.ToString(CultureInfo.InvariantCulture);
            if (display)
            {
                features += ",display-res-x=" + Math.Max(width, height).ToString(CultureInfo.InvariantCulture)
                    + ",display-res-y=" + Math.Min(width, height).ToString(CultureInfo.InvariantCulture)
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
                log?.Add(name + "=" + result);
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
