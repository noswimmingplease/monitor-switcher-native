using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WorkMonitorSwitcher;
using WorkMonitorSwitcher.Model;
using WorkMonitorSwitcher.Services;

var tests = new List<(string Name, Action Body)>
{
    ("TargetsEquivalent treats quoted and DEV display names as the same device", TargetsEquivalentNormalisesDeviceNames),
    ("PruneKnownTargets removes display devices currently owned by another monitor", PruneKnownTargetsRemovesDeviceOwnedByOther),
    ("ResolveEnableTargetArgs rejects duplicate friendly names and stale devices", ResolveEnableTargetArgsRejectsAmbiguousTargets),
    ("ResolveEnableTargetArgs keeps a safe last-known display target", ResolveEnableTargetArgsKeepsSafeLastKnownDevice),
    ("ResolveEnableTargetArgs prefers live inactive device for the requested monitor", ResolveEnableTargetArgsPrefersLiveInactiveDevice),
    ("Detection assigns distinct fallback identities for duplicate or placeholder serials", DetectionAssignsDistinctFallbackIdentities),
    ("MonitorPresentationBuilder retains active monitors and deduplicates stable keys", PresentationBuilderRetainsActiveAndDeduplicatesStableKeys),
    ("MonitorPresentationBuilder excludes saved-only aliases", PresentationBuilderExcludesSavedOnlyAliases),
    ("MonitorPresentationBuilder excludes detected monitors that are not present", PresentationBuilderExcludesDetectedMonitorsThatAreNotPresent),
    ("MonitorPresentationBuilder retains inactive monitors that are still present", PresentationBuilderRetainsInactiveMonitorsThatArePresent),
    ("Native detection normalises documented driver software keys", NativeDetectionNormalisesDriverSoftwareKeys),
    ("Legacy driver alias migration requires an exact one-to-one match", LegacyDriverAliasMigrationRequiresUniqueMatch),
    ("DisplayTopologyService preserves remaining geometry around the fallback primary", DisplayTopologyPreservesGeometryAroundFallbackPrimary),
    ("DisplayTopologyService requires exactly one saved primary origin", DisplayTopologyRequiresExactlyOneOrigin),
    ("DisplayTopologyService maps saved orientation to CCD rotation", DisplayTopologyMapsSavedOrientationToCcdRotation),
    ("DisplayTopologyService matches saved layout by monitor identity after DISPLAY number changes", DisplayTopologyMatchesSavedLayoutByIdentity),
    ("DisplayTopologyService uses saved sidecar identity before DISPLAY number fallback", DisplayTopologyUsesSavedSidecarIdentityBeforeDisplayNumberFallback),
    ("DisplayTopologyService combines strong identity and safe device fallback", DisplayTopologyCombinesStrongAndWeakIdentityMatching),
    ("DisplayTopologyService excludes inactive saved monitor sections", DisplayTopologyExcludesInactiveSavedSections),
    ("DisplayTopologyService rejects partial and extra monitor-set coverage at any count", DisplayTopologyRejectsPartialAndExtraCoverage),
    ("DisplayTopologyService recognises an exact renamed saved monitor set", DisplayTopologyRecognisesExactRenamedSavedMonitorSet),
    ("DisplayTopologyService exact-set preflight rejects missing, extra, and inactive monitors", DisplayTopologyExactSetRejectsIncompleteOrExtraMonitors),
    ("DisplayTopologyService exact-set preflight rejects ambiguous identities", DisplayTopologyExactSetRejectsAmbiguousIdentities),
    ("DisplayTopologyService exact-set preflight rejects missing and empty layouts", DisplayTopologyExactSetRejectsMissingAndEmptyLayouts),
    ("DisplayTopologyService counts only active saved monitor sections", DisplayTopologyCountsOnlyActiveSavedSections),
    ("AliasSettingsMapper applies aliases and primary selections", AliasSettingsMapperAppliesAliasesAndSelections),
    ("AliasSettingsMapper preserves aliases hidden from Settings", AliasSettingsMapperPreservesHiddenAliases),
    ("AliasSettingsMapper clears fallback when it matches preferred primary", AliasSettingsMapperClearsFallbackWhenItMatchesPreferredPrimary),
    ("MonitorOrderService persists arbitrary visible card order", MonitorOrderServicePersistsArbitraryVisibleOrder),
    ("MonitorOrderService preserves hidden monitor positions and rejects invalid orders", MonitorOrderServicePreservesHiddenPositions),
    ("PrimaryMonitorPreference resolves configured primary targets", PrimaryMonitorPreferenceResolvesConfiguredTargets),
    ("AtomicFileWriter replaces existing files and keeps a backup", AtomicFileWriterReplacesExistingFilesAndKeepsBackup),
    ("JSON settings recover a corrupt primary from a valid backup", JsonSettingsRecoverCorruptPrimaryFromBackup),
    ("JSON settings recover a missing primary from a valid backup", JsonSettingsRecoverMissingPrimaryFromBackup),
    ("Explicit JSON save reports a useful filesystem failure", JsonSaveReportsFilesystemFailure),
    ("Atomic JSON save preserves a valid backup behind a corrupt primary", JsonSavePreservesValidBackupBehindCorruptPrimary),
    ("Diagnostics logging serialises concurrent writers", DiagnosticsLogSerialisesConcurrentWriters),
    ("Layout identities fail closed when only an older sidecar backup is valid", LayoutIdentityRejectsIndependentBackupRecovery),
    ("Layout profile transaction commits the layout and identity map together", LayoutProfileTransactionCommitsMatchedPair),
    ("Layout profile transaction restores every original after a mid-commit failure", LayoutProfileTransactionRollsBackMatchedPair),
    ("Layout profile transaction rejects a staged layout without a valid identity map", LayoutProfileTransactionRejectsMissingIdentity),
    ("Layout profile transaction rejects mismatched and duplicate identity maps", LayoutProfileTransactionRejectsMismatchedIdentityMaps),
    ("Layout profile transaction validates native target paths as one matched pair", LayoutProfileTransactionValidatesNativeTargetPair),
    ("Layout profile transaction preserves the last valid complete backup pair", LayoutProfileTransactionPreservesValidBackupPair),
    ("Layout profile transaction recovers an interrupted partial stash", LayoutProfileTransactionRecoversPartialStash),
    ("Layout profile transaction rejects a missing promised stash", LayoutProfileTransactionRejectsMissingPromisedStash),
    ("Layout profile transaction retains a prepared journal when its original and stash are missing", LayoutProfileTransactionRejectsMissingPreparedOriginal),
    ("DisplayTopologyService persistent apply flags save to the Windows database", DisplayTopologyPersistentApplyFlagsSaveToDatabase),
    ("DisplayTopologyService saved-layout check fails closed before querying CCD", DisplayTopologySavedLayoutCheckFailsClosed),
    ("Layout profile deletion removes only the exact profile artefacts", LayoutProfileDeletionIsExact),
    ("Layout profile deletion rolls back staged artefacts on failure", LayoutProfileDeletionRollsBackOnFailure),
    ("Startup command matching rejects arguments and other executables", StartupCommandMatchingIsExact),
    ("Updater semantic versions order stable, prerelease, and build metadata correctly", UpdaterSemanticVersionsAreOrdered),
    ("Updater accepts only the exact expected GitHub release asset URI", UpdaterReleaseAssetUriIsExact),
    ("Updater parses only a matching SHA-256 sidecar", UpdaterChecksumSidecarIsStrict),
    ("Updater rejects unsafe archive paths", UpdaterArchivePathsAreContained),
    ("LayoutService requires one complete active monitor section", LayoutServiceRequiresCompleteActiveMonitorSection),
    ("Reconnect restore tracker establishes its first observation without triggering", ReconnectTrackerFirstObservationDoesNotTrigger),
    ("Reconnect restore tracker triggers once for each inactive-to-active transition", ReconnectTrackerTriggersOncePerActivation),
    ("Reconnect restore tracker consumes transitions suppressed during display actions", ReconnectTrackerConsumesSuppressedTransition),
    ("Reconnect restore tracker resets its baseline when the profile changes", ReconnectTrackerResetsForProfileChange),
    ("Reconnect restore tracker Reset requires a fresh baseline", ReconnectTrackerResetRequiresFreshBaseline),
    ("Reconnect detection rejects fallback and cross-event snapshots", ReconnectDetectionRejectsInconclusiveSnapshots),
    ("Physical reconnect evidence is consumed by generation", PhysicalReconnectEvidenceIsConsumedByGeneration),
    ("UiSettings disables startup profile application by default", UiSettingsDisablesStartupProfileApplicationByDefault),
    ("Profile set-change confirmation follows the disable confirmation setting", ProfileSetChangeConfirmationFollowsSetting),
    ("Profile Apply is enabled only for a verified monitor-set difference", ProfileApplyRequiresVerifiedDifference),
    ("Settings monitor list sizes to the attached count with a safe cap", SettingsMonitorRowsFollowAttachedCount),
    ("Form1 caps large monitor lists and reserves scrollbar width", Form1CapsLargeMonitorLists),
};

NativeDetectionTests.RunAll((name, body) => tests.Add((name, body)));
tests.AddRange(NativeTopologyTests.GetTests());

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        test.Body();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failures.Add($"{test.Name}: {ex.Message}");
        Console.WriteLine($"FAIL {test.Name}");
        Console.WriteLine(ex);
    }
}

if (failures.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine($"{failures.Count} test(s) failed:");
    foreach (var failure in failures)
        Console.WriteLine($"- {failure}");
    Environment.Exit(1);
}

Console.WriteLine();
Console.WriteLine($"{tests.Count} test(s) passed.");

static void TargetsEquivalentNormalisesDeviceNames()
{
    AssertTrue(
        MonitorTargetResolver.TargetsEquivalent("\"\\\\.\\DISPLAY1\"", "DEV:\\.\\DISPLAY1"),
        "Expected quoted display target and DEV fallback key to compare equal.");
}

static void PruneKnownTargetsRemovesDeviceOwnedByOther()
{
    var aliases = new Dictionary<string, MonitorInfo>(StringComparer.OrdinalIgnoreCase)
    {
        ["SN:RIGHT"] = new MonitorInfo
        {
            KnownTargets = new List<string>
            {
                "\"\\\\.\\DISPLAY1\"",
                "\\\\.\\DISPLAY3",
                "\\\\.\\DISPLAY1",
                "Right Friendly"
            }
        }
    };

    var detected = new List<DetectedMonitor>
    {
        Monitor("SN:LEFT", "\\\\.\\DISPLAY3", "Left Friendly", isActive: true),
        Monitor("SN:RIGHT", "\\\\.\\DISPLAY1", "Right Friendly", isActive: true)
    };

    MonitorTargetResolver.PruneKnownTargets(aliases, detected);

    var targets = aliases["SN:RIGHT"].KnownTargets;
    AssertFalse(targets.Any(t => MonitorTargetResolver.TargetsEquivalent(t, "\\\\.\\DISPLAY3")),
        "Expected DISPLAY3 to be pruned because it belongs to the left monitor.");
    AssertEquals(1, targets.Count(t => MonitorTargetResolver.TargetsEquivalent(t, "\\\\.\\DISPLAY1")),
        "Expected DISPLAY1 duplicates to be normalised to one target.");
    AssertContains(targets, "Right Friendly");
}

static void ResolveEnableTargetArgsRejectsAmbiguousTargets()
{
    var aliases = new Dictionary<string, MonitorInfo>(StringComparer.OrdinalIgnoreCase)
    {
        ["SN:RIGHT"] = new MonitorInfo
        {
            LastDeviceName = "\\\\.\\DISPLAY3",
            KnownTargets = new List<string>
            {
                "AOC Q27B3MA",
                "\\\\.\\DISPLAY3"
            }
        }
    };

    var detected = new List<DetectedMonitor>
    {
        Monitor("SN:LEFT", "\\\\.\\DISPLAY3", "AOC Q27B3MA", isActive: true),
        Monitor("SN:OTHER", "\\\\.\\DISPLAY2", "AOC Q27B3MA", isActive: true)
    };

    var targets = MonitorTargetResolver.ResolveEnableTargetArgs("SN:RIGHT", detected, aliases);

    AssertEquals(0, targets.Count, "Expected no safe enable targets when the name is duplicated and the device is in use.");
}

static void ResolveEnableTargetArgsKeepsSafeLastKnownDevice()
{
    var aliases = new Dictionary<string, MonitorInfo>(StringComparer.OrdinalIgnoreCase)
    {
        ["SN:RIGHT"] = new MonitorInfo
        {
            LastDeviceName = "\\\\.\\DISPLAY1",
            KnownTargets = new List<string>
            {
                "AOC Q27B3MA",
                "\\\\.\\DISPLAY3",
                "\\\\.\\DISPLAY1"
            }
        }
    };

    var detected = new List<DetectedMonitor>
    {
        Monitor("SN:LEFT", "\\\\.\\DISPLAY3", "AOC Q27B3MA", isActive: true)
    };

    var targets = MonitorTargetResolver.ResolveEnableTargetArgs("SN:RIGHT", detected, aliases);

    AssertSequence(targets, "\\\\.\\DISPLAY1");
}

