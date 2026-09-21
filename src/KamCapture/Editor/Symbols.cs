using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;

namespace KamCapture.Editor
{
    public sealed class SymbolDef
    {
        public string Name { get; init; } = "";
        public string Label { get; init; } = "";
        public string Group { get; init; } = "";
        public bool StrokeOnly { get; init; }
        public Geometry Geometry { get; init; } = Geometry.Empty;
    }

    /// <summary>
    /// Ready-made marks to stamp onto a capture. Every glyph is authored in a
    /// 100 x 100 box and scaled to whatever rectangle it is dropped into, so it
    /// stays sharp at any size — these are vectors, not clipart bitmaps.
    /// </summary>
    public static class SymbolCatalog
    {
        private static readonly List<SymbolDef> _all = Build();
        public static IReadOnlyList<SymbolDef> All => _all;

        public static SymbolDef Get(string name) =>
            _all.FirstOrDefault(s => s.Name == name) ?? _all[0];

        private static SymbolDef Path(string name, string label, string group, string data, bool strokeOnly = false)
        {
            var geo = Geometry.Parse(data);
            geo.Freeze();
            return new SymbolDef { Name = name, Label = label, Group = group, Geometry = geo, StrokeOnly = strokeOnly };
        }

        private static SymbolDef Glyph(string name, string label, string group, string text)
        {
            // Build the outline from Arial itself rather than hand-tracing it.
            var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Arial"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                100, Brushes.Black, 1.0);

            var geo = ft.BuildGeometry(new Point(0, 0));
            var b = geo.Bounds;

            var group2 = new TransformGroup();
            group2.Children.Add(new TranslateTransform(-b.X, -b.Y));
            double s = 100.0 / Math.Max(b.Width, b.Height);
            group2.Children.Add(new ScaleTransform(s, s));
            group2.Children.Add(new TranslateTransform(
                (100 - b.Width * s) / 2, (100 - b.Height * s) / 2));

            var shaped = Geometry.Combine(geo, Geometry.Empty, GeometryCombineMode.Union, group2);
            shaped.Freeze();
            return new SymbolDef { Name = name, Label = label, Group = group, Geometry = shaped };
        }

        private static List<SymbolDef> Build() => new()
        {
            // ---- arrows: the thing you reach for most ----
            Path("arrow-right",  "Arrow right",  "Arrows", "M 5,38 L 58,38 L 58,16 L 96,50 L 58,84 L 58,62 L 5,62 Z"),
            Path("arrow-left",   "Arrow left",   "Arrows", "M 95,38 L 42,38 L 42,16 L 4,50 L 42,84 L 42,62 L 95,62 Z"),
            Path("arrow-up",     "Arrow up",     "Arrows", "M 38,95 L 38,42 L 16,42 L 50,4 L 84,42 L 62,42 L 62,95 Z"),
            Path("arrow-down",   "Arrow down",   "Arrows", "M 38,5 L 38,58 L 16,58 L 50,96 L 84,58 L 62,58 L 62,5 Z"),
            Path("arrow-diag",   "Arrow diagonal","Arrows","M 95,5 L 52,9 L 66,23 L 9,80 L 20,91 L 77,34 L 91,48 Z"),
            Path("arrow-curved", "Curved arrow", "Arrows", "M 10,88 C 10,42 44,20 78,20 M 78,20 L 60,6 M 78,20 L 60,34", true),
            Path("chevron-right","Chevron right","Arrows", "M 32,10 L 72,50 L 32,90", true),
            Path("chevron-left", "Chevron left", "Arrows", "M 68,10 L 28,50 L 68,90", true),

            // ---- rings and boxes: circle the thing you mean ----
            Path("circle",       "Circle",       "Shapes", "M 8,50 A 42,42 0 1 0 92,50 A 42,42 0 1 0 8,50 Z", true),
            Path("circle-solid", "Filled circle","Shapes", "M 8,50 A 42,42 0 1 0 92,50 A 42,42 0 1 0 8,50 Z"),
            Path("square",       "Square",       "Shapes", "M 10,10 L 90,10 L 90,90 L 10,90 Z", true),
            Path("rounded",      "Rounded box",  "Shapes", "M 24,12 L 76,12 A 12,12 0 0 1 88,24 L 88,76 A 12,12 0 0 1 76,88 L 24,88 A 12,12 0 0 1 12,76 L 12,24 A 12,12 0 0 1 24,12 Z", true),
            Path("triangle",     "Triangle",     "Shapes", "M 50,8 L 94,88 L 6,88 Z", true),
            Path("star",         "Star",         "Shapes", "M 50,4 L 61.2,34.6 L 93.8,35.8 L 68.1,55.9 L 77,87.2 L 50,69 L 23,87.2 L 31.9,55.9 L 6.2,35.8 L 38.8,34.6 Z"),
            Path("target",       "Target",       "Shapes", "M 14,50 A 36,36 0 1 0 86,50 A 36,36 0 1 0 14,50 M 50,4 L 50,26 M 50,74 L 50,96 M 4,50 L 26,50 M 74,50 L 96,50", true),

            // ---- marks: yes, no, look here ----
            Path("check",        "Tick",         "Marks", "M 10,52 L 36,80 L 90,18", true),
            Path("cross",        "Cross",        "Marks", "M 16,16 L 84,84 M 84,16 L 16,84", true),
            Path("plus",         "Plus",         "Marks", "M 50,10 L 50,90 M 10,50 L 90,50", true),
            Path("minus",        "Minus",        "Marks", "M 10,50 L 90,50", true),
            Path("underline",    "Underline",    "Marks", "M 4,62 L 96,62", true),
            Glyph("question",    "Question",     "Marks", "?"),
            Glyph("bang",        "Exclamation",  "Marks", "!"),

            // ---- pointing and grouping ----
            Path("cursor",       "Pointer",      "Pointing", "M 20,6 L 20,78 L 38,60 L 50,90 L 63,84 L 51,56 L 76,54 Z"),
            Path("pin",          "Pin",          "Pointing", "F0 M 50,96 C 50,96 16,58 16,38 A 34,34 0 1 1 84,38 C 84,58 50,96 50,96 Z M 50,22 A 14,14 0 1 0 50.01,22 Z"),
            Path("callout",      "Callout",      "Pointing", "M 10,8 L 90,8 L 90,64 L 48,64 L 26,92 L 30,64 L 10,64 Z", true),
            Path("magnifier",    "Magnifier",    "Pointing", "M 10,40 A 30,30 0 1 0 70,40 A 30,30 0 1 0 10,40 M 62,62 L 92,92", true),
            Path("warning",      "Warning",      "Pointing", "F0 M 50,6 L 97,90 L 3,90 Z M 44,32 L 56,32 L 54,62 L 46,62 Z M 43,70 L 57,70 L 57,83 L 43,83 Z"),

            Path("bracket-left", "Bracket left", "Spans", "M 74,5 L 28,5 L 28,95 L 74,95", true),
            Path("bracket-right","Bracket right","Spans", "M 26,5 L 72,5 L 72,95 L 26,95", true),
            Path("brace",        "Brace",        "Spans", "M 72,4 C 50,4 56,42 32,50 C 56,58 50,96 72,96", true),
        };
    }

