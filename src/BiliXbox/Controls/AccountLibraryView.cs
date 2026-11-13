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
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Markup;
using Windows.UI.Xaml.Media;

namespace BiliXbox.Controls
{
    // A single account surface, with the same poster resources and focus effects
    // as the feed. MainPage continues to own input dispatch, login and playback.
    public sealed partial class AccountLibraryView : UserControl
    {
        private enum LibraryPane { Overview, History, Folders, Favorites, Follows, Episodes }
        private sealed class ViewState
        {
            public LibraryPane Pane;
            public string Title = "我的内容", Notice = "";
            public long FolderId, SeasonId;
            public bool Drama, NeedsRefresh;
            public int PageIndex, SelectedIndex;
            public readonly List<AccountLibraryPage> Pages = new List<AccountLibraryPage>();
            public IReadOnlyList<FavoriteFolder> Folders = Array.Empty<FavoriteFolder>();
            public IReadOnlyList<AccountLibraryItem> Episodes = Array.Empty<AccountLibraryItem>();
        }

        private BilibiliClient _api;
        private long _accountMid;
        private CancellationTokenSource _sessionCts, _loadCts;
        private ViewState _state = new ViewState();
        private readonly Stack<ViewState> _trail = new Stack<ViewState>();
        private bool _busy, _openingItem;
        private int _focusGeneration, _pendingIndex = -1, _openGeneration;
        private ListViewBase _pendingList;
        private Control _lastFocus;

        private readonly Grid _root, _overview, _browse;
        private readonly AvatarView _avatar;
        private readonly TextBlock _name, _subtitle, _sectionTitle, _notice, _empty, _pageLabel, _hint;
        private readonly Button _back, _signOut, _history, _favorites, _follows, _anime, _drama, _refresh, _previous, _next;
        private readonly StackPanel _tabs, _pager;
        private readonly GridView _videoGrid, _folderGrid;
        private readonly ProgressRing _progress;

        public event EventHandler CloseRequested;
        public event EventHandler SignOutRequested;
        public event Func<AccountLibraryItem, Task> OpenItemRequested;

