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
    /// <summary>What finishing a take means.</summary>
    public enum TakeOutcome { Save, SaveAs, Stop, Retake }

    public partial class RecordingBar : Window
    {
        private readonly ScreenRecorder _recorder;
        private readonly AppSettings _cfg;
        private readonly DispatcherTimer _tick;
        private bool _ready;

        /// <summary>The finished file, and what to do with it.</summary>
        public event Action<string?, TakeOutcome>? Stopped;

        /// <summary>Where the bar was on screen when it closed, in pixels, so a retake's bar opens there.</summary>
        public (int X, int Y)? ClosedAt { get; private set; }

        private readonly (int X, int Y)? _placeAt;

        private sealed record MicItem(string Label, string? Id)
        {
            public override string ToString() => Label;
        }

        private static readonly Brush MeterGreen = Frozen(0x2B, 0xB6, 0x73);
        private static readonly Brush MeterAmber = Frozen(0xFF, 0xD4, 0x00);
        private static readonly Brush MeterRed = Frozen(0xE5, 0x34, 0x2A);

        private static Brush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        private int _sizeTicks;

        public RecordingBar(ScreenRecorder recorder, AppSettings cfg, int take = 1, (int X, int Y)? placeAt = null)
        {
            _placeAt = placeAt;
            bool systemAudio = cfg.RecordSystemAudio;
            string? micId = cfg.RecordMicrophone ? cfg.MicrophoneDeviceId : null;

            InitializeComponent();
            _recorder = recorder;
            _cfg = cfg;

            WindowStyling.ExcludeFromCapture(this);

            LblTarget.Text = recorder.Target.Description + (take > 1 ? $"  ·  take {take}" : "");
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

            // A retake opens where the last bar was left, not back in the middle.
            if (_placeAt is { } at) (x, y) = (at.X, at.Y);

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

            double peak = Math.Clamp(_recorder.AudioPeak, 0, 1);
            MeterScale.ScaleY = peak;
            Meter.Background = peak > 0.94 ? MeterRed : peak > 0.7 ? MeterAmber : MeterGreen;

            if (_recorder.IsAudioOnly)
            {
                // The file on disk, once a second: the number that matters when
                // the point of recording sound alone was to keep it small.
                if (_sizeTicks++ % 10 == 0)
                {
                    long bytes = 0;
                    try { bytes = new System.IO.FileInfo(_recorder.OutputPath).Length; } catch { }
                    LblStats.Text = $"{_recorder.AudioFormat.ToUpperInvariant()}  ·  {bytes / 1024.0 / 1024.0:0.0} MB";
                }
            }
            else
            {
                LblStats.Text = $"{_recorder.FramesWritten} frames" +
                    (_recorder.FramesDuplicated > 0 ? $"  ·  {_recorder.FramesDuplicated} padded" : "");
            }

            DotRec.Fill = new SolidColorBrush(_recorder.IsPaused
                ? Color.FromRgb(0xFF, 0xD4, 0x00)
                : Color.FromRgb(0xE5, 0x34, 0x2A));
        }

        // ---------------- live audio switching ----------------

        private void OnSystemToggled(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            if (TglSystem.IsChecked == true)
                _recorder.Audio.AddSystemAudio("system", _cfg.SystemAudioDeviceId, SldSystemGain.Value);
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

        private bool _stopping, _finished;
        private TakeOutcome _outcome = TakeOutcome.Save;

        private void OnSave(object sender, RoutedEventArgs e) => Finish(TakeOutcome.Save);
        private void OnSaveAs(object sender, RoutedEventArgs e) => Finish(TakeOutcome.SaveAs);
        private void OnRetake(object sender, RoutedEventArgs e) => Finish(TakeOutcome.Retake);
        private void OnStop(object sender, RoutedEventArgs e) => Finish(TakeOutcome.Stop);

        /// <summary>End the take one of the four ways. Only the first call counts.</summary>
        public void Finish(TakeOutcome outcome)
        {
            if (_stopping) return;
            _outcome = outcome;
            RequestStop();
        }

        /// <summary>
        /// Stop and save, the same as pressing Stop. Safe to call more than once,
        /// and from the global shortcut as well as the button.
        /// </summary>
        public void RequestStop()
        {
            if (_stopping) return;
            _stopping = true;
            _ready = false;
            _tick.Stop();
            foreach (var b in new[] { BtnPause, BtnRetake, BtnSave, BtnSaveAs, BtnStop })
                b.IsEnabled = false;
            switch (_outcome)
            {
                case TakeOutcome.Save: BtnSave.Content = "Saving…"; break;
                case TakeOutcome.SaveAs: BtnSaveAs.Content = "Saving…"; break;
                case TakeOutcome.Retake: BtnRetake.Content = "Starting again…"; break;
                case TakeOutcome.Stop: BtnStop.Content = "Stopping…"; break;
            }

            // Let the button repaint as "Saving…" before ffmpeg is waited on.
            Dispatcher.BeginInvoke(new Action(FinishStop), DispatcherPriority.Background);
        }

        /// <summary>
        /// Stop and save right now, blocking until the file is finished. Used on
        /// exit: shutting down with ffmpeg mid-write leaves an MP4 with no index,
        /// which most players refuse to open.
        /// </summary>
        public void StopNow()
        {
            _stopping = true;
            _tick.Stop();
            FinishStop();
        }

        private void FinishStop()
        {
            if (_finished) return;
            _finished = true;

            string? path = null;
            try { path = _recorder.Stop(); }
            catch { }

            // Gone before anything is asked: the bar stays on top of everything,
            // and would sit over the Save as dialog.
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero && NativeMethods.GetWindowRect(hwnd, out var r)) ClosedAt = (r.Left, r.Top);

            Close();
            Stopped?.Invoke(path, _outcome);
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
