using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Models;
using Windows.Foundation.Metadata;
using Windows.Graphics.Display.Core;
using Windows.System.Profile;

namespace BiliXbox.Services
{
    // Call from the view's UI thread. The HDMI API is scoped to that view;
    // capability checks alone do not request an HDR/4K output mode.
    internal sealed class PlaybackDisplayController
    {
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private HdmiDisplayInformation _display;
        private bool _ownsMode;
        private bool _ownedHdr, _ownedEotfKnown, _ownedDolbyVision;
        // Follow-system policy: the system mode seen while the app owned no
        // mode, and the HDR10 descriptor requested to match it.
        private HdmiDisplayMode _systemMode, _hdrMode, _dolbyVisionMode;
        private int _deferredResetVersion;
        private int _resetVersion;

        internal bool IsXbox
        {
            get
            {
                try { return AnalyticsInfo.VersionInfo.DeviceFamily == "Windows.Xbox"; }
                catch (Exception) { return false; }
            }
        }

        internal bool Hdr10Active { get; private set; }
        internal bool DolbyVisionActive => _ownsMode && _ownedDolbyVision;
        internal bool FourKActive { get; private set; }
        internal string FailureReason { get; private set; } = "";
        internal string Diagnostic { get; private set; } = "";

        internal async Task<bool> PrepareAsync(PlaybackInfo playback, CancellationToken ct)
        {
            if (playback == null) throw new ArgumentNullException(nameof(playback));
            // A new preparation supersedes a pending follow-system restore.
            Interlocked.Increment(ref _deferredResetVersion);
            int resetVersion = Volatile.Read(ref _resetVersion);
            await _gate.WaitAsync(ct);
            string stage = "检查 HDMI 输出能力";
            bool scaledSdrAllowed = false;
            try
            {
                ct.ThrowIfCancellationRequested();
                Hdr10Active = FourKActive = false;
                FailureReason = "";
                Diagnostic = "action=platform-managed";
                // Windows' normal media/display pipeline owns desktop output.
                // Do not infer that it entered HDR from an Xbox capability flag.
                if (!IsXbox) return true;
                bool hdr = playback.IsHdr10 && !playback.UsesSdrToneMapping;
                int width = Math.Max(playback.VideoWidth, playback.VideoHeight);
                int height = Math.Min(playback.VideoWidth, playback.VideoHeight);
                scaledSdrAllowed = !hdr && playback.QualityId == 120 && playback.AllowsScaledSdrOutput;
                if (playback.UsesSdrToneMapping && (!playback.IsHdr10 || playback.QualityId != 120
                    || (playback.SourceQualityId != 120 && playback.SourceQualityId != 125)
                    || playback.VideoBitsPerChannel != 10 || (!playback.FourKDisplayVerified && !scaledSdrAllowed)
                    || width > 4096 || height > 2160 || (width < 3840 && height < 2160)))
                {
                    FailureReason = "此 HDR10→SDR 播放源未保留有效的原始 4K/10bit 信息或独立 SDR 能力证明";
                    await ResetCoreAsync();
                    return false;
                }
                // Experimental Dolby Vision: needs the HDMI Dolby Vision mode
                // whatever the HDMI policy; failure falls back to the next source.
                if (playback.IsDolbyVision) return await PrepareDolbyVisionAsync(ct, resetVersion);
                // Follow-system policy: keep the system resolution and refresh
                // rate, switching only HDR/SDR. If HDR10 cannot be matched, use per-source rules.
                if (HdmiOutputSettings.FollowSystem && await PrepareFollowSystemAsync(playback, hdr, ct, resetVersion)) return true;
                ct.ThrowIfCancellationRequested();
                bool highResolution = width > 1920 || height > 1080;
                if (!hdr && !highResolution)
                {
                    Diagnostic = "action=default-for-hd owned=" + _ownsMode;
                    await ResetCoreAsync();
                    ct.ThrowIfCancellationRequested();
                    return !_ownsMode && resetVersion == Volatile.Read(ref _resetVersion);
                }
                bool requires4K = width >= 3000 || height >= 2160 || playback.QualityId == 120;
                if (scaledSdrAllowed && !playback.FourKDisplayVerified)
                    return await UseDefaultSdrAsync(ct, resetVersion);
                if ((hdr && !playback.Hdr10DisplayVerified)
                    || (requires4K && !playback.FourKDisplayVerified)
                    || width <= 0 || height <= 0)
                { FailureReason = "此播放源尚未通过所需的解码及显示能力查询"; await ResetCoreAsync(); return false; }
                int outputWidth, outputHeight;
                if (!PlaybackOutputSizePolicy.TryGetOutputSize(width, height, out outputWidth, out outputHeight))
                { FailureReason = "此播放源没有有效的输出尺寸"; await ResetCoreAsync(); return false; }
                double fps = playback.VideoFrameRate > 0 ? playback.VideoFrameRate : 30;
                double outputRate;
                if (!PlaybackFrameRatePolicy.TryGetOutputRate(fps, playback.VideoFrameRateProbe,
                    playback.RequiresNativeFrameRateValidation, out outputRate))
                { FailureReason = "此视频帧率尚未通过所需的原生验证，不能仅依据接口帧率启用输出"; await ResetCoreAsync(); return false; }
                if (!ApiInformation.IsTypePresent("Windows.Graphics.Display.Core.HdmiDisplayInformation"))
                {
                    if (scaledSdrAllowed) return await UseDefaultSdrAsync(ct, resetVersion);
                    FailureReason = "系统未提供 HDMI 显示模式接口"; await ResetCoreAsync(); return false;
                }
                stage = "取得当前视图的 HDMI 接口";
                if (_display == null) _display = HdmiDisplayInformation.GetForCurrentView();
                if (_display == null)
                {
                    if (scaledSdrAllowed) return await UseDefaultSdrAsync(ct, resetVersion);
                    FailureReason = "暂时无法读取 Xbox 当前视图的 HDMI 显示能力"; return false;
                }
                // Enumerate again for each preparation: HDMI settings and the
                // connected display may have changed since the previous video.
                stage = "枚举 HDMI 输出模式";
                var modes = _display.GetSupportedDisplayModes();
                var candidates = new List<OutputCandidate>();
                var failures = new List<string>();
                foreach (var mode in modes)
                {
                    try
                    {
                        if (mode == null || mode.StereoEnabled || mode.ResolutionWidthInRawPixels < outputWidth
                            || mode.ResolutionHeightInRawPixels < outputHeight || mode.RefreshRate <= 0
                            || mode.RefreshRate > 60.001 || mode.RefreshRate + 0.1 < outputRate) continue;
                        var candidate = new OutputCandidate(mode);
                        if (hdr ? candidate.Pq : candidate.Sdr || (requires4K
                            && playback.FourKDisplayVerified && candidate.BaselineSdr)) candidates.Add(candidate);
                    }
                    catch (Exception ex) { failures.Add(Error("读取 HDMI 模式参数", ex)); }
                }
                if (candidates.Count == 0)
                {
                    if (scaledSdrAllowed) return await UseDefaultSdrAsync(ct, resetVersion);
                    FailureReason = "设备能力查询通过，但 Xbox 当前未提供匹配的 " + (hdr ? "HDR10" : requires4K ? "4K SDR" : "高清")
                        + " 输出模式" + (failures.Count > 0 ? "；" + failures[0]
                            : requires4K ? "；请检查电视和显示选项 → 视频模式中的「允许 4K」" : "");
                    await ResetCoreAsync(); return false;
                }
                HdmiDisplayMode currentMode = null;
                try { currentMode = _display.GetCurrentDisplayMode(); } catch (Exception) { }
                foreach (var selected in candidates.OrderBy(mode => !(mode.Mode.ResolutionWidthInRawPixels == outputWidth
                    && mode.Mode.ResolutionHeightInRawPixels == outputHeight && CanReuse(mode, currentMode, hdr)))
                    .ThenBy(mode => !hdr && !mode.Sdr)
                    .ThenBy(mode => (long)mode.Mode.ResolutionWidthInRawPixels * mode.Mode.ResolutionHeightInRawPixels)
                    .ThenBy(mode => Math.Abs(mode.Mode.RefreshRate - 60)).Take(8))
                {
                    ct.ThrowIfCancellationRequested();
                    if (resetVersion != Volatile.Read(ref _resetVersion)) break;
                    bool changed = false;
                    // A plain, SDR-only current mode already establishes SDR.
                    // HDR-capable descriptors alone never establish active HDR.
                    if ((_ownsMode && _ownedEotfKnown && _ownedHdr == hdr)
                        || (!hdr && !_ownsMode && selected.BaselineSdr))
                    {
                        try { changed = selected.Mode.IsEqual(_display.GetCurrentDisplayMode()); }
                        catch (Exception) { /* Re-establish and verify below. */ }
                    }
                    if (!changed)
                    {
                        Diagnostic = "action=request range=" + (hdr ? "HDR10" : "SDR") + " mode=" + selected.Description;
                        // Do not send EotfSdr to a descriptor which does not
                        // advertise that EOTF. The documented mode-only overload
                        // is safe here only for independently verified SDR-only
                        // RGB/BT709 modes, never as an HDR request fallback.
                        bool modeOnly = !hdr && !selected.Sdr && selected.BaselineSdr;
                        try
                        {
                            changed = await RequestAsync(selected, hdr, modeOnly, ct, resetVersion);
                        }
                        catch (Exception ex) when (!(ex is OperationCanceledException))
                        {
                            failures.Add(Error("设置 " + selected.Description + (modeOnly ? " 模式" : hdr ? " HDR10" : " SDR EOTF"), ex));
                            if (ex.HResult == unchecked((int)0x80070057) && !hdr && !modeOnly && selected.BaselineSdr)
                            {
                                try { changed = await RequestAsync(selected, false, true, ct, resetVersion); }
                                catch (Exception retry) when (!(retry is OperationCanceledException))
                                { failures.Add(Error("仅设置 " + selected.Description + " 模式", retry)); }
                            }
                        }
                    }
                    else Diagnostic = "action=keep range=" + (hdr ? "HDR10" : "SDR") + " mode=" + selected.Description;
                    ct.ThrowIfCancellationRequested();
                    if (resetVersion != Volatile.Read(ref _resetVersion)) break;
                    if (!changed) { failures.Add("Xbox 拒绝启用 " + selected.Description + (hdr ? " HDR10" : " SDR")); continue; }
                    try
                    {
                        stage = "确认 " + selected.Description + " 实际输出";
                        var current = _display.GetCurrentDisplayMode();
                        if (current == null || !selected.Mode.IsEqual(current))
                        { failures.Add(stage + "失败：系统当前模式与请求不一致"); await ResetCoreAsync(); if (_ownsMode) break; continue; }
                        Hdr10Active = hdr;
                        FourKActive = current.ResolutionWidthInRawPixels >= 3840 && current.ResolutionHeightInRawPixels >= 2160;
                        return true;
                    }
                    catch (Exception ex)
                    { failures.Add(Error(stage, ex)); await ResetCoreAsync(); if (_ownsMode) break; }
                }
                FailureReason = resetVersion != Volatile.Read(ref _resetVersion) ? "输出模式请求已失效，请重新播放"
                    : string.Join("；", failures.Distinct().Take(4));
                if (scaledSdrAllowed) return await UseDefaultSdrAsync(ct, resetVersion);
                await ResetCoreAsync();
                return false;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Native mode changes are not cancellable. If cancellation won
                // while awaiting the OS, restore the default before finishing.
                await ResetCoreAsync();
                throw;
            }
            catch (Exception ex)
            {
                FailureReason = Error(stage, ex);
                if (scaledSdrAllowed && !ct.IsCancellationRequested) return await UseDefaultSdrAsync(ct, resetVersion);
                await ResetCoreAsync();
                return false;
            }
            finally { _gate.Release(); }
        }

