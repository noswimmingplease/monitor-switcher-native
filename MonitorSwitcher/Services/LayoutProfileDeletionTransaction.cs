using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace WorkMonitorSwitcher.Services
{
    internal enum LayoutProfileDeletionPhase
    {
        Prepared = 0,
        IndexCommitted = 1,
        ArtifactsStaged = 2,
        Committed = 3
    }

    internal sealed class LayoutProfileDeletionEntry
    {
        public string OriginalPath { get; set; } = string.Empty;
        public string StagedPath { get; set; } = string.Empty;
    }

    internal sealed class LayoutProfileDeletionJournal
    {
        public int Version { get; set; } = 1;
        public string OperationId { get; set; } = string.Empty;
        public LayoutProfileDeletionPhase Phase { get; set; }
        public string ProfileName { get; set; } = string.Empty;
        public string IndexPath { get; set; } = string.Empty;
        public List<string> OriginalNames { get; set; } = new();
        public List<string> RemainingNames { get; set; } = new();
        public List<LayoutProfileDeletionEntry> Entries { get; set; } = new();
    }

    /// <summary>
    /// Deletes one profile as a recoverable logical transaction. A flushed
    /// journal precedes the index change and every filesystem mutation. Recovery
    /// rolls a Prepared transaction back, and completes any later phase.
    /// </summary>
    internal static class LayoutProfileDeletionTransaction
    {
        private const int JournalVersion = 1;
        private const string JournalFileName = ".profile-deletion-transaction.json";
        private static readonly object TransactionSync = new();
        private static readonly JsonSerializerOptions JournalJson = new()
        {
            WriteIndented = true
        };

        public static PersistenceResult Delete(
            string profilesDirectory,
            string indexPath,
            string profileName,
            IReadOnlyCollection<string> originalNames,
            IReadOnlyCollection<string> remainingNames,
            IReadOnlyCollection<string> artifactPaths)
        {
            lock (TransactionSync)
            {
                try
                {
                    var directory = Path.GetFullPath(profilesDirectory);
                    var fullIndexPath = Path.GetFullPath(indexPath);
                    Directory.CreateDirectory(directory);
                    if (!PathEquals(Path.GetDirectoryName(fullIndexPath), directory))
                        return PersistenceResult.Failed("The profile index is outside the profiles directory.");

                    var priorRecovery = RecoverCore(directory, fullIndexPath);
                    if (!priorRecovery.Success)
                        return priorRecovery;
                    if (priorRecovery.Changed)
                    {
                        return PersistenceResult.Failed(
                            "An interrupted profile deletion was recovered. Refresh the profile list, then try again.");
                    }

                    if (string.IsNullOrWhiteSpace(profileName) ||
                        originalNames == null ||
                        remainingNames == null ||
                        artifactPaths == null)
                    {
                        return PersistenceResult.Failed("Complete profile deletion data is required.");
                    }

                    var operationId = Guid.NewGuid().ToString("N");
                    var entries = artifactPaths
                        .Where(File.Exists)
                        .Select(Path.GetFullPath)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Select(path =>
                        {
                            if (!PathEquals(Path.GetDirectoryName(path), directory))
                            {
                                throw new InvalidOperationException(
                                    $"Refused to delete a file outside the profiles directory: {path}");
                            }

                            return new LayoutProfileDeletionEntry
                            {
                                OriginalPath = path,
                                StagedPath = Path.Combine(
                                    directory,
                                    $".{Path.GetFileName(path)}.{operationId}.deleting")
                            };
                        })
                        .ToList();

                    var journal = new LayoutProfileDeletionJournal
                    {
                        Version = JournalVersion,
                        OperationId = operationId,
                        Phase = LayoutProfileDeletionPhase.Prepared,
                        ProfileName = profileName,
                        IndexPath = fullIndexPath,
                        OriginalNames = originalNames.ToList(),
                        RemainingNames = remainingNames.ToList(),
                        Entries = entries
                    };
                    var journalPath = GetJournalPath(directory);
                    if (!IsValidJournal(journal, directory, fullIndexPath, out var validationError))
                        return PersistenceResult.Failed($"The profile deletion transaction is invalid: {validationError}");

                    WriteJournalAtomically(journalPath, journal);

                    var indexResult = SaveIndex(fullIndexPath, journal.RemainingNames);
                    if (!indexResult.Success)
                    {
                        TryDelete(journalPath);
                        return indexResult;
                    }

                    UpdateJournalPhase(journalPath, journal, LayoutProfileDeletionPhase.IndexCommitted);

                    try
                    {
                        StageEntries(journal.Entries);
                    }
                    catch (Exception stagingException)
                    {
                        var rollback = RollBack(journalPath, journal);
                        var rollbackMessage = rollback.Success
                            ? string.Empty
                            : $" Rollback also failed: {rollback.ErrorMessage}";
                        return PersistenceResult.Failed(
                            $"Unable to stage layout profile '{profileName}' for deletion: " +
                            $"{stagingException.Message}.{rollbackMessage}");
                    }

                    UpdateJournalPhase(journalPath, journal, LayoutProfileDeletionPhase.ArtifactsStaged);
                    UpdateJournalPhase(journalPath, journal, LayoutProfileDeletionPhase.Committed);

                    var warnings = CompleteCommittedDeletion(journalPath, journal, fullIndexPath);
                    return PersistenceResult.Saved(
                        changed: indexResult.Changed || entries.Count > 0,
                        warningMessage: string.Join(" ", warnings));
                }
                catch (Exception ex)
                {
                    return PersistenceResult.Failed(
                        $"Unable to delete the layout profile transactionally: {ex.Message}");
                }
            }
        }

        public static PersistenceResult Recover(string profilesDirectory, string indexPath)
        {
            lock (TransactionSync)
            {
                try
                {
                    return RecoverCore(
                        Path.GetFullPath(profilesDirectory),
                        Path.GetFullPath(indexPath));
                }
                catch (Exception ex)
                {
                    return PersistenceResult.Failed(
                        $"Unable to recover the interrupted profile deletion: {ex.Message}");
                }
            }
        }

        internal static string GetJournalPath(string profilesDirectory)
            => Path.Combine(Path.GetFullPath(profilesDirectory), JournalFileName);

        private static PersistenceResult RecoverCore(string directory, string indexPath)
        {
            var journalPath = GetJournalPath(directory);
            if (!File.Exists(journalPath))
                return PersistenceResult.Unchanged();

            LayoutProfileDeletionJournal? journal;
            try
            {
                journal = JsonSerializer.Deserialize<LayoutProfileDeletionJournal>(
                    File.ReadAllText(journalPath));
            }
            catch (Exception ex)
            {
                return PersistenceResult.Failed(
                    $"The profile deletion journal could not be read: {ex.Message}");
            }

            if (!IsValidJournal(journal, directory, indexPath, out var validationError))
            {
                return PersistenceResult.Failed(
                    $"Refused an invalid profile deletion journal: {validationError}");
            }

            if (journal!.Phase == LayoutProfileDeletionPhase.Prepared)
                return RollBack(journalPath, journal);

            try
            {
                var indexResult = SaveIndex(indexPath, journal.RemainingNames);
                if (!indexResult.Success)
                    return indexResult;

                StageEntries(journal.Entries);
                if (journal.Phase < LayoutProfileDeletionPhase.ArtifactsStaged)
                    UpdateJournalPhase(journalPath, journal, LayoutProfileDeletionPhase.ArtifactsStaged);
                if (journal.Phase < LayoutProfileDeletionPhase.Committed)
                    UpdateJournalPhase(journalPath, journal, LayoutProfileDeletionPhase.Committed);

                var warnings = CompleteCommittedDeletion(journalPath, journal, indexPath);
                return PersistenceResult.Saved(
                    changed: true,
                    warningMessage: string.Join(" ", warnings));
            }
            catch (Exception ex)
            {
                return PersistenceResult.Failed(
                    $"Unable to complete the interrupted profile deletion: {ex.Message}");
            }
        }

        private static PersistenceResult RollBack(
            string journalPath,
            LayoutProfileDeletionJournal journal)
        {
            var errors = new List<string>();
            foreach (var entry in journal.Entries.AsEnumerable().Reverse())
            {
                try
                {
                    if (!File.Exists(entry.StagedPath))
                    {
                        if (!File.Exists(entry.OriginalPath))
                        {
                            errors.Add(
                                $"Both the original and staged profile artefact are missing: '{entry.OriginalPath}'.");
                        }
                        continue;
                    }
                    if (File.Exists(entry.OriginalPath))
                    {
                        errors.Add(
                            $"Both the original and staged profile artefact exist: '{entry.OriginalPath}'.");
                        continue;
                    }

                    File.Move(entry.StagedPath, entry.OriginalPath);
                }
                catch (Exception ex)
                {
                    errors.Add($"Could not restore '{entry.OriginalPath}': {ex.Message}");
                }
            }

            if (errors.Count == 0)
            {
                var indexResult = SaveIndex(journal.IndexPath, journal.OriginalNames);
                if (!indexResult.Success)
                    errors.Add(indexResult.ErrorMessage);
            }

            if (errors.Count > 0)
                return PersistenceResult.Failed(string.Join(" ", errors));

            var backup = SynchronizeIndexBackup(journal.IndexPath);
            if (!backup.Success)
                return PersistenceResult.Failed(backup.ErrorMessage);

            try
            {
                File.Delete(journalPath);
            }
            catch (Exception ex)
            {
                return PersistenceResult.Failed(
                    $"The profile deletion was rolled back, but its journal could not be removed: {ex.Message}");
            }

            return PersistenceResult.Saved(changed: true, warningMessage: backup.WarningMessage);
        }

        private static List<string> CompleteCommittedDeletion(
            string journalPath,
            LayoutProfileDeletionJournal journal,
            string indexPath)
        {
            var warnings = new List<string>();
            foreach (var entry in journal.Entries)
            {
                try
                {
                    if (File.Exists(entry.OriginalPath))
                    {
                        if (File.Exists(entry.StagedPath))
                        {
                            warnings.Add(
                                $"Both an original and staged deleted artefact remain for '{entry.OriginalPath}'.");
                            continue;
                        }

                        File.Move(entry.OriginalPath, entry.StagedPath);
                    }

                    if (File.Exists(entry.StagedPath))
                        File.Delete(entry.StagedPath);
                }
                catch (Exception ex)
                {
                    warnings.Add(
                        $"A staged deleted artefact remains at '{entry.StagedPath}': {ex.Message}");
                }
            }

            var backup = SynchronizeIndexBackup(indexPath);
            if (!backup.Success)
            {
                warnings.Add(
                    $"The profile-index backup could not be updated: {backup.ErrorMessage}");
            }
            else if (!string.IsNullOrWhiteSpace(backup.WarningMessage))
            {
                warnings.Add(backup.WarningMessage);
            }

            if (warnings.Count == 0)
            {
                try
                {
                    File.Delete(journalPath);
                }
                catch (Exception ex)
                {
                    warnings.Add(
                        $"The completed profile deletion journal remains at '{journalPath}': {ex.Message}");
                }
            }

            return warnings;
        }

        private static void StageEntries(IEnumerable<LayoutProfileDeletionEntry> entries)
        {
            foreach (var entry in entries)
            {
                if (!File.Exists(entry.OriginalPath))
                    continue;
                if (File.Exists(entry.StagedPath))
                {
                    throw new IOException(
                        $"A staged deletion path already exists: {entry.StagedPath}");
                }

                File.Move(entry.OriginalPath, entry.StagedPath);
            }
        }

        private static PersistenceResult SaveIndex(string indexPath, IReadOnlyCollection<string> names)
            => JsonFilePersistence.Save(
                indexPath,
                names.ToList(),
                value => value != null &&
                         value.Any(name => name.Equals(
                             LayoutProfileStore.DefaultProfileName,
                             StringComparison.OrdinalIgnoreCase)));

        private static PersistenceResult SynchronizeIndexBackup(string indexPath)
            => AtomicFileWriter.SynchronizeBackupWithPrimary(
                indexPath,
                contents => JsonFilePersistence.TryDeserialize<List<string>>(
                    contents,
                    value => value != null,
                    out _));

        private static bool IsValidJournal(
            LayoutProfileDeletionJournal? journal,
            string expectedDirectory,
            string expectedIndexPath,
            out string errorMessage)
        {
            errorMessage = string.Empty;
            if (journal == null ||
                journal.Version != JournalVersion ||
                !Enum.IsDefined(journal.Phase) ||
                !Guid.TryParseExact(journal.OperationId, "N", out _) ||
                string.IsNullOrWhiteSpace(journal.ProfileName) ||
                journal.OriginalNames == null ||
                journal.RemainingNames == null ||
                journal.Entries == null ||
                journal.OriginalNames.Any(string.IsNullOrWhiteSpace) ||
                journal.RemainingNames.Any(string.IsNullOrWhiteSpace) ||
                journal.Entries.Any(entry => entry == null ||
                                             string.IsNullOrWhiteSpace(entry.OriginalPath) ||
                                             string.IsNullOrWhiteSpace(entry.StagedPath)))
            {
                errorMessage = "Unsupported or incomplete journal data.";
                return false;
            }

            var directory = Path.GetFullPath(expectedDirectory);
            if (!PathEquals(journal.IndexPath, expectedIndexPath) ||
                !PathEquals(Path.GetDirectoryName(journal.IndexPath), directory))
            {
                errorMessage = "The journal index path is outside the profiles directory.";
                return false;
            }

            if (!journal.OriginalNames.Any(name => name.Equals(
                    LayoutProfileStore.DefaultProfileName,
                    StringComparison.OrdinalIgnoreCase)) ||
                !journal.OriginalNames.Any(name => name.Equals(
                    journal.ProfileName,
                    StringComparison.OrdinalIgnoreCase)) ||
                !journal.RemainingNames.Any(name => name.Equals(
                    LayoutProfileStore.DefaultProfileName,
                    StringComparison.OrdinalIgnoreCase)) ||
                journal.RemainingNames.Any(name => name.Equals(
                    journal.ProfileName,
                    StringComparison.OrdinalIgnoreCase)) ||
                journal.OriginalNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != journal.OriginalNames.Count ||
                journal.RemainingNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != journal.RemainingNames.Count)
            {
                errorMessage = "The journal profile-name sets are inconsistent.";
                return false;
            }

            var expectedRemainingNames = journal.OriginalNames
                .Where(name => !name.Equals(journal.ProfileName, StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!expectedRemainingNames.SetEquals(journal.RemainingNames) ||
                !LayoutProfileStore.NormalizeProfileName(journal.ProfileName).Equals(
                    journal.ProfileName,
                    StringComparison.Ordinal))
            {
                errorMessage = "The journal does not describe exactly one canonical profile deletion.";
                return false;
            }

            foreach (var entry in journal.Entries)
            {
                var expectedStagedFileName =
                    $".{Path.GetFileName(entry.OriginalPath)}.{journal.OperationId}.deleting";
                if (!PathEquals(Path.GetDirectoryName(entry.OriginalPath), directory) ||
                    !PathEquals(Path.GetDirectoryName(entry.StagedPath), directory) ||
                    !Path.GetFileName(entry.StagedPath).Equals(
                        expectedStagedFileName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    errorMessage = "A journal artefact path is outside the profiles directory or malformed.";
                    return false;
                }

                if (!IsAllowedProfileArtifact(directory, journal.ProfileName, entry.OriginalPath))
                {
                    errorMessage = "A journal entry does not belong to the profile being deleted.";
                    return false;
                }
            }

            if (journal.Entries.Select(entry => Path.GetFullPath(entry.OriginalPath))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count() != journal.Entries.Count ||
                journal.Entries.Select(entry => Path.GetFullPath(entry.StagedPath))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count() != journal.Entries.Count)
            {
                errorMessage = "The journal contains duplicate artefact paths.";
                return false;
            }

            return true;
        }

        private static bool IsAllowedProfileArtifact(
            string profilesDirectory,
            string profileName,
            string candidatePath)
        {
            var layoutPath = Path.Combine(profilesDirectory, $"{profileName}.cfg");
            var identityPath = LayoutIdentityStore.GetIdentityPath(layoutPath);
            if (PathEquals(candidatePath, layoutPath) ||
                PathEquals(candidatePath, AtomicFileWriter.GetBackupPath(layoutPath)) ||
                PathEquals(candidatePath, identityPath) ||
                PathEquals(candidatePath, AtomicFileWriter.GetBackupPath(identityPath)))
            {
                return true;
            }

            var fullCandidate = Path.GetFullPath(candidatePath);
            if (LayoutProfileStore.IsExpectedAutoSaveBackup(layoutPath, fullCandidate))
                return true;

            const string identitySuffix = ".identity.json";
            var candidateWithoutIdentity = fullCandidate;
            if (candidateWithoutIdentity.EndsWith(identitySuffix + ".bak", StringComparison.OrdinalIgnoreCase))
                candidateWithoutIdentity = candidateWithoutIdentity[..^(identitySuffix.Length + 4)];
            else if (candidateWithoutIdentity.EndsWith(identitySuffix, StringComparison.OrdinalIgnoreCase))
                candidateWithoutIdentity = candidateWithoutIdentity[..^identitySuffix.Length];
            else
                return false;

            return LayoutProfileStore.IsExpectedAutoSaveBackup(layoutPath, candidateWithoutIdentity);
        }

        private static void UpdateJournalPhase(
            string journalPath,
            LayoutProfileDeletionJournal journal,
            LayoutProfileDeletionPhase phase)
        {
            journal.Phase = phase;
            WriteJournalAtomically(journalPath, journal);
        }

        private static void WriteJournalAtomically(
            string journalPath,
            LayoutProfileDeletionJournal journal)
        {
            var directory = Path.GetDirectoryName(journalPath)
                ?? throw new InvalidOperationException("The deletion journal has no directory.");
            Directory.CreateDirectory(directory);
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
                // Recovery retains the authoritative result if cleanup is blocked.
            }
        }
    }
}
