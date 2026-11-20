namespace BiliXbox.ParallelRangeTests
{
    public static class Tests
    {
        private static int _checks;
        private static void Check(bool value, string message) { ++_checks; if (!value) throw new Exception(message); }
        private static byte[] Bytes(ulong start, int count)
            => Enumerable.Range(0,count).Select(index=>(byte)((start+(ulong)index)*17%251)).ToArray();
        private static Services.ParallelRangeDownloader.RangeChunk Chunk(ulong start,int count,ulong total=12*1024*1024)
            => new Services.ParallelRangeDownloader.RangeChunk { Offset=start,Total=total,Bytes=Bytes(start,count),ContentType="video/mp4" };
        public static async Task<string> Run()
        {
            ulong start; int length;
            Check(Services.ParallelRangeDownloader.TryParseRange("bytes=1024-66559",out start,out length)&&start==1024&&length==65536,"finite ranges retain exact inclusive endpoints");
            foreach(var invalid in new[]{"bytes=0-","bytes=-200","bytes=0-2,4-6","bytes=3-2","bytes=0-16777216","bytes=18446744073709551616-18446744073709551617"})
                Check(!Services.ParallelRangeDownloader.TryParseRange(invalid,out start,out length),"invalid/unbounded/oversized range rejected: "+invalid);
            int active=0,peak=0;var used=new System.Collections.Concurrent.ConcurrentDictionary<string,int>();
            var downloader=new Services.ParallelRangeDownloader(async(url,offset,count,ct)=>
            {
                int current=Interlocked.Increment(ref active);int seen;
                do {seen=Volatile.Read(ref peak);if(seen>=current)break;} while(Interlocked.CompareExchange(ref peak,current,seen)!=seen);
                try {await Task.Yield();ct.ThrowIfCancellationRequested();used.AddOrUpdate(url,1,(_,n)=>n+1);return Chunk(offset,count);}
                finally{Interlocked.Decrement(ref active);}
            });
            var result=await downloader.DownloadAsync(new[]{"primary","backup"},12345,3*1024*1024,CancellationToken.None);
            Check(result.Bytes.SequenceEqual(Bytes(12345,3*1024*1024))&&result.Total==12*1024*1024,"out-of-order parallel responses reconstruct the exact requested bytes and preserve full file length");
            Check(peak<=4&&used.Count==2&&downloader.ReservedBytes==0,"shared connection budget and signed backup candidates remain bounded and memory reservations release");
            Check(await downloader.DownloadAsync(new[]{"primary"},0,16,CancellationToken.None)==null,"small initialization requests bypass acceleration");

            const int segment = 2 * 1024 * 1024;
            int slowMedia = 0;
            int backupMedia = 0;
            var routes = new[] { "burst-then-slow", "steady-backup" };
            var learned = new Services.ParallelRangeDownloader(async (url, offset, count, token) =>
            {
                if (count != Services.ParallelRangeDownloader.AnchorBytes)
                {
                    if (url == routes[0]) Interlocked.Increment(ref slowMedia);
                    else Interlocked.Increment(ref backupMedia);
                    // Both small probes are equally fast. Only the real media
                    // body reveals the slow route or a throttled primary.
                    await Task.Delay(url == routes[0] ? 220 : 12, token);
                }
                return Chunk(offset, count);
            });
            await learned.DownloadAsync(routes, 0, segment, CancellationToken.None);
            Check(slowMedia > 0 && backupMedia > 0 && learned.PerformanceDiagnostic.Contains("sampledVideoRoutes=2"),
                "real media chunks, not the first-byte probes, learn both CDN speeds");
            int slowAfterFirst = slowMedia;
            for (int i = 1; i <= 3; ++i)
            {
                var next = await learned.DownloadAsync(routes, (ulong)(i * segment), segment, CancellationToken.None);
                Check(next.Bytes.SequenceEqual(Bytes((ulong)(i * segment), segment)),
                    "preferred CDN returns exact bytes for a later media range");
            }
            Check(slowMedia == slowAfterFirst,
                "the next ranges remember the slow route and no longer make it hold up every segment");

            var changingRoutes = new[] { "steady-backup", "burst-then-slow" };
            bool bestThrottled = false;
            int throttledRequests = 0, alternativeRequests = 0;
            var changing = new Services.ParallelRangeDownloader(async (url, offset, count, token) =>
            {
                if (count != Services.ParallelRangeDownloader.AnchorBytes)
                {
                    if (url == changingRoutes[0]) Interlocked.Increment(ref throttledRequests);
                    else Interlocked.Increment(ref alternativeRequests);
                    await Task.Delay(url == changingRoutes[0] ? (bestThrottled ? 420 : 12) : 100, token);
                }
                return Chunk(offset, count);
            });
            await changing.DownloadAsync(changingRoutes, 0, segment, CancellationToken.None);
            bestThrottled = true;
            await changing.DownloadAsync(changingRoutes, segment, segment, CancellationToken.None);
            int priorThrottled = throttledRequests;
            int priorAlternatives = alternativeRequests;
            await changing.DownloadAsync(changingRoutes, 2 * segment, segment, CancellationToken.None);
            Check(alternativeRequests - priorAlternatives > throttledRequests - priorThrottled,
                "when the initially fast node drops speed, future chunks prefer the healthy alternative");

            int badMedia=0;
            downloader=new Services.ParallelRangeDownloader(async(url,offset,count,ct)=>
            {
                await Task.Yield();var part=Chunk(offset,count);
                if(url=="different-file") {part.Bytes[0]^=0xff;if(count!=Services.ParallelRangeDownloader.AnchorBytes)Interlocked.Increment(ref badMedia);}
                return part;
            });
            result=await downloader.DownloadAsync(new[]{"correct","different-file"},0,1024*1024,CancellationToken.None);
            Check(result.Bytes.SequenceEqual(Bytes(0,1024*1024))&&badMedia==0,"equal-length CDN data with a different anchor is excluded instead of mixed");
            int truncated=0;
            downloader=new Services.ParallelRangeDownloader(async(url,offset,count,ct)=>
            {
                await Task.Yield();var part=Chunk(offset,count);
                if(url=="flaky"&&count!=Services.ParallelRangeDownloader.AnchorBytes){part.Bytes=new byte[1];Interlocked.Increment(ref truncated);}
                return part;
            });
            result=await downloader.DownloadAsync(new[]{"healthy","flaky"},0,3*1024*1024,CancellationToken.None);
            Check(result.Bytes.SequenceEqual(Bytes(0,3*1024*1024)),"truncated media pieces retry only validated exact-track alternatives");
            downloader=new Services.ParallelRangeDownloader((url,offset,count,ct)=>Task.FromResult(Chunk(offset+1,count)));
            bool rejected=false;try {await downloader.DownloadAsync(new[]{"invalid"},0,65536,CancellationToken.None);}catch(System.IO.IOException){rejected=true;}
            Check(rejected&&downloader.ReservedBytes==0,"misaligned ranges fail before any successful output and release reservations");
            downloader=new Services.ParallelRangeDownloader(async(url,offset,count,ct)=>{await Task.Delay(Timeout.Infinite,ct);return Chunk(offset,count);});
            using(var cancel=new CancellationTokenSource())
            {
                var waiting=downloader.DownloadAsync(new[]{"held"},0,16*1024*1024,cancel.Token);
                Check(await downloader.DownloadAsync(new[]{"second"},0,16*1024*1024,cancel.Token)==null,"global byte budget makes another oversized concurrent request use native fallback");
                cancel.Cancel();bool canceled=false;try{await waiting;}catch(OperationCanceledException){canceled=true;}
                Check(canceled&&downloader.ReservedBytes==0,"source cancellation joins active probes and releases the byte budget");
            }
            var failures=new Services.AdaptiveDownloadFailureState();
            failures.Requested("v","video",3,0,100);failures.Failed("v");
            failures.Requested("a","audio",3,0,100);failures.Completed("a");
            Check(failures.Pending,"audio completion preserves failed video range");
            failures.Requested("index","video",3,100,100);failures.Completed("index");
            Check(failures.Pending,"different video range does not claim initialization success");
            failures.Requested("retry","video",3,0,100);failures.Completed("retry");
            Check(!failures.Pending,"the precise failed video range clears only after its successful retry");
            failures.Failed("retry");Check(!failures.Pending,"late terminal notification cannot resurrect completed request");
            failures.Failed("unknown");failures.Completed("unknown");Check(failures.Pending,"uncorrelated failure remains diagnosable until source replacement");
            return "PASS: "+_checks+" parallel Range, byte integrity, cancellation, memory budget and HDR failure correlation checks.";
        }
    }
}
