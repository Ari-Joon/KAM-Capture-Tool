using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using KamCapture.Controls;
using KamCapture.Editor;
using KamCapture.Interop;
using KamCapture.Recording;
using KamCapture.Services;
using KamCapture.Settings;

namespace KamCapture.UI
{
    public partial class SettingsWindow : Window
    {
        private readonly AppSettings _cfg;
        private BorderPreview _preview = null!;
        private ColorButton _borderColor = null!, _inkColor = null!;
        private HotkeyBox _hkRegion = null!, _hkWindow = null!, _hkFull = null!, _hkRecord = null!;
        private bool _ready;
        private readonly HashSet<string> _confirmed = new(StringComparer.OrdinalIgnoreCase);

        public SettingsWindow(AppSettings cfg)
        {
            InitializeComponent();
            _cfg = cfg;

            WindowStyling.ApplyDarkChrome(this);
            try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/kam-capture.ico")); } catch { }

            foreach (var f in cfg.ConfirmedSyncedFolders)
                if (AppSettings.Normalise(f) is { } key) _confirmed.Add(key);

            BuildControls();
            LoadFrom(cfg);
            _ready = true;
        }

        private sealed record Item(string Label, object Value)
        {
            public override string ToString() => Label;
        }

        private void BuildControls()
        {
            _preview = new BorderPreview(_cfg);
            HostPreview.Content = _preview;

            _borderColor = Colour(HostBorderColor, _cfg.BorderColor,
                c => { _cfg.BorderColor = ColorUtil.ToHex(c); _preview.Refresh(); });
            _inkColor = Colour(HostInkColor, _cfg.DefaultInkColor, _ => { });

            CmbBorderStyle.ItemsSource = new[]
            {
                new Item("Solid", BorderStyleKind.Solid),
                new Item("Dashed", BorderStyleKind.Dashed),
                new Item("Dotted", BorderStyleKind.Dotted),
                new Item("Glow", BorderStyleKind.Glow),
            };

            CmbDefaultMode.ItemsSource = new[]
            {
                new Item("Capture", SnipMode.Region),
                new Item("Window", SnipMode.Window),
                new Item("Monitor", SnipMode.Monitor),
            };

            CmbDelay.ItemsSource = new[]
            {
                new Item("No delay", 0), new Item("1 second", 1), new Item("3 seconds", 3),
                new Item("5 seconds", 5), new Item("10 seconds", 10),
            };

            CmbSandbox.ItemsSource = new[]
            {
                new Item("Tight", 40.0),
                new Item("Comfortable", 260.0),
                new Item("Wide", 520.0),
                new Item("Extra wide", 900.0),
            };

            CmbExportScale.ItemsSource = new[]
            {
                new Item("1x", 1), new Item("2x", 2), new Item("3x", 3), new Item("4x", 4),
            };

            CmbFps.ItemsSource = new[]
            {
                new Item("15 fps", 15), new Item("24 fps", 24), new Item("30 fps", 30),
                new Item("48 fps", 48), new Item("60 fps", 60),
            };

            _hkRegion = Hotkey(HostHkRegion);
            _hkWindow = Hotkey(HostHkWindow);
            _hkFull = Hotkey(HostHkFull);
            _hkRecord = Hotkey(HostHkRecord);
        }

        private static ColorButton Colour(ContentControl host, string hex, Action<System.Windows.Media.Color> onChange)
        {
            var b = new ColorButton { Color = ColorUtil.Parse(hex) };
            b.ColorChanged += c => onChange(c);
            host.Content = b;
            return b;
        }

        private static HotkeyBox Hotkey(ContentControl host)
        {
            var b = new HotkeyBox();
            host.Content = b;
            return b;
        }

        private static void Select(ComboBox box, object value)
        {
            foreach (var o in box.Items)
                if (o is Item it && Equals(it.Value, value)) { box.SelectedItem = o; return; }
            if (box.Items.Count > 0) box.SelectedIndex = 0;
        }

