// Real Windows FLAC-in-MP4 probe, muted; not an Xbox or bit-perfect output test.
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

internal static class HiResRuntime
{
    [MTAThread]
    private static int Main(string[] args)
    {
        try { return Run(args).GetAwaiter().GetResult(); }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL: native audio probe {0}, HRESULT0x{1:X8}", error.GetType().Name, error.HResult);
            return 1;
        }
    }

    private static async Task<int> Run(string[] args)
    {
        if (args.Length != 1 || !File.Exists(args[0])) throw new ArgumentException("Local authorized FLAC-in-MP4 sample required.");
        var codecs = await new CodecQuery().FindAllAsync(CodecKind.Audio, CodecCategory.Decoder, CodecSubtypes.AudioFormatFlac);
        if (codecs.Count == 0) throw new InvalidOperationException("No installed FLAC decoder.");
        Console.WriteLine("Installed native FLAC decoders: {0}.", codecs.Count);
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(args[0]));
        using (var source = MediaSource.CreateFromStorageFile(file))
        using (var player = new MediaPlayer { AutoPlay = false, IsMuted = true })
        {
            var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            player.MediaOpened += (sender, ignored) => opened.TrySetResult(true);
            player.MediaFailed += (sender, failure) =>
            {
                Console.Error.WriteLine("MediaFailed: {0}; HRESULT0x{1:X8}.", failure.Error, failure.ExtendedErrorCode?.HResult ?? 0);
                opened.TrySetResult(false);
            };
            var item = new MediaPlaybackItem(source);
            player.Source = item;
            player.Play();
            if (await Task.WhenAny(opened.Task, Task.Delay(20000)) != opened.Task || !await opened.Task) return 2;
            if (item.AudioTracks.Count == 0) return 3;
            int selected = item.AudioTracks.SelectedIndex;
            if (selected < 0 && item.AudioTracks.Count == 1) selected = 0;
            if (selected < 0) return 4;
            var properties = item.AudioTracks[selected].GetEncodingProperties();
            Console.WriteLine("Native source audio: {0}; {1}Hz; {2}bits; {3}channels.",
                properties.Subtype, properties.SampleRate, properties.BitsPerSample, properties.ChannelCount);
            await Task.Delay(1100);
            double position = player.PlaybackSession.Position.TotalSeconds;
            Console.WriteLine("Muted native playback: {0}; position {1:F3}s.", player.PlaybackSession.PlaybackState, position);
            player.Pause(); player.Source = null;
            if (position <= .3) return 5;
            Console.WriteLine("PASS: Windows opens and plays this FLAC-in-MP4 source. Xbox decode and output sample precision remain unverified.");
            return 0;
        }
    }
}