static void ResolveEnableTargetArgsPrefersLiveInactiveDevice()
{
    var aliases = new Dictionary<string, MonitorInfo>(StringComparer.OrdinalIgnoreCase)
    {
        ["SN:RIGHT"] = new MonitorInfo
        {
            LastDeviceName = "\\\\.\\DISPLAY1",
            KnownTargets = new List<string>
            {
                "\\\\.\\DISPLAY1",
                "\\\\.\\DISPLAY8"
            }
        }
    };

    var detected = new List<DetectedMonitor>
    {
        Monitor("SN:LEFT", "\\\\.\\DISPLAY1", "AOC Q27B3MA", isActive: true),
        Monitor("SN:RIGHT", "\\\\.\\DISPLAY3", "AOC Q27B3MA", isActive: false)
    };

    var targets = MonitorTargetResolver.ResolveEnableTargetArgs("SN:RIGHT", detected, aliases);

    AssertSequence(targets, "\\\\.\\DISPLAY3", "\\\\.\\DISPLAY8");
}

static void DetectionAssignsDistinctFallbackIdentities()
{
    var monitors = new List<DetectedMonitor>
    {
        new()
        {
            SerialNumber = "DUPLICATE",
            InstanceId = "DISPLAY\\INSTANCE-A",
            DeviceName = @"\\.\DISPLAY1"
        },
        new()
        {
            SerialNumber = "DUPLICATE",
            InstanceId = "DISPLAY\\INSTANCE-B",
            DeviceName = @"\\.\DISPLAY2"
        },
        new()
        {
            SerialNumber = "00000000",
            MonitorKey = @"\Registry\Machine\Monitor\Three",
            DeviceName = @"\\.\DISPLAY3"
        },
        new()
        {
            SerialNumber = "UNIQUE-FOUR",
            DeviceName = @"\\.\DISPLAY4"
        },
        new()
        {
            DeviceName = @"\\.\DISPLAY5"
        }
    };

    DetectionService.AssignUniqueStableKeys(monitors);

    AssertEquals("IID:DISPLAY\\INSTANCE-A", monitors[0].StableKey,
        "Expected the first duplicated serial to fall back to its instance ID.");
    AssertEquals("IID:DISPLAY\\INSTANCE-B", monitors[1].StableKey,
        "Expected the second duplicated serial to fall back to its instance ID.");
    AssertEquals(@"MK:\Registry\Machine\Monitor\Three", monitors[2].StableKey,
        "Expected an all-zero serial to fall back to its monitor key.");
    AssertEquals("SN:UNIQUE-FOUR", monitors[3].StableKey,
        "Expected a credible unique serial to remain the preferred identity.");
    AssertEquals(@"DEV:\.\DISPLAY5", monitors[4].StableKey,
        "Expected a monitor without hardware identity to fall back to its device name.");
    AssertEquals(monitors.Count, monitors.Select(m => m.StableKey).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
        "Expected every detected monitor to retain a distinct logical identity.");
}

static void PresentationBuilderRetainsActiveAndDeduplicatesStableKeys()
{
    var detected = new List<DetectedMonitor>
    {
        new DetectedMonitor
        {
            StableKey = "SN:DUP",
            DeviceName = "\\\\.\\DISPLAY9",
            IsPresent = false,
            IsActive = false,
            PositionX = 100
        },
        new DetectedMonitor
        {
            StableKey = "SN:DUP",
            DeviceName = "\\\\.\\DISPLAY1",
            SerialNumber = "DUP",
            MonitorKey = "MK1",
            IsPresent = true,
            IsActive = true,
            PositionX = 0
        }
    };

    var rows = MonitorPresentationBuilder.Build(detected, EmptyAliases());

    AssertEquals(1, rows.Count, "Expected duplicate stable keys to collapse to one row.");
    AssertEquals("\\\\.\\DISPLAY1", rows[0].DeviceName, "Expected active complete row to win.");
    AssertTrue(rows[0].IsActive, "Expected the active detected monitor to remain visible.");
    AssertTrue(rows[0].IsPresent, "Expected selected row to remain present.");
}

static void PresentationBuilderExcludesSavedOnlyAliases()
{
    var aliases = new Dictionary<string, MonitorInfo>(StringComparer.OrdinalIgnoreCase)
    {
        ["SN:STALE"] = new MonitorInfo
        {
            Name = "Stale Monitor",
            LastDeviceName = "\\\\.\\DISPLAY4",
            LastKnownX = 2560
        }
    };

    var rows = MonitorPresentationBuilder.Build(Array.Empty<DetectedMonitor>(), aliases);

    AssertEquals(0, rows.Count, "Expected aliases without a detected present monitor to be excluded.");
}

static void PresentationBuilderExcludesDetectedMonitorsThatAreNotPresent()
{
    var detected = new List<DetectedMonitor>
    {
        Monitor("DEV:\\.\\DISPLAY4", "\\\\.\\DISPLAY4", "Phantom", isPresent: false, isActive: false)
    };

    var rows = MonitorPresentationBuilder.Build(detected, EmptyAliases());

    AssertEquals(0, rows.Count, "Expected a detected monitor marked not present to be excluded.");
}

static void PresentationBuilderRetainsInactiveMonitorsThatArePresent()
{
    var detected = new List<DetectedMonitor>
    {
        Monitor("SN:CONNECTED", "\\\\.\\DISPLAY3", "Connected", isPresent: true, isActive: false)
    };

    var rows = MonitorPresentationBuilder.Build(detected, EmptyAliases());

    AssertEquals(1, rows.Count, "Expected a connected but disabled monitor to remain visible.");
    AssertFalse(rows[0].IsActive, "Expected the retained monitor to remain inactive.");
    AssertTrue(rows[0].IsPresent, "Expected the retained monitor to remain present.");
}

static void NativeDetectionNormalisesDriverSoftwareKeys()
{
    const string expected = "{4D36E96E-E325-11CE-BFC1-08002BE10318}\\0005";
    AssertTrue(
        NativeDisplayDetection.TryNormalizeDriverSoftwareKey(
            @"{4d36e96e-e325-11ce-bfc1-08002be10318}\0005",
            out var documentedValue),
        "Expected DEVPKEY_Device_Driver format to be accepted.");
    AssertEquals(expected, documentedValue, "Expected canonical driver software key.");

    AssertTrue(
        NativeDisplayDetection.TryNormalizeDriverSoftwareKey(
            @"MK:\Registry\Machine\System\CurrentControlSet\Control\Class\{4d36e96e-e325-11ce-bfc1-08002be10318}\0005",
            out var legacyValue),
        "Expected a legacy MK class-registry key to be accepted.");
    AssertEquals(expected, legacyValue, "Expected the legacy key to canonicalise identically.");

    AssertFalse(
        NativeDisplayDetection.TryNormalizeDriverSoftwareKey(@"MK:\Registry\Machine\Enum\DISPLAY\AOC\INSTANCE", out _),
        "Expected a non-driver registry key to be rejected.");
}

static void LegacyDriverAliasMigrationRequiresUniqueMatch()
{
    const string driverPath =
        @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Class\{4d36e96e-e325-11ce-bfc1-08002be10318}\0005";
    const string legacyKey =
        @"MK:\Registry\Machine\System\CurrentControlSet\Control\Class\{4d36e96e-e325-11ce-bfc1-08002be10318}\0005";
    var retained = new MonitorInfo
    {
        Name = "Display 3",
        PreferredOrder = 2,
        IsPreferredPrimary = true
    };
    var monitor = Monitor("SN:CURRENT", @"\\.\DISPLAY1", "AOC", isActive: true);
    monitor.DriverRegistryKey = driverPath;
    var candidates = new Dictionary<string, MonitorInfo>(StringComparer.OrdinalIgnoreCase)
    {
        [legacyKey] = retained,
        ["SN:DISCONNECTED"] = new MonitorInfo { Name = "Keep disconnected" }
    };

    var match = Form1.FindUniqueLegacyDriverAliasMatch(monitor, new[] { monitor }, candidates);
    AssertTrue(match.HasValue, "Expected one exact current and legacy driver-key match.");
    var matchedAlias = match.GetValueOrDefault();
    AssertEquals(legacyKey, matchedAlias.Key, "Expected the matching legacy MK alias.");
    AssertTrue(ReferenceEquals(retained, matchedAlias.Value), "Expected all legacy metadata to be retained by reference.");

    var duplicateCurrent = Monitor("SN:OTHER", @"\\.\DISPLAY2", "AOC", isActive: true);
    duplicateCurrent.DriverRegistryKey = driverPath;
    AssertFalse(
        Form1.FindUniqueLegacyDriverAliasMatch(monitor, new[] { monitor, duplicateCurrent }, candidates).HasValue,
        "Expected duplicate current driver keys to fail closed.");

    candidates[@"MK:HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Class\{4d36e96e-e325-11ce-bfc1-08002be10318}\0005"] = new MonitorInfo();
    AssertFalse(
        Form1.FindUniqueLegacyDriverAliasMatch(monitor, new[] { monitor }, candidates).HasValue,
        "Expected duplicate legacy driver keys to fail closed.");
}

static void DisplayTopologyPreservesGeometryAroundFallbackPrimary()
{
    var left = new DisplaySourceLayout("\\\\.\\DISPLAY1", 1, -2560, 19, 2560, 1440);
    var right = new DisplaySourceLayout("\\\\.\\DISPLAY3", 3, 2560, -1087, 2560, 1440);

    var positions = DisplayTopologyService.CalculateRebasedPositions(
        new[] { left, right },
        left);

    AssertEquals(new DisplayPosition(0, 0), positions[1], "Expected fallback primary to move to origin.");
    AssertEquals(new DisplayPosition(5120, -1106), positions[3],
        "Expected the original horizontal gap and vertical offset to be preserved relative to the new primary.");
}

static void DisplayTopologyRequiresExactlyOneOrigin()
{
    AssertTrue(
        DisplayTopologyService.HasExactlyOneOrigin(new[]
        {
            new DisplayPosition(0, 0),
            new DisplayPosition(2560, -1106),
            new DisplayPosition(-2560, 19)
        }),
        "Expected one primary origin to be accepted.");
    AssertFalse(
        DisplayTopologyService.HasExactlyOneOrigin(new[]
        {
            new DisplayPosition(10, 0),
            new DisplayPosition(2560, 0)
        }),
        "Expected a layout without an origin to be rejected.");
    AssertFalse(
        DisplayTopologyService.HasExactlyOneOrigin(new[]
        {
            new DisplayPosition(0, 0),
            new DisplayPosition(0, 0)
        }),
        "Expected a layout with multiple origins to be rejected.");
}

static void DisplayTopologyMapsSavedOrientationToCcdRotation()
{
    AssertTrue(DisplayTopologyService.TryMapDisplayOrientationToCcdRotation(0, out var identity),
        "Expected landscape orientation to map.");
    AssertTrue(DisplayTopologyService.TryMapDisplayOrientationToCcdRotation(1, out var rotate90),
        "Expected 90-degree orientation to map.");
    AssertTrue(DisplayTopologyService.TryMapDisplayOrientationToCcdRotation(2, out var rotate180),
        "Expected 180-degree orientation to map.");
    AssertTrue(DisplayTopologyService.TryMapDisplayOrientationToCcdRotation(3, out var rotate270),
        "Expected 270-degree orientation to map.");

    AssertEquals<uint>(1, identity, "Expected identity CCD rotation.");
    AssertEquals<uint>(2, rotate90, "Expected 90-degree CCD rotation.");
    AssertEquals<uint>(3, rotate180, "Expected 180-degree CCD rotation.");
    AssertEquals<uint>(4, rotate270, "Expected 270-degree CCD rotation.");
    AssertFalse(DisplayTopologyService.TryMapDisplayOrientationToCcdRotation(99, out _),
        "Expected unknown orientation to be rejected.");
}

