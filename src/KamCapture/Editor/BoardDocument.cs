using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KamCapture.Editor
{
    /// <summary>
    /// The sandbox. A board larger than the screenshot, with the screenshot
    /// placed on it as a movable layer, so there is somewhere to write that is
    /// not on top of the thing being explained.
    /// </summary>
    public sealed class BoardDocument
    {
        public BitmapSource? Image { get; set; }
        public Rect ImageRect { get; set; }
        public Size BoardSize { get; set; } = new Size(1200, 800);
        public List<AnnItem> Items { get; } = new();

        public string BoardColor { get; set; } = "#F4F4F2";
        public bool ShowGrid { get; set; }

        /// <summary>The capture's true pixel size, before any board scaling.</summary>
        public int SourcePixelWidth => Image?.PixelWidth ?? 0;
        public int SourcePixelHeight => Image?.PixelHeight ?? 0;

        public static BoardDocument FromCapture(BitmapSource image, double margin)
        {
            var doc = new BoardDocument { Image = image };
            doc.LayoutWithMargin(image.PixelWidth, image.PixelHeight, margin);
            return doc;
        }

        public void LayoutWithMargin(double imgW, double imgH, double margin)
        {
            BoardSize = new Size(imgW + margin * 2, imgH + margin * 2);
            ImageRect = new Rect(margin, margin, imgW, imgH);
        }

        /// <summary>
        /// Resize the sandbox while holding the image where it is relative to
        /// the board's centre. The image is never allowed off the board.
        /// </summary>
        public void SetMargin(double margin)
        {
            if (Image == null) return;
            double w = ImageRect.Width, h = ImageRect.Height;
            BoardSize = new Size(w + margin * 2, h + margin * 2);
            ImageRect = new Rect(margin, margin, w, h);
        }

        public void ClampImage()
        {
            var r = ImageRect;
            if (r.Width > BoardSize.Width) BoardSize = new Size(r.Width, BoardSize.Height);
            if (r.Height > BoardSize.Height) BoardSize = new Size(BoardSize.Width, r.Height);

            double x = Math.Max(0, Math.Min(r.X, BoardSize.Width - r.Width));
            double y = Math.Max(0, Math.Min(r.Y, BoardSize.Height - r.Height));
            ImageRect = new Rect(x, y, r.Width, r.Height);
        }

        // ---------------- z-order ----------------

        public void BringToFront(IEnumerable<AnnItem> sel)
        {
            var list = sel.ToList();
            foreach (var i in list) Items.Remove(i);
            Items.AddRange(list);
        }

        public void SendToBack(IEnumerable<AnnItem> sel)
        {
            var list = sel.ToList();
            foreach (var i in list) Items.Remove(i);
            Items.InsertRange(0, list);
        }

        public void BringForward(IEnumerable<AnnItem> sel)
        {
            foreach (var i in sel.OrderByDescending(x => Items.IndexOf(x)))
            {
                int idx = Items.IndexOf(i);
                if (idx >= 0 && idx < Items.Count - 1)
                {
                    Items.RemoveAt(idx);
                    Items.Insert(idx + 1, i);
                }
            }
        }

        public void SendBackward(IEnumerable<AnnItem> sel)
        {
            foreach (var i in sel.OrderBy(x => Items.IndexOf(x)))
            {
                int idx = Items.IndexOf(i);
                if (idx > 0)
                {
                    Items.RemoveAt(idx);
                    Items.Insert(idx - 1, i);
                }
            }
        }

        // ---------------- rendering ----------------

        public DrawingVisual RenderToVisual(double scale, bool transparentBoard = false)
        {
            var dv = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);

            using (var dc = dv.RenderOpen())
            {
                dc.PushTransform(new ScaleTransform(scale, scale));

                if (!transparentBoard)
                {
                    var bg = new SolidColorBrush(ColorUtil.Parse(BoardColor));
                    bg.Freeze();
                    dc.DrawRectangle(bg, null, new Rect(0, 0, BoardSize.Width, BoardSize.Height));
                }

                if (Image != null)
                    dc.DrawImage(Image, ImageRect);

                var ctx = new RenderCtx
                {
                    Source = Image,
                    ImageRect = ImageRect,
                    Zoom = scale,
                    PixelsPerDip = 1.0,
                    ForExport = true
                };
                foreach (var item in Items) item.Render(dc, ctx);

                dc.Pop();
            }
            return dv;
        }

        /// <summary>
        /// Flatten to a bitmap. Annotations are vectors, so exporting a small
        /// crop at 2x or 3x gives genuinely sharper text and arrows rather than
        /// an upscale of a picture of them.
        /// </summary>
        public RenderTargetBitmap Export(int scale = 1, bool transparentBoard = false)
        {
            scale = Math.Max(1, Math.Min(8, scale));
            int w = Math.Max(1, (int)Math.Ceiling(BoardSize.Width * scale));
            int h = Math.Max(1, (int)Math.Ceiling(BoardSize.Height * scale));

            var dv = RenderToVisual(scale, transparentBoard);
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }

        /// <summary>Export just the image area, ignoring the sandbox margins.</summary>
        public RenderTargetBitmap ExportImageAreaOnly(int scale = 1)
        {
            scale = Math.Max(1, Math.Min(8, scale));
            var r = ImageRect;
            int w = Math.Max(1, (int)Math.Ceiling(r.Width * scale));
            int h = Math.Max(1, (int)Math.Ceiling(r.Height * scale));

            var dv = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
            using (var dc = dv.RenderOpen())
            {
                dc.PushTransform(new ScaleTransform(scale, scale));
                dc.PushTransform(new TranslateTransform(-r.X, -r.Y));
                if (Image != null) dc.DrawImage(Image, r);
                var ctx = new RenderCtx { Source = Image, ImageRect = r, Zoom = scale, PixelsPerDip = 1.0, ForExport = true };
                foreach (var item in Items) item.Render(dc, ctx);
                dc.Pop();
                dc.Pop();
            }

            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }

        // ---------------- undo ----------------

        public sealed class Snapshot
        {
            public List<AnnItem> Items = new();
            public Rect ImageRect;
            public Size BoardSize;
        }

        public Snapshot Capture()
        {
            var s = new Snapshot { ImageRect = ImageRect, BoardSize = BoardSize };
            foreach (var i in Items) s.Items.Add(i.Clone());
            return s;
        }

        public void Restore(Snapshot s)
        {
            Items.Clear();
            foreach (var i in s.Items) Items.Add(i.Clone());
            ImageRect = s.ImageRect;
            BoardSize = s.BoardSize;
        }
    }

    public sealed class UndoStack
    {
        private readonly Stack<BoardDocument.Snapshot> _undo = new();
        private readonly Stack<BoardDocument.Snapshot> _redo = new();
        private readonly BoardDocument _doc;
        private const int Limit = 120;

        public UndoStack(BoardDocument doc) { _doc = doc; }

        public bool CanUndo => _undo.Count > 0;
        public bool CanRedo => _redo.Count > 0;

        /// <summary>Any change to the stacks: use it to enable buttons.</summary>
        public event Action? Changed;

        /// <summary>Only an actual undo or redo, which invalidates live references.</summary>
        public event Action? Restored;

        public void Push()
        {
            _undo.Push(_doc.Capture());
            if (_undo.Count > Limit)
            {
                var keep = _undo.ToArray().Take(Limit).Reverse().ToList();
                _undo.Clear();
                foreach (var s in keep) _undo.Push(s);
            }
            _redo.Clear();
            Changed?.Invoke();
        }

        public void Undo()
        {
            if (_undo.Count == 0) return;
            _redo.Push(_doc.Capture());
            _doc.Restore(_undo.Pop());
            Restored?.Invoke();
            Changed?.Invoke();
        }

        public void Redo()
        {
            if (_redo.Count == 0) return;
            _undo.Push(_doc.Capture());
            _doc.Restore(_redo.Pop());
            Restored?.Invoke();
            Changed?.Invoke();
        }
    }
}
