using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Models;
using Newtonsoft.Json;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.Web.Http;
using Windows.Web.Http.Filters;

namespace BiliXbox.Services
{
    public enum OfflineCacheState { Queued, Downloading, Paused, Completed, Failed }

    // One stored part of one video at one quality.
    public sealed class OfflineCacheEntry
    {
        public string Id { get; set; } = "";
        public string Bvid { get; set; } = "";
        public long Aid { get; set; }
        public long Cid { get; set; }
        public string Title { get; set; } = "";
        public string PartTitle { get; set; } = "";
        public int PartCount { get; set; } = 1;
        public string Owner { get; set; } = "";
        public long OwnerMid { get; set; }
        public string CoverUrl { get; set; } = "";
        public double DurationSeconds { get; set; }
        public int QualityId { get; set; }
        public string QualityLabel { get; set; } = "";
        public string Kind { get; set; } = "sdr";
        public string VideoCodec { get; set; } = "";
        public int Width { get; set; }
        public int Height { get; set; }
        public double FrameRate { get; set; }
        public int BitsPerChannel { get; set; }
        public long VideoBandwidth { get; set; }
        public long AudioBandwidth { get; set; }
        public long VideoBytes { get; set; }
        public long AudioBytes { get; set; }
        public long VideoDone { get; set; }
        public long AudioDone { get; set; }
        public bool VideoFinalized { get; set; }
        public bool HasCover { get; set; }
        public bool HasDanmaku { get; set; }
        public OfflineCacheState State { get; set; }
        public string Error { get; set; } = "";
        public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;

        [JsonIgnore] public double BytesPerSecond { get; set; }

        [JsonIgnore] public long TotalBytes => VideoBytes + AudioBytes > 0 ? VideoBytes + AudioBytes
            : (long)((VideoBandwidth + AudioBandwidth) / 8.0 * DurationSeconds);
        [JsonIgnore] public long DoneBytes => VideoDone + AudioDone;
        [JsonIgnore] public double Progress => State == OfflineCacheState.Completed ? 1 : TotalBytes > 0 ? Math.Min(1, DoneBytes / (double)TotalBytes) : 0;

        internal static string MakeId(string bvid, long cid) => bvid + "_" + cid.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    // Offline video storage in LocalCacheFolder\Offline. Each entry is a
    // folder with the untouched DASH video and audio files (video.mp4,
    // audio.m4a), the cover and the danmaku list. One download runs at a
    // time; ranges are fetched four at a time and written in order, so the
    // stored length is always a resumable prefix. Signed CDN URLs expire:
    // a refused range asks Resolver for fresh URLs of the same quality.
    public sealed class OfflineCache
    {
        public static OfflineCache Instance { get; } = new OfflineCache();

        private const string FolderName = "Offline";
        private const string IndexName = "index.json";
        private const uint ChunkBytes = 4 * 1024 * 1024;
        private const int ParallelChunks = 4;

        private readonly object _gate = new object();
        private readonly List<OfflineCacheEntry> _entries = new List<OfflineCacheEntry>();
        private readonly SemaphoreSlim _indexWrite = new SemaphoreSlim(1, 1);
        private Task _loadTask;
        private CancellationTokenSource _workerCts;
        private string _activeId;
        private bool _suspended;

        // Fresh URLs for an entry's own quality (CDN signatures expire).
        public Func<OfflineCacheEntry, CancellationToken, Task<OfflineQualityOption>> Resolver { get; set; }
        // Raised on any thread when an entry or the list changes.
        public event Action Changed;

        public Task LoadAsync() => _loadTask ?? (_loadTask = LoadCoreAsync());

        public IReadOnlyList<OfflineCacheEntry> Entries { get { lock (_gate) return _entries.OrderByDescending(entry => entry.Created).ToArray(); } }

        public OfflineCacheEntry Find(string bvid, long cid)
        {
            string id = OfflineCacheEntry.MakeId(bvid, cid);
            lock (_gate) return _entries.FirstOrDefault(entry => entry.Id == id);
        }

