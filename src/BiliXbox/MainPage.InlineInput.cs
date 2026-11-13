using System;
using System.Threading.Tasks;
using BiliXbox.Controls;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace BiliXbox
{
    public sealed partial class MainPage
    {
        private InlineNativeTextInputSession _activeInlineTextInput;
        private TextBox _inlineTextEditor;

        private async Task<bool> ShowInlineTextInputAsync(TextBox editor, bool submitOnEnter = false)
        {
            if (_disposed || _dialogOpen || editor == null || !editor.IsEnabled || editor.Parent == null
                || editor.Visibility != Visibility.Visible) return false;
            ++_textInputRequest;
            _dialogOpen = true;
            DisablePointerCursor();
            var owner = new InlineNativeTextInputSession(editor, submitOnEnter,
                () => submitOnEnter && ReferenceEquals(editor, SearchInput)
                    && ReferenceEquals(FocusManager.GetFocusedElement(), SearchSubmitButton));
            bool wasTabStop = editor.IsTabStop;
            _activeInlineTextInput = owner;
            _inlineTextEditor = editor;
            editor.IsReadOnly = false;
            editor.IsTabStop = true;
            editor.StartBringIntoView();
            try { return await owner.ShowAsync(); }
            finally
            {
                owner.Dispose();
                if (_activeInlineTextInput == owner)
                {
                    _activeInlineTextInput = null;
                    _inlineTextEditor = null;
                    _dialogOpen = false;
                    _inputPaneVisible = false;
                    _lastBack = DateTimeOffset.UtcNow;
                    editor.IsReadOnly = true;
                    editor.IsTabStop = wasTabStop;
                    DisablePointerCursor();
                }
            }
        }

        private async Task ShowInlineSearchAsync()
        {
            if (_disposed || _dialogOpen) return;
            TextBox editor = null;
            bool submit = false;
            // The same home search field is shown while editing from playback
            // or a creator page. Cancel restores that surface without opening
            // a second editor in a modal dialog.
            bool wasWatching = _watching, wasCreator = _creatorVisible, wasLibrary = _libraryVisible, wasLive = _liveVisible;
            var header = Header.Visibility; var footer = Footer.Visibility;
            var home = HomeView.Visibility; var watch = WatchView.Visibility;
            var creator = CreatorHub.Visibility; var library = AccountLibrary.Visibility; var padding = Root.Padding;
            var live = LiveHub.Visibility;
            if (wasWatching || wasCreator || wasLibrary || wasLive)
            {
                WatchView.Visibility = CreatorHub.Visibility = AccountLibrary.Visibility = Visibility.Collapsed;
                LiveHub.Visibility = Visibility.Collapsed;
                Header.Visibility = Footer.Visibility = HomeView.Visibility = Visibility.Visible;
                Root.Padding = new Thickness(52, 28, 52, 20);
            }
            try
            {
                editor = InlineNativeTextInputSession.ReplaceEditor(SearchInput, 200);
                SearchInput = editor;
                editor.Visibility = Visibility.Visible;
                SearchEntryButton.Visibility = Visibility.Collapsed;
                submit = await ShowInlineTextInputAsync(editor, true);
            }
            finally
            {
                // Cleanup belongs to this editor and ends before any result or
                // video request. A slow preceding search must never hide a new
                // editor that the user opened while its video was loading.
                if (editor == null || ReferenceEquals(SearchInput, editor))
                {
                    SearchQueryLabel.Text = string.IsNullOrWhiteSpace(SearchInput.Text)
                        ? "搜索视频或用户，支持 BV 号 / 视频链接" : SearchInput.Text;
                    SearchInput.Visibility = Visibility.Collapsed;
                    SearchEntryButton.Visibility = Visibility.Visible;
                    if (!_disposed && wasWatching == _watching && wasCreator == _creatorVisible && wasLibrary == _libraryVisible && wasLive == _liveVisible)
                    {
                        Header.Visibility = header; Footer.Visibility = footer; HomeView.Visibility = home;
                        WatchView.Visibility = watch; CreatorHub.Visibility = creator; AccountLibrary.Visibility = library; Root.Padding = padding;
                        LiveHub.Visibility = live;
                        if (wasLive) { ApplyLiveSectionSurface(); LiveHub.RestoreFocus(); }
                        else if (wasLibrary) AccountLibrary.RestoreFocus();
                        else if (wasCreator) CreatorHub.RestoreFocus();
                        else if (wasWatching) { ShowControls(); FocusPlayerInput(); }
                        else if (ReferenceEquals(FocusManager.GetFocusedElement(), SearchInput)) SearchEntryButton.Focus(FocusState.Programmatic);
                    }
                }
            }
            if (submit && !_disposed && ReferenceEquals(SearchInput, editor)
                && !string.IsNullOrWhiteSpace(editor.Text)) await SearchAsync();
        }

        private void CommentInput_Tapped(object sender, TappedRoutedEventArgs args)
        {
            if (_dialogOpen || _disposed) return;
            CommentEntry_Click(sender, new RoutedEventArgs());
        }

        private void SearchSubmit_LostFocus(object sender, RoutedEventArgs args)
        {
            var focused = FocusManager.GetFocusedElement();
            if (_activeInlineTextInput != null && ReferenceEquals(_inlineTextEditor, SearchInput)
                && !ReferenceEquals(focused, SearchInput) && !ReferenceEquals(focused, SearchSubmitButton))
                _activeInlineTextInput.Complete(false);
        }
    }
}
