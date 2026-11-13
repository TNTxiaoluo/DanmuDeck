using System;
using Windows.Foundation;

namespace BiliXbox.Controls
{
    internal static class FocusViewportClip
    {
        // Input rectangles share the overlay's coordinate system. Clip geometry
        // belongs to the outline itself, so return coordinates relative to it.
        public static Rect GetRelativeClip(Rect outline, Rect viewport)
        {
            if (outline.Width <= 0 || outline.Height <= 0 || viewport.Width <= 0 || viewport.Height <= 0)
                return new Rect(0, 0, 0, 0);
            double left = Math.Max(outline.Left, viewport.Left);
            double top = Math.Max(outline.Top, viewport.Top);
            double right = Math.Min(outline.Right, viewport.Right);
            double bottom = Math.Min(outline.Bottom, viewport.Bottom);
            if (right <= left || bottom <= top) return new Rect(0, 0, 0, 0);
            return new Rect(left - outline.X, top - outline.Y, right - left, bottom - top);
        }
    }
}
