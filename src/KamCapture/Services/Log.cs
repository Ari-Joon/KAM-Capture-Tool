using System;
using System.Globalization;
using System.IO;
using System.Text;
using KamCapture.Settings;

namespace KamCapture.Services
{
    /// <summary>
    /// A plain text log next to the settings. Small, append-only, and trimmed
    /// when it grows — enough to answer "why did that capture not appear"
    /// without attaching a debugger.
    /// </summary>
    public static class Log
    {
        private static readonly object Gate = new();
        private const long MaxBytes = 512 * 1024;

        public static string Path => System.IO.Path.Combine(AppSettings.Folder, "kam-capture.log");

        public static void Info(string message) => Write("INFO ", message);
        public static void Warn(string message) => Write("WARN ", message);

        public static void Error(string context, Exception ex) =>
            Write("ERROR", context + " — " + Describe(ex));

        private static string Describe(Exception ex)
        {
            var sb = new StringBuilder();
            var e = ex;
            int depth = 0;
            while (e != null && depth++ < 5)
            {
                sb.Append(e.GetType().Name).Append(": ").Append(e.Message);
                if (e.StackTrace != null)
                {
                    var first = e.StackTrace.Split('\n');
                    for (int i = 0; i < Math.Min(4, first.Length); i++)
                        sb.Append("\n        ").Append(first[i].TrimEnd());
                }
                e = e.InnerException;
                if (e != null) sb.Append("\n    caused by ");
            }
            return sb.ToString();
        }

        private static void Write(string level, string message)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(AppSettings.Folder);
                    var path = Path;

                    if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                    {
                        var keep = File.ReadAllLines(path);
                        File.WriteAllLines(path, keep[(keep.Length / 2)..]);
                    }

                    File.AppendAllText(path,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) +
                        "  " + level + "  " + message + Environment.NewLine);
                }
            }
            catch { /* logging must never be the thing that breaks */ }
        }
    }
}