        private void LoadFrom(AppSettings c)
        {
            Select(CmbDefaultMode, c.DefaultMode);
            Select(CmbDelay, c.DelaySeconds);
            ChkClipboard.IsChecked = c.CopyToClipboardOnCapture;
            ChkOpenEditor.IsChecked = c.OpenEditorAfterCapture;
            ChkAutoSave.IsChecked = c.AutoSave;
            ChkCursor.IsChecked = c.IncludeCursor;
            TxtSaveFolder.Text = c.SaveFolder;

            SldBorderThickness.Value = c.BorderThickness;
            SldDim.Value = c.DimOpacity;
            Select(CmbBorderStyle, c.BorderStyle);
            ChkCrosshair.IsChecked = c.ShowCrosshair;
            ChkMagnifier.IsChecked = c.ShowMagnifier;
            ChkDimensions.IsChecked = c.ShowDimensions;

            SldInkThickness.Value = c.DefaultInkThickness;
            SldFontSize.Value = c.DefaultFontSize;
            Select(CmbSandbox, NearestSandbox(c.BoardMargin));
            Select(CmbExportScale, c.ExportScale);

            Select(CmbFps, c.RecordFps);
            SldQuality.Value = c.RecordQuality;
            ChkRecordCursor.IsChecked = c.RecordCursor;
            TxtRecordFolder.Text = c.RecordFolder;
            TxtFfmpeg.Text = c.FfmpegPath;

            ChkHotkeys.IsChecked = c.HotkeysEnabled;
            _hkRegion.Hotkey = c.HotkeyRegion;
            _hkWindow.Hotkey = c.HotkeyWindow;
            _hkFull.Hotkey = c.HotkeyFullScreen;
            _hkRecord.Hotkey = c.HotkeyRecord;

            // The Run key itself, not the setting: the installer changes it
            // without going through here.
            ChkStartup.IsChecked = StartupRegistration.IsSet();
            ChkTray.IsChecked = c.StartMinimisedToTray;
            ChkUpdates.IsChecked = c.CheckForUpdates;
            LblVersion.Text = "You have " + Setup.Updater.Current.ToString(3);

            UpdateLabels();
            CheckFfmpeg();
        }

        private static double NearestSandbox(double margin) => margin switch
        {
            <= 120 => 40.0,
            <= 380 => 260.0,
            <= 700 => 520.0,
            _ => 900.0
        };

        private void UpdateLabels()
        {
            LblBorderThickness.Text = SldBorderThickness.Value.ToString("0.#") + " px";
            LblDim.Text = (SldDim.Value * 100).ToString("0") + "%";
            LblFontSize.Text = ((int)SldFontSize.Value).ToString();
            LblQuality.Text = SldQuality.Value switch
            {
                <= 17 => "Near-lossless",
                <= 21 => "High",
                <= 25 => "Balanced",
                _ => "Small file"
            };
        }

        private void CheckFfmpeg()
        {
            var path = FfmpegLocator.Resolve(TxtFfmpeg.Text);
            LblFfmpegState.Text = path == null
                ? "Not found. Recording needs ffmpeg:  winget install Gyan.FFmpeg"
                : "Found: " + path;
        }

        // ---------------- live preview ----------------

