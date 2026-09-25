using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using KamCapture.Settings;
using KamCapture.UI;
using Activity = KamCapture.Settings.Activity;

namespace KamCapture.Services
{
    /// <summary>
    /// Lays out every window at its real size, in the states that stretch it
    /// most, and reports anything drawn outside the space it was given: text cut
    /// through at an edge, a row pushed below the bottom of a fixed panel, a
    /// button past the edge of the window. Nothing goes on screen. Each state is
    /// also written as a PNG, to look at as well as to count.
    ///
    /// Text that ends in an ellipsis is listed but does not fail the check —
    /// that is a decision about long names and paths, not something cut off.
    /// </summary>
    public static class LayoutCheck
    {
        // Between a window's size and its client area: the frame and title bar.
        private const double FrameWidth = 16;
        private const double FrameHeight = 39;

        // A pixel of overhang is antialiasing on a rounded corner, not clipping.
        private const double Tolerance = 1.0;

        public static int Run(string outputDir)
        {
            try
            {
                Directory.CreateDirectory(outputDir);

                var cfg = new AppSettings
                {
                    SaveFolder = @"C:\Users\you\Pictures\KAM Capture Tool\Screenshots",
                    RecordFolder = @"C:\Users\you\Videos\KAM Capture Tool\Recordings",
                    AudioFolder = @"C:\Users\you\Music\KAM Capture Tool\Audio"
                };

                var cut = new List<string>();
                var trimmed = new List<string>();

                foreach (var (activity, recording, message, name) in new (Activity, bool, string?, string)[]
                {
                    (Activity.Screenshot, false, null, "home-screenshot"),
                    (Activity.Video, false, null, "home-video"),
                    (Activity.Audio, false, null, "home-audio"),
                    (Activity.Video, true, null, "home-video-recording"),
                    (Activity.Audio, true, null, "home-audio-recording"),
                    (Activity.Audio, false, "Audio saved as KAM-2026-09-24-10-15-22.mp3, 86.4 MB", "home-audio-saved"),
                    (Activity.Screenshot, false,
                        "2560 × 1600 — copied to the clipboard, saved as KAM-2026-09-24-10-15-22-2.png", "home-long-message"),
                })
                {
                    var home = new MainWindow(cfg);
                    home.PrepareForLayoutCheck(activity, recording, message);
                    Check(home, home.Width, home.Height, name, outputDir, cut, trimmed);
                }

                var longNames = new MainWindow(cfg);
                longNames.PrepareForLayoutCheck(Activity.Audio, recording: false, message: null, longDeviceNames: true);
                Check(longNames, longNames.Width, longNames.Height, "home-long-device-names", outputDir, cut, trimmed);

                // An update waiting, with a long summary: the window grows by the
                // bar's height, exactly as MainWindow.FitToBar does.
                var now = Setup.Updater.Current;
                var next = new Version(now.Major, now.Minor + 1, 0);
                var waiting = new MainWindow(cfg);
                waiting.PrepareForDocShot(new Setup.Updater.Release(next,
                    $"KAM Capture Tool {next.ToString(3)} - audio on its own, a folder for each kind, and nothing cut off",
                    Setup.Updater.ReleasesPage, "https://example.invalid/KamCapture.exe", null, null, 0));
                waiting.PrepareForLayoutCheck(Activity.Audio, recording: false, message: null);
                waiting.UpdateBar.Measure(new Size(waiting.Width - FrameWidth, double.PositiveInfinity));
                Check(waiting, waiting.Width, waiting.Height + Math.Ceiling(waiting.UpdateBar.DesiredSize.Height),
                    "home-update-waiting", outputDir, cut, trimmed);

                var settings = new SettingsWindow(cfg);
                Check(settings, settings.Width, settings.Height, "settings", outputDir, cut, trimmed);
                // Tall enough to show every section at once, to look over.
                var whole = new SettingsWindow(cfg);
                Check(whole, whole.Width, 1500, "settings-whole", outputDir, cut, trimmed);
                var smallest = new SettingsWindow(cfg);
                Check(smallest, smallest.MinWidth, smallest.MinHeight, "settings-smallest", outputDir, cut, trimmed);

                Check(new Setup.SetupWindow(), 620, 520, "setup", outputDir, cut, trimmed);
                Check(new Setup.SetupWindow { ReplacesRunningCopy = true }, 620, 520, "setup-over-running-copy",
                    outputDir, cut, trimmed);

                var editor = new EditorWindow(SelfTest.SampleCapture(), cfg);
                editor.PrepareForDocShot();
                Check(editor, editor.Width, editor.Height, "annotator", outputDir, cut, trimmed);

                // Every tool shows a different set of options above the board, so
                // each gets its turn at the smallest size the window allows.
                foreach (var tool in Enum.GetValues<Editor.EditTool>())
                {
                    var small = new EditorWindow(SelfTest.SampleCapture(), cfg);
                    small.PrepareForLayoutCheck(tool);
                    Check(small, small.MinWidth, small.MinHeight, "annotator-smallest-" + tool.ToString().ToLowerInvariant(),
                        outputDir, cut, trimmed);
                }

                // The recording bar's transport, in its own styles. The bar itself
                // cannot be built without a live recorder, and hides from every
                // screenshot on purpose, so this is how its buttons get looked at.
                var transport = new System.Windows.Controls.StackPanel
                {
                    Orientation = System.Windows.Controls.Orientation.Horizontal,
                    Margin = new Thickness(12),
                    VerticalAlignment = VerticalAlignment.Center
                };
                foreach (var (label, style) in new[] { ("Pause", ""), ("Retake", ""), ("Save", "PrimaryButton"),
                                                       ("Save as…", ""), ("STOP", "StopButton") })
                {
                    var b = new System.Windows.Controls.Button { Content = label, Margin = new Thickness(0, 0, 6, 0), MinWidth = 70 };
                    if (style.Length > 0) b.Style = (Style)Application.Current.FindResource(style);
                    transport.Children.Add(b);
                }
                var barWindow = new Window
                {
                    Content = transport,
                    Background = (System.Windows.Media.Brush)Application.Current.FindResource("Navy"),
                    Width = 460 + FrameWidth, Height = 56 + FrameHeight
                };
                Check(barWindow, barWindow.Width, barWindow.Height, "recording-bar-buttons", outputDir, cut, trimmed);

                var naming = new NameDialog("KAM-2026-09-24-10-15-22", cfg.SaveFolder);
                Check(naming, naming.Width, MeasuredHeight(naming), "name-dialog", outputDir, cut, trimmed);

                foreach (var t in trimmed) Console.WriteLine("  trimmed  " + t);
                foreach (var c in cut) Console.WriteLine("  CUT      " + c);

                if (cut.Count > 0)
                {
                    Console.Error.WriteLine($"layout-check FAILED: {cut.Count} element(s) cut off");
                    return 1;
                }
                Console.WriteLine("layout-check OK  ->  " + outputDir);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("layout-check failed: " + ex);
                return 1;
            }
        }

