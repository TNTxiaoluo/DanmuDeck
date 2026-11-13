using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Models;
using Newtonsoft.Json.Linq;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;
using Windows.Web.Http;

namespace BiliXbox.Services
{
    public sealed partial class BilibiliClient
    {
        private readonly object _loginChallengeOwner = new object();
        private IReadOnlyList<LoginCountry> _loginCountries;
        private readonly Dictionary<string, DateTimeOffset> _smsNextSend = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);

        public async Task<LoginCaptchaChallenge> GetLoginCaptchaAsync(CancellationToken ct = default(CancellationToken))
        {
            await _sessionGate.WaitAsync(ct);
            try
            {
                ThrowIfDisposed();
                var challenge = new LoginCaptchaChallenge
                {
                    Owner = _loginChallengeOwner, SessionEpoch = _loginSessionEpoch,
                    Session = _cookies.GetCookieValue("SESSDATA"), Mid = CurrentUser?.Mid ?? 0,
                    Created = DateTimeOffset.UtcNow, CookieJar = _cookies.CloneForLogin()
                };
                var data = SessionData(await LoginGetAsync("/x/passport-login/captcha?source=main_web",
                    (ProtectedCookieStore)challenge.CookieJar, ct));
                if (Str(data, "type") != "geetest")
                    throw new BilibiliApiException(-1, "B站要求其他验证方式，请使用扫码登录。");
                challenge.Token = Str(data, "token");
                challenge.Gt = Str(data["geetest"], "gt");
                challenge.Challenge = Str(data["geetest"], "challenge");
                if (!LoginToken(challenge.Token) || !Regex.IsMatch(challenge.Gt, "^[0-9a-fA-F]{32}$")
                    || !Regex.IsMatch(challenge.Challenge, "^[0-9a-fA-F]{32}$"))
                    throw new BilibiliApiException(-1, "B站没有返回有效的人机验证，请重新尝试。");
                ct.ThrowIfCancellationRequested();
                return challenge;
            }
            finally { _sessionGate.Release(); }
        }

        public async Task<IReadOnlyList<LoginCountry>> GetLoginCountriesAsync(CancellationToken ct = default(CancellationToken))
        {
            await _sessionGate.WaitAsync(ct);
            try { return await GetLoginCountriesCoreAsync(ct); }
            finally { _sessionGate.Release(); }
        }

