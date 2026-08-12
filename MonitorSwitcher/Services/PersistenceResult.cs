namespace WorkMonitorSwitcher.Services
{
    internal readonly record struct PersistenceResult(
        bool Success,
        bool Changed,
        string ErrorMessage = "",
        string WarningMessage = "")
    {
        public static PersistenceResult Unchanged()
            => new(true, false);

        public static PersistenceResult Saved(bool changed = true, string warningMessage = "")
            => new(true, changed, WarningMessage: warningMessage);

        public static PersistenceResult Failed(string errorMessage)
            => new(false, false, errorMessage ?? string.Empty);
    }
}
