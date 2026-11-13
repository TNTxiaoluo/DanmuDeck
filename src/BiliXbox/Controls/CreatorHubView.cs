using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Models;
using BiliXbox.Services;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Markup;
using Windows.UI.Xaml.Media;

namespace BiliXbox.Controls
{
    public sealed class CreatorVideoRequestedEventArgs : EventArgs
    {
        public VideoItem Video { get; }
        public CreatorVideoRequestedEventArgs(VideoItem video) { Video = video; }
    }

    // Owns only the creator screen. MainPage owns raw input, system keyboard,
    // modal dialogs and playback; no video-comment state is shared with this view.
    public sealed partial class CreatorHubView : UserControl
    {
        private enum HubPane { Videos, Dynamics, Detail, Replies }
        private BilibiliClient _api;
        private long _mid;
        private CancellationTokenSource _sessionCts, _commentCts, _replyCts, _profileLoadOwner;
        private HubPane _pane;
        private int _videoPage = 1, _replyPage, _focusGeneration;
        private bool _videoHasMore, _dynamicHasMore, _commentHasMore, _replyHasMore;
        private bool _videosLoaded, _dynamicsLoaded, _videosBusy, _dynamicsBusy, _commentsBusy, _repliesBusy, _posting, _writing;
        private bool _commentClosed;
        private string _dynamicOffset = "", _commentCursor = "", _profileNotice = "";
        private readonly CommentPageGuard _commentPages = new CommentPageGuard();
        private CreatorDynamicItem _dynamic;
        private DynamicCommentTarget _target;
        private CommentItem _replyRoot;
        private CommentItem _composeReply;
        private long _composeRoot, _composeParent;
        private CommentCard _actionCard;
        private Control _actionRow;
        private CommentImageViewer _commentImages;
        private string _draft = "";
        private readonly Dictionary<string, CreatorCommentDraftState> _drafts = new Dictionary<string, CreatorCommentDraftState>();
        private string _draftKey = "", _videoNotice = "", _dynamicNotice = "";
        private CreatorCommentDraftState _draftState;
        private readonly List<VideoItem> _videos = new List<VideoItem>();
        private readonly List<CreatorDynamicItem> _dynamics = new List<CreatorDynamicItem>();
        private readonly List<CommentItem> _comments = new List<CommentItem>(), _replies = new List<CommentItem>();
        private ListViewBase _pendingList;
        private int _pendingIndex = -1;
        private Control _lastFocus;

        private readonly Grid _root, _browse, _detail;
        private readonly AvatarView _avatar;
        private readonly TextBlock _name, _signature, _stats, _notice, _pageLabel, _hint;
        private readonly Button _back, _videosTab, _dynamicsTab, _refresh, _previous, _next, _moreDynamics;
        private readonly GridView _videoList;
        private readonly ListView _dynamicList, _commentList, _replyList;
        private readonly StackPanel _pager;
        private readonly ScrollViewer _detailScroll;
        private readonly StackPanel _detailContent;
        private readonly TextBlock _commentTitle, _commentHint, _draftLabel;
        private TextBox _draftEditor;
        private bool _updatingDraftEditor;
        private CancellationTokenSource _draftEditOwner;
        private DynamicCommentTarget _draftEditTarget;
        private string _draftEditKey;
        private readonly Button _refreshComments, _write, _send, _login, _moreComments, _moreReplies, _detailVideo;

        public event EventHandler CloseRequested;
        public event EventHandler<CreatorVideoRequestedEventArgs> VideoRequested;
        public event EventHandler LoginRequested;
        public event EventHandler ImageViewerOpened;
        public event EventHandler ImageViewerClosed;
        public bool IsImageOpen => _commentImages?.IsOpen == true;
        public bool RequestImageBack() => _commentImages?.RequestBack() == true;
        // The editor is already visible in this view. Native keyboard dismissal
        // keeps its current text; only the explicit publish action submits it.
        public Func<TextBox, string, int, Task<bool>> RequestTextInputAsync { get; set; }
        public Func<CommentItem, CommentVote, CancellationToken, Task<string>> RequestCommentVoteAsync { get; set; }
        public CancellationToken CommentCancellation => _commentCts?.Token ?? new CancellationToken(true);

