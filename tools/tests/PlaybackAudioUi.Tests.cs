using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Controls;
using BiliXbox.Models;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Windows.UI.Xaml
{
    public class RoutedEventArgs : EventArgs { }
    public enum TextWrapping { Wrap }
    public enum HorizontalAlignment { Left, Stretch }
    public enum FocusState { Keyboard }
    public struct Thickness { public Thickness(double left, double top, double right, double bottom) { } }
    public sealed class Style { }
    public sealed class Application
    {
        public static Application Current = new Application();
        public readonly Dictionary<string, object> Resources = new Dictionary<string, object> { ["TvRowButtonStyle"] = new Style() };
    }
}
namespace Windows.UI.Xaml.Input { }
namespace Windows.UI.Xaml.Controls
{
    public class Control { }
    public sealed class TextBlock : Control { public string Text; public TextWrapping TextWrapping; }
    public sealed class StackPanel : Control { public List<object> Children = new List<object>(); }
    public sealed class Button : Control
    {
        public Style Style; public object Content, Tag; public bool IsEnabled = true;
        public HorizontalAlignment HorizontalAlignment, HorizontalContentAlignment;
        public double FontSize; public Thickness Padding;
        public event EventHandler<RoutedEventArgs> Click;
        public bool Focus(FocusState state) { if (!IsEnabled) return false; FocusManager.Current = this; return true; }
    }
    public static class FocusManager { public static object Current; public static object GetFocusedElement() => Current; }
}
namespace BiliXbox.Controls
{
    internal enum PlayerDrawer { None, Settings, Comments }
    internal enum PlayerInputMode { Playback, Controls }
    internal sealed class AudioUiState { public PlayerDrawer Drawer = PlayerDrawer.Settings; }
    internal sealed class AudioInputState { public PlayerInputMode Mode = PlayerInputMode.Controls; }
}
namespace BiliXbox
{
    public sealed partial class MainPage
    {
        private sealed class AudioApi { public bool IsLoggedIn = true; public UserProfile CurrentUser = new UserProfile { Mid = 42, IsPremium = true }; }
        private readonly AudioApi _api = new AudioApi();
        private readonly AudioUiState _playerUi = new AudioUiState();
        private readonly AudioInputState _playerInput = new AudioInputState();
        private readonly StackPanel AudioOptions = new StackPanel();
        private readonly Button SettingsCloseButton = new Button();
        private readonly TextBlock AudioHint = new TextBlock();
        private VideoDetail _video = new VideoDetail { Aid = 100, Bvid = "BV1xx411c7mD", Cid = 200 };
        private long _currentCid = 200;
        private int _partGeneration = 1;
        private bool _disposed, _watching = true, _libraryVisible, _creatorVisible, _dialogOpen, _ignoreClick;
        private bool _inputPaneVisible, IsNativeGamepadEditorActive;
        private CancellationTokenSource _watchCts = new CancellationTokenSource(), _qualityCts;
        private PlaybackInfo _playback;
        private int _busy, _closed, _loaded;
        private TaskCompletionSource<bool> _idle;
        private long _loadedCid;
        private int? _loadedQuality;
        private bool _preserved;
        private PlaybackInfo _prepared;
        private TimeSpan _position = TimeSpan.FromSeconds(47), _loadedPosition;
        private bool _playing = true, _loadedPlaying;
        private string _status;
        private int _requestedQuality;
        private PlaybackAudioMode? _requestedMode;
        private Func<CancellationToken, Task<PlaybackInfo>> _response;
        private bool IgnorePlayerClick() => _ignoreClick;
        private Task<PlaybackInfo> GetCurrentPlaybackAsync(VideoDetail video, int quality, CancellationToken token, PlaybackAudioMode? mode = null)
        { _requestedQuality = quality; _requestedMode = mode; return _response(token); }
        private Task LoadPartAsync(long cid, int? quality = null, bool preservePosition = false, PlaybackInfo preparedPlayback = null)
        {
            ++_loaded; _loadedCid = cid; _loadedQuality = quality; _preserved = preservePosition;
            _loadedPosition = _position; _loadedPlaying = _playing; _prepared = preparedPlayback;
            return Task.CompletedTask;
        }
        private void CloseDrawer() { ++_closed; _playerUi.Drawer = PlayerDrawer.None; }
        private void SetStatus(string value) { _status = value; }
        private static string Friendly(Exception error) => error.Message;
        private void SetBusy(bool value)
        {
            if (value) { if (_busy == 0) _idle = new TaskCompletionSource<bool>(); ++_busy; }
            else { --_busy; if (_busy == 0) _idle.TrySetResult(true); }
        }
        private static void Cancel(ref CancellationTokenSource source)
        { var previous = source; source = null; if (previous != null) { previous.Cancel(); previous.Dispose(); } }
        private static PlaybackInfo Info(PlaybackAudioMode mode = PlaybackAudioMode.Standard, int quality = 120) => new PlaybackInfo
        {
            AudioMode = mode, QualityId = quality, AudioCodec = mode == PlaybackAudioMode.HiRes ? "fLaC"
                : mode == PlaybackAudioMode.Dolby ? "ec-3" : "mp4a.40.2",
            AvailableAudioModes = new[] {
                new PlaybackAudioOption { Mode = PlaybackAudioMode.Standard, Label = "标准音质", IsAvailable = true },
                new PlaybackAudioOption { Mode = PlaybackAudioMode.Dolby, Label = "杜比音效", IsAvailable = true, RequiresLogin = true, RequiresPremium = true },
                new PlaybackAudioOption { Mode = PlaybackAudioMode.HiRes, Label = "Hi-Res 无损", IsAvailable = true, RequiresLogin = true, RequiresPremium = true }
            }
        };
        private static MainPage AudioPage() { FocusManager.Current = null; return new MainPage { _playback = Info() }; }
        private Task ClickAudio(PlaybackAudioMode mode)
        {
            Audio_Click(new Button { Tag = mode }, new RoutedEventArgs());
            return _busy == 0 ? Task.CompletedTask : _idle.Task;
        }
        private static int _audioChecks;
        private static void AudioCheck(bool value, string label) { ++_audioChecks; if (!value) throw new Exception("FAIL: " + label); }
        private static Button AudioButton(MainPage page, PlaybackAudioMode mode) => page.AudioOptions.Children.OfType<Button>().First(button => (PlaybackAudioMode)button.Tag == mode);

