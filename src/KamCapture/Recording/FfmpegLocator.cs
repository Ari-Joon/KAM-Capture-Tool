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
