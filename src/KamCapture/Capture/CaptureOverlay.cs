using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KamCapture.Interop;
using KamCapture.Services;
using KamCapture.Settings;

namespace KamCapture.Capture
{
    public enum CaptureAction { Cancel, Edit, Copy, Save, SaveAs, Record }

    public sealed class CaptureResult
    {
        public bool Cancelled => Action == CaptureAction.Cancel;
        public CaptureAction Action { get; set; } = CaptureAction.Cancel;
        public BitmapSource? Image { get; set; }
        public Int32Rect Region { get; set; }
        public IntPtr SourceWindow { get; set; }
    }

    /// <summary>
    /// The selection overlay. It draws a frozen copy of the desktop rather than
    /// a transparent window over the live one, which is what makes it both
    /// perfectly smooth and impossible for the tool to photograph itself.
    /// </summary>
    public static class CaptureOverlay
    {
        /// <summary>
        /// Hide the selection overlay from every capture API. Only worth doing
        /// while a recording is running: the rest of the time it would also
        /// stop the user screenshotting this tool with another one.
        /// </summary>
        public static bool HideFromCapture { get; set; }

        /// <param name="forRecording">
        /// Choosing what to record rather than what to capture. The bar then
        /// offers one thing, Start recording, and clicking a window starts it.
        /// </param>
        public static CaptureResult Run(SnipMode mode, AppSettings cfg, DesktopSnapshot? snapshot = null,
                                        bool forRecording = false)
        {
            var snap = snapshot ?? ScreenGrabber.CaptureVirtualDesktop(cfg.IncludeCursor);
            var state = new OverlayState(snap, cfg, mode) { ForRecording = forRecording };

            var windows = new List<OverlayWindow>();
            foreach (var m in Screens.All())
                windows.Add(new OverlayWindow(m, state));

            state.AllWindows = windows;
            foreach (var w in windows) w.Show();
            windows[0].Activate();
            windows[0].Focus();

            // Whole-screen modes need no interaction at all.
            if (mode == SnipMode.FullScreen)
            {
                state.Selection = new Rect(snap.OriginX, snap.OriginY, snap.Width, snap.Height);
                state.Settled = true;
                state.Commit(state.PrimaryAction);
            }
            else if (mode == SnipMode.Monitor)
            {
                // Full screen means the display under the pointer, taken at
                // once; asking for a click on it would confirm a choice
                // already made.
                var m = Screens.FromCursor();
                state.Selection = new Rect(m.X, m.Y, m.Width, m.Height);
                state.Settled = true;
                state.Commit(state.PrimaryAction);
            }

            var frame = new DispatcherFrame();
            state.Finished += () => frame.Continue = false;

            Log.Info($"Overlay open on {windows.Count} display(s)");
            Dispatcher.PushFrame(frame);

            foreach (var w in windows)
            {
                try { w.Close(); } catch { }
            }

            return state.Result;
        }
    }

    internal sealed class OverlayState
    {
        public DesktopSnapshot Snap { get; }
        public AppSettings Cfg { get; }
        public SnipMode Mode { get; set; }
        public bool ForRecording { get; init; }

        /// <summary>What committing the selection means: annotate a capture, or start recording it.</summary>
        public CaptureAction PrimaryAction => ForRecording ? CaptureAction.Record : CaptureAction.Edit;
        public CaptureResult Result { get; } = new CaptureResult();
        public List<OverlayWindow> AllWindows { get; set; } = new();

        public bool Dragging;
        public bool Settled;
        public Point DragStart;
        public Point DragCurrent;
        public Rect Selection = Rect.Empty;
        public Point Cursor;
        public bool CursorKnown;

        public List<Point> Freeform = new();

        public List<MonitorInfo> Monitors = Screens.All();
        public List<CapturableWindow> Windows = new();
        public CapturableWindow? HoverWindow;

        // Adjusting an already-settled selection
        public int ActiveHandle = -1;      // 0..7, or 8 = move body
        public Point MoveAnchor;
        public Rect MoveOriginal;

        public event Action? Finished;

        public OverlayState(DesktopSnapshot snap, AppSettings cfg, SnipMode mode)
        {
            Snap = snap;
            Cfg = cfg;
            Mode = mode;
            if (mode == SnipMode.Window)
                Windows = WindowFinder.Enumerate();
        }

        public MonitorInfo MonitorAt(int px, int py)
        {
            foreach (var m in Monitors)
                if (m.Contains(px, py)) return m;
            foreach (var m in Monitors)
                if (m.IsPrimary) return m;
            return Monitors[0];
        }

        public void Invalidate()
        {
            foreach (var w in AllWindows) w.Surface.InvalidateVisual();
        }

        public void Commit(CaptureAction action)
        {

            if (action == CaptureAction.Cancel)
            {
                Result.Action = CaptureAction.Cancel;
                Finished?.Invoke();
                return;
            }

            var r = Selection;
            if (r.Width < 1 || r.Height < 1)
            {
                Result.Action = CaptureAction.Cancel;
                Finished?.Invoke();
                return;
            }

            var rect = new Int32Rect(
                (int)Math.Round(r.X), (int)Math.Round(r.Y),
                Math.Max(1, (int)Math.Round(r.Width)), Math.Max(1, (int)Math.Round(r.Height)));

            var img = Snap.Crop(rect);

            if (Mode == SnipMode.Freeform && Freeform.Count > 2)
                img = MaskToPolygon(img, Freeform, rect);

            Result.Action = action;
            Result.Image = img;
            Result.Region = rect;
            Result.SourceWindow = HoverWindow?.Handle ?? IntPtr.Zero;
            Finished?.Invoke();
        }

