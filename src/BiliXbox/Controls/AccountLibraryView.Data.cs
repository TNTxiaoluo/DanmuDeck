using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Models;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace BiliXbox.Controls
{
    public sealed partial class AccountLibraryView
    {
        private void OpenSection(LibraryPane pane)
        {
            if (!HasAccount) return;
            string title = pane == LibraryPane.History ? "播放历史" : pane == LibraryPane.Folders ? "收藏夹" : "追番追剧";
            Push(new ViewState { Pane = pane, Title = title }); Run(RefreshAsync);
        }

        private void OpenFolder(FavoriteFolder folder)
        {
            if (!HasAccount || _busy || folder == null || folder.Id <= 0) return;
            Push(new ViewState { Pane = LibraryPane.Favorites, Title = folder.Title, FolderId = folder.Id }); Run(RefreshAsync);
        }

        private void SwitchFollows(bool drama)
        {
            if (_state.Pane != LibraryPane.Follows || _state.Drama == drama || !HasAccount) return;
            ++_openGeneration; _openingItem = false;
            CancelLoad(); _state.Drama = drama; _state.Pages.Clear(); _state.PageIndex = _state.SelectedIndex = 0;
            _state.Notice = ""; Render(); Run(RefreshAsync);
        }

        private void Push(ViewState state)
        {
            ++_openGeneration; _openingItem = false;
            RememberSelection(); CancelLoad(); _trail.Push(_state); _state = state; _lastFocus = null;
            Render(); Focus(_refresh);
        }

        public bool Back()
        {
            if (_trail.Count == 0) return false;
            ++_openGeneration; _openingItem = false;
            CancelLoad(); _state = _trail.Pop(); _lastFocus = null; Render(); RestoreFocus(); return true;
        }

        private async Task RefreshAsync()
        {
            if (!EnsureSession() || _state.Pane == LibraryPane.Overview) return;
            var state = _state;
            if (state.Pane == LibraryPane.Folders)
            {
                await LoadAsync(state, async token =>
                {
                    var folders = await _api.GetFavoriteFoldersAsync(token);
                    return () => { state.Folders = folders; state.Notice = ""; state.SelectedIndex = 0; };
                });
            }
            else if (state.Pane == LibraryPane.Episodes)
            {
                await LoadAsync(state, async token =>
                {
                    var season = await _api.GetSeasonDetailAsync(state.SeasonId, 0, token);
                    return () => { state.Episodes = season.Episodes; state.Title = season.Title; state.Notice = season.Notice; state.SelectedIndex = 0; };
                });
            }
            else await LoadPageAsync(state, 0, true);
        }

        private async Task ChangePageAsync(int delta)
        {
            if (_busy || delta == 0 || !EnsureSession()) return;
            var state = _state; int index = state.PageIndex + delta;
            if (index < 0) return;
            if (index < state.Pages.Count)
            {
                ++_focusGeneration; state.PageIndex = index; state.SelectedIndex = 0;
                state.Notice = state.Pages[index].Notice; Render(); FocusFirst(); return;
            }
            if (delta > 0 && CurrentPage?.HasMore == true) await LoadPageAsync(state, index, false);
        }

        private async Task LoadPageAsync(ViewState state, int index, bool reset)
        {
            if (!reset && (index != state.Pages.Count || index == 0)) return;
            var previous = index > 0 ? state.Pages[index - 1] : null;
            await LoadAsync(state, async token =>
            {
                AccountLibraryPage page;
                if (state.Pane == LibraryPane.History) page = await _api.GetHistoryAsync(previous?.NextCursor ?? "", token);
                else if (state.Pane == LibraryPane.Favorites) page = await _api.GetFavoriteItemsAsync(state.FolderId, previous?.NextPage ?? 1, token);
                else page = await _api.GetFollowedSeasonsAsync(state.Drama, previous?.NextPage ?? 1, token);
                return () =>
                {
                    if (reset) state.Pages.Clear();
                    state.Pages.Add(page); state.PageIndex = index; state.SelectedIndex = 0; state.Notice = page.Notice;
                };
            });
        }

        private async Task LoadAsync(ViewState state, Func<CancellationToken, Task<Action>> request, bool focusFirst = true)
        {
            if (!EnsureSession()) return;
            CancelLoad();
            var session = _sessionCts;
            var owner = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
            _loadCts = owner; _busy = true; Render(); Focus(_back);
            try
            {
                var apply = await request(owner.Token);
                if (!IsCurrent(state, session, owner)) return;
                apply();
            }
            catch (OperationCanceledException) when (owner.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (!IsCurrent(state, session, owner)) return;
                state.Notice = "读取失败：" + Friendly(ex) + "。可选择刷新重试。";
            }
            finally
            {
                if (ReferenceEquals(_loadCts, owner))
                {
                    _loadCts = null; _busy = false; owner.Dispose();
                    if (ReferenceEquals(state, _state) && HasAccount)
                    {
                        Render();
                        if (focusFirst) FocusFirst(); else RestoreFocus();
                    }
                }
            }
        }

        private async Task OpenItemAsync(AccountLibraryItem item)
        {
            if (!HasAccount || _busy || _openingItem || item == null) return;
            if (item.IsSeason && item.EpisodeId <= 0 && item.SeasonId > 0)
            {
                Push(new ViewState { Pane = LibraryPane.Episodes, Title = item.Title, SeasonId = item.SeasonId });
                await RefreshAsync(); return;
            }
            RememberSelection();
            int generation = ++_openGeneration; long account = _accountMid; var state = _state;
            _openingItem = true;
            try
            {
                // History rows can identify a licensed episode without its
                // aid/cid. Resolve it through the actual season response before
                // applying availability checks or requesting playback.
                if (item.EpisodeId > 0 && (item.Aid <= 0 || item.Cid <= 0))
                {
                    var original = item; AccountLibraryItem resolved = null;
                    await LoadAsync(state, async token =>
                    {
                        var season = await _api.GetSeasonDetailAsync(original.SeasonId, original.EpisodeId, token);
                        var episode = season.Episodes.FirstOrDefault(entry => entry.EpisodeId == original.EpisodeId);
                        if (episode == null) throw new InvalidOperationException("未能读取历史记录对应的剧集。");
                        return () => { resolved = episode; resolved.ProgressSeconds = original.ProgressSeconds; state.Notice = season.Notice; };
                    }, false);
                    if (generation != _openGeneration || !ReferenceEquals(state, _state) || account != _accountMid || !HasAccount || resolved == null) return;
                    item = resolved;
                }
                if (!item.IsPlayable || (item.EpisodeId <= 0 && string.IsNullOrWhiteSpace(item.Bvid)))
                {
                    _state.Notice = string.IsNullOrWhiteSpace(item.UnavailableReason) ? "此内容暂时无法播放。" : item.UnavailableReason;
                    Render(); return;
                }
                var handler = OpenItemRequested;
                if (handler != null) await handler(item);
            }
            finally { if (generation == _openGeneration) _openingItem = false; }
        }

        private bool EnsureSession()
        {
            if (!HasAccount)
            {
                ClearAccount(); _subtitle.Text = "登录已失效，请返回后重新登录。"; Render(); return false;
            }
            if (_sessionCts == null || _sessionCts.IsCancellationRequested) _sessionCts = new CancellationTokenSource();
            return true;
        }

        private bool IsCurrent(ViewState state, CancellationTokenSource session, CancellationTokenSource owner) =>
            ReferenceEquals(state, _state) && ReferenceEquals(session, _sessionCts) && ReferenceEquals(owner, _loadCts)
            && !owner.IsCancellationRequested && HasAccount;

        private void CancelLoad()
        {
            Cancel(ref _loadCts); _busy = false; ++_focusGeneration; _pendingList = null; _pendingIndex = -1;
        }

        private async void Run(Func<Task> request)
        {
            var state = _state; long account = _accountMid;
            try { await request(); }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!ReferenceEquals(state, _state) || account != _accountMid || !HasAccount) return;
                _state.Notice = "操作失败：" + Friendly(ex); Render();
            }
        }

        private static string Friendly(Exception ex) => (ex?.Message ?? "未知错误").Replace("\r", " ").Replace("\n", " ");
    }
}
