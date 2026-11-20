using System;
using System.Collections.Generic;
using System.Threading.Tasks;
namespace BiliXbox.Controls
{
    public enum Visibility { Visible,Collapsed }
    public class Control { public Visibility Visibility; public bool IsEnabled=true; }
    public sealed class Button:Control { }
    public static class FocusManager { public static Control Current; public static object GetFocusedElement()=>Current; }
    public sealed partial class LiveHubView
    {
        private enum Pane { None,Chat,Quality,Gifts,GiftAmount,GiftConfirm,DanmakuSettings }
        private bool _editing,_categoryOpen,OwnsFocus;
        private bool IsPlayingRoom=true;
        private Pane _pane;
        private object _quote;
        private int _paneGeneration;
        private readonly Control _chrome=new Control{Visibility=Visibility.Collapsed},_drawer=new Control();
        private readonly Button _play=new Button(),_chatButton=new Button(),_qualityButton=new Button(),_giftButton=new Button(),_danmakuSettingsButton=new Button();
        private readonly Control _roomInput=new Control(),_chatInput=new Control();
        private readonly List<Room> _roomItems=new List<Room>();
        private readonly Dictionary<Button,Action> _actions=new Dictionary<Button,Action>();
        private int restores,actions,leaves;
        private void ReconcileScrollFocus() { }
        private void WakeChrome()=>_chrome.Visibility=Visibility.Visible;
        private void RestoreFocus() { ++restores;WakeChrome();OwnsFocus=true;Focus(_play); }
        private void Focus(Control c)=>FocusManager.Current=c;
        private void FocusFirstRow() { }
        private void CloseLiveCategories()=>_categoryOpen=false;
        private void OpenPane(Pane pane)=>_pane=pane;
        private void LeaveRoom() { ++leaves;IsPlayingRoom=false; }
        private int RoomIndex(Control control)=>-1;
        private Task OpenRoomAsync(long id)=>Task.CompletedTask;
        private Task EditAsync(bool room)=>Task.CompletedTask;
        private void Run(Func<Task> action)=>action().GetAwaiter().GetResult();
        /* METHODS */
        private sealed class Room { internal long Id; }
        private static int checks;
        private static void Check(bool ok,string reason) {++checks;if(!ok)throw new Exception(reason);}
        private LiveHubView() {_actions[_play]=()=>++actions;}
        public static string RunTests()
        {
            foreach(bool owns in new[]{false,true})
            {
                var view=new LiveHubView{OwnsFocus=owns};var stale=new Button();view._actions[stale]=()=>view.LeaveRoom();FocusManager.Current=stale;
                view.Confirm(false);
                Check(view._chrome.Visibility==Visibility.Visible&&view.restores==1&&FocusManager.Current==view._play,"A must recover hidden chrome even when focus moved away");
                Check(view.actions==0&&view.leaves==0,"wake press cannot activate stale/hidden button");
                view.Confirm(true);Check(view.actions==0,"held A cannot activate recovered play button");
                view.Confirm(false);Check(view.actions==1,"next deliberate A executes visible focused action once");
                view.Back();Check(view._chrome.Visibility==Visibility.Collapsed&&view.leaves==0,"B hides visible controls first");
                view.Confirm(false);Check(view._chrome.Visibility==Visibility.Visible&&view.actions==1,"A can restore controls after B hides them");
                view.Back();view.Back();Check(view.leaves==1,"B from already-hidden controls retains room return behavior");
            }
            var visible=new LiveHubView{OwnsFocus=false};visible._chrome.Visibility=Visibility.Visible;visible.Confirm(false);
            Check(visible.restores==1&&visible.actions==0,"visible chrome with lost native focus recovers without activation");
            var editing=new LiveHubView{_editing=true};editing.Confirm(false);editing.Back();
            Check(editing.restores==0&&editing.leaves==0,"native keyboard keeps A/B ownership");
            var repeat=new LiveHubView();repeat.Confirm(true);Check(repeat.restores==0,"repeat alone does not wake controls");
            var pane=new LiveHubView{_pane=Pane.DanmakuSettings,OwnsFocus=true};pane.Back();
            Check(pane._pane==Pane.None&&FocusManager.Current==pane._danmakuSettingsButton&&pane.leaves==0,"B from settings restores its bottom-bar button");
            return "PASS: "+checks+" production live chrome A/B, lost-focus and repeat-input assertions.";
        }
    }
}
