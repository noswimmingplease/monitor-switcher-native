using System;
using System.Collections.Generic;

namespace WorkMonitorSwitcher.Model
{
    // Persistent per-user UI settings
    public class UiSettings
    {
        public bool DarkMode { get; set; }
        public int WindowX { get; set; }
        public int WindowY { get; set; }
        public int WindowWidth { get; set; }
        public int WindowHeight { get; set; }
        public bool AlwaysOnTop { get; set; }   // default false
        public bool MinimizeToTray { get; set; }
        public bool StartWithWindows { get; set; }
        public bool ConfirmBeforeDisable { get; set; } = true;
        public bool AutoSaveLayoutBeforeDisable { get; set; }
        public bool RestoreLayoutOnStartup { get; set; }
        public string SelectedLayoutProfile { get; set; } = "Default";

    }

    // Saved metadata for a given monitor stable key
    public class MonitorInfo
    {
        public string Name { get; set; } = string.Empty;   // Friendly alias shown in UI

        // Last-seen identifiers (kept for backward compatibility / quick access)
        public string? LastDeviceName { get; set; }        // Last seen \\.\DISPLAYn
        public string? LastRegistryKey { get; set; }       // Last seen registry path (for copy)
        public string? LastSerialNumber { get; set; }      // Last seen EDID serial
        public string? LastInstanceId { get; set; }        // Last seen PnP instance id
        public string? LastMonitorId { get; set; }         // Last seen EDID model/product

        // Optional UI hints
        public int? PreferredOrder { get; set; }           // Optional pin for row ordering
        public int? LastKnownX { get; set; }               // Last known X position (for sorting)

        // Historical display identifiers retained for alias migration and
        // diagnostics. Native topology changes never guess from this list.
        public List<string> KnownTargets { get; set; } = new List<string>();

        // User can mark this alias as the preferred primary monitor
        public bool IsPreferredPrimary { get; set; }

        // User can mark this alias as the preferred fallback when disabling the current primary
        public bool IsFallbackPrimary { get; set; }
    }

    // A detected (or reconstructed) monitor instance used by the app
    public class DetectedMonitor
    {
        public string Name { get; set; } = string.Empty;        // Current display or target name
        public string DeviceName { get; set; } = string.Empty;  // \\.\DISPLAYn
        public string NativeTargetPath { get; set; } = string.Empty; // CCD monitor device-interface path
        public string MonitorKey { get; set; } = string.Empty;  // Registry path if available
        public string MonitorId { get; set; } = string.Empty;   // EDID model/product
        public string InstanceId { get; set; } = string.Empty;  // PnP instance path
        public string SerialNumber { get; set; } = string.Empty;// EDID serial
        public string StableKey { get; set; } = string.Empty;   // Our persistent key
        public bool IsActive { get; set; }                      // Currently enabled/visible
        public bool IsPresent { get; set; }                     // Currently connected/present
        public int PositionX { get; set; }                      // X position for sorting
    }

    // Row used in the Settings grid
    public class AliasViewRow
    {
        public string StableKey { get; set; } = string.Empty;   // Full stable key (shown in tooltip)
        public string ShortKey { get; set; } = string.Empty;    // Abbreviated display in grid
        public string RegistryKey { get; set; } = string.Empty; // Copyable registry path
        public string RegistryKeyShort => ShortenRegistryKey(RegistryKey);
        public string? Alias { get; set; }                      // Editable alias
        public bool IsPreferredPrimary { get; set; }
        public bool IsFallbackPrimary { get; set; }
        public string DeviceName { get; set; } = string.Empty;
        public string MonitorName { get; set; } = string.Empty;
        public string MonitorId { get; set; } = string.Empty;
        public string InstanceId { get; set; } = string.Empty;
        public string SerialNumber { get; set; } = string.Empty;
        public string KnownTargets { get; set; } = string.Empty;
        public string StableKeyFull => StableKey;

        private static string ShortenRegistryKey(string raw)
        {
            var value = (raw ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            const string classMarker = @"\Control\Class\";
            var markerIndex = value.IndexOf(classMarker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex >= 0)
                return value[(markerIndex + classMarker.Length)..].TrimStart('\\');

            const string machinePrefix = @"\Registry\Machine\";
            if (value.StartsWith(machinePrefix, StringComparison.OrdinalIgnoreCase))
                value = value[machinePrefix.Length..];

            var parts = value
                .Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);

            return parts.Length >= 2
                ? string.Join("\\", parts[^2], parts[^1])
                : value;
        }
    }
}
