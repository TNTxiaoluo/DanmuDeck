using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;

namespace BiliXbox.Services
{
    // Only allowlisted playback metadata is passed here. Never record response
    // JSON, request/media URLs, cookies, account identifiers or native messages.
    internal static class PlaybackDiagnosticsLog
    {
        private static readonly object Gate = new object();
        private sealed class Report
        {
            internal long Id;
            internal string Timestamp, Text;
        }
        private const int MaxReportChars = 131072;
        private const int ChunkChars = 16384;
        private const long MaxFileBytes = 262144;
        private static readonly Encoding Utf8 = new UTF8Encoding(false);
        private static readonly Queue<Report> Pending = new Queue<Report>();
        private static Task _worker = Task.CompletedTask;
        private static bool _running;
        private static long _nextReport, _droppedReports, _writeFailures;
        public static void Record(string report)
        {
            if (string.IsNullOrWhiteSpace(report)) return;
            if (report.Length > MaxReportChars)
            {
                int original = report.Length, kept = MaxReportChars;
                if (char.IsHighSurrogate(report[kept - 1])) --kept;
                report = report.Substring(0, kept) + "\n[report truncated: originalChars=" + original
                    + " keptChars=" + kept + " maxReportChars=" + MaxReportChars + "]\n";
            }
            lock (Gate)
            {
                if (Pending.Count >= 32) { Pending.Dequeue(); ++_droppedReports; }
                Pending.Enqueue(new Report { Id = ++_nextReport, Timestamp = DateTimeOffset.UtcNow.ToString("u"), Text = report });
                if (!_running) { _running = true; _worker = Task.Run(WritePending); }
            }
        }
        public static Task DrainAsync() { lock (Gate) return _worker; }
        private static void WritePending()
        {
            while (true)
            {
                Report report;
                long dropped, failures;
                lock (Gate)
                {
                    if (Pending.Count == 0) { _running = false; return; }
                    report = Pending.Dequeue();
                    dropped = _droppedReports; _droppedReports = 0;
                    failures = _writeFailures; _writeFailures = 0;
                }
                // A catalogue larger than 24 KiB must retain its final track.
                // Split one bounded report in the writer, not into queue items,
                // so a large catalogue cannot evict its own trailing chunks.
                var chunks = new List<string>();
                for (int start = 0; start < report.Text.Length;)
                {
                    int count = Math.Min(ChunkChars, report.Text.Length - start);
                    if (start + count < report.Text.Length)
                    {
                        int newline = report.Text.LastIndexOf('\n', start + count - 1, count);
                        if (newline >= start + count / 2) count = newline - start + 1;
                        if (char.IsHighSurrogate(report.Text[start + count - 1])) --count;
                    }
                    chunks.Add(report.Text.Substring(start, count)); start += count;
                }
                for (int part = 0; part < chunks.Count; ++part)
                {
                    string header = report.Timestamp + " report=" + report.Id + " part=" + (part + 1) + "/" + chunks.Count + "\n";
                    if (part == 0 && dropped > 0) header += "[diagnostic reports dropped: " + dropped + "; queue limit=32]\n";
                    if (part == 0 && failures > 0) header += "[diagnostic write failures: " + failures + "]\n";
                    try { WriteChunk(header + chunks[part] + "\n\n"); }
                    catch (Exception)
                    {
                        lock (Gate)
                        {
                            ++_writeFailures;
                            // If even the marker could not be written, carry it
                            // forward to the next report rather than lose it.
                            if (part == 0) { _droppedReports += dropped; _writeFailures += failures; }
                        }
                    }
                }
            }
        }

        private static void WriteChunk(string text)
        {
            string folder = ApplicationData.Current.LocalFolder.Path;
            string file = Path.Combine(folder, "playback-diagnostics.log");
            var bytes = Utf8.GetBytes(text);
            if (File.Exists(file) && new FileInfo(file).Length + bytes.Length > MaxFileBytes)
            {
                string previous = Path.Combine(folder, "playback-diagnostics.previous.log");
                if (File.Exists(previous)) File.Delete(previous);
                File.Move(file, previous);
            }
            // Each chunk is at most 16 Ki UTF-16 characters, below a file's
            // byte limit even with multibyte CJK text. Retain two files only.
            using (var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.Read))
                stream.Write(bytes, 0, bytes.Length);
        }
    }
}
