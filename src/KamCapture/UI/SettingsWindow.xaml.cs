using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using KamCapture.Controls;
using KamCapture.Editor;
using KamCapture.Interop;
using KamCapture.Recording;
using KamCapture.Settings;

namespace KamCapture.UI
{
    public partial class SettingsWindow : Window
    {
        private readonly AppSettings _cfg;
        private BorderPreview _preview = null!;
        private ColorButton _borderColor = null!, _dimColor = null!, _handleColor = null!,
                            _inkColor = null!, _boardColor = null!;
        private HotkeyBox _hkRegion = null!, _hkWindow = null!, _hkFull = null!, _hkRecord = null!;
        private bool _ready;

        public SettingsWindow(AppSettings cfg)
        {
            InitializeComponent();
            _cfg = cfg;

            WindowStyling.ApplyDarkChrome(this);
            try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/kam-capture.ico")); } catch { }

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

            _borderColor = Color(HostBorderColor, _cfg.BorderColor, c => { _cfg.BorderColor = ColorUtil.ToHex(c); _preview.Refresh(); });
            _dimColor = Color(HostDimColor, _cfg.DimColor, c => { _cfg.DimColor = ColorUtil.ToHex(c); _preview.Refresh(); });
            _handleColor = Color(HostHandleColor, _cfg.HandleColor, c => { _cfg.HandleColor = ColorUtil.ToHex(c); _preview.Refresh(); });
            _inkColor = Color(HostInkColor, _cfg.DefaultInkColor, _ => { });
            _boardColor = Color(HostBoardColor, _cfg.BoardBackground, _ => { });

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
                new Item("None", 0), new Item("1 second", 1), new Item("3 seconds", 3),
                new Item("5 seconds", 5), new Item("10 seconds", 10),
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

            CmbEncoder.ItemsSource = new[]
            {
                new Item("Automatic", "auto"),
                new Item("Software (libx264)", "libx264"),
                new Item("NVIDIA (h264_nvenc)", "h264_nvenc"),
                new Item("Intel Quick Sync (h264_qsv)", "h264_qsv"),
                new Item("AMD (h264_amf)", "h264_amf"),
            };

            _hkRegion = Hotkey(HostHkRegion);
            _hkWindow = Hotkey(HostHkWindow);
            _hkFull = Hotkey(HostHkFull);
            _hkRecord = Hotkey(HostHkRecord);
        }

        private static ColorButton Color(ContentControl host, string hex, Action<System.Windows.Media.Color> onChange)
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
            SldBorderThickness.Value = c.BorderThickness;
            SldDim.Value = c.DimOpacity;
            Select(CmbBorderStyle, c.BorderStyle);
            ChkCrosshair.IsChecked = c.ShowCrosshair;
            ChkMagnifier.IsChecked = c.ShowMagnifier;
            ChkDimensions.IsChecked = c.ShowDimensions;
            ChkThirds.IsChecked = c.ShowRuleOfThirds;

            Select(CmbDefaultMode, c.DefaultMode);
            Select(CmbDelay, c.DelaySeconds);
            ChkCursor.IsChecked = c.IncludeCursor;
            ChkClipboard.IsChecked = c.CopyToClipboardOnCapture;
            ChkAutoSave.IsChecked = c.AutoSave;
            ChkOpenEditor.IsChecked = c.OpenEditorAfterCapture;
            TxtSaveFolder.Text = c.SaveFolder;
            TxtFileName.Text = c.FileNameTemplate;

            SldInkThickness.Value = c.DefaultInkThickness;
            SldFontSize.Value = c.DefaultFontSize;
            ChkGrid.IsChecked = c.ShowBoardGrid;
            Select(CmbExportScale, c.ExportScale);

            Select(CmbFps, c.RecordFps);
            SldQuality.Value = c.RecordQuality;
            Select(CmbEncoder, c.VideoEncoder);
            ChkRecordCursor.IsChecked = c.RecordCursor;
            TxtRecordFolder.Text = c.RecordFolder;
            TxtFfmpeg.Text = c.FfmpegPath;

            ChkHotkeys.IsChecked = c.HotkeysEnabled;
            _hkRegion.Hotkey = c.HotkeyRegion;
            _hkWindow.Hotkey = c.HotkeyWindow;
            _hkFull.Hotkey = c.HotkeyFullScreen;
            _hkRecord.Hotkey = c.HotkeyRecord;

            ChkStartup.IsChecked = c.RunAtStartup;
            ChkTray.IsChecked = c.StartMinimisedToTray;

