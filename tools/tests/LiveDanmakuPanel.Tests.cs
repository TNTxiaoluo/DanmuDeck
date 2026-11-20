namespace Windows.UI.Xaml { public enum Visibility { Visible, Collapsed } }
namespace Windows.UI.Xaml.Controls
{
    public sealed class TextBlock { public string Text; }
    public sealed class Button { public object Content; public System.Action Click; }
}
namespace BiliXbox.Models { public sealed class DanmakuItem { } }
namespace BiliXbox.Controls
{
    public sealed partial class LiveHubView
    {
        private readonly PanelOverlay _overlay = new PanelOverlay();
        private readonly Button _danmakuButton = new Button();
        private readonly TextBlock _drawerNotice = new TextBlock(), _watchNotice = new TextBlock();
        private readonly List<Button> rows = new List<Button>();
        private Button focus;
        private bool _danmaku;
        private int _overlayAdmissions;
        private Button Row(string label, Action action, string id)
        { var button=new Button { Content=new TextBlock { Text=label },Click=action };rows.Add(button);return button; }
        private void FocusFirstRow() { focus=rows[0]; }
        private static string Friendly(Exception ex) => ex.GetType().Name;
        private static int checks;
        private static void Check(bool ok,string why) { ++checks;if(!ok)throw new Exception(why); }
        public static string RunPanelTests()
        {
            Windows.Storage.ApplicationData.Current.LocalSettings.Values.Clear();
            var video = new DanmakuSettingsStore();video.Save(DanmakuSettings.Default.With(opacity:.75));
            var page=new LiveHubView();page.InitializeLiveDanmakuSettings();page.RenderLiveDanmakuSettings();
            Check(page.rows.Count==14&&page.focus==page._liveSettingButtons["enabled"],"settings initially focus first actionable row");
            Check(page._liveSettings.Opacity==.9&&!page._liveSettings.SmartMask,"live uses independent defaults");
            var font=page._liveSettingButtons["font"];page.focus=font;font.Click();
            Check(page.focus==font&&page._liveSettingButtons["font"]==font,"A cycles a value without rebuilding focused row");
            Check(page._overlay.Settings.FontScale==1.25&&((TextBlock)font.Content).Text.Contains("1.25"),"renderer and visible label update immediately");
            page._overlayAdmissions=99;page._liveSettingButtons["enabled"].Click();
            Check(!page._danmaku&&page._overlay.Visibility==Visibility.Collapsed&&page._overlay.Resets==1&&page._overlayAdmissions==0,"settings switch stops current live animations");
            Check(!page._liveSettingsStore.LoadEnabled(),"settings switch persists");
            var reopened=new LiveHubView();reopened.InitializeLiveDanmakuSettings();reopened.RenderLiveDanmakuSettings();
            Check(!reopened._danmaku&&reopened._liveSettings.FontScale==1.25,"new live view restores options and disabled switch");
            reopened._liveSettingButtons["reset"].Click();
            Check(reopened._danmaku&&reopened._liveSettings.FontScale==1&&!reopened._liveSettings.SmartMask,"reset restores live preferences and enables overlay");
            Check(video.Load().Opacity==.75,"reset cannot modify video preferences");
            Check(reopened.focus==reopened.rows[0]&&reopened.rows.Count==14,"reset preserves existing control tree");
            return "PASS: "+checks+" production live settings-panel focus, immediate rendering, persistence and reset checks (UI substitutes).";
        }
    }
    internal sealed class PanelOverlay
    {
        internal DanmakuSettings Settings; internal Visibility Visibility; internal int Resets;
        internal void ApplySettings(DanmakuSettings s) { Settings=s; }
        internal void Reset() { ++Resets; }
        internal void Load(IReadOnlyList<DanmakuItem> items) { }
    }
}
