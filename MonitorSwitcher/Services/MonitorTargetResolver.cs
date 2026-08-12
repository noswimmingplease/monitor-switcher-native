using System;
using System.Collections.Generic;
using System.Linq;
using WorkMonitorSwitcher.Model;

namespace WorkMonitorSwitcher.Services
{
    internal static class MonitorTargetResolver
    {
        private const int MaxKnownTargets = 8;

        public static string NormaliseTarget(string s)
        {
            var t = (s ?? string.Empty).Trim();
            if (t.Length >= 2 &&
                t.StartsWith("\"", StringComparison.Ordinal) &&
                t.EndsWith("\"", StringComparison.Ordinal))
            {
                t = t[1..^1];
            }
            return t;
        }

        public static string NormalizeDeviceNameForComparison(string? value)
        {
            var t = NormaliseTarget(value ?? string.Empty).Trim();
            if (t.StartsWith("DEV:", StringComparison.OrdinalIgnoreCase))
                t = t[4..].Trim();

            t = t.Replace('/', '\\');
            while (t.Contains("\\\\", StringComparison.Ordinal))
                t = t.Replace("\\\\", "\\");
            return t;
        }

        public static bool IsLikelyDeviceName(string? t)
        {
            if (string.IsNullOrWhiteSpace(t)) return false;
            var v = NormalizeDeviceNameForComparison(t);
            return v.StartsWith(@"\.\DISPLAY", StringComparison.OrdinalIgnoreCase);
        }

