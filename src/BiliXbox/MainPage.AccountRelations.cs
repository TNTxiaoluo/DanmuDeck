using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace BiliXbox
{
    public sealed partial class MainPage
    {
        private void InitializeAccountRelations()
        {
            AccountLibrary.ProfileRequested += async (sender, args) =>
            {
                var user = _api.CurrentUser;
                if (user != null) await OpenCreatorFromLibraryAsync(user.Mid, user.Name, user.AvatarUrl);
            };
            AccountLibrary.RelationsRequested += async followers => await ShowRelationsAsync(followers);
        }

        private bool _creatorReturnsToLibrary;

        // A creator page opened from the account page (own space, or someone
        // from the following / follower list) returns to the account page.
        private async Task OpenCreatorFromLibraryAsync(long mid, string name, string avatar)
        {
            if (_disposed || _dialogOpen || mid <= 0 || !_libraryVisible) return;
            _libraryVisible = false;
            AccountLibrary.Visibility = Visibility.Collapsed;
            _creatorReturnsToLibrary = true;
            _creatorReturn = null;
            _creatorMid = mid;
            _videoReturnsToCreator = false;
            ShowCreatorSurface();
            try { await CreatorHub.OpenAsync(_api, mid, name, avatar); }
            catch (Exception ex) { if (!_disposed) SetStatus("UP 主页未能打开：" + Friendly(ex)); }
        }

        private bool ReturnCreatorToLibrary()
        {
            if (!_creatorReturnsToLibrary) return false;
            _creatorReturnsToLibrary = false;
            ShowAccountLibrarySurface();
            return true;
        }

        private async Task ShowRelationsAsync(bool followers)
        {
            if (_disposed || _dialogOpen || !_api.IsLoggedIn) return;
            string title = followers ? "我的粉丝" : "我的关注";
            var rows = new System.Collections.Generic.List<Button>();
            var list = new StackPanel { Spacing = 8 };
            var notice = new TextBlock { Text = "正在读取" + title + "…", FontSize = 16, Opacity = .75, TextWrapping = TextWrapping.Wrap,
                Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xF2, 0xF2, 0xF7)) };
            list.Children.Add(notice);
            Models.CreatorProfile chosen = null;
            Button more = null;
            int page = 0;
            bool busy = false;
            var owner = new CancellationTokenSource();
            async Task LoadAsync()
            {
                if (busy) return;
                busy = true;
                try
                {
                    var result = await _api.GetRelationsAsync(followers, page + 1, owner.Token);
                    if (owner.IsCancellationRequested) return;
                    ++page;
                    if (more != null) { list.Children.Remove(more); rows.Remove(more); }
                    foreach (var user in result.Items)
                    {
                        var target = user;
                        string label = user.Name + (string.IsNullOrWhiteSpace(user.Signature) ? "" : "\n" + user.Signature.Replace('\n', ' '));
                        var row = CreateTvPanelRow(label, () => { chosen = target; FinishTvPanel(true); }, true);
                        rows.Add(row); list.Children.Add(row);
                    }
                    notice.Text = result.Total > 0 ? "共 " + result.Total + " 人" : rows.Count == 0 ? "这里还没有人。" : "";
                    if (followers && result.Total > 0 && !result.HasMore && rows.Count < result.Total)
                        notice.Text += "，B 站只开放最近的粉丝列表";
                    if (result.HasMore)
                    {
                        more = more ?? CreateTvPanelRow("加载更多", () => { var _ = LoadAsync(); });
                        rows.Add(more); list.Children.Add(more);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { if (!owner.IsCancellationRequested) notice.Text = title + "暂时无法读取：" + Friendly(ex); }
                finally { busy = false; }
            }
            var load = LoadAsync();
            bool accepted;
            try { accepted = await ShowTvPanelAsync(title, list, rows); }
            finally { owner.Cancel(); owner.Dispose(); }
            if (accepted && chosen != null && !_disposed)
                await OpenCreatorFromLibraryAsync(chosen.Mid, chosen.Name, chosen.AvatarUrl);
        }
    }
}
