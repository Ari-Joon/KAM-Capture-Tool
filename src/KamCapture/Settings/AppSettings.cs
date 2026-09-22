using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KamCapture.Settings
{
    public enum SnipMode { Region, Window, FullScreen, Monitor, Freeform }
    public enum BorderStyleKind { Solid, Dashed, Dotted, Glow }

    public sealed class AppSettings
    {
        // ---- Capture overlay appearance (the colour wheel lives here) ----
        public string BorderColor { get; set; } = "#D9A93A";
        public double BorderThickness { get; set; } = 2.0;
        public BorderStyleKind BorderStyle { get; set; } = BorderStyleKind.Solid;
        public double DimOpacity { get; set; } = 0.45;
        public string DimColor { get; set; } = "#000000";
        public bool ShowCrosshair { get; set; } = true;
        public bool ShowMagnifier { get; set; } = true;
        public bool ShowDimensions { get; set; } = true;
        public bool ShowRuleOfThirds { get; set; } = false;
        public string HandleColor { get; set; } = "#FFFFFF";

        // ---- Capture behaviour ----
        public SnipMode DefaultMode { get; set; } = SnipMode.Region;
        public int DelaySeconds { get; set; } = 0;
        public bool IncludeCursor { get; set; } = false;
        public bool CopyToClipboardOnCapture { get; set; } = true;
        public bool AutoSave { get; set; } = false;
        public bool OpenEditorAfterCapture { get; set; } = true;
        public string SaveFolder { get; set; } = "";
        public string FileNameTemplate { get; set; } = "KAM-{date}-{time}";

        // ---- Editor ----
        public double BoardMargin { get; set; } = 260;
        public string BoardBackground { get; set; } = "#F4F4F2";
        public double DefaultFontSize { get; set; } = 18;
        public string DefaultInkColor { get; set; } = "#E5342A";
        public double DefaultInkThickness { get; set; } = 3;
        public int ExportScale { get; set; } = 1;
        public bool ShowBoardGrid { get; set; } = false;

        // ---- Recording ----
        public int RecordFps { get; set; } = 30;
        public int RecordQuality { get; set; } = 20;          // x264 CRF: lower is better
        public bool RecordCursor { get; set; } = true;
        public bool RecordSystemAudio { get; set; } = true;
        public bool RecordMicrophone { get; set; } = false;
        public string MicrophoneDeviceId { get; set; } = "";
        public double MicrophoneGain { get; set; } = 1.0;
        public double SystemAudioGain { get; set; } = 1.0;
        public string FfmpegPath { get; set; } = "";
        public string VideoEncoder { get; set; } = "auto";    // auto | libx264 | h264_nvenc | h264_qsv | h264_amf
        public string RecordFolder { get; set; } = "";

        /// <summary>
        /// OneDrive folders the user explicitly chose and confirmed. Windows
        /// redirecting Pictures into OneDrive is not a choice, and is undone; a
        /// folder picked and confirmed in Settings is, and is left alone.
        /// </summary>
        public List<string> ConfirmedSyncedFolders { get; set; } = new();

        // ---- Hotkeys ----
        public string HotkeyRegion { get; set; } = "Ctrl+Shift+S";
        public string HotkeyFullScreen { get; set; } = "Ctrl+Shift+F";
        public string HotkeyWindow { get; set; } = "Ctrl+Shift+W";
        public string HotkeyRecord { get; set; } = "Ctrl+Shift+R";
        public bool HotkeysEnabled { get; set; } = true;

        // ---- Shell ----
        public bool StartMinimisedToTray { get; set; } = false;
        public bool SkipSetupPrompt { get; set; } = false;
        public bool RunAtStartup { get; set; } = false;

        // ---------------------------------------------------------------

        [JsonIgnore]
        public static string Folder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KAM Capture Tool");

        [JsonIgnore]
        public static string FilePath => Path.Combine(Folder, "settings.json");

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public static AppSettings Current { get; private set; } = new AppSettings();

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts);
                    if (loaded != null) Current = loaded;
                }
            }
            catch
            {
                // A corrupt settings file must never stop the tool from opening.
                Current = new AppSettings();
            }

            // Captures stay on this machine. If the folder is empty, or points
            // into a OneDrive sync root because Windows redirected Pictures,
            // put it back on local disk.
            var captures = Relocate(Current.SaveFolder,
                Services.OutputFolder.DefaultCaptures(), ".png", Current.IsChosen);
            var recordings = Relocate(Current.RecordFolder,
                Services.OutputFolder.DefaultRecordings(), ".mp4", Current.IsChosen);

            // Write the move back out, or the settings file keeps pointing at a
            // folder nothing is being saved to.
            bool moved = !string.Equals(captures, Current.SaveFolder, StringComparison.OrdinalIgnoreCase)
                      || !string.Equals(recordings, Current.RecordFolder, StringComparison.OrdinalIgnoreCase);

            Current.SaveFolder = captures;
            Current.RecordFolder = recordings;
            if (moved) Current.Save();

            return Current;
        }

        /// <summary>
        /// Keep output local and organised. An empty setting, one Windows has
        /// redirected into OneDrive, or one of the old flat folders all move to
        /// the current default â€” bringing anything this tool wrote along with
        /// them. A folder the user deliberately chose is left alone.
        /// </summary>
        internal static string Relocate(string configured, string localDefault, string extension,
                                       Func<string, bool> chosen)
        {
            if (string.IsNullOrWhiteSpace(configured)) return localDefault;

            // A OneDrive folder the user picked and confirmed is theirs to keep.
            if (Services.OutputFolder.IsSynced(configured) && chosen(configured))
                return configured;

            if (Services.OutputFolder.IsSynced(configured) ||
                Services.OutputFolder.IsLegacyLayout(configured))
            {
                Services.OutputFolder.MigrateLegacy(configured, localDefault, extension);
                return localDefault;
            }

            return configured;
        }

        /// <summary>
        /// The folder captures will actually be written to, created and proven
        /// writable first. If the configured one cannot be used â€” or has been
        /// redirected into OneDrive â€” this returns a local one instead and
        /// remembers it, so a capture is never lost to a folder problem and
        /// never quietly turned into an upload.
        /// </summary>
        public string EnsureSaveFolder() =>
            Remember(Services.OutputFolder.Resolve(SaveFolder, Services.OutputFolder.CapturesLeaf, IsChosen(SaveFolder)),
                     SaveFolder, v => SaveFolder = v, "Capture");

        public string EnsureRecordFolder() =>
            Remember(Services.OutputFolder.Resolve(RecordFolder, Services.OutputFolder.RecordingsLeaf, IsChosen(RecordFolder)),
                     RecordFolder, v => RecordFolder = v, "Recording");

        /// <summary>True when this synced folder was explicitly confirmed in Settings.</summary>
        public bool IsChosen(string? path)
        {
            var key = Normalise(path);
            if (key == null) return false;
            foreach (var chosen in ConfirmedSyncedFolders)
                if (string.Equals(Normalise(chosen), key, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static string? Normalise(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim())); }
            catch { return path.Trim(); }
        }

        private string Remember(string resolved, string current, Action<string> set, string what)
        {
            if (!string.Equals(resolved, current, StringComparison.OrdinalIgnoreCase))
            {
                set(resolved);
                Save();
                Services.Log.Info($"{what} folder moved to {resolved}");
            }
            return resolved;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
            }
            catch { /* read-only profile; keep running with in-memory settings */ }
        }



        public string BuildFileName(string extension)
        {
            var now = DateTime.Now;
            var name = FileNameTemplate
                .Replace("{date}", now.ToString("yyyy-MM-dd"))
                .Replace("{time}", now.ToString("HH-mm-ss"))
                .Replace("{datetime}", now.ToString("yyyy-MM-dd-HH-mm-ss"))
                .Replace("{unix}", DateTimeOffset.Now.ToUnixTimeSeconds().ToString());

            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '-');

            if (string.IsNullOrWhiteSpace(name)) name = "KAM-" + now.ToString("yyyy-MM-dd-HH-mm-ss");
            return name + extension;
        }
    }
}