        public AccountLibraryView()
        {
            RequiresPointer = RequiresPointer.Never;
            XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Enabled;
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            VerticalContentAlignment = VerticalAlignment.Stretch;
            _root = new Grid { Background = Brush(255, 13, 13, 16), Padding = new Thickness(52, 28, 52, 20), RowSpacing = 14 };
            _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Content = _root;
            var header = new Grid { ColumnSpacing = 18 };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _back = MakeButton("返回", () => { if (!Back()) CloseRequested?.Invoke(this, EventArgs.Empty); }, "AccountBackButton");
            header.Children.Add(_back);
            _avatar = new AvatarView { Width = 56, Height = 56, VerticalAlignment = VerticalAlignment.Center };
            Add(header, _avatar, 0, 1);
            var identity = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
            _name = Text("我的账户", 28, true); _name.TextTrimming = TextTrimming.CharacterEllipsis;
            _subtitle = Text("你的内容，继续精彩", 14); _subtitle.Foreground = SecondaryBrush();
            identity.Children.Add(_name); identity.Children.Add(_subtitle); Add(header, identity, 0, 2);
            _signOut = MakeButton("退出登录", () => SignOutRequested?.Invoke(this, EventArgs.Empty), "AccountSignOutButton");
            Add(header, _signOut, 0, 3); _root.Children.Add(header);

            _overview = new Grid { ColumnSpacing = 24, Padding = new Thickness(20, 38, 20, 24), VerticalAlignment = VerticalAlignment.Center };
            for (int i = 0; i < 3; ++i) _overview.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _history = SectionButton("\uE81C", "播放历史", "从上次看到的地方继续", () => OpenSection(LibraryPane.History), "AccountHistoryButton");
            _favorites = SectionButton("\uE8B7", "收藏", "浏览收藏夹与收藏的视频", () => OpenSection(LibraryPane.Folders), "AccountFavoritesButton");
            _follows = SectionButton("\uE8F2", "追番追剧", "番剧、电视剧、电影与纪录片", () => OpenSection(LibraryPane.Follows), "AccountFollowsButton");
            Add(_overview, _history, 0, 0); Add(_overview, _favorites, 0, 1); Add(_overview, _follows, 0, 2);
            Add(_root, _overview, 1, 0);

            _browse = new Grid { RowSpacing = 10 };
            _browse.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _browse.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _browse.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _browse.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var toolbar = new Grid { ColumnSpacing = 14 };
            toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _sectionTitle = Text("我的内容", 24, true); _sectionTitle.VerticalAlignment = VerticalAlignment.Center;
            _sectionTitle.TextTrimming = TextTrimming.CharacterEllipsis; toolbar.Children.Add(_sectionTitle);
            _tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            _anime = MakeButton("追番", () => SwitchFollows(false), "AccountAnimeTab");
            _drama = MakeButton("追剧 · 电影", () => SwitchFollows(true), "AccountDramaTab");
            _tabs.Children.Add(_anime); _tabs.Children.Add(_drama); Add(toolbar, _tabs, 0, 1);
            _refresh = MakeButton("刷新", () => Run(RefreshAsync), "AccountRefreshButton"); Add(toolbar, _refresh, 0, 2);
            _browse.Children.Add(toolbar);
            _notice = Text("", 14); _notice.Foreground = SecondaryBrush(); _notice.TextWrapping = TextWrapping.Wrap;
            _notice.MaxLines = 2; _notice.TextTrimming = TextTrimming.CharacterEllipsis; Add(_browse, _notice, 1, 0);
            var content = new Grid();
            _videoGrid = MakeGrid("AccountVideoGrid", "账户内容", 282);
            _videoGrid.ItemTemplate = (DataTemplate)Application.Current.Resources["TvVideoCardTemplate"];
            _videoGrid.ItemClick += (sender, args) => Run(() => OpenItemAsync(args.ClickedItem as AccountLibraryItem));
            _folderGrid = MakeGrid("AccountFolderGrid", "收藏夹", 370);
            _folderGrid.ItemTemplate = (DataTemplate)XamlReader.Load(@"<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Grid Width='332' Margin='18,14,18,24'><Grid Tag='TvShadow' IsHitTestVisible='False'/><Border Background='#1C1C1E' CornerRadius='16' Padding='24'><StackPanel Spacing='18'><FontIcon Glyph='&#xE8B7;' FontFamily='Segoe MDL2 Assets' FontSize='36' HorizontalAlignment='Left' Foreground='#F2F2F7'/><TextBlock Text='{Binding Title}' FontSize='21' FontWeight='SemiBold' Foreground='#F2F2F7' TextWrapping='Wrap' MaxLines='2' Height='56' TextTrimming='CharacterEllipsis'/><StackPanel Orientation='Horizontal'><TextBlock Text='{Binding Count}' Foreground='#8E8E93' FontSize='14'/><TextBlock Text=' 个内容' Foreground='#8E8E93' FontSize='14'/></StackPanel></StackPanel></Border></Grid></DataTemplate>");
            _folderGrid.ItemClick += (sender, args) => { var folder = args.ClickedItem as FavoriteFolder; if (folder != null) OpenFolder(folder); };
            content.Children.Add(_videoGrid); content.Children.Add(_folderGrid);
            _empty = Text("", 20); _empty.TextWrapping = TextWrapping.Wrap; _empty.TextAlignment = TextAlignment.Center;
            _empty.HorizontalAlignment = HorizontalAlignment.Center; _empty.VerticalAlignment = VerticalAlignment.Center;
            _empty.MaxWidth = 760; _empty.Foreground = SecondaryBrush(); _empty.IsHitTestVisible = false; content.Children.Add(_empty);
            _progress = new ProgressRing { Width = 42, Height = 42, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            content.Children.Add(_progress); Add(_browse, content, 2, 0);
            _pager = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, HorizontalAlignment = HorizontalAlignment.Center };
            _previous = MakeButton("上一页", () => Run(() => ChangePageAsync(-1)), "AccountPreviousButton");
            _next = MakeButton("下一页", () => Run(() => ChangePageAsync(1)), "AccountNextButton");
            _pageLabel = Text("第 1 页", 15); _pageLabel.VerticalAlignment = VerticalAlignment.Center;
            _pager.Children.Add(_previous); _pager.Children.Add(_pageLabel); _pager.Children.Add(_next); Add(_browse, _pager, 3, 0);
            Add(_root, _browse, 1, 0);
            _hint = Text("左摇杆 / 方向键选择 · A 确认 · B 返回", 13); _hint.Foreground = Brush(102, 242, 242, 247); Add(_root, _hint, 2, 0);
            PreviewKeyDown += NativeKeyDown;
            PreviewKeyUp += NativeKeyUp;
            Unloaded += (sender, args) => StopRequests();
            Render();
        }

