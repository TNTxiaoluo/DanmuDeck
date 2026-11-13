// Only the immutable WinRT rectangle value type is substituted in these pure
// geometry tests. The production clipping implementation is compiled unchanged.
namespace Windows.Foundation
{
    public struct Rect
    {
        public double X, Y, Width, Height;
        public double Left => X;
        public double Top => Y;
        public double Right => X + Width;
        public double Bottom => Y + Height;
        public Rect(double x, double y, double width, double height)
        { X = x; Y = y; Width = width; Height = height; }
    }
}
