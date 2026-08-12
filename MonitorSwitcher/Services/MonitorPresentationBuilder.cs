using System;
using System.Collections.Generic;
using System.Linq;
using WorkMonitorSwitcher.Model;

namespace WorkMonitorSwitcher.Services
{
    internal static class MonitorPresentationBuilder
    {
        public static List<DetectedMonitor> Build(
            IReadOnlyCollection<DetectedMonitor> detected,
            IReadOnlyDictionary<string, MonitorInfo> aliasMap)
        {
            var presentByKey = BuildDetectedMonitorLookup(detected);
            var allKeys = new HashSet<string>(presentByKey.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (var key in aliasMap.Keys) allKeys.Add(key);

            var list = new List<DetectedMonitor>();
            foreach (var key in allKeys)
            {
                if (presentByKey.TryGetValue(key, out var d))
                {
                    list.Add(d);
                }
                else
                {
                    var info = aliasMap.TryGetValue(key, out var mi) ? mi : new MonitorInfo();
                    list.Add(new DetectedMonitor
                    {
                        StableKey = key,
                        Name = GetAliasFor(aliasMap, key),
                        DeviceName = info.LastDeviceName ?? string.Empty,
                        MonitorKey = info.LastRegistryKey ?? string.Empty,
                        MonitorId = string.Empty,
                        InstanceId = string.Empty,
                        SerialNumber = string.Empty,
                        IsActive = false,
                        IsPresent = false,
                        PositionX = info.LastKnownX ?? 0
                    });
                }
            }

            return list
                .OrderBy(m => GetPreferredOrder(aliasMap, m.StableKey))
                .ThenBy(m => AliasHintRank(GetAliasFor(aliasMap, m.StableKey)))
                .ThenBy(m => m.PositionX)
                .ThenBy(m => GetAliasFor(aliasMap, m.StableKey), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        internal static Dictionary<string, DetectedMonitor> BuildDetectedMonitorLookup(
            IEnumerable<DetectedMonitor> detected)
        {
            var result = new Dictionary<string, DetectedMonitor>(StringComparer.OrdinalIgnoreCase);

            foreach (var group in detected
                         .Where(d => !string.IsNullOrWhiteSpace(d.StableKey))
                         .GroupBy(d => d.StableKey, StringComparer.OrdinalIgnoreCase))
            {
                var selected = group
                    .OrderByDescending(d => d.IsPresent && d.IsActive)
                    .ThenByDescending(d => d.IsPresent)
                    .ThenByDescending(DetectionCompletenessScore)
                    .ThenBy(d => d.PositionX)
                    .First();

                result[group.Key] = selected;
            }

            return result;
        }

        internal static int DetectionCompletenessScore(DetectedMonitor monitor)
        {
            int score = 0;
            if (!string.IsNullOrWhiteSpace(monitor.SerialNumber)) score += 8;
            if (!string.IsNullOrWhiteSpace(monitor.InstanceId)) score += 4;
            if (!string.IsNullOrWhiteSpace(monitor.MonitorKey)) score += 2;
            if (!string.IsNullOrWhiteSpace(monitor.MonitorId)) score += 1;
            return score;
        }

        private static int GetPreferredOrder(IReadOnlyDictionary<string, MonitorInfo> aliasMap, string stableKey)
        {
            if (aliasMap.TryGetValue(stableKey, out var info) && info.PreferredOrder.HasValue)
                return info.PreferredOrder.Value;
            return int.MaxValue - 100000;
        }

        private static int AliasHintRank(string alias)
        {
            if (alias.Contains("Left", StringComparison.OrdinalIgnoreCase)) return 0;
            if (alias.Contains("Middle", StringComparison.OrdinalIgnoreCase)) return 1;
            if (alias.Contains("Centre", StringComparison.OrdinalIgnoreCase)) return 1;
            if (alias.Contains("Right", StringComparison.OrdinalIgnoreCase)) return 2;
            return 50;
        }

        private static string GetAliasFor(IReadOnlyDictionary<string, MonitorInfo> aliasMap, string stableKey)
        {
            return aliasMap.TryGetValue(stableKey, out var info) && !string.IsNullOrWhiteSpace(info.Name)
                ? info.Name
                : stableKey;
        }
    }
}
