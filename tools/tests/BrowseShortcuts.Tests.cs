namespace Windows.System { public enum VirtualKey { X, Control, GamepadX, GamepadY, GamepadLeftShoulder } }
namespace Windows.Gaming.Input {
    [Flags] public enum GamepadButtons { None=0, X=1, Y=2, LeftShoulder=4 }
    public struct GamepadReading { public GamepadButtons Buttons; }
    public sealed class Gamepad { public GamepadReading Reading; public GamepadReading GetCurrentReading()=>Reading; }
}
namespace BiliXbox {
    public sealed partial class MainPage {
        private bool _disposed,_dialogOpen,_inputPaneVisible,IsNativeGamepadEditorActive,RawGamepadOwnsNavigation;
        private bool IsSectionBrowseSurface=true;
        private bool IsCommentReplySurface, _replyShortcutBusy, _libraryVisible, _creatorVisible;
        private DateTimeOffset _lastReplyShortcut=DateTimeOffset.MinValue;
        private int replies;
        private sealed class FakeHub {
            public Task WriteActivityCommentAsync(bool reply)=>Task.CompletedTask;
            public Task WriteCommentAsync(bool reply)=>Task.CompletedTask;
        }
        private readonly FakeHub AccountLibrary=new FakeHub(),CreatorHub=new FakeHub();
        private bool TryOpenCommentComposer(bool reply){if(reply)replies++;return true;}
        private bool _virtualLb,_rawLb,_lbChordUsed;
        private Gamepad _activeGamepad;
        private readonly ShoulderChordState _viewingChord=new ShoulderChordState();
        private DateTimeOffset _lastViewingChord=DateTimeOffset.MinValue;
        private int refreshes,steps,seeks;
        private Task RefreshFeedAsync(){++refreshes;return Task.CompletedTask;}
        private void StepHomeSection(int value){++steps;}
        private void SeekVideoShoulder(int value){++seeks;}
        private void SetStatus(string value){throw new Exception(value);}
        private static string Friendly(Exception error)=>error.Message;
        /* PRODUCTION_METHODS */
        private static int checks;
        private static void Check(bool value,string reason){++checks;if(!value)throw new Exception(reason);}
        public static string RunBrowseTests(){
            var page=new MainPage();
            Check(!page.HandleViewingChord(VirtualKey.GamepadX,false)&&page.refreshes==0,"single X never refreshes browse surface");
            Check(!page.HandleViewingChord(VirtualKey.GamepadX,true)&&page.refreshes==0,"held X never refreshes browse surface");
            foreach(var block in new Action<MainPage>[] {p=>p._dialogOpen=true,p=>p._inputPaneVisible=true,p=>p.IsNativeGamepadEditorActive=true,p=>p._disposed=true,p=>p.IsSectionBrowseSurface=false}){
                page=new MainPage();block(page);
                page.HandleViewingChord(VirtualKey.GamepadLeftShoulder,false);
                Check(!page.HandleViewingChord(VirtualKey.GamepadX,false)&&page.refreshes==0,"refresh cannot steal input from dialogs/editors/playback/library/creator surfaces");
            }
            page=new MainPage();page.HandleViewingChord(VirtualKey.GamepadLeftShoulder,false);
            Check(page.HandleViewingChord(VirtualKey.GamepadX,false)&&page.refreshes==1,"LB+X refresh alias");
            page.ReleaseViewingChord(VirtualKey.GamepadLeftShoulder);
            Check(page.steps==0&&page.seeks==0,"LB+X release never changes section or seeks");
            page=new MainPage();page.HandleViewingChord(VirtualKey.GamepadLeftShoulder,false);
            Check(!page.HandleViewingChord(VirtualKey.GamepadY,false)&&page.refreshes==0,"Y is available to search rather than another refresh binding");
            page=new MainPage();page.HandleViewingChord(VirtualKey.GamepadLeftShoulder,false);page.ReleaseViewingChord(VirtualKey.GamepadLeftShoulder);
            Check(page.steps==1&&page.refreshes==0,"standalone LB still selects previous section");
            page=new MainPage();Check(!page.HandleViewingChord(VirtualKey.GamepadY,false),"standalone Y remains available to search");
            page=new MainPage();page.IsSectionBrowseSurface=false;page.HandleViewingChord(VirtualKey.GamepadLeftShoulder,false);
            Check(!page.HandleViewingChord(VirtualKey.GamepadX,false),"LB+X outside browse retains comment/danmaku behavior");
            page=new MainPage();page._activeGamepad=new Gamepad();page.RawGamepadOwnsNavigation=true;
            page._activeGamepad.Reading=new GamepadReading {Buttons=GamepadButtons.LeftShoulder|GamepadButtons.X};
            page.PollViewingChord(page._activeGamepad.Reading,true);page.HandleViewingChord(VirtualKey.GamepadX,false);
            page.PollViewingChord(page._activeGamepad.Reading,true);
            page.PollViewingChord(new GamepadReading(),true);
            Check(page.refreshes==1&&page.steps==0&&page.seeks==0,"raw plus mapped LB+X delivery is deduplicated without trailing section action");
            page=new MainPage();page.HandleViewingChord(VirtualKey.GamepadLeftShoulder,false);
            page.HandleViewingChord(VirtualKey.GamepadX,false);page.PollViewingChord(new GamepadReading(),false);
            page.ReleaseViewingChord(VirtualKey.GamepadLeftShoulder);
            Check(page.refreshes==1&&page.steps==0,"disabled raw sampler cannot erase a remote chord's consumed LB");
            page=new MainPage();page.RawGamepadOwnsNavigation=true;
            page.HandleViewingChord(VirtualKey.GamepadLeftShoulder,false);page.HandleViewingChord(VirtualKey.GamepadX,false);
            page.PollViewingChord(new GamepadReading {Buttons=GamepadButtons.LeftShoulder},true);
            page.ReleaseViewingChord(VirtualKey.GamepadLeftShoulder);page.PollViewingChord(new GamepadReading(),true);
            Check(page.refreshes==1&&page.steps==0,"mapped face-button tap missed between raw frames cannot cause trailing section switch");
            page=new MainPage();page.IsSectionBrowseSurface=false;page.IsCommentReplySurface=true;
            page.HandleViewingChord(VirtualKey.GamepadLeftShoulder,false);page.HandleViewingChord(VirtualKey.GamepadX,false);
            page.PollViewingChord(new GamepadReading{Buttons=GamepadButtons.LeftShoulder|GamepadButtons.X},true);
            page.ReleaseViewingChord(VirtualKey.GamepadLeftShoulder);page.PollViewingChord(new GamepadReading(),true);
            Check(page.replies==1&&page.refreshes==0&&page.steps==0&&page.seeks==0,"LB+X targets comment reply once without refresh or trailing seek");
            return "PASS: "+checks+" production LB+X refresh/reply, repeat, editor, single-X exclusion and search-preservation checks (external input substitutes).";
        }
    }
}
