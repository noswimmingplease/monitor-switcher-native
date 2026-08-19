using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;
using WorkMonitorSwitcher;
using WorkMonitorSwitcher.Model;
using WorkMonitorSwitcher.Services;

internal static class CoreServiceHardeningTests
{
    internal static IReadOnlyList<(string Name, Action Body)> GetTests()
        => new (string, Action)[]
        {
            ("Topology verification retains captured orientation and effective size", TopologyVerificationUsesCapturedGeometryByDefault),
            ("Activation verification rejects retained monitor geometry changes", ActivationVerificationProtectsRetainedGeometry),
            ("Startup registration distinguishes missing, current and other executables", StartupRegistrationIsTriState),
            ("Startup settings preserve an unchanged registration", StartupSettingsRequireExplicitIntent),
            ("Duplicate EDID serials use unique native identities", DuplicateSerialsUseUniqueNativeIdentity),
            ("Stable monitor keys survive duplicate-serial population changes", DuplicateSerialPopulationChangesDoNotChangeKeys),
            ("Alias migration rejects an uncorroborated disconnected duplicate serial", DuplicateSerialAliasMigrationRequiresStrongIdentity),
            ("Duplicate EDID serials remain fail-closed without unique native identity", DuplicateSerialsWithoutFallbackFailClosed),
            ("Profile names cannot resolve to extended DOS device names", ProfileNamesRejectExtendedDosDevices),
            ("Prepared profile deletion is rolled back after interruption", PreparedDeletionIsRolledBack),
            ("Prepared profile deletion fails closed when its artefact disappears", PreparedDeletionRejectsMissingArtifact),
            ("Committed profile deletion phases complete after interruption", CommittedDeletionPhasesAreRecovered),
            ("Profile preview resolves present inactive native targets", ProfilePreviewIncludesPresentInactiveTargets),
            ("Native profile identity rejects an absent duplicate-serial peer", NativeProfileIdentityRejectsAbsentDuplicateSerialPeer),
            ("Profile deletion refuses a retained save journal", ProfileDeletionRefusesRetainedSaveJournal)
        };

    internal static void RunAll(Action<string, Action> register)
    {
        foreach (var (name, body) in GetTests())
            register(name, body);
    }

    private static void TopologyVerificationUsesCapturedGeometryByDefault()
    {
        var captured = new DisplaySize(1440, 2560);
        AssertEqual(captured, DisplayTopologyService.ResolveVerificationSize(captured, null));
        AssertEqual(4u, DisplayTopologyService.ResolveVerificationRotation(4, null));
        AssertEqual(new DisplaySize(2560, 1440), DisplayTopologyService.ResolveVerificationSize(
            captured,
            new DisplaySize(2560, 1440)));
        AssertEqual(2u, DisplayTopologyService.ResolveVerificationRotation(4, 2));

        AssertFalse(
            DisplayTopologyService.EffectiveDisplaySizeMatches(
                new DisplaySize(2560, 1440),
                captured,
                rotation: 4,
                allowQuarterTurnEquivalent: false),
            "Captured effective size verification must reject a swapped result.");
        AssertTrue(
            DisplayTopologyService.EffectiveDisplaySizeMatches(
                new DisplaySize(2560, 1440),
                captured,
                rotation: 4,
                allowQuarterTurnEquivalent: true),
            "An explicit legacy size override may retain quarter-turn equivalence.");
    }

