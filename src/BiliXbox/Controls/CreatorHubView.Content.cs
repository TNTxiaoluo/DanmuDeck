using System;
using BiliXbox.Models;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Documents;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace BiliXbox.Controls
{
    public sealed partial class CreatorHubView
    {
        private FrameworkElement BuildDynamicSummary(CreatorDynamicItem item)
        {
            // Keep the summary a stretched content card. A horizontal StackPanel
            // measures its text at infinity and can collapse rich posts to a tiny
            // strip when used as a ListView item.
            var body = new StackPanel { Spacing = 16, Padding = new Thickness(22) };
            body.Children.Add(BuildAuthor(item));
            var contentGrid = new Grid { ColumnSpacing = 22 };
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var prose = new StackPanel { Spacing = 12 };
            var content = DynamicText(item, 18); content.MaxLines = 4; content.TextTrimming = TextTrimming.CharacterEllipsis;
            prose.Children.Add(content);
            if (item.Original != null)
            {
                var forwarded = new StackPanel { Spacing = 6 };
                forwarded.Children.Add(SecondaryText("转发 · " + item.Original.Author, 13));
                var preview = DynamicText(item.Original, 15); preview.MaxLines = 2; preview.TextTrimming = TextTrimming.CharacterEllipsis;
                forwarded.Children.Add(preview);
                prose.Children.Add(new Border
                {
                    Padding = new Thickness(14), CornerRadius = new CornerRadius(12),
                    Background = Brush(255, 38, 38, 42), BorderBrush = SubtleBorderBrush(),
                    BorderThickness = new Thickness(1), Child = forwarded
                });
            }
            var video = FindDynamicVideo(item);
            if (video != null)
            {
                var title = Text(video.Title, 17, true);
                title.TextWrapping = TextWrapping.Wrap; title.MaxLines = 2; title.TextTrimming = TextTrimming.CharacterEllipsis;
                prose.Children.Add(title);
            }
            contentGrid.Children.Add(prose);
            var images = item.ImageUrls?.Count > 0 ? item.ImageUrls : item.Original?.ImageUrls;
            string cover = video?.CoverUrl ?? (images?.Count > 0 ? images[0] : null);
            if (!string.IsNullOrWhiteSpace(cover))
            {
                contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(244) });
                var media = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Top };
                var image = MakeImage(cover, 138, false); if (image != null) media.Children.Add(image);
                media.Children.Add(SecondaryText(video == null ? (images.Count + " 张图片") : "视频 · " + video.DurationText, 13));
                Add(contentGrid, media, 0, 1);
            }
            body.Children.Add(contentGrid);
            var footer = new Grid { ColumnSpacing = 20 };
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            footer.Children.Add(SecondaryText("点赞 " + item.LikeCount + "   ·   评论 " + item.CommentCount + "   ·   转发 " + item.RepostCount, 13));
            Add(footer, Text("A 查看全文", 14, true), 0, 1); body.Children.Add(footer);
            var card = new Grid
            {
                Tag = item, MaxWidth = 1000, HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(16, 14, 16, 22)
            };
            card.Children.Add(new Grid { Tag = "TvShadow", IsHitTestVisible = false });
            card.Children.Add(new Border
            {
                Child = body, Background = CardBrush(), CornerRadius = new CornerRadius(16),
                BorderBrush = SubtleBorderBrush(), BorderThickness = new Thickness(1)
            });
            return card;
        }

        private void AppendDynamic(StackPanel panel, CreatorDynamicItem item, bool original)
        {
            if (item == null) return;
            panel.Children.Add(BuildAuthor(item));
            panel.Children.Add(DynamicText(item, original ? 16 : 18));
            if (item.ImageUrls != null) foreach (var url in item.ImageUrls)
            { var image = MakeImage(url, 700, true); if (image != null) panel.Children.Add(image); }
            if (item.Video != null)
            {
                var video = new StackPanel { Spacing = 12 };
                var cover = MakeImage(item.Video.CoverUrl, 350, true); if (cover != null) video.Children.Add(cover);
                var title = Text(item.Video.Title, 18, true); title.TextWrapping = TextWrapping.Wrap;
                video.Children.Add(title);
                if (!string.IsNullOrEmpty(item.Video.DurationText)) video.Children.Add(SecondaryText("视频 · " + item.Video.DurationText, 13));
                panel.Children.Add(video);
            }
            panel.Children.Add(SecondaryText("点赞 " + item.LikeCount + "   ·   评论 " + item.CommentCount + "   ·   转发 " + item.RepostCount, 14));
            if (!original && item.Original != null)
            {
                var forwarded = new StackPanel { Spacing = 16 };
                forwarded.Children.Add(SecondaryText("转发的动态", 14));
                AppendDynamic(forwarded, item.Original, true);
                panel.Children.Add(new Border
                {
                    Padding = new Thickness(18), Background = Brush(255, 38, 38, 42),
                    CornerRadius = new CornerRadius(12), BorderBrush = SubtleBorderBrush(),
                    BorderThickness = new Thickness(1), Child = forwarded
                });
            }
        }

        private static Grid BuildAuthor(CreatorDynamicItem item)
        {
            var author = new Grid { ColumnSpacing = 12 };
            author.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            author.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            author.Children.Add(new AvatarView { Url = item.AvatarUrl, Width = 42, Height = 42 });
            var identity = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            var name = Text(item.Author, 17, true); name.TextTrimming = TextTrimming.CharacterEllipsis;
            identity.Children.Add(name); identity.Children.Add(SecondaryText(item.TimeText, 13));
            Add(author, identity, 0, 1); return author;
        }

        private static RichTextBlock DynamicText(CreatorDynamicItem item, double fontSize)
        {
            var text = new RichTextBlock
            {
                FontSize = fontSize, LineHeight = fontSize * 1.48, TextWrapping = TextWrapping.Wrap,
                Foreground = Brush(255, 242, 242, 247), IsTextSelectionEnabled = false
            };
            var paragraph = new Paragraph();
            if (item.ContentParts == null || item.ContentParts.Count == 0)
                paragraph.Inlines.Add(new Run { Text = string.IsNullOrWhiteSpace(item.Content) ? "（此动态没有文字）" : item.Content });
            else foreach (var part in item.ContentParts)
            {
                Uri address;
                if (!part.IsImage || !Uri.TryCreate(part.ImageUrl, UriKind.Absolute, out address) || address.Scheme != "https")
                { paragraph.Inlines.Add(new Run { Text = part.IsImage ? part.AltText : part.Text }); continue; }
                var fallback = Text(part.AltText, fontSize);
                var image = new Image { Width = 28, Height = 28, Stretch = Stretch.Uniform };
                var container = new Grid(); container.Children.Add(image); container.Children.Add(fallback);
                image.ImageOpened += (s, e) => fallback.Visibility = Visibility.Collapsed;
                image.ImageFailed += (s, e) => { image.Visibility = Visibility.Collapsed; fallback.Visibility = Visibility.Visible; };
                image.Source = new BitmapImage(address) { DecodePixelWidth = 56 };
                paragraph.Inlines.Add(new InlineUIContainer { Child = container });
            }
            text.Blocks.Add(paragraph); return text;
        }

        private static FrameworkElement MakeImage(string url, double maxHeight, bool full)
        {
            Uri address;
            if (!Uri.TryCreate(url, UriKind.Absolute, out address) || address.Scheme != "https") return null;
            var photo = new Image
            {
                MaxHeight = maxHeight, Stretch = full ? Stretch.Uniform : Stretch.UniformToFill,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            if (!full) photo.Height = maxHeight;
            var fallback = SecondaryText("图片暂时无法显示", 14); fallback.Visibility = Visibility.Collapsed;
            var wrap = new Grid(); wrap.Children.Add(photo); wrap.Children.Add(fallback);
            photo.ImageFailed += (s, e) => { photo.Visibility = Visibility.Collapsed; fallback.Visibility = Visibility.Visible; };
            photo.Source = new BitmapImage(address) { DecodePixelWidth = full ? 960 : 260 };
            return new Border
            {
                Child = wrap, Background = Brush(255, 38, 38, 42), CornerRadius = new CornerRadius(12),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
        }

        private static TextBlock SecondaryText(string value, double size)
        { var text = Text(value, size); text.Foreground = SecondaryBrush(); return text; }

        private static VideoItem FindDynamicVideo(CreatorDynamicItem item) => item?.Video ?? item?.Original?.Video;
    }
}
