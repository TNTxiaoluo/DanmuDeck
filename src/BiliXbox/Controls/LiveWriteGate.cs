using System;
using System.Collections.Generic;

namespace BiliXbox.Controls
{
    // A timed-out gift may have reached the server. Keep its account/room
    // blocked across drawer close/reopen; a second click must not spend twice.
    internal sealed class LiveWriteGate
    {
        private readonly HashSet<string> _pending = new HashSet<string>();
        private readonly HashSet<string> _uncertain = new HashSet<string>();
        internal bool IsBlocked(long room, long mid) => _pending.Contains(Key(room, mid)) || _uncertain.Contains(Key(room, mid));
        internal bool IsUncertain(long room, long mid) => _uncertain.Contains(Key(room, mid));
        internal bool Begin(long room, long mid)
        {
            if (room <= 0 || mid <= 0 || IsBlocked(room, mid)) return false;
            _pending.Add(Key(room, mid)); return true;
        }
        internal void Finish(long room, long mid, bool knownResult)
        { string key = Key(room, mid); if (!_pending.Remove(key)) return; if (!knownResult) _uncertain.Add(key); }
        internal bool ConfirmAnotherGift(long room, long mid)
        { string key = Key(room, mid); return !_pending.Contains(key) && _uncertain.Remove(key); }
        private static string Key(long room, long mid) => room + ":" + mid;
    }

    internal static class LiveInput
    {
        internal static bool TryRoomId(string input, out long room)
        {
            room = 0; string value = (input ?? "").Trim();
            if (long.TryParse(value, out room)) return room > 0;
            Uri url;
            if (!Uri.TryCreate(value, UriKind.Absolute, out url) && !Uri.TryCreate("https://" + value, UriKind.Absolute, out url)) return false;
            if (!string.Equals(url.Host, "live.bilibili.com", StringComparison.OrdinalIgnoreCase) ||
                (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp)) return false;
            var path = url.AbsolutePath.Trim('/');
            return path.IndexOf('/') < 0 && long.TryParse(path, out room) && room > 0;
        }
    }
}
