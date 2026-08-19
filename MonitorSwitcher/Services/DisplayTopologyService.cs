using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using WorkMonitorSwitcher.Model;

namespace WorkMonitorSwitcher.Services
{
    internal readonly record struct DisplayPosition(int X, int Y);

    internal readonly record struct DisplaySize(int Width, int Height);

    internal sealed record DisplaySourceLayout(
        string DeviceName,
        int SourceModeIndex,
        int X,
        int Y,
        int Width,
        int Height);

    internal sealed record ActiveDisplayGeometry(
        string TargetPath,
        string DeviceName,
        int X,
        int Y,
        int Width,
        int Height,
        uint Rotation,
        bool IsPrimary);

    internal sealed record SavedProfileMembershipResolution(
        bool Success,
        IReadOnlyList<DetectedMonitor> PresentSavedMonitors,
        int UnavailableSavedMonitorCount,
        string ErrorMessage)
    {
        public static SavedProfileMembershipResolution Failed(string message)
            => new(false, Array.Empty<DetectedMonitor>(), 0, message);
    }

    internal enum SavedLayoutAppliedState
    {
        Applied,
        NotApplied,
        Inconclusive
    }

    internal sealed record SavedLayoutAppliedCheck(
        SavedLayoutAppliedState State,
        string Message);

    internal sealed class DisplayTopologyResult
    {
        public bool Success { get; init; }
        public int? ValidateCode { get; init; }
        public int? ApplyCode { get; init; }
        public bool RollbackAttempted { get; init; }
        public bool RollbackVerified { get; init; }
        public string Message { get; init; } = string.Empty;
        public IReadOnlyList<string> Details { get; init; } = Array.Empty<string>();
    }

    internal sealed class DisplayTopologyService
    {
        private const int ErrorSuccess = 0;
        private const int ErrorInsufficientBuffer = 122;

        private const uint QdcAllPaths = 0x00000001;
        private const uint QdcOnlyActivePaths = 0x00000002;

        private const uint SdcTopologySupplied = 0x00000010;
        private const uint SdcUseSuppliedDisplayConfig = 0x00000020;
        private const uint SdcValidate = 0x00000040;
        private const uint SdcApply = 0x00000080;
        private const uint SdcSaveToDatabase = 0x00000200;
        private const uint SdcAllowChanges = 0x00000400;
        private const uint SdcAllowPathOrderChanges = 0x00002000;

        private const int DisplayConfigDeviceInfoGetSourceName = 1;
        private const int DisplayConfigDeviceInfoGetTargetName = 2;
        private const uint DisplayConfigModeInfoTypeSource = 1;
        private const uint DisplayConfigPathActive = 0x00000001;
        private const uint DisplayConfigPathModeIndexInvalid = 0xffffffff;
        private const int PositionNormalisationTolerancePixels = 1;
        private const int PostApplyVerificationAttempts = 3;

        internal static uint GetPersistentApplyFlags()
            => SdcUseSuppliedDisplayConfig |
               SdcAllowChanges |
               SdcSaveToDatabase |
               SdcApply;

        internal static uint GetAllPathsQueryFlags() => QdcAllPaths;

        internal static uint GetTopologyActivationValidateFlags()
            => SdcTopologySupplied | SdcAllowPathOrderChanges | SdcValidate;

        internal static uint GetTopologyActivationApplyFlags()
            => SdcTopologySupplied | SdcAllowPathOrderChanges | SdcApply;

        internal static uint GetBestModeActivationValidateFlags()
            => SdcUseSuppliedDisplayConfig | SdcAllowChanges | SdcValidate;

        internal static uint GetBestModeActivationApplyFlags()
            => SdcUseSuppliedDisplayConfig | SdcAllowChanges | SdcSaveToDatabase | SdcApply;

