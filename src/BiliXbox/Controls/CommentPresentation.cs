using System;
using System.Globalization;
using BiliXbox.Models;

namespace BiliXbox.Controls
{
    /// <summary>Describes a completed comment response without inventing an empty or complete list.</summary>
    public sealed class CommentPresentation
    {
        public string Title { get; private set; }
        public string Hint { get; private set; }
        public string MoreText { get; private set; }
        public bool CanLoadMore { get; private set; }
        public bool CanRetry { get; private set; }
        public bool CanWrite { get; private set; }

        public static CommentPresentation Create(CommentPage page, int loadedCount, long? expectedTotal)
        {
            if (page == null) throw new ArgumentNullException(nameof(page));
            int loaded = Math.Max(Math.Max(0, loadedCount), page.Items == null ? 0 : page.Items.Count);
            long expected = Math.Max(0, expectedTotal ?? 0);
            bool known = page.TotalKnown && page.Total >= 0;
            long total = known && page.Total > 0 ? page.Total : expected;
            total = Math.Max(total, loaded);

            if (page.IsClosed)
            {
                return new CommentPresentation
                {
                    Title = "评论 · 已关闭",
                    Hint = WithNotice(loaded > 0 ? "已加载 " + Count(loaded) + " 条 · 该视频已关闭评论。" : "该视频已关闭评论。", page.Notice),
                    MoreText = "评论已关闭",
                    CanWrite = false
                };
            }

            bool empty = loaded == 0;
            bool invalidCursor = page.HasMore && string.IsNullOrWhiteSpace(page.NextCursor);
            bool unavailable = empty && (!known || page.Total > 0 || expected > 0 || page.IsPartial || page.HasMore);
            bool contradictoryZero = known && page.Total == 0 && loaded > 0;
            bool partial = page.IsPartial || invalidCursor || contradictoryZero;
            bool canLoadMore = page.HasMore && !string.IsNullOrWhiteSpace(page.NextCursor) && !unavailable;
            bool canRetry = unavailable || invalidCursor || contradictoryZero || partial && !canLoadMore;
            string hint;
            if (unavailable)
                hint = total > 0 ? "暂未加载到评论，请重试。" : "暂未取得评论数据，请重试。";
            else if (empty)
                hint = "还没有评论，来聊聊吧。";
            else
                hint = "已加载 " + Count(loaded) + " 条" + (partial ? " · 当前仅返回部分评论。" : "。");

            return new CommentPresentation
            {
                Title = total > 0 ? (known || expected > 0 ? "评论 · " + Count(total) : "评论 · 已加载 " + Count(loaded)) : (unavailable ? "评论 · 暂不可用" : "评论 · 0"),
                Hint = WithNotice(hint, page.Notice),
                MoreText = canLoadMore ? "加载更多评论" : canRetry ? "重试加载评论" : partial || !known ? "当前可用评论已加载" : empty ? "暂无评论" : "已加载全部评论",
                CanLoadMore = canLoadMore,
                CanRetry = canRetry,
                CanWrite = true
            };
        }

        private static string Count(long value) => value.ToString(CultureInfo.InvariantCulture);

        private static string WithNotice(string hint, string notice)
            => string.IsNullOrWhiteSpace(notice) ? hint : hint + "\n" + notice.Trim();
    }
}