    private static void ActivationVerificationProtectsRetainedGeometry()
    {
        string left = MonitorPath("MODEL", "LEFT");
        string right = MonitorPath("MODEL", "RIGHT");
        string added = MonitorPath("MODEL", "ADDED");
        var original = new[]
        {
            new ActiveDisplayGeometry(left, @"\\.\DISPLAY1", 0, 0, 1920, 1080, 1, true),
            new ActiveDisplayGeometry(right, @"\\.\DISPLAY2", 1920, 0, 1080, 1920, 2, false)
        };
        var preserved = new[]
        {
            original[0],
            original[1],
            new ActiveDisplayGeometry(added, @"\\.\DISPLAY3", 3000, 0, 1920, 1080, 1, false)
        };

        AssertTrue(
            DisplayTopologyService.TryVerifyRetainedDisplayGeometry(
                original,
                preserved,
                new[] { left, right, added },
                out var preservedIssues),
            string.Join(" ", preservedIssues));

        var rotated = preserved
            .Select(entry => entry.TargetPath.Equals(right, StringComparison.OrdinalIgnoreCase)
                ? entry with { Rotation = 1 }
                : entry)
            .ToList();
        AssertFalse(
            DisplayTopologyService.TryVerifyRetainedDisplayGeometry(
                original,
                rotated,
                new[] { left, right, added },
                out var rotationIssues),
            "A retained monitor rotation change must fail verification.");
        AssertTrue(rotationIssues.Any(issue => issue.Contains("rotation", StringComparison.OrdinalIgnoreCase)));

        var resized = preserved
            .Select(entry => entry.TargetPath.Equals(right, StringComparison.OrdinalIgnoreCase)
                ? entry with { Width = entry.Width + 1 }
                : entry)
            .ToList();
        AssertFalse(
            DisplayTopologyService.TryVerifyRetainedDisplayGeometry(
                original,
                resized,
                new[] { left, right, added },
                out var sizeIssues),
            "A retained monitor effective-size change must fail verification.");
        AssertTrue(sizeIssues.Any(issue => issue.Contains("effective size", StringComparison.OrdinalIgnoreCase)));

        var moved = preserved
            .Select(entry => entry.TargetPath.Equals(right, StringComparison.OrdinalIgnoreCase)
                ? entry with { X = entry.X + 80 }
                : entry)
            .ToList();
        AssertFalse(
            DisplayTopologyService.TryVerifyRetainedDisplayGeometry(
                original,
                moved,
                new[] { left, right, added },
                out var movementIssues),
            "Retained monitors moving relative to one another must fail verification.");
        AssertTrue(movementIssues.Any(issue => issue.Contains("relative", StringComparison.OrdinalIgnoreCase)));

        var primaryChanged = preserved
            .Select(entry => entry.TargetPath.Equals(left, StringComparison.OrdinalIgnoreCase)
                ? entry with { IsPrimary = false }
                : entry.TargetPath.Equals(right, StringComparison.OrdinalIgnoreCase)
                    ? entry with { IsPrimary = true }
                    : entry)
            .ToList();
        AssertFalse(
            DisplayTopologyService.TryVerifyRetainedDisplayGeometry(
                original,
                primaryChanged,
                new[] { left, right, added },
                out var primaryIssues),
            "The retained primary must not silently change.");
        AssertTrue(primaryIssues.Any(issue => issue.Contains("primary", StringComparison.OrdinalIgnoreCase)));

        var oldPrimaryRemoved = new[]
        {
            new ActiveDisplayGeometry(right, @"\\.\DISPLAY2", 1920, 0, 1080, 1920, 2, false),
            new ActiveDisplayGeometry(added, @"\\.\DISPLAY3", 3000, 0, 1920, 1080, 1, false)
        };
        var translated = new[]
        {
            new ActiveDisplayGeometry(right, @"\\.\DISPLAY2", 0, 0, 1080, 1920, 2, true),
            new ActiveDisplayGeometry(added, @"\\.\DISPLAY3", 1080, 0, 1920, 1080, 1, false)
        };
        AssertTrue(
            DisplayTopologyService.TryVerifyRetainedDisplayGeometry(
                oldPrimaryRemoved,
                translated,
                new[] { right, added },
                out var translatedIssues),
            string.Join(" ", translatedIssues));
    }

