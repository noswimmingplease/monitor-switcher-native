using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WorkMonitorSwitcher.Model;

namespace WorkMonitorSwitcher.Services
{
    /// <summary>
    /// Persists the alias map (friendly names and last-known metadata) to
    /// %APPDATA%\WorkMonitorSwitcher\monitor-aliases.json.
    /// </summary>
    internal sealed class AliasStore
    {
        private readonly string _path;

        public AliasStore(string appDataDir)
        {
            _path = Path.Combine(appDataDir, "monitor-aliases.json");
        }

        public Dictionary<string, MonitorInfo> Load()
        {
            var load = JsonFilePersistence.Load<Dictionary<string, MonitorInfo>>(_path, IsValidMap);
            if (load.Success && load.Value != null)
            {
                var result = new Dictionary<string, MonitorInfo>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in load.Value)
                {
                    var info = kv.Value;
                    info.Name ??= string.Empty;
                    info.KnownTargets = (info.KnownTargets ?? new List<string>())
                        .Where(target => !string.IsNullOrWhiteSpace(target))
                        .ToList();
                    result[kv.Key] = info;
                }

                return result;
            }

            return new Dictionary<string, MonitorInfo>(StringComparer.OrdinalIgnoreCase);
        }

        public void Save(Dictionary<string, MonitorInfo> map)
            => _ = SaveWithResult(map);

        public PersistenceResult SaveWithResult(Dictionary<string, MonitorInfo> map)
        {
            if (map == null)
                return PersistenceResult.Failed("No monitor aliases were supplied.");

            return JsonFilePersistence.Save(_path, map, IsValidMap);
        }

        public string AliasPath => _path;

        private static bool IsValidMap(Dictionary<string, MonitorInfo> map)
            => map != null && map.All(kv => kv.Value != null);
    }
}
