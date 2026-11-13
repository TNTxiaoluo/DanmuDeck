namespace BiliXbox.DashRoutesTests
{
    public static class Tests
    {
        private static int _count;
        private static void Check(bool value, string name) { if (!value) throw new Exception(name); ++_count; }
        public static void Run()
        {
            const string video = "https://video.example.test/v.m4s?token=video%2F1&flag=1";
            const string audio = "https://audio.example.test/a.m4s?token=audio";
            const string v1 = "https://v1.example.test/v.m4s?token=separate%2Fsignature";
            const string v2 = "https://v2.example.test/v.m4s?token=last";
            const string a1 = "https://a1.example.test/a.m4s?token=audio-backup";
            var ns = (XNamespace)"urn:mpeg:dash:schema:mpd:2011";
            var xml = new XDocument(new XElement(ns + "MPD", new XElement(ns + "Period",
                new XElement(ns + "AdaptationSet", new XAttribute("contentType", "video"), new XElement(ns + "Representation", new XElement(ns + "BaseURL", video))),
                new XElement(ns + "AdaptationSet", new XAttribute("contentType", "audio"), new XElement(ns + "Representation", new XElement(ns + "BaseURL", audio)))))).ToString();
            var v = new[] { video, v1, v2, v1, "file:///denied", "http://insecure.test/v", "https://user:secret@private.test/v", "https://fragment.test/v#bad" };
            var a = new[] { audio, a1 };
            var policy = new Services.DashRoutePolicy(xml, v, a, 0);
            Check(policy.HasAlternateRoutes && policy.ResolveRequest(new Uri(video), "v0").AbsoluteUri == video, "default keeps primary");
            Check(policy.ResolveRequest(new Uri(audio), "a0").AbsoluteUri == audio, "audio uses own primary");
            policy.ResolveRequest(new Uri(video), "concurrent-old");
            Check(policy.ReportFailure("v0"), "failed video rotates once");
            Check(policy.NextRouteOffset == 2, "rebuild advances from native-selected route rather than stale UI offset");
            Check(!policy.ReportFailure("concurrent-old") && policy.RouteChangeCount == 1, "stale concurrent failure cannot skip next source");
            Check(policy.ResolveRequest(new Uri(video), "v1").AbsoluteUri == v1, "whole signed video backup preserved");
            Check(policy.ResolveRequest(new Uri(audio), "a-ok").AbsoluteUri == audio, "video failure leaves audio untouched");
            policy.CompleteRequest("a-ok");
            Check(!policy.ReportFailure("a-ok"), "successful request removed");
            Check(policy.ReportFailure("v1") && policy.ResolveRequest(new Uri(video), "v2").AbsoluteUri == v2, "second video backup");
            Check(!policy.ReportFailure("v2") && policy.RouteChangeCount == 2, "all failures exhaust finite candidates");
            Check(!policy.ReportFailure("v2") && !policy.ReportFailure("missing"), "duplicate and unknown failure are inert");
            var unknown = new Uri("https://other.example.test/manifest.mpd?private=1");
            Check(policy.ResolveRequest(unknown, "unknown") == unknown && !policy.ReportFailure("unknown"), "unrelated resource never rewritten");
            var manual = new Services.DashRoutePolicy(xml, v, a, 1);
            Check(manual.ResolveRequest(new Uri(video), "mv").AbsoluteUri == v1 && manual.ResolveRequest(new Uri(audio), "ma").AbsoluteUri == a1, "explicit source rotation selects both tracks independently");
            Check(manual.ReportFailure("mv") && manual.ResolveRequest(new Uri(video), "mv2").AbsoluteUri == v2, "manual alternate failure moves forward");
            Check(!manual.ReportFailure("mv2"), "manual rotation does not return to earlier failed primary");
            Check(!manual.RouteSummary.Contains("token") && !manual.RouteSummary.Contains("m4s") && manual.RouteSummary.Contains("v2.example.test"), "diagnostics contain host only");
            var mismatch = new Services.DashRoutePolicy(xml, new[] { v1 }, a, 1);
            Check(mismatch.ResolveRequest(new Uri(video), "wrong").AbsoluteUri == video, "mismatched track list cannot replace manifest representation");
            var single = new Services.DashRoutePolicy(xml, new[] { video }, new[] { audio }, 0);
            Check(!single.HasAlternateRoutes && !single.ReportFailure("none"), "single-source remains usable without invented fallback");
            var empty = new Services.DashRoutePolicy(xml, null, null, 0);
            Check(!empty.HasAlternateRoutes && empty.ResolveRequest(new Uri(video), "legacy").AbsoluteUri == video, "legacy overload stays unchanged");
            for (int offset = 0; offset < 15; ++offset)
            {
                var rotated = new Services.DashRoutePolicy(xml, v, a, offset);
                Check(rotated.ResolveRequest(new Uri(video), "v").AbsoluteUri == new[] { video, v1, v2 }[offset % 3], "video modulo rotation " + offset);
                Check(rotated.ResolveRequest(new Uri(audio), "a").AbsoluteUri == new[] { audio, a1 }[offset % 2], "audio modulo rotation " + offset);
            }
            bool rejected = false;
            try { new Services.DashRoutePolicy("<!DOCTYPE MPD [<!ENTITY x SYSTEM 'file:///private'>]><MPD>&x;</MPD>", v, a, 0); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "DTD not read");
            Console.WriteLine("PASS: " + _count + " production DASH route assertions (track isolation, signed URLs, stale failures, bounded rotation, redacted diagnostics).");
        }
    }
}
