using System;
using System.Drawing;
using Microsoft.Win32;

namespace WorkMonitorSwitcher.Services
{
    /// <summary>
    /// Reads system theme preferences:
    /// - AppsUseLightTheme (light/dark preference for apps)
    /// - Accent color (ColorizationColor)
    /// </summary>
    internal static class WindowsTheme
    {
        /// <summary>
        /// Returns true if Windows is set to "Light" for apps; false for dark.
        /// Defaults to light when registry is unavailable.
        /// </summary>
        public static bool AppsUseLightTheme()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                if (key?.GetValue("AppsUseLightTheme") is int v)
                    return v != 0;
            }
            catch { }
            return true;
        }

        /// <summary>
        /// Returns the user's Windows accent color, or null if unavailable.
        /// </summary>
        public static Color? AccentColor()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
                if (key?.GetValue("ColorizationColor") is int abgr)
                {
                    // Registry stores ABGR; convert to ARGB
                    byte a = (byte)((abgr >> 24) & 0xFF);
                    byte r = (byte)(abgr & 0xFF);
                    byte g = (byte)((abgr >> 8) & 0xFF);
                    byte b = (byte)((abgr >> 16) & 0xFF);
                    if (a == 0) a = 255;
                    return Color.FromArgb(a, r, g, b);
                }
            }
            catch { }
            return null;
        }
    }
}
