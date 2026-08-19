using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using WorkMonitorSwitcher.Model;

namespace WorkMonitorSwitcher.Services
{
    internal sealed record MonitorDetectionResult(
        List<DetectedMonitor> Monitors,
        bool UsedScreenFallback,
        string WarningMessage);

    /// <summary>
    /// Detects connected displays through the Windows CCD APIs.
    /// </summary>
    internal sealed class DetectionService
    {
        private readonly Action<string>? _diagnosticsLog;
        private readonly SemaphoreSlim _detectionGate = new(1, 1);
        private volatile bool _lastDetectionUsedScreenFallback;
        private string _lastWarning = string.Empty;

        public DetectionService(Action<string>? diagnosticsLog = null)
        {
            _diagnosticsLog = diagnosticsLog;
        }

        public List<DetectedMonitor> Detect()
        {
            _detectionGate.Wait();
            try
            {
                return DetectCore(CancellationToken.None).Monitors;
            }
            finally
            {
                _detectionGate.Release();
            }
        }

        public async Task<List<DetectedMonitor>> DetectAsync(CancellationToken cancellationToken = default)
            => (await DetectWithStatusAsync(cancellationToken).ConfigureAwait(false)).Monitors;

        public async Task<MonitorDetectionResult> DetectWithStatusAsync(
            CancellationToken cancellationToken = default)
        {
            await _detectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await Task.Run(
                        () => DetectCore(cancellationToken),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                _detectionGate.Release();
            }
        }

        public bool LastDetectionUsedScreenFallback => _lastDetectionUsedScreenFallback;
        public string LastWarning => Volatile.Read(ref _lastWarning);