        private async Task<IReadOnlyList<LoginCountry>> GetLoginCountriesCoreAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); ThrowIfDisposed();
            if (_loginCountries != null) return _loginCountries;
            var data = SessionData(await LoginGetAsync("/web/generic/country/list", _cookies.CloneForLogin(), ct));
            var common = data["common"] as JArray;
            var others = data["others"] as JArray;
            if (common == null || others == null) throw new BilibiliApiException(-1, "B站没有返回有效的国家与地区列表。");
            var items = common.Concat(others).Select(item => new LoginCountry
            { Id = checked((int)Number(item, "id")), CountryCode = Str(item, "country_id"), Name = PlainText(Str(item, "cname")) })
                .Where(item => item.Id > 0 && Regex.IsMatch(item.CountryCode, "^[0-9]{1,5}$") && !string.IsNullOrWhiteSpace(item.Name))
                .GroupBy(item => item.Id).Select(group => group.First()).ToList();
            if (items.Count == 0 || items.Count > 500) throw new BilibiliApiException(-1, "B站国家与地区列表格式已变化。");
            _loginCountries = items.AsReadOnly();
            return _loginCountries;
        }

        public async Task<AccountLoginResult> LoginWithPasswordAsync(string username, string password,
            LoginCaptchaChallenge challenge, LoginCaptchaProof proof, CancellationToken ct = default(CancellationToken))
        {
            username = (username ?? "").Trim();
            if (username.Length == 0 || username.Length > 254 || username.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
                throw new ArgumentException("请输入手机号或邮箱。", nameof(username));
            if (string.IsNullOrEmpty(password) || password.Length > 128 || password.Any(char.IsControl))
                throw new ArgumentException("请输入有效的密码。", nameof(password));
            await _sessionGate.WaitAsync(ct);
            try
            {
                ValidateLoginProof(challenge, proof, ct);
                // A Geetest proof is one use. Network failure never causes an
                // automatic repeat submission of credentials or SMS requests.
                challenge.Consumed = true;
                var jar = (ProtectedCookieStore)challenge.CookieJar;
                var keyData = SessionData(await LoginGetAsync("/x/passport-login/web/key", jar, ct));
                ValidateLoginContext(challenge.SessionEpoch, challenge.Mid, challenge.Session, ct);
                var encrypted = EncryptLoginPassword(Str(keyData, "key"), Str(keyData, "hash"), password);
                var form = LoginCaptchaForm(challenge, proof);
                form["username"] = username; form["password"] = encrypted; form["keep"] = "0";
                form["go_url"] = Referer;
                var root = await LoginPostAsync("/x/passport-login/web/login", form, jar, ct);
                return await CompleteAccountLoginAsync(root, jar, challenge.SessionEpoch, challenge.Mid, challenge.Session, ct);
            }
            finally { _sessionGate.Release(); }
        }

        public async Task<SmsLoginTicket> SendLoginSmsAsync(string phone, int countryId,
            LoginCaptchaChallenge challenge, LoginCaptchaProof proof, CancellationToken ct = default(CancellationToken))
        {
            phone = NormalizeLoginPhone(phone);
            if (countryId <= 0) throw new ArgumentOutOfRangeException(nameof(countryId));
            await _sessionGate.WaitAsync(ct);
            try
            {
                ValidateLoginProof(challenge, proof, ct);
                var countries = await GetLoginCountriesCoreAsync(ct);
                var country = countries.FirstOrDefault(item => item.Id == countryId);
                if (country == null) throw new ArgumentException("请选择有效的国家或地区。", nameof(countryId));
                ValidateLoginContext(challenge.SessionEpoch, challenge.Mid, challenge.Session, ct);
                var phoneKey = countryId.ToString(CultureInfo.InvariantCulture) + ":" + phone;
                DateTimeOffset next;
                if (_smsNextSend.TryGetValue(phoneKey, out next) && next > DateTimeOffset.UtcNow)
                    throw new BilibiliApiException(1003, "请稍后再获取短信验证码。", true, next - DateTimeOffset.UtcNow);
                challenge.Consumed = true;
                // Also retain cooldown when a request has an unknown outcome.
                // The server remains the authority on actual send limits.
                foreach (var expired in _smsNextSend.Where(pair => pair.Value <= DateTimeOffset.UtcNow).Select(pair => pair.Key).ToList())
                    _smsNextSend.Remove(expired);
                if (_smsNextSend.Count >= 32) throw new BilibiliApiException(1003, "短信请求较多，请稍后再试。", true, TimeSpan.FromSeconds(60));
                _smsNextSend[phoneKey] = DateTimeOffset.UtcNow.AddSeconds(60);
                var form = LoginCaptchaForm(challenge, proof);
                form["tel"] = phone; form["cid"] = country.CountryCode;
                var jar = (ProtectedCookieStore)challenge.CookieJar;
                var root = await LoginPostAsync("/x/passport-login/web/sms/send", form, jar, ct);
                int code = ResponseCode(root);
                if (code != 0)
                {
                    // An explicit refusal is different from a timed-out POST.
                    // Preserve actual send/rate limits, but never show a false
                    // "SMS sent" countdown after a rejected 86103 request.
                    bool rateLimited = code == 1003 || code == 86203 || code == -509;
                    if (!rateLimited) _smsNextSend.Remove(phoneKey);
                    var message = code == 86103
                        ? "B站拒绝了本次短信请求（代码 86103）。请先在最新版官方 App 完成注册或安全验证，再使用扫码登录或密码登录。"
                        : ApiError(code, Str(root, "message")).Message;
                    throw new BilibiliApiException(code, message, true,
                        rateLimited ? TimeSpan.FromSeconds(60) : TimeSpan.Zero);
                }
                var data = SessionData(root);
                var key = Str(data, "captcha_key");
                if (!LoginToken(key)) throw new BilibiliApiException(-1, "B站未返回短信登录凭据，请稍后重新获取验证码。");
                ValidateLoginContext(challenge.SessionEpoch, challenge.Mid, challenge.Session, ct);
                return new SmsLoginTicket { Phone = phone, CountryId = countryId, CountryCode = country.CountryCode, CaptchaKey = key,
                    CookieJar = jar, Owner = _loginChallengeOwner, Created = DateTimeOffset.UtcNow,
                    SessionEpoch = challenge.SessionEpoch, Mid = challenge.Mid, Session = challenge.Session };
            }
            finally { _sessionGate.Release(); }
        }

        public async Task<AccountLoginResult> LoginWithSmsAsync(SmsLoginTicket ticket, string code,
            CancellationToken ct = default(CancellationToken))
        {
            code = (code ?? "").Trim();
            if (!Regex.IsMatch(code, "^[0-9]{4,8}$")) throw new ArgumentException("请输入收到的短信验证码。", nameof(code));
            await _sessionGate.WaitAsync(ct);
            try
            {
                if (ticket == null || !ReferenceEquals(ticket.Owner, _loginChallengeOwner) || ticket.Consumed
                    || !(ticket.CookieJar is ProtectedCookieStore) || !LoginToken(ticket.CaptchaKey)
                    || !Regex.IsMatch(ticket.CountryCode ?? "", "^[0-9]{1,5}$")
                    || ticket.Created.AddMinutes(5) <= DateTimeOffset.UtcNow || ticket.Created > DateTimeOffset.UtcNow)
                    throw new BilibiliApiException(-1, "短信验证码已失效，请重新获取。");
                ValidateLoginContext(ticket.SessionEpoch, ticket.Mid, ticket.Session, ct);
                ticket.Consumed = true;
                var jar = (ProtectedCookieStore)ticket.CookieJar;
                var root = await LoginPostAsync("/x/passport-login/web/login/sms", new Dictionary<string, string>
                { ["tel"] = ticket.Phone, ["cid"] = ticket.CountryCode, ["code"] = code,
                    ["captcha_key"] = ticket.CaptchaKey, ["source"] = "main_web", ["go_url"] = Referer, ["keep"] = "true" }, jar, ct);
                // A definite wrong-code response may be corrected with the same
                // still-valid SMS ticket. Unknown transport outcomes stay used.
                if (ResponseCode(root) == 1006) { ticket.Consumed = false; ticket.CookieJar = jar.CloneForLogin(); }
                return await CompleteAccountLoginAsync(root, jar, ticket.SessionEpoch, ticket.Mid, ticket.Session, ct);
            }
            finally { _sessionGate.Release(); }
        }

        private async Task<AccountLoginResult> CompleteAccountLoginAsync(JObject root, ProtectedCookieStore jar,
            long epoch, long mid, string session, CancellationToken ct)
        {
            ValidateLoginContext(epoch, mid, session, ct);
            int code = ResponseCode(root);
            if (code == -2100) return SecondaryLoginResult(root["data"]);
            var data = SessionData(root);
            int status;
            if (data["status"] == null || !int.TryParse(data["status"].ToString(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out status)) throw new BilibiliApiException(-1, "B站登录状态格式已变化。");
            if (status == 1 || status == 2 || status == 5) return SecondaryLoginResult(data);
            if (status != 0) throw new BilibiliApiException(-1, "B站尚未确认登录，请使用扫码登录或稍后重试。");
            jar.ImportLoginRedirectCookies(Str(data, "url"));
            await CommitLoginSessionAsync(jar, epoch, mid, session, ct);
            return new AccountLoginResult { Status = AccountLoginStatus.Success, Message = "登录成功" };
        }

        private static AccountLoginResult SecondaryLoginResult(JToken data)
        {
            // Never treat an existing account cookie as a successful new login,
            // nor follow a game cross-domain URL containing credential values.
            var url = Str(data, "url");
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || uri.Scheme != "https" || !uri.IsDefaultPort
                || !string.IsNullOrEmpty(uri.UserInfo) || !uri.Host.Equals("passport.bilibili.com", StringComparison.OrdinalIgnoreCase)
                || url.Length > 8192) url = "";
            return new AccountLoginResult { Status = AccountLoginStatus.RequiresSecondaryVerification,
                VerificationUrl = url, Message = "B站要求进一步验证手机号或邮箱。请在官方安全页面完成验证，或改用扫码登录。" };
        }

        private void ValidateLoginProof(LoginCaptchaChallenge challenge, LoginCaptchaProof proof, CancellationToken ct)
        {
            if (challenge == null || !ReferenceEquals(challenge.Owner, _loginChallengeOwner) || challenge.Consumed
                || !(challenge.CookieJar is ProtectedCookieStore) || challenge.Created.AddMinutes(5) <= DateTimeOffset.UtcNow
                || challenge.Created > DateTimeOffset.UtcNow) throw new BilibiliApiException(-1, "人机验证已失效，请重新验证。");
            ValidateLoginContext(challenge.SessionEpoch, challenge.Mid, challenge.Session, ct);
            // Geetest v3 may append a two-hex-character suffix to its original
            // challenge. No unrelated challenge or synthesized proof is used.
            var proofChallenge = proof?.Challenge ?? "";
            if (proof == null || (proofChallenge != challenge.Challenge && !(proofChallenge.Length == challenge.Challenge.Length + 2
                && proofChallenge.StartsWith(challenge.Challenge, StringComparison.Ordinal)
                && Regex.IsMatch(proofChallenge.Substring(challenge.Challenge.Length), "^[0-9a-fA-F]{2}$")))
                || !Regex.IsMatch(proof.Validate ?? "", "^[0-9a-fA-F]{32}$")
                || proof.Seccode != proof.Validate + "|jordan")
                throw new BilibiliApiException(-105, "请先完成B站人机验证。");
        }

        private void ValidateLoginContext(long epoch, long mid, string session, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); ThrowIfDisposed();
            if (!SessionMatches(epoch, mid, session)) throw new BilibiliApiException(-101, "账号已改变，请重新验证并登录。");
        }

        private static Dictionary<string, string> LoginCaptchaForm(LoginCaptchaChallenge challenge, LoginCaptchaProof proof)
            => new Dictionary<string, string> { ["token"] = challenge.Token, ["challenge"] = proof.Challenge,
                ["validate"] = proof.Validate, ["seccode"] = proof.Seccode, ["source"] = "main_web" };

        private static bool LoginToken(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 1024
            && !value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c));

        private static string NormalizeLoginPhone(string phone)
        {
            phone = (phone ?? "").Trim();
            if (!Regex.IsMatch(phone, "^[0-9]{5,20}$")) throw new ArgumentException("请输入手机号，国际区号请在国家或地区中选择。", nameof(phone));
            return phone;
        }

        private Task<JObject> LoginGetAsync(string path, ProtectedCookieStore jar, CancellationToken ct)
            => SendSessionJsonAsync(LoginRequest(HttpMethod.Get, path), jar, ct);

        private Task<JObject> LoginPostAsync(string path, IDictionary<string, string> form, ProtectedCookieStore jar, CancellationToken ct)
        {
            var request = LoginRequest(HttpMethod.Post, path);
            request.Content = new HttpFormUrlEncodedContent(form);
            return SendSessionJsonAsync(request, jar, ct);
        }

        private static HttpRequestMessage LoginRequest(HttpMethod method, string path)
        {
            var request = new HttpRequestMessage(method, new Uri(Passport + path));
            request.Headers.Referer = new Uri(Passport + "/login");
            request.Headers.TryAppendWithoutValidation("Origin", Passport);
            return request;
        }

        private static string EncryptLoginPassword(string pem, string salt, string password)
        {
            if (string.IsNullOrEmpty(pem) || pem.Length > 8192 || !Regex.IsMatch(salt ?? "", "^[0-9a-fA-F]{16}$")
                || !pem.StartsWith("-----BEGIN PUBLIC KEY-----", StringComparison.Ordinal)
                || !pem.TrimEnd().EndsWith("-----END PUBLIC KEY-----", StringComparison.Ordinal))
                throw new BilibiliApiException(-1, "B站未返回有效的密码加密公钥，请重新尝试。");
            try
            {
                var base64 = pem.Replace("-----BEGIN PUBLIC KEY-----", "").Replace("-----END PUBLIC KEY-----", "");
                var algorithm = AsymmetricKeyAlgorithmProvider.OpenAlgorithm(AsymmetricAlgorithmNames.RsaPkcs1);
                var key = algorithm.ImportPublicKey(CryptographicBuffer.DecodeFromBase64String(base64), CryptographicPublicKeyBlobType.X509SubjectPublicKeyInfo);
                var plain = CryptographicBuffer.ConvertStringToBinary(salt + password, BinaryStringEncoding.Utf8);
                return CryptographicBuffer.EncodeToBase64String(CryptographicEngine.Encrypt(key, plain, null));
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            { throw new BilibiliApiException(-1, "密码加密失败，请检查密码长度或使用扫码登录。"); }
        }
    }
}
