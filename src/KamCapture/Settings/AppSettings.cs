using System;
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
        public string BorderColor { get; set; } = "#4A7CFF";
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
        public string BoardBackground { get; set; } = "#F2F4F8";
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

            if (string.IsNullOrWhiteSpace(Current.SaveFolder))
                Current.SaveFolder = DefaultPicturesFolder("KAM Captures");
            if (string.IsNullOrWhiteSpace(Current.RecordFolder))
                Current.RecordFolder = DefaultVideosFolder("KAM Recordings");

            return Current;
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

        private static string DefaultPicturesFolder(string leaf)
        {
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (string.IsNullOrEmpty(baseDir)) baseDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return Path.Combine(baseDir, leaf);
        }

        private static string DefaultVideosFolder(string leaf)
        {
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            if (string.IsNullOrEmpty(baseDir)) baseDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return Path.Combine(baseDir, leaf);
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
