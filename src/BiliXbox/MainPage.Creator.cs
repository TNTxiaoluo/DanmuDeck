using System;
using System.Threading.Tasks;
using BiliXbox.Controls;
using BiliXbox.Models;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace BiliXbox
{
    public sealed partial class MainPage
    {
        private bool _creatorVisible, _videoReturnsToCreator;
        private long _creatorMid;
        private CreatorPlaybackBookmark _creatorReturn;

        private sealed class CreatorPlaybackBookmark
        {
            public string Bvid, Title;
            public long Cid;
            public int Quality;
            public TimeSpan Position;
            public bool Playing;
            public AccountLibraryItem Episode;
        }

        private void InitializeCreatorHub()
        {
            CreatorHub.CloseRequested += async (sender, args) => await CloseCreatorAsync();
            CreatorHub.VideoRequested += async (sender, args) =>
            {
                if (args.Video == null || _disposed) return;
                CreatorHub.StopRequests();
                _creatorVisible = false;
                CreatorHub.Visibility = Visibility.Collapsed;
                _videoReturnsToCreator = true;
                await OpenVideoAsync(args.Video.Bvid, args.Video.Title);
            };
            CreatorHub.LoginRequested += async (sender, args) =>
            {
                if (_dialogOpen || _disposed) return;
                await ShowLoginAsync();
                if (_creatorVisible) CreatorHub.RestoreFocus();
            };
            CreatorHub.RequestTextInputAsync = (editor, title, maxLength) => ShowInlineTextInputAsync(editor, false);
            CreatorHub.RequestCommentVoteAsync = SubmitCommentVoteAsync;
            CreatorHub.ImageViewerOpened += (sender, args) => { if (!_disposed) _dialogOpen = true; };
            CreatorHub.ImageViewerClosed += (sender, args) =>
            {
                _dialogOpen = false; _lastBack = DateTimeOffset.UtcNow;
                if (!_disposed) DisablePointerCursor();
            };
        }

        private async void VideoCreator_Click(object sender, RoutedEventArgs args)
        {
            if (IgnorePlayerClick() || _video == null) return;
            if (_video.OwnerMid <= 0) { SetStatus("当前视频没有提供 UP 主主页信息。"); return; }
            _creatorReturn = CapturePlaybackBookmark();
            _creatorMid = _video.OwnerMid;
            _videoReturnsToCreator = false;
            ShowCreatorSurface();
            await CreatorHub.OpenAsync(_api, _creatorMid, _video.Owner, _video.OwnerAvatarUrl);
        }

        private void ShowCreatorSurface()
        {
            CancelManualGamepadScroll();
            StopSectionMotion();
            _creatorVisible = true;
            _wantsPlayback = _resumeAfterOpen = false;
            _media.Pause();
            _ = ResetPlaybackDisplayAsync();
            UpdatePlaybackUi();
            Header.Visibility = Footer.Visibility = HomeView.Visibility = WatchView.Visibility = Visibility.Collapsed;
            Root.Padding = new Thickness(0);
            GamepadFocusOutline.Visibility = Visibility.Collapsed;
            CreatorHub.Visibility = Visibility.Visible;
            DisablePointerCursor();
            CreatorHub.RestoreFocus();
        }

        private async Task CloseCreatorAsync()
        {
            if (!_creatorVisible) return;
            CreatorHub.StopRequests();
            _creatorVisible = false;
            _videoReturnsToCreator = false;
            CreatorHub.Visibility = Visibility.Collapsed;
            var bookmark = _creatorReturn;
            _creatorReturn = null;
            if (bookmark == null) { GoHome(); return; }
            if (_watching && _video?.Bvid == bookmark.Bvid && _currentCid == bookmark.Cid && _media.Source != null)
            {
                WatchView.Visibility = Visibility.Visible;
                _wantsPlayback = _resumeAfterOpen = bookmark.Playing;
                var source = _media.Source;
                int generation = _partGeneration;
                if (bookmark.Playing) await PlayWithDisplayAsync(); else _media.Pause();
                if (_disposed || !_watching || _creatorVisible || _libraryVisible || source != _media.Source
                    || generation != _partGeneration) return;
                UpdatePlaybackUi();
                ShowControls();
                RestoreNavigationFocus();
            }
            else await OpenVideoAsync(bookmark.Bvid, bookmark.Title, bookmark);
        }

        private void ReturnToCreatorFromVideo()
        {
            _videoReturnsToCreator = false;
            GoHome();
            ShowCreatorSurface();
        }

        private void ClearCreatorNavigation()
        {
            _videoReturnsToCreator = false;
            _creatorReturn = null;
            _creatorMid = 0;
            if (!_creatorVisible) return;
            CreatorHub.StopRequests();
            _creatorVisible = false;
            CreatorHub.Visibility = Visibility.Collapsed;
        }
    }
}
