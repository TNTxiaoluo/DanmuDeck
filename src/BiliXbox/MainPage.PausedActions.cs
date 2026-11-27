using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BiliXbox.Models;
using BiliXbox.Services;
using Windows.Gaming.Input;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace BiliXbox
{
    // 简介 page actions (replacing the old 互动 drawer): 点赞, 投币 (1 or 2
    // coins), 收藏 (choose folders). Holding A on 点赞 sends 一键三连.
    public sealed partial class MainPage
    {
        private static readonly Color ActionActive = Color.FromArgb(255, 251, 114, 153);
        private static readonly Color ActionIdle = Color.FromArgb(230, 255, 255, 255);
        private VideoInteractionState _pausedInteraction;
        private long _pausedInteractionAid;
        private bool _pausedActionBusy;
        private int _pausedActionIndex;

        private Button[] PausedActionButtons => new[] { PausedLikeButton, PausedCoinButton, PausedFavoriteButton };

        private void PreparePausedActions()
        {
            var video = _video;
            if (video == null) return;
            PausedLikeCount.Text = video.LikeCount.HasValue ? IntroCount(video.LikeCount.Value) : "点赞";
            PausedCoinCount.Text = video.CoinCount.HasValue ? IntroCount(video.CoinCount.Value) : "投币";
            PausedFavoriteCount.Text = video.FavoriteCount.HasValue ? IntroCount(video.FavoriteCount.Value) : "收藏";
            if (_pausedInteractionAid != video.Aid) { _pausedInteraction = null; _pausedInteractionAid = video.Aid; ApplyPausedActionState(); _ = LoadPausedInteractionAsync(video.Aid); }
            else ApplyPausedActionState();
        }

        private async Task LoadPausedInteractionAsync(long aid)
        {
            if (aid <= 0 || !_api.IsLoggedIn) return;
            try
            {
                var state = await _api.GetVideoInteractionsAsync(aid);
                if (_video?.Aid != aid) return;
                _pausedInteraction = state;
                ApplyPausedActionState();
            }
            catch (Exception) { /* Counts still show; state appears after the next action. */ }
        }

        private void ApplyPausedActionState()
        {
            var state = _pausedInteraction;
            PausedLikeIcon.Foreground = new SolidColorBrush(state?.IsLiked == true ? ActionActive : ActionIdle);
            var coin = new SolidColorBrush(state != null && state.Coins > 0 ? ActionActive : ActionIdle);
            PausedCoinIcon.BorderBrush = coin; PausedCoinGlyph.Foreground = coin;
            PausedFavoriteIcon.Foreground = new SolidColorBrush(state?.IsFavorited == true ? ActionActive : ActionIdle);
        }

        private int FocusedPausedAction() => Array.IndexOf(PausedActionButtons, FocusManager.GetFocusedElement() as Button);

        private void FocusPausedAction(int index)
        {
            ++_pausedRecommendationFocus;
            _pausedFocusGrid = null;
            _pausedActionIndex = Math.Max(0, Math.Min(PausedActionButtons.Length - 1, index));
            var button = PausedActionButtons[_pausedActionIndex];
            if (button.Focus(FocusState.Keyboard)) button.StartBringIntoView();
        }

        private bool EnsurePausedActionLogin()
        {
            if (_api.IsLoggedIn) return true;
            PausedRecommendationNotice.Text = "点赞、投币和收藏需要先登录。";
            return false;
        }

        private void PausedLike_Click(object sender, RoutedEventArgs args)
        { if (!IgnorePlayerClick()) _ = TogglePausedLikeAsync(); }
        private void PausedCoin_Click(object sender, RoutedEventArgs args)
        { if (!IgnorePlayerClick()) _ = ShowPausedCoinAsync(); }
        private void PausedFavorite_Click(object sender, RoutedEventArgs args)
        { if (!IgnorePlayerClick()) _ = ShowPausedFavoriteAsync(); }

        // A on 点赞, like the official app: a tap toggles the like; holding
        // it draws a ring around 点赞, 投币 and 收藏 (about 1.2 s) and a
        // completed ring sends 一键三连. Releasing early cancels the rings.
        private const double TripleHoldDelay = 0.25, TripleRingSeconds = 1.2;
        private int _likeHoldGeneration;

        private async Task PressPausedLikeAsync()
        {
            if (_pausedActionBusy) return;
            int generation = ++_likeHoldGeneration;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < TripleHoldDelay)
            {
                await Task.Delay(30);
                if (generation != _likeHoldGeneration || !_pausedRecommendationsVisible) return;
                if (!IsConfirmStillHeld()) { await TogglePausedLikeAsync(); return; }
            }
            PausedRecommendationNotice.Text = "继续按住，一键三连…";
            SetTripleRings(0, true);
            clock.Restart();
            while (true)
            {
                await Task.Delay(16);
                if (generation != _likeHoldGeneration || !_pausedRecommendationsVisible) { SetTripleRings(0, false); return; }
                double progress = Math.Min(1, clock.Elapsed.TotalSeconds / TripleRingSeconds);
                SetTripleRings(progress, true);
                if (!IsConfirmStillHeld())
                {
                    SetTripleRings(0, false);
                    PausedRecommendationNotice.Text = "松开了，三连已取消";
                    return;
                }
                if (progress >= 1) break;
            }
            await TriplePausedAsync();
            await Task.Delay(350);
            SetTripleRings(0, false);
        }

        // Draw the three rings to the given fraction (clockwise from the top).
        private void SetTripleRings(double progress, bool visible)
        {
            foreach (var ring in new[] { PausedLikeRing, PausedCoinRing, PausedFavoriteRing })
            {
                ring.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                if (!visible) continue;
                if (!(ring.RenderTransform is RotateTransform))
                {
                    ring.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
                    ring.RenderTransform = new RotateTransform { Angle = -90 };
                }
                // Dash lengths are in multiples of the stroke thickness.
                double circumference = Math.PI * (ring.Width - ring.StrokeThickness) / ring.StrokeThickness;
                ring.StrokeDashArray = new DoubleCollection { Math.Max(0.001, progress * circumference), circumference + 10 };
            }
        }

        private bool IsConfirmStillHeld()
        {
            try { if (_activeGamepad != null && (_activeGamepad.GetCurrentReading().Buttons & GamepadButtons.A) != 0) return true; }
            catch (Exception) { }
            try
            {
                var window = CoreWindow.GetForCurrentThread();
                foreach (var key in new[] { VirtualKey.GamepadA, VirtualKey.Enter, VirtualKey.Space })
                    if ((window.GetKeyState(key) & CoreVirtualKeyStates.Down) != 0) return true;
            }
            catch (Exception) { }
            return _playerInput.ConfirmHeld;
        }

        private async Task TogglePausedLikeAsync()
        {
            var video = _video;
            if (video == null || video.Aid <= 0 || _pausedActionBusy || !EnsurePausedActionLogin()) return;
            if (!await EnsureInteractionAuthorizedAsync()) return;
            _pausedActionBusy = true;
            try
            {
                bool like = _pausedInteraction?.IsLiked != true;
                await _api.SetVideoLikeAsync(video.Aid, like);
                if (_video?.Aid != video.Aid) return;
                if (_pausedInteraction == null) _pausedInteraction = new VideoInteractionState { Aid = video.Aid };
                _pausedInteraction.IsLiked = like;
                if (video.LikeCount.HasValue) video.LikeCount = Math.Max(0, video.LikeCount.Value + (like ? 1 : -1));
                PausedLikeCount.Text = video.LikeCount.HasValue ? IntroCount(video.LikeCount.Value) : "点赞";
                ApplyPausedActionState();
                PausedRecommendationNotice.Text = like ? "已点赞" : "已取消点赞";
            }
            catch (Exception ex) { PausedRecommendationNotice.Text = "点赞失败：" + Friendly(ex); RecordInteractionFailure("op132", ex); }
            finally { _pausedActionBusy = false; }
        }

        private async Task TriplePausedAsync()
        {
            var video = _video;
            if (video == null || video.Aid <= 0 || _pausedActionBusy || !EnsurePausedActionLogin()) return;
            if (!await EnsureInteractionAuthorizedAsync()) return;
            _pausedActionBusy = true;
            PausedRecommendationNotice.Text = "正在一键三连…";
            try
            {
                var result = await _api.TripleVideoAsync(video.Aid);
                if (_video?.Aid != video.Aid) return;
                if (_pausedInteraction == null) _pausedInteraction = new VideoInteractionState { Aid = video.Aid };
                if (result.Like) _pausedInteraction.IsLiked = true;
                if (result.Coin) _pausedInteraction.Coins = Math.Max(_pausedInteraction.Coins, result.Coins > 0 ? result.Coins : 1);
                if (result.Favorite) _pausedInteraction.IsFavorited = true;
                ApplyPausedActionState();
                AccountLibrary.InvalidateFavoriteContent();
                PausedRecommendationNotice.Text = result.IsComplete ? "一键三连成功"
                    : "三连部分完成：" + (result.Like ? "已点赞 " : "") + (result.Coin ? "已投币 " : "") + (result.Favorite ? "已收藏" : "");
            }
            catch (Exception ex) { PausedRecommendationNotice.Text = "一键三连失败：" + Friendly(ex); RecordInteractionFailure("op155", ex); }
            finally { _pausedActionBusy = false; }
        }

        private async Task ShowPausedCoinAsync()
        {
            var video = _video;
            if (video == null || video.Aid <= 0 || _dialogOpen || _pausedActionBusy || !EnsurePausedActionLogin()) return;
            if (!await EnsureInteractionAuthorizedAsync()) return;
            int given = _pausedInteraction?.Coins ?? 0;
            if (given >= 2) { PausedRecommendationNotice.Text = "这个视频已经投过 2 个硬币了。"; return; }
            int chosen = 0;
            var rows = new StackPanel { Spacing = 8, MinWidth = 360 };
            var dialog = new ContentDialog { Title = "投币", Content = rows, CloseButtonText = "取消", RequestedTheme = ElementTheme.Dark,
                DefaultButton = ContentDialogButton.Close, RequiresPointer = RequiresPointer.Never };
            Button first = null;
            for (int count = 1; count <= 2 - given; ++count)
            {
                int value = count;
                var button = new Button { Content = "投 " + count + " 个硬币", HorizontalAlignment = HorizontalAlignment.Stretch,
                    Style = (Style)Application.Current.Resources["TvRowButtonStyle"] };
                button.Click += (s, e) => { chosen = value; dialog.Hide(); };
                rows.Children.Add(button);
                if (first == null) first = button;
            }
            if (given > 0) rows.Children.Add(new TextBlock { Text = "已投 " + given + " 个，每个视频最多 2 个。", Opacity = .7, FontSize = 15 });
            dialog.Opened += async (s, e) => await Dispatcher.RunAsync(CoreDispatcherPriority.Low, () => first?.Focus(FocusState.Keyboard));
            await ShowPausedDialogAsync(dialog);
            FocusPausedAction(1);
            if (chosen == 0 || _video?.Aid != video.Aid) return;
            _pausedActionBusy = true;
            try
            {
                await _api.AddVideoCoinsAsync(video.Aid, chosen);
                if (_pausedInteraction == null) _pausedInteraction = new VideoInteractionState { Aid = video.Aid };
                _pausedInteraction.Coins = given + chosen;
                if (video.CoinCount.HasValue) { video.CoinCount = video.CoinCount.Value + chosen; PausedCoinCount.Text = IntroCount(video.CoinCount.Value); }
                ApplyPausedActionState();
                PausedRecommendationNotice.Text = "已投 " + chosen + " 个硬币";
            }
            catch (Exception ex) { PausedRecommendationNotice.Text = "投币失败：" + Friendly(ex); RecordInteractionFailure("op194", ex); }
            finally { _pausedActionBusy = false; FocusPausedAction(1); }
        }

        private async Task ShowPausedFavoriteAsync()
        {
            var video = _video;
            if (video == null || video.Aid <= 0 || _dialogOpen || _pausedActionBusy || !EnsurePausedActionLogin()) return;
            if (!await EnsureInteractionAuthorizedAsync()) return;
            IReadOnlyList<VideoFavoriteFolder> folders;
            PausedRecommendationNotice.Text = "正在读取收藏夹…";
            try { folders = await _api.GetVideoFavoriteFoldersAsync(video.Aid); }
            catch (Exception ex) { PausedRecommendationNotice.Text = "收藏夹读取失败：" + Friendly(ex); return; }
            PausedRecommendationNotice.Text = PausedHint();
            if (folders.Count == 0) { PausedRecommendationNotice.Text = "没有可用的收藏夹。"; return; }
            var selected = new HashSet<long>(folders.Where(folder => folder.IsSelected).Select(folder => folder.Id));
            var original = new HashSet<long>(selected);
            var rows = new StackPanel { Spacing = 6, MinWidth = 460 };
            var dialog = new ContentDialog { Title = "收藏到", Content = new ScrollViewer { Content = rows, MaxHeight = 520 },
                PrimaryButtonText = "确定", CloseButtonText = "取消", RequestedTheme = ElementTheme.Dark,
                DefaultButton = ContentDialogButton.Primary, RequiresPointer = RequiresPointer.Never };
            Button first = null;
            foreach (var folder in folders)
            {
                var item = folder;
                var button = new Button { HorizontalAlignment = HorizontalAlignment.Stretch, Style = (Style)Application.Current.Resources["TvRowButtonStyle"] };
                Action refresh = () => button.Content = (selected.Contains(item.Id) ? "✓  " : "      ") + item.Title + "  ·  " + item.Count + " 个内容";
                refresh();
                button.Click += (s, e) => { if (!selected.Remove(item.Id)) selected.Add(item.Id); refresh(); };
                rows.Children.Add(button);
                if (first == null) first = button;
            }
            bool confirmed = false;
            dialog.PrimaryButtonClick += (s, e) => confirmed = true;
            dialog.Opened += async (s, e) => await Dispatcher.RunAsync(CoreDispatcherPriority.Low, () => first?.Focus(FocusState.Keyboard));
            await ShowPausedDialogAsync(dialog);
            FocusPausedAction(2);
            if (!confirmed || _video?.Aid != video.Aid) return;
            var add = selected.Except(original).ToArray();
            var remove = original.Except(selected).ToArray();
            if (add.Length == 0 && remove.Length == 0) return;
            _pausedActionBusy = true;
            try
            {
                await _api.UpdateVideoFavoritesAsync(video.Aid, add, remove);
                if (_pausedInteraction == null) _pausedInteraction = new VideoInteractionState { Aid = video.Aid };
                bool was = original.Count > 0, now = selected.Count > 0;
                _pausedInteraction.IsFavorited = now;
                if (video.FavoriteCount.HasValue && was != now) { video.FavoriteCount = Math.Max(0, video.FavoriteCount.Value + (now ? 1 : -1)); PausedFavoriteCount.Text = IntroCount(video.FavoriteCount.Value); }
                ApplyPausedActionState();
                AccountLibrary.InvalidateFavoriteContent();
                PausedRecommendationNotice.Text = now ? "已更新收藏夹" : "已取消收藏";
            }
            catch (Exception ex) { PausedRecommendationNotice.Text = "收藏失败：" + Friendly(ex); RecordInteractionFailure("op246", ex); }
            finally { _pausedActionBusy = false; FocusPausedAction(2); }
        }

        // Sessions signed in by older builds came from the plain website QR;
        // Bilibili refuses their likes/coins (-403). One desktop-client QR
        // scan with the same account replaces the session; a different
        // account or a failed scan keeps the existing login untouched.
        private async Task<bool> EnsureInteractionAuthorizedAsync()
        {
            if (!_api.NeedsInteractionReauthorization) return true;
            if (_dialogOpen) return false;
            bool accepted = false;
            var dialog = new ContentDialog
            {
                Title = "需要重新授权一次",
                Content = new TextBlock
                {
                    Text = "点赞、投币和收藏需要用手机 B 站重新扫码授权一次（只需一次）。\n请使用当前账号“" + (_api.CurrentUser?.Name ?? "") + "”扫码；换了账号或授权没完成，都会保留现在的登录。",
                    TextWrapping = TextWrapping.Wrap, MaxWidth = 520
                },
                PrimaryButtonText = "扫码授权", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary,
                RequestedTheme = ElementTheme.Dark, RequiresPointer = RequiresPointer.Never
            };
            dialog.PrimaryButtonClick += (s, e) => accepted = true;
            await ShowPausedDialogAsync(dialog);
            if (!accepted) return false;
            _api.BeginReauthorization();
            try { await ShowLoginAsync(); }
            finally { _api.EndReauthorization(); }
            bool done = !_api.NeedsInteractionReauthorization;
            PausedRecommendationNotice.Text = done ? "授权完成，可以点赞、投币和收藏了。" : "授权未完成，已保留原来的登录。";
            return done;
        }

        private void RecordInteractionFailure(string operation, Exception ex)
        {
            PlaybackDiagnosticsLog.Record("interaction-failed op=" + operation + " hresult=" + ex.HResult.ToString("X8")
                + " " + _api.LastInteractionDiagnostic + " " + _api.FingerprintDiagnostic);
        }

        private async Task ShowPausedDialogAsync(ContentDialog dialog)
        {
            dialog.Closed += (s, e) => _lastBack = DateTimeOffset.UtcNow;
            _dialogOpen = true; _activeGeneralDialog = dialog; DisablePointerCursor();
            try { await dialog.ShowAsync(); }
            catch (Exception ex) { PausedRecommendationNotice.Text = "选项未能打开：" + Friendly(ex); }
            finally { _activeGeneralDialog = null; _dialogOpen = false; }
        }
    }
}
