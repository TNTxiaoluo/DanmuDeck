using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Models;
using Windows.Storage.Streams;
using Windows.Web.Http;
using Windows.Web.Http.Filters;

namespace BiliXbox.Services
{
    internal sealed class DanmakuMaskService : IDisposable
    {
        private readonly HttpClient _client;
        private readonly Uri _uri;
        private readonly double _fps;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private IReadOnlyList<WebMaskIndexEntry> _index;
        private IReadOnlyList<DanmakuMaskFrame> _frames;
        private int _loadedBlock = -1;
        private long _length;
        private bool _disposed;

        private DanmakuMaskService(DanmakuMaskInfo info)
        {
            if (info == null) throw new ArgumentNullException(nameof(info));
            Uri address;
            if (!Uri.TryCreate(info.Url, UriKind.Absolute, out address) || address.Scheme != "https")
                throw new ArgumentException("人物遮罩地址必须为有效的 HTTPS 地址。", nameof(info));
            _uri = address; _fps = info.Fps;
            _client = new HttpClient(new HttpBaseProtocolFilter { AllowAutoRedirect = false, CookieUsageBehavior = HttpCookieUsageBehavior.NoCookies });
            _client.DefaultRequestHeaders.TryAppendWithoutValidation("User-Agent", BilibiliClient.UserAgent);
            _client.DefaultRequestHeaders.Referer = new Uri(BilibiliClient.Referer);
        }

        public static async Task<DanmakuMaskService> OpenAsync(DanmakuMaskInfo info, CancellationToken ct = default(CancellationToken))
        {
            var service = new DanmakuMaskService(info);
            try
            {
                int count = WebMaskParser.ReadHeader(await service.ReadRangeAsync(0, 16, ct));
                var bytes = await service.ReadRangeAsync(16, checked(count * 16), ct);
                service._index = WebMaskParser.ReadIndex(bytes, count, service._length);
                return service;
            }
            catch { service.Dispose(); throw; }
        }

        public async Task<DanmakuMaskFrame> GetFrameAtAsync(TimeSpan position, CancellationToken ct = default(CancellationToken))
        {
            if (position < TimeSpan.Zero) return null;
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token))
            {
                await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
                try
                {
                    if (_disposed) throw new ObjectDisposedException(nameof(DanmakuMaskService));
                    int block = -1;
                    for (int index = 0; index < _index.Count && _index[index].Time <= position; ++index) block = index;
                    if (block < 0) return null;
                    if (_loadedBlock != block)
                    {
                        long end = block + 1 < _index.Count ? _index[block + 1].Offset : _length;
                        long count = end - _index[block].Offset;
                        if (count <= 0 || count > WebMaskParser.MaxCompressedBlockBytes)
                            throw new InvalidDataException("人物遮罩分段超出本客户端读取上限。");
                        byte[] bytes = await ReadRangeAsync(_index[block].Offset, (int)count, linked.Token).ConfigureAwait(false);
                        TimeSpan segmentEnd = block + 1 < _index.Count ? _index[block + 1].Time : TimeSpan.MaxValue;
                        var frames = await Task.Run(() => WebMaskParser.ReadBlock(bytes, segmentEnd, _fps, linked.Token), linked.Token).ConfigureAwait(false);
                        linked.Token.ThrowIfCancellationRequested();
                        _frames = frames; _loadedBlock = block;
                    }
                    // Keep only the currently requested compressed block's decoded
                    // frames. A seek loads its target block directly, without replay.
                    return _frames.LastOrDefault(frame => frame.Time <= position && position < frame.EndTime);
                }
                finally { _gate.Release(); }
            }
        }

        private async Task<byte[]> ReadRangeAsync(long start, int count, CancellationToken ct)
        {
            if (start < 0 || count <= 0 || count > WebMaskParser.MaxCompressedBlockBytes) throw new ArgumentOutOfRangeException(nameof(count));
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token))
            using (var request = new HttpRequestMessage(HttpMethod.Get, _uri))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                request.Headers.TryAppendWithoutValidation("Range", "bytes=" + start + "-" + checked(start + count - 1));
                using (var response = await _client.SendRequestAsync(request, HttpCompletionOption.ResponseHeadersRead).AsTask(timeout.Token).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    var range = response.Content.Headers.ContentRange;
                    if (response.StatusCode != HttpStatusCode.PartialContent || range == null || !range.Length.HasValue ||
                        !range.FirstBytePosition.HasValue || !range.LastBytePosition.HasValue || range.FirstBytePosition.Value != (ulong)start ||
                        range.LastBytePosition.Value != (ulong)(start + count - 1) || range.Length.Value > long.MaxValue ||
                        !string.Equals(range.Unit, "bytes", StringComparison.OrdinalIgnoreCase))
                        throw new IOException("人物遮罩服务器没有返回所需的 HTTP 字节范围。");
                    long length = (long)range.Length.Value;
                    if (length <= start || (_length != 0 && _length != length) ||
                        (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value != (ulong)count))
                        throw new IOException("人物遮罩文件长度发生变化或响应不完整。");
                    _length = length;
                    var result = new byte[count];
                    using (var input = await response.Content.ReadAsInputStreamAsync().AsTask(timeout.Token).ConfigureAwait(false))
                    {
                        int received = 0;
                        while (received < count)
                        {
                            var part = await input.ReadAsync(new Windows.Storage.Streams.Buffer((uint)(count - received)),
                                (uint)(count - received), InputStreamOptions.Partial).AsTask(timeout.Token).ConfigureAwait(false);
                            if (part.Length == 0) throw new EndOfStreamException("人物遮罩分段下载中断。");
                            using (var reader = DataReader.FromBuffer(part))
                            {
                                var portion = new byte[part.Length]; reader.ReadBytes(portion);
                                Array.Copy(portion, 0, result, received, portion.Length); received += portion.Length;
                            }
                        }
                    }
                    return result;
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true; _lifetime.Cancel(); _client.Dispose(); _frames = null;
        }
    }
}