        public void Initialize(BilibiliClient api, DataTemplate videoTemplate = null, Style itemStyle = null)
        {
            if (api == null) throw new ArgumentNullException(nameof(api));
            _api = api;
            if (videoTemplate != null) _videoGrid.ItemTemplate = videoTemplate;
            if (itemStyle != null) _videoGrid.ItemContainerStyle = _folderGrid.ItemContainerStyle = itemStyle;
        }

        public Task ShowAsync(UserProfile user, CancellationToken token = default(CancellationToken))
        {
            if (_api == null) throw new InvalidOperationException("账户内容尚未初始化。");
            ClearAccount();
            if (user == null || user.Mid <= 0 || !_api.IsLoggedIn || _api.CurrentUser?.Mid != user.Mid)
            {
                _subtitle.Text = "登录已失效，请返回后重新登录。";
                Render(); Focus(_back); return Task.CompletedTask;
            }
            _accountMid = user.Mid; _name.Text = user.Name; _avatar.Url = user.AvatarUrl;
            _subtitle.Text = user.IsPremium ? "大会员 · 我的内容" : "我的内容";
            _sessionCts = token.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(token) : new CancellationTokenSource();
            Render(); Focus(_history); return Task.CompletedTask;
        }

        public void StopRequests()
        {
            Cancel(ref _loadCts); Cancel(ref _sessionCts);
            _busy = _openingItem = false; ++_openGeneration; ++_focusGeneration; _pendingList = null; _pendingIndex = -1;
        }

        public void ClearAccount()
        {
            StopRequests(); _accountMid = 0; _trail.Clear(); _state = new ViewState();
            _lastFocus = null; _name.Text = "我的账户"; _avatar.Url = ""; _subtitle.Text = "登录后查看你的内容";
            _videoGrid.ItemsSource = _folderGrid.ItemsSource = null; Render();
        }

        public void InvalidateFavoriteContent()
        {
            // Keep navigation and selection while the player is in front. The
            // next return refreshes a changed folder instead of showing a stale
            // favorite that the user just removed from the video drawer.
            foreach (var state in _trail.Concat(new[] { _state }))
                if (state.Pane == LibraryPane.Folders || state.Pane == LibraryPane.Favorites)
                    state.NeedsRefresh = true;
        }

        private bool HasAccount => _accountMid > 0 && _api?.IsLoggedIn == true && _api.CurrentUser?.Mid == _accountMid;
        private ListViewBase ActiveList => _state.Pane == LibraryPane.Folders ? (ListViewBase)_folderGrid : _videoGrid;
        private AccountLibraryPage CurrentPage => _state.PageIndex >= 0 && _state.PageIndex < _state.Pages.Count ? _state.Pages[_state.PageIndex] : null;

        private void Render()
        {
            bool overview = _state.Pane == LibraryPane.Overview;
            _overview.Visibility = overview ? Visibility.Visible : Visibility.Collapsed;
            _browse.Visibility = overview ? Visibility.Collapsed : Visibility.Visible;
            _history.IsEnabled = _favorites.IsEnabled = _follows.IsEnabled = _signOut.IsEnabled = HasAccount;
            _sectionTitle.Text = _state.Title;
            _tabs.Visibility = _state.Pane == LibraryPane.Follows ? Visibility.Visible : Visibility.Collapsed;
            SetTab(_anime, !_state.Drama); SetTab(_drama, _state.Drama);
            _refresh.IsEnabled = HasAccount && !_busy;
            _anime.IsEnabled = _drama.IsEnabled = HasAccount;
            bool folders = _state.Pane == LibraryPane.Folders;
            _folderGrid.Visibility = folders ? Visibility.Visible : Visibility.Collapsed;
            _videoGrid.Visibility = !folders && !overview ? Visibility.Visible : Visibility.Collapsed;
            _folderGrid.ItemsSource = _state.Folders;
            _videoGrid.ItemsSource = _state.Pane == LibraryPane.Episodes ? _state.Episodes : CurrentPage?.Items ?? Array.Empty<AccountLibraryItem>();
            _folderGrid.IsEnabled = _videoGrid.IsEnabled = HasAccount && !_busy;
            _notice.Text = _busy ? "正在读取…" : _state.Notice;
            _notice.Visibility = string.IsNullOrWhiteSpace(_notice.Text) ? Visibility.Collapsed : Visibility.Visible;
            _progress.IsActive = _busy; _progress.Visibility = _busy ? Visibility.Visible : Visibility.Collapsed;
            _empty.Text = HasAccount ? "这里还没有内容。" : "登录已失效，请返回后重新登录。";
            _empty.Visibility = !overview && !_busy && ActiveList.Items.Count == 0 && string.IsNullOrWhiteSpace(_state.Notice) ? Visibility.Visible : Visibility.Collapsed;
            bool paged = _state.Pane == LibraryPane.History || _state.Pane == LibraryPane.Favorites || _state.Pane == LibraryPane.Follows;
            _pager.Visibility = paged ? Visibility.Visible : Visibility.Collapsed;
            _previous.IsEnabled = !_busy && _state.PageIndex > 0;
            _next.IsEnabled = !_busy && (CurrentPage?.HasMore == true || _state.PageIndex + 1 < _state.Pages.Count);
            _pageLabel.Text = "第 " + (_state.PageIndex + 1) + " 页";
        }

