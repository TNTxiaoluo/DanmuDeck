using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
namespace Windows.ApplicationModel {
    public sealed class TestDeferral {public int Completions;public readonly TaskCompletionSource<bool> Done=new TaskCompletionSource<bool>();public void Complete(){Completions++;Done.TrySetResult(true);}}
    public sealed class EnteredBackgroundEventArgs {public readonly TestDeferral Deferral=new TestDeferral();public TestDeferral GetDeferral()=>Deferral;}
    public sealed class LeavingBackgroundEventArgs {public readonly TestDeferral Deferral=new TestDeferral();public TestDeferral GetDeferral()=>Deferral;}
}
namespace BiliXbox {
    public sealed partial class MainPage {
        private readonly SemaphoreSlim _backgroundGate=new SemaphoreSlim(1,1);
        private int _backgroundRevision;
        private bool _disposed,_inBackground,_audioOnlyPlayback,_backgroundSourceSwitch,_adjustingVolume;
        private bool _backgroundAudio=true,_watching=true,_wantsPlayback=true,_resumeAfterOpen=true;
        private CancellationTokenSource _partCts=new CancellationTokenSource();
        private readonly object _video=new object();private long _currentCid=123;
        private sealed class Info {public int QualityId=125;public string DashManifest="actual-manifest-substitute";}
        private readonly Info _playback=new Info();
        private sealed class Media {public object Source=new object();public int Pauses;public void Pause(){Pauses++;}}
        private readonly Media _media=new Media();
        private sealed class Presenter {public bool Attached=true,FailAttach,FailDetach;public void SetMediaPlayer(object value){if((value==null&&FailDetach)||(value!=null&&FailAttach))throw new InvalidOperationException("native presenter failure");Attached=value!=null;}}
        private readonly Presenter Player=new Presenter();
        private sealed class Clock {public TimeSpan Interval;public int Starts,Stops;public void Start(){Starts++;}public void Stop(){Stops++;}}
        private readonly Clock _clock=new Clock();
        private readonly List<string> events=new List<string>();private int loads,resumes,errors,plays;
        private bool preserved=true;private TaskCompletionSource<bool> holdAudio;
        private void SuspendGamepadInput(){events.Add("input-suspended");}
        private void ResumeGamepadInput(){resumes++;}
        private void CancelDisplayPlayRequest(){}
        private void UpdateVolumeLabel(){}
        private void TrimBackgroundViews(){events.Add("trim");}
        private void RestoreBackgroundViews(){events.Add("restore-views");}
        private void RecordBackgroundState(string phase){events.Add(phase);}
        private void RecordBackgroundFailure(string phase,Exception error){errors++;}
        private Task ResetPlaybackDisplayAsync(bool force=false){events.Add("reset-display");return Task.CompletedTask;}
        private Task PlayWithDisplayAsync(){plays++;return Task.CompletedTask;}
        private Task WaitForBackgroundAudioAsync(int revision)=>Task.CompletedTask;
        private static void Cancel(ref CancellationTokenSource cts){var old=cts;cts=null;if(old!=null){old.Cancel();old.Dispose();}}
        private async Task LoadPartAsync(long cid,int quality,bool preserve,Info info,bool recovery){
            loads++;preserved&=preserve&&recovery&&cid==123&&quality==125&&ReferenceEquals(info,_playback);
            bool audio=_inBackground;_media.Source=null;var owner=_partCts=new CancellationTokenSource();var token=owner.Token;
            if(audio&&holdAudio!=null){await Task.WhenAny(holdAudio.Task,Task.Delay(Timeout.Infinite,token));token.ThrowIfCancellationRequested();}
            _audioOnlyPlayback=audio;_media.Source=new object();events.Add(audio?"audio-source":"video-source");
        }
        /* METHODS */
        private static int checks;
        private static void Check(bool value,string name){checks++;if(!value)throw new Exception(name);}
        private async Task Enter(){var args=new EnteredBackgroundEventArgs();Viewing_EnteredBackground(this,args);await args.Deferral.Done.Task;Check(args.Deferral.Completions==1,"enter deferral must complete once");}
        private async Task Leave(){var args=new LeavingBackgroundEventArgs();Viewing_LeavingBackground(this,args);await args.Deferral.Done.Task;Check(args.Deferral.Completions==1,"leave deferral must complete once");}
        public static async Task<string> RunBackgroundTests(){
            var page=new MainPage();await page.Enter();
            Check(page._inBackground&&page._audioOnlyPlayback&&!page.Player.Attached&&page.loads==1,"background uses audio-only source with detached presenter");
            Check(page.events.IndexOf("audio-source")<page.events.IndexOf("reset-display"),"video decoder source is released before restoring dashboard display mode");
            Check(page._wantsPlayback&&page.preserved&&page._clock.Interval==TimeSpan.FromSeconds(1),"background preserves intent/identity/position and light timer");
            await page.Leave();
            Check(!page._inBackground&&!page._audioOnlyPlayback&&page.Player.Attached&&page.loads==2&&page.preserved,"foreground restores original video and resume point");
            Check(page._clock.Interval==TimeSpan.FromMilliseconds(100)&&page.resumes==1,"foreground resumes input exactly once");
            await page.Enter();await page.Leave();Check(page.loads==4&&page.errors==0,"repeated background cycles do not dispose or replace the player owner");
            page=new MainPage();page._wantsPlayback=page._resumeAfterOpen=false;await page.Enter();await page.Leave();Check(!page._wantsPlayback&&page.plays==0,"explicitly paused video stays paused across transitions");
            page=new MainPage();page._backgroundAudio=false;await page.Enter();Check(page.loads==0&&!page._wantsPlayback&&page._media.Pauses==1,"disabled background audio does not start or recreate media");
            page=new MainPage();page.holdAudio=new TaskCompletionSource<bool>();var entering=page.Enter();var leaving=page.Leave();await Task.WhenAll(entering,leaving);
            Check(page.Player.Attached&&!page._inBackground&&!page._audioOnlyPlayback&&page.preserved,"rapid return cancels obsolete audio preparation and restores video");
            page=new MainPage();page.Player.FailDetach=true;await page.Enter();Check(page.errors==1,"native detach exception is caught outside async-void event");
            page=new MainPage();await page.Enter();page.Player.FailAttach=true;await page.Leave();Check(page.errors==1&&page.resumes==1,"failed foreground attach still completes deferral and restores input processing");
            return "PASS: "+checks+" production background transition, deferral, audio/video handoff, repeat, pause and stale-cancellation assertions (native media/UI substitutes).";
        }
    }
}
