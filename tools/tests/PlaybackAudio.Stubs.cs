// TEST ONLY: production audio selection, installed-codec query and MPD; no network.
namespace BiliXbox.PlaybackAudioTests
{
    using BiliXbox.Services;
    using BiliXbox.VideoDetailTests;
    using Windows.Media.Core;
    public static class Tests
    {
        private static int _checks, _queries;
        private static readonly VideoDetail Video = new VideoDetail { Bvid = "BV1xx411c7mD", Aid = 100, Cid = 200 };
        private static void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); ++_checks; }
        private static JObject Track(int id, string codec, string path) => new JObject
        {
            ["id"] = id, ["codecs"] = codec, ["base_url"] = "https://cdn.bilivideo.com/" + path + "?auth=fixture-media-secret",
            ["backup_url"] = new JArray("https://backup.bilivideo.com/" + path), ["bandwidth"] = id == 120 ? 12000000 : 300000,
            ["width"] = id == 120 ? 3840 : 1920, ["height"] = id == 120 ? 2160 : 1080,
            ["frame_rate"] = "30", ["segment_base"] = new JObject { ["initialization"] = "0-1199", ["index_range"] = "1200-1499" }
        };
        private static JObject Data()
        {
            var dolby = Track(30250, "ec-3", "dolby.m4s"); dolby["channels"] = 6; dolby["sample_rate"] = 48000;
            return new JObject
            {
                ["quality"] = 120, ["accept_quality"] = new JArray(120, 80), ["accept_description"] = new JArray("4K 超高清", "1080P"),
                ["dash"] = new JObject
                {
                    ["duration"] = 97, ["video"] = new JArray(Track(120, "hvc1.1.6.L153.90", "4k.m4s"), Track(80, "avc1.640028", "1080.m4s")),
                    ["audio"] = new JArray(Track(30280, "mp4a.40.2", "aac.m4s")),
                    ["dolby"] = new JObject { ["type"] = 1, ["audio"] = new JArray(dolby) }
                }
            };
        }
        private static JObject HiResData(bool array = false, bool aliases = false)
        {
            var data = Data();
            var flac = Track(30251, aliases ? "flac" : "fLaC", "hires.m4s");
            flac[aliases ? "sampleRate" : "sample_rate"] = 96000;
            flac[aliases ? "bitsPerSample" : "bits_per_sample"] = 24;
            flac[aliases ? "channelCount" : "channels"] = 2;
            if (aliases)
            {
                flac["baseUrl"] = flac["base_url"]; flac.Remove("base_url");
                flac["SegmentBase"] = new JObject { ["Initialization"] = "0-1199", ["indexRange"] = "1200-1499" };
                flac.Remove("segment_base");
            }
            data["dash"]["flac"] = new JObject { ["display"] = true, ["audio"] = array ? (JToken)new JArray(flac) : flac };
            return data;
        }
        private static void Stub(JObject data, bool logged = true, bool premium = true,
            Func<string, IReadOnlyList<CodecInfo>> codecs = null)
        {
            _queries = 0;
            Windows.System.Profile.AnalyticsInfo.VersionInfo.DeviceFamily = "Windows.Desktop";
            Windows.Graphics.Display.Core.HdmiDisplayInformation.Current = null;
            Windows.Media.Protection.ProtectionCapabilities.Query = (content, system) => Windows.Media.Protection.ProtectionCapabilityResult.Probably;
            CodecQuery.Query = (kind, category, subtype) =>
            {
                ++_queries;
                Check(kind == CodecKind.Audio && category == CodecCategory.Decoder, "installed decoder query uses real audio/decoder categories");
                return codecs == null ? new[] { new CodecInfo() } : codecs(subtype);
            };
            Transport.Reset((request, ct) =>
            {
                Check(request.Method.Method == "GET", "audio selection does not write account state");
                if (request.RequestUri.AbsolutePath == "/x/web-interface/nav")
                {
                    var nav = new JObject { ["code"] = logged ? 0 : -101, ["data"] = new JObject
                    {
                        ["isLogin"] = logged, ["mid"] = logged ? 42 : 0, ["uname"] = "fixture account",
                        ["vip"] = new JObject { ["status"] = premium ? 1 : 0 },
                        ["wbi_img"] = new JObject { ["img_url"] = "https://i0.hdslb.com/bfs/wbi/7cd084941338484aae1ad9425b84077c.png",
                            ["sub_url"] = "https://i0.hdslb.com/bfs/wbi/4932caff0ff746eab6f01bf08b70ac45.png" }
                    }};
                    var response = new HttpResponseMessage { Content = new HttpStringContent(nav.ToString()) };
                    if (logged) response.Headers["Set-Cookie"] = "SESSDATA=fixture-only; Domain=.bilibili.com; Path=/; Secure";
                    return Task.FromResult(response);
                }
                if (logged)
                {
                    string cookie; Check(request.Headers.TryGetValue("Cookie", out cookie) && cookie.Contains("SESSDATA=fixture-only"), "Dolby request retains current account session");
                }
                Check((request.RequestUri.Query.Contains("fnval=2000") || request.RequestUri.Query.Contains("fnval=4048"))
                    && request.RequestUri.Query.Contains("fourk=1"), "DASH retains bounded video format negotiation while requesting actual audio catalogue");
                return Transport.Json(new JObject { ["code"] = 0,
                    [request.RequestUri.AbsolutePath.StartsWith("/pgc/", StringComparison.Ordinal) ? "result" : "data"] = data.DeepClone() }.ToString());
            });
        }
        private static async Task Login(BilibiliClient client)
        {
            var method = typeof(BilibiliClient).GetMethod("RefreshProfileAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            await (Task)method.Invoke(client, new object[] { CancellationToken.None });
        }
        public static async Task<string> Run()
        {
            Stub(Data());
            using (var api = new BilibiliClient())
            {
                await Login(api); var playback = await api.GetPlaybackAsync(Video, 120);
                Check(playback.AudioMode == PlaybackAudioMode.Standard && playback.AudioCodec == "mp4a.40.2"
                    && playback.AvailableAudioModes.Any(option => option.Mode == PlaybackAudioMode.Dolby && option.IsAvailable), "standard default stays AAC while exposing an available real Dolby track");
                Check(!playback.DashManifest.Contains("ec-3") && playback.AudioFallback == null, "AAC is never relabelled as Dolby or given a fake Dolby fallback");
                playback = await api.GetPlaybackAsync(Video, 120, CancellationToken.None, PlaybackAudioMode.Dolby);
                Check(playback.AudioMode == PlaybackAudioMode.Dolby && playback.RequestedAudioMode == PlaybackAudioMode.Dolby
                    && playback.AudioCodec == "ec-3" && playback.AudioChannels == 6 && playback.AudioNotice.Length == 0,
                    "authorized Dolby selection retains real EAC3 codec and supplied six channels");
                var manifest = XDocument.Parse(playback.DashManifest); XNamespace ns = "urn:mpeg:dash:schema:mpd:2011";
                var audio = manifest.Descendants(ns + "Representation").Single(item => item.Attribute("codecs")?.Value == "ec-3");
                Check(audio.Element(ns + "BaseURL").Value.Contains("dolby.m4s") && audio.Attribute("audioSamplingRate")?.Value == "48000"
                    && audio.Element(ns + "AudioChannelConfiguration")?.Attribute("value")?.Value == "6", "MPD uses real Dolby URL, sampling rate and supplied channel layout");
                Check(manifest.Root.Attribute("minBufferTime").Value == "PT3S", "DASH advertises a three-second minimum buffer");
                Check(playback.AudioFallback != null && playback.AudioFallback.QualityId == 120
                    && playback.AudioFallback.VideoWidth == playback.VideoWidth && playback.AudioFallback.AudioCodec == "mp4a.40.2"
                    && playback.AudioFallback.AudioFallback == null, "first Dolby fallback retains video quality and uses genuine AAC once");
                Check(playback.CodecFallback.QualityId == 80 && playback.CodecFallback.AudioMode == PlaybackAudioMode.Standard
                    && ReferenceEquals(playback.AudioFallback.CodecFallback, playback.CodecFallback), "audio fallback preserves bounded video-codec fallback chain without cycles");
                Check(!playback.AudioCapabilityDiagnostic.Contains("fixture-only") && !playback.AudioCapabilityDiagnostic.Contains("https://")
                    && !playback.AudioCapabilityDiagnostic.Contains("Atmos"), "audio logs exclude tokens, media URLs and unsupported Atmos-output claims");
            }
            foreach (bool logged in new[] { false, true })
            {
                Stub(Data(), logged, false);
                using (var api = new BilibiliClient())
                {
                    if (logged) await Login(api);
                    var playback = await api.GetPlaybackAsync(Video, 120, CancellationToken.None, PlaybackAudioMode.Dolby);
                    var option = playback.AvailableAudioModes.Single(item => item.Mode == PlaybackAudioMode.Dolby);
                    Check(playback.AudioMode == PlaybackAudioMode.Standard && !option.IsAvailable
                        && option.Reason == (logged ? "需要大会员" : "需要登录") && _queries == 0,
                        "login and membership requirements gate Dolby selection before installed-decoder query");
                }
            }
            foreach (string invalid in new[] { "missing", "null-info", "null-audio", "aac-only", "segment", "encrypted", "unsafe-url" })
            {
                var data = Data(); var dolby = data["dash"]["dolby"]["audio"][0];
                if (invalid == "missing") ((JObject)data["dash"]).Remove("dolby");
                if (invalid == "null-info") data["dash"]["dolby"] = JValue.CreateNull();
                if (invalid == "null-audio") data["dash"]["dolby"]["audio"] = JValue.CreateNull();
                if (invalid == "aac-only") dolby["codecs"] = "mp4a.40.2";
                if (invalid == "segment") dolby["segment_base"]["index_range"] = "not-a-range";
                if (invalid == "encrypted") dolby["is_drm"] = true;
                if (invalid == "unsafe-url") dolby["base_url"] = "file:///not-media";
                Stub(data);
                using (var api = new BilibiliClient())
                {
                    await Login(api); var playback = await api.GetPlaybackAsync(Video, 120, CancellationToken.None, PlaybackAudioMode.Dolby);
                    Check(playback.AudioMode == PlaybackAudioMode.Standard && playback.AudioNotice.Contains("未返回")
                        && !playback.AvailableAudioModes.Single(item => item.Mode == PlaybackAudioMode.Dolby).IsAvailable,
                        "invalid/missing Dolby data keeps standard audio and explains missing real source: " + invalid);
                }
            }
            foreach (bool unknown in new[] { false, true })
            {
                Stub(Data(), codecs: subtype => { if (unknown) throw new InvalidOperationException("fixture codec service unavailable"); return Array.Empty<CodecInfo>(); });
                using (var api = new BilibiliClient())
                {
                    await Login(api); var playback = await api.GetPlaybackAsync(Video, 120, CancellationToken.None, PlaybackAudioMode.Dolby);
                    Check(playback.AudioMode == PlaybackAudioMode.Standard && playback.AudioNotice.Contains(unknown ? "尚未确认" : "解码器"),
                        "decoder absence and query failures cannot claim Dolby support");
                }
            }
            Stub(Data());
            using (var api = new BilibiliClient())
            {
                await Login(api); var playback = await api.GetEpisodePlaybackAsync(300, 120, CancellationToken.None, PlaybackAudioMode.Dolby);
                Check(playback.AudioMode == PlaybackAudioMode.Dolby && playback.AudioFallback?.QualityId == 120
                    && Transport.Requests.Last().Uri.AbsolutePath == "/pgc/player/web/playurl", "PGC Dolby uses authorized episode endpoint and same-quality AAC fallback");
            }
            await HiResCases();
            await FrameRateProvenanceCases();
            return "PASS: " + _checks + " production Hi-Res FLAC/Dolby/AAC API, authorization, codec-query, DASH, raw-rate provenance and fallback assertions; fixture transport only, no bit-perfect or hardware passthrough claim.";
        }

        private static async Task HiResCases()
        {
            Stub(HiResData());
            using (var api = new BilibiliClient())
            {
                await Login(api); var playback = await api.GetPlaybackAsync(Video,120);
                Check(playback.AudioMode == PlaybackAudioMode.Standard && playback.AudioCodec == "mp4a.40.2"
                    && playback.AvailableAudioModes.Count == 3
                    && playback.AvailableAudioModes.Single(item=>item.Mode==PlaybackAudioMode.HiRes).IsAvailable,
                    "default stays genuine AAC and advertises independently validated Hi-Res as a third mode");
                Check(playback.AudioSampleRate==0 && playback.AudioBitsPerSample==0 && playback.AudioFallback==null,
                    "AAC does not inherit FLAC metadata or a fabricated lossless fallback");
            }
            foreach (bool pgc in new[] {false,true})
            foreach (bool aliases in new[] {false,true})
            {
                bool queriedFlac = false;
                Stub(HiResData(array:aliases, aliases:aliases), codecs:subtype=>
                { queriedFlac |= subtype==CodecSubtypes.AudioFormatFlac; return new[] {new CodecInfo()}; });
                using(var api=new BilibiliClient())
                {
                    await Login(api);
                    var playback=pgc ? await api.GetEpisodePlaybackAsync(300,120,CancellationToken.None,PlaybackAudioMode.HiRes)
                        : await api.GetPlaybackAsync(Video,120,CancellationToken.None,PlaybackAudioMode.HiRes);
                    Check(queriedFlac && playback.AudioMode==PlaybackAudioMode.HiRes && playback.RequestedAudioMode==PlaybackAudioMode.HiRes
                        && string.Equals(playback.AudioCodec,"flac",StringComparison.OrdinalIgnoreCase),
                        "UGC/PGC Hi-Res requires actual FLAC decoder and supplied FLAC stream, with object/array and codec aliases");
                    Check(playback.AudioSampleRate==96000 && playback.AudioBitsPerSample==24 && playback.AudioChannels==2
                        && playback.AudioNotice.Length==0, "only supplied sample rate, bit depth and channels are exposed");
                    var mpd=XDocument.Parse(playback.DashManifest);XNamespace ns="urn:mpeg:dash:schema:mpd:2011";
                    var audio=mpd.Descendants(ns+"Representation").Single(item=>item.Attribute("codecs")?.Value=="fLaC");
                    Check(audio.Element(ns+"BaseURL").Value.Contains("hires.m4s") && audio.Attribute("audioSamplingRate")?.Value=="96000"
                        && audio.Element(ns+"AudioChannelConfiguration")?.Attribute("value")?.Value=="2"
                        && audio.Element(ns+"SegmentBase").Attribute("indexRange").Value=="1200-1499",
                        "FLAC MPD preserves real URL/ranges and supplied rate/layout; codec alias uses MP4 FLAC sample entry");
                    Check(playback.VideoWidth==3840 && playback.VideoHeight==2160 && playback.QualityId==120
                        && playback.AudioFallback?.AudioMode==PlaybackAudioMode.Standard
                        && playback.AudioFallback.VideoCodec==playback.VideoCodec && playback.AudioFallback.Url==playback.Url,
                        "Hi-Res audio failure first selects AAC with identical real 4K video");
                    Check(playback.AudioFallback.AudioFallback==null && playback.CodecFallback?.QualityId==80
                        && ReferenceEquals(playback.AudioFallback.CodecFallback,playback.CodecFallback),
                        "same-video AAC precedes bounded lower-video fallback without cycles");
                    Check(playback.AudioCapabilityDiagnostic.Contains("sampleRate=96000")
                        && playback.AudioCapabilityDiagnostic.Contains("bitsPerSample=24")
                        && !playback.AudioCapabilityDiagnostic.Contains("fixture-media-secret")
                        && !playback.AudioCapabilityDiagnostic.Contains("https://"), "Hi-Res diagnostics report metadata and exclude signed URLs");
                    Check(Transport.Requests.Last().Uri.AbsolutePath==(pgc?"/pgc/player/web/playurl":"/x/player/wbi/playurl"),
                        "Hi-Res reuses authorized playback endpoint for its content type");
                }
            }
            foreach(bool logged in new[]{false,true})
            {
                Stub(HiResData(),logged,false);
                using(var api=new BilibiliClient())
                {
                    if(logged) await Login(api);
                    var playback=await api.GetPlaybackAsync(Video,120,CancellationToken.None,PlaybackAudioMode.HiRes);
                    var option=playback.AvailableAudioModes.Single(item=>item.Mode==PlaybackAudioMode.HiRes);
                    Check(!option.IsAvailable && option.RequiresLogin && option.RequiresPremium
                        && option.Reason==(logged?"需要大会员":"需要登录") && _queries==0
                        && playback.AudioMode==PlaybackAudioMode.Standard, "Hi-Res login/VIP gates apply before FLAC decoder query");
                }
            }
            foreach(string invalid in new[]{"missing","null-info","null-audio","display-only","empty-array","aac","codec-suffix","range","unsafe-url","drm","widevine"})
            {
                var data=HiResData();var flac=data["dash"]["flac"]["audio"];
                if(invalid=="missing") ((JObject)data["dash"]).Remove("flac");
                if(invalid=="null-info") data["dash"]["flac"]=JValue.CreateNull();
                if(invalid=="null-audio") data["dash"]["flac"]["audio"]=JValue.CreateNull();
                if(invalid=="display-only") ((JObject)data["dash"]["flac"]).Remove("audio");
                if(invalid=="empty-array") data["dash"]["flac"]["audio"]=new JArray();
                if(invalid=="aac") flac["codecs"]="mp4a.40.2";
                if(invalid=="codec-suffix") flac["codecs"]="flac.fake";
                if(invalid=="range") flac["segment_base"]["index_range"]="invalid";
                if(invalid=="unsafe-url") flac["base_url"]="file:///not-media";
                if(invalid=="drm") flac["is_drm"]=true;
                if(invalid=="widevine") flac["widevine_pssh"]="fixture-protected";
                Stub(data,codecs:subtype=>{Check(subtype!=CodecSubtypes.AudioFormatFlac,"invalid FLAC cannot reach decoder query");return new[]{new CodecInfo()};});
                using(var api=new BilibiliClient())
                {
                    await Login(api);var playback=await api.GetPlaybackAsync(Video,120,CancellationToken.None,PlaybackAudioMode.HiRes);
                    Check(playback.AudioMode==PlaybackAudioMode.Standard && playback.AudioNotice.Contains("未返回")
                        && !playback.AvailableAudioModes.Single(item=>item.Mode==PlaybackAudioMode.HiRes).IsAvailable,
                        "missing/invalid/encrypted FLAC keeps actual AAC with clear source reason: "+invalid);
                }
            }
            foreach(bool unknown in new[]{false,true})
            {
                Stub(HiResData(),codecs:subtype=>
                {
                    if(subtype!=CodecSubtypes.AudioFormatFlac) return new[]{new CodecInfo()};
                    if(unknown) throw new InvalidOperationException("fixture FLAC decoder service unavailable");
                    return Array.Empty<CodecInfo>();
                });
                using(var api=new BilibiliClient())
                {
                    await Login(api);var playback=await api.GetPlaybackAsync(Video,120,CancellationToken.None,PlaybackAudioMode.HiRes);
                    Check(playback.AudioMode==PlaybackAudioMode.Standard && playback.AudioNotice.Contains(unknown?"尚未确认":"解码器"),
                        "FLAC decoder refusal and unconfirmed query stay distinct from source absence");
                }
            }
            var unknownData=HiResData();var unknownAudio=(JObject)unknownData["dash"]["flac"]["audio"];
            unknownAudio.Remove("sample_rate");unknownAudio.Remove("bits_per_sample");unknownAudio.Remove("channels");
            Stub(unknownData);
            using(var api=new BilibiliClient())
            {
                await Login(api);var playback=await api.GetPlaybackAsync(Video,120,CancellationToken.None,PlaybackAudioMode.HiRes);
                Check(playback.AudioMode==PlaybackAudioMode.HiRes && playback.AudioSampleRate==0 && playback.AudioBitsPerSample==0
                    && playback.AudioChannels==0 && !playback.DashManifest.Contains("audioSamplingRate="),
                    "real FLAC remains selectable without fabricating 24bit/96kHz/channel metadata");
            }
            var noAac=HiResData();noAac["dash"]["audio"]=JValue.CreateNull();
            Stub(noAac);
            using(var api=new BilibiliClient())
            {
                await Login(api);var playback=await api.GetPlaybackAsync(Video,120,CancellationToken.None,PlaybackAudioMode.HiRes);
                Check(playback.AudioMode==PlaybackAudioMode.HiRes && playback.AudioFallback==null
                    && !playback.AvailableAudioModes.Single(item=>item.Mode==PlaybackAudioMode.Standard).IsAvailable,
                    "FLAC-only source never invents a standard AAC fallback");
            }
        }

        private static async Task FrameRateProvenanceCases()
        {
            var data=Data();var video=data["dash"]["video"][0];video["frameRate"]="125/2";video["frame_rate"]="30";
            Stub(data);
            using(var api=new BilibiliClient())
            {
                await Login(api);var playback=await api.GetPlaybackAsync(Video,120);
                Check(playback.VideoFrameRate==62.5 && playback.PlaybackDiagnostic.Contains("frameRateRaw=125/2 frame_rateRaw=30 fpsField=frameRate aliasConflict=true"),
                    "diagnostic retains conflicting raw rate aliases and parser provenance without changing existing priority");
            }
            data=Data();video=data["dash"]["video"][0];video["frameRate"]="30";video["frame_rate"]="https://private.invalid/?session=fixture-raw-secret\nspoof=Probably";
            Stub(data);
            using(var api=new BilibiliClient())
            {
                await Login(api);var playback=await api.GetPlaybackAsync(Video,120);
                Check(playback.VideoFrameRate==30 && playback.PlaybackDiagnostic.Contains("frameRateRaw=30 frame_rateRaw=<invalid>")
                    && !playback.PlaybackDiagnostic.Contains("fixture-raw-secret") && !playback.PlaybackDiagnostic.Contains("spoof=Probably"),
                    "malformed raw aliases cannot inject URLs, secrets or forged diagnostic lines");
            }
            data=Data();video=data["dash"]["video"][0];video["frame_rate"]=new string('1',1000);
            Stub(data);
            using(var api=new BilibiliClient())
            {
                await Login(api);var playback=await api.GetPlaybackAsync(Video,80);
                Check(playback.PlaybackDiagnostic.Contains("frameRateRaw=<missing> frame_rateRaw=<invalid> fpsField=frame_rate")
                    && !playback.PlaybackDiagnostic.Contains(new string('1',100)), "oversized rate metadata is bounded in complete catalogue logs");
            }
        }
    }
}
