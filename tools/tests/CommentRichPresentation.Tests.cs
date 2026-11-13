using System;
using BiliXbox.Controls;
using BiliXbox.Models;

namespace BiliXbox.CommentRichPresentationTests
{
    public static class Tests
    {
        private static int _checks;
        public static string Run()
        {
            _checks = 0;
            Uri address;
            foreach (string url in new[]
            {
                "https://i0.hdslb.com/bfs/emote/actual.png", "https://i1.hdslb.com/bfs/new_dyn/actual.webp",
                "https://i0.biliimg.com/bfs/emoji/actual.png", "https://www.bilibili.com/bfs/image.png",
                "https://i0.hdslb.com/bfs/emote/actual.png?expires=123&token=unchanged%2Bvalue"
            })
            {
                Check(CommentRichPresentation.TryImageUri(url, out address), "Bilibili HTTPS images are accepted");
                Check(address.OriginalString == url, "Signed image parameters and original URL are retained");
            }
            foreach (string url in new[]
            {
                null, "", "relative.png", "http://i0.hdslb.com/image.png", "file:///c:/image.png",
                "data:image/png;base64,xxx", "https://i0.hdslb.com.evil.invalid/image.png",
                "https://evilhdslb.com/image.png", "https://i0.hdslb.com@evil.invalid/image.png",
                "https://name@i0.hdslb.com/image.png", "https://i0.hdslb.com:444/image.png"
            })
                Check(!CommentRichPresentation.TryImageUri(url, out address) && address == null, "Untrusted or non-HTTPS image fails closed");

            Check(CommentRichPresentation.LevelText(null) == "", "An absent user level stays absent");
            for (int level = 0; level <= 6; level++)
                Check(CommentRichPresentation.LevelText(level) == "LV" + level, "A known Bilibili level is displayed accurately");
            Check(CommentRichPresentation.LevelText(-1) == "" && CommentRichPresentation.LevelText(7) == "", "Invalid levels are not presented as an invented badge");

            Check(CommentRichPresentation.Metadata(null, false) == "", "Missing comment has no invented metadata");
            var item = new CommentItem { TimeText = "2026-10-22 12:30", IpLocation = "湖南", Likes = 3171 };
            Check(CommentRichPresentation.Metadata(item, false) == "2026-10-22 12:30   ·   IP属地：湖南", "Time and actual IP region render together");
            Check(CommentRichPresentation.Metadata(item, true).EndsWith("点赞 3,171", StringComparison.Ordinal), "Preview includes the real like count using invariant grouping");
            item.IpLocation = "IP属地：上海";
            Check(CommentRichPresentation.Metadata(item, false) == "2026-10-22 12:30   ·   IP属地：上海", "An existing IP prefix is not duplicated");
            item.IpLocation = "IP 属地：上海";
            Check(CommentRichPresentation.Metadata(item, false) == "2026-10-22 12:30   ·   IP 属地：上海", "A spaced server IP prefix is not duplicated");
            item.IpLocation = "";
            Check(CommentRichPresentation.Metadata(item, false) == "2026-10-22 12:30", "An unavailable IP region is hidden");
            item.TimeText = ""; item.IpLocation = "上海";
            Check(CommentRichPresentation.Metadata(item, false) == "IP属地：上海", "An absent time does not introduce leading punctuation");
            Check(CommentRichPresentation.LikeCount(-1) == "0", "Invalid counts cannot render negative likes");

            var regular = new CommentContentPart { Scale = 1, Width = 50, Height = 50 };
            var large = new CommentContentPart { Scale = 2, Width = 200, Height = 100 };
            Check(CommentRichPresentation.EmoteWidth(regular) == 30 && CommentRichPresentation.EmoteHeight(regular) == 30, "Ordinary emotes remain readable at regular size");
            var highResolutionRegular = new CommentContentPart { Scale = 1, Width = 150, Height = 150 };
            Check(CommentRichPresentation.EmoteWidth(highResolutionRegular) == 30 && CommentRichPresentation.EmoteHeight(highResolutionRegular) == 30, "Native image resolution does not override regular server display size");
            Check(CommentRichPresentation.EmoteWidth(large) == 96 && CommentRichPresentation.EmoteHeight(large) == 48, "Large collection emotes honor size and aspect metadata");
            var tall = new CommentContentPart { Scale = 2, Width = 50, Height = 100 };
            Check(CommentRichPresentation.EmoteWidth(tall) == 24 && CommentRichPresentation.EmoteHeight(tall) == 48, "Portrait emotes keep their aspect ratio");
            var wide = new CommentContentPart { Scale = 100, Width = 100000, Height = 1 };
            Check(CommentRichPresentation.EmoteWidth(wide) == 96 && CommentRichPresentation.EmoteHeight(wide) == 48, "Extreme server sizes cannot make a list row unbounded");
            var malformed = new CommentContentPart { Scale = double.NaN, Width = double.NaN, Height = double.PositiveInfinity };
            Check(!double.IsNaN(CommentRichPresentation.EmoteWidth(malformed)) && CommentRichPresentation.EmoteWidth(malformed) <= 96 && CommentRichPresentation.EmoteHeight(malformed) <= 48, "Malformed size metadata stays finite and bounded");
            Check(CommentRichPresentation.EmoteWidth(null) == 30, "Missing metadata falls back to regular emotes");

            Check(!CommentRichPresentation.OpenDetailsOnPrimary(null, false, false), "An empty row has no action");
            item = new CommentItem();
            Check(CommentRichPresentation.OpenDetailsOnPrimary(item, false, true), "A short comment without replies still exposes its detail and vote actions");
            item.ReplyCount = 2;
            Check(!CommentRichPresentation.OpenDetailsOnPrimary(item, false, true), "A normal thread keeps its established reply shortcut");
            Check(CommentRichPresentation.OpenDetailsOnPrimary(item, true, true), "Truncated text always opens full detail");
            Check(CommentRichPresentation.OpenDetailsOnPrimary(item, false, false), "A reply row without a thread button can still open detail");
            item.Pictures = new[] { new CommentPicture { ImageUrl = "https://i0.hdslb.com/bfs/actual.png" } };
            Check(CommentRichPresentation.HasPictures(item) && CommentRichPresentation.OpenDetailsOnPrimary(item, false, true), "Picture-only comments can open detail without text trimming");
            return "Rich comment presentation: " + _checks + " checks passed.";
        }
        private static void Check(bool condition, string description)
        {
            _checks++;
            if (!condition) throw new InvalidOperationException(description);
        }
    }
}
