namespace Windows.UI { public struct Color { public static Color FromArgb(int a,int r,int g,int b)=>new Color(); } }
namespace Windows.UI.Core { public enum CoreDispatcherPriority { Low } }
namespace Windows.UI.Xaml
{
    public enum Visibility { Visible,Collapsed }
    public enum HorizontalAlignment { Center }
    public enum VerticalAlignment { Center }
    public enum TextTrimming { CharacterEllipsis }
    public class Element { public Visibility Visibility; }
}
namespace Windows.UI.Xaml.Controls
{
    public class Control : Element { public object Content,Tag;public string AccessibleName; }
    public class Button : Control { }
    public enum Orientation { Horizontal }
    public sealed class Children : List<Element> { public int Clears;public new void Clear() { Clears++;base.Clear(); } }
    public sealed class StackPanel : Element { public Orientation Orientation;public double Spacing;public HorizontalAlignment HorizontalAlignment;public readonly Children Children=new Children(); }
    public sealed class TextBlock : Element { public string Text;public double MaxWidth;public TextTrimming TextTrimming;public VerticalAlignment VerticalAlignment; }
}
namespace Windows.UI.Xaml.Media { public sealed class SolidColorBrush { public SolidColorBrush(Windows.UI.Color color) { } } }
namespace Windows.UI.Xaml.Shapes { public sealed class Ellipse : Element { public double Width,Height;public SolidColorBrush Fill;public VerticalAlignment VerticalAlignment; } }
namespace Windows.UI.Xaml.Input { public static class FocusManager { public static object Current;public static object GetFocusedElement()=>Current; } }
namespace Windows.UI.Xaml.Automation { public static class AutomationProperties { public static void SetName(Control control,string name) { control.AccessibleName=name; } } }
namespace BiliXbox.Models
{
    public sealed class CreatorProfile { public long Mid;public string Name; }
    public sealed class CreatorDynamicItem { public long AuthorMid;public string Id; }
    public sealed class DynamicAuthorUpdate { public long Mid;public bool HasUpdate; }
}
namespace BiliXbox.Controls
{
    public enum PlayerDirection { Up,Down,Left,Right }
    public sealed partial class AccountLibraryView
    {
        private enum LibraryPane { Activity }
        private sealed class State { public LibraryPane Pane; }
        private sealed class Hub : Element { public void StopRequests() { }public void Navigate(PlayerDirection direction) { }public void RestoreFocus() { } }
        private readonly Hub _activityHub=new Hub();private readonly State _state=new State();
        private sealed class FakeDispatcher { public readonly Queue<Action> Pending=new Queue<Action>();public global::System.Threading.Tasks.Task RunAsync(Windows.UI.Core.CoreDispatcherPriority priority,Action action){Pending.Enqueue(action);return global::System.Threading.Tasks.Task.CompletedTask;}public void Drain(){while(Pending.Count>0)Pending.Dequeue()();} }
        private readonly FakeDispatcher Dispatcher=new FakeDispatcher();private bool failNextFocus;private int _focusGeneration;
        private readonly List<Button> _authors=new List<Button>();private readonly Dictionary<long,int> _authorOrder=new Dictionary<long,int>();
        private readonly DynamicReadTracker _activityRead=new DynamicReadTracker();
        private readonly StackPanel _authorButtons=new StackPanel();
        private readonly Button _back=new Button(),_signOut=new Button();private Button _moreAuthors;
        private int _authorIndex;private long _activitySelectedMid;private bool _activityEntered,_authorsBusy,_unreadBusy,_activityOrderLocked;
        private readonly CancellationTokenSource _sessionCts=new CancellationTokenSource();private CancellationTokenSource _activityCts;
        private bool HasAccount=true;
        private static void Cancel(ref CancellationTokenSource value) { var old=value;value=null;if(old!=null){old.Cancel();old.Dispose();} }
        private void EnsureActivityReadAccount() { }
        private void SaveActivityRead() { }
        private void ApplyActivityDiscussionLayout() { }
        private void Focus(Control control){_focusGeneration++;if(failNextFocus){failNextFocus=false;return;}Windows.UI.Xaml.Input.FocusManager.Current=control;}
        private void AddAuthor(CreatorProfile author) { var button=new Button { Tag=author,Content=author.Name };_authorOrder[author.Mid]=_authorOrder.Count;_authors.Add(button);_authorButtons.Children.Add(button); }
        private static int checks;
        private static void Check(bool value,string label) { checks++;if(!value)throw new Exception(label); }
        private string Order()=>string.Join(",",_authors.Select(button=>((CreatorProfile)button.Tag).Mid));
        private static AccountLibraryView Fresh()
        {
            var view=new AccountLibraryView();view._activityRead.Reset(42);
            view.AddAuthor(new CreatorProfile { Mid=0,Name="所有动态" });view.AddAuthor(new CreatorProfile { Mid=1,Name="first" });view.AddAuthor(new CreatorProfile { Mid=2,Name="unread" });
            view._activityRead.Observe(new[] { new CreatorDynamicItem { AuthorMid=2,Id="100" } });
            return view;
        }
        public static string RunAuthorTests()
        {
            var view=Fresh();view._authorIndex=2;view.BeginActivityVisit();view.RestoreActivityFocus();
            Check(view.Order()=="0,2,1" && view._authorIndex==0 && ReferenceEquals(Windows.UI.Xaml.Input.FocusManager.Current,view._authors[0]),"Every entry starts at All and sorts unread first");
            view.NavigateActivity(PlayerDirection.Down);Check(view._authorIndex==1 && ReferenceEquals(Windows.UI.Xaml.Input.FocusManager.Current,view._authors[1]),"One Down reaches exactly second row");
            view.NavigateActivity(PlayerDirection.Down);Check(view._authorIndex==2,"Next Down reaches third row");
            view.Focus(view._back);view.NavigateActivity(PlayerDirection.Down);Check(view._authorIndex==0,"Header Down returns to All instead of stale third index");
            view.NavigateActivity(PlayerDirection.Down);var selected=view._authors[1];int clears=view._authorButtons.Children.Clears;
            view.OnActivityViewed(new CreatorDynamicItem { AuthorMid=2,Id="100" });
            Check(view.Order()=="0,2,1" && ReferenceEquals(view._authors[1],selected),"Read author stays at same position for this visit");
            Check(((StackPanel)selected.Content).Children[1].Visibility==Visibility.Collapsed,"Pink dot disappears immediately");
            Check(view._authorButtons.Children.Clears==clears && ReferenceEquals(Windows.UI.Xaml.Input.FocusManager.Current,selected),"Badge update does not reparent rows or lose focus/scroll");
            view.EndActivityVisit();view.BeginActivityVisit();view.RestoreActivityFocus();Check(view.Order()=="0,1,2" && view._authorIndex==0,"Reentry restores default order once everything is read");
            view._activityRead.Observe(new[] { new CreatorDynamicItem { AuthorMid=1,Id="200" },new CreatorDynamicItem { AuthorMid=2,Id="300" } });
            view.BeginActivityVisit();view.RestoreActivityFocus();view.NavigateActivity(PlayerDirection.Down);view.OnActivityViewed(new CreatorDynamicItem { AuthorMid=1,Id="200" });
            Check(view.Order()=="0,1,2","Reading first unread does not move it while browsing");
            view.BeginActivityVisit();view.RestoreActivityFocus();Check(view.Order()=="0,2,1","Still-unread author stays above read author on next entry");
            view._activityRead.MarkViewed(2,"300");view.BeginActivityVisit();Check(view.Order()=="0,1,2","All read returns to exact original follow order");
            view=Fresh();view.BeginActivityVisit();view.RestoreActivityFocus();view.NavigateActivity(PlayerDirection.Down);view._activityEntered=true;view._activitySelectedMid=2;
            view.OnActivityLoaded(new[] { new CreatorDynamicItem { AuthorMid=2,Id="100" } });
            Check(view.Order()=="0,2,1" && !view._activityRead.IsUnread(2),"Opening author marks page read without reordering");
            view=Fresh();view.BeginActivityVisit();view.Focus(view._back);view.failNextFocus=true;view.RestoreActivityFocus();view.Dispatcher.Drain();
            Check(ReferenceEquals(Windows.UI.Xaml.Input.FocusManager.Current,view._authors[0]),"Fresh XAML button gets a guarded focus retry after load");
            view=Fresh();view.BeginActivityVisit();view.Focus(view._back);view.failNextFocus=true;view.RestoreActivityFocus();view._activityEntered=true;view.Dispatcher.Drain();
            Check(ReferenceEquals(Windows.UI.Xaml.Input.FocusManager.Current,view._back),"Late retry cannot steal focus after entering a feed/editor");
            return "PASS: "+checks+" production activity entry, exact directional movement, stable visit order, dot-only updates and reentry sorting assertions (XAML substitutes).";
        }
    }
}
