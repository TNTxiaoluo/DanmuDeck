// Production API/MPD code with deterministic WinRT and authorized HTTP fixtures.
// No real account, hardware mode change, media download or account write.
namespace BiliXbox.ToneMappedPlaybackTests
{
    using BiliXbox.Services;
    using BiliXbox.VideoDetailTests;
    using Windows.Graphics.Display.Core;
    using Windows.Media.Core;
    using Windows.Media.Protection;
    public static class Tests
    {
        private static int _checks;
        private static readonly VideoDetail Video = new VideoDetail { Bvid="BV12Bn86nEXE",Aid=100,Cid=200 };
        private static void Check(bool value,string name) {if(!value) throw new Exception("FAIL: "+name);++_checks;}
        private static JObject Track(int id,string codec,string path,int width,int height,int bits=8) => new JObject
        {
            ["id"]=id,["codecs"]=codec,["base_url"]="https://cdn.bilivideo.com/"+path+"?auth=fixture-tone-secret",
            ["bandwidth"]=12000000,["width"]=width,["height"]=height,["bits_per_channel"]=bits,
            ["frame_rate"]="60000/1001",["segment_base"]=new JObject{["initialization"]="0-1199",["index_range"]="1200-1499"}
        };
        private static JObject Data(bool nativeSdr=false)
        {
            var hdr=Track(125,"hvc1.2.4.L153.B0","hdr4k.m4s",3840,2160,10);
            hdr["hdr_type"]="hdr10";hdr["transfer_characteristics"]=16;hdr["color_primaries"]=9;hdr["matrix_coefficients"]=9;
            var tracks=new JArray(Track(127,"hvc1.1.6.L186.90","sdr8k.m4s",7680,4320),hdr,
                Track(80,"avc1.640028","1080.m4s",1920,1080));
            if(nativeSdr) tracks.Add(Track(120,"av01.0.13M.08","native4ksdr.m4s",3840,2160));
            var flac=Track(30251,"fLaC","lossless.m4s",0,0);
            flac["sample_rate"]=96000;flac["bits_per_sample"]=24;flac["channels"]=2;
            return new JObject{["quality"]=80,["accept_quality"]=new JArray(127,125,120,80),
                ["accept_description"]=new JArray("8K","HDR10","4K","1080P"),
                ["dash"]=new JObject{["duration"]=97,["video"]=tracks,
                    ["audio"]=new JArray(Track(30280,"mp4a.40.2","aac.m4s",0,0)),
                    ["flac"]=new JObject{["display"]=true,["audio"]=flac}}};
        }
        private static void Stub(JObject data, bool sdr=true, bool hdr=false, bool sdrUnknown=false, bool hdrUnknown=false, bool decode=true)
        {
            Windows.System.Profile.AnalyticsInfo.VersionInfo.DeviceFamily="Windows.Xbox";
            HdmiDisplayInformation.ThrowView=HdmiDisplayInformation.ThrowModes=false;
            HdmiDisplayInformation.Current=new HdmiDisplayInformation{Modes=new[]{new HdmiDisplayMode
            {
                ResolutionWidthInRawPixels=sdr||hdr?3840u:1920u,ResolutionHeightInRawPixels=sdr||hdr?2160u:1080u,
                RefreshRate=60,IsSdrLuminanceSupported=sdr,IsSmpte2084Supported=hdr,
                ColorSpace=hdr?HdmiDisplayColorSpace.BT2020:HdmiDisplayColorSpace.RgbLimited
            }}};
            ProtectionCapabilities.Query=(content,key)=>
            {
                if(!content.Contains("display-res-x")) return decode?ProtectionCapabilityResult.Probably:ProtectionCapabilityResult.Maybe;
                if(content.Contains("hdr=1")) return hdr?ProtectionCapabilityResult.Probably:
                    hdrUnknown?ProtectionCapabilityResult.Maybe:ProtectionCapabilityResult.NotSupported;
                if(content.Contains("display-res-x=7680")) return ProtectionCapabilityResult.NotSupported;
                return sdr?ProtectionCapabilityResult.Probably:sdrUnknown?ProtectionCapabilityResult.Maybe:ProtectionCapabilityResult.NotSupported;
            };
            CodecQuery.Query=(kind,category,subtype)=>new[]{new CodecInfo()};
            Transport.Reset((request,ct)=>
            {
                Check(request.Method.Method=="GET","tone map negotiation never writes account data");
                if(request.RequestUri.AbsolutePath=="/x/web-interface/nav")
                {
                    var nav=new JObject{["code"]=0,["data"]=new JObject{["isLogin"]=true,["mid"]=42,["uname"]="fixture",
                        ["vip"]=new JObject{["status"]=1},["wbi_img"]=new JObject{
                        ["img_url"]="https://i0.hdslb.com/bfs/wbi/7cd084941338484aae1ad9425b84077c.png",
                        ["sub_url"]="https://i0.hdslb.com/bfs/wbi/4932caff0ff746eab6f01bf08b70ac45.png"}}};
                    var response=new HttpResponseMessage{Content=new HttpStringContent(nav.ToString())};
                    response.Headers["Set-Cookie"]="SESSDATA=fixture-tone-only; Domain=.bilibili.com; Path=/; Secure";
                    return Task.FromResult(response);
                }
                string cookie;Check(request.Headers.TryGetValue("Cookie",out cookie)&&cookie.Contains("SESSDATA=fixture-tone-only"),
                    "all compatibility requests retain the signed-in account session");
                return Transport.Json(new JObject{["code"]=0,
                    [request.RequestUri.AbsolutePath.StartsWith("/pgc/",StringComparison.Ordinal)?"result":"data"]=data.DeepClone()}.ToString());
            });
        }
        private static async Task Login(BilibiliClient api)
        {
            var method=typeof(BilibiliClient).GetMethod("RefreshProfileAsync",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance);
            await (Task)method.Invoke(api,new object[]{CancellationToken.None});
        }
        public static async Task<string> Run()
        {
            foreach(bool pgc in new[]{false,true}) foreach(bool aliases in new[]{false,true})
            {
                var data=Data();var source=(JObject)data["dash"]["video"][1];
                if(aliases){source["baseUrl"]=source["base_url"];source.Remove("base_url");
                    source["SegmentBase"]=new JObject{["Initialization"]="0-1199",["indexRange"]="1200-1499"};source.Remove("segment_base");
                    source["frameRate"]="60000/1001";source.Remove("frame_rate");}
                Stub(data);
                using(var api=new BilibiliClient())
                {
                    await Login(api);var messages=new List<string>();api.PlaybackDiagnostic+=messages.Add;
                    var result=pgc?await api.GetEpisodePlaybackAsync(601,120,CancellationToken.None,PlaybackAudioMode.HiRes)
                        :await api.GetPlaybackAsync(Video,120,CancellationToken.None,PlaybackAudioMode.HiRes);
                    Check(result.QualityId==120&&result.SourceQualityId==125&&result.UsesSdrToneMapping&&result.IsHdr10
                        && result.FourKDisplayVerified&&!result.Hdr10DisplayVerified&&result.VideoBitsPerChannel==10,
                        "real4K HDR10 source can use independently verified SDR output without claiming HDR output");
                    Check(result.VideoWidth==3840&&result.VideoHeight==2160&&result.VideoTrackIndex==1
                        &&result.Url.Contains("hdr4k.m4s")&&!result.Url.Contains("sdr8k"),
                        "logical4K output retains original HDR source URL/index and never relabels or decodes the8K source");
                    var option=result.AvailableQualities.Single(item=>item.Id==120);
                    Check(option.IsVisible&&option.IsAvailable&&option.HasPlaybackSource&&!option.IsProbe&&option.Label.Contains("色调映射"),
                        "absent rawvideo120 still offers the genuine HDR-to-SDR4K output variant");
                    Check(!result.AvailableQualities.Single(item=>item.Id==125).IsVisible
                        && !result.AvailableQualities.Single(item=>item.Id==127).IsVisible,
                        "SDR output variant cannot re-enable unsupported HDR125 or8K127 menu options");
                    var manifest=XDocument.Parse(result.DashManifest);XNamespace ns="urn:mpeg:dash:schema:mpd:2011";
                    var representation=manifest.Descendants(ns+"Representation").Single(item=>item.Attribute("id")?.Value=="video-125");
                    Check(representation.Attribute("codecs").Value=="hvc1.2.4.L153.B0"
                        &&representation.Element(ns+"BaseURL").Value.Contains("hdr4k.m4s")
                        &&representation.Elements(ns+"SupplementalProperty").Any(item=>item.Attribute("schemeIdUri")?.Value.EndsWith(":TransferCharacteristics")==true&&item.Attribute("value")?.Value=="16"),
                        "MPD preserves the actual Main10 source identity and PQ transfer instead of relabelling its encoded color data");
                    Check(result.PlaybackDiagnostic.Contains("track index=1 qn=125"),"output mapping never rewrites the raw API source identity");
                    Check(result.PlaybackDiagnostic.Contains("SDR-tone-map-eligible; HDR-output-excluded")
                        &&result.PlaybackDiagnostic.Contains("output-variant sourceQn=125 effectiveQn=120 source=HDR10 output=SDR")
                        &&messages.Any(value=>value.StartsWith("selected ")&&value.Contains("sourceQn=125")&&value.Contains("outputRange=SDR")),
                        "diagnostics distinguish source HDR from output SDR rather than declaring the source wholly unusable");
                    Check(result.AudioMode==PlaybackAudioMode.HiRes&&result.AudioFallback?.AudioMode==PlaybackAudioMode.Standard
                        &&result.AudioFallback.UsesSdrToneMapping&&result.AudioFallback.SourceQualityId==125
                        &&result.AudioFallback.VideoTrackIndex==1,"Hi-Res failure keeps the same HDR-to-SDR4K video on its first AAC fallback");
                    Check(Transport.Requests.Count(item=>item.Uri.AbsolutePath.Contains("playurl"))<=(pgc?1:2)
                        &&!Transport.Requests.Any(item=>item.Uri.Query.Contains("fnval=400&")),
                        "tone-map fallback preserves existing bounded AV1 negotiation without speculative SDR-catalogue requests");
                }
            }
            Stub(Data(nativeSdr:true));
            using(var api=new BilibiliClient())
            {
                await Login(api);var result=await api.GetPlaybackAsync(Video,120);
                Check(result.QualityId==120&&result.SourceQualityId==120&&!result.UsesSdrToneMapping&&!result.IsHdr10
                    &&result.Url.Contains("native4ksdr.m4s"),"a real native4K SDR representation is preferred to a HEVC tone-map fallback");
                Check(result.CodecFallback?.QualityId==120&&result.CodecFallback.UsesSdrToneMapping,
                    "verified HDR-to-SDR4K remains an alternate source before lower video quality");
            }
            Stub(Data(nativeSdr:true),hdr:true);
            using(var api=new BilibiliClient())
            {
                await Login(api);var result=await api.GetPlaybackAsync(Video,125);
                Check(result.QualityId==125&&result.SourceQualityId==125&&result.IsHdr10&&!result.UsesSdrToneMapping
                    &&result.Hdr10DisplayVerified,"requested HDR stays actual HDR when the independent HDR output proof succeeds");
            }
            var highRate=Data(nativeSdr:true);highRate["dash"]["video"][3]["frame_rate"]="119.994";
            Stub(highRate);
            using(var api=new BilibiliClient())
            {
                await Login(api);var result=await api.GetPlaybackAsync(Video,120);
                Check(result.UsesSdrToneMapping&&!result.RequiresNativeFrameRateValidation&&result.VideoFrameRate<60
                    &&result.CodecFallback?.RequiresNativeFrameRateValidation==true,
                    "confirmed ordinary-rate4K tone-map source precedes an unverified high-frame-rate native SDR trial");
            }
            foreach(int sourceId in new[]{120,125})
            {
                var data=Data();var source=data["dash"]["video"][1];source["id"]=sourceId;source["frame_rate"]="119.994";
                Stub(data);
                using(var api=new BilibiliClient())
                {
                    await Login(api);var result=await api.GetPlaybackAsync(Video,120);
                    Check(result.QualityId==120&&result.SourceQualityId==sourceId&&result.UsesSdrToneMapping&&result.IsHdr10
                        &&result.VideoTrackIndex==1&&result.VideoFrameRate==119.994&&result.VideoFrameRateProbe==60
                        &&result.RequiresNativeFrameRateValidation&&!result.NativeFrameRateVerified,
                        "HDR-to-SDR variant preserves either raw HDR quality identity and highfps declaration without certifying120fps");
                    Check(result.AvailableQualities.Single(item=>item.Id==120).RequiresCompatibilityTrial
                        &&!XDocument.Parse(result.DashManifest).Descendants().Attributes("frameRate").Any(),
                        "mapped highfps quality remains explicitly labelled as a trial with no fabricated MPD rate");
                }
            }
            var narrowMenu=Data();narrowMenu["accept_quality"]=new JArray(80);narrowMenu["accept_description"]=new JArray("1080P");
            Stub(narrowMenu);
            using(var api=new BilibiliClient())
            {
                await Login(api);var result=await api.GetPlaybackAsync(Video,80);
                var mapped=result.AvailableQualities.Single(item=>item.Id==120);
                Check(result.QualityId==80&&!result.UsesSdrToneMapping&&mapped.IsAvailable&&mapped.IsVisible
                    &&mapped.HasPlaybackSource&&!mapped.IsProbe&&mapped.RequiresPremium&&mapped.Label.Contains("色调映射"),
                    "default1080 menu synthesizes the actual mapped4K variant even when accept_quality omits120");
            }
            foreach(bool unknown in new[]{false,true})
            {
                Stub(Data(),sdr:false,sdrUnknown:unknown);
                using(var api=new BilibiliClient())
                {
                    await Login(api);var result=await api.GetPlaybackAsync(Video,120);
                    Check(result.QualityId==80&&!result.UsesSdrToneMapping,
                        "denied or unknown SDR output cannot authorize HDR-to-SDR4K");
                }
            }
            Stub(Data(),decode:false);
            using(var api=new BilibiliClient())
            {
                await Login(api);var result=await api.GetPlaybackAsync(Video,120);
                Check(result.QualityId==80&&!result.UsesSdrToneMapping,"available SDR display cannot substitute for unconfirmed4K source decoding");
            }
            foreach(string invalid in new[]{"HLG","DolbyVision","8K","8bit","720P"})
            {
                var data=Data();var track=data["dash"]["video"][1];
                if(invalid=="HLG"){track["hdr_type"]="hlg";track["transfer_characteristics"]=18;}
                if(invalid=="DolbyVision"){track["id"]=126;track["hdr_type"]="dolbyvision";}
                if(invalid=="8K"){track["width"]=7680;track["height"]=4320;}
                if(invalid=="8bit")track["bits_per_channel"]=8;
                if(invalid=="720P"){track["width"]=1280;track["height"]=720;}
                Stub(data);
                using(var api=new BilibiliClient())
                {
                    await Login(api);var result=await api.GetPlaybackAsync(Video,120);
                    Check(result.QualityId==80&&!result.UsesSdrToneMapping,"unsupported source is never repackaged as4K HDR-to-SDR: "+invalid);
                }
            }
            Stub(Data(),hdrUnknown:true);
            using(var api=new BilibiliClient())
            {
                await Login(api);var result=await api.GetPlaybackAsync(Video,120);
                var hdr=result.AvailableQualities.Single(item=>item.Id==125);
                Check(result.UsesSdrToneMapping&&result.QualityId==120&&hdr.IsCapabilityPending&&!hdr.IsAvailable,
                    "independent confirmed SDR variant remains usable while HDR output is unconfirmed");
            }
            return "PASS: "+_checks+" production4K HDR-source/SDR-output, raw identity, menu, MPD, authorization, audio fallback and no8K relabelling assertions (offline fixtures).";
        }
    }
}