        /// <summary>Knock out everything outside the lasso, keeping full resolution.</summary>
        private static BitmapSource MaskToPolygon(BitmapSource src, List<Point> poly, Int32Rect bounds)
        {
            try
            {
                var geo = new StreamGeometry();
                using (var g = geo.Open())
                {
                    g.BeginFigure(new Point(poly[0].X - bounds.X, poly[0].Y - bounds.Y), true, true);
                    for (int i = 1; i < poly.Count; i++)
                        g.LineTo(new Point(poly[i].X - bounds.X, poly[i].Y - bounds.Y), true, false);
                }
                geo.Freeze();

                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.PushClip(geo);
                    dc.DrawImage(src, new Rect(0, 0, src.PixelWidth, src.PixelHeight));
                    dc.Pop();
                }

                var rtb = new RenderTargetBitmap(src.PixelWidth, src.PixelHeight, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                rtb.Freeze();
                return rtb;
            }
            catch { return src; }
        }
    }

    internal sealed class OverlayWindow : Window
    {
        public MonitorInfo Monitor { get; }
        public OverlaySurface Surface { get; }
        private readonly OverlayState _state;

        public OverlayWindow(MonitorInfo monitor, OverlayState state)
        {
            Monitor = monitor;
            _state = state;

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = false;
            ShowInTaskbar = false;
            Topmost = true;
            Background = Brushes.Black;
            Cursor = Cursors.Cross;
            WindowStartupLocation = WindowStartupLocation.Manual;
            SnapsToDevicePixels = true;
            UseLayoutRounding = true;

            Surface = new OverlaySurface(monitor, state, this);
            Content = Surface;

            SourceInitialized += OnSourceInitialized;
            KeyDown += OnKeyDown;
        }

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;

            // Never let the selection UI leak into a running recording.
            if (CaptureOverlay.HideFromCapture)
                NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE);

            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST,
                Monitor.X, Monitor.Y, Monitor.Width, Monitor.Height,
                NativeMethods.SWP_SHOWWINDOW);
        }

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    if (_state.Settled)
                    {
                        _state.Settled = false;
                        _state.Selection = Rect.Empty;
                        _state.Freeform.Clear();
                        _state.Invalidate();
                    }
                    else _state.Commit(CaptureAction.Cancel);
                    e.Handled = true;
                    break;

                case Key.Enter:
                    if (_state.Selection.Width >= 1) _state.Commit(_state.PrimaryAction);
                    e.Handled = true;
                    break;

                case Key.C when Keyboard.Modifiers == ModifierKeys.Control && !_state.ForRecording:
                    if (_state.Selection.Width >= 1) _state.Commit(CaptureAction.Copy);
                    e.Handled = true;
                    break;

                case Key.S when Keyboard.Modifiers == ModifierKeys.Control && !_state.ForRecording:
                    if (_state.Selection.Width >= 1) _state.Commit(CaptureAction.Save);
                    e.Handled = true;
                    break;

                // F12, as in Office: Ctrl+Shift+S is the region shortcut, and
                // Windows hands it to the hotkey before this window sees it.
                case Key.F12 when !_state.ForRecording:
                    if (_state.Selection.Width >= 1) _state.Commit(CaptureAction.SaveAs);
                    e.Handled = true;
                    break;

                case Key.A when Keyboard.Modifiers == ModifierKeys.Control:
                    _state.Selection = new Rect(_state.Snap.OriginX, _state.Snap.OriginY,
                                                _state.Snap.Width, _state.Snap.Height);
                    _state.Settled = true;
                    _state.Invalidate();
                    e.Handled = true;
                    break;

                case Key.R:
                    SwitchMode(SnipMode.Region); e.Handled = true; break;
                case Key.W:
                    SwitchMode(SnipMode.Window); e.Handled = true; break;
                case Key.F:
                    SwitchMode(SnipMode.Freeform); e.Handled = true; break;
                case Key.M:
                    _state.Cfg.ShowMagnifier = !_state.Cfg.ShowMagnifier;
                    _state.Invalidate(); e.Handled = true; break;
            }

            // Nudge and grow a settled selection with the keyboard.
            if (_state.Settled && !_state.Selection.IsEmpty)
            {
                double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
                var r = _state.Selection;
                bool resize = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
                switch (e.Key)
                {
                    case Key.Left: if (resize) r.Width = Math.Max(1, r.Width - step); else r.X -= step; break;
                    case Key.Right: if (resize) r.Width += step; else r.X += step; break;
                    case Key.Up: if (resize) r.Height = Math.Max(1, r.Height - step); else r.Y -= step; break;
                    case Key.Down: if (resize) r.Height += step; else r.Y += step; break;
                    default: return;
                }
                _state.Selection = r;
                _state.Invalidate();
                e.Handled = true;
            }
        }

        private void SwitchMode(SnipMode mode)
        {
            _state.Mode = mode;
            _state.Settled = false;
            _state.Selection = Rect.Empty;
            _state.Freeform.Clear();
            if (mode == SnipMode.Window && _state.Windows.Count == 0)
                _state.Windows = WindowFinder.Enumerate();
            _state.Invalidate();
        }
    }
}