static void DisplayTopologyMatchesSavedLayoutByIdentity()
{
    var dir = Path.Combine(Path.GetTempPath(), "MonitorSwitcher.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);

    try
    {
        var path = Path.Combine(dir, "monitor-layout.cfg");
        File.WriteAllText(path, string.Join(Environment.NewLine, new[]
        {
            "[Monitor0]",
            @"Name=\\.\DISPLAY1",
            @"MonitorID=MONITOR\AOC2703\{4d36e96e-e325-11ce-bfc1-08002be10318}\0000",
            "SerialNumber=TEST-LEFT-001",
            "Width=2560",
            "Height=1440",
            "DisplayOrientation=0",
            "PositionX=-2560",
            "PositionY=19",
            "[Monitor1]",
            @"Name=\\.\DISPLAY2",
            @"MonitorID=MONITOR\AOC2730\{4d36e96e-e325-11ce-bfc1-08002be10318}\0005",
            "SerialNumber=",
            "Width=2560",
            "Height=1440",
            "DisplayOrientation=0",
            "PositionX=0",
            "PositionY=0",
            "[Monitor2]",
            @"Name=\\.\DISPLAY3",
            @"MonitorID=MONITOR\AOC2703\{4d36e96e-e325-11ce-bfc1-08002be10318}\0004",
            "SerialNumber=TEST-RIGHT-002",
            "Width=1440",
            "Height=2560",
            "DisplayOrientation=3",
            "PositionX=2560",
            "PositionY=-1087"
        }));

        var detected = new[]
        {
            Monitor("SN:TEST-LEFT-001", @"\\.\DISPLAY3", "Left", isActive: true),
            Monitor("MK:MIDDLE", @"\\.\DISPLAY1", "Middle", isActive: true),
            Monitor("SN:TEST-RIGHT-002", @"\\.\DISPLAY2", "Right", isActive: true)
        };
        detected[0].SerialNumber = "TEST-LEFT-001";
        detected[1].MonitorId = @"MONITOR\AOC2730\{4d36e96e-e325-11ce-bfc1-08002be10318}\0005";
        detected[1].InstanceId = @"DISPLAY\AOC2730\MIDDLE";
        detected[2].SerialNumber = "TEST-RIGHT-002";

        var identities = new[]
        {
            new SavedLayoutIdentity
            {
                LayoutDeviceName = @"\\.\DISPLAY1",
                StableKey = "SN:TEST-LEFT-001",
                SerialNumber = "TEST-LEFT-001"
            },
            new SavedLayoutIdentity
            {
                LayoutDeviceName = @"\\.\DISPLAY2",
                StableKey = @"IID:DISPLAY\AOC2730\MIDDLE",
                InstanceId = @"DISPLAY\AOC2730\MIDDLE"
            },
            new SavedLayoutIdentity
            {
                LayoutDeviceName = @"\\.\DISPLAY3",
                StableKey = "SN:TEST-RIGHT-002",
                SerialNumber = "TEST-RIGHT-002"
            }
        };

        var map = DisplayTopologyService.ResolveSavedLayoutDeviceNameMap(
            path,
            detected,
            new[] { @"\\.\DISPLAY1", @"\\.\DISPLAY2", @"\\.\DISPLAY3" },
            identities);

        AssertEquals(@"\\.\DISPLAY2", map[@"\\.\DISPLAY1"], "Expected current middle display to use the saved middle layout.");
        AssertEquals(@"\\.\DISPLAY3", map[@"\\.\DISPLAY2"], "Expected current right display to use the saved right layout.");
        AssertEquals(@"\\.\DISPLAY1", map[@"\\.\DISPLAY3"], "Expected current left display to use the saved left layout.");
    }
    finally
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }
}

static void DisplayTopologyUsesSavedSidecarIdentityBeforeDisplayNumberFallback()
{
    var dir = Path.Combine(Path.GetTempPath(), "MonitorSwitcher.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);

    try
    {
        var path = Path.Combine(dir, "monitor-layout.cfg");
        File.WriteAllText(path, string.Join(Environment.NewLine, new[]
        {
            "[Monitor0]",
            @"Name=\\.\DISPLAY1",
            @"MonitorID=MONITOR\AOC2703",
            "SerialNumber=",
            "Width=2560",
            "Height=1440",
            "DisplayOrientation=0",
            "PositionX=0",
            "PositionY=0",
            "[Monitor1]",
            @"Name=\\.\DISPLAY2",
            @"MonitorID=MONITOR\AOC2703",
            "SerialNumber=",
            "Width=1440",
            "Height=2560",
            "DisplayOrientation=3",
            "PositionX=2560",
            "PositionY=-1087"
        }));

        var savedIdentities = new[]
        {
            new SavedLayoutIdentity
            {
                LayoutDeviceName = @"\\.\DISPLAY1",
                StableKey = "SN:LEFT",
                SerialNumber = "LEFT",
                MonitorId = @"MONITOR\AOC2703"
            },
            new SavedLayoutIdentity
            {
                LayoutDeviceName = @"\\.\DISPLAY2",
                StableKey = "SN:RIGHT",
                SerialNumber = "RIGHT",
                MonitorId = @"MONITOR\AOC2703"
            }
        };

        var detected = new[]
        {
            Monitor("SN:RIGHT", @"\\.\DISPLAY1", "AOC Q27B3MA", isActive: true),
            Monitor("SN:LEFT", @"\\.\DISPLAY2", "AOC Q27B3MA", isActive: true)
        };
        detected[0].SerialNumber = "RIGHT";
        detected[0].MonitorId = @"MONITOR\AOC2703";
        detected[1].SerialNumber = "LEFT";
        detected[1].MonitorId = @"MONITOR\AOC2703";

        var map = DisplayTopologyService.ResolveSavedLayoutDeviceNameMap(
            path,
            detected,
            new[] { @"\\.\DISPLAY1", @"\\.\DISPLAY2" },
            savedIdentities);

        AssertEquals(@"\\.\DISPLAY2", map[@"\\.\DISPLAY1"], "Expected current right display to use the saved right layout.");
        AssertEquals(@"\\.\DISPLAY1", map[@"\\.\DISPLAY2"], "Expected current left display to use the saved left layout.");
    }
    finally
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }
}

static void DisplayTopologyCombinesStrongAndWeakIdentityMatching()
{
    var dir = Path.Combine(Path.GetTempPath(), "MonitorSwitcher.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);

    try
    {
        var path = Path.Combine(dir, "monitor-layout.cfg");
        File.WriteAllText(path, string.Join(Environment.NewLine, new[]
        {
            "[Monitor0]",
            @"Name=\\.\DISPLAY1",
            "Width=1920",
            "Height=1080",
            "DisplayOrientation=0",
            "PositionX=0",
            "PositionY=0",
            "[Monitor1]",
            @"Name=\\.\DISPLAY2",
            "Width=1920",
            "Height=1080",
            "DisplayOrientation=0",
            "PositionX=1920",
            "PositionY=0"
        }));

        var savedIdentities = new[]
        {
            new SavedLayoutIdentity
            {
                LayoutDeviceName = @"\\.\DISPLAY1",
                StableKey = "SN:STRONG",
                SerialNumber = "STRONG"
            },
            new SavedLayoutIdentity
            {
                LayoutDeviceName = @"\\.\DISPLAY2",
                StableKey = @"DEV:\.\DISPLAY2"
            }
        };
        var detected = new[]
        {
            Monitor("SN:STRONG", @"\\.\DISPLAY7", "Strong", isActive: true),
            Monitor(@"DEV:\.\DISPLAY2", @"\\.\DISPLAY2", "Weak", isActive: true)
        };
        detected[0].SerialNumber = "STRONG";

        var map = DisplayTopologyService.ResolveSavedLayoutDeviceNameMap(
            path,
            detected,
            new[] { @"\\.\DISPLAY7", @"\\.\DISPLAY2" },
            savedIdentities);

        AssertEquals(@"\\.\DISPLAY1", map[@"\\.\DISPLAY7"],
            "Expected the renamed strongly identified monitor to match first.");
        AssertEquals(@"\\.\DISPLAY2", map[@"\\.\DISPLAY2"],
            "Expected the remaining weak monitor to use safe unchanged device fallback.");
    }
    finally
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }
}

static void DisplayTopologyExcludesInactiveSavedSections()
{
    var dir = Path.Combine(Path.GetTempPath(), "MonitorSwitcher.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);

    try
    {
        var path = Path.Combine(dir, "monitor-layout.cfg");
        File.WriteAllText(path, string.Join(Environment.NewLine, new[]
        {
            "[Monitor0]",
            @"Name=\\.\DISPLAY1",
            "BitsPerPixel=32",
            "Width=1920",
            "Height=1080",
            "DisplayOrientation=0",
            "PositionX=0",
            "PositionY=0",
            "[Monitor1]",
            @"Name=\\.\DISPLAY2",
            "BitsPerPixel=0",
            "Width=0",
            "Height=0",
            "PositionX=1920",
            "PositionY=0"
        }));

        var detected = new[]
        {
            Monitor(@"DEV:\.\DISPLAY1", @"\\.\DISPLAY1", "Active", isActive: true)
        };
        var map = DisplayTopologyService.ResolveSavedLayoutDeviceNameMap(
            path,
            detected,
            new[] { @"\\.\DISPLAY1", @"\\.\DISPLAY2" });

        AssertEquals(1, map.Count, "Expected the inactive saved section to be excluded.");
        AssertTrue(map.ContainsKey(@"\\.\DISPLAY1"), "Expected the active saved section to remain available.");
        AssertFalse(map.ContainsKey(@"\\.\DISPLAY2"), "Expected no mapping for the inactive saved section.");
    }
    finally
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }
}

static void DisplayTopologyRejectsPartialAndExtraCoverage()
{
    var saved = new[]
    {
        @"\\.\DISPLAY1",
        @"\\.\DISPLAY2",
        @"\\.\DISPLAY3",
        @"\\.\DISPLAY4",
        @"\\.\DISPLAY5"
    };
    var active = new[]
    {
        @"\\.\DISPLAY7",
        @"\\.\DISPLAY8",
        @"\\.\DISPLAY9",
        @"\\.\DISPLAY10"
    };
    var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [active[0]] = saved[0],
        [active[1]] = saved[1],
        [active[2]] = saved[2]
    };

    var gaps = DisplayTopologyService.FindLayoutCoverageGaps(saved, resolved, active);

    AssertSequence(gaps.ActiveWithoutSavedLayout, active[3]);
    AssertSequence(gaps.SavedWithoutActiveDisplay, saved[3], saved[4]);

    var completeResolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var completeActive = new List<string>();
    for (int i = 0; i < 12; i++)
    {
        var activeName = $@"\\.\DISPLAY{20 + i}";
        var savedName = $@"\\.\DISPLAY{40 + i}";
        completeActive.Add(activeName);
        completeResolved[activeName] = savedName;
    }

    var complete = DisplayTopologyService.FindLayoutCoverageGaps(
        completeResolved.Values,
        completeResolved,
        completeActive);

    AssertEquals(0, complete.ActiveWithoutSavedLayout.Count,
        "Expected complete active coverage for an arbitrary twelve-monitor profile.");
    AssertEquals(0, complete.SavedWithoutActiveDisplay.Count,
        "Expected complete saved coverage for an arbitrary twelve-monitor profile.");
}

static void DisplayTopologyRecognisesExactRenamedSavedMonitorSet()
{
    var dir = Path.Combine(Path.GetTempPath(), "MonitorSwitcher.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);

    try
    {
        var path = Path.Combine(dir, "monitor-layout.cfg");
        File.WriteAllText(path, SavedMonitorLayoutText(8, includeSerialNumbers: false));

        var detected = Enumerable.Range(0, 8)
            .Select(index =>
            {
                var monitor = Monitor(
                    $"SN:SERIAL-{index}",
                    $@"\\.\DISPLAY{30 + index}",
                    $"Monitor {index}",
                    isActive: true);
                monitor.SerialNumber = $"SERIAL-{index}";
                return monitor;
            })
            .ToArray();
        var savedIdentities = Enumerable.Range(0, 8)
            .Select(index => new SavedLayoutIdentity
            {
                LayoutDeviceName = $@"\\.\DISPLAY{index + 1}",
                StableKey = $"SN:SERIAL-{index}",
                SerialNumber = $"SERIAL-{index}"
            })
            .ToArray();

        AssertTrue(
            DisplayTopologyService.IsExactSavedMonitorSetActive(path, detected, savedIdentities),
            "Expected arbitrary-count saved monitors to match saved serial identities after DISPLAY numbers changed.");
    }
    finally
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }
}

static void DisplayTopologyExactSetRejectsIncompleteOrExtraMonitors()
{
    var dir = Path.Combine(Path.GetTempPath(), "MonitorSwitcher.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);

    try
    {
        var path = Path.Combine(dir, "monitor-layout.cfg");
        File.WriteAllText(path, SavedMonitorLayoutText(3));

        DetectedMonitor SavedMonitor(int index, bool isActive = true, bool isPresent = true)
        {
            var monitor = Monitor(
                $"SN:SERIAL-{index}",
                $@"\\.\DISPLAY{20 + index}",
                $"Monitor {index}",
                isActive,
                isPresent);
            monitor.SerialNumber = $"SERIAL-{index}";
            return monitor;
        }

        var missing = new[] { SavedMonitor(0), SavedMonitor(1) };
        AssertFalse(
            DisplayTopologyService.IsExactSavedMonitorSetActive(path, missing, savedIdentities: null),
            "Expected a missing saved monitor to reject exact-set preflight.");

        var inactive = new[] { SavedMonitor(0), SavedMonitor(1), SavedMonitor(2, isActive: false) };
        AssertFalse(
            DisplayTopologyService.IsExactSavedMonitorSetActive(path, inactive, savedIdentities: null),
            "Expected an inactive saved monitor to reject exact-set preflight.");

        var disconnected = new[] { SavedMonitor(0), SavedMonitor(1), SavedMonitor(2, isPresent: false) };
        AssertFalse(
            DisplayTopologyService.IsExactSavedMonitorSetActive(path, disconnected, savedIdentities: null),
            "Expected a disconnected saved monitor to reject exact-set preflight.");

        var foreign = Monitor("SN:FOREIGN", @"\\.\DISPLAY99", "Foreign", isActive: true);
        foreign.SerialNumber = "FOREIGN";
        var extra = new[] { SavedMonitor(0), SavedMonitor(1), SavedMonitor(2), foreign };
        AssertFalse(
            DisplayTopologyService.IsExactSavedMonitorSetActive(path, extra, savedIdentities: null),
            "Expected an extra foreign monitor to reject exact-set preflight.");
    }
    finally
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }
}

