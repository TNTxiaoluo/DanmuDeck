using System;
using System.Collections.Generic;
using System.Diagnostics;
using BiliXbox.Controls;
using BiliXbox.Models;
using Windows.Foundation;
using Windows.Gaming.Input;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace BiliXbox
{
    public sealed partial class MainPage
    {
        private readonly DispatcherTimer _gamepadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        private readonly Stopwatch _gamepadClock = Stopwatch.StartNew();
        private readonly GamepadInputState _rawGamepadInput = new GamepadInputState();
        private readonly GamepadDirectionMapper _gamepadDirectionMapper = new GamepadDirectionMapper();
        private readonly object _gamepadDevicesLock = new object();
        private readonly List<Gamepad> _connectedGamepads = new List<Gamepad>();
        private Gamepad _activeGamepad;
        private Control _selectedGamepadControl;
        private ListView _gamepadNavigationList;
        private int _gamepadNavigationIndex = -1, _gamepadListFocusGeneration;
        private bool _gamepadListFocusPending, _gamepadRescanNeeded;
        private bool _gamepadHooksInstalled, _gamepadStarted;
        private bool _gamepadWindowActive = true, _gamepadWindowVisible = true;
        private bool _gamepadAnalogBlockedUntilNeutral = true;
        private int _gamepadHomeIndex, _gamepadHomeFocusGeneration;
        private bool _gamepadHomeFocusPending;
        private bool _gamepadHomeInitialFocusDone;
        private bool _gamepadHomeFeedReplacing;
        private object _gamepadHomeItem;
        private int _gamepadCount, _rawGamepadActions, _gamepadReadFailures;
        private string _lastRawGamepadDirection = "无";

        // The routed/CoreWindow handlers swallow only gamepad-generated A/D-pad
        // events under this condition. Physical keyboard arrows and Enter stay live.
        private bool RawGamepadOwnsNavigation
        {
            get
            {
                if (!_gamepadStarted || _disposed || !_gamepadWindowActive || !_gamepadWindowVisible ||
                    _dialogOpen || _inputPaneVisible || IsNativeGamepadEditorActive) return false;
                return _activeGamepad != null && _gamepadCount > 0;
            }
        }

        private bool IsNativeGamepadEditorActive
        {
            get
            {
                if (_liveVisible && LiveHub.IsEditing) return true;
                var focused = FocusManager.GetFocusedElement() as DependencyObject;
                while (focused != null && focused != Root)
                {
                    var combo = focused as ComboBox;
                    if (combo != null && (combo.IsDropDownOpen || combo.IsFocusEngaged)) return true;
                    // Restoring focus to the comment TextBox after native input
                    // does not mean the editor still owns every gamepad button.
                    // In particular B must remain able to close its drawer.
                    var editor = focused as Control;
                    if ((focused is TextBox || focused is PasswordBox) && editor.IsFocusEngaged) return true;
                    focused = VisualTreeHelper.GetParent(focused);
                }
                return _watching && (PartSelector.IsDropDownOpen || PartSelector.IsFocusEngaged);
            }
        }

        private string GamepadDiagnostics => "物理手柄：" + _gamepadCount + "，最近方向：" + _lastRawGamepadDirection +
            "，已处理动作：" + _rawGamepadActions + "，读取异常：" + _gamepadReadFailures +
            "\nXbox 指针模式：" + Application.Current.RequiresPointerMode + "（应为 WhenRequested）" +
            "，页面指针请求：" + RequiresPointer + "（应为 Never）";

        private static bool IsGamepadNavigationKey(VirtualKey key) => GamepadKeyRouting.IsNavigationKey(key);

        private void InitializeGamepadInput()
        {
            if (_gamepadHooksInstalled) return;
            _gamepadHooksInstalled = true;
            _gamepadTimer.Tick += GamepadInput_Tick;
            Gamepad.GamepadAdded += GamepadDevice_Added;
            Gamepad.GamepadRemoved += GamepadDevice_Removed;
            foreach (var pad in Gamepad.Gamepads) TrackGamepadDevice(pad);
            Window.Current.Activated += GamepadWindow_Activated;
            Window.Current.VisibilityChanged += GamepadWindow_VisibilityChanged;
            _gamepadWindowVisible = Window.Current.Visible;
        }

        private void StartGamepadInput()
        {
            if (_disposed) return;
            InitializeGamepadInput();
            _gamepadStarted = true;
            if (_gamepadWindowActive && _gamepadWindowVisible) _gamepadTimer.Start();
        }

        private void SuspendGamepadInput()
        {
            _gamepadTimer.Stop();
            _gamepadWindowActive = false;
            ReleaseRawGamepadInput();
        }

        public async void ResumeGamepadInput()
        {
            if (_disposed) return;
            // Application.Resuming may arrive outside the UI thread. Do not read
            // the window, change focus visuals, or start its timer from there.
            if (!Dispatcher.HasThreadAccess)
            {
                try { await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, ResumeGamepadInput); }
                catch (Exception) { /* The page/window may already be unloading. */ }
                return;
            }
            DisablePointerCursor();
            ReleaseRawGamepadInput();
            _gamepadWindowVisible = Window.Current.Visible;
            _gamepadWindowActive = Window.Current.CoreWindow.ActivationMode == CoreWindowActivationMode.ActivatedInForeground;
            _gamepadRescanNeeded = true;
            StartGamepadInput();
            // If resuming occurred in the background, Activated/VisibilityChanged
            // will start polling when the window actually returns to the user.
            if (_liveVisible && !_dialogOpen) await LoadLiveSectionAsync();
        }

        private void StopGamepadInput()
        {
            _gamepadStarted = false;
            _gamepadTimer.Stop();
            ReleaseRawGamepadInput();
            if (!_gamepadHooksInstalled) return;
            _gamepadHooksInstalled = false;
            _gamepadTimer.Tick -= GamepadInput_Tick;
            Gamepad.GamepadAdded -= GamepadDevice_Added;
            Gamepad.GamepadRemoved -= GamepadDevice_Removed;
            lock (_gamepadDevicesLock) _connectedGamepads.Clear();
            _activeGamepad = null;
            _gamepadCount = 0;
            Window.Current.Activated -= GamepadWindow_Activated;
            Window.Current.VisibilityChanged -= GamepadWindow_VisibilityChanged;
        }

        private void GamepadWindow_Activated(object sender, WindowActivatedEventArgs args)
        {
            _gamepadWindowActive = args.WindowActivationState != CoreWindowActivationState.Deactivated;
            if (!_gamepadWindowActive)
            {
                _gamepadTimer.Stop();
                ReleaseRawGamepadInput();
            }
            else if (!_disposed)
            {
                DisablePointerCursor();
                if (_gamepadStarted && _gamepadWindowVisible) _gamepadTimer.Start();
            }
        }

        private void GamepadWindow_VisibilityChanged(object sender, VisibilityChangedEventArgs args)
        {
            _gamepadWindowVisible = args.Visible;
            if (!args.Visible)
            {
                _gamepadTimer.Stop();
                ReleaseRawGamepadInput();
            }
            else if (!_disposed)
            {
                DisablePointerCursor();
                if (_gamepadStarted && _gamepadWindowActive) _gamepadTimer.Start();
            }
        }

        private void ReleaseRawGamepadInput()
        {
            var frame = _rawGamepadInput.Sample(_gamepadClock.Elapsed, null, GamepadInputButtons.None, false);
            if (frame.ConfirmReleased || _playerInput.ConfirmHeld) _playerInput.ConfirmKeyUp(DateTimeOffset.UtcNow);
            _gamepadDirectionMapper.Reset();
            _gamepadAuxiliary.Reset();
            CancelManualGamepadScroll();
            _gamepadAnalogBlockedUntilNeutral = true;
            _selectedGamepadControl = null;
            CancelGamepadListFocus();
            CancelGamepadHomeFocus();
            GamepadFocusOutline.Visibility = Visibility.Collapsed;
        }

        private void TrackGamepadDevice(Gamepad pad)
        {
            lock (_gamepadDevicesLock)
                if (_gamepadHooksInstalled && !_disposed && !_connectedGamepads.Contains(pad)) _connectedGamepads.Add(pad);
        }

        private void GamepadDevice_Added(object sender, Gamepad pad) => TrackGamepadDevice(pad);

        private void GamepadDevice_Removed(object sender, Gamepad pad)
        {
            lock (_gamepadDevicesLock) _connectedGamepads.Remove(pad);
        }

        private void RefreshGamepadDevices()
        {
            // Re-enumeration is only a recovery path. Normal readings retain the
            // object obtained from the added event/initial enumeration; projecting
            // a fresh collection every frame must not reset the input baseline.
            if (_gamepadRescanNeeded)
            {
                lock (_gamepadDevicesLock)
                {
                    _connectedGamepads.Clear();
                    foreach (var pad in Gamepad.Gamepads) _connectedGamepads.Add(pad);
                }
                _gamepadRescanNeeded = false;
            }
            lock (_gamepadDevicesLock)
            {
                _gamepadCount = _connectedGamepads.Count;
                if (_activeGamepad == null || !_connectedGamepads.Contains(_activeGamepad))
                    _activeGamepad = _connectedGamepads.Count == 0 ? null : _connectedGamepads[0];
            }
        }

        private void GamepadInput_Tick(object sender, object args)
        {
            if (_disposed || !_gamepadStarted) return;
            try
            {
                var previousGamepad = _activeGamepad;
                RefreshGamepadDevices();
                if (!ReferenceEquals(previousGamepad, _activeGamepad))
                {
                    _gamepadDirectionMapper.Reset();
                    _gamepadAnalogBlockedUntilNeutral = true;
                }

                GamepadInputButtons buttons = GamepadInputButtons.None;
                GamepadReading reading = default(GamepadReading);
                bool enabled = RawGamepadOwnsNavigation;
                if (!enabled)
                {
                    _gamepadDirectionMapper.Reset();
                    _gamepadAnalogBlockedUntilNeutral = true;
                }
                if (_activeGamepad != null)
                {
                    reading = _activeGamepad.GetCurrentReading();
                    if ((reading.Buttons & GamepadButtons.DPadLeft) != 0) buttons |= GamepadInputButtons.Left;
                    if ((reading.Buttons & GamepadButtons.DPadRight) != 0) buttons |= GamepadInputButtons.Right;
                    if ((reading.Buttons & GamepadButtons.DPadUp) != 0) buttons |= GamepadInputButtons.Up;
                    if ((reading.Buttons & GamepadButtons.DPadDown) != 0) buttons |= GamepadInputButtons.Down;
                    if (enabled && _gamepadAnalogBlockedUntilNeutral && Math.Abs(reading.LeftThumbstickX) <= 0.35 && Math.Abs(reading.LeftThumbstickY) <= 0.35)
                        _gamepadAnalogBlockedUntilNeutral = false;
                    buttons = _gamepadDirectionMapper.Map(buttons,
                        _gamepadAnalogBlockedUntilNeutral ? 0 : reading.LeftThumbstickX,
                        _gamepadAnalogBlockedUntilNeutral ? 0 : reading.LeftThumbstickY);
                    if ((reading.Buttons & GamepadButtons.A) != 0) buttons |= GamepadInputButtons.Confirm;
                }

                var frame = _rawGamepadInput.Sample(_gamepadClock.Elapsed, _activeGamepad, buttons, enabled);
                if (frame.ConfirmReleased) _playerInput.ConfirmKeyUp(DateTimeOffset.UtcNow);
                if (frame.Direction.HasValue)
                {
                    ++_rawGamepadActions;
                    _lastRawGamepadDirection = frame.Direction.Value.ToString();
                    DispatchPlayerGamepadKey(DirectionVirtualKey(frame.Direction.Value), frame.IsRepeat);
                }
                if (frame.ConfirmPressed)
                {
                    ++_rawGamepadActions;
                    DispatchPlayerGamepadKey(VirtualKey.GamepadA, false);
                }
                PollAuxiliaryGamepad(reading, enabled);
                if (enabled && !_watching && !_creatorVisible && !_libraryVisible && !_liveVisible) EnsureGamepadHomeFocus();
                if (enabled) DisablePointerCursor();
                UpdateGamepadFocusOutline();
            }
            catch
            {
                // Device removal can race GetCurrentReading. Keep the UI alive and
                // re-enumerate on the next frame, with a fresh neutral-input barrier.
                ++_gamepadReadFailures;
                _activeGamepad = null;
                _gamepadRescanNeeded = true;
                ReleaseRawGamepadInput();
            }
        }

        // Remote/virtual controllers may provide CoreWindow navigation without a
        // Windows.Gaming.Input device. This is their one dispatch entry; Preview
        // consumes those keys, while physical keyboard input uses its usual path.
        private bool HandleUnroutedGamepadKey(VirtualKey key, bool repeat)
        {
            if (!IsGamepadNavigationKey(key) || _disposed || _dialogOpen || _inputPaneVisible ||
                !_gamepadWindowActive || !_gamepadWindowVisible || IsNativeGamepadEditorActive) return false;
            return DispatchPlayerGamepadKey(key, repeat);
        }

        private bool DispatchPlayerGamepadKey(VirtualKey key, bool repeat)
        {
            ReconcileHomeScrollBeforeNavigation();
            if (_liveVisible) return DispatchLiveSectionKey(key, repeat);
            if (_libraryVisible)
            {
                PlayerDirection libraryDirection;
                if (PlayerDirectionKey(key, out libraryDirection)) { AccountLibrary.Navigate(libraryDirection); return true; }
                if (!PlayerConfirmKey(key)) return false;
                AccountLibrary.Confirm(repeat);
                return true;
            }
            if (_creatorVisible)
            {
                PlayerDirection creatorDirection;
                if (PlayerDirectionKey(key, out creatorDirection)) { CreatorHub.Navigate(creatorDirection); return true; }
                if (!PlayerConfirmKey(key)) return false;
                CreatorHub.Confirm(repeat);
                return true;
            }
            if (!_watching)
            {
                PlayerDirection homeDirection;
                if (PlayerDirectionKey(key, out homeDirection)) { NavigateGamepadHome(homeDirection); return true; }
                if (!PlayerConfirmKey(key)) return false;
                ConfirmGamepadHome(repeat);
                return true;
            }
            if (_playerUi.Drawer == PlayerDrawer.Interactions)
            {
                PlayerDirection interactionDirection;
                if (PlayerDirectionKey(key, out interactionDirection))
                {
                    InteractionSide.Navigate(interactionDirection);
                    return true;
                }
                if (!PlayerConfirmKey(key)) return false;
                InteractionSide.Confirm(repeat);
                return true;
            }
            if (_playerUi.Drawer == PlayerDrawer.None) return HandlePlayerKey(key, repeat);
            PlayerDirection direction;
            if (PlayerDirectionKey(key, out direction))
            {
                NavigateGamepadDrawer(direction);
                return true;
            }
            if (!PlayerConfirmKey(key)) return false;
            ConfirmGamepadDrawer(repeat);
            return true;
        }

        private static VirtualKey DirectionVirtualKey(PlayerDirection direction)
        {
            switch (direction)
            {
                case PlayerDirection.Left: return VirtualKey.Left;
                case PlayerDirection.Right: return VirtualKey.Right;
                case PlayerDirection.Up: return VirtualKey.Up;
                default: return VirtualKey.Down;
            }
        }

        private Control[] HomeHeaderControls => new Control[] { LiveSectionButton, HomeButton, PopularSectionButton,
            AnimationSectionButton, FilmSectionButton, HeaderSearchButton, AccountButton, HelpButton };
        private Control[] HomeSearchControls => new Control[] { SearchEntryButton, SearchSubmitButton };
        private Control[] HomeModeControls => new Control[] { SearchVideosButton, SearchCreatorButton };
        private Control[] HomePagerControls => new Control[] { PreviousPage, NextPage, RefreshFeedButton };

        private void EnsureGamepadHomeFocus()
        {
            RestoreHomeGamepadFocus();
        }

        private void RestoreHomeGamepadFocus()
        {
            if (_disposed || _watching || _creatorVisible || _libraryVisible || _liveVisible || _dialogOpen || _inputPaneVisible || _gamepadHomeFocusPending || _gamepadHomeFeedReplacing || _homeScrollPending) return;
            var current = FocusManager.GetFocusedElement() as Control;
            if (!_gamepadHomeInitialFocusDone)
            {
                if (HomeResultGrid.Items.Count > 0)
                {
                    _gamepadHomeInitialFocusDone = true;
                    FocusGamepadHomeVideo(0);
                }
                else if (!ReferenceEquals(current, SearchEntryButton)) FocusGamepadHomeControl(SearchEntryButton);
                return;
            }
            if (ReferenceEquals(current, HomeResultGrid) && HomeResultGrid.Items.Count > 0)
            {
                FocusGamepadHomeVideo(Math.Min(_gamepadHomeIndex, HomeResultGrid.Items.Count - 1));
                return;
            }
            if (IsNavigableGamepadHomeControl(current) &&
                (FindGamepadAncestor<GridViewItem>(current, HomeResultGrid) == null || GamepadHomeVideoIndex(current) >= 0))
            {
                if (current.FocusState != FocusState.Keyboard) current.Focus(FocusState.Keyboard);
                _selectedGamepadControl = current;
                return;
            }
            if (HomeResultGrid.Items.Count > 0) FocusGamepadHomeVideo(Math.Min(_gamepadHomeIndex, HomeResultGrid.Items.Count - 1));
            else FocusGamepadHomeControl(SearchEntryButton);
        }

        private void NavigateGamepadHome(PlayerDirection direction)
        {
            _gamepadHomeInitialFocusDone = true;
            DisablePointerCursor();
            var current = FocusManager.GetFocusedElement() as Control;
            int index = GamepadHomeVideoIndex(current);
            if (index >= 0)
            {
                int columns = GamepadHomeColumnCount();
                int count = HomeResultGrid.Items.Count;
                int row = index / columns;
                if (direction == PlayerDirection.Left)
                    FocusGamepadHomeVideo(Math.Max(row * columns, index - 1));
                else if (direction == PlayerDirection.Right)
                    FocusGamepadHomeVideo(Math.Min(Math.Min(count - 1, (row + 1) * columns - 1), index + 1));
                else if (direction == PlayerDirection.Up)
                {
                    if (index >= columns) FocusGamepadHomeVideo(index - columns);
                    else FocusGamepadHomeGroup(SearchModes.Visibility == Visibility.Visible ? HomeModeControls : HomeSearchControls, current);
                }
                else if ((row + 1) * columns < count) FocusGamepadHomeVideo(Math.Min(count - 1, index + columns));
                else FocusGamepadHomeGroup(HomePagerControls, current);
                return;
            }

            if (!IsNavigableGamepadHomeControl(current))
            {
                FocusGamepadHomeControl(HomeButton);
                return;
            }
            Control[] group = HomeGroupContaining(current);
            if (group == null)
            {
                FocusGamepadHomeControl(HomeButton);
                return;
            }
            if (direction == PlayerDirection.Left || direction == PlayerDirection.Right)
            {
                var available = AvailableGamepadHomeControls(group);
                int selected = available.IndexOf(current);
                if (available.Count > 0) FocusGamepadHomeControl(available[Math.Max(0, Math.Min(available.Count - 1,
                    selected + (direction == PlayerDirection.Left ? -1 : 1)))]);
                return;
            }
            bool up = direction == PlayerDirection.Up;
            if (ContainsGamepadControl(HomeHeaderControls, current))
            {
                if (!up) FocusGamepadHomeGroup(HomeSearchControls, current);
            }
            else if (ContainsGamepadControl(HomeSearchControls, current))
            {
                if (up) FocusGamepadHomeGroup(HomeHeaderControls, current);
                else if (SearchModes.Visibility == Visibility.Visible) FocusGamepadHomeGroup(HomeModeControls, current);
                else FocusGamepadHomeVideoOrPager(current, false);
            }
            else if (ContainsGamepadControl(HomeModeControls, current))
            {
                if (up) FocusGamepadHomeGroup(HomeSearchControls, current);
                else FocusGamepadHomeVideoOrPager(current, false);
            }
            else if (ContainsGamepadControl(HomePagerControls, current) && up)
            {
                if (HomeResultGrid.Items.Count > 0) FocusGamepadHomeVideoOrPager(current, true);
                else FocusGamepadHomeGroup(SearchModes.Visibility == Visibility.Visible ? HomeModeControls : HomeSearchControls, current);
            }
        }

        private int GamepadHomeColumnCount()
        {
            var wrap = HomeResultGrid.ItemsPanelRoot as ItemsWrapGrid;
            double width = wrap?.ItemWidth ?? 280;
            if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0) width = 280;
            double available = Math.Max(width, HomeResultGrid.ActualWidth - HomeResultGrid.Padding.Left - HomeResultGrid.Padding.Right);
            int columns = Math.Max(1, (int)Math.Floor(available / width));
            if (wrap != null && wrap.MaximumRowsOrColumns > 0) columns = Math.Min(columns, wrap.MaximumRowsOrColumns);
            return columns;
        }

        private int GamepadHomeVideoIndex(Control current)
        {
            if (_gamepadHomeFocusPending && _gamepadHomeIndex >= 0 && _gamepadHomeIndex < HomeResultGrid.Items.Count &&
                ReferenceEquals(_gamepadHomeItem, HomeResultGrid.Items[_gamepadHomeIndex])) return _gamepadHomeIndex;
            var container = FindGamepadAncestor<GridViewItem>(current, HomeResultGrid);
            if (container == null) return -1;
            int index = HomeResultGrid.IndexFromContainer(container);
            return index >= 0 && index < HomeResultGrid.Items.Count ? index : -1;
        }

        private void FocusGamepadHomeVideoOrPager(Control current, bool lastRow)
        {
            int count = HomeResultGrid.Items.Count;
            if (count == 0) { FocusGamepadHomeGroup(HomePagerControls, current); return; }
            int columns = GamepadHomeColumnCount();
            var bounds = current.TransformToVisual(HomeResultGrid).TransformBounds(new Rect(0, 0, current.ActualWidth, current.ActualHeight));
            double width = Math.Max(1, HomeResultGrid.ActualWidth - HomeResultGrid.Padding.Left - HomeResultGrid.Padding.Right) / columns;
            int column = Math.Max(0, Math.Min(columns - 1, (int)((bounds.X + bounds.Width / 2) / width)));
            int rowStart = lastRow ? (count - 1) / columns * columns : 0;
            FocusGamepadHomeVideo(Math.Min(count - 1, rowStart + column));
        }

        private void CancelGamepadHomeFocus()
        {
            ++_gamepadHomeFocusGeneration;
            _gamepadHomeFocusPending = false;
            _gamepadHomeItem = null;
        }

        // A replacement page starts at its first result. Keeping the pager's
        // native focus would make Up enter the last row even though the newly
        // populated GridView is displaying its first row.
        private void BeginHomeFeedReplacement()
        {
            CancelManualGamepadScroll();
            CancelGamepadHomeFocus();
            _gamepadHomeFeedReplacing = true;
            _gamepadHomeIndex = 0;
            _gamepadHomeInitialFocusDone = false;
            if (_disposed || _watching || _creatorVisible || _libraryVisible || _dialogOpen || _inputPaneVisible) return;
            // Move focus out before Clear recycles the old focused container.
            // Search remains usable while a slow feed request is pending.
            FocusGamepadHomeControl(SearchEntryButton);
        }

        private void CompleteHomeFeedReplacement()
        {
            _gamepadHomeFeedReplacing = false;
            _gamepadHomeIndex = 0;
            _gamepadHomeInitialFocusDone = false;
            RestoreHomeGamepadFocus();
        }

        private async void FocusGamepadHomeVideo(int index)
        {
            var grid = HomeResultGrid;
            if (_watching || _creatorVisible || _libraryVisible || _gamepadHomeFeedReplacing || index < 0 || index >= grid.Items.Count) return;
            int generation = ++_gamepadHomeFocusGeneration;
            _gamepadHomeFocusPending = true;
            _gamepadHomeIndex = index;
            object item = _gamepadHomeItem = grid.Items[index];
            try
            {
                // GridView's geometry excludes unrealized rows. Work with logical
                // indices, then realize the requested video before moving focus.
                grid.ScrollIntoView(item, ScrollIntoViewAlignment.Default);
                bool focused = false;
                for (int attempt = 0; attempt < 3 && !focused; ++attempt)
                    await Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                    {
                        if (generation != _gamepadHomeFocusGeneration || _disposed || _watching || _creatorVisible || _libraryVisible || _dialogOpen ||
                            _inputPaneVisible || _gamepadHomeFeedReplacing || !ReferenceEquals(grid, HomeResultGrid) ||
                            index >= grid.Items.Count || !ReferenceEquals(item, grid.Items[index])) return;
                        grid.UpdateLayout();
                        var container = grid.ContainerFromIndex(index) as GridViewItem;
                        if (container == null) return;
                        FocusGamepadControl(container);
                        focused = ReferenceEquals(FocusManager.GetFocusedElement(), container);
                    });
            }
            catch (Exception) { /* Feed replacement can remove a deferred container. */ }
            finally { if (generation == _gamepadHomeFocusGeneration) _gamepadHomeFocusPending = false; }
        }

        private void FocusGamepadHomeControl(Control control)
        {
            if (!IsNavigableGamepadHomeControl(control)) return;
            CancelGamepadHomeFocus();
            FocusGamepadControl(control);
        }

        private void FocusGamepadHomeGroup(Control[] group, Control reference)
        {
            var available = AvailableGamepadHomeControls(group);
            if (available.Count == 0) return;
            Control selected = available[0];
            if (reference != null && reference.ActualWidth > 0)
            {
                var origin = reference.TransformToVisual(Root).TransformBounds(new Rect(0, 0, reference.ActualWidth, reference.ActualHeight));
                double x = origin.X + origin.Width / 2, distance = double.MaxValue;
                foreach (var candidate in available)
                {
                    var bounds = candidate.TransformToVisual(Root).TransformBounds(new Rect(0, 0, candidate.ActualWidth, candidate.ActualHeight));
                    double nextDistance = Math.Abs(bounds.X + bounds.Width / 2 - x);
                    if (nextDistance < distance) { distance = nextDistance; selected = candidate; }
                }
            }
            FocusGamepadHomeControl(selected);
        }

        private static bool ContainsGamepadControl(Control[] controls, Control control)
        {
            foreach (var candidate in controls) if (ReferenceEquals(candidate, control)) return true;
            return false;
        }

        private Control[] HomeGroupContaining(Control control)
        {
            foreach (var group in new[] { HomeHeaderControls, HomeSearchControls, HomeModeControls, HomePagerControls })
                if (ContainsGamepadControl(group, control)) return group;
            return null;
        }

        private List<Control> AvailableGamepadHomeControls(Control[] controls)
        {
            var result = new List<Control>();
            foreach (var control in controls) if (IsNavigableGamepadHomeControl(control)) result.Add(control);
            return result;
        }

        private bool IsNavigableGamepadHomeControl(Control control)
        {
            if (control == null || !control.IsEnabled || !control.IsTabStop || IsGamepadDescendant(control, WatchView)) return false;
            DependencyObject element = control;
            while (element != null)
            {
                var visual = element as UIElement;
                if (visual != null && visual.Visibility != Visibility.Visible) return false;
                if (ReferenceEquals(element, Root)) return true;
                element = VisualTreeHelper.GetParent(element);
            }
            return false;
        }

        private void ConfirmGamepadHome(bool repeat)
        {
            if (!_playerInput.ConfirmKeyDown(DateTimeOffset.UtcNow, repeat)) return;
            _gamepadHomeInitialFocusDone = true;
            var control = FocusManager.GetFocusedElement() as Control;
            int index = GamepadHomeVideoIndex(control);
            if (index < 0 && HomeResultGrid.Items.Count > 0 &&
                (ReferenceEquals(control, HomeResultGrid) || !IsNavigableGamepadHomeControl(control)))
                index = Math.Max(0, Math.Min(_gamepadHomeIndex, HomeResultGrid.Items.Count - 1));
            _dispatchingPlayerAction = true;
            try
            {
                if (index >= 0 && HomeResultGrid.Items[index] is VideoItem item)
                {
                    _gamepadHomeIndex = index;
                    CancelGamepadHomeFocus();
                    OpenGamepadHomeVideo(item);
                    return;
                }
                if (index >= 0 && HomeResultGrid.Items[index] is UserSearchItem user)
                {
                    _gamepadHomeIndex = index;
                    CancelGamepadHomeFocus();
                    OpenGamepadHomeUser(user);
                    return;
                }
                if (!IsNavigableGamepadHomeControl(control)) control = SearchEntryButton;
                var args = new RoutedEventArgs();
                if (control == HomeButton) Home_Click(control, args);
                else if (Array.IndexOf(SectionButtons, control) >= 0) Section_Click(control, args);
                else if (control == HeaderSearchButton || control == SearchEntryButton) SearchFocus_Click(control, args);
                else if (control == SearchSubmitButton) Search_Click(control, args);
                else if (control == AccountButton) Account_Click(control, args);
                else if (control == HelpButton) Help_Click(control, args);
                else if (control == SearchVideosButton) SearchVideos_Click(control, args);
                else if (control == SearchCreatorButton) SearchCreator_Click(control, args);
                else if (control == PreviousPage) PreviousPage_Click(control, args);
                else if (control == NextPage) NextPage_Click(control, args);
                else if (control == RefreshFeedButton) Refresh_Click(control, args);
            }
            finally { _dispatchingPlayerAction = false; }
        }

        private async void OpenGamepadHomeVideo(VideoItem item)
        {
            try { await OpenVideoAsync(item.Bvid, item.Title); }
            catch (Exception ex) { if (!_disposed) SetStatus("打开视频失败：" + Friendly(ex)); }
        }

        private FrameworkElement GamepadDrawerRoot => _playerUi.Drawer == PlayerDrawer.Interactions ? (FrameworkElement)InteractionSide : _playerUi.Drawer == PlayerDrawer.Settings
            ? (FrameworkElement)SettingsSide : _playerUi.Drawer == PlayerDrawer.DanmakuSettings
            ? DanmakuSettingsSide : _viewingReplies ? ReplySide : CommentSide;

        private void NavigateGamepadDrawer(PlayerDirection direction)
        {
            var root = GamepadDrawerRoot;
            var current = FocusManager.GetFocusedElement() as Control;
            bool entering = _playerInput.Mode != PlayerInputMode.Controls || !IsGamepadDescendant(current, root);
            _playerInput.SelectButton(_playerInput.ButtonIndex, PlayerButtons.Length);
            _playerUi.Touch(DateTimeOffset.UtcNow);
            DisablePointerCursor();
            UpdatePlayerInputHint();
            if (entering)
            {
                CancelGamepadListFocus();
                var initial = _playerUi.Drawer == PlayerDrawer.Settings ? SettingsCloseButton :
                    _playerUi.Drawer == PlayerDrawer.DanmakuSettings ? DanmakuSettingsCloseButton :
                    _viewingReplies ? RepliesBackButton : CommentsCloseButton;
                FocusGamepadControl(initial);
                return;
            }

            if (_playerUi.Drawer == PlayerDrawer.DanmakuSettings)
            {
                // Explicit order reaches options outside the ScrollViewer's
                // visible rectangle; focusing the next button scrolls it in.
                var options = new Control[] { DanmakuSettingsCloseButton, DanmakuMaskButton, DanmakuAreaButton,
                    DanmakuOpacityButton, DanmakuDensityButton, DanmakuFontButton, DanmakuSpeedButton,
                    DanmakuOverlapButton, DanmakuMergeButton, DanmakuScrollButton, DanmakuTopButton,
                    DanmakuBottomButton, DanmakuColorButton, DanmakuCloudSyncButton,
                    DanmakuCloudLoginButton, DanmakuResetButton };
                var available = new List<Control>();
                foreach (var option in options) if (option.IsEnabled && option.Visibility == Visibility.Visible) available.Add(option);
                int index = available.IndexOf(current);
                int step = direction == PlayerDirection.Up ? -1 : direction == PlayerDirection.Down ? 1 : 0;
                if (available.Count > 0) FocusGamepadControl(available[Math.Max(0, Math.Min(available.Count - 1, index < 0 ? 0 : index + step))]);
                return;
            }

            if (_playerUi.Drawer == PlayerDrawer.Settings)
            {
                // Explicit order includes options below the viewport. Focusing
                // the next row scrolls its own focus visual into view.
                var options = new List<Control> { SettingsCloseButton, QualityAccountButton };
                foreach (var item in QualityOptions.Children) if (item is Button button) options.Add(button);
                options.Add(PartSelector);
                var available = new List<Control>();
                foreach (var option in options) if (option.IsEnabled && option.Visibility == Visibility.Visible) available.Add(option);
                int index = available.IndexOf(current);
                int step = direction == PlayerDirection.Up ? -1 : direction == PlayerDirection.Down ? 1 : 0;
                if (available.Count > 0) FocusGamepadControl(available[Math.Max(0, Math.Min(available.Count - 1, index < 0 ? 0 : index + step))]);
                return;
            }

            if (NavigateCommentDrawerControls(current, direction)) return;
            if (NavigateGamepadCommentList(current, direction)) return;

            FocusNavigationDirection focusDirection;
            switch (direction)
            {
                case PlayerDirection.Left: focusDirection = FocusNavigationDirection.Left; break;
                case PlayerDirection.Right: focusDirection = FocusNavigationDirection.Right; break;
                case PlayerDirection.Up: focusDirection = FocusNavigationDirection.Up; break;
                default: focusDirection = FocusNavigationDirection.Down; break;
            }
            var target = FocusManager.FindNextElement(focusDirection, new FindNextElementOptions { SearchRoot = root }) as Control;
            if (target != null && target.IsEnabled && IsGamepadDescendant(target, root)) FocusGamepadControl(target);
            else if (current != null) FocusGamepadControl(current);
        }

        private void CancelGamepadListFocus()
        {
            ++_gamepadListFocusGeneration;
            _gamepadListFocusPending = false;
            _gamepadNavigationList = null;
            _gamepadNavigationIndex = -1;
        }

        private bool NavigateGamepadCommentList(Control current, PlayerDirection direction)
        {
            if (_playerUi.Drawer != PlayerDrawer.Comments ||
                (direction != PlayerDirection.Up && direction != PlayerDirection.Down)) return false;
            var list = _viewingReplies ? ReplyList : CommentList;
            if (list.Items.Count == 0) return false;
            var row = FindGamepadAncestor<ListViewItem>(current, list);
            int index = row == null ? -1 : list.IndexFromContainer(row);
            if (_gamepadListFocusPending && ReferenceEquals(_gamepadNavigationList, list)) index = _gamepadNavigationIndex;
            if (index >= 0)
            {
                if (ReadExpandedComment(row, direction)) return true;
                int next = index + (direction == PlayerDirection.Down ? 1 : -1);
                if (next >= 0 && next < list.Items.Count) FocusGamepadListItem(list, next);
                else
                {
                    CancelGamepadListFocus();
                    if (next < 0) FocusGamepadControl(_viewingReplies ? (Control)ReplyRootCard : CommentsCloseButton);
                    else if (_viewingReplies)
                    {
                        if (MoreRepliesButton.IsEnabled) FocusGamepadControl(MoreRepliesButton);
                        else FocusGamepadControl(current);
                    }
                    else FocusGamepadControl(MoreCommentsButton.IsEnabled ? (Control)MoreCommentsButton : CommentInput);
                }
                return true;
            }
            if (ReferenceEquals(current, list))
            {
                FocusGamepadListItem(list, direction == PlayerDirection.Down ? 0 : list.Items.Count - 1);
                return true;
            }
            if (current == null) return false;
            var currentBounds = current.TransformToVisual(list).TransformBounds(new Rect(0, 0, current.ActualWidth, current.ActualHeight));
            if (direction == PlayerDirection.Down && currentBounds.Bottom <= 1)
            {
                if (HasIntermediateGamepadControl(current, list, direction)) return false;
                FocusGamepadListItem(list, 0);
                return true;
            }
            if (direction == PlayerDirection.Up && currentBounds.Top >= list.ActualHeight - 1)
            {
                if (HasIntermediateGamepadControl(current, list, direction)) return false;
                FocusGamepadListItem(list, list.Items.Count - 1);
                return true;
            }
            return false;
        }

        private bool HasIntermediateGamepadControl(Control current, ListView list, PlayerDirection direction)
        {
            var candidate = FocusManager.FindNextElement(direction == PlayerDirection.Down ? FocusNavigationDirection.Down : FocusNavigationDirection.Up,
                new FindNextElementOptions { SearchRoot = GamepadDrawerRoot }) as Control;
            if (candidate == null || ReferenceEquals(candidate, current) || IsGamepadDescendant(candidate, list)) return false;
            var bounds = candidate.TransformToVisual(list).TransformBounds(new Rect(0, 0, candidate.ActualWidth, candidate.ActualHeight));
            // Keep login, load-more, write, and send controls reachable. Only bridge
            // into the virtualized list once no intervening control remains.
            return direction == PlayerDirection.Down ? bounds.Bottom <= 1 : bounds.Top >= list.ActualHeight - 1;
        }

        private async void FocusGamepadListItem(ListView list, int index, bool preserveViewport = false)
        {
            if (index < 0 || index >= list.Items.Count) return;
            int generation = ++_gamepadListFocusGeneration;
            _gamepadNavigationList = list;
            _gamepadNavigationIndex = index;
            _gamepadListFocusPending = true;
            object item = list.Items[index];
            try
            {
                // XY geometry only sees realized containers. Scroll and realize the
                // logical next item so D-pad can traverse the whole comment list.
                if (!preserveViewport || list.ContainerFromIndex(index) == null)
                    list.ScrollIntoView(item, ScrollIntoViewAlignment.Leading);
                bool focused = false;
                for (int attempt = 0; attempt < 2 && !focused; ++attempt)
                    await Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                    {
                        if (generation != _gamepadListFocusGeneration || _disposed || !_watching ||
                            _dialogOpen || _inputPaneVisible || _playerInput.Mode != PlayerInputMode.Controls ||
                            _playerUi.Drawer != PlayerDrawer.Comments ||
                            !ReferenceEquals(list, _viewingReplies ? ReplyList : CommentList) ||
                            index >= list.Items.Count || !ReferenceEquals(item, list.Items[index])) return;
                        list.UpdateLayout();
                        var container = list.ContainerFromIndex(index) as ListViewItem;
                        if (container == null) return;
                        FocusGamepadControl(container, !preserveViewport);
                        focused = ReferenceEquals(FocusManager.GetFocusedElement(), container);
                    });
            }
            catch (Exception)
            {
                // A drawer/feed transition may remove the visual while its
                // deferred container realization is in flight. Its guard above
                // prevents moving focus into another screen.
            }
            finally
            {
                if (generation == _gamepadListFocusGeneration) _gamepadListFocusPending = false;
            }
        }

        private void FocusGamepadControl(Control control, bool bringIntoView = true)
        {
            if (control == null || !control.IsEnabled || control.Visibility != Visibility.Visible) return;
            if (!control.Focus(FocusState.Keyboard)) return;
            _selectedGamepadControl = control;
            if (bringIntoView) control.StartBringIntoView();
            UpdateGamepadFocusOutline();
        }

        private void ConfirmGamepadDrawer(bool repeat)
        {
            if (!_playerInput.ConfirmKeyDown(DateTimeOffset.UtcNow, repeat)) return;
            _playerUi.Touch(DateTimeOffset.UtcNow);
            _dispatchingPlayerAction = true;
            try
            {
                var control = FocusManager.GetFocusedElement() as Control;
                if (!IsGamepadDescendant(control, GamepadDrawerRoot)) control = _selectedGamepadControl;
                if (IsGamepadDescendant(control, GamepadDrawerRoot)) ActivateGamepadDrawerControl(control);
            }
            finally { _dispatchingPlayerAction = false; }
        }

        private void ActivateGamepadDrawerControl(Control control)
        {
            if (control == null || !control.IsEnabled) return;
            var args = new RoutedEventArgs();
            if (control == SettingsCloseButton || control == CommentsCloseButton || control == RepliesCloseButton || control == DanmakuSettingsCloseButton) DrawerClose_Click(control, args);
            else if (control == DanmakuCloudSyncButton) DanmakuCloudSync_Click(control, args);
            else if (control == DanmakuCloudLoginButton) Account_Click(control, args);
            else if (control == DanmakuResetButton) DanmakuReset_Click(control, args);
            else if (control is Button && IsGamepadDescendant(control, DanmakuSettingsSide)) DanmakuOption_Click(control, args);
            else if (control == RepliesBackButton) RepliesBack_Click(control, args);
            else if (control == QualityAccountButton || control == CommentLoginButton) Account_Click(control, args);
            else if (control == RefreshCommentsButton) RefreshComments_Click(control, args);
            else if (control == MoreCommentsButton) MoreComments_Click(control, args);
            else if (control == WriteReplyCommentButton || control == CommentInput || control == ReplyCommentInput) CommentEntry_Click(control, args);
            else if (control == SendReplyCommentButton) SendReplyComment_Click(control, args);
            else if (control == SendCommentButton) SendComment_Click(control, args);
            else if (control == MoreRepliesButton) MoreReplies_Click(control, args);
            else if (control == PartSelector)
            {
                PartSelector.Focus(FocusState.Keyboard);
                PartSelector.IsDropDownOpen = true;
            }
            else
            {
                var button = control as Button;
                if (button != null && QualityOptions.Children.Contains(button)) { Quality_Click(button, args); return; }
                if (TryActivateCommentControl(control)) return;
                // The reply header's close action has no x:Name in older layouts.
                if (button != null && IsGamepadDescendant(button, ReplySide) && button.FontFamily.Source == "Segoe MDL2 Assets")
                    DrawerClose_Click(button, args);
            }
        }

        private static bool IsGamepadDescendant(DependencyObject child, DependencyObject root)
        {
            while (child != null)
            {
                if (ReferenceEquals(child, root)) return true;
                child = VisualTreeHelper.GetParent(child);
            }
            return false;
        }

        private static T FindGamepadAncestor<T>(DependencyObject child, DependencyObject root) where T : DependencyObject
        {
            while (child != null && child != root)
            {
                var match = child as T;
                if (match != null) return match;
                child = VisualTreeHelper.GetParent(child);
            }
            return null;
        }

        private static T FindGamepadChild<T>(DependencyObject root) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                var match = child as T ?? FindGamepadChild<T>(child);
                if (match != null) return match;
            }
            return null;
        }

        private void UpdateGamepadFocusOutline()
        {
            if (_creatorVisible || _libraryVisible || _liveVisible || _disposed || !_gamepadWindowActive || !_gamepadWindowVisible ||
                _dialogOpen || _inputPaneVisible || (_watching && _playerInput.Mode != PlayerInputMode.Controls))
            { GamepadFocusOutline.Visibility = Visibility.Collapsed; return; }

            // Settings buttons paint their focus inside their own templates.
            // A second rectangle on the root canvas ignores their template
            // transform and scrolling clip, creating doubled or offset edges.
            // Keep the root outline only for the player and comment rows that
            // actually depend on it; native ComboBox focus owns the part picker.
            if (_watching && (_playerUi.Drawer == PlayerDrawer.Settings || _playerUi.Drawer == PlayerDrawer.DanmakuSettings
                || _playerUi.Drawer == PlayerDrawer.Interactions))
            { GamepadFocusOutline.Visibility = Visibility.Collapsed; return; }

            Control selected;
            if (!_watching)
            {
                selected = FocusManager.GetFocusedElement() as Control;
                // GridView's native focus visual follows composition scrolling.
                // A second outline on the root canvas lags behind that animation
                // and can appear on a different row from the focused card.
                if (FindGamepadAncestor<GridViewItem>(selected, HomeResultGrid) != null)
                { GamepadFocusOutline.Visibility = Visibility.Collapsed; return; }
                if (!IsNavigableGamepadHomeControl(selected)) selected = null;
            }
            else if (_playerUi.Drawer == PlayerDrawer.None)
            {
                if (!_playerUi.ControlsVisible) { GamepadFocusOutline.Visibility = Visibility.Collapsed; return; }
                selected = _playerInput.Row == PlayerFocusRow.Seek ? (Control)SeekSlider :
                    _playerInput.Row == PlayerFocusRow.Header ? PlayerHeaderButtons[_playerInput.HeaderIndex] : PlayerButtons[_playerInput.ButtonIndex];
            }
            else
            {
                selected = FocusManager.GetFocusedElement() as Control;
                if (!IsGamepadDescendant(selected, GamepadDrawerRoot)) selected = _selectedGamepadControl;
                if (!IsGamepadDescendant(selected, GamepadDrawerRoot)) selected = null;
            }
            if (selected == null || !selected.IsEnabled || selected.Visibility != Visibility.Visible ||
                selected.ActualWidth <= 0 || selected.ActualHeight <= 0)
            { GamepadFocusOutline.Visibility = Visibility.Collapsed; return; }

            try
            {
                var bounds = selected.TransformToVisual(GamepadFocusLayer).TransformBounds(new Rect(0, 0, selected.ActualWidth, selected.ActualHeight));
                if (bounds.Bottom < 0 || bounds.Top > Root.ActualHeight || bounds.Right < 0 || bounds.Left > Root.ActualWidth)
                { GamepadFocusOutline.Visibility = Visibility.Collapsed; return; }
                Canvas.SetLeft(GamepadFocusOutline, bounds.X - 3);
                Canvas.SetTop(GamepadFocusOutline, bounds.Y - 3);
                GamepadFocusOutline.Width = bounds.Width + 6;
                GamepadFocusOutline.Height = bounds.Height + 6;
                GamepadFocusOutline.CornerRadius = new CornerRadius(selected == SeekSlider ? 8 : 20);
                // A list row can be partly outside its scrolling viewport. The
                // root-canvas outline must obey that viewport too, otherwise a
                // long comment paints its focus border over the fixed composer.
                FrameworkElement viewport = IsGamepadDescendant(selected, CommentList) ? CommentListViewport :
                    IsGamepadDescendant(selected, ReplyList) ? ReplyListViewport : null;
                if (viewport != null)
                {
                    var clipBounds = viewport.TransformToVisual(GamepadFocusLayer).TransformBounds(
                        new Rect(0, 0, viewport.ActualWidth, viewport.ActualHeight));
                    GamepadFocusOutline.Clip = new RectangleGeometry { Rect = FocusViewportClip.GetRelativeClip(
                        new Rect(bounds.X - 3, bounds.Y - 3, bounds.Width + 6, bounds.Height + 6), clipBounds) };
                }
                else GamepadFocusOutline.Clip = null;
                GamepadFocusOutline.Visibility = Visibility.Visible;
                _selectedGamepadControl = selected;
            }
            catch { GamepadFocusOutline.Visibility = Visibility.Collapsed; }
        }
    }
}
