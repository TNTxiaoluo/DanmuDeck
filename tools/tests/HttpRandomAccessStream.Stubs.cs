// TEST ONLY: minimal WinRT and HTTP doubles for HttpRandomAccessStream behavior.
// Test-Stream.ps1 compiles the REAL service source together with these doubles.
// No SDK, native WinRT components, Media Foundation, Xbox, or network is involved.
// Passing these tests does NOT mean the UWP app builds or plays video on Xbox.
// Real request cancellation, WinRT ABI projection, HTTP TLS/redirect behavior,
// and MediaSource decoding still require the Windows SDK and device testing.
// FakeInput deliberately returns small chunks to exercise incomplete reads.
// Transport.File is deterministic; assertions compare every returned byte.
namespace Windows.Foundation {
 public interface IAsyncOperation<T> { System.Threading.Tasks.Task<T> Task { get; } }
 public interface IAsyncOperationWithProgress<T,P> : IAsyncOperation<T> { }
 public sealed class Operation<T,P> : IAsyncOperationWithProgress<T,P> { public System.Threading.Tasks.Task<T> Task {get; private set;} public Operation(System.Threading.Tasks.Task<T> task){Task=task;} }
}
namespace System.Runtime.InteropServices.WindowsRuntime {
 public static class AsyncInfo {
  private sealed class ProgressSink<P> : System.IProgress<P> { public void Report(P value){} }
  public static global::Windows.Foundation.IAsyncOperationWithProgress<T,P> Run<T,P>(System.Func<System.Threading.CancellationToken,System.IProgress<P>,System.Threading.Tasks.Task<T>> fn){return new global::Windows.Foundation.Operation<T,P>(fn(System.Threading.CancellationToken.None,new ProgressSink<P>()));}
  public static global::Windows.Foundation.IAsyncOperation<T> Run<T>(System.Func<System.Threading.CancellationToken,System.Threading.Tasks.Task<T>> fn){return new global::Windows.Foundation.Operation<T,uint>(fn(System.Threading.CancellationToken.None));}
 }
 public static class Extensions {
  public static System.Threading.Tasks.Task<T> AsTask<T>(this global::Windows.Foundation.IAsyncOperation<T> operation,System.Threading.CancellationToken token){token.ThrowIfCancellationRequested();return operation.Task;}
  public static void CopyTo(this global::Windows.Storage.Streams.IBuffer source,uint offset,global::Windows.Storage.Streams.IBuffer destination,uint index,uint count){if(offset+count>source.Length||index+count>destination.Capacity)throw new System.ArgumentOutOfRangeException();System.Buffer.BlockCopy(((global::Windows.Storage.Streams.Buffer)source).Data,(int)offset,((global::Windows.Storage.Streams.Buffer)destination).Data,(int)index,(int)count);}
 }
}
namespace Windows.Storage.Streams {
 [System.Flags] public enum InputStreamOptions{None=0,Partial=1,ReadAhead=2}
 public interface IBuffer{uint Capacity{get;}uint Length{get;set;}}
 public sealed class Buffer:IBuffer{public byte[] Data; public Buffer(uint count){Data=new byte[count];} public uint Capacity{get{return (uint)Data.Length;}}public uint Length{get;set;}}
 public interface IInputStream:System.IDisposable{global::Windows.Foundation.IAsyncOperationWithProgress<IBuffer,uint> ReadAsync(IBuffer buffer,uint count,InputStreamOptions options);}
 public interface IOutputStream:System.IDisposable{global::Windows.Foundation.IAsyncOperationWithProgress<uint,uint> WriteAsync(IBuffer buffer);global::Windows.Foundation.IAsyncOperation<bool> FlushAsync();}
 public interface IRandomAccessStream:IInputStream,IOutputStream{bool CanRead{get;}bool CanWrite{get;}ulong Size{get;set;}ulong Position{get;}void Seek(ulong position);IRandomAccessStream CloneStream();IInputStream GetInputStreamAt(ulong position);IOutputStream GetOutputStreamAt(ulong position);}
 public sealed class FakeInput:IInputStream {
  private byte[] _data;private int _position;public FakeInput(byte[] data){_data=data;}
  public global::Windows.Foundation.IAsyncOperationWithProgress<IBuffer,uint> ReadAsync(IBuffer buffer,uint count,InputStreamOptions options){var n=System.Math.Min(8191,System.Math.Min((int)count,_data.Length-_position));var b=(Buffer)buffer;System.Array.Copy(_data,_position,b.Data,0,n);b.Length=(uint)n;_position+=n;return new global::Windows.Foundation.Operation<IBuffer,uint>(System.Threading.Tasks.Task.FromResult<IBuffer>(b));}public void Dispose(){}
 }
}
namespace Windows.Web.Http.Headers {
 public sealed class HttpRequestHeaderCollection {
  public System.Uri Referer{get;set;}public System.Collections.Generic.Dictionary<string,string> Values=new System.Collections.Generic.Dictionary<string,string>();public bool TryAppendWithoutValidation(string key,string value){Values[key]=value;return true;}
 }
 public sealed class HttpContentRangeHeaderValue{public ulong? Length{get;set;}public ulong? FirstBytePosition{get;set;}public ulong? LastBytePosition{get;set;}public string Unit{get;set;}}
 public sealed class HttpContentHeaderCollection{public ulong? ContentLength{get;set;}public HttpContentRangeHeaderValue ContentRange{get;set;}}
}
namespace Windows.Web.Http.Filters {
 public enum HttpCacheReadBehavior{NoCache} public enum HttpCacheWriteBehavior{NoCache}
 public sealed class HttpCacheControl{public HttpCacheReadBehavior ReadBehavior{get;set;}public HttpCacheWriteBehavior WriteBehavior{get;set;}}
 public sealed class HttpBaseProtocolFilter:System.IDisposable{public bool AutomaticDecompression{get;set;}public HttpCacheControl CacheControl=new HttpCacheControl();public void Dispose(){}}
}
namespace Windows.Web.Http {
 public enum HttpStatusCode{Ok=200,PartialContent=206,Forbidden=403}
 public enum HttpCompletionOption{ResponseHeadersRead}
 public sealed class HttpMethod{public static HttpMethod Get=new HttpMethod();}
 public sealed class HttpRequestMessage:System.IDisposable{public HttpRequestMessage(HttpMethod method,System.Uri uri){}public Headers.HttpRequestHeaderCollection Headers=new Headers.HttpRequestHeaderCollection();public void Dispose(){}}
 public sealed class Content {
  public byte[] Data;public Headers.HttpContentHeaderCollection Headers=new Headers.HttpContentHeaderCollection();public global::Windows.Foundation.IAsyncOperation<global::Windows.Storage.Streams.IInputStream> ReadAsInputStreamAsync(){BiliXbox.StreamTests.Transport.BodyReads++;return new global::Windows.Foundation.Operation<global::Windows.Storage.Streams.IInputStream,uint>(System.Threading.Tasks.Task.FromResult<global::Windows.Storage.Streams.IInputStream>(new global::Windows.Storage.Streams.FakeInput(Data)));}
 }
 public sealed class HttpResponseMessage:System.IDisposable{public HttpStatusCode StatusCode;public Content Content=new Content();public void EnsureSuccessStatusCode(){if((int)StatusCode<200||(int)StatusCode>=300)throw new System.IO.IOException("HTTP status");}public void Dispose(){}}
 public sealed class HttpClient:System.IDisposable {
  private bool _disposed;public Headers.HttpRequestHeaderCollection DefaultRequestHeaders=new Headers.HttpRequestHeaderCollection();public HttpClient(Filters.HttpBaseProtocolFilter filter){BiliXbox.StreamTests.Transport.LiveClients++;}
  public global::Windows.Foundation.IAsyncOperationWithProgress<HttpResponseMessage,uint> SendRequestAsync(HttpRequestMessage request,HttpCompletionOption option){if(_disposed)throw new System.ObjectDisposedException("client");if(DefaultRequestHeaders.Referer.AbsoluteUri!="https://www.bilibili.com/")throw new System.Exception("Missing Referer");if(!DefaultRequestHeaders.Values.ContainsKey("User-Agent"))throw new System.Exception("Missing User-Agent");var result=BiliXbox.StreamTests.Transport.AsyncHandler!=null?BiliXbox.StreamTests.Transport.AsyncHandler(request):System.Threading.Tasks.Task.FromResult(BiliXbox.StreamTests.Transport.Handler(request));return new global::Windows.Foundation.Operation<HttpResponseMessage,uint>(result);}public void Dispose(){if(!_disposed){_disposed=true;BiliXbox.StreamTests.Transport.LiveClients--;}}
 }
}
namespace BiliXbox.StreamTests {
 using global::Windows.Storage.Streams;using global::Windows.Web.Http;using global::Windows.Web.Http.Headers;
 public static class Transport {
  public static System.Func<HttpRequestMessage,HttpResponseMessage> Handler;public static System.Func<HttpRequestMessage,System.Threading.Tasks.Task<HttpResponseMessage>> AsyncHandler;public static int Requests,BodyReads,LiveClients;public static byte[] File;public static string LastRange;
  public static void Reset(int length){Requests=0;BodyReads=0;AsyncHandler=null;LastRange=null;File=new byte[length];for(int i=0;i<length;i++)File[i]=(byte)((i*31)%251);Handler=Range;}
  public static HttpResponseMessage Range(HttpRequestMessage request){Requests++;LastRange=request.Headers.Values["Range"];var parts=LastRange.Substring(6).Split('-');ulong start=ulong.Parse(parts[0]),end=System.Math.Min(ulong.Parse(parts[1]),(ulong)File.Length-1);int n=checked((int)(end-start+1));var bytes=new byte[n];System.Array.Copy(File,(int)start,bytes,0,n);var r=new HttpResponseMessage{StatusCode=HttpStatusCode.PartialContent};r.Content.Data=bytes;r.Content.Headers.ContentLength=(ulong)n;r.Content.Headers.ContentRange=new HttpContentRangeHeaderValue{Unit="bytes",FirstBytePosition=start,LastBytePosition=end,Length=(ulong)File.Length};return r;}
 }
 public static class Tests {
  private static int _passed;private static void Assert(bool value,string name){if(!value)throw new System.Exception("FAILED: "+name);_passed++;}
  private static async System.Threading.Tasks.Task ExpectError(System.Func<System.Threading.Tasks.Task> fn,string name){try{await fn();}catch(System.IO.IOException){_passed++;return;}throw new System.Exception("FAILED error missing: "+name);}
  private static async System.Threading.Tasks.Task<IBuffer> Read(IInputStream stream,uint n,InputStreamOptions options=InputStreamOptions.None){return await stream.ReadAsync(new Buffer(n),n,options).AsTask(System.Threading.CancellationToken.None);}
  private static void Verify(IBuffer b,int offset,int count,string name){Assert(b.Length==(uint)count,name+" length");var data=((Buffer)b).Data;for(int i=0;i<count;i++)if(data[i]!=Transport.File[offset+i])throw new System.Exception("FAILED bytes: "+name);_passed++;}
  public static async System.Threading.Tasks.Task<string> Run(){
   _passed=0;
   var uri=new System.Uri("https://cdn.example/video.mp4");const int block=262144;
   Transport.Reset(block*3+123);
   var stream=await BiliXbox.Services.HttpRandomAccessStream.OpenAsync(uri);
   Assert(Transport.LastRange=="bytes=0-262143"&&Transport.BodyReads==1,"startup downloads at most 256 KiB");
   Assert(stream.Size==(ulong)Transport.File.Length&&stream.Position==0&&stream.CanRead&&!stream.CanWrite,"initial properties");
   Verify(await Read(stream,80000),0,80000,"sequential");Assert(stream.Position==80000,"sequential position");
   stream.Seek(block-10);Verify(await Read(stream,100,InputStreamOptions.Partial),block-10,10,"partial across cache boundary");Assert(stream.Position==block,"partial position");
   stream.Seek(block-40);Verify(await Read(stream,100000),block-40,100000,"multi block read");
   var sourcePosition=stream.Position;var clone=stream.CloneStream();Assert(clone.Position==0,"clone starts at zero");
   Verify(await Read(clone,32),0,32,"clone bytes");Assert(stream.Position==sourcePosition,"clone independent position");
   var at=stream.GetInputStreamAt((ulong)block*2+7);Verify(await Read(at,101),block*2+7,101,"input at offset");Assert(stream.Position==sourcePosition,"input offset independent position");at.Dispose();
   try{var invalidRead=stream.ReadAsync(new Buffer(1),2,InputStreamOptions.None);throw new System.Exception("No bounds error");}catch(System.ArgumentOutOfRangeException){_passed++;}
   stream.Dispose();Assert(!stream.CanRead,"disposed state");
   clone.Seek((ulong)Transport.File.Length-9);Verify(await Read(clone,64),Transport.File.Length-9,9,"clone survives source dispose and seek EOF");Assert((await Read(clone,64)).Length==0,"EOF empty");clone.Dispose();
   Transport.Reset(123);Transport.Handler=r=>new HttpResponseMessage{StatusCode=HttpStatusCode.Ok,Content=new Content{Data=Transport.File,Headers=new HttpContentHeaderCollection{ContentLength=123}}};
   using(var small=await BiliXbox.Services.HttpRandomAccessStream.OpenAsync(uri)){small.Seek(80);Verify(await Read(small,43),80,43,"small HTTP 200");Assert(Transport.BodyReads==1,"small file cached");Assert(small.TotalDownloadedBytes==123,"body bytes counted exactly once");Assert(small.CurrentDownloadBytesPerSecond>0,"actual body throughput sampled");using(var cachedClone=small.CloneStream()){await Read(cachedClone,43);Assert(small.TotalDownloadedBytes==123,"cached decoder reads did not inflate network bytes");Assert(((BiliXbox.Services.HttpRandomAccessStream)cachedClone).TotalDownloadedBytes==small.TotalDownloadedBytes,"clones share one download meter");}}
   Transport.Reset(block+1);Transport.Handler=r=>new HttpResponseMessage{StatusCode=HttpStatusCode.Ok,Content=new Content{Data=Transport.File,Headers=new HttpContentHeaderCollection{ContentLength=(ulong)Transport.File.Length}}};
   await ExpectError(async()=>{await BiliXbox.Services.HttpRandomAccessStream.OpenAsync(uri);},"large HTTP 200");Assert(Transport.BodyReads==0,"reject large unbounded body before read");
   Transport.Reset(block*2);Transport.Handler=r=>{var response=Transport.Range(r);response.Content.Headers.ContentRange.FirstBytePosition=1;return response;};await ExpectError(async()=>{await BiliXbox.Services.HttpRandomAccessStream.OpenAsync(uri);},"wrong initial range");
   Transport.Reset(block*2);Transport.Handler=r=>{var response=Transport.Range(r);response.Content.Headers.ContentRange=null;return response;};await ExpectError(async()=>{await BiliXbox.Services.HttpRandomAccessStream.OpenAsync(uri);},"missing Content-Range");
   Transport.Reset(block*2);Transport.Handler=r=>{var response=Transport.Range(r);response.Content.Headers.ContentLength=1;return response;};await ExpectError(async()=>{await BiliXbox.Services.HttpRandomAccessStream.OpenAsync(uri);},"content length mismatch");
   Transport.Reset(block*2);Transport.Handler=r=>{var response=Transport.Range(r);response.Content.Data=new byte[17];return response;};await ExpectError(async()=>{await BiliXbox.Services.HttpRandomAccessStream.OpenAsync(uri);},"truncated range");
   Transport.Reset(block*2);using(var changed=await BiliXbox.Services.HttpRandomAccessStream.OpenAsync(uri)){Transport.Handler=r=>{var response=Transport.Range(r);response.Content.Headers.ContentRange.Length=(ulong)Transport.File.Length+1;return response;};changed.Seek(block);await ExpectError(async()=>{await Read(changed,20);},"entity size changed");}
   Transport.Reset(block*5);using(var shared=await BiliXbox.Services.HttpRandomAccessStream.OpenAsync(uri)){
    Verify(await Read(shared,32),0,32,"read before prefetch");int reads=Transport.Requests;
    using(var ahead=shared.GetInputStreamAt((ulong)block+7)){Verify(await Read(ahead,101),block+7,101,"read-ahead bytes shared with clone");Assert(Transport.Requests==reads+1,"shared clone fetched only the following read-ahead block");}
    reads=Transport.Requests;using(var rewind=shared.CloneStream()){Verify(await Read(rewind,32),0,32,"rewind cache shared");Assert(Transport.Requests==reads,"clone reused both cached current and next blocks");}
   }
   Transport.Reset(block*40);using(var bounded=await BiliXbox.Services.HttpRandomAccessStream.OpenAsync(uri)){
    for(int i=0;i<35;i++){bounded.Seek((ulong)(block*i));Verify(await Read(bounded,1),block*i,1,"bounded sequential "+i);}
    int reads=Transport.Requests;bounded.Seek(0);Verify(await Read(bounded,1),0,1,"reload evicted initial block");Assert(Transport.Requests>reads,"shared cache evicted old blocks instead of growing indefinitely");
   }
   Transport.Reset(block*4);
   var delayed=new System.Threading.Tasks.TaskCompletionSource<HttpResponseMessage>();HttpRequestMessage waitingRequest=null;
   Transport.AsyncHandler=r=>{if(r.Headers.Values["Range"].StartsWith("bytes=262144-",System.StringComparison.Ordinal)){waitingRequest=r;return delayed.Task;}return System.Threading.Tasks.Task.FromResult(Transport.Range(r));};
   var slow=await BiliXbox.Services.HttpRandomAccessStream.OpenAsync(uri);
   var first=Read(slow,32);Assert(first.IsCompleted,"a slow read-ahead request did not block current cached bytes");Verify(await first,0,32,"cached bytes during slow prefetch");Assert(waitingRequest!=null,"slow-network fixture reached background request");
   slow.Seek(block);var waitingRead=Read(slow,32);Assert(!waitingRead.IsCompleted,"next-block read awaited slow prefetch");slow.Dispose();
   try{await waitingRead;throw new System.Exception("FAILED canceled pending read completed");}catch(System.OperationCanceledException){_passed++;}
   delayed.SetResult(Transport.Range(waitingRequest));await System.Threading.Tasks.Task.Delay(20);
   return "PASS: "+_passed+" assertions covering random access, bounded shared cache, asynchronous read-ahead, slow-network cancellation, boundaries, EOF, clone lifetime, partial reads, HTTP 200 control, malformed ranges, truncation, headers.";
  }
 }
}
