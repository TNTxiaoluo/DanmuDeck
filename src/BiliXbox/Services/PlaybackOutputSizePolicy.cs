using System;

namespace BiliXbox.Services
{
    // Decoder limits describe the encoded frame. HDMI requirements describe
    // the display surface; a DCI-width 4K source can fit a UHD surface while
    // retaining its aspect ratio. Never replace the actual decode dimensions.
    internal static class PlaybackOutputSizePolicy
    {
        internal static bool TryGetOutputSize(int sourceWidth, int sourceHeight,
            out int outputWidth, out int outputHeight)
        {
            outputWidth = Math.Max(sourceWidth, sourceHeight);
            outputHeight = Math.Min(sourceWidth, sourceHeight);
            if (sourceWidth <= 0 || sourceHeight <= 0) return false;
            // Bounded DCI 4K family only. In particular this does not make 8K,
            // a taller-than-2160 source, or arbitrary unsupported sizes UHD.
            if (outputWidth > 3840 && outputWidth <= 4096 && outputHeight <= 2160)
            {
                outputWidth = 3840;
                outputHeight = 2160;
            }
            return true;
        }
    }
}
