using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WorkMonitorSwitcher.Model;

namespace WorkMonitorSwitcher.Services
{
    internal sealed class SavedLayoutIdentity
    {
        public string LayoutDeviceName { get; set; } = string.Empty;
        public string StableKey { get; set; } = string.Empty;
        public string DeviceName { get; set; } = string.Empty;
        public string NativeTargetPath { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string MonitorKey { get; set; } = string.Empty;
        public string MonitorId { get; set; } = string.Empty;
        public string InstanceId { get; set; } = string.Empty;
        public string SerialNumber { get; set; } = string.Empty;
    }

    internal sealed class LayoutIdentityFile
    {
        public int Version { get; set; } = 2;
        public DateTimeOffset SavedAtUtc { get; set; } = DateTimeOffset.UtcNow;
        public List<SavedLayoutIdentity> Monitors { get; set; } = new();
    }

    internal static class LayoutIdentityStore
    {
        private const string SidecarSuffix = ".identity.json";

        public static string GetIdentityPath(string layoutPath)
            => $"{layoutPath}{SidecarSuffix}";

        public static IReadOnlyList<SavedLayoutIdentity> Load(string layoutPath)
        {
            if (string.IsNullOrWhiteSpace(layoutPath))
                return Array.Empty<SavedLayoutIdentity>();

            return TryLoadPrimaryIdentityFile(GetIdentityPath(layoutPath), out var monitors)
                ? monitors
                : Array.Empty<SavedLayoutIdentity>();
        }

        public static bool Save(string layoutPath, IEnumerable<DetectedMonitor> detectedMonitors)
            => SaveWithResult(layoutPath, detectedMonitors).Success;

        internal static bool IsValidPrimaryIdentityFile(string identityPath)
            => TryLoadPrimaryIdentityFile(identityPath, out _);

