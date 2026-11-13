using System;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Models;
using BiliXbox.Services;
using Windows.System;
using Windows.UI;
using Windows.UI.Text;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace BiliXbox.Controls
{
    // One account surface: QR remains at the left; native account/password or
    // SMS editors remain in place at the right. It never stores credentials.
    public sealed partial class AccountLoginDialog : ContentDialog, IDisposable
    {
        private readonly BilibiliClient _api;
        private readonly Func<string, Task<BitmapImage>> _renderQr;
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private readonly Image _qrImage;
        private readonly TextBlock _qrHint, _notice, _modeTitle, _privacy;
        private readonly Button _refreshQr, _submitPassword, _sendSms, _submitSms, _switchMode, _cancelCaptcha, _verifySecondary;
        private readonly StackPanel _passwordForm, _smsForm, _passwordHost, _statusRow;
        private readonly Grid _challengeHost, _challengeBody;
        private readonly ScrollViewer _loginContent;
        private readonly ComboBox _country;
        private readonly ProgressRing _working;
        private readonly DispatcherTimer _cooldown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private readonly InputPane _inputPane;
        private TextBox _account, _phone, _code;
        private PasswordBox _password;
        private InlineNativeTextInputSession _input;
        private LoginCaptchaView _captcha;
        private CancellationTokenSource _qrOwner, _challengeOwner;
        private SmsLoginTicket _smsTicket;
        private DateTimeOffset _resendAt;
        private Uri _secondaryUri;
        private int _qrGeneration, _operation;
        private bool _closed, _busy, _smsMode, _passwordEditing, _nativePaneShown, _won;

        public AccountLoginDialog(BilibiliClient api, Func<string, Task<BitmapImage>> renderQr)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _renderQr = renderQr ?? throw new ArgumentNullException(nameof(renderQr));
            Title = "登录 Bilibili";
            CloseButtonText = "取消";
            DefaultButton = ContentDialogButton.None;
            RequiresPointer = RequiresPointer.Never;
            XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Enabled;
            Resources["ContentDialogMaxWidth"] = 1060d;
            Resources["ContentDialogMinWidth"] = 480d;
            Resources["ContentDialogMaxHeight"] = Math.Max(360, Window.Current.Bounds.Height - 28);
            Background = Brush(250, 22, 23, 29);
            Foreground = Brush(255, 242, 242, 247);
            BorderBrush = Brush(36, 255, 255, 255);
            BorderThickness = new Thickness(1);
            CornerRadius = new CornerRadius(24);
            AutomationProperties.SetAutomationId(this, "AccountLoginDialog");

            var body = new Grid { Width = Math.Min(940, Math.Max(460, Window.Current.Bounds.Width - 140)), ColumnSpacing = 32, Padding = new Thickness(8, 4, 8, 12) };
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.42, GridUnitType.Star) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.58, GridUnitType.Star) });
            var left = new StackPanel { Spacing = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(4, 4, 4, 0) };
            AutomationProperties.SetAutomationId(left, "AccountLoginQrColumn");
            var qrTitle = Label("扫码登录", 23, true); AutomationProperties.SetAutomationId(qrTitle, "AccountLoginQrTitle"); left.Children.Add(qrTitle);
            _qrImage = new Image { Width = 176, Height = 176, Stretch = Stretch.Uniform };
            AutomationProperties.SetAutomationId(_qrImage, "AccountLoginQrImage");
            AutomationProperties.SetName(_qrImage, "使用哔哩哔哩手机 App 扫描此二维码");
            left.Children.Add(new Border { Child = _qrImage, Background = Brush(255, 255, 255, 255), Padding = new Thickness(12), CornerRadius = new CornerRadius(16), HorizontalAlignment = HorizontalAlignment.Center });
            _qrHint = Label("正在获取二维码…", 15); _qrHint.MaxWidth = 270; _qrHint.TextAlignment = TextAlignment.Center; left.Children.Add(_qrHint);
            _refreshQr = Button("刷新二维码", "AccountLoginRefreshQr", () => RunQrAsync()); left.Children.Add(_refreshQr);
            var qrGuide = Label("打开哔哩哔哩手机 App\n扫一扫并确认登录", 13); qrGuide.TextAlignment = TextAlignment.Center; qrGuide.Foreground = Brush(153, 242, 242, 247); left.Children.Add(qrGuide);
            body.Children.Add(left);

            var right = new StackPanel { Spacing = 12, Padding = new Thickness(4, 4, 6, 10) };
            AutomationProperties.SetAutomationId(right, "AccountLoginCredentialColumn");
            Grid.SetColumn(right, 1); body.Children.Add(right);
            _modeTitle = Label("手机号 / 邮箱登录", 23, true); AutomationProperties.SetAutomationId(_modeTitle, "AccountLoginModeTitle"); right.Children.Add(_modeTitle);
            _passwordForm = new StackPanel { Spacing = 10 }; AutomationProperties.SetAutomationId(_passwordForm, "AccountLoginPasswordForm"); right.Children.Add(_passwordForm);
            _account = Editor("手机号或邮箱", "AccountLoginUsername", 254, InputScopeNameValue.EmailSmtpAddress); _passwordForm.Children.Add(_account);
            _passwordHost = new StackPanel(); _password = PasswordEditor(); _passwordHost.Children.Add(_password); _passwordForm.Children.Add(_passwordHost);
            _submitPassword = Button("登录", "AccountLoginPasswordSubmit", LoginPasswordAsync); _passwordForm.Children.Add(_submitPassword);

            _smsForm = new StackPanel { Spacing = 10, Visibility = Visibility.Collapsed }; AutomationProperties.SetAutomationId(_smsForm, "AccountLoginSmsForm"); right.Children.Add(_smsForm);
            var phoneRow = new Grid { ColumnSpacing = 8 };
            phoneRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(132) }); phoneRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _country = new ComboBox { PlaceholderText = "区号", HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 44, MaxDropDownHeight = 260, DisplayMemberPath = "DisplayName", IsEnabled = false, FontSize = 15, IsFocusEngagementEnabled = false };
            AutomationProperties.SetAutomationId(_country, "AccountLoginCountry"); AutomationProperties.SetName(_country, "手机国家或地区区号"); _country.SelectionChanged += (s, e) => InvalidateSmsTicket(); phoneRow.Children.Add(_country);
            _country.PreviewKeyDown += CountryKeyDown;
            _phone = Editor("手机号", "AccountLoginPhone", 20, InputScopeNameValue.TelephoneNumber); _phone.TextChanged += (s, e) => InvalidateSmsTicket(); Grid.SetColumn(_phone, 1); phoneRow.Children.Add(_phone); _smsForm.Children.Add(phoneRow);
            var codeRow = new Grid { ColumnSpacing = 8 };
            codeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); codeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _code = Editor("短信验证码", "AccountLoginSmsCode", 8, InputScopeNameValue.Number); codeRow.Children.Add(_code);
            _sendSms = Button("获取验证码", "AccountLoginSendSms", SendSmsAsync); _sendSms.FontSize = 15; _sendSms.Padding = new Thickness(15, 8, 15, 8); Grid.SetColumn(_sendSms, 1); codeRow.Children.Add(_sendSms); _smsForm.Children.Add(codeRow);
            _submitSms = Button("验证码登录", "AccountLoginSmsSubmit", LoginSmsAsync); _smsForm.Children.Add(_submitSms);
            _switchMode = Button("使用短信验证码登录", "AccountLoginSwitchMode", () => { SwitchMode(); return Task.CompletedTask; }); _switchMode.FontSize = 15; right.Children.Add(_switchMode);

            _statusRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            _working = new ProgressRing { Width = 20, Height = 20, IsActive = false, Visibility = Visibility.Collapsed }; _statusRow.Children.Add(_working);
            _notice = Label("", 14); _notice.MaxWidth = Math.Max(200, body.Width * 0.58 - 90); _statusRow.Children.Add(_notice); right.Children.Add(_statusRow);
            // Verification has its own full-width, bounded viewport. The
            // provider's image and confirmation controls must not be clipped
            // inside the short credential column or scroll away its exit.
            _challengeBody = new Grid { Visibility = Visibility.Collapsed, RowSpacing = 10 };
            _challengeBody.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _challengeBody.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _challengeBody.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            AutomationProperties.SetAutomationId(_challengeBody, "AccountLoginChallengeStage");
            var challengeGuide = Label("请完成 B 站安全验证；按 B 或选择下方按钮返回登录。", 15);
            _challengeBody.Children.Add(challengeGuide);
            _challengeHost = new Grid(); Grid.SetRow(_challengeHost, 1); _challengeBody.Children.Add(_challengeHost);
            _cancelCaptcha = Button("取消验证，返回登录", "AccountLoginCancelCaptcha", () => { CancelChallenge(); return Task.CompletedTask; });
            Grid.SetRow(_cancelCaptcha, 2); _challengeBody.Children.Add(_cancelCaptcha);
            _verifySecondary = Button("前往 B 站官方验证", "AccountLoginSecondary", OpenSecondaryAsync); _verifySecondary.Visibility = Visibility.Collapsed; right.Children.Add(_verifySecondary);
            _privacy = Label("登录信息加密保存在本机。密码和验证码不保存。\n使用验证码登录前，请确认该手机号属于你。", 12); _privacy.Foreground = Brush(128, 242, 242, 247); right.Children.Add(_privacy);
            _loginContent = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = Math.Max(260, Window.Current.Bounds.Height - 180), IsTabStop = false };
            var contentHost = new Grid { Width = body.Width };
            contentHost.Children.Add(_loginContent); contentHost.Children.Add(_challengeBody);
            Content = contentHost;
            _inputPane = InputPane.GetForCurrentView();
            _inputPane.Showing += PaneShowing; _inputPane.Hiding += PaneHiding;
            _cooldown.Tick += (s, e) => UpdateCooldown();
            Opened += OnOpened;
            Closing += OnClosing;
            Closed += (s, e) => Dispose();
            PreviewKeyDown += DialogKeyDown;
            // TextBox / PasswordBox class handlers can consume Xbox's mapped
            // Space before ordinary KeyDown subscribers see it.
            AddHandler(KeyDownEvent, new KeyEventHandler(DialogKeyDown), true);
        }

        private void ShowChallenge(bool visible)
        {
            _loginContent.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
            _challengeBody.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            // With no second dialog footer, all remaining height belongs to
            // the challenge and its fixed native return button.
            CloseButtonText = visible ? "" : "取消";
            if (visible)
            {
                _challengeBody.Height = Math.Max(220, Math.Min(620, Window.Current.Bounds.Height - 160));
                _challengeBody.UpdateLayout();
                _cancelCaptcha.Focus(FocusState.Keyboard);
            }
        }

        private TextBox Editor(string placeholder, string id, int maxLength, InputScopeNameValue scopeValue)
        {
            var editor = new TextBox { PlaceholderText = placeholder, MaxLength = maxLength, MinHeight = 44, FontSize = 17, Padding = new Thickness(16, 10, 16, 10), CornerRadius = new CornerRadius(15), IsReadOnly = true, IsSpellCheckEnabled = false, IsTextPredictionEnabled = false, IsFocusEngagementEnabled = false };
            var scope = new InputScope(); scope.Names.Add(new InputScopeName { NameValue = scopeValue }); editor.InputScope = scope;
            AutomationProperties.SetAutomationId(editor, id); AutomationProperties.SetName(editor, placeholder);
            AttachEditor(editor); return editor;
        }
        private void AttachEditor(TextBox editor)
        {
            KeyEventHandler activate = (s, e) => { if (_input?.IsActive == true || _passwordEditing) return; if (IsAcceptKey(e)) { e.Handled = true; _ = EditTextAsync(editor); } };
            editor.PreviewKeyDown += activate;
            editor.AddHandler(KeyDownEvent, activate, true);
            editor.AddHandler(TappedEvent, new TappedEventHandler((s, e) => { if (_input?.IsActive != true && !_passwordEditing) { e.Handled = true; _ = EditTextAsync(editor); } }), true);
        }
        private PasswordBox PasswordEditor()
        {
            var editor = new PasswordBox { PlaceholderText = "密码", MaxLength = 128, MinHeight = 44, FontSize = 17, Padding = new Thickness(16, 10, 16, 10), CornerRadius = new CornerRadius(15), PasswordRevealMode = PasswordRevealMode.Hidden, IsFocusEngagementEnabled = false };
            AutomationProperties.SetAutomationId(editor, "AccountLoginPassword"); AutomationProperties.SetName(editor, "密码");
            KeyEventHandler activate = (s, e) => { if (!_passwordEditing && _input?.IsActive != true && IsAcceptKey(e)) { e.Handled = true; EditPassword(); } };
            editor.PreviewKeyDown += activate;
            editor.AddHandler(KeyDownEvent, activate, true);
            editor.AddHandler(TappedEvent, new TappedEventHandler((s, e) => { if (!_passwordEditing && _input?.IsActive != true) { e.Handled = true; EditPassword(); } }), true);
            editor.LostFocus += (s, e) => { if (_passwordEditing && !ReferenceEquals(FocusManager.GetFocusedElement(), _password)) EndPassword(false); };
            return editor;
        }
        private static Button Button(string text, string id, Func<Task> action)
        {
            var button = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Stretch, IsFocusEngagementEnabled = false, Style = Application.Current.Resources["TvButtonStyle"] as Style };
            AutomationProperties.SetAutomationId(button, id); button.Click += async (s, e) => await action(); return button;
        }
        private static TextBlock Label(string text, double size, bool bold = false) => new TextBlock { Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap, Foreground = Brush(242, 242, 242, 247) };
        private static SolidColorBrush Brush(byte a, byte r, byte g, byte b) => new SolidColorBrush(Color.FromArgb(a, r, g, b));
    }
}