static void DisplayTopologyExactSetRejectsAmbiguousIdentities()
{
    var dir = Path.Combine(Path.GetTempPath(), "MonitorSwitcher.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);

    try
    {
        var path = Path.Combine(dir, "monitor-layout.cfg");
        File.WriteAllText(path, string.Join(Environment.NewLine, new[]
        {
            "[Monitor0]",
            @"Name=\\.\DISPLAY1",
            "SerialNumber=",
            "Width=1920",
            "Height=1080",
            "DisplayOrientation=0",
            "PositionX=0",
            "PositionY=0",
            "[Monitor1]",
            @"Name=\\.\DISPLAY2",
            "SerialNumber=",
            "Width=1920",
            "Height=1080",
            "DisplayOrientation=0",
            "PositionX=1920",
            "PositionY=0"
        }));

        var savedIdentities = new[]
        {
            new SavedLayoutIdentity
            {
                LayoutDeviceName = @"\\.\DISPLAY1",
                SerialNumber = "DUPLICATE"
            },
            new SavedLayoutIdentity
            {
                LayoutDeviceName = @"\\.\DISPLAY2",
                SerialNumber = "DUPLICATE"
            }
        };
        var first = Monitor("IID:FIRST", @"\\.\DISPLAY7", "First", isActive: true);
        first.SerialNumber = "DUPLICATE";
        var second = Monitor("IID:SECOND", @"\\.\DISPLAY8", "Second", isActive: true);
        second.SerialNumber = "DUPLICATE";

        AssertFalse(
            DisplayTopologyService.IsExactSavedMonitorSetActive(
                path,
                new[] { first, second },
                savedIdentities),
            "Expected duplicate identities with changed DISPLAY numbers to be rejected as ambiguous.");
    }
    finally
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }
}

static void DisplayTopologyExactSetRejectsMissingAndEmptyLayouts()
{
    var dir = Path.Combine(Path.GetTempPath(), "MonitorSwitcher.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);

    try
    {
        var missingPath = Path.Combine(dir, "missing.cfg");
        var emptyPath = Path.Combine(dir, "empty.cfg");
        File.WriteAllText(emptyPath, string.Empty);
        var detected = new[]
        {
            Monitor(@"DEV:\.\DISPLAY1", @"\\.\DISPLAY1", "Monitor", isActive: true)
        };

        AssertFalse(
            DisplayTopologyService.IsExactSavedMonitorSetActive(missingPath, detected, savedIdentities: null),
            "Expected a missing layout to reject exact-set preflight.");
        AssertFalse(
            DisplayTopologyService.IsExactSavedMonitorSetActive(emptyPath, detected, savedIdentities: null),
            "Expected an empty layout to reject exact-set preflight.");
    }
    finally
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }
}

static void DisplayTopologyCountsOnlyActiveSavedSections()
{
    var dir = Path.Combine(Path.GetTempPath(), "MonitorSwitcher.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);

    try
    {
        var path = Path.Combine(dir, "monitor-layout.cfg");
        File.WriteAllText(path, string.Join(Environment.NewLine, new[]
        {
            "[Monitor0]",
            @"Name=\\.\DISPLAY1",
            "BitsPerPixel=32",
            "Width=1920",
            "Height=1080",
            "DisplayOrientation=0",
            "PositionX=0",
            "PositionY=0",
            "[Monitor1]",
            @"Name=\\.\DISPLAY2",
            "BitsPerPixel=0",
            "Width=0",
            "Height=0",
            "PositionX=1920",
            "PositionY=0"
        }));

        AssertEquals(1, DisplayTopologyService.GetSavedActiveMonitorCount(path),
            "Expected inactive saved sections not to count towards fallback disconnect evidence.");
        AssertEquals(0, DisplayTopologyService.GetSavedActiveMonitorCount(Path.Combine(dir, "missing.cfg")),
            "Expected a missing layout to report no saved active monitors.");
    }
    finally
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }
}

static string SavedMonitorLayoutText(int monitorCount, bool includeSerialNumbers = true)
{
    var lines = new List<string>();
    for (int index = 0; index < monitorCount; index++)
    {
        lines.Add($"[Monitor{index}]");
        lines.Add($@"Name=\\.\DISPLAY{index + 1}");
        lines.Add(includeSerialNumbers ? $"SerialNumber=SERIAL-{index}" : "SerialNumber=");
        lines.Add("Width=1920");
        lines.Add("Height=1080");
        lines.Add("DisplayOrientation=0");
        lines.Add($"PositionX={index * 1920}");
        lines.Add("PositionY=0");
    }

    return string.Join(Environment.NewLine, lines);
}

static void AliasSettingsMapperAppliesAliasesAndSelections()
{
    var aliases = new Dictionary<string, MonitorInfo>(StringComparer.OrdinalIgnoreCase)
    {
        ["SN:LEFT"] = new MonitorInfo { Name = "Old Left", IsPreferredPrimary = true },
        ["SN:RIGHT"] = new MonitorInfo { Name = "Old Right" },
        ["SN:STALE"] = new MonitorInfo { Name = "Stale" }
    };

    var updated = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["SN:LEFT"] = "Left Monitor",
        ["SN:RIGHT"] = "Right Monitor"
    };

    AliasSettingsMapper.ApplyMonitorSettings(
        aliases,
        new[] { "SN:STALE" },
        updated,
        preferredPrimaryKey: "SN:RIGHT",
        fallbackPrimaryKey: "SN:LEFT");

    AssertFalse(aliases.ContainsKey("SN:STALE"), "Expected removed monitor key to be deleted.");
    AssertEquals("Left Monitor", aliases["SN:LEFT"].Name, "Expected alias to be updated.");
    AssertFalse(aliases["SN:LEFT"].IsPreferredPrimary, "Expected previous preferred primary to be cleared.");
    AssertTrue(aliases["SN:RIGHT"].IsPreferredPrimary, "Expected selected preferred primary to be set.");
    AssertTrue(aliases["SN:LEFT"].IsFallbackPrimary, "Expected selected fallback primary to be set.");
    AssertFalse(aliases["SN:RIGHT"].IsFallbackPrimary, "Expected fallback primary to remain single-select.");

    var rows = AliasSettingsMapper.BuildRows(
        new[] { Monitor("SN:LEFT", "\\\\.\\DISPLAY1", "AOC", isActive: true) },
        aliases);

    AssertEquals("Left Monitor", rows[0].Alias, "Expected settings row to use saved alias.");
    AssertTrue(rows[0].IsFallbackPrimary, "Expected settings row to expose fallback selection.");
}

static void AliasSettingsMapperPreservesHiddenAliases()
{
    var dir = Path.Combine(Path.GetTempPath(), "MonitorSwitcher.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);

    try
    {
        var aliases = new Dictionary<string, MonitorInfo>(StringComparer.OrdinalIgnoreCase)
        {
            ["SN:VISIBLE"] = new MonitorInfo { Name = "Visible" },
            ["SN:HIDDEN-PRIMARY"] = new MonitorInfo
            {
                Name = "Disconnected Primary",
                LastDeviceName = @"\\.\DISPLAY7",
                LastRegistryKey = @"\Registry\Machine\System\TestPrimary",
                LastSerialNumber = "HIDDEN-PRIMARY",
                LastInstanceId = @"DISPLAY\TEST\PRIMARY",
                LastMonitorId = @"MONITOR\TESTPRIMARY",
                LastKnownX = -2560,
                KnownTargets = new List<string> { @"\\.\DISPLAY7", "Disconnected Primary" },
                IsPreferredPrimary = true
            },
            ["SN:HIDDEN-FALLBACK"] = new MonitorInfo
            {
                Name = "Disconnected Fallback",
                IsFallbackPrimary = true
            }
        };

        // Settings passes only rows represented in the dialog to the mapper. Hidden aliases
        // remain in the complete map that is subsequently persisted.
        var representedAliases = new Dictionary<string, MonitorInfo>(StringComparer.OrdinalIgnoreCase)
        {
            ["SN:VISIBLE"] = aliases["SN:VISIBLE"]
        };

        AliasSettingsMapper.ApplyMonitorSettings(
            representedAliases,
            Array.Empty<string>(),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["SN:VISIBLE"] = "Renamed Visible"
            },
            preferredPrimaryKey: null,
            fallbackPrimaryKey: null);

        foreach (var pair in representedAliases)
            aliases[pair.Key] = pair.Value;

        var store = new AliasStore(dir);
        var save = store.SaveWithResult(aliases);
        AssertTrue(save.Success, "Expected the complete alias map to save successfully.");

        var persisted = store.Load();
        AssertEquals(3, persisted.Count, "Expected saving visible Settings rows not to delete hidden aliases.");
        AssertEquals("Renamed Visible", persisted["SN:VISIBLE"].Name, "Expected the visible alias edit to be persisted.");

        var hiddenPrimary = persisted["SN:HIDDEN-PRIMARY"];
        AssertEquals("Disconnected Primary", hiddenPrimary.Name, "Expected the hidden alias name to be retained.");
        AssertEquals(@"\\.\DISPLAY7", hiddenPrimary.LastDeviceName, "Expected the hidden last device name to be retained.");
        AssertEquals(@"\Registry\Machine\System\TestPrimary", hiddenPrimary.LastRegistryKey,
            "Expected the hidden registry identity to be retained.");
        AssertEquals("HIDDEN-PRIMARY", hiddenPrimary.LastSerialNumber, "Expected the hidden serial to be retained.");
        AssertEquals(@"DISPLAY\TEST\PRIMARY", hiddenPrimary.LastInstanceId, "Expected the hidden instance ID to be retained.");
        AssertEquals(@"MONITOR\TESTPRIMARY", hiddenPrimary.LastMonitorId, "Expected the hidden monitor ID to be retained.");
        AssertEquals<int?>(-2560, hiddenPrimary.LastKnownX, "Expected the hidden position hint to be retained.");
        AssertSequence(hiddenPrimary.KnownTargets, @"\\.\DISPLAY7", "Disconnected Primary");
        AssertTrue(hiddenPrimary.IsPreferredPrimary,
            "Expected a hidden preferred-primary selection to survive saving unrelated visible rows.");
        AssertTrue(persisted["SN:HIDDEN-FALLBACK"].IsFallbackPrimary,
            "Expected a hidden fallback-primary selection to survive saving unrelated visible rows.");
    }
    finally
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }
}

static void AliasSettingsMapperClearsFallbackWhenItMatchesPreferredPrimary()
{
    var aliases = new Dictionary<string, MonitorInfo>(StringComparer.OrdinalIgnoreCase)
    {
        ["SN:LEFT"] = new MonitorInfo { Name = "Left Monitor" }
    };

    AliasSettingsMapper.ApplyMonitorSettings(
        aliases,
        Array.Empty<string>(),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        preferredPrimaryKey: "SN:LEFT",
        fallbackPrimaryKey: "SN:LEFT");

    AssertTrue(aliases["SN:LEFT"].IsPreferredPrimary, "Expected preferred primary to be set.");
    AssertFalse(aliases["SN:LEFT"].IsFallbackPrimary, "Expected same-key fallback primary to be cleared.");
}

static void MonitorOrderServicePersistsArbitraryVisibleOrder()
{
    var aliases = Enumerable.Range(0, 12).ToDictionary(
        index => $"SN:{index:00}",
        index => new MonitorInfo { Name = $"Monitor {index:00}", PreferredOrder = index },
        StringComparer.OrdinalIgnoreCase);
    var requested = aliases.Keys.Reverse().ToList();

    AssertTrue(
        MonitorOrderService.TryApplyVisibleOrder(aliases, requested, out var error),
        $"Expected arbitrary monitor order to be accepted: {error}");

    var persistedOrder = aliases
        .OrderBy(pair => pair.Value.PreferredOrder)
        .Select(pair => pair.Key)
        .ToList();
    AssertSequence(persistedOrder, requested.ToArray());
}

