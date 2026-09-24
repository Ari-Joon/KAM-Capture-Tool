using System;
using System.Diagnostics;
using Activity = KamCapture.Settings.Activity;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using KamCapture.Capture;
using KamCapture.Interop;
using KamCapture.Recording;
using KamCapture.Settings;
using KamCapture.UI;

namespace KamCapture.Services
{
    /// <summary>
    /// Starts and stops recordings. The choices — what to record, which sound —
    /// are made on the home window and remembered, so starting one from the
    /// window, the tray or the shortcut all do the same thing with no dialog
    /// in between.
    /// </summary>
    public static class RecordingController
    {
        private static ScreenRecorder? _active;
        private static RecordingBar? _bar;

        // Which take this is, counting retakes of the same recording.
        private static int _take = 1;

        public static bool IsRecording => _active != null;
        public static bool IsRecordingAudioOnly => _active?.IsAudioOnly == true;

        public static event Action<string>? Notified;
        public static event Action? StateChanged;

        /// <summary>
        /// Record the screen as the home window is set up: a region you drag, a
        /// window you click, or the whole display under the pointer. A second
        /// call while recording stops it, which is what the shortcut expects.
        /// </summary>
        public static async Task StartVideoAsync(AppSettings cfg)
        {
            if (_active != null) { StopActive(); return; }
            var ffmpeg = RequireFfmpeg(cfg);
            if (ffmpeg == null) return;

            RecordTarget target;
            if (cfg.VideoMode is SnipMode.Region or SnipMode.Window)
            {
                var main = FindMain();
                bool wasVisible = main?.IsVisible == true;
                main?.Hide();
                await Task.Delay(180);

                var pick = CaptureOverlay.Run(cfg.VideoMode, cfg, forRecording: true);
                if (pick.Cancelled)
                {
                    if (wasVisible) main?.Show();
                    return;
                }

                target = cfg.VideoMode == SnipMode.Window && pick.SourceWindow != IntPtr.Zero
                    ? WindowTarget(pick.SourceWindow, pick.Region)
                    : RecordTarget.Region(pick.Region);
            }
            else
            {
                target = RecordTarget.Monitor(Screens.FromCursor());
            }

            await BeginAsync(cfg, target, ffmpeg, hideMain: true);
        }

        /// <summary>
        /// Record sound only. Nothing on screen is captured, so the home window
        /// stays where it is and its button becomes Stop.
        /// </summary>
        public static async Task StartAudioAsync(AppSettings cfg)
        {
            if (_active != null) { StopActive(); return; }
            var ffmpeg = RequireFfmpeg(cfg);
            if (ffmpeg == null) return;

            if (!cfg.RecordSystemAudio && !cfg.RecordMicrophone)
            {
                FindMain()?.ShowActivity(Activity.Audio);
                Notified?.Invoke("Choose system audio, the microphone, or both, then start recording.");
                return;
            }

            await BeginAsync(cfg, RecordTarget.AudioOnly(), ffmpeg, hideMain: false);
        }

        /// <summary>Record a target chosen elsewhere: the Record button on a screenshot selection.</summary>
        public static async Task StartAsync(AppSettings cfg, RecordTarget target)
        {
            if (_active != null) { StopActive(); return; }
            var ffmpeg = RequireFfmpeg(cfg);
            if (ffmpeg == null) return;
            await BeginAsync(cfg, target, ffmpeg, hideMain: true);
        }

