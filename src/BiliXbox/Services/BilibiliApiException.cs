using System;

namespace BiliXbox.Services
{
    /// <summary>The code is the real service code (or HTTP status for transport failures).</summary>
    public sealed class BilibiliApiException : Exception
    {
        public int Code { get; }
        // A complete, non-success SMS business response (or a local send-limit
        // check) can report a definite refusal. HTTP/network/JSON errors keep
        // the default unknown outcome so callers cannot resend at once.
        public bool IsDefiniteSmsRejection { get; }
        public TimeSpan SmsRetryAfter { get; }

        public BilibiliApiException(int code, string message)
            : this(code, message, false, TimeSpan.Zero) { }

        public BilibiliApiException(int code, string message, bool isDefiniteSmsRejection, TimeSpan smsRetryAfter) : base(message)
        {
            Code = code;
            IsDefiniteSmsRejection = isDefiniteSmsRejection;
            SmsRetryAfter = smsRetryAfter > TimeSpan.Zero ? smsRetryAfter : TimeSpan.Zero;
        }
    }
}
