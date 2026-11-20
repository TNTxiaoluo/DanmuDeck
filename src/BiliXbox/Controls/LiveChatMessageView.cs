using System;
using System.Collections.Generic;
using BiliXbox.Models;
using BiliXbox.Services;
using Windows.UI;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Documents;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace BiliXbox.Controls
{
    // The ListView creates these only for realized rows. Never build images for
    // all incoming packets or from a rendering/composition callback.
    public sealed class LiveChatMessageView : UserControl
    {
        public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(nameof(Message), typeof(LiveChatMessage),
            typeof(LiveChatMessageView), new PropertyMetadata(null, (owner, args) => ((LiveChatMessageView)owner).Refresh()));
        public LiveChatMessage Message { get => (LiveChatMessage)GetValue(MessageProperty); set => SetValue(MessageProperty, value); }
        public LiveChatMessageView() { IsTabStop = false; IsHitTestVisible = false; Margin = new Thickness(6, 7, 6, 7); }
        private void Refresh() => Content = LiveRichContent.Chat(Message);
    }

    internal static class LiveRichContent
    {
        internal static FrameworkElement Chat(LiveChatMessage message)
        {
            if (message == null) return new TextBlock();
            string prefix = string.IsNullOrWhiteSpace(message.UserName) ? "" : message.UserName + "：";
            string content = message.Kind == LiveChatMessageKind.Gift
                ? message.UserName + " 赠送 " + message.GiftName + " × " + message.GiftCount : message.Text ?? "";
            var parts = LiveEmoteContent.Split(content, message.Emotes);
            if (parts.Count == 0) return new TextBlock { Text = message.Kind == LiveChatMessageKind.Gift ? content : prefix + content,
                FontSize = 16, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Colors.White) };
            var merged = new List<CommentContentPart> { new CommentContentPart { Text = prefix } };
            merged.AddRange(parts);
            return Build(merged, 16, 430, 0xFFFFFF, true);
        }
        internal static FrameworkElement Overlay(IReadOnlyList<CommentContentPart> parts, double font, double maximum, int rgb)
            => Build(parts, font, maximum, rgb, false);

        private static RichTextBlock Build(IReadOnlyList<CommentContentPart> parts, double font, double maximum, int rgb, bool wrap)
        {
            var block = new RichTextBlock { FontSize = font, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)),
                IsTextSelectionEnabled = false, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
                HorizontalAlignment = HorizontalAlignment.Left, MaxWidth = maximum, IsHitTestVisible = false };
            var paragraph = new Paragraph(); int images = 0;
            foreach (var part in parts)
            {
                if (part == null) continue;
                string url;
                if (!part.IsImage || ++images > 8 || !LiveEmoteMetadata.TryUrl(part.ImageUrl, out url))
                { paragraph.Inlines.Add(new Run { Text = part.IsImage ? part.AltText : part.Text }); continue; }
                double height = Math.Min(48, Math.Max(20, font * 1.25));
                double ratio = part.Width > 0 && part.Height > 0 ? part.Width / part.Height : 1;
                if (double.IsNaN(ratio) || double.IsInfinity(ratio)) ratio = 1;
                double width = height * Math.Max(.5, Math.Min(3, ratio));
                var image = new Image { Width = width, Height = height, Stretch = Stretch.Uniform };
                var fallback = new TextBlock { Text = part.AltText ?? "[表情]", FontSize = font,
                    Foreground = block.Foreground, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, VerticalAlignment = VerticalAlignment.Center };
                var box = new Grid { Width = width, Height = height, VerticalAlignment = VerticalAlignment.Center };
                box.Children.Add(image); box.Children.Add(fallback);
                AutomationProperties.SetName(image, part.AltText ?? "表情");
                image.ImageOpened += (sender, args) => fallback.Visibility = Visibility.Collapsed;
                image.ImageFailed += (sender, args) => { image.Visibility = Visibility.Collapsed; fallback.Visibility = Visibility.Visible; };
                image.Source = new BitmapImage(new Uri(url)) { DecodePixelWidth = (int)Math.Ceiling(width * 2) };
                paragraph.Inlines.Add(new InlineUIContainer { Child = box });
            }
            block.Blocks.Add(paragraph); return block;
        }
    }
}
