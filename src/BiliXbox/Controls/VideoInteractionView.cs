using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Models;
using BiliXbox.Services;
using Windows.System;
using Windows.UI;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace BiliXbox.Controls
{
    // MainPage owns the surrounding drawer and gamepad polling. This surface
    // keeps coin/folder confirmation next to the action instead of opening a
    // second dialog, and only explicit action buttons can submit a mutation.
    public sealed partial class VideoInteractionView : UserControl
    {
        private enum Pane { Overview, Coins, Favorites, Triple }
        private readonly VideoInteractionSession _session = new VideoInteractionSession();
        private readonly VideoFavoriteSelection _selection = new VideoFavoriteSelection();
        private readonly StackPanel _body;
        private readonly TextBlock _title, _notice, _hint;
        private readonly Button _close;
        private readonly ProgressRing _progress;
        private readonly Dictionary<Button, Action> _actions = new Dictionary<Button, Action>();
        private readonly List<Button> _rows = new List<Button>();
        private BilibiliClient _api;
        private CancellationTokenSource _sessionCts;
        private CancellationToken _externalToken;
        private VideoInteractionState _state;
        private IReadOnlyList<VideoFavoriteFolder> _folders = Array.Empty<VideoFavoriteFolder>();
        private Pane _pane;
        private bool _busy;
        private long _aid, _ownerMid;
        private string _ownerName = "", _message = "", _lastFocus = "InteractionLikeButton";

        public event EventHandler CloseRequested;
        public event EventHandler CreatorRequested;
        public event EventHandler InteractionChanged;
        public event Func<Task> LoginRequested;

        public VideoInteractionView()
        {
            RequiresPointer = RequiresPointer.Never;
            XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Enabled;
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            VerticalContentAlignment = VerticalAlignment.Stretch;
            var root = new Grid { Background = Brush(230, 0, 0, 0), Padding = new Thickness(26, 28, 26, 24), RowSpacing = 16 };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Content = root;
            var header = new Grid { ColumnSpacing = 12 };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _title = Text("视频互动", 27, true); _title.VerticalAlignment = VerticalAlignment.Center;
            header.Children.Add(_title);
            _close = MakeButton("关闭", () => { if (!Back()) CloseRequested?.Invoke(this, EventArgs.Empty); }, "InteractionCloseButton");
            Grid.SetColumn(_close, 1); header.Children.Add(_close); root.Children.Add(header);
            var status = new Grid { ColumnSpacing = 10 };
            status.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            status.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _progress = new ProgressRing { Width = 24, Height = 24, VerticalAlignment = VerticalAlignment.Top };
            _notice = Text("", 14); _notice.Foreground = Brush(190, 242, 242, 247);
            _notice.TextWrapping = TextWrapping.Wrap;
            status.Children.Add(_progress); Grid.SetColumn(_notice, 1); status.Children.Add(_notice);
            Grid.SetRow(status, 1); root.Children.Add(status);
            _body = new StackPanel { Spacing = 12 };
            var scroll = new ScrollViewer { Content = _body, HorizontalScrollMode = ScrollMode.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollMode = ScrollMode.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                ZoomMode = ZoomMode.Disabled, IsTabStop = false };
            Grid.SetRow(scroll, 2); root.Children.Add(scroll);
            _hint = Text("左摇杆 / 方向键选择 · A 确认 · B 返回", 12);
            _hint.Foreground = Brush(120, 242, 242, 247); _hint.TextWrapping = TextWrapping.Wrap;
            Grid.SetRow(_hint, 3); root.Children.Add(_hint);
            PreviewKeyDown += NativeKeyDown; PreviewKeyUp += NativeKeyUp;
            Unloaded += (sender, args) => StopRequests();
            Render();
        }

        public void Initialize(BilibiliClient api) => _api = api ?? throw new ArgumentNullException(nameof(api));

        public async Task ShowAsync(long aid, long ownerMid, string ownerName,
            CancellationToken token = default(CancellationToken))
        {
            if (_api == null) throw new InvalidOperationException("视频互动尚未初始化。");
            if (aid <= 0) throw new ArgumentOutOfRangeException(nameof(aid));
            StopRequests();
            _aid = aid; _ownerMid = ownerMid; _ownerName = ownerName ?? "";
            _externalToken = token; _state = null; _folders = Array.Empty<VideoFavoriteFolder>();
            _selection.Reset(Array.Empty<KeyValuePair<long, bool>>());
            _session.Open(aid, CurrentMid);
            _sessionCts = token.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(token) : new CancellationTokenSource();
            _pane = Pane.Overview; _lastFocus = "InteractionLikeButton";
            _message = ""; Render();
            await RefreshAsync(false);
            if (_session.Active) RestoreFocus();
        }

        public void StopRequests()
        {
            _session.Stop();
            var old = _sessionCts; _sessionCts = null;
            if (old != null) { old.Cancel(); old.Dispose(); }
            _busy = false;
        }

        private long CurrentMid => _api?.IsLoggedIn == true ? _api.CurrentUser?.Mid ?? 0 : 0;
        private bool HasAccount => _session.AccountMid > 0 && _session.AccountMid == CurrentMid;
        private bool Current(long generation) => _session.Matches(generation, CurrentMid)
            && _sessionCts != null && !_sessionCts.IsCancellationRequested;
        private bool SameResource(long generation) => _session.Active && generation == _session.Generation
            && _sessionCts != null && !_sessionCts.IsCancellationRequested;

        private void FinishOperation(long generation)
        {
            // Authentication can expire inside the awaited API request. Its
            // old response stays rejected, but this resource still needs to
            // leave the spinner and expose login/new-account refresh actions.
            if (!SameResource(generation)) return;
            _busy = false;
            if (CurrentMid != _session.AccountMid)
            {
                _session.Open(_aid, CurrentMid); _state = null; _pane = Pane.Overview;
                _folders = Array.Empty<VideoFavoriteFolder>();
                _selection.Reset(Array.Empty<KeyValuePair<long, bool>>());
                _message = CurrentMid > 0 ? "账户已变化，请刷新读取当前账号的互动状态。" : "登录已失效，请重新扫码登录。";
                _lastFocus = CurrentMid > 0 ? "InteractionRefreshButton" : "InteractionLoginButton";
            }
            Render(false); RestoreFocus();
        }

        private void Render(bool rememberFocus = true)
        {
            var current = FocusManager.GetFocusedElement() as Button;
            if (rememberFocus && current != null && (_rows.Contains(current) || current == _close))
                _lastFocus = AutomationProperties.GetAutomationId(current);
            _rows.Clear(); _actions.Clear(); _body.Children.Clear();
            _title.Text = _pane == Pane.Coins ? "投币支持" : _pane == Pane.Favorites ? "选择收藏夹"
                : _pane == Pane.Triple ? "一键三连" : "视频互动";
            _close.Content = _pane == Pane.Overview || _busy ? "关闭" : "返回";
            _progress.IsActive = _busy; _progress.Visibility = _busy ? Visibility.Visible : Visibility.Collapsed;
            _notice.Text = _busy ? "正在处理，请稍候…" : _message;
            _notice.Visibility = string.IsNullOrWhiteSpace(_notice.Text) ? Visibility.Collapsed : Visibility.Visible;
            bool enabled = HasAccount && _session.CanMutate && !_busy && _state != null;
            if (_pane == Pane.Overview)
            {
                if (!HasAccount)
                {
                    AddText("登录后可点赞、投币和收藏。", 16);
                    AddButton("扫码登录", () => Run(LoginAsync), "InteractionLoginButton", !_busy);
                }
                AddButton(_state?.IsLiked == true ? "✓  已点赞 · 取消点赞" : "点赞", () => Run(LikeAsync), "InteractionLikeButton", enabled);
                AddButton(_state != null && _state.Coins > 0 ? "投币 · 已投 " + _state.Coins + " / 2 枚" : "投币", () => OpenPane(Pane.Coins),
                    "InteractionCoinButton", enabled && _state.Coins < 2);
                AddButton(_state?.IsFavorited == true ? "✓  已收藏 · 管理收藏夹" : "收藏到…", () => Run(OpenFavoritesAsync), "InteractionFavoriteButton", enabled);
                AddButton("一键三连", () => OpenPane(Pane.Triple), "InteractionTripleButton", enabled && _state.Coins == 0);
                if (_state?.Coins == 1) AddText("已投过 1 枚硬币，可再投 1 枚，并单独点赞、收藏。", 13);
                if (_state != null && _state.Coins >= 2) AddText("此视频已投满 2 枚硬币，可单独点赞或管理收藏。", 13);
                if (_session.IsUncertain) AddText("上次提交结果尚未确认。请稍后刷新状态，勿立即重复投币。", 14);
                AddButton("刷新状态", () => Run(() => RefreshAsync(true)), "InteractionRefreshButton", !_busy && HasAccount);
                AddButton("查看 UP 主主页" + (_ownerName.Length > 0 ? " · " + _ownerName : ""),
                    () => CreatorRequested?.Invoke(this, EventArgs.Empty), "InteractionCreatorButton", !_busy && _ownerMid > 0);
            }
            else if (_pane == Pane.Coins)
            {
                AddText("硬币会从当前账户扣除，投出后无法撤回。", 16);
                int remaining = Math.Max(0, 2 - (_state?.Coins ?? 2));
                AddText("此视频已投 " + (_state?.Coins ?? 0) + " 枚，还可投 " + remaining + " 枚。", 14);
                AddButton("投 1 枚硬币", () => Run(() => CoinAsync(1)), "InteractionOneCoinButton", enabled && remaining >= 1);
                AddButton("投 2 枚硬币", () => Run(() => CoinAsync(2)), "InteractionTwoCoinsButton", enabled && remaining >= 2);
            }
            else if (_pane == Pane.Favorites)
            {
                AddText("A 勾选收藏夹，选择「保存收藏」后才会提交。B 返回会放弃未保存的选择。", 14);
                foreach (var folder in _folders)
                {
                    long id = folder.Id;
                    AddButton((_selection.Contains(id) ? "✓  " : "○  ") + folder.Title + (folder.IsDefault ? " · 默认" : ""),
                        () => { if (!_busy && enabled) { _selection.Toggle(id); Render(); RestoreFocus(); } },
                        "InteractionFolder_" + id, enabled);
                }
                if (_folders.Count == 0) AddText("当前账户尚未返回可用收藏夹，请先在官方客户端创建收藏夹后刷新。", 15);
                AddButton("保存收藏" + (_selection.Changed ? " · 有未保存修改" : ""), () => Run(SaveFavoritesAsync),
                    "InteractionSaveFavoritesButton", enabled && _selection.Changed);
            }
            else
            {
                AddText("为当前视频点赞、投币，并收藏到默认收藏夹。", 17);
                AddText("本次最多扣除 2 枚硬币，实际数量由平台返回。硬币投出后无法撤回。", 15);
                AddButton("确认三连 · 最多 2 枚硬币", () => Run(TripleAsync), "InteractionConfirmTripleButton", enabled && _state.Coins == 0);
            }
        }

        private void OpenPane(Pane pane)
        {
            if (_busy || !_session.CanMutate || _state == null) return;
            _pane = pane; _message = ""; Render(); FocusFirstRow();
        }

        public bool Back()
        {
            if (_pane == Pane.Overview || _busy) return false;
            var previous = _pane; _pane = Pane.Overview; _message = "";
            _lastFocus = previous == Pane.Favorites ? "InteractionFavoriteButton" : previous == Pane.Coins ? "InteractionCoinButton" : "InteractionTripleButton";
            _selection.Reset(Array.Empty<KeyValuePair<long, bool>>()); Render(false); RestoreFocus(); return true;
        }

        public void RestoreFocus()
        {
            if (!_session.Active) return;
            var target = _rows.FirstOrDefault(row => row.IsEnabled && AutomationProperties.GetAutomationId(row) == _lastFocus)
                ?? _rows.FirstOrDefault(row => row.IsEnabled) ?? _close;
            Focus(target);
        }

        internal bool Navigate(PlayerDirection direction)
        {
            var available = new[] { _close }.Concat(_rows).Where(row => row.IsEnabled && row.Visibility == Visibility.Visible).ToList();
            var current = FocusManager.GetFocusedElement() as Button;
            int index = available.IndexOf(current);
            int step = direction == PlayerDirection.Up || direction == PlayerDirection.Left ? -1 : 1;
            Focus(available[Math.Max(0, Math.Min(available.Count - 1, index < 0 ? 0 : index + step))]);
            return true;
        }

        internal void Confirm(bool repeat)
        {
            if (repeat) return;
            var button = FocusManager.GetFocusedElement() as Button;
            if (button == _close) { if (!Back()) CloseRequested?.Invoke(this, EventArgs.Empty); return; }
            Action action;
            if (button != null && button.IsEnabled && _actions.TryGetValue(button, out action)) action();
        }

        private void FocusFirstRow() => Focus(_rows.FirstOrDefault(row => row.IsEnabled) ?? _close);
        private void Focus(Button button)
        {
            if (button == null) return;
            _lastFocus = AutomationProperties.GetAutomationId(button);
            button.Focus(FocusState.Keyboard);
            button.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
        }
        private void AddText(string value, double size)
        {
            var text = Text(value, size); text.Foreground = Brush(190, 242, 242, 247);
            text.TextWrapping = TextWrapping.Wrap; _body.Children.Add(text);
        }
        private void AddButton(string label, Action action, string id, bool enabled)
        {
            long generation = _session.Generation;
            Action guarded = () =>
            {
                // A routed/click completion from a recycled old surface must
                // never submit against whichever video opened afterward.
                if (_session.Active && generation == _session.Generation) action();
            };
            var button = MakeButton(label, guarded, id); button.IsEnabled = enabled;
            _rows.Add(button); _actions.Add(button, guarded); _body.Children.Add(button);
        }
        private Button MakeButton(string label, Action action, string id)
        {
            var button = new Button { Content = label, Style = (Style)Application.Current.Resources["TvRowButtonStyle"],
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
                FontSize = 18, Padding = new Thickness(16, 12, 16, 12) };
            AutomationProperties.SetAutomationId(button, id);
            button.Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 };
            button.Click += (sender, args) => { if (button.IsEnabled) action(); };
            button.GotFocus += (sender, args) => { _lastFocus = id; button.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false }); };
            return button;
        }
        private static TextBlock Text(string value, double size, bool strong = false)
            => new TextBlock { Text = value, FontSize = size, FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = Brush(242, 255, 255, 255) };
        private static SolidColorBrush Brush(byte alpha, byte red, byte green, byte blue)
            => new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
        private async void Run(Func<Task> operation)
        {
            long generation = _session.Generation;
            try { await operation(); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (Current(generation)) { _busy = false; _message = "操作未完成：" + ex.Message; Render(); RestoreFocus(); } }
        }
        private void NativeKeyDown(object sender, KeyRoutedEventArgs args)
        {
            if (GamepadKeyRouting.IsNavigationEvent(args)) { args.Handled = true; return; }
            PlayerDirection direction;
            if (args.Key == VirtualKey.Up) direction = PlayerDirection.Up;
            else if (args.Key == VirtualKey.Down) direction = PlayerDirection.Down;
            else if (args.Key == VirtualKey.Left) direction = PlayerDirection.Left;
            else if (args.Key == VirtualKey.Right) direction = PlayerDirection.Right;
            else if (args.Key == VirtualKey.Enter || args.Key == VirtualKey.Space)
            { args.Handled = true; Confirm(args.KeyStatus.WasKeyDown); return; }
            else return;
            args.Handled = true; Navigate(direction);
        }
        private void NativeKeyUp(object sender, KeyRoutedEventArgs args)
        { if (GamepadKeyRouting.IsNavigationEvent(args)) args.Handled = true; }
    }
}
