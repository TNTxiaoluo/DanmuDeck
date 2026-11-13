// Official UWP mapping: Key may be Arrow/Space while OriginalKey identifies
// GamepadDPad*/GamepadA. These types simulate data only, never event delivery.
namespace Windows.System
{
    public enum VirtualKey
    {
        None = 0, Enter = 13, Escape = 27, Space = 32,
        Left = 37, Up = 38, Right = 39, Down = 40, A = 65, Y = 89,
        NavigationUp = 138, NavigationDown = 139, NavigationLeft = 140,
        NavigationRight = 141, NavigationAccept = 142, NavigationCancel = 143,
        GamepadA = 195, GamepadB = 196, GamepadX = 197, GamepadY = 198,
        GamepadDPadUp = 203, GamepadDPadDown = 204,
        GamepadDPadLeft = 205, GamepadDPadRight = 206,
        GamepadLeftThumbstickUp = 211, GamepadLeftThumbstickDown = 212,
        GamepadLeftThumbstickRight = 213, GamepadLeftThumbstickLeft = 214
    }
}

namespace Windows.UI.Xaml.Input
{
    public sealed class KeyRoutedEventArgs
    {
        public Windows.System.VirtualKey Key { get; set; }
        public Windows.System.VirtualKey OriginalKey { get; set; }
    }
}
