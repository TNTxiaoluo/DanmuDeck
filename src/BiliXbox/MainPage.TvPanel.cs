using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BiliXbox.Controls;
using Windows.Gaming.Input;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace BiliXbox
{
    public sealed partial class MainPage
    {
        private TaskCompletionSource<bool> _tvPanelCompletion;
        private IReadOnlyList<Button> _tvPanelRows;
        private DialogConfirmReleaseGate _tvPanelGate;
        private bool IsTvPanelOpen => _tvPanelCompletion != null;
        private bool OwnsTvPanelNavigation => IsTvPanelOpen && _activeInlineTextInput == null && !_inputPaneVisible;

        private Button CreateTvPanelRow(string text, Action action, bool wrap = false)
        {
            var button = new Button { Content = text, Style = (Style)Application.Current.Resources["TvRowButtonStyle"], Tag = action };
            if (wrap)
            {
                var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = button.Foreground };
                button.Content = label;
                button.GotFocus += (s, e) => label.Foreground = (Brush)Application.Current.Resources["TvFocusForegroundBrush"];
                button.LostFocus += (s, e) => label.Foreground = button.Foreground;
                Windows.UI.Xaml.Automation.AutomationProperties.SetName(button, text);
            }
            button.Click += (s, e) => { if (AcceptTvPanelClick()) action(); };
            return button;
        }

        private bool AcceptTvPanelClick() => OwnsTvPanelNavigation && _tvPanelGate?.Armed == true
            && !_rawGamepadInput.SuppressesClick(_gamepadClock.Elapsed);

        private async Task<bool> ShowTvPanelAsync(string title, FrameworkElement content, IReadOnlyList<Button> rows, Button initial = null)
        {
            if (_disposed || _dialogOpen) return false;
            var previous = FocusManager.GetFocusedElement() as Control;
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var gate = new DialogConfirmReleaseGate();
            var releaseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _tvPanelCompletion = completion;
            _tvPanelGate = gate;
            _tvPanelRows = rows;
            _dialogOpen = true;
            TvPanelTitle.Text = title;
            TvPanelContent.Content = content;
            TvPanelLayer.Visibility = Visibility.Visible;
            TvPanelScroll.ChangeView(null, 0, null, true);
            FocusGamepadControl(initial ?? TvPanelCloseButton);
            releaseTimer.Tick += (s, e) =>
            {
                gate.Observe(_gamepadClock.Elapsed, IsTvPanelConfirmHeld());
                if (gate.Armed) releaseTimer.Stop();
                if (!IsGamepadDescendant(FocusManager.GetFocusedElement() as DependencyObject, TvPanelLayer))
                    FocusGamepadControl(initial ?? TvPanelCloseButton);
            };
            releaseTimer.Start();
            try { return await completion.Task; }
            finally
            {
                releaseTimer.Stop();
                TvPanelLayer.Visibility = Visibility.Collapsed;
                TvPanelContent.Content = null;
                _tvPanelRows = null;
                _tvPanelGate = null;
                _tvPanelCompletion = null;
                _dialogOpen = false;
                _lastBack = DateTimeOffset.UtcNow;
                if (!_disposed)
                {
                    if (previous != null && previous.Focus(FocusState.Keyboard)) _selectedGamepadControl = previous;
                    else RestoreNavigationFocus();
                }
            }
        }

        private void FinishTvPanel(bool accepted) => _tvPanelCompletion?.TrySetResult(accepted);

        private void TvPanelClose_Click(object sender, RoutedEventArgs e)
        {
            if (AcceptTvPanelClick()) FinishTvPanel(false);
        }

        private bool HandleTvPanelKey(VirtualKey key, bool repeat)
        {
            if (!OwnsTvPanelNavigation) return false;
            if (key == VirtualKey.GamepadB || key == VirtualKey.NavigationCancel || key == VirtualKey.Escape)
            {
                if (!repeat && DateTimeOffset.UtcNow - _lastBack >= TimeSpan.FromMilliseconds(250)) FinishTvPanel(false);
                return true;
            }
            var current = FocusManager.GetFocusedElement() as Button;
            if (PlayerDirectionKey(key, out var direction) || key == VirtualKey.Tab)
            {
                if (key != VirtualKey.Tab && _tvPanelGridNavigation != null) return _tvPanelGridNavigation(direction, current);
                if (key != VirtualKey.Tab && (direction == PlayerDirection.Left || direction == PlayerDirection.Right)) return true;
                var rows = new[] { TvPanelCloseButton }.Concat(_tvPanelRows.Where(row => row.IsEnabled && row.Visibility == Visibility.Visible)).ToList();
                int index = rows.IndexOf(current);
                bool backwards = key == VirtualKey.Tab
                    ? (CoreWindow.GetForCurrentThread().GetKeyState(VirtualKey.Shift) & CoreVirtualKeyStates.Down) != 0
                    : direction == PlayerDirection.Up;
                int next = index < 0 ? 0 : index + (backwards ? -1 : 1);
                if (key == VirtualKey.Tab) next = (next + rows.Count) % rows.Count;
                FocusGamepadControl(rows[Math.Max(0, Math.Min(rows.Count - 1, next))]);
                return true;
            }
            if (!PlayerConfirmKey(key)) return false;
            if (!_tvPanelGate.KeyDown(repeat)) return true;
            if (current == TvPanelCloseButton) FinishTvPanel(false);
            else if (current != null && current.IsEnabled && _tvPanelRows.Contains(current) && current.Tag is Action action) action();
            return true;
        }

        private bool IsTvPanelConfirmHeld()
        {
            try
            {
                if (_activeGamepad != null && (_activeGamepad.GetCurrentReading().Buttons & GamepadButtons.A) != 0) return true;
                var window = CoreWindow.GetForCurrentThread();
                return window != null && (IsDown(VirtualKey.Enter) || IsDown(VirtualKey.Space) || IsDown(VirtualKey.NavigationAccept)
                    || (_activeGamepad == null && IsDown(VirtualKey.GamepadA)));
                bool IsDown(VirtualKey key) => (window.GetKeyState(key) & CoreVirtualKeyStates.Down) != 0;
            }
            catch (Exception) { return false; }
        }
    }
}