    private static void StartupRegistrationIsTriState()
    {
        var executable = Path.Combine(Path.GetTempPath(), "MonitorSwitcher", "MonitorSwitcher.exe");
        AssertEqual(
            StartupRegistrationState.Missing,
            StartupManager.ClassifyCommand(null, executable));
        AssertEqual(
            StartupRegistrationState.CurrentExecutable,
            StartupManager.ClassifyCommand($"\"{executable}\"", executable));
        AssertEqual(
            StartupRegistrationState.OtherExecutable,
            StartupManager.ClassifyCommand(
                $"\"{Path.Combine(Path.GetDirectoryName(executable)!, "Old", "MonitorSwitcher.exe")}\"",
                executable));
        AssertEqual(
            StartupRegistrationState.OtherExecutable,
            StartupManager.ClassifyCommand("not a valid startup command", executable));
    }

    private static void StartupSettingsRequireExplicitIntent()
    {
        AssertEqual<bool?>(null, AliasSettingsForm.ResolveStartupIntent(
            CheckState.Checked,
            CheckState.Checked));
        AssertEqual<bool?>(null, AliasSettingsForm.ResolveStartupIntent(
            CheckState.Indeterminate,
            CheckState.Indeterminate));
        AssertEqual<bool?>(true, AliasSettingsForm.ResolveStartupIntent(
            CheckState.Indeterminate,
            CheckState.Checked));
        AssertEqual<bool?>(false, AliasSettingsForm.ResolveStartupIntent(
            CheckState.Indeterminate,
            CheckState.Unchecked));
    }

    private static void DuplicateSerialsUseUniqueNativeIdentity()
    {
        var candidates = new[]
        {
            Candidate("GPU:1", @"\\.\DISPLAY1", MonitorPath("AOC2703", "INSTANCE-A"), "DUPLICATE"),
            Candidate("GPU:2", @"\\.\DISPLAY2", MonitorPath("AOC2703", "INSTANCE-B"), "DUPLICATE")
        };

        AssertTrue(
            NativeDisplayDetection.TryConsolidateCandidates(candidates, out var monitors, out var error),
            error);
        DetectionService.AssignUniqueStableKeys(monitors);
        AssertEqual(2, monitors.Count);
        AssertTrue(monitors.All(monitor => monitor.StableKey.StartsWith("IID:", StringComparison.Ordinal)),
            "Unique Windows instance identities should replace the duplicate serial.");
        AssertEqual(2, monitors.Select(monitor => monitor.StableKey)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count());

        foreach (var monitor in monitors)
            monitor.InstanceId = string.Empty;
        DetectionService.AssignUniqueStableKeys(monitors);
        AssertTrue(monitors.All(monitor => monitor.StableKey.StartsWith("NTP:", StringComparison.Ordinal)),
            "Unique native target paths should be the next duplicate-serial fallback.");
    }

    private static void DuplicateSerialPopulationChangesDoNotChangeKeys()
    {
        var first = new DetectedMonitor
        {
            SerialNumber = "DUPLICATE",
            InstanceId = @"DISPLAY\MODEL\INSTANCE-A",
            NativeTargetPath = @"\\?\DISPLAY#MODEL#INSTANCE-A#{GUID}"
        };
        var second = new DetectedMonitor
        {
            SerialNumber = "DUPLICATE",
            InstanceId = @"DISPLAY\MODEL\INSTANCE-B",
            NativeTargetPath = @"\\?\DISPLAY#MODEL#INSTANCE-B#{GUID}"
        };

        DetectionService.AssignUniqueStableKeys(new[] { first, second });
        string firstWithPeer = first.StableKey;
        string secondWithPeer = second.StableKey;

        DetectionService.AssignUniqueStableKeys(new[] { first });
        AssertEqual(firstWithPeer, first.StableKey);
        AssertTrue(first.StableKey.StartsWith("IID:", StringComparison.Ordinal),
            "The surviving display must not be promoted to a population-dependent serial key.");

        DetectionService.AssignUniqueStableKeys(new[] { first, second });
        AssertEqual(firstWithPeer, first.StableKey);
        AssertEqual(secondWithPeer, second.StableKey);
    }

