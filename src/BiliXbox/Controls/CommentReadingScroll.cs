using System;

namespace BiliXbox.Controls
{
    // A tall expanded comment is read page by page before leaving its floor.
    internal static class CommentReadingScroll
    {
        internal static double GetDelta(double top, double bottom, double viewportHeight, bool down)
        {
            if (double.IsNaN(top) || double.IsInfinity(top) || double.IsNaN(bottom) || double.IsInfinity(bottom)
                || double.IsNaN(viewportHeight) || double.IsInfinity(viewportHeight)
                || viewportHeight <= 0 || bottom <= top || bottom <= 0 || top >= viewportHeight) return 0;
            double page = Math.Max(48, viewportHeight * .65);
            if (down && bottom > viewportHeight + 2) return Math.Min(page, bottom - viewportHeight);
            if (!down && top < -2) return -Math.Min(page, -top);
            return 0;
        }
    }
}
