using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using KamCapture.Capture;
using KamCapture.Interop;
using KamCapture.Recording;
using KamCapture.Settings;
using KamCapture.UI;

namespace KamCapture.Services
{
    /// <summary>Runs one capture from start to finish.</summary>
    public static class CaptureController
    {
        private static bool _busy;

        public static event Action<string>? Notified;

        public static async Task RunAsync(SnipMode mode, AppSettings cfg, int? delayOverride = null)
        {
            if (_busy) return;
            _busy = true;
            Log.Info($"Capture requested: {mode}");

            var hidden = new List<Window>();
            try
            {
                // Our own windows must be off the glass before the desktop is
                // frozen, otherwise they end up in the picture.
                var ours = Application.Current.Windows.OfType<Window>()
                    .Where(w => w is MainWindow or EditorWindow or SettingsWindow)
                    .ToList();
                hidden = ClearTheGlass(ours);

                int delay = delayOverride ?? cfg.DelaySeconds;
                if (delay > 0) await Task.Delay(delay * 1000);

                DesktopSnapshot snapshot;
                try
                {
                    snapshot = ScreenGrabber.CaptureVirtualDesktop(cfg.IncludeCursor);
                }
                finally
                {
                    // Exclusion only matters for the grab itself. Left on, it
                    // would stop anyone screenshotting this tool with another.
                    ReleaseTheGlass(hidden);
                }
                Log.Info($"Desktop frozen: {snapshot.Width} x {snapshot.Height} at ({snapshot.OriginX},{snapshot.OriginY})");

                var result = CaptureOverlay.Run(mode, cfg, snapshot);
                Log.Info($"Overlay finished: {result.Action} {result.Region.Width}x{result.Region.Height}");

                if (result.Cancelled || result.Image == null)
                {
                    RestoreAll(hidden);
                    return;
                }

                await HandleResultAsync(result, cfg, hidden);
            }
            catch (Exception ex)
            {
                RestoreAll(hidden);
                MessageBox.Show("The capture failed.\n\n" + ex.Message, "KAM Capture Tool",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                _busy = false;
            }
        }

        internal static async Task HandleResultAsync(CaptureResult result, AppSettings cfg, List<Window> hidden)
        {
            var image = result.Image!;

            if (result.Action == CaptureAction.Record)
            {
                RestoreAll(hidden);
                await RecordingController.StartAsync(cfg, RecordTarget.Region(result.Region));
                return;
            }

            bool copied = false, saved = false;
            string? savedPath = null;

            if (result.Action == CaptureAction.Copy || cfg.CopyToClipboardOnCapture)
            {
                EditorWindow.CopyToClipboard(image);
                copied = true;
            }

            bool savedAs = false;
            if (result.Action == CaptureAction.SaveAs)
            {
                // Chosen by name and folder, instead of the automatic save, not
                // as well as it: one capture, one file.
                var chosen = SaveAs.AskForImage(cfg, null);
                if (chosen != null)
                {
                    try
                    {
                        EditorWindow.SaveTo(chosen, image);
                        savedPath = chosen;
                        saved = savedAs = true;
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Could not save the capture.\n\n" + ex.Message, "KAM Capture Tool",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
            }
            else if (result.Action == CaptureAction.Save || cfg.AutoSave)
            {
                try
                {
                    var folder = cfg.EnsureSaveFolder();
                    // Names are to the second; capturing a deck quickly can take
                    // two in one second, and the second must not replace the first.
                    savedPath = NameDialog.UniquePath(Path.Combine(folder, cfg.BuildFileName(".png")));
                    EditorWindow.SaveTo(savedPath, image);
                    saved = true;
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Could not save the capture.\n\n" + ex.Message, "KAM Capture Tool",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }

            bool openEditor = result.Action == CaptureAction.Edit && cfg.OpenEditorAfterCapture;
            var home = hidden.OfType<MainWindow>().FirstOrDefault();

            // Any annotator or settings window that was open is work in
            // progress. Bring it straight back — previously a second capture
            // left the first annotator hidden, unsaved annotations and all.
            RestoreAll(hidden.Where(w => w is not MainWindow).ToList());

            if (openEditor)
            {
                // The home window steps aside while you annotate, and comes back
                // when the last annotator closes, so there is always a way to
                // the next capture without restarting.
                if (home != null) _parkedHome = home;

                var editor = new EditorWindow(image, cfg, result.Mode, saved ? savedPath : null);
                editor.Closed += OnEditorClosed;
                editor.Show();
                editor.Activate();
            }
            else if (home != null)
            {
                // Back where it was, without taking the keyboard: a slide taken
                // in the middle of a call should leave the call in front.
                home.ShowActivated = false;
                home.Show();
                home.ShowActivated = true;
            }

            var parts = new List<string>();
            if (copied) parts.Add("copied to the clipboard");
            if (saved && savedPath != null)
                parts.Add("saved as " + (savedAs ? SaveAs.Describe(savedPath, cfg.SaveFolder) : Path.GetFileName(savedPath)));
            else if (result.Action == CaptureAction.SaveAs)
                parts.Add("not saved");
            if (parts.Count > 0)
                Notified?.Invoke($"{image.PixelWidth} × {image.PixelHeight} — " + string.Join(", ", parts));
        }

        private static MainWindow? _parkedHome;
        private static bool _retaking;

        /// <summary>
        /// Close an annotator and take its capture again, the same way. The home
        /// window is not brought back in between, as closing the last annotator
        /// normally does; if the new capture is cancelled, it comes back then.
        /// </summary>
        public static async Task RetakeAsync(SnipMode mode, AppSettings cfg, EditorWindow old)
        {
            _retaking = true;
            try { old.Close(); }
            finally { _retaking = false; }

            await RunAsync(mode, cfg);

            bool annotatorOpen = Application.Current.Windows.OfType<EditorWindow>().Any(w => w.IsVisible);
            if (!annotatorOpen && _parkedHome is { } home)
            {
                _parkedHome = null;
                home.Show();
                home.Activate();
            }
        }

        private static void OnEditorClosed(object? sender, EventArgs e)
        {
            if (_parkedHome == null || _retaking) return;

            bool anotherOpen = Application.Current.Windows.OfType<EditorWindow>()
                .Any(w => w.IsVisible && !ReferenceEquals(w, sender));
            if (anotherOpen) return;

            var home = _parkedHome;
            _parkedHome = null;
            home.Show();
            home.Activate();
        }

        /// <summary>
        /// Take our own windows off the glass before the desktop is frozen, and
        /// make sure they are really gone rather than merely on their way out.
        ///
        /// Hide() alone does not do it. The compositor fades a hidden window out
        /// over several frames after the call returns, and the old fixed
        /// 160 ms wait lost that race: measured with --ghosttest, 10–30% of the
        /// home window was still in the grab, varying run to run — the faint ghost that turned up
        /// in real captures. Waiting two presented frames with the fade still
        /// on was worse, at 100%. Excluding the window from capture, or
        /// switching its fade off, each brought it to 0% on its own; both are
        /// applied, so one failing does not bring the ghost back.
        /// </summary>
        public static List<Window> ClearTheGlass(IEnumerable<Window> candidates)
        {
            var cleared = new List<Window>();
            foreach (var w in candidates)
            {
                if (!w.IsVisible) continue;
                WindowStyling.ExcludeFromCapture(w, true);
                WindowStyling.SetTransitionsDisabled(w, true);
                w.Hide();
                cleared.Add(w);
            }

            if (cleared.Count > 0)
            {
                NativeMethods.DwmFlush();
                NativeMethods.DwmFlush();
            }
            return cleared;
        }

        /// <summary>Undo <see cref="ClearTheGlass"/> once the desktop has been read.</summary>
        public static void ReleaseTheGlass(IEnumerable<Window> windows)
        {
            foreach (var w in windows)
            {
                WindowStyling.ExcludeFromCapture(w, false);
                WindowStyling.SetTransitionsDisabled(w, false);
            }
        }

        private static void RestoreAll(List<Window> hidden)
        {
            foreach (var w in hidden)
            {
                try { w.Show(); } catch { }
            }
        }
    }
}
