// TEST ONLY: compile the unchanged production view/data partials. No actual
// XAML, native controller events, account credentials or HTTP writes occur.
namespace Windows.UI
{
    public struct Color { public static Color FromArgb(byte a, byte r, byte g, byte b) => new Color(); }
}
namespace Windows.UI.Text
{
    public struct FontWeight { }
    public static class FontWeights { public static FontWeight Normal, SemiBold; }
}
namespace Windows.System { public enum VirtualKey { Left, Right, Up, Down, Enter, Space } }
namespace Windows.UI.Xaml
{
    public enum Visibility { Visible, Collapsed }
    public enum HorizontalAlignment { Stretch, Left, Center, Right }
    public enum VerticalAlignment { Stretch, Top, Center, Bottom }
    public enum FocusState { Keyboard, Programmatic }
    public enum TextWrapping { Wrap }
    public enum GridUnitType { Star }
    public struct Thickness { public Thickness(double a, double b, double c, double d) { } }
    public struct GridLength { public GridLength(double n, GridUnitType type) { } public static GridLength Auto => new GridLength(); }
    public class Style { }
    public class RoutedEventArgs : EventArgs { }
    public class BringIntoViewOptions { public bool AnimationDesired { get; set; } }
    public class UIElement
    {
        public Visibility Visibility { get; set; }
        public bool IsEnabled { get; set; } = true;
        public bool IsTabStop { get; set; }
        public string AutomationId;
        public bool Focus(FocusState state) { Input.FocusManager.Current = this; GotFocus?.Invoke(this, new RoutedEventArgs()); return true; }
        public void StartBringIntoView(BringIntoViewOptions options) { }
        public event EventHandler<RoutedEventArgs> GotFocus;
        public event EventHandler<RoutedEventArgs> Unloaded;
        public event EventHandler<Input.KeyRoutedEventArgs> PreviewKeyDown;
        public event EventHandler<Input.KeyRoutedEventArgs> PreviewKeyUp;
    }
    public class FrameworkElement : UIElement
    {
        public double Width, Height, MaxWidth;
        public Thickness Padding;
        public object Background, Foreground;
        public HorizontalAlignment HorizontalAlignment;
        public VerticalAlignment VerticalAlignment;
    }
    public sealed class Application
    {
        public static Application Current { get; } = new Application();
        public Dictionary<string, object> Resources { get; } = new Dictionary<string, object> { ["TvRowButtonStyle"] = new Style() };
    }
}
namespace Windows.UI.Xaml.Media { public sealed class SolidColorBrush { public SolidColorBrush(Color color) { } } }
namespace Windows.UI.Xaml.Automation
{
    public static class AutomationProperties
    {
        public static void SetAutomationId(UIElement element, string id) { element.AutomationId = id; }
        public static string GetAutomationId(UIElement element) => element.AutomationId;
    }
}
namespace Windows.UI.Xaml.Input
{
    public enum XYFocusKeyboardNavigationMode { Enabled }
    public struct KeyStatus { public bool WasKeyDown; }
    public sealed class KeyRoutedEventArgs : EventArgs { public bool Handled; public VirtualKey Key; public KeyStatus KeyStatus; }
    public static class FocusManager { public static object Current; public static object GetFocusedElement() => Current; }
}
namespace Windows.UI.Xaml.Controls
{
    public enum RequiresPointer { Never }
    public enum ScrollMode { Disabled, Auto }
    public enum ScrollBarVisibility { Disabled, Hidden }
    public enum ZoomMode { Disabled }
    public class Control : FrameworkElement
    {
        public object Content;
        public Style Style;
        public RequiresPointer RequiresPointer;
        public XYFocusKeyboardNavigationMode XYFocusKeyboardNavigation;
        public HorizontalAlignment HorizontalContentAlignment;
        public VerticalAlignment VerticalContentAlignment;
        public double FontSize;
        public FontWeight FontWeight;
    }
    public class UserControl : Control { }
    public class Button : Control { public event EventHandler<RoutedEventArgs> Click; }
    public class TextBlock : Control { public string Text; public TextWrapping TextWrapping; }
    public class ProgressRing : Control { public bool IsActive; }
    public class Panel : Control { public List<UIElement> Children { get; } = new List<UIElement>(); }
    public class StackPanel : Panel { public double Spacing; }
    public class Grid : Panel
    {
        public double RowSpacing, ColumnSpacing;
        public List<RowDefinition> RowDefinitions { get; } = new List<RowDefinition>();
        public List<ColumnDefinition> ColumnDefinitions { get; } = new List<ColumnDefinition>();
        public static void SetRow(UIElement item, int index) { }
        public static void SetColumn(UIElement item, int index) { }
    }
    public class RowDefinition { public GridLength Height; }
    public class ColumnDefinition { public GridLength Width; }
    public class ScrollViewer : Control
    {
        public ScrollMode HorizontalScrollMode, VerticalScrollMode;
        public ScrollBarVisibility HorizontalScrollBarVisibility, VerticalScrollBarVisibility;
        public ZoomMode ZoomMode;
    }
}
namespace BiliXbox.Controls
{
    internal enum PlayerDirection { Left, Right, Up, Down }
    internal static class GamepadKeyRouting { internal static bool IsNavigationEvent(KeyRoutedEventArgs args) => false; }
}
namespace BiliXbox.Services
{
    public sealed class BilibiliClient
    {
        public UserProfile CurrentUser { get; set; } = new UserProfile { Mid = 30, Name = "Fixture" };
        public bool IsLoggedIn => CurrentUser != null;
        public VideoInteractionState State = new VideoInteractionState { Aid = 200, AccountMid = 30 };
        public IReadOnlyList<VideoFavoriteFolder> Folders = new[] { new VideoFavoriteFolder { Id = 1, Title = "Default", IsDefault = true },
            new VideoFavoriteFolder { Id = 2, Title = "Other" } };
        public int Reads, LikePosts, CoinPosts, FavoritePosts, TriplePosts;
        public Func<long, CancellationToken, Task<VideoInteractionState>> Read;
        public Func<long, CancellationToken, Task<IReadOnlyList<VideoFavoriteFolder>>> ReadFolders;
        public Func<Task> WriteLike;
        public Func<Task<VideoTripleResult>> WriteTriple;
        public Task<VideoInteractionState> GetVideoInteractionsAsync(long aid, CancellationToken token)
        {
            Reads++;
            return Read != null ? Read(aid, token) : Task.FromResult(new VideoInteractionState { Aid = aid,
                AccountMid = CurrentUser?.Mid ?? 0, IsLiked = State.IsLiked, IsFavorited = State.IsFavorited, Coins = State.Coins });
        }
        public Task<IReadOnlyList<VideoFavoriteFolder>> GetVideoFavoriteFoldersAsync(long aid, CancellationToken token)
            => ReadFolders != null ? ReadFolders(aid, token) : Task.FromResult(Folders);
        public async Task SetVideoLikeAsync(long aid, bool value, CancellationToken token)
        { LikePosts++; if (WriteLike != null) await WriteLike(); else State.IsLiked = value; }
        public Task AddVideoCoinsAsync(long aid, int value, CancellationToken token)
        { CoinPosts++; State.Coins += value; return Task.CompletedTask; }
        public Task UpdateVideoFavoritesAsync(long aid, IEnumerable<long> add, IEnumerable<long> remove, CancellationToken token)
        {
            FavoritePosts++;
            foreach (var folder in Folders) { if (add.Contains(folder.Id)) folder.IsSelected = true; if (remove.Contains(folder.Id)) folder.IsSelected = false; }
            State.IsFavorited = Folders.Any(folder => folder.IsSelected); return Task.CompletedTask;
        }
        public Task<VideoTripleResult> TripleVideoAsync(long aid, CancellationToken token)
        {
            TriplePosts++;
            return WriteTriple != null ? WriteTriple() : Task.FromResult(new VideoTripleResult());
        }
    }
}
