using System;
using System.IO;
using System.Windows;
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
        public static int Run(string outputDir)
        {
            try
            {
                Directory.CreateDirectory(outputDir);
                var cfg = AppSettings.Load();

                Shoot(new MainWindow(cfg), 726, 560, Path.Combine(outputDir, "home.png"));
                Shoot(new SettingsWindow(cfg), 720, 760, Path.Combine(outputDir, "settings.png"));
                Shoot(new RecorderSetupWindow(cfg), 620, 580, Path.Combine(outputDir, "record-setup.png"));

                var editor = new EditorWindow(SelfTest.SampleCapture(), cfg);
                editor.PrepareForDocShot();
                Shoot(editor, 1280, 820, Path.Combine(outputDir, "annotator.png"));

                Console.WriteLine("doc shots written to " + outputDir);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("doc shots failed: " + ex);
                return 1;
            }
        }

        private static void Shoot(Window window, double width, double height, string path, double scale = 2.0)
        {
            window.Width = width;
            window.Height = height;

            if (window.Content is not FrameworkElement root)
                throw new InvalidOperationException("window has no content: " + window.GetType().Name);

            var size = new Size(width, height);
            root.Measure(size);
            root.Arrange(new Rect(size));
            root.UpdateLayout();

            // Arrange twice: the first pass settles sizes that later passes read.
            root.Measure(size);
            root.Arrange(new Rect(size));
            root.UpdateLayout();

            var brush = new VisualBrush(root)
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
                dc.DrawRectangle(window.Background ?? Brushes.Black, null, new Rect(size));
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

            Console.WriteLine($"  {Path.GetFileName(path)}  {rtb.PixelWidth} x {rtb.PixelHeight}");
        }
    }
}
