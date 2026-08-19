using System;
using System.Collections.Generic;
using System.Linq;
using WorkMonitorSwitcher.Model;

namespace WorkMonitorSwitcher.Services
{
    internal static class MonitorOrderService
    {
        public static bool TryApplyVisibleOrder(
            IDictionary<string, MonitorInfo> aliasMap,
            IReadOnlyList<string> visibleStableKeys,
            out string errorMessage)
        {
            errorMessage = string.Empty;
            if (aliasMap == null)
            {
                errorMessage = "No monitor metadata was supplied.";
                return false;
            }

            var requested = visibleStableKeys?
                .Select(key => (key ?? string.Empty).Trim())
                .ToList() ?? new List<string>();
            if (requested.Count == 0)
            {
                errorMessage = "No visible monitor order was supplied.";
                return false;
            }

            if (requested.Any(string.IsNullOrWhiteSpace) ||
                requested.Distinct(StringComparer.OrdinalIgnoreCase).Count() != requested.Count)
            {
                errorMessage = "The visible monitor order contains a blank or duplicate identity.";
                return false;
            }

            if (requested.Any(key => !aliasMap.ContainsKey(key)))
            {
                errorMessage = "The visible monitor order contains an unknown monitor identity.";
                return false;
            }

            var canonical = aliasMap
                .OrderBy(pair => pair.Value.PreferredOrder ?? int.MaxValue)
                .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => pair.Key)
                .ToList();
            var requestedSet = requested.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var visibleSlots = canonical
                .Select((key, index) => (key, index))
                .Where(item => requestedSet.Contains(item.key))
                .Select(item => item.index)
                .ToList();

            if (visibleSlots.Count != requested.Count)
            {
                errorMessage = "The visible monitor order could not be reconciled with saved monitor metadata.";
                return false;
            }

            for (int index = 0; index < requested.Count; index++)
                canonical[visibleSlots[index]] = requested[index];

            for (int index = 0; index < canonical.Count; index++)
                aliasMap[canonical[index]].PreferredOrder = index;

            return true;
        }
    }
}
