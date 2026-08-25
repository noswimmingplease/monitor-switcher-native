#nullable enable
using System;
using System.Drawing;
using System.IO;

namespace WorkMonitorSwitcher.UI
{
    internal static class AppIcon
    {
        internal const string ResourceName = "WorkMonitorSwitcher.MonitorSwitcher.ico";

        private static readonly Lazy<Icon> BundledIcon = new(Load);

        internal static Icon Current => BundledIcon.Value;

        internal static bool HasBundledResource
            => typeof(AppIcon).Assembly.GetManifestResourceInfo(ResourceName) != null;

        private static Icon Load()
        {
            using Stream? stream = typeof(AppIcon).Assembly.GetManifestResourceStream(ResourceName);
            if (stream == null)
                throw new InvalidOperationException($"The bundled application icon '{ResourceName}' is missing.");

            using var icon = new Icon(stream);
            return (Icon)icon.Clone();
        }
    }
}
