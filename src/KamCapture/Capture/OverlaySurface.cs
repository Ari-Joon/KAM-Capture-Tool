using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using KamCapture.Editor;
using KamCapture.Settings;

namespace KamCapture.Capture
{
    /// <summary>
    /// One monitor's worth of selection overlay. Everything is drawn in
    /// virtual-desktop physical pixels and mapped down to this monitor's DIPs,
    /// so a selection dragged across displays of different scaling stays
    /// exactly under the pointer.
    /// </summary>
    internal sealed class OverlaySurface : FrameworkElement
    {
        private readonly MonitorInfo _mon;
        private readonly OverlayState _s;
        private readonly Window _host;

        // WPF's own DPI for this visual is authoritative: it is the scale the
        // window is actually rendered at. Anything else risks the selection
        // drifting away from the pointer.
        private double U
        {
            get
            {
                double s = VisualTreeHelper.GetDpi(this).DpiScaleX;
                return s > 0.05 ? s : Math.Max(0.05, _mon.Scale);
            }
        }

        private double Ppd => VisualTreeHelper.GetDpi(this).PixelsPerDip;

        private readonly List<(Rect Rect, CaptureAction Action, string Label)> _bar = new();

        public OverlaySurface(MonitorInfo monitor, OverlayState state, Window host)
        {
            _mon = monitor;
            _s = state;
            _host = host;
            Focusable = true;
            ClipToBounds = true;
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        }

        // ---------------- coordinate mapping ----------------

        private Point ToGlobal(Point local) =>
            new Point(local.X * U + _mon.X, local.Y * U + _mon.Y);

        private Matrix GlobalToLocal()
        {
            var m = Matrix.Identity;
            m.Translate(-_mon.X, -_mon.Y);
            m.Scale(1.0 / U, 1.0 / U);
            return m;
        }

        // ---------------- input ----------------

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            Focus();
            var g = ToGlobal(e.GetPosition(this));

            if (e.ClickCount == 2 && _s.Selection.Width >= 1)
            {
                _s.Commit(CaptureAction.Edit);
                return;
            }

            if (_s.Settled)
            {
                foreach (var (rect, action, _) in _bar)
                {
                    if (rect.Contains(g)) { _s.Commit(action); return; }
                }

                int handle = HitHandle(g);
                if (handle >= 0)
                {
                    _s.ActiveHandle = handle;
                    _s.MoveAnchor = g;
                    _s.MoveOriginal = _s.Selection;
                    CaptureMouse();
                    return;
                }

                if (_s.Selection.Contains(g))
                {
                    _s.ActiveHandle = 8;
                    _s.MoveAnchor = g;
                    _s.MoveOriginal = _s.Selection;
                    CaptureMouse();
                    return;
                }

                // Clicking outside a settled selection starts a fresh one.
                _s.Settled = false;
                _s.Freeform.Clear();
            }

            if (_s.Mode == SnipMode.Window)
            {
                if (_s.HoverWindow != null)
                {
                    _s.Selection = new Rect(_s.HoverWindow.X, _s.HoverWindow.Y,
                                            _s.HoverWindow.Width, _s.HoverWindow.Height);
                    _s.Settled = true;
                    _s.Invalidate();
                }
                return;
            }

            if (_s.Mode == SnipMode.Monitor)
            {
                _s.Selection = new Rect(_mon.X, _mon.Y, _mon.Width, _mon.Height);
                _s.Settled = true;
                _s.Invalidate();
                return;
            }

            _s.Dragging = true;
            _s.DragStart = g;
            _s.DragCurrent = g;
            _s.Freeform.Clear();
            if (_s.Mode == SnipMode.Freeform) _s.Freeform.Add(g);
            _s.Selection = Rect.Empty;
            CaptureMouse();
            _s.Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var g = ToGlobal(e.GetPosition(this));
            _s.Cursor = g;
            _s.CursorKnown = true;

            if (_s.ActiveHandle >= 0 && e.LeftButton == MouseButtonState.Pressed)
            {
                ResizeFromHandle(g);
                _s.Invalidate();
                return;
            }

            if (_s.Dragging)
            {
                _s.DragCurrent = g;

                if (_s.Mode == SnipMode.Freeform)
                {
                    var last = _s.Freeform.Count > 0 ? _s.Freeform[^1] : g;
                    if ((g - last).Length >= 2) _s.Freeform.Add(g);
                    _s.Selection = BoundsOf(_s.Freeform);
                }
                else
                {
                    _s.Selection = Normalise(_s.DragStart, _s.DragCurrent);
                }
            }
            else if (_s.Mode == SnipMode.Window && !_s.Settled)
            {
                var hit = WindowFinder.FromPoint(_s.Windows, (int)g.X, (int)g.Y);
                if (!ReferenceEquals(hit, _s.HoverWindow))
                {
                    _s.HoverWindow = hit;
                    _s.Selection = hit == null ? Rect.Empty
                        : new Rect(hit.X, hit.Y, hit.Width, hit.Height);
                }
            }