            UpdateLabels();
            CheckFfmpeg();
        }

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
            if (path == null)
            {
                LblFfmpegState.Text = "Not found. Video recording needs ffmpeg — install it with:  winget install Gyan.FFmpeg";
                return;
            }
            LblFfmpegState.Text = "Found: " + path;
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
            _cfg.ShowRuleOfThirds = ChkThirds.IsChecked == true;
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
            if (dlg.ShowDialog(this) == true) target.Text = dlg.FolderName;
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

            c.BorderColor = ColorUtil.ToHex(_borderColor.Color);
            c.DimColor = ColorUtil.ToHex(_dimColor.Color);
            c.HandleColor = ColorUtil.ToHex(_handleColor.Color);
            c.BorderThickness = SldBorderThickness.Value;
            c.DimOpacity = SldDim.Value;
            if (CmbBorderStyle.SelectedItem is Item { Value: BorderStyleKind bs }) c.BorderStyle = bs;
            c.ShowCrosshair = ChkCrosshair.IsChecked == true;
            c.ShowMagnifier = ChkMagnifier.IsChecked == true;
            c.ShowDimensions = ChkDimensions.IsChecked == true;
            c.ShowRuleOfThirds = ChkThirds.IsChecked == true;

            if (CmbDefaultMode.SelectedItem is Item { Value: SnipMode dm }) c.DefaultMode = dm;
            if (CmbDelay.SelectedItem is Item { Value: int delay }) c.DelaySeconds = delay;
            c.IncludeCursor = ChkCursor.IsChecked == true;
            c.CopyToClipboardOnCapture = ChkClipboard.IsChecked == true;
            c.AutoSave = ChkAutoSave.IsChecked == true;
            c.OpenEditorAfterCapture = ChkOpenEditor.IsChecked == true;
            c.SaveFolder = TxtSaveFolder.Text.Trim();
            c.FileNameTemplate = TxtFileName.Text.Trim();

            c.DefaultInkColor = ColorUtil.ToHex(_inkColor.Color);
            c.BoardBackground = ColorUtil.ToHex(_boardColor.Color);
            c.DefaultInkThickness = SldInkThickness.Value;
            c.DefaultFontSize = SldFontSize.Value;
            c.ShowBoardGrid = ChkGrid.IsChecked == true;
            if (CmbExportScale.SelectedItem is Item { Value: int es }) c.ExportScale = es;

            if (CmbFps.SelectedItem is Item { Value: int fps }) c.RecordFps = fps;
            c.RecordQuality = (int)Math.Round(SldQuality.Value);
            if (CmbEncoder.SelectedItem is Item { Value: string enc }) c.VideoEncoder = enc;
            c.RecordCursor = ChkRecordCursor.IsChecked == true;
            c.RecordFolder = TxtRecordFolder.Text.Trim();
            c.FfmpegPath = TxtFfmpeg.Text.Trim();

            c.HotkeysEnabled = ChkHotkeys.IsChecked == true;
            c.HotkeyRegion = _hkRegion.Hotkey;
            c.HotkeyWindow = _hkWindow.Hotkey;
            c.HotkeyFullScreen = _hkFull.Hotkey;
            c.HotkeyRecord = _hkRecord.Hotkey;

            bool startup = ChkStartup.IsChecked == true;
            if (startup != c.RunAtStartup)
            {
                c.RunAtStartup = startup;
                StartupRegistration.Set(startup);
            }
            c.StartMinimisedToTray = ChkTray.IsChecked == true;

            c.Save();
            DialogResult = true;
            Close();
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            // The preview edits the live settings object, so put it back.
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
            _dimColor.Color = ColorUtil.Parse(d.DimColor);
            _handleColor.Color = ColorUtil.Parse(d.HandleColor);
            _inkColor.Color = ColorUtil.Parse(d.DefaultInkColor);
            _boardColor.Color = ColorUtil.Parse(d.BoardBackground);

            _cfg.BorderColor = d.BorderColor;
            _cfg.DimColor = d.DimColor;
            _cfg.HandleColor = d.HandleColor;
            _cfg.BorderStyle = d.BorderStyle;

            d.SaveFolder = _cfg.SaveFolder;
            d.RecordFolder = _cfg.RecordFolder;
            d.FfmpegPath = _cfg.FfmpegPath;
            LoadFrom(d);

            _ready = true;
            _preview.Refresh();
        }
    }
}