        public bool TryGetDisplayActiveState(
            string deviceName,
            out bool isActive,
            out string errorMessage)
        {
            isActive = false;
            errorMessage = string.Empty;

            if (string.IsNullOrWhiteSpace(deviceName))
            {
                errorMessage = "No display was supplied.";
                return false;
            }

            try
            {
                var snapshot = QueryActiveTopology();
                isActive = snapshot.Entries.Any(entry =>
                    DeviceNameEquals(entry.Name, deviceName) ||
                    TargetPathEquals(entry.MonitorDevicePath, deviceName));
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = $"Unable to query display topology: {ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// Captures the current extended desktop into MonitorSwitcher's versioned
        /// .cfg format. This is read-only with respect to the live display topology.
        /// </summary>
        public PersistenceResult CaptureLayoutToConfig(string layoutPath)
        {
            if (string.IsNullOrWhiteSpace(layoutPath))
                return PersistenceResult.Failed("A layout destination is required.");

            try
            {
                var snapshot = QueryActiveTopology();
                if (!TryValidateExtendedDesktop(snapshot.Entries, out var validationError))
                    return PersistenceResult.Failed(validationError);

                var monitors = snapshot.Entries.Select(entry =>
                    new NativeDisplayProfileMonitor(
                        entry.Name,
                        entry.MonitorDevicePath,
                        ToInt64(entry.Path.sourceInfo.adapterId),
                        entry.Path.sourceInfo.id,
                        ToInt64(entry.Path.targetInfo.adapterId),
                        entry.Path.targetInfo.id,
                        entry.X,
                        entry.Y,
                        entry.Width,
                        entry.Height,
                        entry.Rotation,
                        entry.X == 0 && entry.Y == 0,
                        entry.TargetFriendlyName,
                        entry.EdidIdsValid ? entry.EdidManufactureId : null,
                        entry.EdidIdsValid ? entry.EdidProductCodeId : null,
                        entry.ConnectorInstance)).ToList();
                var profile = new NativeDisplayProfile(NativeDisplayProfileCodec.CurrentVersion, monitors);
                var contents = NativeDisplayProfileCodec.Serialise(profile);

                string fullPath = Path.GetFullPath(layoutPath);
                string directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
                if (directory.Length == 0)
                    return PersistenceResult.Failed("The layout destination directory could not be resolved.");
                Directory.CreateDirectory(directory);

                string stagingPath = Path.Combine(
                    directory,
                    $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.native.tmp");
                try
                {
                    File.WriteAllText(stagingPath, contents);
                    if (!NativeDisplayProfileCodec.TryRead(stagingPath, out _, out var parseError))
                        return PersistenceResult.Failed($"The captured native profile was invalid: {parseError}");

                    PromoteCapturedProfile(stagingPath, fullPath);
                    stagingPath = string.Empty;
                    return NativeDisplayProfileCodec.TryRead(fullPath, out _, out parseError)
                        ? PersistenceResult.Saved()
                        : PersistenceResult.Failed($"The saved native profile could not be verified: {parseError}");
                }
                finally
                {
                    TryDeleteFile(stagingPath);
                }
            }
            catch (Exception ex)
            {
                return PersistenceResult.Failed($"Unable to capture the native display profile: {ex.Message}");
            }
        }

        /// <summary>
        /// Enables one present, currently enumerated CCD target. The argument must
        /// be either its exact monitorDevicePath or an exact current CCD source
        /// name. Friendly names and historical aliases are deliberately rejected.
        /// </summary>
        public DisplayTopologyResult EnableDisplay(string currentNativeTarget)
        {
            if (string.IsNullOrWhiteSpace(currentNativeTarget))
                return Failure("No current native display target was supplied.");

            TopologySnapshot? original = null;
            bool topologyMayHaveChanged = false;
            try
            {
                original = QueryActiveTopology();
                if (!TryValidateExtendedDesktop(original.Entries, out var validationError))
                    return Failure(validationError);

                var allPaths = QueryAllTopologyPaths();
                var candidates = allPaths.Entries.Select(ToCandidate).ToList();
                var occupied = original.Entries
                    .Select(entry => (ToInt64(entry.Path.sourceInfo.adapterId), entry.Path.sourceInfo.id))
                    .ToList();
                var selection = NativeDisplayPathSelector.SelectEnablePath(
                    currentNativeTarget,
                    candidates,
                    occupied);
                if (!selection.Success)
                    return Failure(selection.ErrorMessage);

                var selectedCandidate = selection.Paths.Single();
                if (selectedCandidate.IsActive)
                {
                    return new DisplayTopologyResult
                    {
                        Success = true,
                        Message = "The selected native display target is already active."
                    };
                }

                var selectedPath = allPaths.Entries.Single(entry => entry.PathIndex == selectedCandidate.PathIndex);
                var requested = original.Entries
                    .Select(entry => entry.Path)
                    .Append(selectedPath.Path)
                    .ToArray();
                var expectedTargets = original.Entries
                    .Select(entry => entry.MonitorDevicePath)
                    .Append(selectedPath.MonitorDevicePath)
                    .ToList();

                var activation = ActivateTopology(requested, expectedTargets, original);
                topologyMayHaveChanged = activation.ApplyCode == ErrorSuccess;
                if (!activation.Success)
                    return topologyMayHaveChanged
                        ? WithRollbackDetails(activation, original)
                        : activation;

                return new DisplayTopologyResult
                {
                    Success = true,
                    ValidateCode = activation.ValidateCode,
                    ApplyCode = activation.ApplyCode,
                    Message = $"Enabled native display '{selectedPath.TargetFriendlyName}' using a Windows-managed arrangement."
                };
            }
            catch (Exception ex)
            {
                var failure = Failure($"Unable to enable the native display target: {ex.Message}");
                return topologyMayHaveChanged && original != null
                    ? WithRollbackDetails(failure, original)
                    : failure;
            }
        }

        /// <summary>
        /// Applies only the exact active target set from a native profile. Source
        /// modes, positions, rotation and primary selection come from Windows'
        /// persistence database and are never read from the profile here.
        /// </summary>
        public DisplayTopologyResult RestoreExactDisplaySetFromConfig(
            string layoutPath,
            IReadOnlyCollection<DetectedMonitor>? detectedMonitors = null,
            IReadOnlyCollection<SavedLayoutIdentity>? savedIdentities = null)
        {
            if (!NativeDisplayProfileCodec.TryRead(layoutPath, out var profile, out var profileError))
                return Failure(profileError);
            if (profile.Version != NativeDisplayProfileCodec.CurrentVersion ||
                profile.Monitors.Any(monitor => !monitor.HasNativeRouteIdentity))
            {
                return Failure(
                    "This legacy profile has no complete native CCD identity. Save it once with this version before using exact-set restore.");
            }

            TopologySnapshot? original = null;
            bool topologyMayHaveChanged = false;
            try
            {
                original = QueryActiveTopology();
                if (!TryValidateExtendedDesktop(original.Entries, out var validationError))
                    return Failure(validationError);

                var allPaths = QueryAllTopologyPaths();
                var candidates = allPaths.Entries.Select(ToCandidate).ToList();
                var targetResolution = ResolveCurrentTargetPaths(
                    profile,
                    candidates,
                    detectedMonitors,
                    savedIdentities);
                if (!targetResolution.Success)
                    return Failure(targetResolution.ErrorMessage);

                var requestedTargets = GetProfileMonitorSet(
                    profile,
                    targetResolution.TargetPathByLayoutDevice);
                var currentTargets = original.Entries
                    .Select(entry => NativeDisplayProfileCodec.NormaliseTargetPath(entry.MonitorDevicePath))
                    .ToList();
                if (currentTargets.Count == requestedTargets.Count &&
                    currentTargets.Distinct(StringComparer.OrdinalIgnoreCase).Count() == currentTargets.Count &&
                    requestedTargets.SetEquals(currentTargets))
                {
                    return new DisplayTopologyResult
                    {
                        Success = true,
                        Message = "The requested monitor set is already active; Windows' arrangement was left unchanged."
                    };
                }

                var requests = profile.Monitors.Select(monitor => new NativePathRequest(
                    targetResolution.TargetPathByLayoutDevice[monitor.LayoutDeviceName],
                    monitor.SourceAdapterLuid!.Value,
                    monitor.SourceId!.Value,
                    monitor.TargetAdapterLuid!.Value,
                    monitor.TargetId!.Value)).ToList();
                var selection = NativeDisplayPathSelector.SelectExactPaths(requests, candidates);
                if (!selection.Success)
                    return Failure(selection.ErrorMessage);

                var selectedByTarget = selection.Paths.ToDictionary(
                    path => NativeDisplayProfileCodec.NormaliseTargetPath(path.MonitorDevicePath),
                    StringComparer.OrdinalIgnoreCase);
                var currentOrder = original.Entries
                    .Select((entry, index) => new
                    {
                        Target = NativeDisplayProfileCodec.NormaliseTargetPath(entry.MonitorDevicePath),
                        Index = index
                    })
                    .ToDictionary(item => item.Target, item => item.Index, StringComparer.OrdinalIgnoreCase);
                var orderedCandidates = selectedByTarget.Values
                    .OrderBy(candidate => currentOrder.TryGetValue(
                        NativeDisplayProfileCodec.NormaliseTargetPath(candidate.MonitorDevicePath),
                        out var index) ? index : int.MaxValue)
                    .ThenBy(candidate => candidate.PathIndex)
                    .ToList();
                var requestedPaths = orderedCandidates
                    .Select(candidate => allPaths.Entries.Single(entry => entry.PathIndex == candidate.PathIndex).Path)
                    .ToArray();
                var expectedTargets = orderedCandidates.Select(candidate => candidate.MonitorDevicePath).ToList();

                var activation = ActivateTopology(requestedPaths, expectedTargets, original);
                topologyMayHaveChanged = activation.ApplyCode == ErrorSuccess;
                if (!activation.Success)
                    return topologyMayHaveChanged
                        ? WithRollbackDetails(activation, original)
                        : activation;

                return new DisplayTopologyResult
                {
                    Success = true,
                    ValidateCode = activation.ValidateCode,
                    ApplyCode = activation.ApplyCode,
                    Message = $"Applied monitor set '{Path.GetFileName(layoutPath)}' using a Windows-managed arrangement."
                };
            }
            catch (Exception ex)
            {
                var failure = Failure($"Unable to restore the exact native display profile: {ex.Message}");
                return topologyMayHaveChanged && original != null
                    ? WithRollbackDetails(failure, original)
                    : failure;
            }
        }

        internal static HashSet<string> GetProfileMonitorSet(
            NativeDisplayProfile profile,
            IReadOnlyDictionary<string, string> resolvedTargetPaths)
        {
            if (profile == null)
                throw new ArgumentNullException(nameof(profile));
            if (resolvedTargetPaths == null)
                throw new ArgumentNullException(nameof(resolvedTargetPaths));

            return profile.Monitors
                .Select(monitor => resolvedTargetPaths[monitor.LayoutDeviceName])
                .Select(NativeDisplayProfileCodec.NormaliseTargetPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private static bool TryValidateExtendedDesktop(
            IReadOnlyCollection<PathEntry> entries,
            out string errorMessage)
        {
            errorMessage = string.Empty;
            if (entries == null || entries.Count == 0)
            {
                errorMessage = "No active display path is available.";
                return false;
            }

            if (entries.Count(entry => entry.X == 0 && entry.Y == 0) != 1)
            {
                errorMessage = "Only an extended desktop with one unambiguous primary origin is supported.";
                return false;
            }

            if (entries.Any(entry =>
                    string.IsNullOrWhiteSpace(entry.Name) ||
                    !NativeDisplayProfileCodec.IsStrongTargetPath(entry.MonitorDevicePath) ||
                    !entry.IsAvailable ||
                    entry.Width <= 0 ||
                    entry.Height <= 0 ||
                    entry.Rotation is < 1 or > 4))
            {
                errorMessage = "An active path is unavailable or has no complete CCD source, target identity, size, or rotation.";
                return false;
            }

            if (entries.Select(entry =>
                        NativeDisplayProfileCodec.NormaliseTargetPath(entry.MonitorDevicePath))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Count ||
                entries.Select(entry => (ToInt64(entry.Path.sourceInfo.adapterId), entry.Path.sourceInfo.id))
                    .Distinct().Count() != entries.Count)
            {
                errorMessage = "Clone, mirror, or duplicate target paths are not supported.";
                return false;
            }

            return true;
        }

        private static void PromoteCapturedProfile(string stagingPath, string fullPath)
        {
            if (!File.Exists(fullPath))
            {
                File.Move(stagingPath, fullPath);
                return;
            }

            string replacementBackup = Path.Combine(
                Path.GetDirectoryName(fullPath)!,
                $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.previous.tmp");
            File.Replace(stagingPath, fullPath, replacementBackup, ignoreMetadataErrors: true);
            try
            {
                File.Move(replacementBackup, fullPath + ".bak", overwrite: true);
            }
            catch
            {
                try
                {
                    File.Replace(replacementBackup, fullPath, null, ignoreMetadataErrors: true);
                }
                catch
                {
                    // The immediate predecessor remains at replacementBackup.
                }
                throw;
            }
        }

        private static void TryDeleteFile(string? path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }

        private static NativePathCandidate ToCandidate(PathEntry entry)
            => new(
                entry.PathIndex,
                entry.MonitorDevicePath,
                entry.Name,
                ToInt64(entry.Path.sourceInfo.adapterId),
                entry.Path.sourceInfo.id,
                ToInt64(entry.Path.targetInfo.adapterId),
                entry.Path.targetInfo.id,
                entry.IsActive,
                entry.IsAvailable);

        private static DisplayTopologyResult ActivateTopology(
            DISPLAYCONFIG_PATH_INFO[] requestedPaths,
            IReadOnlyCollection<string> expectedTargetPaths,
            TopologySnapshot original)
        {
            if (requestedPaths.Length == 0 || expectedTargetPaths.Count == 0)
                return Failure("No display path was selected for activation.");

            for (int index = 0; index < requestedPaths.Length; index++)
            {
                var path = requestedPaths[index];
                path.flags |= DisplayConfigPathActive;
                path.sourceInfo.modeInfoIdx = DisplayConfigPathModeIndexInvalid;
                path.targetInfo.modeInfoIdx = DisplayConfigPathModeIndexInvalid;
                requestedPaths[index] = path;
            }

            uint validateFlags = GetTopologyActivationValidateFlags();
            uint applyFlags = GetTopologyActivationApplyFlags();
            var validateCode = SetDisplayConfig(
                (uint)requestedPaths.Length,
                requestedPaths,
                0,
                null,
                validateFlags);
            if (validateCode != ErrorSuccess)
            {
                // A newly connected target may not yet have this route in CCD's
                // persistence database. Let best-mode logic create it while still
                // supplying only the explicitly selected paths.
                validateFlags = GetBestModeActivationValidateFlags();
                applyFlags = GetBestModeActivationApplyFlags();
                validateCode = SetDisplayConfig(
                    (uint)requestedPaths.Length,
                    requestedPaths,
                    0,
                    null,
                    validateFlags);
                if (validateCode != ErrorSuccess)
                {
                    return new DisplayTopologyResult
                    {
                        Success = false,
                        ValidateCode = validateCode,
                        Message = $"Native display path validation failed ({validateCode})."
                    };
                }
            }

            var applyCode = SetDisplayConfig(
                (uint)requestedPaths.Length,
                requestedPaths,
                0,
                null,
                applyFlags);
            if (applyCode != ErrorSuccess)
            {
                return new DisplayTopologyResult
                {
                    Success = false,
                    ValidateCode = validateCode,
                    ApplyCode = applyCode,
                    Message = $"Native display path activation failed ({applyCode})."
                };
            }

            var expected = expectedTargetPaths
                .Select(NativeDisplayProfileCodec.NormaliseTargetPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            IReadOnlyList<string> verification = Array.Empty<string>();
            for (int attempt = 0; attempt < PostApplyVerificationAttempts; attempt++)
            {
                try
                {
                    var active = QueryActiveTopology();
                    var actual = active.Entries
                        .Select(entry => NativeDisplayProfileCodec.NormaliseTargetPath(entry.MonitorDevicePath))
                        .ToList();
                    bool exactTargetSet = actual.Count == expected.Count &&
                                          actual.Distinct(StringComparer.OrdinalIgnoreCase).Count() == actual.Count &&
                                          expected.SetEquals(actual);
                    IReadOnlyList<string> geometryIssues = Array.Empty<string>();
                    bool retainedGeometryVerified = exactTargetSet &&
                        TryVerifyRetainedDisplayGeometry(
                            original.Entries.Select(ToGeometry).ToList(),
                            active.Entries.Select(ToGeometry).ToList(),
                            expected,
                            out geometryIssues);
                    if (retainedGeometryVerified)
                    {
                        return new DisplayTopologyResult
                        {
                            Success = true,
                            ValidateCode = validateCode,
                            ApplyCode = applyCode,
                            Message = "Native display paths were activated and verified."
                        };
                    }

                    verification = exactTargetSet
                        ? geometryIssues
                        : actual
                            .Where(path => !expected.Contains(path))
                            .Select(path => $"Unexpected active target: {path}.")
                            .Concat(expected.Where(path => !actual.Contains(path, StringComparer.OrdinalIgnoreCase))
                                .Select(path => $"Expected target is inactive: {path}."))
                            .ToList();
                }
                catch (Exception ex)
                {
                    verification = new[] { $"Unable to query the activated topology: {ex.Message}" };
                }

                if (attempt + 1 < PostApplyVerificationAttempts)
                    System.Threading.Thread.Sleep(100);
            }

            return new DisplayTopologyResult
            {
                Success = false,
                ValidateCode = validateCode,
                ApplyCode = applyCode,
                Message = "Native activation returned success, but target-set or retained-arrangement verification failed.",
                Details = verification
            };
        }

        private static ActiveDisplayGeometry ToGeometry(PathEntry entry)
            => new(
                NativeDisplayProfileCodec.NormaliseTargetPath(entry.MonitorDevicePath),
                entry.Name,
                entry.X,
                entry.Y,
                entry.Width,
                entry.Height,
                entry.Rotation,
                entry.X == 0 && entry.Y == 0);

        internal static bool TryVerifyRetainedDisplayGeometry(
            IReadOnlyCollection<ActiveDisplayGeometry> original,
            IReadOnlyCollection<ActiveDisplayGeometry> actual,
            IReadOnlyCollection<string> requestedTargetPaths,
            out IReadOnlyList<string> discrepancies)
        {
            if (original == null)
                throw new ArgumentNullException(nameof(original));
            if (actual == null)
                throw new ArgumentNullException(nameof(actual));
            if (requestedTargetPaths == null)
                throw new ArgumentNullException(nameof(requestedTargetPaths));

            var issues = new List<string>();
            var requested = requestedTargetPaths
                .Select(NativeDisplayProfileCodec.NormaliseTargetPath)
                .Where(path => path.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var retained = original
                .Where(entry => requested.Contains(
                    NativeDisplayProfileCodec.NormaliseTargetPath(entry.TargetPath)))
                .ToList();
            var actualByTarget = actual
                .GroupBy(
                    entry => NativeDisplayProfileCodec.NormaliseTargetPath(entry.TargetPath),
                    StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
            if (actual.Count(entry => entry.IsPrimary) != 1)
                issues.Add("The activated extended desktop does not contain exactly one primary display.");

            foreach (var expected in retained)
            {
                var target = NativeDisplayProfileCodec.NormaliseTargetPath(expected.TargetPath);
                if (!actualByTarget.TryGetValue(target, out var matches) || matches.Count != 1)
                {
                    issues.Add($"Retained target '{target}' is not represented exactly once after activation.");
                    continue;
                }

                var observed = matches[0];
                if (observed.Rotation != expected.Rotation)
                {
                    issues.Add(
                        $"Retained target '{target}' rotation is {observed.Rotation}; expected {expected.Rotation}.");
                }
                if (!EffectiveDisplaySizeMatches(
                        new DisplaySize(observed.Width, observed.Height),
                        new DisplaySize(expected.Width, expected.Height),
                        expected.Rotation,
                        allowQuarterTurnEquivalent: false))
                {
                    issues.Add(
                        $"Retained target '{target}' effective size is {observed.Width}x{observed.Height}; " +
                        $"expected {expected.Width}x{expected.Height}.");
                }
            }

            var retainedPrimary = retained.Where(entry => entry.IsPrimary).ToList();
            if (retainedPrimary.Count > 0)
            {
                if (retainedPrimary.Count != 1)
                {
                    issues.Add("The captured retained topology did not contain one unambiguous primary display.");
                }
                else
                {
                    var primaryTarget = NativeDisplayProfileCodec.NormaliseTargetPath(retainedPrimary[0].TargetPath);
                    if (!actualByTarget.TryGetValue(primaryTarget, out var primaryMatches) ||
                        primaryMatches.Count != 1 ||
                        !primaryMatches[0].IsPrimary)
                    {
                        issues.Add($"Retained primary target '{primaryTarget}' is no longer primary.");
                    }
                }
            }

            for (int leftIndex = 0; leftIndex < retained.Count; leftIndex++)
            {
                var expectedLeft = retained[leftIndex];
                var leftTarget = NativeDisplayProfileCodec.NormaliseTargetPath(expectedLeft.TargetPath);
                if (!actualByTarget.TryGetValue(leftTarget, out var actualLeftMatches) || actualLeftMatches.Count != 1)
                    continue;

                for (int rightIndex = leftIndex + 1; rightIndex < retained.Count; rightIndex++)
                {
                    var expectedRight = retained[rightIndex];
                    var rightTarget = NativeDisplayProfileCodec.NormaliseTargetPath(expectedRight.TargetPath);
                    if (!actualByTarget.TryGetValue(rightTarget, out var actualRightMatches) || actualRightMatches.Count != 1)
                        continue;

                    long expectedDeltaX = (long)expectedRight.X - expectedLeft.X;
                    long expectedDeltaY = (long)expectedRight.Y - expectedLeft.Y;
                    long actualDeltaX = (long)actualRightMatches[0].X - actualLeftMatches[0].X;
                    long actualDeltaY = (long)actualRightMatches[0].Y - actualLeftMatches[0].Y;
                    if (Math.Abs(actualDeltaX - expectedDeltaX) > PositionNormalisationTolerancePixels ||
                        Math.Abs(actualDeltaY - expectedDeltaY) > PositionNormalisationTolerancePixels)
                    {
                        issues.Add(
                            $"Retained targets '{leftTarget}' and '{rightTarget}' moved relative to one another: " +
                            $"offset is {actualDeltaX},{actualDeltaY}; expected {expectedDeltaX},{expectedDeltaY}.");
                    }
                }
            }

            discrepancies = issues;
            return issues.Count == 0;
        }

        private static DisplayTopologyResult WithRollbackDetails(
            DisplayTopologyResult failure,
            TopologySnapshot original)
        {
            const string restoredMessage = "The previous display topology was restored and verified.";
            const string restoredDetail = "The original display topology was restored and verified after the failed operation.";
            var rollbackDetails = failure.Details
                .Where(detail => !detail.Equals(restoredDetail, StringComparison.Ordinal))
                .ToList();
            bool rollbackVerified = false;
            try
            {
                var rollbackPaths = original.Entries.Select(entry => entry.Path).ToArray();
                var rollbackCode = SetDisplayConfig(
                    (uint)rollbackPaths.Length,
                    rollbackPaths,
                    original.ModeCount,
                    original.Modes,
                    GetPersistentApplyFlags());
                if (rollbackCode != ErrorSuccess)
                {
                    rollbackDetails.Add($"Rollback of the original display topology failed ({rollbackCode}).");
                }
                else
                {
                    bool verified = false;
                    IReadOnlyList<string> verificationDetails = Array.Empty<string>();
                    for (int attempt = 0; attempt < PostApplyVerificationAttempts; attempt++)
                    {
                        try
                        {
                            verified = TryVerifySnapshotRestored(
                                QueryActiveTopology(),
                                original,
                                out verificationDetails);
                        }
                        catch (Exception ex)
                        {
                            verificationDetails = new[]
                            {
                                $"Unable to query the topology after rollback: {ex.Message}"
                            };
                        }

                        if (verified)
                            break;
                        if (attempt + 1 < PostApplyVerificationAttempts)
                            System.Threading.Thread.Sleep(100);
                    }

                    if (verified)
                    {
                        rollbackVerified = true;
                        rollbackDetails.Add(restoredDetail);
                    }
                    else
                    {
                        rollbackDetails.Add(
                            "Windows accepted the rollback request, but the original topology could not be verified.");
                        rollbackDetails.AddRange(
                            verificationDetails.Select(detail => $"Rollback verification: {detail}"));
                    }
                }
            }
            catch (Exception ex)
            {
                rollbackDetails.Add($"Rollback of the original display topology failed: {ex.Message}");
            }

            var failureMessage = failure.Message.EndsWith(
                    $" {restoredMessage}",
                    StringComparison.Ordinal)
                ? failure.Message[..^(restoredMessage.Length + 1)]
                : failure.Message;

            return new DisplayTopologyResult
            {
                Success = false,
                ValidateCode = failure.ValidateCode,
                ApplyCode = failure.ApplyCode,
                RollbackAttempted = true,
                RollbackVerified = rollbackVerified,
                Message = rollbackVerified
                    ? $"{failureMessage} {restoredMessage}"
                    : $"{failureMessage} Rollback could not be verified; refresh Windows Display Settings before another monitor action.",
                Details = rollbackDetails
            };
        }

        private static bool TryVerifySnapshotRestored(
            TopologySnapshot actual,
            TopologySnapshot expected,
            out IReadOnlyList<string> discrepancies)
        {
            var issues = new List<string>();
            if (actual.Entries.Count != expected.Entries.Count)
            {
                issues.Add(
                    $"Active display count is {actual.Entries.Count}; expected {expected.Entries.Count}.");
            }

            var expectedPrimary = expected.Entries
                .Where(entry => entry.X == 0 && entry.Y == 0)
                .ToList();
            var actualPrimary = actual.Entries
                .Where(entry => entry.X == 0 && entry.Y == 0)
                .ToList();
            if (expectedPrimary.Count != 1 || actualPrimary.Count != 1 ||
                !TargetPathEquals(
                    expectedPrimary.SingleOrDefault()?.MonitorDevicePath,
                    actualPrimary.SingleOrDefault()?.MonitorDevicePath))
            {
                issues.Add("The primary physical display was not restored exactly.");
            }

            foreach (var expectedEntry in expected.Entries)
            {
                var matches = actual.Entries
                    .Where(entry => TargetPathEquals(
                        entry.MonitorDevicePath,
                        expectedEntry.MonitorDevicePath))
                    .ToList();
                if (matches.Count != 1)
                {
                    issues.Add(
                        $"Expected physical target '{expectedEntry.MonitorDevicePath}' matched {matches.Count} active paths.");
                    continue;
                }

                var actualEntry = matches[0];
                if (actualEntry.X != expectedEntry.X || actualEntry.Y != expectedEntry.Y)
                {
                    issues.Add(
                        $"Target '{expectedEntry.MonitorDevicePath}' position is {actualEntry.X},{actualEntry.Y}; " +
                        $"expected {expectedEntry.X},{expectedEntry.Y}.");
                }
                if (actualEntry.Width != expectedEntry.Width || actualEntry.Height != expectedEntry.Height)
                {
                    issues.Add(
                        $"Target '{expectedEntry.MonitorDevicePath}' size is {actualEntry.Width}x{actualEntry.Height}; " +
                        $"expected {expectedEntry.Width}x{expectedEntry.Height}.");
                }
                if (actualEntry.Rotation != expectedEntry.Rotation)
                {
                    issues.Add(
                        $"Target '{expectedEntry.MonitorDevicePath}' rotation is {actualEntry.Rotation}; " +
                        $"expected {expectedEntry.Rotation}.");
                }
            }

            discrepancies = issues;
            return issues.Count == 0;
        }

        private static TargetResolutionResult ResolveCurrentTargetPaths(
            NativeDisplayProfile profile,
            IReadOnlyCollection<NativePathCandidate> candidates,
            IReadOnlyCollection<DetectedMonitor>? detectedMonitors,
            IReadOnlyCollection<SavedLayoutIdentity>? savedIdentities)
        {
            var presentPaths = candidates
                .Where(candidate => candidate.IsAvailable &&
                                    NativeDisplayProfileCodec.IsStrongTargetPath(candidate.MonitorDevicePath))
                .Select(candidate => NativeDisplayProfileCodec.NormaliseTargetPath(candidate.MonitorDevicePath))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (detectedMonitors == null || savedIdentities == null ||
                !HasExactNativeIdentityPair(profile, savedIdentities))
            {
                return TargetResolutionResult.Failed(
                    "Exact native restore requires a complete current physical detection and matching version-2 identity map. Save this profile again first.");
            }

            var presentDetected = detectedMonitors.Where(detected => detected.IsPresent).ToList();

            foreach (var monitor in profile.Monitors)
            {
                var savedIdentity = savedIdentities
                    .Where(identity => DeviceNameEquals(
                        FirstNonBlank(identity.LayoutDeviceName, identity.DeviceName),
                        monitor.LayoutDeviceName))
                    .ToList();
                if (savedIdentity.Count != 1)
                {
                    return TargetResolutionResult.Failed(
                        $"Saved target '{monitor.LayoutDeviceName}' has no unique physical identity mapping.");
                }

                var identity = savedIdentity[0];
                var identityMatches = ResolveNativeIdentityToDetected(
                    identity,
                    presentDetected);
                var matches = identityMatches
                    .Select(detected => detected.NativeTargetPath)
                    .Where(path => NativeDisplayProfileCodec.IsStrongTargetPath(path) &&
                                   presentPaths.Contains(path, StringComparer.OrdinalIgnoreCase) &&
                                   !usedPaths.Contains(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (matches.Count != 1)
                {
                    return TargetResolutionResult.Failed(
                        $"Saved target '{monitor.LayoutDeviceName}' could not be mapped uniquely to one present CCD target.");
                }

                usedPaths.Add(matches[0]);
                resolved[monitor.LayoutDeviceName] = matches[0];
            }

            return new TargetResolutionResult(true, resolved, string.Empty);
        }

        internal static SavedProfileMembershipResolution ResolveSavedProfileMembership(
            string layoutPath,
            IReadOnlyCollection<DetectedMonitor>? detectedMonitors,
            IReadOnlyCollection<SavedLayoutIdentity>? savedIdentities)
        {
            if (string.IsNullOrWhiteSpace(layoutPath))
                return SavedProfileMembershipResolution.Failed("No saved profile path was supplied.");
            if (!NativeDisplayProfileCodec.TryRead(layoutPath, out var profile, out var profileError))
                return SavedProfileMembershipResolution.Failed(profileError);
            if (profile.Version != NativeDisplayProfileCodec.CurrentVersion ||
                profile.Monitors.Any(monitor => !monitor.HasNativeRouteIdentity))
            {
                return SavedProfileMembershipResolution.Failed(
                    "This profile has no complete current native display identity.");
            }
            if (detectedMonitors == null || savedIdentities == null ||
                !HasExactNativeIdentityPair(profile, savedIdentities))
            {
                return SavedProfileMembershipResolution.Failed(
                    "The profile has no complete matching physical identity map.");
            }

            var presentDetected = detectedMonitors
                .Where(monitor => monitor.IsPresent &&
                                  NativeDisplayProfileCodec.IsStrongTargetPath(monitor.NativeTargetPath))
                .ToList();
            var usedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var resolved = new List<DetectedMonitor>();
            int unavailable = 0;

            foreach (var profileMonitor in profile.Monitors)
            {
                var identities = savedIdentities
                    .Where(identity => DeviceNameEquals(
                        GetIdentityLayoutDeviceName(identity),
                        profileMonitor.LayoutDeviceName))
                    .ToList();
                if (identities.Count != 1)
                {
                    unavailable++;
                    continue;
                }

                var matches = ResolveNativeIdentityToDetected(
                        identities[0],
                        presentDetected)
                    .Where(monitor =>
                        NativeDisplayProfileCodec.IsStrongTargetPath(monitor.NativeTargetPath) &&
                        !usedTargets.Contains(NativeDisplayProfileCodec.NormaliseTargetPath(monitor.NativeTargetPath)))
                    .GroupBy(
                        monitor => NativeDisplayProfileCodec.NormaliseTargetPath(monitor.NativeTargetPath),
                        StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToList();
                if (matches.Count != 1)
                {
                    unavailable++;
                    continue;
                }

                usedTargets.Add(NativeDisplayProfileCodec.NormaliseTargetPath(matches[0].NativeTargetPath));
                resolved.Add(matches[0]);
            }

            return new SavedProfileMembershipResolution(
                true,
                resolved,
                unavailable,
                string.Empty);
        }

        private static bool HasExactNativeIdentityPair(
            NativeDisplayProfile profile,
            IReadOnlyCollection<SavedLayoutIdentity>? identities)
        {
            if (profile.Version <= 0 || identities == null || identities.Count != profile.Monitors.Count)
                return false;

            var identityByName = identities
                .Where(identity =>
                    !string.IsNullOrWhiteSpace(GetIdentityLayoutDeviceName(identity)) &&
                    NativeDisplayProfileCodec.IsStrongTargetPath(identity.NativeTargetPath))
                .GroupBy(GetIdentityLayoutDeviceName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
            if (identityByName.Count != profile.Monitors.Count ||
                identityByName.Values.Any(group => group.Count != 1))
            {
                return false;
            }

            return profile.Monitors.All(monitor =>
                identityByName.TryGetValue(monitor.LayoutDeviceName, out var matches) &&
                TargetPathEquals(matches[0].NativeTargetPath, monitor.MonitorDevicePath));
        }

        private static IReadOnlyList<DetectedMonitor> FindUniqueDetectedByStrongIdentity(
            SavedLayoutIdentity saved,
            IReadOnlyCollection<DetectedMonitor> detected)
        {
            var selectors = new (
                string SavedValue,
                Func<DetectedMonitor, string?> Selector,
                Func<string, bool> IsUsable)[]
            {
                (saved.StableKey, monitor => monitor.StableKey, IsStrongStableKey),
                (saved.SerialNumber, monitor => monitor.SerialNumber, DetectionService.IsCredibleSerial),
                (saved.InstanceId, monitor => monitor.InstanceId, value => !string.IsNullOrWhiteSpace(value)),
                (saved.MonitorKey, monitor => monitor.MonitorKey, value => !string.IsNullOrWhiteSpace(value))
            };

            for (int index = 0; index < selectors.Length; index++)
            {
                var (savedValue, selector, isUsable) = selectors[index];
                if (!isUsable(savedValue))
                {
                    continue;
                }

                var matches = detected
                    .Where(monitor => IdentityValueEquals(savedValue, selector(monitor)))
                    .ToList();
                if (matches.Count == 1)
                    return matches;
                if (matches.Count > 1)
                    return Array.Empty<DetectedMonitor>();
            }

            return Array.Empty<DetectedMonitor>();
        }

        private static bool TargetPathEquals(string? left, string? right)
            => NativeDisplayProfileCodec.NormaliseTargetPath(left).Equals(
                NativeDisplayProfileCodec.NormaliseTargetPath(right),
                StringComparison.OrdinalIgnoreCase);

        private sealed record TargetResolutionResult(
            bool Success,
            IReadOnlyDictionary<string, string> TargetPathByLayoutDevice,
            string ErrorMessage)
        {
            public static TargetResolutionResult Failed(string message)
                => new(false, new Dictionary<string, string>(), message);
        }

        public SavedLayoutAppliedCheck CheckSavedLayoutApplied(
            string path,
            IReadOnlyCollection<DetectedMonitor> detected,
            IReadOnlyCollection<SavedLayoutIdentity> identities)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return new SavedLayoutAppliedCheck(
                    SavedLayoutAppliedState.Inconclusive,
                    "The saved layout file was not found.");
            }

            if (detected == null || detected.Count == 0 ||
                identities == null || identities.Count == 0)
            {
                return new SavedLayoutAppliedCheck(
                    SavedLayoutAppliedState.Inconclusive,
                    "Reliable detected monitors and saved identities are required.");
            }

            try
            {
                var savedLayouts = ReadSavedLayoutSettings(path);
                if (savedLayouts.Count == 0)
                {
                    return new SavedLayoutAppliedCheck(
                        SavedLayoutAppliedState.Inconclusive,
                        "The saved layout does not contain an active display.");
                }

                var savedIdentityNames = identities
                    .Where(HasStrongIdentity)
                    .Select(GetIdentityLayoutDeviceName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .ToList();
                if (savedIdentityNames.Count != savedLayouts.Count ||
                    savedIdentityNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != savedIdentityNames.Count ||
                    !savedLayouts.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase)
                        .SetEquals(savedIdentityNames))
                {
                    return new SavedLayoutAppliedCheck(
                        SavedLayoutAppliedState.Inconclusive,
                        "The saved layout and strong identity map are not an exact one-to-one pair.");
                }

                var snapshot = QueryActiveTopology();
                if (!TryValidateDetectionAgainstTopology(
                        detected,
                        snapshot.Entries,
                        out var detectionTopologyError))
                {
                    return new SavedLayoutAppliedCheck(
                        SavedLayoutAppliedState.Inconclusive,
                        detectionTopologyError);
                }
                var resolutionIssues = new List<string>();
                var resolvedLayouts = ResolveSavedLayoutsForProfile(
                    path,
                    savedLayouts,
                    detected,
                    snapshot.Entries.Select(entry => entry.Name),
                    identities,
                    resolutionIssues);
                var coverageGaps = FindLayoutCoverageGaps(
                    savedLayouts.Keys,
                    resolvedLayouts.ToDictionary(
                        entry => entry.Key,
                        entry => entry.Value.DeviceName,
                        StringComparer.OrdinalIgnoreCase),
                    snapshot.Entries.Select(entry => entry.Name));

                if (resolutionIssues.Count > 0 ||
                    coverageGaps.ActiveWithoutSavedLayout.Count > 0 ||
                    coverageGaps.SavedWithoutActiveDisplay.Count > 0)
                {
                    var reasons = resolutionIssues
                        .Concat(coverageGaps.ActiveWithoutSavedLayout
                            .Select(name => $"Unmatched active display: {name}."))
                        .Concat(coverageGaps.SavedWithoutActiveDisplay
                            .Select(name => $"Saved display is not active: {name}."));
                    return new SavedLayoutAppliedCheck(
                        SavedLayoutAppliedState.Inconclusive,
                        string.Join(" ", reasons));
                }

                var intendedPrimary = snapshot.Entries
                    .Where(entry =>
                    {
                        var position = resolvedLayouts[entry.Name].Position;
                        return position.X == 0 && position.Y == 0;
                    })
                    .ToList();
                if (intendedPrimary.Count != 1)
                {
                    return new SavedLayoutAppliedCheck(
                        SavedLayoutAppliedState.Inconclusive,
                        "The saved extended-desktop layout must contain exactly one display at the origin.");
                }

                var positions = snapshot.Entries.ToDictionary(
                    entry => entry.SourceModeIndex,
                    entry => resolvedLayouts[entry.Name].Position);
                var sizes = snapshot.Entries.ToDictionary(
                    entry => entry.SourceModeIndex,
                    entry => resolvedLayouts[entry.Name].Size!.Value);
                var rotations = snapshot.Entries.ToDictionary(
                    entry => entry.SourceModeIndex,
                    entry => resolvedLayouts[entry.Name].Rotation!.Value);
                var orderedEntries = snapshot.Entries
                    .OrderBy(entry => ReferenceEquals(entry, intendedPrimary[0]) ? 0 : 1)
                    .ThenBy(entry => positions[entry.SourceModeIndex].X)
                    .ThenBy(entry => positions[entry.SourceModeIndex].Y)
                    .ToList();

                var actualOrigins = snapshot.Entries.Count(entry => entry.X == 0 && entry.Y == 0);
                if (actualOrigins != 1)
                {
                    return new SavedLayoutAppliedCheck(
                        SavedLayoutAppliedState.Inconclusive,
                        "The current topology is not an unambiguous extended desktop with one primary origin.");
                }

                if (TryVerifyAppliedTopology(
                        snapshot.Entries,
                        orderedEntries,
                        positions,
                        sizes,
                        rotations,
                        out var discrepancies))
                {
                    return new SavedLayoutAppliedCheck(
                        SavedLayoutAppliedState.Applied,
                        "The saved monitor positions, sizes, orientations, and primary display are applied.");
                }

                return new SavedLayoutAppliedCheck(
                    SavedLayoutAppliedState.NotApplied,
                    string.Join(" ", discrepancies));
            }
            catch (Exception ex)
            {
                return new SavedLayoutAppliedCheck(
                    SavedLayoutAppliedState.Inconclusive,
                    $"Unable to compare the saved and current display topology: {ex.Message}");
            }
        }

        private static bool TryValidateDetectionAgainstTopology(
            IReadOnlyCollection<DetectedMonitor>? detectedMonitors,
            IReadOnlyCollection<PathEntry> topologyEntries,
            out string errorMessage)
        {
            errorMessage = string.Empty;
            if (detectedMonitors == null)
                return true;

            var detected = detectedMonitors
                .Where(monitor => monitor.IsPresent &&
                                  monitor.IsActive &&
                                  !string.IsNullOrWhiteSpace(monitor.DeviceName) &&
                                  NativeDisplayProfileCodec.IsStrongTargetPath(monitor.NativeTargetPath))
                .Select(monitor => (monitor.DeviceName, monitor.NativeTargetPath!))
                .ToList();
            var topology = topologyEntries
                .Select(entry => (entry.Name, entry.MonitorDevicePath))
                .ToList();
            if (!HaveExactDetectionTopologyBindings(detected, topology))
            {
                errorMessage =
                    "Windows reassigned a display name or monitor target while the topology operation was being prepared; refresh and try again. No display change was made.";
                return false;
            }

            return true;
        }

        internal static bool HaveExactDetectionTopologyBindings(
            IEnumerable<(string DeviceName, string TargetPath)> detectedBindings,
            IEnumerable<(string DeviceName, string TargetPath)> topologyBindings)
        {
            var detected = detectedBindings?.ToList() ?? new List<(string, string)>();
            var topology = topologyBindings?.ToList() ?? new List<(string, string)>();
            if (detected.Count == 0 || detected.Count != topology.Count)
                return false;

            return topology.All(entry => detected.Count(candidate =>
                DeviceNameEquals(candidate.DeviceName, entry.DeviceName) &&
                TargetPathEquals(candidate.TargetPath, entry.TargetPath)) == 1);
        }

        public DisplayTopologyResult DisableDisplayUsingFallbackPrimary(
            string disableDeviceName,
            string fallbackDeviceName,
            string expectedDisableTargetPath,
            string expectedFallbackTargetPath)
        {
            if (string.IsNullOrWhiteSpace(disableDeviceName))
                return Failure("No display was supplied to disable.");
            if (string.IsNullOrWhiteSpace(fallbackDeviceName))
                return Failure("No fallback primary display was supplied.");
            if (!NativeDisplayProfileCodec.IsStrongTargetPath(expectedDisableTargetPath) ||
                !NativeDisplayProfileCodec.IsStrongTargetPath(expectedFallbackTargetPath))
            {
                return Failure("Unique current physical identities are required for both affected displays.");
            }

            try
            {
                var snapshot = QueryActiveTopology();
                var disable = snapshot.Entries.FirstOrDefault(e => DeviceNameEquals(e.Name, disableDeviceName));
                var fallback = snapshot.Entries.FirstOrDefault(e => DeviceNameEquals(e.Name, fallbackDeviceName));

                if (disable == null)
                    return Failure($"Display '{disableDeviceName}' is not active in the current topology.");
                if (fallback == null)
                    return Failure($"Fallback display '{fallbackDeviceName}' is not active in the current topology.");
                if (!TargetPathEquals(disable.MonitorDevicePath, expectedDisableTargetPath))
                {
                    return Failure(
                        $"Display '{disableDeviceName}' no longer belongs to the selected physical monitor.");
                }
                if (!TargetPathEquals(fallback.MonitorDevicePath, expectedFallbackTargetPath))
                {
                    return Failure(
                        $"Fallback display '{fallbackDeviceName}' no longer belongs to the expected physical monitor.");
                }
                if (ReferenceEquals(disable, fallback))
                    return Failure("The display being disabled cannot also be the fallback primary display.");

                var remainingEntries = snapshot.Entries
                    .Where(e => !ReferenceEquals(e, disable))
                    .ToList();
                if (remainingEntries.Count == 0)
                    return Failure("At least one display must remain active.");

                var layouts = remainingEntries.Select(e => e.ToLayout()).ToList();
                var fallbackLayout = layouts.First(l => l.SourceModeIndex == fallback.SourceModeIndex);
                var positions = CalculateRebasedPositions(layouts, fallbackLayout);
                var orderedEntries = remainingEntries
                    .OrderBy(e => ReferenceEquals(e, fallback) ? 0 : 1)
                    .ThenBy(e => positions[e.SourceModeIndex].X)
                    .ToList();

                return ValidateAndApply(
                    snapshot,
                    orderedEntries,
                    positions,
                    $"Disabled '{disable.Name}' with '{fallback.Name}' as primary.");
            }
            catch (Exception ex)
            {
                return Failure($"Unable to update display topology: {ex.Message}");
            }
        }

        public DisplayTopologyResult SetPrimaryDisplay(
            string primaryDeviceName,
            string expectedPrimaryTargetPath)
        {
            if (string.IsNullOrWhiteSpace(primaryDeviceName))
                return Failure("No primary display was supplied.");
            if (!NativeDisplayProfileCodec.IsStrongTargetPath(expectedPrimaryTargetPath))
                return Failure("A unique current physical identity is required for the primary display.");

            try
            {
                var snapshot = QueryActiveTopology();
                var primary = snapshot.Entries.FirstOrDefault(e => DeviceNameEquals(e.Name, primaryDeviceName));
                if (primary == null)
                    return Failure($"Display '{primaryDeviceName}' is not active in the current topology.");
                if (!TargetPathEquals(primary.MonitorDevicePath, expectedPrimaryTargetPath))
                {
                    return Failure(
                        $"Display '{primaryDeviceName}' no longer belongs to the expected physical monitor.");
                }
                if (primary.X == 0 && primary.Y == 0 &&
                    snapshot.Entries.Count(entry => entry.X == 0 && entry.Y == 0) == 1)
                {
                    return new DisplayTopologyResult
                    {
                        Success = true,
                        Message = $"Display '{primary.Name}' is already the verified primary display."
                    };
                }

                var positions = snapshot.Entries.ToDictionary(
                    e => e.SourceModeIndex,
                    e => new DisplayPosition(e.X - primary.X, e.Y - primary.Y));

                var orderedEntries = snapshot.Entries
                    .OrderBy(e => ReferenceEquals(e, primary) ? 0 : 1)
                    .ThenBy(e => positions[e.SourceModeIndex].X)
                    .ToList();

                return ValidateAndApply(
                    snapshot,
                    orderedEntries,
                    positions,
                    $"Set '{primary.Name}' as primary.");
            }
            catch (Exception ex)
            {
                return Failure($"Unable to update display topology: {ex.Message}");
            }
        }

        public DisplayTopologyResult ApplyLayoutPositionsFromConfig(
            string layoutPath,
            IReadOnlyCollection<DetectedMonitor>? detectedMonitors = null,
            IReadOnlyCollection<SavedLayoutIdentity>? savedIdentities = null)
        {
            if (string.IsNullOrWhiteSpace(layoutPath) || !File.Exists(layoutPath))
                return Failure("Saved layout file was not found.");

            try
            {
                var savedLayouts = ReadSavedLayoutSettings(layoutPath);
                if (savedLayouts.Count == 0)
                    return Failure("Saved layout file does not contain monitor positions.");

                var snapshot = QueryActiveTopology();
                if (!TryValidateDetectionAgainstTopology(
                        detectedMonitors,
                        snapshot.Entries,
                        out var detectionTopologyError))
                {
                    return Failure(detectionTopologyError);
                }
                var resolutionIssues = new List<string>();
                var resolvedLayouts = ResolveSavedLayoutsForProfile(
                    layoutPath,
                    savedLayouts,
                    detectedMonitors,
                    snapshot.Entries.Select(e => e.Name),
                    savedIdentities,
                    resolutionIssues);

                var coverageGaps = FindLayoutCoverageGaps(
                    savedLayouts.Keys,
                    resolvedLayouts.ToDictionary(
                        kv => kv.Key,
                        kv => kv.Value.DeviceName,
                        StringComparer.OrdinalIgnoreCase),
                    snapshot.Entries.Select(e => e.Name));

                if (resolutionIssues.Count > 0 ||
                    coverageGaps.ActiveWithoutSavedLayout.Count > 0 ||
                    coverageGaps.SavedWithoutActiveDisplay.Count > 0)
                {
                    var coverageDetails = resolutionIssues
                        .Concat(coverageGaps.ActiveWithoutSavedLayout
                        .Select(name => $"Unmatched active display: {name}.")
                        .Concat(coverageGaps.SavedWithoutActiveDisplay
                            .Select(name => $"Saved display is not active: {name}.")))
                        .ToList();

                    return new DisplayTopologyResult
                    {
                        Success = false,
                        Message = "The active display set does not match the saved layout.",
                        Details = coverageDetails
                    };
                }

                var positions = snapshot.Entries.ToDictionary(
                    e => e.SourceModeIndex,
                    e => resolvedLayouts[e.Name].Position);
                var sizes = snapshot.Entries
                    .Where(e => resolvedLayouts[e.Name].Size.HasValue)
                    .ToDictionary(
                        e => e.SourceModeIndex,
                        e => resolvedLayouts[e.Name].Size!.Value);
                var rotations = snapshot.Entries
                    .Where(e => resolvedLayouts[e.Name].Rotation.HasValue)
                    .ToDictionary(
                        e => e.SourceModeIndex,
                        e => resolvedLayouts[e.Name].Rotation!.Value);
                var layoutSourceDevices = snapshot.Entries.ToDictionary(
                    e => e.SourceModeIndex,
                    e => resolvedLayouts[e.Name].DeviceName);

                var primaryCandidates = snapshot.Entries
                    .Where(e => IsOrigin(resolvedLayouts[e.Name].Position))
                    .ToList();
                if (primaryCandidates.Count != 1)
                {
                    return Failure(
                        "The saved extended-desktop layout must contain exactly one primary display at position 0,0.");
                }

                var primary = primaryCandidates[0];

                var orderedEntries = snapshot.Entries
                    .OrderBy(e => ReferenceEquals(e, primary) ? 0 : 1)
                    .ThenBy(e => resolvedLayouts[e.Name].Position.X)
                    .ToList();

                return ValidateAndApply(
                    snapshot,
                    orderedEntries,
                    positions,
                    $"Applied saved layout positions from '{layoutPath}'.",
                    sizes,
                    rotations,
                    layoutSourceDevices);
            }
            catch (Exception ex)
            {
                return Failure($"Unable to apply saved layout positions: {ex.Message}");
            }
        }

        internal static IReadOnlyDictionary<int, DisplayPosition> CalculateRebasedPositions(
            IReadOnlyCollection<DisplaySourceLayout> remainingDisplays,
            DisplaySourceLayout fallbackPrimary)
        {
            var positions = new Dictionary<int, DisplayPosition>();
            foreach (var display in remainingDisplays)
            {
                positions[display.SourceModeIndex] =
                    new DisplayPosition(
                        display.X - fallbackPrimary.X,
                        display.Y - fallbackPrimary.Y);
            }

            return positions;
        }

        internal static bool HasExactlyOneOrigin(IEnumerable<DisplayPosition> positions)
            => positions != null && positions.Count(IsOrigin) == 1;

        private static bool IsOrigin(DisplayPosition position)
            => position.X == 0 && position.Y == 0;

        private static DisplayTopologyResult ValidateAndApply(
            TopologySnapshot snapshot,
            IReadOnlyList<PathEntry> orderedEntries,
            IReadOnlyDictionary<int, DisplayPosition> positions,
            string successMessage,
            IReadOnlyDictionary<int, DisplaySize>? sizes = null,
            IReadOnlyDictionary<int, uint>? rotations = null,
            IReadOnlyDictionary<int, string>? layoutSourceDevices = null)
        {
            var original = CloneTopologySnapshot(snapshot);
            foreach (var kv in positions)
            {
                var source = snapshot.Modes[kv.Key].modeInfo.sourceMode;
                source.position = new POINTL { x = kv.Value.X, y = kv.Value.Y };
                if (sizes != null && sizes.TryGetValue(kv.Key, out var size))
                {
                    source.width = checked((uint)size.Width);
                    source.height = checked((uint)size.Height);
                }
                snapshot.Modes[kv.Key].modeInfo.sourceMode = source;
            }

            var paths = orderedEntries.Select(e => e.Path).ToArray();
            if (rotations != null)
            {
                for (int i = 0; i < paths.Length; i++)
                {
                    var sourceModeIndex = orderedEntries[i].SourceModeIndex;
                    if (!rotations.TryGetValue(sourceModeIndex, out var rotation))
                        continue;

                    var target = paths[i].targetInfo;
                    target.rotation = rotation;
                    paths[i].targetInfo = target;
                }
            }

            var details = orderedEntries
                .Select(e =>
                {
                    var p = positions[e.SourceModeIndex];
                    var parts = new List<string> { $"{e.Name}: {e.X},{e.Y} -> {p.X},{p.Y}" };
                    if (layoutSourceDevices != null &&
                        layoutSourceDevices.TryGetValue(e.SourceModeIndex, out var savedDeviceName) &&
                        !DeviceNameEquals(e.Name, savedDeviceName))
                    {
                        parts.Add($"saved as {savedDeviceName}");
                    }
                    if (sizes != null && sizes.TryGetValue(e.SourceModeIndex, out var size))
                        parts.Add($"size {e.Width}x{e.Height} -> {size.Width}x{size.Height}");
                    if (rotations != null && rotations.TryGetValue(e.SourceModeIndex, out var rotation))
                        parts.Add($"rotation {e.Rotation} -> {rotation}");
                    return string.Join("; ", parts);
                })
                .ToList();

            bool topologyAlreadyApplied = IsTopologyAlreadyApplied(
                snapshot.Entries.Count,
                orderedEntries,
                positions,
                sizes,
                rotations);

            var validateCode = SetDisplayConfig(
                (uint)paths.Length,
                paths,
                snapshot.ModeCount,
                snapshot.Modes,
                SdcUseSuppliedDisplayConfig | SdcAllowChanges | SdcValidate);
            if (validateCode != ErrorSuccess)
            {
                return new DisplayTopologyResult
                {
                    Success = false,
                    ValidateCode = validateCode,
                    Message = $"Display topology validation failed ({validateCode}).",
                    Details = details
                };
            }

            var applyCode = SetDisplayConfig(
                (uint)paths.Length,
                paths,
                snapshot.ModeCount,
                snapshot.Modes,
                GetPersistentApplyFlags());

            if (applyCode == ErrorSuccess)
            {
                IReadOnlyList<string> verificationDetails = Array.Empty<string>();
                bool verified = false;

                for (int attempt = 0; attempt < PostApplyVerificationAttempts; attempt++)
                {
                    try
                    {
                        var appliedSnapshot = QueryActiveTopology();
                        verified = TryVerifyAppliedTopology(
                            appliedSnapshot.Entries,
                            orderedEntries,
                            positions,
                            sizes,
                            rotations,
                            out verificationDetails);
                    }
                    catch (Exception ex)
                    {
                        verificationDetails = new[] { $"Unable to query the applied topology: {ex.Message}" };
                    }

                    if (verified)
                        break;

                    if (attempt + 1 < PostApplyVerificationAttempts)
                        System.Threading.Thread.Sleep(100);
                }

                if (!verified)
                {
                    details.AddRange(verificationDetails.Select(detail => $"Post-apply verification: {detail}"));
                    return WithRollbackDetails(new DisplayTopologyResult
                    {
                        Success = false,
                        ValidateCode = validateCode,
                        ApplyCode = applyCode,
                        Message = "Display topology apply returned success, but the resulting topology did not match the request.",
                        Details = details
                    }, original);
                }
            }

            return new DisplayTopologyResult
            {
                Success = applyCode == ErrorSuccess,
                ValidateCode = validateCode,
                ApplyCode = applyCode,
                Message = applyCode == ErrorSuccess
                    ? topologyAlreadyApplied
                        ? $"{successMessage} No geometry change was required; the topology was persisted."
                        : successMessage
                    : $"Display topology apply failed ({applyCode}).",
                Details = details
            };
        }

        private static TopologySnapshot CloneTopologySnapshot(TopologySnapshot snapshot)
            => new(
                (DISPLAYCONFIG_PATH_INFO[])snapshot.Paths.Clone(),
                (DISPLAYCONFIG_MODE_INFO[])snapshot.Modes.Clone(),
                snapshot.PathCount,
                snapshot.ModeCount,
                snapshot.Entries.ToList());

        private static bool IsTopologyAlreadyApplied(
            int activePathCount,
            IReadOnlyList<PathEntry> orderedEntries,
            IReadOnlyDictionary<int, DisplayPosition> positions,
            IReadOnlyDictionary<int, DisplaySize>? sizes,
            IReadOnlyDictionary<int, uint>? rotations)
        {
            if (orderedEntries.Count != activePathCount)
                return false;

            if (orderedEntries.Count == 0)
                return true;

            foreach (var entry in orderedEntries)
            {
                if (!positions.TryGetValue(entry.SourceModeIndex, out var position))
                    return false;

                var isPrimary = ReferenceEquals(entry, orderedEntries[0]);
                if (!DisplayPositionMatches(entry, position, requireExact: isPrimary))
                    return false;

                uint? rotationOverride = rotations != null &&
                                         rotations.TryGetValue(entry.SourceModeIndex, out var rotation)
                    ? rotation
                    : null;
                var intendedRotation = ResolveVerificationRotation(entry.Rotation, rotationOverride);

                if (entry.Rotation != intendedRotation)
                {
                    return false;
                }

                DisplaySize? sizeOverride = sizes != null &&
                                            sizes.TryGetValue(entry.SourceModeIndex, out var resolvedSize)
                    ? resolvedSize
                    : null;
                var intendedSize = ResolveVerificationSize(
                    new DisplaySize(entry.Width, entry.Height),
                    sizeOverride);
                if (!DisplaySizeMatches(
                        entry,
                        intendedSize,
                        intendedRotation,
                        allowQuarterTurnEquivalent: sizeOverride.HasValue))
                {
                    return false;
                }
            }

            var primary = orderedEntries[0];
            return primary.X == 0 && primary.Y == 0;
        }

        private static bool TryVerifyAppliedTopology(
            IReadOnlyCollection<PathEntry> actualEntries,
            IReadOnlyList<PathEntry> intendedEntries,
            IReadOnlyDictionary<int, DisplayPosition> intendedPositions,
            IReadOnlyDictionary<int, DisplaySize>? intendedSizes,
            IReadOnlyDictionary<int, uint>? intendedRotations,
            out IReadOnlyList<string> discrepancies)
        {
            var issues = new List<string>();

            var actualPrimaryEntries = actualEntries
                .Where(entry => entry.X == 0 && entry.Y == 0)
                .ToList();
            var intendedPrimary = intendedEntries.Count > 0 ? intendedEntries[0] : null;
            if (intendedPrimary != null &&
                (actualPrimaryEntries.Count != 1 ||
                 !TargetPathEquals(
                     actualPrimaryEntries[0].MonitorDevicePath,
                     intendedPrimary.MonitorDevicePath)))
            {
                var actualPrimaryDescription = actualPrimaryEntries.Count == 0
                    ? "none"
                    : string.Join(", ", actualPrimaryEntries.Select(entry => entry.Name));
                issues.Add(
                    $"Primary display is {actualPrimaryDescription}; expected '{intendedPrimary.Name}'.");
            }

            if (actualEntries.Count != intendedEntries.Count)
            {
                issues.Add($"Active display count is {actualEntries.Count}; expected {intendedEntries.Count}.");
            }

            for (int i = 0; i < intendedEntries.Count; i++)
            {
                var intended = intendedEntries[i];
                var matches = actualEntries
                    .Where(actual => TargetPathEquals(
                        actual.MonitorDevicePath,
                        intended.MonitorDevicePath))
                    .ToList();

                if (matches.Count == 0)
                {
                    issues.Add($"Expected display '{intended.Name}' is not active.");
                    continue;
                }

                if (matches.Count > 1)
                {
                    issues.Add($"Expected display '{intended.Name}' matched {matches.Count} active paths.");
                    continue;
                }

                var actual = matches[0];
                if (!intendedPositions.TryGetValue(intended.SourceModeIndex, out var intendedPosition))
                {
                    issues.Add($"No intended position was supplied for '{intended.Name}'.");
                    continue;
                }

                if (!DisplayPositionMatches(actual, intendedPosition, requireExact: i == 0))
                {
                    issues.Add(
                        $"Display '{intended.Name}' position is {actual.X},{actual.Y}; " +
                        $"expected {intendedPosition.X},{intendedPosition.Y}.");
                }

                uint? rotationOverride = intendedRotations != null &&
                                         intendedRotations.TryGetValue(intended.SourceModeIndex, out var rotation)
                    ? rotation
                    : null;
                var intendedRotation = ResolveVerificationRotation(
                    intended.Rotation,
                    rotationOverride);

                if (actual.Rotation != intendedRotation)
                {
                    issues.Add(
                        $"Display '{intended.Name}' rotation is {actual.Rotation}; expected {intendedRotation}.");
                }

                DisplaySize? sizeOverride = intendedSizes != null &&
                                            intendedSizes.TryGetValue(intended.SourceModeIndex, out var resolvedSize)
                    ? resolvedSize
                    : null;
                var intendedSize = ResolveVerificationSize(
                    new DisplaySize(intended.Width, intended.Height),
                    sizeOverride);
                if (!DisplaySizeMatches(
                        actual,
                        intendedSize,
                        intendedRotation,
                        allowQuarterTurnEquivalent: sizeOverride.HasValue))
                {
                    issues.Add(
                        $"Display '{intended.Name}' size is {actual.Width}x{actual.Height}; " +
                        $"expected {intendedSize.Width}x{intendedSize.Height}.");
                }
            }

            foreach (var actual in actualEntries.Where(actual =>
                         !intendedEntries.Any(intended => TargetPathEquals(
                             actual.MonitorDevicePath,
                             intended.MonitorDevicePath))))
            {
                issues.Add($"Unexpected display '{actual.Name}' remains active.");
            }

            discrepancies = issues;
            return issues.Count == 0;
        }

        private static bool DisplayPositionMatches(
            PathEntry actual,
            DisplayPosition intended,
            bool requireExact)
        {
            var tolerance = requireExact ? 0 : PositionNormalisationTolerancePixels;
            return Math.Abs((long)actual.X - intended.X) <= tolerance &&
                   Math.Abs((long)actual.Y - intended.Y) <= tolerance;
        }

        internal static DisplaySize ResolveVerificationSize(
            DisplaySize capturedSize,
            DisplaySize? overrideSize)
            => overrideSize ?? capturedSize;

        internal static uint ResolveVerificationRotation(
            uint capturedRotation,
            uint? overrideRotation)
            => overrideRotation ?? capturedRotation;

        internal static bool EffectiveDisplaySizeMatches(
            DisplaySize actualSize,
            DisplaySize intendedSize,
            uint rotation,
            bool allowQuarterTurnEquivalent)
        {
            if (actualSize == intendedSize)
                return true;

            return allowQuarterTurnEquivalent &&
                   IsQuarterTurn(rotation) &&
                   actualSize.Width == intendedSize.Height &&
                   actualSize.Height == intendedSize.Width;
        }

        private static bool DisplaySizeMatches(
            PathEntry entry,
            DisplaySize size,
            uint rotation,
            bool allowQuarterTurnEquivalent)
            => EffectiveDisplaySizeMatches(
                new DisplaySize(entry.Width, entry.Height),
                size,
                rotation,
                allowQuarterTurnEquivalent);

        private static bool IsQuarterTurn(uint rotation)
            => rotation == 2 || rotation == 4;

        private static TopologySnapshot QueryActiveTopology()
            => QueryTopology(QdcOnlyActivePaths, includeInactivePaths: false);

        private static TopologySnapshot QueryAllTopologyPaths()
            => QueryTopology(QdcAllPaths, includeInactivePaths: true);

        private static TopologySnapshot QueryTopology(uint queryFlags, bool includeInactivePaths)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var sizeCode = GetDisplayConfigBufferSizes(
                    queryFlags,
                    out var pathCount,
                    out var modeCount);
                if (sizeCode != ErrorSuccess)
                    throw new InvalidOperationException($"GetDisplayConfigBufferSizes failed ({sizeCode}).");

                var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
                var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];

                var queryCode = QueryDisplayConfig(
                    queryFlags,
                    ref pathCount,
                    paths,
                    ref modeCount,
                    modes,
                    IntPtr.Zero);

                if (queryCode == ErrorInsufficientBuffer)
                    continue;
                if (queryCode != ErrorSuccess)
                    throw new InvalidOperationException($"QueryDisplayConfig failed ({queryCode}).");

                var entries = new List<PathEntry>();
                for (int i = 0; i < pathCount; i++)
                {
                    bool isActive = (paths[i].flags & DisplayConfigPathActive) != 0;
                    var sourceIndex = FindSourceModeIndex(paths[i], modes, modeCount);
                    if (isActive && sourceIndex < 0)
                        throw new InvalidOperationException($"Source mode was not found for path {i}.");

                    var targetName = GetTargetDeviceName(paths[i].targetInfo);
                    var sourceName = isActive ? GetSourceDeviceName(paths[i].sourceInfo) : string.Empty;
                    var source = sourceIndex >= 0
                        ? modes[sourceIndex].modeInfo.sourceMode
                        : default;
                    if (!isActive && !includeInactivePaths)
                        continue;
                    entries.Add(new PathEntry
                    {
                        PathIndex = i,
                        Path = paths[i],
                        Name = sourceName,
                        MonitorDevicePath = targetName.monitorDevicePath ?? string.Empty,
                        TargetFriendlyName = targetName.monitorFriendlyDeviceName ?? string.Empty,
                        EdidIdsValid = (targetName.flags & 0x00000004) != 0,
                        EdidManufactureId = targetName.edidManufactureId,
                        EdidProductCodeId = targetName.edidProductCodeId,
                        ConnectorInstance = targetName.connectorInstance,
                        IsActive = isActive,
                        IsAvailable = paths[i].targetInfo.targetAvailable != 0,
                        SourceModeIndex = sourceIndex,
                        X = source.position.x,
                        Y = source.position.y,
                        Width = checked((int)source.width),
                        Height = checked((int)source.height),
                        Rotation = paths[i].targetInfo.rotation
                    });
                }

                return new TopologySnapshot(paths, modes, pathCount, modeCount, entries);
            }

            throw new InvalidOperationException("Display topology changed while it was being queried.");
        }

        private static int FindSourceModeIndex(
            DISPLAYCONFIG_PATH_INFO path,
            DISPLAYCONFIG_MODE_INFO[] modes,
            uint modeCount)
        {
            if (path.sourceInfo.modeInfoIdx < modeCount &&
                modes[path.sourceInfo.modeInfoIdx].infoType == DisplayConfigModeInfoTypeSource)
            {
                return checked((int)path.sourceInfo.modeInfoIdx);
            }

            for (int i = 0; i < modeCount; i++)
            {
                if (modes[i].infoType == DisplayConfigModeInfoTypeSource &&
                    SameLuid(modes[i].adapterId, path.sourceInfo.adapterId) &&
                    modes[i].id == path.sourceInfo.id)
                {
                    return i;
                }
            }

            return -1;
        }

        internal static bool IsExactSavedMonitorSetActive(
            string layoutPath,
            IReadOnlyCollection<DetectedMonitor>? detectedMonitors,
            IReadOnlyCollection<SavedLayoutIdentity>? savedIdentities)
        {
            if (string.IsNullOrWhiteSpace(layoutPath) ||
                !File.Exists(layoutPath) ||
                detectedMonitors == null)
            {
                return false;
            }

            try
            {
                var savedLayouts = ReadSavedLayoutSettings(layoutPath);
                if (savedLayouts.Count == 0)
                    return false;

                var activeDeviceNames = detectedMonitors
                    .Where(monitor => monitor.IsPresent &&
                                      monitor.IsActive &&
                                      !string.IsNullOrWhiteSpace(monitor.DeviceName))
                    .Select(monitor => monitor.DeviceName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (activeDeviceNames.Count == 0)
                    return false;

                var resolutionIssues = new List<string>();
                var resolvedLayouts = ResolveSavedLayoutsForProfile(
                    layoutPath,
                    savedLayouts,
                    detectedMonitors,
                    activeDeviceNames,
                    savedIdentities,
                    resolutionIssues);
                if (resolutionIssues.Count > 0)
                    return false;

                var coverageGaps = FindLayoutCoverageGaps(
                    savedLayouts.Keys,
                    resolvedLayouts.ToDictionary(
                        entry => entry.Key,
                        entry => entry.Value.DeviceName,
                        StringComparer.OrdinalIgnoreCase),
                    activeDeviceNames);

                return coverageGaps.ActiveWithoutSavedLayout.Count == 0 &&
                       coverageGaps.SavedWithoutActiveDisplay.Count == 0;
            }
            catch
            {
                return false;
            }
        }

        internal static int GetSavedActiveMonitorCount(string layoutPath)
        {
            if (string.IsNullOrWhiteSpace(layoutPath) || !File.Exists(layoutPath))
                return 0;

            try
            {
                return ReadSavedLayoutSettings(layoutPath).Count;
            }
            catch
            {
                return 0;
            }
        }

        internal static IReadOnlyDictionary<string, string> ResolveSavedLayoutDeviceNameMap(
            string layoutPath,
            IReadOnlyCollection<DetectedMonitor> detectedMonitors,
            IEnumerable<string> currentDeviceNames,
            IReadOnlyCollection<SavedLayoutIdentity>? savedIdentities = null)
        {
            var savedLayouts = ReadSavedLayoutSettings(layoutPath);
            return ResolveSavedLayoutsForProfile(
                    layoutPath,
                    savedLayouts,
                    detectedMonitors,
                    currentDeviceNames,
                    savedIdentities)
                .ToDictionary(kv => kv.Key, kv => kv.Value.DeviceName, StringComparer.OrdinalIgnoreCase);
        }

        private static Dictionary<string, SavedDisplayLayout> ResolveSavedLayoutsForProfile(
            string layoutPath,
            IReadOnlyDictionary<string, SavedDisplayLayout> savedLayouts,
            IReadOnlyCollection<DetectedMonitor>? detectedMonitors,
            IEnumerable<string> currentDeviceNames,
            IReadOnlyCollection<SavedLayoutIdentity>? savedIdentities = null,
            ICollection<string>? resolutionIssues = null)
        {
            bool declaresNativeProfile = File.ReadLines(layoutPath).Any(line =>
            {
                int separatorIndex = line.IndexOf('=');
                return separatorIndex > 0 &&
                       line[..separatorIndex].Trim().Equals(
                           "NativeProfileVersion",
                           StringComparison.OrdinalIgnoreCase);
            });
            if (NativeDisplayProfileCodec.TryRead(layoutPath, out var profile, out var nativeError) && profile.Version > 0)
            {
                return ResolveNativeSavedLayoutsForCurrentDevices(
                    profile,
                    savedLayouts,
                    detectedMonitors,
                    currentDeviceNames,
                    savedIdentities,
                    resolutionIssues);
            }

            if (declaresNativeProfile)
            {
                resolutionIssues?.Add(
                    $"The file declares a native profile but is invalid or unsupported: {nativeError}");
                return new Dictionary<string, SavedDisplayLayout>(StringComparer.OrdinalIgnoreCase);
            }

            return ResolveSavedLayoutsForCurrentDevices(
                savedLayouts,
                detectedMonitors,
                currentDeviceNames,
                savedIdentities,
                resolutionIssues);
        }

        private static Dictionary<string, SavedDisplayLayout> ResolveNativeSavedLayoutsForCurrentDevices(
            NativeDisplayProfile profile,
            IReadOnlyDictionary<string, SavedDisplayLayout> savedLayouts,
            IReadOnlyCollection<DetectedMonitor>? detectedMonitors,
            IEnumerable<string> currentDeviceNames,
            IReadOnlyCollection<SavedLayoutIdentity>? savedIdentities,
            ICollection<string>? resolutionIssues)
        {
            var resolved = new Dictionary<string, SavedDisplayLayout>(StringComparer.OrdinalIgnoreCase);
            if (detectedMonitors == null || savedIdentities == null ||
                !HasExactNativeIdentityPair(profile, savedIdentities))
            {
                resolutionIssues?.Add(
                    "The native layout has no complete matching version-2 physical identity map. Save the profile again before restoring it.");
                return resolved;
            }

            var currentDevices = currentDeviceNames
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var activeDetected = detectedMonitors
                .Where(monitor => monitor.IsPresent &&
                                  monitor.IsActive &&
                                  !string.IsNullOrWhiteSpace(monitor.DeviceName) &&
                                  NativeDisplayProfileCodec.IsStrongTargetPath(monitor.NativeTargetPath))
                .ToList();
            var usedCurrentDevices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var monitor in profile.Monitors)
            {
                var identities = savedIdentities
                    .Where(identity => DeviceNameEquals(
                        GetIdentityLayoutDeviceName(identity),
                        monitor.LayoutDeviceName))
                    .ToList();
                if (identities.Count != 1 || !savedLayouts.TryGetValue(monitor.LayoutDeviceName, out var layout))
                {
                    resolutionIssues?.Add(
                        $"Saved display '{monitor.LayoutDeviceName}' has no unique matching layout and physical identity.");
                    continue;
                }

                var matches = ResolveNativeIdentityToDetected(
                    identities[0],
                    activeDetected);
                if (matches.Count != 1 ||
                    !currentDevices.Contains(matches[0].DeviceName) ||
                    !usedCurrentDevices.Add(matches[0].DeviceName))
                {
                    resolutionIssues?.Add(
                        $"Saved display '{monitor.LayoutDeviceName}' could not be mapped uniquely to one active physical monitor.");
                    continue;
                }

                resolved[matches[0].DeviceName] = layout;
            }

            return resolved;
        }

        private static IReadOnlyList<DetectedMonitor> ResolveNativeIdentityToDetected(
            SavedLayoutIdentity identity,
            IReadOnlyCollection<DetectedMonitor> detectedMonitors)
        {
            return detectedMonitors
                .Where(detected => HasConsistentNativeIdentityMatch(identity, detected))
                .ToList();
        }

        private static bool HasConsistentNativeIdentityMatch(
            SavedLayoutIdentity saved,
            DetectedMonitor detected)
        {
            bool hasStrongMatch = false;

            if (NativeDisplayProfileCodec.IsStrongTargetPath(saved.NativeTargetPath) &&
                NativeDisplayProfileCodec.IsStrongTargetPath(detected.NativeTargetPath))
            {
                if (!TargetPathEquals(saved.NativeTargetPath, detected.NativeTargetPath))
                    return false;

                hasStrongMatch = true;
            }

            if (!string.IsNullOrWhiteSpace(saved.InstanceId) &&
                !string.IsNullOrWhiteSpace(detected.InstanceId))
            {
                if (!IdentityValueEquals(saved.InstanceId, detected.InstanceId))
                    return false;

                hasStrongMatch = true;
            }

            if (!string.IsNullOrWhiteSpace(saved.MonitorKey) &&
                !string.IsNullOrWhiteSpace(detected.MonitorKey))
            {
                if (!IdentityValueEquals(saved.MonitorKey, detected.MonitorKey))
                    return false;

                hasStrongMatch = true;
            }

            // Serial numbers can be duplicated or reported inconsistently by otherwise
            // identical displays. They may reject a contradictory candidate, but never
            // establish physical identity on their own.
            if (DetectionService.IsCredibleSerial(saved.SerialNumber) &&
                DetectionService.IsCredibleSerial(detected.SerialNumber) &&
                !IdentityValueEquals(saved.SerialNumber, detected.SerialNumber))
            {
                return false;
            }

            return hasStrongMatch;
        }

        internal static (
            IReadOnlyList<string> ActiveWithoutSavedLayout,
            IReadOnlyList<string> SavedWithoutActiveDisplay) FindLayoutCoverageGaps(
                IEnumerable<string> savedDeviceNames,
                IReadOnlyDictionary<string, string> resolvedSavedDeviceByActiveDevice,
                IEnumerable<string> activeDeviceNames)
        {
            var active = activeDeviceNames
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var saved = savedDeviceNames
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var matchedSaved = resolvedSavedDeviceByActiveDevice.Values
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var matchedActive = resolvedSavedDeviceByActiveDevice.Keys
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var activeWithoutSavedLayout = active
                .Where(name => !matchedActive.Contains(name))
                .ToList();
            var savedWithoutActiveDisplay = saved
                .Where(name => !matchedSaved.Contains(name))
                .ToList();

            return (activeWithoutSavedLayout, savedWithoutActiveDisplay);
        }

        private static Dictionary<string, SavedDisplayLayout> ResolveSavedLayoutsForCurrentDevices(
            IReadOnlyDictionary<string, SavedDisplayLayout> savedLayouts,
            IReadOnlyCollection<DetectedMonitor>? detectedMonitors,
            IEnumerable<string> currentDeviceNames,
            IReadOnlyCollection<SavedLayoutIdentity>? savedIdentities = null,
            ICollection<string>? resolutionIssues = null)
        {
            var resolved = new Dictionary<string, SavedDisplayLayout>(StringComparer.OrdinalIgnoreCase);
            var usedSavedDeviceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var detectedByDevice = BuildDetectedByDeviceName(detectedMonitors);
            var activeDetected = detectedByDevice.Values.ToList();
            var currentDevices = currentDeviceNames
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Resolve every credible hardware identity before allowing device-name
            // fallback, so a weak unchanged entry cannot consume another monitor's
            // strongly identified saved layout.
            foreach (var currentDeviceName in currentDevices)
            {
                SavedDisplayLayout? layout = null;
                var currentDeviceKey = MonitorTargetResolver.NormalizeDeviceNameForComparison(currentDeviceName);

                if (detectedByDevice.TryGetValue(currentDeviceKey, out var detected))
                {
                    layout = FindUniqueUnusedSavedLayout(
                        savedLayouts,
                        savedIdentities,
                        usedSavedDeviceNames,
                        detected,
                        activeDetected);

                    if (layout == null &&
                        DetectionService.IsCredibleSerial(detected.SerialNumber) &&
                        IsUniqueDetectedIdentity(activeDetected, detected.SerialNumber, d => d.SerialNumber) &&
                        savedLayouts.Values.Count(saved =>
                            IdentityValueEquals(saved.SerialNumber, detected.SerialNumber)) == 1)
                    {
                        layout = FindUniqueUnusedSavedLayout(
                            savedLayouts.Values,
                            usedSavedDeviceNames,
                            saved => IdentityValueEquals(saved.SerialNumber, detected.SerialNumber));
                    }

                }

                if (layout == null)
                    continue;

                resolved[currentDeviceName] = layout;
                usedSavedDeviceNames.Add(layout.DeviceName);
            }

            foreach (var currentDeviceName in currentDevices.Where(name => !resolved.ContainsKey(name)))
            {
                var currentDeviceKey = MonitorTargetResolver.NormalizeDeviceNameForComparison(currentDeviceName);
                detectedByDevice.TryGetValue(currentDeviceKey, out var detected);

                var strongCandidates = detected == null
                    ? Array.Empty<string>()
                    : FindStrongIdentityCandidateDeviceNames(
                        savedLayouts,
                        savedIdentities,
                        usedSavedDeviceNames,
                        detected);

                if (strongCandidates.Count > 0)
                {
                    resolutionIssues?.Add(
                        $"Active display '{currentDeviceName}' has ambiguous or conflicting saved identity match(es): " +
                        $"{string.Join(", ", strongCandidates)}.");
                    continue;
                }

                if (!savedLayouts.TryGetValue(currentDeviceName, out var savedByDeviceName) ||
                    usedSavedDeviceNames.Contains(savedByDeviceName.DeviceName))
                {
                    continue;
                }

                if (HasStrongIdentityForSavedLayout(savedIdentities, savedByDeviceName.DeviceName))
                {
                    resolutionIssues?.Add(
                        $"Active display '{currentDeviceName}' did not match the strong identity saved for that device name.");
                    continue;
                }

                resolved[currentDeviceName] = savedByDeviceName;
                usedSavedDeviceNames.Add(savedByDeviceName.DeviceName);
            }

            return resolved;
        }

        private static Dictionary<string, DetectedMonitor> BuildDetectedByDeviceName(
            IReadOnlyCollection<DetectedMonitor>? detectedMonitors)
        {
            if (detectedMonitors == null || detectedMonitors.Count == 0)
                return new Dictionary<string, DetectedMonitor>(StringComparer.OrdinalIgnoreCase);

            return detectedMonitors
                .Where(d => d.IsPresent &&
                            d.IsActive &&
                            !string.IsNullOrWhiteSpace(d.DeviceName))
                .Select(d => new
                {
                    DeviceName = MonitorTargetResolver.NormalizeDeviceNameForComparison(d.DeviceName),
                    Monitor = d
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.DeviceName))
                .GroupBy(x => x.DeviceName, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() == 1)
                .ToDictionary(
                    g => g.Key,
                    g => g.First().Monitor,
                    StringComparer.OrdinalIgnoreCase);
        }

        private static SavedDisplayLayout? FindUniqueUnusedSavedLayout(
            IReadOnlyDictionary<string, SavedDisplayLayout> savedLayouts,
            IReadOnlyCollection<SavedLayoutIdentity>? savedIdentities,
            ISet<string> usedSavedDeviceNames,
            DetectedMonitor detected,
            IReadOnlyCollection<DetectedMonitor> activeDetected)
        {
            if (savedIdentities == null || savedIdentities.Count == 0)
                return null;

            SavedLayoutIdentity? identity = null;
            if (IsStrongStableKey(detected.StableKey) &&
                IsUniqueDetectedIdentity(activeDetected, detected.StableKey, d => d.StableKey))
            {
                identity = FindUniqueUnusedSavedIdentity(
                    savedLayouts,
                    savedIdentities,
                    usedSavedDeviceNames,
                    saved => IdentityValueEquals(saved.StableKey, detected.StableKey));
            }

            if (identity == null &&
                DetectionService.IsCredibleSerial(detected.SerialNumber) &&
                IsUniqueDetectedIdentity(activeDetected, detected.SerialNumber, d => d.SerialNumber))
            {
                identity = FindUniqueUnusedSavedIdentity(
                    savedLayouts,
                    savedIdentities,
                    usedSavedDeviceNames,
                    saved => IdentityValueEquals(saved.SerialNumber, detected.SerialNumber));
            }

            if (identity == null &&
                IsUniqueDetectedIdentity(activeDetected, detected.InstanceId, d => d.InstanceId))
            {
                identity = FindUniqueUnusedSavedIdentity(
                    savedLayouts,
                    savedIdentities,
                    usedSavedDeviceNames,
                    saved => IdentityValueEquals(saved.InstanceId, detected.InstanceId));
            }

            if (identity == null &&
                IsUniqueDetectedIdentity(activeDetected, detected.MonitorKey, d => d.MonitorKey))
            {
                identity = FindUniqueUnusedSavedIdentity(
                    savedLayouts,
                    savedIdentities,
                    usedSavedDeviceNames,
                    saved => IdentityValueEquals(saved.MonitorKey, detected.MonitorKey));
            }

            if (identity == null)
                return null;

            var layoutDeviceName = GetIdentityLayoutDeviceName(identity);
            return savedLayouts.TryGetValue(layoutDeviceName, out var layout)
                ? layout
                : null;
        }

        private static SavedLayoutIdentity? FindUniqueUnusedSavedIdentity(
            IReadOnlyDictionary<string, SavedDisplayLayout> savedLayouts,
            IEnumerable<SavedLayoutIdentity> savedIdentities,
            ISet<string> usedSavedDeviceNames,
            Func<SavedLayoutIdentity, bool> predicate)
        {
            var matches = savedIdentities
                .Where(saved =>
                {
                    var layoutDeviceName = GetIdentityLayoutDeviceName(saved);
                    return !string.IsNullOrWhiteSpace(layoutDeviceName) &&
                           savedLayouts.ContainsKey(layoutDeviceName) &&
                           !usedSavedDeviceNames.Contains(layoutDeviceName) &&
                           predicate(saved);
                })
                .GroupBy(GetIdentityLayoutDeviceName, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            return matches.Count == 1 ? matches[0] : null;
        }

        private static bool IsUniqueDetectedIdentity(
            IEnumerable<DetectedMonitor> detectedMonitors,
            string? identityValue,
            Func<DetectedMonitor, string?> selector)
        {
            var value = NormalizeIdentityValue(identityValue);
            return value.Length > 0 &&
                   detectedMonitors.Count(detected => IdentityValueEquals(selector(detected), value)) == 1;
        }

        private static IReadOnlyList<string> FindStrongIdentityCandidateDeviceNames(
            IReadOnlyDictionary<string, SavedDisplayLayout> savedLayouts,
            IReadOnlyCollection<SavedLayoutIdentity>? savedIdentities,
            ISet<string> usedSavedDeviceNames,
            DetectedMonitor detected)
        {
            if (savedIdentities == null || savedIdentities.Count == 0)
                return Array.Empty<string>();

            return savedIdentities
                .Where(HasStrongIdentity)
                .Where(saved =>
                    IdentityValueEquals(saved.StableKey, detected.StableKey) ||
                    IdentityValueEquals(saved.SerialNumber, detected.SerialNumber) ||
                    IdentityValueEquals(saved.InstanceId, detected.InstanceId) ||
                    IdentityValueEquals(saved.MonitorKey, detected.MonitorKey) ||
                    TargetPathEquals(saved.NativeTargetPath, detected.NativeTargetPath))
                .Select(GetIdentityLayoutDeviceName)
                .Where(deviceName =>
                    savedLayouts.ContainsKey(deviceName) &&
                    !usedSavedDeviceNames.Contains(deviceName))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool HasStrongIdentityForSavedLayout(
            IReadOnlyCollection<SavedLayoutIdentity>? savedIdentities,
            string savedDeviceName)
        {
            return savedIdentities != null &&
                   savedIdentities.Any(saved =>
                       DeviceNameEquals(GetIdentityLayoutDeviceName(saved), savedDeviceName) &&
                       HasStrongIdentity(saved));
        }

        private static bool HasStrongIdentity(SavedLayoutIdentity identity)
        {
            if (DetectionService.IsCredibleSerial(identity.SerialNumber))
                return true;
            if (NormalizeIdentityValue(identity.InstanceId).Length > 0)
                return true;
            if (NormalizeIdentityValue(identity.MonitorKey).Length > 0)
                return true;
            if (NativeDisplayProfileCodec.IsStrongTargetPath(identity.NativeTargetPath))
                return true;

            var stableKey = NormalizeIdentityValue(identity.StableKey);
            return IsStrongStableKey(stableKey);
        }

        private static bool IsStrongStableKey(string? stableKey)
        {
            var value = NormalizeIdentityValue(stableKey);
            return value.StartsWith("SN:", StringComparison.OrdinalIgnoreCase) ||
                   value.StartsWith("IID:", StringComparison.OrdinalIgnoreCase) ||
                   value.StartsWith("MK:", StringComparison.OrdinalIgnoreCase) ||
                   value.StartsWith("NTP:", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetIdentityLayoutDeviceName(SavedLayoutIdentity identity)
            => FirstNonBlank(identity.LayoutDeviceName, identity.DeviceName);

        private static SavedDisplayLayout? FindUniqueUnusedSavedLayout(
            IEnumerable<SavedDisplayLayout> savedLayouts,
            ISet<string> usedSavedDeviceNames,
            Func<SavedDisplayLayout, bool> predicate)
        {
            var matches = savedLayouts
                .Where(saved => !usedSavedDeviceNames.Contains(saved.DeviceName))
                .Where(predicate)
                .ToList();

            return matches.Count == 1 ? matches[0] : null;
        }

        private static string FirstNonBlank(params string?[] values)
            => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? string.Empty;

        private static bool IdentityValueEquals(string? left, string? right)
        {
            var leftValue = NormalizeIdentityValue(left);
            var rightValue = NormalizeIdentityValue(right);

            return leftValue.Length > 0 &&
                   rightValue.Length > 0 &&
                   leftValue.Equals(rightValue, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeIdentityValue(string? value)
        {
            var normalized = (value ?? string.Empty).Trim().Replace('/', '\\');
            while (normalized.Contains("\\\\", StringComparison.Ordinal))
                normalized = normalized.Replace("\\\\", "\\");
            return normalized;
        }

        private static string GetSourceDeviceName(DISPLAYCONFIG_PATH_SOURCE_INFO source)
        {
            var request = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
            {
                header =
                {
                    type = DisplayConfigDeviceInfoGetSourceName,
                    size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                    adapterId = source.adapterId,
                    id = source.id
                }
            };

            var code = DisplayConfigGetDeviceInfo(ref request);
            if (code != ErrorSuccess)
                throw new InvalidOperationException($"DisplayConfigGetDeviceInfo failed ({code}).");

            return request.viewGdiDeviceName ?? string.Empty;
        }

        private static DISPLAYCONFIG_TARGET_DEVICE_NAME GetTargetDeviceName(
            DISPLAYCONFIG_PATH_TARGET_INFO target)
        {
            var request = new DISPLAYCONFIG_TARGET_DEVICE_NAME
            {
                header =
                {
                    type = DisplayConfigDeviceInfoGetTargetName,
                    size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                    adapterId = target.adapterId,
                    id = target.id
                }
            };

            var code = DisplayConfigGetDeviceInfo(ref request);
            if (code != ErrorSuccess)
                throw new InvalidOperationException($"DisplayConfigGetDeviceInfo for target failed ({code}).");

            request.monitorFriendlyDeviceName ??= string.Empty;
            request.monitorDevicePath ??= string.Empty;
            return request;
        }

        private static Dictionary<string, SavedDisplayLayout> ReadSavedLayoutSettings(string layoutPath)
        {
            var layouts = new Dictionary<string, SavedDisplayLayout>(StringComparer.OrdinalIgnoreCase);
            bool inMonitorSection = false;
            string sectionName = string.Empty;
            bool hasInvalidSetting = false;
            string? name = null;
            string? serialNumber = null;
            string? monitorId = null;
            int? bitsPerPixel = null;
            int? x = null;
            int? y = null;
            int? width = null;
            int? height = null;
            uint? rotation = null;

            void Commit()
            {
                if (!inMonitorSection)
                    return;

                if (hasInvalidSetting ||
                    string.IsNullOrWhiteSpace(name) ||
                    !x.HasValue ||
                    !y.HasValue ||
                    !width.HasValue ||
                    !height.HasValue ||
                    width.Value < 0 ||
                    height.Value < 0 ||
                    (width.Value == 0) != (height.Value == 0))
                {
                    throw new InvalidDataException(
                        $"Saved layout section '{sectionName}' is missing or contains invalid monitor settings.");
                }

                var explicitlyInactive = bitsPerPixel.HasValue && bitsPerPixel.Value <= 0;

                if (explicitlyInactive)
                    return;

                if (width.Value <= 0 || height.Value <= 0)
                {
                    throw new InvalidDataException(
                        $"Active saved layout section '{sectionName}' is missing a valid display size.");
                }

                if (!rotation.HasValue)
                {
                    throw new InvalidDataException(
                        $"Active saved layout section '{sectionName}' is missing a valid DisplayOrientation value.");
                }

                var deviceName = name.Trim();
                if (layouts.ContainsKey(deviceName))
                {
                    throw new InvalidDataException(
                        $"Saved layout contains duplicate active display '{deviceName}'.");
                }

                layouts.Add(
                    deviceName,
                    new SavedDisplayLayout(
                        deviceName,
                        serialNumber ?? string.Empty,
                        monitorId ?? string.Empty,
                        new DisplayPosition(x.Value, y.Value),
                        new DisplaySize(width.Value, height.Value),
                        rotation.Value));
            }

            void ResetSection()
            {
                hasInvalidSetting = false;
                name = null;
                serialNumber = null;
                monitorId = null;
                bitsPerPixel = null;
                x = null;
                y = null;
                width = null;
                height = null;
                rotation = null;
            }

            void ReadInteger(string value, ref int? destination)
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                    destination = parsed;
                else
                    hasInvalidSetting = true;
            }

            foreach (var rawLine in File.ReadLines(layoutPath))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith(";", StringComparison.Ordinal))
                    continue;

                if (line.StartsWith("[", StringComparison.Ordinal) &&
                    line.EndsWith("]", StringComparison.Ordinal))
                {
                    Commit();
                    inMonitorSection = line.StartsWith("[Monitor", StringComparison.OrdinalIgnoreCase);
                    sectionName = line;
                    ResetSection();
                    continue;
                }

                if (!inMonitorSection)
                    continue;

                var equals = line.IndexOf('=');
                if (equals <= 0)
                    continue;

                var key = line[..equals].Trim();
                var value = line[(equals + 1)..].Trim();
                if (key.Equals("Name", StringComparison.OrdinalIgnoreCase))
                {
                    name = value;
                    if (string.IsNullOrWhiteSpace(name))
                        hasInvalidSetting = true;
                }
                else if (key.Equals("SerialNumber", StringComparison.OrdinalIgnoreCase))
                {
                    serialNumber = value;
                }
                else if (key.Equals("MonitorID", StringComparison.OrdinalIgnoreCase))
                {
                    monitorId = value;
                }
                else if (key.Equals("BitsPerPixel", StringComparison.OrdinalIgnoreCase))
                {
                    ReadInteger(value, ref bitsPerPixel);
                }
                else if (key.Equals("PositionX", StringComparison.OrdinalIgnoreCase))
                {
                    ReadInteger(value, ref x);
                }
                else if (key.Equals("PositionY", StringComparison.OrdinalIgnoreCase))
                {
                    ReadInteger(value, ref y);
                }
                else if (key.Equals("Width", StringComparison.OrdinalIgnoreCase))
                {
                    ReadInteger(value, ref width);
                }
                else if (key.Equals("Height", StringComparison.OrdinalIgnoreCase))
                {
                    ReadInteger(value, ref height);
                }
                else if (key.Equals("DisplayOrientation", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedOrientation) &&
                        TryMapDisplayOrientationToCcdRotation(parsedOrientation, out var parsedRotation))
                    {
                        rotation = parsedRotation;
                    }
                    else
                    {
                        hasInvalidSetting = true;
                    }
                }
            }

            Commit();
            return layouts;
        }

        internal static bool TryMapDisplayOrientationToCcdRotation(int orientation, out uint rotation)
        {
            // Legacy profiles store the same values as DEVMODE.dmDisplayOrientation.
            // CCD rotation uses 1-based DISPLAYCONFIG_ROTATION values.
            rotation = orientation switch
            {
                0 => 1, // identity / landscape
                1 => 2, // 90 degrees
                2 => 3, // 180 degrees
                3 => 4, // 270 degrees
                _ => 0
            };

            return rotation != 0;
        }

        private static bool DeviceNameEquals(string left, string right)
            => MonitorTargetResolver.TargetsEquivalent(left, right);

        private static bool SameLuid(LUID left, LUID right)
            => left.LowPart == right.LowPart && left.HighPart == right.HighPart;

        private static long ToInt64(LUID value)
            => unchecked((long)(((ulong)(uint)value.HighPart << 32) | value.LowPart));

        private static DisplayTopologyResult Failure(string message)
            => new() { Success = false, Message = message };

        private sealed record TopologySnapshot(
            DISPLAYCONFIG_PATH_INFO[] Paths,
            DISPLAYCONFIG_MODE_INFO[] Modes,
            uint PathCount,
            uint ModeCount,
            IReadOnlyList<PathEntry> Entries);

        private sealed class PathEntry
        {
            public int PathIndex { get; init; }
            public DISPLAYCONFIG_PATH_INFO Path { get; init; }
            public string Name { get; init; } = string.Empty;
            public string MonitorDevicePath { get; init; } = string.Empty;
            public string TargetFriendlyName { get; init; } = string.Empty;
            public bool EdidIdsValid { get; init; }
            public ushort EdidManufactureId { get; init; }
            public ushort EdidProductCodeId { get; init; }
            public uint ConnectorInstance { get; init; }
            public bool IsActive { get; init; }
            public bool IsAvailable { get; init; }
            public int SourceModeIndex { get; init; }
            public int X { get; init; }
            public int Y { get; init; }
            public int Width { get; init; }
            public int Height { get; init; }
            public uint Rotation { get; init; }

            public DisplaySourceLayout ToLayout()
                => new(Name, SourceModeIndex, X, Y, Width, Height);
        }

        private sealed record SavedDisplayLayout(
            string DeviceName,
            string SerialNumber,
            string MonitorId,
            DisplayPosition Position,
            DisplaySize? Size,
            uint? Rotation);

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
        private static extern int SetDisplayConfig(
            uint numPathArrayElements,
            [In] DISPLAYCONFIG_PATH_INFO[] pathInfoArray,
            uint numModeInfoArrayElements,
            [In] DISPLAYCONFIG_MODE_INFO[]? modeInfoArray,
            uint flags);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(
            ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(
            ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);
    }
}