    private static void DuplicateSerialAliasMigrationRequiresStrongIdentity()
    {
        string currentTarget = MonitorPath("MODEL", "CURRENT");
        var current = Detected(
            @"\\.\DISPLAY1",
            currentTarget,
            "DUPLICATE",
            "INSTANCE-CURRENT",
            active: true);
        current.MonitorKey = @"SYSTEM\CurrentControlSet\Enum\DISPLAY\MODEL\INSTANCE-CURRENT";
        var disconnectedPeer = new KeyValuePair<string, MonitorInfo>(
            @"IID:DISPLAY\MODEL\INSTANCE-PEER",
            new MonitorInfo
            {
                Name = "Peer",
                LastSerialNumber = "DUPLICATE",
                LastInstanceId = @"DISPLAY\MODEL\INSTANCE-PEER",
                LastRegistryKey = @"SYSTEM\CurrentControlSet\Enum\DISPLAY\MODEL\INSTANCE-PEER",
                LastNativeTargetPath = MonitorPath("MODEL", "PEER")
            });

        var unsafeSerialOnly = Form1.FindUniquePhysicalAliasMatch(
            current,
            new[] { disconnectedPeer });
        AssertTrue(unsafeSerialOnly == null,
            "A lone alias with only the shared serial must not be assigned to the current monitor.");

        var legacySerialKey = new KeyValuePair<string, MonitorInfo>(
            "SN:DUPLICATE",
            new MonitorInfo
            {
                Name = "Current",
                LastSerialNumber = "DUPLICATE",
                LastInstanceId = current.InstanceId
            });
        var corroborated = Form1.FindUniquePhysicalAliasMatch(current, new[] { legacySerialKey });
        AssertTrue(corroborated.HasValue, "A matching instance should permit legacy SN to IID migration.");
        AssertEqual("SN:DUPLICATE", corroborated!.Value.Key);

        var internallyConflicting = new KeyValuePair<string, MonitorInfo>(
            "SN:DUPLICATE-CONFLICT",
            new MonitorInfo
            {
                LastSerialNumber = "DUPLICATE",
                LastInstanceId = current.InstanceId,
                LastRegistryKey = @"SYSTEM\CurrentControlSet\Enum\DISPLAY\MODEL\DIFFERENT"
            });
        AssertTrue(Form1.FindUniquePhysicalAliasMatch(
                current,
                new[] { internallyConflicting }) == null,
            "One matching strong field must not override another non-empty conflicting field.");

        var conflictingTarget = new KeyValuePair<string, MonitorInfo>(
            "SN:DUPLICATE-TARGET",
            new MonitorInfo
            {
                LastNativeTargetPath = currentTarget
            });
        var conflictingInstance = new KeyValuePair<string, MonitorInfo>(
            "SN:DUPLICATE-INSTANCE",
            new MonitorInfo
            {
                LastInstanceId = current.InstanceId
            });
        AssertTrue(Form1.FindUniquePhysicalAliasMatch(
                current,
                new[] { conflictingTarget, conflictingInstance }) == null,
            "Conflicting stronger identities must fail closed instead of using serial order.");
    }

    private static void DuplicateSerialsWithoutFallbackFailClosed()
    {
        var candidates = new[]
        {
            new NativeDisplayCandidate("GPU:1", false, true, string.Empty, "Monitor", string.Empty,
                string.Empty, "DUPLICATE", "AOC2703", 1, 0),
            new NativeDisplayCandidate("GPU:2", false, true, string.Empty, "Monitor", string.Empty,
                string.Empty, "DUPLICATE", "AOC2703", 2, 0)
        };

        AssertFalse(
            NativeDisplayDetection.TryConsolidateCandidates(candidates, out var monitors, out var error),
            "A duplicate serial without a unique instance or native path must remain ambiguous.");
        AssertEqual(0, monitors.Count);
        AssertTrue(error.Contains("ambiguous", StringComparison.OrdinalIgnoreCase), error);
    }

