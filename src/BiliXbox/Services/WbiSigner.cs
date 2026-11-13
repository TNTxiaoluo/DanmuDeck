using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;

namespace BiliXbox.Services
{
    /// <summary>Web WBI request signing. The image keys are obtained from /x/web-interface/nav.</summary>
    internal static class WbiSigner
    {
        private static readonly int[] Permutation =
        {
            46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35,
            27, 43, 5, 49, 33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13,
            37, 48, 7, 16, 24, 55, 40, 61, 26, 17, 0, 1, 60, 51, 30, 4,
            22, 25, 54, 21, 56, 59, 6, 63, 57, 62, 11, 36, 20, 34, 44, 52
        };

        internal static string Sign(IDictionary<string, string> parameters, string imageKey, string subKey,
            long timestamp)
        {
            var keys = imageKey + subKey;
            if (keys.Length != 64)
                throw new BilibiliApiException(-1, "B站签名密钥格式已变化，请稍后重试或更新客户端。");

            var mixin = new StringBuilder(32);
            for (var i = 0; i < 32; i++) mixin.Append(keys[Permutation[i]]);
            var signed = new SortedDictionary<string, string>(parameters, StringComparer.Ordinal)
            {
                ["wts"] = timestamp.ToString(CultureInfo.InvariantCulture)
            };
            signed.Remove("w_rid");
            var query = string.Join("&", signed.Select(p =>
                Encode(p.Key) + "=" + Encode(Filter(p.Value ?? ""))));
            var hashProvider = HashAlgorithmProvider.OpenAlgorithm(HashAlgorithmNames.Md5);
            var bytes = CryptographicBuffer.ConvertStringToBinary(query + mixin, BinaryStringEncoding.Utf8);
            var digest = CryptographicBuffer.EncodeToHexString(hashProvider.HashData(bytes)).ToLowerInvariant();
            return query + "&w_rid=" + digest;
        }

        private static string Filter(string value)
        {
            var result = new StringBuilder(value.Length);
            foreach (var ch in value)
                if (ch != '!' && ch != '\'' && ch != '(' && ch != ')' && ch != '*') result.Append(ch);
            return result.ToString();
        }

        // Uri.EscapeDataString implements the required RFC 3986 encoding (spaces are %20).
        private static string Encode(string value) => Uri.EscapeDataString(value);
    }
}
