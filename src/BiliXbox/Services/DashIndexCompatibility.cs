using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Windows.Storage.Streams;
using Windows.Web.Http;
using Windows.Web.Http.Filters;

namespace BiliXbox.Services
{
    internal static class DashIndexCompatibility
    {
        internal static async Task<string> ConvertAsync(string manifest, IReadOnlyList<string> videoUrls,
            IReadOnlyList<string> audioUrls, int routeOffset, CancellationToken token, Action<string> diagnostic)
        {
            var document = Read(manifest); var ns = document.Root.Name.Namespace;
            var routes = new DashRoutePolicy(manifest, videoUrls, audioUrls, routeOffset);
            using (var filter = new HttpBaseProtocolFilter { AutomaticDecompression = false, CookieUsageBehavior = HttpCookieUsageBehavior.NoCookies })
            using (var http = new HttpClient(filter))
            {
                filter.CacheControl.ReadBehavior = HttpCacheReadBehavior.NoCache;
                filter.CacheControl.WriteBehavior = HttpCacheWriteBehavior.NoCache;
                http.DefaultRequestHeaders.Referer = new Uri(BilibiliClient.Referer);
                http.DefaultRequestHeaders.TryAppendWithoutValidation("User-Agent", BilibiliClient.UserAgent);
                http.DefaultRequestHeaders.TryAppendWithoutValidation("Accept-Encoding", "identity");
                int converted = 0;
                foreach (var representation in document.Descendants(ns + "Representation"))
                {
                    token.ThrowIfCancellationRequested();
                    var segment = representation.Element(ns + "SegmentBase");
                    if (segment == null) continue;
                    var uri = new Uri((string)representation.Element(ns + "BaseURL"));
                    ulong start, end;
                    var parts = ((string)segment.Attribute("indexRange") ?? "").Split('-');
                    if (parts.Length != 2 || !ulong.TryParse(parts[0], out start) || !ulong.TryParse(parts[1], out end)
                        || end < start || end - start >= DashSegmentIndex.MaximumIndexBytes) throw new InvalidDataException("媒体索引范围无效。");
                    var candidates = routes.CandidateUris(uri);
                    if (candidates.Count == 0) throw new InvalidDataException("媒体索引缺少已验证的播放线路。");
                    DashSegmentIndex index = null; Exception last = null;
                    foreach (var candidate in candidates.Take(3))
                    {
                        using (var attempt = CancellationTokenSource.CreateLinkedTokenSource(token))
                        {
                            attempt.CancelAfter(TimeSpan.FromSeconds(8));
                            try { index = await FetchAsync(http, candidate, start, (int)(end - start + 1), attempt.Token); break; }
                            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                            catch (Exception ex) when (!(ex is OutOfMemoryException))
                            { last = ex; diagnostic?.Invoke("index-compat-attempt-failed kind=" + ex.GetType().Name + " hresult=" + ex.HResult.ToString("X8")); }
                        }
                    }
                    if (index == null) throw last ?? new InvalidDataException("无法读取媒体索引。");
                    diagnostic?.Invoke("index-compat-parsed track=" + (string)representation.Attribute("id")
                        + " version=" + index.Version + " referenceId=" + index.ReferenceId + " timescale=" + index.Timescale
                        + " segments=" + index.Segments.Count + " firstByte=" + index.Segments[0].Start
                        + " lastByte=" + index.Segments[index.Segments.Count - 1].End);
                    index.Replace(segment); ++converted;
                }
                if (converted == 0) throw new InvalidDataException("没有可转换的媒体索引。");
                document.Root.SetAttributeValue("profiles", "urn:mpeg:dash:profile:isoff-main:2011");
                return document.ToString(SaveOptions.DisableFormatting);
            }
        }

        private static async Task<DashSegmentIndex> FetchAsync(HttpClient http, Uri uri, ulong start, int length, CancellationToken token)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, uri))
            {
                request.Headers.TryAppendWithoutValidation("Range", "bytes=" + start + "-" + (start + (ulong)length - 1));
                using (var response = await http.SendRequestAsync(request, HttpCompletionOption.ResponseHeadersRead).AsTask(token))
                {
                    var range = response.Content?.Headers.ContentRange;
                    if (response.StatusCode != HttpStatusCode.PartialContent || range == null || range.Unit != "bytes"
                        || range.FirstBytePosition != start || range.LastBytePosition != start + (ulong)length - 1 || !range.Length.HasValue
                        || (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength != (ulong)length))
                        throw new InvalidDataException("媒体索引线路没有返回正确的 HTTP 206 范围。");
                    var bytes = new byte[length]; int received = 0;
                    using (var input = await response.Content.ReadAsInputStreamAsync().AsTask(token))
                    {
                        while (received < length)
                        {
                            var buffer = new Windows.Storage.Streams.Buffer((uint)(length - received));
                            var part = await input.ReadAsync(buffer, buffer.Capacity, InputStreamOptions.Partial).AsTask(token);
                            if (part.Length == 0) throw new EndOfStreamException("媒体索引不完整。");
                            part.CopyTo(0, bytes, received, (int)part.Length); received += (int)part.Length;
                        }
                    }
                    return DashSegmentIndex.Parse(bytes, start, range.Length.Value);
                }
            }
        }
        private static XDocument Read(string manifest)
        {
            using (var text = new StringReader(manifest))
            using (var reader = System.Xml.XmlReader.Create(text, new System.Xml.XmlReaderSettings
                { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null })) return XDocument.Load(reader);
        }
    }
}
