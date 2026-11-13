using System;
using System.Collections.Generic;

namespace BiliXbox.HomeFeedFocusTests
{
    public enum FocusState { Unfocused, Pointer, Keyboard }
    public enum Visibility { Visible, Collapsed }
    public enum PlayerDirection { Left, Right, Up, Down }
    public class Control
    {
        public bool IsEnabled = true;
        public FocusState FocusState;
        public string Name;
        public Control(string name) { Name = name; }
        public bool Focus(FocusState state) { FocusState = state; FocusManager.Current = this; return true; }
    }
    public sealed class GridViewItem : Control
    {
        public int Index;
        public GridViewItem(int index) : base("result " + index) { Index = index; }
    }
    public sealed class Grid
    {
        public readonly List<object> Items = new List<object>();
        public Visibility Visibility = Visibility.Collapsed;
    }
    public static class FocusManager
    {
        public static Control Current;
        public static object GetFocusedElement() => Current;
    }
    public sealed class MainPage
    {
        private bool _disposed, _watching, _creatorVisible, _libraryVisible, _liveVisible, _dialogOpen, _inputPaneVisible, _homeScrollPending;
        private void CancelManualGamepadScroll() { }
        private bool _gamepadHomeFocusPending, _gamepadHomeInitialFocusDone, _gamepadHomeFeedReplacing;
        private int _gamepadHomeIndex, _gamepadHomeFocusGeneration;
        private object _gamepadHomeItem;
        private Control _selectedGamepadControl;
        private readonly Grid VideoGrid = new Grid();
        private readonly Grid UserGrid = new Grid();
        private Grid HomeResultGrid => UserGrid.Visibility == Visibility.Visible ? UserGrid : VideoGrid;
        private readonly Grid SearchModes = new Grid();
        private readonly Control HomeButton = new Control("home");
        private readonly Control SearchEntryButton = new Control("search entry");
        private readonly Control SearchSubmitButton = new Control("search submit");
        private readonly Control SearchVideosButton = new Control("video mode");
        private readonly Control SearchCreatorButton = new Control("user mode");
        private readonly Control NextPage = new Control("next page");
        private readonly Control PreviousPage = new Control("previous page");
        private readonly Control RefreshFeedButton = new Control("refresh");
        private Control[] HomeHeaderControls => new[] { HomeButton };
        private Control[] HomeSearchControls => new[] { SearchEntryButton, SearchSubmitButton };
        private Control[] HomeModeControls => new[] { SearchVideosButton, SearchCreatorButton };
        private Control[] HomePagerControls => new[] { PreviousPage, NextPage, RefreshFeedButton };
        private int _videoFocusCalls, _lastFocusedIndex = -1;
        private string _lastGroup;
        private void DisablePointerCursor() { }
        private int GamepadHomeColumnCount() => 4;
        private int GamepadHomeVideoIndex(Control control) => (control as GridViewItem)?.Index ?? -1;
        private T FindGamepadAncestor<T>(Control control, Grid grid) where T : Control => control as T;
        private bool IsNavigableGamepadHomeControl(Control control) => control != null && control.IsEnabled;
        private static bool ContainsGamepadControl(Control[] group, Control control) => Array.IndexOf(group, control) >= 0;
        private List<Control> AvailableGamepadHomeControls(Control[] group) => new List<Control>(Array.FindAll(group, x => x.IsEnabled));
        private Control[] HomeGroupContaining(Control control)
        {
            foreach (var group in new[] { HomeHeaderControls, HomeSearchControls, HomeModeControls, HomePagerControls })
                if (ContainsGamepadControl(group, control)) return group;
            return null;
        }
        private void FocusGamepadHomeControl(Control control)
        { CancelGamepadHomeFocus(); control.Focus(FocusState.Keyboard); }
        private void FocusGamepadHomeVideo(int index)
        {
            _videoFocusCalls++; _lastFocusedIndex = _gamepadHomeIndex = index;
            new GridViewItem(index).Focus(FocusState.Keyboard);
        }
        private void FocusGamepadHomeGroup(Control[] group, Control current)
        {
            _lastGroup = group[0].Name;
            FocusGamepadHomeControl(group[0]);
        }
        private void FocusGamepadHomeVideoOrPager(Control control, bool lastRow)
        { FocusGamepadHomeVideo(lastRow ? (HomeResultGrid.Items.Count - 1) / 4 * 4 : 0); }

