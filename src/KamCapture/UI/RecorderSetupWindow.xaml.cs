using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Imaging;
using KamCapture.Capture;
using KamCapture.Interop;
using KamCapture.Recording;
using KamCapture.Settings;

namespace KamCapture.UI
{
    public partial class RecorderSetupWindow : Window
    {
        private readonly AppSettings _cfg;

        public RecordTarget? ChosenTarget { get; private set; }
        public bool WantRegionPick { get; private set; }
        public bool SystemAudio { get; private set; }
        public string? MicDeviceId { get; private set; }

        private sealed record WinItem(string Label, CapturableWindow Window)
        {
            public override string ToString() => Label;
        }

        private sealed record MicItem(string Label, string Id)
        {
            public override string ToString() => Label;
        }

        public RecorderSetupWindow(AppSettings cfg)
        {
            InitializeComponent();
            _cfg = cfg;

            WindowStyling.ApplyDarkChrome(this);
            try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/kam-capture.ico")); } catch { }

            var mon = Screens.FromCursor();
            LblMonitor.Text = $"{mon.Width} × {mon.Height}";
            var (_, _, vw, vh) = Screens.VirtualBounds();
            LblAll.Text = $"{vw} × {vh}";

            RefreshWindows();
            LoadMicrophones();

            ChkSystem.IsChecked = cfg.RecordSystemAudio;
            ChkMic.IsChecked = cfg.RecordMicrophone;

            SetTarget(TgtMonitor);
            UpdateQuality();
            Loaded += (_, _) => OnMicToggled(this, new RoutedEventArgs());
        }

        private void UpdateQuality()
        {
            string q = _cfg.RecordQuality switch
            {
                <= 17 => "near-lossless",
                <= 21 => "high",
                <= 25 => "balanced",
                _ => "small file"
            };
            LblQuality.Text = $"{_cfg.RecordFps} fps, {q}, " +
                              (_cfg.VideoEncoder == "auto" ? "software H.264" : _cfg.VideoEncoder) +
                              (_cfg.RecordCursor ? ", cursor shown" : ", cursor hidden");

            var ff = FfmpegLocator.Resolve(_cfg.FfmpegPath);
            LblFfmpeg.Text = ff == null
                ? "ffmpeg was not found. Recording needs it — install with:  winget install Gyan.FFmpeg"
                : "Encoder: " + ff;
            BtnStart.IsEnabled = ff != null;
        }

        private void RefreshWindows()
        {
            var own = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var items = WindowFinder.Enumerate(own)
                .Where(w => w.Handle != own)
                .Select(w => new WinItem(w.Display, w))
                .ToList();

            CmbWindow.ItemsSource = items;
            if (items.Count > 0) CmbWindow.SelectedIndex = 0;
        }

        private void LoadMicrophones()
        {
            var items = new List<MicItem> { new("Default microphone", "") };
            foreach (var d in AudioDevices.Inputs())
                items.Add(new MicItem(d.Name, d.Id));

            CmbMic.ItemsSource = items;
            CmbMic.SelectedItem = items.FirstOrDefault(i => i.Id == _cfg.MicrophoneDeviceId) ?? items[0];
        }

        private void OnRefreshWindows(object sender, RoutedEventArgs e) => RefreshWindows();

        private void OnMicToggled(object sender, RoutedEventArgs e)
        {
            CmbMic.IsEnabled = ChkMic.IsChecked == true;
        }

        private void OnTargetClick(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleButton tb) SetTarget(tb);
        }

        private void SetTarget(ToggleButton chosen)
        {
            foreach (var tb in new[] { TgtMonitor, TgtAll, TgtWindow, TgtRegion })
                tb.IsChecked = ReferenceEquals(tb, chosen);

            PanelWindow.Visibility = ReferenceEquals(chosen, TgtWindow)
                ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnOpenSettings(object sender, RoutedEventArgs e)
        {
            var w = new SettingsWindow(_cfg) { Owner = this };
            w.ShowDialog();
            UpdateQuality();
        }

        private void OnStart(object sender, RoutedEventArgs e)
        {
            SystemAudio = ChkSystem.IsChecked == true;
            MicDeviceId = ChkMic.IsChecked == true
                ? ((CmbMic.SelectedItem as MicItem)?.Id ?? "")
                : null;

            _cfg.RecordSystemAudio = SystemAudio;
            _cfg.RecordMicrophone = MicDeviceId != null;
            if (MicDeviceId != null) _cfg.MicrophoneDeviceId = MicDeviceId;
            _cfg.Save();

            if (TgtRegion.IsChecked == true)
            {
                WantRegionPick = true;
            }
            else if (TgtWindow.IsChecked == true)
            {
                if (CmbWindow.SelectedItem is not WinItem item)
                {
                    MessageBox.Show("Choose an application first.", "KAM Capture Tool",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                NativeMethods.SetForegroundWindow(item.Window.Handle);
                ChosenTarget = RecordTarget.WindowTarget(item.Window);
            }
            else if (TgtAll.IsChecked == true)
            {
                ChosenTarget = RecordTarget.FullScreen();
            }
            else
            {
                ChosenTarget = RecordTarget.Monitor(Screens.FromCursor());
            }

            DialogResult = true;
            Close();
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
