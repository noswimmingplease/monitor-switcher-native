using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace WorkMonitorSwitcher.Services
{
    internal enum LayoutProfileTransactionPhase
    {
        Prepared = 0,
        Stashed = 1,
        Installed = 2,
        BackupsWritten = 3,
        Committed = 4
    }

    internal sealed class LayoutProfileTransactionEntry
    {
        public string TargetPath { get; set; } = string.Empty;
        public string StashPath { get; set; } = string.Empty;
        public bool OriginalExisted { get; set; }
    }

    internal sealed class LayoutProfileTransactionJournal
    {
        public int Version { get; set; } = 1;
        public LayoutProfileTransactionPhase Phase { get; set; }
        public string TargetLayoutPath { get; set; } = string.Empty;
        public string TargetIdentityPath { get; set; } = string.Empty;
        public string BackupLayoutPath { get; set; } = string.Empty;
        public string BackupIdentityPath { get; set; } = string.Empty;
        public bool BackupPairExpected { get; set; }
        public List<LayoutProfileTransactionEntry> Entries { get; set; } = new();
    }

    /// <summary>
    /// Promotes a staged layout and its identity sidecar as one recoverable logical unit.
    /// A flushed journal is written before the first mutation, so an interrupted commit
    /// can either be rolled back or have its committed cleanup completed safely.
    /// </summary>
    internal static class LayoutProfileTransaction
    {
        private const int JournalVersion = 1;
        private static readonly object TransactionSync = new();
        private static readonly JsonSerializerOptions JournalJson = new()
        {
            WriteIndented = true
        };

        public static string CreateStagingLayoutPath(string layoutPath)
        {
            var fullPath = Path.GetFullPath(layoutPath);
            var directory = Path.GetDirectoryName(fullPath)
                ?? throw new ArgumentException("The layout path must include a directory.", nameof(layoutPath));

            Directory.CreateDirectory(directory);
            return Path.Combine(
                directory,
                $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.profile-saving");
        }

        public static PersistenceResult Commit(string stagedLayoutPath, string layoutPath)
        {
            lock (TransactionSync)
            {
                return CommitCore(stagedLayoutPath, layoutPath);
            }
        }

        public static PersistenceResult Recover(string layoutPath)
        {
            lock (TransactionSync)
            {
                if (string.IsNullOrWhiteSpace(layoutPath))
                    return PersistenceResult.Failed("A destination layout path is required for recovery.");

                try
                {
                    return RecoverCore(Path.GetFullPath(layoutPath));
                }
                catch (Exception ex)
                {
                    return PersistenceResult.Failed(
                        $"Unable to recover the interrupted layout profile transaction: {ex.Message}");
                }
            }
        }

        internal static string GetJournalPath(string layoutPath)
        {
            var fullPath = Path.GetFullPath(layoutPath);
            var directory = Path.GetDirectoryName(fullPath)
                ?? throw new ArgumentException("The layout path must include a directory.", nameof(layoutPath));
            return Path.Combine(directory, $".{Path.GetFileName(fullPath)}.profile-transaction.json");
        }

        internal static bool TryGetTargetLayoutPathFromJournalFileName(
            string journalPath,
            out string targetLayoutPath)
        {
            targetLayoutPath = string.Empty;
            try
            {
                var fullJournalPath = Path.GetFullPath(journalPath);
                var directory = Path.GetDirectoryName(fullJournalPath);
                var fileName = Path.GetFileName(fullJournalPath);
                const string prefix = ".";
                const string suffix = ".profile-transaction.json";
                if (string.IsNullOrWhiteSpace(directory) ||
                    !fileName.StartsWith(prefix, StringComparison.Ordinal) ||
                    !fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var targetFileName = fileName[prefix.Length..^suffix.Length];
                if (string.IsNullOrWhiteSpace(targetFileName) ||
                    !targetFileName.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(Path.GetFileName(targetFileName), targetFileName, StringComparison.Ordinal))
                {
                    return false;
                }

                targetLayoutPath = Path.Combine(directory, targetFileName);
                return PathEquals(GetJournalPath(targetLayoutPath), fullJournalPath);
            }
            catch
            {
                return false;
            }
        }

        internal static bool IsValidProfilePair(
            string layoutPath,
            string identityPath,
            out string errorMessage)
        {
            errorMessage = string.Empty;
            if (!LayoutService.TryGetActiveLayoutDeviceNames(layoutPath, out var activeDeviceNames))
            {
                errorMessage = $"The layout is missing or invalid: {layoutPath}";
                return false;
            }

            if (!LayoutIdentityStore.TryLoadPrimaryIdentityFile(identityPath, out var identities))
            {
                errorMessage = $"The identity map is missing or invalid: {identityPath}";
                return false;
            }

            var identityDeviceNames = identities
                .Select(identity => MonitorTargetResolver.NormalizeDeviceNameForComparison(
                    FirstNonBlank(identity.LayoutDeviceName, identity.DeviceName)))
                .ToList();
            if (identityDeviceNames.Count != activeDeviceNames.Count ||
                identityDeviceNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != identityDeviceNames.Count)
            {
                errorMessage = "The active layout and identity map do not contain one identity per display.";
                return false;
            }

            var activeSet = activeDeviceNames
                .Select(MonitorTargetResolver.NormalizeDeviceNameForComparison)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var identitySet = identityDeviceNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!activeSet.SetEquals(identitySet))
            {
                errorMessage = "The active layout display names do not match the identity map.";
                return false;
            }

            if (NativeDisplayProfileCodec.TryRead(layoutPath, out var nativeProfile, out _) &&
                nativeProfile.Version > 0)
            {
                var identityByName = identities.ToDictionary(
                    identity => MonitorTargetResolver.NormalizeDeviceNameForComparison(
                        FirstNonBlank(identity.LayoutDeviceName, identity.DeviceName)),
                    StringComparer.OrdinalIgnoreCase);
                foreach (var monitor in nativeProfile.Monitors)
                {
                    var name = MonitorTargetResolver.NormalizeDeviceNameForComparison(
                        monitor.LayoutDeviceName);
                    if (!identityByName.TryGetValue(name, out var identity) ||
                        !NativeDisplayProfileCodec.IsStrongTargetPath(identity.NativeTargetPath) ||
                        !NativeDisplayProfileCodec.NormaliseTargetPath(identity.NativeTargetPath).Equals(
                            NativeDisplayProfileCodec.NormaliseTargetPath(monitor.MonitorDevicePath),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        errorMessage =
                            "The native layout target paths do not match the identity map captured in the same transaction.";
                        return false;
                    }
                }
            }

            return true;
        }

        public static void DeleteStagingArtifacts(string? stagedLayoutPath)
        {
            if (string.IsNullOrWhiteSpace(stagedLayoutPath))
                return;

            TryDelete(stagedLayoutPath);
            TryDelete(LayoutIdentityStore.GetIdentityPath(stagedLayoutPath));
            TryDelete(AtomicFileWriter.GetBackupPath(stagedLayoutPath));
            TryDelete(AtomicFileWriter.GetBackupPath(LayoutIdentityStore.GetIdentityPath(stagedLayoutPath)));
        }

        private static PersistenceResult CommitCore(string stagedLayoutPath, string layoutPath)
        {
            if (string.IsNullOrWhiteSpace(stagedLayoutPath) || string.IsNullOrWhiteSpace(layoutPath))
                return PersistenceResult.Failed("Both staged and destination layout paths are required.");

            string stagedLayout;
            string stagedIdentity;
            string targetLayout;
            string targetIdentity;
            string targetLayoutBackup;
            string targetIdentityBackup;
            string directory;

            try
            {
                stagedLayout = Path.GetFullPath(stagedLayoutPath);
                stagedIdentity = Path.GetFullPath(LayoutIdentityStore.GetIdentityPath(stagedLayout));
                targetLayout = Path.GetFullPath(layoutPath);
                targetIdentity = Path.GetFullPath(LayoutIdentityStore.GetIdentityPath(targetLayout));
                targetLayoutBackup = AtomicFileWriter.GetBackupPath(targetLayout);
                targetIdentityBackup = AtomicFileWriter.GetBackupPath(targetIdentity);
                directory = Path.GetDirectoryName(targetLayout) ?? string.Empty;

                if (string.IsNullOrWhiteSpace(directory) ||
                    !PathEquals(Path.GetDirectoryName(stagedLayout), directory) ||
                    !PathEquals(Path.GetDirectoryName(stagedIdentity), directory) ||
                    !PathEquals(Path.GetDirectoryName(targetIdentity), directory) ||
                    !PathEquals(Path.GetDirectoryName(targetLayoutBackup), directory) ||
                    !PathEquals(Path.GetDirectoryName(targetIdentityBackup), directory))
                {
                    return PersistenceResult.Failed(
                        "The staged layout, identity sidecar, destination, and backups must share one directory.");
                }

                if (PathEquals(stagedLayout, targetLayout) || PathEquals(stagedIdentity, targetIdentity))
                    return PersistenceResult.Failed("The staged profile must not overwrite itself.");

                Directory.CreateDirectory(directory);
                var recovery = RecoverCore(targetLayout);
                if (!recovery.Success)
                    return recovery;

                if (!IsValidProfilePair(stagedLayout, stagedIdentity, out var stagedPairError))
                    return PersistenceResult.Failed($"The staged layout profile is not a matched pair. {stagedPairError}");
            }
            catch (Exception ex)
            {
                return PersistenceResult.Failed($"Unable to validate the staged layout profile: {ex.Message}");
            }

            bool currentPairValid = IsValidProfilePair(targetLayout, targetIdentity, out _);
            bool backupPairValid = IsValidProfilePair(targetLayoutBackup, targetIdentityBackup, out _);
            var backupSource = currentPairValid
                ? BackupSource.CurrentPrimary
                : backupPairValid
                    ? BackupSource.ExistingBackup
                    : BackupSource.None;

            var operationId = Guid.NewGuid().ToString("N");
            var journal = new LayoutProfileTransactionJournal
            {
                Version = JournalVersion,
                Phase = LayoutProfileTransactionPhase.Prepared,
                TargetLayoutPath = targetLayout,
                TargetIdentityPath = targetIdentity,
                BackupLayoutPath = targetLayoutBackup,
                BackupIdentityPath = targetIdentityBackup,
                BackupPairExpected = backupSource != BackupSource.None,
                Entries = new[]
                {
                    targetLayout,
                    targetIdentity,
                    targetLayoutBackup,
                    targetIdentityBackup
                }
                .Select(path => new LayoutProfileTransactionEntry
                {
                    TargetPath = path,
                    StashPath = Path.Combine(
                        directory,
                        $".{Path.GetFileName(path)}.{operationId}.profile-old"),
                    OriginalExisted = File.Exists(path)
                })
                .ToList()
            };
            var journalPath = GetJournalPath(targetLayout);

            try
            {
                WriteJournalAtomically(journalPath, journal);

                foreach (var entry in journal.Entries.Where(entry => entry.OriginalExisted))
                    File.Move(entry.TargetPath, entry.StashPath);

                UpdateJournalPhase(journalPath, journal, LayoutProfileTransactionPhase.Stashed);

                File.Move(stagedLayout, targetLayout);
                File.Move(stagedIdentity, targetIdentity);
                UpdateJournalPhase(journalPath, journal, LayoutProfileTransactionPhase.Installed);

                CopyBackupPair(journal, backupSource);
                UpdateJournalPhase(journalPath, journal, LayoutProfileTransactionPhase.BackupsWritten);

                if (!IsValidProfilePair(targetLayout, targetIdentity, out var committedPairError))
                    throw new InvalidDataException($"The committed profile pair failed validation. {committedPairError}");
                if (journal.BackupPairExpected &&
                    !IsValidProfilePair(targetLayoutBackup, targetIdentityBackup, out var backupPairError))
                {
                    throw new InvalidDataException($"The rollback profile pair failed validation. {backupPairError}");
                }

                UpdateJournalPhase(journalPath, journal, LayoutProfileTransactionPhase.Committed);

                var cleanupWarnings = CleanupCommittedJournal(journalPath, journal);
                return PersistenceResult.Saved(warningMessage: string.Join(" ", cleanupWarnings));
            }
            catch (Exception commitException)
            {
                var rollback = RecoverCore(targetLayout);
                var rollbackDetail = rollback.Success
                    ? string.Empty
                    : $" Recovery issue: {rollback.ErrorMessage}";
                return PersistenceResult.Failed(
                    $"Unable to commit the layout and identity map together: " +
                    $"{commitException.Message}.{rollbackDetail}");
            }
        }

        private static PersistenceResult RecoverCore(string targetLayoutPath)
        {
            var targetLayout = Path.GetFullPath(targetLayoutPath);
            var journalPath = GetJournalPath(targetLayout);
            if (!File.Exists(journalPath))
                return PersistenceResult.Unchanged();

            LayoutProfileTransactionJournal? journal;
            try
            {
                journal = JsonSerializer.Deserialize<LayoutProfileTransactionJournal>(
                    File.ReadAllText(journalPath));
            }
            catch (Exception ex)
            {
                return PersistenceResult.Failed(
                    $"The profile transaction journal could not be read: {ex.Message}");
            }

            if (!IsValidJournal(journal, targetLayout, out var journalError))
                return PersistenceResult.Failed($"The profile transaction journal is invalid: {journalError}");

            if (journal!.Phase == LayoutProfileTransactionPhase.Committed)
            {
                if (!IsValidProfilePair(
                        journal.TargetLayoutPath,
                        journal.TargetIdentityPath,
                        out var currentPairError))
                {
                    return PersistenceResult.Failed(
                        $"The interrupted transaction was committed, but its current profile pair is invalid. {currentPairError}");
                }

                if (journal.BackupPairExpected &&
                    !IsValidProfilePair(
                        journal.BackupLayoutPath,
                        journal.BackupIdentityPath,
                        out var backupPairError))
                {
                    return PersistenceResult.Failed(
                        $"The interrupted transaction was committed, but its rollback pair is invalid. {backupPairError}");
                }

                var warnings = CleanupCommittedJournal(journalPath, journal);
                return PersistenceResult.Saved(
                    changed: true,
                    warningMessage: string.Join(" ", warnings));
            }

            var rollbackErrors = RollBack(journal);
            if (rollbackErrors.Count > 0)
            {
                return PersistenceResult.Failed(
                    $"The interrupted layout profile transaction could not be fully rolled back: " +
                    string.Join(" ", rollbackErrors));
            }

            try
            {
                File.Delete(journalPath);
            }
            catch (Exception ex)
            {
                return PersistenceResult.Failed(
                    $"The profile was rolled back, but its transaction journal could not be removed: {ex.Message}");
            }

            return PersistenceResult.Saved(
                warningMessage: "Recovered an interrupted layout profile transaction.");
        }

        private static void CopyBackupPair(
            LayoutProfileTransactionJournal journal,
            BackupSource source)
        {
            if (source == BackupSource.None)
                return;

            var layoutSourceTarget = source == BackupSource.CurrentPrimary
                ? journal.TargetLayoutPath
                : journal.BackupLayoutPath;
            var identitySourceTarget = source == BackupSource.CurrentPrimary
                ? journal.TargetIdentityPath
                : journal.BackupIdentityPath;
            var layoutSource = FindEntry(journal, layoutSourceTarget).StashPath;
            var identitySource = FindEntry(journal, identitySourceTarget).StashPath;

            File.Copy(layoutSource, journal.BackupLayoutPath, overwrite: false);
            File.Copy(identitySource, journal.BackupIdentityPath, overwrite: false);
        }

        private static List<string> RollBack(LayoutProfileTransactionJournal journal)
        {
            var errors = new List<string>();
            foreach (var entry in journal.Entries.AsEnumerable().Reverse())
            {
                try
                {
                    if (entry.OriginalExisted)
                    {
                        if (!File.Exists(entry.StashPath))
                        {
                            if (journal.Phase >= LayoutProfileTransactionPhase.Stashed ||
                                !File.Exists(entry.TargetPath))
                            {
                                errors.Add(
                                    $"Could not prove the original '{entry.TargetPath}' is recoverable because its stash is missing.");
                            }
                            continue;
                        }

                        if (File.Exists(entry.TargetPath))
                            File.Delete(entry.TargetPath);
                        File.Move(entry.StashPath, entry.TargetPath);
                    }
                    else if (MayContainReplacement(journal, entry.TargetPath) &&
                             File.Exists(entry.TargetPath))
                    {
                        File.Delete(entry.TargetPath);
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"Could not restore '{entry.TargetPath}': {ex.Message}");
                }
            }

            return errors;
        }

        private static bool MayContainReplacement(
            LayoutProfileTransactionJournal journal,
            string targetPath)
        {
            if (PathEquals(targetPath, journal.TargetLayoutPath) ||
                PathEquals(targetPath, journal.TargetIdentityPath))
            {
                return journal.Phase >= LayoutProfileTransactionPhase.Stashed;
            }

            return journal.Phase >= LayoutProfileTransactionPhase.Installed;
        }

        private static List<string> CleanupCommittedJournal(
            string journalPath,
            LayoutProfileTransactionJournal journal)
        {
            var warnings = new List<string>();
            foreach (var entry in journal.Entries)
            {
                try
                {
                    if (File.Exists(entry.StashPath))
                        File.Delete(entry.StashPath);
                }
                catch (Exception ex)
                {
                    warnings.Add($"A superseded profile artefact remains at '{entry.StashPath}': {ex.Message}");
                }
            }

            if (warnings.Count == 0)
            {
                try
                {
                    File.Delete(journalPath);
                }
                catch (Exception ex)
                {
                    warnings.Add($"The completed profile transaction journal remains at '{journalPath}': {ex.Message}");
                }
            }

            return warnings;
        }

        private static bool IsValidJournal(
            LayoutProfileTransactionJournal? journal,
            string expectedTargetLayout,
            out string errorMessage)
        {
            errorMessage = string.Empty;
            if (journal == null ||
                journal.Version != JournalVersion ||
                !Enum.IsDefined(journal.Phase) ||
                journal.Entries == null ||
                journal.Entries.Count != 4)
            {
                errorMessage = "Unsupported or incomplete journal data.";
                return false;
            }

            var expectedTargetIdentity = LayoutIdentityStore.GetIdentityPath(expectedTargetLayout);
            var expectedBackupLayout = AtomicFileWriter.GetBackupPath(expectedTargetLayout);
            var expectedBackupIdentity = AtomicFileWriter.GetBackupPath(expectedTargetIdentity);
            if (!PathEquals(journal.TargetLayoutPath, expectedTargetLayout) ||
                !PathEquals(journal.TargetIdentityPath, expectedTargetIdentity) ||
                !PathEquals(journal.BackupLayoutPath, expectedBackupLayout) ||
                !PathEquals(journal.BackupIdentityPath, expectedBackupIdentity))
            {
                errorMessage = "Journal target paths do not match the requested profile.";
                return false;
            }

            var directory = Path.GetDirectoryName(expectedTargetLayout);
            var expectedTargets = new[]
            {
                expectedTargetLayout,
                expectedTargetIdentity,
                expectedBackupLayout,
                expectedBackupIdentity
            };
            if (journal.Entries.Select(entry => entry.TargetPath)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != expectedTargets.Length ||
                expectedTargets.Any(expected =>
                    !journal.Entries.Any(entry => PathEquals(entry.TargetPath, expected))))
            {
                errorMessage = "Journal entries do not describe the complete profile pair.";
                return false;
            }

            foreach (var entry in journal.Entries)
            {
                var stashFileName = Path.GetFileName(entry.StashPath);
                var expectedPrefix = $".{Path.GetFileName(entry.TargetPath)}.";
                const string expectedSuffix = ".profile-old";
                if (!PathEquals(Path.GetDirectoryName(entry.TargetPath), directory) ||
                    !PathEquals(Path.GetDirectoryName(entry.StashPath), directory) ||
                    !stashFileName.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase) ||
                    !stashFileName.EndsWith(expectedSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    errorMessage = "A journal path is outside the profile directory or has an unexpected name.";
                    return false;
                }

                var operationId = stashFileName[
                    expectedPrefix.Length..
                    ^expectedSuffix.Length];
                if (!Guid.TryParseExact(operationId, "N", out _))
                {
                    errorMessage = "A journal stash name does not contain a valid operation identifier.";
                    return false;
                }
            }

            if (journal.Entries.Select(entry => Path.GetFullPath(entry.StashPath))
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != journal.Entries.Count)
            {
                errorMessage = "Journal stash paths are not unique.";
                return false;
            }

            var operationIds = journal.Entries
                .Select(entry =>
                {
                    var fileName = Path.GetFileName(entry.StashPath);
                    var prefixLength = Path.GetFileName(entry.TargetPath).Length + 2;
                    return fileName[prefixLength..^".profile-old".Length];
                })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            if (operationIds != 1)
            {
                errorMessage = "Journal stash paths do not share one operation identifier.";
                return false;
            }

            return true;
        }

        private static LayoutProfileTransactionEntry FindEntry(
            LayoutProfileTransactionJournal journal,
            string targetPath)
            => journal.Entries.Single(entry => PathEquals(entry.TargetPath, targetPath));

        private static void UpdateJournalPhase(
            string journalPath,
            LayoutProfileTransactionJournal journal,
            LayoutProfileTransactionPhase phase)
        {
            journal.Phase = phase;
            WriteJournalAtomically(journalPath, journal);
        }

        private static void WriteJournalAtomically(
            string journalPath,
            LayoutProfileTransactionJournal journal)
        {
            var directory = Path.GetDirectoryName(journalPath)
                ?? throw new InvalidOperationException("The transaction journal has no directory.");
            var temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(journalPath)}.{Guid.NewGuid():N}.tmp");

            try
            {
                using (var stream = new FileStream(
                           temporaryPath,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None,
                           4096,
                           FileOptions.WriteThrough))
                {
                    JsonSerializer.Serialize(stream, journal, JournalJson);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporaryPath, journalPath, overwrite: true);
            }
            finally
            {
                TryDelete(temporaryPath);
            }
        }

        private static string FirstNonBlank(params string?[] values)
            => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

        private static bool PathEquals(string? left, string? right)
            => !string.IsNullOrWhiteSpace(left) &&
               !string.IsNullOrWhiteSpace(right) &&
               Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar)
                   .Equals(
                       Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
                       StringComparison.OrdinalIgnoreCase);

        private static void TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // Best-effort cleanup; a prior operation result remains authoritative.
            }
        }

        private enum BackupSource
        {
            None,
            CurrentPrimary,
            ExistingBackup
        }
    }
}
