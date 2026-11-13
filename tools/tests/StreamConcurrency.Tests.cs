namespace BiliXbox.StreamTests
{
    public static class ConcurrencyTests
    {
        private static int _count;
        private static readonly Uri Address = new Uri("https://cdn.example/video.mp4");
        private const int Block = 262144;
        private static void Check(bool value, string name) { if (!value) throw new Exception(name); ++_count; }
        private static Task<Windows.Storage.Streams.IBuffer> Read(Windows.Storage.Streams.IInputStream input)
            => input.ReadAsync(new Windows.Storage.Streams.Buffer(32), 32, Windows.Storage.Streams.InputStreamOptions.None).AsTask(CancellationToken.None);
        private static async Task Canceled(Task task)
        {
            try { await task; } catch (OperationCanceledException) { ++_count; return; }
            throw new Exception("Canceled clone completed successfully");
        }
        private static void Bytes(Windows.Storage.Streams.IBuffer buffer)
        {
            Check(buffer.Length == 32, "read completes full payload");
            var data = ((Windows.Storage.Streams.Buffer)buffer).Data;
            for (int i = 0; i < 32; ++i) Check(data[i] == Transport.File[3 * Block + i], "shared bytes " + i);
        }
        public static async Task<string> Run()
        {
            _count = 0;
            Check(Transport.LiveClients == 0, "original suite releases HTTP clients");
            Transport.Reset(4 * Block);
            var release = new TaskCompletionSource<Windows.Web.Http.HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            Windows.Web.Http.HttpRequestMessage held = null;
            int requests = 0;
            Transport.AsyncHandler = request =>
            {
                if (request.Headers.Values["Range"].StartsWith("bytes=786432-", StringComparison.Ordinal))
                { ++requests; held = request; return release.Task; }
                return Task.FromResult(Transport.Range(request));
            };
            var source = await Services.HttpRandomAccessStream.OpenAsync(Address);
            var a = source.GetInputStreamAt(3 * Block);
            var b = source.GetInputStreamAt(3 * Block);
            var readA = Read(a); var readB = Read(b);
            Check(requests == 1 && !readA.IsCompleted && !readB.IsCompleted, "two concurrent clones share one pending range");
            Check(Transport.LiveClients == 1, "clones share a single HTTP transport");
            a.Dispose(); await Canceled(readA);
            Check(!readB.IsCompleted, "one clone cancellation leaves the other waiting");
            source.Dispose();
            Check(Transport.LiveClients == 1, "source disposal retains transport needed by clone");
            release.SetResult(Transport.Range(held));
            Bytes(await readB);
            Check(requests == 1 && ((Services.HttpRandomAccessStream)b).TotalDownloadedBytes == 2L * Block,
                "shared network range and meter counted exactly once");
            b.Dispose();
            Check(Transport.LiveClients == 0, "last clone releases shared HTTP client");

            Transport.Reset(4 * Block);
            var fail = new TaskCompletionSource<Windows.Web.Http.HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            requests = 0;
            Transport.AsyncHandler = request =>
            {
                if (request.Headers.Values["Range"].StartsWith("bytes=786432-", StringComparison.Ordinal) && ++requests == 1) return fail.Task;
                return Task.FromResult(Transport.Range(request));
            };
            using (source = await Services.HttpRandomAccessStream.OpenAsync(Address))
            using (a = source.GetInputStreamAt(3 * Block))
            using (b = source.GetInputStreamAt(3 * Block))
            {
                readA = Read(a); readB = Read(b);
                Check(!readA.IsCompleted && !readB.IsCompleted, "both clones await shared failure");
                fail.SetException(new IOException("controlled connection failure"));
                foreach (var read in new[] { readA, readB })
                {
                    bool failed = false;
                    try { await read; } catch (IOException) { failed = true; }
                    Check(failed, "network error reaches each interested clone");
                }
                Bytes(await Read(a));
                Check(requests == 2, "failed flight removed so decoder can retry once");
            }
            Check(Transport.LiveClients == 0, "failed and retried streams release transport");

            Transport.Reset(4 * Block);
            var abandoned = new TaskCompletionSource<Windows.Web.Http.HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            held = null; requests = 0;
            Transport.AsyncHandler = request =>
            {
                if (request.Headers.Values["Range"].StartsWith("bytes=786432-", StringComparison.Ordinal) && ++requests == 1)
                { held = request; return abandoned.Task; }
                return Task.FromResult(Transport.Range(request));
            };
            using (source = await Services.HttpRandomAccessStream.OpenAsync(Address))
            {
                a = source.GetInputStreamAt(3 * Block); b = source.GetInputStreamAt(3 * Block);
                readA = Read(a); readB = Read(b);
                a.Dispose(); b.Dispose(); await Canceled(readA); await Canceled(readB);
                using (var retry = source.GetInputStreamAt(3 * Block)) Bytes(await Read(retry));
                Check(requests == 2, "all waiters canceled removes flight and allows new read");
                abandoned.SetResult(Transport.Range(held));
                await Task.Delay(30);
                Check(source.TotalDownloadedBytes == 2L * Block, "abandoned response is not added to cache or counted again");
            }
            Check(Transport.LiveClients == 0, "canceled flights release final transport");
            return "PASS: " + _count + " shared MP4 flight assertions (deduplication, independent cancellation, retry, exact bytes and clone lifetime).";
        }
    }
}
