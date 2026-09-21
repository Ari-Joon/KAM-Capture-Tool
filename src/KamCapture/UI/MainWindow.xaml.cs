using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KamCapture.Capture;
using KamCapture.Interop;
using KamCapture.Services;
using KamCapture.Settings;

namespace KamCapture.UI
{
    public partial class MainWindow : Window
    {
        private readonly AppSettings _cfg;
        private bool _ready;

        private sealed record Item(string Label, object Value)
        {
            public override string ToString() => Label;
        }

        public MainWindow(AppSettings cfg)
        {
            InitializeComponent();
            _cfg = cfg;

            WindowStyling.ApplyDarkChrome(this);
            try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/kam-capture.ico")); } catch { }

            CmbDelay.ItemsSource = new[]
            {
                new Item("No delay", 0), new Item("1 second", 1), new Item("3 seconds", 3),
                new Item("5 seconds", 5), new Item("10 seconds", 10)
            };

            LoadFromSettings();

            CaptureController.Notified += Say;
            RecordingController.Notified += Say;
            RecordingController.StateChanged += UpdateRecordButton;

            Closing += (_, _) =>
            {
                CaptureController.Notified -= Say;
                RecordingController.Notified -= Say;
                RecordingController.StateChanged -= UpdateRecordButton;
            };

            Loaded += (_, _) => LoadFromSettings();
            _ready = true;
        }

        private void LoadFromSettings()
        {
            SetMode(_cfg.DefaultMode);

            foreach (var o in CmbDelay.Items)
                if (o is Item it && Equals(it.Value, _cfg.DelaySeconds)) { CmbDelay.SelectedItem = o; break; }
            if (CmbDelay.SelectedItem == null) CmbDelay.SelectedIndex = 0;

            var mons = Screens.All();
            var (_, _, vw, vh) = Screens.VirtualBounds();

            // The scale this window is really rendered at, which is the number
            // that decides whether a selection keeps its pixels.
            double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            string scaling = Math.Abs(scale - 1.0) > 0.01 ? $" @ {scale * 100:0}% scaling" : "";

            LblSubtitle.Text = mons.Count == 1
                ? $"{mons[0].Describe()}{scaling} — captured at full device resolution"
                : $"{mons.Count} displays, {vw} × {vh} together{scaling} — captured at full device resolution";

            UpdateHotkeyHint();
            UpdateRecordButton();
        }

        private void UpdateHotkeyHint()
        {
            // One hint, not three: the full list is in Settings.
            LblHotkeys.Text = _cfg.HotkeysEnabled
                ? _cfg.HotkeyRegion + " anywhere in Windows"
                : "Global shortcuts are off";
        }

        private void UpdateRecordButton()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                BtnRecord.Content = RecordingController.IsRecording ? "Recording…" : "Record screen";
                BtnRecord.IsEnabled = !RecordingController.IsRecording;
            }));
        }

        private void Say(string message)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                LblStatus.Text = message;
                var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
                t.Tick += (_, _) => { t.Stop(); if (LblStatus.Text == message) LblStatus.Text = ""; };
                t.Start();
            }));
        }

        private void SetMode(SnipMode mode)
        {
            foreach (var tb in new[] { ModeRegion, ModeWindow, ModeMonitor })
                tb.IsChecked = (tb.Tag as string) == mode.ToString();
        }

        private SnipMode CurrentMode()
        {
            foreach (var tb in new[] { ModeRegion, ModeWindow, ModeMonitor })
                if (tb.IsChecked == true && Enum.TryParse<SnipMode>(tb.Tag as string, out var m)) return m;
            return SnipMode.Region;
        }

        private void OnModeClick(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton tb) return;
            if (Enum.TryParse<SnipMode>(tb.Tag as string, out var m))
            {
                SetMode(m);
                _cfg.DefaultMode = m;
                _cfg.Save();
            }
        }


        private async void OnCapture(object sender, RoutedEventArgs e)
        {
            if (CmbDelay.SelectedItem is Item { Value: int d })
            {
                _cfg.DelaySeconds = d;
                _cfg.Save();
            }
            await CaptureController.RunAsync(CurrentMode(), _cfg);
        }

        private async void OnRecord(object sender, RoutedEventArgs e)
        {
            await RecordingController.StartAsync(_cfg);
        }

        private void OnSettings(object sender, RoutedEventArgs e)
        {
            var w = new SettingsWindow(_cfg) { Owner = this };
            if (w.ShowDialog() == true)
            {
                LoadFromSettings();
                App.ReapplyHotkeys();
            }
        }

        private void OnOpenFolder(object sender, RoutedEventArgs e)
        {
            try
            {
                var folder = _cfg.EnsureSaveFolder();
                Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
            }
            catch (Exception ex) { Say("Could not open the folder: " + ex.Message); }
        }
    }
}
