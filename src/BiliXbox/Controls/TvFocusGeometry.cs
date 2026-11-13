using System;

namespace BiliXbox.Controls
{
    internal static class TvFocusGeometry
    {
        private const double MaximumScale = 1.06;
        private const double MaximumSideExpansion = 12;

        // A wide dynamic row must not grow by the same percentage as a poster.
        // Keep the visual expansion within the list's reserved focus gutter.
        internal static float GetScale(double width)
        {
            if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0) return 1f;
            double target = Math.Min(MaximumScale, 1d + MaximumSideExpansion * 2d / width);
            float scale = (float)target;
            if ((double)scale > target || ((double)scale - 1d) * width > MaximumSideExpansion * 2d)
            {
                // Casting to float can round upward past the gutter limit.
                // BitConverter is available on the UWP target; no newer MathF
                // APIs or unsafe code are needed for the next lower float.
                int bits = BitConverter.ToInt32(BitConverter.GetBytes(scale), 0);
                scale = BitConverter.ToSingle(BitConverter.GetBytes(bits - 1), 0);
            }
            return scale;
        }
    }
}
