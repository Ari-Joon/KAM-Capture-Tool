using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace KamCapture.Interop
{
    /// <summary>
    /// Native chrome, styled dark. Keeping the real title bar means snapping,
    /// maximising and the system menu all behave the way Windows users expect,
    /// which a hand-drawn caption never quite manages.
    /// </summary>
    public static class WindowStyling
    {
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_CAPTION_COLOR = 35;
        private const int DWMWA_BORDER_COLOR = 34;
        private const int DWMWA_TEXT_COLOR = 36;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public static void ApplyDarkChrome(Window window)
        {
            void Apply()
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;

                int on = 1;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));

                int caption = Bgr(0x0F, 0x12, 0x19);
                DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));

                int border = Bgr(0x26, 0x2E, 0x3C);
                DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));

                int text = Bgr(0xE6, 0xEA, 0xF2);
                DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref text, sizeof(int));
            }

            if (window.IsLoaded || new WindowInteropHelper(window).Handle != IntPtr.Zero) Apply();
            else window.SourceInitialized += (_, _) => Apply();
        }

        private static int Bgr(byte r, byte g, byte b) => r | (g << 8) | (b << 16);

        /// <summary>Hide a window from every screen capture on the machine, including our own.</summary>
        public static void ExcludeFromCapture(Window window, bool exclude = true)
        {
            void Apply()
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;
                NativeMethods.SetWindowDisplayAffinity(hwnd,
                    exclude ? NativeMethods.WDA_EXCLUDEFROMCAPTURE : NativeMethods.WDA_NONE);
            }

            if (new WindowInteropHelper(window).Handle != IntPtr.Zero) Apply();
            else window.SourceInitialized += (_, _) => Apply();
        }
    }
}
