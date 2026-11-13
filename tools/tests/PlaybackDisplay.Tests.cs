// Test-only platform doubles. These do not establish real HDMI/TV capabilities.
namespace Windows.System.Profile
{
    public static class AnalyticsInfo
    {
        public static readonly AnalyticsVersionInfo VersionInfo = new AnalyticsVersionInfo();
    }
    public sealed class AnalyticsVersionInfo { public string DeviceFamily { get; set; } = "Windows.Xbox"; }
}
namespace Windows.Foundation.Metadata
{
    public static class ApiInformation
    {
        public static bool Available = true;
        public static bool IsTypePresent(string name) => Available;
    }
}
namespace Windows.Graphics.Display.Core
{
    public enum HdmiDisplayColorSpace { RgbLimited = 0, RgbFull = 1, BT2020 = 2, BT709 = 3 }
    public enum HdmiDisplayHdrOption { None = 0, EotfSdr = 1, Eotf2084 = 2, DolbyVisionLowLatency = 3 }
    public sealed class HdmiDisplayMode
    {
        private uint _width;
        private bool _sdr = true, _pq, _dv;
        public Exception DescriptorReadError, SdrReadError, PqReadError, DvReadError;
        public uint ResolutionWidthInRawPixels { get { if (DescriptorReadError != null) throw DescriptorReadError; return _width; } set { _width = value; } }
        public uint ResolutionHeightInRawPixels { get; set; }
        public double RefreshRate { get; set; }
        public bool StereoEnabled { get; set; }
        public bool IsSmpte2084Supported { get { if (PqReadError != null) throw PqReadError; return _pq; } set { _pq = value; } }
        public bool IsSdrLuminanceSupported { get { if (SdrReadError != null) throw SdrReadError; return _sdr; } set { _sdr = value; } }
        public bool IsDolbyVisionLowLatencySupported { get { if (DvReadError != null) throw DvReadError; return _dv; } set { _dv = value; } }
        public HdmiDisplayColorSpace ColorSpace { get; set; }
        public uint BitsPerPixel { get; set; } = 24;
        public bool IsEqual(HdmiDisplayMode mode) => mode != null
            && ResolutionWidthInRawPixels == mode.ResolutionWidthInRawPixels
            && ResolutionHeightInRawPixels == mode.ResolutionHeightInRawPixels
            && RefreshRate == mode.RefreshRate && StereoEnabled == mode.StereoEnabled
            && IsSmpte2084Supported == mode.IsSmpte2084Supported && ColorSpace == mode.ColorSpace;
    }
    public sealed class HdmiDisplayInformation
    {
        public static HdmiDisplayInformation Current;
        public static int ViewCalls;
        public List<HdmiDisplayMode> Modes = new List<HdmiDisplayMode>();
        public int Queries, Requests, Resets, ModeOnlyRequests, HdrRequests, CurrentQueries;
        public Exception EnumerationError, CurrentModeError;
        public bool ApplyRequestedMode = true;
        public HdmiDisplayMode RequestedMode;
        public HdmiDisplayMode CurrentMode;
        public HdmiDisplayHdrOption RequestedOption;
        public Func<Task<bool>> Request = () => Task.FromResult(true);
        public Func<HdmiDisplayMode, HdmiDisplayHdrOption?, Task<bool>> RequestMode;
        public Func<Task> Reset = () => Task.CompletedTask;
        public static HdmiDisplayInformation GetForCurrentView() { ViewCalls++; return Current; }
        public IReadOnlyList<HdmiDisplayMode> GetSupportedDisplayModes()
        { Queries++; if (EnumerationError != null) throw EnumerationError; return Modes; }
        public HdmiDisplayMode GetCurrentDisplayMode()
        { CurrentQueries++; if (CurrentModeError != null) throw CurrentModeError; return CurrentMode; }
        public Task<bool> RequestSetCurrentDisplayModeAsync(HdmiDisplayMode mode)
        { ModeOnlyRequests++; return RequestCore(mode, null); }
        public Task<bool> RequestSetCurrentDisplayModeAsync(HdmiDisplayMode mode, HdmiDisplayHdrOption hdr)
        { HdrRequests++; return RequestCore(mode, hdr); }
        private async Task<bool> RequestCore(HdmiDisplayMode mode, HdmiDisplayHdrOption? hdr)
        {
            Requests++; RequestedMode = mode; RequestedOption = hdr ?? HdmiDisplayHdrOption.None;
            bool changed = await (RequestMode != null ? RequestMode(mode, hdr) : Request());
            if (changed && ApplyRequestedMode) CurrentMode = mode;
            return changed;
        }
        public Task SetDefaultDisplayModeAsync() { Resets++; return Reset(); }
    }
}
namespace BiliXbox.PlaybackDisplayTests
{
    public static class Tests
    {
        private static int _checks;
        private static void Check(bool value, string message)
        { _checks++; if (!value) throw new InvalidOperationException("FAIL: " + message); }
        private static HdmiDisplayMode Mode(uint width = 3840, uint height = 2160, double fps = 60, bool hdr = false)
            => new HdmiDisplayMode { ResolutionWidthInRawPixels = width, ResolutionHeightInRawPixels = height,
                RefreshRate = fps, IsSmpte2084Supported = hdr,
                ColorSpace = hdr ? HdmiDisplayColorSpace.BT2020 : HdmiDisplayColorSpace.RgbLimited };
        private static PlaybackInfo Stream(int width = 3840, int height = 2160, double fps = 30, bool hdr = false)
            => new PlaybackInfo { VideoWidth = width, VideoHeight = height, VideoFrameRate = fps,
                FourKDisplayVerified = Math.Max(width, height) >= 3000, IsHdr10 = hdr, Hdr10DisplayVerified = hdr };
        private static HdmiDisplayInformation Device(params HdmiDisplayMode[] modes)
        {
            AnalyticsInfo.VersionInfo.DeviceFamily = "Windows.Xbox";
            ApiInformation.Available = true;
            HdmiDisplayInformation.ViewCalls = 0;
            return HdmiDisplayInformation.Current = new HdmiDisplayInformation { Modes = modes.ToList() };
        }
        public static async Task<string> Run()
        {
            await OrdinaryDesktopAndUnavailable();
            await ModeSelection();
            await Dashboard1080ToSdr4K();
            await IndependentCapabilityAndLegacyDescriptors();
            await InvalidArgumentAndAlternateCandidates();
            await PostRequestModeVerification();
            await NativeReadFailures();
            await BaselineResetAndCancellation();
            await RepeatedPreparation();
            await FalseAndCleanup();
            await CancellationAndSerialization();
            return "PASS: " + _checks + " production HDMI selection, HDR/SDR, no120Hz, cancellation and reset ownership assertions (test platform, no device mode changes).";
        }
        private static async Task OrdinaryDesktopAndUnavailable()
        {
            var device = Device(Mode());
            var controller = new Services.PlaybackDisplayController();
            Check(HdmiDisplayInformation.ViewCalls == 0, "constructing the page does not activate Xbox HDMI APIs");
            Check(await controller.PrepareAsync(Stream(1920, 1080), CancellationToken.None), "ordinary SDR uses default display");
            await controller.ResetAsync();
            Check(device.Requests == 0 && device.Resets == 0, "ordinary playback never resets a mode it did not own");
            AnalyticsInfo.VersionInfo.DeviceFamily = "Windows.Desktop";
            Check(await controller.PrepareAsync(Stream(hdr: true), CancellationToken.None), "desktop media remains usable");
            Check(!controller.IsXbox && !controller.Hdr10Active && !controller.FourKActive && HdmiDisplayInformation.ViewCalls == 0,
                "desktop does not claim an Xbox HDMI mode or touch its APIs");
            AnalyticsInfo.VersionInfo.DeviceFamily = "Windows.Xbox";
            ApiInformation.Available = false;
            Check(!await controller.PrepareAsync(Stream(hdr: true), CancellationToken.None), "unavailable HDMI API does not report HDR success");
            ApiInformation.Available = true;
            HdmiDisplayInformation.Current = null;
            Check(!await controller.PrepareAsync(Stream(), CancellationToken.None), "missing current-view HDMI info is unavailable");
        }
        private static async Task ModeSelection()
        {
            var m120 = Mode(fps: 120, hdr: true);
            var m60 = Mode(hdr: true); m60.BitsPerPixel = 20; // valid 10-bit YCbCr422 is not 30 bits/pixel.
            var m30 = Mode(fps: 30, hdr: true);
            var stereo = Mode(hdr: true); stereo.StereoEnabled = true;
            var wrongGamut = Mode(hdr: true); wrongGamut.ColorSpace = HdmiDisplayColorSpace.RgbLimited;
            var device = Device(m120, stereo, wrongGamut, m30, m60, Mode(1920, 1080, hdr: true));
            var controller = new Services.PlaybackDisplayController();
            Check(await controller.PrepareAsync(Stream(hdr: true), CancellationToken.None), "confirmed HDR10 requests a real HDR mode");
            Check(device.RequestedMode == m60 && device.RequestedOption == HdmiDisplayHdrOption.Eotf2084,
                "HDR uses BT2020/PQ 60Hz, not 120Hz/stereo/wrong gamut, and accepts YCbCr422");
            Check(controller.Hdr10Active && controller.FourKActive, "active flags reflect successful mode request");
            Check(await controller.PrepareAsync(Stream(2160, 3840, hdr: true), CancellationToken.None), "portrait 4K selects a covering landscape output");
            Check(device.RequestedMode == m60, "portrait normalization does not require impossible tall HDMI mode");
            Check(await controller.PrepareAsync(Stream(1920, 1080, hdr: true), CancellationToken.None), "1080P HDR is independent of4K source");
            Check(device.RequestedMode.ResolutionWidthInRawPixels == 1920 && controller.Hdr10Active && !controller.FourKActive,
                "1080P HDR chooses adequate mode and does not claim4K output");
            Check(await controller.PrepareAsync(Stream(1920, 1080), CancellationToken.None), "SDR afterHDR restores default");
            Check(device.Resets == 1 && !controller.Hdr10Active && !controller.FourKActive, "HDR flags cleared afterSDR restore");
            device.Modes = new List<HdmiDisplayMode> { Mode(fps: 120, hdr: true), Mode(1920, 1080, hdr: true) };
            Check(!await controller.PrepareAsync(Stream(hdr: true), CancellationToken.None), "120Hz-only4K output is rejected even though lower-resHDR exists");
            device.Modes = new List<HdmiDisplayMode> { Mode() };
            Check(await controller.PrepareAsync(Stream(), CancellationToken.None), "freshlychanged display modes are re-enumerated");
            Check(device.RequestedOption == HdmiDisplayHdrOption.EotfSdr && !controller.Hdr10Active && controller.FourKActive,
                "4K SDR explicitly requestsSDR transfer");
            device.Modes = new List<HdmiDisplayMode> { Mode(2560, 1440, hdr: true) };
            Check(await controller.PrepareAsync(Stream(2560, 1440, hdr: true), CancellationToken.None), "1440P HDR does not need a4K source flag");
            await controller.ResetAsync();
            Check(device.Queries == 6, "each nondefault preparation reads current supported modes");
        }
        private static async Task FalseAndCleanup()
        {
            var device = Device(Mode(hdr: true));
            var controller = new Services.PlaybackDisplayController();
            device.Request = () => Task.FromResult(false);
            Check(!await controller.PrepareAsync(Stream(hdr: true), CancellationToken.None), "OSdenial is not playback-output success");
            await controller.ResetAsync();
            Check(device.Resets == 0 && !controller.Hdr10Active && !controller.FourKActive, "denied request cannot claim ownership");
            device.Request = () => Task.FromResult(true);
            Check(await controller.PrepareAsync(Stream(hdr: true), CancellationToken.None), "later successful request can recover");
            int resets = 0;
            device.Reset = () => { resets++; if (resets == 1) throw new InvalidOperationException("simulatedOSfailure"); return Task.CompletedTask; };
            await controller.ResetAsync();
            Check(!controller.Hdr10Active && !controller.FourKActive && device.Resets == 1, "failed reset clears unconfirmed active indicators");
            await controller.ResetAsync();
            Check(device.Resets == 2, "failed reset retains ownership for explicit cleanup retry");
            await controller.ResetAsync();
            Check(device.Resets == 2, "successful cleanup releases ownership and avoids redundant resets");
            await controller.PrepareAsync(Stream(hdr: true), CancellationToken.None);
            var unverified = Stream(hdr: true); unverified.Hdr10DisplayVerified = false;
            Check(!await controller.PrepareAsync(unverified, CancellationToken.None) && device.Resets == 3,
                "unverifiedHDR request restores priorownedmode before refusal");
            Check(!await controller.PrepareAsync(Stream(fps: 120), CancellationToken.None), "source above60fps not sent tomedia HDMI mode");
            device.Request = () => { throw new InvalidOperationException("simulatedHDMIunavailable"); };
            Check(!await controller.PrepareAsync(Stream(), CancellationToken.None), "APIexception degrades to unavailable without crash");
        }
        private static async Task Dashboard1080ToSdr4K()
        {
            var fourK8bit = Mode(); fourK8bit.BitsPerPixel = 24;
            var dashboard = Mode(1920, 1080);
            var device = Device(dashboard, fourK8bit);
            device.CurrentMode = dashboard;
            var controller = new Services.PlaybackDisplayController();
            Check(await controller.PrepareAsync(Stream(), CancellationToken.None),
                "current1080 dashboard does not prevent a supported4K SDR media output");
            Check(device.Requests == 1 && device.RequestedMode == fourK8bit
                && device.RequestedOption == HdmiDisplayHdrOption.EotfSdr && controller.FourKActive && !controller.Hdr10Active,
                "actual playback requests the real3840x2160 eight-bit SDR mode instead of relabeling1080");
            Check(!await controller.PrepareAsync(Stream(hdr: true), CancellationToken.None)
                && device.Resets == 1 && !controller.Hdr10Active && !controller.FourKActive,
                "fourK eight-bit support cannot be treated as HDR10 output");
        }
        private static async Task RepeatedPreparation()
        {
            var hdr = Mode(hdr: true);
            var device = Device(hdr, Mode());
            // The OS mode can initially compare equal while its EOTF is unknown.
            device.CurrentMode = hdr;
            var controller = new Services.PlaybackDisplayController();
            Check(await controller.PrepareAsync(Stream(hdr: true), CancellationToken.None) && device.Requests == 1,
                "equal initial OSmode still establishes its unknownEOTF once");
            Check(await controller.PrepareAsync(Stream(hdr: true), CancellationToken.None) && device.Requests == 1,
                "repeatedPlay/resume does not repeat an owned HDR handshake");
            Check(controller.Hdr10Active && controller.FourKActive && device.Queries == 2,
                "skipped nativehandshake still re-enumerates modes and preserves actualactiveflags");
            device.CurrentMode = Mode(1920, 1080);
            Check(await controller.PrepareAsync(Stream(hdr: true), CancellationToken.None) && device.Requests == 2,
                "externalmode change forces re-establishing HDR output");
            // Same mode descriptor can support SDR and HDR, so EOTF ownership
            // must be compared separately from the OS mode's equality.
            Check(await controller.PrepareAsync(Stream(), CancellationToken.None) && device.Requests == 3
                && device.RequestedOption == HdmiDisplayHdrOption.EotfSdr,
                "changing ownedEOTF fromHDR toSDR requests the native output");
            await controller.ResetAsync();
            Check(await controller.PrepareAsync(Stream(), CancellationToken.None) && device.Requests == 4,
                "reset clears ownedEOTF even when OSmode remains equal");
            device.Reset = () => { throw new InvalidOperationException("simulatedresetfailure"); };
            await controller.ResetAsync();
            device.Reset = () => Task.CompletedTask;
            Check(await controller.PrepareAsync(Stream(), CancellationToken.None) && device.Requests == 5,
                "failed reset also invalidates EOTF knowledge before reuse");
            await controller.ResetAsync();
        }
        private static async Task IndependentCapabilityAndLegacyDescriptors()
        {
            foreach (var space in new[] { HdmiDisplayColorSpace.RgbLimited, HdmiDisplayColorSpace.RgbFull, HdmiDisplayColorSpace.BT709 })
            {
                var legacy = Mode(); legacy.IsSdrLuminanceSupported = false; legacy.ColorSpace = space;
                var dashboard = Mode(1920, 1080);
                var device = Device(dashboard, legacy); device.CurrentMode = dashboard;
                var controller = new Services.PlaybackDisplayController();
                device.Request = () => Task.FromResult(false);
                Check(!await controller.PrepareAsync(Stream(), CancellationToken.None)
                    && device.RequestedMode == legacy && !controller.FourKActive
                    && controller.FailureReason.Contains("拒绝"),
                    "combined display proof may try a real baseline4K descriptor, but OS denial never claims4K: " + space);
                device.Request = () => Task.FromResult(true);
                Check(await controller.PrepareAsync(Stream(), CancellationToken.None) && controller.FourKActive
                    && !controller.Hdr10Active && controller.FailureReason.Length == 0
                    && device.ModeOnlyRequests == 2 && device.HdrRequests == 0,
                    "confirmed baselineSDR descriptor withoutSDR flag uses a successful real mode-only request: " + space);
                await controller.ResetAsync();
            }
            var only1080 = Device(Mode(1920, 1080));
            var missing = new Services.PlaybackDisplayController();
            Check(!await missing.PrepareAsync(Stream(), CancellationToken.None) && only1080.Requests == 0
                && !missing.FourKActive && missing.FailureReason.Contains("允许 4K"),
                "combinedProbably may expose a menu tier but cannot fabricate a4K output absent from OS modes");
            var hdrWithoutPq = Mode(); hdrWithoutPq.ColorSpace = HdmiDisplayColorSpace.BT2020;
            hdrWithoutPq.IsSdrLuminanceSupported = false;
            var deviceHdr = Device(hdrWithoutPq);
            var controllerHdr = new Services.PlaybackDisplayController();
            Check(!await controllerHdr.PrepareAsync(Stream(hdr: true), CancellationToken.None)
                && deviceHdr.Requests == 0 && !controllerHdr.Hdr10Active,
                "a BT2020 descriptor withoutPQ can never receive the SDR compatibility exception forHDR");
        }
        private static Exception InvalidArgument()
            => new System.Runtime.InteropServices.COMException("simulated native invalid argument", unchecked((int)0x80070057));
        private static async Task InvalidArgumentAndAlternateCandidates()
        {
            var baseline = Mode();
            var device = Device(baseline);
            device.RequestMode = (mode, option) =>
            {
                if (option.HasValue) throw InvalidArgument();
                return Task.FromResult(true);
            };
            var controller = new Services.PlaybackDisplayController();
            Check(await controller.PrepareAsync(Stream(), CancellationToken.None)
                && device.HdrRequests == 1 && device.ModeOnlyRequests == 1 && controller.FourKActive && !controller.Hdr10Active,
                "explicitSDR E_INVALIDARG retries the same proven nonHDR baseline descriptor using the mode-only overload");
            await controller.ResetAsync();

            foreach (bool dv in new[] { false, true })
            {
                var ambiguous = Mode(); ambiguous.IsSdrLuminanceSupported = false;
                ambiguous.IsSmpte2084Supported = !dv; ambiguous.IsDolbyVisionLowLatencySupported = dv;
                device = Device(ambiguous); controller = new Services.PlaybackDisplayController();
                Check(!await controller.PrepareAsync(Stream(), CancellationToken.None)
                    && device.Requests == 0 && !controller.FourKActive,
                    "missingSDR flag cannot imply baselineSDR for a descriptor advertising " + (dv ? "DolbyVision" : "PQ"));
            }

            var hdr = Mode(hdr: true);
            device = Device(hdr); device.Request = () => { throw InvalidArgument(); };
            controller = new Services.PlaybackDisplayController();
            Check(!await controller.PrepareAsync(Stream(hdr: true), CancellationToken.None)
                && device.HdrRequests == 1 && device.ModeOnlyRequests == 0 && !controller.Hdr10Active,
                "HDR E_INVALIDARG never retries with an unqualified mode-only request");

            foreach (bool throws in new[] { false, true })
            {
                var rejected = Mode(); rejected.IsSmpte2084Supported = true;
                var accepted = Mode(fps: 59.94);
                device = Device(rejected, accepted);
                device.RequestMode = (mode, option) =>
                {
                    if (mode == rejected)
                    {
                        if (throws) throw InvalidArgument();
                        return Task.FromResult(false);
                    }
                    return Task.FromResult(true);
                };
                controller = new Services.PlaybackDisplayController();
                Check(await controller.PrepareAsync(Stream(), CancellationToken.None)
                    && device.RequestedMode == accepted && device.Requests == 2
                    && device.ModeOnlyRequests == 0 && controller.FourKActive,
                    "another real supported candidate is attempted after first candidate " + (throws ? "E_INVALIDARG" : "denial"));
                await controller.ResetAsync();
            }
        }
        private static async Task PostRequestModeVerification()
        {
            var already = Mode(); var matching = Device(already); matching.CurrentMode = already;
            var borrowed = new Services.PlaybackDisplayController();
            Check(await borrowed.PrepareAsync(Stream(), CancellationToken.None)
                && matching.Requests == 0 && matching.CurrentQueries > 0 && borrowed.FourKActive && !borrowed.Hdr10Active,
                "an exact alreadycurrent SDR-only4K mode is verified without an unnecessary nativeEOTF request");
            await borrowed.ResetAsync();
            Check(matching.Resets == 0, "an alreadycurrent borrowed SDR-onlymode does not acquire resetownership");
            foreach (bool absent in new[] { false, true })
            {
                var device = Device(Mode()); device.ApplyRequestedMode = false;
                device.CurrentMode = absent ? null : Mode(1920, 1080);
                var controller = new Services.PlaybackDisplayController();
                Check(!await controller.PrepareAsync(Stream(), CancellationToken.None)
                    && device.Requests > 0 && device.CurrentQueries > 0
                    && !controller.FourKActive && !controller.Hdr10Active && device.Resets == 1,
                    "a native true result with " + (absent ? "missing" : "still1080") + " currentmode does not claim4K and restores ownership");
            }
            var failing = Device(Mode()); failing.CurrentModeError = InvalidArgument();
            var failed = new Services.PlaybackDisplayController();
            Check(!await failed.PrepareAsync(Stream(), CancellationToken.None)
                && failing.Requests > 0 && failing.Resets == 1 && !failed.FourKActive
                && failed.FailureReason.Contains("80070057"),
                "post-request currentmode getter failure clears activeflags and cleans up an accepted nativechange");
        }
        private static async Task NativeReadFailures()
        {
            var device = Device(Mode()); device.EnumerationError = InvalidArgument();
            var controller = new Services.PlaybackDisplayController();
            Check(!await controller.PrepareAsync(Stream(), CancellationToken.None)
                && device.Requests == 0 && controller.FailureReason.Contains("80070057"),
                "enumeration E_INVALIDARG fails safely before attempting any displaychange");
            string enumerationReason = controller.FailureReason;

            var unreadable = Mode(); unreadable.DescriptorReadError = InvalidArgument();
            var valid = Mode(fps: 59.94);
            device = Device(unreadable, valid); controller = new Services.PlaybackDisplayController();
            Check(await controller.PrepareAsync(Stream(), CancellationToken.None)
                && device.RequestedMode == valid && controller.FourKActive,
                "one unreadable native descriptor does not abort other readable supported4K modes");
            await controller.ResetAsync();

            var unreadableSdr = Mode(); unreadableSdr.SdrReadError = InvalidArgument();
            device = Device(unreadableSdr, valid); controller = new Services.PlaybackDisplayController();
            Check(await controller.PrepareAsync(Stream(), CancellationToken.None)
                && device.RequestedMode == valid && controller.FourKActive,
                "one throwing SDR-luminance getter does not prevent using a separately readable validcandidate");
            await controller.ResetAsync();

            device = Device(Mode(hdr: true)); device.Request = () => { throw InvalidArgument(); };
            controller = new Services.PlaybackDisplayController();
            Check(!await controller.PrepareAsync(Stream(hdr: true), CancellationToken.None)
                && controller.FailureReason.Contains("80070057")
                && controller.FailureReason != enumerationReason,
                "stage diagnostics distinguish native enumeration failure from native request failure with the sameHRESULT");

            foreach (bool pq in new[] { false, true })
            {
                var unknownHdr = Mode(); unknownHdr.IsSdrLuminanceSupported = false;
                if (pq) unknownHdr.PqReadError = InvalidArgument(); else unknownHdr.DvReadError = InvalidArgument();
                device = Device(unknownHdr); controller = new Services.PlaybackDisplayController();
                Check(!await controller.PrepareAsync(Stream(), CancellationToken.None)
                    && device.Requests == 0 && !controller.FourKActive,
                    "an unreadable " + (pq ? "PQ" : "DolbyVision") + " capability cannot qualify a descriptor as SDR-only");
            }
        }
        private static async Task BaselineResetAndCancellation()
        {
            var hdr = Mode(hdr: true); var baseline = Mode(); baseline.IsSdrLuminanceSupported = false;
            var device = Device(hdr); var controller = new Services.PlaybackDisplayController();
            Check(await controller.PrepareAsync(Stream(hdr: true), CancellationToken.None), "setup owns a confirmedHDR output");
            device.Modes = new List<HdmiDisplayMode> { baseline };
            device.Reset = () => { throw InvalidArgument(); };
            Check(!await controller.PrepareAsync(Stream(), CancellationToken.None)
                && device.ModeOnlyRequests == 0 && device.Resets >= 1 && !controller.FourKActive && !controller.Hdr10Active,
                "failed reset of an ownedHDR EOTF prohibits a baseline mode-only request");
            device.Reset = () => Task.CompletedTask;
            await controller.ResetAsync();

            device = Device(hdr); controller = new Services.PlaybackDisplayController();
            Check(await controller.PrepareAsync(Stream(hdr: true), CancellationToken.None), "setup ownsHDR before pendingreset cancellation");
            device.Modes = new List<HdmiDisplayMode> { baseline };
            var resetCompletion = new TaskCompletionSource<bool>();
            device.Reset = () => resetCompletion.Task;
            using (var canceled = new CancellationTokenSource())
            {
                var pending = controller.PrepareAsync(Stream(), canceled.Token);
                Check(device.Resets == 1 && device.ModeOnlyRequests == 0, "baselineSDR preparation awaits the ownedHDR reset first");
                canceled.Cancel(); resetCompletion.SetResult(true);
                try { await pending; throw new InvalidOperationException("acceptedcancel"); }
                catch (OperationCanceledException)
                {
                    Check(device.ModeOnlyRequests == 0 && !controller.FourKActive && !controller.Hdr10Active,
                        "cancellation during ownedHDR reset never starts the subsequent mode-only request");
                }
            }

            device = Device(baseline); controller = new Services.PlaybackDisplayController();
            var requestCompletion = new TaskCompletionSource<bool>();
            device.Request = () => requestCompletion.Task;
            using (var canceled = new CancellationTokenSource())
            {
                var pending = controller.PrepareAsync(Stream(), canceled.Token);
                Check(device.ModeOnlyRequests == 1 && !controller.FourKActive, "pending native mode-only response does not publish4K output");
                canceled.Cancel(); requestCompletion.SetResult(true);
                try { await pending; throw new InvalidOperationException("acceptedcancel"); }
                catch (OperationCanceledException)
                {
                    Check(device.Resets == 1 && !controller.FourKActive && !controller.Hdr10Active,
                        "late cancellation of an accepted mode-only request acquires cleanupownership and restores default");
                }
            }
        }
        private static async Task CancellationAndSerialization()
        {
            var device = Device(Mode(hdr: true));
            var controller = new Services.PlaybackDisplayController();
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                try { await controller.PrepareAsync(Stream(hdr: true), canceled.Token); throw new InvalidOperationException("acceptedcancel"); }
                catch (OperationCanceledException) { Check(device.Requests == 0, "pre-canceled preparation never touchesHDMI"); }
            }
            var completion = new TaskCompletionSource<bool>();
            device.Request = () => completion.Task;
            using (var cancel = new CancellationTokenSource())
            {
                var pending = controller.PrepareAsync(Stream(hdr: true), cancel.Token);
                Check(device.Requests == 1 && !controller.Hdr10Active, "awaitingOSresponse does not publish activeHDR");
                cancel.Cancel(); completion.SetResult(true);
                try { await pending; throw new InvalidOperationException("acceptedcancel"); }
                catch (OperationCanceledException) { Check(device.Resets == 1 && !controller.Hdr10Active, "late cancellation restores nativechange before completing"); }
            }
            completion = new TaskCompletionSource<bool>();
            device.Request = () => completion.Task;
            var stale = controller.PrepareAsync(Stream(hdr: true), CancellationToken.None);
            var cleanup = controller.ResetAsync();
            Check(device.Resets == 1, "reset waits for pendingnative modeoperation");
            completion.SetResult(true);
            Check(!await stale, "reset invalidates an olderawaited preparation even without tokencancellation");
            await cleanup;
            Check(device.Resets == 2 && !controller.Hdr10Active, "stale successfulnative request restored once before queuedcleanup");
            device.Request = () => Task.FromResult(true);
            Check(await controller.PrepareAsync(Stream(), CancellationToken.None), "gate released after cancellation andreset");
            await controller.ResetAsync();
            Check(device.Resets == 3, "final successfulmode released by cleanup");
        }
    }
}
