namespace Windows.UI.Core
{
    public struct CorePhysicalKeyStatus { public bool WasKeyDown; }
}
namespace Windows.UI.Xaml
{
    public struct Thickness { public Thickness(double a, double b, double c, double d) { } }
    public struct CornerRadius { public CornerRadius(double value) { } }
}
namespace Windows.UI.Xaml.Input
{
    public sealed class TappedRoutedEventArgs { public bool Handled; }
    public delegate void TappedEventHandler(object sender, TappedRoutedEventArgs args);
}
namespace Windows.UI.Xaml.Controls
{
    public enum PasswordRevealMode { Hidden }
    public sealed partial class TextBox
    {
        public bool IsTextPredictionEnabled;
        public event KeyEventHandler PreviewKeyDown;
        private KeyEventHandler _handledKey;
        private TappedEventHandler _tap;
        public void AddHandler(object routedEvent, KeyEventHandler action, bool handledEventsToo)
        { if (!handledEventsToo) throw new Exception("Must observe handled control keys."); _handledKey += action; }
        public void AddHandler(object routedEvent, TappedEventHandler action, bool handledEventsToo)
        { if (!handledEventsToo) throw new Exception("Must observe handled native taps."); _tap += action; }
        public bool MappedKey(VirtualKey mapped, VirtualKey original, bool consumed = false, bool preview = true)
        {
            var args = new KeyRoutedEventArgs { Key = mapped, OriginalKey = original, Handled = consumed };
            if (preview) PreviewKeyDown?.Invoke(this, args);
            _handledKey?.Invoke(this, args); return args.Handled;
        }
        public bool HandledTap()
        { var args = new TappedRoutedEventArgs { Handled = true }; _tap?.Invoke(this, args); return args.Handled; }
    }
    public sealed class PasswordBox
    {
        public string PlaceholderText, Password = "";
        public int MaxLength, FocusAttempts, FocusFailuresRemaining, LayoutCalls;
        public double MinHeight, FontSize;
        public Thickness Padding;
        public CornerRadius CornerRadius;
        public PasswordRevealMode PasswordRevealMode;
        public bool IsFocusEngagementEnabled, IsFocusEngaged;
        public CoreDispatcher Dispatcher = new CoreDispatcher();
        public event KeyEventHandler PreviewKeyDown;
        public event EventHandler<RoutedEventArgs> LostFocus;
        private KeyEventHandler _handledKey;
        private TappedEventHandler _tap;
        public void AddHandler(object routedEvent, KeyEventHandler action, bool handledEventsToo)
        { if (!handledEventsToo) throw new Exception("Must observe handled password keys."); _handledKey += action; }
        public void AddHandler(object routedEvent, TappedEventHandler action, bool handledEventsToo)
        { if (!handledEventsToo) throw new Exception("Must observe handled password taps."); _tap += action; }
        public bool MappedKey(VirtualKey mapped, VirtualKey original, bool preview = true)
        {
            var args = new KeyRoutedEventArgs { Key = mapped, OriginalKey = original, Handled = !preview };
            if (preview) PreviewKeyDown?.Invoke(this, args);
            _handledKey?.Invoke(this, args); return args.Handled;
        }
        public void Tap() { _tap?.Invoke(this, new TappedRoutedEventArgs { Handled = true }); }
        public void LoseFocus() { FocusManager.Focused = new object(); LostFocus?.Invoke(this, new RoutedEventArgs()); }
        public void UpdateLayout() { ++LayoutCalls; }
        public bool Focus(FocusState state)
        {
            ++FocusAttempts;
            if (FocusFailuresRemaining > 0) { --FocusFailuresRemaining; return false; }
            FocusManager.Focused = this; return true;
        }
    }
    public sealed class ComboBox
    {
        public bool IsDropDownOpen, IsEnabled = true;
    }
    public sealed class Button
    {
        public int Focuses;
        public bool Focus(FocusState state) { ++Focuses; FocusManager.Focused = this; return true; }
    }
    public sealed class TextBlock { public string Text = ""; }
    public sealed class StackPanel { public global::System.Collections.Generic.List<object> Children = new global::System.Collections.Generic.List<object>(); }
}
namespace BiliXbox.Controls
{
    internal static class InlineTextBoxFactory
    {
        internal static TextBox Replace(TextBox previous, int limit)
        {
            // Only editor renewal identity is substituted; actual XAML reparenting
            // is covered by a native probe, not this keyboard lifecycle harness.
            previous.IsFocusEngaged = false;
            return new TextBox { Text = previous.Text, MaxLength = limit, IsFocusEngagementEnabled = true };
        }
    }
    public sealed partial class AccountLoginDialog : ContentDialog
    {
        private static readonly object TappedEvent = new object();
        private TextBox _account = new TextBox { MaxLength = 254, IsReadOnly = true }, _phone = new TextBox { MaxLength = 20, IsReadOnly = true }, _code = new TextBox { MaxLength = 8, IsReadOnly = true };
        private PasswordBox _password;
        private readonly StackPanel _passwordHost = new StackPanel();
        private readonly Button _submitPassword = new Button();
        private readonly ComboBox _country = new ComboBox();
        private readonly TextBlock _notice = new TextBlock();
        private readonly InputPane _inputPane;
        private InlineNativeTextInputSession _input;
        private bool _closed, _busy, _passwordEditing, _nativePaneShown, _won;
        private object _captcha;
        private global::System.Threading.CancellationTokenSource _challengeOwner;
        private int _passwordInputGeneration, CountryInvalidations;
        private DateTimeOffset _lastKeyboardBack;
        private void InvalidateSmsTicket() { ++CountryInvalidations; }
        private void CancelChallenge() { _captcha = null; }

