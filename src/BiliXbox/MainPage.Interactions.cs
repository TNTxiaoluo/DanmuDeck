using System;
using System.Threading.Tasks;
using BiliXbox.Controls;
using Windows.UI.Xaml;

namespace BiliXbox
{
    public sealed partial class MainPage
    {
        private void InitializeVideoInteractions()
        {
            InteractionSide.Initialize(_api);
            InteractionSide.CloseRequested += (sender, args) =>
            {
                if (_playerUi.Drawer == PlayerDrawer.Interactions) CloseDrawer();
            };
            InteractionSide.CreatorRequested += (sender, args) =>
            {
                if (_disposed || !_watching || _video == null || _playerUi.Drawer != PlayerDrawer.Interactions) return;
                CloseDrawer();
                // Reuse the same profile, upload list, bookmark and return path
                // as the avatar/name entry in the player's header.
                _dispatchingPlayerAction = true;
                try { VideoCreator_Click(VideoOwnerButton, new RoutedEventArgs()); }
                finally { _dispatchingPlayerAction = false; }
            };
            InteractionSide.LoginRequested += async () =>
            {
                var watch = _watchCts;
                long aid = _video?.Aid ?? 0;
                if (_dialogOpen || _disposed || aid <= 0 || watch == null) return;
                await ShowLoginAsync();
                if (!_disposed && _watching && _watchCts == watch && !watch.IsCancellationRequested
                    && _video?.Aid == aid && _playerUi.Drawer == PlayerDrawer.Interactions)
                    await ShowVideoInteractionsAsync();
            };
            InteractionSide.InteractionChanged += (sender, args) => AccountLibrary.InvalidateFavoriteContent();
        }

        private async void Interactions_Click(object sender, RoutedEventArgs args)
        {
            if (IgnorePlayerClick()) return;
            await ShowVideoInteractionsAsync();
        }

        private async Task ShowVideoInteractionsAsync()
        {
            if (_disposed || !_watching || _video == null || _video.Aid <= 0 || _watchCts == null
                || _watchCts.IsCancellationRequested || _creatorVisible || _libraryVisible) return;
            var watch = _watchCts;
            var video = _video;
            CancelGamepadListFocus();
            _playerInput.SelectButton(Array.IndexOf(PlayerButtons, InteractionButton), PlayerButtons.Length);
            _playerUi.Open(PlayerDrawer.Interactions, DateTimeOffset.UtcNow);
            ApplyPlayerUi();
            try
            {
                await InteractionSide.ShowAsync(video.Aid, video.OwnerMid, video.Owner, watch.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!_disposed && _watchCts == watch && _video == video && _playerUi.Drawer == PlayerDrawer.Interactions)
                    SetStatus("互动面板未能打开：" + Friendly(ex));
            }
        }

        private void SuspendVideoInteractions()
        {
            InteractionSide.StopRequests();
            if (_playerUi.Drawer != PlayerDrawer.Interactions) return;
            _playerUi.Close(DateTimeOffset.UtcNow);
            ApplyPlayerUi();
        }
    }
}
