using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Models;
using BiliXbox.Services;
using Newtonsoft.Json.Linq;

namespace BiliXbox.LiveDanmakuTests
{
    public static class Tests
    {
        private static int _checks;
        private static void Check(bool value, string message) { if (!value) throw new Exception(message); _checks++; }
        private static byte[] Json(string json, int operation = 5) { return LiveDanmakuProtocol.Packet(operation, Encoding.UTF8.GetBytes(json), 0); }
        private static byte[] Join(params byte[][] parts) { return parts.SelectMany(part => part).ToArray(); }
        private static byte[] Chat(string text = "中文弹幕")
        { return Json("{\"cmd\":\"DANMU_MSG:4:0:2:2:2:0\",\"info\":[[0,1,25,16711680],\"" + text + "\",[123,\"测试用户\"]]}"); }
        private static byte[] Zip(byte[] plain)
        {
            using (var stream = new MemoryStream())
            {
                stream.WriteByte(0x78); stream.WriteByte(0x9c);
                using (var deflate = new DeflateStream(stream, CompressionLevel.Optimal, true)) deflate.Write(plain, 0, plain.Length);
                uint a = 1, b = 0;
                foreach (byte item in plain) { a = (a + item) % 65521; b = (b + a) % 65521; }
                uint checksum = (b << 16) | a;
                stream.WriteByte((byte)(checksum >> 24)); stream.WriteByte((byte)(checksum >> 16));
                stream.WriteByte((byte)(checksum >> 8)); stream.WriteByte((byte)checksum);
                return LiveDanmakuProtocol.Packet(5, stream.ToArray(), 2);
            }
        }
        private static void Reject(byte[] bytes, string message)
        {
            try { LiveDanmakuProtocol.Decode(bytes); throw new Exception("Accepted: " + message); }
            catch (InvalidDataException) { _checks++; }
        }

        private static LiveDanmakuConnection Connection(long id = 100)
        {
            return new LiveDanmakuConnection { RoomId = id, UserId = 123, Token = "test-token-not-a-real-credential", Buvid = "test-platform-buvid",
                Hosts = new[] { new LiveDanmakuHost { Host = "test.chat.bilibili.com", WssPort = 443 } } };
        }

