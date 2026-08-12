using System;
using System.IO;

namespace WorkMonitorSwitcher.Services
{
    internal static class AtomicFileWriter
    {
        public static void WriteAllText(string path, string contents)
        {
            var result = WriteAllTextWithResult(path, contents);
            if (!result.Success)
                throw new IOException(result.ErrorMessage);
        }

        public static PersistenceResult WriteAllTextWithResult(
            string path,
            string contents,
            Func<string, bool>? existingContentValidator = null)
        {
            if (string.IsNullOrWhiteSpace(path))
                return PersistenceResult.Failed("A file path is required.");
            if (contents == null)
                return PersistenceResult.Failed("File contents are required.");

            string fullPath;
            string directory;
            try
            {
                fullPath = Path.GetFullPath(path);
                directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(directory))
                    return PersistenceResult.Failed("The file path must include a directory.");

                Directory.CreateDirectory(directory);
            }
            catch (Exception ex)
            {
                return PersistenceResult.Failed($"Unable to prepare '{path}': {ex.Message}");
            }

            try
            {
                if (File.Exists(fullPath) &&
                    string.Equals(File.ReadAllText(fullPath), contents, StringComparison.Ordinal))
                {
                    return PersistenceResult.Unchanged();
                }
            }
            catch
            {
                // A read failure is handled by the atomic replacement below.
            }

            var backupPath = GetBackupPath(fullPath);
            bool preserveExistingBackup =
                existingContentValidator != null &&
                File.Exists(fullPath) &&
                File.Exists(backupPath) &&
                !IsValidFile(fullPath, existingContentValidator) &&
                IsValidFile(backupPath, existingContentValidator);

            var tempPath = BuildTemporaryPath(directory, Path.GetFileName(fullPath), "tmp");
            var replacementBackupPath = BuildTemporaryPath(directory, Path.GetFileName(fullPath), "backup.tmp");

            try
            {
                File.WriteAllText(tempPath, contents);

                if (!File.Exists(fullPath))
                {
                    File.Move(tempPath, fullPath);
                    return PersistenceResult.Saved();
                }

                File.Replace(
                    tempPath,
                    fullPath,
                    replacementBackupPath,
                    ignoreMetadataErrors: true);

                if (preserveExistingBackup)
                {
                    TryDelete(replacementBackupPath);
                    return PersistenceResult.Saved();
                }

                try
                {
                    File.Move(replacementBackupPath, backupPath, overwrite: true);
                    return PersistenceResult.Saved();
                }
                catch (Exception ex)
                {
                    return PersistenceResult.Saved(
                        warningMessage:
                        $"The primary file was saved, but its backup could not be updated. " +
                        $"The previous primary remains at '{replacementBackupPath}': {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                TryDelete(tempPath);
                TryDelete(replacementBackupPath);
                return PersistenceResult.Failed($"Unable to save '{fullPath}': {ex.Message}");
            }
        }

        public static PersistenceResult RestorePrimaryPreservingBackup(string path, string contents)
        {
            if (string.IsNullOrWhiteSpace(path))
                return PersistenceResult.Failed("A file path is required.");
            if (contents == null)
                return PersistenceResult.Failed("File contents are required.");

            string fullPath;
            string directory;
            try
            {
                fullPath = Path.GetFullPath(path);
                directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(directory))
                    return PersistenceResult.Failed("The file path must include a directory.");

                Directory.CreateDirectory(directory);

                if (File.Exists(fullPath) &&
                    string.Equals(File.ReadAllText(fullPath), contents, StringComparison.Ordinal))
                {
                    return PersistenceResult.Unchanged();
                }
            }
            catch (Exception ex)
            {
                return PersistenceResult.Failed($"Unable to prepare '{path}' for recovery: {ex.Message}");
            }

            var tempPath = BuildTemporaryPath(directory, Path.GetFileName(fullPath), "recovery.tmp");
            try
            {
                File.WriteAllText(tempPath, contents);
                File.Move(tempPath, fullPath, overwrite: true);
                return PersistenceResult.Saved();
            }
            catch (Exception ex)
            {
                TryDelete(tempPath);
                return PersistenceResult.Failed($"Unable to recover '{fullPath}': {ex.Message}");
            }
        }

        public static PersistenceResult SynchronizeBackupWithPrimary(
            string path,
            Func<string, bool> primaryContentValidator)
        {
            if (string.IsNullOrWhiteSpace(path))
                return PersistenceResult.Failed("A file path is required.");
            if (primaryContentValidator == null)
                return PersistenceResult.Failed("A primary file validator is required.");

            string fullPath;
            string directory;
            string contents;
            try
            {
                fullPath = Path.GetFullPath(path);
                directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(directory) || !File.Exists(fullPath))
                    return PersistenceResult.Failed($"The primary file does not exist: {fullPath}");

                contents = File.ReadAllText(fullPath);
                if (!primaryContentValidator(contents))
                    return PersistenceResult.Failed($"The primary file is not valid: {fullPath}");
            }
            catch (Exception ex)
            {
                return PersistenceResult.Failed($"Unable to read '{path}' before updating its backup: {ex.Message}");
            }

            var backupPath = GetBackupPath(fullPath);
            try
            {
                if (File.Exists(backupPath) &&
                    string.Equals(File.ReadAllText(backupPath), contents, StringComparison.Ordinal))
                {
                    return PersistenceResult.Unchanged();
                }
            }
            catch
            {
                // Replace an unreadable backup below.
            }

            var tempPath = BuildTemporaryPath(directory, Path.GetFileName(backupPath), "backup-sync.tmp");
            try
            {
                File.WriteAllText(tempPath, contents);
                File.Move(tempPath, backupPath, overwrite: true);
                return PersistenceResult.Saved();
            }
            catch (Exception ex)
            {
                TryDelete(tempPath);
                return PersistenceResult.Failed($"Unable to update '{backupPath}': {ex.Message}");
            }
        }

        public static string GetBackupPath(string path)
            => Path.GetFullPath(path) + ".bak";

        private static string BuildTemporaryPath(string directory, string fileName, string suffix)
            => Path.Combine(directory, $".{fileName}.{Guid.NewGuid():N}.{suffix}");

        private static bool IsValidFile(string path, Func<string, bool> validator)
        {
            try
            {
                return validator(File.ReadAllText(path));
            }
            catch
            {
                return false;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }
}