        private AccountLoginDialog()
        {
            InputPane.Reset(); CoreInputView.Reset();
            ApiInformation.Available = true;
            _inputPane = InputPane.GetForCurrentView();
            _inputPane.Showing += PaneShowing; _inputPane.Hiding += PaneHiding;
            _password = PasswordEditor(); _passwordHost.Children.Add(_password);
            AttachEditor(_account); AttachEditor(_phone); AttachEditor(_code);
            Closing += OnClosing;
        }
        private static int _assertions;
        private static void Check(bool value, string message)
        { ++_assertions; if (!value) throw new Exception(message); }
        private static KeyRoutedEventArgs Key(VirtualKey mapped, VirtualKey original)
            => new KeyRoutedEventArgs { Key = mapped, OriginalKey = original };
        private static void Pump(TextBox box) { box.Dispatcher.Flush(); }
        public static void RunInputTests()
        {
            _assertions = 0;
            Check(IsAcceptKey(Key(VirtualKey.Space, VirtualKey.GamepadA)), "Xbox A mapped to Space is an activation.");
            Check(IsAcceptKey(Key(VirtualKey.Space, VirtualKey.NavigationAccept)), "Mapped NavigationAccept is recognized.");
            Check(!IsAcceptKey(Key(VirtualKey.Space, VirtualKey.Space)), "A physical Space remains native text.");
            Check(IsBackKey(Key(VirtualKey.Back, VirtualKey.GamepadB)), "Xbox B mapped to Back is recognized.");
            Check(IsBackKey(Key(VirtualKey.Back, VirtualKey.NavigationCancel)), "Mapped navigation cancellation is recognized.");

            var dialog = new AccountLoginDialog(); var previous = dialog._account;
            Check(previous.MappedKey(VirtualKey.Space, VirtualKey.GamepadA), "One mapped A is handled by the account editor.");
            Check(dialog._input?.IsActive == true && !ReferenceEquals(previous, dialog._account), "One mapped A starts a fresh native editor session.");
            Pump(dialog._account);
            Check(CoreInputView.GetForCurrentView().ShowCount == 1 && dialog._account.IsFocusEngaged && !dialog._account.IsReadOnly, "One A requests exactly one native keyboard and owns the same visible editor.");
            dialog._account.MappedKey(VirtualKey.Space, VirtualKey.GamepadA);
            Check(CoreInputView.GetForCurrentView().ShowCount == 1, "A inside native editing cannot duplicate keyboard activation.");
            var cancel = Key(VirtualKey.Back, VirtualKey.GamepadB); dialog.DialogKeyDown(dialog, cancel);
            Check(cancel.Handled && !dialog.ClosedState && dialog._input == null, "First mapped B ends editing while login stays open.");
            Check(dialog._account.IsReadOnly && !dialog._account.IsFocusEngagementEnabled, "B restores editor navigation without stale focus engagement.");
            dialog.RequestBack(); dialog.PlatformBack();
            Check(!dialog.ClosedState, "Duplicate routed, shell and dialog B delivery cannot dismiss two layers.");
            previous = dialog._account; previous.MappedKey(VirtualKey.Space, VirtualKey.GamepadA); Pump(dialog._account);
            Check(CoreInputView.GetForCurrentView().ShowCount == 2 && !ReferenceEquals(previous, dialog._account), "A after B reopens a renewed native editor.");
            dialog.EndInput(false); dialog._lastKeyboardBack = DateTimeOffset.UtcNow.AddSeconds(-1); dialog.RequestBack();
            Check(dialog.ClosedState, "A later distinct B may close login after the keyboard is gone.");

            dialog = new AccountLoginDialog(); previous = dialog._phone;
            previous.MappedKey(VirtualKey.Space, VirtualKey.GamepadA, consumed: true, preview: false); Pump(dialog._phone);
            Check(CoreInputView.GetForCurrentView().ShowCount == 1 && dialog._input?.IsActive == true, "A consumed by a TextBox class handler still activates via handledEventsToo.");
            dialog.EndInput(false);
            dialog._code.HandledTap(); Pump(dialog._code);
            Check(CoreInputView.GetForCurrentView().ShowCount == 2 && dialog._input?.IsActive == true, "An already-handled native click opens the SMS-code keyboard.");
            dialog.EndInput(false);
            dialog._busy = true; previous = dialog._account; previous.MappedKey(VirtualKey.Space, VirtualKey.GamepadA);
            Check(ReferenceEquals(previous, dialog._account) && dialog._input == null, "Busy form cannot open another editor.");

            dialog = new AccountLoginDialog(); var country = Key(VirtualKey.Space, VirtualKey.GamepadA);
            dialog.CountryKeyDown(dialog._country, country);
            Check(country.Handled && dialog._country.IsDropDownOpen, "Country list opens on the first mapped A.");
            var selecting = Key(VirtualKey.Space, VirtualKey.GamepadA); dialog.CountryKeyDown(dialog._country, selecting);
            Check(!selecting.Handled, "Open country list keeps native A selection.");
            dialog.RequestBack(); Check(!dialog._country.IsDropDownOpen && !dialog.ClosedState, "B closes country choices before login.");
            dialog._country.IsEnabled = false; country = Key(VirtualKey.Space, VirtualKey.GamepadA); dialog.CountryKeyDown(dialog._country, country);
            Check(!country.Handled && !dialog._country.IsDropDownOpen, "Disabled country selector cannot open.");
            dialog = new AccountLoginDialog(); dialog._country.IsDropDownOpen = true; dialog.PlatformBack();
            Check(!dialog._country.IsDropDownOpen && !dialog.ClosedState, "ContentDialog's own back route cannot dismiss login through an open country list.");

            dialog = new AccountLoginDialog(); dialog._password.Password = "offline secret";
            var oldPassword = dialog._password; oldPassword.MappedKey(VirtualKey.Space, VirtualKey.GamepadA);
            Check(dialog._passwordEditing && !ReferenceEquals(oldPassword, dialog._password) && oldPassword.Password == "", "Mapped A renews the protected PasswordBox and erases its previous secret.");
            Check(dialog._password.Password == "offline secret" && CoreInputView.GetForCurrentView().ShowCount == 0, "Password stays protected while the native focus request waits for layout.");
            dialog._password.Dispatcher.Flush();
            Check(CoreInputView.GetForCurrentView().ShowCount == 1 && dialog._password.IsFocusEngaged && dialog._password.LayoutCalls == 1, "Password keyboard opens only after its layout/focus dispatcher pass.");
            dialog.DialogKeyDown(dialog, Key(VirtualKey.Back, VirtualKey.GamepadB));
            Check(!dialog._passwordEditing && !dialog.ClosedState && !dialog._password.IsFocusEngagementEnabled, "B leaves password keyboard without closing login or retaining engagement.");
            dialog._password.MappedKey(VirtualKey.Space, VirtualKey.GamepadA, preview: false); dialog._password.Dispatcher.Flush();
            Check(CoreInputView.GetForCurrentView().ShowCount == 2 && dialog._passwordEditing, "Handled mapped A can reopen a protected PasswordBox.");

            dialog = new AccountLoginDialog(); dialog._password.Tap(); var queued = dialog._password;
            dialog.RequestBack(); queued.Dispatcher.Flush();
            Check(CoreInputView.GetForCurrentView().ShowCount == 0 && !dialog._passwordEditing && !dialog.ClosedState, "B before deferred password focus prevents a late keyboard from appearing.");
            dialog._password.Tap(); var older = dialog._password; dialog.EndPassword(false);
            dialog._password.Tap(); var newer = dialog._password;
            older.Dispatcher.Flush(); newer.Dispatcher.Flush();
            Check(CoreInputView.GetForCurrentView().ShowCount == 1 && ReferenceEquals(FocusManager.GetFocusedElement(), newer), "A cancelled generation cannot steal focus from the newer password session.");

            dialog = new AccountLoginDialog(); dialog._captcha = new object();
            var repeatedBack = Key(VirtualKey.Back, VirtualKey.GamepadB);
            repeatedBack.KeyStatus = new CorePhysicalKeyStatus { WasKeyDown = true };
            dialog.DialogKeyDown(dialog, repeatedBack);
            Check(repeatedBack.Handled && dialog._captcha != null, "Held B repeat is consumed without cancelling another layer.");
            dialog.DialogKeyDown(dialog, Key(VirtualKey.Back, VirtualKey.GamepadB));
            Check(dialog._captcha == null && !dialog.ClosedState, "A fresh B cancels verification while preserving login.");
            dialog._lastKeyboardBack = DateTimeOffset.UtcNow.AddSeconds(-1); dialog.DialogKeyDown(dialog, repeatedBack);
            Check(!dialog.ClosedState, "Held B cannot dismiss restored login after its duplicate-event grace interval.");
            Console.WriteLine("PASS: " + _assertions + " production login input assertions (mapped Xbox events; native platform substitutes; no authentication or SMS).");
        }
    }
}
