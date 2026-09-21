using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using KamCapture.Editor;
using KamCapture.Settings;

namespace KamCapture.Controls
{
    /// <summary>
    /// A live rehearsal of the selection overlay, so the colour wheel is
    /// answering a question you can actually see.
    /// </summary>
    public sealed class BorderPreview : FrameworkElement
    {
        private readonly AppSettings _cfg;

        public BorderPreview(AppSettings cfg)
        {
            _cfg = cfg;
            Height = 132;
            ClipToBounds = true;
        }

        public void Refresh() => InvalidateVisual();

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 10 || h < 10) return;

            var outer = new Rect(0, 0, w, h);
            dc.PushClip(new RectangleGeometry(outer, 6, 6));

            // A stand-in desktop: something with enough structure to judge against.
            var wall = new LinearGradientBrush(
                Color.FromRgb(0x1C, 0x2A, 0x46), Color.FromRgb(0x2E, 0x1F, 0x3E), 55);
            wall.Freeze();
            dc.DrawRectangle(wall, null, outer);

            var card = new SolidColorBrush(Color.FromRgb(0xEC, 0xEF, 0xF5));
            card.Freeze();
            dc.DrawRoundedRectangle(card, null, new Rect(w * 0.10, h * 0.20, w * 0.52, h * 0.60), 5, 5);

            var bar = new SolidColorBrush(Color.FromRgb(0xD9, 0xA9, 0x3A));
            bar.Freeze();
            dc.DrawRoundedRectangle(bar, null, new Rect(w * 0.14, h * 0.30, w * 0.26, h * 0.09), 3, 3);

            var grey = new SolidColorBrush(Color.FromRgb(0xC2, 0xC8, 0xD4));
            grey.Freeze();
            for (int i = 0; i < 3; i++)
                dc.DrawRoundedRectangle(grey, null,
                    new Rect(w * 0.14, h * (0.46 + i * 0.12), w * (0.42 - i * 0.07), h * 0.06), 3, 3);

            var sel = new Rect(w * 0.30, h * 0.26, w * 0.46, h * 0.50);

            // Dim everything outside the selection, exactly as the overlay does.
            var dimColor = ColorUtil.Parse(_cfg.DimColor);
            dimColor.A = (byte)Math.Clamp(_cfg.DimOpacity * 255, 0, 255);
            var dim = new SolidColorBrush(dimColor);
            dim.Freeze();
            var mask = new CombinedGeometry(GeometryCombineMode.Exclude,
                new RectangleGeometry(outer), new RectangleGeometry(sel));
            mask.Freeze();
            dc.DrawGeometry(dim, null, mask);

            var accent = ColorUtil.Parse(_cfg.BorderColor);
            var pen = new Pen(new SolidColorBrush(accent), Math.Max(0.5, _cfg.BorderThickness));
            pen.Brush.Freeze();
            switch (_cfg.BorderStyle)
            {
                case BorderStyleKind.Dashed: pen.DashStyle = new DashStyle(new double[] { 5, 3 }, 0); break;
                case BorderStyleKind.Dotted:
                    pen.DashStyle = new DashStyle(new double[] { 1, 2 }, 0);
                    pen.DashCap = PenLineCap.Round; break;
                case BorderStyleKind.Glow:
                    for (int i = 4; i >= 1; i--)
                    {
                        var c = accent; c.A = (byte)(26 * (5 - i) / 2);
                        var gp = new Pen(new SolidColorBrush(c), _cfg.BorderThickness + i * 2.2);
                        gp.Brush.Freeze();
                        dc.DrawRectangle(null, gp, sel);
                    }
                    break;
            }
            dc.DrawRectangle(null, pen, sel);

            if (_cfg.ShowRuleOfThirds)
            {
                var thin = new Pen(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), 1);
                thin.Brush.Freeze();
                for (int i = 1; i <= 2; i++)
                {
                    double x = sel.Left + sel.Width * i / 3.0, y = sel.Top + sel.Height * i / 3.0;
                    dc.DrawLine(thin, new Point(x, sel.Top), new Point(x, sel.Bottom));
                    dc.DrawLine(thin, new Point(sel.Left, y), new Point(sel.Right, y));
                }
            }

            var handleFill = new SolidColorBrush(ColorUtil.Parse(_cfg.HandleColor));
            handleFill.Freeze();
            var handleEdge = new Pen(new SolidColorBrush(accent), 1.4);
            handleEdge.Brush.Freeze();
            const double hs = 3.6;
            foreach (var p in new[]
            {
                new Point(sel.Left, sel.Top), new Point(sel.Left + sel.Width / 2, sel.Top), new Point(sel.Right, sel.Top),
                new Point(sel.Right, sel.Top + sel.Height / 2), new Point(sel.Right, sel.Bottom),
                new Point(sel.Left + sel.Width / 2, sel.Bottom), new Point(sel.Left, sel.Bottom),
                new Point(sel.Left, sel.Top + sel.Height / 2)
            })
                dc.DrawRectangle(handleFill, handleEdge, new Rect(p.X - hs, p.Y - hs, hs * 2, hs * 2));

            if (_cfg.ShowCrosshair)
            {
                var c = accent; c.A = 150;
                var cp = new Pen(new SolidColorBrush(c), 1) { DashStyle = new DashStyle(new double[] { 4, 4 }, 0) };
                cp.Brush.Freeze();
                double cx = sel.Right, cy = sel.Bottom;
                dc.DrawLine(cp, new Point(0, cy), new Point(w, cy));
                dc.DrawLine(cp, new Point(cx, 0), new Point(cx, h));
            }

            if (_cfg.ShowDimensions)
            {
                var ft = new FormattedText("820 × 460 px", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                    11, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);

                var box = new Rect(sel.Left, Math.Max(2, sel.Top - ft.Height - 8), ft.Width + 12, ft.Height + 6);
                var bg = new SolidColorBrush(Color.FromArgb(225, 12, 12, 14));
                bg.Freeze();
                var edge = new Pen(new SolidColorBrush(accent), 1);
                edge.Brush.Freeze();
                dc.DrawRoundedRectangle(bg, edge, box, 4, 4);
                dc.DrawText(ft, new Point(box.X + 6, box.Y + 3));
            }

            dc.Pop();
        }
    }
}
