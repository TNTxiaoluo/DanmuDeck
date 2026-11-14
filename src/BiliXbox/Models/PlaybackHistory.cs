using System;

namespace BiliXbox.Models
{
    // A short-lived account binding, never persisted or exposed in UI/logs.
    public sealed class PlaybackHistoryAccount
    {
        public long Mid { get; }
        internal long Epoch { get; }
        internal string Session { get; }
        internal PlaybackHistoryAccount(long mid, long epoch, string session)
        { Mid = mid; Epoch = epoch; Session = session ?? ""; }
        internal bool Matches(PlaybackHistoryAccount other) => other != null && Mid == other.Mid
            && Epoch == other.Epoch && string.Equals(Session, other.Session, StringComparison.Ordinal);
    }

    public sealed class PlaybackHistoryTarget
    {
        public PlaybackHistoryAccount Account { get; }
        public long Aid { get; }
        public string Bvid { get; }
        public long Cid { get; }
        public long EpisodeId { get; }
        public long SeasonId { get; }
        public int Quality { get; }
        public PlaybackHistoryTarget(PlaybackHistoryAccount account, long aid, string bvid, long cid,
            long episodeId = 0, long seasonId = 0, int quality = 0)
        {
            Account = account; Aid = aid; Bvid = bvid ?? ""; Cid = cid;
            EpisodeId = episodeId; SeasonId = seasonId; Quality = quality;
        }
        internal bool Matches(PlaybackHistoryTarget other) => other != null && Account != null
            && Account.Matches(other.Account) && Aid == other.Aid && Cid == other.Cid
            && EpisodeId == other.EpisodeId && SeasonId == other.SeasonId;
    }

    public sealed class PlaybackHistoryReport
    {
        public PlaybackHistoryTarget Target { get; }
        public long StartTimestamp { get; }
        public int PositionSeconds { get; }
        public int DurationSeconds { get; }
        public int WatchedSeconds { get; }
        public int MaxPositionSeconds { get; }
        public int PlayType { get; }
        public bool Completed { get; }
        internal PlaybackHistoryReport(PlaybackHistoryTarget target, long start, int position, int duration,
            int watched, int maxPosition, int playType, bool completed)
        {
            Target = target; StartTimestamp = start; PositionSeconds = position; DurationSeconds = duration;
            WatchedSeconds = watched; MaxPositionSeconds = maxPosition; PlayType = playType; Completed = completed;
        }
    }
}
