#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskLogSource = [IO.File]::ReadAllText((Join-Path $taskRoot 'src/BiliXbox/Services/PlaybackDiagnosticsLog.cs'))
$taskFixture = @'
namespace Windows.Storage
{
    public sealed class ApplicationData
    {
        public static ApplicationData Current = new ApplicationData();
        public Folder LocalFolder = new Folder();
    }
    public sealed class Folder
    {
        private string _path;
        public System.Threading.ManualResetEventSlim BlockWrites, Entered;
        public string Path
        {
            get { Entered?.Set(); BlockWrites?.Wait(); return _path; }
            set { _path = value; }
        }
    }
}
namespace BiliXbox.Services
{
    public static class PlaybackDiagnosticsTests
    {
        private static string ReadLogs(string folder)
        {
            var previous = System.IO.Path.Combine(folder, "playback-diagnostics.previous.log");
            var current = System.IO.Path.Combine(folder, "playback-diagnostics.log");
            return (System.IO.File.Exists(previous) ? System.IO.File.ReadAllText(previous) : "")
                + (System.IO.File.Exists(current) ? System.IO.File.ReadAllText(current) : "");
        }
        public static string Run()
        {
            var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DanmuCabin-log-test-" + System.Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(folder);
            try
            {
                Windows.Storage.ApplicationData.Current.LocalFolder.Path = folder;
                PlaybackDiagnosticsLog.Record("catalog qn=120 auth=True sessionCookie=True\ntrack 2880x2160 codec=hvc1 decision=eligible");
                PlaybackDiagnosticsLog.DrainAsync().GetAwaiter().GetResult();
                var file = System.IO.Path.Combine(folder, "playback-diagnostics.log");
                if (!System.IO.File.ReadAllText(file).Contains("2880x2160")) throw new System.Exception("Missing persisted playback catalogue");
                PlaybackDiagnosticsLog.Record("catalog-begin\n" + new string('c', 30000) + "\nLAST_REJECTED_TRACK_30K");
                PlaybackDiagnosticsLog.DrainAsync().GetAwaiter().GetResult();
                string contents = ReadLogs(folder);
                if (!contents.Contains("LAST_REJECTED_TRACK_30K") || !contents.Contains("part=2/2")) throw new System.Exception("Large catalogue tail was silently lost instead of chunked");
                PlaybackDiagnosticsLog.Record("catalog-cjk\n" + new string('\u8f68', 120000) + "\nLAST_REJECTED_TRACK_CJK");
                PlaybackDiagnosticsLog.DrainAsync().GetAwaiter().GetResult();
                contents = ReadLogs(folder);
                if (!contents.Contains("catalog-cjk") || !contents.Contains("LAST_REJECTED_TRACK_CJK")) throw new System.Exception("Multibyte catalogue did not fit the two retained files");
                PlaybackDiagnosticsLog.Record("catalog-over-limit\n" + new string('z', 200000) + "\nOVER_LIMIT_TAIL");
                PlaybackDiagnosticsLog.DrainAsync().GetAwaiter().GetResult();
                contents = ReadLogs(folder);
                if (!contents.Contains("[report truncated: originalChars=") || contents.Contains("OVER_LIMIT_TAIL")) throw new System.Exception("Oversized report truncation was not explicit and bounded");
                PlaybackDiagnosticsLog.Record("catalog-surrogate\n" + string.Concat(System.Linq.Enumerable.Repeat("\ud83d\ude00", 20000)) + "\nVALID_SURROGATE_TAIL");
                PlaybackDiagnosticsLog.DrainAsync().GetAwaiter().GetResult();
                contents = ReadLogs(folder);
                if (!contents.Contains("VALID_SURROGATE_TAIL") || contents.Contains("\ufffd")) throw new System.Exception("Chunking split a Unicode surrogate pair");
                for (int i = 0; i < 24; ++i)
                {
                    PlaybackDiagnosticsLog.Record(new string('x', 30000));
                    PlaybackDiagnosticsLog.DrainAsync().GetAwaiter().GetResult();
                }
                var logs = System.IO.Directory.GetFiles(folder);
                if (logs.Length != 2 || logs.Any(path => new System.IO.FileInfo(path).Length > 262144)) throw new System.Exception("Diagnostic files did not rotate within byte limit");
                var storage = Windows.Storage.ApplicationData.Current.LocalFolder;
                storage.BlockWrites = new System.Threading.ManualResetEventSlim(false);
                storage.Entered = new System.Threading.ManualResetEventSlim(false);
                PlaybackDiagnosticsLog.Record("block-worker-for-overflow-fixture");
                if (!storage.Entered.Wait(5000)) throw new System.Exception("Diagnostic worker did not start");
                for (int i = 0; i < 40; ++i) PlaybackDiagnosticsLog.Record("queue-item-" + i);
                storage.BlockWrites.Set();
                PlaybackDiagnosticsLog.DrainAsync().GetAwaiter().GetResult();
                storage.BlockWrites.Dispose(); storage.Entered.Dispose(); storage.BlockWrites = storage.Entered = null;
                contents = ReadLogs(folder);
                if (!contents.Contains("[diagnostic reports dropped: 8; queue limit=32]") || !contents.Contains("queue-item-39"))
                    throw new System.Exception("Bounded queue dropped reports without a visible loss marker");
                Windows.Storage.ApplicationData.Current.LocalFolder.Path = System.IO.Path.Combine(folder, "nonexistent");
                PlaybackDiagnosticsLog.Record("write failure cannot break playback");
                PlaybackDiagnosticsLog.DrainAsync().GetAwaiter().GetResult();
                storage.Path = folder;
                PlaybackDiagnosticsLog.Record("logging-recovered");
                PlaybackDiagnosticsLog.DrainAsync().GetAwaiter().GetResult();
                if (!ReadLogs(folder).Contains("[diagnostic write failures: 1]")) throw new System.Exception("Recovered logger silently lost its write-failure count");
                return "PASS: playback diagnostic persistence, large-catalogue tails, Unicode chunks, explicit truncation/queue loss, two-file byte bounds and write-failure isolation (fixture storage).";
            }
            finally { System.IO.Directory.Delete(folder, true); }
        }
    }
}
'@
$taskSource = "using System.Linq;`n" + $taskLogSource + $taskFixture
Add-Type -TypeDefinition $taskSource -CompilerOptions '/langversion:7.3'
[BiliXbox.Services.PlaybackDiagnosticsTests]::Run()
