namespace BiliXbox.Models
{
    // Only the DTO is replaced; the complete production bridge is compiled above.
    public sealed class LoginCaptchaProof { public string Challenge { get; set; } public string Validate { get; set; } public string Seccode { get; set; } }
}
namespace BiliXbox.LoginCaptchaTests
{
    public static class Checks
    {
        private static int _count;
        private const string Challenge = "0123456789abcdef0123456789abcdef";
        private const string Nonce = "11111111111111111111111111111111";
        private static void Check(bool value, string name) { ++_count; if (!value) throw new System.Exception(name); }
        private static string Message(string challenge = Challenge, string nonce = Nonce, string validate = "abcdef123456", string seccode = "abcdef123456|jordan")
            => Newtonsoft.Json.JsonConvert.SerializeObject(new { kind = "success", challenge, nonce, validate, seccode });
        public static string Page() => Services.LoginCaptchaBridge.BuildPage(Challenge, Challenge, Nonce);
        public static void Run()
        {
            string state; Models.LoginCaptchaProof proof;
            var bridge = new Services.LoginCaptchaBridge(Nonce, Challenge);
            Check(!bridge.TryRead("bad json", out state, out proof), "reject malformed json");
            Check(!bridge.TryRead("{\"nonce\":[],\"kind\":{}}", out state, out proof), "reject non-string fields without throwing");
            Check(!bridge.TryRead(Message(nonce: "another-attempt"), out state, out proof), "reject previous verification window");
            Check(!bridge.TryRead(Message(challenge: "other_challenge"), out state, out proof), "reject unrelated challenge");
            Check(!bridge.TryRead(Message(challenge: Challenge + "zz"), out state, out proof), "reject nonhex extended suffix");
            Check(!bridge.TryRead(Message(challenge: Challenge + "abc"), out state, out proof), "reject overextended challenge");
            Check(!bridge.TryRead(Message(seccode: "abcdef123456|wrong"), out state, out proof), "require matching secondary proof");
            Check(!bridge.TryRead(Message(validate: "a\nb", seccode: "a\nb|jordan"), out state, out proof), "reject control characters");
            Check(!bridge.TryRead(Message(validate: new string('a',257),seccode:new string('a',257)+"|jordan"), out state, out proof), "reject excessive token length");
            Check(bridge.TryRead("{\"nonce\":\""+Nonce+"\",\"kind\":\"ready\"}", out state, out proof) && state == "ready" && proof == null, "ready is not successful authentication");
            Check(bridge.TryRead(Message(), out state, out proof) && state == "success" && proof.Challenge == Challenge && proof.Validate == "abcdef123456", "accept genuine complete proof");
            Check(!bridge.TryRead(Message(), out state, out proof), "proof can be delivered once");
            bridge = new Services.LoginCaptchaBridge(Nonce, Challenge);
            Check(bridge.TryRead(Message(challenge:Challenge+"9a"), out state, out proof) && proof.Challenge == Challenge+"9a", "preserve provider challenge extension");
            bridge = new Services.LoginCaptchaBridge(Nonce, Challenge);
            Check(!bridge.TryRead(new string('x',4097), out state, out proof), "bound script payload");
            Check(!bridge.TryRead("{\"nonce\":\""+Nonce+"\",\"kind\":\"unknown\"}", out state, out proof), "reject unknown event");
            var html = Services.LoginCaptchaBridge.BuildPage("</script><script>evil()", Challenge, Nonce);
            Check(!html.Contains("</script><script>evil()"), "escape script-delimiting server data");
            Check(html.Contains("https://static.geetest.com/static/tools/gt.js"), "load provider SDK over HTTPS");
            Check(html.Contains("getValidate()") && html.Contains("new_captcha:true"), "use provider proof, not fabricated values");
            Check(html.Contains("default-src 'none'") && html.Contains("connect-src https://*.geetest.com"), "restrict page resource origins");
            Check(!html.Contains("password") && !html.Contains("SESSDATA") && !html.Contains("bili_jct"), "keep account secrets out of verification page");
            System.Console.WriteLine("PASS: " + _count + " human-verification bridge checks. No login/SMS requests sent.");
        }
    }
}
