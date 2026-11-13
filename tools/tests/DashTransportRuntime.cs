// Native WinRT transport check: the server withholds most of a range until the
// client proves it can read its first bytes. No external service is involved.
using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage.Streams;
using Windows.Web.Http;
using Windows.Web.Http.Filters;
using BiliXbox.Services;

internal static class DashTransportRuntime
{
    internal static async Task Run()
    {
        const int length = 1024 * 1024;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        var allowRemainder = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.Start();
        var address = new Uri("http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/test-range");
        var server = Serve(listener, length, allowRemainder.Task);
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        using (var filter = new HttpBaseProtocolFilter { CookieUsageBehavior = HttpCookieUsageBehavior.NoCookies })
        {
            // Match the production native HttpClient chain. DASH no longer
            // wraps response bodies in a managed stream just to count reads.
            using (var http = new HttpClient(filter))
            using (var request = new HttpRequestMessage(HttpMethod.Get, address))
            {
                request.Headers.TryAppendWithoutValidation("Range", "bytes=0-1048575");
                try
                {
                    var pending = http.SendRequestAsync(request, HttpCompletionOption.ResponseHeadersRead).AsTask(cancellation.Token);
                    if (await Task.WhenAny(pending, Task.Delay(3000)).ConfigureAwait(false) != pending)
                        throw new InvalidOperationException("The range response was buffered before returning its headers.");
                    using (var response = await pending.ConfigureAwait(false))
                    using (var stream = await response.Content.ReadAsInputStreamAsync().AsTask(cancellation.Token).ConfigureAwait(false))
                    {
                        var firstRead = stream.ReadAsync(new Windows.Storage.Streams.Buffer(64), 64, InputStreamOptions.Partial).AsTask(cancellation.Token);
                        if (await Task.WhenAny(firstRead, Task.Delay(3000)).ConfigureAwait(false) != firstRead)
                            throw new InvalidOperationException("The range body was buffered to completion before its first read.");
                        var first = await firstRead.ConfigureAwait(false);
                        if (first.Length == 0 || allowRemainder.Task.IsCompleted)
                            throw new InvalidOperationException("The native range did not expose its first body bytes progressively.");
                        allowRemainder.TrySetResult(true);
                        long read = first.Length;
                        var buffer = new Windows.Storage.Streams.Buffer(65536);
                        while (true)
                        {
                            var chunk = await stream.ReadAsync(buffer, buffer.Capacity, InputStreamOptions.Partial).AsTask(cancellation.Token).ConfigureAwait(false);
                            if (chunk.Length == 0) break;
                            read += chunk.Length;
                        }
                        if (read != length)
                            throw new InvalidOperationException("The completed native range had an unexpected length.");
                        Console.WriteLine("PASS: native DASH HTTP chain returns headers and first bytes before the rest of a1MiB HTTP206 range is released; body length is exact.");
                    }
                }
                finally { allowRemainder.TrySetResult(true); listener.Stop(); }
            }
        }
        await server.ConfigureAwait(false);
    }

    private static async Task Serve(TcpListener listener, int length, Task allowRemainder)
    {
        using (var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false))
        using (var stream = client.GetStream())
        {
            var request = new byte[4096];
            int used = 0;
            while (used < request.Length)
            {
                int received = await stream.ReadAsync(request, used, request.Length - used).ConfigureAwait(false);
                if (received == 0) throw new InvalidOperationException("Test request ended before headers.");
                used += received;
                if (Encoding.ASCII.GetString(request, 0, used).Contains("\r\n\r\n")) break;
            }
            var headers = Encoding.ASCII.GetBytes("HTTP/1.1 206 Partial Content\r\nContent-Length: " + length
                + "\r\nContent-Range: bytes 0-1048575/1048576\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(headers, 0, headers.Length).ConfigureAwait(false);
            var bytes = new byte[16384];
            await stream.WriteAsync(bytes, 0, 64).ConfigureAwait(false);
            await allowRemainder.ConfigureAwait(false);
            for (int remaining = length - 64; remaining > 0;)
            {
                int count = Math.Min(remaining, bytes.Length);
                await stream.WriteAsync(bytes, 0, count).ConfigureAwait(false);
                remaining -= count;
            }
        }
    }
}
