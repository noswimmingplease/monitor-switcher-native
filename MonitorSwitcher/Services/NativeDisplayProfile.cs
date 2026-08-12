using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace WorkMonitorSwitcher.Services
{
    /// <summary>
    /// MonitorSwitcher-owned, versioned data stored in the existing .cfg profile.
    /// Version 0 represents the legacy profile format. Version 1 adds CCD target
    /// and route identities required for safe native activation.
    /// </summary>
    internal sealed record NativeDisplayProfile(
        int Version,
        IReadOnlyList<NativeDisplayProfileMonitor> Monitors);

    internal sealed record NativeDisplayProfileMonitor(
        string LayoutDeviceName,
        string MonitorDevicePath,
        long? SourceAdapterLuid,
        uint? SourceId,
        long? TargetAdapterLuid,
        uint? TargetId,
        int X,
        int Y,
        int Width,
        int Height,
        uint Rotation,
        bool IsPrimary,
        string FriendlyName,
        ushort? EdidManufactureId,
        ushort? EdidProductCodeId,
        uint? ConnectorInstance)
    {
        public bool HasNativeRouteIdentity =>
            NativeDisplayProfileCodec.IsStrongTargetPath(MonitorDevicePath) &&
            SourceAdapterLuid.HasValue &&
            SourceId.HasValue &&
            TargetAdapterLuid.HasValue &&
            TargetId.HasValue;
    }

    internal static class NativeDisplayProfileCodec
    {
        internal const int CurrentVersion = 1;

        public static bool TryRead(
            string? path,
            out NativeDisplayProfile profile,
            out string errorMessage)
        {
            profile = new NativeDisplayProfile(0, Array.Empty<NativeDisplayProfileMonitor>());
            errorMessage = string.Empty;

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                errorMessage = "The saved layout file was not found.";
                return false;
            }

            try
            {
                return TryParse(File.ReadAllText(path), out profile, out errorMessage);
            }
            catch (Exception ex)
            {
                errorMessage = $"Unable to read the saved layout: {ex.Message}";
                return false;
            }
        }

        internal static bool TryParse(
            string? contents,
            out NativeDisplayProfile profile,
            out string errorMessage)
        {
            profile = new NativeDisplayProfile(0, Array.Empty<NativeDisplayProfileMonitor>());
            errorMessage = string.Empty;
            if (string.IsNullOrWhiteSpace(contents))
            {
                errorMessage = "The saved layout is empty.";
                return false;
            }

            var monitors = new List<NativeDisplayProfileMonitor>();
            var builder = new MonitorBuilder();
            bool inMonitorSection = false;
            bool sawMonitorSection = false;
            string sectionName = string.Empty;
            int version = 0;
            bool sawVersion = false;
            string sectionError = string.Empty;

            bool CommitSection()
            {
                if (!inMonitorSection)
                    return true;

                if (!builder.TryBuild(sectionName, out var monitor, out sectionError))
                    return false;

                if (monitor != null)
                    monitors.Add(monitor);
                return true;
            }

            using var reader = new StringReader(contents);
            string? rawLine;
            while ((rawLine = reader.ReadLine()) != null)
            {
                var line = rawLine.Trim();
                if (line.Length == 0 ||
                    line.StartsWith(";", StringComparison.Ordinal) ||
                    line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                if (line.StartsWith("[", StringComparison.Ordinal) &&
                    line.EndsWith("]", StringComparison.Ordinal))
                {
                    if (!CommitSection())
                    {
                        errorMessage = sectionError;
                        return false;
                    }

                    inMonitorSection = line.StartsWith("[Monitor", StringComparison.OrdinalIgnoreCase);
                    sawMonitorSection |= inMonitorSection;
                    sectionName = line;
                    builder = new MonitorBuilder();
                    continue;
                }

                var equals = line.IndexOf('=');
                if (equals <= 0)
                    continue;

                var key = line[..equals].Trim();
                var value = line[(equals + 1)..].Trim();
                if (!inMonitorSection)
                {
                    if (!key.Equals("NativeProfileVersion", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (sawVersion ||
                        !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out version) ||
                        version < 1 ||
                        version > CurrentVersion)
                    {
                        errorMessage = "The native display profile version is missing, duplicated, or unsupported.";
                        return false;
                    }

                    sawVersion = true;
                    continue;
                }

                if (!builder.TrySet(key, value, out errorMessage))
                {
                    errorMessage = $"{sectionName}: {errorMessage}";
                    return false;
                }
            }

            if (!CommitSection())
            {
                errorMessage = sectionError;
                return false;
            }

            if (!sawMonitorSection || monitors.Count == 0)
            {
                errorMessage = "The saved layout does not contain a complete active monitor section.";
                return false;
            }

            if (monitors.Select(m => m.LayoutDeviceName)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count() != monitors.Count)
            {
                errorMessage = "The saved layout contains duplicate active display names.";
                return false;
            }

            if (monitors.Count(m => m.IsPrimary) != 1)
            {
                errorMessage = "The saved extended-desktop layout must contain exactly one primary display at 0,0.";
                return false;
            }

            if (version > 0)
            {
                if (monitors.Any(m => !m.HasNativeRouteIdentity))
                {
                    errorMessage = "The native profile is missing a strong CCD target or route identity.";
                    return false;
                }

                if (monitors.Select(m => NormaliseTargetPath(m.MonitorDevicePath))
                        .Distinct(StringComparer.OrdinalIgnoreCase).Count() != monitors.Count)
                {
                    errorMessage = "The native profile contains a duplicate CCD target identity.";
                    return false;
                }

                if (monitors.Select(m => (m.SourceAdapterLuid!.Value, m.SourceId!.Value))
                        .Distinct().Count() != monitors.Count)
                {
                    errorMessage = "Clone or mirror profiles are not supported.";
                    return false;
                }
            }

            profile = new NativeDisplayProfile(version, monitors);
            return true;
        }

        internal static string Serialise(NativeDisplayProfile profile)
        {
            if (profile == null)
                throw new ArgumentNullException(nameof(profile));
            if (profile.Version != CurrentVersion || profile.Monitors.Count == 0)
                throw new InvalidDataException("Only a complete current native display profile can be serialised.");

            var builder = new StringBuilder();
            builder.AppendLine("; MonitorSwitcher native display profile. Values are physical desktop pixels.");
            builder.Append("NativeProfileVersion=")
                .Append(profile.Version.ToString(CultureInfo.InvariantCulture))
                .AppendLine()
                .AppendLine();

            var ordered = profile.Monitors
                .OrderBy(m => m.IsPrimary ? 0 : 1)
                .ThenBy(m => m.X)
                .ThenBy(m => m.Y)
                .ThenBy(m => m.LayoutDeviceName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            for (int index = 0; index < ordered.Count; index++)
            {
                var monitor = ordered[index];
                if (!monitor.HasNativeRouteIdentity ||
                    monitor.Width <= 0 ||
                    monitor.Height <= 0 ||
                    monitor.Rotation is < 1 or > 4 ||
                    monitor.IsPrimary != (monitor.X == 0 && monitor.Y == 0))
                {
                    throw new InvalidDataException("The native display profile contains an incomplete monitor entry.");
                }

                builder.Append("[Monitor").Append(index).AppendLine("]");
                AppendSetting(builder, "Name", monitor.LayoutDeviceName);
                AppendSetting(builder, "MonitorDevicePath", monitor.MonitorDevicePath);
                AppendSetting(builder, "SourceAdapterLuid", monitor.SourceAdapterLuid!.Value.ToString(CultureInfo.InvariantCulture));
                AppendSetting(builder, "SourceId", monitor.SourceId!.Value.ToString(CultureInfo.InvariantCulture));
                AppendSetting(builder, "TargetAdapterLuid", monitor.TargetAdapterLuid!.Value.ToString(CultureInfo.InvariantCulture));
                AppendSetting(builder, "TargetId", monitor.TargetId!.Value.ToString(CultureInfo.InvariantCulture));
                AppendSetting(builder, "PositionX", monitor.X.ToString(CultureInfo.InvariantCulture));
                AppendSetting(builder, "PositionY", monitor.Y.ToString(CultureInfo.InvariantCulture));
                AppendSetting(builder, "Width", monitor.Width.ToString(CultureInfo.InvariantCulture));
                AppendSetting(builder, "Height", monitor.Height.ToString(CultureInfo.InvariantCulture));
                AppendSetting(builder, "BitsPerPixel", "32");
                AppendSetting(builder, "DisplayOrientation", RotationToOrientation(monitor.Rotation).ToString(CultureInfo.InvariantCulture));
                AppendSetting(builder, "Primary", monitor.IsPrimary ? "1" : "0");
                if (!string.IsNullOrWhiteSpace(monitor.FriendlyName))
                    AppendSetting(builder, "FriendlyName", monitor.FriendlyName);
                if (monitor.EdidManufactureId.HasValue)
                    AppendSetting(builder, "EdidManufactureId", monitor.EdidManufactureId.Value.ToString(CultureInfo.InvariantCulture));
                if (monitor.EdidProductCodeId.HasValue)
                    AppendSetting(builder, "EdidProductCodeId", monitor.EdidProductCodeId.Value.ToString(CultureInfo.InvariantCulture));
                if (monitor.ConnectorInstance.HasValue)
                    AppendSetting(builder, "ConnectorInstance", monitor.ConnectorInstance.Value.ToString(CultureInfo.InvariantCulture));
                builder.AppendLine();
            }

            return builder.ToString();
        }

        internal static bool IsStrongTargetPath(string? value)
        {
            var path = (value ?? string.Empty).Trim();
            return path.Length is > 4 and <= 512 &&
                   path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase) &&
                   !path.StartsWith(@"\\.\DISPLAY", StringComparison.OrdinalIgnoreCase) &&
                   path.IndexOfAny(new[] { '\r', '\n', '\0' }) < 0;
        }

        internal static string NormaliseTargetPath(string? value)
            => (value ?? string.Empty).Trim().Replace('/', '\\');

        private static void AppendSetting(StringBuilder builder, string key, string value)
        {
            if (value.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
                throw new InvalidDataException($"The '{key}' value contains an invalid line break.");
            builder.Append(key).Append('=').AppendLine(value.Trim());
        }

        private static int RotationToOrientation(uint rotation)
            => rotation switch
            {
                1 => 0,
                2 => 1,
                3 => 2,
                4 => 3,
                _ => throw new InvalidDataException($"Unsupported CCD rotation value {rotation}.")
            };

        private sealed class MonitorBuilder
        {
            private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
            private string _name = string.Empty;
            private string _targetPath = string.Empty;
            private string _friendlyName = string.Empty;
            private long? _sourceAdapter;
            private uint? _sourceId;
            private long? _targetAdapter;
            private uint? _targetId;
            private int? _x;
            private int? _y;
            private int? _width;
            private int? _height;
            private int? _bitsPerPixel;
            private uint? _rotation;
            private bool? _primary;
            private ushort? _edidManufactureId;
            private ushort? _edidProductCodeId;
            private uint? _connectorInstance;

            public bool TrySet(string key, string value, out string errorMessage)
            {
                errorMessage = string.Empty;
                bool known = key.Equals("Name", StringComparison.OrdinalIgnoreCase) ||
                             key.Equals("MonitorDevicePath", StringComparison.OrdinalIgnoreCase) ||
                             key.Equals("SourceAdapterLuid", StringComparison.OrdinalIgnoreCase) ||
                             key.Equals("SourceId", StringComparison.OrdinalIgnoreCase) ||
                             key.Equals("TargetAdapterLuid", StringComparison.OrdinalIgnoreCase) ||
                             key.Equals("TargetId", StringComparison.OrdinalIgnoreCase) ||
                             key.Equals("PositionX", StringComparison.OrdinalIgnoreCase) ||
                             key.Equals("PositionY", StringComparison.OrdinalIgnoreCase) ||
                             key.Equals("Width", StringComparison.OrdinalIgnoreCase) ||
                             key.Equals("Height", StringComparison.OrdinalIgnoreCase) ||
                             key.Equals("BitsPerPixel", StringComparison.OrdinalIgnoreCase) ||
                             key.Equals("DisplayOrientation", StringComparison.OrdinalIgnoreCase) ||
                             key.Equals("Primary", StringComparison.OrdinalIgnoreCase) ||
                             key.Equals("FriendlyName", StringComparison.OrdinalIgnoreCase) ||
                             key.Equals("EdidManufactureId", StringComparison.OrdinalIgnoreCase) ||
                             key.Equals("EdidProductCodeId", StringComparison.OrdinalIgnoreCase) ||
                             key.Equals("ConnectorInstance", StringComparison.OrdinalIgnoreCase);

                if (!known)
                    return true;
                if (!_seen.Add(key))
                {
                    errorMessage = $"Setting '{key}' is duplicated.";
                    return false;
                }

                if (key.Equals("Name", StringComparison.OrdinalIgnoreCase))
                    _name = value;
                else if (key.Equals("MonitorDevicePath", StringComparison.OrdinalIgnoreCase))
                    _targetPath = value;
                else if (key.Equals("FriendlyName", StringComparison.OrdinalIgnoreCase))
                    _friendlyName = value;
                else if (key.Equals("SourceAdapterLuid", StringComparison.OrdinalIgnoreCase))
                    return TryReadLong(value, out _sourceAdapter, key, out errorMessage);
                else if (key.Equals("SourceId", StringComparison.OrdinalIgnoreCase))
                    return TryReadUInt(value, out _sourceId, key, out errorMessage);
                else if (key.Equals("TargetAdapterLuid", StringComparison.OrdinalIgnoreCase))
                    return TryReadLong(value, out _targetAdapter, key, out errorMessage);
                else if (key.Equals("TargetId", StringComparison.OrdinalIgnoreCase))
                    return TryReadUInt(value, out _targetId, key, out errorMessage);
                else if (key.Equals("PositionX", StringComparison.OrdinalIgnoreCase))
                    return TryReadInt(value, out _x, key, out errorMessage);
                else if (key.Equals("PositionY", StringComparison.OrdinalIgnoreCase))
                    return TryReadInt(value, out _y, key, out errorMessage);
                else if (key.Equals("Width", StringComparison.OrdinalIgnoreCase))
                    return TryReadInt(value, out _width, key, out errorMessage);
                else if (key.Equals("Height", StringComparison.OrdinalIgnoreCase))
                    return TryReadInt(value, out _height, key, out errorMessage);
                else if (key.Equals("BitsPerPixel", StringComparison.OrdinalIgnoreCase))
                    return TryReadInt(value, out _bitsPerPixel, key, out errorMessage);
                else if (key.Equals("DisplayOrientation", StringComparison.OrdinalIgnoreCase))
                {
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var orientation) ||
                        !TryOrientationToRotation(orientation, out var rotation))
                    {
                        errorMessage = "DisplayOrientation must be between 0 and 3.";
                        return false;
                    }
                    _rotation = rotation;
                }
                else if (key.Equals("Primary", StringComparison.OrdinalIgnoreCase))
                {
                    if (value == "0") _primary = false;
                    else if (value == "1") _primary = true;
                    else
                    {
                        errorMessage = "Primary must be 0 or 1.";
                        return false;
                    }
                }
                else if (key.Equals("EdidManufactureId", StringComparison.OrdinalIgnoreCase))
                    return TryReadUShort(value, out _edidManufactureId, key, out errorMessage);
                else if (key.Equals("EdidProductCodeId", StringComparison.OrdinalIgnoreCase))
                    return TryReadUShort(value, out _edidProductCodeId, key, out errorMessage);
                else if (key.Equals("ConnectorInstance", StringComparison.OrdinalIgnoreCase))
                    return TryReadUInt(value, out _connectorInstance, key, out errorMessage);

                return true;
            }

            public bool TryBuild(
                string sectionName,
                out NativeDisplayProfileMonitor? monitor,
                out string errorMessage)
            {
                monitor = null;
                errorMessage = string.Empty;
                bool inactive = _bitsPerPixel.HasValue && _bitsPerPixel.Value <= 0;

                if (string.IsNullOrWhiteSpace(_name) ||
                    !_x.HasValue || !_y.HasValue ||
                    !_width.HasValue || !_height.HasValue ||
                    _width.Value < 0 || _height.Value < 0 ||
                    (_width.Value == 0) != (_height.Value == 0))
                {
                    errorMessage = $"Saved layout section '{sectionName}' is incomplete or invalid.";
                    return false;
                }

                if (inactive)
                    return true;
                if (_width.Value <= 0 || _height.Value <= 0 || !_rotation.HasValue)
                {
                    errorMessage = $"Active saved layout section '{sectionName}' has no valid size or orientation.";
                    return false;
                }

                bool origin = _x.Value == 0 && _y.Value == 0;
                if (_primary.HasValue && _primary.Value != origin)
                {
                    errorMessage = $"Saved layout section '{sectionName}' has a Primary value inconsistent with its position.";
                    return false;
                }

                monitor = new NativeDisplayProfileMonitor(
                    _name.Trim(),
                    NormaliseTargetPath(_targetPath),
                    _sourceAdapter,
                    _sourceId,
                    _targetAdapter,
                    _targetId,
                    _x.Value,
                    _y.Value,
                    _width.Value,
                    _height.Value,
                    _rotation.Value,
                    origin,
                    _friendlyName.Trim(),
                    _edidManufactureId,
                    _edidProductCodeId,
                    _connectorInstance);
                return true;
            }

            private static bool TryOrientationToRotation(int orientation, out uint rotation)
            {
                rotation = orientation switch { 0 => 1, 1 => 2, 2 => 3, 3 => 4, _ => 0 };
                return rotation != 0;
            }

            private static bool TryReadInt(string value, out int? destination, string key, out string error)
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    destination = parsed;
                    error = string.Empty;
                    return true;
                }
                destination = null;
                error = $"Setting '{key}' is not a valid integer.";
                return false;
            }

            private static bool TryReadUInt(string value, out uint? destination, string key, out string error)
            {
                if (uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                {
                    destination = parsed;
                    error = string.Empty;
                    return true;
                }
                destination = null;
                error = $"Setting '{key}' is not a valid unsigned integer.";
                return false;
            }

            private static bool TryReadUShort(string value, out ushort? destination, string key, out string error)
            {
                if (ushort.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                {
                    destination = parsed;
                    error = string.Empty;
                    return true;
                }
                destination = null;
                error = $"Setting '{key}' is not a valid 16-bit unsigned integer.";
                return false;
            }

            private static bool TryReadLong(string value, out long? destination, string key, out string error)
            {
                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    destination = parsed;
                    error = string.Empty;
                    return true;
                }
                destination = null;
                error = $"Setting '{key}' is not a valid 64-bit integer.";
                return false;
            }
        }
    }

    internal sealed record NativePathCandidate(
        int PathIndex,
        string MonitorDevicePath,
        string SourceDeviceName,
        long SourceAdapterLuid,
        uint SourceId,
        long TargetAdapterLuid,
        uint TargetId,
        bool IsActive,
        bool IsAvailable);

    internal sealed record NativePathRequest(
        string MonitorDevicePath,
        long SavedSourceAdapterLuid,
        uint SavedSourceId,
        long SavedTargetAdapterLuid,
        uint SavedTargetId);

    internal sealed record NativePathSelectionResult(
        bool Success,
        IReadOnlyList<NativePathCandidate> Paths,
        string ErrorMessage);

    internal static class NativeDisplayPathSelector
    {
        internal static NativePathSelectionResult SelectEnablePath(
            string target,
            IReadOnlyCollection<NativePathCandidate> candidates,
            IReadOnlyCollection<(long AdapterLuid, uint SourceId)> occupiedSources)
        {
            if (string.IsNullOrWhiteSpace(target))
                return Failed("No current native target identity was supplied.");

            var available = DistinctCandidates(candidates.Where(c =>
                c.IsAvailable && NativeDisplayProfileCodec.IsStrongTargetPath(c.MonitorDevicePath)));
            bool targetIsPath = NativeDisplayProfileCodec.IsStrongTargetPath(target);
            List<NativePathCandidate> matches;
            if (targetIsPath)
            {
                matches = available.Where(c => TargetPathEquals(c.MonitorDevicePath, target)).ToList();
            }
            else
            {
                matches = available.Where(c =>
                    !string.IsNullOrWhiteSpace(c.SourceDeviceName) &&
                    MonitorTargetResolver.TargetsEquivalent(c.SourceDeviceName, target)).ToList();
            }

            if (matches.Count == 0)
                return Failed("The supplied target is not a currently enumerated present CCD target.");
            if (matches.Select(c => NativeDisplayProfileCodec.NormaliseTargetPath(c.MonitorDevicePath))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
            {
                return Failed("The supplied target matches more than one physical display.");
            }

            var active = matches.Where(c => c.IsActive).ToList();
            if (active.Count == 1)
                return new NativePathSelectionResult(true, active, string.Empty);
            if (active.Count > 1)
                return Failed("Clone or mirror paths are not supported.");

            var occupied = occupiedSources.ToHashSet();
            var eligible = matches
                .Where(c => !occupied.Contains((c.SourceAdapterLuid, c.SourceId)))
                .OrderBy(c => c.SourceAdapterLuid)
                .ThenBy(c => c.SourceId)
                .ThenBy(c => c.TargetAdapterLuid)
                .ThenBy(c => c.TargetId)
                .ThenBy(c => c.PathIndex)
                .ToList();
            if (eligible.Count == 0)
            {
                return Failed("No unused source route is available for this target.");
            }

            // The physical target is already exact. Multiple free source routes do
            // not identify different monitors, so select one deterministically and
            // let CCD validate it before any mutation.
            return new NativePathSelectionResult(true, new[] { eligible[0] }, string.Empty);
        }

        internal static NativePathSelectionResult SelectExactPaths(
            IReadOnlyCollection<NativePathRequest> requests,
            IReadOnlyCollection<NativePathCandidate> candidates)
        {
            if (requests == null || requests.Count == 0)
                return Failed("The profile does not contain a display target.");
            if (requests.Select(r => NativeDisplayProfileCodec.NormaliseTargetPath(r.MonitorDevicePath))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count() != requests.Count)
            {
                return Failed("The profile resolves more than once to the same current target.");
            }

            var available = DistinctCandidates(candidates.Where(c =>
                c.IsAvailable && NativeDisplayProfileCodec.IsStrongTargetPath(c.MonitorDevicePath)));
            var choices = new List<(NativePathRequest Request, List<NativePathCandidate> Candidates)>();
            foreach (var request in requests)
            {
                var targetCandidates = available
                    .Where(c => TargetPathEquals(c.MonitorDevicePath, request.MonitorDevicePath))
                    .ToList();
                if (targetCandidates.Count == 0)
                    return Failed($"Saved target '{request.MonitorDevicePath}' is not currently present.");

                var active = targetCandidates.Where(c => c.IsActive).ToList();
                if (active.Count > 1)
                    return Failed("Clone or mirror paths are not supported.");

                choices.Add((request, targetCandidates));
            }

            choices = choices.OrderBy(c => c.Candidates.Count).ToList();
            var solutions = new List<IReadOnlyList<NativePathCandidate>>();
            Search(0, new List<NativePathCandidate>(), new HashSet<(long, uint)>());
            if (solutions.Count == 0)
            {
                return Failed("No non-clone source-route assignment can realise this profile.");
            }

            return new NativePathSelectionResult(true, solutions[0], string.Empty);

            void Search(
                int index,
                List<NativePathCandidate> selected,
                HashSet<(long AdapterLuid, uint SourceId)> usedSources)
            {
                if (solutions.Count > 0)
                    return;
                if (index == choices.Count)
                {
                    solutions.Add(selected.ToList());
                    return;
                }

                foreach (var candidate in choices[index].Candidates
                             .OrderBy(candidate => candidate.IsActive ? 0 : 1)
                             .ThenBy(candidate =>
                                 candidate.SourceAdapterLuid == choices[index].Request.SavedSourceAdapterLuid &&
                                 candidate.SourceId == choices[index].Request.SavedSourceId &&
                                 candidate.TargetAdapterLuid == choices[index].Request.SavedTargetAdapterLuid &&
                                 candidate.TargetId == choices[index].Request.SavedTargetId
                                     ? 0 : 1)
                             .ThenBy(candidate => candidate.SourceAdapterLuid)
                             .ThenBy(candidate => candidate.SourceId)
                             .ThenBy(candidate => candidate.TargetAdapterLuid)
                             .ThenBy(candidate => candidate.TargetId)
                             .ThenBy(candidate => candidate.PathIndex))
                {
                    var source = (candidate.SourceAdapterLuid, candidate.SourceId);
                    if (!usedSources.Add(source))
                        continue;
                    selected.Add(candidate);
                    Search(index + 1, selected, usedSources);
                    selected.RemoveAt(selected.Count - 1);
                    usedSources.Remove(source);
                }
            }
        }

        private static List<NativePathCandidate> DistinctCandidates(IEnumerable<NativePathCandidate> candidates)
            => candidates
                .GroupBy(c => (
                    Path: NativeDisplayProfileCodec.NormaliseTargetPath(c.MonitorDevicePath).ToUpperInvariant(),
                    c.SourceAdapterLuid,
                    c.SourceId,
                    c.TargetAdapterLuid,
                    c.TargetId,
                    c.IsActive))
                .Select(group => group.OrderBy(c => c.PathIndex).First())
                .ToList();

        private static bool TargetPathEquals(string left, string right)
            => NativeDisplayProfileCodec.NormaliseTargetPath(left).Equals(
                NativeDisplayProfileCodec.NormaliseTargetPath(right),
                StringComparison.OrdinalIgnoreCase);

        private static NativePathSelectionResult Failed(string message)
            => new(false, Array.Empty<NativePathCandidate>(), message);
    }
}
