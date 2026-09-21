using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KamCapture.Interop;

namespace KamCapture.Capture
{
    /// <summary>
    /// A frozen copy of the whole virtual desktop at native resolution.
    ///
    /// This is the reason the tool exists. Windows' own snipper hands the
    /// selection back at *logical* size, so on a 150% display a 200x100
    /// selection arrives as a 200x100 image upscaled from 133x67 worth of
    /// detail. Here the desktop is captured once at full device resolution and
    /// every selection is a plain crop out of that buffer, so a region is
    /// always as sharp as the pixels that were actually on the glass.
    /// </summary>
    public sealed class DesktopSnapshot
    {
        public BitmapSource Image { get; }

        /// <summary>Top-left of the virtual desktop in physical pixels (can be negative).</summary>
        public int OriginX { get; }
        public int OriginY { get; }
        public int Width { get; }
        public int Height { get; }

        public DesktopSnapshot(BitmapSource image, int originX, int originY, int width, int height)
        {
            Image = image;
            OriginX = originX;
            OriginY = originY;
            Width = width;
            Height = height;
        }

        /// <summary>Crop a rectangle given in virtual-desktop physical pixels.</summary>
        public BitmapSource Crop(Int32Rect globalRect)
        {
            var local = new Int32Rect(
                globalRect.X - OriginX,
                globalRect.Y - OriginY,
                globalRect.Width,
                globalRect.Height);

            // Clamp so a selection dragged past the edge still produces an image.
            if (local.X < 0) { local.Width += local.X; local.X = 0; }
            if (local.Y < 0) { local.Height += local.Y; local.Y = 0; }
            if (local.X + local.Width > Width) local.Width = Width - local.X;
            if (local.Y + local.Height > Height) local.Height = Height - local.Y;
            if (local.Width < 1) local.Width = 1;
            if (local.Height < 1) local.Height = 1;

            var cropped = new CroppedBitmap(Image, local);
            cropped.Freeze();
            return cropped;
        }
    }

    public static class ScreenGrabber
    {
        /// <summary>
        /// Capture every display in one shot, in real device pixels.
        /// Taken before any overlay is shown, which is also why no part of this
        /// application can ever appear in a still capture.
        /// </summary>
        public static DesktopSnapshot CaptureVirtualDesktop(bool includeCursor = false)
        {
            var (vx, vy, vw, vh) = Screens.VirtualBounds();
            if (vw <= 0 || vh <= 0) { vw = 1920; vh = 1080; }

            IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
            IntPtr memDc = NativeMethods.CreateCompatibleDC(screenDc);
            IntPtr hBitmap = NativeMethods.CreateCompatibleBitmap(screenDc, vw, vh);
            IntPtr old = NativeMethods.SelectObject(memDc, hBitmap);

            try
            {
                // CAPTUREBLT so layered windows (tooltips, acrylic surfaces) come through.
                NativeMethods.BitBlt(memDc, 0, 0, vw, vh, screenDc, vx, vy,
                    NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT);

                if (includeCursor)
                    DrawCursor(memDc, vx, vy);

                var source = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap, IntPtr.Zero, Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());

                // Detach from the GDI handle so it survives the cleanup below.
                var stable = new WriteableBitmap(new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0));
                stable.Freeze();

                return new DesktopSnapshot(stable, vx, vy, vw, vh);
            }
            finally
            {
                NativeMethods.SelectObject(memDc, old);
                NativeMethods.DeleteObject(hBitmap);
                NativeMethods.DeleteDC(memDc);
                NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        private static void DrawCursor(IntPtr hdc, int originX, int originY)
        {
            var ci = new CURSORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<CURSORINFO>() };
            if (!NativeMethods.GetCursorInfo(ref ci)) return;
            if ((ci.flags & NativeMethods.CURSOR_SHOWING) == 0) return;

            IntPtr copy = NativeMethods.CopyIcon(ci.hCursor);
            if (copy == IntPtr.Zero) return;

            try
            {
                if (NativeMethods.GetIconInfo(copy, out ICONINFO info))
                {
                    int x = ci.ptScreenPos.X - originX - info.xHotspot;
                    int y = ci.ptScreenPos.Y - originY - info.yHotspot;
                    NativeMethods.DrawIconEx(hdc, x, y, copy, 0, 0, 0, IntPtr.Zero, NativeMethods.DI_NORMAL);
                    if (info.hbmMask != IntPtr.Zero) NativeMethods.DeleteObject(info.hbmMask);
                    if (info.hbmColor != IntPtr.Zero) NativeMethods.DeleteObject(info.hbmColor);
                }
            }
            finally
            {
                NativeMethods.DestroyIcon(copy);
            }
        }

        /// <summary>
        /// Capture a single window's on-screen rectangle at native resolution.
        /// Cropped out of a fresh desktop grab, so what you get is exactly what
        /// was composited on screen, shadows and rounded corners included.
        /// </summary>
        public static BitmapSource? CaptureWindow(IntPtr hwnd, bool includeShadow = false)
        {
            var rect = WindowFinder.GetWindowBounds(hwnd, includeShadow);
            if (rect.Width < 1 || rect.Height < 1) return null;

            var snap = CaptureVirtualDesktop();
            return snap.Crop(new Int32Rect(rect.X, rect.Y, rect.Width, rect.Height));
        }
    }
}
