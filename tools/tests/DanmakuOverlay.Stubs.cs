// Test doubles for Test-Danmaku.ps1 only. These types do not implement UWP,
// layout, font rendering, threading, or real composition frame scheduling.
// No code from this file is compiled into the BiliXbox application.
namespace Windows.Foundation
{
    public struct Size
    {
        public double Width;
        public double Height;
        public Size(double width, double height) { Width = width; Height = height; }
    }

    public struct Rect
    {
        public double X;
        public double Y;
        public double Width;
        public double Height;
        public Rect(double x, double y, double width, double height)
        {
            X = x; Y = y; Width = width; Height = height;
        }
    }
}

namespace Windows.UI
{
    public struct Color
    {
        public byte A;
        public byte R;
        public byte G;
        public byte B;
        public static Color FromArgb(byte alpha, byte red, byte green, byte blue)
        {
            return new Color { A = alpha, R = red, G = green, B = blue };
        }
    }
}

namespace Windows.UI.Text
{
    public static class FontWeights
    {
        public static int SemiBold { get { return 1; } }
    }
}

namespace Windows.UI.Xaml
{
    public enum Visibility { Visible, Collapsed }
    public enum TextWrapping { NoWrap }
    public enum TextTrimming { CharacterEllipsis }
    public class RoutedEventArgs : System.EventArgs { }
    public class SizeChangedEventArgs : System.EventArgs { }

}

namespace Windows.UI.Xaml.Media
{
    public static class CompositionTarget
    {
        private static System.EventHandler<object> _rendering;
        public static int SubscriberCount { get; private set; }
        public static event System.EventHandler<object> Rendering
        {
            add { _rendering += value; SubscriberCount++; }
            remove { _rendering -= value; SubscriberCount--; }
        }
        public static void Pulse() { if (_rendering != null) _rendering(null, null); }
    }

    public class TranslateTransform
    {
        private double _x;
        public static int Writes;
        public double X { get { return _x; } set { _x = value; Writes++; } }
    }

    public class RectangleGeometry
    {
        public Windows.Foundation.Rect Rect { get; set; }
    }

    public class SolidColorBrush
    {
        public Windows.UI.Color Color { get; private set; }
        public SolidColorBrush(Windows.UI.Color color) { Color = color; }
    }
}

namespace Windows.UI.Xaml.Controls
{
    public class Canvas
    {
        public bool IsHitTestVisible { get; set; }
        public object Clip { get; set; }
        public double ActualWidth { get; set; }
        public double ActualHeight { get; set; }
        public Windows.UI.Xaml.Visibility Visibility { get; set; }
        public System.Collections.Generic.List<TextBlock> Children =
            new System.Collections.Generic.List<TextBlock>();

        public event System.EventHandler<Windows.UI.Xaml.RoutedEventArgs> Loaded;
        public event System.EventHandler<Windows.UI.Xaml.RoutedEventArgs> Unloaded;
        public event System.EventHandler<Windows.UI.Xaml.SizeChangedEventArgs> SizeChanged;

        public static void SetLeft(TextBlock block, double left) { block.LayoutLeft = left; block.LeftLayoutWrites++; }
        public static void SetTop(TextBlock block, double top) { block.Top = top; }

        public void SetViewport(double width, double height)
        {
            ActualWidth = width;
            ActualHeight = height;
            if (SizeChanged != null) SizeChanged(this, new Windows.UI.Xaml.SizeChangedEventArgs());
        }

        public void RaiseLoaded()
        {
            if (Loaded != null) Loaded(this, new Windows.UI.Xaml.RoutedEventArgs());
        }

        public void RaiseUnloaded()
        {
            if (Unloaded != null) Unloaded(this, new Windows.UI.Xaml.RoutedEventArgs());
        }
    }

    public class TextBlock
    {
        public static int Creations;
        public static int Measurements;
        public TextBlock() { Creations++; }
        public string Text { get; set; }
        public double FontSize { get; set; }
        public int FontWeight { get; set; }
        public object Foreground { get; set; }
        public Windows.UI.Xaml.TextWrapping TextWrapping { get; set; }
        public Windows.UI.Xaml.TextTrimming TextTrimming { get; set; }
        public double MaxWidth { get; set; }
        public bool IsHitTestVisible { get; set; }
        public double Opacity { get; set; }
        public Windows.Foundation.Size DesiredSize { get; set; }
        public object RenderTransform { get; set; }
        public double LayoutLeft { get; set; }
        public int LeftLayoutWrites { get; set; }
        public double Left
        {
            get
            {
                var translation = RenderTransform as Windows.UI.Xaml.Media.TranslateTransform;
                return LayoutLeft + (translation == null ? 0 : translation.X);
            }
        }
        public double Top { get; set; }

        public void Measure(Windows.Foundation.Size availableSize)
        {
            Measurements++;
            // Deliberately deterministic width approximation for collision tests.
            DesiredSize = new Windows.Foundation.Size(
                System.Math.Min(availableSize.Width, Text.Length * FontSize * 0.6), FontSize * 1.2);
        }
    }
}
