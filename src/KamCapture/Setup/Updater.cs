using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using KamCapture.Services;

namespace KamCapture.Setup
{
    /// <summary>A reason an update did not happen, worded for the person reading it.</summary>
    public sealed class UpdateException : Exception
    {
        public UpdateException(string message) : base(message) { }
    }

    /// <summary>
    /// Asks GitHub whether there is a newer release, and fetches it when the
    /// user says yes. The only code in the application that touches the
    /// network: one request to GitHub's releases API, and — only after a yes —
    /// the download of the new executable, which must match the SHA-256 GitHub
    /// published for it, and say it is the version it claims, before it runs.
    /// </summary>
    public static class Updater
    {
        public const string Repo = "Ari-Joon/KAM-Capture-Tool";
        public const string ReleasesPage = "https://github.com/" + Repo + "/releases/latest";
        private const string LatestApi = "https://api.github.com/repos/" + Repo + "/releases/latest";
        private const string AssetName = "KamCapture.exe";
        private const string SumsName = "SHA256SUMS.txt";

        /// <summary>Read in place of GitHub by the end-to-end check, and only inside its sandbox.</summary>
        public const string FeedVariable = "KAM_CAPTURE_UPDATE_FEED";

        public sealed record Release(Version Version, string Title, string Page,
                                     string? Download, string? Sha256, string? Sums, long Size)
        {
            public string Tag => Version.ToString(3);

            /// <summary>"half the memory", from "KAM Capture Tool 1.1.2 - half the memory".</summary>
            public string Summary
            {
                get
                {
                    var m = Regex.Match(Title, @"^\s*KAM Capture Tool\s+v?[\d.]+\s*[-–—:]\s*(.+)$",
                        RegexOptions.IgnoreCase);
                    return m.Success ? m.Groups[1].Value.Trim() : "";
                }
            }
        }

        public static Version Current { get; } =
            Normalise(Assembly.GetExecutingAssembly().GetName().Version) ?? new Version(0, 0, 0);

        public static string UpdatesFolder => Sandbox.Active
            ? Path.Combine(Sandbox.Root!, "Updates")
            : Path.Combine(Path.GetTempPath(), Installer.ProductName, "Updates");

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            // No overall timeout: the download is large. The API call has its own.
            var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("KAM-Capture-Tool/" + Current.ToString(3));
            return http;
        }

        // ---------------- the check ----------------

        /// <summary>The latest published release, whether or not it is newer than this copy.</summary>
        public static async Task<Release> LatestAsync(CancellationToken ct)
        {
            string json;
            var feed = Sandbox.Active ? Environment.GetEnvironmentVariable(FeedVariable) : null;
            if (!string.IsNullOrWhiteSpace(feed))
            {
                json = await File.ReadAllTextAsync(feed, ct);
            }
            else
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                using var request = new HttpRequestMessage(HttpMethod.Get, LatestApi);
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
                using var response = await Http.SendAsync(request, timeout.Token);
                if (!response.IsSuccessStatusCode)
                    throw new UpdateException($"GitHub replied {(int)response.StatusCode} {response.ReasonPhrase}.");
                json = await response.Content.ReadAsStringAsync(timeout.Token);
            }

