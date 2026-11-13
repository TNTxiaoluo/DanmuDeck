using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Models;
using BiliXbox.Services;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.Streaming.Adaptive;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Input;
using Windows.Web.Http;

namespace BiliXbox.Controls
{
    public sealed partial class LiveHubView
    {
        private MediaPlayer _player;
        private AdaptiveMediaSource _adaptive;
        private HttpClient _playHttp;
        private CancellationTokenSource _playCts;
        private LiveDanmakuClient _socket;
        private CancellationTokenSource _chatCts;
        private int _chatGeneration;
        private readonly Stopwatch _liveClock = new Stopwatch();
        private DateTimeOffset _lastActivity = DateTimeOffset.UtcNow;
        private int _overlayAdmissions;
        private readonly ConcurrentQueue<QueuedLiveMessage> _queuedMessages = new ConcurrentQueue<QueuedLiveMessage>();
        private int _queuedMessageCount;

        private bool RoomCurrent(int generation) => _room != null && generation == _roomGeneration &&
            _roomCts != null && !_roomCts.IsCancellationRequested;

        private async Task OpenRoomAsync(long roomId)
        {
            if (_browseBusy || roomId <= 0) return;
            if (IsPlayingRoom) LeaveRoom(false);
            var browseOwner = _browseCts;
            if (!BrowseCurrent(browseOwner)) return;
            _browseBusy = true; UpdateBrowseActions(); _notice.Text = "正在进入直播间…";
            try
            {
                var room = await _api.GetLiveRoomAsync(roomId, browseOwner.Token);
                if (!BrowseCurrent(browseOwner)) return;
                _room = room; ++_roomGeneration; int generation = _roomGeneration;
                _roomCts = CancellationTokenSource.CreateLinkedTokenSource(_external);
                _roomTitle.Text = room.Title; _roomMeta.Text = room.OwnerName + " · " + room.Area + " · " + room.OnlineText;
                _pane = Pane.None; _quote = null; _gifts = Array.Empty<LiveGift>(); _giftsMid = 0;
                _chatInput.Text = ""; _uncertainChat = ""; ResetChatScroll(); _messages.Items.Clear(); _watchNotice.Text = "左摇杆 / 方向键选择 · A 确认 · R 滚动面板 · B 隐藏控件 / 返回";
                _browse.Visibility = Visibility.Collapsed; _watch.Visibility = Visibility.Visible;
                _drawer.Visibility = Visibility.Collapsed; _wantsPlay = true;
                _liveClock.Reset(); _overlay.Reset(); _overlay.Load(Array.Empty<DanmakuItem>()); _overlayAdmissions = 0;
                _overlay.Visibility = _danmaku ? Visibility.Visible : Visibility.Collapsed;
                WakeChrome(); _tick.Start(); PlaybackChanged?.Invoke(this, EventArgs.Empty); Focus(_play);
                // The room and its chat remain usable when the streamer is
                // offline or media fails; each path reports its own error.
                var chat = ConnectChatAsync(generation);
                if (room.IsLive) await LoadPlaybackAsync(10000, generation);
                else { SetBuffering(false); _watchNotice.Text = "主播暂未开播，可在直播聊天中查看消息。"; }
                await chat;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (BrowseCurrent(browseOwner))
                { if (IsPlayingRoom) { SetBuffering(false); _watchNotice.Text = "直播加载失败：" + Friendly(ex); } else _notice.Text = "无法进入直播间：" + Friendly(ex); }
            }
            finally { if (browseOwner == _browseCts) { _browseBusy = false; UpdateBrowseActions(); } }
        }