    /// <summary>A stamped symbol: any catalogue glyph, at any size and angle.</summary>
    public sealed class SymbolItem : AnnItem
    {
        public Point A { get; set; }
        public Point B { get; set; }
        public string Symbol { get; set; } = "arrow-right";
        public bool Filled { get; set; } = true;
        public double Rotation { get; set; }

        [JsonIgnore] public Rect R => new(Math.Min(A.X, B.X), Math.Min(A.Y, B.Y),
                                          Math.Abs(A.X - B.X), Math.Abs(A.Y - B.Y));

        [JsonIgnore] private SymbolDef Def => SymbolCatalog.Get(Symbol);

        /// <summary>Maps the 100x100 authoring box onto this item's rectangle.</summary>
        private Matrix PlaceMatrix()
        {
            var r = R;
            var m = Matrix.Identity;
            m.Scale(Math.Max(0.0001, r.Width / 100.0), Math.Max(0.0001, r.Height / 100.0));
            m.Translate(r.X, r.Y);
            if (Math.Abs(Rotation) > 0.01)
                m.RotateAt(Rotation, r.X + r.Width / 2, r.Y + r.Height / 2);
            return m;
        }

        private double StrokeWidth()
        {
            var r = R;
            return Math.Max(0.6, Thickness * Math.Max(0.25, Math.Min(r.Width, r.Height) / 80.0));
        }

        public override Rect Bounds
        {
            get
            {
                var r = R;
                if (r.Width < 0.01 || r.Height < 0.01) return r;
                double pad = StrokeWidth();

                if (Math.Abs(Rotation) < 0.01)
                {
                    var b = r; b.Inflate(pad, pad); return b;
                }

                var m = Matrix.Identity;
                m.RotateAt(Rotation, r.X + r.Width / 2, r.Y + r.Height / 2);
                var pts = new[]
                {
                    m.Transform(new Point(r.Left, r.Top)),
                    m.Transform(new Point(r.Right, r.Top)),
                    m.Transform(new Point(r.Right, r.Bottom)),
                    m.Transform(new Point(r.Left, r.Bottom)),
                };
                double minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X);
                double minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
                return new Rect(minX - pad, minY - pad, maxX - minX + pad * 2, maxY - minY + pad * 2);
            }
        }

        private Geometry Placed()
        {
            var geo = Def.Geometry.Clone();
            geo.Transform = new MatrixTransform(PlaceMatrix());
            return geo;
        }

        public override void Render(DrawingContext dc, RenderCtx ctx)
        {
            var r = R;
            if (r.Width < 0.5 || r.Height < 0.5) return;

            var def = Def;
            var geo = Placed();

            var brush = new SolidColorBrush(Ink);
            brush.Freeze();

            if (def.StrokeOnly || !Filled)
            {
                var pen = new Pen(brush, StrokeWidth())
                {
                    LineJoin = PenLineJoin.Round,
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round
                };
                dc.DrawGeometry(null, pen, geo);
            }
            else
            {
                dc.DrawGeometry(brush, null, geo);
            }
        }

        public override bool HitTest(Point p, double tolerance)
        {
            var r = R;
            if (r.Width < 0.5 || r.Height < 0.5) return false;

            var def = Def;
            var geo = Placed();
            double w = StrokeWidth() + tolerance * 2;

            try
            {
                if (!def.StrokeOnly && Filled && geo.FillContains(p, tolerance, ToleranceType.Absolute))
                    return true;
                if (geo.StrokeContains(new Pen(Brushes.Black, w), p, tolerance, ToleranceType.Absolute))
                    return true;
            }
            catch { /* degenerate geometry */ }

            // Fall back to the box so a thin glyph is still easy to grab.
            var b = Bounds;
            b.Inflate(tolerance, tolerance);
            return b.Contains(p);
        }

        public override void ApplyTransform(Matrix m)
        {
            A = m.Transform(A);
            B = m.Transform(B);
            // Stroke weight follows the box size, so it must not be scaled again.
        }

        public override AnnItem Clone()
        {
            var c = new SymbolItem { A = A, B = B, Symbol = Symbol, Filled = Filled, Rotation = Rotation };
            CopyBaseTo(c);
            return c;
        }
    }
}
