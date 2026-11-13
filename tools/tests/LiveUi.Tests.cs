namespace BiliXbox.Controls
{
    public sealed partial class LiveHubView
    {
        private enum Pane { None, Chat, Gifts, GiftAmount, GiftConfirm, Quality }
        private sealed class Label { internal string Text = ""; internal bool IsEnabled = true; }
        private sealed class FakeApi
        {
            internal int ChatCalls, PrepareCalls, GiftCalls;
            internal bool Confirmed;
            internal Func<Task> Chat = () => Task.CompletedTask;
            internal Func<Task> Gift = () => Task.CompletedTask;
            internal Func<Task<LiveGiftQuote>> Prepare = () => Task.FromResult(new LiveGiftQuote());
            internal Task SendLiveDanmakuAsync(long room, string message, CancellationToken token) { ++ChatCalls; return Chat(); }
            internal Task<LiveGiftQuote> PrepareLiveGiftAsync(long room, LiveGift gift, int count, CancellationToken token) { ++PrepareCalls; return Prepare(); }
            internal Task SendLiveGiftAsync(LiveGiftQuote quote, bool confirmed, CancellationToken token) { ++GiftCalls; Confirmed = confirmed; return Gift(); }
        }
        private readonly FakeApi _api = new FakeApi();
        private readonly LiveWriteGate _writeGate = new LiveWriteGate();
        private readonly Label _chatInput = new Label(), _drawerNotice = new Label(), _send = new Label();
        private readonly object _play = new object();
        private int _roomGeneration = 1, _giftCount = 1, _clearCount, _openCount, _chatOperationGeneration, _giftOperationGeneration, _paneGeneration;
        private long _giftsMid = 7, CurrentMid = 7;
        private bool _sending, _editing, _giftBusy, IsPlayingRoom = true;
        private string _uncertainChat = "";
        private CancellationTokenSource _roomCts = new CancellationTokenSource();
        private LiveRoom _room = new LiveRoom { Id = 42 };
        private LiveGift _selectedGift = new LiveGift { Id = 3, Name = "测试礼物", Currency = "gold", UnitPrice = 100 };
        private LiveGiftQuote _quote;
        private IReadOnlyList<LiveGift> _gifts = new LiveGift[0];
        private Pane _pane = Pane.GiftAmount;
        private bool RoomCurrent(int generation) => IsPlayingRoom && generation == _roomGeneration && !_roomCts.IsCancellationRequested;
        private void EnsureChatAccount() { }
        private Task LoginAsync() => Task.CompletedTask;
        private void Focus(object input) { }
        private void ClearRows() { ++_clearCount; }
        private void RenderGiftConfirm() { }
        private void RenderGiftAmount() { }
        private void RenderGifts() { }
        private void OpenPane(Pane pane) { ++_openCount; ++_paneGeneration; _pane = pane; }
        private static string Friendly(Exception exception) => exception.Message;
        internal static async Task<int> RunProductionChecks()
        {
            int count = 0;
            Action<bool, string> check = (value, name) => { ++count; if (!value) throw new Exception(name); };
            Func<LiveHubView> ready = () => new LiveHubView { _quote = new LiveGiftQuote { AccountMid = 7, RoomId = 42, GiftId = 3, GiftName = "测试礼物", Count = 2, UnitPrice = 100, Currency = "gold" }, _pane = Pane.GiftConfirm };
            var view = ready(); await view.SendGiftAsync();
            check(view._api.GiftCalls == 1 && view._api.Confirmed, "Gift only submits once with explicit confirmed=true");
            check(view._quote == null && !view._giftBusy && !view._writeGate.IsBlocked(42, 7), "Successful gift consumes UI quote and releases busy");
            await view.SendGiftAsync(); check(view._api.GiftCalls == 1, "Consumed quote cannot repeat");
            view = ready(); view._api.Gift = () => Task.FromException(new TimeoutException("timeout"));
            await view.SendGiftAsync(); check(view._writeGate.IsUncertain(42, 7), "Timeout is retained as uncertain");
            check(view._api.GiftCalls == 1 && !view._giftBusy && view._quote == null, "Timeout does not retry and clears the old confirmation");
            view._quote = new LiveGiftQuote { AccountMid = 7, RoomId = 42 }; await view.SendGiftAsync();
            check(view._api.GiftCalls == 1, "Reopening with a fresh quote cannot bypass uncertain gate");
            view = ready(); view.CurrentMid = 8; await view.SendGiftAsync(); check(view._api.GiftCalls == 0, "Account change cannot submit old quote");
            view = ready(); view._room.Id = 43; await view.SendGiftAsync(); check(view._api.GiftCalls == 0, "Room change cannot submit old quote");
            view = ready(); var pending = new TaskCompletionSource<bool>(); view._api.Gift = () => pending.Task;
            var first = view.SendGiftAsync(); await view.SendGiftAsync(); check(view._api.GiftCalls == 1 && view._giftBusy, "Fast repeated A submits gift once");
            ++view._roomGeneration; view._drawerNotice.Text = "new room"; pending.SetResult(true); await first;
            check(view._drawerNotice.Text == "new room" && view._openCount == 0 && !view._giftBusy, "Late old gift cannot replace new room UI");
            view = ready(); pending = new TaskCompletionSource<bool>(); view._api.Gift = () => pending.Task;
            first = view.SendGiftAsync(); view.OpenPane(Pane.Chat); view._drawerNotice.Text = "chat panel"; pending.SetResult(true); await first;
            check(view._pane == Pane.Chat && view._drawerNotice.Text == "chat panel" && !view._giftBusy, "Late gift cannot steal a newly opened chat panel");
            view = ready(); view._api.Prepare = () => Task.FromException<LiveGiftQuote>(new Exception("price changed")); view._pane = Pane.GiftAmount;
            await view.PrepareGiftAsync(); check(!view._giftBusy && view._api.GiftCalls == 0 && view._drawerNotice.Text.Contains("price changed"), "Failed quote never sends or leaves busy stuck");
            view = ready(); view._pane = Pane.GiftAmount;
            var quotePending = new TaskCompletionSource<LiveGiftQuote>(); view._api.Prepare = () => quotePending.Task;
            var prepare = view.PrepareGiftAsync(); view.CurrentMid = 8; quotePending.SetResult(new LiveGiftQuote { AccountMid = 7, RoomId = 42 }); await prepare;
            check(view._pane == Pane.GiftAmount && !view._giftBusy && view._api.GiftCalls == 0, "Stale account quote never opens confirmation");
            view = ready(); view._chatInput.Text = "hello"; await view.SendChatAsync();
            check(view._api.ChatCalls == 1 && view._chatInput.Text == "" && view._send.IsEnabled, "Chat explicit send clears confirmed draft");
            view = ready(); view._chatInput.Text = "hello"; view._api.Chat = () => Task.FromException(new TimeoutException("timeout"));
            await view.SendChatAsync(); await view.SendChatAsync(); check(view._api.ChatCalls == 1 && view._uncertainChat == "hello", "Uncertain chat cannot automatically resend same draft");
            view = ready(); view.CurrentMid = 0; view._chatInput.Text = "hello"; await view.SendChatAsync(); check(view._api.ChatCalls == 0, "Anonymous chat requires login");
            view = ready(); view._chatInput.Text = ""; await view.SendChatAsync(); check(view._api.ChatCalls == 0, "Empty chat never posts");
            view = ready(); view._chatInput.Text = "hello"; var chatPending = new TaskCompletionSource<bool>(); view._api.Chat = () => chatPending.Task;
            var sendChat = view.SendChatAsync(); await view.SendChatAsync(); check(view._api.ChatCalls == 1, "Fast repeated A sends chat once");
            ++view._roomGeneration; view._chatInput.Text = "new room draft"; view._drawerNotice.Text = "new room"; chatPending.SetResult(true); await sendChat;
            check(view._chatInput.Text == "new room draft" && view._drawerNotice.Text == "new room", "Late chat cannot clear new room draft or status");
            view = ready(); view._chatInput.Text = "hello"; chatPending = new TaskCompletionSource<bool>(); view._api.Chat = () => chatPending.Task;
            sendChat = view.SendChatAsync(); view.OpenPane(Pane.Quality); view._drawerNotice.Text = "quality panel"; chatPending.SetResult(true); await sendChat;
            check(view._drawerNotice.Text == "quality panel" && view._chatInput.Text == "", "Confirmed chat clears its draft but does not replace the new quality panel status");
            return count;
        }
    }
}

