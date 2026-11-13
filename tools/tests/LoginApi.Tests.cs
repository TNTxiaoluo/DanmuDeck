// Production login and cookie code, isolated transport, actual RSA encryption.
// All identities, passwords, phone numbers, SMS codes and cookies are fixtures.
namespace BiliXbox.LoginApiTests
{
    using BiliXbox.Services;
    using BiliXbox.VideoDetailTests;
    public static class Tests
    {
        private const string Challenge = "0123456789abcdef0123456789abcdef";
        private const string Validation = "abcdef0123456789abcdef0123456789";
        private const string Salt = "fedcba9876543210";
        private const string Password = "fixture-password-测试";
        private static int _checks;
        private static System.Security.Cryptography.RSA _rsa;
        private static Dictionary<string, string> _lastForm;
        private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _custom;
        private static string _loginCookies;
        private static int _loginStatus;
        private static string _loginUrl;
        private static long _navMid;
        private static string AccountCookies(long mid = 42) => "SESSDATA=fixture-session-" + mid + "; Domain=.bilibili.com; Path=/; Secure,"
            + "bili_jct=fixture-csrf-" + mid + "; Domain=.bilibili.com; Path=/; Secure,"
            + "DedeUserID=" + mid + "; Domain=.bilibili.com; Path=/; Secure";
        private static JObject Ok(JToken data) => new JObject { ["code"] = 0, ["data"] = data };
        private static JObject Captcha() => Ok(new JObject { ["type"] = "geetest", ["token"] = "fixture-captcha-token",
            ["geetest"] = new JObject { ["gt"] = Challenge, ["challenge"] = Challenge } });
        private static JObject Countries() => Ok(new JObject
        {
            ["common"] = new JArray(new JObject { ["id"] = 1, ["cname"] = "中国大陆", ["country_id"] = "86" }),
            ["others"] = new JArray(new JObject { ["id"] = 6, ["cname"] = "德国", ["country_id"] = "49" },
                new JObject { ["id"] = 15, ["cname"] = "意大利", ["country_id"] = "39" },
                new JObject { ["id"] = 1, ["cname"] = "duplicate", ["country_id"] = "86" })
        });
        private static LoginCaptchaProof Proof(string suffix = "") => new LoginCaptchaProof
        { Challenge = Challenge + suffix, Validate = Validation, Seccode = Validation + "|jordan" };
        private static void Check(bool value, string message) { if (!value) throw new Exception("FAIL: " + message); _checks++; }
        private static int Posts => Transport.Requests.Count(request => request.Method == "POST");
        private static void Reset()
        {
            _lastForm = null; _custom = null; _loginCookies = AccountCookies(); _loginStatus = 0; _loginUrl = ""; _navMid = 42;
            LoginMemoryStorage.Reset();
            Transport.Reset(async (request, ct) =>
            {
                Check(request.RequestUri.Scheme == "https", "login transport HTTPS only");
                if (request.RequestUri.Host == "passport.bilibili.com")
                {
                    if (!request.RequestUri.AbsolutePath.Contains("qrcode"))
                        Check(request.Headers.Referer?.AbsoluteUri == "https://passport.bilibili.com/login"
                            && request.Headers["Origin"] == "https://passport.bilibili.com", "official login origin and referrer");
                }
                else Check(request.RequestUri.Host == "api.bilibili.com" && request.RequestUri.AbsolutePath == "/x/web-interface/nav", "only passport and authenticated nav are contacted");
                Check(request.RequestUri.Query.IndexOf(Password, StringComparison.Ordinal) < 0 && request.RequestUri.Query.IndexOf("fixture-session", StringComparison.Ordinal) < 0,
                    "password/session credentials never enter request URL");
                if (request.Method.Method == "POST") _lastForm = new Dictionary<string, string>(((HttpFormUrlEncodedContent)request.Content).Values);
                if (_custom != null)
                {
                    var value = await _custom(request, ct);
                    if (value != null) return value;
                }
                switch (request.RequestUri.AbsolutePath)
                {
                    case "/x/passport-login/captcha": return await Transport.Json(Captcha().ToString());
                    case "/web/generic/country/list": return await Transport.Json(Countries().ToString());
                    case "/x/passport-login/web/key": return await Transport.Json(Ok(new JObject { ["hash"] = Salt,
                        ["key"] = "-----BEGIN PUBLIC KEY-----\n" + Convert.ToBase64String(_rsa.ExportSubjectPublicKeyInfo()) + "\n-----END PUBLIC KEY-----\n" }).ToString());
                    case "/x/passport-login/web/sms/send": return await Transport.Json(Ok(new JObject { ["captcha_key"] = "fixture-sms-token" }).ToString());
                    case "/x/passport-login/web/login":
                    case "/x/passport-login/web/login/sms":
                        var response = new HttpResponseMessage { Content = new HttpStringContent(Ok(new JObject { ["status"] = _loginStatus, ["url"] = _loginUrl }).ToString()) };
                        if (_loginCookies != null) response.Headers["Set-Cookie"] = _loginCookies;
                        return response;
                    case "/x/web-interface/nav": return await Transport.Json(Ok(new JObject { ["isLogin"] = true,
                        ["mid"] = _navMid, ["uname"] = "Fixture Account", ["face"] = "https://i0.hdslb.com/avatar.png", ["vipStatus"] = 1 }).ToString());
                    case "/x/passport-login/web/qrcode/generate": return await Transport.Json(Ok(new JObject { ["qrcode_key"] = "fixture-qr-key",
                        ["url"] = "https://passport.bilibili.com/h5-app/passport/login/scan?fixture=1" }).ToString());
                    case "/x/passport-login/web/qrcode/poll":
                        var qr = new HttpResponseMessage { Content = new HttpStringContent(Ok(new JObject { ["code"] = 0, ["url"] = _loginUrl }).ToString()) };
                        if (_loginCookies != null) qr.Headers["Set-Cookie"] = _loginCookies;
                        return qr;
                    default: throw new Exception("Unexpected login route");
                }
            });
        }
        private static async Task Reject(Func<Task> action, string message)
        {
            try { await action(); }
            catch (Exception ex) when (ex is BilibiliApiException || ex is ArgumentException || ex is OperationCanceledException)
            { Check(true, message); return; }
            throw new Exception("FAIL: expected rejection: " + message);
        }
        private static ProtectedCookieStore Cookies(BilibiliClient client) => (ProtectedCookieStore)typeof(BilibiliClient)
            .GetField("_cookies", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(client);
        private static void Existing(BilibiliClient client)
        {
            var response = new HttpResponseMessage();
            response.Headers["Set-Cookie"] = AccountCookies(7) + ",buvid3=genuine-fixture-anonymous; Domain=.bilibili.com; Path=/; Secure";
            Cookies(client).ApplyResponseCookies(response, new Uri("https://passport.bilibili.com/"));
            typeof(BilibiliClient).GetProperty("CurrentUser").SetValue(client, new UserProfile { Mid = 7, Name = "Previous Fixture" });
        }
        private static string Redirect(string host = "passport.biligame.com", string path = "/crossDomain", string extra = "")
            => "https://" + host + path + "?SESSDATA=fixture-session-42&bili_jct=fixture-csrf-42&DedeUserID=42" + extra;
        public static async Task<string> Run()
        {
            _checks = 0;
            using (_rsa = System.Security.Cryptography.RSA.Create(2048))
            {
                Reset();
                using (var client = new BilibiliClient())
                {
                    var countries = await client.GetLoginCountriesAsync();
                    Check(countries.Count == 3 && countries[0].Id == 1 && countries[0].CountryCode == "86", "countries keep distinct record ID and dial code, deduplicate IDs");
                    Check(countries[1].DisplayName == "德国 (+49)", "country label includes actual dial code");
                    await client.GetLoginCountriesAsync();
                    Check(Transport.Requests.Count == 1, "country list cached per client");
                    var challenge = await client.GetLoginCaptchaAsync();
                    Check(challenge.Token == "fixture-captcha-token" && challenge.Challenge == Challenge && challenge.Gt == Challenge, "actual server captcha contract");
                    Check(!client.IsLoggedIn && LoginMemoryStorage.ProtectCalls == 0, "captcha alone does not create/save account");
                    var result = await client.LoginWithPasswordAsync("fixture@example.invalid", Password, challenge, Proof("ab"));
                    Check(result.Status == AccountLoginStatus.Success && client.CurrentUser.Mid == 42 && client.CurrentUser.IsPremium, "password login requires authenticated nav and retains premium profile");
                    var decrypted = Encoding.UTF8.GetString(_rsa.Decrypt(Convert.FromBase64String(_lastForm["password"]), System.Security.Cryptography.RSAEncryptionPadding.Pkcs1));
                    Check(decrypted == Salt + Password && _lastForm["password"] != Password, "real salted UTF-8 PKCS1 RSA encryption");
                    Check(_lastForm["username"] == "fixture@example.invalid" && _lastForm["challenge"] == Challenge + "ab" && _lastForm["seccode"] == Validation + "|jordan", "email and actual successful challenge proof submitted");
                    Check(_lastForm["source"] == "main_web" && _lastForm["keep"] == "0", "website password form matches endpoint");
                    Check(LoginMemoryStorage.ProtectCalls == 1 && LoginMemoryStorage.Files.Count == 1, "one user-bound encrypted vault save");
                    var persisted = Encoding.UTF8.GetString(LoginMemoryStorage.Files.Values.Single().Bytes);
                    Check(!persisted.Contains(Password) && !persisted.Contains("fixture@example.invalid") && !persisted.Contains("fixture-captcha-token"), "password, username and captcha never persisted");
                    var posts = Posts;
                    await Reject(() => client.LoginWithPasswordAsync("fixture@example.invalid", Password, challenge, Proof()), "password proof cannot be reused");
                    Check(Posts == posts, "reused proof rejected before transport");
                }

                foreach (var status in new[] { 1, 2, 5 })
                {
                    Reset(); _loginStatus = status; _loginUrl = "https://passport.bilibili.com/h5-app/passport/risk/verify?tmp_token=fixture";
                    using (var client = new BilibiliClient())
                    {
                        Existing(client);
                        _custom = (request, token) =>
                        {
                            if (request.RequestUri.Host == "passport.bilibili.com")
                            {
                                string header; request.Headers.TryGetValue("Cookie", out header);
                                Check((header ?? "").Contains("buvid3=genuine-fixture-anonymous") && !(header ?? "").Contains("SESSDATA"), "new login retains genuine device cookie but excludes previous auth session");
                            }
                            return Task.FromResult<HttpResponseMessage>(null);
                        };
                        var result = await client.LoginWithPasswordAsync("fixture@example.invalid", Password, await client.GetLoginCaptchaAsync(), Proof());
                        Check(result.Status == AccountLoginStatus.RequiresSecondaryVerification && result.VerificationUrl.StartsWith("https://passport.bilibili.com/", StringComparison.Ordinal), "secondary status is surfaced explicitly");
                        Check(client.CurrentUser.Mid == 7 && Cookies(client).GetCookieValue("SESSDATA") == "fixture-session-7", "secondary response cookies never replace existing signed-in account");
                        Check(LoginMemoryStorage.ProtectCalls == 0 && !Transport.Requests.Any(item => item.Uri.AbsolutePath == "/x/web-interface/nav"), "secondary status never nav-confirms old account or persists candidates");
                    }
                }

                foreach (var url in new[] { "https://evil.invalid/risk", "http://passport.bilibili.com/risk", "https://passport.bilibili.com.evil.invalid/risk", "https://user@passport.bilibili.com/risk", "https://passport.bilibili.com:8443/risk", "https://passport.biligame.com/crossDomain?SESSDATA=fixture" })
                {
                    Reset(); _loginStatus = 2; _loginUrl = url;
                    using (var client = new BilibiliClient())
                    {
                        var result = await client.LoginWithPasswordAsync("fixture@example.invalid", Password, await client.GetLoginCaptchaAsync(), Proof());
                        Check(result.Status == AccountLoginStatus.RequiresSecondaryVerification && result.VerificationUrl == "", "untrusted secondary URL never exposed for navigation");
                    }
                }

                Reset();
                using (var client = new BilibiliClient())
                {
                    var challenge = await client.GetLoginCaptchaAsync(); var count = Transport.Requests.Count;
                    await Reject(() => client.LoginWithPasswordAsync("", Password, challenge, Proof()), "empty username rejected");
                    await Reject(() => client.LoginWithPasswordAsync("a\rb", Password, challenge, Proof()), "control-containing username rejected");
                    await Reject(() => client.LoginWithPasswordAsync("fixture@example.invalid", "", challenge, Proof()), "empty password rejected");
                    await Reject(() => client.LoginWithPasswordAsync("fixture@example.invalid", Password, challenge, new LoginCaptchaProof { Challenge = "another", Validate = Validation, Seccode = Validation + "|jordan" }), "unrelated challenge rejected");
                    await Reject(() => client.LoginWithPasswordAsync("fixture@example.invalid", Password, challenge, new LoginCaptchaProof { Challenge = Challenge, Validate = Validation, Seccode = "invalid" }), "invalid seccode rejected");
                    await Reject(() => client.LoginWithPasswordAsync("fixture@example.invalid", Password, challenge, null), "missing human verification rejected");
                    Check(Transport.Requests.Count == count, "local validation never sends credentials");
                    challenge.Created = DateTimeOffset.UtcNow.AddMinutes(-6);
                    await Reject(() => client.LoginWithPasswordAsync("fixture@example.invalid", Password, challenge, Proof()), "expired challenge rejected");
                }
                Reset();
                using (var client = new BilibiliClient())
                using (var other = new BilibiliClient())
                {
                    var challenge = await client.GetLoginCaptchaAsync();
                    await Reject(() => other.LoginWithPasswordAsync("fixture@example.invalid", Password, challenge, Proof()), "challenge from another client rejected");
                    await client.LogoutAsync();
                    await Reject(() => client.LoginWithPasswordAsync("fixture@example.invalid", Password, challenge, Proof()), "logout invalidates outstanding human verification");
                    Check(Posts == 0, "cross-client and old-epoch challenges cause no POST");
                }

                foreach (var failure in new[] { "missing-cookies", "missing-status", "wrong-nav-mid", "anonymous-nav", "bad-key", "wrong-password", "save-failure" })
                {
                    Reset();
                    if (failure == "missing-cookies") _loginCookies = null;
                    if (failure == "wrong-nav-mid") _navMid = 43;
                    if (failure == "save-failure") LoginMemoryStorage.FailSave = true;
                    _custom = (request, token) =>
                    {
                        if (failure == "missing-status" && request.Method.Method == "POST") return Transport.Json(Ok(new JObject()).ToString());
                        if (failure == "anonymous-nav" && request.RequestUri.AbsolutePath == "/x/web-interface/nav") return Transport.Json("{\"code\":-101,\"message\":\"fixture\"}");
                        if (failure == "bad-key" && request.RequestUri.AbsolutePath == "/x/passport-login/web/key") return Transport.Json(Ok(new JObject { ["hash"] = Salt, ["key"] = "invalid" }).ToString());
                        if (failure == "wrong-password" && request.Method.Method == "POST")
                        {
                            var response = new HttpResponseMessage { Content = new HttpStringContent("{\"code\":-629,\"message\":\"wrong fixture password\"}") };
                            response.Headers["Set-Cookie"] = AccountCookies(); return Task.FromResult(response);
                        }
                        return Task.FromResult<HttpResponseMessage>(null);
                    };
                    using (var client = new BilibiliClient())
                    {
                        Existing(client);
                        await Reject(() => PasswordAttempt(client), "invalid/failed password result rejected: " + failure);
                        Check(client.CurrentUser.Mid == 7 && Cookies(client).GetCookieValue("SESSDATA") == "fixture-session-7", "failed login cannot fall back to previous account: " + failure);
                    }
                }

                await CheckSmsSendFailuresAsync();

                Reset();
                using (var client = new BilibiliClient())
                {
                    var ticket = await client.SendLoginSmsAsync("15555550123", 6, await client.GetLoginCaptchaAsync(), Proof());
                    Check(_lastForm["cid"] == "49" && _lastForm["tel"] == ticket.Phone, "SMS send translates UI country record ID to real dial code");
                    Check(ticket.CountryId == 6 && ticket.ResendAfter == TimeSpan.FromSeconds(60), "SMS ticket binds phone, selected country and cooldown");
                    var result = await client.LoginWithSmsAsync(ticket, "123456");
                    Check(result.Status == AccountLoginStatus.Success && client.CurrentUser.Mid == 42, "SMS login commits actual nav-validated session");
                    Check(_lastForm["cid"] == "49" && _lastForm["captcha_key"] == "fixture-sms-token" && _lastForm["code"] == "123456", "SMS login uses exact phone dial-code ticket and user code");
                    var posts = Posts;
                    await Reject(() => client.LoginWithSmsAsync(ticket, "123456"), "consumed SMS ticket not submitted twice");
                    Check(Posts == posts, "duplicate SMS login causes no POST");
                }

                Reset(); _loginCookies = null; _loginUrl = Redirect();
                using (var client = new BilibiliClient())
                {
                    var ticket = await client.SendLoginSmsAsync("15555550123", 1, await client.GetLoginCaptchaAsync(), Proof());
                    var result = await client.LoginWithSmsAsync(ticket, "123456");
                    Check(result.Status == AccountLoginStatus.Success && client.CurrentUser.Mid == 42, "SMS credentials can come from official server cross-domain URL");
                    Check(!Transport.Requests.Any(item => item.Uri.Host == "passport.biligame.com"), "credential-bearing URL is parsed locally and never followed");
                }

                foreach (var url in new[] { Redirect("evil.invalid"), Redirect("passport.biligame.com.evil.invalid"), Redirect(path: "/wrong"), Redirect(extra: "&SESSDATA=duplicate"), Redirect(extra: "#fragment"), Redirect().Replace("https:", "http:"), Redirect().Replace("fixture-session-42", "unsafe%3bvalue") })
                {
                    Reset(); _loginCookies = null; _loginUrl = url;
                    using (var client = new BilibiliClient())
                    {
                        await Reject(() => SmsAttempt(client), "untrusted or ambiguous login redirect credentials rejected");
                        Check(!client.IsLoggedIn && LoginMemoryStorage.ProtectCalls == 0, "invalid redirect cannot synthesize login");
                    }
                }

                Reset();
                using (var client = new BilibiliClient())
                {
                    var ticket = await client.SendLoginSmsAsync("15555550123", 1, await client.GetLoginCaptchaAsync(), Proof());
                    var next = await client.GetLoginCaptchaAsync(); var posts = Posts;
                    await Reject(() => client.SendLoginSmsAsync("15555550123", 1, next, Proof()), "repeat SMS send blocked for sixty seconds");
                    Check(Posts == posts, "SMS cooldown sends no repeat request");
                    await Reject(() => client.SendLoginSmsAsync("15555550123", 999, next, Proof()), "unknown country rejected");
                    await Reject(() => client.SendLoginSmsAsync("+4915555550123", 1, next, Proof()), "phone field does not silently concatenate dial codes");
                    await Reject(() => client.LoginWithSmsAsync(ticket, "12xx"), "invalid SMS code rejected");
                    ticket.Created = DateTimeOffset.UtcNow.AddMinutes(-6);
                    await Reject(() => client.LoginWithSmsAsync(ticket, "123456"), "expired SMS ticket rejected");
                }

                Reset();
                using (var client = new BilibiliClient())
                {
                    var ticket = await client.SendLoginSmsAsync("15555550123", 1, await client.GetLoginCaptchaAsync(), Proof());
                    var wrong = true;
                    _custom = (request, token) =>
                    {
                        if (wrong && request.RequestUri.AbsolutePath.EndsWith("login/sms", StringComparison.Ordinal))
                            return Transport.Json("{\"code\":1006,\"message\":\"wrong fixture code\"}");
                        return Task.FromResult<HttpResponseMessage>(null);
                    };
                    await Reject(() => client.LoginWithSmsAsync(ticket, "000000"), "definite wrong SMS code surfaced");
                    Check(!ticket.Consumed && !client.IsLoggedIn, "definite wrong SMS code permits manual correction");
                    wrong = false;
                    Check((await client.LoginWithSmsAsync(ticket, "123456")).Status == AccountLoginStatus.Success, "corrected SMS code succeeds with existing ticket");
                }

                Reset();
                using (var client = new BilibiliClient())
                {
                    var ticket = await client.SendLoginSmsAsync("15555550123", 1, await client.GetLoginCaptchaAsync(), Proof());
                    _loginStatus = 5; _loginUrl = "https://passport.bilibili.com/h5-app/passport/risk/verify?tmp_token=fixture";
                    var result = await client.LoginWithSmsAsync(ticket, "123456");
                    Check(result.Status == AccountLoginStatus.RequiresSecondaryVerification && !client.IsLoggedIn && LoginMemoryStorage.ProtectCalls == 0, "SMS status five requires security verification without false success");
                }

                Reset();
                using (var client = new BilibiliClient())
                {
                    var challenge = await client.GetLoginCaptchaAsync();
                    using (var cancel = new CancellationTokenSource())
                    {
                        _custom = (request, token) =>
                        {
                            if (request.Method.Method == "POST") { cancel.Cancel(); return Transport.Json(Ok(new JObject { ["status"] = 0 }).ToString()); }
                            return Task.FromResult<HttpResponseMessage>(null);
                        };
                        await Reject(() => client.LoginWithPasswordAsync("fixture@example.invalid", Password, challenge, Proof(), cancel.Token), "cancelled login response cannot commit account");
                        Check(!client.IsLoggedIn && LoginMemoryStorage.ProtectCalls == 0, "cancelled credentials never persisted");
                    }
                }

                Reset();
                using (var client = new BilibiliClient())
                {
                    Existing(client);
                    var captcha = await client.GetLoginCaptchaAsync();
                    var qr = await client.GenerateQrLoginAsync();
                    Check((await client.PollQrLoginAsync(qr.Key)) == QrLoginStatus.Success && client.CurrentUser.Mid == 42, "QR shares staged cookie commit and user-bound vault");
                    await Reject(() => client.LoginWithPasswordAsync("fixture@example.invalid", Password, captcha, Proof()), "QR success invalidates earlier password challenge");
                    Check((await client.PollQrLoginAsync(qr.Key)) == QrLoginStatus.Expired, "successful QR token cannot commit twice");
                }

                Reset(); _loginCookies = null; _loginUrl = Redirect();
                using (var client = new BilibiliClient())
                {
                    var qr = await client.GenerateQrLoginAsync();
                    Check(await client.PollQrLoginAsync(qr.Key) == QrLoginStatus.Success, "QR official cross-domain credentials nav-validated when header cookies absent");
                }

                Reset();
                using (var client = new BilibiliClient())
                {
                    Existing(client); var qr = await client.GenerateQrLoginAsync();
                    _custom = (request, token) =>
                    {
                        if (request.RequestUri.AbsolutePath.EndsWith("qrcode/poll", StringComparison.Ordinal))
                        {
                            var response = new HttpResponseMessage { Content = new HttpStringContent(Ok(new JObject { ["code"] = 86101 }).ToString()) };
                            response.Headers["Set-Cookie"] = AccountCookies(); return Task.FromResult(response);
                        }
                        return Task.FromResult<HttpResponseMessage>(null);
                    };
                    Check(await client.PollQrLoginAsync(qr.Key) == QrLoginStatus.WaitingForScan && client.CurrentUser.Mid == 7, "waiting QR never replaces old account despite response cookies");
                    await client.LogoutAsync();
                    Check(await client.PollQrLoginAsync(qr.Key) == QrLoginStatus.Expired && !client.IsLoggedIn, "logout invalidates pending QR token");
                }

                await CheckSerializationAndOldNavAsync();
            }
            return "PASS: " + _checks + " login assertions using production services: salted RSA password, SMS dial codes/proofs/cooldown, QR/session epochs, isolated cookie commit, secondary checks, cancellation and encrypted persistence. Mock POSTs only; no real login or SMS sent.";
        }

        private static async Task PasswordAttempt(BilibiliClient client)
            => await client.LoginWithPasswordAsync("fixture@example.invalid", Password, await client.GetLoginCaptchaAsync(), Proof());
        private static async Task SmsAttempt(BilibiliClient client)
            => await client.LoginWithSmsAsync(await client.SendLoginSmsAsync("15555550123", 1, await client.GetLoginCaptchaAsync(), Proof()), "123456");

        private static async Task<BilibiliApiException> RejectedSmsAsync(BilibiliClient client, LoginCaptchaChallenge challenge)
        {
            try { await client.SendLoginSmsAsync("15555550123", 15, challenge, Proof()); }
            catch (BilibiliApiException ex) { return ex; }
            throw new Exception("FAIL: mocked SMS refusal was accepted");
        }

        private static async Task CheckSmsSendFailuresAsync()
        {
            foreach (int code in new[] { 86103, 1002, -105, 2400, 2406 })
            {
                Reset();
                _custom = (request, token) => request.RequestUri.AbsolutePath == "/x/passport-login/web/sms/send"
                    ? Transport.Json(new JObject { ["code"] = code, ["message"] = "fixture refusal", ["data"] = null }.ToString())
                    : Task.FromResult<HttpResponseMessage>(null);
                using (var client = new BilibiliClient())
                {
                    var challenge = await client.GetLoginCaptchaAsync();
                    var error = await RejectedSmsAsync(client, challenge);
                    Check(error.Code == code && error.IsDefiniteSmsRejection && error.SmsRetryAfter == TimeSpan.Zero,
                        "complete SMS business refusal has no false sent countdown: " + code);
                    Check(Posts == 1 && challenge.Consumed && !client.IsLoggedIn && LoginMemoryStorage.ProtectCalls == 0,
                        "refusal uses proof once, never retries or creates an account: " + code);
                    Check(_lastForm["cid"] == "39" && _lastForm["source"] == "main_web" && _lastForm["tel"] == "15555550123",
                        "Italy selection sends its actual dial code without changing source or phone: " + code);
                    if (code == 86103)
                        Check(error.Message.Contains("官方 App") && error.Message.Contains("扫码登录") && error.Message.Contains("密码登录")
                            && !error.Message.Contains("15555550123"), "86103 gives safe official-app/QR/password recovery without phone details");
                    await Reject(() => client.SendLoginSmsAsync("15555550123", 15, challenge, Proof()), "refused proof still cannot be reused");
                    Check(Posts == 1, "same rejected proof cannot cause a second SMS POST");
                    _custom = null;
                    var ticket = await client.SendLoginSmsAsync("15555550123", 15, await client.GetLoginCaptchaAsync(), Proof());
                    Check(Posts == 2 && ticket.CountryId == 15, "only a new explicit verified attempt can proceed after refusal");
                }
            }

            foreach (int code in new[] { 1003, 86203, -509 })
            {
                Reset();
                _custom = (request, token) => request.RequestUri.AbsolutePath == "/x/passport-login/web/sms/send"
                    ? Transport.Json(new JObject { ["code"] = code, ["message"] = "fixture send limit" }.ToString())
                    : Task.FromResult<HttpResponseMessage>(null);
                using (var client = new BilibiliClient())
                {
                    var error = await RejectedSmsAsync(client, await client.GetLoginCaptchaAsync());
                    Check(error.Code == code && error.IsDefiniteSmsRejection && error.SmsRetryAfter == TimeSpan.FromSeconds(60),
                        "definite send/rate limit retains backoff: " + code);
                    var next = await client.GetLoginCaptchaAsync();
                    error = await RejectedSmsAsync(client, next);
                    Check(error.Code == 1003 && error.IsDefiniteSmsRejection && error.SmsRetryAfter > TimeSpan.Zero
                        && error.SmsRetryAfter <= TimeSpan.FromSeconds(60), "local backoff reports actual remaining wait");
                    Check(Posts == 1 && !next.Consumed, "rate-limit wait neither resends SMS nor consumes a fresh proof");
                }
            }

            foreach (var failure in new[] { "http429", "http500", "timeout", "malformed", "missing-code", "missing-data", "missing-key", "unsafe-key" })
            {
                Reset();
                _custom = (request, token) =>
                {
                    if (request.RequestUri.AbsolutePath != "/x/passport-login/web/sms/send") return Task.FromResult<HttpResponseMessage>(null);
                    if (failure == "timeout") throw new OperationCanceledException();
                    if (failure == "http429" || failure == "http500") return Task.FromResult(new HttpResponseMessage
                    { StatusCode = failure == "http429" ? 429 : 500, Content = new HttpStringContent("fixture error") });
                    if (failure == "malformed") return Transport.Json("not-json");
                    if (failure == "missing-code") return Transport.Json("{\"data\":{}}");
                    if (failure == "missing-data") return Transport.Json("{\"code\":0}");
                    if (failure == "unsafe-key") return Transport.Json("{\"code\":0,\"data\":{\"captcha_key\":\"unsafe key\"}}");
                    return Transport.Json("{\"code\":0,\"data\":{}}");
                };
                using (var client = new BilibiliClient())
                {
                    var challenge = await client.GetLoginCaptchaAsync();
                    var error = await RejectedSmsAsync(client, challenge);
                    Check(!error.IsDefiniteSmsRejection && error.SmsRetryAfter == TimeSpan.Zero,
                        "transport/malformed/incomplete success is never mislabeled definite refusal: " + failure);
                    Check(challenge.Consumed && Posts == 1 && !client.IsLoggedIn, "uncertain outcome consumes proof and never retries: " + failure);
                    _custom = null;
                    error = await RejectedSmsAsync(client, await client.GetLoginCaptchaAsync());
                    Check(error.Code == 1003 && error.IsDefiniteSmsRejection && error.SmsRetryAfter > TimeSpan.Zero && Posts == 1,
                        "unknown send outcome keeps server-independent duplicate-send protection: " + failure);
                }
            }
        }

        private static async Task CheckSerializationAndOldNavAsync()
        {
            Reset();
            using (var client = new BilibiliClient())
            {
                var passwordChallenge = await client.GetLoginCaptchaAsync();
                var qr = await client.GenerateQrLoginAsync();
                var postStarted = new TaskCompletionSource<bool>(); var releasePost = new TaskCompletionSource<bool>();
                _custom = async (request, token) =>
                {
                    if (request.RequestUri.AbsolutePath == "/x/passport-login/web/login") { postStarted.TrySetResult(true); await releasePost.Task; }
                    return null;
                };
                var password = client.LoginWithPasswordAsync("fixture@example.invalid", Password, passwordChallenge, Proof());
                await postStarted.Task;
                var poll = client.PollQrLoginAsync(qr.Key);
                Check(!poll.IsCompleted && !Transport.Requests.Any(item => item.Uri.AbsolutePath.EndsWith("qrcode/poll", StringComparison.Ordinal)), "QR and password share one session gate");
                releasePost.SetResult(true);
                await password;
                Check(await poll == QrLoginStatus.Expired && client.CurrentUser.Mid == 42, "losing QR attempt cannot overwrite completed password account");
            }

            Reset();
            using (var client = new BilibiliClient())
            {
                var navStarted = new TaskCompletionSource<bool>(); var releaseNav = new TaskCompletionSource<bool>(); var navs = 0;
                _custom = async (request, token) =>
                {
                    if (request.RequestUri.AbsolutePath == "/x/web-interface/nav" && Interlocked.Increment(ref navs) == 1)
                    {
                        navStarted.TrySetResult(true); await releaseNav.Task;
                        return await Transport.Json("{\"code\":-101,\"data\":{\"wbi_img\":{\"img_url\":\"https://i0.hdslb.com/bfs/wbi/7cd084941338484aae1ad9425b84077c.png\",\"sub_url\":\"https://i0.hdslb.com/bfs/wbi/4932caff0ff746eab6f01bf08b70ac45.png\"}}}");
                    }
                    return null;
                };
                var signedMethod = typeof(BilibiliClient).GetMethod("SignedQueryAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var signed = (Task<string>)signedMethod.Invoke(client, new object[] { new Dictionary<string, string> { ["test"] = "public" }, CancellationToken.None });
                await navStarted.Task;
                await PasswordAttempt(client);
                releaseNav.SetResult(true); await signed;
                Check(client.IsLoggedIn && client.CurrentUser.Mid == 42, "late anonymous WBI nav cannot erase newly committed account");
            }
        }
    }
}
