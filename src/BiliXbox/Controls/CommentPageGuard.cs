using System;
using System.Collections.Generic;
using System.Linq;
using BiliXbox.Models;

namespace BiliXbox.Controls
{
    // Pagination tokens are opaque session state and can repeat even as the
    // server advances. Detect progress from reply IDs, not token identity.
    internal sealed class CommentPageGuard
    {
        internal void Accept(CommentPage page, string requested, bool reset, IEnumerable<CommentItem> existing)
        {
            if (page == null) throw new ArgumentNullException(nameof(page));
            if (!reset && page.Items.Count > 0)
            {
                var ids = new HashSet<long>((existing ?? Array.Empty<CommentItem>()).Select(item => item.Id));
                if (page.Items.All(item => ids.Contains(item.Id)))
                    throw new InvalidOperationException("B站未返回新的评论，已保留已加载内容。可重试，或刷新评论。");
            }
            if (page.HasMore && string.IsNullOrWhiteSpace(page.NextCursor))
            {
                // Keep genuinely new rows without inventing a continuation.
                page.HasMore = false;
                page.NextCursor = "";
                page.IsPartial = true;
                page.Notice = "B站未返回可继续翻页的有效游标，已保留加载的评论。可刷新后重试。";
            }
        }
    }
}