        public OfflineCacheEntry FindCompleted(string bvid, long cid)
        {
            var entry = Find(bvid, cid);
            return entry != null && entry.State == OfflineCacheState.Completed ? entry : null;
        }

        public IReadOnlyList<OfflineCacheEntry> CompletedParts(string bvid)
        { lock (_gate) return _entries.Where(entry => entry.Bvid == bvid && entry.State == OfflineCacheState.Completed).ToArray(); }

        public static async Task<StorageFolder> RootAsync()
            => await ApplicationData.Current.LocalCacheFolder.CreateFolderAsync(FolderName, CreationCollisionOption.OpenIfExists);

        internal static async Task<StorageFolder> EntryFolderAsync(OfflineCacheEntry entry)
            => await (await RootAsync()).CreateFolderAsync(entry.Id, CreationCollisionOption.OpenIfExists);

        public static async Task<ulong?> GetFreeSpaceAsync()
        {
            try
            {
                var folder = ApplicationData.Current.LocalCacheFolder;
                var props = await folder.Properties.RetrievePropertiesAsync(new[] { "System.FreeSpace" });
                object value;
                if (props.TryGetValue("System.FreeSpace", out value) && value != null) return Convert.ToUInt64(value);
            }
            catch (Exception) { }
            return null;
        }

        public static string FormatBytes(double bytes)
        {
            if (bytes >= 1024d * 1024 * 1024) return (bytes / (1024d * 1024 * 1024)).ToString("0.00") + " GB";
            if (bytes >= 1024d * 1024) return (bytes / (1024d * 1024)).ToString("0.0") + " MB";
            return Math.Max(0, bytes / 1024d).ToString("0") + " KB";
        }

        // A new download (or a re-download at another quality) of one part.
        public async Task<OfflineCacheEntry> EnqueueAsync(VideoDetail video, long cid, OfflineQualityOption option,
            IReadOnlyList<DanmakuItem> danmaku)
        {
            await LoadAsync();
            var existing = Find(video.Bvid, cid);
            if (existing != null) await DeleteAsync(existing);
            var part = video.Parts?.FirstOrDefault(item => item.Cid == cid);
            var entry = new OfflineCacheEntry
            {
                Id = OfflineCacheEntry.MakeId(video.Bvid, cid), Bvid = video.Bvid, Aid = video.Aid, Cid = cid,
                Title = video.Title, PartTitle = (video.Parts?.Count ?? 0) > 1 ? part?.Title ?? "" : "", PartCount = Math.Max(1, video.Parts?.Count ?? 1),
                Owner = video.Owner, OwnerMid = video.OwnerMid, CoverUrl = video.CoverUrl, DurationSeconds = option.DurationSeconds,
                QualityId = option.QualityId, QualityLabel = option.Label, Kind = option.Kind, VideoCodec = option.VideoCodec,
                Width = option.Width, Height = option.Height, FrameRate = option.FrameRate, BitsPerChannel = option.BitsPerChannel,
                VideoBandwidth = option.VideoBandwidth, AudioBandwidth = option.AudioBandwidth, State = OfflineCacheState.Queued
            };
            var folder = await EntryFolderAsync(entry);
            if (danmaku != null)
            {
                try
                {
                    var file = await folder.CreateFileAsync("danmaku.json", CreationCollisionOption.ReplaceExisting);
                    await FileIO.WriteTextAsync(file, JsonConvert.SerializeObject(danmaku));
                    entry.HasDanmaku = true;
                }
                catch (Exception) { }
            }
            lock (_gate) _entries.Add(entry);
            _pendingSources[entry.Id] = option;
            await SaveAsync();
            RaiseChanged();
            _ = DownloadCoverAsync(entry);
            Kick();
            return entry;
        }

        private readonly Dictionary<string, OfflineQualityOption> _pendingSources = new Dictionary<string, OfflineQualityOption>();