    private static void ProfileNamesRejectExtendedDosDevices()
    {
        AssertEqual("_CON.foo", LayoutProfileStore.NormalizeProfileName("CON.foo"));
        AssertEqual("_lpt1.backup", LayoutProfileStore.NormalizeProfileName("lpt1.backup"));
        AssertEqual("Work", LayoutProfileStore.NormalizeProfileName("Work. "));
        AssertEqual(LayoutProfileStore.DefaultProfileName, LayoutProfileStore.NormalizeProfileName("... "));
    }

    private static void PreparedDeletionIsRolledBack()
    {
        WithDeletionFixture((directory, indexPath, layoutPath, identityPath) =>
        {
            var journal = CreateJournal(
                directory,
                indexPath,
                LayoutProfileDeletionPhase.Prepared,
                layoutPath);
            WriteJournal(directory, journal);

            AssertTrue(JsonFilePersistence.Save(indexPath, journal.RemainingNames).Success,
                "Expected the interrupted index write to be simulated.");
            var recovery = LayoutProfileDeletionTransaction.Recover(directory, indexPath);

            AssertTrue(recovery.Success, recovery.ErrorMessage);
            AssertTrue(File.Exists(layoutPath), "Prepared recovery must retain the profile artefact.");
            AssertContains(ReadIndex(indexPath), "Work");
            AssertFalse(File.Exists(LayoutProfileDeletionTransaction.GetJournalPath(directory)),
                "A successfully rolled-back journal should be removed.");
        });
    }

    private static void CommittedDeletionPhasesAreRecovered()
    {
        foreach (var phase in new[]
                 {
                     LayoutProfileDeletionPhase.IndexCommitted,
                     LayoutProfileDeletionPhase.ArtifactsStaged,
                     LayoutProfileDeletionPhase.Committed
                 })
        {
            WithDeletionFixture((directory, indexPath, layoutPath, identityPath) =>
            {
                var journal = CreateJournal(directory, indexPath, phase, layoutPath, identityPath);
                AssertTrue(JsonFilePersistence.Save(indexPath, journal.RemainingNames).Success,
                    "Expected the deletion index to be committed.");

                if (phase == LayoutProfileDeletionPhase.IndexCommitted)
                {
                    File.Move(layoutPath, journal.Entries[0].StagedPath);
                }
                else
                {
                    foreach (var entry in journal.Entries)
                        File.Move(entry.OriginalPath, entry.StagedPath);
                }

                WriteJournal(directory, journal);
                var recovery = LayoutProfileDeletionTransaction.Recover(directory, indexPath);

                AssertTrue(recovery.Success, $"{phase}: {recovery.ErrorMessage}");
                AssertFalse(journal.Entries.Any(entry =>
                        File.Exists(entry.OriginalPath) || File.Exists(entry.StagedPath)),
                    $"{phase}: every profile artefact should be removed.");
                AssertFalse(ReadIndex(indexPath).Contains("Work", StringComparer.OrdinalIgnoreCase),
                    $"{phase}: the deleted profile must stay out of the index.");
                AssertFalse(File.Exists(LayoutProfileDeletionTransaction.GetJournalPath(directory)),
                    $"{phase}: a completed journal should be removed.");
            });
        }
    }

    private static void PreparedDeletionRejectsMissingArtifact()
    {
        WithDeletionFixture((directory, indexPath, layoutPath, identityPath) =>
        {
            var journal = CreateJournal(
                directory,
                indexPath,
                LayoutProfileDeletionPhase.Prepared,
                layoutPath);
            WriteJournal(directory, journal);
            File.Delete(layoutPath);

            var recovery = LayoutProfileDeletionTransaction.Recover(directory, indexPath);

            AssertFalse(recovery.Success,
                "Recovery must not claim to restore an artefact that disappeared from both locations.");
            AssertTrue(File.Exists(LayoutProfileDeletionTransaction.GetJournalPath(directory)),
                "An unresolved journal must remain for diagnosis or a later recovery attempt.");
        });
    }