        public static bool TargetsEquivalent(string? a, string? b)
        {
            var left = NormaliseTarget(a ?? string.Empty);
            var right = NormaliseTarget(b ?? string.Empty);

            if (IsLikelyDeviceName(left) || IsLikelyDeviceName(right))
            {
                return string.Equals(
                    NormalizeDeviceNameForComparison(left),
                    NormalizeDeviceNameForComparison(right),
                    StringComparison.OrdinalIgnoreCase);
            }

            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        public static void EnsureKnownTargets(
            IDictionary<string, MonitorInfo> aliasMap,
            string stableKey,
            params string?[] candidates)
        {
            if (!aliasMap.TryGetValue(stableKey, out var info))
            {
                info = new MonitorInfo();
                aliasMap[stableKey] = info;
            }

            info.KnownTargets ??= new List<string>();

            for (int i = candidates.Length - 1; i >= 0; i--)
            {
                var c = candidates[i];
                var t = (c ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(t)) continue;

                t = NormaliseTarget(t);

                int existingIndex = info.KnownTargets.FindIndex(x => TargetsEquivalent(x, t));
                if (existingIndex >= 0)
                    info.KnownTargets.RemoveAt(existingIndex);

                info.KnownTargets.Insert(0, t);
            }

            TrimKnownTargets(info.KnownTargets);
        }

        public static void PruneKnownTargets(
            IDictionary<string, MonitorInfo> aliasMap,
            IReadOnlyCollection<DetectedMonitor> detected)
        {
            if (aliasMap.Count == 0) return;

            var liveDeviceOwners = detected
                .Where(d => !string.IsNullOrWhiteSpace(d.DeviceName))
                .Select(d => new
                {
                    Device = NormalizeDeviceNameForComparison(d.DeviceName),
                    d.StableKey
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.Device))
                .GroupBy(x => x.Device, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Select(x => x.StableKey).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
                .ToDictionary(
                    g => g.Key,
                    g => g.First().StableKey,
                    StringComparer.OrdinalIgnoreCase);

            foreach (var kv in aliasMap.ToList())
            {
                var info = kv.Value;
                info.KnownTargets ??= new List<string>();
                var pruned = new List<string>();

                foreach (var raw in info.KnownTargets)
                {
                    var target = NormaliseTarget(raw);
                    if (string.IsNullOrWhiteSpace(target))
                        continue;

                    if (IsLikelyDeviceName(target))
                    {
                        var device = NormalizeDeviceNameForComparison(target);
                        if (string.IsNullOrWhiteSpace(device))
                            continue;

                        if (liveDeviceOwners.TryGetValue(device, out var ownerKey) &&
                            !ownerKey.Equals(kv.Key, StringComparison.OrdinalIgnoreCase))
                            continue;
                    }

                    if (pruned.Any(existing => TargetsEquivalent(existing, target)))
                        continue;

                    pruned.Add(target);
                }

                TrimKnownTargets(pruned);
                info.KnownTargets = pruned;
                aliasMap[kv.Key] = info;
            }
        }

        public static string? ResolveTargetArg(
            string stableKey,
            IReadOnlyCollection<DetectedMonitor> detected,
            IReadOnlyDictionary<string, MonitorInfo> aliasMap)
        {
            var live = detected.FirstOrDefault(d => d.StableKey.Equals(stableKey, StringComparison.OrdinalIgnoreCase));
            if (live != null)
            {
                var liveDevice = NormaliseTarget(live.DeviceName);
                if (!string.IsNullOrWhiteSpace(liveDevice)) return liveDevice;

                var liveName = NormaliseTarget(live.Name);
                if (!string.IsNullOrWhiteSpace(liveName) && IsMonitorNameSafeForStableKey(stableKey, liveName, detected))
                    return liveName;
            }

            if (aliasMap.TryGetValue(stableKey, out var info))
            {
                info.KnownTargets ??= new List<string>();

                var knownName = info.KnownTargets
                    .FirstOrDefault(t => !IsLikelyDeviceName(t) && IsMonitorNameSafeForStableKey(stableKey, t, detected));
                if (!string.IsNullOrWhiteSpace(knownName))
                    return NormaliseTarget(knownName);

                if (!string.IsNullOrWhiteSpace(info.LastDeviceName) &&
                    !IsDeviceNameInUseByOther(stableKey, info.LastDeviceName, detected))
                    return NormaliseTarget(info.LastDeviceName);

                var knownDevice = info.KnownTargets
                    .FirstOrDefault(t => IsLikelyDeviceName(t) && !IsDeviceNameInUseByOther(stableKey, t, detected));
                if (!string.IsNullOrWhiteSpace(knownDevice))
                    return knownDevice;
            }

            return null;
        }

        public static string? ResolveDisableTargetArg(
            string stableKey,
            IReadOnlyCollection<DetectedMonitor> detected,
            IReadOnlyDictionary<string, MonitorInfo> aliasMap)
        {
            var live = detected.FirstOrDefault(d =>
                d.StableKey.Equals(stableKey, StringComparison.OrdinalIgnoreCase) &&
                d.IsPresent &&
                d.IsActive);
            if (live != null)
            {
                var liveDevice = NormaliseTarget(live.DeviceName);
                if (!string.IsNullOrWhiteSpace(liveDevice))
                    return liveDevice;
            }

            return ResolveTargetArg(stableKey, detected, aliasMap);
        }

        public static IReadOnlyList<string> ResolveEnableTargetArgs(
            string stableKey,
            IReadOnlyCollection<DetectedMonitor> detected,
            IReadOnlyDictionary<string, MonitorInfo> aliasMap)
        {
            var candidates = new List<string>();
            void Add(string? raw)
            {
                var t = NormaliseTarget(raw ?? string.Empty);
                if (string.IsNullOrWhiteSpace(t)) return;
                if (candidates.Any(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase))) return;
                candidates.Add(t);
            }

            var live = detected.FirstOrDefault(d => d.StableKey.Equals(stableKey, StringComparison.OrdinalIgnoreCase));
            if (live != null)
            {
                var liveName = NormaliseTarget(live.Name);
                if (!string.IsNullOrWhiteSpace(liveName) &&
                    !IsLikelyDeviceName(liveName) &&
                    IsMonitorNameSafeForStableKey(stableKey, liveName, detected))
                {
                    Add(liveName);
                }

                if (!string.IsNullOrWhiteSpace(live.DeviceName) &&
                    !IsDeviceNameInUseByOther(stableKey, live.DeviceName, detected))
                {
                    Add(live.DeviceName);
                }
            }

            if (aliasMap.TryGetValue(stableKey, out var info))
            {
                info.KnownTargets ??= new List<string>();

                foreach (var knownName in info.KnownTargets.Where(t => !IsLikelyDeviceName(t)))
                {
                    if (IsMonitorNameSafeForStableKey(stableKey, knownName, detected))
                        Add(knownName);
                }

                if (!string.IsNullOrWhiteSpace(info.LastDeviceName) &&
                    !IsDeviceNameInUseByOther(stableKey, info.LastDeviceName, detected))
                {
                    Add(info.LastDeviceName);
                }

                foreach (var knownDevice in info.KnownTargets.Where(t =>
                             IsLikelyDeviceName(t) && !IsDeviceNameInUseByOther(stableKey, t, detected)))
                {
                    Add(knownDevice);
                }
            }

            Add(ResolveTargetArg(stableKey, detected, aliasMap));
            return candidates;
        }

        public static bool IsStableKeyActive(IEnumerable<DetectedMonitor> detected, string stableKey)
        {
            return detected.Any(d =>
                d.StableKey.Equals(stableKey, StringComparison.OrdinalIgnoreCase) &&
                d.IsPresent &&
                d.IsActive);
        }

        private static bool IsDeviceNameInUseByOther(
            string stableKey,
            string? deviceName,
            IEnumerable<DetectedMonitor> detected)
        {
            var device = NormalizeDeviceNameForComparison(deviceName);
            if (string.IsNullOrWhiteSpace(device)) return false;

            var live = detected.FirstOrDefault(d =>
                NormalizeDeviceNameForComparison(d.DeviceName).Equals(device, StringComparison.OrdinalIgnoreCase));
            return live != null && !live.StableKey.Equals(stableKey, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsMonitorNameSafeForStableKey(
            string stableKey,
            string name,
            IEnumerable<DetectedMonitor> detected)
        {
            var target = NormaliseTarget(name);
            if (string.IsNullOrWhiteSpace(target) || IsLikelyDeviceName(target))
                return false;

            var matches = detected
                .Where(d => NormaliseTarget(d.Name).Equals(target, StringComparison.OrdinalIgnoreCase))
                .ToList();

            return matches.Count == 1 &&
                   matches[0].StableKey.Equals(stableKey, StringComparison.OrdinalIgnoreCase);
        }

        private static void TrimKnownTargets(List<string> knownTargets)
        {
            if (knownTargets.Count > MaxKnownTargets)
                knownTargets.RemoveRange(MaxKnownTargets, knownTargets.Count - MaxKnownTargets);
        }
    }
}
