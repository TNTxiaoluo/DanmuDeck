using System;
using System.Collections.Generic;

namespace BiliXbox.Controls
{
    // State shared by popup buttons and routed/CoreWindow B input. Closing an
    // image must not let a second representation of the same press close its
    // comment reader as well.
    internal sealed class CommentImageNavigationState
    {
        private IReadOnlyList<string> _urls = Array.Empty<string>();
        private long _closedAt = -1;
        private long _confirmBlockedAt = -1;
        private bool _confirmReleased;
        public bool IsOpen { get; private set; }
        public int Index { get; private set; }
        public int Count => _urls.Count;
        public string CurrentUrl => IsOpen ? _urls[Index] : "";

        public bool Open(IReadOnlyList<string> urls, int index, long now = 0)
        {
            if (urls == null || urls.Count == 0) return false;
            int selected = Math.Max(0, Math.Min(urls.Count - 1, index));
            Uri address;
            if (!TryGetAddress(urls[selected], out address)) return false;
            _urls = urls;
            Index = selected;
            IsOpen = true;
            _closedAt = -1;
            _confirmBlockedAt = now;
            _confirmReleased = false;
            return true;
        }

        public void ReleaseConfirm()
        {
            if (IsOpen) _confirmReleased = true;
        }

        public bool ConsumeConfirm(long now, bool isRepeat)
        {
            if (!IsOpen || isRepeat) return false;
            // Opening the popup from raw gamepad A can be followed by the same
            // press as a routed key. An actual release arms an immediate new
            // press; the short fallback also works when an input source omits
            // KeyUp during the popup focus transfer.
            if (!_confirmReleased && (now < _confirmBlockedAt || now - _confirmBlockedAt < 250)) return false;
            _confirmReleased = false;
            _confirmBlockedAt = now;
            return true;
        }

        public bool Move(int direction)
        {
            if (!IsOpen || direction == 0) return false;
            int step = direction > 0 ? 1 : -1;
            for (int next = Index + step; next >= 0 && next < Count; next += step)
            {
                Uri address;
                if (!TryGetAddress(_urls[next], out address)) continue;
                Index = next;
                return true;
            }
            return false;
        }

        public void Close(long now)
        {
            if (!IsOpen) return;
            IsOpen = false;
            _closedAt = now;
            _urls = Array.Empty<string>();
            Index = 0;
            _confirmReleased = false;
        }

        public bool ConsumeBack(long now)
        {
            if (IsOpen) { Close(now); return true; }
            return _closedAt >= 0 && now >= _closedAt && now - _closedAt < 300;
        }

        internal static bool TryGetAddress(string value, out Uri address)
        {
            address = null;
            return !string.IsNullOrWhiteSpace(value) && value.Length <= 8192 &&
                Uri.TryCreate(value, UriKind.Absolute, out address) &&
                address.Scheme == Uri.UriSchemeHttps && address.Host.Length > 0 && address.UserInfo.Length == 0;
        }
    }
}
