namespace BiliXbox.MultiTrackPlaybackTests
{
    using BiliXbox.Services;
    using BiliXbox.VideoDetailTests;
    using Windows.Graphics.Display.Core;
    using Windows.Media.Core;
    using Windows.Media.Protection;
    public static class Tests
    {
        private static int _checks;
        private static readonly VideoDetail Video = new VideoDetail { Bvid = "BV1xx411c7mD", Aid = 100, Cid = 200 };
        private static void Check(bool value, string message) { if (!value) throw new Exception("FAIL: " + message); ++_checks; }
        private static JObject Track(int id, string codec, string path, long bitrate = 12000000) => new JObject
        {
            ["id"] = id, ["codecs"] = codec, ["base_url"] = "https://cdn.bilivideo.com/" + path + "?auth=fixture-secret",
            ["bandwidth"] = bitrate, ["width"] = id >= 120 ? 3840 : 1920, ["height"] = id >= 120 ? 2160 : 1080,
            ["frame_rate"] = "60", ["segment_base"] = new JObject { ["initialization"] = "0-1199", ["index_range"] = "1200-1499" }
        };
        private static JObject Data(params JObject[] tracks) => new JObject
        {
            ["quality"] = 120, ["accept_quality"] = new JArray(125,120,80),
            ["accept_description"] = new JArray("HDR10","4K","1080P"),
            ["dash"] = new JObject { ["duration"] = 97, ["video"] = new JArray(tracks),
                ["audio"] = new JArray(Track(30280,"mp4a.40.2","aac.m4s", 300000)) }
        };
        private static void Stub(Func<HttpRequestMessage,JObject> response, bool hdrDisplay = false)
        {
            Windows.System.Profile.AnalyticsInfo.VersionInfo.DeviceFamily = "Windows.Xbox";
            HdmiDisplayInformation.ThrowView = HdmiDisplayInformation.ThrowModes = false;
            HdmiDisplayInformation.Current = new HdmiDisplayInformation { Modes = new[] { new HdmiDisplayMode
            {
                ResolutionWidthInRawPixels=3840, ResolutionHeightInRawPixels=2160, RefreshRate=60,
                IsSdrLuminanceSupported=true, IsSmpte2084Supported=hdrDisplay,
                ColorSpace=hdrDisplay ? HdmiDisplayColorSpace.BT2020 : HdmiDisplayColorSpace.RgbLimited
            } } };
            ProtectionCapabilities.Query = (content,key) => ProtectionCapabilityResult.Probably;
            CodecQuery.Query = (kind,category,subtype) => new[] { new CodecInfo() };
            Transport.Reset((request,ct) =>
            {
                Check(request.Method.Method == "GET", "fixture does not write account data");
                if (request.RequestUri.AbsolutePath == "/x/web-interface/nav")
                {
                    var nav = new JObject { ["code"]=0,["data"]=new JObject { ["isLogin"]=true,["mid"]=42,["uname"]="fixture",
                        ["vip"]=new JObject{["status"]=1},["wbi_img"]=new JObject{
                            ["img_url"]="https://i0.hdslb.com/bfs/wbi/7cd084941338484aae1ad9425b84077c.png",
                            ["sub_url"]="https://i0.hdslb.com/bfs/wbi/4932caff0ff746eab6f01bf08b70ac45.png"}}};
                    var result = new HttpResponseMessage { Content = new HttpStringContent(nav.ToString()) };
                    result.Headers["Set-Cookie"]="SESSDATA=fixture-only; Domain=.bilibili.com; Path=/; Secure";
                    return Task.FromResult(result);
                }
                return Transport.Json(new JObject { ["code"]=0,
                    [request.RequestUri.AbsolutePath.StartsWith("/pgc/",StringComparison.Ordinal)?"result":"data"] = response(request).DeepClone() }.ToString());
            });
        }
        private static async Task Login(BilibiliClient api)
        {
            var refresh=typeof(BilibiliClient).GetMethod("RefreshProfileAsync",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance);
            await (Task)refresh.Invoke(api,new object[]{CancellationToken.None});
        }
        private static List<PlaybackInfo> Chain(PlaybackInfo head)
        {
            var seen=new HashSet<PlaybackInfo>();var chain=new List<PlaybackInfo>();
            for(var item=head; item!=null;item=item.CodecFallback)
            { Check(seen.Add(item),"fallback chain has no cycles");chain.Add(item); }
            return chain;
        }
        public static async Task<string> Run()
        {
            // Regression from BV1ct4y1n7t9 on Xbox: the API declares 62.5fps,
            // although Microsoft's type string accepts at most the 60fps tier.
            // Exercise the full production request, selection, model and MPD path.
            foreach (bool pgc in new[] { false, true })
            {
                var near60 = Track(120, "hvc1.1.6.L153.90", "screenshot-4k.m4s", 1861276);
                near60["frame_rate"] = "125/2";
                Stub(request => Data(near60, Track(80, "avc1.640028", "1080.m4s")));
                var queries = new List<string>();
                ProtectionCapabilities.Query = (content, key) =>
                { queries.Add(content); return ProtectionCapabilityResult.Probably; };
                using (var api = new BilibiliClient())
                {
                    await Login(api);
                    var result = pgc ? await api.GetEpisodePlaybackAsync(601, 120) : await api.GetPlaybackAsync(Video, 120);
                    Check(result.QualityId == 120 && result.VideoCodec.StartsWith("hvc1."), "near60 real HEVC4K survives UGC/PGC selection");
                    Check(result.VideoWidth == 3840 && result.VideoHeight == 2160 && result.VideoFrameRate == 62.5,
                        "real dimensions and API declaration are preserved without pretending60fps");
                    Check(result.VideoFrameRateProbe == 60 && result.RequiresNativeFrameRateValidation && !result.NativeFrameRateVerified,
                        "source explicitly requires native verification after a separate60fps baseline query");
                    Check(queries.Any(content => content.Contains("decode-res-x=3840") && content.Contains("decode-fps=60"))
                        && !queries.Any(content => content.Contains("decode-fps=62.5")), "documented baseline query is actually issued for4K");
                    var option = result.AvailableQualities.First(item => item.Id == 120);
                    Check(option.IsVisible && option.IsAvailable, "4K option remains selectable with confirmed baseline and pending native trial");
                    var manifest = XDocument.Parse(result.DashManifest);
                    Check(!manifest.Descendants().Attributes("frameRate").Any(),
                        "untrusted near60 hint is omitted, never replaced with a fabricated60fps MPD attribute");
                    Check(result.PlaybackDiagnostic.Contains("reported-fps=62.5") && result.PlaybackDiagnostic.Contains("probe-fps=60")
                        && result.PlaybackDiagnostic.Contains("nativeTrialRequired=true"), "complete track diagnostics distinguish raw, probe and pending native validation");
                    Check(result.CodecFallback != null && result.CodecFallback.QualityId == 80,
                        "near60 trial retains the actual lower-resolution automatic fallback");
                }
            }
            foreach (string rate in new[] { "60000/1001", "60", "119.993", "119.994", "120", "120.002", "240" })
            {
                var track = Track(120, "hvc1.1.6.L153.90", "rate-boundary.m4s"); track["frame_rate"] = rate;
                Stub(request => Data(track, Track(80, "avc1.640028", "1080.m4s")));
                using (var api = new BilibiliClient())
                {
                    await Login(api); var result = await api.GetPlaybackAsync(Video, 120);
                    if (rate == "120.002" || rate == "240")
                        Check(result.QualityId == 80 && result.QualityFailure == PlaybackQualityFailure.CapabilityUnconfirmed,
                            "rates beyond the explicit120 compatibility bound remain unconfirmed and fall back");
                    else if (rate == "119.993" || rate == "119.994" || rate == "120")
                    {
                        Check(result.QualityId==120 && result.VideoFrameRate==double.Parse(rate,CultureInfo.InvariantCulture)
                            &&result.VideoFrameRateProbe==60&&result.RequiresNativeFrameRateValidation&&!result.NativeFrameRateVerified,
                            "highfps declaration retains raw rate and receives only a documented60 baseline compatibility trial");
                        Check(result.AvailableQualities.Single(item=>item.Id==120).RequiresCompatibilityTrial
                            && !XDocument.Parse(result.DashManifest).Descendants().Attributes("frameRate").Any(),
                            "highfps option is labelled as a trial and MPD never fabricates60 or certifies raw120");
                    }
                    else
                    {
                        Check(result.QualityId == 120 && !result.RequiresNativeFrameRateValidation,
                            "normal fractional60 and60fps retain the ordinary native path");
                        Check(XDocument.Parse(result.DashManifest).Descendants().Attributes("frameRate").Any(attribute => attribute.Value == rate),
                            "trusted normal frame-rate hints remain unchanged in the MPD");
                    }
                }
            }
            foreach (bool pgc in new[] { false, true })
            {
                var menuTrack = Track(120, "hvc1.1.6.L153.90", "default-menu-4k.m4s", 1861276);
                menuTrack["frame_rate"] = "62.5";
                var menuData = Data(menuTrack, Track(80, "avc1.640028", "default-1080.m4s")); menuData["quality"] = 80;
                Stub(request => menuData);
                using (var api = new BilibiliClient())
                {
                    await Login(api);
                    var result = pgc ? await api.GetEpisodePlaybackAsync(601, 80) : await api.GetPlaybackAsync(Video, 80);
                    Check(result.QualityId == 80 && !result.RequiresNativeFrameRateValidation,
                        "ordinary default1080 playback is not forced into a4K native trial");
                    Check(result.AvailableQualities.Any(item => item.Id == 120 && item.IsVisible && item.IsAvailable),
                        "default1080 settings still offer returned near60 real4K for explicit selection");
                }
            }
            foreach (bool pgc in new[] { false, true })
            {
                var cinema = Track(120, "hvc1.1.6.L153.90", "cinema-3996.m4s");
                cinema["width"] = 3996; cinema["frame_rate"] = "25";
                Stub(request => Data(cinema, Track(80, "avc1.640028", "cinema-1080.m4s")));
                var cinemaQueries = new List<string>();
                ProtectionCapabilities.Query = (content, key) =>
                {
                    cinemaQueries.Add(content);
                    return content.Contains("display-res-x=3996") ? ProtectionCapabilityResult.NotSupported : ProtectionCapabilityResult.Probably;
                };
                using (var api = new BilibiliClient())
                {
                    await Login(api);
                    var result = pgc ? await api.GetEpisodePlaybackAsync(601, 120) : await api.GetPlaybackAsync(Video, 120);
                    Check(result.QualityId == 120 && result.VideoWidth == 3996 && result.VideoHeight == 2160,
                        "DCI-width real4K UGC/PGC remains selectable without changing encoded dimensions");
                    Check(result.HardwareHevcVerified && result.FourKDisplayVerified,
                        "actual3996 decode and UHD output are independently confirmed");
                    Check(cinemaQueries.Any(content => content.Contains("decode-res-x=3996"))
                        && !cinemaQueries.Any(content => content.Contains("display-res-x=3996")),
                        "decoder sees actual sourcewidth while3840 HDMI proof avoids unsupported3996 display query");
                    Check(XDocument.Parse(result.DashManifest).Descendants().Attributes("width").Any(attribute => attribute.Value == "3996"),
                        "MPD still describes the real3996x2160 source, no transcode or substituted1080");
                }
            }
            foreach (var outcome in new[] { "Probably", "NotSupported", "Maybe", "exception" })
            {
                Stub(request=>Data(Track(120,"hvc1.1.6.L153.90","4k.m4s"),Track(80,"avc1.640028","1080.m4s")));
                ProtectionCapabilities.Query=(content,key)=>
                {
                    if(outcome=="exception") throw new InvalidOperationException("fixture unavailable");
                    return outcome=="Probably" ? ProtectionCapabilityResult.Probably : outcome=="NotSupported"
                        ? ProtectionCapabilityResult.NotSupported : ProtectionCapabilityResult.Maybe;
                };
                using(var api=new BilibiliClient())
                {
                    await Login(api);var result=await api.GetPlaybackAsync(Video,120);
                    var option=result.AvailableQualities.First(item=>item.Id==120);
                    Check(result.QualityId==(outcome=="Probably"?120:80),"Probably authorizes real track; other results do not");
                    Check(option.IsVisible==(outcome!="NotSupported") && option.IsAvailable==(outcome=="Probably"),"real NotSupported hides; Maybe/exception remain pending");
                    if(outcome!="Probably") Check(result.QualityFailure==(outcome=="NotSupported"?PlaybackQualityFailure.DeviceUnsupported:PlaybackQualityFailure.CapabilityUnconfirmed),
                        "device refusal and unknown capability keep distinct classifications");
                }
            }
            foreach (bool rejectedHdr in new[] { false,true })
            {
                Stub(request =>
                {
                    var sdr=Track(120,request.RequestUri.Query.Contains("fnval=4048")?"av01.0.13M.08":"hvc1.1.6.L153.90","sdr.m4s");
                    sdr["bits_per_channel"]=8;
                    if(!rejectedHdr || request.RequestUri.Query.Contains("fnval=4048")) return Data(sdr,Track(80,"avc1.640028","1080.m4s"));
                    var hdr=Track(120,"hvc1.2.4.L153.B0","hdr.m4s");hdr["bits_per_channel"]=10;hdr["hdr_type"]="hdr10";
                    return Data(hdr,sdr,Track(80,"avc1.640028","1080.m4s"));
                });
                ProtectionCapabilities.Query=(content,key)=> content.Contains("av01") || content.Contains("codecs=\"avc1")
                    || content.Contains("decode-bpc=10") && !content.Contains("hdr=1") ? ProtectionCapabilityResult.Probably : ProtectionCapabilityResult.NotSupported;
                using(var api=new BilibiliClient())
                {
                    await Login(api);var result=await api.GetPlaybackAsync(Video,120);
                    Check(result.QualityId==120 && result.VideoCodec.StartsWith("av01."),"rejected HDR cannot block SDR AV1 negotiation");
                    Check(Transport.Requests.Count(request=>request.Uri.AbsolutePath.Contains("playurl"))==2,"alternate codec negotiation is bounded to one extra request");
                }
            }
            var invalidHdr=Track(125,"hvc1.1.6.L153.90","hdr-without-main10.m4s");
            var invalidHdrData=Data(invalidHdr,Track(80,"avc1.640028","1080.m4s"));
            invalidHdrData["accept_quality"]=new JArray(125,80);
            Stub(request=>invalidHdrData,true);
            int hdrQueries=0;
            ProtectionCapabilities.Query=(content,key)=>{++hdrQueries;return ProtectionCapabilityResult.Probably;};
            using(var api=new BilibiliClient())
            {
                await Login(api);var result=await api.GetPlaybackAsync(Video,125);
                Check(result.QualityId==80 && result.QualityFailure==PlaybackQualityFailure.SourceUnsupported,"implicit8-bit HDR10 metadata is a source issue rather than device refusal");
                Check(hdrQueries==0 && result.PlaybackDiagnostic.Contains("validation=hdr-bit-depth")
                    && result.PlaybackDiagnostic.Contains("query=not-issued"),"invalid HDR10 depth never claims to have issued a hardware query");
            }
            var first=Track(120,"hvc1.1.6.L153.90","first.m4s",15000000);
            var second=Track(120,"hvc1.2.4.L153.B0","second.m4s",14000000);
            var av1=Track(120,"av01.0.13M.08","av1.m4s",13000000);
            var duplicate=(JObject)first.DeepClone();duplicate["base_url"]="https://other.bilivideo.com/first.m4s?auth=other-fixture";
            var data=Data(first,second,av1,duplicate,Track(120,"avc1.640033","avc4k.m4s"),Track(80,"avc1.640028","1080.m4s"));
            foreach(bool pgc in new[]{false,true})
            {
                Stub(request=>data);
                using(var api=new BilibiliClient())
                {
                    await Login(api);var messages=new List<string>();api.PlaybackDiagnostic+=messages.Add;
                    var result=pgc?await api.GetEpisodePlaybackAsync(601,120):await api.GetPlaybackAsync(Video,120);
                    var chain=Chain(result);
                    Check(chain.Count==4 && chain.Take(3).All(item=>item.QualityId==120) && chain[3].QualityId==80,"UGC/PGC try all distinct supported same-tier sources before lower resolution");
                    Check(chain[0].VideoCodec.StartsWith("hvc1.1") && chain[1].VideoCodec.StartsWith("hvc1.2") && chain[2].VideoCodec.StartsWith("av01"),"confirmed HEVC precedes compatible alternate encoding");
                    Check(chain[0].VideoTrackIndex==0 && chain[1].VideoTrackIndex==1 && chain[2].VideoTrackIndex==2,"native candidates retain raw track indices");
                    Check(messages.Count(value=>value.StartsWith("selected "))==1 && messages.Count(value=>value.StartsWith("candidate qn="))==4,"unplayed fallback candidates are not falsely logged as selected");
                    Check(result.PlaybackDiagnostic.Contains("track index=4") && result.PlaybackDiagnostic.Contains("Xbox 的 H.264")
                        && result.PlaybackDiagnostic.Contains("probe index=0") && result.PlaybackDiagnostic.Contains("Probably"),"every rejection and actual capability result is recorded");
                    Check(!result.PlaybackDiagnostic.Contains("fixture-secret") && !result.PlaybackDiagnostic.Contains("https://"),"track logs exclude authenticated media URLs");
                }
            }
            var limited = Enumerable.Range(0,15).Select(index=>Track(120,"hvc1.1.6.L153.90","same-"+index+".m4s",12000000+index)).ToList();
            limited.Add(Track(80,"avc1.640028","1080.m4s"));
            Stub(request=>Data(limited.ToArray()));
            using(var api=new BilibiliClient())
            {
                await Login(api);var messages=new List<string>();api.PlaybackDiagnostic+=messages.Add;var result=await api.GetPlaybackAsync(Video,120);
                var chain=Chain(result);Check(chain.Count==13 && chain.Take(12).All(item=>item.QualityId==120) && chain.Last().QualityId==80,"candidate retry budget preserves final lower-resolution fallback");
                Check(messages.Any(value=>value.Contains("omittedByBudget=3")),"retry budget omissions are explicit");
                Check(result.PlaybackDiagnostic.Contains("track index=15"),"complete catalogue remains logged beyond retry budget");
            }
            var drm=Track(120,"hvc1.1.6.L153.90","encrypted.m4s");drm["is_drm"]=true;
            Stub(request=>Data(drm,Track(120,"hvc1.1.6.L153.90","clear.m4s"),Track(80,"avc1.640028","1080.m4s")));
            using(var api=new BilibiliClient())
            {
                await Login(api);var result=await api.GetEpisodePlaybackAsync(601,120);
                Check(result.VideoTrackIndex==1 && result.QualityId==120,"encrypted track does not invalidate clear same-tier PGC track");
                Check(result.PlaybackDiagnostic.Contains("track index=0 qn=120") && result.PlaybackDiagnostic.Contains("版权保护")
                    && result.PlaybackDiagnostic.Contains("decode=not_requested"),"pre-filtered DRM track is logged as rejected without claiming a hardware query");
            }
            return "PASS: "+_checks+" production multi-track UGC/PGC selection, independent capabilities, fallback and diagnostic assertions (fixture transport).";
        }
    }
}
