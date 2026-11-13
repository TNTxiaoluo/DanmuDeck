using System;
using BiliXbox.Controls;

namespace BiliXbox.CommentImageNavigationTests
{
    public static class Tests
    {
        private static int _checks;
        private static void Check(bool value, string message)
        {
            ++_checks;
            if (!value) throw new Exception(message);
        }
        public static void Run()
        {
            var state = new CommentImageNavigationState();
            Check(!state.IsOpen && state.Count == 0, "Initial viewer must be closed.");
            Check(!state.Open(null, 0), "Null pictures must not open.");
            Check(!state.Open(Array.Empty<string>(), 0), "Empty pictures must not open.");
            Check(!state.Move(1) && !state.Move(-1), "Cannot navigate a closed viewer.");
            Check(!state.ConsumeBack(100), "B without an open or just-closed picture belongs to reader.");
            Check(state.Open(new[] { "https://i0.hdslb.com/bfs/new_dyn/original.png?token=opaque%2Bvalue" }, 0), "Original HTTPS source opens.");
            Check(state.CurrentUrl.EndsWith("?token=opaque%2Bvalue", StringComparison.Ordinal), "Original signed URL must be preserved exactly.");
            Check(state.Index == 0 && state.Count == 1, "One image tracks original order.");
            Check(!state.Move(1) && !state.Move(-1), "No wrapping outside the only image.");
            Check(state.ConsumeBack(1000) && !state.IsOpen, "First B closes image.");
            Check(state.Count == 0 && state.CurrentUrl == "", "Closed viewer releases source references.");
            Check(state.ConsumeBack(1001), "Routed duplicate B must not close the reader.");
            Check(state.ConsumeBack(1299), "Duplicate B grace includes 299ms.");
            Check(!state.ConsumeBack(1300), "A later B returns to reader dismissal.");
            Check(!state.ConsumeBack(900), "Clock rollback cannot suppress future navigation indefinitely.");

            var urls = new[] { "https://example.com/a.png", "http://example.com/unsafe.png", "https://example.com/b.png" };
            Check(!state.Open(urls, 1), "Invalid selected source does not silently open another picture.");
            Check(state.Open(urls, -5) && state.Index == 0, "Negative index clamps to first picture.");
            Check(state.Move(1) && state.Index == 2, "Next skips unsupported source while preserving original index.");
            Check(!state.Move(1) && state.Index == 2, "Next cannot overflow.");
            Check(state.Move(-1) && state.Index == 0, "Previous skips unsupported source.");
            Check(!state.Move(0), "Zero direction has no effect.");
            state.Close(2000); state.Close(2100);
            Check(!state.ConsumeBack(2300), "Repeated cleanup does not extend B suppression.");
            Check(state.Open(urls, 999) && state.Index == 2, "Large index clamps to last picture.");
            Check(state.ConsumeBack(0) && !state.IsOpen, "Reopening resets prior close timer and B closes new image.");
            Uri address;
            Check(!CommentImageNavigationState.TryGetAddress("file:///C:/secret.png", out address), "File sources rejected.");
            Check(!CommentImageNavigationState.TryGetAddress("javascript:alert(1)", out address), "Script sources rejected.");
            Check(!CommentImageNavigationState.TryGetAddress("https://user:password@example.com/image.png", out address), "Embedded credentials rejected.");
            Check(!CommentImageNavigationState.TryGetAddress("/image.png", out address), "Relative source rejected.");
            Check(!CommentImageNavigationState.TryGetAddress("https://example.com/" + new string('a', 8192), out address), "Unbounded URL rejected.");
            Check(CommentImageNavigationState.TryGetAddress("https://i0.hdslb.com/image.webp", out address), "WebP source admitted without replacing it with thumbnail.");
            state.Close(3000);
            Check(state.Open(new[] { "https://i0.hdslb.com/image.webp" }, 0, 4000), "A fresh image session starts its own confirmation barrier.");
            Check(!state.ConsumeConfirm(4000, false) && !state.ConsumeConfirm(4249, false), "The routed opener A cannot immediately zoom the newly opened image.");
            state.ReleaseConfirm();
            Check(state.ConsumeConfirm(4050, false), "A real release allows an intentional quick second press without waiting 250 ms.");
            Check(!state.ConsumeConfirm(4051, false), "Duplicate representations of the accepted A press do not toggle zoom back.");
            Check(!state.ConsumeConfirm(5000, true), "A held repeat never zooms repeatedly even after the timeout.");
            state.ReleaseConfirm();
            Check(state.ConsumeConfirm(4100, false), "The next released A press can restore image size immediately.");
            Check(state.ConsumeBack(4200) && !state.ConsumeConfirm(4500, false), "A closed viewer cannot consume confirm input from the comment drawer.");
            state.ReleaseConfirm();
            Check(state.Open(new[] { "https://i0.hdslb.com/image.webp" }, 0, 5000), "Reopening after close resets the release state.");
            Check(!state.ConsumeConfirm(5001, false), "An earlier release cannot arm the opener of a new image session.");
            Check(state.ConsumeConfirm(5250, false), "Input sources that omit KeyUp recover after the short opening barrier.");
            Console.WriteLine("Comment image navigation: " + _checks + " checks passed.");
        }
    }
}
