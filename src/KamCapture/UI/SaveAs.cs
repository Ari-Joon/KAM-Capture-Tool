using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using KamCapture.Settings;
using Microsoft.Win32;

namespace KamCapture.UI
{
    /// <summary>
    /// Save as, for screenshots, videos and audio alike.
    ///
    /// The dialog opens in the folder the last Save as of that kind went to,
    /// and suggests the next name in the sequence: after "Lecture 5 slide 3"
    /// comes "Lecture 5 slide 4". Naming a deck of slides one at a time is then
    /// mostly pressing Enter. A name the tool made up itself is not counted on —
    /// its last number is the seconds of a timestamp — so after one of those the
    /// suggestion is simply a fresh automatic name.
    /// </summary>
    public static class SaveAs
    {
        private const string ImageFilter =
            "PNG image (*.png)|*.png|JPEG image (*.jpg)|*.jpg|Bitmap (*.bmp)|*.bmp";

        /// <summary>Ask where a screenshot goes. Null if the dialog was cancelled.</summary>
        public static string? AskForImage(AppSettings cfg, Window? owner)
        {
            var last = cfg.LastScreenshotSaveAs;
            var ext = Path.GetExtension(last).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => ".jpg",
                ".bmp" => ".bmp",
                _ => ".png"
            };
            var folder = StartFolder(last, cfg.EnsureSaveFolder);

            var dlg = new SaveFileDialog
            {
                Title = "Save screenshot as",
                Filter = ImageFilter,
                FilterIndex = ext switch { ".jpg" => 2, ".bmp" => 3, _ => 1 },
                DefaultExt = ext,
                AddExtension = true,
                OverwritePrompt = true,
                InitialDirectory = folder,
                FileName = SuggestName(Path.GetFileNameWithoutExtension(last), cfg, folder, ext)
            };
            if (!Show(dlg, owner)) return null;

            cfg.LastScreenshotSaveAs = dlg.FileName;
            cfg.Save();
            return dlg.FileName;
        }

