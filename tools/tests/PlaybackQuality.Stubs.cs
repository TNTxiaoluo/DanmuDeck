// TEST ONLY: production playback service and MPD builder, deterministic transport.
namespace BiliXbox.PlaybackQualityTests
{
    using BiliXbox.Services;
    using BiliXbox.VideoDetailTests;
    public static class Tests
    {
        private static int _passed;
        private static readonly VideoDetail Video = new VideoDetail { Bvid = "BV1spz8B4EXv", Aid = 123456, Cid = 987654 };
        private static void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); _passed++; }
        private static JObject Nav(bool logged, bool premium) => new JObject { ["code"] = logged ? 0 : -101, ["data"] = new JObject
        {
            ["isLogin"] = logged, ["mid"] = logged ? 1 : 0, ["uname"] = "fixture account", ["vip"] = new JObject { ["status"] = premium ? 1 : 0 },
            ["wbi_img"] = new JObject { ["img_url"] = "https://i0.hdslb.com/bfs/wbi/7cd084941338484aae1ad9425b84077c.png", ["sub_url"] = "https://i0.hdslb.com/bfs/wbi/4932caff0ff746eab6f01bf08b70ac45.png" }
        }};
        private static void Stub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler, bool logged = false, bool premium = false)
        {
            Windows.System.Profile.AnalyticsInfo.VersionInfo.DeviceFamily = "Windows.Desktop";
            Windows.Graphics.Display.Core.HdmiDisplayInformation.Current = null;
            Windows.Graphics.Display.Core.HdmiDisplayInformation.ViewQueries = 0;
            Windows.Graphics.Display.Core.HdmiDisplayInformation.ThrowView = false;
            Windows.Graphics.Display.Core.HdmiDisplayInformation.ThrowModes = false;
            Windows.Media.Protection.ProtectionCapabilities.Query = (contentType, keySystem) => Windows.Media.Protection.ProtectionCapabilityResult.Probably;
            Transport.Reset((request, token) =>
            {
                if (request.RequestUri.AbsolutePath == "/x/web-interface/nav")
                {
                    var response = new HttpResponseMessage { Content = new HttpStringContent(Nav(logged, premium).ToString()) };
                    if (logged) response.Headers["Set-Cookie"] = "SESSDATA=fixture-only; Domain=.bilibili.com; Path=/; Secure";
                    return Task.FromResult(response);
                }
                Check(request.Method.Method == "GET" && request.RequestUri.Host == "api.bilibili.com", "playback is read-only");
                if (logged)
                {
                    string cookies;
                    Check(request.Headers.TryGetValue("Cookie", out cookies) && cookies.Contains("SESSDATA=fixture-only"),
                        "signed playback request retains the authenticated session cookie");
                }
                var query = request.RequestUri.Query.TrimStart('?').Split('&').Select(pair => pair.Split(new[] { '=' }, 2)).ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]));
                Check(query["bvid"] == Video.Bvid && query["cid"] == "987654", "video and part retained");
                if (request.RequestUri.AbsolutePath == "/x/player/wbi/playurl")
                {
                    Check(int.Parse(query["qn"]) > 0 && query["fnval"] == "2000" && query["fourk"] == "1" && !query.ContainsKey("platform") && query.ContainsKey("w_rid") && query.ContainsKey("wts"), "signed DASH enables HD/UHD without HTML5 ceiling");
                    Check((int.Parse(query["fnval"]) & 2048) == 0, "codec negotiation never advertises unsupported AV1");
                }
                else Check(request.RequestUri.AbsolutePath == "/x/player/playurl" && query["fnval"] == "1", "only justified compatibility request uses old endpoint");
                return handler(request, token);
            });
        }
        private static void Stub(JObject dash, JObject fallback = null, bool logged = false, bool premium = false)
            => Stub((request, token) => Transport.Json((request.RequestUri.AbsolutePath == "/x/player/wbi/playurl" ? dash : fallback ?? new JObject { ["code"] = -404 }).ToString()), logged, premium);
        private static int PlaybackRequests => Transport.Requests.Count(item => item.Uri.AbsolutePath.Contains("playurl"));
        private static int LastRequestedQuality => int.Parse(Transport.Requests.Last(item => item.Uri.AbsolutePath == "/x/player/wbi/playurl").Uri.Query.TrimStart('?').Split('&')
            .Select(pair => pair.Split(new[] { '=' }, 2)).First(pair => pair[0] == "qn")[1]);
        private static async Task Login(BilibiliClient client)
        {
            var method = typeof(BilibiliClient).GetMethod("RefreshProfileAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            await (Task)method.Invoke(client, new object[] { CancellationToken.None });
        }
        private static JObject Track(int id, string codec, string kind = "video") => new JObject
        {
            ["id"] = id, ["codecs"] = codec, ["bandwidth"] = kind == "audio" ? 173230 : id * 10000,
            ["width"] = kind == "video" && id == 120 ? 3840 : 1920, ["height"] = kind == "video" && id == 120 ? 2160 : 1080, ["frameRate"] = "30000/1001", ["mimeType"] = kind + "/mp4",
            ["baseUrl"] = "https://cdn.example.test/" + kind + "-" + id + ".m4s?token=fixture&flag=1",
            ["SegmentBase"] = new JObject { ["Initialization"] = "0-927", ["indexRange"] = "928-1811" }
        };
        private static JObject Dash(params int[] provided)
        {
            var ids = new[] { 120, 112, 80, 64, 32, 16 };
            return new JObject { ["code"] = 0, ["data"] = new JObject
            {
                ["quality"] = 64, ["format"] = "flv720", ["accept_quality"] = new JArray(ids), ["accept_description"] = new JArray(ids.Select(id => "Source tier " + id)),
                ["support_formats"] = new JArray(ids.Select(id => new JObject { ["quality"] = id, ["format"] = "flv", ["new_description"] = "Source tier " + id })),
                ["dash"] = new JObject { ["duration"] = 353.25, ["video"] = new JArray(provided.Select(id => Track(id, "avc1.640032"))), ["audio"] = new JArray(Track(30280, "mp4a.40.2", "audio")) }
            }};
        }
        private static JObject Mp4(int actual = 64) => new JObject { ["code"] = 0, ["data"] = new JObject
        {
            ["quality"] = actual, ["format"] = "mp4720", ["accept_quality"] = new JArray(64, 16), ["accept_description"] = new JArray("720P", "360P"),
            ["durl"] = new JArray(new JObject { ["url"] = "https://cdn.example.test/muxed.mp4", ["backup_url"] = new JArray("http://backup.example.test/muxed.mp4", "file:///denied") })
        }};
        private static async Task Error(Func<Task> run, int code, string name)
        {
            try { await run(); } catch (BilibiliApiException ex) { Check(ex.Code == code, name); return; }
            throw new Exception("FAIL: expected error: " + name);
        }
        public static string BuildPublicManifest(string responseJson, bool useHevc = false)
        {
            var dash = JObject.Parse(responseJson)["data"]["dash"];
            var video = ((JArray)dash["video"]).Where(DashManifestBuilder.IsSupportedVideo)
                .Where(item => useHevc
                    ? item["codecs"].ToString().StartsWith("hvc1.", StringComparison.OrdinalIgnoreCase) || item["codecs"].ToString().StartsWith("hev1.", StringComparison.OrdinalIgnoreCase)
                    : item["codecs"].ToString().StartsWith("avc1.", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item["id"].Value<int>()).First();
            var audio = ((JArray)dash["audio"]).Where(DashManifestBuilder.IsSupportedAudio)
                .OrderByDescending(item => item["bandwidth"].Value<int>()).First();
            return DashManifestBuilder.Build(video, audio, dash["duration"].Value<double>());
        }
        private static JObject AdvertisedHdr(JObject response)
        {
            var data = response["data"];
            ((JArray)data["accept_quality"]).Insert(0, 125);
            ((JArray)data["accept_description"]).Insert(0, "HDR10");
            ((JArray)data["support_formats"]).Insert(0, new JObject { ["quality"] = 125, ["new_description"] = "HDR10" });
            return response;
        }
        private static async Task RunCapabilityFiltering()
        {
            foreach (string state in new[] { "unsupported", "maybe", "exception", "decode-only", "4k-sdr", "1080-hdr" })
            {
                Stub(AdvertisedHdr(Dash(80)), logged: true, premium: true);
                Windows.Media.Protection.ProtectionCapabilities.Query = (content, key) =>
                {
                    if (state == "exception") throw new InvalidOperationException("unavailable display");
                    if (state == "maybe") return Windows.Media.Protection.ProtectionCapabilityResult.Maybe;
                    bool supported = state == "4k-sdr" ? !content.Contains("hdr=1")
                        : state == "1080-hdr" ? !content.Contains("decode-res-x=3840")
                        : state == "decode-only" ? !content.Contains("display-res-x=") : false;
                    return supported ? Windows.Media.Protection.ProtectionCapabilityResult.Probably
                        : Windows.Media.Protection.ProtectionCapabilityResult.NotSupported;
                };
                using (var client = new BilibiliClient())
                {
                    await Login(client); var info = await client.GetPlaybackAsync(Video);
                    var uhd = info.AvailableQualities.First(item => item.Id == 120);
                    var hdr = info.AvailableQualities.First(item => item.Id == 125);
                    bool unknown = state == "maybe" || state == "exception";
                    Check(uhd.IsVisible == (state == "4k-sdr" || unknown) && uhd.IsAvailable == (state == "4k-sdr"),
                        "advertised-only 4K is independently filtered before its track is returned: " + state);
                    Check(hdr.IsVisible == (state == "1080-hdr" || unknown) && hdr.IsAvailable == (state == "1080-hdr"),
                        "advertised-only HDR is independently filtered before its track is returned: " + state);
                    Check(info.QualityId == 80 && info.VideoWidth == 1920 && !info.IsHdr10,
                        "capability checks never relabel the actual SDR1080 source: " + state);
                }
            }
            var avc = Dash(120, 80);
            Stub(avc, logged: true, premium: true);
            var queried = new List<string>();
            Windows.Media.Protection.ProtectionCapabilities.Query = (content, key) =>
            {
                queried.Add(content);
                return content.Contains("codecs=\"avc1,") ? Windows.Media.Protection.ProtectionCapabilityResult.Probably
                    : Windows.Media.Protection.ProtectionCapabilityResult.NotSupported;
            };
            using (var client = new BilibiliClient())
            {
                await Login(client); var info = await client.GetPlaybackAsync(Video, 120);
                Check(info.QualityId == 120 && info.FourKDisplayVerified && !info.HardwareHevcVerified,
                    "real AVC4K is tested with AVC capabilities and does not require a HEVC decoder");
                Check(queried.Count > 0 && queried.All(content => content.Contains("codecs=\"avc1,")),
                    "returned AVC4K does not query the wrong video codec");
            }
            var hdr1080 = AdvertisedHdr(Dash(80));
            var hdrTrack = Track(125, "hvc1.2.4.L153.B0");
            hdrTrack["width"] = 1920; hdrTrack["height"] = 1080;
            ((JArray)hdr1080["data"]["dash"]["video"]).Insert(0, hdrTrack);
            foreach (bool hdrSupported in new[] { false, true })
            {
                Stub(hdr1080, logged: true, premium: true);
                Windows.Media.Protection.ProtectionCapabilities.Query = (content, key) =>
                    !hdrSupported && content.Contains("hdr=1") ? Windows.Media.Protection.ProtectionCapabilityResult.NotSupported
                        : Windows.Media.Protection.ProtectionCapabilityResult.Probably;
                using (var client = new BilibiliClient())
                {
                    await Login(client); var info = await client.GetPlaybackAsync(Video, 125);
                    Check(info.QualityId == (hdrSupported ? 125 : 80)
                        && info.AvailableQualities.First(item => item.Id == 125).IsVisible == hdrSupported,
                        "1080 HDR is gated even though its dimensions are below UHD: " + hdrSupported);
                    Check(info.IsHdr10 == hdrSupported && info.Hdr10DisplayVerified == hdrSupported && !info.FourKDisplayVerified,
                        "HDR verification stays independent of the 4K flag: " + hdrSupported);
                }
            }
            foreach (int bits in new[] { 8, 12 })
            {
                var incompatibleDepth = (JObject)hdr1080.DeepClone();
                incompatibleDepth["data"]["dash"]["video"][0]["bit_depth"] = bits;
                Stub(incompatibleDepth, logged: true, premium: true);
                using (var client = new BilibiliClient())
                {
                    await Login(client); var info = await client.GetPlaybackAsync(Video, 125);
                    Check(info.QualityId == 80 && !info.AvailableQualities.First(item => item.Id == 125).IsVisible,
                        "HDR10 refuses an explicitly incompatible source bit depth: " + bits);
                }
            }
            var false4k = Dash(120, 80);
            false4k["data"]["dash"]["video"][0]["width"] = 1920;
            false4k["data"]["dash"]["video"][0]["height"] = 1080;
            Stub(false4k, logged: true, premium: true);
            using (var client = new BilibiliClient())
            {
                await Login(client); var info = await client.GetPlaybackAsync(Video, 120);
                Check(info.QualityId == 80 && !info.AvailableQualities.First(item => item.Id == 120).IsVisible,
                    "a tier120 label on a1080 track cannot masquerade as real4K");
            }
            foreach (int tier in new[] { 120, 127 })
            {
                var malformed = Dash(120, 80);
                malformed["data"]["dash"]["video"][0]["id"] = tier;
                malformed["data"]["dash"]["video"][0]["width"] = tier == 127 ? 3840 : 3200;
                malformed["data"]["dash"]["video"][0]["height"] = tier == 127 ? 2160 : 1800;
                malformed["data"]["accept_quality"][0] = tier;
                malformed["data"]["support_formats"][0]["quality"] = tier;
                Stub(malformed, logged: true, premium: true);
                using (var client = new BilibiliClient())
                {
                    await Login(client); var info = await client.GetPlaybackAsync(Video, tier);
                    Check(info.QualityId == 80 && !info.AvailableQualities.First(item => item.Id == tier).IsVisible,
                        "an oversized tier label cannot promote an undersized actual video track: " + tier);
                }
            }
            var onlyUhd = Dash(120);
            var hevcUhd = Track(120, "hvc1.2.4.L153.B0");
            ((JArray)onlyUhd["data"]["dash"]["video"]).Add(hevcUhd);
            Stub(onlyUhd, logged: true, premium: true);
            using (var client = new BilibiliClient())
            {
                await Login(client); var info = await client.GetPlaybackAsync(Video, 120);
                Check(info.HardwareHevcVerified && info.CodecFallback != null && info.CodecFallback.QualityId == 120
                    && info.CodecFallback.FourKDisplayVerified && !info.CodecFallback.IsHdr10,
                    "a verified4K AVC fallback keeps its display proof when no lower tier is returned");
            }
            foreach (int tier in new[] { 120, 125, 126, 127 })
            {
                Stub(Mp4(tier), logged: true, premium: true);
                using (var client = new BilibiliClient())
                {
                    await Login(client);
                    await Error(() => client.GetPlaybackAsync(Video, tier), -1,
                        "MP4 compatibility response cannot bypass verification for UHD/HDR tier " + tier);
                }
            }
        }
        private static async Task RunSupportedOutputModes()
        {
            var response = AdvertisedHdr(Dash(120, 80));
            response["data"]["dash"]["video"][0]["codecs"] = "hvc1.2.4.L153.B0";
            Stub(response, logged: true, premium: true);
            Windows.System.Profile.AnalyticsInfo.VersionInfo.DeviceFamily = "Windows.Xbox";
            var sdr4k = new Windows.Graphics.Display.Core.HdmiDisplayMode
            {
                ResolutionWidthInRawPixels = 3840, ResolutionHeightInRawPixels = 2160,
                RefreshRate = 60, IsSdrLuminanceSupported = true
            };
            var sdr1080 = new Windows.Graphics.Display.Core.HdmiDisplayMode
            {
                ResolutionWidthInRawPixels = 1920, ResolutionHeightInRawPixels = 1080,
                RefreshRate = 60, IsSdrLuminanceSupported = true
            };
            var display = Windows.Graphics.Display.Core.HdmiDisplayInformation.Current
                = new Windows.Graphics.Display.Core.HdmiDisplayInformation
                { Modes = new[] { sdr4k, sdr1080 }, CurrentMode = sdr1080 };
            var queried = new List<string>();
            Windows.Media.Protection.ProtectionCapabilities.Query = (content, key) =>
            {
                queried.Add(content);
                return content.Contains("display-res-x") ? Windows.Media.Protection.ProtectionCapabilityResult.NotSupported
                    : Windows.Media.Protection.ProtectionCapabilityResult.Probably;
            };
            using (var client = new BilibiliClient())
            {
                Check(Windows.Graphics.Display.Core.HdmiDisplayInformation.ViewQueries == 0,
                    "constructing API client does not activate a view-scoped HDMI API");
                await Login(client);
                var info = await client.GetPlaybackAsync(Video, 120);
                Check(info.QualityId == 120 && info.VideoWidth == 3840 && info.VideoHeight == 2160
                    && info.HardwareHevcVerified && info.FourKDisplayVerified && !info.IsHdr10,
                    "1080 dashboard plus supported4K8-bit SDR selects real4K10-bit-decode SDR track");
                Check(info.AvailableQualities.First(item => item.Id == 120).IsVisible
                    && !info.AvailableQualities.First(item => item.Id == 125).IsVisible,
                    "4K8-bit SDR output independently exposes4K and hides unsupportedHDR");
                Check(queried.Any(content => !content.Contains("display-res-x"))
                    && !queried.Any(content => content.Contains("display-res-x=3840") && !content.Contains("hdr=1")),
                    "supported HDMI4K mode establishes display independently of active1080, while unsupportedHDR gets its own query");
                Check(display.Queries == 1 && display.CurrentQueries == 0,
                    "capability menu enumerates supported modes once and never reads current output");
                display.Modes = new[] { sdr1080 };
                display.CurrentMode = sdr4k;
                info = await client.GetPlaybackAsync(Video, 120);
                Check(info.QualityId == 80 && !info.AvailableQualities.First(item => item.Id == 120).IsVisible
                    && display.Queries == 2 && display.CurrentQueries == 0,
                    "changed supported-mode configuration refreshes gates even when stale currentmode is4K");
                display.Modes = new[] { new Windows.Graphics.Display.Core.HdmiDisplayMode
                {
                    ResolutionWidthInRawPixels = 3840, ResolutionHeightInRawPixels = 2160,
                    RefreshRate = 120, IsSdrLuminanceSupported = true
                }};
                info = await client.GetPlaybackAsync(Video, 120);
                Check(info.QualityId == 80 && !info.AvailableQualities.First(item => item.Id == 120).IsVisible,
                    "120Hz-only4K does not advertise an unsupported media output");
            }
            foreach (bool hdrAt4k in new[] { false, true })
            {
                var hdrResponse = AdvertisedHdr(Dash(120, 80));
                hdrResponse["data"]["dash"]["video"][0]["codecs"] = "hvc1.2.4.L153.B0";
                var hdrTrack = Track(125, "hvc1.2.4.L153.B0");
                hdrTrack["width"] = 3840; hdrTrack["height"] = 2160;
                ((JArray)hdrResponse["data"]["dash"]["video"]).Insert(0, hdrTrack);
                Stub(hdrResponse, logged: true, premium: true);
                Windows.System.Profile.AnalyticsInfo.VersionInfo.DeviceFamily = "Windows.Xbox";
                display = Windows.Graphics.Display.Core.HdmiDisplayInformation.Current
                    = new Windows.Graphics.Display.Core.HdmiDisplayInformation
                    { Modes = new[] { sdr4k, new Windows.Graphics.Display.Core.HdmiDisplayMode
                    {
                        ResolutionWidthInRawPixels = hdrAt4k ? 3840u : 1920u,
                        ResolutionHeightInRawPixels = hdrAt4k ? 2160u : 1080u,
                        RefreshRate = 60, IsSmpte2084Supported = true,
                        ColorSpace = Windows.Graphics.Display.Core.HdmiDisplayColorSpace.BT2020
                    }}, CurrentMode = sdr1080 };
                Windows.Media.Protection.ProtectionCapabilities.Query = (content, key) =>
                    !hdrAt4k && content.Contains("hdr=1") && content.Contains("display-res-x=3840")
                        ? Windows.Media.Protection.ProtectionCapabilityResult.NotSupported
                        : Windows.Media.Protection.ProtectionCapabilityResult.Probably;
                using (var client = new BilibiliClient())
                {
                    await Login(client); var info = await client.GetPlaybackAsync(Video, 125);
                    Check(info.QualityId == (hdrAt4k ? 125 : 120) && info.IsHdr10 == hdrAt4k,
                        "HDR display must cover the actual track resolution independently of SDR4K: " + hdrAt4k);
                    Check(info.AvailableQualities.First(item => item.Id == 125).IsVisible == hdrAt4k,
                        "real4KHDR appears only with matchingBT2020PQ output: " + hdrAt4k);
                }
            }
            // A lower-resolution HEVC source can be returned after the HDR/4K
            // gate. Its decoder failure must still have an authorized fallback.
            var lowerHevc = Dash(116, 80);
            lowerHevc["data"]["dash"]["video"][0]["codecs"] = "hvc1.2.4.L153.B0";
            Stub(lowerHevc, logged: true, premium: true);
            Windows.System.Profile.AnalyticsInfo.VersionInfo.DeviceFamily = "Windows.Xbox";
            using (var client = new BilibiliClient())
            {
                await Login(client); var info = await client.GetPlaybackAsync(Video, 116);
                Check(info.QualityId == 116 && info.HardwareHevcVerified && info.CodecFallback != null
                    && info.CodecFallback.QualityId == 80 && info.CodecFallback.VideoCodec.StartsWith("avc1.")
                    && info.CodecFallback.CodecFallback == null,
                    "SDR1080HEVC receives a verifieddecode gate and one realAVC fallback without cycles");
            }
        }
        private static async Task RunLimitedOutputCatalogue()
        {
            foreach (string state in new[] { "1080-only", "empty", "null-view", "view-failure", "mode-failure", "sdr-flag-absent" })
            {
                var response = Dash(120, 80);
                response["data"]["dash"]["video"][0]["codecs"] = "hvc1.2.4.L153.B0";
                Stub(response, logged: true, premium: true);
                Windows.System.Profile.AnalyticsInfo.VersionInfo.DeviceFamily = "Windows.Xbox";
                var dashboard = new Windows.Graphics.Display.Core.HdmiDisplayMode
                { ResolutionWidthInRawPixels = 1920, ResolutionHeightInRawPixels = 1080, RefreshRate = 60, IsSdrLuminanceSupported = true };
                var candidate = new Windows.Graphics.Display.Core.HdmiDisplayMode
                { ResolutionWidthInRawPixels = 3840, ResolutionHeightInRawPixels = 2160, RefreshRate = 60,
                    ColorSpace = Windows.Graphics.Display.Core.HdmiDisplayColorSpace.BT709 };
                var display = new Windows.Graphics.Display.Core.HdmiDisplayInformation
                { CurrentMode = dashboard, Modes = state == "empty" ? new Windows.Graphics.Display.Core.HdmiDisplayMode[0]
                    : state == "sdr-flag-absent" ? new[] { dashboard, candidate } : new[] { dashboard } };
                Windows.Graphics.Display.Core.HdmiDisplayInformation.Current = state == "null-view" ? null : display;
                Windows.Graphics.Display.Core.HdmiDisplayInformation.ThrowView = state == "view-failure";
                Windows.Graphics.Display.Core.HdmiDisplayInformation.ThrowModes = state == "mode-failure";
                bool queriedDisplay = false;
                Windows.Media.Protection.ProtectionCapabilities.Query = (content, key) =>
                {
                    queriedDisplay |= content.Contains("display-res-x=3840") && content.Contains("display-bpc=8");
                    return Windows.Media.Protection.ProtectionCapabilityResult.Probably;
                };
                using (var client = new BilibiliClient())
                {
                    await Login(client); var info = await client.GetPlaybackAsync(Video, 120);
                    var option = info.AvailableQualities.First(item => item.Id == 120);
                    Check(queriedDisplay && info.QualityId == 120 && info.FourKDisplayVerified && option.IsVisible && option.IsAvailable,
                        "official combined4K SDR proof survives a limited/unavailable HDMI catalogue: " + state);
                    Check(display.CurrentQueries == 0 && info.CapabilityDiagnostic.Contains("Probably")
                        && info.CapabilityDiagnostic.Contains("HDMI") && info.CodecFallback?.QualityId == 80,
                        "diagnostic keeps query proof and real1080 fallback without consulting current resolution: " + state);
                }
            }
            foreach (string state in new[] { "unsupported", "maybe", "exception", "unavailable-enumeration" })
            {
                var response = Dash(120, 80);
                response["data"]["dash"]["video"][0]["codecs"] = "hvc1.2.4.L153.B0";
                Stub(response, logged: true, premium: true);
                Windows.System.Profile.AnalyticsInfo.VersionInfo.DeviceFamily = "Windows.Xbox";
                Windows.Graphics.Display.Core.HdmiDisplayInformation.Current = new Windows.Graphics.Display.Core.HdmiDisplayInformation();
                Windows.Graphics.Display.Core.HdmiDisplayInformation.ThrowModes = state == "unavailable-enumeration";
                Windows.Media.Protection.ProtectionCapabilities.Query = (content, key) =>
                {
                    if (!content.Contains("display-res-x=")) return Windows.Media.Protection.ProtectionCapabilityResult.Probably;
                    if (state == "exception") throw new InvalidOperationException("fixture unavailable");
                    return state == "maybe" ? Windows.Media.Protection.ProtectionCapabilityResult.Maybe
                        : Windows.Media.Protection.ProtectionCapabilityResult.NotSupported;
                };
                using (var client = new BilibiliClient())
                {
                    await Login(client); var info = await client.GetPlaybackAsync(Video, 120);
                    var option = info.AvailableQualities.First(item => item.Id == 120);
                    Check(info.QualityId == 80 && !info.FourKDisplayVerified && !option.IsAvailable
                        && option.IsVisible == (state != "unsupported") && option.IsCapabilityPending == (state != "unsupported"),
                        "known display refusal hides4K, unknown keeps a disabled option without approving a4K track: " + state);
                }
            }
            Stub(Dash(120, 80), logged: true, premium: true);
            Windows.System.Profile.AnalyticsInfo.VersionInfo.DeviceFamily = "Windows.Xbox";
            using (var client = new BilibiliClient())
            {
                await Login(client); var info = await client.GetPlaybackAsync(Video, 120);
                Check(info.QualityId == 80 && !info.AvailableQualities.First(item => item.Id == 120).IsVisible,
                    "Xbox does not authorize4K AVC from a genericProbably result beyond its documented decoder ceiling");
            }
        }
        public static async Task<string> Run()
        {
            _passed = 0;
            await RunCapabilityFiltering();
            await RunSupportedOutputModes();
            await RunLimitedOutputCatalogue();
            var routed = Dash(80);
            var routedVideo = routed["data"]["dash"]["video"][0];
            var routedAudio = routed["data"]["dash"]["audio"][0];
            routedVideo["backupUrl"] = new JArray("https://v-backup.example.test/video.m4s?token=v%2F1&flag=2",
                routedVideo["baseUrl"].ToString(), "file:///denied", "https://user:secret@cdn.example.test/v");
            routedVideo["backup_url"] = new JArray("//v-second.example.test/video.m4s?token=v2", JValue.CreateNull(), new JObject());
            routedAudio["backup_url"] = new JArray("https://a-backup.example.test/audio.m4s?token=a%2F2");
            Stub(routed, logged: true);
            using (var client = new BilibiliClient())
            {
                await Login(client);
                var info = await client.GetPlaybackAsync(Video, 80);
                Check(info.DashVideoUrls.Count == 3 && info.DashAudioUrls.Count == 2, "retain validated per-track CDN alternatives, both API spellings and no duplicates");
                Check(info.DashVideoUrls[0] == info.Url && info.DashVideoUrls[1].Contains("token=v%2F1&flag=2"), "primary order and complete signed backup query preserved");
                Check(info.DashAudioUrls[1].Contains("a-backup") && !info.DashVideoUrls.Any(value => value.Contains("a-backup")), "audio alternatives never mixed into video");
                Check(info.AudioBitrate == 173230 && info.DashManifest.Contains("video-80"), "supply diagnostics include actual audio bitrate without changing selected media");
            }
            var full = Dash(120, 112, 80, 64, 32, 16);
            ((JArray)full["data"]["dash"]["video"]).Insert(0, Track(80, "hev1.1.6.L150.90"));
            ((JArray)full["data"]["dash"]["video"]).Insert(0, Track(80, "av01.0.08M.08"));
            Stub(full, logged: true, premium: true);
            using (var client = new BilibiliClient())
            {
                await Login(client);
                var info = await client.GetPlaybackAsync(Video);
                Check(LastRequestedQuality == 80 && info.RequestedQualityId == 80, "default1080 sends actual requested qn80 instead of hardcoded127");
                Check(info.QualityId == 80 && info.Quality == "Source tier 80", "normal1080 selected using real track, not false720 metadata");
                Check(info.ContentType == "application/dash+xml" && info.DashManifest.Length > 0, "DASH audio/video transport contract");
                var mpd = XDocument.Parse(info.DashManifest); XNamespace ns = "urn:mpeg:dash:schema:mpd:2011";
                var sets = mpd.Descendants(ns + "AdaptationSet").ToList();
                Check(sets.Count == 2 && sets[0].Attribute("contentType").Value == "video" && sets[1].Attribute("contentType").Value == "audio", "synchronized audio/video adaptation sets");
                var reps = mpd.Descendants(ns + "Representation").ToList();
                Check(reps.Count == 2 && reps[0].Attribute("codecs").Value == "avc1.640032" && reps[1].Attribute("codecs").Value == "mp4a.40.2", "H264 preferred over HEVC and AV1, AAC preserved");
                Check(mpd.Root.Attribute("mediaPresentationDuration").Value == "PT353.25S" && mpd.Descendants(ns + "Initialization").All(item => item.Attribute("range").Value == "0-927"), "duration and exact init byte ranges");
                Check(mpd.Descendants(ns + "BaseURL").All(item => item.Value.Contains("&flag=1")) && info.DashManifest.Contains("&amp;flag=1"), "signed URL query XML escaping");
                Check(info.AvailableQualities.Any(item => item.Id == 120 && item.IsAvailable) && info.AvailableQualities.All(item => item.IsAvailable), "returned4K allowed, historical FLV label does not disable DASH");
                var uhd = await client.GetPlaybackAsync(Video, 120);
                Check(uhd.QualityId == 120 && uhd.DashManifest.Contains("video-120"), "manual4K selects real4K track");
                Check(LastRequestedQuality == 120 && uhd.VideoCodec == "avc1.640032" && uhd.VideoWidth == 3840 && uhd.QualityNotice.Length == 0,
                    "manual4K signed request is qn120 and diagnostics use returned track without inventing dimensions");
                var low = await client.GetPlaybackAsync(Video, 16);
                Check(low.QualityId == 16 && low.DashManifest.Contains("video-16"), "low manual quality fixed");
            }
            Stub(Dash(80, 64, 32, 16), logged: true);
            using (var client = new BilibiliClient())
            {
                await Login(client); var info = await client.GetPlaybackAsync(Video, 120);
                Check(info.QualityId == 80 && info.AvailableQualities.First(item => item.Id == 120).IsAvailable, "no fabricated4K and advertised tier remains requestable after actual1080 downgrade");
                Check(info.AvailableQualities.First(item => item.Id == 120).IsProbe && !info.AvailableQualities.First(item => item.Id == 120).HasPlaybackSource,
                    "missing returned track means fetch-on-selection, not an invented account ban");
                Check(info.QualityNotice.Contains("Source tier 120") && info.QualityNotice.Contains("Source tier 80") && info.ServerQualityId == 64,
                    "downgrade notice separates requested, actual selected and advisory server quality");
            }
            var hevc = Dash(120, 80);
            hevc["data"]["dash"]["video"][0]["codecs"] = "hvc1.2.4.L153.B0";
            Stub(hevc, logged: true, premium: true);
            Windows.Media.Protection.ProtectionCapabilities.Query = (contentType, keySystem) => Windows.Media.Protection.ProtectionCapabilityResult.Probably;
            using (var client = new BilibiliClient())
            {
                await Login(client);
                var info = await client.GetPlaybackAsync(Video, 120);
                Check(info.QualityId == 120 && info.DashManifest.Contains("hvc1.2.4.L153.B0"), "HEVC-only UHD remains selectable for a compatible Xbox decoder");
                Check(info.HardwareHevcVerified && info.CodecFallback != null && info.CodecFallback.QualityId == 80
                    && info.CodecFallback.VideoCodec.StartsWith("avc1.") && info.CodecFallback.CodecFallback == null,
                    "verified HEVC carries one authorized1080 H264 fallback with no retry cycle");
            }
            var rights = Dash(120, 112, 80, 64);
            rights["data"]["support_formats"][0]["need_vip"] = true;
            rights["data"]["support_formats"][1]["need_login"] = true;
            rights["data"]["support_formats"][3]["can_watch_qn_reason"] = 7;
            Stub(rights, logged: true);
            using (var client = new BilibiliClient())
            {
                await Login(client);
                var options = (await client.GetPlaybackAsync(Video)).AvailableQualities;
                Check(options.First(item => item.Id == 120).RequiresPremium && !options.First(item => item.Id == 120).IsAvailable, "explicit premium restriction is honored");
                Check(options.First(item => item.Id == 112).RequiresLogin && options.First(item => item.Id == 112).IsAvailable, "explicit login tier honors actual logged-in state");
                Check(options.First(item => item.Id == 64).IsAvailable && options.First(item => item.Id == 64).HasPlaybackSource,
                    "an undocumented numeric reason cannot disable a real compatible returned tier");
            }
            // A real entitlement may only return the selected qn. The catalogue
            // cannot decide availability before that signed qn is requested.
            Stub((request, token) =>
            {
                var qn = int.Parse(request.RequestUri.Query.TrimStart('?').Split('&').Select(pair => pair.Split(new[] { '=' }, 2)).First(pair => pair[0] == "qn")[1]);
                var response = Dash(qn == 120 ? 120 : qn == 116 ? 116 : 80, 64, 32, 16);
                ((JArray)response["data"]["accept_quality"]).Insert(1, 116);
                ((JArray)response["data"]["support_formats"]).Insert(1, new JObject { ["quality"] = 116, ["new_description"] = "1080P60", ["need_vip"] = true });
                response["data"]["support_formats"][0]["can_watch_qn_reason"] = 13;
                response["data"]["support_formats"][0]["need_vip"] = true;
                return Transport.Json(response.ToString());
            }, logged: true, premium: true);
            using (var client = new BilibiliClient())
            {
                await Login(client);
                Check(client.CurrentUser.IsPremium, "active premium detected from actual nav nested vip.status contract");
                var initial = await client.GetPlaybackAsync(Video);
                var option = initial.AvailableQualities.First(item => item.Id == 120);
                Check(option.IsAvailable && option.IsProbe && !option.HasPlaybackSource,
                    "premium4K with numeric reason but no initial track remains selectable for its own signed request");
                var uhd = await client.GetPlaybackAsync(Video, 120);
                Check(uhd.QualityId == 120 && LastRequestedQuality == 120 && uhd.AvailableQualities.First(item => item.Id == 120).HasPlaybackSource,
                    "request-on-selection obtains genuine4K that default1080 response omitted");
                var hfr = await client.GetPlaybackAsync(Video, 116);
                Check(hfr.QualityId == 116 && LastRequestedQuality == 116, "1080P60 has a distinct qn116 request and real track");
            }
            Stub((request, token) =>
            {
                var q = request.RequestUri.Query.TrimStart('?').Split('&').Select(pair => pair.Split(new[] { '=' }, 2)).ToDictionary(pair => pair[0], pair => pair[1]);
                var response = Dash(120, 80, 64, 32, 16);
                // Simulate a server that sends only AV1 at the top tier when
                // AV1 was advertised, but a compatible HEVC source otherwise.
                response["data"]["dash"]["video"][0]["codecs"] = (int.Parse(q["fnval"]) & 2048) != 0
                    ? "av01.0.13M.08" : "hvc1.1.6.L153.90";
                return Transport.Json(response.ToString());
            }, logged: true, premium: true);
            Windows.Media.Protection.ProtectionCapabilities.Query = (contentType, keySystem) => Windows.Media.Protection.ProtectionCapabilityResult.Probably;
            using (var client = new BilibiliClient())
            {
                await Login(client);
                var info = await client.GetPlaybackAsync(Video, 120);
                Check(info.QualityId == 120 && info.VideoCodec.StartsWith("hvc1.", StringComparison.Ordinal),
                    "AVC/HEVC negotiation obtains a genuine compatible4K source instead of filtering AV1 into a downgrade");
            }
            var alternate = Dash(120, 80, 64, 32, 16);
            var preferredHevc = Track(120, "hvc1.2.4.L153.B0");
            preferredHevc["width"] = 3840; preferredHevc["height"] = 2160;
            preferredHevc["bandwidth"] = 12345000; preferredHevc["frameRate"] = "60000/1001";
            ((JArray)alternate["data"]["dash"]["video"]).Add(preferredHevc);
            Stub(alternate, logged: true, premium: true);
            Windows.Media.Protection.ProtectionCapabilities.Query = (contentType, keySystem) =>
            {
                bool hevcQuery = contentType.Contains("codecs=\"hvc1,");
                Check(contentType.Contains("decode-res-x=3840,decode-res-y=2160")
                    && contentType.Contains(hevcQuery ? "decode-fps=59.94" : "decode-fps=29.97")
                    && contentType.Contains(hevcQuery ? "decode-bpc=10" : "decode-bpc=8")
                    && contentType.Contains("decode-bitrate=20000") && keySystem == "com.microsoft.playready.hardware",
                    "hardware query includes the actual codec, dimensions, rate and bit depth");
                return Windows.Media.Protection.ProtectionCapabilityResult.Probably;
            };
            using (var client = new BilibiliClient())
            {
                await Login(client);
                var info = await client.GetPlaybackAsync(Video, 120);
                Check(info.QualityId == 120 && info.VideoCodec.StartsWith("hvc1.") && info.VideoWidth == 3840
                    && info.VideoBitrate == 12345000 && Math.Abs(info.VideoFrameRate - 59.94005994) < 0.0001,
                    "confirmed4K hardware HEVC wins over4K AVC while retaining real dimensions bitrate and rational fps");
                Check(info.CodecFallback.QualityId == 80 && info.CodecFallback.DashManifest.Contains("avc1.")
                    && info.CodecFallback.AvailableQualities.Any(item => item.Id == 120), "H264 fallback stays in authorized response and preserves quality catalogue");
            }
            var hdr = Dash(120, 80);
            var hdrTrack = (JObject)hdr["data"]["dash"]["video"].First(item => (int)item["id"] == 120);
            hdrTrack["id"] = 125; hdrTrack["width"] = 3840; hdrTrack["height"] = 2160;
            hdrTrack["codecs"] = "hvc1.2.4.L153.B0"; hdrTrack["bandwidth"] = 22000000;
            hdrTrack["hdr_type"] = "hdr10";
            hdr["data"]["accept_quality"][0] = 125; hdr["data"]["accept_description"][0] = "HDR10 4K";
            hdr["data"]["support_formats"][0]["quality"] = 125; hdr["data"]["support_formats"][0]["new_description"] = "HDR10 4K";
            Stub(hdr, logged: true, premium: true);
            Windows.Media.Protection.ProtectionCapabilities.Query = (contentType, keySystem) =>
            {
                if (contentType.Contains("display-res-x"))
                    Check(contentType.Contains("display-res-x=3840,display-res-y=2160"), "4K capability query includes display dimensions");
                if (contentType.Contains("hdr=1"))
                    Check(contentType.Contains("display-bpc=10") && contentType.Contains("decode-bpc=10"), "HDR10 query includes 10-bit decode/display constraints");
                return Windows.Media.Protection.ProtectionCapabilityResult.Probably;
            };
            using (var client = new BilibiliClient())
            {
                await Login(client);
                var info = await client.GetPlaybackAsync(Video, 125);
                Check(info.QualityId == 125 && info.FourKDisplayVerified && info.Hdr10DisplayVerified && info.IsHdr10,
                    "supported real UHD HDR10 track remains selectable");
                Check(info.DashManifest.Contains("urn:mpeg:mpegB:cicp:ColourPrimaries")
                    && info.DashManifest.Contains("urn:mpeg:mpegB:cicp:TransferCharacteristics")
                    && info.DashManifest.Contains("urn:mpeg:mpegB:cicp:MatrixCoefficients"),
                    "HDR10 DASH representation carries CICP color metadata");
            }
            Stub(hdr, logged: true, premium: true);
            Windows.Media.Protection.ProtectionCapabilities.Query = (contentType, keySystem) =>
                Windows.Media.Protection.ProtectionCapabilityResult.NotSupported;
            using (var client = new BilibiliClient())
            {
                await Login(client);
                var info = await client.GetPlaybackAsync(Video, 125);
                var hidden = info.AvailableQualities.First(item => item.Id == 125);
                Check(info.QualityId == 80 && !hidden.IsVisible && !hidden.IsAvailable
                    && (hidden.Reason.Contains("硬件") || hidden.Reason.Contains("4K")),
                    "unsupported real UHD HDR10 option is hidden and falls back");
            }
            foreach (var unavailable in new[] { "not-supported", "maybe", "exception" })
            {
                Stub(alternate, logged: true, premium: true);
                Windows.Media.Protection.ProtectionCapabilities.Query = (contentType, keySystem) =>
                {
                    if (unavailable == "exception") throw new InvalidOperationException("fixture capability unavailable");
                    return unavailable == "maybe" ? Windows.Media.Protection.ProtectionCapabilityResult.Maybe : Windows.Media.Protection.ProtectionCapabilityResult.NotSupported;
                };
                using (var client = new BilibiliClient())
                {
                    await Login(client);
                    var info = await client.GetPlaybackAsync(Video, 120);
                    Check(info.QualityId == 80 && !info.HardwareHevcVerified && info.CodecFallback == null
                        && info.AvailableQualities.First(item => item.Id == 120).IsVisible == (unavailable != "not-supported")
                        && !info.AvailableQualities.First(item => item.Id == 120).IsAvailable,
                        "unknown UHD query remains visible/disabled while confirmedNotSupported is hidden: " + unavailable);
                }
            }
            Stub(hevc, logged: true, premium: true);
            Windows.Media.Protection.ProtectionCapabilities.Query = (contentType, keySystem) => Windows.Media.Protection.ProtectionCapabilityResult.NotSupported;
            using (var client = new BilibiliClient())
            {
                await Login(client);
                var info = await client.GetPlaybackAsync(Video, 120);
                Check(info.QualityId == 80 && !info.HardwareHevcVerified && !info.AvailableQualities.First(item => item.Id == 120).IsAvailable,
                    "HEVC-only UHD without hardware confirmation uses real1080 compatibility source instead of software4K");
            }
            var unavailableCodec = Dash(120, 80);
            unavailableCodec["data"]["dash"]["video"][0]["codecs"] = "av01.0.13M.08";
            Stub(unavailableCodec, logged: true, premium: true);
            using (var client = new BilibiliClient())
            {
                await Login(client);
                var info = await client.GetPlaybackAsync(Video, 120);
                var uhd = info.AvailableQualities.First(item => item.Id == 120);
                Check(info.QualityId == 80 && !uhd.IsAvailable && uhd.Reason.Contains("编码"), "AV1-only4K has a codec reason instead of falsely blaming membership");
            }
            var absent = Dash(80, 64, 32, 16);
            ((JArray)absent["data"]["accept_quality"]).RemoveAt(0);
            Stub(absent, logged: true, premium: true);
            using (var client = new BilibiliClient())
            {
                await Login(client);
                var info = await client.GetPlaybackAsync(Video, 120);
                Check(info.QualityId == 80 && !info.AvailableQualities.Any(item => item.Id == 120), "video with no advertised4K does not gain an invented4K option");
            }
            foreach (var nested in new[] { false, true })
            {
                Transport.Reset((request, token) =>
                {
                    Check(request.RequestUri.AbsolutePath == "/x/web-interface/nav", "premium profile probe is read-only nav");
                    var nav = Nav(true, nested);
                    if (!nested) nav["data"]["vipStatus"] = 1;
                    var response = new HttpResponseMessage { Content = new HttpStringContent(nav.ToString()) };
                    response.Headers["Set-Cookie"] = "SESSDATA=fixture-only; Domain=.bilibili.com; Path=/; Secure";
                    return Task.FromResult(response);
                });
                using (var client = new BilibiliClient())
                {
                    await Login(client);
                    Check(client.IsLoggedIn && client.CurrentUser.IsPremium,
                        "nav active membership recognized from " + (nested ? "nested vip.status" : "flat vipStatus"));
                }
            }
            Stub(Dash(32, 16), Mp4());
            using (var client = new BilibiliClient())
            {
                var info = await client.GetPlaybackAsync(Video);
                Check(info.QualityId == 64 && info.DashManifest.Length == 0 && info.ContentType == "video/mp4", "anonymous genuine720 fallback preserves old working resolution");
                Check(info.BackupUrls.Count == 1 && info.BackupUrls[0].StartsWith("https://", StringComparison.Ordinal), "fallback backup URI validation");
                Check(info.AvailableQualities.Any(item => item.Id == 80 && !item.IsAvailable && item.RequiresLogin) && info.AvailableQualities.Any(item => item.Id == 64 && item.IsAvailable), "1080 visible with login reason and actual720 usable");
                Check(PlaybackRequests == 2, "one DASH request and one justified MP4 request");
            }
            Stub(Dash(32, 16));
            using (var client = new BilibiliClient())
            {
                var info = await client.GetPlaybackAsync(Video, 32);
                Check(info.QualityId == 32 && info.DashManifest.Length > 0 && PlaybackRequests == 1, "480 DASH ignores misleading720 header with no extra compatibility request");
            }
            Stub(Dash(32, 16), Mp4(16));
            using (var client = new BilibiliClient())
            {
                var info = await client.GetPlaybackAsync(Video, 80);
                Check(info.QualityId == 32 && info.DashManifest.Length > 0, "lower MP4 cannot replace better DASH");
            }
            var snake = Dash(80); var st = (JObject)snake["data"]["dash"]["video"][0];
            st["base_url"] = st["baseUrl"]; st.Remove("baseUrl");
            st["segment_base"] = new JObject { ["initialization"] = "0-927", ["index_range"] = "928-1811" }; st.Remove("SegmentBase");
            Stub(snake);
            using (var client = new BilibiliClient()) Check((await client.GetPlaybackAsync(Video)).QualityId == 80, "snake-case metadata");
            foreach (var variant in new[] { "missing-audio", "bad-range", "bad-url", "unsupported-codec", "bad-duration" })
            {
                var invalid = Dash(80);
                if (variant == "missing-audio") invalid["data"]["dash"]["audio"] = new JArray();
                if (variant == "bad-range") invalid["data"]["dash"]["video"][0]["SegmentBase"]["indexRange"] = "1811-0";
                if (variant == "bad-url") invalid["data"]["dash"]["video"][0]["baseUrl"] = "file:///private";
                if (variant == "unsupported-codec") invalid["data"]["dash"]["video"][0]["codecs"] = "av01.0.08M.08";
                if (variant == "bad-duration") invalid["data"]["dash"]["duration"] = "Infinity";
                Stub(invalid);
                using (var client = new BilibiliClient()) await Error(async () => { await client.GetPlaybackAsync(Video); }, -1, "unsafe DASH rejected: " + variant);
                Check(PlaybackRequests == 1, "invalid HD is not substituted with a video-only source");
            }
            Stub((request, token) => Transport.Json("{\"code\":-101,\"message\":\"authentication required\"}"));
            using (var client = new BilibiliClient()) await Error(async () => { await client.GetPlaybackAsync(Video); }, -101, "permission error preserved");
            Stub((request, token) => Transport.Json("{}", 412));
            using (var client = new BilibiliClient()) await Error(async () => { await client.GetPlaybackAsync(Video); }, 412, "risk412 not bypassed");
            Check(PlaybackRequests == 1, "risk412 does not retry legacy endpoint");
            Stub(full);
            using (var client = new BilibiliClient())
            {
                foreach (var quality in new[] { 0, -1 })
                {
                    var invalid = false; try { await client.GetPlaybackAsync(Video, quality); } catch (ArgumentOutOfRangeException) { invalid = true; }
                    Check(invalid, "nonpositive quality rejected");
                }
                using (var cancel = new CancellationTokenSource())
                {
                    cancel.Cancel(); var canceled = false;
                    try { await client.GetPlaybackAsync(Video, 80, cancel.Token); } catch (OperationCanceledException) { canceled = true; }
                    Check(canceled && Transport.Requests.Count == 0, "pre-canceled request avoids network");
                }
            }
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Stub(async (request, token) => { started.SetResult(true); await Task.Delay(Timeout.Infinite, token); throw new Exception("Unexpected completion"); });
            using (var client = new BilibiliClient())
            using (var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                var pending = client.GetPlaybackAsync(Video, 80, cancel.Token); if (await Task.WhenAny(started.Task, pending) == pending) await pending;
                cancel.Cancel(); var canceled = false; try { await pending; } catch (OperationCanceledException) { canceled = true; }
                Check(canceled && PlaybackRequests == 1, "in-flight HD cancellation propagates without retry");
            }
            return "PASS: " + _passed + " production playback assertions: signed DASH, real1080/4K selection, synchronized H264/AAC MPD, byte ranges, server downgrade, account rights, anonymous720 fallback, invalid tracks and cancellation. Xbox decoding is not established.";
        }
    }
}
