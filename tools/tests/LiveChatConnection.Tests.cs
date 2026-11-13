namespace BiliXbox.Services
{
    public sealed class LiveDanmakuStatusEventArgs : EventArgs { public string Message; }
    public sealed class LiveDanmakuClient : IDisposable
    {
        internal static readonly List<LiveDanmakuClient> Created = new List<LiveDanmakuClient>();
        internal static Func<LiveDanmakuClient, Task> Connect = socket => Task.CompletedTask;
        internal int StopCount, DisposeCount;
        internal long UserId;
        internal CancellationToken Token;
        public event EventHandler<LiveChatMessage> MessageReceived;
        public event EventHandler<LiveDanmakuStatusEventArgs> StatusChanged;
        public LiveDanmakuClient() { Created.Add(this); }
        public Task ConnectAsync(LiveDanmakuConnection connection, long room, CancellationToken token)
        { UserId = connection.UserId; Token = token; return Connect(this); }
        public void Stop() { ++StopCount; }
        public void Dispose() { ++DisposeCount; }
        internal void Message(string text) => MessageReceived?.Invoke(this, new LiveChatMessage { Text = text });
        internal void Status(string text) => StatusChanged?.Invoke(this, new LiveDanmakuStatusEventArgs { Message = text });
        internal static void Reset() { Created.Clear(); Connect = socket => Task.CompletedTask; }
    }
}
namespace Windows.UI.Xaml.Automation
{
    public static class AutomationProperties
    {
        public static void SetHelpText(BiliXbox.Controls.LiveTestLabel label, string text) { label.HelpText = text; }
    }
}
namespace BiliXbox.Controls
{
    public sealed class LiveTestLabel { internal string Text = "", HelpText = ""; }
    public sealed partial class LiveHubView
    {
        private enum Pane { None, Chat }
        private sealed class FakeApi
        {
            internal int HistoryCalls, ConfigCalls;
            internal CancellationToken FirstHistoryToken;
            internal Func<Task<IReadOnlyList<LiveChatMessage>>> History = () => Task.FromResult<IReadOnlyList<LiveChatMessage>>(new LiveChatMessage[0]);
            internal Func<Task<LiveDanmakuConnection>> Config = () => Task.FromResult(new LiveDanmakuConnection { RoomId = 42, UserId = 7 });
            internal Task<IReadOnlyList<LiveChatMessage>> GetRecentLiveDanmakuAsync(long room, CancellationToken token)
            { if (++HistoryCalls == 1) FirstHistoryToken = token; return History(); }
            internal Task<LiveDanmakuConnection> GetLiveDanmakuConnectionAsync(long room, CancellationToken token)
            { ++ConfigCalls; return Config(); }
        }
        private readonly FakeApi _api = new FakeApi();
        private readonly LiveTestLabel _watchNotice = new LiveTestLabel(), _drawerNotice = new LiveTestLabel(), _chatButton = new LiveTestLabel();
        private CancellationTokenSource _roomCts = new CancellationTokenSource(), _chatCts;
        private int _roomGeneration = 1, _chatGeneration, _messageCount, _historyCount;
        private LiveRoom _room = new LiveRoom { Id = 42 };
        private LiveDanmakuClient _socket;
        private Pane _pane = Pane.Chat;
        private long CurrentMid = 7;
        private bool IsPlayingRoom => _room != null;
        private bool RoomCurrent(int generation) => IsPlayingRoom && generation == _roomGeneration && !_roomCts.IsCancellationRequested;
        private void AddMessage(LiveChatMessage message, bool overlay) { ++_historyCount; }
        private void Queue(int generation, Action action) { if (RoomCurrent(generation)) action(); }
        private void EnqueueMessage(int roomGeneration, int chatGeneration, LiveDanmakuClient socket, LiveChatMessage message)
        { if (ChatCurrent(roomGeneration, chatGeneration) && ReferenceEquals(socket, _socket)) ++_messageCount; }
        private static string Friendly(Exception exception) => exception.Message;
        private static void Cancel(ref CancellationTokenSource source)
        { var old = source; source = null; if (old != null) { old.Cancel(); old.Dispose(); } }
        internal static async Task<int> RunProductionChecks()
        {
            int count = 0;
            Action<bool, string> check = (value, name) => { ++count; if (!value) throw new Exception(name); };
            LiveDanmakuClient.Reset(); var view = new LiveHubView();
            var history = new TaskCompletionSource<IReadOnlyList<LiveChatMessage>>();
            view._api.History = () => view._api.HistoryCalls == 1 ? history.Task : Task.FromResult<IReadOnlyList<LiveChatMessage>>(new LiveChatMessage[0]);
            var old = view.ConnectChatAsync(1); check(view._api.HistoryCalls == 1 && LiveDanmakuClient.Created.Count == 0, "First connection awaits real history path");
            view._api.Config = () => Task.FromResult(new LiveDanmakuConnection { RoomId = 42, UserId = 8 });
            await view.ConnectChatAsync(1); var latest = view._socket;
            check(view._api.FirstHistoryToken.IsCancellationRequested && !view._roomCts.IsCancellationRequested, "New chat cancels old request without cancelling video room");
            check(latest != null && latest.UserId == 8 && LiveDanmakuClient.Created.Count == 1, "Latest account owns one connected socket");
            history.SetResult(new[] { new LiveChatMessage { Text = "stale history" } }); await old;
            check(ReferenceEquals(latest, view._socket) && view._api.ConfigCalls == 1 && view._historyCount == 0, "Late old history cannot request config, append old history or overwrite socket");
            latest.Message("current"); latest.Status("connected");
            check(view._messageCount == 1 && view._chatButton.HelpText == "connected", "Current attempt publishes chat and status");
            LiveDanmakuClient.Reset(); view = new LiveHubView();
            var config = new TaskCompletionSource<LiveDanmakuConnection>();
            view._api.Config = () => view._api.ConfigCalls == 1 ? config.Task : Task.FromResult(new LiveDanmakuConnection { RoomId = 42, UserId = 8 });
            old = view.ConnectChatAsync(1); await view.ConnectChatAsync(1); latest = view._socket;
            config.SetResult(new LiveDanmakuConnection { RoomId = 42, UserId = 7 }); await old;
            check(ReferenceEquals(latest, view._socket) && LiveDanmakuClient.Created.Count == 1 && latest.UserId == 8, "Late old config cannot create or leak a replacement socket");
            LiveDanmakuClient.Reset(); view = new LiveHubView(); var acknowledgement = new TaskCompletionSource<bool>();
            LiveDanmakuClient.Connect = socket => LiveDanmakuClient.Created.Count == 1 ? acknowledgement.Task : Task.CompletedTask;
            old = view.ConnectChatAsync(1); var oldSocket = view._socket; await view.ConnectChatAsync(1); latest = view._socket;
            check(oldSocket.StopCount >= 1 && oldSocket.DisposeCount >= 1 && oldSocket.Token.IsCancellationRequested, "Reauthentication stops/disposes old socket and cancels its heartbeat token");
            oldSocket.Message("old frame"); oldSocket.Status("old disconnected");
            check(view._messageCount == 0 && view._chatButton.HelpText == "", "Old attempt message/status callbacks cannot update new connection");
            latest.Message("new frame"); latest.Status("new connected"); acknowledgement.SetResult(true); await old;
            check(view._messageCount == 1 && view._chatButton.HelpText == "new connected" && ReferenceEquals(latest, view._socket), "Late old auth acknowledgement cannot replace latest socket or status");
            view.StopChatConnection(); latest.Message("after stop"); latest.Status("after stop");
            check(view._socket == null && latest.StopCount >= 1 && latest.DisposeCount >= 1 && latest.Token.IsCancellationRequested, "Stop cancels and disposes current chat attempt");
            check(view._messageCount == 1 && view._chatButton.HelpText == "new connected", "Stopped attempt cannot publish late callbacks");
            LiveDanmakuClient.Reset(); view = new LiveHubView(); history = new TaskCompletionSource<IReadOnlyList<LiveChatMessage>>();
            view._api.History = () => history.Task; old = view.ConnectChatAsync(1); ++view._roomGeneration; view.StopChatConnection();
            history.SetResult(new[] { new LiveChatMessage { Text = "left room" } }); await old;
            check(view._socket == null && view._api.ConfigCalls == 0 && view._historyCount == 0, "Leaving room rejects history/config work still awaiting");
            LiveDanmakuClient.Reset(); view = new LiveHubView();
            view._api.History = () => Task.FromException<IReadOnlyList<LiveChatMessage>>(new Exception("history unavailable"));
            await view.ConnectChatAsync(1); check(view._socket != null && view._api.ConfigCalls == 1, "History endpoint failure does not prevent current realtime chat");
            LiveDanmakuClient.Reset(); view = new LiveHubView();
            view._api.History = () => Task.FromException<IReadOnlyList<LiveChatMessage>>(new TaskCanceledException("history timeout"));
            await view.ConnectChatAsync(1); check(view._socket != null && view._api.ConfigCalls == 1, "History request timeout without room cancellation still connects current socket");
            LiveDanmakuClient.Reset(); view = new LiveHubView(); history = new TaskCompletionSource<IReadOnlyList<LiveChatMessage>>();
            view._api.History = () => view._api.HistoryCalls == 1 ? history.Task : Task.FromResult<IReadOnlyList<LiveChatMessage>>(new LiveChatMessage[0]);
            old = view.ConnectChatAsync(1); await view.ConnectChatAsync(1); latest = view._socket;
            history.SetException(new Exception("old history failed")); await old;
            check(view._api.ConfigCalls == 1 && ReferenceEquals(latest, view._socket), "Stale failed history also cannot request fresh config");
            LiveDanmakuClient.Reset(); view = new LiveHubView(); LiveDanmakuClient.Connect = socket => Task.FromException(new Exception("auth failed"));
            await view.ConnectChatAsync(1);
            check(view._socket == null && LiveDanmakuClient.Created[0].DisposeCount >= 1 && view._drawerNotice.Text.Contains("auth failed"), "Latest auth failure disposes socket and offers visible reconnect feedback");
            LiveDanmakuClient.Connect = socket => Task.CompletedTask; await view.ReconnectChatAsync();
            check(view._socket != null && LiveDanmakuClient.Created.Count == 2, "Explicit reconnect uses the same attempt lifecycle");
            view._room = null; int calls = view._api.HistoryCalls; await view.ReconnectChatAsync();
            check(view._api.HistoryCalls == calls, "Reconnect at browse root performs no requests");
            return count;
        }
    }
}
namespace BiliXbox.LiveChatConnectionTests
{
    public static class Tests
    {
        public static async Task Run()
        {
            int count = await BiliXbox.Controls.LiveHubView.RunProductionChecks();
            Console.WriteLine("PASS: " + count + " production live chat connection races, cancellation, stale callbacks and explicit reconnect assertions (offline fake API/socket only).");
        }
    }
}