        /* PRODUCTION_METHODS */

        private static int _checks;
        private static void Check(bool condition, string message)
        { ++_checks; if (!condition) throw new Exception(message); }
        private void Fill(int count)
        { HomeResultGrid.Items.Clear(); for (int i = 0; i < count; i++) HomeResultGrid.Items.Add(new object()); }
        public static void Run()
        {
            foreach (bool userResults in new[] { false, true })
            foreach (int oldIndex in new[] { 0, 4, 16, 19 })
            foreach (int count in new[] { 0, 1, 3, 4, 20 })
            {
                var page = new MainPage();
                page.UserGrid.Visibility = userResults ? Visibility.Visible : Visibility.Collapsed;
                page.Fill(20);
                page._gamepadHomeIndex = oldIndex;
                page._gamepadHomeInitialFocusDone = true;
                page._gamepadHomeFocusPending = true;
                page._gamepadHomeItem = page.HomeResultGrid.Items[oldIndex];
                page.NextPage.Focus(FocusState.Keyboard);
                int generation = page._gamepadHomeFocusGeneration;
                page.BeginHomeFeedReplacement();
                Check(page._gamepadHomeFocusGeneration > generation && !page._gamepadHomeFocusPending && page._gamepadHomeItem == null,
                    "Page replacement must invalidate a deferred old container.");
                Check(page._gamepadHomeIndex == 0 && !page._gamepadHomeInitialFocusDone && page._gamepadHomeFeedReplacing,
                    "New page must reset old logical position before collections are cleared.");
                Check(FocusManager.Current == page.SearchEntryButton, "Old native pager/container must lose focus before Clear.");
                page.Fill(count);
                page.RestoreHomeGamepadFocus();
                Check(page._videoFocusCalls == 0, "Polling must not select old/new data while the page is loading.");
                page.CompleteHomeFeedReplacement();
                Check(!page._gamepadHomeFeedReplacing && page._gamepadHomeIndex == 0, "Completion must select the top of the new page.");
                if (count == 0)
                    Check(FocusManager.Current == page.SearchEntryButton && !page._gamepadHomeInitialFocusDone, "Empty pages retain a reachable search entry.");
                else
                {
                    Check(page._lastFocusedIndex == 0 && page._gamepadHomeInitialFocusDone, "New result set must focus its first card, never old pager/last row.");
                    page.NavigateGamepadHome(PlayerDirection.Up);
                    Check(page._lastGroup == "search entry", "Up from the new first card must reach search, not the bottom result row.");
                }
            }
            // A user may open a native keyboard while the request is in flight.
            var modal = new MainPage(); modal.Fill(20); modal.BeginHomeFeedReplacement();
            modal._dialogOpen = true; modal.CompleteHomeFeedReplacement();
            Check(modal._videoFocusCalls == 0 && !modal._gamepadHomeInitialFocusDone, "Completion must not steal native modal focus.");
            modal._dialogOpen = false; modal.RestoreHomeGamepadFocus();
            Check(modal._lastFocusedIndex == 0, "Closing the modal restores the new first result.");
            var library = new MainPage(); library.Fill(20); library.BeginHomeFeedReplacement();
            library._libraryVisible = true;
            var libraryControl = new Control("account history"); libraryControl.Focus(FocusState.Keyboard);
            library.CompleteHomeFeedReplacement();
            Check(library._videoFocusCalls == 0 && FocusManager.Current == libraryControl,
                "Feed completion must not steal personal-library focus.");
            library._libraryVisible = false; library.RestoreHomeGamepadFocus();
            Check(library._lastFocusedIndex == 0, "Closing personal-library can restore the new feed result.");
            // Existing-page footer traversal still enters its final row.
            var footer = new MainPage(); footer.Fill(20); footer._gamepadHomeInitialFocusDone = true;
            footer.NextPage.Focus(FocusState.Keyboard); footer.NavigateGamepadHome(PlayerDirection.Up);
            Check(footer._lastFocusedIndex == 16, "Up from an unchanged page footer should retain normal bottom-row traversal.");
            Console.WriteLine("PASS: " + _checks + " production feed replacement and direction-dispatch checks (UI calls substituted).");
        }
    }
}
