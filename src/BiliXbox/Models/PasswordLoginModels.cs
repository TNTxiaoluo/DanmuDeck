using System;

namespace BiliXbox.Models
{
    // Challenge and ticket credentials are short lived, memory only, and never persisted.
    public sealed class LoginCaptchaChallenge
    {
        public string Token { get; internal set; } = "";
        public string Gt { get; internal set; } = "";
        public string Challenge { get; internal set; } = "";
        internal object Owner;
        internal long SessionEpoch;
        internal string Session;
        internal long Mid;
        internal DateTimeOffset Created;
        internal bool Consumed;
        internal object CookieJar;
    }

    public sealed class LoginCaptchaProof
    {
        public string Challenge { get; set; } = "";
        public string Validate { get; set; } = "";
        public string Seccode { get; set; } = "";
    }

    public sealed class LoginCountry
    {
        public int Id { get; internal set; }
        public string CountryCode { get; internal set; } = "";
        public string Name { get; internal set; } = "";
        public string DisplayName => Name + " (+" + CountryCode + ")";
        public override string ToString() => DisplayName;
    }

    public sealed class SmsLoginTicket
    {
        public string Phone { get; internal set; } = "";
        public int CountryId { get; internal set; }
        internal string CountryCode;
        public TimeSpan ResendAfter { get; internal set; } = TimeSpan.FromSeconds(60);
        internal string CaptchaKey;
        internal object Owner;
        internal long SessionEpoch;
        internal string Session;
        internal long Mid;
        internal DateTimeOffset Created;
        internal bool Consumed;
        internal object CookieJar;
    }

    public enum AccountLoginStatus { Success, RequiresSecondaryVerification }

    public sealed class AccountLoginResult
    {
        public AccountLoginStatus Status { get; internal set; }
        public string VerificationUrl { get; internal set; } = "";
        public string Message { get; internal set; } = "";
    }
}
