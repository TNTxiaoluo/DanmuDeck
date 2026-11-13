using System;
using System.IO;
using System.Text;
using Windows.Storage;

namespace BiliXbox.Services
{
    // Small local-only boot records also survive failure before a page is shown.
    // Do not serialize account state, request URLs, cookies or exception messages.
    internal static class StartupDiagnostics
    {
        private static readonly object Gate = new object();
        public static string Stage { get; private set; } = "启动";
        public static string LastFailureSummary { get; private set; } = "";

        public static void Mark(string stage)
        {
            Stage = stage;
            Write("startup-status.txt", DateTimeOffset.UtcNow.ToString("u") + " · " + stage);
        }

        public static void RecordFailure(string stage, Exception error)
        {
            if (error == null) return;
            LastFailureSummary = stage + "\n" + error.GetType().Name + " · 0x" + error.HResult.ToString("X8");
            var details = new StringBuilder(DateTimeOffset.UtcNow.ToString("u") + "\n" + LastFailureSummary);
            for (var current = error; current != null; current = current.InnerException)
            {
                if (details.Length >= 12000) break;
                details.Append("\n").Append(current.GetType().FullName).Append(" · 0x").Append(current.HResult.ToString("X8"));
                details.Append("\n").Append(current.StackTrace);
            }
            if (details.Length > 16000) details.Length = 16000;
            Write("startup-error.txt", details.ToString());
        }

        private static void Write(string fileName, string text)
        {
            // Reporting an error must never become a second startup failure.
            try
            {
                lock (Gate)
                    File.WriteAllText(Path.Combine(ApplicationData.Current.LocalFolder.Path, fileName), text);
            }
            catch (Exception) { }
        }
    }
}
