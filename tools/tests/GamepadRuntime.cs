// Read-only probe of the real Windows Gaming Input APIs, without fixtures.
// It neither injects gamepad input nor installs or launches the UWP application.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Windows.Gaming.Input;

internal static class GamepadRuntime
{
    private static readonly object Sync = new object();
    private static readonly List<Gamepad> Tracked = new List<Gamepad>();
    private static int _added, _removed;

    [MTAThread]
    private static int Main(string[] args)
    {
        int seconds = args.Length == 1 ? int.Parse(args[0]) : 3;
        if (seconds < 1 || seconds > 10) return 2;
        int snapshots = 0, reads = 0, maximumConnected = 0, wrapperChanges = 0;
        int transitions = 0;
        var previous = new Dictionary<Gamepad, GamepadButtons>();
        Gamepad.GamepadAdded += OnAdded;
        Gamepad.GamepadRemoved += OnRemoved;
        try
        {
            // The device list can populate asynchronously after the first query.
            foreach (Gamepad pad in Gamepad.Gamepads) Track(pad);
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(seconds))
            {
                var current = Gamepad.Gamepads;
                ++snapshots;
                maximumConnected = Math.Max(maximumConnected, current.Count);
                Gamepad[] stable;
                lock (Sync) stable = Tracked.ToArray();
                foreach (Gamepad pad in stable)
                {
                    bool sameWrapper = false;
                    foreach (Gamepad candidate in current)
                        if (ReferenceEquals(candidate, pad)) { sameWrapper = true; break; }
                    if (!sameWrapper) ++wrapperChanges;
                    var reading = pad.GetCurrentReading();
                    ++reads;
                    GamepadButtons last;
                    if (previous.TryGetValue(pad, out last) && last != reading.Buttons) ++transitions;
                    previous[pad] = reading.Buttons;
                }
                Thread.Sleep(33);
            }
            Console.WriteLine("Real Windows Gamepad API: {0} enumeration snapshots, maximum connected {1}, added {2}, removed {3}.",
                snapshots, maximumConnected, _added, _removed);
            Console.WriteLine("Hardware reads: {0}; button transitions: {1}; tracked-wrapper mismatches: {2}.",
                reads, transitions, wrapperChanges);
            if (reads == 0)
                Console.WriteLine("SKIP: no gamepad reading available; Xbox D-pad routing, focus, and hardware interaction were not tested.");
            else
                Console.WriteLine("PASS: actual GetCurrentReading calls; this console does not verify Xbox XAML focus or player actions.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL: actual Gamepad runtime {0}, HRESULT 0x{1:X8}.", error.GetType().Name, error.HResult);
            return 1;
        }
        finally
        {
            Gamepad.GamepadAdded -= OnAdded;
            Gamepad.GamepadRemoved -= OnRemoved;
        }
    }

    private static void Track(Gamepad pad)
    {
        lock (Sync) if (!Tracked.Contains(pad)) Tracked.Add(pad);
    }

    private static void OnAdded(object sender, Gamepad pad)
    {
        Interlocked.Increment(ref _added);
        Track(pad);
    }

    private static void OnRemoved(object sender, Gamepad pad)
    {
        Interlocked.Increment(ref _removed);
        lock (Sync) Tracked.Remove(pad);
    }
}