        /// <summary>For a window that sizes itself to its content: the height it would choose.</summary>
        private static double MeasuredHeight(Window window)
        {
            if (window.Content is not FrameworkElement root) return window.Height;
            root.Measure(new Size(window.Width - FrameWidth, double.PositiveInfinity));
            return Math.Ceiling(root.DesiredSize.Height) + FrameHeight;
        }

        private static void Check(Window window, double width, double height, string name, string outputDir,
                                  List<string> cut, List<string> trimmed)
        {
            var size = new Size(width - FrameWidth, height - FrameHeight);
            var host = DocShots.Shoot(window, size.Width, size.Height, Path.Combine(outputDir, name + ".png"),
                                      scale: 1.0, quiet: true);

            int before = cut.Count;
            Walk(host, host, new Rect(size), name, cut, trimmed, reported: false);
            Console.WriteLine($"  {name,-26} {size.Width:0} x {size.Height:0}  " +
                              (cut.Count == before ? "clear" : $"{cut.Count - before} cut off"));
        }

        private static void Walk(DependencyObject node, Visual root, Rect clip, string where,
                                 List<string> cut, List<string> trimmed, bool reported)
        {
            if (node is UIElement { Visibility: not Visibility.Visible }) return;

            if (node is FrameworkElement fe && fe.ActualWidth > 0 && fe.ActualHeight > 0)
            {
                Rect bounds;
                try { bounds = fe.TransformToAncestor(root).TransformBounds(new Rect(fe.RenderSize)); }
                catch (InvalidOperationException) { return; }

                if (!reported && IsReportable(fe))
                {
                    double over = Overhang(bounds, clip);
                    if (over > Tolerance)
                    {
                        cut.Add($"{where}: {Describe(fe)} is cut off by {over:0} px");
                        reported = true;   // its insides are cut too; say it once
                    }
                }

                if (fe is TextBlock tb && tb.TextTrimming != TextTrimming.None && IsTrimmed(tb))
                    trimmed.Add($"{where}: {Describe(tb)}");

                // Not cut, but worth knowing: a bar that only fits by scrolling.
                if (fe is ScrollViewer { HorizontalScrollBarVisibility: not ScrollBarVisibility.Disabled } bar &&
                    bar.ExtentWidth > bar.ViewportWidth + Tolerance)
                    trimmed.Add($"{where}: {Describe(bar)} scrolls sideways, {bar.ExtentWidth:0} in {bar.ViewportWidth:0}");

                // Text boxes scroll their text; that is not clipping.
                if (fe is TextBoxBase or PasswordBox) return;

                var layoutClip = LayoutInformation.GetLayoutClip(fe);
                if (layoutClip != null)
                    clip = Rect.Intersect(clip, fe.TransformToAncestor(root).TransformBounds(layoutClip.Bounds));
                if (fe.ClipToBounds)
                    clip = Rect.Intersect(clip, bounds);

                // A scrolling panel shows the rest when scrolled, so overflow is
                // only a cut in a direction it cannot scroll.
                if (fe is ScrollContentPresenter { ScrollOwner: { } sv } && !clip.IsEmpty)
                {
                    if (sv.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled)
                        clip = new Rect(-1e6, clip.Y, 2e6, clip.Height);
                    if (sv.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled)
                        clip = new Rect(clip.X, -1e6, clip.Width, 2e6);
                }
            }

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                Walk(VisualTreeHelper.GetChild(node, i), root, clip, where, cut, trimmed, reported);
        }