        private async Task LoadPlaybackAsync(int quality, int roomGeneration)
        {
            if (!RoomCurrent(roomGeneration)) return;
            int attempt = ++_playGeneration;
            Cancel(ref _playCts); var owner = CancellationTokenSource.CreateLinkedTokenSource(_roomCts.Token); _playCts = owner;
            long roomId = _room.Id;
            SetBuffering(true); _qualityButton.IsEnabled = false;
            _watchNotice.Text = "正在读取直播信息…";
            try
            {
                var playback = await _api.GetLivePlaybackAsync(roomId, quality, owner.Token);
                if (!RoomCurrent(roomGeneration) || attempt != _playGeneration) return;
                _watchNotice.Text = "正在连接直播视频…";
                var candidates = new[] { playback.Url }.Concat(playback.BackupUrls ?? Array.Empty<string>()).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().ToArray();
                Exception failure = null; bool attached = false;
                foreach (string address in candidates)
                {
                    AdaptiveMediaSource adaptive = null; HttpClient http = null;
                    try
                    {
                        Uri uri;
                        if (!Uri.TryCreate(address, UriKind.Absolute, out uri) || (uri.Scheme != "https" && uri.Scheme != "http")) continue;
                        http = new HttpClient();
                        http.DefaultRequestHeaders.Referer = new Uri("https://live.bilibili.com/" + roomId);
                        http.DefaultRequestHeaders.TryAppendWithoutValidation("User-Agent", BilibiliClient.UserAgent);
                        owner.Token.ThrowIfCancellationRequested();
                        var operation = AdaptiveMediaSource.CreateFromUriAsync(uri, http);
                        var result = await LiveSourceConnectionGate.ConnectAsync(operation.AsTask(), owner.Token,
                            TimeSpan.FromSeconds(12), () => operation.Cancel(), late => late.MediaSource?.Dispose());
                        if (!RoomCurrent(roomGeneration) || attempt != _playGeneration) { result.MediaSource?.Dispose(); http.Dispose(); return; }
                        if (result.Status != AdaptiveMediaSourceCreationStatus.Success || result.MediaSource == null)
                            throw new InvalidOperationException("直播播放源未能打开（" + result.Status + "）。");
                        adaptive = result.MediaSource;
                        // Keep full, actual server quality. The adaptive HLS
                        // pipeline uses its segment buffer and native decoder.
                        var player = new MediaPlayer { AutoPlay = false };
                        player.MediaOpened += (s, e) => Queue(roomGeneration, () => { if (ReferenceEquals(player, _player)) { SetBuffering(false); if (_wantsPlay) player.Play(); } });
                        player.MediaFailed += (s, e) => Queue(roomGeneration, () => { if (ReferenceEquals(player, _player)) { SetBuffering(false); _watchNotice.Text = "直播播放失败，可打开画质面板重新加载。"; } });
                        player.PlaybackSession.PlaybackStateChanged += (s, e) => Queue(roomGeneration, () =>
                        {
                            if (!ReferenceEquals(player, _player)) return;
                            var state = player.PlaybackSession.PlaybackState;
                            AutomationProperties.SetHelpText(_surface, state.ToString());
                            AutomationProperties.SetHelpText(_play, state.ToString());
                            SetBuffering(state == MediaPlaybackState.Opening || state == MediaPlaybackState.Buffering);
                            _play.Content = state == MediaPlaybackState.Playing ? "暂停" : "播放";
                            if (state == MediaPlaybackState.Playing)
                                _watchNotice.Text = "直播中 · " + playback.QualityLabel + " · 左摇杆 / 方向键选择 · A 确认 · R 滚动面板 · B 隐藏控件 / 返回";
                            else if (state == MediaPlaybackState.Opening || state == MediaPlaybackState.Buffering)
                                _watchNotice.Text = "正在缓冲直播视频…";
                        });
                        var media = MediaSource.CreateFromAdaptiveMediaSource(adaptive);
                        DisposeMedia(); _adaptive = adaptive; adaptive = null; _playHttp = http; http = null; _player = player; _playback = playback;
                        _surface.SetMediaPlayer(player); player.Source = media;
                        _qualityButton.Content = string.IsNullOrWhiteSpace(playback.QualityLabel) ? "画质" : playback.QualityLabel;
                        if (_wantsPlay) player.Play();
                        _watchNotice.Text = "正在缓冲直播视频…";
                        failure = null; attached = true; break;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { failure = ex; }
                    finally { adaptive?.Dispose(); http?.Dispose(); }
                }
                if (!attached || failure != null) throw failure ?? new InvalidOperationException("平台没有返回可用的直播播放源。");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (RoomCurrent(roomGeneration) && attempt == _playGeneration) { SetBuffering(false); _watchNotice.Text = Friendly(ex); } }
            finally { if (RoomCurrent(roomGeneration) && attempt == _playGeneration) _qualityButton.IsEnabled = true; }
        }

        private async Task ConnectChatAsync(int generation)
        {
            if (!RoomCurrent(generation)) return;
            // Re-authentication can start while the previous history/config
            // requests are still pending in this same room. The connection has
            // its own generation and cancellation scope, separate from video.
            StopChatConnection();
            int chatGeneration = _chatGeneration;
            var owner = CancellationTokenSource.CreateLinkedTokenSource(_roomCts.Token); _chatCts = owner;
            long room = _room.Id;
            try
            {
                var recent = await _api.GetRecentLiveDanmakuAsync(room, owner.Token);
                if (!ChatCurrent(generation, chatGeneration)) return;
                foreach (var message in recent) AddMessage(message, false);
            }
            catch (OperationCanceledException) when (owner.IsCancellationRequested) { return; }
            catch (Exception) { /* The socket can still provide current messages. */ }
            if (!ChatCurrent(generation, chatGeneration)) return;
            try
            {
                var connection = await _api.GetLiveDanmakuConnectionAsync(room, owner.Token);
                if (!ChatCurrent(generation, chatGeneration)) return;
                var socket = new LiveDanmakuClient(); _socket = socket;
                socket.MessageReceived += (s, message) =>
                {
                    // This event runs on the socket worker. Read only the
                    // atomic generation here; room/CTS/UI ownership is checked
                    // by FlushMessages on the UI thread after admission.
                    if (Volatile.Read(ref _chatGeneration) == chatGeneration)
                        EnqueueMessage(generation, chatGeneration, socket, message);
                };
                socket.StatusChanged += (s, status) => Queue(generation, () =>
                {
                    if (!ChatCurrent(generation, chatGeneration) || !ReferenceEquals(socket, _socket)) return;
                    AutomationProperties.SetHelpText(_chatButton, status.Message);
                    if (_pane == Pane.Chat)
                        _drawerNotice.Text = status.Message + (CurrentMid > 0 ? "" : " · 登录后可发送弹幕");
                });
                await socket.ConnectAsync(connection, room, owner.Token);
                if (!ChatCurrent(generation, chatGeneration) || !ReferenceEquals(socket, _socket)) socket.Dispose();
            }
            catch (OperationCanceledException) when (owner.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (ChatCurrent(generation, chatGeneration))
                {
                    var failed = _socket; _socket = null; failed?.Stop(); failed?.Dispose();
                    string message = "实时弹幕暂未连接：" + Friendly(ex) + "。可在聊天面板重新连接。";
                    _watchNotice.Text = message; if (_pane == Pane.Chat) _drawerNotice.Text = message;
                    AutomationProperties.SetHelpText(_chatButton, message);
                }
            }
        }
        private bool ChatCurrent(int roomGeneration, int chatGeneration) => RoomCurrent(roomGeneration) &&
            chatGeneration == _chatGeneration && _chatCts != null && !_chatCts.IsCancellationRequested;
        private void StopChatConnection()
        {
            Interlocked.Increment(ref _chatGeneration); Cancel(ref _chatCts);
            var socket = _socket; _socket = null; socket?.Stop(); socket?.Dispose();
        }
        private Task ReconnectChatAsync() => IsPlayingRoom ? ConnectChatAsync(_roomGeneration) : Task.CompletedTask;
        private void Queue(int generation, Action action)
        {
            try { var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => { if (RoomCurrent(generation)) action(); }); }
            catch (Exception) { }
        }
        private void EnqueueMessage(int generation, int chatGeneration, LiveDanmakuClient socket, LiveChatMessage message)
        {
            // Popular rooms can deliver thousands of frames per minute. Never
            // enqueue one dispatcher operation per message or grow without a
            // bound; the UI timer admits a fixed batch each frame interval.
            if (Interlocked.Increment(ref _queuedMessageCount) > 384)
            { Interlocked.Decrement(ref _queuedMessageCount); return; }
            _queuedMessages.Enqueue(new QueuedLiveMessage { Generation = generation, ChatGeneration = chatGeneration, Socket = socket, Message = message });
        }
        private void FlushMessages()
        {
            QueuedLiveMessage item;
            for (int i = 0; i < 48 && _queuedMessages.TryDequeue(out item); ++i)
            {
                Interlocked.Decrement(ref _queuedMessageCount);
                if (ChatCurrent(item.Generation, item.ChatGeneration) && ReferenceEquals(item.Socket, _socket)) AddMessage(item.Message, true);
            }
        }
        private sealed class QueuedLiveMessage
        { internal int Generation, ChatGeneration; internal LiveDanmakuClient Socket; internal LiveChatMessage Message; }
        private void AddMessage(LiveChatMessage message, bool overlay)
        {
            if (message == null) return;
            if (message.Kind == LiveChatMessageKind.Popularity) { _roomMeta.Text = _room.OwnerName + " · " + _room.Area + " · " + message.Popularity + " 热度"; return; }
            if (message.Kind == LiveChatMessageKind.Preparing) { _watchNotice.Text = "主播已下播，等待重新开播。"; return; }
            if (message.Kind == LiveChatMessageKind.Live) { _watchNotice.Text = "主播已开播，可打开画质面板重新加载。"; return; }
            string text = message.Kind == LiveChatMessageKind.Gift ? message.UserName + " 赠送 " + message.GiftName + " × " + message.GiftCount
                : (string.IsNullOrWhiteSpace(message.UserName) ? "" : message.UserName + "：") + message.Text;
            if (string.IsNullOrWhiteSpace(text)) return;
            var line = Text(text, 16); line.TextWrapping = TextWrapping.Wrap; line.Margin = new Thickness(6, 7, 6, 7);
            _messages.Items.Add(line); while (_messages.Items.Count > 250) _messages.Items.RemoveAt(0);
            if (_pane == Pane.Chat) FollowLatestMessage();
            if (overlay && message.Kind == LiveChatMessageKind.Danmaku && _danmaku)
            {
                // Live has no stable media Position. Use one monotonic room
                // clock for arrival and rendering, paused while video buffers.
                if (++_overlayAdmissions > 3000) { _overlay.Load(Array.Empty<DanmakuItem>()); _overlayAdmissions = 0; }
                _overlay.Add(new DanmakuItem { Time = _liveClock.Elapsed.TotalSeconds, Text = message.Text,
                    Color = (int)(message.Color & 0xFFFFFF), SenderHash = message.UserId.ToString(), Mode = 1 });
            }
        }
        private void Tick(object sender, object args)
        {
            if (!IsPlayingRoom) return;
            bool playing = _player?.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
            if (playing) _liveClock.Start(); else _liveClock.Stop();
            _overlay.Update(_liveClock.Elapsed, playing);
            FlushMessages();
            if (_pane == Pane.None && !_editing && playing && DateTimeOffset.UtcNow - _lastActivity > TimeSpan.FromSeconds(5))
                _chrome.Visibility = Visibility.Collapsed;
        }
        private void SetBuffering(bool value)
        { _loading.IsActive = value; ((FrameworkElement)_loading.Tag).Visibility = value ? Visibility.Visible : Visibility.Collapsed; }
        private void WakeChrome() { if (!IsPlayingRoom) return; _lastActivity = DateTimeOffset.UtcNow; _chrome.Visibility = Visibility.Visible; }
        private void TogglePlayback()
        { if (_player == null) return; WakeChrome(); _wantsPlay = _player.PlaybackSession.PlaybackState != MediaPlaybackState.Playing; if (_wantsPlay) _player.Play(); else _player.Pause(); }
        private void ToggleDanmaku()
        { _danmaku = !_danmaku; _overlay.Visibility = _danmaku ? Visibility.Visible : Visibility.Collapsed; _danmakuButton.Content = _danmaku ? "弹幕 开" : "弹幕 关"; }
        private void DisposeMedia()
        {
            var player = _player; _player = null;
            if (player != null) { _surface.SetMediaPlayer(null); player.Source = null; player.Dispose(); }
            _adaptive?.Dispose(); _adaptive = null; _playHttp?.Dispose(); _playHttp = null;
        }
        private void LeaveRoom(bool focus = true)
        {
            bool wasPlaying = IsPlayingRoom; ++_roomGeneration; ++_playGeneration;
            ++_chatOperationGeneration; ++_giftOperationGeneration;
            _sending = false; _giftBusy = false; _send.IsEnabled = true;
            Cancel(ref _roomCts); Cancel(ref _playCts); StopChatConnection();
            QueuedLiveMessage queued;
            while (_queuedMessages.TryDequeue(out queued)) Interlocked.Decrement(ref _queuedMessageCount);
            _tick.Stop(); DisposeMedia(); _liveClock.Reset(); _overlay.Reset(); _overlay.Load(Array.Empty<DanmakuItem>());
            _room = null; _playback = null; _quote = null; _selectedGift = null; _pane = Pane.None; _editing = false; ResetChatScroll();
            _drawer.Visibility = Visibility.Collapsed; _watch.Visibility = Visibility.Collapsed; _browse.Visibility = Visibility.Visible;
            SetBuffering(false); if (wasPlaying) PlaybackChanged?.Invoke(this, EventArgs.Empty);
            if (focus && Visibility == Visibility.Visible) RestoreFocus();
        }
    }