            return Parse(json) ?? throw new UpdateException("GitHub's reply did not name a version.");
        }

        public static bool IsNewer(Release release) => release.Version > Current;

        internal static Release? Parse(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (IsTrue(root, "draft") || IsTrue(root, "prerelease")) return null;

            var version = ParseVersion(Text(root, "tag_name"));
            if (version == null) return null;

            string? download = null, sha = null, sums = null;
            long size = 0;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = Text(asset, "name");
                    if (string.Equals(name, AssetName, StringComparison.OrdinalIgnoreCase))
                    {
                        download = Text(asset, "browser_download_url");
                        var digest = Text(asset, "digest");
                        if (digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                            sha = digest["sha256:".Length..];
                        if (asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var n)) size = n;
                    }
                    else if (string.Equals(name, SumsName, StringComparison.OrdinalIgnoreCase))
                    {
                        sums = Text(asset, "browser_download_url");
                    }
                }
            }

            return new Release(version, Text(root, "name") ?? "", Text(root, "html_url") ?? ReleasesPage,
                download, sha, sums, size);
        }

        /// <summary>"v1.10.0" and "1.10" both read as 1.10.0, which is newer than 1.9.9.</summary>
        internal static Version? ParseVersion(string? tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return null;
            var m = Regex.Match(tag.Trim(), @"^[vV]?(\d+(?:\.\d+){0,3})$");
            if (!m.Success) return null;
            var text = m.Groups[1].Value;
            return Version.TryParse(text.Contains('.') ? text : text + ".0", out var v) ? Normalise(v) : null;
        }

        private static Version? Normalise(Version? v) =>
            v == null ? null : new Version(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));

        private static string? Text(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static bool IsTrue(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

        // ---------------- the download ----------------

        /// <summary>
        /// Fetch the release's executable and prove it before returning it: the
        /// SHA-256 must match what GitHub published, and the file must say it
        /// is the version the release says it is.
        /// </summary>
        public static async Task<string> DownloadAsync(Release release, IProgress<double>? progress, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(release.Download))
                throw new UpdateException("This release has no " + AssetName + " to install.");

            var expected = release.Sha256 ?? await ChecksumFromSumsAsync(release, ct);
            var destination = Path.Combine(UpdatesFolder, $"KamCapture-{release.Tag}.exe");

            var uri = new Uri(release.Download);
            if (uri.IsFile)
            {
                await using var file = File.OpenRead(uri.LocalPath);
                await SaveVerifiedAsync(file, file.Length, expected, destination, progress, ct);
            }
            else
            {
                using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode)
                    throw new UpdateException($"The download failed: GitHub replied {(int)response.StatusCode}.");
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                await SaveVerifiedAsync(stream, response.Content.Headers.ContentLength ?? release.Size,
                    expected, destination, progress, ct);
            }

            var stamped = Version.TryParse(FileVersionInfo.GetVersionInfo(destination).FileVersion, out var v)
                ? Normalise(v) : null;
            if (stamped != release.Version)
            {
                TryDelete(destination);
                throw new UpdateException(
                    $"The download says it is version {stamped?.ToString(3) ?? "unknown"}, not {release.Tag}, so it was not installed.");
            }
            return destination;
        }

        /// <summary>The release's own SHA256SUMS.txt, for a release whose asset carries no digest.</summary>
        private static async Task<string?> ChecksumFromSumsAsync(Release release, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(release.Sums)) return null;
            try
            {
                var uri = new Uri(release.Sums);
                var text = uri.IsFile
                    ? await File.ReadAllTextAsync(uri.LocalPath, ct)
                    : await Http.GetStringAsync(uri, ct);
                foreach (var line in text.Split('\n'))
                {
                    var parts = line.Trim().Split(new[] { ' ', '\t', '*' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2 && parts[1].Equals(AssetName, StringComparison.OrdinalIgnoreCase))
                        return parts[0];
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn("Could not read " + SumsName + ": " + ex.Message);
            }
            return null;
        }

        /// <summary>
        /// Write the stream to <paramref name="destination"/>, hashing as it
        /// goes, and keep it only if the hash is the expected one. Nothing is
        /// left behind otherwise — not the file, not a partial.
        /// </summary>
        internal static async Task SaveVerifiedAsync(Stream source, long? length, string? expected,
            string destination, IProgress<double>? progress, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(expected))
                throw new UpdateException("GitHub published no checksum for this download, so it was not installed.");

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var partial = destination + ".partial";
            try
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write,
                                 FileShare.None, 1 << 16, useAsync: true))
                {
                    var buffer = new byte[1 << 16];
                    long done = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer, ct)) > 0)
                    {
                        hash.AppendData(buffer, 0, read);
                        await file.WriteAsync(buffer.AsMemory(0, read), ct);
                        done += read;
                        if (length is > 0) progress?.Report(Math.Min(1.0, (double)done / length.Value));
                    }
                }

                var actual = Convert.ToHexString(hash.GetHashAndReset());
                if (!string.Equals(actual, expected.Trim(), StringComparison.OrdinalIgnoreCase))
                    throw new UpdateException(
                        "The download did not match the checksum GitHub published for it, so it was deleted. Nothing was changed.");

                File.Move(partial, destination, overwrite: true);
            }
            finally
            {
                TryDelete(partial);
            }
        }

        // ---------------- the hand-over ----------------

        /// <summary>
        /// Start the downloaded copy with --apply-update. It asks this copy to
        /// close, installs itself where this one is installed, and starts.
        /// Every version from 1.2.0 on must keep accepting --apply-update,
        /// because the copy that starts it is always the older one.
        /// </summary>
        public static void Launch(string exe, bool hidden, bool tray)
        {
            var args = "--apply-update" + (hidden ? " --tray" : "") + (tray ? "" : " --no-tray");
            Process.Start(new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? Path.GetTempPath()
            });
        }

        /// <summary>Downloads whose update has been installed. Anything still in use is left for next time.</summary>
        public static void CleanUpDownloads()
        {
            try
            {
                if (!Directory.Exists(UpdatesFolder)) return;
                foreach (var file in Directory.GetFiles(UpdatesFolder)) TryDelete(file);
            }
            catch { }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
