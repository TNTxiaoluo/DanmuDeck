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
        private bool _ownedHdr, _ownedEotfKnown;
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
        internal bool FourKActive { get; private set; }
        internal string FailureReason { get; private set; } = "";

        internal async Task<bool> PrepareAsync(PlaybackInfo playback, CancellationToken ct)
        {
            if (playback == null) throw new ArgumentNullException(nameof(playback));
            int resetVersion = Volatile.Read(ref _resetVersion);
            await _gate.WaitAsync(ct);
            string stage = "检查 HDMI 输出能力";
            try
            {
                ct.ThrowIfCancellationRequested();
                Hdr10Active = FourKActive = false;
                FailureReason = "";
                // Windows' normal media/display pipeline owns desktop output.
                // Do not infer that it entered HDR from an Xbox capability flag.
                if (!IsXbox) return true;
                bool hdr = playback.IsHdr10 && !playback.UsesSdrToneMapping;
                int width = Math.Max(playback.VideoWidth, playback.VideoHeight);
                int height = Math.Min(playback.VideoWidth, playback.VideoHeight);
                if (playback.UsesSdrToneMapping && (!playback.IsHdr10 || playback.QualityId != 120
                    || (playback.SourceQualityId != 120 && playback.SourceQualityId != 125)
                    || playback.VideoBitsPerChannel != 10 || !playback.FourKDisplayVerified
                    || width > 4096 || height > 2160 || (width < 3840 && height < 2160)))
                {
                    FailureReason = "此 HDR10→SDR 播放源未保留有效的原始 4K/10bit 信息或独立 SDR 能力证明";
                    await ResetCoreAsync();
                    return false;
                }
                bool highResolution = width > 1920 || height > 1080;
                if (!hdr && !highResolution)
                {
                    await ResetCoreAsync();
                    ct.ThrowIfCancellationRequested();
                    return !_ownsMode && resetVersion == Volatile.Read(ref _resetVersion);
                }
                bool requires4K = width >= 3000 || height >= 2160 || playback.QualityId == 120;
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
                { FailureReason = "系统未提供 HDMI 显示模式接口"; await ResetCoreAsync(); return false; }
                stage = "取得当前视图的 HDMI 接口";
                if (_display == null) _display = HdmiDisplayInformation.GetForCurrentView();
                if (_display == null) { FailureReason = "暂时无法读取 Xbox 当前视图的 HDMI 显示能力"; return false; }
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
                    FailureReason = "设备能力查询通过，但 Xbox 当前未提供匹配的 " + (hdr ? "HDR10" : requires4K ? "4K SDR" : "高清")
                        + " 输出模式" + (failures.Count > 0 ? "；" + failures[0]
                            : requires4K ? "；请检查电视和显示选项 → 视频模式中的「允许 4K」" : "");
                    await ResetCoreAsync(); return false;
                }
                foreach (var selected in candidates.OrderBy(mode => !hdr && !mode.Sdr)
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
                await ResetCoreAsync();
                return false;
            }
            finally { _gate.Release(); }
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
            if (changed) { _ownsMode = true; _ownedHdr = hdr; _ownedEotfKnown = true; }
            return changed;
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

        internal async Task ResetAsync()
        {
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
            }
            catch (Exception)
            {
                // Keep ownership after a failed reset so the next cleanup or
                // ordinary SDR preparation retries restoring the default.
            }
        }
    }
}