        private void OnPreviewValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_ready) return;
            _cfg.BorderThickness = SldBorderThickness.Value;
            _cfg.DimOpacity = SldDim.Value;
            UpdateLabels();
            _preview.Refresh();
        }

        private void OnPreviewToggle(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            _cfg.ShowCrosshair = ChkCrosshair.IsChecked == true;
            _cfg.ShowMagnifier = ChkMagnifier.IsChecked == true;
            _cfg.ShowDimensions = ChkDimensions.IsChecked == true;
            _preview.Refresh();
        }

        private void OnBorderStyleChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready) return;
            if (CmbBorderStyle.SelectedItem is Item { Value: BorderStyleKind k }) _cfg.BorderStyle = k;
            _preview.Refresh();
        }

        private void OnQualityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_ready) return;
            UpdateLabels();
        }

        // ---------------- browsing ----------------

        private void OnBrowseSaveFolder(object sender, RoutedEventArgs e) => Browse(TxtSaveFolder);
        private void OnBrowseRecordFolder(object sender, RoutedEventArgs e) => Browse(TxtRecordFolder);

        private void Browse(TextBox target)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder" };
            if (Directory.Exists(target.Text)) dlg.InitialDirectory = target.Text;
            if (dlg.ShowDialog(this) != true) return;

            if (!ConfirmIfSynced(dlg.FolderName)) return;
            target.Text = dlg.FolderName;
        }

        /// <summary>
        /// Ask once before using a folder inside OneDrive, and remember the
        /// answer, so a confirmed choice is honoured from then on rather than
        /// quietly moved back to local disk at the next save or launch.
        /// </summary>
        private bool ConfirmIfSynced(string folder)
        {
            if (!OutputFolder.IsSynced(folder)) return true;
            var key = AppSettings.Normalise(folder);
            if (key != null && _confirmed.Contains(key)) return true;

            var answer = MessageBox.Show(
                "That folder is inside OneDrive, so every capture saved there would be uploaded.\n\nUse it anyway?",
                "KAM Capture Tool", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return false;

            if (key != null) _confirmed.Add(key);
            return true;
        }

        /// <summary>A folder typed rather than browsed gets the same question.</summary>
        private string ChooseFolder(TextBox box, string localDefault)
        {
            var chosen = box.Text.Trim();
            if (ConfirmIfSynced(chosen)) return chosen;
            box.Text = localDefault;
            return localDefault;
        }

        private void OnFindFfmpeg(object sender, RoutedEventArgs e)
        {
            var auto = FfmpegLocator.Discover();
            if (auto != null)
            {
                TxtFfmpeg.Text = auto;
                CheckFfmpeg();
                return;
            }

            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Locate ffmpeg.exe",
                Filter = "ffmpeg (ffmpeg.exe)|ffmpeg.exe|All programs (*.exe)|*.exe"
            };
            if (dlg.ShowDialog(this) == true)
            {
                TxtFfmpeg.Text = dlg.FileName;
                CheckFfmpeg();
            }
        }

        // ---------------- commit ----------------

        private void OnSaveSettings(object sender, RoutedEventArgs e)
        {
            var c = _cfg;

            if (CmbDefaultMode.SelectedItem is Item { Value: SnipMode dm }) c.DefaultMode = dm;
            if (CmbDelay.SelectedItem is Item { Value: int delay }) c.DelaySeconds = delay;
            c.CopyToClipboardOnCapture = ChkClipboard.IsChecked == true;
            c.OpenEditorAfterCapture = ChkOpenEditor.IsChecked == true;
            c.AutoSave = ChkAutoSave.IsChecked == true;
            c.IncludeCursor = ChkCursor.IsChecked == true;
            c.SaveFolder = ChooseFolder(TxtSaveFolder, OutputFolder.DefaultCaptures());

            c.BorderColor = ColorUtil.ToHex(_borderColor.Color);
            c.BorderThickness = SldBorderThickness.Value;
            c.DimOpacity = SldDim.Value;
            if (CmbBorderStyle.SelectedItem is Item { Value: BorderStyleKind bs }) c.BorderStyle = bs;
            c.ShowCrosshair = ChkCrosshair.IsChecked == true;
            c.ShowMagnifier = ChkMagnifier.IsChecked == true;
            c.ShowDimensions = ChkDimensions.IsChecked == true;

            c.DefaultInkColor = ColorUtil.ToHex(_inkColor.Color);
            c.DefaultInkThickness = SldInkThickness.Value;
            c.DefaultFontSize = SldFontSize.Value;
            if (CmbSandbox.SelectedItem is Item { Value: double margin }) c.BoardMargin = margin;
            if (CmbExportScale.SelectedItem is Item { Value: int es }) c.ExportScale = es;

            if (CmbFps.SelectedItem is Item { Value: int fps }) c.RecordFps = fps;
            c.RecordQuality = (int)Math.Round(SldQuality.Value);
            c.RecordCursor = ChkRecordCursor.IsChecked == true;
            c.RecordFolder = ChooseFolder(TxtRecordFolder, OutputFolder.DefaultRecordings());

            // Keep confirmations only for folders still in use, so an old yes
            // cannot silently apply to a folder picked again months later.
            c.ConfirmedSyncedFolders = new List<string>();
            foreach (var f in new[] { c.SaveFolder, c.RecordFolder })
                if (OutputFolder.IsSynced(f) && AppSettings.Normalise(f) is { } key && _confirmed.Contains(key))
                    c.ConfirmedSyncedFolders.Add(key);
            c.FfmpegPath = TxtFfmpeg.Text.Trim();

            c.HotkeysEnabled = ChkHotkeys.IsChecked == true;
            c.HotkeyRegion = _hkRegion.Hotkey;
            c.HotkeyWindow = _hkWindow.Hotkey;
            c.HotkeyFullScreen = _hkFull.Hotkey;
            c.HotkeyRecord = _hkRecord.Hotkey;

            bool startup = ChkStartup.IsChecked == true;
            if (startup != StartupRegistration.IsSet()) StartupRegistration.Set(startup);
            c.RunAtStartup = startup;
            c.StartMinimisedToTray = ChkTray.IsChecked == true;
            c.CheckForUpdates = ChkUpdates.IsChecked == true;

            c.Save();
            DialogResult = true;
            Close();
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            // The live preview edits the settings object directly, so put it back.
            AppSettings.Load();
            DialogResult = false;
            Close();
        }

        private void OnRestoreDefaults(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("Put every setting back to its default?", "KAM Capture Tool",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

            var d = new AppSettings();
            _ready = false;

            _borderColor.Color = ColorUtil.Parse(d.BorderColor);
            _inkColor.Color = ColorUtil.Parse(d.DefaultInkColor);

            _cfg.BorderColor = d.BorderColor;
            _cfg.BorderStyle = d.BorderStyle;

            // Keep the folders they chose; defaults should not relocate their files.
            d.SaveFolder = _cfg.SaveFolder;
            d.RecordFolder = _cfg.RecordFolder;
            d.FfmpegPath = _cfg.FfmpegPath;
            LoadFrom(d);

            _ready = true;
            _preview.Refresh();
        }
    }
}