    // Some CDN/WinRT connection attempts do not complete promptly after Cancel.
    // Bound the caller's wait, then own and dispose any late result separately.
    // Only the result returned before this gate closes may reach a MediaPlayer.
    internal static class LiveSourceConnectionGate
    {
        internal static async Task<T> ConnectAsync<T>(Task<T> pending, CancellationToken token,
            TimeSpan timeout, Action cancel, Action<T> dispose)
        {
            if (pending == null) throw new ArgumentNullException(nameof(pending));
            if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
            using (var delayCts = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                var delay = Task.Delay(timeout, delayCts.Token);
                if (await Task.WhenAny(pending, delay).ConfigureAwait(false) == pending)
                {
                    delayCts.Cancel();
                    T result = await pending.ConfigureAwait(false);
                    if (token.IsCancellationRequested)
                    { dispose?.Invoke(result); token.ThrowIfCancellationRequested(); }
                    return result;
                }
                try { cancel?.Invoke(); } catch (Exception) { }
                var observer = ObserveLateAsync(pending, dispose);
                token.ThrowIfCancellationRequested();
                throw new TimeoutException("连接直播视频超时，请稍后重试。");
            }
        }
        private static async Task ObserveLateAsync<T>(Task<T> pending, Action<T> dispose)
        {
            try { T result = await pending.ConfigureAwait(false); dispose?.Invoke(result); }
            catch (Exception) { /* Cancelled/faulted attempts are observed, not replayed. */ }
        }
    }
}
