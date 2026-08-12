using System;
using System.Collections.Generic;
using System.Linq;
using WorkMonitorSwitcher.Model;

namespace WorkMonitorSwitcher.Services
{
    internal static class PrimaryMonitorPreference
    {
        public static string? ResolveConfiguredFallbackDeviceName(
            string? excludeDeviceName,
            IReadOnlyCollection<DetectedMonitor> detected,
            IReadOnlyDictionary<string, MonitorInfo> aliasMap)
        {
            var fallback = aliasMap.FirstOrDefault(kv => kv.Value.IsFallbackPrimary);
            if (string.IsNullOrWhiteSpace(fallback.Key))
                return null;

            var live = detected.FirstOrDefault(d =>
                d.StableKey.Equals(fallback.Key, StringComparison.OrdinalIgnoreCase) &&
                d.IsPresent &&
                d.IsActive &&
                !string.IsNullOrWhiteSpace(d.DeviceName) &&
                !MonitorTargetResolver.TargetsEquivalent(d.DeviceName, excludeDeviceName));

            return live?.DeviceName;
        }

        public static IReadOnlyList<string> ResolveAutomaticFallbackDeviceNames(
            string? excludeDeviceName,
            IReadOnlyCollection<DetectedMonitor> detected)
        {
            return detected
                .Where(d => d.IsPresent && d.IsActive)
                .Where(d => !string.IsNullOrWhiteSpace(d.DeviceName) &&
                            !MonitorTargetResolver.TargetsEquivalent(d.DeviceName, excludeDeviceName))
                .OrderBy(d => d.PositionX)
                .ThenBy(d => d.DeviceName, StringComparer.OrdinalIgnoreCase)
                .Select(d => d.DeviceName)
                .ToList();
        }

        public static string? ResolvePreferredPrimaryTarget(
            IReadOnlyCollection<DetectedMonitor> detected,
            IReadOnlyDictionary<string, MonitorInfo> aliasMap)
        {
            var primary = aliasMap.FirstOrDefault(kv => kv.Value.IsPreferredPrimary);
            if (string.IsNullOrWhiteSpace(primary.Key))
                return null;

            var activePrimary = detected.FirstOrDefault(d =>
                d.IsPresent &&
                d.IsActive &&
                d.StableKey.Equals(primary.Key, StringComparison.OrdinalIgnoreCase));
            if (activePrimary == null)
                return null;

            return MonitorTargetResolver.ResolveTargetArg(primary.Key, detected, aliasMap);
        }

        public static string? ResolveLeftMostActiveTarget(
            IReadOnlyCollection<DetectedMonitor> detected,
            IReadOnlyDictionary<string, MonitorInfo> aliasMap)
        {
            var firstActive = detected
                .Where(d => d.IsPresent && d.IsActive)
                .OrderBy(d => d.PositionX)
                .FirstOrDefault();

            return firstActive == null
                ? null
                : MonitorTargetResolver.ResolveTargetArg(firstActive.StableKey, detected, aliasMap);
        }
    }
}
