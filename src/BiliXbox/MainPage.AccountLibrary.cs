using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Controls;
using BiliXbox.Models;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace BiliXbox
{
    public sealed partial class MainPage
    {
        private bool _libraryVisible, _videoReturnsToLibrary;
        private long _libraryAccountMid;
        private LibraryReturnState _libraryReturn;
        private AccountLibraryItem _playingEpisodeItem;

        private sealed class LibraryReturnState
        {
            public bool CreatorVisible, VideoReturnsToCreator;
            public CreatorPlaybackBookmark Playback;
        }

        private CreatorPlaybackBookmark CapturePlaybackBookmark() => !_watching ? null
            : new CreatorPlaybackBookmark
            {
                Bvid = _video?.Bvid ?? _requestedBvid, Title = _video?.Title ?? _requestedTitle, Cid = _currentCid,
                Quality = _playback?.QualityId ?? _preferredQuality,
                Position = _restorePosition ?? _media.PlaybackSession.Position, Playing = _wantsPlayback,
                Episode = _playingEpisodeItem
            };

        private void InitializeAccountLibrary()
        {
            AccountLibrary.Initialize(_api);
            AccountLibrary.CloseRequested += async (sender, args) => await CloseAccountLibraryAsync();
            AccountLibrary.SignOutRequested += async (sender, args) => await SignOutAccountAsync();
            AccountLibrary.OpenItemRequested += OpenLibraryItemAsync;
        }

        private async Task ShowAccountLibraryAsync()
        {
            if (_disposed || _dialogOpen || !_api.IsLoggedIn) return;
            if (_libraryVisible) { AccountLibrary.RestoreFocus(); return; }
            var historyFlush = FlushPlaybackHistoryAsync();
            bool returning = _videoReturnsToLibrary && _libraryReturn != null;
            if (returning) ReturnToAccountLibraryFromVideo(false);
            else
            {
                _libraryReturn = new LibraryReturnState
                {
                    CreatorVisible = _creatorVisible, VideoReturnsToCreator = _videoReturnsToCreator,
                    Playback = CapturePlaybackBookmark()
                };
                _libraryAccountMid = CurrentAccountMid;
                ShowAccountLibrarySurface(false);
            }
            var owner = _libraryReturn;
            long accountMid = _libraryAccountMid;
            AccountLibrary.IsEnabled = false;
            try
            {
                // Show the surface immediately, but do not read an empty cached
                // history before the last watched position has been submitted.
                await historyFlush;
                if (_disposed || !_libraryVisible || !ReferenceEquals(owner, _libraryReturn)
                    || accountMid != CurrentAccountMid) return;
                AccountLibrary.IsEnabled = true;
                if (returning) AccountLibrary.RestoreFocus();
                else await AccountLibrary.ShowAsync(_api.CurrentUser);
            }
            catch (Exception ex)
            {
                if (!_disposed && _libraryVisible && ReferenceEquals(owner, _libraryReturn)
                    && accountMid == CurrentAccountMid) SetStatus("个人中心未能打开：" + Friendly(ex));
            }
            finally
            {
                if (!_disposed && ReferenceEquals(owner, _libraryReturn)) AccountLibrary.IsEnabled = true;
            }
        }

        private void ShowAccountLibrarySurface(bool restoreFocus = true)
        {
            CancelManualGamepadScroll();
            StopSectionMotion();
            HideLiveSection();
            _libraryVisible = true;
            _creatorVisible = false;
            CreatorHub.StopRequests();
            _wantsPlayback = _resumeAfterOpen = false;
            _media.Pause();
            _ = ResetPlaybackDisplayAsync();
            UpdatePlaybackUi();
            Header.Visibility = Footer.Visibility = HomeView.Visibility = WatchView.Visibility = CreatorHub.Visibility = Visibility.Collapsed;
            Root.Padding = new Thickness(0);
            GamepadFocusOutline.Visibility = Visibility.Collapsed;
            AccountLibrary.Visibility = Visibility.Visible;
            DisablePointerCursor();
            if (restoreFocus) AccountLibrary.RestoreFocus();
        }

        private async Task OpenLibraryItemAsync(AccountLibraryItem item)
        {
            if (_disposed || !_libraryVisible || item == null || CurrentAccountMid != _libraryAccountMid) return;
            if (!item.IsPlayable)
            { SetStatus(string.IsNullOrWhiteSpace(item.UnavailableReason) ? "此条内容暂不可播放。" : item.UnavailableReason); return; }
            AccountLibrary.StopRequests();
            _libraryVisible = false;
            AccountLibrary.Visibility = Visibility.Collapsed;
            _videoReturnsToCreator = false;
            _videoReturnsToLibrary = true;
            var bookmark = new CreatorPlaybackBookmark
            {
                Bvid = item.Bvid, Title = item.Title, Cid = item.Cid,
                Quality = _preferredQuality, Position = TimeSpan.FromSeconds(Math.Min(604800, Math.Max(0, item.ProgressSeconds))),
                Playing = true, Episode = item.EpisodeId > 0 ? item : null
            };
            await OpenVideoAsync(item.Bvid, item.Title, bookmark);
        }

        private void ReturnToAccountLibraryFromVideo(bool restoreFocus = true)
        {
            _videoReturnsToLibrary = false;
            GoHome();
            if (!_api.IsLoggedIn || CurrentAccountMid != _libraryAccountMid)
            { ClearAccountLibraryNavigation(true); return; }
            ShowAccountLibrarySurface(restoreFocus);
        }

        private async Task CloseAccountLibraryAsync()
        {
            if (!_libraryVisible) return;
            var previous = _libraryReturn;
            ClearAccountLibraryNavigation();
            if (previous?.CreatorVisible == true)
            {
                GoHome();
                _videoReturnsToCreator = previous.VideoReturnsToCreator;
                ShowCreatorSurface();
                return;
            }
            var bookmark = previous?.Playback;
            if (bookmark == null) { GoHome(); return; }
            _videoReturnsToCreator = previous.VideoReturnsToCreator;
            if (_watching && _video?.Bvid == bookmark.Bvid && _currentCid == bookmark.Cid
                && (_playingEpisodeItem?.EpisodeId ?? 0) == (bookmark.Episode?.EpisodeId ?? 0) && _media.Source != null)
            {
                WatchView.Visibility = Visibility.Visible;
                Header.Visibility = Footer.Visibility = HomeView.Visibility = CreatorHub.Visibility = Visibility.Collapsed;
                Root.Padding = new Thickness(0);
                _wantsPlayback = _resumeAfterOpen = bookmark.Playing;
                var source = _media.Source;
                int generation = _partGeneration;
                if (bookmark.Playing) await PlayWithDisplayAsync(); else _media.Pause();
                if (_disposed || !_watching || _libraryVisible || _creatorVisible || source != _media.Source
                    || generation != _partGeneration) return;
                UpdatePlaybackUi(); ShowControls(); RestoreNavigationFocus();
            }
            else await OpenVideoAsync(bookmark.Bvid, bookmark.Title, bookmark);
        }

        private void ClearAccountLibraryNavigation(bool clearAccount = false)
        {
            AccountLibrary.IsEnabled = true;
            _libraryVisible = _videoReturnsToLibrary = false;
            _libraryReturn = null;
            AccountLibrary.StopRequests();
            AccountLibrary.Visibility = Visibility.Collapsed;
            if (clearAccount) { _libraryAccountMid = 0; AccountLibrary.ClearAccount(); }
        }

        private async Task SignOutAccountAsync()
        {
            if (_disposed || _dialogOpen || !_api.IsLoggedIn) return;
            _dialogOpen = true;
            var dialog = new ContentDialog
            {
                Title = "退出登录", Content = "退出此设备的 Bilibili 账号？",
                PrimaryButtonText = "退出登录", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close,
                RequiresPointer = RequiresPointer.Never,
                XYFocusKeyboardNavigation = Windows.UI.Xaml.Input.XYFocusKeyboardNavigationMode.Enabled
            };
            _activeGeneralDialog = dialog;
            dialog.Closed += (s, e) => _lastBack = DateTimeOffset.UtcNow;
            try
            {
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
                await _api.LogoutAsync();
                ClearAccountLibraryNavigation(true);
                ClearCreatorNavigation();
                GoHome(); UpdateAccount();
                SetStatus("已退出登录并清除本地凭证");
                await RefreshHomeForAccountAsync();
            }
            catch (Exception ex) { if (!_disposed) SetStatus("退出登录失败：" + Friendly(ex)); }
            finally { _activeGeneralDialog = null; _dialogOpen = false; RestoreNavigationFocus(); }
        }

        private Task<PlaybackInfo> GetCurrentPlaybackAsync(VideoDetail video, int quality, CancellationToken token, PlaybackAudioMode? audioMode = null)
            => _playingEpisodeItem?.EpisodeId > 0
                ? _api.GetEpisodePlaybackAsync(_playingEpisodeItem.EpisodeId, quality, token, audioMode ?? _preferredAudioMode)
                : _api.GetPlaybackAsync(video, quality, token, audioMode ?? _preferredAudioMode);

        private static VideoDetail EpisodeVideoDetail(AccountLibraryItem item)
        {
            if (item == null || item.EpisodeId <= 0 || item.Aid <= 0 || item.Cid <= 0)
                throw new InvalidOperationException("剧集未提供完整的播放信息，请返回个人中心刷新重试。");
            return new VideoDetail
            {
                Aid = item.Aid, Bvid = item.Bvid, Cid = item.Cid, Title = item.Title,
                CoverUrl = item.CoverUrl, Owner = item.Owner, OwnerMid = item.OwnerMid,
                OwnerAvatarUrl = item.OwnerAvatarUrl, PlayText = item.PlayText, DurationText = item.DurationText,
                Parts = new[] { new VideoPart { Cid = item.Cid, Title = item.Title, Page = 1 } }
            };
        }
    }
}
