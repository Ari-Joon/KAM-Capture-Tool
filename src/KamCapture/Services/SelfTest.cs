using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KamCapture.Editor;

namespace KamCapture.Services
{
    /// <summary>
    /// Renders one board containing every kind of annotation and writes it to a
    /// PNG. No windows, no screen capture — a smoke test for the drawing,
    /// grouping and export paths that can run anywhere, including CI.
    /// </summary>
    public static class SelfTest
    {
        public static int Run(string outputPath)
        {
            try
            {
                var doc = BuildSample();

                // Exercise grouping: bind three marks together, then move and
                // scale the group as one, which is what the editor does.
                var group = new GroupItem();
                group.Children.Add(new SymbolItem
                {
                    A = new Point(0, 0), B = new Point(70, 70),
                    Symbol = "circle", Filled = false, StrokeColor = "#2BB673", Thickness = 4
                });
                group.Children.Add(new LineItem
                {
                    A = new Point(70, 70), B = new Point(150, 120),
                    StrokeColor = "#2BB673", Thickness = 4, ArrowEnd = true
                });
                group.Children.Add(new TextItem
                {
                    Origin = new Point(150, 110), Text = "grouped", FontSize = 20,
                    StrokeColor = "#0B3D2A", BackgroundColor = "#C9F2DE"
                });

                var before = group.Bounds;
                group.Translate(560, 470);
                group.Scale(1.4, 1.4, new Point(560, 470));
                var after = group.Bounds;
                doc.Items.Add(group);

                double grew = after.Width / Math.Max(0.01, before.Width);
                if (Math.Abs(grew - 1.4) > 0.08)
                    return Fail($"group scaling wrong: expected ~1.40x, measured {grew:0.00}x");

                var png = doc.Export(2);
                if (png.PixelWidth != (int)Math.Ceiling(doc.BoardSize.Width * 2))
                    return Fail("export scale did not apply");

                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
                using (var fs = File.Create(outputPath))
                {
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(png));
                    enc.Save(fs);
                }

                Console.WriteLine($"self-test OK  ->  {outputPath}  ({png.PixelWidth} x {png.PixelHeight})");
                Console.WriteLine($"  items: {doc.Items.Count}, symbols in catalogue: {SymbolCatalog.All.Count}");
                return 0;
            }
            catch (Exception ex)
            {
                return Fail(ex.ToString());
            }
        }

        private static int Fail(string message)
        {
            Console.Error.WriteLine("self-test FAILED: " + message);
            return 1;
        }

        /// <summary>The stand-in screenshot, also used for documentation shots.</summary>
        public static BitmapSource SampleCapture() => BuildSample().Image!;

        private static BoardDocument BuildSample()
        {
            // Stand-in for a screenshot: a small "UI" with a button to point at.
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1E, 0x24, 0x31)), null, new Rect(0, 0, 520, 300));
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x4A, 0x7C, 0xFF)), null,
                    new Rect(60, 90, 150, 44), 6, 6);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x2A, 0x33, 0x44)), null,
                    new Rect(60, 160, 380, 26), 4, 4);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x2A, 0x33, 0x44)), null,
                    new Rect(60, 200, 300, 26), 4, 4);
            }
            var rtb = new RenderTargetBitmap(520, 300, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();

            var doc = BoardDocument.FromCapture(rtb, 260);

            var opts = new ToolOptions();

            // 1. A. i. — the numbering styles, on the board.
            doc.Items.Add(new StepItem
            {
                Center = new Point(300, 320), Radius = 20,
                Label = StepItem.LabelFor(StepStyle.Number, 1),
                StrokeColor = "#E5342A", FillColor = "#E5342A"
            });
            doc.Items.Add(new StepItem
            {
                Center = new Point(300, 400), Radius = 20,
                Label = StepItem.LabelFor(StepStyle.UpperLetter, 1),
                StrokeColor = "#FF8A00", FillColor = "#FF8A00"
            });
            doc.Items.Add(new StepItem
            {
                Center = new Point(300, 480), Radius = 20,
                Label = StepItem.LabelFor(StepStyle.LowerRoman, 3),
                StrokeColor = "#7A5CFF", FillColor = "#7A5CFF"
            });

            doc.Items.Add(new TextItem
            {
                Origin = new Point(40, 300), Text = "1.  make this button bigger", FontSize = 20,
                StrokeColor = "#111111", BackgroundColor = "#FFFFFF", MaxWidth = 240
            });
            doc.Items.Add(new TextItem
            {
                Origin = new Point(40, 380), Text = "A.  this row wraps badly", FontSize = 20,
                StrokeColor = "#111111", BackgroundColor = "#FFFFFF", MaxWidth = 240
            });
            doc.Items.Add(new TextItem
            {
                Origin = new Point(40, 460), Text = "iii.  and redact this", FontSize = 20,
                StrokeColor = "#111111", BackgroundColor = "#FFFFFF", MaxWidth = 240
            });

            // Arrows from the notes to the thing being talked about.
            doc.Items.Add(new LineItem
            {
                A = new Point(330, 320), B = new Point(410, 370),
                StrokeColor = "#E5342A", Thickness = 4, ArrowEnd = true
            });
            doc.Items.Add(new RectItem
            {
                A = new Point(312, 342), B = new Point(472, 396),
                StrokeColor = "#E5342A", Thickness = 3
            });
            doc.Items.Add(new EllipseItem
            {
                A = new Point(300, 410), B = new Point(700, 460),
                StrokeColor = "#FF8A00", Thickness = 3
            });
            doc.Items.Add(new StrokeItem
            {
                StrokeColor = "#FFD400", Thickness = 22, IsHighlighter = true,
                Points = { new Point(320, 452), new Point(480, 452), new Point(640, 452) }
            });
            doc.Items.Add(new RedactItem
            {
                A = new Point(320, 460), B = new Point(620, 486), BlockSize = 9
            });

            // A sample of the symbol palette, drawn from the catalogue.
            double x = 300;
            foreach (var name in new[] { "arrow-right", "check", "cross", "warning", "callout", "target", "star", "cursor" })
            {
                doc.Items.Add(new SymbolItem
                {
                    A = new Point(x, 560), B = new Point(x + 52, 612),
                    Symbol = name, Filled = true, StrokeColor = "#4A7CFF", Thickness = 6
                });
                x += 64;
            }

            return doc;
        }
    }
}
