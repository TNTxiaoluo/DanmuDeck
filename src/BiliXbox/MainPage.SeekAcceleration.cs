using System;
using BiliXbox.Controls;
using Windows.Media.Playback;
using Windows.UI.Xaml;

namespace BiliXbox
{
    public sealed partial class MainPage
    {
        private readonly SeekAccelerationState _seekAcceleration = new SeekAccelerationState();
        private DispatcherTimer _seekCommitTimer;
        private double? _seekPreviewSeconds;
        private bool _seekPreviewDirty;
        private IMediaPlaybackSource _seekPreviewSource, _seekAccelerationSource;

        private bool SeekPreviewActive => _seekPreviewSeconds.HasValue;

        // Every button, shoulder and remote seek comes here. A press with no
        // pending preview seeks at once; held or rapid presses in the same
        // direction grow their step and only move the preview until input
        // pauses, so a long jump does not restart buffering at every step.
        private void Skip(double seconds)
        {
            if (_media.Source == null || !_media.PlaybackSession.CanSeek || seconds == 0) return;
            var session = _media.PlaybackSession;
            double duration = Math.Max(0, session.NaturalDuration.TotalSeconds);
            var now = DateTimeOffset.UtcNow;
            int direction = Math.Sign(seconds);
            if (!ReferenceEquals(_seekAccelerationSource, _media.Source)) { ClearSeekPreview(); _seekAcceleration.Reset(); }
            _seekAccelerationSource = _media.Source;
            bool pending = SeekPreviewActive && ReferenceEquals(_seekPreviewSource, _media.Source)
                && _seekAcceleration.IsContinuing(direction, now);
            double step = _seekAcceleration.Step(direction, Math.Abs(seconds), duration, now);
            double origin = pending ? _seekPreviewSeconds.Value : (_restorePosition ?? session.Position).TotalSeconds;
            double target = Math.Max(0, Math.Min(duration, origin + step));
            _seekPreviewSource = _media.Source;
            _seekPreviewSeconds = target;
            if (!pending)
            {
                // No preview is waiting: seek now. A press in the other
                // direction also lands here and supersedes the old preview.
                _seekPreviewDirty = false;
                SeekToPosition(TimeSpan.FromSeconds(target));
            }
            else _seekPreviewDirty = true;
            ShowSeekPreview(target, duration);
            RestartSeekCommitTimer();
        }

        private void ShowSeekPreview(double target, double duration)
        {
            _updatingSlider = true;
            SeekSlider.Maximum = Math.Max(1, duration);
            SeekSlider.Value = Math.Min(SeekSlider.Maximum, target);
            _updatingSlider = false;
            TimeLabel.Text = MediaTimeFormatter.Format(target) + " / " + FormatTime(_media.PlaybackSession.NaturalDuration);
            _playerUi.Touch(DateTimeOffset.UtcNow);
        }

        private void RestartSeekCommitTimer()
        {
            if (_seekCommitTimer == null)
            {
                _seekCommitTimer = new DispatcherTimer { Interval = SeekAccelerationState.CommitDelay };
                _seekCommitTimer.Tick += SeekCommitTimer_Tick;
            }
            _seekCommitTimer.Stop();
            _seekCommitTimer.Start();
        }

        private void SeekCommitTimer_Tick(object sender, object e)
        {
            _seekCommitTimer?.Stop();
            var target = _seekPreviewSeconds;
            bool dirty = _seekPreviewDirty;
            bool sameSource = _media.Source != null && ReferenceEquals(_seekPreviewSource, _media.Source);
            ClearSeekPreview();
            if (_disposed || !dirty || !target.HasValue || !sameSource || !_media.PlaybackSession.CanSeek) return;
            SeekToPosition(TimeSpan.FromSeconds(target.Value));
            _playerUi.Touch(DateTimeOffset.UtcNow);
        }

        // Drops a pending preview without seeking. The acceleration streak
        // keeps running so a held direction continues at its current speed.
        private void ClearSeekPreview()
        {
            _seekCommitTimer?.Stop();
            _seekPreviewSeconds = null;
            _seekPreviewDirty = false;
            _seekPreviewSource = null;
        }
    }
}