static void MonitorOrderServicePreservesHiddenPositions()
{
    var aliases = new Dictionary<string, MonitorInfo>(StringComparer.OrdinalIgnoreCase)
    {
        ["SN:A"] = new MonitorInfo { PreferredOrder = 0 },
        ["SN:HIDDEN"] = new MonitorInfo { PreferredOrder = 1 },
        ["SN:C"] = new MonitorInfo { PreferredOrder = 2 },
        ["SN:D"] = new MonitorInfo { PreferredOrder = 3 }
    };

    AssertTrue(
        MonitorOrderService.TryApplyVisibleOrder(aliases, new[] { "SN:D", "SN:A", "SN:C" }, out var error),
        $"Expected visible order to be accepted: {error}");
    AssertEquals(0, aliases["SN:D"].PreferredOrder, "Expected first visible monitor to occupy the first visible slot.");
    AssertEquals(1, aliases["SN:HIDDEN"].PreferredOrder, "Expected hidden monitor to retain its relative slot.");
    AssertEquals(2, aliases["SN:A"].PreferredOrder, "Expected second visible monitor to occupy the next visible slot.");
    AssertEquals(3, aliases["SN:C"].PreferredOrder, "Expected third visible monitor to occupy the final visible slot.");

    var before = aliases.ToDictionary(pair => pair.Key, pair => pair.Value.PreferredOrder);
    AssertFalse(
        MonitorOrderService.TryApplyVisibleOrder(aliases, new[] { "SN:A", "SN:A" }, out _),
        "Expected duplicate physical identities to be rejected.");
    AssertFalse(
        MonitorOrderService.TryApplyVisibleOrder(aliases, new[] { "SN:UNKNOWN" }, out _),
        "Expected unknown physical identities to be rejected.");
    foreach (var pair in before)
        AssertEquals(pair.Value, aliases[pair.Key].PreferredOrder, "Expected an invalid reorder not to mutate saved ordering.");
}

static void PrimaryMonitorPreferenceResolvesConfiguredTargets()
{
    var aliases = new Dictionary<string, MonitorInfo>(StringComparer.OrdinalIgnoreCase)
    {
        ["SN:LEFT"] = new MonitorInfo { IsFallbackPrimary = true },
        ["SN:RIGHT"] = new MonitorInfo { IsPreferredPrimary = true }
    };

    var detected = new List<DetectedMonitor>
    {
        new DetectedMonitor
        {
            StableKey = "SN:LEFT",
            DeviceName = "\\\\.\\DISPLAY1",
            Name = "Left",
            IsPresent = true,
            IsActive = true,
            PositionX = -2560
        },
        new DetectedMonitor
        {
            StableKey = "SN:RIGHT",
            DeviceName = "\\\\.\\DISPLAY3",
            Name = "Right",
            IsPresent = true,
            IsActive = true,
            PositionX = 2560
        }
    };

    var preferred = PrimaryMonitorPreference.ResolvePreferredPrimaryTarget(detected, aliases);
    var fallback = PrimaryMonitorPreference.ResolveConfiguredFallbackDeviceName("\\\\.\\DISPLAY3", detected, aliases);
    var excludedFallback = PrimaryMonitorPreference.ResolveConfiguredFallbackDeviceName("\\\\.\\DISPLAY1", detected, aliases);
    var automatic = PrimaryMonitorPreference.ResolveLeftMostActiveTarget(detected, aliases);

    AssertEquals("\\\\.\\DISPLAY3", preferred, "Expected active preferred primary target to resolve.");
    AssertEquals("\\\\.\\DISPLAY1", fallback, "Expected configured fallback device to resolve.");
    AssertEquals<string?>(null, excludedFallback, "Expected excluded fallback device to be ignored.");
    AssertEquals("\\\\.\\DISPLAY1", automatic, "Expected left-most active monitor to be automatic fallback.");
}

static void AtomicFileWriterReplacesExistingFilesAndKeepsBackup()
{
    var dir = Path.Combine(Path.GetTempPath(), "MonitorSwitcher.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);

    try
    {
        var path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, "old");

        AtomicFileWriter.WriteAllText(path, "new");

        AssertEquals("new", File.ReadAllText(path), "Expected target file to contain replacement content.");
        AssertTrue(File.Exists(path + ".bak"), "Expected a backup file to be created for replaced content.");
        AssertEquals("old", File.ReadAllText(path + ".bak"), "Expected backup file to contain previous content.");
    }
    finally
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }
}

static void JsonSettingsRecoverCorruptPrimaryFromBackup()
{
    var dir = Path.Combine(Path.GetTempPath(), "MonitorSwitcher.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);

    try
    {
        var store = new UiSettingsStore(dir);
        var expected = new UiSettings
        {
            DarkMode = true,
            ConfirmBeforeDisable = false,
            SelectedLayoutProfile = "Desk"
        };
        var initialSave = store.SaveWithResult(expected);
        AssertTrue(initialSave.Success, "Expected initial settings save to succeed.");

        var primaryPath = store.SettingsPath;
        var backupPath = AtomicFileWriter.GetBackupPath(primaryPath);
        File.Copy(primaryPath, backupPath);
        var validBackup = File.ReadAllText(backupPath);
        File.WriteAllText(primaryPath, "{ this is not valid JSON");

        var recovered = store.LoadOrDefault();

        AssertTrue(recovered.DarkMode, "Expected settings to load from the valid backup.");
        AssertFalse(recovered.ConfirmBeforeDisable, "Expected recovered Boolean settings to be preserved.");
        AssertEquals("Desk", recovered.SelectedLayoutProfile, "Expected recovered profile selection.");
        AssertEquals(validBackup, File.ReadAllText(primaryPath), "Expected the corrupt primary to be repaired.");
        AssertEquals(validBackup, File.ReadAllText(backupPath), "Expected recovery not to replace the good backup.");

        var noOpSave = store.SaveWithResult(recovered);
        AssertTrue(noOpSave.Success, "Expected repeat save to succeed.");
        AssertFalse(noOpSave.Changed, "Expected an unchanged save not to rewrite the file.");
    }
    finally
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }
}

static void JsonSettingsRecoverMissingPrimaryFromBackup()
{
    var dir = Path.Combine(Path.GetTempPath(), "MonitorSwitcher.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);

    try
    {
        var store = new UiSettingsStore(dir);
        AssertTrue(store.SaveWithResult(new UiSettings { DarkMode = true }).Success,
            "Expected initial settings save.");
        var primaryPath = store.SettingsPath;
        var backupPath = AtomicFileWriter.GetBackupPath(primaryPath);
        File.Move(primaryPath, backupPath);

        var recovered = store.LoadOrDefault();

        AssertTrue(recovered.DarkMode, "Expected settings to load from the backup.");
        AssertTrue(File.Exists(primaryPath), "Expected the missing primary to be repaired.");
        AssertTrue(File.Exists(backupPath), "Expected the recovery backup to be preserved.");
    }
    finally
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }
}

static void JsonSaveReportsFilesystemFailure()
{
    var dir = Path.Combine(Path.GetTempPath(), "MonitorSwitcher.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);

    try
    {
        var blockerPath = Path.Combine(dir, "not-a-directory");
        File.WriteAllText(blockerPath, "block directory creation");
        var store = new UiSettingsStore(Path.Combine(blockerPath, "child"));

        var result = store.SaveWithResult(new UiSettings());

        AssertFalse(result.Success, "Expected an impossible settings path to fail explicitly.");
        AssertTrue(!string.IsNullOrWhiteSpace(result.ErrorMessage),
            "Expected the save failure to include a useful error message.");
    }
    finally
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }
}

static void JsonSavePreservesValidBackupBehindCorruptPrimary()
{
    var dir = Path.Combine(Path.GetTempPath(), "MonitorSwitcher.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);

    try
    {
        var path = Path.Combine(dir, "settings.json");
        var backupPath = AtomicFileWriter.GetBackupPath(path);
        File.WriteAllText(path, "{ broken primary");
        File.WriteAllText(backupPath, "{\"DarkMode\":false}");
        var expectedBackup = File.ReadAllText(backupPath);

        var result = JsonFilePersistence.Save(
            path,
            new UiSettings { DarkMode = true },
            value => value != null);

        AssertTrue(result.Success, "Expected the replacement JSON save to succeed.");
        AssertEquals(expectedBackup, File.ReadAllText(backupPath),
            "Expected the existing valid backup not to be replaced by the corrupt primary.");
        var loaded = JsonFilePersistence.Load<UiSettings>(path, value => value != null);
        AssertTrue(loaded.Success && loaded.Value?.DarkMode == true,
            "Expected the new primary JSON to be valid and readable.");
    }
    finally
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }
}

static void DiagnosticsLogSerialisesConcurrentWriters()
{
    var dir = Path.Combine(Path.GetTempPath(), "MonitorSwitcher.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);

    try
    {
        var log = new DiagnosticsLog(dir);
        Parallel.For(0, 120, index => log.Write($"concurrent-entry-{index:D3}"));

        var contents = log.Read();
        var lines = contents.Split(
            new[] { Environment.NewLine },
            StringSplitOptions.RemoveEmptyEntries);
        AssertEquals(120, lines.Length, "Expected every concurrent diagnostic entry to be retained.");
        for (int index = 0; index < 120; index++)
        {
            AssertTrue(contents.Contains($"concurrent-entry-{index:D3}", StringComparison.Ordinal),
                $"Expected diagnostic entry {index} to be present.");
        }
    }
    finally
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }
}

static void LayoutIdentityRejectsIndependentBackupRecovery()
{
    var dir = Path.Combine(Path.GetTempPath(), "MonitorSwitcher.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);

    try
    {
        var layoutPath = Path.Combine(dir, "Home.cfg");
        var identityPath = LayoutIdentityStore.GetIdentityPath(layoutPath);
        var populated = new LayoutIdentityFile
        {
            Version = 1,
            Monitors = new List<SavedLayoutIdentity>
            {
                new()
                {
                    LayoutDeviceName = @"\\.\DISPLAY1",
                    StableKey = "SN:RECOVERED",
                    SerialNumber = "RECOVERED"
                }
            }
        };
        AssertTrue(JsonFilePersistence.Save(identityPath, populated).Success,
            "Expected populated identity primary save.");
        AssertTrue(JsonFilePersistence.Save(
                identityPath,
                new LayoutIdentityFile
                {
                    Version = 1,
                    Monitors = new List<SavedLayoutIdentity>()
                }).Success,
            "Expected test setup to replace the primary with an empty identity file.");

        var loaded = LayoutIdentityStore.Load(layoutPath);

        AssertEquals(0, loaded.Count,
            "Expected an older identity-only backup not to be combined with the current layout.");
        AssertTrue(File.Exists(AtomicFileWriter.GetBackupPath(identityPath)),
            "Expected fail-closed loading not to delete the older sidecar backup.");
    }
    finally
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }
}

static void LayoutProfileTransactionCommitsMatchedPair()
{
    var dir = Path.Combine(Path.GetTempPath(), "MonitorSwitcher.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);

    try
    {
        var layoutPath = Path.Combine(dir, "Home.cfg");
        var oldLayout = ValidLayoutText(positionX: 10);
        var newLayout = ValidLayoutText(positionX: 20);
        File.WriteAllText(layoutPath, oldLayout);
        SaveIdentity(layoutPath, "OLD");
        var oldIdentity = File.ReadAllText(LayoutIdentityStore.GetIdentityPath(layoutPath));

        var stagedPath = LayoutProfileTransaction.CreateStagingLayoutPath(layoutPath);
        File.WriteAllText(stagedPath, newLayout);
        SaveIdentity(stagedPath, "NEW");

        var result = LayoutProfileTransaction.Commit(stagedPath, layoutPath);

        AssertTrue(result.Success, $"Expected the matched profile commit to succeed: {result.ErrorMessage}");
        AssertEquals(newLayout, File.ReadAllText(layoutPath), "Expected the new layout to be promoted.");
        AssertEquals("NEW", LayoutIdentityStore.Load(layoutPath).Single().SerialNumber,
            "Expected the matching new identity map to be promoted.");
        AssertEquals(oldLayout, File.ReadAllText(AtomicFileWriter.GetBackupPath(layoutPath)),
            "Expected the old layout to remain as the rollback backup.");
        AssertEquals(oldIdentity, File.ReadAllText(
                AtomicFileWriter.GetBackupPath(LayoutIdentityStore.GetIdentityPath(layoutPath))),
            "Expected the identity rollback backup to match the old layout.");
        AssertFalse(File.Exists(stagedPath), "Expected the staged layout to be consumed.");
        AssertFalse(File.Exists(LayoutIdentityStore.GetIdentityPath(stagedPath)),
            "Expected the staged identity map to be consumed.");
    }
    finally
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }
}

