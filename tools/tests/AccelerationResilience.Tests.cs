namespace BiliXbox.AccelerationResilience
{
    public static class Tests
    {
        private static int checks;
        private static void Check(bool value,string name){++checks;if(!value)throw new Exception(name);}
        public static async Task<string> Run()
        {
            var gate=new Controls.DialogConfirmReleaseGate();gate.Observe(TimeSpan.Zero,true);
            Check(!gate.KeyDown(false)&&!gate.KeyUp(),"opening confirm press/release cannot change a dialog option");
            gate.Observe(TimeSpan.FromSeconds(5),true);Check(!gate.Armed,"holding confirm never arms the dialog");
            gate.Observe(TimeSpan.FromSeconds(5.1),false);gate.Observe(TimeSpan.FromSeconds(5.15),false);
            Check(!gate.Armed,"short neutral interval does not pass through the old activation");
            gate.Observe(TimeSpan.FromSeconds(5.21),false);
            Check(gate.Armed&&!gate.KeyUp(),"late opening key-up is consumed even after controls become enabled");
            Check(gate.KeyDown(false)&&!gate.KeyDown(true)&&!gate.KeyDown(false)&&gate.KeyUp()&&!gate.KeyUp(),"only a fresh confirm can activate once");
            var health=new Controls.PlaybackAccelerationHealthState();var source=new object();var start=DateTimeOffset.UtcNow;
            bool restart=false;for(int i=0;i<=12;i++)restart=health.Observe(source,1,start.AddSeconds(i),true,true,true,false,0,.3);
            Check(restart&&!health.Observe(source,1,start.AddSeconds(30),true,true,true,false,0,.3),"constant tiny playable cache triggers one finite fallback");
            health=new Controls.PlaybackAccelerationHealthState();
            for(int i=0;i<=30;i++)Check(!health.Observe(source,1,start.AddSeconds(i),true,true,true,false,i*.5,.3),"advancing media is not restarted");
            health=new Controls.PlaybackAccelerationHealthState();
            for(int i=0;i<=30;i++)Check(!health.Observe(source,1,start.AddSeconds(i),true,true,true,true,0,0),"modal/seeking state prevents automatic source replacement");
            using(var stream=new Services.OrderedRangeReadStream(8,CancellationToken.None,CancellationToken.None))
            {
                await stream.AddAsync(4,new byte[]{5,6,7,8},CancellationToken.None);
                var data=new byte[8];var read=stream.ReadAsync(data,0,8,CancellationToken.None);
                Check(!read.IsCompleted,"future data is not returned before its contiguous prefix");
                await stream.AddAsync(0,new byte[]{1,2,3,4},CancellationToken.None);
                Check(await read==4&&await stream.ReadAsync(data,4,4,CancellationToken.None)==4&&data.SequenceEqual(new byte[]{1,2,3,4,5,6,7,8}),"ordered stream joins out-of-order pieces exactly");
                Check(await stream.ReadAsync(data,0,8,CancellationToken.None)==0,"EOF only occurs after the declared full range");
            }
            using(var stream=new Services.OrderedRangeReadStream(4,CancellationToken.None,CancellationToken.None))
            {
                var waiting=stream.ReadAsync(new byte[1],0,1,CancellationToken.None);stream.Dispose();bool canceled=false;
                try{await waiting;}catch(OperationCanceledException){canceled=true;}Check(canceled,"disposing a waiting native body cancels its reader");
            }
            var waits=new Services.OrderedRangeReadStream.WaitStatistics();
            using(var stream=new Services.OrderedRangeReadStream(4*1024*1024,CancellationToken.None,CancellationToken.None,waits))
            {
                var window=stream.WaitForWindowAsync(3*1024*1024,stream.ProducerToken);
                var read=stream.ReadAsync(new byte[1],0,1,CancellationToken.None);
                Check(!window.IsCompleted&&waits.WindowWaiters==1,"the diagnostic counts producers waiting for native consumption");
                Check(!read.IsCompleted&&waits.ReadWaiters==1,"the diagnostic separately counts native reads waiting for contiguous data");
                await stream.AddAsync(0,new byte[]{7},CancellationToken.None);
                Check(await read==1&&waits.ReadWaiters==0&&waits.WindowWaiters==1,"contiguous bytes clear the read wait without unblocking a distant prefetch");
                stream.Dispose();bool canceled=false;try{await window;}catch(OperationCanceledException){canceled=true;}
                Check(canceled&&waits.WindowWaiters==0,"cancelling the response also clears its reported backpressure");
            }
            return "PASS: "+checks+" dialog release, finite acceleration recovery and bounded ordered-stream checks.";
        }
    }
}
