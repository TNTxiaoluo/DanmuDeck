using System;
using System.Collections.Generic;
using System.Globalization;
using BiliXbox.Models;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Documents;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace BiliXbox.Controls
{
    public sealed class CommentRepliesRequestedEventArgs : EventArgs
    {
        public CommentItem Comment { get; }
        public CommentRepliesRequestedEventArgs(CommentItem comment) { Comment = comment; }
    }
    public sealed class CommentImageRequestedEventArgs : EventArgs
    {
        public CommentItem Comment { get; }
        public CommentPicture Picture { get; }
        public int ImageIndex { get; }
        public CommentImageRequestedEventArgs(CommentItem comment, int imageIndex)
        { Comment = comment; ImageIndex = imageIndex; Picture = comment.Pictures[imageIndex]; }
    }
    public sealed class CommentVoteRequestedEventArgs : EventArgs
    {
        public CommentItem Comment { get; }
        public CommentVote Vote { get; }
        public CommentVoteRequestedEventArgs(CommentItem comment, CommentVote vote) { Comment = comment; Vote = vote; }
    }
    public sealed class CommentCard : UserControl
    {
        public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(
            nameof(Item), typeof(CommentItem), typeof(CommentCard), new PropertyMetadata(null, Rebuild));
        public static readonly DependencyProperty ShowThreadButtonProperty = DependencyProperty.Register(
            nameof(ShowThreadButton), typeof(bool), typeof(CommentCard), new PropertyMetadata(true, Rebuild));
        public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(
            nameof(IsExpanded), typeof(bool), typeof(CommentCard), new PropertyMetadata(false, Rebuild));
        public CommentItem Item { get => (CommentItem)GetValue(ItemProperty); set => SetValue(ItemProperty, value); }
        public bool ShowThreadButton { get => (bool)GetValue(ShowThreadButtonProperty); set => SetValue(ShowThreadButtonProperty, value); }
        public bool IsExpanded { get => (bool)GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }
        public event EventHandler<CommentRepliesRequestedEventArgs> RepliesRequested;
        public event EventHandler<CommentRepliesRequestedEventArgs> DetailsRequested;
        public event EventHandler<CommentImageRequestedEventArgs> ImageRequested;
        public event EventHandler<CommentVoteRequestedEventArgs> VoteRequested;
        private readonly Dictionary<Control, Action> _actions = new Dictionary<Control, Action>();
        private readonly List<Control> _orderedActions = new List<Control>();
        private readonly List<Control> _pictureActions = new List<Control>();
        private readonly Dictionary<TextBlock, CommentItem> _previewMetadata = new Dictionary<TextBlock, CommentItem>();
        private RichTextBlock _mainText;
        private Button _likeButton;
        private Button _hateButton;
        private FontIcon _likeIcon;
        private FontIcon _hateIcon;
        private TextBlock _likeCount;
        private bool _reactionBusy;

        public CommentCard() { IsTabStop = false; }
        public void RequestReplies()
        {
            if (ShowThreadButton && Item != null) RepliesRequested?.Invoke(this, new CommentRepliesRequestedEventArgs(Item));
        }
        public void RequestPrimaryAction()
        {
            if (CommentRichPresentation.OpenDetailsOnPrimary(Item, _mainText?.IsTextTrimmed == true, ShowThreadButton)) RequestDetails(Item);
            else RequestReplies();
        }
        public void RequestDetails(CommentItem item)
        { if (item != null) DetailsRequested?.Invoke(this, new CommentRepliesRequestedEventArgs(item)); }
        public bool TryActivate(Control control)
        {
            Action action;
            if (!CanFocusAction(control) || !_actions.TryGetValue(control, out action)) return false;
            InvokeAction(action);
            return true;
        }
        private void InvokeAction(Action action)
        {
            // Native clicks on summary images/reaction buttons follow the same
            // entry rule as the raw gamepad dispatcher; voting happens inside
            // the thread after the user deliberately selects an action there.
            if (ShowThreadButton && !IsExpanded) RequestPrimaryAction();
            else action();
        }
        public Control PrimaryActionControl(bool preferPictures)
        {
            if (preferPictures)
                foreach (var picture in _pictureActions) if (CanFocusAction(picture)) return picture;
            if (CanFocusAction(_likeButton)) return _likeButton;
            foreach (var action in _orderedActions) if (CanFocusAction(action)) return action;
            return null;
        }
        public bool OwnsAction(Control control) => control != null && _actions.ContainsKey(control);
        internal Control NavigateAction(Control current, PlayerDirection direction)
        {
            var available = new List<Control>();
            foreach (var action in _orderedActions) if (CanFocusAction(action)) available.Add(action);
            if (available.Count == 0) return null;
            int position = available.IndexOf(current);
            if (position < 0) return PrimaryActionControl(IsExpanded);
            int step = direction == PlayerDirection.Left || direction == PlayerDirection.Up ? -1 : 1;
            return available[Math.Max(0, Math.Min(available.Count - 1, position + step))];
        }
        private static bool CanFocusAction(Control control)
            => control != null && control.IsEnabled && control.Visibility == Visibility.Visible;
        public void SetReactionBusy(bool busy)
        {
            _reactionBusy = busy;
            if (_likeButton != null) _likeButton.IsEnabled = !busy;
            if (_hateButton != null) _hateButton.IsEnabled = !busy;
        }
        public void RefreshReaction()
        {
            if (Item == null) return;
            foreach (var preview in _previewMetadata) preview.Key.Text = CommentRichPresentation.Metadata(preview.Value, true);
            if (_likeButton != null)
            {
                bool selected = Item.VoteKnown && Item.Vote == CommentVote.Like;
                _likeCount.Text = CommentRichPresentation.LikeCount(Item.Likes);
                _likeIcon.Opacity = selected ? 1 : 0.75;
                _likeCount.FontWeight = selected ? Windows.UI.Text.FontWeights.Bold : Windows.UI.Text.FontWeights.Normal;
                AutomationProperties.SetName(_likeButton, (Item.VoteKnown && Item.Vote == CommentVote.Like ? "取消点赞，" : "点赞，") + CommentRichPresentation.LikeCount(Item.Likes));
            }
            if (_hateButton != null)
            {
                _hateIcon.Opacity = Item.VoteKnown && Item.Vote == CommentVote.Hate ? 1 : 0.75;
                AutomationProperties.SetName(_hateButton, Item.VoteKnown && Item.Vote == CommentVote.Hate ? "取消点踩" : "点踩");
            }
        }
        private void RequestVote(CommentVote vote)
        {
            if (_reactionBusy || Item == null) return;
            VoteRequested?.Invoke(this, new CommentVoteRequestedEventArgs(Item,
                Item.VoteKnown && Item.Vote == vote ? CommentVote.None : vote));
        }
        private static void Rebuild(DependencyObject sender, DependencyPropertyChangedEventArgs args) => ((CommentCard)sender).Render();

        private void Render()
        {
            _actions.Clear(); _orderedActions.Clear(); _pictureActions.Clear(); _previewMetadata.Clear();
            _likeButton = _hateButton = null; _likeIcon = _hateIcon = null; _likeCount = null; _mainText = null;
            var item = Item;
            if (item == null) { Content = null; return; }
            var panel = new StackPanel { Spacing = 10, Margin = new Thickness(0, 8, 0, 18) };
            panel.Children.Add(BuildMessage(item, false));
            if (ShowThreadButton && !CommentRichPresentation.HasPictures(item) && item.Replies != null && item.Replies.Count > 0)
            {
                var previews = new StackPanel { Spacing = 12 };
                previews.Children.Add(BuildMessage(item.Replies[0], true));
                var previewBorder = new Border
                {
                    Background = Brush(25, 255, 255, 255), CornerRadius = new CornerRadius(12),
                    Padding = new Thickness(12), Child = previews
                };
                if (_mainText != null)
                {
                    var rootText = _mainText;
                    rootText.IsTextTrimmedChanged += (sender, args) =>
                        previewBorder.Visibility = rootText.IsTextTrimmed ? Visibility.Collapsed : Visibility.Visible;
                }
                panel.Children.Add(previewBorder);
            }
            if (ShowThreadButton)
            {
                bool hasReplies = item.ReplyCount > 0 || (item.Replies?.Count ?? 0) > 0;
                var button = ActionButton(item.ReplyCount > 0 ? "查看 " + item.ReplyCount.ToString("N0", CultureInfo.InvariantCulture) + " 条回复" : hasReplies ? "查看回复" : "查看评论详情",
                    () => { if (hasReplies) RequestReplies(); else RequestDetails(item); });
                AutomationProperties.SetAutomationId(button, "CommentThreadDetails");
                panel.Children.Add(button);
            }
            Content = panel;
            RefreshReaction(); SetReactionBusy(_reactionBusy);
        }

        private FrameworkElement BuildMessage(CommentItem item, bool compact)
        {
            var panel = new StackPanel { Spacing = compact ? 7 : 9 };
            panel.Children.Add(BuildAuthor(item, compact));
            var content = BuildContent(item, compact ? 15 : 17);
            if (!compact) _mainText = content;
            if (!IsExpanded)
            {
                content.MaxLines = compact || CommentRichPresentation.HasPictures(item) ? 2 : 4;
                content.TextTrimming = TextTrimming.CharacterEllipsis;
            }
            if (!string.IsNullOrEmpty(item.Content) || (item.ContentParts?.Count ?? 0) > 0) panel.Children.Add(content);
            if (!compact && CommentRichPresentation.HasPictures(item)) panel.Children.Add(BuildPictures(item));
            if (compact)
            {
                var metadata = new TextBlock { Text = CommentRichPresentation.Metadata(item, true), Foreground = Brush(150, 242, 242, 247), FontSize = 12, TextWrapping = TextWrapping.Wrap };
                _previewMetadata[metadata] = item;
                panel.Children.Add(metadata);
            }
            else
            {
                var footer = new Grid { ColumnSpacing = 8 };
                footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                footer.Children.Add(new TextBlock
                {
                    Text = CommentRichPresentation.Metadata(item, false), Foreground = Brush(150, 242, 242, 247), FontSize = 12,
                    TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center
                });
                _likeButton = ActionButton("", () => RequestVote(CommentVote.Like));
                _hateButton = ActionButton("", () => RequestVote(CommentVote.Hate));
                _likeIcon = ReactionIcon("\uE8E1"); _hateIcon = ReactionIcon("\uE8E0");
                _likeCount = new TextBlock { FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
                var likeContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
                likeContent.Children.Add(_likeIcon); likeContent.Children.Add(_likeCount);
                _likeButton.Content = likeContent; _hateButton.Content = _hateIcon;
                _likeButton.CornerRadius = _hateButton.CornerRadius = new CornerRadius(18);
                AutomationProperties.SetAutomationId(_likeButton, "CommentLike");
                AutomationProperties.SetAutomationId(_hateButton, "CommentDislike");
                Grid.SetColumn(_likeButton, 1); Grid.SetColumn(_hateButton, 2);
                footer.Children.Add(_likeButton); footer.Children.Add(_hateButton);
                panel.Children.Add(footer);
                if (!IsExpanded && !ShowThreadButton)
                {
                    var read = ActionButton("查看评论详情", () => RequestDetails(item));
                    read.Tag = item;
                    read.Visibility = CommentRichPresentation.HasPictures(item) ? Visibility.Visible : Visibility.Collapsed;
                    Action update = () => read.Visibility = CommentRichPresentation.HasPictures(item) || content.IsTextTrimmed ? Visibility.Visible : Visibility.Collapsed;
                    content.IsTextTrimmedChanged += (sender, args) => update();
                    content.Loaded += (sender, args) => update();
                    panel.Children.Add(read);
                }
            }
            return panel;
        }

        private FrameworkElement BuildAuthor(CommentItem item, bool compact)
        {
            var author = new Grid { ColumnSpacing = compact ? 7 : 10 };
            author.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            author.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            author.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            author.Children.Add(new AvatarView { Url = item.AvatarUrl, Width = compact ? 26 : 38, Height = compact ? 26 : 38, VerticalAlignment = VerticalAlignment.Center });
            var identity = new Grid { ColumnSpacing = 5, VerticalAlignment = VerticalAlignment.Center };
            identity.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            identity.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            identity.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            identity.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            identity.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(item.Author) ? "用户" : item.Author,
                Foreground = Brush(255, 243, 167, 191), FontSize = compact ? 14 : 16,
                FontWeight = Windows.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1
            });
            string level = CommentRichPresentation.LevelText(item.Level);
            if (level.Length > 0) { var badge = Badge(level, false); Grid.SetColumn(badge, 1); identity.Children.Add(badge); }
            if (item.IsUploader) { var badge = Badge("UP", true); Grid.SetColumn(badge, 2); identity.Children.Add(badge); }
            if (item.IsPinned && !compact) { var badge = Badge("置顶", false); Grid.SetColumn(badge, 3); identity.Children.Add(badge); }
            Grid.SetColumn(identity, 1); author.Children.Add(identity);
            if (!compact)
            {
                Uri badgeUri;
                bool hasImage = CommentRichPresentation.TryImageUri(item.FanBadgeImageUrl, out badgeUri);
                if (hasImage || !string.IsNullOrWhiteSpace(item.FanNumber))
                {
                    var decoration = new Grid { Width = 94, MinHeight = 42, VerticalAlignment = VerticalAlignment.Center };
                    if (hasImage)
                    {
                        var image = new Image { Stretch = Stretch.Uniform, Height = 48, Source = new BitmapImage(badgeUri) { DecodePixelWidth = 188 } };
                        image.ImageFailed += (sender, args) => image.Visibility = Visibility.Collapsed;
                        AutomationProperties.SetName(image, "用户收藏装扮");
                        decoration.Children.Add(image);
                    }
                    if (!string.IsNullOrWhiteSpace(item.FanNumber))
                        decoration.Children.Add(new TextBlock
                        {
                            Text = "CD. " + item.FanNumber, FontSize = 10, FontFamily = new FontFamily("Consolas"),
                            Foreground = Brush(255, 242, 242, 247), HorizontalAlignment = HorizontalAlignment.Right,
                            VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(2),
                            TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 90
                        });
                    Grid.SetColumn(decoration, 2); author.Children.Add(decoration);
                }
            }
            return author;
        }

        private FrameworkElement BuildPictures(CommentItem item)
        {
            int visible = IsExpanded ? item.Pictures.Count : Math.Min(3, item.Pictures.Count);
            int columns = Math.Min(3, visible);
            var grid = new Grid { ColumnSpacing = 8, RowSpacing = 8, HorizontalAlignment = IsExpanded ? HorizontalAlignment.Stretch : HorizontalAlignment.Left };
            if (!IsExpanded) grid.Width = columns * 116 + (columns - 1) * 8;
            for (int column = 0; column < columns; column++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int row = 0; row < (visible + columns - 1) / columns; row++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int index = 0; index < visible; index++)
            {
                int imageIndex = index;
                var picture = item.Pictures[index];
                var image = new Image { Stretch = IsExpanded ? Stretch.Uniform : Stretch.UniformToFill };
                var visual = new Grid { Background = Brush(255, 28, 28, 32), CornerRadius = new CornerRadius(10) };
                var placeholder = new TextBlock
                {
                    Text = "图片", FontSize = 13, Foreground = Brush(150, 242, 242, 247),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                };
                visual.Children.Add(placeholder); visual.Children.Add(image);
                Uri address;
                if (picture != null && CommentRichPresentation.TryImageUri(picture.ImageUrl, out address))
                {
                    image.ImageFailed += (sender, args) => image.Visibility = Visibility.Collapsed;
                    image.ImageOpened += (sender, args) => placeholder.Visibility = Visibility.Collapsed;
                    image.Source = new BitmapImage(address) { DecodePixelWidth = IsExpanded ? 640 : 256 };
                }
                var button = ActionButton("", () =>
                {
                    if (!IsExpanded) RequestDetails(item);
                    else ImageRequested?.Invoke(this, new CommentImageRequestedEventArgs(item, imageIndex));
                });
                _pictureActions.Add(button);
                button.Content = visual; button.Padding = new Thickness(4); button.CornerRadius = new CornerRadius(12);
                button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                button.Height = IsExpanded ? (columns == 1 ? 240 : 155) : 96;
                AutomationProperties.SetName(button, (IsExpanded ? "全屏查看图片 " : "查看图片评论 ") + (index + 1).ToString(CultureInfo.InvariantCulture) + "，共 " + item.Pictures.Count.ToString(CultureInfo.InvariantCulture) + " 张");
                AutomationProperties.SetAutomationId(button, "CommentPicture" + index.ToString(CultureInfo.InvariantCulture));
                Grid.SetColumn(button, index % columns); Grid.SetRow(button, index / columns); grid.Children.Add(button);
            }
            return grid;
        }

        private Button ActionButton(string text, Action action)
        {
            var button = new Button
            {
                Content = text, FontSize = 14, Padding = new Thickness(12, 7, 12, 7), MinHeight = 36,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            _actions[button] = action;
            _orderedActions.Add(button);
            button.Click += (sender, args) => { if (button.IsEnabled) InvokeAction(action); };
            return button;
        }

        private static FontIcon ReactionIcon(string glyph) => new FontIcon
        {
            Glyph = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 17,
            VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false
        };

        private static Border Badge(string text, bool accent)
            => new Border
            {
                Background = accent ? Brush(255, 190, 79, 114) : Brush(35, 255, 255, 255),
                CornerRadius = new CornerRadius(3), Padding = new Thickness(3, 1, 3, 1), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = text, FontSize = 10, Foreground = Brush(255, 242, 242, 247), FontWeight = Windows.UI.Text.FontWeights.SemiBold }
            };

        private static RichTextBlock BuildContent(CommentItem item, double fontSize)
        {
            var text = new RichTextBlock { FontSize = fontSize, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = false };
            var paragraph = new Paragraph();
            if (item.ContentParts == null || item.ContentParts.Count == 0) paragraph.Inlines.Add(new Run { Text = item.Content ?? "" });
            else foreach (var part in item.ContentParts)
            {
                if (part == null) continue;
                Uri address;
                if (!part.IsImage || !CommentRichPresentation.TryImageUri(part.ImageUrl, out address))
                { paragraph.Inlines.Add(new Run { Text = (part.IsImage ? part.AltText : part.Text) ?? "" }); continue; }
                var fallback = new TextBlock { Text = part.AltText ?? "[表情]", FontSize = fontSize, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
                var image = new Image
                {
                    Width = CommentRichPresentation.EmoteWidth(part), Height = CommentRichPresentation.EmoteHeight(part),
                    Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center
                };
                var container = new Grid { VerticalAlignment = VerticalAlignment.Center };
                container.Children.Add(image); container.Children.Add(fallback);
                AutomationProperties.SetName(image, part.AltText ?? "表情");
                image.ImageFailed += (sender, args) => { image.Visibility = Visibility.Collapsed; fallback.Visibility = Visibility.Visible; };
                image.ImageOpened += (sender, args) => fallback.Visibility = Visibility.Collapsed;
                image.Source = new BitmapImage(address) { DecodePixelWidth = (int)Math.Ceiling(image.Width * 2) };
                paragraph.Inlines.Add(new InlineUIContainer { Child = container });
            }
            text.Blocks.Add(paragraph);
            return text;
        }

        private static SolidColorBrush Brush(byte alpha, byte red, byte green, byte blue)
            => new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
    }
}