static void LayoutProfileTransactionRollsBackMatchedPair()
{
    var dir = Path.Combine(Path.GetTempPath(), "MonitorSwitcher.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);

    try
    {
        var layoutPath = Path.Combine(dir, "Home.cfg");
        var identityPath = LayoutIdentityStore.GetIdentityPath(layoutPath);
        var oldLayout = ValidLayoutText(positionX: 10);
        File.WriteAllText(layoutPath, oldLayout);
        SaveIdentity(layoutPath, "OLD");
        var oldIdentity = File.ReadAllText(identityPath);

        var stagedPath = LayoutProfileTransaction.CreateStagingLayoutPath(layoutPath);
        File.WriteAllText(stagedPath, ValidLayoutText(positionX: 20));
        SaveIdentity(stagedPath, "NEW");

        PersistenceResult result;
        using (var identityLock = new FileStream(identityPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            result = LayoutProfileTransaction.Commit(stagedPath, layoutPath);

        AssertFalse(result.Success, "Expected the locked identity map to fail the paired commit.");
        AssertEquals(oldLayout, File.ReadAllText(layoutPath),
            "Expected the old layout to be restored after the mid-commit failure.");
        AssertEquals(oldIdentity, File.ReadAllText(identityPath),
            "Expected the old identity map to remain unchanged after rollback.");
        AssertFalse(Directory.EnumerateFiles(dir, "*.profile-old").Any(),
            "Expected rollback not to leave transaction stashes behind.");

        LayoutProfileTransaction.DeleteStagingArtifacts(stagedPath);
    }
    finally
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }
}

static void LayoutProfileTransactionRejectsMissingIdentity()
{
    var dir = Path.Combine(Path.GetTempPath(), "MonitorSwitcher.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);

    try
    {
        var layoutPath = Path.Combine(dir, "Home.cfg");
        var oldLayout = ValidLayoutText(positionX: 10);
        File.WriteAllText(layoutPath, oldLayout);
        SaveIdentity(layoutPath, "OLD");

        var stagedPath = LayoutProfileTransaction.CreateStagingLayoutPath(layoutPath);
        File.WriteAllText(stagedPath, ValidLayoutText(positionX: 20));

        var result = LayoutProfileTransaction.Commit(stagedPath, layoutPath);

        AssertFalse(result.Success, "Expected a staged cfg without its identity map to be rejected.");
        AssertEquals(oldLayout, File.ReadAllText(layoutPath),
            "Expected rejection not to change the existing layout.");
        AssertEquals("OLD", LayoutIdentityStore.Load(layoutPath).Single().SerialNumber,
            "Expected rejection not to change the existing identity map.");

        LayoutProfileTransaction.DeleteStagingArtifacts(stagedPath);
    }
    finally
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }
}

static void LayoutProfileTransactionRejectsMismatchedIdentityMaps()
{
    var dir = Path.Combine(Path.GetTempPath(), "MonitorSwitcher.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);

    try
    {
        var layoutPath = Path.Combine(dir, "Home.cfg");
        File.WriteAllText(layoutPath, ValidLayoutText(positionX: 10));
        SaveIdentity(layoutPath, "OLD");

        var stagedPath = LayoutProfileTransaction.CreateStagingLayoutPath(layoutPath);
        File.WriteAllText(stagedPath, ValidLayoutText(positionX: 20));
        SaveIdentity(stagedPath, "NEW", @"\\.\DISPLAY2");

        var mismatchResult = LayoutProfileTransaction.Commit(stagedPath, layoutPath);
        AssertFalse(mismatchResult.Success,
            "Expected an identity map naming a different display to be rejected.");

        LayoutProfileTransaction.DeleteStagingArtifacts(stagedPath);
        stagedPath = LayoutProfileTransaction.CreateStagingLayoutPath(layoutPath);
        File.WriteAllText(stagedPath, ValidLayoutText(positionX: 20));
        var identityPath = LayoutIdentityStore.GetIdentityPath(stagedPath);
        var duplicateIdentityFile = new LayoutIdentityFile
        {
            Version = 1,
            Monitors = new List<SavedLayoutIdentity>
            {
                new() { LayoutDeviceName = @"\\.\DISPLAY1", StableKey = "SN:A", SerialNumber = "A" },
                new() { LayoutDeviceName = @"\\.\DISPLAY1", StableKey = "SN:B", SerialNumber = "B" }
            }
        };
        AssertTrue(JsonFilePersistence.Save(identityPath, duplicateIdentityFile).Success,
            "Expected duplicate identity JSON fixture write.");

        var duplicateResult = LayoutProfileTransaction.Commit(stagedPath, layoutPath);
        AssertFalse(duplicateResult.Success, "Expected duplicate layout-device identities to be rejected.");
        AssertEquals("OLD", LayoutIdentityStore.Load(layoutPath).Single().SerialNumber,
            "Expected rejected staged pairs not to change the current profile.");

        LayoutProfileTransaction.DeleteStagingArtifacts(stagedPath);
    }
    finally
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }
}

static void LayoutProfileTransactionValidatesNativeTargetPair()
{
    var dir = Path.Combine(Path.GetTempPath(), "MonitorSwitcher.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);

    try
    {
        var layoutPath = Path.Combine(dir, "Native.cfg");
        var identityPath = LayoutIdentityStore.GetIdentityPath(layoutPath);
        var savedTargetPath = NativeTargetPath("SAVED");
        var otherTargetPath = NativeTargetPath("OTHER");
        var profile = new NativeDisplayProfile(
            NativeDisplayProfileCodec.CurrentVersion,
            new[]
            {
                new NativeDisplayProfileMonitor(
                    @"\\.\DISPLAY1",
                    savedTargetPath,
                    1,
                    0,
                    1,
                    5,
                    0,
                    0,
                    1920,
                    1080,
                    1,
                    true,
                    "Test display",
                    1,
                    2,
                    0)
            });
        File.WriteAllText(layoutPath, NativeDisplayProfileCodec.Serialise(profile));

        var matchingIdentity = new LayoutIdentityFile
        {
            Version = 2,
            Monitors = new List<SavedLayoutIdentity>
            {
                new()
                {
                    LayoutDeviceName = @"\\.\DISPLAY1",
                    StableKey = "SN:NATIVE",
                    SerialNumber = "NATIVE",
                    NativeTargetPath = savedTargetPath
                }
            }
        };
        AssertTrue(JsonFilePersistence.Save(identityPath, matchingIdentity).Success,
            "Expected matching v2 native identity fixture save.");
        AssertTrue(
            LayoutProfileTransaction.IsValidProfilePair(layoutPath, identityPath, out var matchingError),
            $"Expected the native profile and its captured target path to validate: {matchingError}");

        matchingIdentity.Monitors[0].NativeTargetPath = otherTargetPath;
        AssertTrue(JsonFilePersistence.Save(identityPath, matchingIdentity).Success,
            "Expected structurally valid mismatched v2 identity fixture save.");
        AssertFalse(
            LayoutProfileTransaction.IsValidProfilePair(layoutPath, identityPath, out var mismatchError),
            "Expected a native profile paired with another physical target to be rejected.");
        AssertTrue(mismatchError.Contains("target paths", StringComparison.OrdinalIgnoreCase), mismatchError);
    }
    finally
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }
}

static void LayoutProfileTransactionPreservesValidBackupPair()
{
    var dir = Path.Combine(Path.GetTempPath(), "MonitorSwitcher.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);

    try
    {
        var layoutPath = Path.Combine(dir, "Home.cfg");
        var identityPath = LayoutIdentityStore.GetIdentityPath(layoutPath);
        var backupLayoutPath = AtomicFileWriter.GetBackupPath(layoutPath);
        var backupIdentityPath = AtomicFileWriter.GetBackupPath(identityPath);
        File.WriteAllText(layoutPath, "corrupt current layout");
        File.WriteAllText(backupLayoutPath, ValidLayoutText(positionX: 10));
        var backupIdentityFile = new LayoutIdentityFile
        {
            Version = 1,
            Monitors = new List<SavedLayoutIdentity>
            {
                new()
                {
                    LayoutDeviceName = @"\\.\DISPLAY1",
                    StableKey = "SN:GOOD-BACKUP",
                    SerialNumber = "GOOD-BACKUP"
                }
            }
        };
        AssertTrue(JsonFilePersistence.Save(backupIdentityPath, backupIdentityFile).Success,
            "Expected the valid rollback identity fixture.");

        var stagedPath = LayoutProfileTransaction.CreateStagingLayoutPath(layoutPath);
        File.WriteAllText(stagedPath, ValidLayoutText(positionX: 20));
        SaveIdentity(stagedPath, "NEW");

        var result = LayoutProfileTransaction.Commit(stagedPath, layoutPath);

        AssertTrue(result.Success, $"Expected save over corrupt primary to succeed: {result.ErrorMessage}");
        AssertEquals("NEW", LayoutIdentityStore.Load(layoutPath).Single().SerialNumber,
            "Expected the new complete profile pair.");
        AssertEquals(10, ReadLayoutFixtureRevision(backupLayoutPath),
            "Expected the last valid backup layout to remain the rollback point.");
        AssertTrue(LayoutIdentityStore.TryLoadPrimaryIdentityFile(backupIdentityPath, out var backupIdentities),
            "Expected the rollback sidecar to remain valid.");
        AssertEquals("GOOD-BACKUP", backupIdentities.Single().SerialNumber,
            "Expected the last valid backup identity map to remain paired with it.");
    }
    finally
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }
}

static void LayoutProfileTransactionRecoversPartialStash()
{
    var dir = Path.Combine(Path.GetTempPath(), "MonitorSwitcher.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);

    try
    {
        var layoutPath = Path.Combine(dir, "Home.cfg");
        var identityPath = LayoutIdentityStore.GetIdentityPath(layoutPath);
        var originalLayout = ValidLayoutText(positionX: 10);
        File.WriteAllText(layoutPath, originalLayout);
        SaveIdentity(layoutPath, "OLD");
        var originalIdentity = File.ReadAllText(identityPath);

        var operationId = Guid.NewGuid().ToString("N");
        var layoutStash = Path.Combine(dir, $".{Path.GetFileName(layoutPath)}.{operationId}.profile-old");
        var identityStash = Path.Combine(dir, $".{Path.GetFileName(identityPath)}.{operationId}.profile-old");
        var backupLayout = AtomicFileWriter.GetBackupPath(layoutPath);
        var backupIdentity = AtomicFileWriter.GetBackupPath(identityPath);
        var journal = new LayoutProfileTransactionJournal
        {
            Phase = LayoutProfileTransactionPhase.Prepared,
            TargetLayoutPath = layoutPath,
            TargetIdentityPath = identityPath,
            BackupLayoutPath = backupLayout,
            BackupIdentityPath = backupIdentity,
            Entries = new List<LayoutProfileTransactionEntry>
            {
                new() { TargetPath = layoutPath, StashPath = layoutStash, OriginalExisted = true },
                new() { TargetPath = identityPath, StashPath = identityStash, OriginalExisted = true },
                new() { TargetPath = backupLayout, StashPath = Path.Combine(dir, $".{Path.GetFileName(backupLayout)}.{operationId}.profile-old") },
                new() { TargetPath = backupIdentity, StashPath = Path.Combine(dir, $".{Path.GetFileName(backupIdentity)}.{operationId}.profile-old") }
            }
        };
        AssertTrue(JsonFilePersistence.Save(LayoutProfileTransaction.GetJournalPath(layoutPath), journal).Success,
            "Expected interrupted transaction journal fixture.");
        File.Move(layoutPath, layoutStash);

        var result = LayoutProfileTransaction.Recover(layoutPath);

        AssertTrue(result.Success, $"Expected partial-stash recovery: {result.ErrorMessage}");
        AssertEquals(originalLayout, File.ReadAllText(layoutPath), "Expected the stashed layout to be restored.");
        AssertEquals(originalIdentity, File.ReadAllText(identityPath),
            "Expected the untouched identity original never to be deleted.");
        AssertFalse(File.Exists(LayoutProfileTransaction.GetJournalPath(layoutPath)),
            "Expected successful recovery to remove the journal.");
    }
    finally
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }
}

static void LayoutProfileTransactionRejectsMissingPromisedStash()
{
    var dir = Path.Combine(Path.GetTempPath(), "MonitorSwitcher.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);

    try
    {
        var layoutPath = Path.Combine(dir, "Home.cfg");
        var identityPath = LayoutIdentityStore.GetIdentityPath(layoutPath);
        var backupLayout = AtomicFileWriter.GetBackupPath(layoutPath);
        var backupIdentity = AtomicFileWriter.GetBackupPath(identityPath);
        File.WriteAllText(layoutPath, ValidLayoutText(positionX: 10));
        SaveIdentity(layoutPath, "OLD");

        var operationId = Guid.NewGuid().ToString("N");
        string Stash(string target) =>
            Path.Combine(dir, $".{Path.GetFileName(target)}.{operationId}.profile-old");
        var journal = new LayoutProfileTransactionJournal
        {
            Phase = LayoutProfileTransactionPhase.Stashed,
            TargetLayoutPath = layoutPath,
            TargetIdentityPath = identityPath,
            BackupLayoutPath = backupLayout,
            BackupIdentityPath = backupIdentity,
            Entries = new List<LayoutProfileTransactionEntry>
            {
                new() { TargetPath = layoutPath, StashPath = Stash(layoutPath), OriginalExisted = true },
                new() { TargetPath = identityPath, StashPath = Stash(identityPath), OriginalExisted = true },
                new() { TargetPath = backupLayout, StashPath = Stash(backupLayout) },
                new() { TargetPath = backupIdentity, StashPath = Stash(backupIdentity) }
            }
        };
        AssertTrue(JsonFilePersistence.Save(LayoutProfileTransaction.GetJournalPath(layoutPath), journal).Success,
            "Expected missing-stash journal fixture.");

        var result = LayoutProfileTransaction.Recover(layoutPath);

        AssertFalse(result.Success,
            "Expected recovery to reject a Stashed phase that cannot prove every original is recoverable.");
        AssertTrue(File.Exists(layoutPath), "Expected ambiguous recovery not to delete the current layout.");
        AssertTrue(File.Exists(identityPath), "Expected ambiguous recovery not to delete the current identity map.");
        AssertTrue(File.Exists(LayoutProfileTransaction.GetJournalPath(layoutPath)),
            "Expected failed recovery to retain its journal for diagnosis or later repair.");
    }
    finally
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }
}

static void LayoutProfileTransactionRejectsMissingPreparedOriginal()
{
    var dir = Path.Combine(Path.GetTempPath(), "MonitorSwitcher.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);

    try
    {
        var layoutPath = Path.Combine(dir, "Home.cfg");
        var identityPath = LayoutIdentityStore.GetIdentityPath(layoutPath);
        var backupLayout = AtomicFileWriter.GetBackupPath(layoutPath);
        var backupIdentity = AtomicFileWriter.GetBackupPath(identityPath);
        SaveIdentity(layoutPath, "OLD");

        var operationId = Guid.NewGuid().ToString("N");
        string Stash(string target) =>
            Path.Combine(dir, $".{Path.GetFileName(target)}.{operationId}.profile-old");
        var journal = new LayoutProfileTransactionJournal
        {
            Phase = LayoutProfileTransactionPhase.Prepared,
            TargetLayoutPath = layoutPath,
            TargetIdentityPath = identityPath,
            BackupLayoutPath = backupLayout,
            BackupIdentityPath = backupIdentity,
            Entries = new List<LayoutProfileTransactionEntry>
            {
                new() { TargetPath = layoutPath, StashPath = Stash(layoutPath), OriginalExisted = true },
                new() { TargetPath = identityPath, StashPath = Stash(identityPath), OriginalExisted = true },
                new() { TargetPath = backupLayout, StashPath = Stash(backupLayout) },
                new() { TargetPath = backupIdentity, StashPath = Stash(backupIdentity) }
            }
        };
        AssertTrue(JsonFilePersistence.Save(LayoutProfileTransaction.GetJournalPath(layoutPath), journal).Success,
            "Expected prepared-phase missing-original journal fixture.");

        var result = LayoutProfileTransaction.Recover(layoutPath);

        AssertFalse(result.Success,
            "Expected recovery to fail when an original and its prepared-phase stash are both missing.");
        AssertTrue(File.Exists(identityPath), "Expected the untouched identity original to remain in place.");
        AssertTrue(File.Exists(LayoutProfileTransaction.GetJournalPath(layoutPath)),
            "Expected failed prepared-phase recovery to retain its journal.");
    }
    finally
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }
}

static void DisplayTopologyPersistentApplyFlagsSaveToDatabase()
{
    const uint sdcUseSuppliedDisplayConfig = 0x00000020;
    const uint sdcApply = 0x00000080;
    const uint sdcSaveToDatabase = 0x00000200;
    var flags = DisplayTopologyService.GetPersistentApplyFlags();

    AssertTrue((flags & sdcUseSuppliedDisplayConfig) != 0, "Expected supplied CCD paths to be used.");
    AssertTrue((flags & sdcApply) != 0, "Expected the validated topology to be applied.");
    AssertTrue((flags & sdcSaveToDatabase) != 0, "Expected the topology to be persisted.");
}

static void DisplayTopologySavedLayoutCheckFailsClosed()
{
    var check = new DisplayTopologyService().CheckSavedLayoutApplied(
        Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.cfg"),
        Array.Empty<DetectedMonitor>(),
        Array.Empty<SavedLayoutIdentity>());

    AssertEquals(SavedLayoutAppliedState.Inconclusive, check.State,
        "Expected a missing or unverifiable profile never to be classified as not applied.");
    AssertTrue(!string.IsNullOrWhiteSpace(check.Message),
        "Expected the fail-closed result to explain why comparison was inconclusive.");
}

static int ReadLayoutFixtureRevision(string layoutPath)
{
    var line = File.ReadLines(layoutPath)
        .Single(value => value.StartsWith("TestRevision=", StringComparison.OrdinalIgnoreCase));
    return int.Parse(line["TestRevision=".Length..], System.Globalization.CultureInfo.InvariantCulture);
}

static void SaveIdentity(
    string layoutPath,
    string serialNumber,
    string layoutDeviceName = @"\\.\DISPLAY1")
{
    var result = JsonFilePersistence.Save(
        LayoutIdentityStore.GetIdentityPath(layoutPath),
        new LayoutIdentityFile
        {
            Version = 1,
            Monitors = new List<SavedLayoutIdentity>
            {
                new()
                {
                    LayoutDeviceName = layoutDeviceName,
                    StableKey = $"SN:{serialNumber}",
                    SerialNumber = serialNumber
                }
            }
        });
    AssertTrue(result.Success, $"Expected identity fixture save to succeed: {result.ErrorMessage}");
}

static string NativeTargetPath(string suffix)
    => $@"\\?\DISPLAY#TEST{suffix}#INSTANCE{suffix}#{{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}}";

static void LayoutProfileDeletionIsExact()
{
    var dir = Path.Combine(Path.GetTempPath(), "MonitorSwitcher.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);

    try
    {
        var store = new LayoutProfileStore(dir, Path.Combine(dir, "monitor-layout.cfg"));
        AssertTrue(store.AddProfileNameWithResult("Home").Success, "Expected profile index save to succeed.");
        var layoutPath = store.GetLayoutPath("Home");
        var identityPath = LayoutIdentityStore.GetIdentityPath(layoutPath);
        var autoSavePath = layoutPath + ".autosave-20260810-120000.bak";
        var autoSaveIdentityPath = LayoutIdentityStore.GetIdentityPath(autoSavePath);
        var exactArtefacts = new[]
        {
            layoutPath,
            AtomicFileWriter.GetBackupPath(layoutPath),
            identityPath,
            AtomicFileWriter.GetBackupPath(identityPath),
            autoSavePath,
            autoSaveIdentityPath,
            AtomicFileWriter.GetBackupPath(autoSaveIdentityPath)
        };
        foreach (var artefact in exactArtefacts)
            File.WriteAllText(artefact, "test");

        var similarLayout = Path.Combine(Path.GetDirectoryName(layoutPath)!, "Home 2.cfg");
        var malformedBackup = layoutPath + ".autosave-manual.bak";
        File.WriteAllText(similarLayout, "keep");
        File.WriteAllText(malformedBackup, "keep");

        var result = store.DeleteProfileWithResult("Home");

        AssertTrue(result.Success, "Expected exact profile deletion to succeed.");
        AssertFalse(exactArtefacts.Any(File.Exists), "Expected every exact profile artefact to be removed.");
        AssertTrue(File.Exists(similarLayout), "Expected a similarly named profile to be preserved.");
        AssertTrue(File.Exists(malformedBackup), "Expected a non-timestamped backup to be preserved.");

        var indexPath = Path.Combine(dir, "layouts", "profiles.json");
        File.WriteAllText(indexPath, "{ corrupt index");
        var recoveredNames = store.LoadProfileNames();
        AssertFalse(recoveredNames.Any(name => name.Equals("Home", StringComparison.OrdinalIgnoreCase)),
            "Expected backup recovery not to resurrect a deleted profile.");
        AssertContains(recoveredNames, "Home 2");
    }
    finally
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }
}

static void LayoutProfileDeletionRollsBackOnFailure()
{
    var dir = Path.Combine(Path.GetTempPath(), "MonitorSwitcher.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);

    try
    {
        var store = new LayoutProfileStore(dir, Path.Combine(dir, "monitor-layout.cfg"));
        AssertTrue(store.AddProfileNameWithResult("Home").Success, "Expected profile index save to succeed.");
        var layoutPath = store.GetLayoutPath("Home");
        var identityPath = LayoutIdentityStore.GetIdentityPath(layoutPath);
        var layoutBackupPath = AtomicFileWriter.GetBackupPath(layoutPath);
        var artefacts = new[] { identityPath, layoutBackupPath, layoutPath };
        foreach (var artefact in artefacts)
            File.WriteAllText(artefact, "keep");

        PersistenceResult result;
        using (var backupLock = new FileStream(layoutBackupPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            result = store.DeleteProfileWithResult("Home");

        AssertFalse(result.Success, "Expected a locked artefact to fail deletion staging.");
        AssertTrue(artefacts.All(File.Exists),
            "Expected every profile artefact to be restored after a mid-staging failure.");
        AssertContains(store.LoadProfileNames(), "Home");
        AssertFalse(Directory.EnumerateFiles(Path.GetDirectoryName(layoutPath)!, "*.deleting").Any(),
            "Expected no staged deletion files after rollback.");
    }
    finally
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }
}

static void StartupCommandMatchingIsExact()
{
    var executablePath = Path.Combine(Path.GetTempPath(), "Monitor Switcher", "MonitorSwitcher.exe");

    AssertTrue(StartupManager.IsCommandForExecutable($"\"{executablePath}\"", executablePath),
        "Expected a canonical quoted path to match.");
    AssertTrue(StartupManager.IsCommandForExecutable(executablePath, executablePath),
        "Expected a legacy path-only value to match before canonical repair.");
    AssertTrue(StartupManager.IsCanonicalCommand($"\"{executablePath}\"", executablePath),
        "Expected production canonical matching to accept the quoted command.");
    AssertFalse(StartupManager.IsCanonicalCommand(executablePath, executablePath),
        "Expected production canonical matching to repair an unquoted legacy command.");
    AssertFalse(StartupManager.IsCommandForExecutable($"\"{executablePath}\" --hidden", executablePath),
        "Expected startup arguments to be rejected.");
    AssertFalse(StartupManager.IsCommandForExecutable(
            $"\"{Path.Combine(Path.GetDirectoryName(executablePath)!, "Other.exe")}\"",
            executablePath),
        "Expected another executable to be rejected.");
}

static void LayoutServiceRequiresCompleteActiveMonitorSection()
{
    var dir = Path.Combine(Path.GetTempPath(), "MonitorSwitcher.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);

    try
    {
        var splitFieldsPath = Path.Combine(dir, "split-fields.cfg");
        File.WriteAllText(splitFieldsPath, string.Join(Environment.NewLine, new[]
        {
            "[Monitor0]",
            @"Name=\\.\DISPLAY1",
            "[Monitor1]",
            "PositionX=0",
            "PositionY=0"
        }));
        AssertFalse(LayoutService.IsValidLayoutFile(splitFieldsPath),
            "Expected fields from separate monitor sections not to be combined.");

        var inactivePath = Path.Combine(dir, "inactive.cfg");
        File.WriteAllText(inactivePath, string.Join(Environment.NewLine, new[]
        {
            "[Monitor0]",
            @"Name=\\.\DISPLAY1",
            "BitsPerPixel=0",
            "Width=0",
            "Height=0",
            "PositionX=0",
            "PositionY=0"
        }));
        AssertFalse(LayoutService.IsValidLayoutFile(inactivePath),
            "Expected an explicitly inactive-only layout not to be accepted.");

        var truncatedSecondMonitorPath = Path.Combine(dir, "truncated-second-monitor.cfg");
        File.WriteAllText(truncatedSecondMonitorPath, ValidLayoutText(positionX: 0) + Environment.NewLine +
            "[Monitor1]" + Environment.NewLine +
            @"Name=\\.\DISPLAY2" + Environment.NewLine +
            "PositionX=1920");
        AssertFalse(LayoutService.IsValidLayoutFile(truncatedSecondMonitorPath),
            "Expected a truncated later monitor section to invalidate the whole layout.");

        var activePath = Path.Combine(dir, "active.cfg");
        File.WriteAllText(activePath, ValidLayoutText(positionX: -1920));
        AssertTrue(LayoutService.IsValidLayoutFile(activePath),
            "Expected a complete active monitor section to be accepted.");
    }
    finally
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }
}

static string ValidLayoutText(int positionX)
{
    return string.Join(Environment.NewLine, new[]
    {
        "[Monitor0]",
        @"Name=\\.\DISPLAY1",
        "Width=1920",
        "Height=1080",
        "DisplayOrientation=0",
        "PositionX=0",
        "PositionY=0",
        "Primary=1",
        $"TestRevision={positionX}"
    });
}

static void ReconnectTrackerFirstObservationDoesNotTrigger()
{
    var tracker = new ReconnectLayoutRestoreTracker();

    AssertFalse(tracker.Observe("Home", exactSavedMonitorSetActive: true, allowTrigger: true),
        "Expected the first observation to establish a baseline without restoring.");
    AssertFalse(tracker.Observe("Home", exactSavedMonitorSetActive: true, allowTrigger: true),
        "Expected an unchanged active snapshot not to restore.");
}

static void ReconnectTrackerTriggersOncePerActivation()
{
    var tracker = new ReconnectLayoutRestoreTracker();

    AssertFalse(tracker.Observe("Home", exactSavedMonitorSetActive: false, allowTrigger: true),
        "Expected the first inactive observation not to restore.");
    AssertTrue(tracker.Observe("Home", exactSavedMonitorSetActive: true, allowTrigger: true),
        "Expected an inactive-to-active transition to restore.");
    AssertFalse(tracker.Observe("Home", exactSavedMonitorSetActive: true, allowTrigger: true),
        "Expected an unchanged active snapshot not to restore twice.");
    AssertFalse(tracker.Observe("Home", exactSavedMonitorSetActive: false, allowTrigger: true),
        "Expected an active-to-inactive transition not to restore.");
    AssertTrue(tracker.Observe("Home", exactSavedMonitorSetActive: true, allowTrigger: true),
        "Expected a later inactive-to-active transition to restore once again.");
}

static void ReconnectTrackerConsumesSuppressedTransition()
{
    var tracker = new ReconnectLayoutRestoreTracker();

    tracker.Observe("Home", exactSavedMonitorSetActive: false, allowTrigger: true);
    AssertFalse(tracker.Observe("Home", exactSavedMonitorSetActive: true, allowTrigger: false),
        "Expected an app-initiated activation not to trigger reconnect restore.");
    AssertFalse(tracker.Observe("Home", exactSavedMonitorSetActive: true, allowTrigger: true),
        "Expected a suppressed transition to be consumed rather than firing later.");
}

static void ReconnectTrackerResetsForProfileChange()
{
    var tracker = new ReconnectLayoutRestoreTracker();

    tracker.Observe("Home", exactSavedMonitorSetActive: false, allowTrigger: true);
    AssertFalse(tracker.Observe("Work", exactSavedMonitorSetActive: true, allowTrigger: true),
        "Expected a profile change to establish a new baseline without restoring.");
    AssertFalse(tracker.Observe("work", exactSavedMonitorSetActive: true, allowTrigger: true),
        "Expected profile identity matching to be case-insensitive.");
    AssertFalse(tracker.Observe("Work", exactSavedMonitorSetActive: false, allowTrigger: true),
        "Expected an active-to-inactive transition on the new profile not to restore.");
    AssertTrue(tracker.Observe("Work", exactSavedMonitorSetActive: true, allowTrigger: true),
        "Expected a subsequent transition on the new profile to restore.");
}

static void ReconnectTrackerResetRequiresFreshBaseline()
{
    var tracker = new ReconnectLayoutRestoreTracker();

    tracker.Observe("Home", exactSavedMonitorSetActive: false, allowTrigger: true);
    tracker.Reset();
    AssertFalse(tracker.Observe("Home", exactSavedMonitorSetActive: true, allowTrigger: true),
        "Expected Reset to discard prior transition state.");
}

static void ReconnectDetectionRejectsInconclusiveSnapshots()
{
    AssertTrue(Form1.IsReconnectDetectionSnapshotReliable(7, 7, usedScreenFallback: false),
        "Expected a stable helper-backed snapshot to be eligible for reconnect evaluation.");
    AssertFalse(Form1.IsReconnectDetectionSnapshotReliable(7, 8, usedScreenFallback: false),
        "Expected an event arriving during detection to make that snapshot inconclusive.");
    AssertFalse(Form1.IsReconnectDetectionSnapshotReliable(7, 7, usedScreenFallback: true),
        "Expected Screen fallback not to establish a reconnect-complete state.");
    AssertTrue(Form1.ShouldEvaluateReconnectEvent(8, 7),
        "Expected an unconsumed display event to remain eligible.");
    AssertFalse(Form1.ShouldEvaluateReconnectEvent(7, 7),
        "Expected an already consumed display event not to trigger again.");
}

static void PhysicalReconnectEvidenceIsConsumedByGeneration()
{
    AssertTrue(Form1.ShouldEvaluatePhysicalReconnect(2, 1),
        "Expected a newer monitor arrival-after-removal generation to be eligible.");
    AssertFalse(Form1.ShouldEvaluatePhysicalReconnect(1, 1),
        "Expected an already consumed physical reconnect generation to be ignored.");
    AssertFalse(Form1.ShouldEvaluatePhysicalReconnect(0, 0),
        "Expected no physical reconnect evidence at process start.");
}

static void UpdaterSemanticVersionsAreOrdered()
{
    AssertEquals<int?>(1, AliasSettingsForm.CompareSemanticVersions("0.4.0", "0.4.0-test.3"),
        "Expected a stable release to sort after its prerelease.");
    AssertEquals<int?>(1, AliasSettingsForm.CompareSemanticVersions("0.4.0-test.3", "0.3.9"),
        "Expected the current test build to sort after an older stable release.");
    AssertEquals<int?>(-1, AliasSettingsForm.CompareSemanticVersions("0.4.0-test.2", "0.4.0-test.10"),
        "Expected numeric prerelease identifiers to use numeric ordering.");
    AssertEquals<int?>(0, AliasSettingsForm.CompareSemanticVersions("0.4.0+build.1", "0.4.0+build.2"),
        "Expected build metadata not to affect precedence.");
    AssertEquals<int?>(null, AliasSettingsForm.CompareSemanticVersions("01.4.0", "0.4.0"),
        "Expected a core version with leading zeroes to be rejected.");
}

static void UpdaterReleaseAssetUriIsExact()
{
    const string tag = "v0.4.0";
    const string asset = "MonitorSwitcher-v0.4.0-win-x64.zip";
    AssertTrue(
        AliasSettingsForm.IsExpectedGitHubReleaseAssetUri(
            new Uri("https://github.com/Ci303/monitor-switcher-native/releases/download/v0.4.0/MonitorSwitcher-v0.4.0-win-x64.zip"),
            tag,
            asset),
        "Expected the exact GitHub HTTPS release asset URI to be accepted.");
    AssertFalse(
        AliasSettingsForm.IsExpectedGitHubReleaseAssetUri(
            new Uri("https://example.com/Ci303/monitor-switcher-native/releases/download/v0.4.0/MonitorSwitcher-v0.4.0-win-x64.zip"),
            tag,
            asset),
        "Expected a different host to be rejected.");
    AssertFalse(
        AliasSettingsForm.IsExpectedGitHubReleaseAssetUri(
            new Uri("https://github.com/Ci303/monitor-switcher-native/releases/download/v0.4.0/MonitorSwitcher-v0.4.0-win-x64.zip?redirect=1"),
            tag,
            asset),
        "Expected a query-bearing release address to be rejected.");
    AssertFalse(
        AliasSettingsForm.IsExpectedGitHubReleaseAssetUri(
            new Uri("https://github.com/Ci303/monitor-switcher-native/releases/download/v0.4.0/evil.zip"),
            tag,
            "../evil.zip"),
        "Expected an asset name containing traversal to be rejected.");
}

static void UpdaterChecksumSidecarIsStrict()
{
    const string asset = "MonitorSwitcher-v0.4.0-win-x64.zip";
    string hash = new('a', 64);

    AssertTrue(AliasSettingsForm.TryParsePublishedSha256($"{hash}  {asset}", asset, out string parsedHash),
        "Expected the release workflow's SHA-256 sidecar format to be accepted.");
    AssertEquals(hash, parsedHash, "Expected the parsed hash to be preserved.");
    AssertTrue(AliasSettingsForm.TryParsePublishedSha256($"{hash} *{asset}", asset, out _),
        "Expected the standard binary-marker sidecar form to be accepted.");
    AssertFalse(AliasSettingsForm.TryParsePublishedSha256($"{hash}  other.zip", asset, out _),
        "Expected a checksum for another filename to be rejected.");
    AssertFalse(AliasSettingsForm.TryParsePublishedSha256($"{hash}  {asset}\n{hash}  other.zip", asset, out _),
        "Expected multi-entry checksum content to be rejected.");
}

static void UpdaterArchivePathsAreContained()
{
    AssertTrue(AliasSettingsForm.IsSafeArchivePath("runtimes/win-x64/native/helper.dll"),
        "Expected a normal nested archive path to be accepted.");
    AssertFalse(AliasSettingsForm.IsSafeArchivePath("../outside.exe"),
        "Expected parent traversal to be rejected.");
    AssertFalse(AliasSettingsForm.IsSafeArchivePath("folder/../../outside.exe"),
        "Expected nested parent traversal to be rejected.");
    AssertFalse(AliasSettingsForm.IsSafeArchivePath("/absolute/file.exe"),
        "Expected an absolute archive path to be rejected.");
    AssertFalse(AliasSettingsForm.IsSafeArchivePath("C:/absolute/file.exe"),
        "Expected a drive-qualified archive path to be rejected.");
    AssertFalse(AliasSettingsForm.IsSafeArchivePath("folder//file.exe"),
        "Expected an empty archive path segment to be rejected.");
    AssertFalse(AliasSettingsForm.IsSafeArchivePath("folder/CON.txt"),
        "Expected a reserved Windows device name to be rejected.");
}

static void UiSettingsDisablesStartupProfileApplicationByDefault()
{
    var settings = new UiSettings();

    AssertFalse(settings.RestoreLayoutOnStartup,
        "Expected startup profile application to be off by default.");
}

static void ProfileSetChangeConfirmationFollowsSetting()
{
    AssertTrue(Form1.ShouldConfirmExactSetRestore(confirmBeforeDisable: true),
        "Expected profile set changes to ask when disable confirmation is enabled.");
    AssertFalse(Form1.ShouldConfirmExactSetRestore(confirmBeforeDisable: false),
        "Expected explicit profile application not to ask when disable confirmation is disabled.");
}

static void ProfileApplyRequiresVerifiedDifference()
{
    AssertTrue(Form1.ShouldEnableProfileApply(
            detectionReliable: true,
            profilePairValid: true,
            exactSetActive: false,
            displayActionAvailable: true),
        "Expected Apply to be enabled when the selected profile differs from the verified active set.");
    AssertFalse(Form1.ShouldEnableProfileApply(true, true, true, true),
        "Expected Apply to be disabled when the selected profile is already active.");
    AssertFalse(Form1.ShouldEnableProfileApply(false, true, false, true),
        "Expected unreliable detection to disable Apply.");
    AssertFalse(Form1.ShouldEnableProfileApply(true, false, false, true),
        "Expected an invalid profile pair to disable Apply.");
    AssertFalse(Form1.ShouldEnableProfileApply(true, true, false, false),
        "Expected another running display action to disable Apply.");
}

static void SettingsMonitorRowsFollowAttachedCount()
{
    AssertEquals(1, AliasSettingsForm.CalculateVisibleMonitorRows(0),
        "Expected an empty settings list to retain one usable row of height.");
    AssertEquals(1, AliasSettingsForm.CalculateVisibleMonitorRows(1),
        "Expected a one-monitor setup not to reserve extra rows.");
    AssertEquals(4, AliasSettingsForm.CalculateVisibleMonitorRows(4),
        "Expected the settings grid to follow the attached monitor count.");
    AssertEquals(8, AliasSettingsForm.CalculateVisibleMonitorRows(30),
        "Expected large monitor sets to scroll after eight visible rows.");
}

static void Form1CapsLargeMonitorLists()
{
    var compact = Form1.CalculateScrollableClientSize(
        desiredWidth: 420,
        desiredHeight: 600,
        maxClientHeight: 900,
        verticalScrollbarWidth: 17);
    AssertEquals(420, compact.Width, "Expected a short list not to reserve scrollbar width.");
    AssertEquals(600, compact.Height, "Expected a short list to retain its requested height.");

    var large = Form1.CalculateScrollableClientSize(
        desiredWidth: 420,
        desiredHeight: 10_000,
        maxClientHeight: 900,
        verticalScrollbarWidth: 17);
    AssertEquals(437, large.Width, "Expected a large list to reserve vertical scrollbar width.");
    AssertEquals(900, large.Height, "Expected a large list to remain within the working area.");
}

static DetectedMonitor Monitor(
    string stableKey,
    string deviceName,
    string name,
    bool isActive,
    bool isPresent = true)
{
    return new DetectedMonitor
    {
        StableKey = stableKey,
        DeviceName = deviceName,
        Name = name,
        IsActive = isActive,
        IsPresent = isPresent
    };
}

static Dictionary<string, MonitorInfo> EmptyAliases()
    => new(StringComparer.OrdinalIgnoreCase);

static void AssertTrue(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void AssertFalse(bool condition, string message)
{
    if (condition) throw new InvalidOperationException(message);
}

static void AssertEquals<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{message} Expected '{expected}', got '{actual}'.");
}

static void AssertContains(IEnumerable<string> values, string expected)
{
    if (!values.Any(v => string.Equals(v, expected, StringComparison.OrdinalIgnoreCase)))
        throw new InvalidOperationException($"Expected collection to contain '{expected}'.");
}

static void AssertSequence(IReadOnlyList<string> actual, params string[] expected)
{
    if (actual.Count != expected.Length)
        throw new InvalidOperationException($"Expected {expected.Length} item(s), got {actual.Count}: {string.Join(", ", actual)}.");

    for (int i = 0; i < expected.Length; i++)
    {
        if (!string.Equals(actual[i], expected[i], StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Expected item {i} to be '{expected[i]}', got '{actual[i]}'.");
    }
}
