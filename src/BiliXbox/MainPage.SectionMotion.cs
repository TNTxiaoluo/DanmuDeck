using System;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Controls;
using Windows.Foundation;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Media.Imaging;

namespace BiliXbox
{
    public sealed partial class MainPage
    {
        private CancellationTokenSource _sectionMotionCts;
        private Storyboard _sectionMotion;
        private int _sectionMotionGeneration, _sectionMotionDirection;
        private HomeSection? _pendingSection;

        private async Task<bool> PrepareSectionTransitionAsync(HomeSection target)
        {
            StopSectionMotion();
            int generation = _sectionMotionGeneration;
            _pendingSection = target;
            _sectionMotionDirection = Math.Sign((int)target - (int)_feedNavigation.Section);
            var owner = _sectionMotionCts = new CancellationTokenSource();
            var token = owner.Token;
            try
            {
                // The header stays fixed. Capture the outgoing feed before its
                // data is replaced; protected media is never captured.
                if (_sectionMotionDirection != 0 && target != HomeSection.Live && !_liveVisible
                    && HomeView.Visibility == Visibility.Visible && HomeView.ActualWidth > 0
                    && new UISettings().AnimationsEnabled)
                {
                    var image = new RenderTargetBitmap();
                    await image.RenderAsync(HomeView).AsTask(token);
                    if (generation != _sectionMotionGeneration || _disposed || token.IsCancellationRequested) return false;
                    SectionSnapshot.Source = image;
                }
            }
            catch (OperationCanceledException) { return false; }
            catch (Exception) { /* An unavailable capture must not block navigation. */ }
            return !_disposed && generation == _sectionMotionGeneration && !token.IsCancellationRequested;
        }

        private void StartSectionTransition()
        {
            _pendingSection = null;
            if (_disposed || SectionSnapshot.Source == null || HomeView.Visibility != Visibility.Visible) return;
            int generation = _sectionMotionGeneration;
            double width = Math.Max(1, HomeViewport.ActualWidth);
            HomeViewport.Clip = new RectangleGeometry { Rect = new Rect(0, 0, width, HomeViewport.ActualHeight) };
            var incoming = new TranslateTransform(); var outgoing = new TranslateTransform();
            HomeView.RenderTransform = incoming; SectionSnapshot.RenderTransform = outgoing;
            SectionSnapshot.Visibility = Visibility.Visible;
            incoming.X = _sectionMotionDirection * width;
            var storyboard = _sectionMotion = new Storyboard();
            AddSlide(storyboard, incoming, incoming.X, 0);
            AddSlide(storyboard, outgoing, 0, -_sectionMotionDirection * width);
            storyboard.Completed += (sender, args) =>
            {
                if (_disposed || generation != _sectionMotionGeneration) return;
                SectionSnapshot.Visibility = Visibility.Collapsed; SectionSnapshot.Source = null;
                incoming.X = 0; _sectionMotion = null;
            };
            storyboard.Begin();
        }

        private static void AddSlide(Storyboard storyboard, TranslateTransform target, double from, double to)
        {
            var animation = new DoubleAnimation { From = from, To = to, Duration = TimeSpan.FromMilliseconds(260),
                EnableDependentAnimation = true, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(animation, target); Storyboard.SetTargetProperty(animation, "X"); storyboard.Children.Add(animation);
        }

        private void StopSectionMotion()
        {
            ++_sectionMotionGeneration; _pendingSection = null;
            Cancel(ref _sectionMotionCts);
            _sectionMotion?.Stop(); _sectionMotion = null;
            if (HomeView.RenderTransform is TranslateTransform transform) transform.X = 0;
            SectionSnapshot.Visibility = Visibility.Collapsed; SectionSnapshot.Source = null;
        }
    }
}
