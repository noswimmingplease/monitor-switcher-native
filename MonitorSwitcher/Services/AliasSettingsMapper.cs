using System;
using System.Collections.Generic;
using System.Linq;
using WorkMonitorSwitcher.Model;

namespace WorkMonitorSwitcher.Services
{
    internal static class AliasSettingsMapper
    {
        public static List<AliasViewRow> BuildRows(
            IEnumerable<DetectedMonitor> monitors,
            IReadOnlyDictionary<string, MonitorInfo> aliasMap)
        {
            return monitors
                .Select(monitor =>
                {
                    aliasMap.TryGetValue(monitor.StableKey, out var info);

                    return new AliasViewRow
                    {
                        StableKey = monitor.StableKey,
                        ShortKey = ShortenStableKey(monitor.StableKey),
                        Alias = GetAlias(monitor.StableKey, info),
                        RegistryKey = info?.LastRegistryKey ?? string.Empty,
                        IsPreferredPrimary = info?.IsPreferredPrimary ?? false,
                        IsFallbackPrimary = info?.IsFallbackPrimary ?? false,
                        DeviceName = monitor.DeviceName,
                        MonitorName = monitor.Name,
                        MonitorId = monitor.MonitorId,
                        InstanceId = monitor.InstanceId,
                        SerialNumber = monitor.SerialNumber,
                        KnownTargets = info == null ? string.Empty : string.Join(", ", info.KnownTargets)
                    };
                })
                .ToList();
        }

        public static void ApplyMonitorSettings(
            IDictionary<string, MonitorInfo> aliasMap,
            IEnumerable<string> removedKeys,
            IReadOnlyDictionary<string, string> updatedMappings,
            string? preferredPrimaryKey,
            string? fallbackPrimaryKey)
        {
            if (!string.IsNullOrWhiteSpace(preferredPrimaryKey) &&
                string.Equals(preferredPrimaryKey, fallbackPrimaryKey, StringComparison.OrdinalIgnoreCase))
            {
                fallbackPrimaryKey = null;
            }

            foreach (var removedKey in removedKeys)
                aliasMap.Remove(removedKey);

            foreach (var kvp in updatedMappings)
            {
                if (!aliasMap.TryGetValue(kvp.Key, out var info))
                    info = new MonitorInfo();

                info.Name = kvp.Value ?? info.Name;
                aliasMap[kvp.Key] = info;
            }

            foreach (var key in aliasMap.Keys.ToList())
            {
                var info = aliasMap[key];
                info.IsPreferredPrimary = string.Equals(key, preferredPrimaryKey, StringComparison.OrdinalIgnoreCase);
                info.IsFallbackPrimary = string.Equals(key, fallbackPrimaryKey, StringComparison.OrdinalIgnoreCase);
                aliasMap[key] = info;
            }
        }

        private static string GetAlias(string stableKey, MonitorInfo? info)
            => info != null && !string.IsNullOrWhiteSpace(info.Name)
                ? info.Name
                : stableKey;

        private static string ShortenStableKey(string stableKey)
            => stableKey.Length <= 28 ? stableKey : "\u2026" + stableKey[^28..];
    }
}
