using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KamCapture.Editor
{
    public enum EditTool { Select, Pan, Pencil, Highlighter, Line, Arrow, Rectangle, Ellipse, Text, Step, Symbol, Redact, Crop }

    public sealed class ToolOptions
    {
        public string Color { get; set; } = "#E5342A";
        public double Thickness { get; set; } = 3;
        public double FontSize { get; set; } = 18;

        public bool ShapeFilled { get; set; }
        public string ShapeFillColor { get; set; } = "#FFD400";

        public bool TextBackground { get; set; } = true;
        public string TextBackgroundColor { get; set; } = "#FFFFFF";
        public bool TextBorder { get; set; }
        public bool Bold { get; set; }

        public StepStyle StepStyle { get; set; } = StepStyle.Number;
        public Dictionary<StepStyle, int> StepCounters { get; } = new()
        {
            { StepStyle.Number, 1 }, { StepStyle.UpperLetter, 1 }, { StepStyle.LowerLetter, 1 },
            { StepStyle.LowerRoman, 1 }, { StepStyle.UpperRoman, 1 }
        };

        public string Symbol { get; set; } = "arrow-right";
        public bool SymbolFilled { get; set; } = true;
        public double SymbolRotation { get; set; }

        public int RedactBlock { get; set; } = 12;
        public bool ArrowStart { get; set; }
        public bool ArrowEnd { get; set; } = true;

        public int NextStep()
        {
            int n = StepCounters.TryGetValue(StepStyle, out var v) ? v : 1;
            StepCounters[StepStyle] = n + 1;
            return n;
        }

        public void ResetSteps()
        {
            foreach (var k in StepCounters.Keys.ToList()) StepCounters[k] = 1;
        }
    }

    /// <summary>
    /// The annotation canvas: the board, the screenshot sitting on it, and
    /// every drawn object. A Canvas rather than a bare element so the live text
    /// editor can be parked on top of the thing it is editing.
    /// </summary>
    public sealed class BoardSurface : Canvas
    {
        public BoardDocument Doc { get; private set; }
        public UndoStack Undo { get; private set; }
        public ToolOptions Options { get; } = new();

        private EditTool _tool = EditTool.Select;
        public EditTool Tool
        {
            get => _tool;
            set
            {
                if (_tool == value) return;
                CommitTextEdit();
                _tool = value;
                if (value != EditTool.Select) ClearSelection();
                UpdateCursor();
                InvalidateVisual();
                ToolChanged?.Invoke();
            }
        }

        public double Zoom { get; private set; } = 1.0;
        public Vector Offset { get; private set; }

        public readonly HashSet<AnnItem> Selection = new();
        public bool ImageSelected { get; private set; }

        public event Action? SelectionChanged;
        public event Action? ToolChanged;
        public event Action? ViewChanged;
        public event Action? DocumentChanged;

        private static readonly List<AnnItem> ClipboardItems = new();

        // interaction state
        private bool _panning;
        private Point _panStart;
        private Vector _panOffsetStart;

        private AnnItem? _drawing;
        private Point _drawAnchor;

        private bool _marquee;
        private Point _marqueeStart, _marqueeCurrent;

        private bool _movingSelection;
        private Point _moveLast;
        private bool _movedSomething;

        private int _scaleHandle = -1;
        private Rect _scaleOriginal;
        private Point _scaleAnchor;

        private bool _movingImage;

        private TextBox? _editor;
        private TextItem? _editingItem;
        private bool _editingIsNew;

        private bool _spaceHeld;

        public BoardSurface(BoardDocument doc)
        {
            Doc = doc;
            Undo = new UndoStack(doc);
            Background = Brushes.Transparent;
            Focusable = true;
            ClipToBounds = true;
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
            Undo.Restored += OnUndoRestored;
        }

        private void OnUndoRestored()
        {
            ClearSelection();
            InvalidateVisual();
            DocumentChanged?.Invoke();
        }

        public void LoadDocument(BoardDocument doc)
        {
            CommitTextEdit();
            Doc = doc;
            Undo = new UndoStack(doc);
            Undo.Restored += OnUndoRestored;
            ClearSelection();
            ZoomToFit();
            InvalidateVisual();
        }

        // ---------------- view ----------------

        private Matrix BoardToScreen()
        {
            var m = Matrix.Identity;
            m.Scale(Zoom, Zoom);
            m.Translate(Offset.X, Offset.Y);
            return m;
        }

        public Point ToBoard(Point screen)
        {
            var m = BoardToScreen();
            m.Invert();
            return m.Transform(screen);
        }

        public Point ToScreen(Point board) => BoardToScreen().Transform(board);

        public void ZoomToFit()
        {
            if (ActualWidth < 4 || ActualHeight < 4) return;
            double pad = 28;
            double z = Math.Min((ActualWidth - pad) / Doc.BoardSize.Width,
                                (ActualHeight - pad) / Doc.BoardSize.Height);
            SetZoom(Math.Max(0.02, Math.Min(8, z)), centreBoard: true);
        }

        public void ZoomTo100() => SetZoom(1.0, centreBoard: true);

        public void SetZoom(double zoom, bool centreBoard = false, Point? anchorScreen = null)
        {
            zoom = Math.Max(0.02, Math.Min(32, zoom));

            if (centreBoard)
            {
                Zoom = zoom;
                Offset = new Vector(
                    (ActualWidth - Doc.BoardSize.Width * zoom) / 2,
                    (ActualHeight - Doc.BoardSize.Height * zoom) / 2);
            }
            else
            {
                var anchor = anchorScreen ?? new Point(ActualWidth / 2, ActualHeight / 2);
                var boardPoint = ToBoard(anchor);
                Zoom = zoom;
                Offset = new Vector(anchor.X - boardPoint.X * zoom, anchor.Y - boardPoint.Y * zoom);
            }

            ClampPan();
            SyncEditorPosition();
            InvalidateVisual();
            ViewChanged?.Invoke();
        }

        /// <summary>
        /// The board can be pushed around but never thrown away: it is always
        /// kept overlapping the viewport, and centred when it is smaller.
        /// </summary>
        private void ClampPan()
        {
            double bw = Doc.BoardSize.Width * Zoom, bh = Doc.BoardSize.Height * Zoom;
            double x = Offset.X, y = Offset.Y;

            if (bw <= ActualWidth) x = (ActualWidth - bw) / 2;
            else x = Math.Max(ActualWidth - bw, Math.Min(0, x));

            if (bh <= ActualHeight) y = (ActualHeight - bh) / 2;
            else y = Math.Max(ActualHeight - bh, Math.Min(0, y));

            Offset = new Vector(x, y);
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo info)
        {
            base.OnRenderSizeChanged(info);
            ClampPan();
            SyncEditorPosition();
            InvalidateVisual();
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            // Wheel zooms around the pointer: this is an image editor, not a document.
            var pos = e.GetPosition(this);
            double factor = e.Delta > 0 ? 1.12 : 1 / 1.12;
            SetZoom(Zoom * factor, false, pos);
            e.Handled = true;
        }

        // ---------------- selection ----------------

        public void ClearSelection()
        {
            if (Selection.Count == 0 && !ImageSelected) return;
            Selection.Clear();
            ImageSelected = false;
            SelectionChanged?.Invoke();
            InvalidateVisual();
        }

        public void SelectAll()
        {
            Selection.Clear();
            foreach (var i in Doc.Items) Selection.Add(i);
            ImageSelected = false;
            SelectionChanged?.Invoke();
            InvalidateVisual();
        }

        public Rect SelectionBounds()
        {
            Rect r = Rect.Empty;
            if (ImageSelected) r = Doc.ImageRect;
            foreach (var i in Selection)
            {
                var b = i.Bounds;
                if (b.IsEmpty) continue;
                r = r.IsEmpty ? b : Rect.Union(r, b);
            }
            return r;
        }

        private AnnItem? HitItem(Point board)
        {
            double tol = 5 / Zoom;
            for (int i = Doc.Items.Count - 1; i >= 0; i--)
                if (Doc.Items[i].HitTest(board, tol)) return Doc.Items[i];
            return null;
        }

        // ---------------- grouping ----------------

        public bool CanGroup => Selection.Count > 1;
        public bool CanUngroup => Selection.Any(s => s is GroupItem);

        public void GroupSelection()
        {
            if (Selection.Count < 2) return;
            Undo.Push();

            var ordered = Doc.Items.Where(Selection.Contains).ToList();
            int insertAt = Doc.Items.IndexOf(ordered[^1]);

            var group = new GroupItem();
            foreach (var i in ordered) group.Children.Add(i);
            foreach (var i in ordered) Doc.Items.Remove(i);

            insertAt = Math.Max(0, Math.Min(insertAt - ordered.Count + 1, Doc.Items.Count));
            Doc.Items.Insert(insertAt, group);

            Selection.Clear();
            Selection.Add(group);
            SelectionChanged?.Invoke();
            DocumentChanged?.Invoke();
            InvalidateVisual();
        }

        public void UngroupSelection()
        {
            var groups = Selection.OfType<GroupItem>().ToList();
            if (groups.Count == 0) return;
            Undo.Push();

            foreach (var g in groups)
            {
                int idx = Doc.Items.IndexOf(g);
                if (idx < 0) continue;
                Doc.Items.RemoveAt(idx);
                Doc.Items.InsertRange(idx, g.Children);
                Selection.Remove(g);
                foreach (var c in g.Children) Selection.Add(c);
            }

            SelectionChanged?.Invoke();
            DocumentChanged?.Invoke();
            InvalidateVisual();
        }

        // ---------------- editing commands ----------------

        public void DeleteSelection()
        {
            if (Selection.Count == 0) return;
            Undo.Push();
            foreach (var i in Selection.ToList()) Doc.Items.Remove(i);
            Selection.Clear();
            SelectionChanged?.Invoke();
            DocumentChanged?.Invoke();
            InvalidateVisual();
        }

        public void CopySelection()
        {
            if (Selection.Count == 0) return;
            ClipboardItems.Clear();
            foreach (var i in Doc.Items.Where(Selection.Contains))
                ClipboardItems.Add(i.Clone());
        }

        public void CutSelection() { CopySelection(); DeleteSelection(); }

        public void Paste()
        {
            if (ClipboardItems.Count == 0) return;
            Undo.Push();
            Selection.Clear();
            foreach (var i in ClipboardItems)
            {
                var c = i.Clone();
                c.Translate(18, 18);
                Doc.Items.Add(c);
                Selection.Add(c);
            }
            ImageSelected = false;
            SelectionChanged?.Invoke();
            DocumentChanged?.Invoke();
            InvalidateVisual();
        }

        public void DuplicateSelection()
        {
            if (Selection.Count == 0) return;
            CopySelection();
            Paste();
        }

        public void NudgeSelection(double dx, double dy)
        {
            if (Selection.Count == 0 && !ImageSelected) return;
            Undo.Push();
            foreach (var i in Selection) i.Translate(dx, dy);
            if (ImageSelected)
            {
                Doc.ImageRect = Rect.Offset(Doc.ImageRect, dx, dy);
                Doc.ClampImage();
            }
            DocumentChanged?.Invoke();
            InvalidateVisual();
        }

        public void ApplyToSelection(Action<AnnItem> apply)
        {
            if (Selection.Count == 0) return;
            Undo.Push();
            foreach (var i in Selection) ApplyDeep(i, apply);
            DocumentChanged?.Invoke();
            InvalidateVisual();
        }

        private static void ApplyDeep(AnnItem item, Action<AnnItem> apply)
        {
            if (item is GroupItem g)
            {
                foreach (var c in g.Children) ApplyDeep(c, apply);
                return;
            }
            apply(item);
        }

        // ---------------- input ----------------

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            Focus();
            var screen = e.GetPosition(this);
            var board = ToBoard(screen);

            if (e.ChangedButton == MouseButton.Middle ||
                (e.ChangedButton == MouseButton.Left && (_tool == EditTool.Pan || _spaceHeld)))
            {
                _panning = true;
                _panStart = screen;
                _panOffsetStart = Offset;
                CaptureMouse();
                Cursor = Cursors.ScrollAll;
                e.Handled = true;
                return;
            }

            if (e.ChangedButton != MouseButton.Left) return;

            CommitTextEdit();

            if (_tool == EditTool.Select)
            {
                HandleSelectPress(screen, board, e);
                return;
            }

            StartDrawing(board);
            e.Handled = true;
        }

        private void HandleSelectPress(Point screen, Point board, MouseButtonEventArgs e)
        {
            bool additive = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ||
                            Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

            // A handle on the current selection wins over everything else.
            int handle = HitScaleHandle(screen);
            if (handle >= 0)
            {
                Undo.Push();
                _scaleHandle = handle;
                _scaleOriginal = SelectionBounds();
                _scaleAnchor = AnchorFor(handle, _scaleOriginal);
                CaptureMouse();
                e.Handled = true;
                return;
            }

            var hit = HitItem(board);

            if (hit != null)
            {
                if (additive)
                {
                    if (!Selection.Remove(hit)) Selection.Add(hit);
                    ImageSelected = false;
                }
                else if (!Selection.Contains(hit))
                {
                    Selection.Clear();
                    Selection.Add(hit);
                    ImageSelected = false;
                }

                if (e.ClickCount == 2 && hit is TextItem ti)
                {
                    BeginTextEdit(ti, false);
                    SelectionChanged?.Invoke();
                    e.Handled = true;
                    return;
                }

                _movingSelection = true;
                _moveLast = board;
                _movedSomething = false;
                CaptureMouse();
                SelectionChanged?.Invoke();
                InvalidateVisual();
                e.Handled = true;
                return;
            }

            // Already-selected image: press inside it to drag it.
            if (ImageSelected && Doc.ImageRect.Contains(board))
            {
                _movingImage = true;
                _moveLast = board;
                _movedSomething = false;
                CaptureMouse();
                e.Handled = true;
                return;
            }

            // Otherwise this is a marquee; a press-and-release with no drag over
            // the image selects the image layer instead.
            if (!additive) { Selection.Clear(); ImageSelected = false; }
            _marquee = true;
            _marqueeStart = board;
            _marqueeCurrent = board;
            CaptureMouse();
            SelectionChanged?.Invoke();
            InvalidateVisual();
            e.Handled = true;
        }

        private void StartDrawing(Point board)
        {
            Undo.Push();
            _drawAnchor = board;
            var col = Options.Color;

            switch (_tool)
            {
                case EditTool.Pencil:
                case EditTool.Highlighter:
                    _drawing = new StrokeItem
                    {
                        StrokeColor = col,
                        Thickness = _tool == EditTool.Highlighter ? Math.Max(10, Options.Thickness * 5) : Options.Thickness,
                        IsHighlighter = _tool == EditTool.Highlighter,
                        Points = { board }
                    };
                    break;

                case EditTool.Line:
                case EditTool.Arrow:
                    _drawing = new LineItem
                    {
                        StrokeColor = col, Thickness = Options.Thickness,
                        A = board, B = board,
                        ArrowEnd = _tool == EditTool.Arrow && Options.ArrowEnd,
                        ArrowStart = _tool == EditTool.Arrow && Options.ArrowStart
                    };
                    break;

                case EditTool.Rectangle:
                    _drawing = new RectItem
                    {
                        StrokeColor = col, Thickness = Options.Thickness,
                        A = board, B = board,
                        FillColor = Options.ShapeFilled ? Options.ShapeFillColor : null
                    };
                    break;

                case EditTool.Ellipse:
                    _drawing = new EllipseItem
                    {
                        StrokeColor = col, Thickness = Options.Thickness,
                        A = board, B = board,
                        FillColor = Options.ShapeFilled ? Options.ShapeFillColor : null
                    };
                    break;

                case EditTool.Symbol:
                    _drawing = new SymbolItem
                    {
                        StrokeColor = col, Thickness = Math.Max(2, Options.Thickness),
                        A = board, B = board,
                        Symbol = Options.Symbol,
                        Filled = Options.SymbolFilled,
                        Rotation = Options.SymbolRotation
                    };
                    break;

                case EditTool.Redact:
                    _drawing = new RedactItem
                    {
                        StrokeColor = col, A = board, B = board,
                        BlockSize = Options.RedactBlock,
                        Solid = false
                    };
                    break;

                case EditTool.Crop:
                    _drawing = new RectItem
                    {
                        StrokeColor = "#4A7CFF", Thickness = 1.5 / Zoom,
                        A = board, B = board
                    };
                    break;

                case EditTool.Step:
                {
                    int n = Options.NextStep();
                    double radius = Math.Max(12, Options.FontSize * 0.95);
                    var step = new StepItem
                    {
                        Center = board, Radius = radius,
                        Label = StepItem.LabelFor(Options.StepStyle, n),
                        StrokeColor = col, FillColor = col, TextColor = "#FFFFFF"
                    };
                    Doc.Items.Add(step);
                    Selection.Clear(); Selection.Add(step);
                    _movingSelection = true;
                    _moveLast = board;
                    CaptureMouse();
                    SelectionChanged?.Invoke();
                    DocumentChanged?.Invoke();
                    InvalidateVisual();
                    return;
                }

                case EditTool.Text:
                {
                    var t = new TextItem
                    {
                        Origin = board,
                        StrokeColor = col,
                        FontSize = Options.FontSize,
                        Bold = Options.Bold,
                        MaxWidth = 320,
                        BackgroundColor = Options.TextBackground ? Options.TextBackgroundColor : null,
                        ShowBorder = Options.TextBorder,
                        Thickness = Math.Max(1, Options.Thickness * 0.6)
                    };
                    Doc.Items.Add(t);
                    BeginTextEdit(t, true);
                    DocumentChanged?.Invoke();
                    InvalidateVisual();
                    return;
                }
            }

            if (_drawing != null)
            {
                Doc.Items.Add(_drawing);
                CaptureMouse();
                InvalidateVisual();
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var screen = e.GetPosition(this);
            var board = ToBoard(screen);

            if (_panning)
            {
                Offset = _panOffsetStart + (screen - _panStart);
                ClampPan();
                SyncEditorPosition();
                InvalidateVisual();
                ViewChanged?.Invoke();
                return;
            }

            if (_scaleHandle >= 0 && e.LeftButton == MouseButtonState.Pressed)
            {
                ScaleSelectionTo(board);
                return;
            }

            if (_movingSelection && e.LeftButton == MouseButtonState.Pressed)
            {
                var d = board - _moveLast;
                if (d.Length > 0)
                {
                    if (!_movedSomething) { Undo.Push(); _movedSomething = true; }
                    foreach (var i in Selection) i.Translate(d.X, d.Y);
                    _moveLast = board;
                    InvalidateVisual();
                }
                return;
            }

            if (_movingImage && e.LeftButton == MouseButtonState.Pressed)
            {
                var d = board - _moveLast;
                if (d.Length > 0)
                {
                    if (!_movedSomething) { Undo.Push(); _movedSomething = true; }
                    Doc.ImageRect = Rect.Offset(Doc.ImageRect, d.X, d.Y);
                    Doc.ClampImage();
                    _moveLast = board;
                    InvalidateVisual();
                }
                return;
            }

            if (_marquee && e.LeftButton == MouseButtonState.Pressed)
            {
                _marqueeCurrent = board;
                InvalidateVisual();
                return;
            }

            if (_drawing != null && e.LeftButton == MouseButtonState.Pressed)
            {
                UpdateDrawing(board);
                return;
            }

            UpdateCursor(screen, board);
        }

        private void UpdateDrawing(Point board)
        {
            bool constrain = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

            switch (_drawing)
            {
                case StrokeItem s:
                    if (s.Points.Count == 0 || (board - s.Points[^1]).Length >= 1.2 / Zoom)
                        s.Points.Add(board);
                    break;

                case LineItem l:
                    l.B = constrain ? ConstrainAngle(_drawAnchor, board) : board;
                    break;

                case RectItem r:
                    r.B = constrain ? ConstrainSquare(_drawAnchor, board) : board;
                    break;

                case EllipseItem el:
                    el.B = constrain ? ConstrainSquare(_drawAnchor, board) : board;
                    break;

                case RedactItem rd:
                    rd.B = constrain ? ConstrainSquare(_drawAnchor, board) : board;
                    break;

                case SymbolItem sy:
                    sy.B = constrain ? ConstrainSquare(_drawAnchor, board) : board;
                    break;
            }
            InvalidateVisual();
        }

        private static Point ConstrainAngle(Point a, Point b)
        {
            var v = b - a;
            double angle = Math.Atan2(v.Y, v.X);
            double step = Math.PI / 8;
            angle = Math.Round(angle / step) * step;
            double len = v.Length;
            return new Point(a.X + Math.Cos(angle) * len, a.Y + Math.Sin(angle) * len);
        }

        private static Point ConstrainSquare(Point a, Point b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double s = Math.Max(Math.Abs(dx), Math.Abs(dy));
            return new Point(a.X + Math.Sign(dx == 0 ? 1 : dx) * s, a.Y + Math.Sign(dy == 0 ? 1 : dy) * s);
        }

        protected override void OnMouseUp(MouseButtonEventArgs e)
        {
            if (IsMouseCaptured) ReleaseMouseCapture();

            if (_panning)
            {
                _panning = false;
                UpdateCursor();
                return;
            }

            if (_scaleHandle >= 0)
            {
                _scaleHandle = -1;
                DocumentChanged?.Invoke();
                InvalidateVisual();
                return;
            }

            if (_movingSelection)
            {
                _movingSelection = false;
                DocumentChanged?.Invoke();
                InvalidateVisual();
                return;
            }

            if (_movingImage)
            {
                _movingImage = false;
                DocumentChanged?.Invoke();
                InvalidateVisual();
                return;
            }

            if (_marquee)
            {
                _marquee = false;
                var r = new Rect(_marqueeStart, _marqueeCurrent);

                if (r.Width < 3 / Zoom && r.Height < 3 / Zoom)
                {
                    // A click, not a drag: grab the image layer if it is under there.
                    if (Doc.Image != null && Doc.ImageRect.Contains(_marqueeStart))
                    {
                        ImageSelected = true;
                        Selection.Clear();
                    }
                }
                else
                {
                    foreach (var item in Doc.Items)
                    {
                        var b = item.Bounds;
                        if (!b.IsEmpty && r.IntersectsWith(b)) Selection.Add(item);
                    }
                    ImageSelected = false;
                }

                SelectionChanged?.Invoke();
                InvalidateVisual();
                return;
            }

            if (_drawing != null)
            {
                var finished = _drawing;
                _drawing = null;

                if (finished is SymbolItem stamped &&
                    (stamped.R.Width < 6 / Zoom || stamped.R.Height < 6 / Zoom))
                {
                    double half = 46;
                    stamped.A = new Point(_drawAnchor.X - half, _drawAnchor.Y - half);
                    stamped.B = new Point(_drawAnchor.X + half, _drawAnchor.Y + half);
                    Selection.Clear();
                    Selection.Add(stamped);
                    SelectionChanged?.Invoke();
                    DocumentChanged?.Invoke();
                    InvalidateVisual();
                    return;
                }

                bool degenerate = finished switch
                {
                    StrokeItem s => s.Points.Count < 2 && s.Thickness < 2,
                    LineItem l => (l.B - l.A).Length < 3 / Zoom,
                    RectItem r2 => r2.R.Width < 3 / Zoom || r2.R.Height < 3 / Zoom,
                    EllipseItem el => el.R.Width < 3 / Zoom || el.R.Height < 3 / Zoom,
                    RedactItem rd => rd.R.Width < 3 / Zoom || rd.R.Height < 3 / Zoom,
                    _ => false
                };

                if (degenerate) Doc.Items.Remove(finished);
                else if (_tool == EditTool.Crop && finished is RectItem crop)
                {
                    Doc.Items.Remove(finished);
                    CropImageTo(crop.R);
                }

                DocumentChanged?.Invoke();
                InvalidateVisual();
            }
        }

        // ---------------- scale handles ----------------

        private const double HandleScreenSize = 8;

        private Point[] HandlePoints(Rect r) => new[]
        {
            new Point(r.Left, r.Top),
            new Point(r.Left + r.Width / 2, r.Top),
            new Point(r.Right, r.Top),
            new Point(r.Right, r.Top + r.Height / 2),
            new Point(r.Right, r.Bottom),
            new Point(r.Left + r.Width / 2, r.Bottom),
            new Point(r.Left, r.Bottom),
            new Point(r.Left, r.Top + r.Height / 2),
        };

        private int HitScaleHandle(Point screen)
        {
            var r = SelectionBounds();
            if (r.IsEmpty) return -1;
            var pts = HandlePoints(r);
            for (int i = 0; i < pts.Length; i++)
            {
                var sp = ToScreen(pts[i]);
                if (Math.Abs(screen.X - sp.X) <= HandleScreenSize && Math.Abs(screen.Y - sp.Y) <= HandleScreenSize)
                    return i;
            }
            return -1;
        }

        private static Point AnchorFor(int handle, Rect r) => handle switch
        {
            0 => new Point(r.Right, r.Bottom),
            1 => new Point(r.Left + r.Width / 2, r.Bottom),
            2 => new Point(r.Left, r.Bottom),
            3 => new Point(r.Left, r.Top + r.Height / 2),
            4 => new Point(r.Left, r.Top),
            5 => new Point(r.Left + r.Width / 2, r.Top),
            6 => new Point(r.Right, r.Top),
            7 => new Point(r.Right, r.Top + r.Height / 2),
            _ => new Point(r.Left, r.Top)
        };

        private void ScaleSelectionTo(Point board)
        {
            var o = _scaleOriginal;
            if (o.Width < 0.01 || o.Height < 0.01) return;

            bool corner = _scaleHandle % 2 == 0;
            bool freeform = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

            double sx = 1, sy = 1;
            switch (_scaleHandle)
            {
                case 0: sx = (_scaleAnchor.X - board.X) / o.Width; sy = (_scaleAnchor.Y - board.Y) / o.Height; break;
                case 2: sx = (board.X - _scaleAnchor.X) / o.Width; sy = (_scaleAnchor.Y - board.Y) / o.Height; break;
                case 4: sx = (board.X - _scaleAnchor.X) / o.Width; sy = (board.Y - _scaleAnchor.Y) / o.Height; break;
                case 6: sx = (_scaleAnchor.X - board.X) / o.Width; sy = (board.Y - _scaleAnchor.Y) / o.Height; break;
                case 1: sy = (_scaleAnchor.Y - board.Y) / o.Height; break;
                case 5: sy = (board.Y - _scaleAnchor.Y) / o.Height; break;
                case 3: sx = (board.X - _scaleAnchor.X) / o.Width; break;
                case 7: sx = (_scaleAnchor.X - board.X) / o.Width; break;
            }

            // Corners keep the shape unless Shift asks for free scaling.
            if (corner && !freeform)
            {
                double s = Math.Max(Math.Abs(sx), Math.Abs(sy));
                sx = s * Math.Sign(sx == 0 ? 1 : sx);
                sy = s * Math.Sign(sy == 0 ? 1 : sy);
            }

            sx = Clamp(sx); sy = Clamp(sy);
            if (_scaleHandle is 1 or 5) sx = 1;
            if (_scaleHandle is 3 or 7) sy = 1;

            var current = SelectionBounds();
            if (current.IsEmpty) return;

            // Map the live bounds back to the original, then apply the new scale.
            double fx = current.Width > 0.01 ? (o.Width * sx) / current.Width : 1;
            double fy = current.Height > 0.01 ? (o.Height * sy) / current.Height : 1;
            fx = Clamp(fx); fy = Clamp(fy);

            var m = Matrix.Identity;
            m.ScaleAt(fx, fy, _scaleAnchor.X, _scaleAnchor.Y);

            foreach (var i in Selection) i.ApplyTransform(m);

            if (ImageSelected)
            {
                var tl = m.Transform(new Point(Doc.ImageRect.Left, Doc.ImageRect.Top));
                var br = m.Transform(new Point(Doc.ImageRect.Right, Doc.ImageRect.Bottom));
                var nr = new Rect(Math.Min(tl.X, br.X), Math.Min(tl.Y, br.Y),
                                  Math.Max(4, Math.Abs(br.X - tl.X)), Math.Max(4, Math.Abs(br.Y - tl.Y)));
                Doc.ImageRect = nr;
                Doc.ClampImage();
            }

            InvalidateVisual();
        }

        private static double Clamp(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return 1;
            double a = Math.Abs(v);
            if (a < 0.02) return 0.02 * (v < 0 ? -1 : 1);
            if (a > 50) return 50 * (v < 0 ? -1 : 1);
            return v;
        }

        // ---------------- crop ----------------

        private void CropImageTo(Rect boardRect)
        {
            if (Doc.Image == null) return;
            var inter = Rect.Intersect(boardRect, Doc.ImageRect);
            if (inter.IsEmpty || inter.Width < 4 || inter.Height < 4) return;

            double sx = Doc.Image.PixelWidth / Doc.ImageRect.Width;
            double sy = Doc.Image.PixelHeight / Doc.ImageRect.Height;

            int px = (int)Math.Round((inter.X - Doc.ImageRect.X) * sx);
            int py = (int)Math.Round((inter.Y - Doc.ImageRect.Y) * sy);
            int pw = (int)Math.Round(inter.Width * sx);
            int ph = (int)Math.Round(inter.Height * sy);

            px = Math.Max(0, Math.Min(px, Doc.Image.PixelWidth - 1));
            py = Math.Max(0, Math.Min(py, Doc.Image.PixelHeight - 1));
            pw = Math.Max(1, Math.Min(pw, Doc.Image.PixelWidth - px));
            ph = Math.Max(1, Math.Min(ph, Doc.Image.PixelHeight - py));

            var cropped = new CroppedBitmap(Doc.Image, new Int32Rect(px, py, pw, ph));
            cropped.Freeze();
            Doc.Image = cropped;
            Doc.ImageRect = inter;
            Doc.ClampImage();
            Tool = EditTool.Select;
        }

        // ---------------- text editing ----------------

        public void BeginTextEdit(TextItem item, bool isNew)
        {
            CommitTextEdit();
            _editingItem = item;
            _editingIsNew = isNew;

            _editor = new TextBox
            {
                Text = item.Text,
                AcceptsReturn = true,
                AcceptsTab = false,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily(TextItem.FontName),
                FontSize = Math.Max(4, item.FontSize * Zoom),
                FontWeight = item.Bold ? FontWeights.Bold : FontWeights.Normal,
                FontStyle = item.Italic ? FontStyles.Italic : FontStyles.Normal,
                Foreground = new SolidColorBrush(item.Ink),
                Background = new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(ColorUtil.Parse("#4A7CFF")),
                BorderThickness = new Thickness(1.5),
                Padding = new Thickness(item.Padding * Zoom),
                MinWidth = 60,
                SelectionBrush = new SolidColorBrush(ColorUtil.Parse("#4A7CFF")),
                CaretBrush = new SolidColorBrush(item.Ink),
                Tag = "board-text-editor"
            };

            _editor.LostKeyboardFocus += (_, _) => CommitTextEdit();
            _editor.PreviewKeyDown += EditorKeyDown;
            _editor.TextChanged += (_, _) => SyncEditorPosition();

            Children.Add(_editor);
            SyncEditorPosition();

            Dispatcher.BeginInvoke(new Action(() =>
            {
                _editor?.Focus();
                _editor?.SelectAll();
            }), System.Windows.Threading.DispatcherPriority.Input);

            InvalidateVisual();
        }

        private void EditorKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                CancelTextEdit();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                CommitTextEdit();
                Focus();
                e.Handled = true;
            }
        }

        private void SyncEditorPosition()
        {
            if (_editor == null || _editingItem == null) return;
            var p = ToScreen(_editingItem.Origin);
            SetLeft(_editor, p.X);
            SetTop(_editor, p.Y);
            _editor.FontSize = Math.Max(4, _editingItem.FontSize * Zoom);
            _editor.Padding = new Thickness(_editingItem.Padding * Zoom);
            _editor.MaxWidth = Math.Max(80, _editingItem.MaxWidth * Zoom + 20);
            _editor.MinWidth = Math.Max(60, 60 * Zoom);
        }

        public void CommitTextEdit()
        {
            if (_editor == null || _editingItem == null) return;

            var editor = _editor;
            var item = _editingItem;
            _editor = null;
            _editingItem = null;

            Children.Remove(editor);
            item.Text = editor.Text;

            if (string.IsNullOrWhiteSpace(item.Text))
                Doc.Items.Remove(item);

            _editingIsNew = false;
            DocumentChanged?.Invoke();
            InvalidateVisual();
        }

        private void CancelTextEdit()
        {
            if (_editor == null || _editingItem == null) return;
            var editor = _editor;
            var item = _editingItem;
            _editor = null;
            _editingItem = null;
            Children.Remove(editor);

            if (_editingIsNew) Doc.Items.Remove(item);
            _editingIsNew = false;
            Focus();
            InvalidateVisual();
        }

        public bool IsEditingText => _editor != null;

        /// <summary>Restyle the text box currently being edited, live.</summary>
        public void RefreshEditingStyle()
        {
            if (_editor == null || _editingItem == null) return;
            _editor.FontWeight = _editingItem.Bold ? FontWeights.Bold : FontWeights.Normal;
            _editor.Foreground = new SolidColorBrush(_editingItem.Ink);
            SyncEditorPosition();
        }

        public TextItem? EditingItem => _editingItem;

        // ---------------- keyboard ----------------

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Space && !IsEditingText) { _spaceHeld = true; UpdateCursor(); }
            base.OnKeyDown(e);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.Key == Key.Space) { _spaceHeld = false; UpdateCursor(); }
            base.OnKeyUp(e);
        }

        // ---------------- cursor ----------------

        private void UpdateCursor() => UpdateCursor(null, null);

        private void UpdateCursor(Point? screen, Point? board)
        {
            if (_panning || _spaceHeld || _tool == EditTool.Pan) { Cursor = Cursors.ScrollAll; return; }

            if (_tool == EditTool.Select)
            {
                if (screen.HasValue)
                {
                    int h = HitScaleHandle(screen.Value);
                    if (h >= 0)
                    {
                        Cursor = h switch
                        {
                            0 or 4 => Cursors.SizeNWSE,
                            2 or 6 => Cursors.SizeNESW,
                            1 or 5 => Cursors.SizeNS,
                            _ => Cursors.SizeWE
                        };
                        return;
                    }
                    if (board.HasValue && (HitItem(board.Value) != null ||
                        (ImageSelected && Doc.ImageRect.Contains(board.Value))))
                    {
                        Cursor = Cursors.SizeAll;
                        return;
                    }
                }
                Cursor = Cursors.Arrow;
                return;
            }

            Cursor = _tool == EditTool.Text ? Cursors.IBeam : Cursors.Cross;
        }

        // ---------------- render ----------------

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);

            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x0C, 0x0F, 0x16)), null,
                new Rect(0, 0, ActualWidth, ActualHeight));

            dc.PushTransform(new MatrixTransform(BoardToScreen()));

            var board = new Rect(0, 0, Doc.BoardSize.Width, Doc.BoardSize.Height);

            var shadow = new SolidColorBrush(Color.FromArgb(70, 0, 0, 0));
            shadow.Freeze();
            dc.DrawRectangle(shadow, null, new Rect(board.X + 3 / Zoom, board.Y + 3 / Zoom, board.Width, board.Height));

            var bg = new SolidColorBrush(ColorUtil.Parse(Doc.BoardColor));
            bg.Freeze();
            dc.DrawRectangle(bg, null, board);

            if (Doc.ShowGrid) DrawGrid(dc, board);

            if (Doc.Image != null)
            {
                dc.DrawImage(Doc.Image, Doc.ImageRect);
                var edge = new Pen(new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)), 1 / Zoom);
                edge.Brush.Freeze();
                dc.DrawRectangle(null, edge, Doc.ImageRect);
            }

            var ctx = new RenderCtx
            {
                Source = Doc.Image,
                ImageRect = Doc.ImageRect,
                Zoom = Zoom,
                PixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip
            };

            foreach (var item in Doc.Items)
            {
                if (ReferenceEquals(item, _editingItem)) continue;   // the live TextBox stands in for it
                item.Render(dc, ctx);
            }

            dc.Pop();

            DrawAdorners(dc);
        }

        private void DrawGrid(DrawingContext dc, Rect board)
        {
            double step = 50;
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(28, 0, 0, 0)), 1 / Zoom);
            pen.Brush.Freeze();
            for (double x = 0; x <= board.Width; x += step)
                dc.DrawLine(pen, new Point(x, 0), new Point(x, board.Height));
            for (double y = 0; y <= board.Height; y += step)
                dc.DrawLine(pen, new Point(0, y), new Point(board.Width, y));
        }

        private void DrawAdorners(DrawingContext dc)
        {
            var accent = ColorUtil.Parse("#4A7CFF");

            if (_marquee)
            {
                var a = ToScreen(_marqueeStart);
                var b = ToScreen(_marqueeCurrent);
                var r = new Rect(a, b);
                var fill = new SolidColorBrush(Color.FromArgb(38, accent.R, accent.G, accent.B));
                fill.Freeze();
                var pen = new Pen(new SolidColorBrush(accent), 1) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) };
                pen.Brush.Freeze();
                dc.DrawRectangle(fill, pen, r);
            }

            // Outline every selected object individually so a group reads as a group.
            if (Selection.Count > 0)
            {
                var thin = new Pen(new SolidColorBrush(Color.FromArgb(150, accent.R, accent.G, accent.B)), 1)
                { DashStyle = new DashStyle(new double[] { 3, 2 }, 0) };
                thin.Brush.Freeze();

                foreach (var i in Selection)
                {
                    var b = i.Bounds;
                    if (b.IsEmpty) continue;
                    var tl = ToScreen(new Point(b.Left, b.Top));
                    var br = ToScreen(new Point(b.Right, b.Bottom));
                    dc.DrawRectangle(null, thin, new Rect(tl, br));

                    if (i is GroupItem g)
                    {
                        var child = new Pen(new SolidColorBrush(Color.FromArgb(70, accent.R, accent.G, accent.B)), 1);
                        child.Brush.Freeze();
                        foreach (var c in g.Flatten())
                        {
                            var cb = c.Bounds;
                            if (cb.IsEmpty) continue;
                            dc.DrawRectangle(null, child,
                                new Rect(ToScreen(new Point(cb.Left, cb.Top)), ToScreen(new Point(cb.Right, cb.Bottom))));
                        }
                    }
                }
            }

            var bounds = SelectionBounds();
            if (bounds.IsEmpty) return;

            var stl = ToScreen(new Point(bounds.Left, bounds.Top));
            var sbr = ToScreen(new Point(bounds.Right, bounds.Bottom));
            var box = new Rect(stl, sbr);

            var boxPen = new Pen(new SolidColorBrush(accent), 1.4);
            boxPen.Brush.Freeze();
            dc.DrawRectangle(null, boxPen, box);

            var fillH = Brushes.White;
            var edgeH = new Pen(new SolidColorBrush(accent), 1.4);
            edgeH.Brush.Freeze();
            double hs = HandleScreenSize / 2 + 1;

            foreach (var p in HandlePoints(bounds))
            {
                var sp = ToScreen(p);
                dc.DrawRectangle(fillH, edgeH, new Rect(sp.X - hs, sp.Y - hs, hs * 2, hs * 2));
            }

            // Live size readout while scaling.
            if (_scaleHandle >= 0)
            {
                var label = new FormattedText(
                    $"{Math.Round(bounds.Width)} × {Math.Round(bounds.Height)}",
                    CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                    12, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);

                var lr = new Rect(box.X, box.Y - label.Height - 8, label.Width + 12, label.Height + 6);
                var lbg = new SolidColorBrush(Color.FromArgb(235, 15, 18, 25));
                lbg.Freeze();
                dc.DrawRoundedRectangle(lbg, null, lr, 4, 4);
                dc.DrawText(label, new Point(lr.X + 6, lr.Y + 3));
            }
        }
    }
}