        public static async Task Run()
        {
            var auth = LiveDanmakuProtocol.Authentication(Connection(), 100);
            var body = JObject.Parse(Encoding.UTF8.GetString(auth, 16, auth.Length - 16));
            Check(auth[5] == 16 && auth[7] == 1 && auth[11] == 7 && auth[15] == 1, "Authentication big-endian header");
            Check((long)body["roomid"] == 100 && (long)body["uid"] == 123 && (int)body["protover"] == 2, "Auth uses resolved room, exact user, zlib capability");
            Check((string)body["key"] == Connection().Token && (string)body["buvid"] == Connection().Buvid, "Auth retains actual API credential and buvid");
            Check((string)body["platform"] == "web" && (int)body["type"] == 2, "Auth platform fields");
            Check(LiveDanmakuProtocol.Heartbeat().Length == 16 && LiveDanmakuProtocol.Heartbeat()[11] == 2, "Heartbeat packet");
            var result = LiveDanmakuProtocol.Decode(Chat());
            Check(result.Messages.Count == 1 && result.Messages[0].Kind == LiveChatMessageKind.Danmaku, "DANMU_MSG suffix recognized");
            Check(result.Messages[0].Text == "中文弹幕" && result.Messages[0].UserName == "测试用户" && result.Messages[0].UserId == 123, "Unicode, UID, nickname preserved");
            Check(result.Messages[0].Color == 0xff0000 && result.Messages[0].Timestamp > DateTimeOffset.UtcNow.AddSeconds(-10), "Danmaku color and timestamp");
            byte[] ack = Json("{\"code\":0}", 8);
            byte[] heartbeat = LiveDanmakuProtocol.Packet(3, new byte[] { 0xff, 0xff, 0xff, 0xfe });
            result = LiveDanmakuProtocol.Decode(Join(ack, heartbeat, Chat()));
            Check(result.AuthenticationCode == 0 && result.HeartbeatReply && result.Messages.Count == 2, "Concatenated packets and auth");
            Check(result.Messages[0].Popularity == 4294967294L, "Popularity is unsigned network integer");
            result = LiveDanmakuProtocol.Decode(Zip(Join(Chat(), Json("{\"cmd\":\"PREPARING\"}"), Json("{\"cmd\":\"LIVE\"}"))));
            Check(result.Messages.Count == 3 && result.Messages[1].Kind == LiveChatMessageKind.Preparing && result.Messages[2].Kind == LiveChatMessageKind.Live, "zlib envelope decodes all nested packets");
            result = LiveDanmakuProtocol.Decode(Zip(Zip(Chat())));
            Check(result.Messages.Count == 1 && result.Messages[0].Text == "中文弹幕", "Two compressed nesting levels allowed");
            result = LiveDanmakuProtocol.Decode(Join(Json("{\"cmd\":\"SEND_GIFT\",\"data\":{\"uid\":44,\"uname\":\"送礼用户\",\"giftName\":\"小花花\",\"num\":2,\"action\":\"投喂\"}}"),
                Json("{\"cmd\":\"SUPER_CHAT_MESSAGE\",\"data\":{\"uid\":45,\"user_info\":{\"uname\":\"留言用户\"},\"message\":\"醒目留言\"}}")));
            Check(result.Messages.Count == 2 && result.Messages[0].Kind == LiveChatMessageKind.Gift && result.Messages[0].GiftName == "小花花" && result.Messages[0].GiftCount == 2, "Gift event parses exact name/count");
            Check(result.Messages[1].Kind == LiveChatMessageKind.SuperChat && result.Messages[1].UserName == "留言用户" && result.Messages[1].Text == "醒目留言", "Super Chat event");
            Check(LiveDanmakuProtocol.Decode(Json("{\"cmd\":\"UNKNOWN\"}")).Messages.Count == 0, "Unknown command ignored");
            Check(LiveDanmakuProtocol.Decode(Json("{\"cmd\":\"DANMU_MSG\",\"info\":[]}")).Messages.Count == 0, "Short optional danmaku ignored");
            Check(LiveDanmakuProtocol.Decode(Json("{\"cmd\":\"DANMU_MSG\",\"info\":[[],\"ok\",[]]}")).Messages[0].Color == 0xffffff, "Missing optional color defaults white");
            Check(LiveDanmakuProtocol.Decode(Json("{\"code\":-101}", 8)).AuthenticationCode == -101, "Negative auth never converted to zero");
            string emoji = char.ConvertFromUtf32(0x1f3ae);
            result = LiveDanmakuProtocol.Decode(Chat(new string('x', 511) + emoji));
            Check(result.Messages[0].Text.Length == 511 && !char.IsHighSurrogate(result.Messages[0].Text.Last()), "Unicode truncation preserves surrogate pair");
            result = LiveDanmakuProtocol.Decode(Join(Enumerable.Range(0, 600).Select(i => Chat("flood")).ToArray()));
            Check(result.Messages.Count == 256, "One frame dispatch count bounded");
            foreach (byte[] malformed in new[] { new byte[0], new byte[15], new byte[LiveDanmakuProtocol.MaxFrameBytes + 1], Join(Chat(), new byte[1]) }) Reject(malformed, "frame length");
            var invalid = Chat(); invalid[5] = 15; Reject(invalid, "short header");
            invalid = Chat(); invalid[0] = 0x7f; Reject(invalid, "declared packet exceeds buffer");
            invalid = Chat(); invalid[5] = 255; Reject(invalid, "header exceeds packet");
            Reject(LiveDanmakuProtocol.Packet(5, new byte[8], 3), "unnegotiated Brotli");
            Reject(Json("{}", 8), "missing auth code");
            Reject(Json("{\"code\":false}", 8), "boolean auth code");
            Reject(Json("{\"code\":0.0}", 8), "fractional auth code");
            Reject(Join(ack, Json("{\"code\":-101}", 8)), "conflicting auth replies");
            Reject(LiveDanmakuProtocol.Packet(3, new byte[3]), "short heartbeat reply");
            Reject(Json("{broken json"), "invalid JSON");
            Reject(Json("{}{}"), "extra JSON value");
            Reject(LiveDanmakuProtocol.Packet(5, new byte[] { 0xff }), "invalid UTF-8");
            Reject(Zip(Zip(Zip(Chat()))), "nested compression depth");
            invalid = Zip(Chat()); invalid[invalid.Length - 1] ^= 1; Reject(invalid, "zlib checksum");
            invalid = Zip(Chat()); invalid[16] = 0; Reject(invalid, "zlib header");
            Reject(LiveDanmakuProtocol.Packet(8, new byte[] { 0x78, 0x9c, 0, 0, 0, 0 }, 2), "compressed auth");
            Reject(Json("{\"cmd\":\"IGNORE\",\"x\":\"" + new string('x', 256 * 1024) + "\"}"), "JSON input cap");
            Reject(Zip(new byte[LiveDanmakuProtocol.MaxExpandedBytes + 1]), "decompression bomb");
            Reject(Join(Enumerable.Range(0, 2049).Select(i => LiveDanmakuProtocol.Packet(99, new byte[0])).ToArray()), "packet count cap");

            await ClientLifecycle();
            Console.WriteLine("PASS: " + _checks + " live protocol, authentication, heartbeat, retry, room ownership, and cleanup assertions. Offline production-code tests; no chat or gift was sent.");
        }

