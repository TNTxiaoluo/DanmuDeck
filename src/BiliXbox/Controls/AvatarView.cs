using System;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Shapes;

namespace BiliXbox.Controls
{
    public sealed class AvatarView : Grid
    {
        public static readonly DependencyProperty UrlProperty = DependencyProperty.Register(
            nameof(Url), typeof(string), typeof(AvatarView), new PropertyMetadata("", OnUrlChanged));
        public string Url { get => (string)GetValue(UrlProperty); set => SetValue(UrlProperty, value); }
        private readonly Ellipse _picture;
        private readonly FontIcon _placeholder;
        private BitmapImage _current;

        public AvatarView()
        {
            Width = Height = 36;
            Children.Add(new Ellipse { Fill = new SolidColorBrush(Color.FromArgb(255, 49, 55, 69)) });
            _placeholder = new FontIcon { Glyph = "\uE77B", FontSize = 20, Foreground = new SolidColorBrush(Colors.LightGray) };
            Children.Add(_placeholder);
            _picture = new Ellipse();
            Children.Add(_picture);
        }

        private static void OnUrlChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
            => ((AvatarView)sender).Load(args.NewValue as string);

        private void Load(string url)
        {
            _current = null;
            _picture.Fill = null;
            _placeholder.Visibility = Visibility.Visible;
            Uri address;
            if (!Uri.TryCreate(url, UriKind.Absolute, out address) || address.Scheme != "https") return;
            var bitmap = _current = new BitmapImage { DecodePixelWidth = 96 };
            bitmap.ImageOpened += (sender, args) => { if (_current == bitmap) _placeholder.Visibility = Visibility.Collapsed; };
            bitmap.ImageFailed += (sender, args) => { if (_current == bitmap) { _picture.Fill = null; _placeholder.Visibility = Visibility.Visible; } };
            _picture.Fill = new ImageBrush { ImageSource = bitmap, Stretch = Stretch.UniformToFill };
            bitmap.UriSource = address;
        }
    }
}