        public void Pause(OfflineCacheEntry entry)
        {
            if (entry == null || entry.State == OfflineCacheState.Completed) return;
            entry.State = OfflineCacheState.Paused;
            entry.BytesPerSecond = 0;
            if (_activeId == entry.Id) _workerCts?.Cancel();
            _ = SaveAsync(); RaiseChanged();
        }

        public void Resume(OfflineCacheEntry entry)
        {
            if (entry == null || entry.State == OfflineCacheState.Completed || entry.State == OfflineCacheState.Downloading) return;
            entry.State = OfflineCacheState.Queued; entry.Error = "";
            _ = SaveAsync(); RaiseChanged(); Kick();
        }

        public async Task DeleteAsync(OfflineCacheEntry entry)
        {
            if (entry == null) return;
            lock (_gate) _entries.Remove(entry);
            _pendingSources.Remove(entry.Id);
            if (_activeId == entry.Id) _workerCts?.Cancel();
            try
            {
                var root = await RootAsync();
                var folder = await root.TryGetItemAsync(entry.Id) as StorageFolder;
                if (folder != null)
                {
                    // An open file (still playing) fails the delete; retry briefly.
                    for (int attempt = 0; attempt < 5; ++attempt)
                    {
                        try { await folder.DeleteAsync(StorageDeleteOption.PermanentDelete); break; }
                        catch (Exception) { await Task.Delay(400); }
                    }
                }
            }
            catch (Exception) { }
            await SaveAsync(); RaiseChanged();
        }

        // App lifecycle: stop network work while suspended, resume afterwards.
        public void Suspend() { _suspended = true; _workerCts?.Cancel(); }
        public void ResumeAll() { _suspended = false; Kick(); }

        private void Kick()
        {
            if (_suspended) return;
            lock (_gate)
            {
                if (_activeId != null) return;
                var next = _entries.Where(entry => entry.State == OfflineCacheState.Queued || entry.State == OfflineCacheState.Downloading)
                    .OrderBy(entry => entry.Created).FirstOrDefault();
                if (next == null) return;
                _activeId = next.Id;
                _workerCts = new CancellationTokenSource();
                var token = _workerCts.Token;
                _ = Task.Run(() => RunAsync(next, token));
            }
        }

        private async Task RunAsync(OfflineCacheEntry entry, CancellationToken token)
        {
            try
            {
                entry.State = OfflineCacheState.Downloading; entry.Error = ""; RaiseChanged();
                OfflineQualityOption source;
                _pendingSources.TryGetValue(entry.Id, out source);
                if (source == null) source = await ResolveAsync(entry, token);
                var folder = await EntryFolderAsync(entry);
                var meter = new RateMeter(entry);
                if (entry.AudioBytes == 0 || entry.AudioDone < entry.AudioBytes)
                    await DownloadTrackAsync(entry, folder, "audio.m4a", false, source, meter, token);
                if (!entry.VideoFinalized)
                {
                    await DownloadTrackAsync(entry, folder, "video.mp4", true, source, meter, token);
                    await FinalizeVideoAsync(entry, folder);
                }
                entry.State = OfflineCacheState.Completed; entry.BytesPerSecond = 0;
                _pendingSources.Remove(entry.Id);
            }
            catch (OperationCanceledException)
            {
                entry.BytesPerSecond = 0;
                if (entry.State == OfflineCacheState.Downloading) entry.State = _suspended ? OfflineCacheState.Queued : OfflineCacheState.Paused;
            }
            catch (Exception ex)
            {
                entry.BytesPerSecond = 0;
                entry.State = OfflineCacheState.Failed;
                entry.Error = ex.Message;
            }
            finally
            {
                lock (_gate) { _activeId = null; }
                bool alive; lock (_gate) alive = _entries.Contains(entry);
                if (alive) await SaveAsync();
                RaiseChanged();
                Kick();
            }
        }