        /// <summary>
        /// Ask where a finished recording goes, and move it there. Cancelling
        /// leaves it under its automatic name, where it already is: closing a
        /// dialog never throws a recording away.
        /// </summary>
        public static string AskForRecording(AppSettings cfg, string recorded, bool audio, Window? owner)
        {
            var ext = Path.GetExtension(recorded).ToLowerInvariant();
            var last = audio ? cfg.LastAudioSaveAs : cfg.LastVideoSaveAs;
            var folder = StartFolder(last, () => Path.GetDirectoryName(recorded)!);

            var dlg = new SaveFileDialog
            {
                Title = audio ? "Save audio as" : "Save video as",
                Filter = $"{Describe(ext)} (*{ext})|*{ext}",
                DefaultExt = ext,
                AddExtension = true,
                OverwritePrompt = true,
                InitialDirectory = folder,
                FileName = SuggestName(Path.GetFileNameWithoutExtension(last), cfg, folder, ext,
                                       fallback: Path.GetFileNameWithoutExtension(recorded))
            };
            if (!Show(dlg, owner)) return recorded;

            try
            {
                var placed = Place(recorded, WithExtension(dlg.FileName, ext));
                if (audio) cfg.LastAudioSaveAs = placed;
                else cfg.LastVideoSaveAs = placed;
                cfg.Save();
                return placed;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not move the recording there.\n\n{ex.Message}\n\nIt is still saved at:\n{recorded}",
                    "KAM Capture Tool", MessageBoxButton.OK, MessageBoxImage.Warning);
                return recorded;
            }
        }

        /// <summary>
        /// "Lecture 5.mp3", or "Lecture 5.mp3 in Week 3" when it went somewhere
        /// other than the usual folder for its kind.
        /// </summary>
        public static string Describe(string path, string usualFolder)
        {
            var name = Path.GetFileName(path);
            var dir = Path.GetDirectoryName(path) ?? "";
            bool usual = string.Equals(AppSettings.Normalise(dir), AppSettings.Normalise(usualFolder),
                                       StringComparison.OrdinalIgnoreCase);
            return usual ? name : $"{name} in {Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar))}";
        }

        /// <summary>Move a finished file to its chosen path, replacing what is there (the dialog has asked).</summary>
        internal static string Place(string recorded, string chosen)
        {
            if (string.Equals(Path.GetFullPath(recorded), Path.GetFullPath(chosen), StringComparison.OrdinalIgnoreCase))
                return recorded;

            var dir = Path.GetDirectoryName(chosen);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.Move(recorded, chosen, overwrite: true);
            return chosen;
        }

        /// <summary>
        /// A screenshot saved by Save as has just been thrown away. If it was the
        /// last one, step the memory back one, so the next Save as offers its
        /// name again: retaking "slide 4" should be saved as "slide 4".
        /// </summary>
        public static void Forget(AppSettings cfg, string discarded)
        {
            if (!string.Equals(cfg.LastScreenshotSaveAs, discarded, StringComparison.OrdinalIgnoreCase)) return;

            // No number to step back: the folder is still right, and the name
            // suggested next was never going to count on it.
            var previous = PreviousInSequence(Path.GetFileNameWithoutExtension(discarded), cfg.FileNameTemplate);
            if (previous == null) return;

            cfg.LastScreenshotSaveAs = Path.Combine(Path.GetDirectoryName(discarded) ?? "",
                                                    previous + Path.GetExtension(discarded));
            cfg.Save();
        }

        /// <summary>"slide 4" → "slide 3", "slide 10" → "slide 09" when it was padded. Null below 1.</summary>
        internal static string? PreviousInSequence(string? name, string template)
        {
            if (string.IsNullOrWhiteSpace(name) || IsAutomatic(name, template)) return null;

            var m = Regex.Match(name, @"^(.*?)(\d+)$");
            if (!m.Success || m.Groups[2].Value.Length > 18) return null;

            var digits = m.Groups[2].Value;
            var n = long.Parse(digits) - 1;
            if (n < 0) return null;
            return m.Groups[1].Value + n.ToString().PadLeft(digits.Length, '0');
        }

        /// <summary>The name to offer: the next in the last sequence, or a fresh automatic one.</summary>
        internal static string SuggestName(string? lastName, AppSettings cfg, string folder, string ext,
                                           string? fallback = null)
        {
            var next = NextInSequence(lastName, cfg.FileNameTemplate);
            if (next != null)
            {
                // Step past any already taken, so accepting it never overwrites.
                for (int i = 0; i < 1000 && File.Exists(Path.Combine(folder, next + ext)); i++)
                    next = NextInSequence(next, cfg.FileNameTemplate) ?? next;
                return next;
            }

            var automatic = fallback ?? Path.GetFileNameWithoutExtension(cfg.BuildFileName(ext));
            return Path.GetFileNameWithoutExtension(NameDialog.UniquePath(Path.Combine(folder, automatic + ext)));
        }

        /// <summary>
        /// "slide 9" → "slide 10", "slide 09" → "slide 10", keeping the padding.
        /// Null when the name does not end in a number, or is one the tool made up.
        /// </summary>
        internal static string? NextInSequence(string? name, string template)
        {
            if (string.IsNullOrWhiteSpace(name) || IsAutomatic(name, template)) return null;

            var m = Regex.Match(name, @"^(.*?)(\d+)$");
            if (!m.Success || m.Groups[2].Value.Length > 18) return null;

            var digits = m.Groups[2].Value;
            var n = long.Parse(digits) + 1;
            return m.Groups[1].Value + n.ToString().PadLeft(digits.Length, '0');
        }

        /// <summary>True when the name is what the file name template would have produced.</summary>
        internal static bool IsAutomatic(string name, string template)
        {
            var pattern = Regex.Escape(template)
                .Replace(Regex.Escape("{datetime}"), @"\d{4}-\d{2}-\d{2}-\d{2}-\d{2}-\d{2}")
                .Replace(Regex.Escape("{date}"), @"\d{4}-\d{2}-\d{2}")
                .Replace(Regex.Escape("{time}"), @"\d{2}-\d{2}-\d{2}")
                .Replace(Regex.Escape("{unix}"), @"\d+");
            return Regex.IsMatch(name, "^" + pattern + @"(-\d+)?$", RegexOptions.IgnoreCase);
        }

        private static string StartFolder(string? last, Func<string> usual)
        {
            var dir = string.IsNullOrWhiteSpace(last) ? null : Path.GetDirectoryName(last);
            return !string.IsNullOrEmpty(dir) && Directory.Exists(dir) ? dir : usual();
        }

        /// <summary>A recording cannot change format by being renamed, so its extension stays.</summary>
        private static string WithExtension(string chosen, string ext)
        {
            var typed = Path.GetExtension(chosen).ToLowerInvariant();
            if (typed == ext) return chosen;
            return typed is ".mp4" or ".mp3" or ".m4a" or ".wav"
                ? Path.ChangeExtension(chosen, ext)
                : chosen + ext;
        }

        private static string Describe(string ext) => ext switch
        {
            ".mp4" => "MP4 video",
            ".mp3" => "MP3 audio",
            ".m4a" => "M4A audio",
            ".wav" => "WAV audio",
            _ => ext.TrimStart('.').ToUpperInvariant() + " file"
        };

        private static bool Show(SaveFileDialog dlg, Window? owner) =>
            (owner is { IsVisible: true } ? dlg.ShowDialog(owner) : dlg.ShowDialog()) == true;
    }
}
