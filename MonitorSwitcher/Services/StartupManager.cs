using System;
using System.IO;
using Microsoft.Win32;

namespace WorkMonitorSwitcher.Services
{
    internal enum StartupRegistrationState
    {
        Missing,
        CurrentExecutable,
        OtherExecutable
    }

    internal static class StartupManager
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "MonitorSwitcher";

        public static bool IsEnabled()
            => GetRegistrationState() == StartupRegistrationState.CurrentExecutable;

        /// <summary>
        /// Reads MonitorSwitcher's current-user Run entry without changing it.
        /// OtherExecutable means an app-owned entry exists, but it does not point
        /// at this running executable and should therefore be retargeted when the
        /// user enables startup or removed when they disable it.
        /// </summary>
        public static StartupRegistrationState GetRegistrationState()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
                var command = key?.GetValue(ValueName) as string;
                if (string.IsNullOrWhiteSpace(command))
                    return StartupRegistrationState.Missing;

                var executablePath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(executablePath))
                    return StartupRegistrationState.OtherExecutable;

                // This query must remain read-only. In particular, launching a portable,
                // test, or rollback copy must not silently retarget an existing startup entry.
                return ClassifyCommand(command, executablePath);
            }
            catch
            {
                return StartupRegistrationState.Missing;
            }
        }

        public static void SetEnabled(bool enabled, string executablePath)
            => _ = SetEnabledWithResult(enabled, executablePath);

        public static PersistenceResult SetEnabledWithResult(bool enabled, string executablePath)
        {
            string expectedPath = string.Empty;
            string expectedCommand = string.Empty;
            if (enabled)
            {
                if (string.IsNullOrWhiteSpace(executablePath))
                    return PersistenceResult.Failed("An executable path is required to enable startup.");

                try
                {
                    expectedPath = Path.GetFullPath(executablePath);
                    expectedCommand = BuildCommand(expectedPath);
                }
                catch (Exception ex)
                {
                    return PersistenceResult.Failed($"The startup executable path is invalid: {ex.Message}");
                }
            }

            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
                if (key == null)
                    return PersistenceResult.Failed("Windows did not provide access to the current-user Run key.");

                if (enabled)
                {
                    var currentCommand = key.GetValue(ValueName) as string;
                    if (IsCanonicalCommand(currentCommand, expectedPath))
                        return PersistenceResult.Unchanged();

                    key.SetValue(ValueName, expectedCommand, RegistryValueKind.String);
                    var savedCommand = key.GetValue(ValueName) as string;
                    return IsCanonicalCommand(savedCommand, expectedPath)
                        ? PersistenceResult.Saved()
                        : PersistenceResult.Failed("Windows did not retain the expected startup command.");
                }

                if (key.GetValue(ValueName) == null)
                    return PersistenceResult.Unchanged();

                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return key.GetValue(ValueName) == null
                    ? PersistenceResult.Saved()
                    : PersistenceResult.Failed("Windows did not remove the startup command.");
            }
            catch (Exception ex)
            {
                return PersistenceResult.Failed($"Unable to update the startup command: {ex.Message}");
            }
        }

        internal static bool IsCommandForExecutable(string? command, string executablePath)
        {
            if (string.IsNullOrWhiteSpace(command) || string.IsNullOrWhiteSpace(executablePath))
                return false;

            try
            {
                var expectedPath = Path.GetFullPath(executablePath);
                var trimmed = command.Trim();

                string commandPath;
                if (trimmed.StartsWith('"'))
                {
                    var closingQuote = trimmed.IndexOf('"', 1);
                    if (closingQuote <= 1 || !string.IsNullOrWhiteSpace(trimmed[(closingQuote + 1)..]))
                        return false;

                    commandPath = trimmed[1..closingQuote];
                }
                else
                {
                    // Accept a path-only legacy value. SetEnabledWithResult will canonicalise its quoting.
                    commandPath = trimmed;
                }

                return Path.GetFullPath(commandPath)
                    .Equals(expectedPath, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        internal static StartupRegistrationState ClassifyCommand(
            string? command,
            string executablePath)
        {
            if (string.IsNullOrWhiteSpace(command))
                return StartupRegistrationState.Missing;

            return IsCommandForExecutable(command, executablePath)
                ? StartupRegistrationState.CurrentExecutable
                : StartupRegistrationState.OtherExecutable;
        }

        internal static bool IsCanonicalCommand(string? command, string executablePath)
            => !string.IsNullOrWhiteSpace(command) &&
               string.Equals(command.Trim(), BuildCommand(Path.GetFullPath(executablePath)), StringComparison.OrdinalIgnoreCase);

        private static string BuildCommand(string executablePath)
            => $"\"{executablePath}\"";
    }
}
