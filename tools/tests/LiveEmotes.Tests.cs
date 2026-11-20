using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BiliXbox.Models;
using BiliXbox.Services;
using BiliXbox.Controls;
using Newtonsoft.Json.Linq;
using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
namespace BiliXbox.LiveEmoteTests
{
    public static class Tests
    {
        private static int checks;
        private static void Check(bool ok,string reason){++checks;if(!ok)throw new Exception(reason);}
        private static JObject Image(string url="http://i0.hdslb.com/bfs/live/example.png")=>new JObject{["url"]=url,["width"]=162,["height"]=162};
        private static LiveChatMessage Packet(string text,JToken large=null,JObject emots=null,string extra=null)
        {
            var properties=new JArray();for(int i=0;i<16;i++)properties.Add(0);
            properties[1]=1;properties[2]=25;properties[3]=16777215;properties[13]=large??new JObject();
            properties[15]=new JObject{["extra"]=extra??new JObject{["emots"]=emots}.ToString()};
            var json=new JObject{["cmd"]="DANMU_MSG",["info"]=new JArray(properties,text,new JArray(1,"viewer"))};
            return LiveDanmakuProtocol.Decode(LiveDanmakuProtocol.Packet(5,Encoding.UTF8.GetBytes(json.ToString()),0)).Messages.Single();
        }
        public static string Run()
        {
            var collection=Packet("[收藏集_呐呐呐]",Image());
            Check(collection.Emotes.Count==1&&collection.Emotes[0].ImageUrl.StartsWith("https://"),"whole-message collection/dress emote preserves CDN image and upgrades HTTPS");
            var mixed=Packet("你好[装扮_哭哭]！[花]",emots:new JObject{["[花]"]=Image(),["[装扮_哭哭]"]=Image()});
            var parts=LiveEmoteContent.Split(mixed.Text,mixed.Emotes);
            Check(parts.Count(p=>p.IsImage)==2,"mixed text and multiple emotes produce inline images");
            Check(string.Concat(parts.Select(p=>p.IsImage?p.AltText:p.Text))==mixed.Text,"image fallback preserves full original text and ordering");
            Check(parts.First().Text=="你好"&&parts.Last().AltText=="[花]","metadata dictionary order cannot reorder message content");
            Check(Packet("still readable",extra:"{malformed").Text=="still readable","malformed extra metadata cannot discard chat");
            Check(Packet("[bad]",Image("https://evil.example/image.png")).Emotes.Count==0,"non-CDN image rejected with text retained");
            Check(Packet("[bad]",new JObject{["url"]=new JArray("bad")}).Emotes.Count==0,"malformed image URL type cannot fail packet decoding");
            string safe;
            foreach(var url in new[]{"file:///c:/secret","https://i0.hdslb.com.evil.example/a","https://u:p@i0.hdslb.com/a","https://i0.hdslb.com:999/a","data:image/png;base64,x"})
                Check(!LiveEmoteMetadata.TryUrl(url,out safe),"unsafe image URL accepted");
            Check(LiveEmoteMetadata.TryUrl("//i0.hdslb.com/bfs/image.gif",out safe)&&safe.StartsWith("https://"),"protocol-relative emote CDN supported");
            var history=new JObject{["emots"]=new JObject{["[花]"]=Image()}};
            Check(LiveEmoteMetadata.FromHistory(history,"history[花]").Count==1,"recent history retains image metadata too");
            var many=new JObject();for(int i=0;i<30;i++)many["["+i+"]"]=Image();
            var flood=Packet(string.Concat(Enumerable.Range(0,30).Select(i=>"["+i+"]")),emots:many);
            Check(flood.Emotes.Count==16,"per-packet emote metadata bounded");
            Check(LiveEmoteContent.Split(flood.Text,flood.Emotes).Count(p=>p.IsImage)==8,"per-message realized images bounded");
            var snapshot=new List<CommentContentPart>{new CommentContentPart{ImageUrl=collection.Emotes[0].ImageUrl,AltText=collection.Text}};
            using(var surface=new DanmakuOverlay())
            {
                surface.SetViewport(1280,720);surface.RaiseLoaded();int builds=0;
                surface.RichContentFactory=(p,f,m,c)=>{++builds;Check(p.Count==1&&p[0].IsImage,"renderer snapshots valid image parts");return new RichFixture();};
                surface.Load(new[]{new DanmakuItem{Text=collection.Text,ContentParts=snapshot}});snapshot.Clear();surface.Update(TimeSpan.Zero,true);
                Check(surface.Children.Single() is RichFixture,"image emote uses rich surface in actual danmaku lane");
                int before=builds;surface.Update(TimeSpan.FromSeconds(.1),true);
                Check(builds==before,"animation updates never reconstruct images");
                surface.ApplySettings(surface.Settings.With(opacity:.25));
                Check(builds==before&&surface.Children.Single().Opacity==.25,"opacity updates rich visual without recreating image");
                surface.Update(TimeSpan.FromSeconds(100),false);Check(surface.Children.Count==0,"expired image emote leaves canvas");
                surface.RichContentFactory=(p,f,m,c)=>throw new InvalidOperationException("decode failed");
                surface.Load(new[]{new DanmakuItem{Text="[original]",ContentParts=new[]{new CommentContentPart{ImageUrl=collection.Emotes[0].ImageUrl,AltText="[original]"}}}});
                surface.Update(TimeSpan.Zero,false);Check(surface.Children.Single() is TextBlock&&surface.Children.Single().Text=="[original]","rich render failure retains readable text");
            }
            return "PASS: "+checks+" live emote protocol, inline ordering, URL/size bounds and production overlay lifecycle assertions.";
        }
        private sealed class RichFixture:FrameworkElement
        { public override void Measure(Size size){DesiredSize=new Size(80,30);} }
    }
}
