// Production category view and room loading with external XAML and API substitutes.
namespace Windows.UI.Xaml
{
    public enum Visibility { Visible, Collapsed }
    public enum GridUnitType { Star }
    public enum TextWrapping { Wrap }
    public enum HorizontalAlignment { Stretch }
    public struct Thickness { public Thickness(double all) { } public Thickness(double a,double b,double c,double d) { } }
    public struct GridLength { public GridLength(double value,GridUnitType unit=GridUnitType.Star) { } public static GridLength Auto; }
    public class Style { }
    public class UIElement { public Visibility Visibility; }
    public sealed class Application
    { public static Application Current=new Application(); public readonly Dictionary<string,object> Resources=new Dictionary<string,object> { ["TvRowButtonStyle"]=new Style() }; }
}
namespace Windows.UI.Xaml.Controls
{
    public class Control : UIElement { public object Tag,Content; public bool IsEnabled=true; public Style Style; public HorizontalAlignment HorizontalAlignment; }
    public sealed class TextBlock : Control { public string Text; public TextWrapping TextWrapping; }
    public sealed class Button : Control { }
    public sealed class ColumnDefinition { public GridLength Width; }
    public sealed class RowDefinition { public GridLength Height; }
    public class Grid : UIElement
    { public object Background;public Thickness Padding;public double RowSpacing,ColumnSpacing; public readonly List<UIElement> Children=new List<UIElement>();public readonly List<RowDefinition> RowDefinitions=new List<RowDefinition>();public readonly List<ColumnDefinition> ColumnDefinitions=new List<ColumnDefinition>(); }
    public enum Orientation { Horizontal }
    public sealed class StackPanel : UIElement { public double Spacing;public Orientation Orientation;public readonly List<UIElement> Children=new List<UIElement>(); }
    public enum ScrollMode { Disabled }
    public enum ScrollBarVisibility { Disabled, Auto }
    public sealed class ScrollViewer : Control { public Thickness Padding;public ScrollMode HorizontalScrollMode;public ScrollBarVisibility HorizontalScrollBarVisibility,VerticalScrollBarVisibility; }
    public sealed class FakeRooms { public readonly List<object> Items=new List<object>(); }
}
namespace Windows.UI.Xaml.Input { public static class FocusManager { public static object Current;public static object GetFocusedElement()=>Current; } }
namespace BiliXbox.Models
{
    public sealed class LiveArea { public long Id,ParentId;public string Name;public IReadOnlyList<LiveArea> Children=Array.Empty<LiveArea>(); }
    public sealed class LiveRoom { public long Id; }
    public sealed class LiveRoomPage { public int Page;public bool HasMore;public IReadOnlyList<LiveRoom> Items=Array.Empty<LiveRoom>(); }
}
namespace BiliXbox.Controls
{
    public enum PlayerDirection { Left,Right,Up,Down }
    public sealed partial class LiveHubView
    {
        private sealed class FakeApi
        {
            public Func<CancellationToken,Task<IReadOnlyList<LiveArea>>> Areas;
            public Func<int,long,long,CancellationToken,Task<LiveRoomPage>> Rooms;
            public Task<IReadOnlyList<LiveArea>> GetLiveAreasAsync(CancellationToken ct)=>Areas(ct);
            public Task<LiveRoomPage> GetLiveRoomsAsync(int page,long parent,long area,CancellationToken ct)=>Rooms(page,parent,area,ct);
        }
        private sealed class LivePoster { public LivePoster(LiveRoom room) { } }
        private readonly Grid _root=new Grid(),_browse=new Grid();
        private readonly FakeRooms _rooms=new FakeRooms();
        private readonly List<LiveRoom> _roomItems=new List<LiveRoom>();
        private readonly TextBlock _pageText=new TextBlock(),_notice=new TextBlock();
        private readonly Button _category=new Button(),_open=new Button(),_refresh=new Button(),_previous=new Button(),_next=new Button();
        private readonly Dictionary<Button,Action> _actions=new Dictionary<Button,Action>();
        private CancellationTokenSource _browseCts=new CancellationTokenSource();
        private CancellationToken _external;
        private int _focusGeneration,_page=1,_selectedIndex;
        private bool IsPlayingRoom,_browseBusy,_hasMore;
        private Visibility Visibility;
        private Control _lastFocus;
        private Task _lastAction;
        private FakeApi _api=new FakeApi();
        private int requestPage;private long requestParent,requestArea;
        private static object Brush(int a,int r,int g,int b)=>null;
        private static TextBlock Text(string text,int size,bool bold=false)=>new TextBlock { Text=text };
        private static void Add(Grid grid,UIElement child,int row,int col)=>grid.Children.Add(child);
        private Button MakeButton(string text,Action action,string id) { var button=new Button { Content=text };_actions.Add(button,action);return button; }
        private void Focus(Control control) { _lastFocus=control;FocusManager.Current=control; }
        private static bool IsWithin(object child,object root)=>child!=null;
        private static void Cancel(ref CancellationTokenSource owner) { var previous=owner;owner=null;if(previous!=null){previous.Cancel();previous.Dispose();} }
        private static string Friendly(Exception error)=>error.Message;
        private void Run(Func<Task> action)=>_lastAction=action();
        private bool BrowseCurrent(CancellationTokenSource owner)=>owner==_browseCts && !owner.IsCancellationRequested;
        private void RestoreFocus()=>Focus(_category);
        private static int checks;
        private static void Check(bool value,string label) { checks++;if(!value)throw new Exception(label); }
        private static LiveHubView Fresh()
        {
            var view=new LiveHubView(); view.InitializeLiveCategories();
            view._api.Areas=ct=>Task.FromResult<IReadOnlyList<LiveArea>>(new[] {
                new LiveArea { Id=2,Name="网游",Children=new[] { new LiveArea { Id=86,ParentId=2,Name="英雄联盟" } } },
                new LiveArea { Id=6,Name="单机游戏" } });
            view._api.Rooms=(page,parent,area,ct)=> { view.requestPage=page;view.requestParent=parent;view.requestArea=area;
                return Task.FromResult(new LiveRoomPage { Page=page,HasMore=true,Items=new[] { new LiveRoom { Id=area+100 } } }); };
            return view;
        }
        public static async Task RunCategoryTests()
        {
            var view=Fresh();await view.OpenLiveCategoriesAsync();
            Check(view._categoryOpen && view._browse.Visibility==Visibility.Collapsed,"Category overlay owns browse space");
            Check(view._categoryGroupButtons.Count==3 && view._categoryGroupButtons[0].Tag==null,"Recommended plus real parent groups in API order");
            view.NavigateLiveCategories(PlayerDirection.Down);view.NavigateLiveCategories(PlayerDirection.Right);
            Check(view._categoryParentId==2 && view._categoryAreaButtons.Count==2,"Right opens whole-parent and real child choices");
            view.NavigateLiveCategories(PlayerDirection.Down);
            Check(view._lastFocus==view._categoryAreaButtons[1],"Down reaches concrete subcategory");
            view._actions[(Button)view._lastFocus]();await view._lastAction;
            Check(!view._categoryOpen && view._parentAreaId==2 && view._areaId==86 && view.requestPage==1,"Selection closes overlay and reloads first page");
            Check(view.requestParent==2 && view.requestArea==86 && view._notice.Text.Contains("英雄联盟"),"Rooms and visible label use selected real filter");
            await view.LoadRoomsAsync(2);
            Check(view.requestPage==2 && view.requestArea==86,"Pagination preserves filter");
            await view.OpenLiveCategoriesAsync();view.CloseLiveCategories();
            Check(view._areaId==86 && view._page==2,"Cancel preserves selection, page and room list");
            await view.OpenLiveCategoriesAsync();view._actions[view._categoryGroupButtons[2]]();
            view._actions[view._categoryAreaButtons[0]]();await view._lastAction;
            Check(view.requestParent==6 && view.requestArea==0,"Whole parent area is selectable");
            await view.OpenLiveCategoriesAsync();var obsolete=view._categoryGroupButtons[1];
            view._actions[view._categoryGroupButtons[0]]();await view._lastAction;
            Check(view.requestParent==0 && view.requestArea==0 && (string)view._category.Content=="分类","Recommended clears both category IDs");
            await view.OpenLiveCategoriesAsync();view.RenderCategoryGroups();view._actions.TryGetValue(obsolete,out var stale);
            Check(stale==null,"Removed rows leave action registry");
            var oldPage=new TaskCompletionSource<LiveRoomPage>();var normal=view._api.Rooms;
            view.CloseLiveCategories();view._api.Rooms=(page,parent,area,ct)=>oldPage.Task;
            var pending=view.LoadRoomsAsync(9);await view.OpenLiveCategoriesAsync();view._api.Rooms=normal;
            view._actions[view._categoryGroupButtons[1]]();view._actions[view._categoryAreaButtons[1]]();await view._lastAction;
            oldPage.SetResult(new LiveRoomPage { Page=9,Items=new[] { new LiveRoom { Id=999 } } });await pending;
            Check(view._page==1 && view._roomItems[0].Id==186 && !view._browseBusy,"Late old-filter page cannot replace selected category or loading state");
            view=Fresh();var late=new TaskCompletionSource<IReadOnlyList<LiveArea>>();view._api.Areas=ct=>late.Task;
            pending=view.OpenLiveCategoriesAsync();view.CloseLiveCategories();late.SetResult(new[] { new LiveArea { Id=88,Name="late" } });await pending;
            Check(!view._categoryOpen && view._liveAreas.Count==0,"Late catalogue after B cannot reopen panel or steal focus");
            view=Fresh();view._api.Areas=ct=>Task.FromException<IReadOnlyList<LiveArea>>(new Exception("offline"));await view.OpenLiveCategoriesAsync();
            Check(view._categoryRetry.Visibility==Visibility.Visible && view._categoryGroupButtons.Count==1,"Failure offers retry and real recommended option without fabricated areas");
            view.NavigateLiveCategories(PlayerDirection.Down);Check(view._lastFocus==view._categoryRetry,"Retry reachable by D-pad");
            Console.WriteLine("PASS: "+checks+" production live category selection, focus, cancellation, pagination, empty/error and late-request assertions (external XAML/API substitutes).");
        }
    }
}
