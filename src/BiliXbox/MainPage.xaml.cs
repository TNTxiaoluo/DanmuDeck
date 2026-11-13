using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Controls;
using BiliXbox.Models;
using BiliXbox.Services;
using QRCoder;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace BiliXbox
{
    public sealed partial class MainPage : Page
    {
        public ObservableCollection<VideoItem> Videos { get; } = new ObservableCollection<VideoItem>();
        public ObservableCollection<CommentItem> Comments { get; } = new ObservableCollection<CommentItem>();
        public ObservableCollection<CommentItem> Replies { get; } = new ObservableCollection<CommentItem>();

        private readonly BilibiliClient _api = new BilibiliClient();
        private readonly MediaPlayer _media = new MediaPlayer { AutoPlay = false };
        private readonly DispatcherTimer _clock = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        private readonly InputPane _inputPane;
        private readonly PlayerUiState _playerUi = new PlayerUiState();
        private readonly PlayerInputState _playerInput = new PlayerInputState();
        private readonly PlaybackHealthState _playbackHealth = new PlaybackHealthState();
        private readonly FeedNavigationState _feedNavigation = new FeedNavigationState();
        private CancellationTokenSource _feedCts, _watchCts, _partCts;
        private CancellationTokenSource _replyCts;
        private CancellationTokenSource _qualityCts;
        private string _qualityRequestNotice = "";
        private CommentItem _replyRoot;
        private int _replyPage;
        private bool _viewingReplies, _loadingReplies, _replyHasMore;
        private long _loadedFeedAccountMid = -1;
        private bool _dispatchingPlayerAction;
        private HttpRandomAccessStream _stream;
        private MediaSource _source;
        private ContentDialog _activeLoginDialog, _activeGeneralDialog;
        private NativeTextInputDialog _activeTextDialog;
        private AdaptivePlaybackSource _adaptivePlayback;
        private DownloadSpeedMeter _activeDownloadSpeed;
        private VideoDetail _video;
        private string _requestedBvid, _requestedTitle;
        private int _commentPage, _partGeneration, _busyCount;
        private string _commentCursor = "";
        private readonly CommentPageGuard _commentPages = new CommentPageGuard();
        private CommentPage _lastCommentResult;
        private int _textInputRequest, _preferredQuality = 80;
        private PlaybackInfo _playback;
        private TimeSpan? _restorePosition;
        private bool _resumeAfterOpen = true;
        private DateTimeOffset _lastBack = DateTimeOffset.MinValue;
        private long _currentCid;
        private bool _initialized, _watching, _updatingSlider, _seeking;
        private bool _selectingPart, _loadingComments, _dialogOpen, _disposed, _hasNextFeed = true;
        private bool _commentsNeedRetry, _commentsLoadFailed, _refreshCommentsAfterLoad, _postingComment;
        private bool _inputPaneVisible, _sourceLoading, _sessionBuffering, _wantsPlayback;

        public MainPage()
        {
            InitializeComponent();
            InitializeDanmakuSettings();
            InitializeCreatorHub();
            InitializeAccountLibrary();
            InitializeVideoInteractions();
            InitializeSections();
            _inputPane = InputPane.GetForCurrentView();
            _inputPane.Showing += InputPane_Showing;
            _inputPane.Hiding += InputPane_Hiding;
            Player.SetMediaPlayer(_media);
            Loaded += Page_Loaded;
            Unloaded += Page_Unloaded;
            _clock.Tick += Clock_Tick;
            _media.MediaOpened += Media_Opened;
            _media.MediaFailed += Media_Failed;
            _media.MediaEnded += Media_Ended;
            _media.PlaybackSession.PlaybackStateChanged += Playback_StateChanged;
            _media.PlaybackSession.BufferingStarted += Playback_BufferingStarted;
            _media.PlaybackSession.BufferingEnded += Playback_BufferingEnded;
            RequiresPointer = RequiresPointer.Never;
            DisablePointerCursor();
            SeekSlider.AddHandler(PointerPressedEvent, new PointerEventHandler(Seek_Pressed), true);
            SeekSlider.AddHandler(PointerReleasedEvent, new PointerEventHandler(Seek_Released), true);
            SeekSlider.PointerCaptureLost += Seek_Released;
            WatchView.PreviewKeyDown += Watch_PreviewKeyDown;
            WatchView.PreviewKeyUp += Watch_PreviewKeyUp;
            Root.PreviewKeyDown += Home_PreviewKeyDown;
            Root.PreviewKeyUp += Home_PreviewKeyUp;
            SystemNavigationManager.GetForCurrentView().BackRequested += Back_Requested;
            Window.Current.CoreWindow.KeyDown += Core_KeyDown;
            Window.Current.CoreWindow.KeyUp += Core_KeyUp;
            InitializeGamepadInput();
        }

        private async void Page_Loaded(object sender, RoutedEventArgs e)
        {
            if (_initialized) return;
            _initialized = true;
            _clock.Start();
            StartGamepadInput();
            // Attach tvOS-style composition focus lift to the main video grid.
            TvFocusEffects.AttachCardFocus(VideoGrid);
            TvFocusEffects.AttachCardFocus(UserGrid);
            // Drawers slide in from the right edge.
            TvFocusEffects.AttachShowHide(SettingsSide, 32f, 0f);
            TvFocusEffects.AttachShowHide(DanmakuSettingsSide, 32f, 0f);
            TvFocusEffects.AttachShowHide(CommentSide, 32f, 0f);
            TvFocusEffects.AttachShowHide(ReplySide, 32f, 0f);
            TvFocusEffects.AttachShowHide(InteractionSide, 32f, 0f);
            SetBusy(true);
            try { await _api.InitializeAsync(); UpdateAccount(); }
            catch (Exception ex) { SetStatus("账号恢复失败：" + Friendly(ex)); }
            finally { SetBusy(false); }
            await LoadFeedAsync();
            RestoreHomeGamepadFocus();
        }

        private async Task LoadFeedAsync(bool refreshRecommendations = false)
        {
            if (_liveVisible && !_feedNavigation.IsSearch) { await LoadLiveSectionAsync(); return; }
            Cancel(ref _feedCts);
            var owner = _feedCts = new CancellationTokenSource();
            var token = owner.Token;
            var request = _feedNavigation.BeginRequest();
            SetBusy(true);
            BeginHomeFeedReplacement();
            Videos.Clear();
            Users.Clear();
            _hasNextFeed = false;
            EmptyState.Visibility = Visibility.Visible;
            bool userMode = request.IsSearch && request.Mode == FeedSearchMode.Users;
            UserGrid.Visibility = userMode ? Visibility.Visible : Visibility.Collapsed;
            VideoGrid.Visibility = userMode ? Visibility.Collapsed : Visibility.Visible;
            EmptyState.Text = userMode ? "正在搜索用户…" : "正在加载视频…";
            NextPage.IsEnabled = PreviousPage.IsEnabled = false;
            long accountMid = CurrentAccountMid;
            FeedTitle.Text = request.IsSearch ? (userMode ? "用户" : "相关视频") : SectionTitle(request.Section);
            UpdateSectionButtons();
            UpdateSearchModeUi(request);
            FeedNotice.Text = "";
            FeedNotice.Visibility = Visibility.Collapsed;
            try
            {
                IReadOnlyList<VideoItem> items = Array.Empty<VideoItem>();
                IReadOnlyList<UserSearchItem> users = Array.Empty<UserSearchItem>();
                bool hasMore;
                long total = 0;
                string notice = null;
                if (userMode)
                {
                    var result = await _api.SearchUsersAsync(request.Keyword, request.Page, token);
                    users = result.Users; hasMore = result.HasMore; total = result.Total; notice = result.Notice;
                }
                else if (request.IsSearch)
                {
                    var result = await _api.SearchPageAsync(request.Keyword, request.Page, token);
                    items = result.Videos; hasMore = result.HasMore;
                    notice = result.Notice;
                    total = result.Total;
                }
                else if (request.Section != HomeSection.Recommendations)
                {
                    int region = request.Section == HomeSection.Animation ? 1 : request.Section == HomeSection.Film ? 181 : 0;
                    var result = await _api.GetSectionVideosAsync(region, request.Page, token);
                    items = result.Videos; hasMore = result.HasMore; total = result.Total; notice = result.Notice;
                }
                else
                {
                    var result = await _api.GetRecommendationsAsync(request.Page, refreshRecommendations, token);
                    items = result.Videos; hasMore = result.HasMore; notice = result.Notice;
                    token.ThrowIfCancellationRequested();
                    if (_feedCts != owner || !_feedNavigation.IsCurrent(request)) return;
                    FeedTitle.Text = result.IsPersonalized ? "为你推荐" : result.IsPopularFallback ? "热门推荐" : "推荐";
                    _loadedFeedAccountMid = accountMid;
                }
                token.ThrowIfCancellationRequested();
                if (_feedCts != owner || !_feedNavigation.IsCurrent(request)) return;
                foreach (var item in items) Videos.Add(item);
                foreach (var user in users) Users.Add(user);
                _hasNextFeed = hasMore;
                FeedNotice.Text = notice ?? "";
                FeedNotice.Visibility = string.IsNullOrWhiteSpace(notice) ? Visibility.Collapsed : Visibility.Visible;
                EmptyState.Text = !request.IsSearch ? "此页暂无视频，请稍后刷新或返回上一页。" : userMode
                    ? "没有找到相关用户，请尝试其他昵称。"
                    : "没有找到相关视频。试试其他关键词，或输入 BV 号。";
                int count = userMode ? users.Count : items.Count;
                EmptyState.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
                SetStatus(userMode ? "已找到 " + count + " 位用户" + (total > 0 ? " · 共 " + total + " 位" : "") : "已加载 " + count + " 个视频");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested && _feedCts == owner && _feedNavigation.IsCurrent(request))
                {
                    EmptyState.Text = "加载失败\n" + Friendly(ex) + "\n可按下方「刷新」重试。";
                    SetStatus((userMode ? "用户搜索失败：" : "视频加载失败：") + Friendly(ex));
                }
            }
            finally
            {
                SetBusy(false);
                if (_feedCts == owner && _feedNavigation.IsCurrent(request))
                {
                    PageLabel.Text = "第 " + request.Page + " 页";
                    PreviousPage.IsEnabled = request.Page > 1;
                    NextPage.IsEnabled = _hasNextFeed;
                    CompleteHomeFeedReplacement();
                }
            }
        }

        private void UpdateSearchModeUi(FeedRequest request)
        {
            SearchModes.Visibility = request.IsSearch ? Visibility.Visible : Visibility.Collapsed;
            bool creator = request.Mode == FeedSearchMode.Users;
            var selected = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 251, 114, 153));
            var normal = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 37, 41, 56));
            var darkText = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 17, 19, 29));
            var lightText = new SolidColorBrush(Windows.UI.Colors.White);
            SearchVideosButton.Background = creator ? normal : selected;
            SearchVideosButton.Foreground = creator ? lightText : darkText;
            SearchCreatorButton.Background = creator ? selected : normal;
            SearchCreatorButton.Foreground = creator ? darkText : lightText;
            Windows.UI.Xaml.Automation.AutomationProperties.SetName(SearchVideosButton, "相关视频" + (creator ? "" : "，已选择"));
            Windows.UI.Xaml.Automation.AutomationProperties.SetName(SearchCreatorButton, "用户" + (creator ? "，已选择" : ""));
        }

        private async Task SelectSearchModeAsync(FeedSearchMode mode)
        {
            if (_feedNavigation.SelectSearchMode(mode)) await LoadFeedAsync();
        }

        private async Task SearchAsync()
        {
            var query = SearchInput.Text.Trim();
            if (query.Length == 0) { await ShowSearchInputAsync(); return; }
            var mode = !_watching && !_creatorVisible && !_libraryVisible && _feedNavigation.IsSearch ? _feedNavigation.Mode : FeedSearchMode.Videos;
            HideLiveSection();
            ClearAccountLibraryNavigation();
            ClearCreatorNavigation();
            ++_textInputRequest;
            _inputPane.TryHide();
            var match = Regex.Match(query, @"BV[0-9A-Za-z]{10}", RegexOptions.IgnoreCase);
            if (match.Success && mode == FeedSearchMode.Videos) { await OpenVideoAsync("BV" + match.Value.Substring(2)); return; }
            _feedNavigation.Search(query, mode);
            if (_watching || HomeView.Visibility != Visibility.Visible) GoHome();
            SearchQueryLabel.Text = query;
            await LoadFeedAsync();
        }

        private async Task OpenVideoAsync(string bvid, string title = null, CreatorPlaybackBookmark bookmark = null)
        {
            StopSectionMotion();
            CancelManualGamepadScroll();
            HideLiveSection();
            long previousEpisodeId = _playingEpisodeItem?.EpisodeId ?? 0;
            _playingEpisodeItem = bookmark?.Episode;
            ++_textInputRequest;
            Cancel(ref _watchCts);
            Cancel(ref _partCts);
            Cancel(ref _qualityCts);
            _qualityRequestNotice = "";
            StopMedia();
            _wantsPlayback = _resumeAfterOpen = bookmark?.Playing ?? true;
            _restorePosition = bookmark?.Position;
            var owner = _watchCts = new CancellationTokenSource();
            var token = owner.Token;
            _watching = true;
            if (_requestedBvid != bvid || previousEpisodeId != (_playingEpisodeItem?.EpisodeId ?? 0))
            {
                _requestedTitle = null;
                CommentInput.Text = "";
                DanmakuInput.Text = "";
            }
            _requestedBvid = bvid;
            if (!string.IsNullOrWhiteSpace(title)) _requestedTitle = title;
            _video = null;
            Cancel(ref _replyCts);
            _viewingReplies = _loadingReplies = _replyHasMore = false;
            _replyRoot = null; _replyPage = 0; Replies.Clear(); ReplyRootCard.Item = null;
            _commentActionCard = null; _commentActionReturn = null;
            _replyComposerTarget = null; _replyDrafts.Clear(); ReplyCommentInput.Text = "";
            _currentCid = 0;
            _commentPage = 0;
            _commentCursor = "";
            _lastCommentResult = null;
            _commentsNeedRetry = _commentsLoadFailed = _refreshCommentsAfterLoad = _postingComment = false;
            _loadingComments = false;
            HomeView.Visibility = Visibility.Collapsed;
            WatchView.Visibility = Visibility.Visible;
            Header.Visibility = Footer.Visibility = Visibility.Collapsed;
            Root.Padding = new Thickness(0);
            _playerUi.Reset(DateTimeOffset.UtcNow);
            _playerInput.Reset();
            ApplyPlayerUi();
            PlayerSurface.Focus(FocusState.Programmatic);
            _playback = null;
            QualityOptions.Children.Clear();
            QualityHint.Text = "正在读取此视频可用的画质…";
            QualityButton.Content = "画质 / 分 P";
            VideoTitle.Text = _requestedTitle ?? "正在读取视频…";
            VideoOwner.Text = bvid + " · 正在读取视频信息…";
            VideoOwnerAvatar.Url = "";
            VideoOwnerButton.IsEnabled = false;
            InteractionButton.IsEnabled = false;
            PlaybackError.Text = "";
            ErrorPanel.Visibility = Visibility.Collapsed;
            OfficialVideoButton.Visibility = Visibility.Collapsed;
            _sourceLoading = true;
            UpdatePlaybackUi();
            CommentInput.IsEnabled = SendCommentButton.IsEnabled = false;
            WriteReplyCommentButton.IsEnabled = RefreshCommentsButton.IsEnabled = false;
            CommentLoginButton.Visibility = Visibility.Collapsed;
            CommentBusy.IsActive = false;
            DanmakuInput.IsEnabled = SendDanmakuButton.IsEnabled = false;
            UpdateAccount();
            Comments.Clear();
            CommentTitle.Text = "评论";
            CommentHint.Text = "视频信息就绪后加载评论…";
            MoreCommentsButton.Content = "加载更多评论";
            MoreCommentsButton.IsEnabled = false;
            _selectingPart = true;
            PartSelector.ItemsSource = null;
            PartSelector.Visibility = Visibility.Collapsed;
            _selectingPart = false;
            SetBusy(true);
            try
            {
                var video = _playingEpisodeItem != null ? EpisodeVideoDetail(_playingEpisodeItem) : await _api.GetVideoAsync(bvid, token);
                token.ThrowIfCancellationRequested();
                _video = video;
                _requestedTitle = video.Title;
                VideoTitle.Text = video.Title;
                VideoOwner.Text = (_playingEpisodeItem == null ? "UP · " : "剧集 · ") + video.Owner + "    " + video.PlayText;
                VideoOwnerAvatar.Url = video.OwnerAvatarUrl;
                VideoOwnerButton.IsEnabled = video.OwnerMid > 0;
                InteractionButton.IsEnabled = video.Aid > 0;
                CommentInput.IsEnabled = SendCommentButton.IsEnabled = true;
                WriteReplyCommentButton.IsEnabled = true;
                CommentTitle.Text = video.ReplyCount.HasValue ? "评论 · " + video.ReplyCount.Value : "评论";
                DanmakuInput.IsEnabled = SendDanmakuButton.IsEnabled = true;
                UpdateAccount();
                _selectingPart = true;
                PartSelector.ItemsSource = video.Parts;
                long startCid = bookmark != null && video.Parts.Any(part => part.Cid == bookmark.Cid) ? bookmark.Cid : video.Cid;
                PartSelector.SelectedIndex = Math.Max(0, video.Parts.ToList().FindIndex(part => part.Cid == startCid));
                PartSelector.Visibility = video.Parts != null && video.Parts.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
                SinglePartHint.Visibility = PartSelector.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
                _selectingPart = false;
                _commentPage = 0;
                if (bookmark != null)
                {
                    _restorePosition = bookmark.Position;
                    _wantsPlayback = _resumeAfterOpen = bookmark.Playing && !_libraryVisible && !_creatorVisible;
                }
                await Task.WhenAll(LoadPartAsync(startCid, bookmark?.Quality, bookmark != null), LoadCommentsAsync(true, owner));
                if (!token.IsCancellationRequested && !_creatorVisible && !_libraryVisible && !_dialogOpen && _playerUi.Drawer == PlayerDrawer.None)
                {
                    if (_playerInput.Mode == PlayerInputMode.Controls) FocusPlayerInput();
                    else PlayerSurface.Focus(FocusState.Programmatic);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    VideoTitle.Text = _requestedTitle ?? "视频读取失败";
                    VideoOwner.Text = bvid + " · 视频信息读取失败";
                    PlaybackError.Text = PlaybackFailureText(ex);
                    ErrorPanel.Visibility = Visibility.Visible;
                    ShowControls();
                    OfficialVideoButton.Visibility = Visibility.Visible;
                    CommentHint.Text = "视频信息读取失败。请先重试读取视频，再加载评论。";
                    MoreCommentsButton.Content = "等待视频信息";
                    _sourceLoading = false;
                    UpdatePlaybackUi();
                    SetStatus("视频读取失败：" + Friendly(ex));
                    RetryPlaybackButton.Focus(FocusState.Programmatic);
                }
            }
            finally { SetBusy(false); }
        }

        private async Task LoadPartAsync(long cid, int? quality = null, bool preservePosition = false, PlaybackInfo preparedPlayback = null,
            bool routeRecovery = false)
        {
            if (_video == null || _watchCts == null) return;
            Cancel(ref _qualityCts);
            _qualityRequestNotice = "";
            // A second quality change during loading must retain the first change's resume target.
            TimeSpan position = preservePosition ? _restorePosition ?? _media.PlaybackSession.Position : TimeSpan.Zero;
            bool resume = !_libraryVisible && !_creatorVisible
                && (!preservePosition || (_restorePosition.HasValue ? _resumeAfterOpen : _wantsPlayback));
            if (quality.HasValue) _preferredQuality = quality.Value;
            Cancel(ref _partCts);
            var owner = _partCts = CancellationTokenSource.CreateLinkedTokenSource(_watchCts.Token);
            var token = owner.Token;
            int generation = ++_partGeneration;
            _currentCid = cid;
            StopMedia(false);
            ResetPlaybackRecovery(routeRecovery);
            _restorePosition = position;
            _resumeAfterOpen = resume;
            _wantsPlayback = resume;
            PlaybackError.Text = "";
            ErrorPanel.Visibility = Visibility.Collapsed;
            OfficialVideoButton.Visibility = Visibility.Collapsed;
            _sourceLoading = true;
            var downloadSpeed = _activeDownloadSpeed = new DownloadSpeedMeter();
            UpdatePlaybackUi();
            var video = new VideoDetail { Aid = _video.Aid, Bvid = _video.Bvid, Cid = cid };
            _ = StartDanmakuMaskAsync(video, generation, token);
            var danmakuTask = LoadDanmakuAsync(cid, generation, token);
            HttpRandomAccessStream pendingStream = null;
            AdaptivePlaybackSource pendingAdaptive = null;
            try
            {
                var playback = preparedPlayback ?? await GetCurrentPlaybackAsync(video, _preferredQuality, token);
                token.ThrowIfCancellationRequested();
                playback = await PrepareSourceDisplayAsync(playback, token);
                token.ThrowIfCancellationRequested();
                if (_partCts != owner || generation != _partGeneration || _disposed) return;
                if (!routeRecovery) await PreparePlaybackRoutesAsync(playback, token);
                token.ThrowIfCancellationRequested();
                if (!string.IsNullOrEmpty(playback.DashManifest))
                {
                    try { pendingAdaptive = await AdaptivePlaybackSource.OpenAsync(playback.DashManifest,
                        playback.DashVideoUrls, playback.DashAudioUrls, downloadSpeed, _playbackRouteOffset, token); }
                    catch (Exception) when (!token.IsCancellationRequested && playback.CodecFallback != null)
                    {
                        playback = playback.CodecFallback;
                        playback.QualityNotice = "HEVC 播放源无法打开，已回退到 " + playback.Quality + " H.264 兼容播放。";
                        playback = await PrepareSourceDisplayAsync(playback, token);
                        token.ThrowIfCancellationRequested();
                        await PreparePlaybackRoutesAsync(playback, token);
                        pendingAdaptive = await AdaptivePlaybackSource.OpenAsync(playback.DashManifest,
                            playback.DashVideoUrls, playback.DashAudioUrls, downloadSpeed, _playbackRouteOffset, token);
                    }
                    token.ThrowIfCancellationRequested();
                    if (_partCts != owner || generation != _partGeneration || _disposed) return;
                    _adaptivePlayback = pendingAdaptive;
                    pendingAdaptive = null;
                    _source = _adaptivePlayback.Source;
                }
                else
                {
                    // Only use MP4 URLs provided by the playback API. DASH uses
                    // the native downloader so its audio/video buffers stay in sync.
                    var candidates = Mp4PlaybackUrls(playback);
                    if (candidates.Count == 0) throw new InvalidOperationException("没有可用的播放地址。");
                    var urls = candidates.Skip(_playbackRouteOffset % candidates.Count)
                        .Concat(candidates.Take(_playbackRouteOffset % candidates.Count));
                    Exception lastError = null;
                    foreach (var url in urls)
                    {
                        try
                        {
                            pendingStream = await HttpRandomAccessStream.OpenAsync(new Uri(url), downloadSpeed, token);
                            _playbackRouteOffset = candidates.IndexOf(url);
                            break;
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                        catch (Exception ex) { lastError = ex; }
                    }
                    if (pendingStream == null) throw lastError ?? new InvalidOperationException("没有可用的播放地址。");
                    token.ThrowIfCancellationRequested();
                    if (_partCts != owner || generation != _partGeneration || _disposed) return;
                    _stream = pendingStream;
                    pendingStream = null;
                    _source = MediaSource.CreateFromStream(_stream, "video/mp4");
                }
                _playback = playback;
                UpdateQualities();
                _media.Source = _source;
                // Start only after MediaOpened applies the previous position and pause state.
                SetStatus(string.IsNullOrWhiteSpace(playback.QualityNotice) ? "正在连接 · " + playback.Quality : playback.QualityNotice);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested && _partCts == owner && generation == _partGeneration)
                {
                    await ResetPlaybackDisplayAsync();
                    if (token.IsCancellationRequested || _partCts != owner || generation != _partGeneration || _disposed) return;
                    PlaybackError.Text = PlaybackFailureText(ex);
                    ErrorPanel.Visibility = Visibility.Visible;
                    ShowControls();
                    OfficialVideoButton.Visibility = Visibility.Visible;
                    SetStatus("播放失败：" + Friendly(ex));
                }
            }
            finally
            {
                pendingStream?.Dispose();
                pendingAdaptive?.Dispose();
                if (generation == _partGeneration && _media.Source == null) { _sourceLoading = false; UpdatePlaybackUi(); }
                await danmakuTask;
            }
        }

        private async Task LoadDanmakuAsync(long cid, int generation, CancellationToken token)
        {
            try
            {
                var items = await _api.GetDanmakuAsync(cid, token);
                if (!token.IsCancellationRequested && generation == _partGeneration) Danmaku.Load(items);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested) SetStatus("弹幕加载失败：" + Friendly(ex));
            }
        }

        private async Task LoadCommentsAsync(bool reset, CancellationTokenSource owner = null)
        {
            owner = owner ?? _watchCts;
            if (_video == null || owner == null || owner.IsCancellationRequested || _watchCts != owner) return;
            if (_loadingComments)
            {
                if (reset) _refreshCommentsAfterLoad = true;
                return;
            }
            if (!reset && _commentPage > 0 && string.IsNullOrWhiteSpace(_commentCursor)) return;
            var token = owner.Token;
            long aid = _video.Aid;
            string cursor = reset ? "" : _commentCursor;
            _loadingComments = true;
            MoreCommentsButton.IsEnabled = false;
            RefreshCommentsButton.IsEnabled = false;
            CommentBusy.IsActive = true;
            CommentHint.Text = Comments.Count > 0 ? "正在更新评论…" : "正在加载评论…";
            MoreCommentsButton.Content = "正在加载…";
            try
            {
                var result = await _api.GetCommentsAsync(aid, cursor, token);
                token.ThrowIfCancellationRequested();
                if (_disposed || _watchCts != owner || _video?.Aid != aid) return;
                _commentPages.Accept(result, cursor, reset, Comments);
                if (reset) Comments.Clear();
                foreach (var item in result.Items) if (!Comments.Any(x => x.Id == item.Id)) Comments.Add(item);
                _commentPage = reset ? 1 : _commentPage + 1;
                _commentCursor = result.NextCursor;
                _lastCommentResult = result;
                _commentsLoadFailed = false;
                ApplyCommentPresentation();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested && _watchCts == owner && _video?.Aid == aid)
                {
                    // Retrying a failed later page keeps its original cursor;
                    // only a failed refresh should restart from the first page.
                    _commentsNeedRetry = reset;
                    _commentsLoadFailed = true;
                    CommentHint.Text = "评论未能加载：" + Friendly(ex);
                    MoreCommentsButton.IsEnabled = true;
                    MoreCommentsButton.Content = "重试加载评论";
                    CommentLoginButton.Visibility = _api.IsLoggedIn ? Visibility.Collapsed : Visibility.Visible;
                }
            }
            finally
            {
                if (_watchCts == owner && _video?.Aid == aid)
                {
                    _loadingComments = false;
                    CommentBusy.IsActive = false;
                    RefreshCommentsButton.IsEnabled = true;
                    if (_refreshCommentsAfterLoad)
                    {
                        _refreshCommentsAfterLoad = false;
                        await LoadCommentsAsync(true, owner);
                    }
                }
            }
        }

        private async void CommentReplies_Requested(object sender, CommentRepliesRequestedEventArgs args)
        {
            if (IgnorePlayerClick()) return;
            if (_video == null || _watchCts == null || _watchCts.IsCancellationRequested) return;
            Cancel(ref _replyCts);
            var owner = _replyCts = CancellationTokenSource.CreateLinkedTokenSource(_watchCts.Token);
            _replyRoot = args.Comment; _replyPage = 0; _replyHasMore = true; _loadingReplies = false;
            _commentActionCard = null; _commentActionReturn = null;
            _viewingReplies = true;
            Replies.Clear();
            foreach (var item in _replyRoot.Replies) Replies.Add(item);
            ReplyRootCard.Item = _replyRoot;
            SelectReplyComposerTarget(_replyRoot);
            RepliesTitle.Text = _replyRoot.ReplyCount > 0 ? "回复 · " + _replyRoot.ReplyCount : "回复";
            ApplyPlayerUi();
            FocusGamepadControl(ReplyRootCard);
            await LoadRepliesAsync(owner);
        }

        private async Task LoadRepliesAsync(CancellationTokenSource owner = null)
        {
            owner = owner ?? _replyCts;
            if (owner == null || owner.IsCancellationRequested || owner != _replyCts
                || _replyRoot == null || _video == null || _loadingReplies || !_replyHasMore) return;
            var token = owner.Token;
            long aid = _video.Aid, root = _replyRoot.Id;
            int page = _replyPage + 1;
            _loadingReplies = true;
            RepliesBusy.IsActive = true;
            MoreRepliesButton.IsEnabled = false;
            MoreRepliesButton.Content = "正在加载…";
            RepliesHint.Text = Replies.Count > 0 ? "已有 " + Replies.Count + " 条回复，正在读取…" : "正在加载回复…";
            try
            {
                var result = await _api.GetCommentRepliesAsync(aid, root, page, token);
                token.ThrowIfCancellationRequested();
                if (owner != _replyCts || _video?.Aid != aid || _replyRoot?.Id != root) return;
                if (page == 1 && result.Items.Count == 0 && _replyRoot.ReplyCount > 0)
                    throw new BilibiliApiException(-1, "B站暂未返回回复内容，请稍后重试；这不代表没有回复。");
                if (page > 1 && result.Items.Count > 0 && result.Items.All(item => Replies.Any(existing => existing.Id == item.Id)))
                    throw new BilibiliApiException(-1, "B站未返回新的回复，请稍后重试。");
                if (page == 1) Replies.Clear();
                foreach (var item in result.Items) if (!Replies.Any(existing => existing.Id == item.Id)) Replies.Add(item);
                _replyPage = page; _replyHasMore = result.HasMore;
                if (result.TotalKnown) RepliesTitle.Text = "回复 · " + result.Total;
                RepliesHint.Text = "已加载 " + Replies.Count + " 条回复。" + result.Notice;
                MoreRepliesButton.Content = result.HasMore ? "加载更多回复" : result.IsPartial
                    ? "刷新回复" : result.TotalKnown && Replies.Count >= result.Total ? "已加载全部回复" : "当前可用回复已加载";
                // Partial results can be refreshed; never invent a missing next page.
                MoreRepliesButton.IsEnabled = result.HasMore || result.IsPartial;
                if (result.IsPartial) MoreRepliesButton.Tag = "refresh"; else MoreRepliesButton.Tag = null;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested && owner == _replyCts)
                {
                    RepliesHint.Text = "回复加载失败：" + Friendly(ex)
                        + (page == 1 && Replies.Count > 0 ? " 当前保留回复预览。" : "");
                    MoreRepliesButton.Content = "重试加载回复";
                    MoreRepliesButton.Tag = null;
                    MoreRepliesButton.IsEnabled = true;
                }
            }
            finally
            {
                if (owner == _replyCts) { _loadingReplies = false; RepliesBusy.IsActive = false; }
            }
        }

        private void ReturnToMainComments()
        {
            _commentActionCard = null; _commentActionReturn = null;
            Cancel(ref _replyCts);
            _viewingReplies = _loadingReplies = false;
            RepliesBusy.IsActive = false;
            ApplyPlayerUi();
            int index = _replyRoot == null ? -1 : Comments.IndexOf(_replyRoot);
            if (index >= 0) FocusGamepadListItem(CommentList, index, true);
            else if (!CommentList.Focus(FocusState.Programmatic)) CommentsCloseButton.Focus(FocusState.Programmatic);
        }

        private void RepliesBack_Click(object sender, RoutedEventArgs args) { if (!IgnorePlayerClick()) ReturnToMainComments(); }
        private async void MoreReplies_Click(object sender, RoutedEventArgs args)
        {
            if (IgnorePlayerClick()) return;
            if ((string)MoreRepliesButton.Tag == "refresh")
            { _replyPage = 0; _replyHasMore = true; MoreRepliesButton.Tag = null; }
            await LoadRepliesAsync();
        }

        private void ApplyCommentPresentation()
        {
            if (_lastCommentResult == null || _video == null) return;
            var state = CommentPresentation.Create(_lastCommentResult, Comments.Count, _video.ReplyCount);
            _commentsNeedRetry = state.CanRetry;
            CommentTitle.Text = state.Title;
            CommentHint.Text = state.Hint;
            MoreCommentsButton.Content = state.MoreText;
            MoreCommentsButton.IsEnabled = state.CanLoadMore || state.CanRetry;
            CommentInput.IsEnabled = WriteReplyCommentButton.IsEnabled = state.CanWrite;
            ReplyCommentInput.IsEnabled = state.CanWrite;
            SendReplyCommentButton.IsEnabled = state.CanWrite && !_postingComment;
            SendCommentButton.IsEnabled = state.CanWrite && !_postingComment;
            CommentLoginButton.Visibility = !_api.IsLoggedIn && !_lastCommentResult.IsClosed
                && (_lastCommentResult.IsPartial || _commentsNeedRetry) ? Visibility.Visible : Visibility.Collapsed;
        }

        private async Task SendDanmakuAsync()
        {
            if (_video == null || _currentCid == 0 || !SendDanmakuButton.IsEnabled) return;
            var text = DanmakuInput.Text.Trim();
            if (text.Length == 0) { SetStatus("请输入弹幕内容"); return; }
            if (!_api.IsLoggedIn) { await ShowLoginAsync(); return; }
            if (_media.Source == null) { SetStatus("视频就绪后才能发送弹幕"); return; }
            var owner = _watchCts;
            var token = owner.Token;
            long aid = _video.Aid, cid = _currentCid;
            var position = _media.PlaybackSession.Position;
            SendDanmakuButton.IsEnabled = false;
            try
            {
                await _api.SendDanmakuAsync(aid, cid, text, position, ct: token);
                if (_watchCts == owner && _currentCid == cid && !token.IsCancellationRequested)
                {
                    DanmakuInput.Text = "";
                    Danmaku.Add(new DanmakuItem { Time = _media.PlaybackSession.Position.TotalSeconds, Text = text, Color = 0xFB7299, Mode = 1 });
                    SetStatus("弹幕已提交，展示以 Bilibili 的处理结果为准");
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { SetStatus("弹幕发送未确认：" + Friendly(ex) + "；请确认后再试"); }
            finally { SendDanmakuButton.IsEnabled = true; UpdateAccount(); }
        }

        private async void SendComment_Click(object sender, RoutedEventArgs e)
        {
            if (IgnorePlayerClick()) return;
            if (_video == null || _postingComment || _lastCommentResult?.IsClosed == true) return;
            var text = CommentInput.Text.Trim();
            if (text.Length == 0) { SetStatus("请输入评论内容"); return; }
            if (!_api.IsLoggedIn) { await ShowLoginAsync(); return; }
            var owner = _watchCts;
            var token = owner.Token;
            long aid = _video.Aid;
            _postingComment = true;
            SendCommentButton.IsEnabled = false;
            try
            {
                await _api.PostCommentAsync(aid, text, token);
                if (_watchCts == owner && !token.IsCancellationRequested)
                {
                    CommentInput.Text = "";
                    SetStatus("评论已提交，展示以 Bilibili 的处理结果为准");
                    // Refresh from the server: moderation may delay publication.
                    await LoadCommentsAsync(true, owner);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { SetStatus("评论发布未确认：" + Friendly(ex) + "；请刷新后确认再试"); }
            finally
            {
                if (_watchCts == owner)
                {
                    _postingComment = false;
                    SendCommentButton.IsEnabled = _lastCommentResult?.IsClosed != true;
                    UpdateAccount();
                }
            }
        }

        private async Task ShowLoginAsync()
        {
            if (_disposed || _dialogOpen) return;
            DisablePointerCursor();
            _dialogOpen = true;
            try
            {
                using (var dialog = new AccountLoginDialog(_api, RenderQrAsync))
                {
                    _activeLoginDialog = dialog;
                    dialog.Closed += (sender, args) => _lastBack = DateTimeOffset.UtcNow;
                    await dialog.ShowAsync();
                }
            }
            finally
            {
                _activeLoginDialog = null;
                _dialogOpen = false;
                UpdateAccount();
                RestoreNavigationFocus();
            }
            if (_api.IsLoggedIn)
            {
                SetStatus("登录成功，欢迎 " + (_api.CurrentUser?.Name ?? "回来"));
                await Task.WhenAll(RefreshPlaybackQualitiesAsync(), LoadCommentsAsync(true), RefreshHomeForAccountAsync());
            }
        }

        private static async Task<BitmapImage> RenderQrAsync(string url)
        {
            using (var generator = new QRCodeGenerator())
            using (var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M))
            using (var code = new PngByteQRCode(data))
            using (var memory = new InMemoryRandomAccessStream())
            {
                await memory.WriteAsync(code.GetGraphic(8).AsBuffer());
                memory.Seek(0);
                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(memory);
                return bitmap;
            }
        }

        private async void Account_Click(object sender, RoutedEventArgs e)
        {
            if (IgnorePlayerClick()) return;
            if (_dialogOpen) return;
            if (!_api.IsLoggedIn) { await ShowLoginAsync(); return; }
            await ShowAccountLibraryAsync();
        }

        private void UpdateAccount()
        {
            if (_libraryAccountMid > 0 && _libraryAccountMid != CurrentAccountMid) ClearAccountLibraryNavigation(true);
            AccountButton.Content = _api.IsLoggedIn ? Shorten(_api.CurrentUser?.Name ?? "我的账号", 10) : "登录";
            WatchAccountButton.Content = AccountButton.Content;
            QualityAccountButton.Visibility = _api.IsLoggedIn ? Visibility.Collapsed : Visibility.Visible;
            if (_api.IsLoggedIn) CommentLoginButton.Visibility = Visibility.Collapsed;
            DanmakuInput.PlaceholderText = _watching && _video == null ? "视频信息就绪后可发送弹幕" : _api.IsLoggedIn ? "发一条友善的弹幕…" : "发一条友善的弹幕（需登录）";
            CommentInput.PlaceholderText = _watching && _video == null ? "视频信息就绪后可发布评论" : _api.IsLoggedIn ? "留下你的评论…" : "留下你的评论（需登录）";
            UpdateDanmakuAccount();
            RefreshCommentReactionCards();
        }

        private long CurrentAccountMid => _api.IsLoggedIn ? _api.CurrentUser?.Mid ?? 0 : 0;

        private async Task RefreshHomeForAccountAsync()
        {
            if (_creatorVisible || _libraryVisible || _watching || _liveVisible || _feedNavigation.Section != HomeSection.Recommendations
                || _feedNavigation.IsSearch || _loadedFeedAccountMid == CurrentAccountMid) return;
            _feedNavigation.Recommendations();
            await LoadFeedAsync(true);
        }

        private async Task GoHomeAndRestoreFeedAsync()
        {
            GoHome();
            await RefreshHomeForAccountAsync();
        }

        private async Task RefreshFeedAsync()
        {
            _feedNavigation.Refresh();
            await LoadFeedAsync(!_feedNavigation.IsSearch && _feedNavigation.Section == HomeSection.Recommendations);
        }

        private async void Media_Opened(MediaPlayer sender, object args)
        {
            var source = sender.Source;
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (_disposed || !_watching || source == null || source != _media.Source) return;
                _sourceLoading = false;
                _playbackHealth.Reset();
                PlaybackError.Text = "";
                ErrorPanel.Visibility = Visibility.Collapsed;
                OfficialVideoButton.Visibility = Visibility.Collapsed;
                ApplyRestorePosition();
                if (_libraryVisible || _creatorVisible) _wantsPlayback = _resumeAfterOpen = false;
                if (_resumeAfterOpen) _ = PlayWithDisplayAsync(); else _media.Pause();
                UpdatePlaybackUi();
                _playerUi.Touch(DateTimeOffset.UtcNow);
                if (_playback != null) SetStatus(string.IsNullOrWhiteSpace(_playback.QualityNotice)
                    ? "当前画质 · " + _playback.Quality : _playback.QualityNotice);
            });
        }

        private async void Media_Failed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            var source = sender.Source;
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
            {
                if (_disposed || !_watching || source == null || source != _media.Source) return;
                if (_playback?.CodecFallback != null && _video != null)
                {
                    var fallback = _playback.CodecFallback;
                    _playback.CodecFallback = null;
                    fallback.QualityNotice = "HEVC 解码失败，已回退到 " + fallback.Quality + " H.264 兼容播放。";
                    await LoadPartAsync(_currentCid, fallback.QualityId, true, fallback);
                    return;
                }
                _sourceLoading = _sessionBuffering = _wantsPlayback = false;
                _media.Pause();
                await ResetPlaybackDisplayAsync();
                if (_disposed || !_watching || source != _media.Source) return;
                PlaybackError.Text = "视频播放失败。播放地址可能已过期，或当前编码无法解码。\n请选择「重试」重新加载。";
                ErrorPanel.Visibility = Visibility.Visible;
                ShowControls();
                OfficialVideoButton.Visibility = Visibility.Visible;
                string nativeError = args.ExtendedErrorCode == null ? "" : " · 0x" + args.ExtendedErrorCode.HResult.ToString("X8");
                SetStatus("播放器错误：" + args.Error + nativeError);
                UpdatePlaybackUi();
            });
        }

        private async void Media_Ended(MediaPlayer sender, object args)
        {
            var source = sender.Source;
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
            {
                try
                {
                    if (_disposed || !_watching || source == null || source != _media.Source) return;
                    _wantsPlayback = _resumeAfterOpen = false;
                    _sessionBuffering = false;
                    _media.Pause();
                    await ResetPlaybackDisplayAsync();
                    if (_disposed || !_watching || source != _media.Source) return;
                    UpdatePlaybackUi(); ShowControls(); SetStatus("播放结束，可选择其他分 P 或按 B 返回");
                }
                catch (Exception ex) { if (!_disposed) SetStatus("播放结束：" + Friendly(ex)); }
            });
        }

        private async void Playback_StateChanged(MediaPlaybackSession sender, object args)
        {
            var source = _media.Source;
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (_disposed || !_watching || source == null || source != _media.Source) return;
                UpdatePlaybackUi();
            });
        }
        private async void Playback_BufferingStarted(MediaPlaybackSession sender, object args)
        {
            var source = _media.Source;
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (_disposed || !_watching || source == null || source != _media.Source) return;
                _sessionBuffering = true; UpdatePlaybackUi();
            });
        }
        private async void Playback_BufferingEnded(MediaPlaybackSession sender, object args)
        {
            var source = _media.Source;
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (_disposed || !_watching || source == null || source != _media.Source) return;
                _sessionBuffering = false; _playbackHealth.Reset(); UpdatePlaybackUi();
            });
        }
        private void UpdatePlaybackUi()
        {
            if (_disposed) return;
            var session = _media.PlaybackSession;
            bool playing = _media.Source != null && _wantsPlayback && session.PlaybackState == MediaPlaybackState.Playing;
            bool opening = _sourceLoading || (_media.Source != null && session.PlaybackState == MediaPlaybackState.Opening);
            bool buffering = _wantsPlayback && (_sessionBuffering || session.PlaybackState == MediaPlaybackState.Buffering);
            _playbackHealth.Update(DateTimeOffset.UtcNow, session.Position, playing, opening, buffering);
            bool waiting = _watching && ErrorPanel.Visibility != Visibility.Visible && _playbackHealth.Waiting;
            PlayerBusy.IsActive = waiting;
            BufferingOverlay.Visibility = waiting ? Visibility.Visible : Visibility.Collapsed;
            BufferingLabel.Text = opening ? "正在加载视频…" : "正在缓冲，请稍候…";
            UpdatePlaybackTransport(waiting);
            Danmaku.Update(session.Position, playing && !waiting);
            UpdateDanmakuMask(session.Position);
            PlayPauseButton.Content = _wantsPlayback ? "暂停" : "播放";
        }

        private void Clock_Tick(object sender, object e)
        {
            if (!_watching || _creatorVisible || _libraryVisible) return;
            UpdatePlaybackUi();
            if (_media.Source == null) return;
            ApplyRestorePosition();
            var session = _media.PlaybackSession;
            double duration = session.NaturalDuration.TotalSeconds;
            if (!_seeking)
            {
                _updatingSlider = true;
                SeekSlider.Maximum = Math.Max(1, duration);
                SeekSlider.Value = Math.Min(SeekSlider.Maximum, session.Position.TotalSeconds);
                _updatingSlider = false;
            }
            bool playing = session.PlaybackState == MediaPlaybackState.Playing;
            TimeLabel.Text = (_seeking ? MediaTimeFormatter.Format(SeekSlider.Value) : FormatTime(session.Position)) + " / " + FormatTime(session.NaturalDuration);
            if (_playerUi.CanAutoHide(DateTimeOffset.UtcNow, playing, _dialogOpen || _inputPaneVisible || _seeking || PlayerBusy.IsActive || ErrorPanel.Visibility == Visibility.Visible)) HideControls();
        }

        private void ApplyRestorePosition()
        {
            if (!_restorePosition.HasValue || _media.Source == null) return;
            var session = _media.PlaybackSession;
            if (!session.CanSeek || session.NaturalDuration <= TimeSpan.Zero) return;
            session.Position = TimeSpan.FromSeconds(Math.Max(0, Math.Min(_restorePosition.Value.TotalSeconds, session.NaturalDuration.TotalSeconds)));
            _restorePosition = null;
        }

        private void SeekSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (!_updatingSlider && _media.Source != null && _media.PlaybackSession.CanSeek)
            {
                _media.PlaybackSession.Position = TimeSpan.FromSeconds(e.NewValue);
                TimeLabel.Text = MediaTimeFormatter.Format(e.NewValue) + " / " + FormatTime(_media.PlaybackSession.NaturalDuration);
            }
        }
        private void Seek_Pressed(object sender, PointerRoutedEventArgs e) { _seeking = true; _playerUi.Touch(DateTimeOffset.UtcNow); }
        private void Seek_Released(object sender, PointerRoutedEventArgs e) { _seeking = false; _playerUi.Touch(DateTimeOffset.UtcNow); }
        private void Skip(double seconds)
        {
            if (_media.Source == null || !_media.PlaybackSession.CanSeek) return;
            var session = _media.PlaybackSession;
            session.Position = TimeSpan.FromSeconds(Math.Max(0, Math.Min(session.NaturalDuration.TotalSeconds, session.Position.TotalSeconds + seconds)));
        }
        private async void PlayPause_Click(object sender, RoutedEventArgs e)
        {
            if (IgnorePlayerClick()) return;
            if (_media.Source == null) return;
            _wantsPlayback = !_wantsPlayback;
            _resumeAfterOpen = _wantsPlayback;
            if (_wantsPlayback) await PlayWithDisplayAsync();
            else { CancelDisplayPlayRequest(); _media.Pause(); }
            if (_disposed) return;
            _playbackHealth.Reset(); UpdatePlaybackUi();
            ShowControls();
        }
        private void BackTen_Click(object sender, RoutedEventArgs e) { if (!IgnorePlayerClick()) Skip(-10); }
        private void ForwardTen_Click(object sender, RoutedEventArgs e) { if (!IgnorePlayerClick()) Skip(10); }
        private void DanmakuToggle_Click(object sender, RoutedEventArgs e)
        {
            if (IgnorePlayerClick()) { DanmakuToggle.IsChecked = Danmaku.Visibility == Visibility.Visible; return; }
            bool enabled = DanmakuToggle.IsChecked == true;
            Danmaku.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            DanmakuToggle.Content = enabled ? "弹幕 开" : "弹幕 关";
        }
        private void ShowControls(bool focus = false)
        {
            bool changed = !_playerUi.ControlsVisible;
            _playerUi.Show(DateTimeOffset.UtcNow);
            if (changed) ApplyPlayerUi();
            if (focus && _playerUi.Drawer == PlayerDrawer.None) PlayPauseButton.Focus(FocusState.Programmatic);
        }
        private void HideControls()
        {
            if (!_playerUi.ControlsVisible || _playerUi.Drawer != PlayerDrawer.None) return;
            _playerInput.UsePlayback();
            _playerUi.Hide(); ApplyPlayerUi(); PlayerSurface.Focus(FocusState.Programmatic);
        }
        private void DisablePointerCursor()
        {
            // Application.RequiresPointerMode is configured once in App's
            // constructor. Never write it from page loading, polling or resume:
            // the Xbox runtime can reject late application configuration.
            RequiresPointer = RequiresPointer.Never;
            var frame = Window.Current.Content as Frame;
            if (frame != null) frame.RequiresPointer = RequiresPointer.Never;
            Window.Current.CoreWindow.PointerCursor = null;
        }
        private void RestoreNavigationFocus()
        {
            if (_disposed || _dialogOpen || _inputPaneVisible) return;
            DisablePointerCursor();
            if (_liveVisible) { LiveHub.RestoreFocus(); return; }
            if (_libraryVisible) { AccountLibrary.RestoreFocus(); return; }
            if (_creatorVisible) { CreatorHub.RestoreFocus(); return; }
            if (!_watching) { RestoreHomeGamepadFocus(); return; }
            if (_playerUi.Drawer == PlayerDrawer.Comments)
            {
                if (_viewingReplies) RepliesBackButton.Focus(FocusState.Keyboard);
                else CommentsCloseButton.Focus(FocusState.Keyboard);
            }
            else if (_playerUi.Drawer == PlayerDrawer.Settings) SettingsCloseButton.Focus(FocusState.Keyboard);
            else if (_playerUi.Drawer == PlayerDrawer.DanmakuSettings) DanmakuSettingsCloseButton.Focus(FocusState.Keyboard);
            else if (_playerUi.Drawer == PlayerDrawer.Interactions) InteractionSide.RestoreFocus();
            else if (_playerUi.ControlsVisible)
            {
                if (_playerInput.Mode != PlayerInputMode.Controls) _playerInput.SelectButton(0, PlayerButtons.Length);
                FocusPlayerInput();
            }
            else PlayerSurface.Focus(FocusState.Programmatic);
        }
        private void ApplyPlayerUi()
        {
            PlaybackChrome.Visibility = _playerUi.ControlsVisible ? Visibility.Visible : Visibility.Collapsed;
            UpdatePlayerInputHint();
            SettingsSide.Visibility = _playerUi.Drawer == PlayerDrawer.Settings ? Visibility.Visible : Visibility.Collapsed;
            DanmakuSettingsSide.Visibility = _playerUi.Drawer == PlayerDrawer.DanmakuSettings ? Visibility.Visible : Visibility.Collapsed;
            CommentSide.Visibility = _playerUi.Drawer == PlayerDrawer.Comments && !_viewingReplies ? Visibility.Visible : Visibility.Collapsed;
            ReplySide.Visibility = _playerUi.Drawer == PlayerDrawer.Comments && _viewingReplies ? Visibility.Visible : Visibility.Collapsed;
            InteractionSide.Visibility = _playerUi.Drawer == PlayerDrawer.Interactions ? Visibility.Visible : Visibility.Collapsed;
            if (_playerUi.Drawer != PlayerDrawer.Interactions) InteractionSide.StopRequests();
            DrawerBackdrop.Visibility = _playerUi.Drawer == PlayerDrawer.None ? Visibility.Collapsed : Visibility.Visible;
            // A drawer owns focus; prevent invisible/covered controls from intercepting the D-pad.
            PlaybackChrome.IsHitTestVisible = _playerUi.Drawer == PlayerDrawer.None;
            PlaybackChrome.IsEnabled = _playerUi.Drawer == PlayerDrawer.None;
            PlayerSurface.IsEnabled = _playerUi.Drawer == PlayerDrawer.None;
            UpdateGamepadFocusOutline();
        }
        private void CloseDrawer()
        {
            _commentActionCard = null; _commentActionReturn = null;
            var drawer = _playerUi.Drawer;
            Cancel(ref _replyCts); _viewingReplies = _loadingReplies = false;
            RepliesBusy.IsActive = false;
            _playerUi.Close(DateTimeOffset.UtcNow);
            ApplyPlayerUi();
            if (_playerInput.Mode == PlayerInputMode.Controls)
            {
                var target = drawer == PlayerDrawer.Settings ? QualityButton : drawer == PlayerDrawer.DanmakuSettings ? DanmakuSettingsButton
                    : drawer == PlayerDrawer.Interactions ? InteractionButton : CommentsButton;
                _playerInput.SelectButton(Array.IndexOf(PlayerButtons, target), PlayerButtons.Length);
                FocusPlayerInput();
            }
            else PlayerSurface.Focus(FocusState.Programmatic);
        }
        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            if (IgnorePlayerClick()) return;
            DisablePointerCursor();
            _playerUi.Open(PlayerDrawer.Settings, DateTimeOffset.UtcNow); ApplyPlayerUi();
            SettingsCloseButton.Focus(FocusState.Programmatic);
        }
        private async void Comments_Click(object sender, RoutedEventArgs e)
        {
            if (IgnorePlayerClick()) return;
            _viewingReplies = false;
            DisablePointerCursor();
            _playerUi.Open(PlayerDrawer.Comments, DateTimeOffset.UtcNow); ApplyPlayerUi();
            CommentsCloseButton.Focus(FocusState.Programmatic);
            if (!_loadingComments && (_lastCommentResult == null || _commentsLoadFailed)) await LoadCommentsAsync(true);
        }
        private void DrawerClose_Click(object sender, RoutedEventArgs e) { if (!IgnorePlayerClick()) CloseDrawer(); }
        private void DrawerBackdrop_Tapped(object sender, TappedRoutedEventArgs e) { if (!IgnorePlayerClick()) CloseDrawer(); e.Handled = true; }
        private void HideControls_Click(object sender, RoutedEventArgs e) { if (!IgnorePlayerClick()) HideControls(); }
        private void PlayerSurface_Click(object sender, RoutedEventArgs e) => PlayPause_Click(sender, e);
        private void Home_PreviewKeyDown(object sender, KeyRoutedEventArgs args)
        {
            if ((_watching && !_creatorVisible) || _dialogOpen || _inputPaneVisible || IsNativeGamepadEditorActive) return;
            if (IsAuxiliaryGamepadEvent(args)) { args.Handled = true; return; }
            if (_liveVisible && !GamepadKeyRouting.IsNavigationEvent(args) &&
                (args.Key == VirtualKey.Up || args.Key == VirtualKey.Down || args.Key == VirtualKey.Left || args.Key == VirtualKey.Right
                    || args.Key == VirtualKey.Enter || args.Key == VirtualKey.Space))
            {
                args.Handled = DispatchLiveSectionKey(args.Key, args.KeyStatus.WasKeyDown);
                return;
            }
            // CoreWindow owns virtual navigation; physical pads use the sampler.
            // Neither path should also execute a native GridView action.
            if (GamepadKeyRouting.IsNavigationEvent(args)) args.Handled = true;
        }

        private void Home_PreviewKeyUp(object sender, KeyRoutedEventArgs args)
        {
            if ((_watching && !_creatorVisible) || _dialogOpen || _inputPaneVisible || IsNativeGamepadEditorActive) return;
            if (GamepadKeyRouting.IsNavigationEvent(args)) args.Handled = true;
        }

        private void Watch_PreviewKeyDown(object sender, KeyRoutedEventArgs args)
        {
            if (_dialogOpen || _inputPaneVisible || IsNativeGamepadEditorActive) return;
            if (IsAuxiliaryGamepadEvent(args)) { args.Handled = true; return; }
            if (GamepadKeyRouting.IsNavigationEvent(args)) { args.Handled = true; return; }
            if (PlayerConfirmKey(args.Key) && _playerInput.ConfirmHeld) { args.Handled = true; return; }
            // Tunnel before Button/ToggleButton can turn A into a native Click.
            if (HandlePlayerKey(args.Key, args.KeyStatus.WasKeyDown)) args.Handled = true;
        }

        private void Watch_PreviewKeyUp(object sender, KeyRoutedEventArgs args)
        {
            if (_dialogOpen || _inputPaneVisible || IsNativeGamepadEditorActive) return;
            if (GamepadKeyRouting.IsNavigationEvent(args)) { args.Handled = true; return; }
            if (!PlayerConfirmKey(args.Key)) return;
            bool handled = _playerInput.ConfirmHeld;
            _playerInput.ConfirmKeyUp(DateTimeOffset.UtcNow);
            if (handled) args.Handled = true;
        }

        private Control[] PlayerHeaderButtons => new Control[] { WatchBackButton, VideoOwnerButton, WatchSearchButton, WatchAccountButton };

        private Control[] PlayerButtons => new Control[] { PlayPauseButton, BackTenButton, ForwardTenButton,
            DanmakuToggle, DanmakuEntryButton, CommentsButton, InteractionButton, DanmakuSettingsButton, QualityButton, RetryPlaybackButton, HidePlayerControlsButton };

        private bool IgnorePlayerClick() => !_dispatchingPlayerAction
            && (!_playerInput.AcceptClick(DateTimeOffset.UtcNow) || _rawGamepadInput.SuppressesClick(_gamepadClock.Elapsed));

        private void UpdatePlayerInputHint()
        {
            PlayerSurface.IsTabStop = false;
            PlayerInputHint.Text = _playerInput.Mode == PlayerInputMode.Playback
                ? "A 播放 / 暂停    左摇杆 / 方向键选择    B 返回    X 发弹幕    Y 搜索"
                : "左摇杆 / 方向键选择 · A 确认    ↑ 进度条 / 顶栏    ↓ 返回    B 隐藏 / 返回";
        }

        private void FocusPlayerInput()
        {
            UpdatePlayerInputHint();
            DisablePointerCursor();
            if (_playerInput.Row == PlayerFocusRow.Seek) SeekSlider.Focus(FocusState.Keyboard);
            else if (_playerInput.Row == PlayerFocusRow.Header) PlayerHeaderButtons[_playerInput.HeaderIndex].Focus(FocusState.Keyboard);
            else PlayerButtons[_playerInput.ButtonIndex].Focus(FocusState.Keyboard);
            UpdateGamepadFocusOutline();
        }

        private static bool PlayerDirectionKey(VirtualKey key, out PlayerDirection direction)
        {
            direction = PlayerDirection.Left;
            switch (key)
            {
                case VirtualKey.Left: case VirtualKey.GamepadDPadLeft: case VirtualKey.GamepadLeftThumbstickLeft: case VirtualKey.NavigationLeft: return true;
                case VirtualKey.Right: case VirtualKey.GamepadDPadRight: case VirtualKey.GamepadLeftThumbstickRight: case VirtualKey.NavigationRight: direction = PlayerDirection.Right; return true;
                case VirtualKey.Up: case VirtualKey.GamepadDPadUp: case VirtualKey.GamepadLeftThumbstickUp: case VirtualKey.NavigationUp: direction = PlayerDirection.Up; return true;
                case VirtualKey.Down: case VirtualKey.GamepadDPadDown: case VirtualKey.GamepadLeftThumbstickDown: case VirtualKey.NavigationDown: direction = PlayerDirection.Down; return true;
                default: return false;
            }
        }

        private static bool PlayerConfirmKey(VirtualKey key) => key == VirtualKey.GamepadA || key == VirtualKey.NavigationAccept || key == VirtualKey.Enter || key == VirtualKey.Space;

        private bool HandlePlayerKey(VirtualKey key, bool repeat)
        {
            if (!_watching || _libraryVisible || _dialogOpen || _inputPaneVisible || _playerUi.Drawer != PlayerDrawer.None) return false;
            var now = DateTimeOffset.UtcNow;
            PlayerDirection direction;
            if (PlayerDirectionKey(key, out direction))
            {
                ShowControls();
                var navigation = _playerInput.Navigate(direction, PlayerButtons.Length, PlayerHeaderButtons.Length);
                _playerUi.Touch(now);
                if (navigation.SeekSeconds != 0) Skip(navigation.SeekSeconds);
                FocusPlayerInput();
                return true;
            }
            if (!PlayerConfirmKey(key)) return false;
            if (!_playerInput.ConfirmKeyDown(now, repeat)) return true;
            _playerUi.Touch(now);
            _dispatchingPlayerAction = true;
            try
            {
                if (_playerInput.Mode == PlayerInputMode.Controls)
                {
                    if (_playerInput.Row == PlayerFocusRow.Seek)
                    { _playerInput.SelectButton(_playerInput.ButtonIndex, PlayerButtons.Length); FocusPlayerInput(); }
                    else if (_playerInput.Row == PlayerFocusRow.Header) ActivatePlayerButton(PlayerHeaderButtons[_playerInput.HeaderIndex]);
                    else ActivatePlayerButton(PlayerButtons[_playerInput.ButtonIndex]);
                }
                else
                {
                    PlayPause_Click(PlayPauseButton, new RoutedEventArgs());
                    _playerInput.SelectButton(0, PlayerButtons.Length);
                    FocusPlayerInput();
                }
            }
            finally { _dispatchingPlayerAction = false; }
            return true;
        }

        private void ActivatePlayerButton(Control button)
        {
            var args = new RoutedEventArgs();
            if (button == PlayPauseButton || button == PlayerSurface) PlayPause_Click(button, args);
            else if (button == BackTenButton) BackTen_Click(button, args);
            else if (button == ForwardTenButton) ForwardTen_Click(button, args);
            else if (button == DanmakuToggle) { DanmakuToggle.IsChecked = DanmakuToggle.IsChecked != true; DanmakuToggle_Click(button, args); }
            else if (button == DanmakuEntryButton) DanmakuEntry_Click(button, args);
            else if (button == CommentsButton) Comments_Click(button, args);
            else if (button == InteractionButton) Interactions_Click(button, args);
            else if (button == DanmakuSettingsButton) DanmakuSettings_Click(button, args);
            else if (button == QualityButton) Settings_Click(button, args);
            else if (button == RetryPlaybackButton || button == ErrorRetryButton) RetryPlayback_Click(button, args);
            else if (button == HidePlayerControlsButton) HideControls();
            else if (button == WatchBackButton) BackToFeed_Click(button, args);
            else if (button == VideoOwnerButton) VideoCreator_Click(button, args);
            else if (button == WatchSearchButton) SearchFocus_Click(button, args);
            else if (button == WatchAccountButton) Account_Click(button, args);
            else if (button == OfficialVideoButton) OfficialVideo_Click(button, args);
        }
        private void UpdateQualities()
        {
            var previousQuality = (FocusManager.GetFocusedElement() as Button)?.Tag as int?;
            QualityOptions.Children.Clear();
            if (_playback == null) return;
            QualityButton.Content = _playback.Quality;
            string membership = !_api.IsLoggedIn ? "未登录" : _api.CurrentUser?.IsPremium == true ? "大会员已识别" : "已登录，未识别大会员";
            string dimensions = _playback.VideoWidth > 0 && _playback.VideoHeight > 0
                ? " · " + _playback.VideoWidth + "×" + _playback.VideoHeight : "";
            string codec = _playback.VideoCodec ?? "";
            string codecName = codec.StartsWith("avc1", StringComparison.OrdinalIgnoreCase) ? "H.264"
                : codec.StartsWith("hev1", StringComparison.OrdinalIgnoreCase) || codec.StartsWith("hvc1", StringComparison.OrdinalIgnoreCase) ? "HEVC" : "";
            var notice = string.IsNullOrWhiteSpace(_qualityRequestNotice) ? _playback.QualityNotice : _qualityRequestNotice;
            QualityHint.Text = "当前：" + _playback.Quality + dimensions + (codecName.Length > 0 ? " · " + codecName : "")
                + "\n" + membership
                + (_playback.VideoFrameRate > 0 ? " · " + _playback.VideoFrameRate.ToString("0.##") + " 帧/秒" : "")
                + (_playback.VideoBitrate > 0 ? " · " + (_playback.VideoBitrate / 1000000.0).ToString("0.##") + " Mbps" : "")
                + (_playback.HardwareHevcVerified ? "\n已通过此分辨率的硬件 HEVC 能力查询" : "")
                + (_playback.FourKDisplayVerified ? "\n已通过此分辨率的 4K 显示能力查询" : "")
                + (_playback.IsHdr10 ? "\n视频源：HDR10" : "")
                + (_playbackDisplay?.IsXbox == true && _playbackDisplay.Hdr10Active ? " · HDMI HDR10 输出已启用" : "")
                + (_playbackDisplay?.IsXbox == true && _playbackDisplay.FourKActive ? "\nHDMI 4K 输出已启用" : "")
                + (string.IsNullOrWhiteSpace(_playback.CapabilityDiagnostic) ? "" : "\n" + _playback.CapabilityDiagnostic)
                + (string.IsNullOrWhiteSpace(notice) ? "" : "\n" + notice);
            foreach (var quality in _playback.AvailableQualities.Where(item => item.IsVisible))
            {
                var button = new Button
                {
                    Style = (Style)Application.Current.Resources["TvRowButtonStyle"],
                    Content = new TextBlock { Text = (_playback.QualityId == quality.Id ? "✓  " : "     ") + quality.Label
                        + (!quality.IsAvailable ? " · " + quality.Reason : quality.IsProbe ? " · 点击获取" : ""), TextWrapping = TextWrapping.Wrap, MaxWidth = 305 },
                    Tag = quality.Id, IsEnabled = quality.IsAvailable,
                    HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
                    FontSize = 18, Padding = new Thickness(16, 12, 16, 12)
                };
                button.Click += Quality_Click;
                QualityOptions.Children.Add(button);
            }
            if (previousQuality.HasValue && _playerUi.Drawer == PlayerDrawer.Settings && _playerInput.Mode == PlayerInputMode.Controls)
                (QualityOptions.Children.OfType<Button>().FirstOrDefault(button => (int)button.Tag == previousQuality.Value))?.Focus(FocusState.Keyboard);
            UpdateGamepadFocusOutline();
        }
        private async Task RefreshPlaybackQualitiesAsync()
        {
            if (!_watching || _video == null || _playback == null || _watchCts == null) return;
            var current = _playback;
            int generation = _partGeneration;
            var token = _watchCts.Token;
            var video = new VideoDetail { Bvid = _video.Bvid, Aid = _video.Aid, Cid = _currentCid };
            try
            {
                var latest = await GetCurrentPlaybackAsync(video, current.QualityId, token);
                if (token.IsCancellationRequested || generation != _partGeneration || _playback != current) return;
                // Refresh permissions without replacing the playing stream or its actual resolution.
                MergePlaybackQualities(current, latest);
                UpdateQualities();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { if (!token.IsCancellationRequested) SetStatus("画质列表刷新失败，可选择「重试」：" + Friendly(ex)); }
        }
        private async void Quality_Click(object sender, RoutedEventArgs e)
        {
            if (IgnorePlayerClick() || _video == null || _watchCts == null) return;
            int quality = (int)((Button)sender).Tag;
            if (_playback != null && quality == _playback.QualityId) { CloseDrawer(); return; }
            Cancel(ref _qualityCts);
            var owner = _qualityCts = CancellationTokenSource.CreateLinkedTokenSource(_watchCts.Token);
            var token = owner.Token;
            var current = _playback;
            var video = new VideoDetail { Bvid = _video.Bvid, Aid = _video.Aid, Cid = _currentCid };
            int generation = _partGeneration;
            string target = current?.AvailableQualities.FirstOrDefault(item => item.Id == quality)?.Label ?? "所选画质";
            QualityHint.Text = "正在请求 " + target + "，当前视频继续播放…";
            SetBusy(true);
            try
            {
                var received = await GetCurrentPlaybackAsync(video, quality, token);
                token.ThrowIfCancellationRequested();
                if (_qualityCts != owner || _video?.Aid != video.Aid || _currentCid != video.Cid || _partGeneration != generation) return;
                if (received.QualityId != quality && current != null && _media.Source != null)
                {
                    MergePlaybackQualities(current, received);
                    _qualityRequestNotice = "已请求 " + target + "，B站本次返回 " + received.Quality + "。继续播放当前 " + current.Quality + "。";
                    UpdateQualities(); SetStatus(_qualityRequestNotice);
                    return;
                }
                CloseDrawer();
                await LoadPartAsync(video.Cid, quality, true, received);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (_qualityCts == owner && !token.IsCancellationRequested)
                {
                    _qualityRequestNotice = "画质请求失败：" + Friendly(ex) + "。当前播放保持不变。";
                    UpdateQualities(); SetStatus(_qualityRequestNotice);
                }
            }
            finally { if (_qualityCts == owner) Cancel(ref _qualityCts); SetBusy(false); }
        }

        private void GoHome(bool loadLive = true)
        {
            HideLiveSection();
            Cancel(ref _qualityCts);
            ++_textInputRequest;
            DisablePointerCursor();
            _playerUi.Reset(DateTimeOffset.UtcNow);
            _playerInput.Reset();
            Cancel(ref _replyCts);
            _viewingReplies = _loadingReplies = false;
            ApplyPlayerUi();
            Header.Visibility = Footer.Visibility = Visibility.Visible;
            Root.Padding = new Thickness(52, 28, 52, 20);
            _watching = false;
            _playingEpisodeItem = null;
            ++_partGeneration;
            Cancel(ref _watchCts);
            Cancel(ref _partCts);
            StopMedia();
            HomeView.Visibility = Visibility.Visible;
            WatchView.Visibility = Visibility.Collapsed;
            UpdateSectionButtons();
            if (_feedNavigation.Section == HomeSection.Live && !_feedNavigation.IsSearch)
            { ShowLiveSectionSurface(); if (loadLive) _ = LoadLiveSectionAsync(); return; }
            if (!HomeResultGrid.Focus(FocusState.Programmatic)) HomeButton.Focus(FocusState.Programmatic);
            RestoreHomeGamepadFocus();
        }
        private async Task ShowRecommendationsAsync()
        {
            if (_disposed) return;
            ClearAccountLibraryNavigation();
            ClearCreatorNavigation();
            // The recommendation action changes the feed, whereas leaving a
            // video restores whichever search/recommendation list opened it.
            _feedNavigation.Recommendations();
            GoHome();
            SearchInput.Text = "";
            SearchQueryLabel.Text = "搜索视频或用户，支持 BV 号 / 视频链接";
            HomeButton.Focus(FocusState.Programmatic);
            await LoadFeedAsync(true);
        }
        private void StopMedia(bool resetDisplay = true)
        {
            CancelDisplayPlayRequest();
            _media.Pause();
            _sourceLoading = _sessionBuffering = _wantsPlayback = false;
            _activeDownloadSpeed = null;
            ResetPlaybackTransportDisplay();
            _playbackHealth.Reset();
            PlayerBusy.IsActive = false;
            BufferingOverlay.Visibility = Visibility.Collapsed;
            _restorePosition = null;
            _media.Source = null;
            if (_adaptivePlayback != null) { _adaptivePlayback.Dispose(); _adaptivePlayback = null; }
            else _source?.Dispose();
            _source = null;
            _stream?.Dispose(); _stream = null;
            Danmaku.Reset();
            StopDanmakuMask();
            _updatingSlider = true; SeekSlider.Value = 0; _updatingSlider = false;
            TimeLabel.Text = "0:00 / 0:00";
            if (resetDisplay) _ = ResetPlaybackDisplayAsync();
        }
        public void SuspendPlayback() { DisposeCommentImages(); StopSectionMotion(); SuspendVideoInteractions(); StopCommentReactions(); LiveHub.StopRequests(); Cancel(ref _liveCts); ++_textInputRequest; _activeInlineTextInput?.Cancel(); AccountLibrary.StopRequests(); CreatorHub.StopRequests(); SuspendGamepadInput(); Cancel(ref _qualityCts); _playerInput.Reset(); _resumeAfterOpen = _wantsPlayback = false; _media.Pause(); DisablePointerCursor(); (_activeLoginDialog as AccountLoginDialog)?.Dispose(); (_activeGeneralDialog as CommentReaderDialog)?.Dispose(); _activeLoginDialog?.Hide(); _activeTextDialog?.Hide(); _activeGeneralDialog?.Hide(); }

        private async Task ShowSearchInputAsync()
        {
            try { await ShowInlineSearchAsync(); }
            catch (Exception ex) { if (!_disposed) SetStatus("搜索输入未能打开：" + Friendly(ex)); }
        }

        private async Task<bool> ShowTextInputAsync(TextBox input, string title, string primary, int maxLength)
        {
            if (_disposed || _dialogOpen || !input.IsEnabled) return false;
            ++_textInputRequest;
            _dialogOpen = true;
            _inputPane.TryHide();
            DisablePointerCursor();
            var dialog = new NativeTextInputDialog(input.Text, title, primary, maxLength);
            dialog.Closed += (sender, args) => _lastBack = DateTimeOffset.UtcNow;
            _activeTextDialog = dialog;
            try
            {
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;
                input.Text = dialog.QueryText;
                return true;
            }
            finally
            {
                _activeTextDialog = null;
                _dialogOpen = false;
                _inputPane.TryHide();
                _inputPaneVisible = false;
                DisablePointerCursor();
                if (_creatorVisible) CreatorHub.RestoreFocus();
                else if (_watching)
                {
                    ShowControls();
                    if (_playerUi.Drawer == PlayerDrawer.Comments)
                    { if (_viewingReplies) RepliesBackButton.Focus(FocusState.Programmatic); else CommentsCloseButton.Focus(FocusState.Programmatic); }
                    else if (_playerUi.Drawer == PlayerDrawer.Settings) SettingsCloseButton.Focus(FocusState.Programmatic);
                    else if (_playerUi.Drawer == PlayerDrawer.Interactions) InteractionSide.RestoreFocus();
                    else
                    {
                        _playerInput.SelectButton(0, PlayerButtons.Length);
                        FocusPlayerInput();
                    }
                }
                else SearchEntryButton.Focus(FocusState.Programmatic);
            }
        }

        private async void DanmakuEntry_Click(object sender, RoutedEventArgs e)
        { if (!IgnorePlayerClick() && await ShowTextInputAsync(DanmakuInput, "发送弹幕", "发送", 100)) await SendDanmakuAsync(); }
        private async void CommentEntry_Click(object sender, RoutedEventArgs e)
        {
            if (IgnorePlayerClick()) return;
            RememberCommentComposerPosition(FocusManager.GetFocusedElement() as Control);
            bool thread = _viewingReplies;
            if (thread && _replyComposerTarget == null) SelectReplyComposerTarget(_replyRoot);
            var editor = thread ? ReplyCommentInput : CommentInput;
            if (_dialogOpen || !editor.IsEnabled) return;
            try
            {
                editor = InlineNativeTextInputSession.ReplaceEditor(editor, 1000);
                if (thread) ReplyCommentInput = editor; else CommentInput = editor;
                editor.Tapped += CommentInput_Tapped;
                await ShowInlineTextInputAsync(editor, false);
            }
            catch (Exception ex) { if (!_disposed) SetStatus("评论输入未能打开：" + Friendly(ex)); }
            if (!_disposed && _watching && _playerUi.Drawer == PlayerDrawer.Comments)
            {
                if (!string.IsNullOrWhiteSpace(editor.Text)) FocusGamepadControl(thread ? SendReplyCommentButton : SendCommentButton);
                else RestoreCommentComposerPosition();
            }
        }

        private void InputPane_Showing(InputPane sender, InputPaneVisibilityEventArgs args) { _inputPaneVisible = true; }
        private void InputPane_Hiding(InputPane sender, InputPaneVisibilityEventArgs args) { _inputPaneVisible = false; }

        private bool LeaveTextInput()
        {
            var input = FocusManager.GetFocusedElement() as TextBox;
            if (!_inputPaneVisible && input == null) return false;
            _inputPane.TryHide();
            _inputPaneVisible = false;
            if (input != null && input.IsFocusEngaged) input.IsFocusEngaged = false;
            if (_liveVisible) LiveHub.RestoreFocus();
            else if (_creatorVisible) CreatorHub.RestoreFocus();
            else if (_watching && _playerUi.Drawer == PlayerDrawer.Comments)
            { if (_viewingReplies) RepliesBackButton.Focus(FocusState.Programmatic); else CommentsCloseButton.Focus(FocusState.Programmatic); }
            else if (_watching) PlayPauseButton.Focus(FocusState.Programmatic);
            else HomeButton.Focus(FocusState.Programmatic);
            return true;
        }

        private async void Back_Requested(object sender, BackRequestedEventArgs e)
        {
            if (_disposed) return;
            if (!_dialogOpen && PartSelector.IsDropDownOpen) { PartSelector.IsDropDownOpen = false; e.Handled = true; return; }
            if (_dialogOpen)
            {
                e.Handled = true;
                _lastBack = DateTimeOffset.UtcNow;
                if (_commentImages?.IsOpen == true) _commentImages.RequestBack();
                else if (_creatorVisible && CreatorHub.IsImageOpen) CreatorHub.RequestImageBack();
                else if (_activeInlineTextInput != null) _activeInlineTextInput.RequestBack();
                else if (_activeTextDialog != null) _activeTextDialog.RequestBack();
                else if (_activeLoginDialog is AccountLoginDialog accountLogin) accountLogin.RequestBack();
                else if (_activeGeneralDialog is CommentReaderDialog commentReader) commentReader.RequestBack();
                else { _activeLoginDialog?.Hide(); _activeGeneralDialog?.Hide(); }
                return;
            }
            // Claim navigation before the awaited request; otherwise Xbox may
            // perform its own back navigation while the feed is being loaded.
            e.Handled = true;
            e.Handled = await HandleBackAsync();
        }
        private async Task<bool> HandleBackAsync()
        {
            var now = DateTimeOffset.UtcNow;
            // Some Xbox shells deliver the same B to both CoreWindow and system back.
            if (now - _lastBack < TimeSpan.FromMilliseconds(250)) return true;
            _lastBack = now;
            ++_textInputRequest;
            if (LeaveTextInput()) return true;
            if (_liveVisible)
            {
                if (!LiveHub.Back()) await ShowRecommendationsAsync();
                return true;
            }
            if (_libraryVisible)
            {
                if (!AccountLibrary.Back()) await CloseAccountLibraryAsync();
                return true;
            }
            if (_creatorVisible)
            {
                if (!CreatorHub.Back()) await CloseCreatorAsync();
                return true;
            }
            if (!_watching)
            {
                if (!_feedNavigation.CanReturnToRecommendations) return false;
                await ShowRecommendationsAsync();
                return true;
            }
            if (_playerUi.Drawer == PlayerDrawer.Comments && ExitCommentActions()) return true;
            if (_playerUi.Drawer == PlayerDrawer.Comments && _viewingReplies) { ReturnToMainComments(); return true; }
            if (_playerUi.Drawer == PlayerDrawer.Interactions && InteractionSide.Back()) return true;
            if (_playerUi.Drawer != PlayerDrawer.None) { CloseDrawer(); return true; }
            var action = _playerUi.Back(now);
            if (action == PlayerBackAction.LeaveVideo)
            {
                if (_videoReturnsToCreator) ReturnToCreatorFromVideo();
                else if (_videoReturnsToLibrary) ReturnToAccountLibraryFromVideo();
                else await GoHomeAndRestoreFeedAsync();
            }
            else
            {
                _playerInput.UsePlayback();
                ApplyPlayerUi();
                if (action == PlayerBackAction.CloseDrawer) PlayPauseButton.Focus(FocusState.Programmatic);
                else PlayerSurface.Focus(FocusState.Programmatic);
            }
            return true;
        }
        private async void Core_KeyDown(CoreWindow sender, KeyEventArgs e)
        {
            if (_dialogOpen || _disposed) return;
            // Leave B and the other gamepad buttons to the native keyboard while it is open.
            if (_inputPaneVisible || IsNativeGamepadEditorActive) return;
            if (HandleAuxiliaryGamepadKey(e.VirtualKey, e.KeyStatus.WasKeyDown)) { e.Handled = true; return; }
            if (IsGamepadNavigationKey(e.VirtualKey))
            {
                if (RawGamepadOwnsNavigation) { e.Handled = true; return; }
                if (HandleUnroutedGamepadKey(e.VirtualKey, e.KeyStatus.WasKeyDown)) { e.Handled = true; return; }
            }
            if (e.VirtualKey == VirtualKey.GamepadB || e.VirtualKey == VirtualKey.Escape)
            {
                e.Handled = true;
                e.Handled = await HandleBackAsync();
            }
            else if (e.VirtualKey == VirtualKey.GamepadY || e.VirtualKey == VirtualKey.F3)
            {
                e.Handled = true;
                await ShowSearchInputAsync();
            }
            else if (_libraryVisible) return;
            else if (e.VirtualKey == VirtualKey.GamepadX && (_watching || _creatorVisible))
            {
                e.Handled = true;
                if (_creatorVisible) await CreatorHub.WriteCommentAsync();
                else if (!TryOpenCommentComposer()) DanmakuEntry_Click(sender, new RoutedEventArgs());
            }
            else if (e.VirtualKey == VirtualKey.GamepadLeftShoulder && _watching && !_creatorVisible) { ShowControls(); Skip(-10); e.Handled = true; }
            else if (e.VirtualKey == VirtualKey.GamepadRightShoulder && _watching && !_creatorVisible) { ShowControls(); Skip(10); e.Handled = true; }
            else if (_watching && !_creatorVisible) _playerUi.Touch(DateTimeOffset.UtcNow);
        }

        private void Core_KeyUp(CoreWindow sender, KeyEventArgs args)
        {
            if (RawGamepadOwnsNavigation && IsGamepadNavigationKey(args.VirtualKey)) { args.Handled = true; return; }
            if (PlayerConfirmKey(args.VirtualKey)) _playerInput.ConfirmKeyUp(DateTimeOffset.UtcNow);
        }

        private async void Video_ItemClick(object sender, ItemClickEventArgs e) { if (!IgnorePlayerClick() && e.ClickedItem is VideoItem item) await OpenVideoAsync(item.Bvid, item.Title); }
        private async void Home_Click(object sender, RoutedEventArgs e) { if (!IgnorePlayerClick()) await SelectSectionAsync(HomeSection.Recommendations); }
        private async void BackToFeed_Click(object sender, RoutedEventArgs e)
        {
            if (IgnorePlayerClick()) return;
            if (_videoReturnsToCreator) ReturnToCreatorFromVideo();
            else if (_videoReturnsToLibrary) ReturnToAccountLibraryFromVideo();
            else await GoHomeAndRestoreFeedAsync();
        }
        private async void SearchFocus_Click(object sender, RoutedEventArgs e) { if (!IgnorePlayerClick()) await ShowSearchInputAsync(); }
        private async void Search_Click(object sender, RoutedEventArgs e)
        {
            if (_activeInlineTextInput != null && ReferenceEquals(_inlineTextEditor, SearchInput))
            { _activeInlineTextInput.Complete(true); return; }
            if (IgnorePlayerClick()) return;
            if (!string.IsNullOrWhiteSpace(SearchInput.Text)) await SearchAsync();
            else await ShowSearchInputAsync();
        }
        private async void SearchVideos_Click(object sender, RoutedEventArgs e) { if (!IgnorePlayerClick()) await SelectSearchModeAsync(FeedSearchMode.Videos); }
        private async void SearchCreator_Click(object sender, RoutedEventArgs e) { if (!IgnorePlayerClick()) await SelectSearchModeAsync(FeedSearchMode.Users); }
        private async void SearchInput_KeyDown(object sender, KeyRoutedEventArgs e) { if (e.Key == VirtualKey.Enter) { e.Handled = true; await SearchAsync(); } }
        private async void DanmakuInput_KeyDown(object sender, KeyRoutedEventArgs e) { if (e.Key == VirtualKey.Enter) { e.Handled = true; await SendDanmakuAsync(); } }
        private async void SendDanmaku_Click(object sender, RoutedEventArgs e) => await SendDanmakuAsync();
        private async void Refresh_Click(object sender, RoutedEventArgs e) { if (!IgnorePlayerClick()) await RefreshFeedAsync(); }
        private async void PreviousPage_Click(object sender, RoutedEventArgs e) { if (!IgnorePlayerClick() && PreviousPage.IsEnabled && _feedNavigation.PreviousPage()) await LoadFeedAsync(); }
        private async void NextPage_Click(object sender, RoutedEventArgs e) { if (IgnorePlayerClick() || !NextPage.IsEnabled) return; _feedNavigation.NextPage(); await LoadFeedAsync(); }
        private async void MoreComments_Click(object sender, RoutedEventArgs e) { if (!IgnorePlayerClick()) await LoadCommentsAsync(_commentPage == 0 || _commentsNeedRetry); }
        private async void RefreshComments_Click(object sender, RoutedEventArgs e) { if (!IgnorePlayerClick()) await LoadCommentsAsync(true); }
        private async void RetryPlayback_Click(object sender, RoutedEventArgs e) { if (IgnorePlayerClick()) return; if (_video != null) await LoadPartAsync(_currentCid); else if (_requestedBvid != null) await OpenVideoAsync(_requestedBvid, _requestedTitle, _playingEpisodeItem == null ? null : new CreatorPlaybackBookmark { Episode = _playingEpisodeItem, Cid = _playingEpisodeItem.Cid, Playing = true, Quality = _preferredQuality }); }
        private async void OfficialVideo_Click(object sender, RoutedEventArgs e)
        {
            if (IgnorePlayerClick()) return;
            if (string.IsNullOrWhiteSpace(_requestedBvid) && !(_playingEpisodeItem?.EpisodeId > 0)) return;
            try
            {
                bool opened = await Launcher.LaunchUriAsync(new Uri(_playingEpisodeItem?.EpisodeId > 0
                    ? "https://www.bilibili.com/bangumi/play/ep" + _playingEpisodeItem.EpisodeId
                    : "https://www.bilibili.com/video/" + Uri.EscapeDataString(_requestedBvid)));
                SetStatus(opened ? "已请求打开官网视频。官网如提示验证，请按页面指引完成。" : "无法打开浏览器，请在手机或电脑的 Bilibili 官网查看 " + _requestedBvid);
            }
            catch (Exception ex) { SetStatus("打开官网失败：" + Friendly(ex)); }
        }
        private async void Part_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_selectingPart && PartSelector.SelectedItem is VideoPart part && part.Cid != _currentCid) await LoadPartAsync(part.Cid); }

        private async void Help_Click(object sender, RoutedEventArgs e)
        {
            if (_dialogOpen || IgnorePlayerClick()) return;
            _dialogOpen = true;
            try
            {
                var dialog = new ContentDialog
                {
                    Title = "弹幕舱 · 使用帮助", CloseButtonText = "知道了",
                    RequiresPointer = RequiresPointer.Never, XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Enabled,
                    Content = new TextBlock { Text = "左摇杆 / 方向键：移动焦点\nA：确认；播放控件隐藏时播放 / 暂停\n首页：左右选视频，上下换行；边缘可到搜索、顶栏、翻页\n播放器：左右选按钮；上进进度条，再上到顶栏；下返回\n进度条：左右快退 / 快进 10 秒\nB：回复回主评论，再关闭面板 / 隐藏控件 / 返回列表\nX：发弹幕　Y：原生搜索键盘\n首页 LB / RB：切换分区；播放器 LB / RB：前后 10 秒\n右摇杆：滚动列表 / 聊天，焦点随画面移动\n\n首页刷新换一批推荐，服务端识别账号后显示为你推荐。\n评论支持头像、点赞、表情、回复及逐条滚动。\n播放时闲置 5 秒隐藏控件，方向键或左摇杆重新显示。\n搜索和评论使用系统原生键盘，取消后可重新打开。\n先扫码登录，再发布评论或弹幕。\n底栏「互动」可点赞、投币、选择收藏夹和确认三连。\n三连最多使用2枚硬币，收藏到默认收藏夹；提交未确认时先刷新。\nUP头像/名字或互动侧栏的主页入口可浏览投稿和动态。\n\n第三方客户端，与哔哩哔哩和微软无隶属关系。画质和内容访问由平台及账号权限决定。", FontSize = 17, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 }
                };
                ((TextBlock)dialog.Content).Text += "\n\n手柄输入诊断\n" + GamepadDiagnostics;
                _activeGeneralDialog = dialog;
                dialog.Closed += (s, args) => _lastBack = DateTimeOffset.UtcNow;
                await dialog.ShowAsync();
            }
            finally { _activeGeneralDialog = null; _dialogOpen = false; RestoreNavigationFocus(); }
        }

        private void SetBusy(bool busy) { _busyCount = Math.Max(0, _busyCount + (busy ? 1 : -1)); Busy.IsActive = _busyCount > 0; }
        private void SetStatus(string text) { StatusText.Text = text; WatchStatus.Text = text; ToolTipService.SetToolTip(StatusText, text); }
        private static string Friendly(Exception ex) => ex is TaskCanceledException ? "请求超时，请检查网络后重试" : Shorten(ex.Message, 160);
        private static string PlaybackFailureText(Exception ex)
        {
            var apiError = ex as BilibiliApiException;
            bool restricted = apiError != null && (apiError.Code == 403 || apiError.Code == 412 || apiError.Code == 429 || apiError.Code == -352 || apiError.Code == -412 || apiError.Code == -509);
            return Friendly(ex) + (restricted
                ? "\n可稍后重试，或选择「官网打开」查看是否需要验证。\n扫码登录不保证解除访问限制。"
                : "\n请选择「重试」，或选择「官网打开」查看视频。按 B 返回。");
        }
        private static string Shorten(string value, int length) => string.IsNullOrEmpty(value) ? "未知错误" : value.Length <= length ? value : value.Substring(0, length) + "…";
        private static string FormatTime(TimeSpan time) => MediaTimeFormatter.Format(time);
        private static void Cancel(ref CancellationTokenSource source) { var old = source; source = null; if (old != null) { old.Cancel(); old.Dispose(); } }

        private void Page_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_disposed) return;
            StopSectionMotion();
            _disposed = true;
            DisposeCommentImages();
            StopCommentReactions(true);
            (_activeGeneralDialog as CommentReaderDialog)?.Dispose();
            _activeGeneralDialog?.Hide();
            _activeInlineTextInput?.Cancel();
            InteractionSide.StopRequests();
            HideLiveSection();
            ++_textInputRequest;
            _clock.Stop();
            AccountLibrary.ClearAccount();
            CreatorHub.StopRequests();
            StopGamepadInput();
            Cancel(ref _danmakuSyncCts);
            StopDanmakuMask();
            _danmakuMaskController?.Dispose();
            Cancel(ref _qualityCts);
            Cancel(ref _feedCts); Cancel(ref _watchCts); Cancel(ref _partCts); Cancel(ref _replyCts);
            Window.Current.CoreWindow.KeyDown -= Core_KeyDown;
            Window.Current.CoreWindow.KeyUp -= Core_KeyUp;
            SystemNavigationManager.GetForCurrentView().BackRequested -= Back_Requested;
            _inputPane.Showing -= InputPane_Showing;
            _inputPane.Hiding -= InputPane_Hiding;
            _media.MediaOpened -= Media_Opened;
            _media.MediaFailed -= Media_Failed;
            _media.MediaEnded -= Media_Ended;
            _media.PlaybackSession.PlaybackStateChanged -= Playback_StateChanged;
            _media.PlaybackSession.BufferingStarted -= Playback_BufferingStarted;
            _media.PlaybackSession.BufferingEnded -= Playback_BufferingEnded;
            DisablePointerCursor();
            StopMedia();
            _media.Dispose();
            Danmaku.Dispose();
            _api.Dispose();
        }
    }
}
