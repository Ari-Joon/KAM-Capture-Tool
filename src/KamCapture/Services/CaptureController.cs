using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using KamCapture.Capture;
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
                foreach (Window w in Application.Current.Windows)
                {
                    if (w.IsVisible && w is MainWindow or EditorWindow or SettingsWindow)
                    {
                        hidden.Add(w);
                        w.Hide();
                    }
                }

                int delay = delayOverride ?? cfg.DelaySeconds;
                await Task.Delay(Math.Max(0, delay) * 1000 + 160);

                var snapshot = ScreenGrabber.CaptureVirtualDesktop(cfg.IncludeCursor);
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

        private static async Task HandleResultAsync(CaptureResult result, AppSettings cfg, List<Window> hidden)
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

            if (result.Action == CaptureAction.Save || cfg.AutoSave)
            {
                try
                {
                    var folder = cfg.EnsureSaveFolder();
                    savedPath = Path.Combine(folder, cfg.BuildFileName(".png"));
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

            if (openEditor)
            {
                var editor = new EditorWindow(image, cfg);
                editor.Show();
                editor.Activate();
            }
            else
            {
                RestoreAll(hidden);
            }

            var parts = new List<string>();
            if (copied) parts.Add("copied to the clipboard");
            if (saved && savedPath != null) parts.Add("saved to " + savedPath);
            if (parts.Count > 0)
                Notified?.Invoke($"{image.PixelWidth} × {image.PixelHeight} — " + string.Join(", ", parts));
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