        private GridView MakeGrid(string id, string name, int width)
        {
            var grid = new GridView
            {
                SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = true, RequiresPointer = RequiresPointer.Never,
                Padding = new Thickness(20, 18, 20, 24), HorizontalContentAlignment = HorizontalAlignment.Stretch,
                ItemContainerStyle = (Style)Application.Current.Resources["TvCardItemStyle"]
            };
            grid.ItemsPanel = (ItemsPanelTemplate)XamlReader.Load("<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><ItemsWrapGrid Orientation='Horizontal' ItemWidth='" + width + "' /></ItemsPanelTemplate>");
            ScrollViewer.SetHorizontalScrollMode(grid, ScrollMode.Disabled); ScrollViewer.SetVerticalScrollMode(grid, ScrollMode.Enabled);
            ScrollViewer.SetVerticalScrollBarVisibility(grid, ScrollBarVisibility.Hidden);
            AutomationProperties.SetAutomationId(grid, id); AutomationProperties.SetName(grid, name);
            TvFocusEffects.AttachCardFocus(grid, item => { if (ReferenceEquals(grid, ActiveList)) _state.SelectedIndex = Math.Max(0, grid.Items.IndexOf(item)); });
            return grid;
        }

        private static Button SectionButton(string glyph, string title, string description, Action action, string id)
        {
            var panel = new StackPanel { Spacing = 18 };
            panel.Children.Add(new FontIcon { Glyph = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 38, HorizontalAlignment = HorizontalAlignment.Left });
            panel.Children.Add(new TextBlock { Text = title, FontSize = 26, FontWeight = FontWeights.SemiBold });
            panel.Children.Add(new TextBlock { Text = description, FontSize = 14, Opacity = .65, TextWrapping = TextWrapping.Wrap });
            var button = MakeButton(title, action, id); button.Content = panel; button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.HorizontalContentAlignment = HorizontalAlignment.Left; button.VerticalContentAlignment = VerticalAlignment.Center;
            button.Padding = new Thickness(26); button.Height = 230; button.CornerRadius = new CornerRadius(18);
            return button;
        }

        private static Button MakeButton(string text, Action action, string id)
        {
            var button = new Button { Content = text, Style = (Style)Application.Current.Resources["TvButtonStyle"], RequiresPointer = RequiresPointer.Never };
            AutomationProperties.SetAutomationId(button, id); AutomationProperties.SetName(button, text);
            button.Click += (sender, args) => action(); return button;
        }
        private static TextBlock Text(string text, double size, bool bold = false) => new TextBlock { Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Foreground = Brush(255, 242, 242, 247) };
        private static SolidColorBrush Brush(byte a, byte r, byte g, byte b) => new SolidColorBrush(Color.FromArgb(a, r, g, b));
        private static SolidColorBrush SecondaryBrush() => Brush(255, 142, 142, 147);
        private static void Add(Grid grid, FrameworkElement child, int row, int column) { Grid.SetRow(child, row); Grid.SetColumn(child, column); grid.Children.Add(child); }
        private static void SetTab(Button button, bool selected) { button.Background = selected ? Brush(255, 245, 245, 247) : Brush(31, 255, 255, 255); button.Foreground = selected ? Brush(255, 17, 18, 22) : Brush(242, 255, 255, 255); }
        private static void Cancel(ref CancellationTokenSource source) { var old = source; source = null; if (old != null) { old.Cancel(); old.Dispose(); } }
    }
}