        private async Task<OfflineQualityOption> ResolveAsync(OfflineCacheEntry entry, CancellationToken token)
        {
            var resolver = Resolver;
            if (resolver == null) throw new InvalidOperationException("播放地址需要重新获取，请打开应用后继续。");
            var option = await resolver(entry, token);
            if (option == null) throw new InvalidOperationException("此画质已不可用，请重新缓存。");
            _pendingSources[entry.Id] = option;
            return option;
        }

        private sealed class RateMeter
        {
            private readonly OfflineCacheEntry _entry;
            private readonly Stopwatch _clock = Stopwatch.StartNew();
            private readonly Queue<KeyValuePair<double, long>> _samples = new Queue<KeyValuePair<double, long>>();
            private long _total;
            internal RateMeter(OfflineCacheEntry entry) { _entry = entry; }
            internal void Add(long bytes)
            {
                double now = _clock.Elapsed.TotalSeconds;
                _total += bytes;
                _samples.Enqueue(new KeyValuePair<double, long>(now, _total));
                while (_samples.Count > 1 && now - _samples.Peek().Key > 4) _samples.Dequeue();
                var first = _samples.Peek();
                double span = now - first.Key;
                if (span > 0.5) _entry.BytesPerSecond = (_total - first.Value) / span;
            }
        }

        private async Task DownloadTrackAsync(OfflineCacheEntry entry, StorageFolder folder, string name, bool video,
            OfflineQualityOption source, RateMeter meter, CancellationToken token)
        {
            var file = await folder.CreateFileAsync(name, CreationCollisionOption.OpenIfExists);
            using (var output = await file.OpenAsync(FileAccessMode.ReadWrite))
            using (var client = CreateClient())
            {
                long done = (long)output.Size;
                long total = video ? entry.VideoBytes : entry.AudioBytes;
                if (total <= 0)
                {
                    total = await ProbeLengthAsync(client, entry, video, source, token);
                    source = _pendingSources.TryGetValue(entry.Id, out var fresh) ? fresh : source;
                    if (video) entry.VideoBytes = total; else entry.AudioBytes = total;
                    await SaveAsync();
                }
                if (done > total) { output.Size = 0; done = 0; }
                if (video) entry.VideoDone = done; else entry.AudioDone = done;
                RaiseChanged();
                var lastSave = Stopwatch.StartNew();
                var pending = new Queue<KeyValuePair<long, Task<byte[]>>>();
                long next = done;
                while (done < total)
                {
                    token.ThrowIfCancellationRequested();
                    while (pending.Count < ParallelChunks && next < total)
                    {
                        long start = next, end = Math.Min(total, next + ChunkBytes) - 1;
                        pending.Enqueue(new KeyValuePair<long, Task<byte[]>>(start, FetchRangeAsync(client, entry, video, start, end, token)));
                        next = end + 1;
                    }
                    var head = pending.Dequeue();
                    byte[] bytes = await head.Value;
                    output.Seek((ulong)head.Key);
                    await output.WriteAsync(bytes.AsBuffer());
                    done = head.Key + bytes.Length;
                    meter.Add(bytes.Length);
                    if (video) entry.VideoDone = done; else entry.AudioDone = done;
                    RaiseChanged();
                    if (lastSave.Elapsed.TotalSeconds > 3)
                    {
                        await output.FlushAsync();
                        await SaveAsync();
                        lastSave.Restart();
                    }
                }
                await output.FlushAsync();
            }
        }

        private static HttpClient CreateClient()
        {
            var filter = new HttpBaseProtocolFilter();
            filter.CacheControl.ReadBehavior = HttpCacheReadBehavior.NoCache;
            filter.CacheControl.WriteBehavior = HttpCacheWriteBehavior.NoCache;
            var client = new HttpClient(filter);
            client.DefaultRequestHeaders.TryAppendWithoutValidation("User-Agent", BilibiliClient.UserAgent);
            client.DefaultRequestHeaders.Referer = new Uri(BilibiliClient.Referer);
            return client;
        }

