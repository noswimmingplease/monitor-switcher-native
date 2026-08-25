using System;
using System.Collections.Generic;
using WorkMonitorSwitcher;
using WorkMonitorSwitcher.UI;

internal static class FormAccessibilityHardeningTests
{
    internal static IEnumerable<(string Name, Action Body)> GetTests()
    {
        yield return ("Form1 scales logical metrics for the active monitor DPI", ScaleLogicalMetrics);
        yield return ("Profile Apply requires healthy profile transactions", ProfileApplyRequiresHealthyTransactions);
        yield return ("Profile restore fails closed during topology quarantine", ProfileRestoreFailsClosedDuringTopologyQuarantine);
        yield return ("Profile membership preview describes every set change", ProfilePreviewDescribesSetChanges);
        yield return ("Profile selection status uses concise action wording", ProfileSelectionStatusUsesConciseActionWording);
        yield return ("Application icon resource is bundled and readable", ApplicationIconIsBundled);
    }

    private static void ScaleLogicalMetrics()
    {
        AssertEqual(14, Form1.ScaleLogicalValue(14, 96));
        AssertEqual(21, Form1.ScaleLogicalValue(14, 144));
        AssertEqual(28, Form1.ScaleLogicalValue(14, 192));
    }

    private static void ProfileApplyRequiresHealthyTransactions()
    {
        if (Form1.ShouldEnableProfileApply(
                detectionReliable: true,
                profilePairValid: true,
                exactSetActive: false,
                displayActionAvailable: true,
                profileTransactionsHealthy: false))
        {
            throw new InvalidOperationException("Apply was enabled while profile transactions were unhealthy.");
        }
    }

    private static void ProfileRestoreFailsClosedDuringTopologyQuarantine()
    {
        if (Form1.CanAttemptProfileRestore(topologyActionsQuarantined: true))
            throw new InvalidOperationException("Profile restore was allowed during topology quarantine.");
        if (!Form1.CanAttemptProfileRestore(topologyActionsQuarantined: false))
            throw new InvalidOperationException("Profile restore was blocked without topology quarantine.");
    }

    private static void ProfilePreviewDescribesSetChanges()
    {
        var preview = Form1.FormatProfileMembershipPreview(
            new[] { "Left" },
            new[] { "Right" },
            unavailableCount: 1);
        AssertContains(preview, "Enable: Left");
        AssertContains(preview, "Disable: Right");
        AssertContains(preview, "Unavailable saved monitor: 1");
    }

    private static void ProfileSelectionStatusUsesConciseActionWording()
    {
        AssertEqual(
            "Current profile",
            Form1.FormatProfileSelectionStatus(
                exactSetActive: true,
                applyEnabled: false,
                unavailableReason: "unused"));
        AssertEqual(
            "Click Apply to use this profile",
            Form1.FormatProfileSelectionStatus(
                exactSetActive: false,
                applyEnabled: true,
                unavailableReason: "unused"));
        AssertEqual(
            "Profile unavailable",
            Form1.FormatProfileSelectionStatus(
                exactSetActive: false,
                applyEnabled: false,
                unavailableReason: "Profile unavailable"));
    }

    private static void ApplicationIconIsBundled()
    {
        if (!AppIcon.HasBundledResource)
            throw new InvalidOperationException("The application icon is not embedded in the application assembly.");

        var icon = AppIcon.Current;
        if (icon.Width < 16 || icon.Height < 16)
            throw new InvalidOperationException($"The bundled application icon is unexpectedly small: {icon.Width}x{icon.Height}.");
    }

    private static void AssertEqual(int expected, int actual)
    {
        if (expected != actual)
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }

    private static void AssertEqual(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }

    private static void AssertContains(string value, string expected)
    {
        if (!value.Contains(expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"Expected '{value}' to contain '{expected}'.");
    }
}