            _s.Invalidate();
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (IsMouseCaptured) ReleaseMouseCapture();

            if (_s.ActiveHandle >= 0)
            {
                _s.ActiveHandle = -1;
                _s.Invalidate();
                return;
            }

            if (_s.Dragging)
            {
                _s.Dragging = false;
                if (_s.Selection.Width >= 2 && _s.Selection.Height >= 2)
                    _s.Settled = true;
                else
                    _s.Selection = Rect.Empty;
                _s.Invalidate();
            }
        }

        protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
        {
            if (_s.Settled || _s.Dragging)
            {
                _s.Settled = false;
                _s.Dragging = false;
                _s.Selection = Rect.Empty;
                _s.Freeform.Clear();
                if (IsMouseCaptured) ReleaseMouseCapture();
                _s.Invalidate();
            }
            else _s.Commit(CaptureAction.Cancel);
        }

        private static Rect Normalise(Point a, Point b) =>
            new Rect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
                     Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

        private static Rect BoundsOf(List<Point> pts)
        {
            if (pts.Count == 0) return Rect.Empty;
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var p in pts)
            {
                minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
            }
            return new Rect(minX, minY, maxX - minX, maxY - minY);
        }

        // ---------------- handles ----------------

        private Point[] HandlePoints(Rect r) => new[]
        {
            new Point(r.Left,  r.Top),
            new Point(r.Left + r.Width / 2, r.Top),
            new Point(r.Right, r.Top),
            new Point(r.Right, r.Top + r.Height / 2),
            new Point(r.Right, r.Bottom),
            new Point(r.Left + r.Width / 2, r.Bottom),
            new Point(r.Left,  r.Bottom),
            new Point(r.Left,  r.Top + r.Height / 2),
        };

        private int HitHandle(Point g)
        {
            if (_s.Selection.IsEmpty) return -1;
            double tol = 9 * U;
            var pts = HandlePoints(_s.Selection);
            for (int i = 0; i < pts.Length; i++)
                if (Math.Abs(g.X - pts[i].X) <= tol && Math.Abs(g.Y - pts[i].Y) <= tol) return i;
            return -1;
        }

        private void ResizeFromHandle(Point g)
        {
            var o = _s.MoveOriginal;
            double l = o.Left, t = o.Top, r = o.Right, b = o.Bottom;

            switch (_s.ActiveHandle)
            {
                case 0: l = g.X; t = g.Y; break;
                case 1: t = g.Y; break;
                case 2: r = g.X; t = g.Y; break;
                case 3: r = g.X; break;
                case 4: r = g.X; b = g.Y; break;
                case 5: b = g.Y; break;
                case 6: l = g.X; b = g.Y; break;
                case 7: l = g.X; break;
                case 8:
                    var d = g - _s.MoveAnchor;
                    l += d.X; r += d.X; t += d.Y; b += d.Y;
                    break;
            }

            _s.Selection = new Rect(Math.Min(l, r), Math.Min(t, b),
                                    Math.Max(1, Math.Abs(r - l)), Math.Max(1, Math.Abs(b - t)));
        }

        // ---------------- rendering ----------------

        protected override void OnRender(DrawingContext dc)
        {
            var cfg = _s.Cfg;
            var full = new Rect(0, 0, ActualWidth, ActualHeight);
            dc.DrawRectangle(Brushes.Black, null, full);   // guarantees a hit-testable surface

            dc.PushTransform(new MatrixTransform(GlobalToLocal()));

            var desktop = new Rect(_s.Snap.OriginX, _s.Snap.OriginY, _s.Snap.Width, _s.Snap.Height);
            dc.DrawImage(_s.Snap.Image, desktop);

            var dimColor = ColorUtil.Parse(cfg.DimColor);
            dimColor.A = (byte)Math.Clamp(cfg.DimOpacity * 255, 0, 255);
            var dim = new SolidColorBrush(dimColor);
            dim.Freeze();

            var sel = _s.Selection;
            bool hasSel = sel.Width >= 1 && sel.Height >= 1;

            if (hasSel)
            {
                Geometry hole = _s.Mode == SnipMode.Freeform && _s.Freeform.Count > 2
                    ? PolyGeometry(_s.Freeform)
                    : new RectangleGeometry(sel);

                var mask = new CombinedGeometry(GeometryCombineMode.Exclude,
                    new RectangleGeometry(desktop), hole);
                mask.Freeze();
                dc.DrawGeometry(dim, null, mask);

                DrawSelectionChrome(dc, sel, hole);
            }
            else
            {
                dc.DrawRectangle(dim, null, desktop);
            }

            if (cfg.ShowCrosshair && _s.CursorKnown && !_s.Settled && _mon.Contains((int)_s.Cursor.X, (int)_s.Cursor.Y))
                DrawCrosshair(dc, desktop);

            if (!hasSel && !_s.Settled)
                DrawHint(dc);

            if (cfg.ShowMagnifier && _s.CursorKnown && !_s.Settled &&
                _mon.Contains((int)_s.Cursor.X, (int)_s.Cursor.Y))
                DrawMagnifier(dc);

            if (_s.Settled && hasSel)
                DrawActionBar(dc, sel);
            else
                _bar.Clear();

            dc.Pop();
        }

        private static Geometry PolyGeometry(List<Point> pts)
        {
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(pts[0], true, true);
                for (int i = 1; i < pts.Count; i++) g.LineTo(pts[i], true, false);
            }
            geo.Freeze();
            return geo;
        }

        private Pen BorderPen()
        {
            var cfg = _s.Cfg;
            var pen = new Pen(new SolidColorBrush(ColorUtil.Parse(cfg.BorderColor)),
                              Math.Max(0.5, cfg.BorderThickness) * U);
            pen.Brush.Freeze();
            switch (cfg.BorderStyle)
            {
                case BorderStyleKind.Dashed:
                    pen.DashStyle = new DashStyle(new double[] { 5, 3 }, 0); break;
                case BorderStyleKind.Dotted:
                    pen.DashStyle = new DashStyle(new double[] { 1, 2 }, 0);
                    pen.DashCap = PenLineCap.Round; break;
            }
            return pen;
        }

        private void DrawSelectionChrome(DrawingContext dc, Rect sel, Geometry outline)
        {
            var cfg = _s.Cfg;
            var pen = BorderPen();

            if (cfg.BorderStyle == BorderStyleKind.Glow)
            {
                var glow = ColorUtil.Parse(cfg.BorderColor);
                for (int i = 4; i >= 1; i--)
                {
                    var c = glow; c.A = (byte)(26 * (5 - i) / 2);
                    var gp = new Pen(new SolidColorBrush(c), (cfg.BorderThickness + i * 2.2) * U);
                    gp.Brush.Freeze();
                    dc.DrawGeometry(null, gp, outline);
                }
            }

            dc.DrawGeometry(null, pen, outline);

            if (cfg.ShowRuleOfThirds && sel.Width > 30 && sel.Height > 30)
            {
                var thin = new Pen(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), 1 * U);
                thin.Brush.Freeze();
                for (int i = 1; i <= 2; i++)
                {
                    double x = sel.Left + sel.Width * i / 3.0;
                    double y = sel.Top + sel.Height * i / 3.0;
                    dc.DrawLine(thin, new Point(x, sel.Top), new Point(x, sel.Bottom));
                    dc.DrawLine(thin, new Point(sel.Left, y), new Point(sel.Right, y));
                }
            }

            if (_s.Settled)
            {
                var handleFill = new SolidColorBrush(ColorUtil.Parse(cfg.HandleColor));
                handleFill.Freeze();
                var handleEdge = new Pen(new SolidColorBrush(ColorUtil.Parse(cfg.BorderColor)), 1.4 * U);
                handleEdge.Brush.Freeze();
                double hs = 4.5 * U;
                foreach (var p in HandlePoints(sel))
                    dc.DrawRectangle(handleFill, handleEdge,
                        new Rect(p.X - hs, p.Y - hs, hs * 2, hs * 2));
            }

            if (cfg.ShowDimensions)
                DrawSizeBadge(dc, sel);
        }

        private void DrawSizeBadge(DrawingContext dc, Rect sel)
        {
            string text = $"{Math.Round(sel.Width)} × {Math.Round(sel.Height)} px";
            var ft = Text(text, 12 * U, Brushes.White, true);

            double padX = 7 * U, padY = 4 * U;
            double w = ft.Width + padX * 2, h = ft.Height + padY * 2;

            double x = sel.Left;
            double y = sel.Top - h - 6 * U;
            if (y < _mon.Y + 2) y = sel.Top + 6 * U;
            if (x + w > _mon.X + _mon.Width) x = _mon.X + _mon.Width - w;
            if (x < _mon.X) x = _mon.X;

            var bg = new SolidColorBrush(Color.FromArgb(225, 12, 12, 14));
            bg.Freeze();
            var edge = new Pen(new SolidColorBrush(ColorUtil.Parse(_s.Cfg.BorderColor)), 1 * U);
            edge.Brush.Freeze();
            dc.DrawRoundedRectangle(bg, edge, new Rect(x, y, w, h), 4 * U, 4 * U);
            dc.DrawText(ft, new Point(x + padX, y + padY));
        }

        private void DrawCrosshair(DrawingContext dc, Rect desktop)
        {
            var c = ColorUtil.Parse(_s.Cfg.BorderColor);
            c.A = 150;
            var pen = new Pen(new SolidColorBrush(c), 1 * U) { DashStyle = new DashStyle(new double[] { 4, 4 }, 0) };
            pen.Brush.Freeze();
            dc.DrawLine(pen, new Point(desktop.Left, _s.Cursor.Y), new Point(desktop.Right, _s.Cursor.Y));
            dc.DrawLine(pen, new Point(_s.Cursor.X, desktop.Top), new Point(_s.Cursor.X, desktop.Bottom));
        }

        private void DrawHint(DrawingContext dc)
        {
            if (!_mon.IsPrimary && !_mon.Contains((int)_s.Cursor.X, (int)_s.Cursor.Y)) return;

            string mode = _s.Mode switch
            {
                SnipMode.Window => "Window",
                SnipMode.Freeform => "Freeform",
                SnipMode.Monitor => "Monitor",
                _ => "Region"
            };
            string text = _s.Mode == SnipMode.Window
                ? "Click a window to capture it     C capture  ·  M magnifier  ·  Esc cancel"
                : "Drag to select     Ctrl+A whole screen  ·  W window  ·  M magnifier  ·  Esc cancel";

            var title = Text($"KAM Capture — {mode}", 15 * U, Brushes.White, true);
            var body = Text(text, 12.5 * U, new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x94)), false);

            double padX = 18 * U, padY = 13 * U, gap = 5 * U;
            double w = Math.Max(title.Width, body.Width) + padX * 2;
            double h = title.Height + body.Height + gap + padY * 2;
            double x = _mon.X + (_mon.Width - w) / 2;
            double y = _mon.Y + _mon.Height * 0.08;

            var bg = new SolidColorBrush(Color.FromArgb(232, 12, 12, 14));
            bg.Freeze();
            var edge = new Pen(new SolidColorBrush(ColorUtil.Parse(_s.Cfg.BorderColor)), 1 * U);
            edge.Brush.Freeze();
            dc.DrawRoundedRectangle(bg, edge, new Rect(x, y, w, h), 8 * U, 8 * U);
            dc.DrawText(title, new Point(x + padX, y + padY));
            dc.DrawText(body, new Point(x + padX, y + padY + title.Height + gap));
        }

        private void DrawMagnifier(DrawingContext dc)
        {
            const int srcPx = 17;          // source pixels across the loupe
            double zoom = 9 * U;
            double size = srcPx * zoom;

            double ox = _s.Cursor.X + 22 * U;
            double oy = _s.Cursor.Y + 22 * U;
            if (ox + size > _mon.X + _mon.Width) ox = _s.Cursor.X - size - 22 * U;
            if (oy + size > _mon.Y + _mon.Height) oy = _s.Cursor.Y - size - 22 * U;

            var box = new Rect(ox, oy, size, size);
            var clip = new RectangleGeometry(box, 6 * U, 6 * U);
            clip.Freeze();

            dc.PushClip(clip);
            double half = srcPx / 2.0;
            var src = new Rect(
                _s.Cursor.X - half * 1, _s.Cursor.Y - half * 1, srcPx, srcPx);
            var dest = new Rect(ox, oy, size, size);

            // Draw the frozen desktop scaled up around the cursor.
            var scale = size / srcPx;
            dc.PushTransform(new TranslateTransform(dest.X - src.X * scale, dest.Y - src.Y * scale));
            dc.PushTransform(new ScaleTransform(scale, scale));
            var desktop = new Rect(_s.Snap.OriginX, _s.Snap.OriginY, _s.Snap.Width, _s.Snap.Height);
            dc.DrawImage(_s.Snap.Image, desktop);
            dc.Pop();
            dc.Pop();
            dc.Pop();

            var acc = ColorUtil.Parse(_s.Cfg.BorderColor);
            var pen = new Pen(new SolidColorBrush(acc), 1.5 * U);
            pen.Brush.Freeze();
            dc.DrawRoundedRectangle(null, pen, box, 6 * U, 6 * U);

            // Centre cell marks the exact pixel under the pointer.
            double cell = zoom;
            var centre = new Rect(ox + size / 2 - cell / 2, oy + size / 2 - cell / 2, cell, cell);
            var cp = new Pen(new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)), 1.2 * U);
            cp.Brush.Freeze();
            dc.DrawRectangle(null, cp, centre);

            var label = Text($"{(int)_s.Cursor.X}, {(int)_s.Cursor.Y}", 11 * U, Brushes.White, true);
            var lb = new Rect(ox, oy + size + 4 * U, Math.Max(label.Width + 10 * U, size), label.Height + 6 * U);
            var bg = new SolidColorBrush(Color.FromArgb(225, 12, 12, 14));
            bg.Freeze();
            dc.DrawRoundedRectangle(bg, null, lb, 4 * U, 4 * U);
            dc.DrawText(label, new Point(lb.X + 5 * U, lb.Y + 3 * U));
        }

        private void DrawActionBar(DrawingContext dc, Rect sel)
        {
            _bar.Clear();

            var items = new (CaptureAction Action, string Label)[]
            {
                (CaptureAction.Edit,   "Annotate"),
                (CaptureAction.Copy,   "Copy"),
                (CaptureAction.Save,   "Save"),
                (CaptureAction.Record, "Record"),
                (CaptureAction.Cancel, "Cancel"),
            };

            double h = 34 * U, padX = 13 * U, gap = 4 * U, edgePad = 5 * U;
            var texts = new List<FormattedText>();
            double total = edgePad * 2;
            foreach (var it in items)
            {
                var ft = Text(it.Label, 12.5 * U, Brushes.White, it.Action == CaptureAction.Edit);
                texts.Add(ft);
                total += ft.Width + padX * 2 + gap;
            }
            total -= gap;

            double bx = sel.Right - total;
            double by = sel.Bottom + 9 * U;

            // Keep the bar on screen: below, else above, else inside.
            var host = _s.MonitorAt((int)sel.GetCenterX(), (int)sel.Bottom);
            if (by + h > host.Bottom - 4) by = sel.Top - h - 9 * U;
            if (by < host.Y + 4) by = Math.Min(sel.Bottom - h - 8 * U, host.Bottom - h - 8 * U);
            if (bx < host.X + 4) bx = host.X + 4;
            if (bx + total > host.Right - 4) bx = host.Right - total - 4;

            var barRect = new Rect(bx, by, total, h);

            // Exactly one monitor draws the bar: the one holding its centre.
            if (!_mon.Contains((int)(bx + total / 2), (int)(by + h / 2))) return;

            var bg = new SolidColorBrush(Color.FromArgb(242, 12, 12, 14));
            bg.Freeze();
            var edge = new Pen(new SolidColorBrush(Color.FromArgb(255, 38, 46, 60)), 1 * U);
            edge.Brush.Freeze();
            dc.DrawRoundedRectangle(bg, edge, barRect, 8 * U, 8 * U);

            double x = bx + edgePad;
            for (int i = 0; i < items.Length; i++)
            {
                var ft = texts[i];
                double bw = ft.Width + padX * 2;
                var r = new Rect(x, by + edgePad, bw, h - edgePad * 2);

                bool primary = items[i].Action == CaptureAction.Edit;
                bool danger = items[i].Action == CaptureAction.Cancel;
                if (primary)
                {
                    var b = new SolidColorBrush(ColorUtil.Parse("#A9781F"));
                    b.Freeze();
                    dc.DrawRoundedRectangle(b, null, r, 5 * U, 5 * U);
                }
                else if (danger)
                {
                    var b = new SolidColorBrush(Color.FromArgb(40, 229, 52, 42));
                    b.Freeze();
                    dc.DrawRoundedRectangle(b, null, r, 5 * U, 5 * U);
                }

                dc.DrawText(ft, new Point(r.X + padX, r.Y + (r.Height - ft.Height) / 2));
                _bar.Add((r, items[i].Action, items[i].Label));
                x += bw + gap;
            }
        }

        private FormattedText Text(string s, double size, Brush brush, bool bold)
        {
            return new FormattedText(
                s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal,
                             bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
                size, brush, Ppd);
        }
    }

    internal static class RectExt
    {
        public static double GetCenterX(this Rect r) => r.X + r.Width / 2;
        public static double GetCenterY(this Rect r) => r.Y + r.Height / 2;
    }
}
