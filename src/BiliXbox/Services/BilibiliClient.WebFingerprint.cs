using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Windows.Web.Http;

namespace BiliXbox.Services
{
    // The website's scripts give a browser two more device cookies before
    // it may like, coin or favorite: buvid4 (from /x/frontend/finger/spi)
    // and bili_ticket (GenWebTicket, HMAC-SHA256 of the timestamp). Without
    // them account writes can be refused with -403 "账号异常,操作失败".
    public sealed partial class BilibiliClient
    {
        private DateTimeOffset _ticketCheckedUntil;

        partial void InitializeWebFingerprint() { WebFingerprint = EnsureWebFingerprintAsync; }

        private bool _buvidActivated;
        public string FingerprintDiagnostic { get; private set; } = "";

        private static string RandomHex(int bytes)
        {
            var data = new byte[bytes];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(data);
            var builder = new StringBuilder(bytes * 2);
            foreach (var value in data) builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
            return builder.ToString();
        }

        // The website's fingerprint script: _uuid, b_lsid and buvid_fp cookies,
        // then registers the browser with ExClimbWuzhi ("激活 buvid"). Writes
        // from an unregistered buvid are a common cause of -403/-352.
        private async Task EnsureBuvidActivatedAsync(CancellationToken ct)
        {
            if (_buvidActivated) return;
            var expires = DateTimeOffset.UtcNow.AddYears(1);
            long ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string uuid = _cookies.GetCookieValue("_uuid");
            if (string.IsNullOrEmpty(uuid))
            {
                uuid = Guid.NewGuid().ToString().ToUpperInvariant() + (ms % 100000).ToString("D5", CultureInfo.InvariantCulture) + "infoc";
                _cookies.SetClientCookie("_uuid", uuid, expires);
            }
            if (string.IsNullOrEmpty(_cookies.GetCookieValue("b_lsid")))
                _cookies.SetClientCookie("b_lsid", RandomHex(4).ToUpperInvariant() + "_" + ms.ToString("X", CultureInfo.InvariantCulture), null);
            if (string.IsNullOrEmpty(_cookies.GetCookieValue("buvid_fp")))
                _cookies.SetClientCookie("buvid_fp", RandomHex(16), expires);
            var inner = new JObject
            {
                ["3064"] = 1, ["5062"] = ms.ToString(CultureInfo.InvariantCulture), ["03bf"] = "https://www.bilibili.com/",
                ["39c8"] = "333.788.fp.risk", ["34f1"] = "", ["d402"] = "", ["654a"] = "", ["6e7c"] = "1920x1080",
                ["3c43"] = new JObject
                {
                    ["2673"] = 0, ["5766"] = 24, ["6527"] = 0, ["7003"] = 1, ["807e"] = 1, ["b8ce"] = UserAgent,
                    ["641c"] = 0, ["07a4"] = "zh-CN", ["1c57"] = "not available", ["0bd0"] = 8,
                    ["748e"] = new JArray(1080, 1920), ["d61f"] = new JArray(1040, 1920), ["fc9d"] = -480,
                    ["6aa9"] = "Asia/Shanghai", ["75b8"] = 1, ["3b21"] = 1, ["8a1c"] = 0, ["d52f"] = "not available",
                    ["adca"] = "Win32", ["80c9"] = new JArray(), ["13ab"] = "", ["bfe9"] = "", ["a3c1"] = new JArray(),
                    ["6bc5"] = "", ["ed31"] = 0, ["72bd"] = 0, ["097b"] = 0, ["52cd"] = new JArray(0, 0, 0),
                    ["a658"] = new JArray(), ["d02f"] = "124.04347527516074"
                },
                ["54ef"] = "{\"in_new_ab\":true,\"ab_version\":{},\"ab_split_num\":{}}",
                ["8b94"] = "", ["df35"] = uuid, ["07a4"] = "zh-CN", ["5f45"] = null, ["db46"] = 0
            };
            var body = new JObject { ["payload"] = inner.ToString(Newtonsoft.Json.Formatting.None) };
            var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Api + "/x/internal/gaia-gateway/ExClimbWuzhi"))
            {
                Content = new HttpStringContent(body.ToString(Newtonsoft.Json.Formatting.None), Windows.Storage.Streams.UnicodeEncoding.Utf8, "application/json")
            };
            request.Headers.TryAppendWithoutValidation("Origin", "https://www.bilibili.com");
            var root = ParseJson(await SendTextAsync(request, ct));
            int code = ResponseCode(root);
            FingerprintDiagnostic = "exclimb=" + code + " " + Str(root, "message");
            _buvidActivated = code == 0;
            try { await _cookies.SaveAsync(); } catch (Exception) { }
        }

        private async Task EnsureWebFingerprintAsync(CancellationToken ct)
        {
            if (string.IsNullOrEmpty(_cookies.GetCookieValue("buvid4")))
            {
                var spi = await GetJsonAsync(Api + "/x/frontend/finger/spi", ct);
                if (ResponseCode(spi) == 0)
                {
                    var data = spi["data"];
                    string b3 = Str(data, "b_3"), b4 = Str(data, "b_4");
                    var expires = DateTimeOffset.UtcNow.AddYears(1);
                    if (string.IsNullOrEmpty(_cookies.GetCookieValue("buvid3")) && b3.Length > 0) _cookies.SetClientCookie("buvid3", b3, expires);
                    if (b4.Length > 0) _cookies.SetClientCookie("buvid4", b4, expires);
                }
            }
            await EnsureBuvidActivatedAsync(ct);
            if (!string.IsNullOrEmpty(_cookies.GetCookieValue("bili_ticket")) && DateTimeOffset.UtcNow < _ticketCheckedUntil) return;
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string hexsign;
            using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes("XgwSnGZ1p")))
            {
                var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes("ts" + ts.ToString(CultureInfo.InvariantCulture)));
                var builder = new StringBuilder(hash.Length * 2);
                foreach (var value in hash) builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
                hexsign = builder.ToString();
            }
            string csrf = _cookies.GetCookieValue("bili_jct") ?? "";
            var url = Api + "/bapis/bilibili.api.ticket.v1.Ticket/GenWebTicket?key_id=ec02&hexsign=" + hexsign
                + "&context%5Bts%5D=" + ts.ToString(CultureInfo.InvariantCulture) + "&csrf=" + Uri.EscapeDataString(csrf);
            var request = new HttpRequestMessage(HttpMethod.Post, new Uri(url)) { Content = new HttpStringContent("") };
            var root = ParseJson(await SendTextAsync(request, ct));
            if (ResponseCode(root) != 0) return;
            string ticket = Str(root["data"], "ticket");
            long created = Number(root["data"], "created_at"), ttl = Number(root["data"], "ttl");
            if (ticket.Length == 0) return;
            if (created <= 0) created = ts;
            if (ttl <= 0) ttl = 259200;
            var until = DateTimeOffset.FromUnixTimeSeconds(created + ttl);
            _cookies.SetClientCookie("bili_ticket", ticket, until);
            _cookies.SetClientCookie("bili_ticket_expires", (created + ttl).ToString(CultureInfo.InvariantCulture), until);
            _ticketCheckedUntil = until.AddHours(-1);
            try { await _cookies.SaveAsync(); } catch (Exception) { }
        }
    }
}