    private static void ProfilePreviewIncludesPresentInactiveTargets()
    {
        string root = Path.Combine(Path.GetTempPath(), "MonitorSwitcher.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string layoutPath = Path.Combine(root, "Work.cfg");
        string leftTarget = MonitorPath("MODEL", "LEFT");
        string rightTarget = MonitorPath("MODEL", "RIGHT");
        var profile = new NativeDisplayProfile(
            NativeDisplayProfileCodec.CurrentVersion,
            new[]
            {
                new NativeDisplayProfileMonitor(
                    @"\\.\DISPLAY1", leftTarget, 1, 0, 1, 0,
                    0, 0, 1920, 1080, 1, true, "Left", null, null, null),
                new NativeDisplayProfileMonitor(
                    @"\\.\DISPLAY2", rightTarget, 1, 1, 1, 1,
                    1920, 0, 1080, 1920, 2, false, "Right", null, null, null)
            });
        var detected = new[]
        {
            Detected(@"\\.\DISPLAY1", leftTarget, "SERIAL-LEFT", "INSTANCE-LEFT", active: true),
            Detected(@"\\.\DISPLAY2", rightTarget, "SERIAL-RIGHT", "INSTANCE-RIGHT", active: false)
        };
        var identities = detected.Select(monitor => new SavedLayoutIdentity
        {
            LayoutDeviceName = monitor.DeviceName,
            DeviceName = monitor.DeviceName,
            NativeTargetPath = monitor.NativeTargetPath,
            StableKey = monitor.StableKey,
            SerialNumber = monitor.SerialNumber,
            InstanceId = monitor.InstanceId
        }).ToList();

        try
        {
            File.WriteAllText(layoutPath, NativeDisplayProfileCodec.Serialise(profile));
            var result = DisplayTopologyService.ResolveSavedProfileMembership(
                layoutPath,
                detected,
                identities);

            AssertTrue(result.Success, result.ErrorMessage);
            AssertEqual(2, result.PresentSavedMonitors.Count);
            AssertEqual(0, result.UnavailableSavedMonitorCount);
            AssertTrue(result.PresentSavedMonitors.Any(monitor =>
                !monitor.IsActive && monitor.NativeTargetPath.Equals(rightTarget, StringComparison.OrdinalIgnoreCase)));

            var disconnected = DisplayTopologyService.ResolveSavedProfileMembership(
                layoutPath,
                new[] { detected[0] },
                identities);
            AssertTrue(disconnected.Success, disconnected.ErrorMessage);
            AssertEqual(1, disconnected.PresentSavedMonitors.Count);
            AssertEqual(1, disconnected.UnavailableSavedMonitorCount);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static void NativeProfileIdentityRejectsAbsentDuplicateSerialPeer()
    {
        string root = Path.Combine(Path.GetTempPath(), "MonitorSwitcher.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string layoutPath = Path.Combine(root, "DuplicateSerial.cfg");
        string savedTarget = MonitorPath("MODEL", "SAVED");
        string peerTarget = MonitorPath("MODEL", "PEER");
        var profile = new NativeDisplayProfile(
            NativeDisplayProfileCodec.CurrentVersion,
            new[]
            {
                new NativeDisplayProfileMonitor(
                    @"\\.\DISPLAY1", savedTarget, 1, 0, 1, 0,
                    0, 0, 1920, 1080, 1, true, "Saved", null, null, null)
            });
        var savedMonitor = Detected(
            @"\\.\DISPLAY1",
            savedTarget,
            "DUPLICATE",
            "SAVED",
            active: true);
        var duplicateSerialPeer = Detected(
            @"\\.\DISPLAY2",
            peerTarget,
            "DUPLICATE",
            "PEER",
            active: false);
        var identities = new[]
        {
            new SavedLayoutIdentity
            {
                LayoutDeviceName = savedMonitor.DeviceName,
                DeviceName = savedMonitor.DeviceName,
                NativeTargetPath = savedMonitor.NativeTargetPath,
                StableKey = savedMonitor.StableKey,
                SerialNumber = savedMonitor.SerialNumber,
                InstanceId = savedMonitor.InstanceId
            }
        };

        try
        {
            File.WriteAllText(layoutPath, NativeDisplayProfileCodec.Serialise(profile));

            var absentSavedMonitor = DisplayTopologyService.ResolveSavedProfileMembership(
                layoutPath,
                new[] { duplicateSerialPeer },
                identities);

            AssertTrue(absentSavedMonitor.Success, absentSavedMonitor.ErrorMessage);
            AssertEqual(0, absentSavedMonitor.PresentSavedMonitors.Count);
            AssertEqual(1, absentSavedMonitor.UnavailableSavedMonitorCount);

            var exactSavedMonitor = DisplayTopologyService.ResolveSavedProfileMembership(
                layoutPath,
                new[] { savedMonitor },
                identities);

            AssertTrue(exactSavedMonitor.Success, exactSavedMonitor.ErrorMessage);
            AssertEqual(1, exactSavedMonitor.PresentSavedMonitors.Count);
            AssertEqual(0, exactSavedMonitor.UnavailableSavedMonitorCount);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static void ProfileDeletionRefusesRetainedSaveJournal()
    {
        WithDeletionFixture((directory, indexPath, layoutPath, identityPath) =>
        {
            string target = MonitorPath("MODEL", "WORK");
            var detected = Detected(@"\\.\DISPLAY1", target, "SERIAL-WORK", "INSTANCE-WORK", active: true);
            var profile = new NativeDisplayProfile(
                NativeDisplayProfileCodec.CurrentVersion,
                new[]
                {
                    new NativeDisplayProfileMonitor(
                        detected.DeviceName, target, 1, 0, 1, 0,
                        0, 0, 1920, 1080, 1, true, "Work", null, null, null)
                });
            File.WriteAllText(layoutPath, NativeDisplayProfileCodec.Serialise(profile));
            AssertTrue(LayoutIdentityStore.SaveWithResult(layoutPath, new[] { detected }).Success);

            string operationId = Guid.NewGuid().ToString("N");
            string backupLayoutPath = AtomicFileWriter.GetBackupPath(layoutPath);
            string backupIdentityPath = AtomicFileWriter.GetBackupPath(identityPath);
            var targets = new[] { layoutPath, identityPath, backupLayoutPath, backupIdentityPath };
            var saveJournal = new LayoutProfileTransactionJournal
            {
                Phase = LayoutProfileTransactionPhase.Committed,
                TargetLayoutPath = layoutPath,
                TargetIdentityPath = identityPath,
                BackupLayoutPath = backupLayoutPath,
                BackupIdentityPath = backupIdentityPath,
                BackupPairExpected = false,
                Entries = targets.Select(path => new LayoutProfileTransactionEntry
                {
                    TargetPath = path,
                    StashPath = Path.Combine(
                        directory,
                        $".{Path.GetFileName(path)}.{operationId}.profile-old"),
                    OriginalExisted = File.Exists(path)
                }).ToList()
            };
            string journalPath = LayoutProfileTransaction.GetJournalPath(layoutPath);
            File.WriteAllText(journalPath, JsonSerializer.Serialize(saveJournal));

            using var journalLock = new FileStream(
                journalPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            string appDataDirectory = Directory.GetParent(directory)!.FullName;
            var store = new LayoutProfileStore(
                appDataDirectory,
                Path.Combine(appDataDirectory, "Default.cfg"));
            var deletion = store.DeleteProfileWithResult("Work");

            AssertFalse(deletion.Success,
                "Deletion must stop when completed save-journal cleanup is still pending.");
            AssertTrue(deletion.ErrorMessage.Contains("journal", StringComparison.OrdinalIgnoreCase));
            AssertTrue(File.Exists(layoutPath), "The saved layout must remain intact.");
            AssertTrue(File.Exists(identityPath), "The saved identity map must remain intact.");
            AssertContains(ReadIndex(indexPath), "Work");
        });
    }

    private static LayoutProfileDeletionJournal CreateJournal(
        string directory,
        string indexPath,
        LayoutProfileDeletionPhase phase,
        params string[] artifactPaths)
    {
        var operationId = Guid.NewGuid().ToString("N");
        return new LayoutProfileDeletionJournal
        {
            OperationId = operationId,
            Phase = phase,
            ProfileName = "Work",
            IndexPath = indexPath,
            OriginalNames = new List<string> { LayoutProfileStore.DefaultProfileName, "Work" },
            RemainingNames = new List<string> { LayoutProfileStore.DefaultProfileName },
            Entries = artifactPaths.Select(path => new LayoutProfileDeletionEntry
            {
                OriginalPath = path,
                StagedPath = Path.Combine(
                    directory,
                    $".{Path.GetFileName(path)}.{operationId}.deleting")
            }).ToList()
        };
    }

    private static void WriteJournal(string directory, LayoutProfileDeletionJournal journal)
        => File.WriteAllText(
            LayoutProfileDeletionTransaction.GetJournalPath(directory),
            JsonSerializer.Serialize(journal));

    private static void WithDeletionFixture(
        Action<string, string, string, string> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "MonitorSwitcher.Tests", Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "layouts");
        Directory.CreateDirectory(directory);
        var indexPath = Path.Combine(directory, "profiles.json");
        var layoutPath = Path.Combine(directory, "Work.cfg");
        var identityPath = LayoutIdentityStore.GetIdentityPath(layoutPath);
        File.WriteAllText(layoutPath, "layout");
        File.WriteAllText(identityPath, "identity");
        AssertTrue(JsonFilePersistence.Save(
                indexPath,
                new List<string> { LayoutProfileStore.DefaultProfileName, "Work" }).Success,
            "Expected the fixture profile index to save.");

        try
        {
            body(directory, indexPath, layoutPath, identityPath);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static List<string> ReadIndex(string indexPath)
    {
        var result = JsonFilePersistence.Load<List<string>>(indexPath);
        if (!result.Success || result.Value == null)
            throw new InvalidOperationException(result.ErrorMessage);
        return result.Value;
    }

    private static DetectedMonitor Detected(
        string deviceName,
        string targetPath,
        string serial,
        string instance,
        bool active)
        => new()
        {
            Name = deviceName,
            DeviceName = deviceName,
            NativeTargetPath = targetPath,
            SerialNumber = serial,
            InstanceId = $@"DISPLAY\MODEL\{instance}",
            StableKey = $@"IID:DISPLAY\MODEL\{instance}",
            IsPresent = true,
            IsActive = active
        };

    private static NativeDisplayCandidate Candidate(
        string endpoint,
        string source,
        string path,
        string serial)
    {
        NativeDisplayDetection.TryParseMonitorDevicePath(path, out var hardware, out var instance);
        return new NativeDisplayCandidate(
            endpoint,
            true,
            true,
            source,
            "AOC2703",
            path,
            $"DISPLAY\\{hardware}\\{instance}",
            serial,
            "AOC2703",
            1,
            0);
    }

    private static string MonitorPath(string hardware, string instance)
        => $@"\\?\DISPLAY#{hardware}#{instance}#{{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}}";

    private static void AssertTrue(bool condition, string message = "Expected true.")
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void AssertFalse(bool condition, string message = "Expected false.")
    {
        if (condition) throw new InvalidOperationException(message);
    }

    private static void AssertEqual<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }

    private static void AssertContains(IEnumerable<string> values, string expected)
    {
        if (!values.Contains(expected, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Expected collection to contain '{expected}'.");
    }
}
