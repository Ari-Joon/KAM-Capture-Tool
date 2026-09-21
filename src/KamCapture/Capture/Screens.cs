using System;
using System.Collections.Generic;
using KamCapture.Interop;

namespace KamCapture.Capture
{
    /// <summary>A physical display, measured in real device pixels.</summary>
    public sealed class MonitorInfo
    {
        public IntPtr Handle { get; init; }
        public string DeviceName { get; init; } = "";
        public bool IsPrimary { get; init; }

        /// <summary>Bounds in virtual-desktop physical pixels.</summary>
        public int X { get; init; }
        public int Y { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }

        public int WorkX { get; init; }
        public int WorkY { get; init; }
        public int WorkWidth { get; init; }
        public int WorkHeight { get; init; }

        /// <summary>Effective DPI. 96 = 100%, 144 = 150%, 192 = 200%.</summary>
        public uint Dpi { get; init; } = 96;

        public double Scale => Dpi / 96.0;
        public int Right => X + Width;
        public int Bottom => Y + Height;

        public bool Contains(int px, int py) => px >= X && px < Right && py >= Y && py < Bottom;

        public string Describe() =>
            $"{Width} x {Height}" + (IsPrimary ? "  (primary)" : "");
    }

    public static class Screens
    {
        /// <summary>The whole virtual desktop in physical pixels.</summary>
        public static (int X, int Y, int Width, int Height) VirtualBounds()
        {
            return (
                NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN),
                NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN),
                NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN),
                NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN));
        }

        public static List<MonitorInfo> All()
        {
            var list = new List<MonitorInfo>();

            NativeMethods.MonitorEnumProc cb = (IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data) =>
            {
                var mi = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
                if (!NativeMethods.GetMonitorInfo(hMonitor, ref mi)) return true;

                uint dpi = 0;
                try
                {
                    // MDT_EFFECTIVE_DPI = 0
                    if (NativeMethods.GetDpiForMonitor(hMonitor, 0, out uint dx, out _) == 0 && dx > 0)
                        dpi = dx;
                }
                catch (DllNotFoundException) { }
                catch (EntryPointNotFoundException) { }

                if (dpi == 0)
                {
                    // Better a system-wide answer than silently pretending 100%.
                    try { dpi = NativeMethods.GetDpiForSystem(); } catch { }
                }
                if (dpi == 0) dpi = 96;

                list.Add(new MonitorInfo
                {
                    Handle = hMonitor,
                    DeviceName = mi.szDevice ?? "",
                    IsPrimary = (mi.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0,
                    X = mi.rcMonitor.Left,
                    Y = mi.rcMonitor.Top,
                    Width = mi.rcMonitor.Width,
                    Height = mi.rcMonitor.Height,
                    WorkX = mi.rcWork.Left,
                    WorkY = mi.rcWork.Top,
                    WorkWidth = mi.rcWork.Width,
                    WorkHeight = mi.rcWork.Height,
                    Dpi = dpi
                });
                return true;
            };

            NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);
            GC.KeepAlive(cb);

            if (list.Count == 0)
            {
                var (vx, vy, vw, vh) = VirtualBounds();
                list.Add(new MonitorInfo
                {
                    DeviceName = "DISPLAY",
                    IsPrimary = true,
                    X = vx, Y = vy, Width = vw, Height = vh,
                    WorkX = vx, WorkY = vy, WorkWidth = vw, WorkHeight = vh
                });
            }
            return list;
        }

        public static MonitorInfo FromPoint(int px, int py)
        {
            var all = All();
            foreach (var m in all)
                if (m.Contains(px, py)) return m;
            foreach (var m in all)
                if (m.IsPrimary) return m;
            return all[0];
        }

        public static MonitorInfo Primary()
        {
            var all = All();
            foreach (var m in all)
                if (m.IsPrimary) return m;
            return all[0];
        }

        public static MonitorInfo FromCursor()
        {
            NativeMethods.GetCursorPos(out POINT p);
            return FromPoint(p.X, p.Y);
        }
    }
}
