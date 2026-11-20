namespace BiliXbox.Controls
{
    public sealed partial class LiveHubView
    {
        private FixturePlayer _player = new FixturePlayer();
        private FixtureResource _liveMediaSource = new FixtureResource(), _adaptive = new FixtureResource(), _playHttp = new FixtureResource();
        private FixtureSurface _surface = new FixtureSurface();
        private int _errors;
        private void LiveMediaOpened(object s, object e) { }
        private void LiveMediaFailed(object s, object e) { }
        private void LivePlaybackStateChanged(object s, object e) { }
        private void RecordLive(string phase, Exception error) { ++_errors; }
        private static int checks;
        private static void Check(bool value, string reason) { ++checks; if (!value) throw new Exception(reason); }

        public static string RunResilienceTests()
        {
            var queue = new LiveMessageQueue<int>();
            for (int i=0;i<10000;i++) queue.Enqueue(i);
            Check(queue.Count==LiveMessageQueue<int>.Capacity,"live flood queue stays bounded");
            int item;queue.TryDequeue(out item);
            Check(item==10000-LiveMessageQueue<int>.Capacity,"overload drops stale arrivals instead of newest live messages");
            queue.Clear();Check(queue.Count==0&&!queue.TryDequeue(out item),"leaving clears pending messages");
            System.Threading.Tasks.Parallel.For(0,10000,i=>queue.Enqueue(i));
            Check(queue.Count==LiveMessageQueue<int>.Capacity,"concurrent producers cannot bypass capacity");
            var values=new System.Collections.Generic.HashSet<int>();
            while(queue.TryDequeue(out item))Check(values.Add(item),"concurrent drain must not duplicate a packet");

            for(int failure=0;failure<5;failure++)
            {
                var page=new LiveHubView(); var player=page._player;var source=page._liveMediaSource;
                var adaptive=page._adaptive;var http=page._playHttp;
                page._surface.OnDetach=()=>Check(page._player==null&&page._liveMediaSource==null&&page._adaptive==null&&page._playHttp==null,
                    "ownership retires before native callbacks can run");
                page._surface.Fail=failure==0;player.FailSession=failure==1;player.FailSource=failure==2;
                player.FailDispose=failure==3;source.Fail=failure==4;
                page.DisposeMedia();
                Check(page._errors==1,"native cleanup errors are caught once");
                Check(player.DisposeCalls==1&&source.Calls==1&&adaptive.Calls==1&&http.Calls==1,
                    "one failed detach/source/native dispose cannot strand remaining resources");
                page._surface.Fail=false;page._surface.OnDetach=null;page.DisposeMedia();
                Check(player.DisposeCalls==1&&source.Calls==1&&adaptive.Calls==1&&http.Calls==1,
                    "repeated leave/dispose is harmless");
            }
            return "PASS: "+checks+" production live cleanup fault-injection and concurrent message-queue checks.";
        }
    }
    internal sealed class FixtureSurface
    {
        internal bool Fail; internal Action OnDetach;
        internal void SetMediaPlayer(object value) { OnDetach?.Invoke();if(Fail)throw new System.Runtime.InteropServices.COMException(); }
    }
    internal sealed class FixtureResource
    {
        internal bool Fail;internal int Calls;
        internal void Dispose() { ++Calls;if(Fail)throw new System.Runtime.InteropServices.COMException(); }
    }
    internal sealed class FixtureSession { internal event Action<object,object> PlaybackStateChanged; }
    internal sealed class FixturePlayer
    {
        internal event Action<object,object> MediaOpened,MediaFailed;
        private readonly FixtureSession session=new FixtureSession();
        internal bool FailSession,FailSource,FailDispose;internal int DisposeCalls;
        internal FixtureSession PlaybackSession { get { if(FailSession)throw new ObjectDisposedException("session");return session; } }
        internal object Source { set { if(FailSource)throw new System.Runtime.InteropServices.COMException(); } }
        internal void Pause() { }
        internal void Dispose() { ++DisposeCalls;if(FailDispose)throw new System.Runtime.InteropServices.COMException(); }
    }
}
