using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;
using WorkMonitorSwitcher.Model;

namespace WorkMonitorSwitcher.Services
{
    internal sealed record NativeDisplayDetectionResult(
        List<DetectedMonitor> Monitors,
        string WarningMessage);

    /// <summary>
    /// One CCD route before alternative source-to-target paths are consolidated.
    /// Kept as a managed value so path consolidation can be tested without touching
    /// the live display topology.
    /// </summary>
    internal sealed record NativeDisplayCandidate(
        string EndpointKey,
        bool IsActive,
        bool IsAvailable,
        string SourceDeviceName,
        string FriendlyName,
        string MonitorDevicePath,
        string InstanceId,
        string SerialNumber,
        string MonitorId,
        uint ConnectorInstance,
        int PositionX);

    internal sealed record NativeEdidIdentity(
        string Manufacturer,
        string ProductCode,
        string SerialNumber,
        string MonitorName);

    /// <summary>
    /// Read-only Windows Connecting and Configuring Displays (CCD) enumeration.
    /// </summary>
    internal static class NativeDisplayDetection
    {
        private const int ErrorSuccess = 0;
        private const int ErrorInvalidParameter = 87;
        private const int ErrorInsufficientBuffer = 122;

        private const uint QdcAllPaths = 0x00000001;
        private const uint QdcVirtualModeAware = 0x00000010;
        private const uint DisplayConfigPathActive = 0x00000001;
        private const uint DisplayConfigPathSupportVirtualMode = 0x00000008;
        private const uint InvalidModeIndex = 0xffffffff;
        private const uint DisplayConfigModeInfoTypeSource = 1;
        private const int DisplayConfigDeviceInfoGetSourceName = 1;
        private const int DisplayConfigDeviceInfoGetTargetName = 2;

        internal static NativeDisplayDetectionResult Detect(CancellationToken cancellationToken)
        {
            var (paths, modes) = QueryAllPaths(cancellationToken);
            var candidates = new List<NativeDisplayCandidate>();
            var edidByPath = new Dictionary<string, NativeEdidIdentity?>(StringComparer.OrdinalIgnoreCase);
            var edidReadFailures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var isActive = (path.flags & DisplayConfigPathActive) != 0;
                var isAvailable = path.targetInfo.targetAvailable != 0;
                if (!isActive && !isAvailable)
                    continue;

                var target = GetTargetName(path.targetInfo);
                var sourceName = GetSourceName(path.sourceInfo);
                var monitorPath = (target.monitorDevicePath ?? string.Empty).Trim();
                var friendlyName = (target.monitorFriendlyDeviceName ?? string.Empty).Trim();

                TryParseMonitorDevicePath(
                    monitorPath,
                    out var hardwareId,
                    out var instanceId);

                NativeEdidIdentity? edidIdentity = null;
                if (!string.IsNullOrWhiteSpace(monitorPath))
                {
                    var normalisedPath = DetectionService.NormalizeStable(monitorPath);
                    if (!edidByPath.TryGetValue(normalisedPath, out edidIdentity))
                    {
                        if (TryReadEdidIdentity(monitorPath, out var readIdentity))
                            edidIdentity = readIdentity;
                        else
                            edidReadFailures.Add(normalisedPath);

                        edidByPath[normalisedPath] = edidIdentity;
                    }
                }

                if (string.IsNullOrWhiteSpace(friendlyName))
                    friendlyName = edidIdentity?.MonitorName ?? string.Empty;

                var monitorId = FirstNonBlank(
                    hardwareId,
                    BuildMonitorId(
                        edidIdentity?.Manufacturer,
                        edidIdentity?.ProductCode),
                    BuildTargetMonitorId(target.edidManufactureId, target.edidProductCodeId));

                candidates.Add(new NativeDisplayCandidate(
                    EndpointKey(path.targetInfo.adapterId, path.targetInfo.id),
                    isActive,
                    isAvailable,
                    sourceName,
                    friendlyName,
                    monitorPath,
                    string.IsNullOrWhiteSpace(instanceId)
                        ? string.Empty
                        : $"DISPLAY\\{hardwareId}\\{instanceId}",
                    edidIdentity?.SerialNumber ?? string.Empty,
                    monitorId,
                    target.connectorInstance,
                    FindPositionX(path, modes)));
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!TryConsolidateCandidates(candidates, out var monitors, out var consolidationError))
                throw new InvalidOperationException(consolidationError);

