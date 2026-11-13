using System;
using System.Collections.Generic;
using System.Linq;

namespace BiliXbox.Controls
{
    // Per drawer instance. Unknown writes survive closing/reopening the same
    // video until the user explicitly refreshes its server state.
    internal sealed class VideoInteractionSession
    {
        private readonly HashSet<string> _uncertain = new HashSet<string>(StringComparer.Ordinal);
        private long _generation, _attempt, _activeAttempt;
        internal long Aid { get; private set; }
        internal long AccountMid { get; private set; }
        internal bool Active { get; private set; }
        internal bool Verified { get; private set; }
        internal bool InFlight { get; private set; }
        internal bool IsUncertain => _uncertain.Contains(Key(Aid, AccountMid));
        internal bool CanMutate => Active && AccountMid > 0 && Verified && !InFlight && !IsUncertain;
        internal long Generation => _generation;

        internal long Open(long aid, long accountMid)
        {
            Stop();
            Aid = aid; AccountMid = accountMid; Active = aid > 0;
            Verified = false;
            return _generation;
        }

        internal bool Matches(long generation, long currentAccountMid)
            => Active && generation == _generation && currentAccountMid == AccountMid;

        internal bool AcceptRead(long generation, long aid, long accountMid, bool explicitRefresh)
        {
            if (!Matches(generation, accountMid) || aid != Aid || accountMid <= 0) return false;
            Verified = true;
            if (explicitRefresh && !InFlight) _uncertain.Remove(Key(aid, accountMid));
            return true;
        }

        internal long BeginMutation()
        {
            if (!CanMutate) return 0;
            InFlight = true;
            _activeAttempt = ++_attempt;
            return _activeAttempt;
        }

        internal bool CompleteMutation(long generation, long attempt, bool verified)
        {
            if (!Active || generation != _generation || !InFlight || attempt != _activeAttempt) return false;
            InFlight = false; _activeAttempt = 0; Verified = verified;
            if (verified) _uncertain.Remove(Key(Aid, AccountMid));
            else _uncertain.Add(Key(Aid, AccountMid));
            return true;
        }

        internal void Stop()
        {
            if (InFlight) _uncertain.Add(Key(Aid, AccountMid));
            Active = Verified = InFlight = false; _activeAttempt = 0; ++_generation;
        }

        private static string Key(long aid, long accountMid) => aid + ":" + accountMid;
    }

    internal sealed class VideoFavoriteSelection
    {
        private readonly Dictionary<long, bool> _original = new Dictionary<long, bool>();
        private readonly HashSet<long> _selected = new HashSet<long>();
        internal bool Changed => Added.Count > 0 || Removed.Count > 0;
        internal IReadOnlyList<long> Added => _selected.Where(id => !_original[id]).OrderBy(id => id).ToArray();
        internal IReadOnlyList<long> Removed => _original.Where(pair => pair.Value && !_selected.Contains(pair.Key))
            .Select(pair => pair.Key).OrderBy(id => id).ToArray();
        internal bool Contains(long id) => _selected.Contains(id);
        internal void Reset(IEnumerable<KeyValuePair<long, bool>> folders)
        {
            var replacement = new Dictionary<long, bool>();
            foreach (var folder in folders)
            {
                if (folder.Key <= 0 || replacement.ContainsKey(folder.Key))
                    throw new ArgumentException("收藏夹选择数据无效。", nameof(folders));
                replacement.Add(folder.Key, folder.Value);
            }
            _original.Clear(); _selected.Clear();
            foreach (var folder in replacement)
            {
                _original.Add(folder.Key, folder.Value);
                if (folder.Value) _selected.Add(folder.Key);
            }
        }
        internal bool Toggle(long id)
        {
            if (!_original.ContainsKey(id)) return false;
            if (!_selected.Remove(id)) _selected.Add(id);
            return true;
        }
    }
}
