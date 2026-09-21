using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KamCapture.Editor
{
    /// <summary>Everything a shape needs in order to draw itself.</summary>
    public sealed class RenderCtx
    {
        public BitmapSource? Source;      // the screenshot, for redaction blocks
        public Rect ImageRect;            // where that screenshot sits on the board
        public double Zoom = 1.0;
        public double PixelsPerDip = 1.0;
        public bool ForExport;
    }

    [JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
    [JsonDerivedType(typeof(StrokeItem), "stroke")]
    [JsonDerivedType(typeof(LineItem), "line")]
    [JsonDerivedType(typeof(RectItem), "rect")]
    [JsonDerivedType(typeof(EllipseItem), "ellipse")]
    [JsonDerivedType(typeof(TextItem), "text")]
    [JsonDerivedType(typeof(StepItem), "step")]
    [JsonDerivedType(typeof(RedactItem), "redact")]
    [JsonDerivedType(typeof(SymbolItem), "symbol")]
    [JsonDerivedType(typeof(GroupItem), "group")]
    public abstract class AnnItem
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string StrokeColor { get; set; } = "#E5342A";
        public double Thickness { get; set; } = 3;

        [JsonIgnore] public Color Ink => ColorUtil.Parse(StrokeColor);

        public abstract Rect Bounds { get; }
        public abstract void Render(DrawingContext dc, RenderCtx ctx);
        public abstract bool HitTest(Point p, double tolerance);
        public abstract void ApplyTransform(Matrix m);
        public abstract AnnItem Clone();

        public void Translate(double dx, double dy)
        {
            var m = Matrix.Identity;
            m.Translate(dx, dy);
            ApplyTransform(m);
        }

        /// <summary>Scale about an anchor point. Used by the selection handles.</summary>
        public void Scale(double sx, double sy, Point anchor)
        {
            var m = Matrix.Identity;
            m.ScaleAt(sx, sy, anchor.X, anchor.Y);
            ApplyTransform(m);
        }

        protected static double ScaleMagnitude(Matrix m)
        {
            double sx = Math.Sqrt(m.M11 * m.M11 + m.M12 * m.M12);
            double sy = Math.Sqrt(m.M21 * m.M21 + m.M22 * m.M22);
            double s = (sx + sy) / 2.0;
            return s <= 0.0001 ? 1.0 : s;
        }

        protected void CopyBaseTo(AnnItem other)
        {
            other.StrokeColor = StrokeColor;
            other.Thickness = Thickness;
        }

        protected static Rect NormaliseRect(Point a, Point b) =>
            new Rect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

        protected static bool NearSegment(Point p, Point a, Point b, double tol)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double lenSq = dx * dx + dy * dy;
            double t = lenSq <= 0 ? 0 : ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq;
            t = Math.Max(0, Math.Min(1, t));
            double cx = a.X + t * dx, cy = a.Y + t * dy;
            double ddx = p.X - cx, ddy = p.Y - cy;
            return ddx * ddx + ddy * ddy <= tol * tol;
        }

        protected Pen MakePen(double? widthOverride = null, bool round = true)
        {
            var pen = new Pen(new SolidColorBrush(Ink), widthOverride ?? Thickness);
            pen.StartLineCap = round ? PenLineCap.Round : PenLineCap.Flat;
            pen.EndLineCap = round ? PenLineCap.Round : PenLineCap.Flat;
            pen.LineJoin = PenLineJoin.Round;
            pen.Brush.Freeze();
            return pen;
        }
    }

    public static class ColorUtil
    {
        public static Color Parse(string s)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(s)) return Colors.Red;
                var obj = ColorConverter.ConvertFromString(s);
                return obj is Color c ? c : Colors.Red;
            }
            catch { return Colors.Red; }
        }

        public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        public static string ToHexA(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
    }

    // ------------------------------------------------------------------
    //  Freehand pencil and highlighter
    // ------------------------------------------------------------------
    public sealed class StrokeItem : AnnItem
    {
        public List<Point> Points { get; set; } = new();
        public bool IsHighlighter { get; set; }

        public override Rect Bounds
        {
            get
            {
                if (Points.Count == 0) return Rect.Empty;
                double minX = Points.Min(p => p.X), maxX = Points.Max(p => p.X);
                double minY = Points.Min(p => p.Y), maxY = Points.Max(p => p.Y);
                double pad = Thickness / 2 + 1;
                return new Rect(minX - pad, minY - pad, maxX - minX + pad * 2, maxY - minY + pad * 2);
            }
        }

        public override void Render(DrawingContext dc, RenderCtx ctx)
        {
            if (Points.Count == 0) return;

            var pen = MakePen(null, !IsHighlighter);
            if (IsHighlighter)
            {
                var c = Ink; c.A = 110;
                pen = new Pen(new SolidColorBrush(c), Thickness)
                {
                    StartLineCap = PenLineCap.Flat,
                    EndLineCap = PenLineCap.Flat,
                    LineJoin = PenLineJoin.Round
                };
                pen.Brush.Freeze();
            }

            if (Points.Count == 1)
            {
                dc.DrawEllipse(pen.Brush, null, Points[0], Thickness / 2, Thickness / 2);
                return;
            }

            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(Points[0], false, false);
                // Quadratic smoothing through midpoints: the pencil follows the
                // hand rather than showing every sampled jitter.
                for (int i = 1; i < Points.Count - 1; i++)
                {
                    var mid = new Point((Points[i].X + Points[i + 1].X) / 2,
                                        (Points[i].Y + Points[i + 1].Y) / 2);
                    g.QuadraticBezierTo(Points[i], mid, true, false);
                }
                g.LineTo(Points[^1], true, false);
            }
            geo.Freeze();
            dc.DrawGeometry(null, pen, geo);
        }

        public override bool HitTest(Point p, double tolerance)
        {
            double tol = tolerance + Thickness / 2;
            if (Points.Count == 1)
                return (p - Points[0]).Length <= tol;
            for (int i = 0; i < Points.Count - 1; i++)
                if (NearSegment(p, Points[i], Points[i + 1], tol)) return true;
            return false;
        }

        public override void ApplyTransform(Matrix m)
        {
            for (int i = 0; i < Points.Count; i++) Points[i] = m.Transform(Points[i]);
            Thickness = Math.Max(0.5, Thickness * ScaleMagnitude(m));
        }

        public override AnnItem Clone()
        {
            var c = new StrokeItem { Points = new List<Point>(Points), IsHighlighter = IsHighlighter };
            CopyBaseTo(c);
            return c;
        }
    }

    // ------------------------------------------------------------------
    //  Straight line / arrow
    // ------------------------------------------------------------------
    public sealed class LineItem : AnnItem
    {
        public Point A { get; set; }
        public Point B { get; set; }
        public bool ArrowEnd { get; set; }
        public bool ArrowStart { get; set; }

        public override Rect Bounds
        {
            get
            {
                double pad = Thickness * 3 + 4;
                var r = NormaliseRect(A, B);
                r.Inflate(pad, pad);
                return r;
            }
        }

        public override void Render(DrawingContext dc, RenderCtx ctx)
        {
            var pen = MakePen();
            dc.DrawLine(pen, A, B);
            if (ArrowEnd) DrawHead(dc, pen, A, B);
            if (ArrowStart) DrawHead(dc, pen, B, A);
        }

        private void DrawHead(DrawingContext dc, Pen pen, Point from, Point tip)
        {
            var v = tip - from;
            double len = v.Length;
            if (len < 0.5) return;
            v.Normalize();

            double size = Math.Max(8, Thickness * 4);
            double angle = 26 * Math.PI / 180;
            double cos = Math.Cos(angle), sin = Math.Sin(angle);

            var back = new Vector(-v.X, -v.Y);
            var left = new Vector(back.X * cos - back.Y * sin, back.X * sin + back.Y * cos) * size;
            var right = new Vector(back.X * cos + back.Y * sin, -back.X * sin + back.Y * cos) * size;

            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(tip, true, true);
                g.LineTo(tip + left, true, false);
                g.LineTo(tip + right, true, false);
            }
            geo.Freeze();
            dc.DrawGeometry(pen.Brush, null, geo);
        }

        public override bool HitTest(Point p, double tolerance) =>
            NearSegment(p, A, B, tolerance + Thickness / 2 + 2);

        public override void ApplyTransform(Matrix m)
        {
            A = m.Transform(A);
            B = m.Transform(B);
            Thickness = Math.Max(0.5, Thickness * ScaleMagnitude(m));
        }

        public override AnnItem Clone()
        {
            var c = new LineItem { A = A, B = B, ArrowEnd = ArrowEnd, ArrowStart = ArrowStart };
            CopyBaseTo(c);
            return c;
        }
    }

    // ------------------------------------------------------------------
    //  Rectangle
    // ------------------------------------------------------------------
    public sealed class RectItem : AnnItem
    {
        public Point A { get; set; }
        public Point B { get; set; }
        public string? FillColor { get; set; }
        public double CornerRadius { get; set; } = 0;

        [JsonIgnore] public Rect R => NormaliseRect(A, B);

        public override Rect Bounds
        {
            get { var r = R; r.Inflate(Thickness / 2 + 1, Thickness / 2 + 1); return r; }
        }

        public override void Render(DrawingContext dc, RenderCtx ctx)
        {
            Brush? fill = null;
            if (!string.IsNullOrEmpty(FillColor))
            {
                fill = new SolidColorBrush(ColorUtil.Parse(FillColor));
                fill.Freeze();
            }
            dc.DrawRoundedRectangle(fill, MakePen(), R, CornerRadius, CornerRadius);
        }

        public override bool HitTest(Point p, double tolerance)
        {
            var r = R;
            if (!string.IsNullOrEmpty(FillColor))
            {
                var inflated = r; inflated.Inflate(tolerance, tolerance);
                return inflated.Contains(p);
            }
            double tol = tolerance + Thickness / 2 + 2;
            var outer = r; outer.Inflate(tol, tol);
            var inner = r; inner.Inflate(-tol, -tol);
            return outer.Contains(p) && !(inner.Width > 0 && inner.Height > 0 && inner.Contains(p));
        }

        public override void ApplyTransform(Matrix m)
        {
            A = m.Transform(A);
            B = m.Transform(B);
            double s = ScaleMagnitude(m);
            Thickness = Math.Max(0.5, Thickness * s);
            CornerRadius *= s;
        }

        public override AnnItem Clone()
        {
            var c = new RectItem { A = A, B = B, FillColor = FillColor, CornerRadius = CornerRadius };
            CopyBaseTo(c);
            return c;
        }
    }

    // ------------------------------------------------------------------
    //  Ellipse
    // ------------------------------------------------------------------
    public sealed class EllipseItem : AnnItem
    {
        public Point A { get; set; }
        public Point B { get; set; }
        public string? FillColor { get; set; }

        [JsonIgnore] public Rect R => NormaliseRect(A, B);

        public override Rect Bounds
        {
            get { var r = R; r.Inflate(Thickness / 2 + 1, Thickness / 2 + 1); return r; }
        }

        public override void Render(DrawingContext dc, RenderCtx ctx)
        {
            var r = R;
            Brush? fill = null;
            if (!string.IsNullOrEmpty(FillColor))
            {
                fill = new SolidColorBrush(ColorUtil.Parse(FillColor));
                fill.Freeze();
            }
            dc.DrawEllipse(fill, MakePen(),
                new Point(r.X + r.Width / 2, r.Y + r.Height / 2), r.Width / 2, r.Height / 2);
        }

        public override bool HitTest(Point p, double tolerance)
        {
            var r = R;
            if (r.Width < 1 || r.Height < 1) return false;
            double cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
            double rx = r.Width / 2, ry = r.Height / 2;
            double tol = tolerance + Thickness / 2 + 2;

            double nOuter = Sq((p.X - cx) / (rx + tol)) + Sq((p.Y - cy) / (ry + tol));
            if (nOuter > 1) return false;
            if (!string.IsNullOrEmpty(FillColor)) return true;

            double irx = Math.Max(0.01, rx - tol), iry = Math.Max(0.01, ry - tol);
            double nInner = Sq((p.X - cx) / irx) + Sq((p.Y - cy) / iry);
            return nInner >= 1;
        }

        private static double Sq(double v) => v * v;

        public override void ApplyTransform(Matrix m)
        {
            A = m.Transform(A);
            B = m.Transform(B);
            Thickness = Math.Max(0.5, Thickness * ScaleMagnitude(m));
        }

        public override AnnItem Clone()
        {
            var c = new EllipseItem { A = A, B = B, FillColor = FillColor };
            CopyBaseTo(c);
            return c;
        }
    }

    // ------------------------------------------------------------------
    //  Text box. Arial only, by request — one font, any size.
    // ------------------------------------------------------------------
    public sealed class TextItem : AnnItem
    {
        public Point Origin { get; set; }
        public double MaxWidth { get; set; } = 320;
        public string Text { get; set; } = "";
        public double FontSize { get; set; } = 18;
        public bool Bold { get; set; }
        public bool Italic { get; set; }
        public string? BackgroundColor { get; set; }
        public bool ShowBorder { get; set; }
        public double Padding { get; set; } = 6;

        public const string FontName = "Arial";

        [JsonIgnore] public static double MinFontSize => 6;
        [JsonIgnore] public static double MaxFontSize => 400;

        private FormattedText Build(RenderCtx ctx, string? textOverride = null)
        {
            var typeface = new Typeface(
                new FontFamily(FontName),
                Italic ? FontStyles.Italic : FontStyles.Normal,
                Bold ? FontWeights.Bold : FontWeights.Normal,
                FontStretches.Normal);

            var brush = new SolidColorBrush(Ink);
            brush.Freeze();

            var ft = new FormattedText(
                textOverride ?? (string.IsNullOrEmpty(Text) ? " " : Text),
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                typeface,
                Math.Max(MinFontSize, FontSize),
                brush,
                Math.Max(0.5, ctx.PixelsPerDip));

            ft.MaxTextWidth = Math.Max(20, MaxWidth);
            ft.Trimming = TextTrimming.None;
            return ft;
        }

        /// <summary>Measured with a throwaway context, for hit-testing and bounds.</summary>
        public Size Measure(double pixelsPerDip = 1.0)
        {
            var ft = Build(new RenderCtx { PixelsPerDip = pixelsPerDip });
            return new Size(Math.Max(ft.Width, 20) + Padding * 2, ft.Height + Padding * 2);
        }

        public override Rect Bounds
        {
            get
            {
                var s = Measure();
                return new Rect(Origin.X, Origin.Y, s.Width, s.Height);
            }
        }

        public override void Render(DrawingContext dc, RenderCtx ctx)
        {
            var ft = Build(ctx);
            var box = new Rect(Origin.X, Origin.Y,
                Math.Max(ft.Width, 20) + Padding * 2, ft.Height + Padding * 2);

            if (!string.IsNullOrEmpty(BackgroundColor))
            {
                var bg = new SolidColorBrush(ColorUtil.Parse(BackgroundColor));
                bg.Freeze();
                dc.DrawRoundedRectangle(bg, null, box, 3, 3);
            }
            if (ShowBorder)
                dc.DrawRoundedRectangle(null, MakePen(Math.Max(1, Thickness * 0.6)), box, 3, 3);

            dc.DrawText(ft, new Point(Origin.X + Padding, Origin.Y + Padding));
        }

        public override bool HitTest(Point p, double tolerance)
        {
            var b = Bounds;
            b.Inflate(tolerance, tolerance);
            return b.Contains(p);
        }

        public override void ApplyTransform(Matrix m)
        {
            Origin = m.Transform(Origin);
            double s = ScaleMagnitude(m);
            FontSize = Math.Max(MinFontSize, Math.Min(MaxFontSize, FontSize * s));
            MaxWidth = Math.Max(20, MaxWidth * s);
            Padding *= s;
            Thickness = Math.Max(0.5, Thickness * s);
        }

        public override AnnItem Clone()
        {
            var c = new TextItem
            {
                Origin = Origin, MaxWidth = MaxWidth, Text = Text, FontSize = FontSize,
                Bold = Bold, Italic = Italic, BackgroundColor = BackgroundColor,
                ShowBorder = ShowBorder, Padding = Padding
            };
            CopyBaseTo(c);
            return c;
        }
    }

    // ------------------------------------------------------------------
    //  Numbered step markers: 1 2 3, A B C, i ii iii.
    //  The whole point of the tool — point at a thing, then talk about it by name.
    // ------------------------------------------------------------------
    public enum StepStyle { Number, UpperLetter, LowerLetter, LowerRoman, UpperRoman }

    public sealed class StepItem : AnnItem
    {
        public Point Center { get; set; }
        public double Radius { get; set; } = 18;
        public string Label { get; set; } = "1";
        public string? FillColor { get; set; } = "#E5342A";
        public string TextColor { get; set; } = "#FFFFFF";
        public bool Outlined { get; set; }

        public override Rect Bounds =>
            new Rect(Center.X - Radius - 2, Center.Y - Radius - 2, Radius * 2 + 4, Radius * 2 + 4);

        public override void Render(DrawingContext dc, RenderCtx ctx)
        {
            Brush fill = new SolidColorBrush(Outlined ? Colors.Transparent : ColorUtil.Parse(FillColor ?? StrokeColor));
            fill.Freeze();
            var pen = MakePen(Math.Max(1.5, Radius * 0.12));
            dc.DrawEllipse(fill, Outlined ? pen : null, Center, Radius, Radius);

            var brush = new SolidColorBrush(Outlined ? Ink : ColorUtil.Parse(TextColor));
            brush.Freeze();

            var ft = new FormattedText(
                Label,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily(TextItem.FontName), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                Radius * 1.25,
                brush,
                Math.Max(0.5, ctx.PixelsPerDip));

            dc.DrawText(ft, new Point(Center.X - ft.Width / 2, Center.Y - ft.Height / 2));
        }

        public override bool HitTest(Point p, double tolerance) =>
            (p - Center).Length <= Radius + tolerance;

        public override void ApplyTransform(Matrix m)
        {
            Center = m.Transform(Center);
            Radius = Math.Max(5, Radius * ScaleMagnitude(m));
        }

        public override AnnItem Clone()
        {
            var c = new StepItem
            {
                Center = Center, Radius = Radius, Label = Label,
                FillColor = FillColor, TextColor = TextColor, Outlined = Outlined
            };
            CopyBaseTo(c);
            return c;
        }

        public static string LabelFor(StepStyle style, int index)
        {
            return style switch
            {
                StepStyle.Number => index.ToString(CultureInfo.InvariantCulture),
                StepStyle.UpperLetter => Alpha(index).ToUpperInvariant(),
                StepStyle.LowerLetter => Alpha(index),
                StepStyle.LowerRoman => Roman(index).ToLowerInvariant(),
                StepStyle.UpperRoman => Roman(index),
                _ => index.ToString(CultureInfo.InvariantCulture)
            };
        }

        private static string Alpha(int n)
        {
            // 1 -> a, 26 -> z, 27 -> aa
            var sb = new System.Text.StringBuilder();
            while (n > 0)
            {
                n--;
                sb.Insert(0, (char)('a' + (n % 26)));
                n /= 26;
            }
            return sb.Length == 0 ? "a" : sb.ToString();
        }

        private static string Roman(int n)
        {
            if (n <= 0) return "i";
            int[] values = { 1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1 };
            string[] sym = { "M", "CM", "D", "CD", "C", "XC", "L", "XL", "X", "IX", "V", "IV", "I" };
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < values.Length; i++)
                while (n >= values[i]) { sb.Append(sym[i]); n -= values[i]; }
            return sb.ToString();
        }
    }

    // ------------------------------------------------------------------
    //  Redaction block: mosaics whatever is underneath it.
    // ------------------------------------------------------------------
    public sealed class RedactItem : AnnItem
    {
        public Point A { get; set; }
        public Point B { get; set; }
        public int BlockSize { get; set; } = 12;
        public bool Solid { get; set; }

        [JsonIgnore] public Rect R => NormaliseRect(A, B);

        [JsonIgnore] private BitmapSource? _cache;
        [JsonIgnore] private string _cacheKey = "";

        public override Rect Bounds => R;

        public override void Render(DrawingContext dc, RenderCtx ctx)
        {
            var r = R;
            if (r.Width < 1 || r.Height < 1) return;

            if (Solid || ctx.Source == null || ctx.ImageRect.Width < 1)
            {
                var b = new SolidColorBrush(Colors.Black);
                b.Freeze();
                dc.DrawRectangle(b, null, r);
                return;
            }

            var key = $"{r.X:0.##},{r.Y:0.##},{r.Width:0.##},{r.Height:0.##},{BlockSize},{ctx.ImageRect}";
            if (_cache == null || _cacheKey != key)
            {
                _cache = BuildMosaic(r, ctx);
                _cacheKey = key;
            }

            if (_cache != null) dc.DrawImage(_cache, r);
            else dc.DrawRectangle(Brushes.Black, null, r);
        }

        private BitmapSource? BuildMosaic(Rect r, RenderCtx ctx)
        {
            try
            {
                var src = ctx.Source!;
                double scaleX = src.PixelWidth / ctx.ImageRect.Width;
                double scaleY = src.PixelHeight / ctx.ImageRect.Height;

                int px = (int)Math.Round((r.X - ctx.ImageRect.X) * scaleX);
                int py = (int)Math.Round((r.Y - ctx.ImageRect.Y) * scaleY);
                int pw = (int)Math.Round(r.Width * scaleX);
                int ph = (int)Math.Round(r.Height * scaleY);

                if (px < 0) { pw += px; px = 0; }
                if (py < 0) { ph += py; py = 0; }
                if (px + pw > src.PixelWidth) pw = src.PixelWidth - px;
                if (py + ph > src.PixelHeight) ph = src.PixelHeight - py;
                if (pw < 1 || ph < 1) return null;

                var crop = new CroppedBitmap(src, new Int32Rect(px, py, pw, ph));
                var conv = new FormatConvertedBitmap(crop, PixelFormats.Bgra32, null, 0);

                int stride = pw * 4;
                var pixels = new byte[stride * ph];
                conv.CopyPixels(pixels, stride, 0);

                int block = Math.Max(2, (int)Math.Round(BlockSize * scaleX));

                for (int by = 0; by < ph; by += block)
                {
                    for (int bx = 0; bx < pw; bx += block)
                    {
                        int w = Math.Min(block, pw - bx);
                        int h = Math.Min(block, ph - by);
                        long sb = 0, sg = 0, sr = 0, sa = 0;
                        int n = 0;
                        for (int y = by; y < by + h; y++)
                        {
                            int rowStart = y * stride;
                            for (int x = bx; x < bx + w; x++)
                            {
                                int o = rowStart + x * 4;
                                sb += pixels[o]; sg += pixels[o + 1]; sr += pixels[o + 2]; sa += pixels[o + 3];
                                n++;
                            }
                        }
                        if (n == 0) continue;
                        byte ab = (byte)(sb / n), ag = (byte)(sg / n), ar = (byte)(sr / n), aa = (byte)(sa / n);
                        for (int y = by; y < by + h; y++)
                        {
                            int rowStart = y * stride;
                            for (int x = bx; x < bx + w; x++)
                            {
                                int o = rowStart + x * 4;
                                pixels[o] = ab; pixels[o + 1] = ag; pixels[o + 2] = ar; pixels[o + 3] = aa;
                            }
                        }
                    }
                }

                var wb = new WriteableBitmap(pw, ph, 96, 96, PixelFormats.Bgra32, null);
                wb.WritePixels(new Int32Rect(0, 0, pw, ph), pixels, stride, 0);
                wb.Freeze();
                return wb;
            }
            catch { return null; }
        }

        public override bool HitTest(Point p, double tolerance)
        {
            var r = R; r.Inflate(tolerance, tolerance);
            return r.Contains(p);
        }

        public override void ApplyTransform(Matrix m)
        {
            A = m.Transform(A);
            B = m.Transform(B);
            _cache = null;
        }

        public override AnnItem Clone()
        {
            var c = new RedactItem { A = A, B = B, BlockSize = BlockSize, Solid = Solid };
            CopyBaseTo(c);
            return c;
        }
    }

    // ------------------------------------------------------------------
    //  Group: several shapes that move and scale as one.
    // ------------------------------------------------------------------
    public sealed class GroupItem : AnnItem
    {
        public List<AnnItem> Children { get; set; } = new();

        public override Rect Bounds
        {
            get
            {
                Rect r = Rect.Empty;
                foreach (var c in Children)
                {
                    var b = c.Bounds;
                    if (b.IsEmpty) continue;
                    r = r.IsEmpty ? b : Rect.Union(r, b);
                }
                return r;
            }
        }

        public override void Render(DrawingContext dc, RenderCtx ctx)
        {
            foreach (var c in Children) c.Render(dc, ctx);
        }

        public override bool HitTest(Point p, double tolerance)
        {
            foreach (var c in Children)
                if (c.HitTest(p, tolerance)) return true;

            // Clicking inside the group's box also grabs it, which is what
            // people expect once several things have been bound together.
            var b = Bounds;
            if (!b.IsEmpty)
            {
                b.Inflate(tolerance, tolerance);
                return b.Contains(p);
            }
            return false;
        }

        public override void ApplyTransform(Matrix m)
        {
            foreach (var c in Children) c.ApplyTransform(m);
        }

        public override AnnItem Clone()
        {
            var c = new GroupItem();
            foreach (var child in Children) c.Children.Add(child.Clone());
            CopyBaseTo(c);
            return c;
        }

        /// <summary>Flatten one level, preserving paint order.</summary>
        public IEnumerable<AnnItem> Flatten()
        {
            foreach (var c in Children)
            {
                if (c is GroupItem g)
                    foreach (var inner in g.Flatten()) yield return inner;
                else
                    yield return c;
            }
        }
    }
}
