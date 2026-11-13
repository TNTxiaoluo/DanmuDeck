using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Models;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace BiliXbox.Services
{
    public enum LiveDanmakuStatus { Connecting, Connected, Reconnecting, Disconnected }

    public sealed class LiveDanmakuStatusEventArgs : EventArgs
    {
        public LiveDanmakuStatus Status { get; internal set; }
        public string Message { get; internal set; }
        public long RoomId { get; internal set; }
    }

    internal interface ILiveDanmakuSocket : IDisposable
    {
        event Action<byte[]> Message;
        event Action Closed;
        Task ConnectAsync(Uri uri, CancellationToken ct);
        Task SendAsync(byte[] bytes, CancellationToken ct);
        void Close();
    }

    /// <summary>
    /// A room-scoped receive-only live message connection. SendAsync on the
    /// transport is used only for authentication and heartbeat packets; posting
    /// chat and gifts belongs to the separately authenticated HTTP API.
    /// Callbacks run on a background thread. Consumers should dispatch to the UI
    /// and retain their own room generation when queuing UI work.
    /// </summary>
    public sealed class LiveDanmakuClient : IDisposable
    {
        private readonly object _sync = new object();
        private readonly Func<ILiveDanmakuSocket> _factory;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;
        private readonly TimeSpan _heartbeatInterval;
        private readonly TimeSpan _timeout;
        private Session _session;
        private bool _disposed;

        public event EventHandler<LiveChatMessage> MessageReceived;
        public event EventHandler<LiveDanmakuStatusEventArgs> StatusChanged;

        public LiveDanmakuClient() : this(() => new UwpLiveDanmakuSocket(),
            (duration, ct) => Task.Delay(duration, ct), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(15)) { }

        internal LiveDanmakuClient(Func<ILiveDanmakuSocket> factory,
            Func<TimeSpan, CancellationToken, Task> delay, TimeSpan heartbeatInterval, TimeSpan timeout)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _delay = delay ?? throw new ArgumentNullException(nameof(delay));
            _heartbeatInterval = heartbeatInterval;
            _timeout = timeout;
        }

        public Task ConnectAsync(LiveDanmakuConnection connection, long roomId, CancellationToken ct)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (roomId <= 0 || connection.RoomId != roomId) throw new ArgumentException("直播弹幕房间号不匹配。");
            if (connection.UserId < 0 || string.IsNullOrWhiteSpace(connection.Token) || connection.Token.Length > 8192 ||
                (connection.Buvid?.Length ?? 0) > 512) throw new ArgumentException("直播弹幕连接凭证无效。");
            var hosts = (connection.Hosts ?? new LiveDanmakuHost[0]).Select(HostUri).Where(uri => uri != null).Distinct().Take(8).ToArray();
            if (hosts.Length == 0) throw new ArgumentException("没有可用的直播弹幕服务器。");
            ct.ThrowIfCancellationRequested();
            // Snapshot credentials and hosts so a mutable API result cannot change
            // the identity of an already-running/reconnecting room session.
            var auth = LiveDanmakuProtocol.Authentication(connection, roomId);
            var next = new Session(roomId, auth, hosts, ct);
            Session previous;
            lock (_sync)
            {
                if (_disposed) { next.Cancel(); next.Cancellation.Dispose(); throw new ObjectDisposedException(nameof(LiveDanmakuClient)); }
                previous = _session;
                _session = next;
            }
            previous?.Cancel();
            next.Loop = Task.Run(() => RunAsync(next));
            return next.Ready.Task;
        }

        public void Stop()
        {
            Session previous;
            lock (_sync) { previous = _session; _session = null; }
            previous?.Cancel();
        }

        public void Dispose()
        {
            lock (_sync) { if (_disposed) return; _disposed = true; }
            Stop();
        }

        private async Task RunAsync(Session session)
        {
            var ct = session.Cancellation.Token;
            using (ct.Register(session.CloseAttempt))
            {
                try
                {
                    // A bounded retry budget avoids background reconnect storms.
                    // A new user-requested ConnectAsync starts a fresh budget.
                    for (int retry = 0; retry < 6; retry++)
                    {
                        ct.ThrowIfCancellationRequested();
                        Status(session, retry == 0 ? LiveDanmakuStatus.Connecting : LiveDanmakuStatus.Reconnecting,
                            retry == 0 ? "正在连接直播弹幕…" : "直播弹幕连接中断，正在重连…");
                        if (retry > 0) await _delay(TimeSpan.FromSeconds(Math.Min(15, retry * 3)), ct).ConfigureAwait(false);
                        Attempt attempt = null;
                        try
                        {
                            attempt = new Attempt(_factory());
                            session.Attempt = attempt;
                            var ownedAttempt = attempt;
                            attempt.Socket.Message += bytes => Receive(session, ownedAttempt, bytes);
                            attempt.Socket.Closed += attempt.OnClosed;
                            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
                            {
                                connect.CancelAfter(_timeout);
                                await attempt.Socket.ConnectAsync(session.Hosts[retry % session.Hosts.Length], connect.Token).ConfigureAwait(false);
                                await attempt.Socket.SendAsync(session.Auth, connect.Token).ConfigureAwait(false);
                                await WaitAsync(attempt.Auth.Task, connect.Token).ConfigureAwait(false);
                            }
                            ct.ThrowIfCancellationRequested();
                            if (!Current(session, attempt)) throw new OperationCanceledException(ct);
                            Status(session, LiveDanmakuStatus.Connected, "直播弹幕已连接");
                            session.Ready.TrySetResult(true);
                            await HeartbeatLoopAsync(session, attempt, ct).ConfigureAwait(false);
                        }
                        catch (AuthenticationException)
                        {
                            Status(session, LiveDanmakuStatus.Disconnected, "直播弹幕认证失败，请重新进入直播间。");
                            session.Ready.TrySetException(new InvalidOperationException("直播弹幕认证失败，请重新进入直播间。"));
                            return;
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (Exception)
                        {
                            // Never expose connection tokens, endpoint query data,
                            // arbitrary remote errors, or credentials in UI errors.
                        }
                        finally
                        {
                            attempt?.Close();
                            if (ReferenceEquals(session.Attempt, attempt)) session.Attempt = null;
                        }
                    }
                    Status(session, LiveDanmakuStatus.Disconnected, "直播弹幕暂时无法连接，请重新进入直播间重试。");
                    session.Ready.TrySetException(new InvalidOperationException("直播弹幕暂时无法连接。"));
                }
                catch (OperationCanceledException) { session.Ready.TrySetCanceled(); }
                catch (Exception)
                {
                    Status(session, LiveDanmakuStatus.Disconnected, "直播弹幕连接已停止。");
                    session.Ready.TrySetException(new InvalidOperationException("直播弹幕连接已停止。"));
                }
                finally
                {
                    session.CloseAttempt();
                    lock (_sync) { session.Ended = true; }
                    Array.Clear(session.Auth, 0, session.Auth.Length);
                    session.Cancellation.Dispose();
                }
            }
        }

        private async Task HeartbeatLoopAsync(Session session, Attempt attempt, CancellationToken ct)
        {
            while (Current(session, attempt) && !attempt.Disconnected.Task.IsCompleted)
            {
                // The first heartbeat is sent immediately after authentication.
                await attempt.Socket.SendAsync(LiveDanmakuProtocol.Heartbeat(), ct).ConfigureAwait(false);
                using (var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    var wait = _delay(_heartbeatInterval, heartbeat.Token);
                    var finished = await Task.WhenAny(wait, attempt.Disconnected.Task).ConfigureAwait(false);
                    if (finished == attempt.Disconnected.Task) { heartbeat.Cancel(); return; }
                    await wait.ConfigureAwait(false);
                }
                if ((Stopwatch.GetTimestamp() - Interlocked.Read(ref attempt.LastReceivedStamp)) / (double)Stopwatch.Frequency > 75)
                    throw new IOException("直播弹幕心跳超时。");
            }
        }

        private void Receive(Session session, Attempt attempt, byte[] bytes)
        {
            lock (attempt.ReceiveSync)
            {
                if (!Current(session, attempt)) return;
                try
                {
                    var result = LiveDanmakuProtocol.Decode(bytes);
                    Interlocked.Exchange(ref attempt.LastReceivedStamp, Stopwatch.GetTimestamp());
                    if (result.AuthenticationCode.HasValue)
                    {
                        if (result.AuthenticationCode.Value != 0)
                        {
                            attempt.Auth.TrySetException(new AuthenticationException());
                            attempt.OnClosed();
                            return;
                        }
                        attempt.Authenticated = true;
                        attempt.Auth.TrySetResult(true);
                    }
                    if (!attempt.Authenticated) return;
                    foreach (var message in result.Messages)
                    {
                        lock (_sync)
                        {
                            if (!CurrentLocked(session, attempt)) return;
                            try { MessageReceived?.Invoke(this, message); } catch (Exception) { }
                        }
                    }
                }
                catch (Exception)
                {
                    attempt.Auth.TrySetException(new InvalidDataException("直播弹幕数据无效。"));
                    attempt.OnClosed();
                }
            }
        }

        private void Status(Session session, LiveDanmakuStatus status, string message)
        {
            lock (_sync)
            {
                if (!ReferenceEquals(_session, session) || session.Ended || session.Cancellation.IsCancellationRequested) return;
                try { StatusChanged?.Invoke(this, new LiveDanmakuStatusEventArgs { RoomId = session.RoomId, Status = status, Message = message }); }
                catch (Exception) { }
            }
        }

        private bool Current(Session session, Attempt attempt) { lock (_sync) { return CurrentLocked(session, attempt); } }
        private bool CurrentLocked(Session session, Attempt attempt)
        { return !_disposed && ReferenceEquals(_session, session) && !session.Ended && !session.Cancellation.IsCancellationRequested && ReferenceEquals(session.Attempt, attempt) && !attempt.Disconnected.Task.IsCompleted; }

        private static async Task WaitAsync(Task task, CancellationToken ct)
        {
            var canceled = NewCompletion();
            using (ct.Register(() => canceled.TrySetCanceled()))
                await (await Task.WhenAny(task, canceled.Task).ConfigureAwait(false)).ConfigureAwait(false);
        }

        private static Uri HostUri(LiveDanmakuHost host)
        {
            if (host == null || string.IsNullOrWhiteSpace(host.Host) || host.WssPort < 1 || host.WssPort > 65535) return null;
            string name = host.Host.Trim();
            if (name.IndexOfAny(new[] { '/', '\\', ':', '@', '?', '#', ' ' }) >= 0 ||
                !name.EndsWith(".chat.bilibili.com", StringComparison.OrdinalIgnoreCase) || Uri.CheckHostName(name) != UriHostNameType.Dns) return null;
            return new UriBuilder("wss", name, host.WssPort, "sub").Uri;
        }

        private static TaskCompletionSource<bool> NewCompletion()
        { return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); }
        private sealed class AuthenticationException : Exception { }

        private sealed class Session
        {
            internal readonly long RoomId;
            internal readonly byte[] Auth;
            internal readonly Uri[] Hosts;
            internal readonly CancellationTokenSource Cancellation;
            internal readonly TaskCompletionSource<bool> Ready = NewCompletion();
            internal volatile Attempt Attempt;
            internal bool Ended;
            internal Task Loop;
            internal Session(long room, byte[] auth, Uri[] hosts, CancellationToken ct)
            { RoomId = room; Auth = auth; Hosts = hosts; Cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct); }
            internal void CloseAttempt() { Attempt?.Close(); }
            internal void Cancel()
            {
                try { Cancellation.Cancel(); } catch (ObjectDisposedException) { }
                CloseAttempt();
                Ready.TrySetCanceled();
            }
        }

        private sealed class Attempt
        {
            internal readonly object ReceiveSync = new object();
            internal readonly ILiveDanmakuSocket Socket;
            internal readonly TaskCompletionSource<bool> Auth = NewCompletion();
            internal readonly TaskCompletionSource<bool> Disconnected = NewCompletion();
            internal long LastReceivedStamp = Stopwatch.GetTimestamp();
            internal bool Authenticated;
            private int _closed;
            internal Attempt(ILiveDanmakuSocket socket) { Socket = socket; }
            internal void OnClosed()
            {
                Auth.TrySetException(new IOException("直播弹幕连接中断。"));
                Disconnected.TrySetResult(true);
            }
            internal void Close()
            {
                if (Interlocked.Exchange(ref _closed, 1) != 0) return;
                OnClosed();
                try { Socket.Close(); } catch (Exception) { }
                try { Socket.Dispose(); } catch (Exception) { }
            }
        }
    }

    internal sealed class UwpLiveDanmakuSocket : ILiveDanmakuSocket
    {
        private readonly MessageWebSocket _socket = new MessageWebSocket();
        private readonly SemaphoreSlim _write = new SemaphoreSlim(1, 1);
        private int _closed;
        public event Action<byte[]> Message;
        public event Action Closed;

        internal UwpLiveDanmakuSocket()
        {
            _socket.Control.MessageType = SocketMessageType.Binary;
            _socket.Control.MaxMessageSize = LiveDanmakuProtocol.MaxFrameBytes;
            _socket.SetRequestHeader("Origin", "https://live.bilibili.com");
            _socket.SetRequestHeader("User-Agent", BilibiliClient.UserAgent);
            _socket.MessageReceived += OnMessage;
            _socket.Closed += OnClosed;
        }

        public async Task ConnectAsync(Uri uri, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); await _socket.ConnectAsync(uri).AsTask(ct).ConfigureAwait(false); }

        public async Task SendAsync(byte[] bytes, CancellationToken ct)
        {
            await _write.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();
                if (Volatile.Read(ref _closed) != 0) throw new IOException("直播弹幕连接已关闭。");
                using (var writer = new DataWriter(_socket.OutputStream))
                {
                    try { writer.WriteBytes(bytes); await writer.StoreAsync().AsTask(ct).ConfigureAwait(false); }
                    finally { writer.DetachStream(); }
                }
            }
            finally { _write.Release(); }
        }

        private void OnMessage(MessageWebSocket sender, MessageWebSocketMessageReceivedEventArgs args)
        {
            try
            {
                if (Volatile.Read(ref _closed) != 0) return;
                if (args.MessageType != SocketMessageType.Binary) { Close(); return; }
                using (var reader = args.GetDataReader())
                {
                    uint count = reader.UnconsumedBufferLength;
                    if (count < 16 || count > LiveDanmakuProtocol.MaxFrameBytes) { Close(); return; }
                    var bytes = new byte[count];
                    reader.ReadBytes(bytes);
                    Message?.Invoke(bytes);
                }
            }
            catch (Exception) { Close(); }
        }

        private void OnClosed(IWebSocket sender, WebSocketClosedEventArgs args) { Close(); }
        public void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            _socket.MessageReceived -= OnMessage;
            _socket.Closed -= OnClosed;
            try { _socket.Close(1000, "Leaving room"); } catch (Exception) { }
            try { _socket.Dispose(); } catch (Exception) { }
            try { Closed?.Invoke(); } catch (Exception) { }
        }
        public void Dispose() { Close(); }
    }
}
