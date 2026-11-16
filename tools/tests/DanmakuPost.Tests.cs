// Real client post validation with account and transport fixtures. No remote writes.
namespace BiliXbox.DanmakuPostTests
{
    using BiliXbox.Services;
    using BiliXbox.VideoDetailTests;
    public static class Tests
    {
        private static int checks;
        private static Dictionary<string,string> lastBody;
        private static void Check(bool value,string label) { checks++;if(!value)throw new Exception(label); }
        private static int Posts => Transport.Requests.Count(request=>request.Method=="POST");
        private static void Setup(bool premium,Action<HttpRequestMessage> onRequest=null)
        {
            Transport.Reset((request,ct)=> {
                onRequest?.Invoke(request);
                if (request.Method.Method == "POST") lastBody = ((HttpFormUrlEncodedContent)request.Content).Values;
                JObject data=new JObject();
                if(request.RequestUri.AbsolutePath=="/x/web-interface/nav") data=new JObject { ["mid"]=42,["uname"]="fixture",["isLogin"]=true,["vipStatus"]=premium?1:0,
                    ["wbi_img"]=new JObject { ["img_url"]="https://i0.hdslb.com/bfs/wbi/7cd084941338484aae1ad9425b84077c.png",["sub_url"]="https://i0.hdslb.com/bfs/wbi/4932caff0ff746eab6f01bf08b70ac45.png" } };
                else Check(request.RequestUri.AbsolutePath=="/x/v2/dm/post" && request.Method.Method=="POST","Only real danmaku endpoint can post");
                var response=new HttpResponseMessage { Content=new HttpStringContent(new JObject { ["code"]=0,["data"]=data }.ToString()) };
                if(request.RequestUri.AbsolutePath=="/x/web-interface/nav") response.Headers["Set-Cookie"]="SESSDATA=fixture-session; Domain=.bilibili.com; Path=/; Secure,bili_jct=fixture-csrf; Domain=.bilibili.com; Path=/; Secure";
                return Task.FromResult(response);
            });
        }
        private static Task Login(BilibiliClient client)=>(Task)typeof(BilibiliClient).GetMethod("RefreshProfileAsync",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).Invoke(client,new object[] { CancellationToken.None });
        private static async Task Reject(Func<Task> run,string label)
        { try { await run(); }catch(Exception ex)when(ex is BilibiliApiException || ex is ArgumentException || ex is OperationCanceledException){Check(true,label);return;}throw new Exception("Expected rejection: "+label); }
        public static async Task<string> Run()
        {
            Setup(false);
            using(var client=new BilibiliClient()) {
                await Reject(()=>client.SendDanmakuAsync(1,2,"test",TimeSpan.Zero),"Anonymous cannot send");Check(Posts==0,"No anonymous POST");
                await Login(client);
                await Reject(()=>client.SendDanmakuAsync(1,2,"test",TimeSpan.Zero,0xff0000),"Colored danmaku requires premium");
                await Reject(()=>client.SendDanmakuAsync(1,2,"test",TimeSpan.Zero,fontSize:24),"Invalid font rejected");
                await Reject(()=>client.SendDanmakuAsync(1,2,"test",TimeSpan.Zero,mode:8),"Invalid mode rejected");Check(Posts==0,"Rejected styles never reach POST");
                await client.SendDanmakuAsync(1,2,"white",TimeSpan.Zero);
                Check(Posts==1,"Non premium can send normal white comment");
            }
            Setup(true);
            using(var client=new BilibiliClient()) {
                await Login(client);
                foreach(int mode in new[] {1,5,4})foreach(int size in new[] {25,18,36}) {
                    await client.SendDanmakuAsync(1,2," 测试🎮 ",TimeSpan.FromMilliseconds(12340),0xfb7299,mode,fontSize:size);
                    var body=lastBody;
                    Check(body["mode"]==mode.ToString() && body["fontsize"]==size.ToString() && body["color"]=="16478873","Exact selected mode, size and RGB sent");
                    Check(body["msg"]=="测试🎮" && body["progress"]=="12340" && body["csrf"]=="fixture-csrf","Text, original media position and session preserved");
                }
                int before=Posts;var cancel=new CancellationTokenSource();cancel.Cancel();await Reject(()=>client.SendDanmakuAsync(1,2,"cancel",TimeSpan.Zero,ct:cancel.Token),"Cancelled request rejected");Check(Posts==before,"Cancellation does not send another comment");
            }
            BilibiliClient changing=null;bool switchDuringSign=false;
            Setup(true,request=> { if(switchDuringSign && request.RequestUri.AbsolutePath=="/x/web-interface/nav") typeof(BilibiliClient).GetProperty("CurrentUser").SetValue(changing,new UserProfile { Mid=43,IsPremium=true }); });
            using(changing=new BilibiliClient()) { await Login(changing);typeof(BilibiliClient).GetField("_keysExpire",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).SetValue(changing,DateTimeOffset.MinValue);switchDuringSign=true;await Reject(()=>changing.SendDanmakuAsync(1,2,"stale",TimeSpan.Zero),"Account switch during signing rejects old draft");Check(Posts==0,"Stale account cannot POST"); }
            return "PASS: "+checks+" production danmaku style, premium, exact form, cancellation and account ownership assertions (no network or writes).";
        }
    }
}
