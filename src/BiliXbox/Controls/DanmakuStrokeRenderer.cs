using System;
using System.Numerics;
using BiliXbox.Models;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Hosting;

namespace BiliXbox.Controls
{
    // XAML text has no stroke. A composition drop shadow masked by the text's
    // own alpha draws the edge on the GPU: zero offset with a small blur reads
    // as an outline, a wider blur as a heavy halo, an offset as a 45° shadow.
    // One host is created per pooled TextBlock and reused with it.
    internal sealed class CompositionDanmakuStrokeRenderer : IDanmakuStrokeRenderer
    {
        private sealed class State
        {
            internal SpriteVisual Sprite;
            internal DropShadow Shadow;
        }

        public FrameworkElement Create(TextBlock text)
        {
            var host = new Canvas { IsHitTestVisible = false };
            var compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
            var sprite = compositor.CreateSpriteVisual();
            var shadow = compositor.CreateDropShadow();
            shadow.Mask = text.GetAlphaMask();
            shadow.Color = Colors.Black;
            sprite.Shadow = shadow;
            ElementCompositionPreview.SetElementChildVisual(host, sprite);
            void Resize() => sprite.Size = new Vector2((float)Math.Max(0, text.ActualWidth), (float)Math.Max(0, text.ActualHeight));
            text.SizeChanged += (s, e) => Resize();
            Resize();
            host.Tag = new State { Sprite = sprite, Shadow = shadow };
            return host;
        }

        public void Apply(FrameworkElement host, DanmakuStrokeStyle style)
        {
            var state = host?.Tag as State;
            if (state == null) return;
            var shadow = state.Shadow;
            switch (style)
            {
                case DanmakuStrokeStyle.Heavy:
                    shadow.Offset = Vector3.Zero; shadow.BlurRadius = 6; shadow.Opacity = 1; break;
                case DanmakuStrokeStyle.Shadow:
                    shadow.Offset = new Vector3(2, 2, 0); shadow.BlurRadius = 1.5f; shadow.Opacity = 0.9f; break;
                default:
                    shadow.Offset = Vector3.Zero; shadow.BlurRadius = 2.5f; shadow.Opacity = 1; break;
            }
        }
    }
}
