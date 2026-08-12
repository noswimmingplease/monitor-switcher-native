using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace WorkMonitorSwitcher.Services
{
    /// <summary>
    /// Small wrapper for DWM (Desktop Window Manager) attributes:
    /// - Dark title bar (Win10 1809+ / Win11)
    /// - Caption color (Win11 22H2+)
    /// Safe to call on older builds (it will no-op).
    /// </summary>
    internal static class DwmInterop
    {
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19; // Win10 1809/1903
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20; // Win10 1909+ / Win11
        private const int DWMWA_CAPTION_COLOR = 35; // Win11 22H2+

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(
            IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        /// <summary>
        /// Toggle native dark title bar. Works on Win10 1809+ and Win11.
        /// </summary>
        public static void SetDarkTitleBar(IntPtr handle, bool dark)
        {
            try
            {
                int useDark = dark ? 1 : 0;
                _ = DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));
                _ = DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref useDark, sizeof(int));
            }
            catch
            {
                // ignore if not supported
            }
        }

        /// <summary>
        /// Set caption (title bar) color on Windows 11 (no-op elsewhere).
        /// </summary>
        public static void SetCaptionColor(IntPtr handle, Color color)
        {
            try
            {
                // DWM expects COLORREF (0x00BBGGRR) packed into int.
                int argb = ColorTranslator.ToWin32(Color.FromArgb(color.A, color.R, color.G, color.B));
                _ = DwmSetWindowAttribute(handle, DWMWA_CAPTION_COLOR, ref argb, sizeof(int));
            }
            catch
            {
                // ignore if not supported
            }
        }
    }
}
