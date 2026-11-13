using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Models;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace BiliXbox.Controls
{
    public sealed partial class LiveHubView
    {
        private int _paneGeneration;
        private long _chatDraftMid;
        private string _uncertainChat = "";
        private int _chatOperationGeneration, _giftOperationGeneration;

        private void OpenPane(Pane pane)
        {
            if (!IsPlayingRoom || _editing) return;
            ResetManualScrollState();
            _pane = pane; ++_paneGeneration; WakeChrome();
            _drawer.Visibility = Visibility.Visible;
            _drawerTitle.Text = pane == Pane.Chat ? "直播聊天" : pane == Pane.Quality ? "播放画质" : "直播礼物";
            _closeDrawer.Content = pane == Pane.GiftAmount || pane == Pane.GiftConfirm ? "返回" : "关闭";
            _messages.Visibility = _chatComposer.Visibility = pane == Pane.Chat ? Visibility.Visible : Visibility.Collapsed;
            _drawerBody.Visibility = pane == Pane.Chat ? Visibility.Collapsed : Visibility.Visible;
            ClearRows(); _drawerNotice.Text = "";
            if (pane == Pane.Chat)
            {
                EnsureChatAccount();
                _drawerNotice.Text = CurrentMid > 0 ? "R 滚动消息 · ← 编写弹幕。编辑完成后选择「发送弹幕」才会发布。" : "R 滚动消息；发送弹幕需要登录。";
                Focus(_chatInput);
                EnsureChatScroll(); FollowLatestMessage();
            }
            else if (pane == Pane.Quality)
            {
                _drawerNotice.Text = _playback == null ? "按平台实际返回的直播清晰度选择。" : "当前画质：" + _playback.QualityLabel;
                foreach (var quality in _playback?.Qualities ?? Array.Empty<LiveQuality>())
                {
                    var item = quality;
                    Row((_playback.QualityId == item.Id ? "✓  " : "") + item.Label,
                        () => Run(() => ChangeQualityAsync(item.Id)), "LiveQuality" + item.Id);
                }
                Row("重新加载直播", () => Run(() => ChangeQualityAsync(_playback?.QualityId ?? 10000)), "LiveReloadButton");
                FocusFirstRow();
            }
            else if (pane == Pane.Gifts) RenderGifts();
            else if (pane == Pane.GiftAmount) RenderGiftAmount();
            else if (pane == Pane.GiftConfirm) RenderGiftConfirm();
        }
        private void ClearRows()
        {
            foreach (var child in _drawerBody.Children) if (child is Button button) _actions.Remove(button);
            _drawerBody.Children.Clear();
        }
        private async Task ChangeQualityAsync(int quality)
        {
            if (!IsPlayingRoom) return;
            int generation = _roomGeneration; _pane = Pane.None; ++_paneGeneration; _drawer.Visibility = Visibility.Collapsed;
            Focus(_qualityButton); await LoadPlaybackAsync(quality, generation);
        }
        private void EnsureChatAccount()
        {
            if (_chatDraftMid == CurrentMid) return;
            _chatDraftMid = CurrentMid; _chatInput.Text = ""; _uncertainChat = "";
        }
        private void AttachEditor(TextBox editor, bool room)
        {
            editor.Tapped += (s, e) => { if (!_editing && ReferenceEquals(editor, room ? _roomInput : _chatInput)) Run(() => EditAsync(room)); };
        }
        private async Task EditAsync(bool roomInput)
        {
            if (_editing || (!roomInput && (!IsPlayingRoom || _sending))) return;
            if (!roomInput)
            {
                EnsureChatAccount();
                if (CurrentMid <= 0) { await LoginAsync(); return; }
            }
            if (RequestTextInputAsync == null) { if (roomInput) _notice.Text = "系统键盘暂不可用，请返回后重试。"; else _drawerNotice.Text = "系统键盘暂不可用，请返回后重试。"; return; }
            int generation = _roomGeneration; long mid = CurrentMid;
            ++_focusGeneration;
            var editor = InlineNativeTextInputSession.ReplaceEditor(roomInput ? _roomInput : _chatInput, roomInput ? 240 : 30);
            if (roomInput) _roomInput = editor; else _chatInput = editor;
            AttachEditor(editor, roomInput); _editing = true;
            try
            {
                await RequestTextInputAsync(editor, roomInput ? "进入直播间" : "发送直播弹幕", roomInput ? 240 : 30);
                if (generation != _roomGeneration || (!roomInput && CurrentMid != mid)) return;
                if (!roomInput && editor.Text != _uncertainChat) _uncertainChat = "";
            }
            finally
            {
                if (ReferenceEquals(editor, roomInput ? _roomInput : _chatInput) && generation == _roomGeneration)
                {
                    _editing = false; if (editor.IsFocusEngaged) editor.IsFocusEngaged = false; editor.IsReadOnly = true;
                    if (roomInput && !IsPlayingRoom) Focus(string.IsNullOrWhiteSpace(editor.Text) ? (Control)editor : _open);
                    else if (!roomInput && IsPlayingRoom) Focus(string.IsNullOrWhiteSpace(editor.Text) ? (Control)editor : _send);
                }
            }
        }
        private async Task LoginAsync()
        {
            if (LoginRequested == null) return;
            int generation = _roomGeneration; var pane = _pane;
            await LoginRequested();
            if (!RoomCurrent(generation)) return;
            EnsureChatAccount();
            // Re-authenticate the socket with the new session so server-side
            // account policies match the API sending account.
            var reconnect = ConnectChatAsync(generation);
            if (pane == Pane.Chat) OpenPane(Pane.Chat);
            else if (pane == Pane.Gifts || pane == Pane.GiftAmount || pane == Pane.GiftConfirm) await OpenGiftsAsync();
            await reconnect;
        }
        private async Task SendChatAsync()
        {
            if (!IsPlayingRoom || _sending || _editing) return;
            EnsureChatAccount(); if (CurrentMid <= 0) { await LoginAsync(); return; }
            string message = (_chatInput.Text ?? "").Trim();
            if (message.Length == 0) { Focus(_chatInput); return; }
            if (message == _uncertainChat)
            { _drawerNotice.Text = "上条弹幕的提交结果尚未确认，请先检查聊天记录；不会自动重复发送。修改内容后可发送新弹幕。"; return; }
            int generation = _roomGeneration, paneGeneration = _paneGeneration; var owner = _roomCts; long room = _room.Id, mid = CurrentMid;
            int operation = ++_chatOperationGeneration;
            _sending = true; _send.IsEnabled = false; _drawerNotice.Text = "正在发送直播弹幕…";
            try
            {
                await _api.SendLiveDanmakuAsync(room, message, owner.Token);
                if (!RoomCurrent(generation) || CurrentMid != mid) return;
                _chatInput.Text = ""; _uncertainChat = "";
                if (_pane == Pane.Chat && _paneGeneration == paneGeneration)
                    _drawerNotice.Text = "弹幕已提交，直播间审核与展示可能有延迟。";
                // Do not inject a second local copy: the server socket echoes
                // accepted messages, preserving authoritative sender metadata.
            }
            catch (Exception ex)
            {
                if (RoomCurrent(generation) && CurrentMid == mid)
                {
                    _uncertainChat = message;
                    if (_pane == Pane.Chat && _paneGeneration == paneGeneration)
                        _drawerNotice.Text = "未能确认发送结果：" + Friendly(ex) + "。不会自动重发。";
                }
            }
            finally { if (operation == _chatOperationGeneration) { _sending = false; if (RoomCurrent(generation)) _send.IsEnabled = true; } }
        }

        private async Task OpenGiftsAsync()
        {
            if (!IsPlayingRoom) return;
            _gifts = Array.Empty<LiveGift>(); _giftsMid = CurrentMid; _quote = null;
            OpenPane(Pane.Gifts);
            if (CurrentMid <= 0 || _giftBusy) return;
            int generation = _roomGeneration, paneGeneration = _paneGeneration; long mid = CurrentMid; var owner = _roomCts;
            _drawerNotice.Text = "正在读取" + (_bagMode ? "背包礼物" : "礼物价格") + "…";
            try
            {
                var catalog = _bagMode ? await _api.GetLiveGiftBagAsync(_room.Id, owner.Token) : await _api.GetLiveGiftCatalogAsync(_room.Id, owner.Token);
                if (!RoomCurrent(generation) || CurrentMid != mid || _paneGeneration != paneGeneration || catalog.AccountMid != mid) return;
                _gifts = catalog.Items ?? Array.Empty<LiveGift>(); _giftsMid = mid; RenderGifts(); FocusFirstRow();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (RoomCurrent(generation) && _paneGeneration == paneGeneration) _drawerNotice.Text = "礼物加载失败：" + Friendly(ex); }
        }
        private void RenderGifts()
        {
            ClearRows();
            if (CurrentMid <= 0)
            {
                _drawerNotice.Text = "登录后可查看背包礼物和送礼。";
                Row("扫码登录", () => Run(LoginAsync), "LiveGiftLoginButton"); FocusFirstRow(); return;
            }
            long room = _room.Id, mid = CurrentMid;
            Row(_bagMode ? "✓  背包礼物" : "背包礼物", () => { _bagMode = true; Run(OpenGiftsAsync); }, "LiveBagTab", !_giftBusy);
            Row(!_bagMode ? "✓  全部礼物" : "全部礼物", () => { _bagMode = false; Run(OpenGiftsAsync); }, "LiveShopTab", !_giftBusy);
            _drawerNotice.Text = _giftBusy ? "礼物提交中，请勿重复操作。" : "选择礼物与数量后会显示明确金额；只有最后选择「确认赠送」才会提交。";
            if (_writeGate.IsUncertain(room, mid))
            {
                _drawerNotice.Text = "上次赠送结果未确认，请先在官方客户端核对背包或交易记录。不会自动重复赠送。";
                Row("我已核对记录，允许重新选择礼物", () => { _writeGate.ConfirmAnotherGift(room, mid); Run(OpenGiftsAsync); }, "LiveGiftAcknowledgeButton", !_giftBusy);
            }
            bool available = _giftsMid == mid && !_giftBusy && !_writeGate.IsBlocked(room, mid);
            foreach (var gift in _gifts.Where(item => item != null && item.Id > 0))
            {
                var item = gift;
                Row(item.Name + " · " + item.PriceText, () => { _selectedGift = item; _giftCount = 1; _quote = null; OpenPane(Pane.GiftAmount); },
                    "LiveGift" + item.Id + "Bag" + item.BagId, available && (!item.IsBag || item.Quantity > 0));
            }
            if (_gifts.Count == 0 && !_giftBusy && _giftsMid == mid)
            {
                var empty = Text(_bagMode ? "背包暂无可用礼物，可切换「全部礼物」。" : "当前直播间暂无可用礼物。", 15);
                empty.TextWrapping = TextWrapping.Wrap; _drawerBody.Children.Add(empty);
            }
            Row("刷新礼物", () => Run(OpenGiftsAsync), "LiveGiftRefreshButton", !_giftBusy);
        }
        private void RenderGiftAmount()
        {
            if (_selectedGift == null || _giftsMid != CurrentMid) { OpenPane(Pane.Gifts); return; }
            _drawerNotice.Text = _selectedGift.Name + " · 单价 " + _selectedGift.PriceText + "\n数量：" + _giftCount;
            int maximum = (int)Math.Min(Math.Max(0, _selectedGift.MaxCount), Math.Min(999,
                _selectedGift.IsBag ? Math.Max(0, _selectedGift.Quantity) : 999));
            Row("数量 − 1", () => { _giftCount = Math.Max(1, _giftCount - 1); OpenPane(Pane.GiftAmount); }, "LiveGiftLessButton", _giftCount > 1 && !_giftBusy);
            Row("数量 + 1", () => { _giftCount = Math.Min(maximum, _giftCount + 1); OpenPane(Pane.GiftAmount); }, "LiveGiftMoreButton", _giftCount < maximum && !_giftBusy);
            Row("查看金额并继续", () => Run(PrepareGiftAsync), "LiveGiftPrepareButton", !_giftBusy && _giftCount > 0 && _giftCount <= maximum);
            FocusFirstRow();
        }
        private async Task PrepareGiftAsync()
        {
            if (!IsPlayingRoom || _giftBusy || _selectedGift == null || _giftsMid != CurrentMid || CurrentMid <= 0) return;
            int generation = _roomGeneration, paneGeneration = _paneGeneration; long mid = CurrentMid; var owner = _roomCts;
            if (_writeGate.IsBlocked(_room.Id, mid)) return;
            int operation = ++_giftOperationGeneration;
            var gift = _selectedGift; int count = _giftCount; _giftBusy = true;
            _drawerNotice.Text = "正在核对库存和金额…"; RenderGiftAmount();
            try
            {
                var quote = await _api.PrepareLiveGiftAsync(_room.Id, gift, count, owner.Token);
                if (!RoomCurrent(generation) || CurrentMid != mid || _selectedGift != gift || _pane != Pane.GiftAmount || _paneGeneration != paneGeneration) return;
                _quote = quote; _giftBusy = false; OpenPane(Pane.GiftConfirm);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (RoomCurrent(generation) && CurrentMid == mid && _paneGeneration == paneGeneration) _drawerNotice.Text = "金额核对失败：" + Friendly(ex); }
            finally
            {
                if (operation == _giftOperationGeneration) _giftBusy = false;
                if (operation == _giftOperationGeneration && RoomCurrent(generation) && CurrentMid == mid && _pane == Pane.GiftAmount && _paneGeneration == paneGeneration)
                { string result = _drawerNotice.Text; ClearRows(); RenderGiftAmount(); _drawerNotice.Text = result; }
                else if (operation == _giftOperationGeneration && RoomCurrent(generation) && CurrentMid == mid && _pane == Pane.Gifts)
                    RenderGifts();
            }
        }
        private void RenderGiftConfirm()
        {
            if (_quote == null || _quote.AccountMid != CurrentMid || _room == null || _quote.RoomId != _room.Id)
            { _quote = null; OpenPane(Pane.Gifts); return; }
            string unit = _quote.BagId > 0 ? "背包礼物" : _selectedGift?.PriceText ?? "";
            _drawerNotice.Text = "赠送给：" + _room.OwnerName + "\n礼物：" + _quote.GiftName + " × " + _quote.Count +
                "\n单价：" + unit + "\n合计：" + _quote.CostText + "\n确认后将扣除对应礼物或账户余额。";
            Row(_giftBusy ? "正在赠送…" : "确认赠送 · " + _quote.CostText, () => Run(SendGiftAsync), "LiveGiftConfirmButton",
                !_giftBusy && !_writeGate.IsBlocked(_quote.RoomId, _quote.AccountMid));
            Row("取消，返回礼物", () => { _quote = null; OpenPane(Pane.Gifts); }, "LiveGiftCancelButton", !_giftBusy);
            FocusFirstRow();
        }
        private async Task SendGiftAsync()
        {
            var quote = _quote;
            if (!IsPlayingRoom || _giftBusy || quote == null || quote.AccountMid != CurrentMid || quote.RoomId != _room.Id ||
                !_writeGate.Begin(quote.RoomId, quote.AccountMid)) return;
            int generation = _roomGeneration, paneGeneration = _paneGeneration, operation = ++_giftOperationGeneration; var owner = _roomCts; _giftBusy = true; ClearRows(); RenderGiftConfirm();
            bool known = false;
            try
            {
                await _api.SendLiveGiftAsync(quote, true, owner.Token); known = true;
                if (!RoomCurrent(generation) || CurrentMid != quote.AccountMid || _paneGeneration != paneGeneration) return;
                _quote = null; _gifts = Array.Empty<LiveGift>(); _drawerNotice.Text = "礼物已赠送：" + quote.GiftName + " × " + quote.Count;
            }
            catch (Exception ex)
            {
                if (RoomCurrent(generation) && CurrentMid == quote.AccountMid && _paneGeneration == paneGeneration)
                    _drawerNotice.Text = "未能确认赠送结果：" + Friendly(ex) + "。不会自动重发，请先核对记录。";
            }
            finally
            {
                _writeGate.Finish(quote.RoomId, quote.AccountMid, known);
                if (operation == _giftOperationGeneration) _giftBusy = false;
                if (operation == _giftOperationGeneration && RoomCurrent(generation) && CurrentMid == quote.AccountMid && _paneGeneration == paneGeneration)
                {
                    string result = _drawerNotice.Text; _quote = null; OpenPane(Pane.Gifts); _drawerNotice.Text = result;
                }
                else if (operation == _giftOperationGeneration && RoomCurrent(generation) && CurrentMid == quote.AccountMid && _pane == Pane.Gifts)
                    RenderGifts();
            }
        }
        private void FocusFirstRow()
        {
            var row = _drawerBody.Children.OfType<Button>().FirstOrDefault(button => button.IsEnabled);
            Focus(row ?? _closeDrawer);
        }
    }
}
