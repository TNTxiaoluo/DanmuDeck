using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Services;
using Windows.Foundation;
using Windows.Web.Http;
using Windows.Web.Http.Filters;
using Windows.Web.Http.Headers;
using System.IO;
using System.Text;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.Streaming.Adaptive;
using Windows.Storage.Streams;

internal static class RangeAccelerationRuntime
{
    private const string Video="https://primary.example.test/v.m4s?signature=fixture%2Fvideo";
    private const string Backup="https://backup.example.test/v.m4s?signature=fixture%2Fbackup";
    private const string Audio="https://primary.example.test/a.m4s?signature=fixture%2Faudio";
    private const int Total=8*1024*1024;
    private static int checks;
    private static void Check(bool result,string name) { if(!result)throw new Exception(name);++checks; }
    private static DashRoutePolicy Routes()=>new DashRoutePolicy("<MPD xmlns='urn:mpeg:dash:schema:mpd:2011'><Period>"
        +"<AdaptationSet contentType='video'><Representation><BaseURL>"+Video+"</BaseURL></Representation></AdaptationSet>"
        +"<AdaptationSet contentType='audio'><Representation><BaseURL>"+Audio+"</BaseURL></Representation></AdaptationSet></Period></MPD>",
        new[]{Video,Backup},new[]{Audio},0);
    private static byte Value(ulong offset)=>(byte)(offset*17%251);
    private static async Task<HttpResponseMessage> Send(HttpClient client,string uri=Video,int count=2*1024*1024,CancellationToken token=default(CancellationToken))
    {
        using(var request=new HttpRequestMessage(HttpMethod.Get,new Uri(uri)))
        {
            request.Headers.TryAppendWithoutValidation("Range","bytes=1024-"+(1024+count-1));
            request.Headers.TryAppendWithoutValidation("X-Fixture-Context",uri==Video?"video":"audio");
            return await client.SendRequestAsync(request).AsTask(token);
        }
    }
    private static async Task Verify(HttpResponseMessage response,int count=2*1024*1024)
    {
        using(response)
        {
            Check(response.StatusCode==HttpStatusCode.PartialContent,"real Windows client sees 206");
            var range=response.Content.Headers.ContentRange;
            Check(range.FirstBytePosition==1024&&range.LastBytePosition==(ulong)(1024+count-1)&&range.Length==Total,"native Content-Range retains original offsets and full file length");
            Check(response.Content.Headers.ContentLength==(ulong)count,"native response Content-Length matches the delivered byte range (actual="+response.Content.Headers.ContentLength+")");
            var buffer=await response.Content.ReadAsBufferAsync();var bytes=buffer.ToArray();
            Check(bytes.Length==count&&bytes.Where((value,index)=>value!=Value(1024+(ulong)index)).Count()==0,"real WinRT IBuffer roundtrip retains every media byte");
        }
    }
    private static async Task Run()
    {
        await TestRangeTrace();
        var tailServer = new Server { HoldMedia = true };
        using (var tailFilter = new RangeAccelerationFilter(tailServer, Routes(), new DownloadSpeedMeter()))
        using (var tailClient = new HttpClient(tailFilter))
        using (var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Video)))
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
        {
            request.Headers.TryAppendWithoutValidation("Range", "bytes=1024-2098175");
            request.Headers.TryAppendWithoutValidation("X-Fixture-Context", "video");
            try
            {
                var headers = tailClient.SendRequestAsync(request, HttpCompletionOption.ResponseHeadersRead).AsTask(timeout.Token);
                Check(await Task.WhenAny(headers, Task.Delay(2000)) == headers, "accelerated HTTP headers arrive before a withheld media tail");
                using (var response = await headers)
                using (var input = await response.Content.ReadAsInputStreamAsync())
                {
                    var first = input.ReadAsync(new Windows.Storage.Streams.Buffer(128),128,InputStreamOptions.Partial).AsTask(timeout.Token);
                    Check(await Task.WhenAny(first,Task.Delay(2000))==first&&(await first).Length>0&&!tailServer.AllowMedia.Task.IsCompleted,
                        "real native stream reads validated first bytes without waiting for the whole multi-megabyte range");
                    tailServer.AllowMedia.TrySetResult(true);
                    long received=(await first).Length;
                    while(true){var part=await input.ReadAsync(new Windows.Storage.Streams.Buffer(65536),65536,InputStreamOptions.Partial).AsTask(timeout.Token);if(part.Length==0)break;received+=part.Length;}
                    Check(received==2*1024*1024,"streamed native response reaches exact EOF after tail release");
                }
            }
            finally {tailServer.AllowMedia.TrySetResult(true);}
        }
        var server=new Server();var meter=new DownloadSpeedMeter();
        var signed=new Uri("https://upos-hz-mirrorakam.akamaized.net/upgcxcode/1/2/3/3-1-30125.m4s?e=x&deadline=1&os=akam");
        var donor=new Uri("https://upos-sz-mirrorcos.bilivideo.com/upgcxcode/1/2/3/3-1-30125.m4s?e=x&deadline=1&os=cosbv");
        var expanded=CdnMirrors.Expand(new[]{signed,donor});
        Check(expanded[0]==signed&&expanded[1]==donor&&expanded.Count==2+CdnMirrors.Hosts.Length-1
            &&expanded.Skip(2).All(uri=>uri.PathAndQuery==donor.PathAndQuery&&uri.DnsSafeHost.EndsWith(".bilivideo.com"))
            &&expanded.Select(uri=>uri.DnsSafeHost).Distinct().Count()==expanded.Count,
            "mirror expansion keeps server routes first and swaps only the host of a bilivideo upos donor");
        var relay=new Uri("https://xy1x2x3x4xy.mcdn.bilivideo.cn:8082/v1/resource/a.m4s?os=mcdn");
        Check(CdnMirrors.Expand(new[]{relay}).Count==1,"PCDN/MCDN relay URLs are never used as mirror donors");
        var failServer=new Server{FailMedia=true};
        using(var failedFilter=new RangeAccelerationFilter(failServer,Routes(),new DownloadSpeedMeter()))
        using(var failedClient=new HttpClient(failedFilter))
        {
            bool failed=false;try{using(var ignored=await Send(failedClient)){}}catch(Exception){failed=true;}
            Check(failed&&failedFilter.HasBodyFailure&&failedFilter.Diagnostic.Contains("bypass=True"),
                "a failure after response headers is surfaced and disables further acceleration on this source");
            await Verify(await Send(failedClient));
        }
        using(var filter=new RangeAccelerationFilter(server,Routes(),meter))
        using(var client=new HttpClient(filter))
        {
            var responses=await Task.WhenAll(Send(client),Send(client,Audio,65536));
            await Verify(responses[0]);await Verify(responses[1],65536);
            Check(filter.Diagnostic.Contains("acceleratedRanges=1")&&filter.Diagnostic.Contains("nativeFallbacks=0"),"video accelerated while audio remains native");
            Check(server.Requests>4&&server.Peak<=2*ParallelRangeDownloader.Concurrency+1&&server.HeaderErrors==0,"native headers/context survive concurrent audio/video and bounded subrequests");
            Check(meter.TotalDownloadedBytes>=2*1024*1024,"meter counts actual subresponse body reads");
        }
        server=new Server{InvalidSubranges=true};
        using(var filter=new RangeAccelerationFilter(server,Routes(),new DownloadSpeedMeter()))
        using(var client=new HttpClient(filter))
        {
            await Verify(await Send(client));
            Check(filter.Diagnostic.Contains("acceleratedRanges=0")&&filter.Diagnostic.Contains("nativeFallbacks=1"),"invalid 200 subresponses cleanly fall back to the original native request");
            Check(filter.Diagnostic.Contains("bypass=False") && !filter.HasBodyFailure,
                "one native retry before accelerated response headers needs no playback restart and keeps acceleration for later ranges");
            await Verify(await Send(client));await Verify(await Send(client));
            Check(filter.Diagnostic.Contains("bypass=True") && !filter.HasBodyFailure,
                "repeated pre-header failures switch this source to native requests");
        }
        server=new Server{DifferentBackup=true};
        using(var filter=new RangeAccelerationFilter(server,Routes(),new DownloadSpeedMeter()))
        using(var client=new HttpClient(filter))
        {
            await Verify(await Send(client));
            Check(server.BackupMedia==0,"different backup anchor is never used for media pieces");
            using(var request=new HttpRequestMessage(HttpMethod.Get,new Uri(Video)))
            {
                request.Headers.TryAppendWithoutValidation("Range","bytes=1024-66559");
                request.Headers.TryAppendWithoutValidation("Authorization","Bearer fixture-only");
                int before=server.Requests;
                using(var response=await client.SendRequestAsync(request))Check(server.Requests==before+1,"credential-bearing request bypasses subrequest cloning");
            }
        }
        server=new Server{Hold=true};
        using(var filter=new RangeAccelerationFilter(server,Routes(),new DownloadSpeedMeter()))
        using(var client=new HttpClient(filter))
        using(var cancel=new CancellationTokenSource())
        {
            var task=Send(client,Video,2*1024*1024,cancel.Token);cancel.CancelAfter(100);
            bool canceled=false;try{using(var response=await task){}}catch(OperationCanceledException){canceled=true;}
            for(int i=0;i<100&&Volatile.Read(ref server.Active)!=0;i++)await Task.Delay(10);
            Check(canceled&&server.Active==0,"real native client cancellation reaches and drains all outstanding managed filter requests");
        }
    }
    private static async Task TestRangeTrace()
    {
        var server = new Server { HoldOffset = 1024 + ParallelRangeDownloader.AnchorBytes };
        var log = new ConcurrentQueue<string>();
        var foundGap = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var meter = new DownloadSpeedMeter();
        using (var filter = new RangeAccelerationFilter(server, Routes(), meter, detail =>
        {
            log.Enqueue(detail);
            if (detail.Contains(" write ") && detail.Contains("gap=16384")) foundGap.TrySetResult(true);
        }))
        using (var client = new HttpClient(filter))
        using (var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Video)))
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6)))
        {
            request.Headers.TryAppendWithoutValidation("Range", "bytes=1024-2098175");
            request.Headers.TryAppendWithoutValidation("X-Fixture-Context", "video");
            using (var response = await client.SendRequestAsync(request, HttpCompletionOption.ResponseHeadersRead).AsTask(timeout.Token))
            using (var input = await response.Content.ReadAsInputStreamAsync())
            {
                var clock = Stopwatch.StartNew();
                int consumed = 0;
                while (consumed < ParallelRangeDownloader.AnchorBytes)
                {
                    var head = await input.ReadAsync(new Windows.Storage.Streams.Buffer(16384),
                        (uint)(ParallelRangeDownloader.AnchorBytes - consumed), InputStreamOptions.Partial).AsTask(timeout.Token);
                    if (head.Length == 0) throw new IOException("trace fixture lost the anchor");
                    consumed += (int)head.Length;
                }
                var read = input.ReadAsync(new Windows.Storage.Streams.Buffer(65536),65536,InputStreamOptions.Partial).AsTask(timeout.Token);
                Check(await Task.WhenAny(foundGap.Task, Task.Delay(2000)) == foundGap.Task,
                    "a later subrange is fully written while the earlier Range is pending");
                await Task.Delay(250, timeout.Token);
                Check(!read.IsCompleted && log.Any(line => line.Contains(" read-wait ") && line.Contains("consumedNext=16384")),
                    "the WinRT HTTP consumer is blocked at the missing byte");
                long blockedMs = clock.ElapsedMilliseconds;
                long downloadedBeforeRelease = meter.TotalDownloadedBytes;
                server.AllowMedia.TrySetResult(true);
                do
                {
                    var buffer = await read;
                    if (buffer.Length == 0) break;
                    var bytes = buffer.ToArray();
                    Check(!bytes.Where((value, index) => value != Value((ulong)(1024 + consumed + index))).Any(), "joined media bytes remain intact");
                    consumed += bytes.Length;
                    read = input.ReadAsync(new Windows.Storage.Streams.Buffer(65536),65536,InputStreamOptions.Partial).AsTask(timeout.Token);
                }
                while (true);
                Check(consumed == 2*1024*1024 && log.Any(line => line.Contains("consumedNext=2097152")),
                    "the consumer reaches the exact response end after the missing Range arrives");
                Check(log.Any(line => line.Contains(" fetch-end ") && line.Contains("start=17408 end=541695") &&
                    line.Contains("http=206") && line.Contains("received=524288") && line.Contains("firstBodyReadMs=")) &&
                    log.All(line => !line.Contains("signature") && !line.Contains("/v.m4s")),
                    "timing and range bounds are recorded without the signed URL");
                Console.WriteLine("Range gap fixture: held cursor 16384 with " + downloadedBeforeRelease +
                    " bytes downloaded at " + blockedMs + " ms before release. After release, consumed " + consumed + " bytes intact.");
            }
        }
        var pausedServer = new Server();
        var quietMeter = new DownloadSpeedMeter();
        var windowWait = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var filter = new RangeAccelerationFilter(pausedServer, Routes(), quietMeter, detail =>
        {
            if (detail.Contains(" window-wait ") && detail.Contains("consumedNext=0")) windowWait.TrySetResult(true);
        }))
        using (var client = new HttpClient(filter))
        using (var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Video)))
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6)))
        {
            request.Headers.TryAppendWithoutValidation("Range", "bytes=1024-4195327");
            request.Headers.TryAppendWithoutValidation("X-Fixture-Context", "video");
            using (var response = await client.SendRequestAsync(request, HttpCompletionOption.ResponseHeadersRead).AsTask(timeout.Token))
            {
                Check(await Task.WhenAny(windowWait.Task, Task.Delay(2000)) == windowWait.Task, "pre-read window applies consumer backpressure");
                await Task.Delay(2300, timeout.Token);
                Check(quietMeter.CurrentDownloadBytesPerSecond == 0, "an idle consumer's measured intake falls to zero despite an unrestricted source");
                Console.WriteLine("Range backpressure fixture: " + quietMeter.TotalDownloadedBytes + " bytes sampled, then 0 B/s while the consumer was idle.");
                await Verify(response, 4*1024*1024);
            }
        }
    }
    private static int Main(string[] args)
    {
        try{Run().GetAwaiter().GetResult();if(args.Length>0)RunPlayback(args[0]).GetAwaiter().GetResult();Console.WriteLine("PASS: "+checks+" real Windows HTTP/IBuffer acceleration, headers, audio isolation, fallback and cancellation checks (in-memory server; no Xbox HDR output).");return 0;}
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
    private static string Segment(byte[] data)
    {
        int at=0;
        while(at+8<=data.Length)
        {
            int size=(data[at]<<24)|(data[at+1]<<16)|(data[at+2]<<8)|data[at+3];
            if(size<8||size>data.Length-at)throw new Exception("invalid fixture atom");
            if(Encoding.ASCII.GetString(data,at+4,4)=="sidx")return "<SegmentBase indexRange='"+at+"-"+(at+size-1)+"'><Initialization range='0-"+(at-1)+"'/></SegmentBase>";
            at+=size;
        }
        throw new Exception("fixture requires global SIDX");
    }
    private static async Task RunPlayback(string folder)
    {
        var video=File.ReadAllBytes(Path.Combine(folder,"video.mp4"));var audio=File.ReadAllBytes(Path.Combine(folder,"audio.mp4"));
        string manifest="<MPD xmlns='urn:mpeg:dash:schema:mpd:2011' type='static' profiles='urn:mpeg:dash:profile:isoff-on-demand:2011' mediaPresentationDuration='PT18S' minBufferTime='PT3S'><Period start='PT0S' duration='PT18S'>"
            +"<AdaptationSet contentType='video' mimeType='video/mp4'><Representation id='v' bandwidth='2500000' codecs='avc1.42c01e' width='640' height='360' frameRate='30'><BaseURL>"+Video+"</BaseURL>"+Segment(video)+"</Representation></AdaptationSet>"
            +"<AdaptationSet contentType='audio' mimeType='audio/mp4'><Representation id='a' bandwidth='128000' codecs='mp4a.40.2' audioSamplingRate='48000'><BaseURL>"+Audio+"</BaseURL>"+Segment(audio)+"</Representation></AdaptationSet></Period></MPD>";
        string settings=Path.Combine(folder,"video-settings.txt");
        int width=640;
        if(File.Exists(settings))
        {
            var values=File.ReadAllLines(settings); width=int.Parse(values[1]);
            SlowBody.DelayMilliseconds=5;
            var doc=System.Xml.Linq.XDocument.Parse(manifest);var ns=doc.Root.Name.Namespace;
            var rep=doc.Descendants(ns+"Representation").First();
            rep.SetAttributeValue("codecs",values[0]);rep.SetAttributeValue("width",values[1]);
            rep.SetAttributeValue("height",values[2]);rep.SetAttributeValue("frameRate",values[3]);
            manifest=doc.ToString();
        }
        foreach(bool compatible in new[]{false,true})
        foreach(bool accelerated in new[]{false,true})
        {
            string current=manifest;
            if(compatible)
            {
                var doc=System.Xml.Linq.XDocument.Parse(manifest);var ns=doc.Root.Name.Namespace;
                foreach(var rep in doc.Descendants(ns+"Representation"))
                {
                    var data=(string)rep.Attribute("id")=="v"?video:audio;
                    var segment=rep.Element(ns+"SegmentBase");var parts=((string)segment.Attribute("indexRange")).Split('-');
                    int start=int.Parse(parts[0]),length=int.Parse(parts[1])-start+1;
                    var bytes=new byte[length];Array.Copy(data,start,bytes,0,length);
                    DashSegmentIndex.Parse(bytes,(ulong)start,(ulong)data.Length).Replace(segment);
                }
                doc.Root.SetAttributeValue("profiles","urn:mpeg:dash:profile:isoff-main:2011");current=doc.ToString();
            }
            var origin=new PlaybackOrigin(video,audio);var meter=new DownloadSpeedMeter();
            var routes=new DashRoutePolicy(current,new[]{Video,Backup},new[]{Audio},0);
            using(var acceleration=new RangeAccelerationFilter(origin,routes,meter))
            using(var http=new HttpClient(accelerated?(IHttpFilter)acceleration:origin))
            using(var stream=new InMemoryRandomAccessStream())
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(current).AsBuffer());stream.Seek(0);
                var result=await AdaptiveMediaSource.CreateFromStreamAsync(stream,new Uri("https://primary.example.test/"),"application/dash+xml",http);
                Check(result.Status==AdaptiveMediaSourceCreationStatus.Success,"native SegmentBase fixture opens");
                var opened=new TaskCompletionSource<bool>();
                using(var adaptive=result.MediaSource)
                using(var source=MediaSource.CreateFromAdaptiveMediaSource(adaptive))
                using(var player=new MediaPlayer{IsMuted=true,AutoPlay=false})
                {
                    player.MediaOpened+=(s,e)=>opened.TrySetResult(true);
                    player.MediaFailed+=(s,e)=>{Console.WriteLine("fixture media failure "+e.Error+" "+e.ExtendedErrorCode?.HResult.ToString("X8"));opened.TrySetResult(false);};
                    var item = new MediaPlaybackItem(source);
                    player.Source=item;if(!compatible)player.Play();
                    bool ready=await Task.WhenAny(opened.Task,Task.Delay(12000))==opened.Task&&await opened.Task;
                    Check(ready,"real MediaPlayer opens throttled video, acceleration="+accelerated);
                    if(compatible){player.PlaybackSession.Position=TimeSpan.FromSeconds(12);player.Play();}
                    await Task.Delay(6500);
                    double position=player.PlaybackSession.Position.TotalSeconds;
                    Console.WriteLine("PLAYBACK compatible="+compatible+" acceleration="+accelerated+" state="+player.PlaybackSession.PlaybackState+" position="+position.ToString("0.00")+" native="+player.PlaybackSession.NaturalVideoWidth+"x"+player.PlaybackSession.NaturalVideoHeight+" "+acceleration.Diagnostic);
                    Check(position>=(compatible?13.5:3)&&player.PlaybackSession.NaturalVideoWidth==width,"native dimensions and clock advance with throttled body and resume");
                    int audioTrack = item.AudioTracks.SelectedIndex;
                    Check(audioTrack >= 0 && audioTrack < item.AudioTracks.Count
                        && item.AudioTracks[audioTrack].GetEncodingProperties().SampleRate > 0,
                        "the native player retains the selected audio track while the video advances");
                    if(accelerated)Check(acceleration.Diagnostic.Contains("acceleratedRanges=")&&!acceleration.Diagnostic.Contains("acceleratedRanges=0 "),"actual native media requests use acceleration");
                    player.Pause();player.Source=null;
                }
            }
        }
    }
    private sealed class PlaybackOrigin:IHttpFilter
    {
        private readonly byte[] video,audio;
        internal PlaybackOrigin(byte[] v,byte[] a){video=v;audio=a;}
        public IAsyncOperationWithProgress<HttpResponseMessage,HttpProgress> SendRequestAsync(HttpRequestMessage request)
            =>AsyncInfo.Run<HttpResponseMessage,HttpProgress>((ct,progress)=>
            {
                var data=request.RequestUri.AbsolutePath.Contains("a.m4s")?audio:video;
                string header;ulong offset;int count;
                if(!request.Headers.TryGetValue("Range",out header)||!ParallelRangeDownloader.TryParseRange(header,out offset,out count))throw new Exception("native request requires exact Range");
                var content=new HttpStreamContent(new SlowBody(data,(int)offset,count).AsInputStream());
                content.Headers.ContentLength=(ulong)count;content.Headers.ContentRange=new HttpContentRangeHeaderValue(offset,offset+(ulong)count-1,(ulong)data.Length);
                content.Headers.TryAppendWithoutValidation("Content-Type",request.RequestUri.AbsolutePath.Contains("a.m4s")?"audio/mp4":"video/mp4");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent){Content=content,RequestMessage=request});
            });
        public void Dispose(){}
    }
    private sealed class SlowBody:MemoryStream
    {
        internal static int DelayMilliseconds=20;
        internal SlowBody(byte[] data,int offset,int count):base(data,offset,count,false){}
        public override async Task<int> ReadAsync(byte[] buffer,int offset,int count,CancellationToken ct)
        {await Task.Delay(DelayMilliseconds,ct);return await base.ReadAsync(buffer,offset,Math.Min(count,32768),ct);}
    }
    private sealed class Server:IHttpFilter
    {
        internal int Requests,Active,Peak,HeaderErrors,BackupMedia;
        internal bool InvalidSubranges,DifferentBackup,Hold,HoldMedia,FailMedia;
        internal ulong? HoldOffset;
        internal readonly TaskCompletionSource<bool> AllowMedia=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        public IAsyncOperationWithProgress<HttpResponseMessage,HttpProgress> SendRequestAsync(HttpRequestMessage request)
            =>AsyncInfo.Run<HttpResponseMessage,HttpProgress>(async(ct,progress)=>
            {
                Interlocked.Increment(ref Requests);int active=Interlocked.Increment(ref Active),seen;
                do{seen=Volatile.Read(ref Peak);if(seen>=active)break;}while(Interlocked.CompareExchange(ref Peak,active,seen)!=seen);
                try
                {
                    await Task.Yield();if(Hold)await Task.Delay(Timeout.Infinite,ct);ct.ThrowIfCancellationRequested();
                    string range;request.Headers.TryGetValue("Range",out range);ulong offset;int count;
                    if(!ParallelRangeDownloader.TryParseRange(range,out offset,out count))throw new Exception("fixture expected finite Range");
                    if(FailMedia&&count>ParallelRangeDownloader.AnchorBytes&&count<=ParallelRangeDownloader.ChunkBytes)
                        throw new IOException("fixture delayed media failure");
                    if((HoldMedia&&count!=ParallelRangeDownloader.AnchorBytes) || HoldOffset == offset)
                    {
                        var canceled=new TaskCompletionSource<bool>();
                        using(ct.Register(()=>canceled.TrySetCanceled()))await await Task.WhenAny(AllowMedia.Task,canceled.Task);
                    }
                    bool backup=request.RequestUri.Host=="backup.example.test";
                    string context;request.Headers.TryGetValue("X-Fixture-Context",out context);
                    if(request.RequestUri.AbsolutePath.EndsWith("a.m4s")?context!="audio":context!="video"&&!request.Headers.ContainsKey("Authorization"))Interlocked.Increment(ref HeaderErrors);
                    if(backup&&count!=ParallelRangeDownloader.AnchorBytes)Interlocked.Increment(ref BackupMedia);
                    var bytes=Enumerable.Range(0,count).Select(index=>Value(offset+(ulong)index)).ToArray();
                    if(backup&&DifferentBackup)bytes[0]^=0xff;
                    var content=new HttpBufferContent(bytes.AsBuffer());
                    content.Headers.ContentRange=new HttpContentRangeHeaderValue(offset,offset+(ulong)count-1,Total);
                    content.Headers.TryAppendWithoutValidation("Content-Type","video/mp4");
                    return new HttpResponseMessage(InvalidSubranges&&count==ParallelRangeDownloader.AnchorBytes?HttpStatusCode.Ok:HttpStatusCode.PartialContent){Content=content,RequestMessage=request};
                }
                finally{Interlocked.Decrement(ref Active);}
            });
        public void Dispose(){}
    }
}