        private MonitorDetectionResult DetectCore(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _lastDetectionUsedScreenFallback = false;
            Volatile.Write(ref _lastWarning, string.Empty);

            try
            {
                var nativeResult = NativeDisplayDetection.Detect(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                if (nativeResult.Monitors.Count > 0)
                {
                    AssignUniqueStableKeys(nativeResult.Monitors);

                    var missingActiveDevices = FindMissingActiveScreenDevices(nativeResult.Monitors);
                    if (missingActiveDevices.Count == 0)
                    {
                        if (!string.IsNullOrWhiteSpace(nativeResult.WarningMessage))
                            LogDetectionWarning(nativeResult.WarningMessage);

                        return CompleteDetection(nativeResult.Monitors, usedScreenFallback: false);
                    }

                    LogDetectionWarning(
                        "Windows display configuration was incomplete; missing active device(s): " +
                        string.Join(", ", missingActiveDevices) + ".");
                }
                else
                {
                    LogDetectionWarning(
                        string.IsNullOrWhiteSpace(nativeResult.WarningMessage)
                            ? "Windows display configuration returned no connected displays."
                            : nativeResult.WarningMessage);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogDetectionWarning($"Native monitor detection failed: {ex.Message}");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var fallback = DetectFromScreenApi(cancellationToken);
            AssignUniqueStableKeys(fallback);
            _lastDetectionUsedScreenFallback = true;

            if (string.IsNullOrWhiteSpace(LastWarning))
                LogDetectionWarning("Native monitor detection was unavailable; active displays only are shown.");

            return CompleteDetection(fallback, usedScreenFallback: true);
        }

        private MonitorDetectionResult CompleteDetection(
            List<DetectedMonitor> monitors,
            bool usedScreenFallback)
        {
            _lastDetectionUsedScreenFallback = usedScreenFallback;
            return new MonitorDetectionResult(monitors, usedScreenFallback, LastWarning);
        }

        private static List<string> FindMissingActiveScreenDevices(
            IReadOnlyCollection<DetectedMonitor> detected)
        {
            var representedDevices = detected
                .Where(monitor => monitor.IsActive)
                .Select(monitor => NormalizeStable(monitor.DeviceName))
                .Where(deviceName => deviceName.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return Screen.AllScreens
                .Select(screen => screen.DeviceName)
                .Where(deviceName => !representedDevices.Contains(NormalizeStable(deviceName)))
                .ToList();
        }

        private static List<DetectedMonitor> DetectFromScreenApi(
            CancellationToken cancellationToken)
        {
            var result = new List<DetectedMonitor>();
            foreach (var screen in Screen.AllScreens)
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.Add(new DetectedMonitor
                {
                    Name = screen.DeviceName,
                    DeviceName = screen.DeviceName,
                    IsActive = true,
                    IsPresent = true,
                    PositionX = screen.Bounds.X
                });
            }

            return result;
        }

        private void LogDetectionWarning(string message)
        {
            var warning = message ?? string.Empty;
            Volatile.Write(ref _lastWarning, warning);
            try
            {
                _diagnosticsLog?.Invoke(warning);
            }
            catch
            {
                // Diagnostics must never prevent monitor detection or its fallback.
            }
        }

        internal static string BuildStableKey(
            string? serial,
            string? monitorId,
            string? instanceId,
            string? monitorKey,
            string? deviceName)
        {
            _ = monitorId;

            if (!string.IsNullOrWhiteSpace(instanceId))
                return $"IID:{NormalizeStable(instanceId)}";
            if (IsCredibleSerial(serial))
                return $"SN:{serial!.Trim()}";
            if (!string.IsNullOrWhiteSpace(monitorKey))
                return $"MK:{NormalizeStable(monitorKey)}";
            if (!string.IsNullOrWhiteSpace(deviceName))
                return $"DEV:{NormalizeStable(deviceName)}";

            return "DEV:UNKNOWN";
        }

        internal static void AssignUniqueStableKeys(IReadOnlyCollection<DetectedMonitor> monitors)
        {
            if (monitors.Count == 0)
                return;

            var uniqueSerials = UniqueIdentityValues(
                monitors,
                monitor => IsCredibleSerial(monitor.SerialNumber) ? monitor.SerialNumber : null);
            var uniqueInstanceIds = UniqueIdentityValues(monitors, monitor => monitor.InstanceId);
            var uniqueMonitorKeys = UniqueIdentityValues(monitors, monitor => monitor.MonitorKey);
            var uniqueNativeTargets = UniqueIdentityValues(monitors, monitor => monitor.NativeTargetPath);
            var uniqueDeviceNames = UniqueIdentityValues(monitors, monitor => monitor.DeviceName);

            foreach (var monitor in monitors)
            {
                var serial = NormalizeStable(monitor.SerialNumber);
                var instanceId = NormalizeStable(monitor.InstanceId);
                var monitorKey = NormalizeStable(monitor.MonitorKey);
                var nativeTargetPath = NormalizeStable(monitor.NativeTargetPath);

                // A unique Windows instance remains deterministic when another
                // monitor with the same EDID serial is connected or removed.
                // Serial identity remains the cross-port reconciliation hint,
                // but it must not make the UI key population-dependent.
                if (uniqueInstanceIds.Contains(instanceId))
                {
                    monitor.StableKey = $"IID:{instanceId}";
                }
                else if (IsCredibleSerial(monitor.SerialNumber) && uniqueSerials.Contains(serial))
                {
                    monitor.StableKey = $"SN:{monitor.SerialNumber.Trim()}";
                }
                else if (uniqueNativeTargets.Contains(nativeTargetPath))
                {
                    monitor.StableKey = $"NTP:{nativeTargetPath}";
                }
                else if (uniqueMonitorKeys.Contains(monitorKey))
                {
                    monitor.StableKey = $"MK:{monitorKey}";
                }
                else if (uniqueDeviceNames.Contains(NormalizeStable(monitor.DeviceName)))
                {
                    monitor.StableKey = $"DEV:{NormalizeStable(monitor.DeviceName)}";
                }
                else
                {
                    monitor.StableKey = "DEV:UNKNOWN";
                }
            }

            MakeWeakKeysUnique(monitors);
        }

        internal static bool IsCredibleSerial(string? serial)
        {
            var value = (serial ?? string.Empty).Trim();
            if (value.Length == 0)
                return false;

            if (value.All(character =>
                    character == '0' ||
                    character == '-' ||
                    character == '_' ||
                    char.IsWhiteSpace(character)))
            {
                return false;
            }

            return !value.Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase) &&
                   !value.Equals("NONE", StringComparison.OrdinalIgnoreCase) &&
                   !value.Equals("N/A", StringComparison.OrdinalIgnoreCase) &&
                   !value.Equals("NA", StringComparison.OrdinalIgnoreCase);
        }

        private static void MakeWeakKeysUnique(IReadOnlyCollection<DetectedMonitor> monitors)
        {
            foreach (var group in monitors
                         .GroupBy(monitor => monitor.StableKey, StringComparer.OrdinalIgnoreCase)
                         .Where(group => group.Count() > 1))
            {
                var ordered = group
                    .OrderBy(monitor => NormalizeStable(monitor.DeviceName), StringComparer.OrdinalIgnoreCase)
                    .ThenBy(monitor => monitor.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                for (var index = 0; index < ordered.Count; index++)
                    ordered[index].StableKey = $"{group.Key}|DUP:{index + 1}";
            }
        }

        private static HashSet<string> UniqueIdentityValues(
            IEnumerable<DetectedMonitor> monitors,
            Func<DetectedMonitor, string?> selector)
        {
            return monitors
                .Select(selector)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => NormalizeStable(value!))
                .Where(value => value.Length > 0)
                .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() == 1)
                .Select(group => group.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        internal static string NormalizeStable(string? value)
        {
            var normalised = (value ?? string.Empty).Trim().Replace('/', '\\');
            while (normalised.Contains("\\\\", StringComparison.Ordinal))
                normalised = normalised.Replace("\\\\", "\\", StringComparison.Ordinal);
            return normalised;
        }
    }
}
