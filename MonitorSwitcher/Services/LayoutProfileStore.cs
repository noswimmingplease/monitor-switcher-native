using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace WorkMonitorSwitcher.Services
{
    internal sealed class LayoutProfileStore
    {
        public const string DefaultProfileName = "Default";

        private readonly string _profilesDir;
        private readonly string _indexPath;
        private readonly string _legacyLayoutPath;

        public LayoutProfileStore(string appDataDir, string legacyLayoutPath)
        {
            _profilesDir = Path.Combine(appDataDir, "layouts");
            _indexPath = Path.Combine(_profilesDir, "profiles.json");
            _legacyLayoutPath = legacyLayoutPath;
        }

        public List<string> LoadProfileNames()
        {
            try
            {
                Directory.CreateDirectory(_profilesDir);
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { DefaultProfileName };

                var index = JsonFilePersistence.Load<List<string>>(_indexPath, value => value != null);
                if (index.Success && index.Value != null)
                {
                    foreach (var name in index.Value.Where(n => !string.IsNullOrWhiteSpace(n)))
                        names.Add(NormalizeProfileName(name));
                }

                foreach (var file in Directory.EnumerateFiles(_profilesDir, "*.cfg"))
                    names.Add(NormalizeProfileName(Path.GetFileNameWithoutExtension(file)));

                var ordered = names
                    .OrderBy(n => n.Equals(DefaultProfileName, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                _ = SaveProfileNamesWithResult(ordered);
                return ordered;
            }
            catch
            {
                return new List<string> { DefaultProfileName };
            }
        }

        public PersistenceResult RecoverProfileTransactionsWithResult()
        {
            var errors = new List<string>();
            var warnings = new List<string>();
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var legacyPath = Path.GetFullPath(_legacyLayoutPath);
                var legacyDirectory = Path.GetDirectoryName(legacyPath);
                targets.Add(legacyPath);

                Directory.CreateDirectory(_profilesDir);
                foreach (var profilePath in Directory.EnumerateFiles(
                             _profilesDir,
                             "*.cfg",
                             SearchOption.TopDirectoryOnly))
                {
                    targets.Add(Path.GetFullPath(profilePath));
                }

                var directories = new[] { legacyDirectory, Path.GetFullPath(_profilesDir) }
                    .Where(directory => !string.IsNullOrWhiteSpace(directory))
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                foreach (var directory in directories)
                {
                    if (!Directory.Exists(directory))
                        continue;

                    foreach (var journalPath in Directory.EnumerateFiles(
                                 directory!,
                                 ".*.cfg.profile-transaction.json",
                                 SearchOption.TopDirectoryOnly))
                    {
                        if (LayoutProfileTransaction.TryGetTargetLayoutPathFromJournalFileName(
                                journalPath,
                                out var targetLayoutPath))
                        {
                            targets.Add(targetLayoutPath);
                        }
                        else
                        {
                            errors.Add($"Refused an unexpected profile transaction journal: {journalPath}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return PersistenceResult.Failed(
                    $"Unable to enumerate layout profile transactions: {ex.Message}");
            }

            bool changed = false;
            foreach (var target in targets)
            {
                var result = LayoutProfileTransaction.Recover(target);
                if (!result.Success)
                    errors.Add(result.ErrorMessage);
                else
                {
                    changed |= result.Changed;
                    if (!string.IsNullOrWhiteSpace(result.WarningMessage))
                        warnings.Add(result.WarningMessage);
                }
            }

            return errors.Count > 0
                ? PersistenceResult.Failed(string.Join(" ", errors))
                : PersistenceResult.Saved(
                    changed: changed,
                    warningMessage: string.Join(" ", warnings));
        }

        public string GetLayoutPath(string? profileName)
        {
            var name = NormalizeProfileName(profileName);
            if (name.Equals(DefaultProfileName, StringComparison.OrdinalIgnoreCase))
                return _legacyLayoutPath;

            Directory.CreateDirectory(_profilesDir);
            return Path.Combine(_profilesDir, $"{SanitizeFileName(name)}.cfg");
        }

        public void AddProfileName(string? profileName)
            => _ = AddProfileNameWithResult(profileName);

        public PersistenceResult AddProfileNameWithResult(string? profileName)
        {
            var name = NormalizeProfileName(profileName);
            var names = LoadProfileNames();
            if (names.Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase)))
                return PersistenceResult.Unchanged();

            names.Add(name);
            return SaveProfileNamesWithResult(names);
        }

        public bool DeleteProfile(string? profileName)
            => DeleteProfileWithResult(profileName).Success;

        public PersistenceResult DeleteProfileWithResult(string? profileName)
        {
            var name = NormalizeProfileName(profileName);
            if (name.Equals(DefaultProfileName, StringComparison.OrdinalIgnoreCase))
                return PersistenceResult.Failed("The Default layout profile cannot be deleted.");

            var originalNames = LoadProfileNames();
            var remainingNames = originalNames
                .Where(n => !n.Equals(name, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var indexResult = SaveProfileNamesWithResult(remainingNames);
            if (!indexResult.Success)
                return indexResult;

            var deleteResult = DeleteProfileArtifacts(name);
            if (!deleteResult.Success)
            {
                var rollback = SaveProfileNamesWithResult(originalNames);
                var rollbackMessage = rollback.Success
                    ? string.Empty
                    : $" The profile index rollback also failed: {rollback.ErrorMessage}";
                return PersistenceResult.Failed(deleteResult.ErrorMessage + rollbackMessage);
            }

            // Once the profile artefacts are gone, the previous index is no longer
            // a safe recovery point because it would resurrect the deleted name.
            var backupResult = AtomicFileWriter.SynchronizeBackupWithPrimary(
                _indexPath,
                contents => JsonFilePersistence.TryDeserialize<List<string>>(
                    contents,
                    value => value != null,
                    out _));

            return PersistenceResult.Saved(
                changed: indexResult.Changed || deleteResult.Changed || backupResult.Changed,
                warningMessage: CombineWarnings(
                    CombineWarnings(indexResult.WarningMessage, deleteResult.WarningMessage),
                    backupResult.Success
                        ? backupResult.WarningMessage
                        : $"The profile was deleted, but the profile-index backup could not be updated: {backupResult.ErrorMessage}"));
        }

        public static string NormalizeProfileName(string? profileName)
        {
            if (string.IsNullOrWhiteSpace(profileName))
                return DefaultProfileName;

            var value = profileName.Trim();
            foreach (var c in Path.GetInvalidFileNameChars())
                value = value.Replace(c, '_');

            if (value.Equals(DefaultProfileName, StringComparison.OrdinalIgnoreCase))
                return DefaultProfileName;

            var reservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "CON", "PRN", "AUX", "NUL",
                "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
                "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
            };
            if (reservedNames.Contains(value))
                value = "_" + value;

            return string.IsNullOrWhiteSpace(value) ? DefaultProfileName : value;
        }

        private PersistenceResult SaveProfileNamesWithResult(IEnumerable<string> profileNames)
        {
            if (profileNames == null)
                return PersistenceResult.Failed("No layout profile names were supplied.");

            try
            {
                Directory.CreateDirectory(_profilesDir);
                var names = profileNames
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(NormalizeProfileName)
                    .Append(DefaultProfileName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n.Equals(DefaultProfileName, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return JsonFilePersistence.Save(_indexPath, names, value => value != null);
            }
            catch (Exception ex)
            {
                return PersistenceResult.Failed($"Unable to prepare the layout profile index: {ex.Message}");
            }
        }

        private PersistenceResult DeleteProfileArtifacts(string profileName)
        {
            string layoutPath;
            string profilesDirectory;
            try
            {
                layoutPath = Path.GetFullPath(GetLayoutPath(profileName));
                profilesDirectory = Path.GetFullPath(_profilesDir);
                if (!PathEquals(Path.GetDirectoryName(layoutPath), profilesDirectory))
                    return PersistenceResult.Failed("The resolved layout profile path is outside the profiles directory.");
            }
            catch (Exception ex)
            {
                return PersistenceResult.Failed($"Unable to resolve layout profile files: {ex.Message}");
            }

            var identityPath = LayoutIdentityStore.GetIdentityPath(layoutPath);
            var filesToDelete = new List<string>
            {
                AtomicFileWriter.GetBackupPath(identityPath),
                identityPath,
                AtomicFileWriter.GetBackupPath(layoutPath)
            };

            try
            {
                if (Directory.Exists(profilesDirectory))
                {
                    var fileName = Path.GetFileName(layoutPath);
                    foreach (var candidate in Directory.EnumerateFiles(
                                 profilesDirectory,
                                 $"{fileName}.autosave-*.bak",
                                 SearchOption.TopDirectoryOnly))
                    {
                        var fullCandidate = Path.GetFullPath(candidate);
                        if (!IsExpectedAutoSaveBackup(layoutPath, fullCandidate))
                            continue;

                        filesToDelete.Add(fullCandidate);
                        var backupIdentityPath = LayoutIdentityStore.GetIdentityPath(fullCandidate);
                        filesToDelete.Add(AtomicFileWriter.GetBackupPath(backupIdentityPath));
                        filesToDelete.Add(backupIdentityPath);
                    }
                }

                filesToDelete.Add(layoutPath);

                var existingFiles = new List<string>();
                foreach (var candidate in filesToDelete.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var fullCandidate = Path.GetFullPath(candidate);
                    if (!PathEquals(Path.GetDirectoryName(fullCandidate), profilesDirectory))
                        return PersistenceResult.Failed($"Refused to delete a file outside the profiles directory: {fullCandidate}");

                    if (File.Exists(fullCandidate))
                        existingFiles.Add(fullCandidate);
                }

                var operationId = Guid.NewGuid().ToString("N");
                var stagedFiles = new List<(string OriginalPath, string StagedPath)>();
                try
                {
                    foreach (var originalPath in existingFiles)
                    {
                        var stagedPath = Path.Combine(
                            profilesDirectory,
                            $".{Path.GetFileName(originalPath)}.{operationId}.deleting");
                        File.Move(originalPath, stagedPath);
                        stagedFiles.Add((originalPath, stagedPath));
                    }
                }
                catch (Exception stagingException)
                {
                    var rollbackErrors = new List<string>();
                    foreach (var staged in stagedFiles.AsEnumerable().Reverse())
                    {
                        try
                        {
                            if (File.Exists(staged.StagedPath))
                                File.Move(staged.StagedPath, staged.OriginalPath);
                        }
                        catch (Exception rollbackException)
                        {
                            rollbackErrors.Add(
                                $"Could not restore '{staged.OriginalPath}': {rollbackException.Message}");
                        }
                    }

                    var rollbackDetail = rollbackErrors.Count == 0
                        ? string.Empty
                        : $" Rollback issue(s): {string.Join(" ", rollbackErrors)}";
                    return PersistenceResult.Failed(
                        $"Unable to stage layout profile '{profileName}' for deletion: " +
                        $"{stagingException.Message}.{rollbackDetail}");
                }

                var cleanupWarnings = new List<string>();
                foreach (var staged in stagedFiles)
                {
                    try
                    {
                        File.Delete(staged.StagedPath);
                    }
                    catch (Exception cleanupException)
                    {
                        cleanupWarnings.Add(
                            $"A staged deleted artefact remains at '{staged.StagedPath}': {cleanupException.Message}");
                    }
                }

                return PersistenceResult.Saved(
                    changed: stagedFiles.Count > 0,
                    warningMessage: string.Join(" ", cleanupWarnings));
            }
            catch (Exception ex)
            {
                return PersistenceResult.Failed($"Unable to delete layout profile '{profileName}': {ex.Message}");
            }
        }

        private static bool IsExpectedAutoSaveBackup(string layoutPath, string candidatePath)
        {
            var expectedDirectory = Path.GetDirectoryName(Path.GetFullPath(layoutPath));
            var candidateDirectory = Path.GetDirectoryName(Path.GetFullPath(candidatePath));
            if (!PathEquals(expectedDirectory, candidateDirectory))
                return false;

            var layoutFileName = Path.GetFileName(layoutPath);
            var candidateFileName = Path.GetFileName(candidatePath);
            var pattern =
                $"^{Regex.Escape(layoutFileName)}\\.autosave-\\d{{8}}-\\d{{6}}" +
                $"(?:-(?:\\d+|[0-9a-fA-F]{{32}}))?\\.bak$";
            return Regex.IsMatch(candidateFileName, pattern, RegexOptions.CultureInvariant);
        }

        private static bool PathEquals(string? left, string? right)
            => !string.IsNullOrWhiteSpace(left) &&
               !string.IsNullOrWhiteSpace(right) &&
               Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar)
                   .Equals(
                       Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
                       StringComparison.OrdinalIgnoreCase);

        private static string CombineWarnings(string first, string second)
        {
            if (string.IsNullOrWhiteSpace(first)) return second ?? string.Empty;
            if (string.IsNullOrWhiteSpace(second)) return first;
            return $"{first} {second}";
        }

        private static string SanitizeFileName(string name)
            => NormalizeProfileName(name);
    }
}
