using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Models;
using Windows.ApplicationModel;
using Windows.Media;
using Windows.Storage;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace BiliXbox
{
    public sealed partial class MainPage
    {
        private VideoEndMode _endMode;
        private bool _backgroundAudio, _inBackground, _splitComments;
        private bool _addingWatchLater;
        private CancellationTokenSource _subtitleCts;
        private IReadOnlyList<SubtitleCue> _subtitleCues = Array.Empty<SubtitleCue>();
        private IReadOnlyList<AccountLibraryItem> _episodeChoices = Array.Empty<AccountLibraryItem>();
        private readonly List<Control> _experienceOptions = new List<Control>();

        private void InitializeViewingExperience()
        {
            InitializeVolume();
            InitializePlaybackSpeed();
            AccountLibrary.ConfigureActivity((editor, title, maxLength) => ShowInlineTextInputAsync(editor, false), SubmitCommentVoteAsync);
            var values = ApplicationData.Current.LocalSettings.Values;
            int mode; if (int.TryParse(values["videoEndMode"] as string, out mode) && mode >= 0 && mode <= 2) _endMode = (VideoEndMode)mode;
            _backgroundAudio = values["backgroundAudio"] as bool? == true;
            _splitComments = values["splitComments"] as bool? == true;
            AddExperienceButton("播放结束", () => {
                _endMode = (VideoEndMode)(((int)_endMode + 1) % 3);
                values["videoEndMode"] = ((int)_endMode).ToString(); UpdateExperienceButtons();
            });
            AddExperienceButton("后台音频", () => {
                _backgroundAudio = !_backgroundAudio; values["backgroundAudio"] = _backgroundAudio; UpdateExperienceButtons();
            });
            ExperienceOptions.Children.Add(new TextBlock { Text = "后台音频开启后，回到 Xbox 首页或切换应用继续收听。系统退出或终止应用后停止。", TextWrapping = TextWrapping.Wrap, FontSize = 14 });
            AddExperienceButton("评论布局", () => { _splitComments = !_splitComments; values["splitComments"] = _splitComments; UpdateExperienceButtons(); ApplyCommentLayout(); });
            AddExperienceButton("选集", async () => await ShowEpisodeChoicesAsync());
            AddExperienceButton("加入稍后再看", async () => {
                if (_addingWatchLater || _video == null || _playingEpisodeItem != null) return;
                if (!_api.IsLoggedIn) { await ShowLoginAsync(); return; }
                _addingWatchLater = true;
                try { await _api.AddWatchLaterAsync(_video.Aid, _watchCts.Token); AccountLibrary.InvalidateWatchLater(); SetStatus("已加入稍后再看"); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { SetStatus("加入稍后再看未确认，请先查看列表再重试：" + Friendly(ex)); }
                finally { _addingWatchLater = false; }
            });
            UpdateExperienceButtons();
            Application.Current.EnteredBackground += Viewing_EnteredBackground;
            Application.Current.LeavingBackground += Viewing_LeavingBackground;
            InitializeBackgroundMemoryHandling();
            // Automatic SMTC integration is required for single-process UWP background audio.
            _media.CommandManager.IsEnabled = true;
            _media.CommandManager.PauseReceived += (s,e) => {
                e.Handled = true;
                _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => { if (_disposed) return; _wantsPlayback = _resumeAfterOpen = false; _media.Pause(); ResetPlaybackBuffering(); });
            };
            _media.CommandManager.PlayReceived += (s,e) => {
                e.Handled = true;
                _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => { if (_disposed || _media.Source == null || !_watching) return;
                    _wantsPlayback = _resumeAfterOpen = true; if (_inBackground) _media.Play(); else _ = PlayWithDisplayAsync(); });
            };
            _media.AudioCategory = Windows.Media.Playback.MediaPlayerAudioCategory.Media;
        }
        private void AddExperienceButton(string label, Action action)
        {
            var button = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Stretch,
                Style = (Style)Application.Current.Resources["TvRowButtonStyle"], Tag = action };
            button.Click += (s, e) => { if (!IgnorePlayerClick()) action(); }; _experienceOptions.Add(button); ExperienceOptions.Children.Add(button);
        }
        private void UpdateExperienceButtons()
        {
            ((Button)_experienceOptions[0]).Content = "播放结束：" + (_endMode == VideoEndMode.Stop ? "暂停，不循环" : _endMode == VideoEndMode.NextPart ? "连续播放分 P / 剧集" : "循环当前视频");
            ((Button)_experienceOptions[1]).Content = "后台音频：" + (_backgroundAudio ? "开" : "关");
            ((Button)_experienceOptions[2]).Content = "评论布局：" + (_splitComments ? "视频与评论并排" : "默认悬浮");
            ((Button)_experienceOptions[3]).Content = "选集 / 分 P";
        }
        private void ApplyCommentLayout()
        {
            bool split = _splitComments && _playerUi.Drawer == Controls.PlayerDrawer.Comments;
            double width = split ? Math.Max(360, WatchView.ActualWidth / 3) : 440;
            CommentSide.Width = ReplySide.Width = width;
            Player.Margin = DanmakuMaskHost.Margin = new Thickness(0, 0, split ? width : 0, 0);
            SubtitleLayer.Margin = new Thickness(32, 0, split ? width + 32 : 32, _playerUi.ControlsVisible ? 190 : 40);
            DrawerBackdrop.Visibility = split ? Visibility.Collapsed : _playerUi.Drawer == Controls.PlayerDrawer.None ? Visibility.Collapsed : Visibility.Visible;
        }
        private void DisposeViewingExperience()
        {
            Cancel(ref _subtitleCts);
            Application.Current.EnteredBackground -= Viewing_EnteredBackground;
            Application.Current.LeavingBackground -= Viewing_LeavingBackground;
            DisposeBackgroundMemoryHandling();
        }
        private async Task<bool> AdvanceAfterEndAsync(object source)
        {
            if (_endMode == VideoEndMode.Loop) { await LoadPartAsync(_currentCid); return true; }
            if (_endMode == VideoEndMode.NextPart)
            {
                if (_playingEpisodeItem != null && !_episodeChoices.Any(item => item.EpisodeId == _playingEpisodeItem.EpisodeId))
                {
                    var episode = _playingEpisodeItem;
                    var season = await _api.GetSeasonDetailAsync(episode.SeasonId, episode.EpisodeId, _watchCts.Token);
                    if (_media.Source != source || episode != _playingEpisodeItem || !_watching) return true;
                    _episodeChoices = season.Episodes;
                }
                if (_playingEpisodeItem != null && _episodeChoices.Count > 0)
                {
                    int index = _episodeChoices.ToList().FindIndex(item => item.EpisodeId == _playingEpisodeItem.EpisodeId);
                    if (index >= 0 && index + 1 < _episodeChoices.Count) { await PlaySelectedEpisodeAsync(_episodeChoices[index + 1]); return true; }
                }
                else if (_video != null)
                {
                    int index = _video.Parts.ToList().FindIndex(item => item.Cid == _currentCid);
                    if (index >= 0 && index + 1 < _video.Parts.Count) { await LoadPartAsync(_video.Parts[index + 1].Cid); return true; }
                }
            }
            if (_videoReturnsToCreator && CreatorHub.ContinuousPlayback)
            {
                var video = await CreatorHub.NextPlaylistVideoAsync(_video?.Bvid, _watchCts.Token);
                if (_media.Source != source || !_watching || !_videoReturnsToCreator) return true;
                if (video != null) { await OpenVideoAsync(video.Bvid, video.Title); return true; }
            }
            return false;
        }
        private async Task ShowEpisodeChoicesAsync(AccountLibraryItem season = null)
        {
            if (_dialogOpen) return;
            _dialogOpen = true;
            var watch = _watchCts; var video = _video;
            var dialog = new ContentDialog { Title = "选集 / 分 P", CloseButtonText = "取消", PrimaryButtonText = "播放", RequiresPointer = RequiresPointer.Never,
                XYFocusKeyboardNavigation = Windows.UI.Xaml.Input.XYFocusKeyboardNavigationMode.Enabled };
            var list = new ListView { MaxHeight = 480, MinWidth = 520, SelectionMode = ListViewSelectionMode.Single,
                IsItemClickEnabled = true, DisplayMemberPath = "Title" };
            bool confirmed = false;
            list.ItemClick += (s, e) => { list.SelectedItem = e.ClickedItem; confirmed = true; dialog.Hide(); };
            _activeGeneralDialog = dialog;
            try
            {
                var episode = season ?? _playingEpisodeItem;
                if (episode != null)
                {
                    var data = season == null ? await _api.GetSeasonDetailAsync(episode.SeasonId, episode.EpisodeId, watch?.Token ?? CancellationToken.None)
                        : await _api.GetPublicSeasonDetailAsync(episode.SeasonId, episode.EpisodeId, watch?.Token ?? CancellationToken.None);
                    _episodeChoices = data.Episodes; list.ItemsSource = data.Episodes;
                }
                else list.ItemsSource = _video?.Parts;
                if (list.Items.Count == 0) { SetStatus("此内容暂无可用分集"); return; }
                list.SelectedIndex = Math.Max(0, episode == null ? _video.Parts.ToList().FindIndex(p => p.Cid == _currentCid)
                    : _episodeChoices.ToList().FindIndex(p => p.EpisodeId == episode.EpisodeId));
                dialog.Content = list; dialog.Opened += (s,e) => list.Focus(FocusState.Keyboard);
                var result = await dialog.ShowAsync();
                if (result != ContentDialogResult.Primary && !confirmed) return;
                if (season == null && (watch != _watchCts || video != _video)) return;
                var selected = list.SelectedItem;
                _dialogOpen = false;
                if (selected is AccountLibraryItem item) {
                    if (season != null) _videoReturnsToCreator = _videoReturnsToLibrary = false;
                    await PlaySelectedEpisodeAsync(item);
                }
                else if (selected is VideoPart part) { CloseDrawer(); await LoadPartAsync(part.Cid); }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { SetStatus("选集失败：" + Friendly(ex)); }
            finally { _activeGeneralDialog = null; _dialogOpen = false; RestoreNavigationFocus(); }
        }
        private async Task PlaySelectedEpisodeAsync(AccountLibraryItem item)
        {
            if (item == null || !item.IsPlayable) { SetStatus(item?.UnavailableReason ?? "此分集不可播放"); return; }
            if (!_api.IsLoggedIn) { await ShowLoginAsync(); if (!_api.IsLoggedIn) return; }
            await OpenVideoAsync(item.Bvid, item.Title, new CreatorPlaybackBookmark { Bvid = item.Bvid, Title = item.Title, Episode = item,
                Cid = item.Cid, Quality = _preferredQuality, Playing = true });
        }

        private async void Subtitle_Click(object sender, RoutedEventArgs args)
        {
            if (_dialogOpen || _video == null || !_watching) return;
            _dialogOpen = true; Cancel(ref _subtitleCts);
            var owner = _subtitleCts = CancellationTokenSource.CreateLinkedTokenSource(_watchCts.Token);
            long cid = _currentCid;
            var dialog = new ContentDialog { Title = "字幕", CloseButtonText = "取消", PrimaryButtonText = "选择", RequiresPointer = RequiresPointer.Never,
                XYFocusKeyboardNavigation = Windows.UI.Xaml.Input.XYFocusKeyboardNavigationMode.Enabled };
            _activeGeneralDialog = dialog;
            try
            {
                var tracks = await _api.GetSubtitlesAsync(_video, cid, owner.Token);
                var choices = new List<SubtitleTrack> { new SubtitleTrack { Label = "关闭字幕" } }; choices.AddRange(tracks);
                if (tracks.Count == 0) SetStatus(_api.IsLoggedIn ? "B站未提供此分 P 的可用字幕" : "此视频没有可用字幕，登录后可重试");
                var list = new ListView { ItemsSource = choices, DisplayMemberPath = "Label", SelectedIndex = 0, MaxHeight = 440, MinWidth = 420, IsItemClickEnabled = true };
                bool confirmed = false; list.ItemClick += (s,e) => { list.SelectedItem = e.ClickedItem; confirmed = true; dialog.Hide(); };
                dialog.Content = list; dialog.Opened += (s,e) => list.Focus(FocusState.Keyboard);
                var result = await dialog.ShowAsync();
                if (result != ContentDialogResult.Primary && !confirmed) return;
                var track = list.SelectedItem as SubtitleTrack;
                var cues = string.IsNullOrEmpty(track?.Url) ? Array.Empty<SubtitleCue>() : await _api.GetSubtitleCuesAsync(track, owner.Token);
                if (owner == _subtitleCts && cid == _currentCid && !owner.IsCancellationRequested) { _subtitleCues = cues; RenderSubtitle(); }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { SetStatus("字幕读取失败：" + Friendly(ex)); }
            finally { _activeGeneralDialog = null; _dialogOpen = false; RestoreNavigationFocus(); }
        }
        private void ResetSubtitles()
        {
            Cancel(ref _subtitleCts); _subtitleCues = Array.Empty<SubtitleCue>(); SubtitleText.Text = ""; SubtitleLayer.Visibility = Visibility.Collapsed;
        }
        private void RenderSubtitle()
        {
            double seconds = _media.PlaybackSession.Position.TotalSeconds;
            // Binary search the most recent cue, then include overlapping cues in a bounded window.
            int lo = 0, hi = _subtitleCues.Count;
            while (lo < hi) { int mid = lo + (hi - lo) / 2; if (_subtitleCues[mid].Start <= seconds) lo = mid + 1; else hi = mid; }
            SubtitleText.Text = string.Join("\n", _subtitleCues.Skip(Math.Max(0, lo - 8)).Take(Math.Min(8, lo)).Where(cue => cue.Start <= seconds && cue.End > seconds).Select(cue => cue.Text));
            SubtitleLayer.Visibility = string.IsNullOrEmpty(SubtitleText.Text) ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
