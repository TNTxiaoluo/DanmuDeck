using System;
using System.Collections.Generic;
using BiliXbox.Models;

namespace BiliXbox.Services
{
    internal static class LiveEmoteContent
    {
        internal static IReadOnlyList<CommentContentPart> Split(string text, IReadOnlyList<LiveEmote> emotes)
        {
            text = text ?? "";
            if (emotes == null || emotes.Count == 0) return Array.Empty<CommentContentPart>();
            var parts = new List<CommentContentPart>(); int at = 0, plain = 0, images = 0;
            while (at < text.Length && images < 8)
            {
                LiveEmote match = null;
                for (int i = 0; i < emotes.Count && i < LiveEmoteMetadata.MaximumEmotes; i++)
                {
                    var candidate = emotes[i]; string url;
                    if (candidate == null || string.IsNullOrEmpty(candidate.Text) || candidate.Text.Length > text.Length - at
                        || !LiveEmoteMetadata.TryUrl(candidate.ImageUrl, out url)) continue;
                    if ((match == null || candidate.Text.Length > match.Text.Length)
                        && string.CompareOrdinal(text, at, candidate.Text, 0, candidate.Text.Length) == 0) match = candidate;
                }
                if (match == null) { ++at; continue; }
                if (at > plain) parts.Add(new CommentContentPart { Text = text.Substring(plain, at - plain) });
                string image; LiveEmoteMetadata.TryUrl(match.ImageUrl, out image);
                parts.Add(new CommentContentPart { ImageUrl = image, AltText = match.Text, Width = match.Width, Height = match.Height });
                at += match.Text.Length; plain = at; ++images;
            }
            if (plain < text.Length) parts.Add(new CommentContentPart { Text = text.Substring(plain) });
            if (images == 0) return Array.Empty<CommentContentPart>();
            return parts;
        }
    }
}