        private async Task<bool> UseDefaultSdrAsync(CancellationToken ct, int resetVersion)
        {
            await ResetCoreAsync();
            ct.ThrowIfCancellationRequested();
            if (_ownsMode || resetVersion != Volatile.Read(ref _resetVersion)) return false;
            FailureReason = "";
            Diagnostic = "action=default-sdr-downscale";
            return true;
        }

        private bool CanReuse(OutputCandidate candidate, HdmiDisplayMode current, bool hdr)
        {
            if (current == null || !((_ownsMode && _ownedEotfKnown && _ownedHdr == hdr)
                || (!hdr && !_ownsMode && candidate.BaselineSdr))) return false;
            try { return candidate.Mode.IsEqual(current); }
            catch (Exception) { return false; }
        }

        private async Task<bool> RequestAsync(OutputCandidate selected, bool hdr, bool modeOnly,
            CancellationToken ct, int resetVersion)
        {
            if (modeOnly && _ownsMode && (!_ownedEotfKnown || _ownedHdr))
            {
                await ResetCoreAsync();
                if (_ownsMode) throw new InvalidOperationException("无法恢复默认 SDR 输出");
            }
            ct.ThrowIfCancellationRequested();
            if (resetVersion != Volatile.Read(ref _resetVersion)) return false;
            bool changed = modeOnly ? await _display.RequestSetCurrentDisplayModeAsync(selected.Mode)
                : await _display.RequestSetCurrentDisplayModeAsync(selected.Mode,
                    hdr ? HdmiDisplayHdrOption.Eotf2084 : HdmiDisplayHdrOption.EotfSdr);
            // Acquire cleanup ownership before cancellation or result reads.
            if (changed) { _ownsMode = true; _ownedHdr = hdr; _ownedEotfKnown = true; _ownedDolbyVision = false; }
            return changed;
        }