        private static bool IsReportable(FrameworkElement fe) =>
            fe is TextBlock or Control or Image or System.Windows.Shapes.Shape ||
            (fe is Border b && b.Background != null && !string.IsNullOrEmpty(b.Name));

        private static double Overhang(Rect bounds, Rect clip)
        {
            if (clip.IsEmpty) return Math.Max(bounds.Width, bounds.Height);
            return Math.Max(Math.Max(clip.Left - bounds.Left, bounds.Right - clip.Right),
                            Math.Max(clip.Top - bounds.Top, bounds.Bottom - clip.Bottom));
        }

        private static bool IsTrimmed(TextBlock tb)
        {
            if (string.IsNullOrEmpty(tb.Text) || tb.TextWrapping != TextWrapping.NoWrap) return false;
            var ft = new FormattedText(tb.Text, CultureInfo.CurrentUICulture, tb.FlowDirection,
                new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch),
                tb.FontSize, Brushes.Black, 1.0);
            return ft.WidthIncludingTrailingWhitespace > tb.ActualWidth - tb.Padding.Left - tb.Padding.Right + 0.5;
        }

        private static string Describe(FrameworkElement fe)
        {
            string kind = fe.GetType().Name;
            string name = string.IsNullOrEmpty(fe.Name) ? "" : " " + fe.Name;
            string text = fe switch
            {
                TextBlock t => t.Text,
                ContentControl { Content: string s } => s,
                _ => ""
            };
            if (text.Length > 60) text = text[..57] + "...";
            return text.Length > 0 ? $"{kind}{name} \"{text}\"" : kind + name;
        }
    }
}
