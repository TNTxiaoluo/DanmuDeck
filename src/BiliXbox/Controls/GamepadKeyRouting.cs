using Windows.System;
using Windows.UI.Xaml.Input;

namespace BiliXbox.Controls
{
    internal static class GamepadKeyRouting
    {
        public static bool IsNavigationKey(VirtualKey key)
        {
            switch (key)
            {
                case VirtualKey.GamepadDPadLeft: case VirtualKey.GamepadDPadRight:
                case VirtualKey.GamepadDPadUp: case VirtualKey.GamepadDPadDown:
                case VirtualKey.GamepadLeftThumbstickLeft: case VirtualKey.GamepadLeftThumbstickRight:
                case VirtualKey.GamepadLeftThumbstickUp: case VirtualKey.GamepadLeftThumbstickDown:
                case VirtualKey.NavigationLeft: case VirtualKey.NavigationRight:
                case VirtualKey.NavigationUp: case VirtualKey.NavigationDown:
                case VirtualKey.GamepadA: case VirtualKey.NavigationAccept:
                    return true;
                default: return false;
            }
        }

        public static bool IsNavigationEvent(KeyRoutedEventArgs args)
        {
            // Xbox maps D-pad to arrows and A to Space. Inspect the origin so
            // XAML cannot move focus again after our physical sampler handled it.
            // Ordinary keyboard arrows/Space must retain their native behavior.
            return IsNavigationKey(args.OriginalKey) || IsNavigationKey(args.Key);
        }
    }
}
