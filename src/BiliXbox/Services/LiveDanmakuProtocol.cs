using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using BiliXbox.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BiliXbox.Services
{
    // Bilibili live wire protocol: 16-byte big-endian header, JSON, or a zlib
    // envelope containing complete packets. The public client requests protover=2.
    // No Brotli dependency or unknown-version-as-UTF8 fallback is used on Xbox.
    internal static class LiveDanmakuProtocol
    {
        internal const int MaxFrameBytes = 1024 * 1024;
        internal const int MaxExpandedBytes = 4 * 1024 * 1024;
        internal const int MaxMessages = 256;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        internal sealed class Result
        {
            internal int? AuthenticationCode;
            internal bool HeartbeatReply;
            internal readonly List<LiveChatMessage> Messages = new List<LiveChatMessage>();
        }

        private sealed class Budget { internal int Expanded; internal int Packets; }

        internal static byte[] Authentication(LiveDanmakuConnection connection, long roomId)
        {
            var body = new JObject
            {
                ["uid"] = connection.UserId,
                ["roomid"] = roomId,
                ["protover"] = 2,
                ["platform"] = "web",
                ["type"] = 2,
                ["key"] = connection.Token
            };
            if (!string.IsNullOrEmpty(connection.Buvid)) body["buvid"] = connection.Buvid;
            return Packet(7, Utf8.GetBytes(body.ToString(Formatting.None)));
        }

        internal static byte[] Heartbeat() { return Packet(2, new byte[0]); }

        internal static byte[] Packet(int operation, byte[] body, ushort version = 1)
        {
            if (body == null || body.Length > MaxFrameBytes - 16) throw new InvalidDataException("直播数据包过大。");
            var packet = new byte[body.Length + 16];
            Write32(packet, 0, (uint)packet.Length);
            packet[5] = 16;
            packet[6] = (byte)(version >> 8);
            packet[7] = (byte)version;
            Write32(packet, 8, (uint)operation);
            Write32(packet, 12, 1);
            Buffer.BlockCopy(body, 0, packet, 16, body.Length);
            return packet;
        }

        internal static Result Decode(byte[] frame)
        {
            if (frame == null || frame.Length < 16 || frame.Length > MaxFrameBytes)
                throw new InvalidDataException("直播弹幕数据长度无效。");
            var result = new Result();
            DecodePackets(frame, 0, new Budget(), result);
            return result;
        }

        private static void DecodePackets(byte[] bytes, int depth, Budget budget, Result result)
        {
            if (depth > 2) throw new InvalidDataException("直播弹幕压缩层级过深。");
            int offset = 0;
            while (offset < bytes.Length)
            {
                if (bytes.Length - offset < 16 || ++budget.Packets > 2048)
                    throw new InvalidDataException("直播弹幕数据包头无效。");
                uint length = Read32(bytes, offset);
                int header = (bytes[offset + 4] << 8) | bytes[offset + 5];
                int version = (bytes[offset + 6] << 8) | bytes[offset + 7];
                uint operation = Read32(bytes, offset + 8);
                if (header < 16 || length < header || length > bytes.Length - offset || length > MaxFrameBytes)
                    throw new InvalidDataException("直播弹幕数据包边界无效。");
                int count = (int)length - header;
                int start = offset + header;
                if (version == 2)
                {
                    if (operation != 5) throw new InvalidDataException("直播弹幕压缩包类型无效。");
                    DecodePackets(Inflate(bytes, start, count, budget), depth + 1, budget, result);
                }
                else if (version == 0 || version == 1)
                {
                    if (operation == 8)
                    {
                        var auth = Json(bytes, start, count);
                        long code;
                        if (!Number(auth["code"], out code) || code < int.MinValue || code > int.MaxValue)
                            throw new InvalidDataException("直播弹幕认证回复无效。");
                        if (result.AuthenticationCode.HasValue && result.AuthenticationCode.Value != (int)code)
                            throw new InvalidDataException("直播弹幕认证回复冲突。");
                        result.AuthenticationCode = (int)code;
                    }
                    else if (operation == 3)
                    {
                        if (count < 4) throw new InvalidDataException("直播弹幕心跳回复无效。");
                        result.HeartbeatReply = true;
                        Add(result, new LiveChatMessage { Kind = LiveChatMessageKind.Popularity, Popularity = Read32(bytes, start) });
                    }
                    else if (operation == 5) ParseMessage(Json(bytes, start, count), result);
                }
                else throw new InvalidDataException("直播弹幕服务器返回了不支持的压缩格式。");
                offset += (int)length;
            }
        }

        private static byte[] Inflate(byte[] bytes, int offset, int count, Budget budget)
        {
            if (count < 6 || (bytes[offset] & 15) != 8 || (bytes[offset] >> 4) > 7 ||
                (((int)bytes[offset] << 8) + bytes[offset + 1]) % 31 != 0 || (bytes[offset + 1] & 32) != 0)
                throw new InvalidDataException("直播弹幕 zlib 头无效。");
            using (var source = new MemoryStream(bytes, offset + 2, count - 6, false))
            using (var deflate = new DeflateStream(source, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                var buffer = new byte[8192];
                int read;
                while ((read = deflate.Read(buffer, 0, buffer.Length)) != 0)
                {
                    budget.Expanded += read;
                    if (budget.Expanded > MaxExpandedBytes) throw new InvalidDataException("直播弹幕解压数据过大。");
                    output.Write(buffer, 0, read);
                }
                var expanded = output.ToArray();
                uint a = 1, b = 0;
                foreach (byte value in expanded) { a = (a + value) % 65521; b = (b + a) % 65521; }
                if ((b << 16 | a) != Read32(bytes, offset + count - 4))
                    throw new InvalidDataException("直播弹幕 zlib 校验失败。");
                return expanded;
            }
        }

        private static JObject Json(byte[] bytes, int offset, int count)
        {
            if (count == 0 || count > 256 * 1024) throw new InvalidDataException("直播弹幕消息大小无效。");
            try
            {
                using (var source = new StringReader(Utf8.GetString(bytes, offset, count)))
                using (var reader = new JsonTextReader(source) { MaxDepth = 48, DateParseHandling = DateParseHandling.None })
                {
                    var value = JObject.Load(reader);
                    if (reader.Read()) throw new InvalidDataException("直播弹幕消息包含多余内容。");
                    return value;
                }
            }
            catch (JsonException) { throw new InvalidDataException("直播弹幕 JSON 无效。"); }
            catch (DecoderFallbackException) { throw new InvalidDataException("直播弹幕 UTF-8 无效。"); }
        }

        private static void ParseMessage(JObject root, Result result)
        {
            string command = Text(root["cmd"], 64);
            int separator = command.IndexOf(':');
            if (separator >= 0) command = command.Substring(0, separator);
            var data = root["data"] as JObject;
            if (command == "DANMU_MSG")
            {
                var info = root["info"] as JArray;
                if (info == null || info.Count < 3) return;
                var user = info[2] as JArray;
                var properties = info[0] as JArray;
                long color;
                var message = new LiveChatMessage
                {
                    Kind = LiveChatMessageKind.Danmaku,
                    Text = Text(info[1], 512),
                    UserName = user != null && user.Count > 1 ? Text(user[1], 80) : string.Empty,
                    UserId = user != null && user.Count > 0 ? Nonnegative(user[0]) : 0,
                    Color = properties != null && properties.Count > 3 && Number(properties[3], out color) && color >= 0 && color <= 0xFFFFFF ? (uint)color : 0xFFFFFF
                };
                if (message.Text.Length > 0) Add(result, message);
            }
            else if (command == "SEND_GIFT" && data != null)
            {
                Add(result, new LiveChatMessage
                {
                    Kind = LiveChatMessageKind.Gift, UserName = Text(data["uname"], 80),
                    UserId = Nonnegative(data["uid"]), GiftName = Text(data["giftName"], 100),
                    GiftCount = Nonnegative(data["num"]), Text = Text(data["action"], 40)
                });
            }
            else if (command == "SUPER_CHAT_MESSAGE" && data != null)
            {
                var user = data["user_info"] as JObject;
                Add(result, new LiveChatMessage
                {
                    Kind = LiveChatMessageKind.SuperChat, UserName = Text(user?["uname"], 80),
                    UserId = Nonnegative(data["uid"]), Text = Text(data["message"], 1024)
                });
            }
            else if (command == "LIVE" || command == "PREPARING")
                Add(result, new LiveChatMessage { Kind = command == "LIVE" ? LiveChatMessageKind.Live : LiveChatMessageKind.Preparing });
        }

        private static void Add(Result result, LiveChatMessage message)
        {
            if (result.Messages.Count >= MaxMessages) return;
            message.Timestamp = DateTimeOffset.UtcNow;
            result.Messages.Add(message);
        }

        private static long Nonnegative(JToken value) { long number; return Number(value, out number) && number >= 0 ? number : 0; }
        private static bool Number(JToken value, out long result)
        {
            result = 0;
            return value != null && (value.Type == JTokenType.Integer || value.Type == JTokenType.String) &&
                long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
        }
        private static string Text(JToken token, int limit)
        {
            string value = token?.Type == JTokenType.String ? (string)token : string.Empty;
            if (value.Length <= limit) return value;
            if (char.IsHighSurrogate(value[limit - 1])) limit--;
            return value.Substring(0, limit);
        }
        private static uint Read32(byte[] bytes, int offset)
        { return ((uint)bytes[offset] << 24) | ((uint)bytes[offset + 1] << 16) | ((uint)bytes[offset + 2] << 8) | bytes[offset + 3]; }
        private static void Write32(byte[] bytes, int offset, uint value)
        { bytes[offset] = (byte)(value >> 24); bytes[offset + 1] = (byte)(value >> 16); bytes[offset + 2] = (byte)(value >> 8); bytes[offset + 3] = (byte)value; }
    }
}
