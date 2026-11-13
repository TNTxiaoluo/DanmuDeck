using System;
using BiliXbox.Controls;
using Windows.Foundation;

namespace BiliXbox.CommentNavigationTests
{
    public static class Tests
    {
        private static int _checks;
        public static string Run()
        {
            _checks = 0;
            var viewport = new Rect(864, 170, 392, 260);
            var longComment = new Rect(861, 158, 401, 570);
            Same(FocusViewportClip.GetRelativeClip(longComment, viewport), new Rect(3, 12, 392, 260),
                "A long comment stops at its scrolling viewport, before the fixed composer");
            Same(FocusViewportClip.GetRelativeClip(new Rect(890, 200, 210, 120), viewport), new Rect(0, 0, 210, 120),
                "Fully visible comments retain the entire outline");
            Same(FocusViewportClip.GetRelativeClip(new Rect(861, -400, 401, 570), viewport), new Rect(0, 0, 0, 0),
                "A comment ending at the top edge cannot paint an outline over the header");
            Same(FocusViewportClip.GetRelativeClip(new Rect(861, 430, 401, 570), viewport), new Rect(0, 0, 0, 0),
                "A comment below the list cannot paint over the fixed publish button");
            Same(FocusViewportClip.GetRelativeClip(new Rect(861, 300, 401, 570), viewport), new Rect(3, 0, 392, 130),
                "Partial lower rows retain only their visible part");
            Same(FocusViewportClip.GetRelativeClip(longComment, new Rect(864, 170, 0, 260)), new Rect(0, 0, 0, 0),
                "A not-yet-measured viewport has no focus paint");

            // During scroll animations rows move through fractional positions;
            // validate the real overlay translation at several display scales.
            foreach (double scale in new[] { 0.75, 1d, 1.5, 2d })
            {
                var scaledViewport = Scale(viewport, scale);
                for (int step = -20; step <= 100; step++)
                {
                    var outline = Scale(new Rect(861.25, step * 11.375, 401.5, 570.75), scale);
                    var clip = FocusViewportClip.GetRelativeClip(outline, scaledViewport);
                    Check(clip.Width >= 0 && clip.Height >= 0, "Clip dimensions stay nonnegative while scrolling");
                    if (clip.Width == 0 || clip.Height == 0) continue;
                    Check(clip.Left >= -0.000001 && clip.Top >= -0.000001 &&
                        clip.Right <= outline.Width + 0.000001 && clip.Bottom <= outline.Height + 0.000001,
                        "Relative clip never escapes the actual focus outline");
                    var painted = new Rect(outline.X + clip.X, outline.Y + clip.Y, clip.Width, clip.Height);
                    Check(painted.Left >= scaledViewport.Left - 0.000001 && painted.Top >= scaledViewport.Top - 0.000001 &&
                        painted.Right <= scaledViewport.Right + 0.000001 && painted.Bottom <= scaledViewport.Bottom + 0.000001,
                        "Visible focus never crosses into fixed header/composer at any tested scale");
                }
            }
            return "Comment focus viewport: " + _checks + " checks passed.";
        }
        private static Rect Scale(Rect value, double scale)
            => new Rect(value.X * scale, value.Y * scale, value.Width * scale, value.Height * scale);
        private static void Same(Rect actual, Rect expected, string message)
        {
            Check(Math.Abs(actual.X - expected.X) < 0.000001 && Math.Abs(actual.Y - expected.Y) < 0.000001 &&
                Math.Abs(actual.Width - expected.Width) < 0.000001 && Math.Abs(actual.Height - expected.Height) < 0.000001, message);
        }
        private static void Check(bool condition, string message)
        { ++_checks; if (!condition) throw new InvalidOperationException(message); }
    }
}
