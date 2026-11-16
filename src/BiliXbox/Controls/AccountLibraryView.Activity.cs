using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Models;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
namespace BiliXbox.Controls
{
    public sealed partial class AccountLibraryView
    {
        private Button _watchLater, _activity, _moreAuthors;
        private Grid _activityHost;
        private StackPanel _authorButtons;
        private CreatorHubView _activityHub;
        private readonly List<Button> _authors = new List<Button>();
        private int _authorIndex, _authorPage;
        private bool _activityEntered, _authorsBusy;
        private bool _activityOrderLocked;
        private CancellationTokenSource _activityCts;
        private void BeginActivityVisit()
        {
            EndActivityVisit();
            _activityCts = CancellationTokenSource.CreateLinkedTokenSource(_sessionCts.Token);
            _activityHub.Visibility = Visibility.Collapsed;
            _authorIndex = 0; _activitySelectedMid = 0; _activityOrderLocked = false;
            if (_authors.Count == 0) AddAuthor(new CreatorProfile { Mid = 0, Name = "所有动态" });
            EnsureActivityReadAccount(); SortActivityAuthors(); ApplyActivityDiscussionLayout();
        }
        private void EndActivityVisit()
        {
            Cancel(ref _activityCts); _activityHub?.StopRequests();
            _activityEntered = _authorsBusy = _unreadBusy = false;
        }
        public bool IsActivityDiscussionOpen => _state.Pane == LibraryPane.Activity && _activityEntered && _activityHub.IsDiscussionOpen;
        public Task WriteActivityCommentAsync(bool replyToFocused = false) => _state.Pane == LibraryPane.Activity && _activityEntered
            ? _activityHub.WriteCommentAsync(replyToFocused) : Task.CompletedTask;
        public void InvalidateWatchLater()
        {
            foreach (var state in _trail) if (state.Pane == LibraryPane.WatchLater) state.NeedsRefresh = true;
            if (_state.Pane == LibraryPane.WatchLater) _state.NeedsRefresh = true;
        }
        public void ConfigureActivity(Func<TextBox,string,int,Task<bool>> input,
            Func<CommentItem,CommentVote,CancellationToken,Task<string>> vote)
        { _activityHub.RequestTextInputAsync = input; _activityHub.RequestCommentVoteAsync = vote; }
        private void InitializeActivityOverview()
        {
            _watchLater = SectionButton("\uE823", "稍后再看", "继续待看的视频", () => OpenSection(LibraryPane.WatchLater), "AccountWatchLater");
            _activity = SectionButton("\uE8A1", "UP 动态", "全部动态与关注的 UP 主", () => OpenSection(LibraryPane.Activity), "AccountActivity");
            Add(_overview, _watchLater, 0, 3); Add(_overview, _activity, 0, 4);
            _activityHost = new Grid { ColumnSpacing = 14, Visibility = Visibility.Collapsed };
            _activityHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
            _activityHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _authorButtons = new StackPanel { Spacing = 10 };
            var scroll = new ScrollViewer { Content = _authorButtons, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, IsTabStop = false };
            _activityHost.Children.Add(scroll);
            _activityHub = new CreatorHubView(); Add(_activityHost, _activityHub, 0, 1); Add(_root, _activityHost, 1, 0);
            _activityHub.DiscussionLayoutChanged += (s, e) => ApplyActivityDiscussionLayout();
            _activityHub.DynamicsLoaded += OnActivityLoaded;
            _activityHub.DynamicViewed += OnActivityViewed;
            _activityHub.CloseRequested += (s,e) => { _activityEntered = false; RestoreActivityFocus(); };
            _activityHub.VideoRequested += (s,e) => {
                if (e.Video != null) Run(async () => { var handler = OpenItemRequested; if (handler != null)
                    await handler(new AccountLibraryItem { Bvid = e.Video.Bvid, Title = e.Video.Title }); });
            };
            ResetActivityAccount();
        }
        private void ApplyActivityDiscussionLayout()
        {
            bool detail = IsActivityDiscussionOpen;
            _activityHost.ColumnDefinitions[0].Width = new GridLength(detail ? 0 : 250);
            _activityHost.Children[0].Visibility = detail ? Visibility.Collapsed : Visibility.Visible;
            _root.Children[0].Visibility = detail ? Visibility.Collapsed : Visibility.Visible;
        }
        private void ResetActivityAccount()
        {
            EndActivityVisit();
            _activityHub?.StopRequests(); _authors.Clear(); _authorButtons?.Children.Clear();
            if (_activityHub != null) _activityHub.Visibility = Visibility.Collapsed;
            _authorIndex = _authorPage = 0; _activityEntered = _authorsBusy = false;
            _authorOrder.Clear(); _activityRead.Reset(0); _unreadBusy = false; _activitySelectedMid = 0;
            ApplyActivityDiscussionLayout();
        }
        private void AddAuthor(CreatorProfile author)
        {
            var button = MakeButton(author.Name, () => Run(() => OpenAuthorActivityAsync(author)), "ActivityAuthor" + author.Mid);
            button.HorizontalAlignment = HorizontalAlignment.Stretch; button.Tag = author;
            _authorOrder[author.Mid] = _authorOrder.Count;
            _authors.Add(button); _authorButtons.Children.Add(button);
        }
        private async Task LoadActivityAuthorsAsync()
        {
            if (_authorsBusy || !HasAccount) return;
            if (_activityCts == null) return;
            _authorsBusy = true; var session = _activityCts; long account = _accountMid;
            bool restoreMore = ReferenceEquals(Windows.UI.Xaml.Input.FocusManager.GetFocusedElement(), _moreAuthors);
            try
            {
                if (_authorPage == 0 && _authors.Count == 0) AddAuthor(new CreatorProfile { Mid = 0, Name = "所有动态" });
                var page = await _api.GetFollowedCreatorsAsync(_authorPage + 1, session.Token);
                if (!CurrentActivitySession(session, account)) return;
                if (_moreAuthors != null) { _authorButtons.Children.Remove(_moreAuthors); _moreAuthors = null; }
                foreach (var author in page.Users) { if (!_authors.Exists(b => (b.Tag as CreatorProfile)?.Mid == author.Mid)) AddAuthor(author); }
                _authorPage++;
                if (page.HasMore) { _moreAuthors = MakeButton("加载更多关注", () => Run(LoadActivityAuthorsAsync), "MoreActivityAuthors"); _authorButtons.Children.Add(_moreAuthors); }
                SortActivityAuthors();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            { if (CurrentActivitySession(session, account)) { _subtitle.Text = "关注列表读取失败：" + Friendly(ex); if (_moreAuthors == null) { _moreAuthors = MakeButton("重试关注列表", () => Run(LoadActivityAuthorsAsync), "RetryActivityAuthors"); _authorButtons.Children.Add(_moreAuthors); } } }
            finally { if (CurrentActivitySession(session, account)) { _authorsBusy = false; if (restoreMore && !_activityEntered) RestoreActivityFocus(); } }
        }
        private async Task OpenAuthorActivityAsync(CreatorProfile author)
        {
            if (author == null || !HasAccount) return;
            _activityOrderLocked = true;
            _authorIndex = Math.Max(0, _authors.FindIndex(b => ReferenceEquals(b.Tag, author)));
            _activitySelectedMid = author.Mid;
            _activityEntered = true;
            _activityHub.Visibility = Visibility.Visible;
            await _activityHub.OpenAsync(_api, author.Mid == 0 ? _accountMid : author.Mid, author.Name, author.AvatarUrl, true);
            if (_state.Pane == LibraryPane.Activity && HasAccount && _activityEntered && _activitySelectedMid == author.Mid) _activityHub.RestoreFocus();
        }
        private void RestoreActivityFocus()
        {
            if (_activityEntered) _activityHub.RestoreFocus();
            else if (_authors.Count > 0) FocusActivityAuthor(_authors[Math.Min(_authorIndex, _authors.Count - 1)]);
            else Focus(_back);
        }
        private async void FocusActivityAuthor(Button button)
        {
            Focus(button);
            if (ReferenceEquals(Windows.UI.Xaml.Input.FocusManager.GetFocusedElement(), button)) return;
            // A freshly attached button may not be focusable until XAML's load
            // pass. Retry once without letting a late pass override navigation.
            var session = _activityCts; int generation = _focusGeneration;
            try
            {
                await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low, () => {
                    if (!HasAccount || session != _activityCts || session == null || session.IsCancellationRequested
                        || _state.Pane != LibraryPane.Activity || _activityEntered || generation != _focusGeneration) return;
                    Focus(button);
                });
            }
            catch (Exception) { }
        }
        private bool BackActivity()
        {
            if (!_activityEntered) return false;
            if (_activityHub.Back()) return true;
            _activityEntered = false; _activityHub.StopRequests(); RestoreActivityFocus(); return true;
        }
        private void NavigateActivity(PlayerDirection direction)
        {
            if (_activityEntered) { _activityHub.Navigate(direction); return; }
            _activityOrderLocked = true;
            var current = Windows.UI.Xaml.Input.FocusManager.GetFocusedElement() as Button;
            if (current == _back || current == _signOut)
            { if (direction == PlayerDirection.Down) { _authorIndex = 0; RestoreActivityFocus(); }
                else if (direction == PlayerDirection.Left) Focus(_back); else if (direction == PlayerDirection.Right) Focus(_signOut); return; }
            if (_moreAuthors != null && current == _moreAuthors)
            { if (direction == PlayerDirection.Up) { _authorIndex = Math.Max(0, _authors.Count - 1); RestoreActivityFocus(); } return; }
            int index = _authors.IndexOf(current);
            if (index < 0) { _authorIndex = 0; RestoreActivityFocus(); return; }
            _authorIndex = index;
            if (direction == PlayerDirection.Up && index == 0) { Focus(_back); return; }
            if (direction == PlayerDirection.Up) _authorIndex--;
            if (direction == PlayerDirection.Down) {
                if (index == _authors.Count - 1 && _moreAuthors != null) { Focus(_moreAuthors); return; }
                _authorIndex = Math.Min(_authors.Count - 1, index + 1);
            }
            RestoreActivityFocus();
        }
        private void ConfirmActivity()
        {
            if (_activityEntered) { _activityHub.Confirm(false); return; }
            var current = Windows.UI.Xaml.Input.FocusManager.GetFocusedElement() as Button;
            if (current == _back) { if (!Back()) CloseRequested?.Invoke(this, EventArgs.Empty); return; }
            if (current == _signOut) { SignOutRequested?.Invoke(this, EventArgs.Empty); return; }
            if (current == _moreAuthors) { Run(LoadActivityAuthorsAsync); return; }
            int focusedIndex = _authors.IndexOf(current); if (focusedIndex >= 0) _authorIndex = focusedIndex;
            if (_authorIndex >= _authors.Count) { Run(LoadActivityAuthorsAsync); return; }
            if (_authors.Count > 0) Run(() => OpenAuthorActivityAsync(_authors[_authorIndex].Tag as CreatorProfile));
        }
    }
}