        // Dolby Vision on Xbox is sent as low-latency Dolby Vision. The TV
        // usually lists it on a single descriptor (for example 4K 59.94Hz), so
        // this is the one case where the output resolution may differ from the
        // system setting. An identical active Dolby Vision mode is kept.
        private async Task<bool> PrepareDolbyVisionAsync(CancellationToken ct, int resetVersion)
        {
            if (!ApiInformation.IsTypePresent("Windows.Graphics.Display.Core.HdmiDisplayInformation"))
            { FailureReason = "系统未提供 HDMI 显示模式接口，无法输出杜比视界"; return false; }
            if (_display == null) _display = HdmiDisplayInformation.GetForCurrentView();
            if (_display == null) { FailureReason = "暂时无法读取 Xbox 当前视图的 HDMI 显示能力"; return false; }
            HdmiDisplayMode current = null;
            try { current = _display.GetCurrentDisplayMode(); } catch (Exception) { }
            if (!_ownsMode && current != null) _systemMode = current;
            var system = _systemMode ?? current;
            if (_ownsMode && _ownedDolbyVision && _dolbyVisionMode != null && current != null && _dolbyVisionMode.IsEqual(current))
            {
                Diagnostic = "action=dolby-vision-keep mode=" + new OutputCandidate(current).Description;
                FourKActive = current.ResolutionWidthInRawPixels >= 3840 && current.ResolutionHeightInRawPixels >= 2160;
                return true;
            }
            var candidates = new List<HdmiDisplayMode>();
            foreach (var mode in _display.GetSupportedDisplayModes())
            {
                try { if (mode != null && !mode.StereoEnabled && mode.IsDolbyVisionLowLatencySupported && mode.RefreshRate <= 60.001) candidates.Add(mode); }
                catch (Exception) { }
            }
            var target = candidates
                .OrderBy(mode => system == null || !(mode.ResolutionWidthInRawPixels == system.ResolutionWidthInRawPixels
                    && mode.ResolutionHeightInRawPixels == system.ResolutionHeightInRawPixels) ? 1 : 0)
                .ThenBy(mode => system == null ? 0 : Math.Abs(mode.RefreshRate - system.RefreshRate))
                .ThenByDescending(mode => (long)mode.ResolutionWidthInRawPixels * mode.ResolutionHeightInRawPixels)
                .FirstOrDefault();
            if (target == null) { FailureReason = "电视或 Xbox 当前未提供杜比视界输出模式"; return false; }
            string description = new OutputCandidate(target).Description;
            Diagnostic = "action=dolby-vision-request mode=" + description + " candidates=" + candidates.Count;
            bool changed;
            try
            {
                ct.ThrowIfCancellationRequested();
                if (resetVersion != Volatile.Read(ref _resetVersion)) return false;
                changed = await _display.RequestSetCurrentDisplayModeAsync(target, HdmiDisplayHdrOption.DolbyVisionLowLatency);
                if (changed) { _ownsMode = true; _ownedHdr = true; _ownedEotfKnown = false; _ownedDolbyVision = true; }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            { FailureReason = Error("启用杜比视界输出 " + description, ex); return false; }
            ct.ThrowIfCancellationRequested();
            if (!changed) { FailureReason = "Xbox 拒绝启用杜比视界输出 " + description; return false; }
            HdmiDisplayMode now = null;
            try { now = _display.GetCurrentDisplayMode(); } catch (Exception) { }
            if (now == null || !target.IsEqual(now))
            { FailureReason = "杜比视界输出确认失败：系统当前模式与请求不一致"; await ResetCoreAsync(); return false; }
            _dolbyVisionMode = target;
            FourKActive = target.ResolutionWidthInRawPixels >= 3840 && target.ResolutionHeightInRawPixels >= 2160;
            return true;
        }

        // Follow-system policy: resolution and refresh rate always stay at the
        // Xbox system setting; the player scales every source into it. Only the
        // dynamic range changes: an HDR10 source requests the PQ descriptor of
        // the same resolution and refresh rate, an SDR source returns to the
        // system default. A target equal to the current mode is never requested.
        private async Task<bool> PrepareFollowSystemAsync(PlaybackInfo playback, bool hdr, CancellationToken ct, int resetVersion)
        {
            if (hdr && !playback.Hdr10DisplayVerified) return false;
            if (!ApiInformation.IsTypePresent("Windows.Graphics.Display.Core.HdmiDisplayInformation"))
            {
                if (hdr) return false;
                Diagnostic = "action=follow-system-unmanaged";
                return true;
            }
            try
            {
                if (_display == null) _display = HdmiDisplayInformation.GetForCurrentView();
                if (_display == null) { if (hdr) return false; Diagnostic = "action=follow-system-unmanaged"; return true; }
                HdmiDisplayMode current = null;
                try { current = _display.GetCurrentDisplayMode(); } catch (Exception) { }
                if (!_ownsMode && current != null) _systemMode = current;
                if (!hdr)
                {
                    if (_ownsMode)
                    {
                        // Leaving HDR10 for an SDR source: the one switch this
                        // policy makes for SDR, back to the system's own mode.
                        Diagnostic = "action=follow-system-sdr-restore";
                        await ResetCoreAsync();
                        ct.ThrowIfCancellationRequested();
                        if (_ownsMode || resetVersion != Volatile.Read(ref _resetVersion)) return false;
                        try { current = _display.GetCurrentDisplayMode(); if (current != null) _systemMode = current; } catch (Exception) { }
                    }
                    else Diagnostic = "action=follow-system-keep range=SDR";
                    var shown = current ?? _systemMode;
                    if (shown != null) Diagnostic += " mode=" + new OutputCandidate(shown).Description;
                    FourKActive = shown != null && shown.ResolutionWidthInRawPixels >= 3840 && shown.ResolutionHeightInRawPixels >= 2160;
                    return true;
                }
                var system = _systemMode ?? current;
                if (system == null) return false;
                if (_ownsMode && _ownedEotfKnown && _ownedHdr && current != null && _hdrMode != null && _hdrMode.IsEqual(current))
                {
                    Diagnostic = "action=follow-system-keep range=HDR10 mode=" + new OutputCandidate(current).Description;
                    Hdr10Active = true;
                    FourKActive = current.ResolutionWidthInRawPixels >= 3840 && current.ResolutionHeightInRawPixels >= 2160;
                    return true;
                }
                var candidates = new List<OutputCandidate>();
                foreach (var mode in _display.GetSupportedDisplayModes())
                {
                    try
                    {
                        if (mode == null || mode.StereoEnabled || Math.Abs(mode.RefreshRate - system.RefreshRate) > 0.01) continue;
                        var candidate = new OutputCandidate(mode);
                        if (candidate.Pq) candidates.Add(candidate);
                    }
                    catch (Exception) { }
                }
                // Same resolution as the system first; only when the TV has no
                // HDR10 descriptor there, the closest resolution at that rate.
                var target = candidates.OrderBy(item => !(item.Mode.ResolutionWidthInRawPixels == system.ResolutionWidthInRawPixels
                        && item.Mode.ResolutionHeightInRawPixels == system.ResolutionHeightInRawPixels))
                    .ThenBy(item => Math.Abs((long)item.Mode.ResolutionWidthInRawPixels * item.Mode.ResolutionHeightInRawPixels
                        - (long)system.ResolutionWidthInRawPixels * system.ResolutionHeightInRawPixels))
                    .ThenBy(item => Math.Abs((int)item.Mode.BitsPerPixel - 30)).FirstOrDefault();
                if (target == null) return false;
                bool sameResolution = target.Mode.ResolutionWidthInRawPixels == system.ResolutionWidthInRawPixels
                    && target.Mode.ResolutionHeightInRawPixels == system.ResolutionHeightInRawPixels;
                if (_ownsMode && _ownedEotfKnown && _ownedHdr && current != null && target.Mode.IsEqual(current))
                    Diagnostic = "action=follow-system-keep range=HDR10 mode=" + target.Description;
                else
                {
                    Diagnostic = "action=follow-system-hdr mode=" + target.Description + (sameResolution ? "" : " resolution=closest");
                    if (!await RequestAsync(target, true, false, ct, resetVersion)) return false;
                    ct.ThrowIfCancellationRequested();
                    var now = _display.GetCurrentDisplayMode();
                    if (now == null || !target.Mode.IsEqual(now)) { await ResetCoreAsync(); return false; }
                }
                _hdrMode = target.Mode;
                Hdr10Active = true;
                FourKActive = target.Mode.ResolutionWidthInRawPixels >= 3840 && target.Mode.ResolutionHeightInRawPixels >= 2160;
                return true;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                FailureReason = Error("按系统设置准备 HDMI 输出", ex);
                return false;
            }
        }

        private static string Error(string stage, Exception ex) => stage + "失败 (0x"
            + ex.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + ")";

        private sealed class OutputCandidate
        {
            internal readonly HdmiDisplayMode Mode;
            internal readonly bool Sdr, Pq, BaselineSdr;
            internal readonly string Description;
            internal OutputCandidate(HdmiDisplayMode mode)
            {
                Mode = mode;
                // Optional luminance properties can be absent on baseline SDR
                // descriptors. Unknown PQ/DV must not qualify as SDR-only.
                try { Sdr = mode.IsSdrLuminanceSupported; } catch (Exception) { Sdr = false; }
                bool pq, dv, pqKnown = true;
                try { pq = mode.IsSmpte2084Supported; } catch (Exception) { pq = true; pqKnown = false; }
                try { dv = mode.IsDolbyVisionLowLatencySupported; } catch (Exception) { dv = true; }
                Pq = pqKnown && pq && mode.ColorSpace == HdmiDisplayColorSpace.BT2020;
                BaselineSdr = !pq && !dv && (mode.ColorSpace == HdmiDisplayColorSpace.RgbLimited
                    || mode.ColorSpace == HdmiDisplayColorSpace.RgbFull || mode.ColorSpace == HdmiDisplayColorSpace.BT709);
                Description = mode.ResolutionWidthInRawPixels + "×" + mode.ResolutionHeightInRawPixels
                    + " " + mode.RefreshRate.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "Hz";
            }
        }

        // Follow-system policy: leaving playback returns an HDR10 output to the
        // system mode after a short grace period, so moving between HDR videos
        // or parts does not cause two HDMI handshakes. An SDR output is already
        // the system mode and needs nothing. Force (background, exit) is immediate.
        internal static TimeSpan FollowSystemRestoreDelay = TimeSpan.FromSeconds(8);

        internal async Task ResetAsync(bool force = false)
        {
            if (!force && HdmiOutputSettings.FollowSystem && IsXbox)
            {
                if (!_ownsMode) return;
                int deferred = Interlocked.Increment(ref _deferredResetVersion);
                await Task.Delay(FollowSystemRestoreDelay);
                if (deferred != Volatile.Read(ref _deferredResetVersion) || !_ownsMode) return;
            }
            Interlocked.Increment(ref _resetVersion);
            await _gate.WaitAsync();
            try { await ResetCoreAsync(); }
            finally { _gate.Release(); }
        }

        private async Task ResetCoreAsync()
        {
            Hdr10Active = FourKActive = false;
            if (!_ownsMode || _display == null) return;
            _ownedEotfKnown = false;
            try
            {
                await _display.SetDefaultDisplayModeAsync();
                _ownsMode = false;
                _hdrMode = null; _dolbyVisionMode = null; _ownedDolbyVision = false;
            }
            catch (Exception)
            {
                // Keep ownership after a failed reset so the next cleanup or
                // ordinary SDR preparation retries restoring the default.
            }
        }
    }
}
