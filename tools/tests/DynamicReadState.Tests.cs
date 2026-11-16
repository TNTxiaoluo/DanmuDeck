namespace BiliXbox.Models {
    public sealed class CreatorDynamicItem { public string Id="";public long AuthorMid; }
    public sealed class DynamicAuthorUpdate { public long Mid;public bool HasUpdate; }
}
namespace BiliXbox.DynamicReadTests {
    public static class Tests {
        private static int checks;
        private static void Check(bool value,string name){checks++;if(!value)throw new Exception(name);}
        private static Models.CreatorDynamicItem Post(long mid,string id)=>new Models.CreatorDynamicItem{AuthorMid=mid,Id=id};
        private static Models.DynamicAuthorUpdate Flag(long mid,bool value)=>new Models.DynamicAuthorUpdate{Mid=mid,HasUpdate=value};
        public static string Run(){
            var state=new Controls.DynamicReadTracker();state.Reset(42);
            Check(!state.IsUnread(1)&&!state.IsUnread(0),"no invented unread state or dot for All");
            state.ApplyPortal(new[]{Flag(1,true),Flag(2,false),Flag(0,true)});
            Check(state.IsUnread(1)&&!state.IsUnread(2)&&!state.IsUnread(0),"server update flags are per real author");
            Check(state.OrderAuthors(new long[]{0,2,3,1}).SequenceEqual(new long[]{0,1,2,3}),"unread authors sort first, All stays first");
            state.MarkViewed(1);Check(!state.IsUnread(1),"opening a successfully loaded empty author view clears initial flag");
            Check(state.OrderAuthors(new long[]{0,2,3,1}).SequenceEqual(new long[]{0,2,3,1}),"all read restores exact default order");
            state.Observe(new[]{Post(1,"99"),Post(2,"77")});Check(state.IsUnread(1)&&state.IsUnread(2),"real new IDs are unread");
            state.MarkPageViewed(1,new[]{Post(1,"99"),Post(1,"4")});Check(!state.IsUnread(1)&&state.IsUnread(2),"pinned older post cannot replace newest page watermark");
            state.Observe(new[]{Post(1,"100")});Check(state.IsUnread(1),"numeric ID growth uses magnitude, not lexicographic99>100");
            state.MarkPageViewed(1,new[]{Post(1,"99")});Check(state.IsUnread(1),"late older page cannot mark a newer concurrently observed post read");
            state.MarkViewed(1,"100");state.ApplyPortal(new[]{Flag(1,true)});
            Check(!state.IsUnread(1)&&state.NeedsLatestCheck(1),"stale portal flag does not re-add a dot immediately after reading");
            var saved=state.Export();var restored=new Controls.DynamicReadTracker();restored.Reset(42,saved);
            restored.ApplyPortal(new[]{Flag(1,true)});restored.Observe(new[]{Post(1,"100")});Check(!restored.IsUnread(1),"read watermark survives reopening");
            restored.Observe(new[]{Post(1,"101")});Check(restored.IsUnread(1),"newer update after reopening restores dot");
            restored.Reset(43);Check(!restored.IsUnread(1)&&restored.Export().Count==0,"different account never inherits read markers");
            restored.Observe(new[]{Post(1,"https://invalid"),Post(0,"500"),Post(2,new string('9',65))});
            Check(!restored.IsUnread(1)&&!restored.IsUnread(2),"invalid/missing IDs cannot become synthetic update markers");
            restored.Reset(0);restored.Observe(new[]{Post(1,"500")});restored.ApplyPortal(new[]{Flag(1,true)});restored.MarkViewed(1,"500");
            Check(!restored.IsUnread(1)&&restored.Export().Count==0,"logged-out state cannot accumulate markers");
            return "PASS: "+checks+" production dynamic unread, stable ordering, persistence, account isolation and late-page assertions.";
        }
    }
}
