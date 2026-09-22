using System;
using System.Collections.Generic;
using System.IO;

namespace KamCapture.Services
{
    /// <summary>
    /// Where captures and recordings go.
    ///
    /// Two rules. Saving must never be the thing that fails: if the configured
    /// folder cannot be created or written to, fall back down a list that will
    /// work and say which one was used, rather than throwing the capture away.
    /// And captures stay **on this machine** — Windows' "known folder move"
    /// quietly repoints Pictures into OneDrive, which turns every screenshot
    /// into an upload, so a redirected folder is stepped around in favour of
    /// the real one in the user profile.
    /// </summary>
    public static class OutputFolder
    {
        /// <summary>
        /// Output is grouped under one product folder and split by kind, so
        /// screenshots and recordings never land in the same pile:
        ///
        ///   Pictures\KAM Capture Tool\Screenshots
        ///   Videos\KAM Capture Tool\Recordings
        /// </summary>
        public const string ProductLeaf = "KAM Capture Tool";

        public static string CapturesLeaf => Path.Combine(ProductLeaf, "Screenshots");
        public static string RecordingsLeaf => Path.Combine(ProductLeaf, "Recordings");

        /// <summary>The flat folders used before the split, kept only to move off.</summary>
        private static readonly string[] LegacyLeaves = { "KAM Captures", "KAM Recordings" };

        /// <summary>True when the path lives inside a OneDrive sync root.</summary>
        public static bool IsSynced(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;

            foreach (var name in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
            {
                var root = Environment.GetEnvironmentVariable(name);
                if (!string.IsNullOrWhiteSpace(root) &&
                    path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return path.Contains(@"\OneDrive", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Pictures on this disk, even if the known folder was redirected.</summary>
        public static string LocalPictures() => Local(Environment.SpecialFolder.MyPictures, "Pictures");

        /// <summary>Videos on this disk, even if the known folder was redirected.</summary>
        public static string LocalVideos() => Local(Environment.SpecialFolder.MyVideos, "Videos");

        private static string Local(Environment.SpecialFolder folder, string profileLeaf)
        {
            var known = Environment.GetFolderPath(folder);
            if (!string.IsNullOrWhiteSpace(known) && !IsSynced(known)) return known;

            // Redirected into OneDrive — use the real folder in the profile.
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(profile)) return known ?? Path.GetTempPath();
            return Path.Combine(profile, profileLeaf);
        }

        public static string DefaultCaptures() => Path.Combine(LocalPictures(), CapturesLeaf);
        public static string DefaultRecordings() => Path.Combine(LocalVideos(), RecordingsLeaf);

        /// <summary>True for one of the old flat folders, which should be moved off.</summary>
        public static bool IsLegacyLayout(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            var leaf = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
            foreach (var old in LegacyLeaves)
                if (string.Equals(leaf, old, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// Move anything this tool wrote out of an old flat folder and into the
        /// new one. Only files matching its own naming are touched, and the old
        /// folder is removed only if it ends up empty — someone else's files in
        /// there are left exactly where they are.
        /// </summary>
        public static void MigrateLegacy(string legacyFolder, string target, string extension)
        {
            try
            {
                if (!Directory.Exists(legacyFolder)) return;
                if (string.Equals(Path.GetFullPath(legacyFolder), Path.GetFullPath(target),
                        StringComparison.OrdinalIgnoreCase)) return;

                var moved = 0;
                foreach (var file in Directory.GetFiles(legacyFolder, "KAM-*" + extension))
                {
                    var destination = Path.Combine(target, Path.GetFileName(file));
                    if (File.Exists(destination)) continue;

                    Directory.CreateDirectory(target);
                    File.Move(file, destination);
                    moved++;
                }

                if (moved > 0) Log.Info($"Moved {moved} file(s) from {legacyFolder} to {target}");

                if (Directory.GetFileSystemEntries(legacyFolder).Length == 0)
                {
                    Directory.Delete(legacyFolder);
                    Log.Info("Removed the empty folder " + legacyFolder);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not tidy '{legacyFolder}': {ex.Message}");
            }
        }

        /// <summary>
        /// The folder to actually write to. Tries the preferred one first, then
        /// sensible local alternatives; only considers a synced folder if
        /// nothing local works at all.
        /// </summary>
        public static string Resolve(string preferred, string leaf, bool honourPreferred = false)
        {
            // Explicitly chosen and confirmed: use it if it works at all, even
            // though it syncs. Falling back quietly would override a decision.
            if (honourPreferred && !string.IsNullOrWhiteSpace(preferred) && IsUsable(preferred))
                return preferred;

            var local = new List<string>();
            var synced = new List<string>();

            foreach (var candidate in Candidates(preferred, leaf))
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                (IsSynced(candidate) ? synced : local).Add(candidate);
            }

            foreach (var candidate in local)
                if (IsUsable(candidate)) return candidate;

            foreach (var candidate in synced)
            {
                if (!IsUsable(candidate)) continue;
                Log.Warn("Falling back to a OneDrive folder: " + candidate);
                return candidate;
            }

            var temp = Path.Combine(Path.GetTempPath(), leaf);
            Directory.CreateDirectory(temp);
            Log.Warn("Nothing else was writable; using " + temp);
            return temp;
        }

        private static IEnumerable<string> Candidates(string preferred, string leaf)
        {
            yield return preferred;

            var pictures = LocalPictures();
            yield return Path.Combine(pictures, leaf);
            yield return pictures;

            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(profile))
                yield return Path.Combine(profile, leaf);

            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!string.IsNullOrWhiteSpace(documents))
                yield return Path.Combine(documents, leaf);
        }

        private static bool IsUsable(string folder)
        {
            try
            {
                Directory.CreateDirectory(folder);

                // Creating a directory is not proof it can be written to.
                var probe = Path.Combine(folder, ".kam-write-probe");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn($"Cannot use '{folder}': {ex.GetType().Name} {ex.Message}");
                return false;
            }
        }
    }
}
