using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage.Streams;
using Windows.Web.Http;
using Windows.Web.Http.Filters;

namespace BiliXbox.Services
{
    // Small samples are taken on the playback device, never on a remote server.
    // Their order is a startup hint, not a promise of sustained CDN bandwidth.
    internal static class PlaybackRouteSelector
    {
        internal const int MaximumProbes = 3;
        internal const uint VideoProbeBytes = 65536;
        internal const uint AudioProbeBytes = 32768;
        internal static readonly TimeSpan ProbeBudget = TimeSpan.FromMilliseconds(1800);

        internal static async Task<IReadOnlyList<string>> SelectAsync(IReadOnlyList<string> urls, bool audio,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var candidates = Normalize(urls);
            if (candidates.Count < 2) return candidates;
            using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                lifetime.CancelAfter(ProbeBudget);
                // Start the deadline before constructing clients or starting
                // any native requests. A slow candidate cannot delay playback.
                var deadline = Task.Delay(ProbeBudget, token);
                var tasks = candidates.Take(MaximumProbes).Select((url, index) =>
                    ProbeAsync(url, index, audio ? AudioProbeBytes : VideoProbeBytes, lifetime.Token)).ToArray();
                await Task.WhenAny(Task.WhenAll(tasks), deadline).ConfigureAwait(false);
                lifetime.Cancel();
                token.ThrowIfCancellationRequested();
                var completed = tasks.Where(task => task.Status == TaskStatus.RanToCompletion)
                    .Select(task => task.Result).Where(result => result != null)
                    .OrderByDescending(result => result.BytesPerSecond).ThenBy(result => result.Index).ToList();
                if (completed.Count == 0) return candidates;
                var ranked = completed.Select(result => candidates[result.Index]).ToList();
                var sampled = new HashSet<int>(completed.Select(result => result.Index));
                for (int index = 0; index < candidates.Count; ++index)
                    if (!sampled.Contains(index)) ranked.Add(candidates[index]);
                return ranked;
            }
        }

        private static List<string> Normalize(IReadOnlyList<string> urls)
        {
            var candidates = new List<string>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in urls ?? new string[0])
            {
                Uri uri;
                if (Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Scheme == Uri.UriSchemeHttps
                    && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 && keys.Add(uri.AbsoluteUri))
                    // Preserve the original signed address, including escaping.
                    candidates.Add(value);
            }
            return candidates;
        }

        private static async Task<ProbeResult> ProbeAsync(string url, int index, uint count, CancellationToken token)
        {
            try
            {
                using (var filter = new HttpBaseProtocolFilter
                {
                    AutomaticDecompression = false,
                    CookieUsageBehavior = HttpCookieUsageBehavior.NoCookies
                })
                {
                    filter.CacheControl.ReadBehavior = HttpCacheReadBehavior.NoCache;
                    filter.CacheControl.WriteBehavior = HttpCacheWriteBehavior.NoCache;
                    using (var http = new HttpClient(filter))
                    using (token.Register(() => http.Dispose()))
                    using (var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url)))
                    {
                        http.DefaultRequestHeaders.Referer = new Uri(BilibiliClient.Referer);
                        http.DefaultRequestHeaders.TryAppendWithoutValidation("User-Agent", BilibiliClient.UserAgent);
                        http.DefaultRequestHeaders.TryAppendWithoutValidation("Accept-Encoding", "identity");
                        request.Headers.TryAppendWithoutValidation("Range", "bytes=0-" + (count - 1));
                        var clock = Stopwatch.StartNew();
                        using (var response = await http.SendRequestAsync(request, HttpCompletionOption.ResponseHeadersRead)
                            .AsTask(token).ConfigureAwait(false))
                        {
                            if (response.StatusCode != HttpStatusCode.PartialContent) return null;
                            var headers = response.Content.Headers;
                            var range = headers.ContentRange;
                            if (range == null || !string.Equals(range.Unit, "bytes", StringComparison.OrdinalIgnoreCase)
                                || !range.Length.HasValue || range.Length.Value == 0 || range.FirstBytePosition != 0
                                || !range.LastBytePosition.HasValue) return null;
                            ulong expected = Math.Min((ulong)count, range.Length.Value);
                            if (range.LastBytePosition.Value != expected - 1
                                || (headers.ContentLength.HasValue && headers.ContentLength.Value != expected)) return null;
                            uint read = 0;
                            using (var input = await response.Content.ReadAsInputStreamAsync().AsTask(token).ConfigureAwait(false))
                            {
                                while (read < expected)
                                {
                                    var buffer = new Windows.Storage.Streams.Buffer((uint)Math.Min(16384UL, expected - read));
                                    var chunk = await input.ReadAsync(buffer, buffer.Capacity, InputStreamOptions.Partial)
                                        .AsTask(token).ConfigureAwait(false);
                                    token.ThrowIfCancellationRequested();
                                    if (chunk.Length == 0 || chunk.Length > buffer.Capacity) return null;
                                    read += chunk.Length;
                                }
                            }
                            return new ProbeResult { Index = index, BytesPerSecond = read / Math.Max(0.001, clock.Elapsed.TotalSeconds) };
                        }
                    }
                }
            }
            // Probe errors are deliberately local to this candidate. The real
            // downloader still gets every legal URL and reports playback errors.
            catch (Exception) { return null; }
        }

        private sealed class ProbeResult
        {
            internal int Index;
            internal double BytesPerSecond;
        }
    }
}
