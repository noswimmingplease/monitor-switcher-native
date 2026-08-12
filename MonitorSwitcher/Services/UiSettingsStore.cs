using System;
using System.IO;
using WorkMonitorSwitcher.Model;

namespace WorkMonitorSwitcher.Services
{
    /// <summary>
    /// Persists UiSettings to a JSON file in %APPDATA%\WorkMonitorSwitcher\ui-settings.json.
    /// On first run (missing file), it defaults DarkMode from the Windows app theme.
    /// </summary>
    internal sealed class UiSettingsStore
    {
        private readonly string _path;

        public UiSettingsStore(string appDataDir)
        {
            _path = Path.Combine(appDataDir, "ui-settings.json");
        }

        public UiSettings LoadOrDefault()
        {
            var load = JsonFilePersistence.Load<UiSettings>(_path, settings => settings != null);
            if (load.Success && load.Value != null)
                return load.Value;

            // First run / corrupted file: default to system theme (light/dark)
            return new UiSettings
            {
                DarkMode = !WindowsTheme.AppsUseLightTheme()
            };
        }

        public void Save(UiSettings settings)
            => _ = SaveWithResult(settings);

        public PersistenceResult SaveWithResult(UiSettings settings)
        {
            if (settings == null)
                return PersistenceResult.Failed("No UI settings were supplied.");

            return JsonFilePersistence.Save(_path, settings, value => value != null);
        }

        public string SettingsPath => _path;
    }
}
