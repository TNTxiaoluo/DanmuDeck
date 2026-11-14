using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Models;

namespace BiliXbox.Services
{
    public sealed partial class BilibiliClient
    {
        public PlaybackHistoryAccount CapturePlaybackHistoryAccount() => !IsLoggedIn || CurrentUser.Mid <= 0
            || string.IsNullOrEmpty(_cookies.GetCookieValue("bili_jct")) ? null
            : new PlaybackHistoryAccount(CurrentUser.Mid, _loginSessionEpoch, _cookies.GetCookieValue("SESSDATA"));

        public bool IsPlaybackHistoryAccountCurrent(PlaybackHistoryAccount account) => account != null && IsLoggedIn
            && SessionMatches(account.Epoch, account.Mid, account.Session);

        public async Task ReportPlaybackHistoryAsync(PlaybackHistoryReport report, CancellationToken ct = default(CancellationToken))
        {
            if (report == null) throw new ArgumentNullException(nameof(report));
            var target = report.Target;
            if (target == null || target.Account == null || target.Aid <= 0 || target.Cid <= 0
                || report.WatchedSeconds < 1 || report.PositionSeconds < 0 || report.StartTimestamp <= 0)
                throw new ArgumentException("播放历史需要真实的账号、视频和已播放进度。", nameof(report));
            ThrowIfDisposed();
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                var token = timeout.Token;
                await _sessionGate.WaitAsync(token);
                try
                {
                    EnsurePlaybackHistoryAccount(target.Account, token);
                    string csrf = RequireSession();
                    // The official web player signs this subset in the query;
                    // actual watch/progress values and CSRF stay in the POST body.
                    var form = new Dictionary<string, string>
                    {
                        ["aid"] = Invariant(target.Aid), ["cid"] = Invariant(target.Cid),
                        ["mid"] = Invariant(target.Account.Mid), ["type"] = target.EpisodeId > 0 ? "4" : "3",
                        ["dt"] = "2", ["play_type"] = Invariant(report.PlayType),
                        ["start_ts"] = Invariant(report.StartTimestamp),
                        ["realtime"] = Invariant(report.WatchedSeconds), ["real_played_time"] = Invariant(report.WatchedSeconds),
                        ["played_time"] = report.Completed ? "-1" : Invariant(report.PositionSeconds),
                        ["video_duration"] = Invariant(report.DurationSeconds),
                        ["last_play_progress_time"] = Invariant(report.PositionSeconds),
                        ["max_play_progress_time"] = Invariant(report.MaxPositionSeconds),
                        ["quality"] = Invariant(target.Quality), ["is_auto_qn"] = "0",
                        ["platform"] = "web", ["device"] = "web", ["mobi_app"] = "web",
                        ["csrf"] = csrf, ["csrf_token"] = csrf
                    };
                    if (!string.IsNullOrWhiteSpace(target.Bvid)) form["bvid"] = target.Bvid;
                    if (target.EpisodeId > 0) form["epid"] = Invariant(target.EpisodeId);
                    if (target.SeasonId > 0) form["sid"] = Invariant(target.SeasonId);
                    var signed = new Dictionary<string, string>();
                    foreach (string field in new[] { "start_ts", "mid", "aid", "dt", "realtime", "played_time",
                        "real_played_time", "video_duration", "last_play_progress_time" }) signed["w_" + field] = form[field];
                    string query = await SignedQueryAsync(signed, token);
                    EnsurePlaybackHistoryAccount(target.Account, token);
                    var response = await PostJsonAsync(Api + "/x/click-interface/web/heartbeat?" + query, form, token);
                    EnsurePlaybackHistoryAccount(target.Account, token);
                    int code = ResponseCode(response);
                    if (code != 0) throw ApiError(code, Str(response, "message"));
                    // Success commonly has data:null, unlike read endpoints.
                }
                finally { _sessionGate.Release(); }
            }
        }

        private void EnsurePlaybackHistoryAccount(PlaybackHistoryAccount account, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); ThrowIfDisposed();
            if (!IsPlaybackHistoryAccountCurrent(account))
                throw new BilibiliApiException(-101, "账号已改变，已停止旧账号的播放记录同步。");
        }
    }
}
