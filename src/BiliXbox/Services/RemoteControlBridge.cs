using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Windows.Networking.Sockets;
using Windows.System;

namespace BiliXbox.Services
{
    // Test builds only. A LAN text protocol that lets tools/RemoteControl.ps1
    // on a Windows PC press remote buttons in the app, so remote-control
    // handling can be tested without a physical remote. One command per line:
    //   down <VirtualKey> | repeat <VirtualKey> | up <VirtualKey> | press <VirtualKey> | ping
    //   media <PlayPause|Play|Pause|Stop|FastForward|Rewind|Next|Previous>   (system media buttons)
    // Keys are limited to what a TV or media remote sends. The listener is
    // never started in Store builds.
    internal sealed class RemoteControlBridge : IDisposable
    {
        internal const string Port = "18765";
        private static readonly HashSet<VirtualKey> AllowedKeys = new HashSet<VirtualKey>
        {
            VirtualKey.GamepadDPadUp, VirtualKey.GamepadDPadDown, VirtualKey.GamepadDPadLeft, VirtualKey.GamepadDPadRight,
            VirtualKey.NavigationUp, VirtualKey.NavigationDown, VirtualKey.NavigationLeft, VirtualKey.NavigationRight,
            VirtualKey.NavigationAccept, VirtualKey.NavigationCancel, VirtualKey.NavigationMenu, VirtualKey.NavigationView,
            VirtualKey.Up, VirtualKey.Down, VirtualKey.Left, VirtualKey.Right, VirtualKey.Enter, VirtualKey.Escape,
            VirtualKey.GamepadA, VirtualKey.GamepadB, VirtualKey.GamepadX, VirtualKey.GamepadY,
            VirtualKey.GamepadMenu, VirtualKey.GamepadView, VirtualKey.GamepadLeftShoulder, VirtualKey.GamepadRightShoulder,
            VirtualKey.PageUp, VirtualKey.PageDown,
            (VirtualKey)173, (VirtualKey)174, (VirtualKey)175, (VirtualKey)176, (VirtualKey)177, (VirtualKey)178, (VirtualKey)179,
            VirtualKey.Number0, VirtualKey.Number1, VirtualKey.Number2, VirtualKey.Number3, VirtualKey.Number4,
            VirtualKey.Number5, VirtualKey.Number6, VirtualKey.Number7, VirtualKey.Number8, VirtualKey.Number9
        };
        private static readonly HashSet<string> MediaCommands = new HashSet<string>
        { "PlayPause", "Play", "Pause", "Stop", "FastForward", "Rewind", "Next", "Previous" };

        private readonly Func<string, VirtualKey, Task> _dispatch;
        private readonly Func<string, Task> _media;
        private StreamSocketListener _listener;

        internal RemoteControlBridge(Func<string, VirtualKey, Task> dispatch, Func<string, Task> media)
        {
            _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
            _media = media ?? throw new ArgumentNullException(nameof(media));
        }

        internal async Task StartAsync()
        {
            if (_listener != null) return;
            var listener = new StreamSocketListener();
            listener.ConnectionReceived += OnConnection;
            await listener.BindServiceNameAsync(Port);
            _listener = listener;
            PlaybackDiagnosticsLog.Record("remote-bridge listening port=" + Port);
        }

        private async void OnConnection(StreamSocketListener sender, StreamSocketListenerConnectionReceivedEventArgs args)
        {
            var socket = args.Socket;
            PlaybackDiagnosticsLog.Record("remote-bridge connected from=" + socket.Information.RemoteAddress?.DisplayName);
            try
            {
                using (var reader = new StreamReader(socket.InputStream.AsStreamForRead()))
                using (var writer = new StreamWriter(socket.OutputStream.AsStreamForWrite()) { AutoFlush = true })
                {
                    await writer.WriteLineAsync("ok BiliXbox remote bridge");
                    string line;
                    while ((line = await reader.ReadLineAsync()) != null)
                    {
                        var parts = line.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length == 1 && parts[0] == "ping") { await writer.WriteLineAsync("pong"); continue; }
                        if (parts.Length == 2 && parts[0] == "media" && MediaCommands.Contains(parts[1]))
                        { await _media(parts[1]); await writer.WriteLineAsync("ok media " + parts[1]); continue; }
                        VirtualKey key;
                        if (parts.Length != 2 || (parts[0] != "down" && parts[0] != "repeat" && parts[0] != "up" && parts[0] != "press")
                            || !Enum.TryParse(parts[1], true, out key) || !AllowedKeys.Contains(key))
                        { await writer.WriteLineAsync("error " + line.Trim()); continue; }
                        await _dispatch(parts[0], key);
                        await writer.WriteLineAsync("ok " + parts[0] + " " + parts[1]);
                    }
                }
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            { PlaybackDiagnosticsLog.Record("remote-bridge closed error=" + ex.GetType().Name); }
            finally { socket.Dispose(); }
        }

        public void Dispose()
        {
            var listener = _listener; _listener = null;
            try { listener?.Dispose(); } catch (Exception) { }
        }
    }
}
