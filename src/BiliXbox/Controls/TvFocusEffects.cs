using System;
using System.Runtime.CompilerServices;
using System.Numerics;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Media;

namespace BiliXbox.Controls
{
    // tvOS-style poster focus: the focused card lifts (scale + soft shadow) and
    // the others stay put. Everything runs on the container's own composition
    // visual, so it follows GridView scrolling without a separate overlay layer.
    internal static class TvFocusEffects
    {
        private static readonly TimeSpan LiftDuration = TimeSpan.FromMilliseconds(320);
        private static readonly TimeSpan DropDuration = TimeSpan.FromMilliseconds(240);
        private static readonly ConditionalWeakTable<Control, object> Attached = new ConditionalWeakTable<Control, object>();

        public static void AttachCardFocus(ListViewBase list, Action<object> focused = null)
        {
            if (list == null) return;
            list.ContainerContentChanging += (sender, args) =>
            {
                var container = args.ItemContainer;
                if (container == null) return;
                if (args.InRecycleQueue) { Settle(container, false, false); return; }
                object attachment;
                if (!Attached.TryGetValue(container, out attachment))
                {
                    Attached.Add(container, new object());
                    container.GotFocus += (s, e) => { Settle(container, true, true); focused?.Invoke(container.Content); };
                    container.LostFocus += (s, e) => Settle(container, false, true);
                    container.Unloaded += (s, e) => Settle(container, false, false);
                }
                // A recycled container may still carry the lifted look of its previous item.
                Settle(container, container.FocusState != FocusState.Unfocused, false);
            };
        }

        private static void Settle(Control container, bool lifted, bool animate)
        {
            try
            {
                var visual = ElementCompositionPreview.GetElementVisual(container);
                var compositor = visual.Compositor;
                visual.CenterPoint = new Vector3((float)container.ActualWidth / 2f, (float)container.ActualHeight / 2f, 0);
                Canvas.SetZIndex(container, lifted ? 10 : 0);
                var shadow = EnsureShadow(container, compositor);
                float scale = lifted ? TvFocusGeometry.GetScale(container.ActualWidth) : 1f;
                if (!animate)
                {
                    visual.StopAnimation("Scale");
                    visual.Scale = new Vector3(scale, scale, 1);
                    if (shadow != null) { shadow.StopAnimation("Opacity"); shadow.Opacity = lifted ? 1f : 0f; }
                    return;
                }
                var easing = lifted
                    ? compositor.CreateCubicBezierEasingFunction(new Vector2(0.18f, 0.89f), new Vector2(0.32f, 1f))
                    : compositor.CreateCubicBezierEasingFunction(new Vector2(0.25f, 0.1f), new Vector2(0.25f, 1f));
                var duration = lifted ? LiftDuration : DropDuration;
                var grow = compositor.CreateVector3KeyFrameAnimation();
                grow.InsertKeyFrame(1f, new Vector3(scale, scale, 1), easing);
                grow.Duration = duration;
                visual.StartAnimation("Scale", grow);
                if (shadow != null)
                {
                    var fade = compositor.CreateScalarKeyFrameAnimation();
                    fade.InsertKeyFrame(1f, lifted ? 1f : 0f, easing);
                    fade.Duration = duration;
                    shadow.StartAnimation("Opacity", fade);
                }
            }
            catch (Exception)
            {
                // Composition is decorative; focus and navigation never depend on it.
            }
        }

        // The card template's first element tagged "TvShadow" hosts a blurred
        // drop shadow that only becomes visible while the card is lifted.
        private static SpriteVisual EnsureShadow(Control container, Compositor compositor)
        {
            // ContentTemplateRoot lives on ContentControl; GridViewItem and
            // ListViewItem are ContentControls so the cast is always valid here.
            var cc = container as ContentControl;
            var contentRoot = cc?.ContentTemplateRoot ?? cc?.Content as DependencyObject;
            var host = FindTagged(contentRoot, "TvShadow");
            if (host == null || host.ActualWidth <= 0) return null;
            var existing = ElementCompositionPreview.GetElementChildVisual(host) as SpriteVisual;
            var size = new Vector2((float)host.ActualWidth, (float)host.ActualHeight);
            if (existing != null) { existing.Size = size; return existing; }
            var drop = compositor.CreateDropShadow();
            drop.BlurRadius = 14f;
            drop.Offset = new Vector3(0, 6f, 0);
            drop.Color = Color.FromArgb(96, 0, 0, 0);
            var sprite = compositor.CreateSpriteVisual();
            sprite.Size = size;
            sprite.Shadow = drop;
            sprite.Opacity = 0f;
            ElementCompositionPreview.SetElementChildVisual(host, sprite);
            return sprite;
        }

        private static FrameworkElement FindTagged(DependencyObject root, string tag)
        {
            if (root == null) return null;
            var element = root as FrameworkElement;
            if (element != null && tag.Equals(element.Tag as string, StringComparison.Ordinal)) return element;
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var match = FindTagged(VisualTreeHelper.GetChild(root, i), tag);
                if (match != null) return match;
            }
            return null;
        }

        // Fades and slides a panel in whenever its Visibility becomes Visible,
        // and fades it out when collapsed. Purely visual; layout is unchanged.
        public static void AttachShowHide(UIElement element, float fromX, float fromY, int showMs = 260, int hideMs = 180)
        {
            if (element == null) return;
            try
            {
                var compositor = ElementCompositionPreview.GetElementVisual(element).Compositor;
                ElementCompositionPreview.SetIsTranslationEnabled(element, true);
                var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.9f), new Vector2(0.3f, 1f));
                var showOpacity = compositor.CreateScalarKeyFrameAnimation();
                showOpacity.Target = "Opacity";
                showOpacity.InsertKeyFrame(0f, 0f);
                showOpacity.InsertKeyFrame(1f, 1f, easing);
                showOpacity.Duration = TimeSpan.FromMilliseconds(showMs);
                var showGroup = compositor.CreateAnimationGroup();
                showGroup.Add(showOpacity);
                if (fromX != 0 || fromY != 0)
                {
                    var slide = compositor.CreateVector3KeyFrameAnimation();
                    slide.Target = "Translation";
                    slide.InsertKeyFrame(0f, new Vector3(fromX, fromY, 0));
                    slide.InsertKeyFrame(1f, Vector3.Zero, easing);
                    slide.Duration = TimeSpan.FromMilliseconds(showMs);
                    showGroup.Add(slide);
                }
                ElementCompositionPreview.SetImplicitShowAnimation(element, showGroup);
                var hideOpacity = compositor.CreateScalarKeyFrameAnimation();
                hideOpacity.Target = "Opacity";
                hideOpacity.InsertKeyFrame(1f, 0f);
                hideOpacity.Duration = TimeSpan.FromMilliseconds(hideMs);
                ElementCompositionPreview.SetImplicitHideAnimation(element, hideOpacity);
            }
            catch (Exception) { /* Decorative only. */ }
        }
    }
}
