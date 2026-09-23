using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using KamCapture.Capture;
using KamCapture.Recording;
using KamCapture.Settings;
using KamCapture.UI;

namespace KamCapture.Services
{
    public static class RecordingController
    {
        private static ScreenRecorder? _active;
        private static RecordingBar? _bar;

        public static bool IsRecording => _active != null;
        public static event Action<string>? Notified;
        public static event Action? StateChanged;

        public static async Task StartAsync(AppSettings cfg, RecordTarget? target = null)
        {
            if (_active != null) { StopActive(); return; }

            var ffmpeg = FfmpegLocator.Resolve(cfg.FfmpegPath);
            if (ffmpeg == null)
            {
                MessageBox.Show(
                    "Recording needs ffmpeg, and it was not found.\n\n" +
                    "Install it with:\n    winget install Gyan.FFmpeg\n\n" +
                    "Then point KAM Capture Tool at it in Settings if it still cannot be found.",
                    "KAM Capture Tool", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            bool systemAudio = cfg.RecordSystemAudio;
            string? micId = cfg.RecordMicrophone ? cfg.MicrophoneDeviceId : null;

            if (target == null)
            {
                var setup = new RecorderSetupWindow(cfg);
                var main = FindMain();
                if (main != null && main.IsVisible) setup.Owner = main;

                if (setup.ShowDialog() != true) return;

                systemAudio = setup.SystemAudio;
                micId = setup.MicDeviceId;

                if (setup.WantRegionPick)
                {
                    main?.Hide();
                    await Task.Delay(180);
                    var pick = CaptureOverlay.Run(SnipMode.Region, cfg);
                    main?.Show();
                    if (pick.Cancelled) return;
                    target = RecordTarget.Region(pick.Region);
                }
                else
                {
                    target = setup.ChosenTarget;
                }
            }

            if (target == null) return;

            // Give whatever is being recorded a moment to come to the front.
            var mainWindow = FindMain();
            mainWindow?.Hide();
            await Task.Delay(420);

            try
            {
                var recorder = new ScreenRecorder(cfg, target, ffmpeg);
                recorder.Failed += m => Application.Current.Dispatcher.BeginInvoke(
                    new Action(() => Notified?.Invoke("Recording: " + m)));

                recorder.Start(systemAudio, micId);
                _active = recorder;
                CaptureOverlay.HideFromCapture = true;

                _bar = new RecordingBar(recorder, cfg, systemAudio, micId);
                _bar.Stopped += OnBarStopped;
                _bar.Show();

                StateChanged?.Invoke();
            }
            catch (Exception ex)
            {
                _active = null;
                mainWindow?.Show();
                MessageBox.Show("Recording could not start.\n\n" + ex.Message, "KAM Capture Tool",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static void OnBarStopped(string? path)
        {
            // The next "Open folder" should land in Recordings, not Screenshots.
            AppSettings.Current.NoteOutput(recording: true);

            var recorder = _active;
            _active = null;
            _bar = null;
            CaptureOverlay.HideFromCapture = false;
            StateChanged?.Invoke();

            try { recorder?.Dispose(); } catch { }

            var main = FindMain();
            main?.Show();

            if (path != null && File.Exists(path))
            {
                var size = new FileInfo(path).Length;
                Notified?.Invoke($"Recording saved — {path}  ({size / 1024.0 / 1024.0:0.0} MB)");
            }
        }

        /// <summary>Stop the running recording and save it, as the Stop button does.</summary>
        public static void StopActive() => _bar?.RequestStop();

        /// <summary>Stop and finish writing the file before returning. For exit.</summary>
        public static void StopActiveNow() => _bar?.StopNow();

        public static void RevealLast(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
            catch { }
        }

        private static MainWindow? FindMain()
        {
            foreach (Window w in Application.Current.Windows)
                if (w is MainWindow m) return m;
            return null;
        }
    }
}