        private static async Task BeginAsync(AppSettings cfg, RecordTarget target, string ffmpeg, bool hideMain,
                                             int take = 1, (int X, int Y)? barAt = null)
        {
            _take = take;
            var main = FindMain();
            if (hideMain)
            {
                // Out of the picture, and a moment for whatever is being
                // recorded to come to the front.
                main?.Hide();
                await Task.Delay(420);
            }

            try
            {
                var recorder = new ScreenRecorder(cfg, target, ffmpeg);
                recorder.Failed += m => Application.Current.Dispatcher.BeginInvoke(
                    new Action(() => Notified?.Invoke("Recording: " + m)));

                recorder.Start(cfg.RecordSystemAudio,
                               cfg.RecordMicrophone ? cfg.MicrophoneDeviceId : null,
                               cfg.SystemAudioDeviceId);
                _active = recorder;
                CaptureOverlay.HideFromCapture = true;

                if (recorder.FormatNote != null) Notified?.Invoke(recorder.FormatNote);

                _bar = new RecordingBar(recorder, cfg, take, barAt);
                _bar.Stopped += OnBarStopped;
                _bar.Show();

                StateChanged?.Invoke();
            }
            catch (Exception ex)
            {
                _active = null;
                main?.Show();
                StateChanged?.Invoke();
                Log.Error("Recording could not start", ex);
                MessageBox.Show("Recording could not start.\n\n" + ex.Message, "KAM Capture Tool",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static string? RequireFfmpeg(AppSettings cfg)
        {
            var ffmpeg = FfmpegLocator.Resolve(cfg.FfmpegPath);
            if (ffmpeg != null) return ffmpeg;

            MessageBox.Show(
                "Recording needs ffmpeg, and it was not found.\n\n" +
                "Install it with:\n    winget install Gyan.FFmpeg\n\n" +
                "Then point KAM Capture Tool at it in Settings if it still cannot be found.",
                "KAM Capture Tool", MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }

        /// <summary>The window that was clicked, followed as it moves; else the rectangle.</summary>
        private static RecordTarget WindowTarget(IntPtr handle, Int32Rect fallback)
        {
            var window = WindowFinder.Enumerate().FirstOrDefault(w => w.Handle == handle);
            if (window == null) return RecordTarget.Region(fallback);

            // It was behind the overlay; bring it forward before the first frame.
            NativeMethods.SetForegroundWindow(handle);
            return RecordTarget.WindowTarget(window);
        }

        private static void OnBarStopped(string? path, TakeOutcome outcome)
        {
            var barAt = _bar?.ClosedAt;
            var recorder = _active;
            _active = null;
            _bar = null;
            CaptureOverlay.HideFromCapture = false;

            // A retake goes straight into the next take, so the home window
            // and the tray are not told it stopped in between.
            if (outcome != TakeOutcome.Retake) StateChanged?.Invoke();

            bool audio = recorder?.IsAudioOnly == true;
            var target = recorder?.Target;
            try { recorder?.Dispose(); } catch { }

            if (outcome is TakeOutcome.Discard or TakeOutcome.Retake)
            {
                bool binned = RecycleBin.Send(path);
                var cfg = AppSettings.Current;

                // Straight into the next take: same target, same sources, the
                // bar where it was left. The home window stays as it is.
                if (outcome == TakeOutcome.Retake && target != null && RequireFfmpeg(cfg) is { } ffmpeg)
                {
                    _ = BeginAsync(cfg, target, ffmpeg, hideMain: false, take: _take + 1, barAt: barAt);
                    return;
                }

                if (outcome == TakeOutcome.Retake) StateChanged?.Invoke();   // the retake could not start
                FindMain()?.Show();
                Notified?.Invoke(binned
                    ? "Take discarded. It is in the Recycle Bin if you want it back."
                    : "Take discarded.");
                return;
            }

            var main = FindMain();
            main?.Show();

            if (path != null && File.Exists(path))
            {
                var cfg = AppSettings.Current;
                if (outcome == TakeOutcome.SaveAs) path = SaveAs.AskForRecording(cfg, path, audio, main);

                var size = new FileInfo(path).Length;
                // The name, not the path: the folder is one button away. Named
                // somewhere else, the folder's name comes too.
                Notified?.Invoke($"{(audio ? "Audio" : "Video")} saved as " +
                                 $"{SaveAs.Describe(path, audio ? cfg.AudioFolder : cfg.RecordFolder)}, " +
                                 $"{size / 1024.0 / 1024.0:0.0} MB");
            }
        }

        /// <summary>Stop the running recording and save it, as the Stop button does.</summary>
        public static void StopActive() => _bar?.RequestStop();

        /// <summary>End the running take one of the bar's four ways, as its buttons do.</summary>
        internal static void FinishActive(TakeOutcome outcome) => _bar?.Finish(outcome);

        /// <summary>Which take is running; 1 until a retake.</summary>
        internal static int Take => _take;

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
