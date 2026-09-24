using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace KamCapture.Recording
{
    /// <summary>
    /// Finds ffmpeg. It does the H.264 encoding; everything else — capture,
    /// mixing, timing — happens in this process.
    /// </summary>
    public static class FfmpegLocator
    {
        public static string? Resolve(string? configured)
        {
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
            return Discover();
        }

        private static readonly Dictionary<string, HashSet<string>> Encoders = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Whether this ffmpeg was built with an encoder. Builds differ — MP3
        /// needs libmp3lame, which minimal builds leave out — so ask rather
        /// than find out from a recording that never started. Asked once.
        /// </summary>
        public static bool HasEncoder(string ffmpeg, string name)
        {
            lock (Encoders)
            {
                if (!Encoders.TryGetValue(ffmpeg, out var known))
                {
                    known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    try
                    {
                        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = ffmpeg,
                            Arguments = "-hide_banner -encoders",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true
                        });
                        var text = p!.StandardOutput.ReadToEnd();
                        p.WaitForExit(5000);

                        // Lines look like " A....D libmp3lame   libmp3lame MP3 ...".
                        foreach (var line in text.Split('\n'))
                        {
                            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length >= 2 && parts[0].Length == 6) known.Add(parts[1]);
                        }
                    }
                    catch { /* treat as unknown: the caller falls back */ }
                    Encoders[ffmpeg] = known;
                }
                return known.Contains(name);
            }
        }

        public static string? Discover()
        {
            foreach (var candidate in Candidates())
            {
                try { if (File.Exists(candidate)) return candidate; }
                catch { }
            }
            return null;
        }

        private static IEnumerable<string> Candidates()
        {
            // Shipped alongside the application wins: it is the version we tested.
            var baseDir = AppContext.BaseDirectory;
            yield return Path.Combine(baseDir, "ffmpeg.exe");
            yield return Path.Combine(baseDir, "ffmpeg", "ffmpeg.exe");
            yield return Path.Combine(baseDir, "tools", "ffmpeg.exe");

            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed;
                try { trimmed = dir.Trim().Trim('"'); } catch { continue; }
                if (trimmed.Length == 0) continue;
                yield return Path.Combine(trimmed, "ffmpeg.exe");
            }

            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var wingetRoot = Path.Combine(local, "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(wingetRoot))
            {
                string[] packages;
                try { packages = Directory.GetDirectories(wingetRoot, "*FFmpeg*"); }
                catch { packages = Array.Empty<string>(); }

                foreach (var pkg in packages)
                {
                    string[] found;
                    try { found = Directory.GetFiles(pkg, "ffmpeg.exe", SearchOption.AllDirectories); }
                    catch { continue; }
                    foreach (var f in found.OrderByDescending(x => x)) yield return f;
                }
            }

            foreach (var guess in new[]
            {
                @"C:\ffmpeg\bin\ffmpeg.exe",
                @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
                Path.Combine(local, "ffmpeg", "bin", "ffmpeg.exe"),
            })
                yield return guess;
        }
    }
}
