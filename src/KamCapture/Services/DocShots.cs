using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KamCapture.Editor;
using KamCapture.Settings;
using KamCapture.UI;

namespace KamCapture.Services
{
    /// <summary>
    /// Renders the real windows to PNG files without ever putting them on a
    /// screen: the layout is measured and arranged in memory and drawn to a
    /// bitmap. Documentation screenshots that can be regenerated on any
    /// machine, including one with no display attached.
    /// </summary>
    public static class DocShots
    {
        // The home window's client area: its size less the title bar and frame.
        private const double HomeWidth = 584;
        private const double HomeHeight = 445;

        public static int Run(string outputDir)
        {
            try
            {
                Directory.CreateDirectory(outputDir);

                // The defaults, not the settings of whoever runs this: no chosen
                // colours, and no real user folders in a public README.
                var cfg = new AppSettings
                {
                    SaveFolder = Path.Combine(ExampleProfile, "Pictures", OutputFolder.CapturesLeaf),
                    RecordFolder = Path.Combine(ExampleProfile, "Videos", OutputFolder.RecordingsLeaf),
                    AudioFolder = Path.Combine(ExampleProfile, "Music", OutputFolder.AudioLeaf)
                };

                // The home window on each of its three activities.
                foreach (var (activity, file) in new[]
                {
                    (Activity.Screenshot, "home.png"),
                    (Activity.Video, "home-video.png"),
                    (Activity.Audio, "home-audio.png")
                })
                {
                    var home = new MainWindow(cfg);
                    home.PrepareActivityForDocShot(activity);
                    Shoot(home, HomeWidth, HomeHeight, Path.Combine(outputDir, file));
                }

                // The same window with an update waiting, one version on from this one.
                var now = Setup.Updater.Current;
                var next = new Version(now.Major, now.Minor, now.Build + 1);
                var waiting = new MainWindow(cfg);
                waiting.PrepareForDocShot(new Setup.Updater.Release(next,
                    $"KAM Capture Tool {next.ToString(3)} - what changed, in a few words",
                    Setup.Updater.ReleasesPage, "https://example.invalid/KamCapture.exe", null, null, 0));
                waiting.PrepareActivityForDocShot(Activity.Screenshot);
                waiting.UpdateBar.Measure(new Size(HomeWidth, double.PositiveInfinity));
                Shoot(waiting, HomeWidth, HomeHeight + Math.Ceiling(waiting.UpdateBar.DesiredSize.Height),
                    Path.Combine(outputDir, "update.png"));
                Shoot(new SettingsWindow(cfg), 660, 700, Path.Combine(outputDir, "settings.png"));

                var editor = new EditorWindow(SelfTest.SampleCapture(), cfg);
                editor.PrepareForDocShot();
                Shoot(editor, 1220, 820, Path.Combine(outputDir, "annotator.png"));

                Console.WriteLine("doc shots written to " + outputDir);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("doc shots failed: " + ex);
                return 1;
            }
        }

        private const string ExampleProfile = @"C:\Users\you";

        /// <summary>
        /// Some text is read from the machine rather than from settings — where
        /// ffmpeg was found, for one — so swap the real profile folder out of
        /// anything shown before the picture is taken.
        /// </summary>
        private static void Scrub(DependencyObject node)
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(profile)) return;

            switch (node)
            {
                case TextBlock t when t.Text.Contains(profile, StringComparison.OrdinalIgnoreCase):
                    t.Text = t.Text.Replace(profile, ExampleProfile, StringComparison.OrdinalIgnoreCase);
                    break;
                case TextBox b when b.Text.Contains(profile, StringComparison.OrdinalIgnoreCase):
                    b.Text = b.Text.Replace(profile, ExampleProfile, StringComparison.OrdinalIgnoreCase);
                    break;
            }

            foreach (var child in LogicalTreeHelper.GetChildren(node))
                if (child is DependencyObject d) Scrub(d);
        }

        /// <summary>Lay the window out at this client size, draw it to a PNG, and hand back what was laid out.</summary>
        internal static FrameworkElement Shoot(Window window, double width, double height, string path,
                                               double scale = 2.0, bool quiet = false)
        {
            window.Width = width;
            window.Height = height;

            if (window.Content is not FrameworkElement root)
                throw new InvalidOperationException("window has no content: " + window.GetType().Name);

            Scrub(root);

            // Arranging the root directly would swallow its margin, because a
            // margin is applied by the parent. Re-host it in a border of the
            // window's own size so the shot matches what the window shows.
            window.Content = null;
            var host = new Border { Child = root, Background = window.Background ?? Brushes.Black };

            var size = new Size(width, height);
            host.Measure(size);
            host.Arrange(new Rect(size));
            host.UpdateLayout();

            // A second pass: the first settles sizes that later passes read.
            host.Measure(size);
            host.Arrange(new Rect(size));
            host.UpdateLayout();

            var brush = new VisualBrush(host)
            {
                Stretch = Stretch.None,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
                TileMode = TileMode.None
            };

            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.PushTransform(new ScaleTransform(scale, scale));
                dc.DrawRectangle(brush, null, new Rect(size));
                dc.Pop();
            }

            var rtb = new RenderTargetBitmap(
                (int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale),
                96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();

            using var fs = File.Create(path);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            enc.Save(fs);

            if (!quiet) Console.WriteLine($"  {Path.GetFileName(path)}  {rtb.PixelWidth} x {rtb.PixelHeight}");
            return host;
        }
    }
}
