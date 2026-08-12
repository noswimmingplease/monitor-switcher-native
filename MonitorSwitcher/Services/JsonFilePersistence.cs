using System;
using System.IO;
using System.Text.Json;

namespace WorkMonitorSwitcher.Services
{
    internal sealed class JsonLoadResult<T>
    {
        public bool Success { get; init; }
        public T? Value { get; init; }
        public bool RecoveredFromBackup { get; init; }
        public bool PrimaryRepaired { get; init; }
        public string ErrorMessage { get; init; } = string.Empty;
        public string WarningMessage { get; init; } = string.Empty;
    }

    internal static class JsonFilePersistence
    {
        private static readonly JsonSerializerOptions IndentedJson = new()
        {
            WriteIndented = true
        };

        public static JsonLoadResult<T> Load<T>(
            string path,
            Func<T, bool>? validator = null)
        {
            var primary = TryRead(path, validator);
            if (primary.Success)
            {
                return new JsonLoadResult<T>
                {
                    Success = true,
                    Value = primary.Value
                };
            }

            var backupPath = AtomicFileWriter.GetBackupPath(path);
            var backup = TryRead(backupPath, validator);
            if (!backup.Success)
            {
                return new JsonLoadResult<T>
                {
                    Success = false,
                    ErrorMessage = CombineErrors(primary.ErrorMessage, backup.ErrorMessage)
                };
            }

            var repair = AtomicFileWriter.RestorePrimaryPreservingBackup(path, backup.Contents);
            return new JsonLoadResult<T>
            {
                Success = true,
                Value = backup.Value,
                RecoveredFromBackup = true,
                PrimaryRepaired = repair.Success,
                WarningMessage = repair.Success
                    ? string.Empty
                    : $"Loaded the backup but could not repair the primary file: {repair.ErrorMessage}"
            };
        }

        public static PersistenceResult Save<T>(
            string path,
            T value,
            Func<T, bool>? validator = null)
        {
            if (value == null)
                return PersistenceResult.Failed($"No data was supplied for '{path}'.");
            if (validator != null && !validator(value))
                return PersistenceResult.Failed($"The data supplied for '{path}' is not valid.");

            string json;
            try
            {
                json = JsonSerializer.Serialize(value, IndentedJson);
            }
            catch (Exception ex)
            {
                return PersistenceResult.Failed($"Unable to serialise '{path}': {ex.Message}");
            }

            bool IsValidJson(string contents)
                => TryDeserialize(contents, validator, out _);

            return AtomicFileWriter.WriteAllTextWithResult(path, json, IsValidJson);
        }

        private static ReadAttempt<T> TryRead<T>(string path, Func<T, bool>? validator)
        {
            if (string.IsNullOrWhiteSpace(path))
                return ReadAttempt<T>.Failure("A JSON file path is required.");

            if (!File.Exists(path))
                return ReadAttempt<T>.Failure($"File not found: {path}");

            try
            {
                var contents = File.ReadAllText(path);
                if (!TryDeserialize(contents, validator, out var value))
                    return ReadAttempt<T>.Failure($"File is not valid JSON for the expected data: {path}");

                return ReadAttempt<T>.Succeeded(value!, contents);
            }
            catch (Exception ex)
            {
                return ReadAttempt<T>.Failure($"Unable to read '{path}': {ex.Message}");
            }
        }

        internal static bool TryDeserialize<T>(
            string contents,
            Func<T, bool>? validator,
            out T? value)
        {
            value = default;
            try
            {
                value = JsonSerializer.Deserialize<T>(contents);
                return value != null && (validator == null || validator(value));
            }
            catch
            {
                return false;
            }
        }

        private static string CombineErrors(string primaryError, string backupError)
            => $"Primary: {primaryError} Backup: {backupError}".Trim();

        private sealed class ReadAttempt<TValue>
        {
            public bool Success { get; private init; }
            public TValue? Value { get; private init; }
            public string Contents { get; private init; } = string.Empty;
            public string ErrorMessage { get; private init; } = string.Empty;

            public static ReadAttempt<TValue> Succeeded(TValue value, string contents)
                => new() { Success = true, Value = value, Contents = contents };

            public static ReadAttempt<TValue> Failure(string errorMessage)
                => new() { ErrorMessage = errorMessage };
        }
    }
}
