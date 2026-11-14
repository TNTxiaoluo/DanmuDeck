// TEST ONLY: fixed native API responses, no Xbox, account or network.
namespace System
{
    public static class TestCapabilityTask
    {
        public static Task<T> AsTask<T>(this Task<T> task, CancellationToken token) { token.ThrowIfCancellationRequested(); return task; }
    }
}
namespace Windows.System.Profile
{
    public static class AnalyticsInfo { public static readonly AnalyticsVersionInfo VersionInfo = new AnalyticsVersionInfo(); }
    public sealed class AnalyticsVersionInfo { public string DeviceFamily = "Windows.Xbox"; }
}
namespace Windows.Graphics.Display.Core
{
    public enum HdmiDisplayColorSpace { BT2020, RgbFull }
    public sealed class HdmiDisplayMode
    {
        public uint ResolutionWidthInRawPixels, ResolutionHeightInRawPixels;
        public double RefreshRate;
        public bool StereoEnabled, IsSdrLuminanceSupported, IsSmpte2084Supported;
        public HdmiDisplayColorSpace ColorSpace;
    }
    public sealed class HdmiDisplayInformation
    {
        public static HdmiDisplayInformation GetForCurrentView() => null;
        public IReadOnlyList<HdmiDisplayMode> GetSupportedDisplayModes() => Array.Empty<HdmiDisplayMode>();
    }
}
namespace Windows.Media.Core
{
    public enum CodecKind { Audio }
    public enum CodecCategory { Decoder }
    public static class CodecSubtypes
    {
        public static string AudioFormatDolbyDDPlus => "ddplus";
        public static string AudioFormatDolbyAC3 => "ac3";
        public static string AudioFormatFlac => "flac";
    }
    public sealed class CodecQuery
    {
        public Task<IReadOnlyList<object>> FindAllAsync(CodecKind kind, CodecCategory category, string subtype)
            => Task.FromResult<IReadOnlyList<object>>(Array.Empty<object>());
    }
}
namespace Windows.Media.Protection
{
    public enum ProtectionCapabilityResult { NotSupported = 0, Maybe = 1, Probably = 2 }
    public sealed class ProtectionCapabilities
    {
        internal static Func<string, string, ProtectionCapabilityResult> Query;
        public ProtectionCapabilityResult IsTypeSupported(string type, string key) => Query(type, key);
    }
}
namespace BiliXbox.Services
{
    using Windows.Media.Protection;
    public static class PlaybackCapabilityTests
    {
        private static int _checks;
        private static void Check(bool value, string message) { if (!value) throw new Exception("FAIL: " + message); ++_checks; }
        public static string Run()
        {
            DciOutputSizes();
            var log = new PlaybackCodecSupport.ProbeLog();
            foreach (var result in new[] { ProtectionCapabilityResult.NotSupported, ProtectionCapabilityResult.Maybe, ProtectionCapabilityResult.Probably })
            {
                ProtectionCapabilities.Query = (type, key) => result;
                var actual = PlaybackCodecSupport.CheckDecode("hvc1.2.4.L153.B0", 3840, 2160, 59.94005994, 22000001, 10, log);
                var expected = result == ProtectionCapabilityResult.Probably ? PlaybackCodecSupport.Support.Supported
                    : result == ProtectionCapabilityResult.NotSupported ? PlaybackCodecSupport.Support.Unsupported : PlaybackCodecSupport.Support.Unknown;
                Check(actual == expected, "native result mapped explicitly: " + result);
            }
            Check(log.Details.Contains("Probably") && log.Details.Contains("Maybe") && log.Details.Contains("NotSupported"), "full native query results retained");
            Check(log.Details.Contains("decode-fps=59.94") && log.Details.Contains("decode-bpc=10")
                && log.Details.Contains("decode-bitrate=22001"), "rational-rate normalization, actual bit depth and bitrate ceiling retained");
            var calls = new List<string>();
            ProtectionCapabilities.Query = (type, key) =>
            {
                calls.Add(type); Check(key == "com.microsoft.playready.hardware", "documented hardware query key");
                return type.Contains("hdr=1") ? ProtectionCapabilityResult.NotSupported : ProtectionCapabilityResult.Probably;
            };
            var modes = new PlaybackCodecSupport.DisplayModes(true, Array.Empty<PlaybackCodecSupport.OutputMode>(), true);
            Check(PlaybackCodecSupport.CheckDisplay("hvc1.2", 3840, 2160, 60, 22000000, 10, false, modes, log)
                == PlaybackCodecSupport.Support.Supported, "10bit SDR decode accepted with SDR8 output despite denied HDR");
            Check(PlaybackCodecSupport.CheckDisplay("hvc1.2", 3840, 2160, 60, 22000000, 10, true, modes, log)
                == PlaybackCodecSupport.Support.Unsupported, "HDR output denied independently of SDR");
            Check(calls.Any(type => type.Contains("decode-bpc=10") && type.Contains("display-bpc=8") && !type.Contains("hdr=1")), "SDR query never requires HDR10 output");
            Check(calls.Any(type => type.Contains("display-bpc=10,hdr=1")), "HDR10 query explicitly requests 10bit PQ output");
            double probe, output; bool trial;
            Check(PlaybackFrameRatePolicy.TryGetProbeRate(62.5, out probe, out trial) && probe == 60 && trial,
                "near60 report requires a distinct60fps baseline probe and native validation");
            Check(PlaybackFrameRatePolicy.TryGetProbeRate(63, out probe, out trial) && probe == 60 && trial
                && PlaybackFrameRatePolicy.TryGetProbeRate(63.0001, out probe, out trial) && probe == 60 && trial,
                "both near60 anomalies and declared high frame rates require a separate60fps baseline");
            Check(PlaybackFrameRatePolicy.TryGetProbeRate(120.001, out probe, out trial) && probe == 60 && trial
                && !PlaybackFrameRatePolicy.TryGetProbeRate(120.0011, out probe, out trial),
                "compatibility trial has a strict120.001 reported-frame-rate upper bound");
            Check(PlaybackFrameRatePolicy.TryGetProbeRate(29.41, out probe, out trial) && probe == 29.97 && !trial,
                "ordinary reported rates retain conservative discrete upper probing");
            Check(PlaybackFrameRatePolicy.TryGetProbeRate(120, out probe, out trial) && probe == 60 && trial
                && !PlaybackFrameRatePolicy.TryGetProbeRate(240, out probe, out trial)
                && !PlaybackFrameRatePolicy.TryGetProbeRate(double.NaN, out probe, out trial),
                "120fps can only use a flagged compatibility trial while240fps and invalid metadata are refused");
            Check(PlaybackFrameRatePolicy.TryGetOutputRate(62.5, 60, true, out output) && output == 60,
                "explicitly pending native-validation source can request60Hz output without changing raw metadata");
            Check(!PlaybackFrameRatePolicy.TryGetOutputRate(62.5, 60, false, out output)
                && !PlaybackFrameRatePolicy.TryGetOutputRate(62.5, 62.5, true, out output)
                && !PlaybackFrameRatePolicy.TryGetOutputRate(120, 60, false, out output)
                && !PlaybackFrameRatePolicy.TryGetOutputRate(120, 120, true, out output)
                && !PlaybackFrameRatePolicy.TryGetOutputRate(240, 60, true, out output),
                "output preparation requires explicit trial ownership and an actual60fps baseline rather than unverified120support");
            Check(PlaybackFrameRatePolicy.TryGetOutputRate(120, 60, true, out output) && output == 60,
                "an explicitly flagged120fps compatibility candidate requests only60Hz output");
            var nearLog = new PlaybackCodecSupport.ProbeLog();
            int nearStart = calls.Count;
            Check(PlaybackCodecSupport.CheckDecode("hvc1", 3840, 2160, 62.5, 1861276, 8, nearLog)
                == PlaybackCodecSupport.Support.Supported && calls.Count == nearStart + 1
                && calls.Last().Contains("decode-fps=60"), "screenshot62.5 makes a real baseline hardware query instead of pre-filtering the4K track");
            Check(nearLog.Details.Contains("reported-fps=62.5") && nearLog.Details.Contains("probe-fps=60")
                && nearLog.Details.Contains("nativeTrialRequired=true") && !nearLog.Details.Contains("query=not-issued"),
                "baseline proof log never claims raw62.5fps has been verified");
            var sdr60 = new PlaybackCodecSupport.DisplayModes(true, new[] { new PlaybackCodecSupport.OutputMode
                { Width = 3840, Height = 2160, RefreshRate = 60, Sdr = true } }, true);
            Check(sdr60.CanOutput(3840,2160,62.5,false) && sdr60.CanOutput(3840,2160,120,false)
                && !sdr60.CanOutput(3840,2160,240,false),
                "candidate HDMI eligibility permits only bounded trials against60Hz rather than requesting120Hz");
            Check(PlaybackCodecSupport.CheckDisplay("hvc1",3840,2160,62.5,1861276,8,false,sdr60,nearLog)
                == PlaybackCodecSupport.Support.Supported && nearLog.Details.Contains("output-fps=60"),
                "4K SDR60 mode can establish only the baseline display proof for a near60 trial");
            foreach (double reported in new[] { 119.993, 119.994, 120, 120.001 })
            {
                var highLog = new PlaybackCodecSupport.ProbeLog();
                int start = calls.Count;
                Check(PlaybackCodecSupport.CheckDecode("hvc1",3840,2160,reported,24000000,10,highLog)
                    == PlaybackCodecSupport.Support.Supported && calls.Count == start + 1
                    && calls.Last().Contains("decode-fps=60") && !calls.Last().Contains("decode-fps=120"),
                    "declared high-fps source issues only a documented60fps baseline query: " + reported);
                Check(highLog.Details.Contains("reported-fps=" + reported.ToString("0.###", CultureInfo.InvariantCulture))
                    && highLog.Details.Contains("trial-kind=high-frame-rate-declaration")
                    && highLog.Details.Contains("raw-frame-rate-not-verified"),
                    "highfps diagnostics preserve source declaration without claiming hardware verification: " + reported);
            }
            foreach (double reported in new[] { 62.5, 119.994 })
            foreach (var outcome in new[] { "NotSupported", "Maybe", "exception" })
            {
                int issued = 0;
                ProtectionCapabilities.Query = (type, key) =>
                {
                    ++issued;
                    Check(type.Contains("decode-fps=60") && !type.Contains("decode-fps=" + reported.ToString("0.###", CultureInfo.InvariantCulture)),
                        "an above60 negative or inconclusive result still uses only the documented baseline query");
                    if (outcome == "exception") throw new InvalidOperationException("fixture native query unavailable");
                    return outcome == "NotSupported" ? ProtectionCapabilityResult.NotSupported : ProtectionCapabilityResult.Maybe;
                };
                var refusal = new PlaybackCodecSupport.ProbeLog();
                Check(PlaybackCodecSupport.CheckDisplay("hvc1",3840,2160,reported,1861276,8,false,sdr60,refusal)
                    == (outcome == "NotSupported" ? PlaybackCodecSupport.Support.Unsupported : PlaybackCodecSupport.Support.Unknown),
                    "compatibility trial requires confirmed baseline decode and preserves native refusal versus uncertainty: " + outcome);
                Check(issued == 1 && refusal.Details.Contains("nativeTrialRequired=true")
                    && !refusal.Details.Contains("输出模式=Supported"),
                    "available HDMI output cannot override denied or unconfirmed baseline decoding: " + outcome);
            }
            int before = calls.Count;
            var invalid = new PlaybackCodecSupport.ProbeLog();
            Check(PlaybackCodecSupport.CheckDecode("hvc1", 0, 2160, 30, 10000000, 8, invalid) == PlaybackCodecSupport.Support.Unsupported,
                "missing width retains fail-closed selection with a metadata diagnostic");
            Check(PlaybackCodecSupport.CheckDecode("hvc1", 3840, 2160, double.NaN, 10000000, 8, invalid) == PlaybackCodecSupport.Support.Unsupported,
                "invalid fps retains fail-closed selection");
            Check(PlaybackCodecSupport.CheckDecode("hvc1", 3840, 2160, 30, 0, 8, invalid) == PlaybackCodecSupport.Support.Unsupported,
                "missing bitrate retains fail-closed selection");
            Check(PlaybackCodecSupport.CheckDecode("hvc1", 3840, 2160, 240, 10000000, 8, invalid) == PlaybackCodecSupport.Support.Unknown,
                "API frame-rate limitation stays unconfirmed rather than forging a hardware refusal");
            Check(PlaybackCodecSupport.CheckDisplay("hvc1", 3840, 2160, 30, 10000000, 8, true, null, invalid) == PlaybackCodecSupport.Support.Unsupported,
                "SDR8 metadata cannot stand in for HDR10 input");
            Check(before == calls.Count, "invalid inputs never issue a native capability query");
            Check(invalid.Details.Contains("缺少有效视频尺寸") && invalid.Details.Contains("缺少有效视频帧率")
                && invalid.Details.Contains("缺少有效视频码率") && invalid.Details.Contains("60fps")
                && invalid.Details.Contains("HDR10 轨道位深必须为 10bit") && invalid.Details.Contains("query=not-issued"), "all pre-query refusals carry their exact validation reason");
            Check(!invalid.Details.Contains("=NotSupported"), "pre-query validation is never forged as a native NotSupported result");
            for (int i = 0; i < 12; ++i) log.Add("track-probe-" + i);
            var merged = new PlaybackCodecSupport.ProbeLog(); merged.Merge(log); merged.Merge(invalid); merged.Merge(log); merged.Merge(null); merged.Merge(merged);
            Check(merged.Details.Contains("track-probe-11") && merged.Details.Contains("query=not-issued"), "merge retains late per-track probes and invalid-input details");
            Check(merged.Details.Split(new[] { "track-probe-11" }, StringSplitOptions.None).Length == 2, "merge de-duplicates identical probe results");
            Check(!merged.ToString().Contains("track-probe-11"), "short UI summary remains separate from complete diagnostic details");
            return "PASS: " + _checks + " production capability mapping, SDR/HDR separation and per-query diagnostic assertions (native API stubs).";
        }
        private static void DciOutputSizes()
        {
            int width, height;
            Check(PlaybackOutputSizePolicy.TryGetOutputSize(3996,2160,out width,out height)
                && width==3840 && height==2160, "DCI3996 source requests UHD display surface");
            Check(PlaybackOutputSizePolicy.TryGetOutputSize(4096,2160,out width,out height)
                && width==3840 && height==2160, "DCI4096 source requests UHD display surface");
            Check(PlaybackOutputSizePolicy.TryGetOutputSize(2160,3996,out width,out height)
                && width==3840 && height==2160, "portrait normalization keeps the same bounded DCI policy");
            Check(PlaybackOutputSizePolicy.TryGetOutputSize(3840,1600,out width,out height)
                && width==3840 && height==1600, "ordinary cropped UHD output requirement remains unchanged");
            Check(PlaybackOutputSizePolicy.TryGetOutputSize(2880,2160,out width,out height)
                && width==2880 && height==2160, "4:3 2160P output requirement remains unchanged");
            Check(PlaybackOutputSizePolicy.TryGetOutputSize(7680,4320,out width,out height)
                && width==7680 && height==4320, "8K dimensions are never blindly clamped to UHD");
            Check(PlaybackOutputSizePolicy.TryGetOutputSize(4096,2304,out width,out height)
                && width==4096 && height==2304, "a source taller than DCI4K is not covered by the exception");
            Check(!PlaybackOutputSizePolicy.TryGetOutputSize(0,2160,out width,out height), "invalid source dimensions do not produce valid output");
            var calls = new List<string>();
            ProtectionCapabilities.Query = (type,key) =>
            {
                calls.Add(type);
                return type.Contains("decode-res-x=7680") || type.Contains("hdr=1")
                    ? ProtectionCapabilityResult.NotSupported : ProtectionCapabilityResult.Probably;
            };
            var sdr = new PlaybackCodecSupport.DisplayModes(true,new[] { new PlaybackCodecSupport.OutputMode
                { Width=3840,Height=2160,RefreshRate=60,Sdr=true } },true);
            foreach (int sourceWidth in new[] { 3996,4096 })
            {
                var log = new PlaybackCodecSupport.ProbeLog();
                int start = calls.Count;
                Check(PlaybackCodecSupport.CheckDisplay("hvc1",sourceWidth,2160,25,25000000,8,false,sdr,log)
                    == PlaybackCodecSupport.Support.Supported, "confirmed DCI decode uses supported real UHD SDR mode: "+sourceWidth);
                Check(calls.Count==start+1 && calls.Last().Contains("decode-res-x="+sourceWidth)
                    && calls.Last().Contains("decode-res-y=2160") && !calls.Last().Contains("display-res-x="),
                    "decode query preserves full source dimensions independently of the HDMI surface");
                Check(log.Details.Contains("source="+sourceWidth+"x2160 output=3840x2160"),
                    "mode proof explicitly records distinct source and output dimensions");
                var noModes = new PlaybackCodecSupport.DisplayModes(true,Array.Empty<PlaybackCodecSupport.OutputMode>(),true);
                Check(PlaybackCodecSupport.CheckDisplay("hvc1",sourceWidth,2160,25,25000000,8,false,noModes,log)
                    == PlaybackCodecSupport.Support.Supported && calls.Last().Contains("decode-res-x="+sourceWidth)
                    && calls.Last().Contains("display-res-x=3840,display-res-y=2160"),
                    "combined fallback query requests actual decoder dimensions and distinct UHD output dimensions");
            }
            Check(!sdr.CanOutput(7680,4320,25,false) && !sdr.CanOutput(4097,2160,25,false)
                && !sdr.CanOutput(4096,2304,25,false), "8K and dimensions outside DCI bounds cannot borrow UHD proof");
            Check(PlaybackCodecSupport.CheckDisplay("hvc1",7680,4320,25,25000000,8,false,sdr)
                == PlaybackCodecSupport.Support.Unsupported, "8K native decode refusal is preserved");
            Check(PlaybackCodecSupport.CheckDisplay("hvc1",3996,2160,25,25000000,10,true,sdr)
                == PlaybackCodecSupport.Support.Unsupported, "DCI mapping cannot convert SDR output support into HDR10 support");
            var hdr = new PlaybackCodecSupport.DisplayModes(true,new[] { new PlaybackCodecSupport.OutputMode
                { Width=3840,Height=2160,RefreshRate=60,Hdr10=true } },true);
            Check(PlaybackCodecSupport.CheckDisplay("hvc1",3996,2160,25,25000000,10,true,hdr)
                == PlaybackCodecSupport.Support.Supported, "DCI HDR10 requires independently confirmed actual PQ output");
            int deniedCalls = 0;
            ProtectionCapabilities.Query = (type,key) => { deniedCalls++; return ProtectionCapabilityResult.NotSupported; };
            Check(PlaybackCodecSupport.CheckDisplay("hvc1",3996,2160,25,25000000,8,false,sdr)
                == PlaybackCodecSupport.Support.Unsupported && deniedCalls==1,
                "UHD mode cannot override actual DCI source decoder refusal");
        }
    }
}
