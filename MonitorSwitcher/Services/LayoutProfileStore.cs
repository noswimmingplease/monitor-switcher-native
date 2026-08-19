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

            var deletionRecovery = LayoutProfileDeletionTransaction.Recover(
                _profilesDir,
                _indexPath);
            if (!deletionRecovery.Success)
                errors.Add(deletionRecovery.ErrorMessage);
            else if (!string.IsNullOrWhiteSpace(deletionRecovery.WarningMessage))
                warnings.Add(deletionRecovery.WarningMessage);

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

            bool changed = deletionRecovery.Success && deletionRecovery.Changed;
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

            string layoutPath;
            try
            {
                layoutPath = Path.GetFullPath(GetLayoutPath(name));
                var profilesDirectory = Path.GetFullPath(_profilesDir);
                if (!PathEquals(Path.GetDirectoryName(layoutPath), profilesDirectory))
                    return PersistenceResult.Failed("The resolved layout profile path is outside the profiles directory.");
            }
            catch (Exception ex)
            {
                return PersistenceResult.Failed($"Unable to resolve layout profile files: {ex.Message}");
            }

            var saveRecovery = LayoutProfileTransaction.Recover(layoutPath);
            if (!saveRecovery.Success)
            {
                return PersistenceResult.Failed(
                    $"The profile cannot be deleted because its interrupted save could not be recovered. " +
                    saveRecovery.ErrorMessage);
            }

            string saveJournalPath = LayoutProfileTransaction.GetJournalPath(layoutPath);
            if (File.Exists(saveJournalPath))
            {
                var recoveryWarning = string.IsNullOrWhiteSpace(saveRecovery.WarningMessage)
                    ? string.Empty
                    : $" {saveRecovery.WarningMessage}";
                return PersistenceResult.Failed(
                    "The profile cannot be deleted while its completed save journal still requires cleanup." +
                    recoveryWarning);
            }

            var originalNames = LoadProfileNames();
            var remainingNames = originalNames
                .Where(n => !n.Equals(name, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var deletion = DeleteProfileArtifacts(name, originalNames, remainingNames);
            if (!deletion.Success || string.IsNullOrWhiteSpace(saveRecovery.WarningMessage))
                return deletion;

            var warning = string.Join(
                " ",
                new[] { saveRecovery.WarningMessage, deletion.WarningMessage }
                    .Where(message => !string.IsNullOrWhiteSpace(message)));
            return PersistenceResult.Saved(
                changed: saveRecovery.Changed || deletion.Changed,
                warningMessage: warning);
        }

        public static string NormalizeProfileName(string? profileName)
        {
            if (string.IsNullOrWhiteSpace(profileName))
                return DefaultProfileName;

            var value = profileName.Trim();
            foreach (var c in Path.GetInvalidFileNameChars())
                value = value.Replace(c, '_');

            // Win32 strips trailing spaces and full stops and treats the device
            // stem as reserved even when an extension is present (for example,
            // CON.foo). Canonicalise both cases before deriving a file path so
            // two visible profile names cannot alias the same filesystem entry.
            value = value.TrimEnd(' ', '.');
            if (string.IsNullOrWhiteSpace(value))
                return DefaultProfileName;

            if (value.Equals(DefaultProfileName, StringComparison.OrdinalIgnoreCase))
                return DefaultProfileName;

            var reservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "CON", "PRN", "AUX", "NUL",
                "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
                "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
            };
            var deviceStem = value.Split('.', 2)[0];
            if (reservedNames.Contains(deviceStem))
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

        private PersistenceResult DeleteProfileArtifacts(
            string profileName,
            IReadOnlyCollection<string> originalNames,
            IReadOnlyCollection<string> remainingNames)
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

                return LayoutProfileDeletionTransaction.Delete(
                    profilesDirectory,
                    _indexPath,
                    profileName,
                    originalNames,
                    remainingNames,
                    existingFiles);
            }
            catch (Exception ex)
            {
                return PersistenceResult.Failed($"Unable to delete layout profile '{profileName}': {ex.Message}");
            }
        }

        internal static bool IsExpectedAutoSaveBackup(string layoutPath, string candidatePath)
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

        private static string SanitizeFileName(string name)
            => NormalizeProfileName(name);
    }
}