            var warnings = new List<string>();
            if (edidReadFailures.Count > 0)
            {
                warnings.Add(
                    $"EDID serial data was unavailable for {edidReadFailures.Count} connected display(s); " +
                    "their saved identity may change after a port or adapter change.");
            }

            var withoutSerial = monitors.Count(monitor =>
                !DetectionService.IsCredibleSerial(monitor.SerialNumber));
            if (withoutSerial > 0)
            {
                warnings.Add(
                    $"{withoutSerial} connected display(s) had no usable EDID serial; " +
                    "their instance/path identity may change after a port or adapter change.");
            }

            return new NativeDisplayDetectionResult(monitors, string.Join(" ", warnings));
        }

        internal static bool TryConsolidateCandidates(
            IEnumerable<NativeDisplayCandidate> source,
            out List<DetectedMonitor> monitors,
            out string error)
        {
            monitors = new List<DetectedMonitor>();
            error = string.Empty;

            var candidates = source
                .Where(candidate => candidate.IsActive || candidate.IsAvailable)
                .ToList();
            if (candidates.Count == 0)
                return true;

            var unavailableActive = candidates.FirstOrDefault(candidate =>
                candidate.IsActive && !candidate.IsAvailable);
            if (unavailableActive != null)
            {
                error =
                    $"Windows reported active display endpoint '{unavailableActive.EndpointKey}' " +
                    "whose target is no longer available.";
                return false;
            }

            var unidentifiedActive = candidates.FirstOrDefault(candidate =>
                candidate.IsActive &&
                !NativeDisplayProfileCodec.IsStrongTargetPath(candidate.MonitorDevicePath));
            if (unidentifiedActive != null)
            {
                error =
                    $"Windows did not provide a strong monitor device path for active display endpoint " +
                    $"'{unidentifiedActive.EndpointKey}'.";
                return false;
            }

            var endpoints = new List<NativeDisplayCandidate>();
            foreach (var group in candidates.GroupBy(
                         candidate => candidate.EndpointKey,
                         StringComparer.OrdinalIgnoreCase))
            {
                if (!TryMergeCandidateGroup(group.ToList(), out var endpoint, out error))
                    return false;

                endpoints.Add(endpoint);
            }

            var physicalGroups = endpoints.GroupBy(
                PhysicalMonitorGroupKey,
                StringComparer.OrdinalIgnoreCase);

            foreach (var group in physicalGroups)
            {
                var alternatives = group.ToList();
                var activeAlternatives = alternatives.Where(candidate => candidate.IsActive).ToList();
                if (activeAlternatives.Count > 1)
                {
                    error =
                        $"Windows reported multiple active display endpoints for monitor '{group.Key}'; " +
                        "the mapping is ambiguous.";
                    monitors.Clear();
                    return false;
                }

                if (!IdentityValuesAgree(alternatives.Select(candidate => candidate.SerialNumber)) ||
                    !IdentityValuesAgree(alternatives.Select(candidate => candidate.InstanceId)) ||
                    !IdentityValuesAgree(alternatives.Select(candidate => candidate.MonitorId)))
                {
                    error =
                        $"Windows reported conflicting hardware identities for monitor '{group.Key}'; " +
                        "the mapping is ambiguous.";
                    monitors.Clear();
                    return false;
                }

                var selected = (activeAlternatives.Count == 1
                        ? activeAlternatives[0]
                        : alternatives.OrderBy(candidate => candidate.EndpointKey, StringComparer.OrdinalIgnoreCase).First())
                    with
                    {
                        IsAvailable = alternatives.Any(candidate => candidate.IsAvailable),
                        FriendlyName = FirstNonBlank(
                            alternatives.Select(candidate => candidate.FriendlyName).ToArray()),
                        MonitorDevicePath = FirstNonBlank(
                            alternatives.Select(candidate => candidate.MonitorDevicePath).ToArray()),
                        InstanceId = FirstNonBlank(
                            alternatives.Select(candidate => candidate.InstanceId).ToArray()),
                        SerialNumber = FirstNonBlank(
                            alternatives.Select(candidate => candidate.SerialNumber).ToArray()),
                        MonitorId = FirstNonBlank(
                            alternatives.Select(candidate => candidate.MonitorId).ToArray())
                    };

                var deviceName = selected.IsActive ? selected.SourceDeviceName : string.Empty;
                if (selected.IsActive && string.IsNullOrWhiteSpace(deviceName))
                {
                    error =
                        $"Windows did not provide a GDI source name for active display endpoint '{selected.EndpointKey}'.";
                    monitors.Clear();
                    return false;
                }

                var registryPath = BuildMonitorRegistryPath(selected.MonitorDevicePath);

                monitors.Add(new DetectedMonitor
                {
                    Name = FirstNonBlank(selected.FriendlyName, deviceName, selected.MonitorDevicePath),
                    DeviceName = deviceName,
                    NativeTargetPath = selected.MonitorDevicePath,
                    MonitorKey = registryPath,
                    MonitorId = selected.MonitorId,
                    InstanceId = selected.InstanceId,
                    SerialNumber = selected.SerialNumber,
                    IsActive = selected.IsActive,
                    IsPresent = selected.IsActive || selected.IsAvailable,
                    PositionX = selected.PositionX
                });
            }

            var duplicateSerials = monitors
                .Where(monitor => DetectionService.IsCredibleSerial(monitor.SerialNumber))
                .GroupBy(
                    monitor => DetectionService.NormalizeStable(monitor.SerialNumber),
                    StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToList();
            if (duplicateSerials.Count > 0)
            {
                error =
                    "More than one connected display reported the same EDID serial number: " +
                    string.Join(", ", duplicateSerials) + ". Physical identity is ambiguous.";
                monitors.Clear();
                return false;
            }

            monitors = monitors
                .OrderByDescending(monitor => monitor.IsActive)
                .ThenBy(monitor => monitor.PositionX)
                .ThenBy(monitor => monitor.DeviceName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return true;
        }

        private static bool TryMergeCandidateGroup(
            IReadOnlyCollection<NativeDisplayCandidate> candidates,
            out NativeDisplayCandidate merged,
            out string error)
        {
            merged = candidates.First();
            error = string.Empty;

            if (!IdentityValuesAgree(candidates.Select(candidate => candidate.MonitorDevicePath)) ||
                !IdentityValuesAgree(candidates.Select(candidate => candidate.InstanceId)) ||
                !IdentityValuesAgree(candidates.Select(candidate => candidate.SerialNumber)) ||
                !IdentityValuesAgree(candidates.Select(candidate => candidate.MonitorId)))
            {
                error =
                    $"Windows reported conflicting identities for display endpoint '{merged.EndpointKey}'.";
                return false;
            }

            var active = candidates.Where(candidate => candidate.IsActive).ToList();
            var activeSourceNames = active
                .Select(candidate => DetectionService.NormalizeStable(candidate.SourceDeviceName))
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (activeSourceNames.Count > 1)
            {
                error =
                    $"Windows reported multiple active source devices for display endpoint '{merged.EndpointKey}'.";
                return false;
            }

            var selected = active.FirstOrDefault() ??
                           candidates.OrderBy(candidate => candidate.SourceDeviceName, StringComparer.OrdinalIgnoreCase).First();
            merged = selected with
            {
                IsActive = active.Count > 0,
                IsAvailable = candidates.Any(candidate => candidate.IsAvailable),
                SourceDeviceName = FirstNonBlank(
                    active.Select(candidate => candidate.SourceDeviceName)
                        .Concat(candidates.Select(candidate => candidate.SourceDeviceName))
                        .ToArray()),
                FriendlyName = FirstNonBlank(
                    candidates.Select(candidate => candidate.FriendlyName).ToArray()),
                MonitorDevicePath = FirstNonBlank(
                    candidates.Select(candidate => candidate.MonitorDevicePath).ToArray()),
                InstanceId = FirstNonBlank(
                    candidates.Select(candidate => candidate.InstanceId).ToArray()),
                SerialNumber = FirstNonBlank(
                    candidates.Select(candidate => candidate.SerialNumber).ToArray()),
                MonitorId = FirstNonBlank(
                    candidates.Select(candidate => candidate.MonitorId).ToArray())
            };
            return true;
        }

        private static string PhysicalMonitorGroupKey(NativeDisplayCandidate candidate)
        {
            var path = DetectionService.NormalizeStable(candidate.MonitorDevicePath);
            if (path.Length > 0)
                return $"PATH:{path}";

            var instanceId = DetectionService.NormalizeStable(candidate.InstanceId);
            if (instanceId.Length > 0)
                return $"INSTANCE:{instanceId}";

            return $"ENDPOINT:{candidate.EndpointKey}";
        }

        private static bool IdentityValuesAgree(IEnumerable<string> values)
        {
            return values
                .Select(DetectionService.NormalizeStable)
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(2)
                .Count() <= 1;
        }

        internal static bool TryParseMonitorDevicePath(
            string? monitorDevicePath,
            out string hardwareId,
            out string instanceId)
        {
            hardwareId = string.Empty;
            instanceId = string.Empty;

            var path = (monitorDevicePath ?? string.Empty).Trim();
            if (path.Length == 0)
                return false;

            var parts = path.Split('#');
            if (parts.Length < 4 ||
                !parts[0].TrimStart('\\').Equals("?\\DISPLAY", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            hardwareId = parts[1].Trim();
            instanceId = parts[2].Trim();
            if (!IsSafeRegistrySegment(hardwareId) || !IsSafeRegistrySegment(instanceId))
            {
                hardwareId = string.Empty;
                instanceId = string.Empty;
                return false;
            }

            return true;
        }

        internal static bool TryReadEdidIdentity(
            string monitorDevicePath,
            out NativeEdidIdentity identity)
        {
            identity = new NativeEdidIdentity(string.Empty, string.Empty, string.Empty, string.Empty);
            if (!TryParseMonitorDevicePath(monitorDevicePath, out var hardwareId, out var instanceId))
                return false;

            var keyPath =
                $@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{hardwareId}\{instanceId}\Device Parameters";
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(keyPath, writable: false);
                if (key?.GetValue("EDID") is not byte[] edid)
                    return false;

                return TryDecodeEdid(edid, out identity);
            }
            catch (Exception ex) when (
                ex is UnauthorizedAccessException ||
                ex is System.Security.SecurityException ||
                ex is System.IO.IOException)
            {
                return false;
            }
        }

        internal static string BuildMonitorRegistryPath(string? monitorDevicePath)
        {
            return TryParseMonitorDevicePath(monitorDevicePath, out var hardwareId, out var instanceId)
                ? $@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Enum\DISPLAY\{hardwareId}\{instanceId}"
                : string.Empty;
        }

        internal static bool TryDecodeEdid(byte[]? edid, out NativeEdidIdentity identity)
        {
            identity = new NativeEdidIdentity(string.Empty, string.Empty, string.Empty, string.Empty);
            if (edid == null ||
                edid.Length < 128 ||
                !HasEdidHeader(edid) ||
                !HasValidBaseBlockChecksum(edid))
                return false;

            var manufacturerWord = (ushort)((edid[8] << 8) | edid[9]);
            var manufacturer = DecodeManufacturerId(manufacturerWord);
            var productCode = (edid[10] | (edid[11] << 8)).ToString("X4");
            var serial = string.Empty;
            var monitorName = string.Empty;

            for (var offset = 54; offset + 18 <= Math.Min(edid.Length, 126); offset += 18)
            {
                if (edid[offset] != 0 || edid[offset + 1] != 0 || edid[offset + 2] != 0)
                    continue;

                var descriptorText = DecodeDescriptorText(edid, offset + 5, 13);
                if (edid[offset + 3] == 0xff && DetectionService.IsCredibleSerial(descriptorText))
                    serial = descriptorText;
                else if (edid[offset + 3] == 0xfc && descriptorText.Length > 0)
                    monitorName = descriptorText;
            }

            if (!DetectionService.IsCredibleSerial(serial))
            {
                var numericSerial = (uint)(edid[12] |
                                           (edid[13] << 8) |
                                           (edid[14] << 16) |
                                           (edid[15] << 24));
                if (numericSerial != 0 && numericSerial != uint.MaxValue)
                    serial = numericSerial.ToString("X8");
            }

            identity = new NativeEdidIdentity(manufacturer, productCode, serial, monitorName);
            return true;
        }

        internal static string DecodeManufacturerId(ushort value)
        {
            Span<char> characters = stackalloc char[3];
            characters[0] = DecodeManufacturerCharacter((value >> 10) & 0x1f);
            characters[1] = DecodeManufacturerCharacter((value >> 5) & 0x1f);
            characters[2] = DecodeManufacturerCharacter(value & 0x1f);

            return characters.IndexOf('?') >= 0
                ? string.Empty
                : new string(characters);
        }

        private static char DecodeManufacturerCharacter(int value)
            => value is >= 1 and <= 26 ? (char)('A' + value - 1) : '?';

        private static string DecodeDescriptorText(byte[] edid, int offset, int length)
        {
            return Encoding.ASCII.GetString(edid, offset, length)
                .Trim('\0', '\r', '\n', ' ');
        }

        private static bool HasEdidHeader(IReadOnlyList<byte> edid)
        {
            ReadOnlySpan<byte> header = stackalloc byte[]
            {
                0x00, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x00
            };
            for (var index = 0; index < header.Length; index++)
            {
                if (edid[index] != header[index])
                    return false;
            }

            return true;
        }

        private static bool HasValidBaseBlockChecksum(IReadOnlyList<byte> edid)
        {
            var sum = 0;
            for (var index = 0; index < 128; index++)
                sum = (sum + edid[index]) & 0xff;
            return sum == 0;
        }

        private static bool IsSafeRegistrySegment(string value)
        {
            return value.Length > 0 &&
                   value != "." &&
                   value != ".." &&
                   value.IndexOfAny(new[] { '\\', '/', '\0' }) < 0;
        }

        private static string BuildMonitorId(string? manufacturer, string? product)
        {
            if (string.IsNullOrWhiteSpace(manufacturer) || string.IsNullOrWhiteSpace(product))
                return string.Empty;
            return manufacturer.Trim() + product.Trim();
        }

        private static string BuildTargetMonitorId(ushort manufacturer, ushort product)
        {
            var decodedManufacturer = DecodeManufacturerId(manufacturer);
            return decodedManufacturer.Length == 0
                ? string.Empty
                : $"{decodedManufacturer}{product:X4}";
        }

        private static string FirstNonBlank(params string?[] values)
            => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

        private static string EndpointKey(LUID adapterId, uint targetId)
            => $"{adapterId.HighPart:X8}:{adapterId.LowPart:X8}:{targetId:X8}";

        private static (DISPLAYCONFIG_PATH_INFO[] Paths, DISPLAYCONFIG_MODE_INFO[] Modes)
            QueryAllPaths(CancellationToken cancellationToken)
        {
            var queryFlags = QdcAllPaths | QdcVirtualModeAware;

            for (var attempt = 0; attempt < 4; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sizeCode = GetDisplayConfigBufferSizes(
                    queryFlags,
                    out var pathCount,
                    out var modeCount);

                if (sizeCode == ErrorInvalidParameter &&
                    (queryFlags & QdcVirtualModeAware) != 0)
                {
                    queryFlags = QdcAllPaths;
                    continue;
                }

                if (sizeCode != ErrorSuccess)
                    throw new InvalidOperationException(
                        $"GetDisplayConfigBufferSizes failed ({sizeCode}).");

                var paths = new DISPLAYCONFIG_PATH_INFO[checked((int)pathCount)];
                var modes = new DISPLAYCONFIG_MODE_INFO[checked((int)modeCount)];
                var queryCode = QueryDisplayConfig(
                    queryFlags,
                    ref pathCount,
                    paths,
                    ref modeCount,
                    modes,
                    IntPtr.Zero);

                if (queryCode == ErrorInsufficientBuffer)
                    continue;
                if (queryCode == ErrorInvalidParameter &&
                    (queryFlags & QdcVirtualModeAware) != 0)
                {
                    queryFlags = QdcAllPaths;
                    continue;
                }
                if (queryCode != ErrorSuccess)
                    throw new InvalidOperationException($"QueryDisplayConfig failed ({queryCode}).");

                if (pathCount != paths.Length)
                    Array.Resize(ref paths, checked((int)pathCount));
                if (modeCount != modes.Length)
                    Array.Resize(ref modes, checked((int)modeCount));

                return (paths, modes);
            }

            throw new InvalidOperationException(
                "Display topology changed repeatedly while it was being queried.");
        }

        private static int FindPositionX(
            DISPLAYCONFIG_PATH_INFO path,
            IReadOnlyList<DISPLAYCONFIG_MODE_INFO> modes)
        {
            if ((path.flags & DisplayConfigPathActive) == 0)
                return 0;

            var sourceModeIndex = DecodeSourceModeIndex(
                path.sourceInfo.modeInfoIdx,
                path.flags);

            if (sourceModeIndex < modes.Count &&
                IsMatchingSourceMode(modes[(int)sourceModeIndex], path.sourceInfo))
            {
                return modes[(int)sourceModeIndex].modeInfo.sourceMode.position.x;
            }

            foreach (var mode in modes)
            {
                if (IsMatchingSourceMode(mode, path.sourceInfo))
                    return mode.modeInfo.sourceMode.position.x;
            }

            return 0;
        }

        internal static uint DecodeSourceModeIndex(uint modeInfoIndex, uint pathFlags)
        {
            if (modeInfoIndex == InvalidModeIndex)
                return InvalidModeIndex;

            return (pathFlags & DisplayConfigPathSupportVirtualMode) != 0
                ? modeInfoIndex >> 16
                : modeInfoIndex;
        }

        private static bool IsMatchingSourceMode(
            DISPLAYCONFIG_MODE_INFO mode,
            DISPLAYCONFIG_PATH_SOURCE_INFO source)
        {
            return mode.infoType == DisplayConfigModeInfoTypeSource &&
                   mode.id == source.id &&
                   mode.adapterId.HighPart == source.adapterId.HighPart &&
                   mode.adapterId.LowPart == source.adapterId.LowPart;
        }

        private static string GetSourceName(DISPLAYCONFIG_PATH_SOURCE_INFO source)
        {
            var request = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
            {
                header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = DisplayConfigDeviceInfoGetSourceName,
                    size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                    adapterId = source.adapterId,
                    id = source.id
                }
            };

            return DisplayConfigGetDeviceInfo(ref request) == ErrorSuccess
                ? request.viewGdiDeviceName?.Trim() ?? string.Empty
                : string.Empty;
        }

        private static DISPLAYCONFIG_TARGET_DEVICE_NAME GetTargetName(
            DISPLAYCONFIG_PATH_TARGET_INFO target)
        {
            var request = new DISPLAYCONFIG_TARGET_DEVICE_NAME
            {
                header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = DisplayConfigDeviceInfoGetTargetName,
                    size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                    adapterId = target.adapterId,
                    id = target.id
                }
            };

            return DisplayConfigGetDeviceInfo(ref request) == ErrorSuccess
                ? request
                : default;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINTL
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_RATIONAL
        {
            public uint Numerator;
            public uint Denominator;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_2DREGION
        {
            public uint cx;
            public uint cy;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_VIDEO_SIGNAL_INFO
        {
            public ulong pixelRate;
            public DISPLAYCONFIG_RATIONAL hSyncFreq;
            public DISPLAYCONFIG_RATIONAL vSyncFreq;
            public DISPLAYCONFIG_2DREGION activeSize;
            public DISPLAYCONFIG_2DREGION totalSize;
            public uint videoStandard;
            public uint scanLineOrdering;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_TARGET_MODE
        {
            public DISPLAYCONFIG_VIDEO_SIGNAL_INFO targetVideoSignalInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_SOURCE_MODE
        {
            public uint width;
            public uint height;
            public uint pixelFormat;
            public POINTL position;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_DESKTOP_IMAGE_INFO
        {
            public POINTL PathSourceSize;
            public RECT DesktopImageRegion;
            public RECT DesktopImageClip;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct DISPLAYCONFIG_MODE_INFO_UNION
        {
            [FieldOffset(0)] public DISPLAYCONFIG_TARGET_MODE targetMode;
            [FieldOffset(0)] public DISPLAYCONFIG_SOURCE_MODE sourceMode;
            [FieldOffset(0)] public DISPLAYCONFIG_DESKTOP_IMAGE_INFO desktopImageInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_MODE_INFO
        {
            public uint infoType;
            public uint id;
            public LUID adapterId;
            public DISPLAYCONFIG_MODE_INFO_UNION modeInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_SOURCE_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_TARGET_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint outputTechnology;
            public uint rotation;
            public uint scaling;
            public DISPLAYCONFIG_RATIONAL refreshRate;
            public uint scanLineOrdering;
            public int targetAvailable;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_INFO
        {
            public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
            public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
            public uint flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
        {
            public int type;
            public uint size;
            public LUID adapterId;
            public uint id;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string? viewGdiDeviceName;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint flags;
            public uint outputTechnology;
            public ushort edidManufactureId;
            public ushort edidProductCodeId;
            public uint connectorInstance;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string? monitorFriendlyDeviceName;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string? monitorDevicePath;
        }

        [DllImport("user32.dll")]
        private static extern int GetDisplayConfigBufferSizes(
            uint flags,
            out uint numPathArrayElements,
            out uint numModeInfoArrayElements);

        [DllImport("user32.dll")]
        private static extern int QueryDisplayConfig(
            uint flags,
            ref uint numPathArrayElements,
            [Out] DISPLAYCONFIG_PATH_INFO[] pathInfoArray,
            ref uint numModeInfoArrayElements,
            [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
            IntPtr currentTopologyId);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(
            ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(
            ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);
    }
}