        private IReadOnlyList<string> Urls(OfflineCacheEntry entry, bool video)
        {
            OfflineQualityOption source;
            if (!_pendingSources.TryGetValue(entry.Id, out source) || source == null) return Array.Empty<string>();
            return video ? source.VideoUrls : source.AudioUrls;
        }

        private async Task<long> ProbeLengthAsync(HttpClient client, OfflineCacheEntry entry, bool video, OfflineQualityOption source, CancellationToken token)
        {
            for (int round = 0; round < 2; ++round)
            {
                foreach (var url in Urls(entry, video))
                {
                    try
                    {
                        using (var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url)))
                        {
                            request.Headers.TryAppendWithoutValidation("Range", "bytes=0-1");
                            using (var response = await client.SendRequestAsync(request, HttpCompletionOption.ResponseHeadersRead).AsTask(token))
                            {
                                string range;
                                if (response.Content.Headers.TryGetValue("Content-Range", out range))
                                {
                                    int slash = range.LastIndexOf('/');
                                    long total;
                                    if (slash >= 0 && long.TryParse(range.Substring(slash + 1), out total) && total > 0) return total;
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception) { }
                }
                await ResolveAsync(entry, token);
            }
            throw new InvalidOperationException("无法读取文件大小，请检查网络后重试。");
        }

        private async Task<byte[]> FetchRangeAsync(HttpClient client, OfflineCacheEntry entry, bool video, long start, long end, CancellationToken token)
        {
            int expected = (int)(end - start + 1);
            Exception last = null;
            for (int round = 0; round < 4; ++round)
            {
                var urls = Urls(entry, video);
                for (int index = 0; index < urls.Count; ++index)
                {
                    string url = urls[(index + (int)(start / ChunkBytes)) % urls.Count];
                    try
                    {
                        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                        {
                            timeout.CancelAfter(TimeSpan.FromSeconds(30));
                            using (var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url)))
                            {
                                request.Headers.TryAppendWithoutValidation("Range", "bytes=" + start + "-" + end);
                                using (var response = await client.SendRequestAsync(request, HttpCompletionOption.ResponseContentRead).AsTask(timeout.Token))
                                {
                                    if (response.StatusCode == HttpStatusCode.PartialContent)
                                    {
                                        var buffer = await response.Content.ReadAsBufferAsync().AsTask(timeout.Token);
                                        if (buffer.Length == expected) return buffer.ToArray();
                                        last = new InvalidOperationException("分段长度不符");
                                    }
                                    else last = new InvalidOperationException("HTTP " + (int)response.StatusCode);
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception ex) { last = ex; }
                }
                if (round == 1) await ResolveAsync(entry, token);
                else await Task.Delay(1000 * (round + 1), token);
            }
            throw new InvalidOperationException("下载中断：" + (last?.Message ?? "网络错误"));
        }

        // Make the stored fragmented MP4 seekable on its own (a random-access
        // index built from its sidx, like streaming playback) and, for Dolby
        // Vision Profile 8.1, hide the configuration box so the HDR10 base
        // layer plays (same as the streaming path's in-memory patch).
        private async Task FinalizeVideoAsync(OfflineCacheEntry entry, StorageFolder folder)
        {
            var file = await folder.GetFileAsync("video.mp4");
            using (var stream = await file.OpenAsync(FileAccessMode.ReadWrite))
            {
                ulong size = stream.Size;
                uint prefixLength = (uint)Math.Min(size, 4UL * 1024 * 1024);
                var buffer = new Windows.Storage.Streams.Buffer(prefixLength);
                stream.Seek(0);
                await stream.ReadAsync(buffer, prefixLength, InputStreamOptions.None);
                var prefix = buffer.ToArray();
                if (entry.Kind == "dv81")
                {
                    foreach (var offset in DolbyVisionBaseLayer.FindConfigBoxTypes(prefix))
                    {
                        stream.Seek((ulong)offset);
                        await stream.WriteAsync(DolbyVisionBaseLayer.FreeType.AsBuffer());
                    }
                }
                var tail = Mp4RandomAccessIndex.Build(prefix, size);
                if (tail != null && tail.Length > 0)
                {
                    stream.Seek(size);
                    await stream.WriteAsync(tail.AsBuffer());
                }
                await stream.FlushAsync();
            }
            entry.VideoFinalized = true;
        }

        private async Task DownloadCoverAsync(OfflineCacheEntry entry)
        {
            if (string.IsNullOrWhiteSpace(entry.CoverUrl)) return;
            try
            {
                using (var client = CreateClient())
                {
                    string url = entry.CoverUrl.StartsWith("//") ? "https:" + entry.CoverUrl : entry.CoverUrl;
                    var buffer = await client.GetBufferAsync(new Uri(url + (url.Contains("@") ? "" : "@480w_270h_1c.jpg")));
                    var folder = await EntryFolderAsync(entry);
                    var file = await folder.CreateFileAsync("cover.jpg", CreationCollisionOption.ReplaceExisting);
                    await FileIO.WriteBufferAsync(file, buffer);
                    entry.HasCover = true;
                    await SaveAsync(); RaiseChanged();
                }
            }
            catch (Exception) { }
        }

        // Fetches the current danmaku list of a part (set by the page).
        public Func<long, CancellationToken, Task<IReadOnlyList<DanmakuItem>>> DanmakuFetcher { get; set; }

        // Replace a stored part's danmaku with the current list; returns the count.
        public async Task<int> RefreshDanmakuAsync(OfflineCacheEntry entry, CancellationToken token = default(CancellationToken))
        {
            var fetcher = DanmakuFetcher;
            if (entry == null || fetcher == null) throw new InvalidOperationException("弹幕暂时无法更新。");
            var items = await fetcher(entry.Cid, token) ?? Array.Empty<DanmakuItem>();
            var folder = await EntryFolderAsync(entry);
            var file = await folder.CreateFileAsync("danmaku.json", CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteTextAsync(file, JsonConvert.SerializeObject(items));
            entry.HasDanmaku = true;
            await SaveAsync(); RaiseChanged();
            return items.Count;
        }

        public static async Task<IReadOnlyList<DanmakuItem>> LoadDanmakuAsync(OfflineCacheEntry entry)
        {
            if (entry == null || !entry.HasDanmaku) return null;
            try
            {
                var file = await (await EntryFolderAsync(entry)).GetFileAsync("danmaku.json");
                return JsonConvert.DeserializeObject<List<DanmakuItem>>(await FileIO.ReadTextAsync(file));
            }
            catch (Exception) { return null; }
        }

        private async Task LoadCoreAsync()
        {
            try
            {
                var root = await RootAsync();
                var file = await root.TryGetItemAsync(IndexName) as StorageFile;
                if (file == null) return;
                var list = JsonConvert.DeserializeObject<List<OfflineCacheEntry>>(await FileIO.ReadTextAsync(file)) ?? new List<OfflineCacheEntry>();
                lock (_gate)
                {
                    _entries.Clear();
                    foreach (var entry in list.Where(item => !string.IsNullOrEmpty(item?.Id)))
                    {
                        if (entry.State == OfflineCacheState.Downloading) entry.State = OfflineCacheState.Queued;
                        _entries.Add(entry);
                    }
                }
            }
            catch (Exception) { }
            RaiseChanged();
            Kick();
        }

        private async Task SaveAsync()
        {
            await _indexWrite.WaitAsync();
            try
            {
                OfflineCacheEntry[] snapshot;
                lock (_gate) snapshot = _entries.ToArray();
                var root = await RootAsync();
                var file = await root.CreateFileAsync(IndexName, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, JsonConvert.SerializeObject(snapshot));
            }
            catch (Exception) { }
            finally { _indexWrite.Release(); }
        }

        private void RaiseChanged()
        {
            try { Changed?.Invoke(); } catch (Exception) { }
        }
    }
}