namespace BiliXbox.LiveUiTests
{
    public static class Tests
    {
        public static async Task Run()
        {
            int count = 0;
            Action<bool, string> check = (value, name) => { ++count; if (!value) throw new Exception(name); };
            long id;
            foreach (string value in new[] { "42", " 42 ", "https://live.bilibili.com/42", "http://live.bilibili.com/42/", "live.bilibili.com/42?x=1", "https://LIVE.BILIBILI.COM/42?broadcast_type=0" })
                check(LiveInput.TryRoomId(value, out id) && id == 42, "Valid public room: " + value);
            foreach (string value in new[] { "", "0", "-4", "999999999999999999999999", "https://evil.test/42", "https://live.bilibili.com.evil.test/42", "https://live.bilibili.com/42/extra", "ftp://live.bilibili.com/42", "https://www.bilibili.com/video/BV123", "javascript:42" })
                check(!LiveInput.TryRoomId(value, out id), "Reject invalid/unrelated room: " + value);
            var gate = new LiveWriteGate();
            check(!gate.Begin(0, 7) && !gate.Begin(42, 0), "Invalid account/room cannot mutate");
            check(gate.Begin(42, 7) && !gate.Begin(42, 7), "Single in-flight gift per account/room");
            check(!gate.ConfirmAnotherGift(42, 7), "Acknowledgement cannot bypass pending POST");
            check(gate.Begin(43, 7) && gate.Begin(42, 8), "Gate keys room and account separately");
            gate.Finish(42, 7, false); check(gate.IsBlocked(42, 7) && gate.IsUncertain(42, 7), "Unknown outcome survives drawer reentry");
            check(!gate.Begin(42, 7), "No blind duplicate of unknown gift");
            check(gate.ConfirmAnotherGift(42, 7) && gate.Begin(42, 7), "Only explicit acknowledgement permits a new selection");
            gate.Finish(42, 7, true); check(!gate.IsBlocked(42, 7), "Confirmed success releases gate");
            gate.Finish(42, 7, false); check(!gate.IsBlocked(42, 7), "Stale completion cannot invent uncertainty");
            var gift = new LiveGift { UnitPrice = 150, Currency = "gold" }; check(gift.PriceText == "1.5 电池", "Gold uses exact 100-to-one batteries");
            gift.Currency = "silver"; check(gift.PriceText == "150 银瓜子", "Silver is not mislabeled as paid gold");
            gift.BagId = 5; gift.Quantity = 8; check(gift.PriceText.Contains("8"), "Bag displays actual inventory");
            var quote = new LiveGiftQuote { UnitPrice = 150, Count = 3, Currency = "gold" }; check(quote.TotalPrice == 450 && quote.CostText == "4.5 电池", "Quote total has exact decimal units");
            quote.BagId = 5; check(quote.CostText == "消耗 3 个背包礼物", "Bag confirmation identifies consumed count");
            count += await LiveHubView.RunProductionChecks();
            Console.WriteLine("PASS: " + count + " live UI input, gift safety, account/room ownership and production send/quote assertions (no network or real mutations).");
        }
    }
}
