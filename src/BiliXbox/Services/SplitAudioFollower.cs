using System;
using System.Threading;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace BiliXbox.Services
{
    /// <summary>
    /// Plays a DASH audio representation in a second MediaPlayer that follows
    /// the page's video MediaPlayer. Used only when the video representation is
    /// played as a plain fragmented MP4 (Xbox AdaptiveMediaSource rejects HEVC
    /// Main10 with E_NOINTERFACE while the progressive MP4 source decodes it).
    /// The video player stays the master clock for every existing control:
    /// play, pause, seeking, speed, volume and mute are mirrored, and audio
    /// drift is corrected by repositioning the follower.
    /// </summary>
    internal sealed class SplitAudioFollower : IDisposable
    {
        private static readonly TimeSpan DriftLimit = TimeSpan.FromMilliseconds(150);
        private readonly object _gate = new object();
        private readonly MediaPlayer _master;
        private readonly MediaPlayer _audio;
        private readonly MediaSource _source;
        private readonly IRandomAccessStream _stream;
        private readonly Timer _timer;
        private bool _disposed;
        private int _corrections;

        internal SplitAudioFollower(MediaPlayer master, IRandomAccessStream audioStream)
        {
            _master = master ?? throw new ArgumentNullException(nameof(master));
            _stream = audioStream ?? throw new ArgumentNullException(nameof(audioStream));
            _source = MediaSource.CreateFromStream(audioStream, "audio/mp4");
            _audio = new MediaPlayer { AutoPlay = false, AudioCategory = master.AudioCategory };
            _audio.CommandManager.IsEnabled = false;
            _audio.Volume = master.Volume;
            _audio.IsMuted = master.IsMuted;
            _audio.Source = _source;
            _master.VolumeChanged += MasterVolumeChanged;
            _master.IsMutedChanged += MasterVolumeChanged;
            _master.PlaybackSession.PlaybackStateChanged += MasterStateChanged;
            _master.PlaybackSession.PlaybackRateChanged += MasterStateChanged;
            _master.PlaybackSession.SeekCompleted += MasterSeekCompleted;
            _audio.MediaFailed += AudioFailed;
            _timer = new Timer(_ => Synchronize(false), null, 500, 500);
        }

        /// <summary>Raised once if the audio track itself cannot be played.</summary>
        internal event Action<string> Failed;

        internal int Corrections => Volatile.Read(ref _corrections);

        private void MasterVolumeChanged(MediaPlayer sender, object args)
        {
            lock (_gate)
            {
                if (_disposed) return;
                try { _audio.Volume = _master.Volume; _audio.IsMuted = _master.IsMuted; } catch (Exception) { }
            }
        }

        private void MasterStateChanged(MediaPlaybackSession sender, object args) => Synchronize(false);

        private void MasterSeekCompleted(MediaPlaybackSession sender, object args) => Synchronize(true);

        private void AudioFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            Action<string> failed;
            lock (_gate)
            {
                if (_disposed) return;
                failed = Failed;
                Failed = null;
            }
            failed?.Invoke(args.Error + ":" + (args.ExtendedErrorCode?.HResult.ToString("X8") ?? "none"));
        }

        private void Synchronize(bool forceSeek)
        {
            lock (_gate)
            {
                if (_disposed) return;
                try
                {
                    var master = _master.PlaybackSession;
                    var audio = _audio.PlaybackSession;
                    if (audio.PlaybackState == MediaPlaybackState.None || audio.PlaybackState == MediaPlaybackState.Opening) return;
                    var masterState = master.PlaybackState;
                    if (Math.Abs(audio.PlaybackRate - master.PlaybackRate) > 0.001) audio.PlaybackRate = master.PlaybackRate;
                    var drift = audio.Position - master.Position;
                    if (forceSeek || drift > DriftLimit || drift < -DriftLimit)
                    {
                        // Seeking a paused/buffering follower is cheap; while
                        // playing, only correct audible drift beyond the limit.
                        if (!forceSeek && audio.PlaybackState == MediaPlaybackState.Buffering) return;
                        audio.Position = master.Position;
                        if (!forceSeek) Interlocked.Increment(ref _corrections);
                    }
                    if (masterState == MediaPlaybackState.Playing)
                    {
                        if (audio.PlaybackState != MediaPlaybackState.Playing && audio.PlaybackState != MediaPlaybackState.Buffering)
                            _audio.Play();
                    }
                    else if (audio.PlaybackState == MediaPlaybackState.Playing || audio.PlaybackState == MediaPlaybackState.Buffering)
                        _audio.Pause();
                }
                catch (Exception) { /* A transient session read must never stop video playback. */ }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                Failed = null;
            }
            _timer.Dispose();
            try
            {
                _master.VolumeChanged -= MasterVolumeChanged;
                _master.IsMutedChanged -= MasterVolumeChanged;
                _master.PlaybackSession.PlaybackStateChanged -= MasterStateChanged;
                _master.PlaybackSession.PlaybackRateChanged -= MasterStateChanged;
                _master.PlaybackSession.SeekCompleted -= MasterSeekCompleted;
            }
            catch (Exception) { }
            try { _audio.MediaFailed -= AudioFailed; _audio.Pause(); _audio.Source = null; _audio.Dispose(); } catch (Exception) { }
            try { _source.Dispose(); } catch (Exception) { }
            try { _stream.Dispose(); } catch (Exception) { }
        }
    }
}