        private static async Task ClientLifecycle()
        {
            var socket = new FakeSocket();
            var delays = new FakeDelay();
            var received = new List<LiveChatMessage>();
            var states = new List<LiveDanmakuStatus>();
            using (var client = new LiveDanmakuClient(() => socket, delays.Delay, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(2)))
            using (var cancel = new CancellationTokenSource())
            {
                client.MessageReceived += (sender, message) => { lock (received) received.Add(message); };
                client.StatusChanged += (sender, status) => { lock (states) states.Add(status.Status); };
                Task connect = client.ConnectAsync(Connection(), 100, cancel.Token);
                await Until(() => socket.Sent.Count > 0);
                Check(!connect.IsCompleted, "TCP connection alone cannot announce connected");
                socket.Emit(Chat("before auth"));
                Check(received.Count == 0, "Ignore messages before successful auth");
                socket.Emit(Json("{\"code\":0}", 8));
                await connect;
                await Until(() => socket.Sent.Count == 2 && delays.PendingCount > 0);
                Check(socket.Sent[0][11] == 7 && socket.Sent[1][11] == 2, "First outbound packet auth; next immediate heartbeat");
                Check(socket.Uri.Scheme == "wss" && socket.Uri.AbsolutePath == "/sub", "Connect uses secure websocket subpath");
                Check(states.Contains(LiveDanmakuStatus.Connected), "Auth success emits connected status");
                socket.Emit(Chat("after auth"));
                Check(received.Count == 1 && received[0].Text == "after auth", "Current authenticated room event delivered");
                delays.ReleaseHeartbeat();
                await Until(() => socket.Sent.Count == 3);
                Check(socket.Sent[2][11] == 2, "30 second tick sends next heartbeat");
                cancel.Cancel();
                await Until(() => socket.IsDisposed && delays.Canceled > 0);
                int count = received.Count;
                socket.Emit(Chat("late"));
                Check(received.Count == count, "Canceled session drops late messages");
                Check(socket.IsClosed && socket.IsDisposed && delays.Canceled > 0, "Cancellation closes socket and cancels heartbeat wait");
            }

            socket = new FakeSocket(); delays = new FakeDelay();
            using (var client = new LiveDanmakuClient(() => socket, delays.Delay, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(2)))
            {
                Task connect = client.ConnectAsync(Connection(), 100, CancellationToken.None);
                await Until(() => socket.Sent.Count > 0);
                socket.Emit(Json("{\"code\":-101}", 8));
                try { await connect; throw new Exception("Negative auth succeeded"); }
                catch (InvalidOperationException error) { Check(!error.Message.Contains("test-token"), "Auth errors are sanitized"); }
                await Until(() => socket.IsDisposed);
                Check(delays.Retries == 0 && socket.Sent.Count == 1, "Authentication rejection does not retry token or start heartbeats");
            }

            var failedSockets = new List<FakeSocket>(); delays = new FakeDelay();
            using (var client = new LiveDanmakuClient(() => { var next = new FakeSocket { FailConnect = true }; lock (failedSockets) failedSockets.Add(next); return next; },
                delays.Delay, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(2)))
            {
                try { await client.ConnectAsync(Connection(), 100, CancellationToken.None); throw new Exception("Failed socket succeeded"); }
                catch (InvalidOperationException) { _checks++; }
                Check(failedSockets.Count == 6 && delays.Retries == 5 && failedSockets.All(item => item.IsDisposed), "Network failures have six-attempt bound and dispose every socket");
            }

            var first = new FakeSocket { AutoAuth = true };
            var second = new FakeSocket { AutoAuth = true };
            int created = 0; delays = new FakeDelay();
            using (var client = new LiveDanmakuClient(() => Interlocked.Increment(ref created) == 1 ? first : second, delays.Delay, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(2)))
            {
                int messages = 0;
                client.MessageReceived += (sender, message) => messages++;
                await client.ConnectAsync(Connection(), 100, CancellationToken.None);
                await Until(() => first.Sent.Count >= 2);
                first.RemoteClose();
                await Until(() => created == 2 && second.Sent.Count >= 2);
                first.Emit(Chat("old connection")); second.Emit(Chat("current connection"));
                Check(messages == 1 && first.IsDisposed, "Reconnect rejects old transport messages");
                client.Stop();
                second.Emit(Chat("after stop"));
                Check(messages == 1 && second.IsDisposed, "Stop synchronously closes current socket and rejects queued events");
            }

            first = new FakeSocket { AutoAuth = true }; second = new FakeSocket { AutoAuth = true }; created = 0; delays = new FakeDelay();
            using (var client = new LiveDanmakuClient(() => Interlocked.Increment(ref created) == 1 ? first : second, delays.Delay, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(2)))
            {
                int messages = 0;
                client.MessageReceived += (sender, message) => messages++;
                await client.ConnectAsync(Connection(), 100, CancellationToken.None);
                await client.ConnectAsync(Connection(200), 200, CancellationToken.None);
                first.Emit(Chat("old room")); second.Emit(Chat("new room"));
                Check(first.IsDisposed && messages == 1, "Replacing room immediately retires prior generation");
                client.Dispose();
                try { _ = client.ConnectAsync(Connection(), 100, CancellationToken.None); throw new Exception("Disposed client accepted connection"); }
                catch (ObjectDisposedException) { _checks++; }
            }
            using (var client = new LiveDanmakuClient())
            {
                foreach (string host in new[] { "evil.example", "evil.chat.bilibili.com.evil.example", "x.chat.bilibili.com@evil.example", "x.chat.bilibili.com/path", "127.0.0.1" })
                {
                    var connection = Connection(); connection.Hosts = new[] { new LiveDanmakuHost { Host = host } };
                    try { _ = client.ConnectAsync(connection, 100, CancellationToken.None); throw new Exception("Accepted malicious websocket host"); }
                    catch (ArgumentException) { _checks++; }
                }
                try { _ = client.ConnectAsync(Connection(), 101, CancellationToken.None); throw new Exception("Accepted room mismatch"); }
                catch (ArgumentException) { _checks++; }
            }
        }

