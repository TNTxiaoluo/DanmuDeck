using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using BiliXbox.Models;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace BiliXbox
{
    public sealed partial class MainPage
    {
        public ObservableCollection<UserSearchItem> Users { get; } = new ObservableCollection<UserSearchItem>();
        private GridView HomeResultGrid => UserGrid.Visibility == Visibility.Visible ? UserGrid : VideoGrid;

        private void User_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.ItemContainer == null) return;
            var user = args.InRecycleQueue ? null : args.Item as UserSearchItem;
            Windows.UI.Xaml.Automation.AutomationProperties.SetName(args.ItemContainer, user?.Name ?? "");
            Windows.UI.Xaml.Automation.AutomationProperties.SetHelpText(args.ItemContainer, user == null ? "" : "按 A 打开用户主页");
        }

        private async void User_ItemClick(object sender, ItemClickEventArgs args)
        {
            if (IgnorePlayerClick() || !(args.ClickedItem is UserSearchItem user)) return;
            await OpenUserHomeAsync(user);
        }

        private async void OpenGamepadHomeUser(UserSearchItem user) => await OpenUserHomeAsync(user);

        private async Task OpenUserHomeAsync(UserSearchItem user)
        {
            if (_disposed || user == null || user.Mid <= 0) return;
            _creatorReturn = null;
            _videoReturnsToCreator = false;
            _creatorMid = user.Mid;
            ShowCreatorSurface();
            try { await CreatorHub.OpenAsync(_api, user.Mid, user.Name, user.AvatarUrl); }
            catch (Exception ex) { if (!_disposed) SetStatus("用户主页未能打开：" + Friendly(ex)); }
        }
    }
}
