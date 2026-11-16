namespace Windows.Storage
{
    public sealed class ApplicationData
    {
        public static ApplicationData Current = new ApplicationData();
        public Settings LocalSettings = new Settings();
    }
    public sealed class Settings
    {
        public sealed class SafeValues : System.Collections.Generic.Dictionary<string,object>
        {
            public new object this[string key] { get { object value; return TryGetValue(key, out value) ? value : null; } set { base[key] = value; } }
        }
        public SafeValues Values = new SafeValues();
    }
}
namespace Windows.UI.Xaml { public enum Visibility { Visible,Collapsed } public class RoutedEventArgs : System.EventArgs { } }
namespace Windows.UI.Xaml.Controls.Primitives
{
    public sealed class RangeBaseValueChangedEventArgs : Windows.UI.Xaml.RoutedEventArgs { public double NewValue; }
}
namespace BiliXbox
{
    public sealed partial class MainPage
    {
        private sealed class FakeMedia { public double Volume = 1; public bool IsMuted; }
        private sealed class FakeLabel { public string Text; public object Content; }
        private sealed class FakeSlider
        {
            private double value;
            public double Value { get { return value; } set { this.value = value;
                ValueChanged?.Invoke(this,new Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs { NewValue = value }); } }
            public event System.EventHandler<Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs> ValueChanged;
        }
        private sealed class FakeUi { public void Touch(System.DateTimeOffset now) { } }
        private readonly FakeMedia _media = new FakeMedia();
        private readonly FakeSlider VolumeSlider = new FakeSlider();
        private readonly FakeLabel VolumeLabel = new FakeLabel(), VolumeButton = new FakeLabel();
        private sealed class FakePanel { public Windows.UI.Xaml.Visibility Visibility; }
        private readonly FakePanel VolumeEditPanel=new FakePanel();
        private readonly FakeUi _playerUi = new FakeUi();
        private bool IgnorePlayerClick() => false;
        private void SetStatus(string value) { }
        private static int checks;
        private static void Check(bool condition, string name) { if (!condition) throw new System.Exception(name); checks++; }
        public static string RunVolumeTests()
        {
            var values = Windows.Storage.ApplicationData.Current.LocalSettings.Values;
            values.Clear(); var page = new MainPage(); page.InitializeVolume();
            Check(page._media.Volume == 1 && page.VolumeSlider.Value == 100 && page.VolumeLabel.Text == "100%", "Default full volume");
            page.VolumeSlider.Value = 45;
            Check(page._media.Volume == .45 && (double)values["playerVolume"] == .45 && page.VolumeLabel.Text == "45%", "Slider changes native player and saves preference");
            page.VolumeMute_Click(null,null); Check(page._media.IsMuted && page._media.Volume == .45, "Mute preserves stored volume");
            page.VolumeMute_Click(null,null); Check(!page._media.IsMuted && page._media.Volume == .45, "Unmute restores unchanged level");
            page.VolumeSlider.Value = 0; page.VolumeMute_Click(null,null);
            Check(page._media.Volume == .45 && !page._media.IsMuted, "Unmute at zero restores last audible level");
            page.VolumeMute_Click(null,null); page.VolumeSlider.Value = 30;
            Check(!page._media.IsMuted && page._media.Volume == .3, "Raising slider unmutes");
            var reopened = new MainPage(); reopened.InitializeVolume(); Check(reopened._media.Volume == .3, "Volume persists across page recreation");
            values["playerVolume"] = -2d; page = new MainPage(); page.InitializeVolume(); Check(page._media.Volume == 0, "Negative saved value clamped");
            values["playerVolume"] = 2d; page = new MainPage(); page.InitializeVolume(); Check(page._media.Volume == 1, "Excess saved value clamped");
            values["playerVolume"] = double.NaN; page = new MainPage(); page.InitializeVolume(); Check(page._media.Volume == 1, "Invalid saved value cannot crash player");
            Check(page.VolumeEditPanel.Visibility == Windows.UI.Xaml.Visibility.Collapsed, "Volume panel starts collapsed");
            page.VolumeSelect_Click(null,null); Check(page._adjustingVolume && page.VolumeEditPanel.Visibility == Windows.UI.Xaml.Visibility.Visible, "A enters visible edit mode");
            page.VolumeSelect_Click(null,null); Check(!page._adjustingVolume && page.VolumeEditPanel.Visibility == Windows.UI.Xaml.Visibility.Collapsed, "A leaves edit mode and collapses panel");
            Check((string)page.VolumeButton.Content == "音量 100%", "Bottom button reflects current percentage");
            return "PASS: " + checks + " production volume, mute, preference and edit-toggle assertions (UI/media substitutes).";
        }
    }
}
