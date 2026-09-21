using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using KamCapture.Interop;

namespace KamCapture.Capture
{
    public sealed class CapturableWindow
    {
        public IntPtr Handle { get; init; }
        public string Title { get; init; } = "";
        public string ProcessName { get; init; } = "";
        public int X { get; init; }
        public int Y { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }

        public int Right => X + Width;
        public int Bottom => Y + Height;
        public bool Contains(int px, int py) => px >= X && px < Right && py >= Y && py < Bottom;
        public long Area => (long)Width * Height;

        public string Display => string.IsNullOrWhiteSpace(ProcessName)
            ? Title
            : $"{Title}  —  {ProcessName}";
    }

    public static class WindowFinder
    {
        /// <summary>
        /// The window's real visible bounds. GetWindowRect includes the invisible
        /// resize border DWM adds, which is why naive window captures come back
        /// with a transparent margin; the extended frame bounds do not.
        /// </summary>
        public static (int X, int Y, int Width, int Height) GetWindowBounds(IntPtr hwnd, bool includeShadow = false)
        {
            RECT r;
            if (!includeShadow &&
                NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
                    out r, Marshal.SizeOf<RECT>()) == 0 && r.Width > 0)
            {
                return (r.Left, r.Top, r.Width, r.Height);
            }

            if (NativeMethods.GetWindowRect(hwnd, out r))
                return (r.Left, r.Top, r.Width, r.Height);

            return (0, 0, 0, 0);
        }

        private static bool IsCloaked(IntPtr hwnd)
        {
            // UWP windows parked off-screen report visible but are cloaked by DWM.
            if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED,
                    out int cloaked, sizeof(int)) == 0)
                return cloaked != 0;
            return false;
        }

        /// <summary>
        /// Top-level windows worth offering, in z-order: topmost first.
        /// </summary>
        public static List<CapturableWindow> Enumerate(IntPtr excludeOwn = default)
        {
            var result = new List<CapturableWindow>();
            IntPtr shell = NativeMethods.GetShellWindow();

            NativeMethods.EnumWindowsProc cb = (hwnd, _) =>
            {
                if (hwnd == shell) return true;
                if (excludeOwn != IntPtr.Zero && hwnd == excludeOwn) return true;
                if (!NativeMethods.IsWindowVisible(hwnd)) return true;
                if (NativeMethods.IsIconic(hwnd)) return true;
                if (NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT) != hwnd) return true;
                if (IsCloaked(hwnd)) return true;

                int ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
                if ((ex & NativeMethods.WS_EX_TOOLWINDOW) != 0) return true;

                int len = NativeMethods.GetWindowTextLength(hwnd);
                if (len == 0) return true;
                var sb = new StringBuilder(len + 2);
                NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
                string title = sb.ToString().Trim();
                if (title.Length == 0) return true;

                var (x, y, w, h) = GetWindowBounds(hwnd);
                if (w < 24 || h < 24) return true;

                string proc = "";
                try
                {
                    NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
                    using var p = Process.GetProcessById((int)pid);
                    proc = p.ProcessName;
                }
                catch { /* process may have exited, or be protected */ }

                result.Add(new CapturableWindow
                {
                    Handle = hwnd,
                    Title = title,
                    ProcessName = proc,
                    X = x, Y = y, Width = w, Height = h
                });
                return true;
            };

            NativeMethods.EnumWindows(cb, IntPtr.Zero);
            GC.KeepAlive(cb);
            return result;
        }

        /// <summary>
        /// The window under a point. The enumeration is already in z-order, so
        /// the first hit is the one the user can actually see.
        /// </summary>
        public static CapturableWindow? FromPoint(List<CapturableWindow> windows, int px, int py)
        {
            foreach (var w in windows)
                if (w.Contains(px, py)) return w;
            return null;
        }
    }
}