        private static async Task Until(Func<bool> condition)
        {
            var stop = DateTime.UtcNow.AddSeconds(3);
            while (!condition()) { if (DateTime.UtcNow > stop) throw new Exception("Timed out waiting for offline worker"); await Task.Delay(5); }
        }

        private sealed class FakeDelay
        {
            private readonly List<TaskCompletionSource<bool>> _pending = new List<TaskCompletionSource<bool>>();
            internal int Retries, Canceled;
            internal int PendingCount { get { lock (_pending) return _pending.Count; } }
            internal Task Delay(TimeSpan duration, CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                if (duration.TotalSeconds < 30) { Interlocked.Increment(ref Retries); return Task.CompletedTask; }
                var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                ct.Register(() => { if (ready.TrySetCanceled()) Interlocked.Increment(ref Canceled); });
                lock (_pending) _pending.Add(ready);
                return ready.Task;
            }
            internal void ReleaseHeartbeat() { lock (_pending) { foreach (var item in _pending) if (item.TrySetResult(true)) return; } }
        }

        private sealed class FakeSocket : ILiveDanmakuSocket
        {
            internal readonly List<byte[]> Sent = new List<byte[]>();
            internal bool IsDisposed, IsClosed, FailConnect, AutoAuth;
            internal Uri Uri;
            public event Action<byte[]> Message;
            public event Action Closed;
            public Task ConnectAsync(Uri uri, CancellationToken ct)
            { ct.ThrowIfCancellationRequested(); Uri = uri; if (FailConnect) throw new IOException("simulated"); return Task.CompletedTask; }
            public Task SendAsync(byte[] bytes, CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                lock (Sent) Sent.Add((byte[])bytes.Clone());
                if (AutoAuth && bytes[11] == 7) Emit(Json("{\"code\":0}", 8));
                return Task.CompletedTask;
            }
            internal void Emit(byte[] bytes) { Message?.Invoke(bytes); }
            internal void RemoteClose() { Closed?.Invoke(); }
            public void Close() { IsClosed = true; Closed?.Invoke(); }
            public void Dispose() { IsDisposed = true; }
        }
    }
}
