using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Models;

namespace BiliXbox.Controls
{
    public sealed partial class VideoInteractionView
    {
        private async Task RefreshAsync(bool explicitRefresh)
        {
            if (_busy || !_session.Active) return;
            if (!HasAccount)
            {
                _message = "请先扫码登录，再进行视频互动。";
                Render(); return;
            }
            long generation = _session.Generation, aid = _aid;
            var token = _sessionCts.Token;
            _busy = true; Render();
            try
            {
                var state = await _api.GetVideoInteractionsAsync(aid, token);
                if (!Current(generation)) return;
                if (!_session.AcceptRead(generation, state.Aid, state.AccountMid, explicitRefresh))
                    throw new InvalidOperationException("互动状态与当前视频或账户不匹配。");
                _state = state; _pane = Pane.Overview;
                _message = _session.IsUncertain
                    ? "上次提交结果仍待确认。请稍后选择「刷新状态」，确认前不会重复提交。"
                    : "已读取当前账户的点赞、投币和收藏状态。";
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (Current(generation)) _message = "读取互动状态失败：" + ex.Message + "。请选择「刷新状态」重试。";
            }
            finally
            {
                FinishOperation(generation);
            }
        }

        private async Task LoginAsync()
        {
            if (_busy || !_session.Active || LoginRequested == null) return;
            long generation = _session.Generation, aid = _aid, owner = _ownerMid;
            string name = _ownerName;
            var token = _externalToken;
            await LoginRequested();
            if (!_session.Active || generation != _session.Generation || token.IsCancellationRequested) return;
            await ShowAsync(aid, owner, name, token);
        }

        private async Task OpenFavoritesAsync()
        {
            if (_busy || !_session.CanMutate) return;
            long generation = _session.Generation, aid = _aid;
            var token = _sessionCts.Token;
            _busy = true; Render();
            try
            {
                var folders = await _api.GetVideoFavoriteFoldersAsync(aid, token);
                if (!Current(generation)) return;
                _folders = folders;
                _selection.Reset(folders.Select(folder => new KeyValuePair<long, bool>(folder.Id, folder.IsSelected)));
                _pane = Pane.Favorites; _message = "";
                _lastFocus = folders.Count > 0 ? "InteractionFolder_" + folders[0].Id : "InteractionCloseButton";
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { if (Current(generation)) _message = "收藏夹读取失败：" + ex.Message; }
            finally { FinishOperation(generation); }
        }

        private Task LikeAsync()
        {
            bool target = _state?.IsLiked != true;
            long aid = _aid;
            return MutateAsync(token => _api.SetVideoLikeAsync(aid, target, token),
                state => state.IsLiked == target, target ? "已点赞。" : "已取消点赞。", "InteractionLikeButton");
        }

        private Task CoinAsync(int count)
        {
            if (_state == null || count < 1 || count > 2 || _state.Coins + count > 2) return Task.CompletedTask;
            int before = _state.Coins;
            long aid = _aid;
            return MutateAsync(token => _api.AddVideoCoinsAsync(aid, count, token),
                state => state.Coins >= before + count, "已投 " + count + " 枚硬币。", "InteractionCoinButton");
        }

        private Task SaveFavoritesAsync()
        {
            if (!_selection.Changed) return Task.CompletedTask;
            long aid = _aid;
            var added = _selection.Added.ToArray(); var removed = _selection.Removed.ToArray();
            var expected = _folders.ToDictionary(folder => folder.Id, folder => _selection.Contains(folder.Id));
            return MutateAsync(token => _api.UpdateVideoFavoritesAsync(aid, added, removed, token),
                state => state.IsFavorited == expected.Values.Any(selected => selected),
                "收藏夹已保存。", "InteractionFavoriteButton", async token =>
                {
                    var actual = await _api.GetVideoFavoriteFoldersAsync(aid, token);
                    return expected.All(pair => actual.Any(folder => folder.Id == pair.Key && folder.IsSelected == pair.Value));
                });
        }

        private async Task TripleAsync()
        {
            if (_state == null || _state.Coins != 0) return;
            long aid = _aid;
            int before = _state.Coins;
            VideoTripleResult result = null;
            await MutateAsync(async token => { result = await _api.TripleVideoAsync(aid, token); },
                state => result != null && result.IsComplete && state.IsLiked && state.IsFavorited && state.Coins > before,
                "三连结果已确认：已点赞、投币并收藏。", "InteractionTripleButton",
                null, () => result == null ? "三连结果未确认。"
                    : "三连返回：点赞" + (result.Like ? "已完成" : "未确认")
                        + "、投币" + (result.Coin ? "已完成" : "未确认")
                        + "、收藏" + (result.Favorite ? "已完成" : "未确认") + "。请刷新确认当前状态。");
        }

        private async Task MutateAsync(Func<CancellationToken, Task> submit,
            Func<VideoInteractionState, bool> verify, string success, string focusId,
            Func<CancellationToken, Task<bool>> extraVerify = null, Func<string> partialNotice = null)
        {
            if (_busy || !HasAccount) return;
            long attempt = _session.BeginMutation();
            if (attempt == 0) return;
            long generation = _session.Generation, aid = _aid;
            var token = _sessionCts.Token;
            _busy = true; _lastFocus = focusId; Render();
            // A submitted operation may finish remotely after this drawer is
            // closed. Mark cached favorites stale before awaiting it, then
            // again when it settles in case a return refreshed them too soon.
            InteractionChanged?.Invoke(this, EventArgs.Empty);
            try
            {
                // Exactly one deliberate POST. A timeout or partial result is
                // followed only by reads, never by an automatic write retry.
                await submit(token);
                if (!Current(generation)) return;
                var state = await _api.GetVideoInteractionsAsync(aid, token);
                if (!Current(generation)) return;
                if (!_session.AcceptRead(generation, state.Aid, state.AccountMid, false))
                    throw new InvalidOperationException("互动结果与当前视频或账户不匹配。");
                bool confirmed = verify(state);
                if (confirmed && extraVerify != null) confirmed = await extraVerify(token);
                if (!Current(generation)) return;
                _state = state;
                _session.CompleteMutation(generation, attempt, confirmed);
                _pane = Pane.Overview;
                _lastFocus = focusId;
                _message = confirmed ? success : (partialNotice?.Invoke() ?? "提交结果尚未确认，请稍后刷新状态；不要立即重复投币。");
                if (!confirmed) _lastFocus = "InteractionRefreshButton";
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (Current(generation))
                {
                    _session.CompleteMutation(generation, attempt, false);
                    _pane = Pane.Overview; _lastFocus = "InteractionRefreshButton";
                    _message = "提交结果未确认：" + ex.Message + "。请稍后刷新状态，不会自动重复提交。";
                }
            }
            finally
            {
                // This only invalidates account-library caches. Old responses
                // still cannot update a different video's interaction UI.
                InteractionChanged?.Invoke(this, EventArgs.Empty);
                FinishOperation(generation);
            }
        }
    }
}
