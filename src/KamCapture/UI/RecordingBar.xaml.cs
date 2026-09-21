using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using KamCapture.Capture;
using KamCapture.Interop;
using KamCapture.Recording;
using KamCapture.Settings;

namespace KamCapture.UI
{
    /// <summary>
    /// The live control bar. Audio sources can be switched while recording
    /// because the mixer's output stream never stops — see AudioEngine.
    ///
    /// The bar sets WDA_EXCLUDEFROMCAPTURE on itself, so it is invisible to
    /// every capture API on the machine, including this recorder.
    /// </summary>
    public partial class RecordingBar : Window
    {
        private readonly ScreenRecorder _recorder;
        private readonly AppSettings _cfg;
        private readonly DispatcherTimer _tick;
        private bool _ready;

        public event Action<string?>? Stopped;

        private sealed record MicItem(string Label, string? Id)
        {
            public override string ToString() => Label;
        }

        public RecordingBar(ScreenRecorder recorder, AppSettings cfg, bool systemAudio, string? micId)
        {
            InitializeComponent();
            _recorder = recorder;
            _cfg = cfg;

            WindowStyling.ExcludeFromCapture(this);

            LblTarget.Text = recorder.Target.Description;
            TglSystem.IsChecked = systemAudio;
            SldSystemGain.Value = cfg.SystemAudioGain;
            SldMicGain.Value = cfg.MicrophoneGain;

            LoadMicrophones(micId);
            TglMic.IsChecked = micId != null;

            _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _tick.Tick += OnTick;
            _tick.Start();

            StartPulse();
            Loaded += (_, _) => PlaceOnScreen();
            _ready = true;
        }

        private void LoadMicrophones(string? selectedId)
        {
            var items = new List<MicItem> { new("Default microphone", "") };
            foreach (var d in AudioDevices.Inputs())
                items.Add(new MicItem(d.Name, d.Id));

            CmbMic.ItemsSource = items;
            CmbMic.SelectedItem = items.FirstOrDefault(i => i.Id == selectedId) ?? items[0];
        }

        private void PlaceOnScreen()
        {
            var mon = Screens.Primary();
            double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            if (scale <= 0.05) scale = mon.Scale;
            double w = ActualWidth * scale;

            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int x = mon.WorkX + (int)((mon.WorkWidth - w) / 2);
            int y = mon.WorkY + mon.WorkHeight - (int)(ActualHeight * scale) - (int)(24 * scale);

            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, x, y, 0, 0,
                NativeMethods.SWP_NOACTIVATE | 0x0001 /* SWP_NOSIZE */);
        }

        private void StartPulse()
        {
            var anim = new DoubleAnimation(1.0, 0.25, TimeSpan.FromSeconds(0.85))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            DotRec.BeginAnimation(OpacityProperty, anim);
        }

        private void OnTick(object? sender, EventArgs e)
        {
            var t = _recorder.Elapsed;
            LblTime.Text = t.TotalHours >= 1
                ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
                : $"{t.Minutes:00}:{t.Seconds:00}";

            double peak = _recorder.AudioPeak;
            Meter.Height = Math.Max(0, Math.Min(26, peak * 26));
            Meter.Background = new SolidColorBrush(peak > 0.94
                ? Color.FromRgb(0xE5, 0x34, 0x2A)
                : peak > 0.7 ? Color.FromRgb(0xFF, 0xD4, 0x00) : Color.FromRgb(0x2B, 0xB6, 0x73));

            LblStats.Text = $"{_recorder.FramesWritten} frames" +
                (_recorder.FramesDropped > 0 ? $"  ·  {_recorder.FramesDropped} dropped" : "");

            DotRec.Fill = new SolidColorBrush(_recorder.IsPaused
                ? Color.FromRgb(0xFF, 0xD4, 0x00)
                : Color.FromRgb(0xE5, 0x34, 0x2A));
        }

        // ---------------- live audio switching ----------------

        private void OnSystemToggled(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            if (TglSystem.IsChecked == true)
                _recorder.Audio.AddSystemAudio("system", null, SldSystemGain.Value);
            else
                _recorder.Audio.Remove("system");

            _cfg.RecordSystemAudio = TglSystem.IsChecked == true;
            _cfg.Save();
        }

        private void OnMicToggled(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            if (TglMic.IsChecked == true) ApplyMicrophone();
            else _recorder.Audio.Remove("mic");

            _cfg.RecordMicrophone = TglMic.IsChecked == true;
            _cfg.Save();
        }

        private void OnMicDeviceChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready) return;
            if (TglMic.IsChecked == true) ApplyMicrophone();
        }

        private void ApplyMicrophone()
        {
            var id = (CmbMic.SelectedItem as MicItem)?.Id ?? "";
            // Remove-then-add on a live mixer: the output stream keeps running,
            // so the track stays continuous and in sync.
            _recorder.Audio.AddMicrophone("mic", id, SldMicGain.Value);
            _cfg.MicrophoneDeviceId = id;
            _cfg.Save();
        }

        private void OnSystemGain(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_ready) return;
            _cfg.SystemAudioGain = e.NewValue;
            _recorder.Audio.SetGain("system", e.NewValue);
        }

        private void OnMicGain(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_ready) return;
            _cfg.MicrophoneGain = e.NewValue;
            _recorder.Audio.SetGain("mic", e.NewValue);
        }

        // ---------------- transport ----------------

        private void OnPause(object sender, RoutedEventArgs e)
        {
            if (_recorder.IsPaused) { _recorder.Resume(); BtnPause.Content = "Pause"; }
            else { _recorder.Pause(); BtnPause.Content = "Resume"; }
        }

        private void OnStop(object sender, RoutedEventArgs e)
        {
            _ready = false;
            _tick.Stop();
            BtnStop.IsEnabled = false;
            BtnStop.Content = "Saving…";
            BtnPause.IsEnabled = false;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                string? path = null;
                try { path = _recorder.Stop(); }
                catch { }
                Stopped?.Invoke(path);
                Close();
            }), DispatcherPriority.Background);
        }

        private void OnDragBar(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                try { DragMove(); } catch { }
            }
        }
    }
}
