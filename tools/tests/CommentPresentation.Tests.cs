using System;
using BiliXbox.Controls;
using BiliXbox.Models;

namespace BiliXbox.CommentPresentationTests
{
    public static class Tests
    {
        private static int _checks;

        public static string Run()
        {
            _checks = 0;
            var absent = CommentPresentation.Create(new CommentPage(), 0, null);
            Check(absent.Title == "评论 · 暂不可用", "Unknown empty is unavailable, not zero");
            Check(absent.Hint.Contains("暂未取得"), "Unknown empty has a retry hint");
            Check(absent.CanRetry && !absent.CanLoadMore && absent.CanWrite, "Unknown empty remains recoverable");
            NoFalseEmpty(absent);

            foreach (long? expected in new long?[] { null, 0, -1 })
            {
                var empty = CommentPresentation.Create(new CommentPage { TotalKnown = true }, 0, expected);
                Check(empty.Title == "评论 · 0", "Authoritative zero with no contrary evidence");
                Check(empty.Hint.Contains("还没有评论"), "Genuine zero receives empty message");
                Check(empty.MoreText == "暂无评论", "Genuine empty does not claim all loaded");
                Check(!empty.CanRetry && !empty.CanLoadMore && empty.CanWrite, "Genuine empty still allows writing");
            }

            foreach (bool known in new[] { false, true })
            {
                var falseZero = CommentPresentation.Create(new CommentPage { TotalKnown = known, Total = 0 }, 0, 3045);
                Check(falseZero.Title == "评论 · 3045", "Detail count survives bogus zero");
                Check(falseZero.CanRetry && falseZero.MoreText == "重试加载评论", "Bogus zero has actionable retry");
                NoFalseEmpty(falseZero);
            }

            var missing = CommentPresentation.Create(new CommentPage { TotalKnown = true, Total = 3045 }, 0, null);
            Check(missing.Title == "评论 · 3045" && missing.CanRetry, "Positive total with missing items is unavailable");
            NoFalseEmpty(missing);

            var partial = CommentPresentation.Create(new CommentPage
            {
                TotalKnown = true, Total = 3050, IsPartial = true,
                Items = Items(3), Notice = "  登录后可查看更完整的评论。  "
            }, 3, 3045);
            Check(partial.Title == "评论 · 3050", "Fresh known total replaces older detail count");
            Check(partial.Hint.Contains("已加载 3 条") && partial.Hint.Contains("部分评论"), "Partial reports actual loaded root count");
            Check(partial.Hint.Contains("\n登录后可查看更完整的评论。"), "Backend notice is retained and trimmed");
            Check(partial.CanRetry && !partial.CanLoadMore && partial.CanWrite, "Interrupted pagination allows explicit refresh without inventing a next page");
            Check(partial.MoreText == "重试加载评论", "Partial response does not permanently disable the only recovery action");
            NoFalseEmpty(partial);

            var next = CommentPresentation.Create(new CommentPage
            {
                TotalKnown = true, Total = 100, Items = Items(20), HasMore = true, NextCursor = "next-page"
            }, 40, 100);
            Check(next.Hint.Contains("40 条"), "Accumulated loaded count used");
            Check(next.CanLoadMore && !next.CanRetry && next.MoreText == "加载更多评论", "Valid backend cursor enables next page");

            var conflictingEmpty = CommentPresentation.Create(new CommentPage
            {
                TotalKnown = true, HasMore = true, NextCursor = "contradictory"
            }, 0, null);
            Check(!conflictingEmpty.CanLoadMore && conflictingEmpty.CanRetry, "Zero with a next-page claim is not genuine empty");
            NoFalseEmpty(conflictingEmpty);

            foreach (string cursor in new[] { null, "", "  " })
            {
                var broken = CommentPresentation.Create(new CommentPage
                {
                    TotalKnown = true, Total = 100, Items = Items(3), HasMore = true, NextCursor = cursor
                }, 3, 100);
                Check(!broken.CanLoadMore && broken.CanRetry, "HasMore without cursor cannot advance");
                Check(broken.Hint.Contains("部分评论") && broken.MoreText == "重试加载评论", "Missing cursor avoids complete claim");
                NoFalseEmpty(broken);
            }

            var ended = CommentPresentation.Create(new CommentPage
            {
                TotalKnown = true, Total = 3, Items = Items(3), NextCursor = "ignored-stale"
            }, 3, 5);
            Check(ended.Title == "评论 · 3", "Latest authoritative positive count wins");
            Check(!ended.CanLoadMore && !ended.CanRetry && ended.MoreText == "已加载全部评论", "Only explicit ordinary end reports complete");

            var unknownLoaded = CommentPresentation.Create(new CommentPage { Items = Items(2) }, 2, null);
            Check(unknownLoaded.Title == "评论 · 已加载 2", "Loaded count is not presented as an unknown total");
            Check(unknownLoaded.MoreText == "当前可用评论已加载", "Unknown total cannot claim a complete comment list");
            var expectedOnly = CommentPresentation.Create(new CommentPage { Items = Items(2) }, 2, 50);
            Check(expectedOnly.Title == "评论 · 50", "Known detail total remains useful with unknown response count");
            Check(expectedOnly.MoreText == "当前可用评论已加载", "Older detail total does not prove the response complete");
            Check(!expectedOnly.Hint.Contains("部分评论") && !expectedOnly.CanRetry, "Child-reply count differences alone do not invent a partial restriction");

            var contradictory = CommentPresentation.Create(new CommentPage { Items = Items(2), TotalKnown = true }, 2, null);
            Check(contradictory.Title == "评论 · 2", "Nonempty items cannot display zero");
            Check(contradictory.CanRetry && contradictory.Hint.Contains("部分评论"), "Contradictory count remains retryable");

            foreach (int loaded in new[] { 0, 3 })
            {
                var closed = CommentPresentation.Create(new CommentPage { IsClosed = true, Notice = "UP 主已关闭评论区。" }, loaded, 3045);
                Check(closed.Title == "评论 · 已关闭" && closed.Hint.Contains("UP 主已关闭"), "Closed state is explicit");
                Check(!closed.CanLoadMore && !closed.CanRetry && !closed.CanWrite, "Closed state disables unavailable actions");
                Check(!closed.Title.Contains("0") && !closed.Hint.Contains("还没有评论"), "Closed is not interpreted as zero comments");
            }

            var negative = CommentPresentation.Create(new CommentPage { Total = -10, TotalKnown = true }, -4, -5);
            Check(negative.CanRetry && negative.Title == "评论 · 暂不可用", "Invalid counts do not invent an authoritative zero");
            var itemCount = CommentPresentation.Create(new CommentPage { Items = Items(3) }, -1, null);
            Check(itemCount.Hint.Contains("3 条"), "Current response items guard an incorrect caller count");
            bool nullRejected = false;
            try { CommentPresentation.Create(null, 0, null); }
            catch (ArgumentNullException) { nullRejected = true; }
            Check(nullRejected, "Null response is rejected rather than interpreted as empty");
            return "Comment presentation: " + _checks + " checks passed.";
        }

        private static CommentItem[] Items(int count)
        {
            var items = new CommentItem[count];
            for (int index = 0; index < count; index++) items[index] = new CommentItem { Id = index + 1 };
            return items;
        }

        private static void NoFalseEmpty(CommentPresentation value)
        {
            Check(!value.Hint.Contains("还没有评论"), "Unavailable/partial never claims no comments");
            Check(value.MoreText != "已加载全部评论", "Unavailable/partial never claims complete");
        }

        private static void Check(bool condition, string message)
        {
            _checks++;
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
