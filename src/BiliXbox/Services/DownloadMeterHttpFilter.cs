using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.Web.Http;
using Windows.Web.Http.Filters;

namespace BiliXbox.Services
{
    /// <summary>
    /// Observes the actual response body consumed by Media Foundation. It leaves
    /// native DASH request scheduling, range requests and cancellation intact.
    /// HttpProgress includes response headers and can finish before body reads;
    /// counting body reads avoids treating that progress as downloaded media.
    /// </summary>
    internal sealed class DownloadMeterHttpFilter : IHttpFilter
    {
        private readonly IHttpFilter _inner;
        private readonly DownloadSpeedMeter _meter;

        public DownloadMeterHttpFilter(IHttpFilter inner, DownloadSpeedMeter meter)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _meter = meter ?? throw new ArgumentNullException(nameof(meter));
        }

        public IAsyncOperationWithProgress<HttpResponseMessage, HttpProgress> SendRequestAsync(HttpRequestMessage request)
        {
            return AsyncInfo.Run<HttpResponseMessage, HttpProgress>(async (token, progress) =>
            {
                var response = await _inner.SendRequestAsync(request).AsTask(token, progress).ConfigureAwait(false);
                try
                {
                    if (response.Content != null)
                    {
                        var original = response.Content;
                        var input = await original.ReadAsInputStreamAsync().AsTask(token).ConfigureAwait(false);
                        var measured = new HttpStreamContent(new DownloadMeterInputStream(input, original, _meter));
                        foreach (var header in original.Headers)
                            measured.Headers.TryAppendWithoutValidation(header.Key, header.Value);
                        response.Content = measured;
                    }
                    return response;
                }
                catch { response.Dispose(); throw; }
            });
        }

        // The owning AdaptivePlaybackSource disposes the native filter once the
        // HTTP client has gone away. Disposing a chained filter twice is avoided.
        public void Dispose() { }
    }

    internal sealed class DownloadMeterInputStream : IInputStream
    {
        private readonly IInputStream _input;
        private readonly IDisposable _content;
        private readonly DownloadSpeedMeter _meter;
        private int _disposed;

        public DownloadMeterInputStream(IInputStream input, IDisposable content, DownloadSpeedMeter meter)
        {
            _input = input ?? throw new ArgumentNullException(nameof(input));
            _content = content;
            _meter = meter ?? throw new ArgumentNullException(nameof(meter));
        }

        public IAsyncOperationWithProgress<IBuffer, uint> ReadAsync(IBuffer buffer, uint count, InputStreamOptions options)
        {
            return AsyncInfo.Run<IBuffer, uint>(async (token, progress) =>
            {
                var received = new ReadProgress(_meter, progress);
                var result = await _input.ReadAsync(buffer, count, options).AsTask(token, received).ConfigureAwait(false);
                // Some streams report progress only intermittently, or never.
                // Complete the count without counting reported bytes twice.
                received.Report(result.Length);
                return result;
            });
        }

        private sealed class ReadProgress : IProgress<uint>
        {
            private readonly object _gate = new object();
            private readonly DownloadSpeedMeter _meter;
            private readonly IProgress<uint> _upstream;
            private uint _received;

            public ReadProgress(DownloadSpeedMeter meter, IProgress<uint> upstream)
            {
                _meter = meter;
                _upstream = upstream;
            }

            public void Report(uint value)
            {
                lock (_gate)
                {
                    if (value <= _received) return;
                    _meter.AddReceivedBytes(value - _received);
                    _received = value;
                    _upstream.Report(value);
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _input.Dispose();
            _content?.Dispose();
        }
    }
}