        public static async Task<string> RunAudioUiTests()
        {
            _audioChecks = 0;
            foreach (var enhanced in new[] { PlaybackAudioMode.Dolby, PlaybackAudioMode.HiRes })
            {
                foreach (bool playing in new[] { false, true })
                {
                    var page = AudioPage(); page._playing = playing;
                    var waiting = new TaskCompletionSource<PlaybackInfo>(); page._response = token => waiting.Task;
                    var original = page._playback; var pending = page.ClickAudio(enhanced);
                    AudioCheck(page._busy == 1 && page._loaded == 0 && ReferenceEquals(page._playback, original) && page._playing == playing,
                        "requesting enhanced audio keeps original playback while the server responds");
                    AudioCheck(page._requestedQuality == 120 && page._requestedMode == enhanced,
                        "audio request retains current quality and explicitly requests the selected enhanced mode");
                    var received = Info(enhanced); received.VideoFrameRate = 62.5; waiting.SetResult(received); await pending;
                    AudioCheck(page._loaded == 1 && page._loadedCid == 200 && page._loadedQuality == 120 && page._preserved && ReferenceEquals(page._prepared, received),
                        "confirmed audio switch loads authorized response at unchanged part and quality with preservePosition");
                    AudioCheck(page._loadedPosition == TimeSpan.FromSeconds(47) && page._loadedPlaying == playing,
                        "audio switch hands off current position and exact playing intent");
                    AudioCheck(page._prepared.VideoFrameRate == 62.5,
                        "enhanced audio switching never rewrites the selected video frame-rate metadata");
                }
                foreach (bool wrongQuality in new[] { false, true })
                {
                    var page = AudioPage(); var original = page._playback;
                    page._response = token => Task.FromResult(Info(wrongQuality ? enhanced : PlaybackAudioMode.Standard, wrongQuality ? 80 : 120));
                    await page.ClickAudio(enhanced);
                    AudioCheck(page._loaded == 0 && page._closed == 0 && ReferenceEquals(page._playback, original) && original.AudioNotice.Contains("继续播放当前音轨"),
                        "missing enhanced audio or changed quality preserves current stream and explains mismatch");
                }
                {
                    var page = AudioPage(); var original = page._playback;
                    page._response = token => Task.FromException<PlaybackInfo>(new InvalidOperationException("fixture error"));
                    await page.ClickAudio(enhanced);
                    AudioCheck(page._loaded == 0 && page._closed == 0 && ReferenceEquals(page._playback, original) && original.AudioNotice.Contains("请求失败"),
                        "request failure preserves current source and drawer");
                }
                foreach (Action<MainPage> invalidate in new Action<MainPage>[] {
                    page => ++page._partGeneration, page => page._currentCid = 201, page => page._video = new VideoDetail { Aid = 101 },
                    page => page._playback = Info(), page => page._disposed = true,
                    page => page._libraryVisible = true, page => page._creatorVisible = true,
                    page => page._dialogOpen = true, page => page._watching = false,
                    page => page._inputPaneVisible = true, page => page.IsNativeGamepadEditorActive = true,
                    page => page._watchCts = new CancellationTokenSource(),
                    page => page._api.IsLoggedIn = false, page => page._api.CurrentUser.Mid = 43,
                    page => page._api.CurrentUser.IsPremium = false,
                    page => page._playerUi.Drawer = PlayerDrawer.None })
                {
                    var page = AudioPage(); var waiting = new TaskCompletionSource<PlaybackInfo>(); page._response = token => waiting.Task;
                    var pending = page.ClickAudio(enhanced); invalidate(page);
                    waiting.SetResult(Info(enhanced)); await pending;
                    AudioCheck(page._loaded == 0 && page._closed == 0, "stale audio completion cannot replace playback or close another surface");
                }
                {
                    var page = AudioPage(); var waiting = new TaskCompletionSource<PlaybackInfo>(); page._response = token => waiting.Task;
                    var pending = page.ClickAudio(enhanced);
                    _ = page.ClickAudio(PlaybackAudioMode.Standard);
                    waiting.SetResult(Info(enhanced)); await pending;
                    AudioCheck(page._loaded == 0 && page._preferredAudioMode == PlaybackAudioMode.Standard,
                        "reselecting current Standard cancels the pending enhanced choice");
                }
                foreach (bool logged in new[] { false, true })
                {
                    var page = AudioPage(); page._api.IsLoggedIn = logged; page._api.CurrentUser.IsPremium = false;
                    page.UpdateAudioOptions(); var audio = AudioButton(page, enhanced);
                    AudioCheck(!audio.IsEnabled && ((TextBlock)audio.Content).Text.Contains(logged ? "需要大会员" : "需要登录"),
                        "audio availability labels distinguish login from premium entitlement");
                    await page.ClickAudio(enhanced);
                    AudioCheck(!page._requestedMode.HasValue && page._loaded == 0,
                        "an unavailable entitlement cannot be bypassed by invoking a detached audio button");
                }
                {
                    var page = AudioPage(); page.UpdateAudioOptions(); var old = AudioButton(page, enhanced); old.Focus(FocusState.Keyboard);
                    page.UpdateAudioOptions();
                    AudioCheck(!ReferenceEquals(old, FocusManager.Current) && ReferenceEquals(FocusManager.Current, AudioButton(page, enhanced)),
                        "audio row rebuild restores equivalent logical focus");
                    page._api.IsLoggedIn = false; page.UpdateAudioOptions();
                    AudioCheck(FocusManager.Current is Button current && current.IsEnabled && page.AudioOptions.Children.Contains(current),
                        "disabled focused audio choice restores an enabled replacement row");
                    page._playback.AvailableAudioModes = new[] { new PlaybackAudioOption { Mode = enhanced,
                        Label = "enhanced audio", IsAvailable = false, RequiresPremium = true } };
                    FocusManager.Current = AudioButton(page, enhanced);
                    page.UpdateAudioOptions();
                    AudioCheck(ReferenceEquals(FocusManager.Current, page.SettingsCloseButton),
                        "no enabled audio row restores settings close instead of detached old row");
                }
                {
                    var standard = Info(); var codec = Info(quality: 80); var enhancedInfo = Info(enhanced);
                    enhancedInfo.AudioFallback = standard; enhancedInfo.CodecFallback = codec;
                    AudioCheck(ReferenceEquals(NativePlaybackFallback(enhancedInfo), standard), "native enhanced audio failure tries same-video Standard audio before video downgrade");
                    standard.CodecFallback = codec;
                    AudioCheck(ReferenceEquals(NativePlaybackFallback(standard), codec) && NativePlaybackFallback(codec) == null && NativePlaybackFallback(null) == null,
                        "audio fallback retains bounded codec fallback and is null-safe");
                    foreach (bool opening in new[] { false, true })
                    {
                        string notice = EnhancedAudioFallbackNotice(enhancedInfo, opening);
                        AudioCheck(notice.Contains(enhanced == PlaybackAudioMode.HiRes ? "Hi-Res 无损" : "杜比")
                            && notice.Contains("正在尝试同画质的标准音轨") && !notice.Contains("已自动回退"),
                            "fallback names the failed enhanced mode without claiming a successful replacement before it opens");
                    }
                }
                foreach (bool logout in new[] { false, true })
                {
                    var page = AudioPage(); var original = page._playback;
                    var waiting = new TaskCompletionSource<PlaybackInfo>(); page._response = token => waiting.Task;
                    var pending = page.ClickAudio(enhanced);
                    if (logout) page._api.IsLoggedIn = false; else page._playerUi.Drawer = PlayerDrawer.Comments;
                    waiting.SetException(new InvalidOperationException("stale fixture error")); await pending;
                    AudioCheck(string.IsNullOrEmpty(original.AudioNotice) && page._status == null && page._loaded == 0,
                        "a stale failed request cannot update the newly active account or drawer");
                }
            }
            {
                var page = AudioPage(); page._playback = Info(PlaybackAudioMode.HiRes);
                page.UpdateAudioOptions();
                AudioCheck(page.AudioHint.Text.Contains("Hi-Res 无损音轨") && page.AudioHint.Text.Contains("fLaC")
                    && page.AudioHint.Text.Contains("输出格式由 Xbox 音频设置和连接设备决定"),
                    "Hi-Res is labelled as a real lossless source with output dependent on the connected system");
                AudioCheck(!page.AudioHint.Text.Contains("kHz") && !page.AudioHint.Text.Contains(" bit")
                    && !page.AudioHint.Text.Contains("声道"), "unknown Hi-Res sample format is never presented as assumed24bit96kHz");
                page._playback.AudioSampleRate = 96000; page._playback.AudioBitsPerSample = 24; page._playback.AudioChannels = 2;
                page.UpdateAudioOptions();
                AudioCheck(page.AudioHint.Text.Contains("96 kHz") && page.AudioHint.Text.Contains("24 bit")
                    && page.AudioHint.Text.Contains("2 声道"), "known source sample rate, bit depth and channels are displayed");
                page._playback.AudioSampleRate = 44100; page.UpdateAudioOptions();
                AudioCheck(page.AudioHint.Text.Contains("44.1 kHz") && !page.AudioHint.Text.Contains("96 kHz"),
                    "source metadata updates retain precise44.1kHz rather than a hardcoded Hi-Res rate");
            }
            {
                var page = AudioPage(); var first = new TaskCompletionSource<PlaybackInfo>();
                var second = new TaskCompletionSource<PlaybackInfo>();
                page._response = token => first.Task; var oldRequest = page.ClickAudio(PlaybackAudioMode.HiRes);
                page._response = token => second.Task; var newRequest = page.ClickAudio(PlaybackAudioMode.Dolby);
                second.SetResult(Info(PlaybackAudioMode.Dolby)); first.SetResult(Info(PlaybackAudioMode.HiRes));
                await Task.WhenAll(oldRequest, newRequest);
                AudioCheck(page._loaded == 1 && page._prepared.AudioMode == PlaybackAudioMode.Dolby
                    && page._preferredAudioMode == PlaybackAudioMode.Dolby,
                    "the latest enhanced choice owns source switching when an older Hi-Res response arrives later");
            }
            return "PASS: " + _audioChecks + " production playback audio UI/ownership checks (external XAML/network/source-switch substitutes).";
        }
    }
}