        internal static bool TryLoadPrimaryIdentityFile(
            string identityPath,
            out IReadOnlyList<SavedLayoutIdentity> monitors)
        {
            monitors = Array.Empty<SavedLayoutIdentity>();
            if (string.IsNullOrWhiteSpace(identityPath) || !File.Exists(identityPath))
                return false;

            try
            {
                if (!JsonFilePersistence.TryDeserialize<LayoutIdentityFile>(
                        File.ReadAllText(identityPath),
                        IsValidIdentityFile,
                        out var file) ||
                    file == null)
                {
                    return false;
                }

                monitors = file.Monitors.Select(NormaliseIdentity).ToList();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static PersistenceResult SaveWithResult(
            string layoutPath,
            IEnumerable<DetectedMonitor> detectedMonitors)
        {
            if (string.IsNullOrWhiteSpace(layoutPath))
                return PersistenceResult.Failed("A layout path is required.");
            if (detectedMonitors == null)
                return PersistenceResult.Failed("No detected monitors were supplied.");

            var monitors = detectedMonitors
                .Where(m => m.IsPresent &&
                            m.IsActive &&
                            !string.IsNullOrWhiteSpace(m.DeviceName))
                .Select(m => new SavedLayoutIdentity
                {
                    LayoutDeviceName = m.DeviceName,
                    StableKey = m.StableKey,
                    DeviceName = m.DeviceName,
                    NativeTargetPath = m.NativeTargetPath,
                    Name = m.Name,
                    MonitorKey = m.MonitorKey,
                    MonitorId = m.MonitorId,
                    InstanceId = m.InstanceId,
                    SerialNumber = m.SerialNumber
                })
                .Select(NormaliseIdentity)
                .OrderBy(m => m.LayoutDeviceName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(m => m.StableKey, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (monitors.Count == 0)
                return PersistenceResult.Failed("No active monitor identities were available to save.");

            var identityPath = GetIdentityPath(layoutPath);
            var existing = JsonFilePersistence.Load<LayoutIdentityFile>(identityPath, IsValidIdentityFile);
            if (existing.Success &&
                existing.Value != null &&
                IdentitiesEqual(existing.Value.Monitors, monitors) &&
                (!existing.RecoveredFromBackup || existing.PrimaryRepaired))
            {
                return existing.RecoveredFromBackup
                    ? PersistenceResult.Saved(
                        changed: existing.PrimaryRepaired,
                        warningMessage: existing.WarningMessage)
                    : PersistenceResult.Unchanged();
            }

            var file = new LayoutIdentityFile
            {
                SavedAtUtc = DateTimeOffset.UtcNow,
                Monitors = monitors
            };

            return JsonFilePersistence.Save(identityPath, file, IsValidIdentityFile);
        }

        private static bool IsValidIdentityFile(LayoutIdentityFile file)
        {
            if (file == null ||
                file.Version is < 1 or > 2 ||
                file.Monitors == null ||
                file.Monitors.Count == 0 ||
                file.Monitors.Any(m =>
                    m == null ||
                    string.IsNullOrWhiteSpace(FirstNonBlank(m.LayoutDeviceName, m.DeviceName)) ||
                    string.IsNullOrWhiteSpace(FirstNonBlank(
                        m.StableKey,
                        m.SerialNumber,
                        m.InstanceId,
                        m.MonitorKey,
                        m.MonitorId))))
            {
                return false;
            }

            if (file.Version >= 2 &&
                (file.Monitors.Any(m =>
                     !NativeDisplayProfileCodec.IsStrongTargetPath(m.NativeTargetPath)) ||
                 file.Monitors.Select(m => NativeDisplayProfileCodec.NormaliseTargetPath(m.NativeTargetPath))
                     .Distinct(StringComparer.OrdinalIgnoreCase).Count() != file.Monitors.Count))
            {
                return false;
            }

            return file.Monitors
                .Select(m => MonitorTargetResolver.NormalizeDeviceNameForComparison(
                    FirstNonBlank(m.LayoutDeviceName, m.DeviceName)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() == file.Monitors.Count;
        }

        private static string FirstNonBlank(params string?[] values)
            => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

        private static SavedLayoutIdentity NormaliseIdentity(SavedLayoutIdentity identity)
        {
            identity.LayoutDeviceName ??= string.Empty;
            identity.StableKey ??= string.Empty;
            identity.DeviceName ??= string.Empty;
            identity.NativeTargetPath ??= string.Empty;
            identity.Name ??= string.Empty;
            identity.MonitorKey ??= string.Empty;
            identity.MonitorId ??= string.Empty;
            identity.InstanceId ??= string.Empty;
            identity.SerialNumber ??= string.Empty;
            return identity;
        }

        private static bool IdentitiesEqual(
            IEnumerable<SavedLayoutIdentity> left,
            IEnumerable<SavedLayoutIdentity> right)
        {
            var leftRows = left
                .Where(m => m != null)
                .Select(NormaliseIdentity)
                .OrderBy(m => m.LayoutDeviceName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(m => m.StableKey, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var rightRows = right
                .Where(m => m != null)
                .Select(NormaliseIdentity)
                .OrderBy(m => m.LayoutDeviceName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(m => m.StableKey, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (leftRows.Count != rightRows.Count)
                return false;

            for (int i = 0; i < leftRows.Count; i++)
            {
                var a = leftRows[i];
                var b = rightRows[i];
                if (!StringEquals(a.LayoutDeviceName, b.LayoutDeviceName) ||
                    !StringEquals(a.StableKey, b.StableKey) ||
                    !StringEquals(a.DeviceName, b.DeviceName) ||
                    !StringEquals(a.NativeTargetPath, b.NativeTargetPath) ||
                    !StringEquals(a.Name, b.Name) ||
                    !StringEquals(a.MonitorKey, b.MonitorKey) ||
                    !StringEquals(a.MonitorId, b.MonitorId) ||
                    !StringEquals(a.InstanceId, b.InstanceId) ||
                    !StringEquals(a.SerialNumber, b.SerialNumber))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool StringEquals(string? left, string? right)
            => string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.Ordinal);
    }
}