        public CreatorHubView()
        {
            RequiresPointer = RequiresPointer.Never;
            XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Enabled;
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            VerticalContentAlignment = VerticalAlignment.Stretch;
            _root = new Grid { Background = Brush(255, 13, 13, 16), Padding = new Thickness(52, 28, 52, 20), RowSpacing = 14 };
            _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Content = _root;

            var header = new Grid { ColumnSpacing = 18 };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _back = Button("返回", () => { if (!Back()) CloseRequested?.Invoke(this, EventArgs.Empty); });
            header.Children.Add(_back);
            _avatar = new AvatarView { Width = 56, Height = 56, VerticalAlignment = VerticalAlignment.Center };
            Add(header, _avatar, 0, 1);
            var identity = new StackPanel { Spacing = 3 };
            _name = Text("UP 主主页", 28, true); _signature = Text("", 14); _stats = Text("", 13);
            _name.TextTrimming = _signature.TextTrimming = _stats.TextTrimming = TextTrimming.CharacterEllipsis;
            _signature.Foreground = _stats.Foreground = SecondaryBrush();
            identity.Children.Add(_name); identity.Children.Add(_signature); identity.Children.Add(_stats);
            Add(header, identity, 0, 2); _root.Children.Add(header);

            var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            _videosTab = Button("视频", () => SwitchTab(HubPane.Videos));
            _dynamicsTab = Button("动态", () => SwitchTab(HubPane.Dynamics));
            _refresh = Button("刷新", () => Run(RefreshAsync));
            tabs.Children.Add(_videosTab); tabs.Children.Add(_dynamicsTab); tabs.Children.Add(_refresh);
            Add(_root, tabs, 1, 0);

            _browse = new Grid { RowSpacing = 8 };
            _browse.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _browse.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _browse.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _notice = Text("正在读取投稿…", 14); _notice.TextWrapping = TextWrapping.Wrap;
            _notice.Foreground = SecondaryBrush(); _notice.MaxLines = 2; _notice.TextTrimming = TextTrimming.CharacterEllipsis;
            _browse.Children.Add(_notice);
            _videoList = new GridView
            {
                SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = true,
                Padding = new Thickness(20, 18, 20, 24), HorizontalContentAlignment = HorizontalAlignment.Stretch,
                ItemContainerStyle = (Style)Application.Current.Resources["TvCardItemStyle"],
                ItemTemplate = (DataTemplate)Application.Current.Resources["TvVideoCardTemplate"]
            };
            _videoList.ItemsPanel = (ItemsPanelTemplate)XamlReader.Load("<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><ItemsWrapGrid Orientation='Horizontal' ItemWidth='282' /></ItemsPanelTemplate>");
            ScrollViewer.SetHorizontalScrollMode(_videoList, ScrollMode.Disabled);
            ScrollViewer.SetVerticalScrollMode(_videoList, ScrollMode.Enabled);
            ScrollViewer.SetVerticalScrollBarVisibility(_videoList, ScrollBarVisibility.Hidden);
            AutomationProperties.SetAutomationId(_videoList, "CreatorVideoGrid");
            AutomationProperties.SetName(_videoList, "UP 主视频");
            TvFocusEffects.AttachCardFocus(_videoList);
            _videoList.ItemClick += (s, e) => RequestVideo(e.ClickedItem as VideoItem);
            Add(_browse, _videoList, 1, 0);
            _dynamicList = MakeList();
            _dynamicList.Padding = new Thickness(20, 18, 20, 24);
            _dynamicList.ItemContainerStyle = (Style)Application.Current.Resources["TvDynamicItemStyle"];
            AutomationProperties.SetAutomationId(_dynamicList, "CreatorDynamicList");
            AutomationProperties.SetName(_dynamicList, "UP 主动态");
            TvFocusEffects.AttachCardFocus(_dynamicList);
            _dynamicList.ItemClick += (s, e) =>
            {
                var element = e.ClickedItem as FrameworkElement;
                var item = element?.Tag as CreatorDynamicItem;
                if (item != null) Run(() => OpenDynamicAsync(item));
            };
            Add(_browse, _dynamicList, 1, 0);
            _pager = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, HorizontalAlignment = HorizontalAlignment.Center };
            _previous = Button("上一页", () => Run(() => LoadVideosAsync(Math.Max(1, _videoPage - 1))));
            _next = Button("下一页", () => Run(() => LoadVideosAsync(_videoPage + 1)));
            _moreDynamics = Button("加载更多动态", () => Run(() => LoadDynamicsAsync(false)));
            _pageLabel = Text("第 1 页", 15); _pageLabel.VerticalAlignment = VerticalAlignment.Center;
            _pager.Children.Add(_previous); _pager.Children.Add(_pageLabel); _pager.Children.Add(_next); _pager.Children.Add(_moreDynamics);
            Add(_browse, _pager, 2, 0); Add(_root, _browse, 2, 0);

