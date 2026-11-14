using System;
using BiliXbox.Controls;
using Windows.Gaming.Input;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace BiliXbox
{
    public sealed partial class MainPage
    {
        private readonly GamepadAuxiliaryState _gamepadAuxiliary = new GamepadAuxiliaryState();
        private bool _homeScrollPending, _homeManuallyScrolled;
        private int _manualScrollRevision;
        private ListViewBase _manuallyScrolledList;

        private bool IsSectionBrowseSurface => !_watching && !_creatorVisible && !_libraryVisible
            && (!_liveVisible || !LiveHub.IsPlayingRoom);

        private void PollAuxiliaryGamepad(GamepadReading reading, bool enabled)
        {
            var extra = _gamepadAuxiliary.Sample(_gamepadClock.Elapsed, _activeGamepad,
                (reading.Buttons & GamepadButtons.LeftShoulder) != 0,
                (reading.Buttons & GamepadButtons.RightShoulder) != 0, reading.RightThumbstickY, enabled);
            if (extra.SectionStep != 0)
            {
                if (IsSectionBrowseSurface) StepHomeSection(extra.SectionStep);
                else SeekVideoShoulder(extra.SectionStep);
            }
            if (Math.Abs(extra.ScrollPixels) >= .05) ScrollGamepadSurface(extra.ScrollPixels);
        }

        private async void StepHomeSection(int step)
        {
            if (_disposed || _dialogOpen || _inputPaneVisible || IsNativeGamepadEditorActive || !IsSectionBrowseSurface) return;
            int current = (int)(_pendingSection ?? _feedNavigation.Section);
            int next = Math.Max(0, Math.Min(SectionButtons.Length - 1, current + Math.Sign(step)));
            if (next == current && !_feedNavigation.IsSearch) return;
            try { await SelectSectionAsync((HomeSection)next); }
            catch (Exception ex) { if (!_disposed) SetStatus("分区加载失败：" + Friendly(ex)); }
        }

        private static int SectionStepKey(VirtualKey key)
            => key == VirtualKey.GamepadLeftShoulder ? -1 : key == VirtualKey.GamepadRightShoulder ? 1 : 0;
        private static int ScrollKey(VirtualKey key)
            => key == VirtualKey.GamepadRightThumbstickUp ? -1 : key == VirtualKey.GamepadRightThumbstickDown ? 1 : 0;

        private bool IsShoulderSeekSurface => _watching && !_creatorVisible && !_libraryVisible && !_liveVisible
            && !_disposed && !_dialogOpen && !_inputPaneVisible && !IsNativeGamepadEditorActive
            && _playerUi.Drawer == PlayerDrawer.None;

        private void SeekVideoShoulder(int step)
        {
            if (!IsShoulderSeekSurface || step == 0) return;
            ShowControls();
            // Seeking only changes Position. Never call Play or change the
            // user's pause intent, whether the controls are shown or hidden.
            Skip(Math.Sign(step) * 5);
        }
        private static bool IsAuxiliaryGamepadEvent(KeyRoutedEventArgs args)
            => SectionStepKey(args.OriginalKey) != 0 || SectionStepKey(args.Key) != 0
                || ScrollKey(args.OriginalKey) != 0 || ScrollKey(args.Key) != 0;

        private bool HandleAuxiliaryGamepadKey(VirtualKey key, bool repeat)
        {
            int step = SectionStepKey(key), scroll = ScrollKey(key);
            if (step != 0 && IsSectionBrowseSurface)
            {
                if (!RawGamepadOwnsNavigation && !repeat) StepHomeSection(step);
                return true;
            }
            if (step != 0 && _watching && !_creatorVisible && !_libraryVisible && !_liveVisible)
            {
                // Raw polling owns the physical bumper edge. Consume the
                // platform's mapped alias so one press never seeks twice.
                if (!RawGamepadOwnsNavigation && !repeat) SeekVideoShoulder(step);
                return true;
            }
            if (scroll != 0)
            {
                if (!RawGamepadOwnsNavigation) ScrollGamepadSurface(scroll * 84);
                return true;
            }
            return false;
        }

        private bool ScrollGamepadSurface(double delta)
        {
            if (_disposed || _dialogOpen || _inputPaneVisible || IsNativeGamepadEditorActive) return false;
            if (_liveVisible) return LiveHub.ScrollBy(delta);
            if (_libraryVisible) return AccountLibrary.ScrollBy(delta);
            if (_creatorVisible) return CreatorHub.ScrollBy(delta);
            if (!_watching)
            {
                if (_gamepadHomeFeedReplacing) return false;
                CancelGamepadHomeFocus();
                int revision = ++_manualScrollRevision;
                var grid = HomeResultGrid;
                _manuallyScrolledList = grid;
                _homeScrollPending = true;
                _homeManuallyScrolled = true;
                bool handled = ViewportFocusScroll.Scroll(grid, delta, result =>
                {
                    if (revision != _manualScrollRevision || _disposed || _watching || _liveVisible || _libraryVisible
                        || _creatorVisible || !ReferenceEquals(grid, HomeResultGrid)) return;
                    ApplyHomeScrollFocus(result); _homeScrollPending = false;
                }, _gamepadHomeIndex);
                if (!handled) _homeScrollPending = false;
                return handled;
            }
            if (_playerUi.Drawer == PlayerDrawer.Comments)
            {
                _commentActionCard = null; _commentActionReturn = null;
                CancelGamepadListFocus();
                if (_viewingReplies) return ScrollReplyThread(delta);
                var list = _viewingReplies ? ReplyList : CommentList;
                _manuallyScrolledList = list;
                return ViewportFocusScroll.Scroll(list, delta, result =>
                {
                    if (_disposed || !_watching || _playerUi.Drawer != PlayerDrawer.Comments
                        || !ReferenceEquals(list, _viewingReplies ? ReplyList : CommentList) || result.Index < 0) return;
                    _gamepadNavigationList = list; _gamepadNavigationIndex = result.Index; _selectedGamepadControl = result.Container;
                    RememberCommentComposerPosition(result.Container);
                }, _gamepadNavigationIndex);
            }
            if (_playerUi.Drawer == PlayerDrawer.Settings || _playerUi.Drawer == PlayerDrawer.DanmakuSettings)
            {
                CancelGamepadListFocus();
                var root = _playerUi.Drawer == PlayerDrawer.Settings ? SettingsSide : DanmakuSettingsSide;
                var scroll = FindGamepadChild<ScrollViewer>(root);
                return scroll != null && ViewportFocusScroll.Scroll(scroll, delta);
            }
            return false;
        }

        private void ApplyHomeScrollFocus(ViewportScrollResult result)
        {
            if (result == null || result.Index < 0 || result.Container == null) return;
            _gamepadHomeInitialFocusDone = true;
            _gamepadHomeIndex = result.Index; _gamepadHomeItem = result.Item; _selectedGamepadControl = result.Container;
        }

        private void ReconcileHomeScrollBeforeNavigation()
        {
            // These views reconcile their own native lists before interpreting
            // direction/A. Do not cancel their pending scroll before that flush.
            if (_liveVisible || _creatorVisible || _libraryVisible) return;
            if (_homeManuallyScrolled && !_watching && !_liveVisible && !_libraryVisible && !_creatorVisible)
                ApplyHomeScrollFocus(ViewportFocusScroll.Reconcile(HomeResultGrid));
            if (_watching && !_liveVisible && !_creatorVisible && !_libraryVisible && _playerUi.Drawer == PlayerDrawer.Comments
                && ReferenceEquals(_manuallyScrolledList, _viewingReplies ? ReplyList : CommentList))
            {
                var list = _viewingReplies ? ReplyList : CommentList;
                var result = ViewportFocusScroll.Reconcile(list);
                if (result != null && result.Index >= 0)
                {
                    _gamepadNavigationList = list; _gamepadNavigationIndex = result.Index; _selectedGamepadControl = result.Container;
                    RememberCommentComposerPosition(result.Container);
                }
            }
            CancelManualGamepadScroll();
        }

        private void CancelManualGamepadScroll()
        {
            ++_manualScrollRevision; _homeScrollPending = _homeManuallyScrolled = false;
            _manuallyScrolledList = null;
            ViewportFocusScroll.Cancel(Root);
        }
    }
}
