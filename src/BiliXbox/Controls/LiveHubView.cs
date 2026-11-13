using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Models;
using BiliXbox.Services;
using Windows.UI;
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
    // MainPage owns raw input and native keyboard engagement. This view owns
    // only its room, so leaving live cannot leak playback or chat callbacks.
    public sealed partial class LiveHubView : UserControl
    {
        private enum Pane { None, Chat, Gifts, GiftAmount, GiftConfirm, Quality }
        private readonly Grid _root, _browse, _watch, _chrome, _drawer;
        private readonly GridView _rooms;
        private readonly TextBlock _notice, _pageText, _roomTitle, _roomMeta, _watchNotice, _drawerTitle, _drawerNotice;
        private readonly Button _open, _refresh, _previous, _next, _leave, _play, _danmakuButton, _chatButton, _giftButton, _qualityButton, _closeDrawer, _send, _reconnectChat;
        private readonly StackPanel _drawerBody, _chatComposer;
        private readonly ScrollViewer _drawerScroll;
        private readonly ListView _messages;
        private readonly ProgressRing _loading;
        private readonly DanmakuOverlay _overlay;
        private readonly MediaPlayerElement _surface;
        private readonly Dictionary<Button, Action> _actions = new Dictionary<Button, Action>();
        private readonly List<LiveRoom> _roomItems = new List<LiveRoom>();
        private readonly LiveWriteGate _writeGate = new LiveWriteGate();
        private readonly DispatcherTimer _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        private TextBox _roomInput, _chatInput;
        private BilibiliClient _api;
        private CancellationTokenSource _browseCts, _roomCts;
        private CancellationToken _external;
        private int _page = 1, _selectedIndex, _focusGeneration, _roomGeneration, _playGeneration;
        private bool _hasMore, _browseBusy, _editing, _sending, _giftBusy, _danmaku = true, _wantsPlay = true;
        private Pane _pane;
        private Control _lastFocus;
        private LiveRoom _room;
        private LivePlayback _playback;
        private LiveGift _selectedGift;
        private LiveGiftQuote _quote;
        private int _giftCount = 1;
        private long _giftsMid;
        private bool _bagMode = true;
        private IReadOnlyList<LiveGift> _gifts = Array.Empty<LiveGift>();

        public event EventHandler PlaybackChanged;
        public event Func<Task> LoginRequested;
        public Func<TextBox, string, int, Task<bool>> RequestTextInputAsync { get; set; }
        public bool IsEditing => _editing;
        public bool IsPlayingRoom => _room != null;
        public bool OwnsFocus => IsWithin(FocusManager.GetFocusedElement() as DependencyObject, this);

        public LiveHubView()
        {
            RequiresPointer = RequiresPointer.Never;
            XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Enabled;
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            VerticalContentAlignment = VerticalAlignment.Stretch;
            _root = new Grid { Background = Brush(255, 13, 13, 16) }; Content = _root;
            _browse = new Grid { Padding = new Thickness(0, 8, 0, 12), RowSpacing = 8 };
            _browse.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _browse.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _browse.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _browse.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var search = new Grid { ColumnSpacing = 12 };
            search.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            search.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            search.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _roomInput = Editor("输入直播间号或 live.bilibili.com 链接", "LiveRoomInput", 240);
            AttachEditor(_roomInput, true); search.Children.Add(_roomInput);
            _open = MakeButton("进入直播间", () => Run(OpenInputAsync), "LiveOpenRoomButton"); Add(search, _open, 0, 1);
            _refresh = MakeButton("刷新", () => Run(() => LoadRoomsAsync(_page)), "LiveRefreshButton"); Add(search, _refresh, 0, 2);
            _browse.Children.Add(search);
            _notice = Text("正在读取直播…", 14); _notice.TextWrapping = TextWrapping.Wrap; _notice.MaxLines = 2;
            _notice.Foreground = Brush(180, 242, 242, 247); Add(_browse, _notice, 1, 0);
            _rooms = new GridView { SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = true,
                Padding = new Thickness(20, 18, 20, 24), HorizontalContentAlignment = HorizontalAlignment.Stretch,
                ItemContainerStyle = (Style)Application.Current.Resources["TvCardItemStyle"],
                ItemTemplate = (DataTemplate)Application.Current.Resources["TvVideoCardTemplate"] };
            _rooms.ItemsPanel = (ItemsPanelTemplate)XamlReader.Load("<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><ItemsWrapGrid Orientation='Horizontal' ItemWidth='282' /></ItemsPanelTemplate>");
            ScrollViewer.SetHorizontalScrollMode(_rooms, ScrollMode.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(_rooms, ScrollBarVisibility.Hidden);
            AutomationProperties.SetAutomationId(_rooms, "LiveRoomGrid"); AutomationProperties.SetName(_rooms, "直播推荐");
            TvFocusEffects.AttachCardFocus(_rooms);
            _rooms.BringIntoViewRequested += SuppressManualFocusBringIntoView;
            _rooms.ItemClick += (s, e) => { var card = e.ClickedItem as LivePoster; if (card != null) Run(() => OpenRoomAsync(card.Room.Id)); };
            Add(_browse, _rooms, 2, 0);
            var pager = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, HorizontalAlignment = HorizontalAlignment.Center };
            _previous = MakeButton("上一页", () => Run(() => LoadRoomsAsync(Math.Max(1, _page - 1))), "LivePreviousButton");
            _next = MakeButton("下一页", () => Run(() => LoadRoomsAsync(_page + 1)), "LiveNextButton");
            _pageText = Text("第 1 页", 14); _pageText.VerticalAlignment = VerticalAlignment.Center;
            pager.Children.Add(_previous); pager.Children.Add(_pageText); pager.Children.Add(_next); Add(_browse, pager, 3, 0);
            _root.Children.Add(_browse);

            _watch = new Grid { Background = Brush(255, 0, 0, 0), Visibility = Visibility.Collapsed };
            _surface = new MediaPlayerElement { AreTransportControlsEnabled = false, Stretch = Stretch.Uniform, IsTabStop = false };
            AutomationProperties.SetAutomationId(_surface, "LivePlayer");
            _watch.Children.Add(_surface);
            _overlay = new DanmakuOverlay(); _watch.Children.Add(_overlay);
            _chrome = new Grid { Padding = new Thickness(44, 28, 44, 24), RowSpacing = 12 };
            _chrome.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _chrome.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _chrome.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _chrome.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _chrome.Background = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(0, 1),
                GradientStops = { new GradientStop { Color = Color.FromArgb(190, 0, 0, 0), Offset = 0 },
                    new GradientStop { Color = Colors.Transparent, Offset = .3 }, new GradientStop { Color = Colors.Transparent, Offset = .65 },
                    new GradientStop { Color = Color.FromArgb(220, 0, 0, 0), Offset = 1 } } };
            var header = new Grid { ColumnSpacing = 18 };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _leave = MakeButton("返回", () => LeaveRoom(), "LiveLeaveButton"); header.Children.Add(_leave);
            var info = new StackPanel { Spacing = 5 }; _roomTitle = Text("直播间", 27, true); _roomTitle.TextTrimming = TextTrimming.CharacterEllipsis;
            AutomationProperties.SetAutomationId(_roomTitle, "LiveRoomTitle");
            _roomMeta = Text("", 15); _roomMeta.Foreground = Brush(200, 242, 242, 247); info.Children.Add(_roomTitle); info.Children.Add(_roomMeta);
            Add(header, info, 0, 1); _chrome.Children.Add(header);
            var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            _play = MakeButton("暂停", TogglePlayback, "LivePlayButton");
            _danmakuButton = MakeButton("弹幕 开", ToggleDanmaku, "LiveDanmakuButton");
            _chatButton = MakeButton("直播聊天", () => OpenPane(Pane.Chat), "LiveChatButton");
            _giftButton = MakeButton("礼物", () => Run(OpenGiftsAsync), "LiveGiftsButton");
            _qualityButton = MakeButton("画质", () => OpenPane(Pane.Quality), "LiveQualityButton");
            foreach (var button in new[] { _play, _danmakuButton, _chatButton, _giftButton, _qualityButton }) controls.Children.Add(button);
            Add(_chrome, controls, 2, 0);
            _watchNotice = Text("左摇杆 / 方向键选择 · A 确认 · R 滚动面板 · B 隐藏控件 / 返回", 13); _watchNotice.TextWrapping = TextWrapping.Wrap;
            AutomationProperties.SetAutomationId(_watchNotice, "LiveWatchStatus");
            _watchNotice.Foreground = Brush(190, 242, 242, 247); Add(_chrome, _watchNotice, 3, 0);
            _watch.Children.Add(_chrome);
            var buffer = new StackPanel { Background = Brush(180, 0, 0, 0), Padding = new Thickness(28), CornerRadius = new CornerRadius(18),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Spacing = 12, IsHitTestVisible = false };
            _loading = new ProgressRing { Width = 42, Height = 42, IsActive = false }; buffer.Children.Add(_loading);
            var bufferText = Text("正在加载直播…", 18); bufferText.HorizontalAlignment = HorizontalAlignment.Center; buffer.Children.Add(bufferText);
            _loading.Tag = buffer; buffer.Visibility = Visibility.Collapsed; _watch.Children.Add(buffer);

            _drawer = new Grid { Width = 470, HorizontalAlignment = HorizontalAlignment.Right, Background = Brush(242, 0, 0, 0),
                Padding = new Thickness(26, 28, 26, 24), RowSpacing = 12, Visibility = Visibility.Collapsed };
            _drawer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _drawer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _drawer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _drawer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var drawerHeader = new Grid { ColumnSpacing = 10 };
            drawerHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            drawerHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _drawerTitle = Text("直播聊天", 26, true); drawerHeader.Children.Add(_drawerTitle);
            _closeDrawer = MakeButton("关闭", () => Back(), "LiveDrawerCloseButton"); Add(drawerHeader, _closeDrawer, 0, 1); _drawer.Children.Add(drawerHeader);
            _drawerNotice = Text("", 14); _drawerNotice.TextWrapping = TextWrapping.Wrap; _drawerNotice.MaxLines = 4; _drawerNotice.Foreground = Brush(200, 242, 242, 247);
            AutomationProperties.SetAutomationId(_drawerNotice, "LiveDrawerStatus");
            Add(_drawer, _drawerNotice, 1, 0);
            _drawerBody = new StackPanel { Spacing = 12 };
            _drawerScroll = new ScrollViewer { Content = _drawerBody, IsTabStop = false, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled };
            _drawerScroll.BringIntoViewRequested += SuppressManualFocusBringIntoView;
            Add(_drawer, _drawerScroll, 2, 0);
            _messages = new ListView { SelectionMode = ListViewSelectionMode.None, IsTabStop = true,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, Background = Brush(0, 0, 0, 0) };
            AutomationProperties.SetAutomationId(_messages, "LiveChatMessages"); AutomationProperties.SetName(_messages, "实时直播消息");
            AutomationProperties.SetHelpText(_messages, "右摇杆滚动消息，方向键上下阅读，向左编写弹幕。");
            ScrollViewer.SetHorizontalScrollMode(_messages, ScrollMode.Disabled); ScrollViewer.SetVerticalScrollBarVisibility(_messages, ScrollBarVisibility.Hidden);
            _messages.BringIntoViewRequested += SuppressManualFocusBringIntoView;
            _messages.Loaded += (s, e) => EnsureChatScroll();
            Add(_drawer, _messages, 2, 0);
            _chatComposer = new StackPanel { Spacing = 10 };
            _reconnectChat = MakeButton("重新连接", () => Run(ReconnectChatAsync), "LiveReconnectChatButton");
            _chatInput = Editor("发送一条友善的直播弹幕", "LiveDanmakuInput", 30); AttachEditor(_chatInput, false);
            _send = MakeButton("发送弹幕", () => Run(SendChatAsync), "LiveSendDanmakuButton");
            _chatComposer.Children.Add(_reconnectChat); _chatComposer.Children.Add(_chatInput); _chatComposer.Children.Add(_send); Add(_drawer, _chatComposer, 3, 0);
            _watch.Children.Add(_drawer); _root.Children.Add(_watch);
            TvFocusEffects.AttachShowHide(_drawer, 32, 0);
            PreviewKeyDown += NativeKeyDown; PreviewKeyUp += NativeKeyUp;
            _tick.Tick += Tick; Unloaded += (s, e) => StopRequests(); UpdateBrowseActions();
        }

        public void Initialize(BilibiliClient api) => _api = api ?? throw new ArgumentNullException(nameof(api));
        public async Task ShowAsync(CancellationToken token = default(CancellationToken))
        {
            if (_api == null) throw new InvalidOperationException("直播尚未初始化。");
            _external = token;
            if (_browseCts == null || _browseCts.IsCancellationRequested)
            { Cancel(ref _browseCts); _browseCts = CancellationTokenSource.CreateLinkedTokenSource(token); }
            if (_roomItems.Count == 0) await LoadRoomsAsync(1); else RestoreFocus();
        }
        public void Hide() => StopRequests();
        public void StopRequests()
        {
            ResetManualScrollState();
            Cancel(ref _browseCts); _browseBusy = false; _editing = false;
            LeaveRoom(false); ++_focusGeneration;
        }
        private async Task LoadRoomsAsync(int page)
        {
            if (_browseBusy || _browseCts == null) return;
            var owner = _browseCts; _browseBusy = true; UpdateBrowseActions(); _notice.Text = "正在读取直播…";
            try
            {
                var result = await _api.GetLiveRoomsAsync(Math.Max(1, page), owner.Token);
                if (!BrowseCurrent(owner)) return;
                ++_focusGeneration; _roomItems.Clear(); _rooms.Items.Clear();
                foreach (var room in result.Items.Where(item => item != null && item.Id > 0))
                { _roomItems.Add(room); _rooms.Items.Add(new LivePoster(room)); }
                _page = Math.Max(1, result.Page); _hasMore = result.HasMore; _selectedIndex = 0;
                _pageText.Text = "第 " + _page + " 页";
                _notice.Text = _roomItems.Count == 0 ? "暂无直播，输入房间号也可直接进入。" : "直播推荐 · " + _roomItems.Count + " 个直播间";
                if (!IsPlayingRoom && Visibility == Visibility.Visible) RestoreFocus();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (BrowseCurrent(owner)) _notice.Text = "直播列表加载失败：" + Friendly(ex) + "。可刷新重试或输入房间号。"; }
            finally { if (owner == _browseCts) { _browseBusy = false; UpdateBrowseActions(); } }
        }
        private bool BrowseCurrent(CancellationTokenSource owner) => owner != null && owner == _browseCts && !owner.IsCancellationRequested;
        private void UpdateBrowseActions()
        { _previous.IsEnabled = !_browseBusy && _page > 1; _next.IsEnabled = !_browseBusy && _hasMore; _refresh.IsEnabled = _open.IsEnabled = !_browseBusy; }
        private async Task OpenInputAsync()
        {
            if (string.IsNullOrWhiteSpace(_roomInput.Text)) { await EditAsync(true); return; }
            long id;
            if (!LiveInput.TryRoomId(_roomInput.Text, out id)) { _notice.Text = "请输入正确的房间号或 live.bilibili.com 直播间链接。"; Focus(_roomInput); return; }
            await OpenRoomAsync(id);
        }
        private async void Run(Func<Task> action)
        { try { await action(); } catch (OperationCanceledException) { } catch (Exception ex) { if (IsPlayingRoom) _watchNotice.Text = Friendly(ex); else _notice.Text = Friendly(ex); } }
        private static TextBox Editor(string placeholder, string id, int limit)
        {
            var editor = new TextBox { PlaceholderText = placeholder, IsReadOnly = true, IsTabStop = true, IsFocusEngagementEnabled = true,
                MaxLength = limit, FontSize = 18, MinHeight = 48, HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetAutomationId(editor, id); AutomationProperties.SetName(editor, placeholder); return editor;
        }
        private Button MakeButton(string label, Action action, string id)
        {
            var button = new Button { Content = label, FontSize = 17, Padding = new Thickness(18, 12, 18, 12) };
            AutomationProperties.SetAutomationId(button, id); _actions.Add(button, action);
            button.Click += (s, e) => { if (button.IsEnabled) action(); };
            button.GotFocus += (s, e) => { _lastFocus = button; button.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false }); WakeChrome(); };
            return button;
        }
        private Button Row(string label, Action action, string id, bool enabled = true)
        {
            int roomGeneration = _roomGeneration, paneGeneration = _paneGeneration; long mid = CurrentMid;
            Action guarded = () =>
            {
                // Removed/recycled rows can still have queued routed events.
                // Their confirmation must never target a new room or account.
                if (RoomCurrent(roomGeneration) && paneGeneration == _paneGeneration && CurrentMid == mid) action();
            };
            var button = MakeButton(label, guarded, id); button.Style = (Style)Application.Current.Resources["TvRowButtonStyle"];
            button.Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, MaxWidth = 355 };
            button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Left;
            button.IsEnabled = enabled; _drawerBody.Children.Add(button); return button;
        }
        private static TextBlock Text(string text, double size, bool strong = false) => new TextBlock { Text = text ?? "", FontSize = size,
            FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal, Foreground = Brush(242, 255, 255, 255) };
        private static SolidColorBrush Brush(byte a, byte r, byte g, byte b) => new SolidColorBrush(Color.FromArgb(a, r, g, b));
        private static void Add(Grid grid, FrameworkElement child, int row, int col) { Grid.SetRow(child, row); Grid.SetColumn(child, col); grid.Children.Add(child); }
        private static void Cancel(ref CancellationTokenSource source) { var old = source; source = null; if (old != null) { old.Cancel(); old.Dispose(); } }
        private static string Friendly(Exception exception) => string.IsNullOrWhiteSpace(exception.Message) ? "请求未完成，请稍后重试。" : exception.Message;
        private long CurrentMid => _api?.IsLoggedIn == true ? _api.CurrentUser?.Mid ?? 0 : 0;

        private sealed class LivePoster : VideoItem
        {
            public LiveRoom Room { get; }
            public LivePoster(LiveRoom room)
            {
                Room = room; Title = room.Title; CoverUrl = room.CoverUrl; Owner = room.OwnerName;
                OwnerMid = room.OwnerMid; OwnerAvatarUrl = room.AvatarUrl; PlayText = room.OnlineText;
                DurationText = room.IsLive ? "直播中" : "未开播";
            }
        }
    }
}