            _detail = new Grid { ColumnSpacing = 20, Visibility = Visibility.Collapsed };
            _detail.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _detail.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(426) });
            var post = new Grid { RowSpacing = 12 };
            post.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            post.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _detailVideo = Button("播放动态中的视频", () => RequestVideo(FindDynamicVideo(_dynamic)));
            post.Children.Add(_detailVideo);
            _detailContent = new StackPanel { Spacing = 18, Padding = new Thickness(22) };
            _detailScroll = new ScrollViewer
            {
                Content = _detailContent, VerticalScrollMode = ScrollMode.Enabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollMode = ScrollMode.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, IsTabStop = true
            };
            AutomationProperties.SetAutomationId(_detailScroll, "CreatorDetailScroll");
            AutomationProperties.SetName(_detailScroll, "动态正文");
            var postSurface = new Border { Background = CardBrush(), BorderBrush = SubtleBorderBrush(), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16), Child = _detailScroll };
            Add(post, postSurface, 1, 0); _detail.Children.Add(post);
            var discussion = new Grid
            {
                RowSpacing = 10, Padding = new Thickness(18), Background = CardBrush(),
                CornerRadius = new CornerRadius(16), BorderBrush = SubtleBorderBrush(), BorderThickness = new Thickness(1)
            };
            discussion.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            discussion.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            discussion.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            discussion.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var composer = new StackPanel { Spacing = 8 };
            _commentTitle = Text("动态评论", 22, true); composer.Children.Add(_commentTitle);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            _refreshComments = Button("刷新", () => Run(() => _pane == HubPane.Replies ? LoadRepliesAsync(true) : LoadCommentsAsync(true)));
            _write = Button("写评论", () => Run(WriteNewCommentAsync)); _send = Button("发布", () => Run(PostCommentAsync));
            actions.Children.Add(_refreshComments); actions.Children.Add(_write); actions.Children.Add(_send); composer.Children.Add(actions);
            _draftEditor = new TextBox
            {
                PlaceholderText = "在这里填写动态评论…", IsReadOnly = true, IsTabStop = true,
                IsFocusEngagementEnabled = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                MaxLength = 1000, MinHeight = 56, MaxHeight = 94, FontSize = 16,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            AutomationProperties.SetAutomationId(_draftEditor, "CreatorCommentInput");
            AutomationProperties.SetName(_draftEditor, "动态评论输入内容");
            AttachDraftEditor();
            composer.Children.Add(_draftEditor);
            _draftLabel = Text("原生键盘输入后保留草稿，选择发布才会发送", 13); _draftLabel.TextWrapping = TextWrapping.Wrap; _draftLabel.MaxLines = 2;
            _draftLabel.Foreground = SecondaryBrush();
            composer.Children.Add(_draftLabel); discussion.Children.Add(composer);
            var status = new StackPanel { Spacing = 7 };
            _commentHint = Text("", 14); _commentHint.TextWrapping = TextWrapping.Wrap; _commentHint.MaxLines = 3; _commentHint.Foreground = SecondaryBrush();
            _login = Button("扫码登录", () => LoginRequested?.Invoke(this, EventArgs.Empty)); status.Children.Add(_commentHint); status.Children.Add(_login);
            Add(discussion, status, 1, 0);
            _commentList = MakeList(); _replyList = MakeList();
            _replyList.ContainerContentChanging += (sender, args) =>
            {
                if (args.ItemContainer != null) args.ItemContainer.IsTabStop = args.Item is CommentCard;
            };
            AutomationProperties.SetAutomationId(_commentList, "CreatorCommentList");
            AutomationProperties.SetAutomationId(_replyList, "CreatorReplyList");
            _commentList.ItemClick += (s, e) => ActivateCommentCard(e.ClickedItem as CommentCard);
            _replyList.ItemClick += (s, e) => ActivateCommentCard(e.ClickedItem as CommentCard);
            Add(discussion, _commentList, 2, 0); Add(discussion, _replyList, 2, 0);
            var more = new Grid();
            _moreComments = Button("加载更多评论", () => Run(() => LoadCommentsAsync(!_commentHasMore)));
            _moreReplies = Button("加载更多回复", () => Run(() => LoadRepliesAsync(!_replyHasMore)));
            more.Children.Add(_moreComments); more.Children.Add(_moreReplies); Add(discussion, more, 3, 0);
            Add(_detail, discussion, 0, 1); Add(_root, _detail, 2, 0);
            _hint = Text("左摇杆 / 方向键选择 · A 确认 · B 返回 · X 写动态评论", 14);
            _hint.Foreground = Brush(102, 242, 242, 247); _hint.FontSize = 13; Add(_root, _hint, 3, 0);
            PreviewKeyDown += NativeKeyDown;
            ApplyPane();
        }

        public async Task OpenAsync(BilibiliClient api, long mid, string name, string avatar)
        {
            if (api == null) throw new ArgumentNullException(nameof(api));
            if (mid <= 0) throw new ArgumentOutOfRangeException(nameof(mid));
            StopRequests();
            _api = api; _mid = mid; _sessionCts = new CancellationTokenSource();
            _pane = HubPane.Videos; _videosLoaded = _dynamicsLoaded = false;
            _videos.Clear(); _dynamics.Clear(); _videoList.Items.Clear(); _dynamicList.Items.Clear();
            _comments.Clear(); _replies.Clear(); _commentList.Items.Clear(); _replyList.Items.Clear();
            _dynamic = null; _target = null; _replyRoot = null;
            ResetReplyTarget(); ClearCommentActions();
            _videoPage = 1; _dynamicOffset = _commentCursor = _profileNotice = "";
            _videoHasMore = _dynamicHasMore = false;
            _avatar.Url = avatar ?? ""; _name.Text = string.IsNullOrWhiteSpace(name) ? "UP 主 · " + mid : name;
            _signature.Text = "正在读取 UP 主资料…"; _stats.Text = "";
            _videoNotice = "正在读取投稿…"; _dynamicNotice = "选择动态标签后读取公开动态。";
            ApplyPane(); Focus(_videosTab);
            var owner = _sessionCts;
            await Task.WhenAll(LoadProfileAsync(owner), LoadVideosAsync(1));
        }

        // Pause or leave the hub without discarding its lists or draft. Restoring
        // it renews request scopes instead of reviving a cancelled operation.
        public void StopRequests()
        {
            _commentImages?.Close(); _commentImages?.Dispose(); _commentImages = null;
            ClearCommentActions();
            _draftState?.DisarmRetry();
            _draftEditOwner = null; _draftEditTarget = null; _draftEditKey = null;
            Cancel(ref _replyCts); Cancel(ref _commentCts); Cancel(ref _sessionCts);
            ++_focusGeneration; _pendingList = null; _pendingIndex = -1;
            _videosBusy = _dynamicsBusy = _commentsBusy = _repliesBusy = _posting = _writing = false;
        }

        public void RestoreFocus()
        {
            EnsureSession(); SelectDraft(); ApplyPane();
            if (IsAvailable(_lastFocus)) Focus(_lastFocus);
            else if (ActiveList.Items.Count > 0) FocusItem(ActiveList, 0);
            else Focus(IsDetail ? (Control)_write : _videosTab);
        }

        public bool Back()
        {
            if (RequestImageBack()) return true;
            if (LeaveCommentActions()) return true;
            _draftState?.DisarmRetry();
            if (_pane == HubPane.Replies)
            {
                var root = _replyRoot;
                Cancel(ref _replyCts); _repliesBusy = false; _pane = HubPane.Detail;
                ResetReplyTarget();
                _commentTitle.Text = "动态评论" + (_dynamic?.CommentCount > 0 ? " · " + _dynamic.CommentCount : "");
                _commentHint.Text = "已加载 " + _comments.Count + " 条评论";
                ApplyPane();
                int index = root == null ? -1 : _comments.FindIndex(item => item.Id == root.Id);
                if (index >= 0) FocusItem(_commentList, index); else Focus(_write);
                return true;
            }
            if (_pane == HubPane.Detail)
            {
                Cancel(ref _replyCts); Cancel(ref _commentCts);
                _commentsBusy = _repliesBusy = _posting = false;
                ResetReplyTarget();
                _pane = HubPane.Dynamics; ApplyPane();
                int index = _dynamics.IndexOf(_dynamic);
                if (index >= 0) FocusItem(_dynamicList, index); else Focus(_dynamicsTab);
                return true;
            }
            return false;
        }

        private bool IsDetail => _pane == HubPane.Detail || _pane == HubPane.Replies;
        private ListViewBase ActiveList => _pane == HubPane.Videos ? (ListViewBase)_videoList : _pane == HubPane.Dynamics ? _dynamicList : _pane == HubPane.Replies ? _replyList : _commentList;
        private Button ActiveMore => _pane == HubPane.Replies ? _moreReplies : _moreComments;
        private string TargetKey(DynamicCommentTarget target)
        {
            return target == null ? "" : (_api?.IsLoggedIn == true ? _api.CurrentUser?.Mid ?? 0 : 0) + ":" + target.Type + ":" + target.Oid + ":" + _composeRoot + ":" + _composeParent;
        }
        private void SelectDraft()
        {
            string key = TargetKey(_target);
            if (key.Length == 0) { _draftKey = ""; _draftState = null; _draft = ""; return; }
            if (!_drafts.TryGetValue(key, out _draftState)) { _draftState = new CreatorCommentDraftState(); _drafts.Add(key, _draftState); }
            if (_draftKey != key) _draftState.DisarmRetry();
            _draftKey = key; _draft = _draftState.Draft; _posting = _draftState.InFlight;
        }
        private void EnsureSession()
        {
            if (_api == null || _mid <= 0) return;
            if (_sessionCts == null) _sessionCts = new CancellationTokenSource();
            if (IsDetail && _commentCts == null) _commentCts = CancellationTokenSource.CreateLinkedTokenSource(_sessionCts.Token);
        }
        private bool Current(CancellationTokenSource owner) => owner != null && ReferenceEquals(owner, _sessionCts) && !owner.IsCancellationRequested;
        private bool CurrentComment(CancellationTokenSource owner, DynamicCommentTarget target) => Current(_sessionCts) && owner != null && ReferenceEquals(owner, _commentCts) && !owner.IsCancellationRequested && ReferenceEquals(target, _target) && IsDetail;

        private async Task LoadProfileAsync(CancellationTokenSource owner)
        {
            if (!Current(owner) || ReferenceEquals(_profileLoadOwner, owner)) return;
            _profileLoadOwner = owner;
            UpdateActions();
            try
            {
                var profile = await _api.GetCreatorProfileAsync(_mid, owner.Token);
                if (!Current(owner)) return;
                _profileNotice = "";
                _name.Text = string.IsNullOrWhiteSpace(profile.Name) ? _name.Text : profile.Name;
                if (!string.IsNullOrWhiteSpace(profile.AvatarUrl)) _avatar.Url = profile.AvatarUrl;
                _signature.Text = profile.Signature;
                _stats.Text = profile.FollowersText + "    " + profile.FollowingText + "    " + profile.VideoCountText;
            }
            catch (OperationCanceledException) when (owner.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (!Current(owner)) return;
                _signature.Text = "资料暂未读取，可继续查看投稿和动态。";
                _profileNotice = "UP 资料读取失败：" + Friendly(ex);
                _stats.Text = _profileNotice;
            }
            finally
            {
                if (ReferenceEquals(_profileLoadOwner, owner)) _profileLoadOwner = null;
                if (Current(owner)) UpdateActions();
            }
        }

        private void SwitchTab(HubPane pane)
        {
            ClearCommentActions(); ResetReplyTarget();
            _draftState?.DisarmRetry();
            if (IsDetail) { Cancel(ref _replyCts); Cancel(ref _commentCts); _commentsBusy = _repliesBusy = _posting = false; }
            _pane = pane; ApplyPane();
            if (pane == HubPane.Videos && !_videosLoaded) Run(() => LoadVideosAsync(1));
            else if (pane == HubPane.Dynamics && !_dynamicsLoaded) Run(() => LoadDynamicsAsync(true));
            else if (ActiveList.Items.Count > 0) FocusItem(ActiveList, 0);
        }

        private Task RefreshAsync()
        {
            EnsureSession();
            if (IsDetail) return _pane == HubPane.Replies ? LoadRepliesAsync(true) : LoadCommentsAsync(true);
            var content = _pane == HubPane.Videos ? LoadVideosAsync(1) : LoadDynamicsAsync(true);
            return _profileNotice.Length > 0 && _sessionCts != null
                ? Task.WhenAll(LoadProfileAsync(_sessionCts), content) : content;
        }

        private async Task LoadVideosAsync(int page)
        {
            EnsureSession(); if (_videosBusy || _sessionCts == null) return;
            var owner = _sessionCts; _videosBusy = true; UpdateActions(); SetBrowseNotice(HubPane.Videos, "正在读取第 " + page + " 页投稿…");
            try
            {
                var result = await _api.GetCreatorVideosAsync(_mid, page, owner.Token);
                if (!Current(owner)) return;
                // A queued focus callback belongs to the old page's containers.
                // Invalidate it before recycling those rows, while allowing a
                // different visible tab to retain its own pending focus.
                if (_pane == HubPane.Videos)
                {
                    ++_focusGeneration; _pendingList = null; _pendingIndex = -1;
                    _lastFocus = _videosTab;
                }
                _videos.Clear(); _videos.AddRange(result.Videos); _videoList.Items.Clear();
                foreach (var video in _videos) _videoList.Items.Add(video);
                _videoPage = page; _videoHasMore = result.HasMore; _videosLoaded = true;
                SetBrowseNotice(HubPane.Videos, (_videos.Count == 0 ? "该页暂无可显示的公开投稿。" : "已加载 " + _videos.Count + " 个视频。") + " " + result.Notice);
                _pageLabel.Text = "第 " + _videoPage + " 页";
                if (_pane == HubPane.Videos)
                {
                    if (_videos.Count > 0) FocusItem(_videoList, 0, true);
                    else Focus(_videosTab);
                }
            }
            catch (OperationCanceledException) when (owner.IsCancellationRequested) { }
            catch (Exception ex) { if (Current(owner)) SetBrowseNotice(HubPane.Videos, "投稿加载失败：" + Friendly(ex) + "。可选择刷新重试。"); }
            finally { if (Current(owner)) { _videosBusy = false; UpdateActions(); } }
        }

        private async Task LoadDynamicsAsync(bool reset)
        {
            EnsureSession(); if (_dynamicsBusy || _sessionCts == null || (!reset && !_dynamicHasMore)) return;
            var owner = _sessionCts; string offset = reset ? "" : _dynamicOffset;
            _dynamicsBusy = true; UpdateActions(); SetBrowseNotice(HubPane.Dynamics, "正在读取动态…");
            try
            {
                var result = await _api.GetCreatorDynamicsAsync(_mid, offset, owner.Token);
                if (!Current(owner)) return;
                if (reset) { _dynamics.Clear(); _dynamicList.Items.Clear(); }
                foreach (var item in result.Items)
                {
                    if (_dynamics.Any(existing => existing.Id == item.Id)) continue;
                    _dynamics.Add(item); _dynamicList.Items.Add(BuildDynamicSummary(item));
                }
                _dynamicOffset = result.NextOffset ?? "";
                _dynamicHasMore = result.HasMore && !string.IsNullOrEmpty(_dynamicOffset) && _dynamicOffset != offset;
                _dynamicsLoaded = true;
                SetBrowseNotice(HubPane.Dynamics, (_dynamics.Count == 0 ? "暂未读取到公开动态。" : "已加载 " + _dynamics.Count + " 条动态，按 A 查看全文和评论。") + " " + result.Notice
                    + (result.HasMore && !_dynamicHasMore ? " 平台未提供新的分页位置，可刷新重试。" : ""));
            }
            catch (OperationCanceledException) when (owner.IsCancellationRequested) { }
            catch (Exception ex) { if (Current(owner)) SetBrowseNotice(HubPane.Dynamics, "动态加载失败：" + Friendly(ex) + "。可选择刷新重试。"); }
            finally { if (Current(owner)) { _dynamicsBusy = false; UpdateActions(); } }
        }

        private async Task OpenDynamicAsync(CreatorDynamicItem item)
        {
            EnsureSession(); if (_sessionCts == null || item == null) return;
            _draftState?.DisarmRetry();
            Cancel(ref _replyCts); Cancel(ref _commentCts);
            _commentCts = CancellationTokenSource.CreateLinkedTokenSource(_sessionCts.Token);
            _dynamic = item; _target = item.CommentTarget; _replyRoot = null;
            ResetReplyTarget(); ClearCommentActions();
            _commentCursor = ""; _commentHasMore = false; _commentClosed = false;
            _commentsBusy = _repliesBusy = _posting = false;
            _comments.Clear(); _replies.Clear(); _commentList.Items.Clear(); _replyList.Items.Clear();
            SelectDraft(); _draftState?.DisarmRetry();
            _pane = HubPane.Detail;
            _detailContent.Children.Clear(); AppendDynamic(_detailContent, item, false);
            _detailVideo.Visibility = FindDynamicVideo(item) == null ? Visibility.Collapsed : Visibility.Visible;
            _detailScroll.ChangeView(null, 0, null, true);
            _commentTitle.Text = "动态评论" + (item.CommentCount > 0 ? " · " + item.CommentCount : "");
            _commentHint.Text = "正在读取评论…"; ApplyPane(); Focus(_write.IsEnabled ? _write : _refreshComments);
            await LoadCommentsAsync(true);
        }

        private async Task LoadCommentsAsync(bool reset)
        {
            EnsureSession(); if (_commentsBusy || _commentCts == null || !IsDetail) return;
            if (_target == null || !_target.IsAvailable)
            { _commentHint.Text = "此动态未提供可读取的评论入口。"; UpdateActions(); return; }
            if (!reset && !_commentHasMore) return;
            var owner = _commentCts; var target = _target; string cursor = reset ? "" : _commentCursor;
            _commentsBusy = true; UpdateActions(); _commentHint.Text = "正在读取动态评论…";
            try
            {
                var result = await _api.GetDynamicCommentsAsync(target, cursor, owner.Token);
                if (!CurrentComment(owner, target)) return;
                _commentPages.Accept(result, cursor, reset, _comments);
                if (reset) { ClearCommentActions(); _comments.Clear(); _commentList.Items.Clear(); }
                foreach (var item in result.Items) if (!_comments.Any(existing => existing.Id == item.Id))
                { _comments.Add(item); _commentList.Items.Add(BuildCommentCard(item, true)); }
                _commentCursor = result.NextCursor ?? ""; _commentClosed = result.IsClosed;
                _commentHasMore = result.HasMore && _commentCursor.Length > 0 && _commentCursor != cursor;
                if (_pane == HubPane.Detail)
                {
                    _commentTitle.Text = result.TotalKnown ? "动态评论 · " + result.Total : "动态评论";
                    _commentHint.Text = result.IsClosed ? "此动态已关闭评论。" : (_comments.Count == 0 ? "平台暂未返回评论。" : "已加载 " + _comments.Count + " 条评论。") + " " + result.Notice;
                }
                _moreComments.Content = _commentHasMore ? "加载更多评论" : result.IsPartial ? "刷新评论" : "刷新评论";
            }
            catch (OperationCanceledException) when (owner.IsCancellationRequested) { }
            catch (Exception ex)
            { if (CurrentComment(owner, target)) { if (_pane == HubPane.Detail) _commentHint.Text = "评论加载失败：" + Friendly(ex) + "。可重试，已加载内容保留。"; _moreComments.Content = "重试加载评论"; } }
            finally { if (CurrentComment(owner, target)) { _commentsBusy = false; UpdateActions(); } }
        }

        private async Task OpenRepliesAsync(CommentItem item)
        {
            EnsureSession(); if (_commentCts == null || _target == null || !_target.IsAvailable || item == null) return;
            ClearCommentActions(); ResetReplyTarget();
            Cancel(ref _replyCts); _replyCts = CancellationTokenSource.CreateLinkedTokenSource(_commentCts.Token);
            _replyRoot = item; _replyPage = 0; _replyHasMore = true; _repliesBusy = false;
            _replies.Clear();
            foreach (var reply in item.Replies ?? Array.Empty<CommentItem>()) if (reply.Id != item.Id) _replies.Add(reply);
            RebuildReplyRows();
            _pane = HubPane.Replies; _commentTitle.Text = "评论回复 · " + item.ReplyCount;
            _commentHint.Text = "A 选择图片或赞踩 · X 回复选中的用户 · B 返回";
            ApplyPane(); FocusItem(_replyList, 0, true); await LoadRepliesAsync(true);
        }

        private async Task LoadRepliesAsync(bool reset)
        {
            EnsureSession(); if (_repliesBusy || _replyRoot == null || _target == null || _pane != HubPane.Replies || _commentCts == null) return;
            if (!reset && !_replyHasMore) return;
            if (_replyCts == null) _replyCts = CancellationTokenSource.CreateLinkedTokenSource(_commentCts.Token);
            var owner = _replyCts; var commentOwner = _commentCts; var target = _target; var root = _replyRoot;
            long focusedId = reset ? FocusedCommentCard()?.Item?.Id ?? 0 : 0;
            int page = reset ? 1 : _replyPage + 1; _repliesBusy = true; UpdateActions();
            _commentHint.Text = "正在读取楼中楼回复…";
            try
            {
                var result = await _api.GetDynamicRepliesAsync(target, root.Id, page, owner.Token);
                if (!CurrentComment(commentOwner, target) || owner != _replyCts || owner.IsCancellationRequested || _replyRoot != root || _pane != HubPane.Replies) return;
                if (reset) { _replies.Clear(); ClearCommentActions(); }
                foreach (var item in result.Items) if (!_replies.Any(existing => existing.Id == item.Id))
                {
                    if (item.Id == root.Id) continue;
                    _replies.Add(item);
                    if (!reset) _replyList.Items.Add(BuildCommentCard(item, false, true));
                }
                if (reset)
                {
                    RebuildReplyRows();
                    if (focusedId > 0)
                    {
                        int selected = _replyList.Items.Cast<object>().ToList().FindIndex(row => (row as CommentCard)?.Item?.Id == focusedId);
                        if (selected >= 0) FocusItem(_replyList, selected);
                    }
                }
                _replyPage = page; _replyHasMore = result.HasMore;
                _commentTitle.Text = "评论回复" + (result.TotalKnown ? " · " + result.Total : "");
                _commentHint.Text = "已加载 " + _replies.Count + " 条回复。A 选择操作 · X 回复当前用户。" + result.Notice;
                _moreReplies.Content = _replyHasMore ? "加载更多回复" : "刷新回复";
            }
            catch (OperationCanceledException) when (owner.IsCancellationRequested) { }
            catch (Exception ex)
            { if (CurrentComment(commentOwner, target) && owner == _replyCts) { _commentHint.Text = "回复加载失败：" + Friendly(ex) + "。可重试。"; _moreReplies.Content = "重试加载回复"; } }
            finally { if (owner == _replyCts && CurrentComment(commentOwner, target)) { _repliesBusy = false; UpdateActions(); } }
        }

        public async Task WriteCommentAsync()
        {
            if (_writing || _posting) return;
            EnsureSession();
            var card = FocusedCommentCard();
            if (card != null && !SelectReplyTarget(card.Item)) return;
            ClearCommentActions();
            SelectDraft();
            if (!IsDetail || _target == null || !_target.IsAvailable || !_target.CanPost || _commentClosed || _draftState?.InFlight == true || _writing) return;
            if (!_api.IsLoggedIn) { LoginRequested?.Invoke(this, EventArgs.Empty); return; }
            if (RequestTextInputAsync == null) { _commentHint.Text = "当前未能打开系统键盘，请返回后重试。"; return; }
            var owner = _commentCts; var target = _target; string draftKey = _draftKey;
            bool completed = false;
            _writing = true;
            _draftEditOwner = owner; _draftEditTarget = target; _draftEditKey = draftKey;
            try
            {
                // Renew Xbox's IME engagement at the SAME composer location.
                // An off-tree temporary editor cannot host inline native input.
                _draftEditor = InlineNativeTextInputSession.ReplaceEditor(_draftEditor, 1000);
                AttachDraftEditor();
                UpdateActions();
                completed = await RequestTextInputAsync(_draftEditor, _composeReply == null ? "写动态评论" : "回复 " + _composeReply.Author, 1000);
                if (!CurrentComment(owner, target) || TargetKey(target) != draftKey) return;
                _draftState.Edit(_draftEditor.Text); _draft = _draftEditor.Text;
            }
            finally
            {
                if (CurrentComment(owner, target) && TargetKey(target) == draftKey)
                {
                    _writing = false;
                    _draftEditOwner = null; _draftEditTarget = null; _draftEditKey = null;
                    if (_draftEditor.IsFocusEngaged) _draftEditor.IsFocusEngaged = false;
                    _draftEditor.IsReadOnly = true;
                    UpdateActions();
                    Focus(completed && !string.IsNullOrWhiteSpace(_draft) ? (Control)_send : _draftEditor);
                }
            }
        }

        private void AttachDraftEditor()
        {
            _draftEditor.TextChanged += DraftEditor_TextChanged;
            _draftEditor.Tapped += (sender, args) =>
            {
                if (!_writing && _draftEditor.IsReadOnly && ReferenceEquals(sender, _draftEditor)) Run(WriteCommentAsync);
            };
        }

        private void DraftEditor_TextChanged(object sender, TextChangedEventArgs args)
        {
            if (_updatingDraftEditor || !_writing || !ReferenceEquals(sender, _draftEditor)
                || _draftState == null || !CurrentComment(_draftEditOwner, _draftEditTarget)
                || _draftKey != _draftEditKey || _draftEditKey != TargetKey(_target)) return;
            // Save each edit to the current account/target draft, so B, native
            // keyboard completion and suspension cannot silently discard text.
            _draftState.Edit(_draftEditor.Text); _draft = _draftEditor.Text;
            UpdateActions();
        }

        private async Task PostCommentAsync()
        {
            EnsureSession();
            SelectDraft();
            if (!IsDetail || _draftState?.InFlight == true || _target == null || !_target.IsAvailable || !_target.CanPost || _commentClosed) return;
            if (!_api.IsLoggedIn) { LoginRequested?.Invoke(this, EventArgs.Empty); return; }
            string message = (_draft ?? "").Trim();
            if (message.Length == 0) { _commentHint.Text = "请先选择写评论。"; Focus(_write); return; }
            var state = _draftState;
            if (state.IsUncertain && !state.RetryArmed)
            {
                state.ConfirmRetry();
                _commentHint.Text = "上次提交可能已经成功。请先刷新确认；如仍要重复提交，请再次选择「确认重发」。";
                UpdateActions(); return;
            }
            long attempt = state.Begin(); if (attempt == 0) return;
            var owner = _commentCts; var target = _target; string draftKey = _draftKey;
            long rootRpid = _composeRoot, parentRpid = _composeParent;
            var postScope = CancellationTokenSource.CreateLinkedTokenSource(_sessionCts.Token);
            _posting = true; UpdateActions();
            _commentHint.Text = "正在提交评论，请稍候…";
            try
            {
                await _api.PostDynamicCommentAsync(target, message, rootRpid, parentRpid, postScope.Token);
                state.Complete(attempt, true, "评论已提交，审核可能延迟展示。");
                if (!CurrentComment(owner, target) || TargetKey(target) != draftKey) return;
                SelectDraft();
                if (rootRpid > 0 && _pane == HubPane.Replies && _replyRoot?.Id == rootRpid) await LoadRepliesAsync(true);
                else await LoadCommentsAsync(true);
                if (CurrentComment(owner, target) && TargetKey(target) == draftKey) _commentHint.Text = "评论已提交，审核可能延迟展示。";
            }
            catch (OperationCanceledException) when (postScope.IsCancellationRequested)
            { state.Complete(attempt, false, "上次发布因离开页面而未能确认结果。草稿保留，请刷新确认后再重发。"); }
            catch (Exception ex)
            { state.Complete(attempt, false, "发布结果未确认：" + Friendly(ex) + "。草稿已保留，请刷新确认后再决定是否重新发布。"); }
            finally
            {
                postScope.Dispose();
                if (IsDetail && TargetKey(_target) == draftKey)
                { SelectDraft(); if (state.IsUncertain) _commentHint.Text = state.Notice; UpdateActions(); }
            }
        }

        private void ApplyPane()
        {
            if (!IsDetail) _notice.Text = _pane == HubPane.Videos ? _videoNotice : _dynamicNotice;
            _browse.Visibility = IsDetail ? Visibility.Collapsed : Visibility.Visible;
            _detail.Visibility = IsDetail ? Visibility.Visible : Visibility.Collapsed;
            _videoList.Visibility = _pane == HubPane.Videos ? Visibility.Visible : Visibility.Collapsed;
            _dynamicList.Visibility = _pane == HubPane.Dynamics ? Visibility.Visible : Visibility.Collapsed;
            _commentList.Visibility = _pane == HubPane.Detail ? Visibility.Visible : Visibility.Collapsed;
            _replyList.Visibility = _pane == HubPane.Replies ? Visibility.Visible : Visibility.Collapsed;
            _moreComments.Visibility = _pane == HubPane.Detail ? Visibility.Visible : Visibility.Collapsed;
            _moreReplies.Visibility = _pane == HubPane.Replies ? Visibility.Visible : Visibility.Collapsed;
            _previous.Visibility = _next.Visibility = _pageLabel.Visibility = _pane == HubPane.Videos ? Visibility.Visible : Visibility.Collapsed;
            _moreDynamics.Visibility = _pane == HubPane.Dynamics ? Visibility.Visible : Visibility.Collapsed;
            _back.Content = _pane == HubPane.Replies ? "返回动态评论" : IsDetail ? "返回动态列表" : "返回";
            SetTabAppearance(_videosTab, _pane == HubPane.Videos);
            SetTabAppearance(_dynamicsTab, _pane != HubPane.Videos);
            _hint.Text = _pane == HubPane.Replies ? "方向键选择 · A 选择图片 / 赞踩 · X 回复当前用户 · B 返回评论"
                : IsDetail ? "方向键选择 · A 查看评论和回复 · X 回复当前用户 · B 返回动态" : "左摇杆 / 方向键选择 · A 确认 · B 返回";
            UpdateActions();
        }

        private void UpdateActions()
        {
            SelectDraft();
            _previous.IsEnabled = !_videosBusy && _videoPage > 1;
            _next.IsEnabled = !_videosBusy && _videoHasMore;
            _moreDynamics.IsEnabled = !_dynamicsBusy && _dynamicHasMore;
            _moreDynamics.Content = _dynamicsBusy ? "正在加载…" : _dynamicHasMore ? "加载更多动态" : "当前动态已加载";
            var profileBusy = _profileLoadOwner != null && ReferenceEquals(_profileLoadOwner, _sessionCts);
            _refresh.IsEnabled = IsDetail ? !_commentsBusy && !_repliesBusy
                : !profileBusy && (_pane == HubPane.Videos ? !_videosBusy : !_dynamicsBusy);
            if (!IsDetail && !string.IsNullOrEmpty(_profileNotice))
                _stats.Text = _profileNotice;
            bool available = _target != null && _target.IsAvailable;
            _refreshComments.IsEnabled = available && !_commentsBusy && !_repliesBusy;
            _write.IsEnabled = available && _target.CanPost && !_commentClosed && !_posting && !_writing;
            _send.IsEnabled = _write.IsEnabled && !string.IsNullOrWhiteSpace(_draft);
            _draftEditor.IsEnabled = available && _target.CanPost && !_commentClosed && !_posting;
            _draftEditor.IsReadOnly = !_writing;
            if (!string.Equals(_draftEditor.Text, _draft, StringComparison.Ordinal))
            {
                _updatingDraftEditor = true;
                try { _draftEditor.Text = _draft; }
                finally { _updatingDraftEditor = false; }
            }
            _login.Visibility = IsDetail && _api != null && !_api.IsLoggedIn ? Visibility.Visible : Visibility.Collapsed;
            _moreComments.IsEnabled = available && !_commentsBusy;
            _moreReplies.IsEnabled = available && !_repliesBusy;
            _draftLabel.Text = "原生键盘输入后保留草稿，选择发布才会发送";
            _draftEditor.PlaceholderText = _composeReply == null ? "在这里填写动态评论…" : "回复 @" + _composeReply.Author + "…";
            if (_composeReply != null) _draftLabel.Text = "回复 @" + _composeReply.Author + " · 选择发布才会发送";
            _send.Content = _posting ? "提交中…" : _draftState?.RetryArmed == true ? "确认重发" : _draftState?.IsUncertain == true ? "检查后重发" : "发布";
            if (_draftState?.IsUncertain == true) _draftLabel.Text += " · 上次提交结果未确认";
        }

        private void RequestVideo(VideoItem video)
        { if (video != null && !string.IsNullOrWhiteSpace(video.Bvid)) VideoRequested?.Invoke(this, new CreatorVideoRequestedEventArgs(video)); }

        private CommentCard BuildCommentCard(CommentItem item, bool canReply, bool expanded = false)
        {
            var card = new CommentCard { Item = item, ShowThreadButton = canReply, IsExpanded = expanded, HorizontalAlignment = HorizontalAlignment.Stretch, IsTabStop = false };
            card.RepliesRequested += (s, e) => Run(() => OpenRepliesAsync(e.Comment));
            card.DetailsRequested += (s, e) =>
            { if (canReply) Run(() => OpenRepliesAsync(e.Comment)); else EnterCommentActions(card, true); };
            card.ImageRequested += (s, e) => OpenCommentImage(card, e);
            card.VoteRequested += (s, e) => Run(async () =>
            {
                if (RequestCommentVoteAsync == null || _commentCts == null) return;
                var owner = _commentCts; var target = _target;
                card.SetReactionBusy(true);
                try
                {
                    string notice = await RequestCommentVoteAsync(e.Comment, e.Vote, owner.Token);
                    if (CurrentComment(owner, target)) _commentHint.Text = notice;
                }
                finally { if (CurrentComment(owner, target)) { card.SetReactionBusy(false); card.RefreshReaction(); } }
            });
            return card;
        }

        public void ApplyCommentVote(CommentVoteResult result)
        {
            foreach (var item in _comments) CommentVotePresentation.Apply(item, result);
            foreach (var item in _replies) CommentVotePresentation.Apply(item, result);
            CommentVotePresentation.Apply(_replyRoot, result);
        }

        public void InvalidateCommentVote(CommentItem target, long account)
        {
            foreach (var item in _comments) CommentVotePresentation.Invalidate(item, target, account);
            foreach (var item in _replies) CommentVotePresentation.Invalidate(item, target, account);
            CommentVotePresentation.Invalidate(_replyRoot, target, account);
        }

        public void SetCommentReactionAccount(long account)
        {
            foreach (var item in _comments) CommentVotePresentation.ForAccount(item, account);
            foreach (var item in _replies) CommentVotePresentation.ForAccount(item, account);
            CommentVotePresentation.ForAccount(_replyRoot, account);
        }

        private void SetBrowseNotice(HubPane pane, string text)
        {
            if (pane == HubPane.Videos) _videoNotice = text; else _dynamicNotice = text;
            if (_pane == pane) _notice.Text = text;
        }
        private async void Run(Func<Task> operation)
        {
            var owner = _sessionCts; var commentOwner = _commentCts; var pane = _pane;
            try { await operation(); }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (owner != _sessionCts || pane != _pane || (IsDetail && commentOwner != _commentCts)) return;
                if (IsDetail) _commentHint.Text = "操作失败：" + Friendly(ex);
                else SetBrowseNotice(pane, "操作失败：" + Friendly(ex));
            }
        }
        private static string Friendly(Exception ex) => (ex?.Message ?? "未知错误").Replace("\r", " ").Replace("\n", " ");
        private static void Cancel(ref CancellationTokenSource source) { var previous = source; source = null; if (previous != null) { previous.Cancel(); previous.Dispose(); } }
        private static string Shorten(string text, int count) => string.IsNullOrEmpty(text) || text.Length <= count ? text ?? "" : text.Substring(0, count) + "…";
        private static SolidColorBrush Brush(byte a, byte r, byte g, byte b) => new SolidColorBrush(Color.FromArgb(a, r, g, b));
        private static SolidColorBrush CardBrush() => Brush(255, 28, 28, 30);
        private static SolidColorBrush SecondaryBrush() => Brush(255, 142, 142, 147);
        private static SolidColorBrush SubtleBorderBrush() => Brush(22, 255, 255, 255);
        private static TextBlock Text(string value, double size, bool bold = false) => new TextBlock { Text = value ?? "", FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Foreground = Brush(255, 242, 242, 247) };
        private static void Add(Grid grid, FrameworkElement child, int row, int column) { Grid.SetRow(child, row); Grid.SetColumn(child, column); grid.Children.Add(child); }
        private static Button Button(string text, Action action)
        {
            var button = new Button
            {
                Style = (Style)Application.Current.Resources["TvButtonStyle"], Content = text,
                RequiresPointer = RequiresPointer.Never
            };
            button.Click += (s, e) => action(); return button;
        }
        private static void SetTabAppearance(Button button, bool selected)
        {
            button.Background = selected ? Brush(255, 245, 245, 247) : Brush(31, 255, 255, 255);
            button.Foreground = selected ? Brush(255, 17, 18, 22) : Brush(242, 255, 255, 255);
        }
        private static ListView MakeList()
        {
            var list = new ListView
            {
                SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = true,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(4), IsTabStop = true, RequiresPointer = RequiresPointer.Never
            };
            ScrollViewer.SetHorizontalScrollMode(list, ScrollMode.Disabled);
            ScrollViewer.SetVerticalScrollMode(list, ScrollMode.Enabled);
            ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Hidden);
            return list;
        }
    }
}
