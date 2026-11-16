namespace Windows.Gaming.Input { public struct GamepadReading { public double RightTrigger; } }
namespace Windows.System { public enum VirtualKey { GamepadRightTrigger,GamepadX } }
namespace Windows.Storage { public sealed class ApplicationData { public static ApplicationData Current=new ApplicationData();public readonly Settings LocalSettings=new Settings();public sealed class Settings { public readonly global::System.Collections.Generic.Dictionary<string,object> Values=new global::System.Collections.Generic.Dictionary<string,object>(); } } }
namespace BiliXbox.Services { public static class PlaybackDiagnosticsLog { public static void Record(string value) { } } }
namespace BiliXbox.Controls { internal enum PlayerDrawer { None,Comments } }
namespace BiliXbox
{
    public sealed partial class MainPage
    {
        private sealed class Session { public bool Reject;private double rate=1;public double PlaybackRate { get=>rate;set { if(Reject)throw new Exception("unsupported");rate=value; } } }
        private sealed class Media { public object Source=new object();public readonly Session PlaybackSession=new Session(); }
        private sealed class Ui { public PlayerDrawer Drawer; }
        private sealed class Label { public object Content; }
        private sealed class Pad { public GamepadReading Reading;public GamepadReading GetCurrentReading()=>Reading; }
        private Pad _activeGamepad;
        private readonly Media _media=new Media();private readonly Ui _playerUi=new Ui();private readonly Label PlaybackSpeedButton=new Label();
        private readonly PlaybackSpeedState _playbackSpeed=new PlaybackSpeedState();
        private bool _mappedSpeedTrigger,_disposed,_creatorVisible,_libraryVisible,_liveVisible,_inBackground,_dialogOpen,_inputPaneVisible,_pausedRecommendationsVisible,_sourceLoading;
        private bool _watching=true,_wantsPlayback=true,_gamepadWindowActive=true,_gamepadWindowVisible=true,RawGamepadOwnsNavigation=true;
        private void SetStatus(string text) { }
        private static int checks;
        private static void Check(bool value,string label) { checks++;if(!value)throw new Exception(label); }
        private void Trigger(double value)=>PollPlaybackSpeed(new GamepadReading { RightTrigger=value },true);
        public static string RunSpeedTests()
        {
            var view=new MainPage();ApplicationData.Current.LocalSettings.Values["playbackRate"]=.75d;view.InitializePlaybackSpeed();
            Check(view._playbackSpeed.Selected==.75 && (string)view.PlaybackSpeedButton.Content=="0.75×","Valid saved selection restored");
            foreach(double rate in PlaybackSpeedState.Rates) { view._playbackSpeed.Select(rate);view.ApplyPlaybackSpeed();Check(view._media.PlaybackSession.PlaybackRate==rate,"All requested fixed rates reach native session"); }
            view._playbackSpeed.Select(1.25);view.ApplyPlaybackSpeed();view.Trigger(0);view.Trigger(.7);
            Check(view._playbackSpeed.Temporary && view._media.PlaybackSession.PlaybackRate==2,"RT holds 2x");
            view.Trigger(.4);Check(view._playbackSpeed.Temporary,"Trigger hysteresis avoids jitter");view.Trigger(.2);
            Check(!view._playbackSpeed.Temporary && view._media.PlaybackSession.PlaybackRate==1.25,"Release restores selected rate, not hardcoded normal");
            foreach(string field in new[] { "_dialogOpen","_inputPaneVisible","_libraryVisible","_creatorVisible","_liveVisible","_inBackground","_pausedRecommendationsVisible","_sourceLoading" }) {
                view.Trigger(0);view.Trigger(1);typeof(MainPage).GetField(field,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).SetValue(view,true);view.Trigger(1);
                Check(!view._playbackSpeed.Temporary && view._media.PlaybackSession.PlaybackRate==1.25,"Blocked surface clears temporary rate: "+field);
                typeof(MainPage).GetField(field,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).SetValue(view,false);view.Trigger(1);
                Check(!view._playbackSpeed.Temporary,"Held trigger cannot reactivate after returning: "+field);
            }
            view.Trigger(0);view.Trigger(1);view._wantsPlayback=false;view.Trigger(1);Check(!view._playbackSpeed.Temporary,"Pause cancels temporary boost without resuming media");view._wantsPlayback=true;
            view.Trigger(0);view.Trigger(1);view.ResetTemporaryPlaybackSpeed();Check(view._media.PlaybackSession.PlaybackRate==1.25,"Focus loss/device removal/source reset restores selected speed");
            view.RawGamepadOwnsNavigation=false;view.HandlePlaybackSpeedKey(VirtualKey.GamepadRightTrigger,false);view.HandlePlaybackSpeedKey(VirtualKey.GamepadRightTrigger,true);
            Check(view._playbackSpeed.Temporary,"Mapped trigger works without raw controller");view.HandlePlaybackSpeedKey(VirtualKey.GamepadRightTrigger,false);
            Check(view._media.PlaybackSession.PlaybackRate==1.25,"Mapped release restores original");
            view._dialogOpen=true;Check(!view.HandlePlaybackSpeedKey(VirtualKey.GamepadRightTrigger,true) && !view.HandlePlaybackSpeedKey(VirtualKey.GamepadRightTrigger,false),"Native keyboard retains trigger events");view._dialogOpen=false;
            view.Trigger(0);view.Trigger(1);view._dialogOpen=true;view._activeGamepad=new Pad { Reading=new GamepadReading { RightTrigger=1 } };
            view.PollPlaybackSpeed(default(GamepadReading),false);view._dialogOpen=false;view.Trigger(1);
            Check(!view._playbackSpeed.Temporary,"Zeroed raw navigation sample during dialog does not fake RT release");
            view._activeGamepad=null;
            view.Trigger(0);view.Trigger(1);view._gamepadWindowActive=false;view.Trigger(1);Check(!view._playbackSpeed.Temporary,"Window deactivation clears temporary speed");view._gamepadWindowActive=true;
            view._media.PlaybackSession.Reject=true;view._playbackSpeed.Select(2);view.ApplyPlaybackSpeed();Check(view._playbackSpeed.Selected==1,"Unsupported native rate is handled");
            bool rejected=false;try{view._playbackSpeed.Select(7);}catch(ArgumentOutOfRangeException){rejected=true;}Check(rejected,"Unlisted rate rejected");
            return "PASS: "+checks+" production fixed/RT speed, release, UI lifecycle guards, preference and native setter failure assertions (media/input substitutes).";
        }
    }
}
