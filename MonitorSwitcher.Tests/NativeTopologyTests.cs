using System;
using System.Collections.Generic;
using System.Linq;
using WorkMonitorSwitcher.Services;

internal static class NativeTopologyTests
{
    internal static IEnumerable<(string Name, Action Body)> GetTests()
    {
        yield return ("Native profile round-trips arbitrary extended displays", ProfileRoundTrips);
        yield return ("Native profile rejects clones, weak targets, and duplicate settings", ProfileRejectsUnsafeInput);
        yield return ("Invalid declared native profiles never downgrade to legacy matching", InvalidNativeProfileFailsClosed);
        yield return ("Native enable selects one deterministic present route", EnableSelectionFailsClosed);
        yield return ("Native exact restore selects a unique non-clone route assignment", ExactRouteSelectionIsUnique);
        yield return ("Native topology flags separate database lookup from persistent best mode", NativeFlagsAreCorrect);
        yield return ("Native topology mutation rejects stale display-name bindings", StaleDetectionBindingFailsClosed);
        yield return ("Native enable placement uses rotated desktop width", EnablePlacementUsesRotatedDesktopWidth);
    }

    private static void EnablePlacementUsesRotatedDesktopWidth()
    {
        AssertEqual(2560,
            DisplayTopologyService.CalculateEffectiveDesktopWidth(2560, 1440, rotation: 1),
            "A landscape display should use its source width.");
        AssertEqual(1440,
            DisplayTopologyService.CalculateEffectiveDesktopWidth(2560, 1440, rotation: 4),
            "A portrait display should use its rotated desktop width.");

        const int portraitX = 2560;
        var rightEdge = portraitX +
                        DisplayTopologyService.CalculateEffectiveDesktopWidth(2560, 1440, rotation: 4);
        AssertEqual(4000, rightEdge,
            "The next display should be placed after the portrait display's effective 1440-pixel width.");
    }

    private static void StaleDetectionBindingFailsClosed()
    {
        var targetA = Target("A");
        var targetB = Target("B");
        var detected = new[]
        {
            (DeviceName: @"\\.\DISPLAY1", TargetPath: targetA),
            (DeviceName: @"\\.\DISPLAY2", TargetPath: targetB)
        };

        AssertTrue(
            DisplayTopologyService.HaveExactDetectionTopologyBindings(detected, detected),
            "An unchanged one-to-one detection/topology binding should be accepted.");
        AssertFalse(
            DisplayTopologyService.HaveExactDetectionTopologyBindings(
                detected,
                new[]
                {
                    (DeviceName: @"\\.\DISPLAY1", TargetPath: targetB),
                    (DeviceName: @"\\.\DISPLAY2", TargetPath: targetA)
                }),
            "A DISPLAY-name reassignment between detection and mutation must fail closed.");
        AssertFalse(
            DisplayTopologyService.HaveExactDetectionTopologyBindings(
                detected,
                detected.Take(1)),
            "A topology-count change between detection and mutation must fail closed.");
    }

    private static void ProfileRoundTrips()
    {
        var monitors = new List<NativeDisplayProfileMonitor>
        {
            Monitor(@"\\.\DISPLAY4", Target("A"), 11, 0, 11, 4, 0, 0, 1920, 1080, 1, true),
            Monitor(@"\\.\DISPLAY9", Target("B"), 11, 1, 11, 7, 1920, -400, 2160, 3840, 2, false),
            Monitor(@"\\.\DISPLAY12", Target("C"), 22, 0, 22, 3, -2560, 180, 2560, 1440, 1, false)
        };
        var contents = NativeDisplayProfileCodec.Serialise(
            new NativeDisplayProfile(NativeDisplayProfileCodec.CurrentVersion, monitors));

        AssertTrue(NativeDisplayProfileCodec.TryParse(contents, out var parsed, out var error), error);
        AssertEqual(1, parsed.Version, "Expected current native profile version.");
        AssertEqual(3, parsed.Monitors.Count, "Expected every monitor to round-trip.");
        AssertEqual(@"\\.\DISPLAY4", parsed.Monitors.Single(m => m.IsPrimary).LayoutDeviceName,
            "Expected primary identity to round-trip.");
        AssertEqual(2u, parsed.Monitors.Single(m => m.LayoutDeviceName.EndsWith("9", StringComparison.Ordinal)).Rotation,
            "Expected portrait rotation to round-trip.");
        AssertEqual(-400, parsed.Monitors.Single(m => m.LayoutDeviceName.EndsWith("9", StringComparison.Ordinal)).Y,
            "Expected vertical offset to round-trip.");
    }

    private static void ProfileRejectsUnsafeInput()
    {
        var clone = new NativeDisplayProfile(
            NativeDisplayProfileCodec.CurrentVersion,
            new[]
            {
                Monitor(@"\\.\DISPLAY1", Target("A"), 1, 0, 1, 1, 0, 0, 1920, 1080, 1, true),
                Monitor(@"\\.\DISPLAY2", Target("B"), 1, 0, 1, 2, 1920, 0, 1920, 1080, 1, false)
            });
        var cloneText = NativeDisplayProfileCodec.Serialise(clone);
        AssertFalse(NativeDisplayProfileCodec.TryParse(cloneText, out _, out _),
            "A profile sharing one source across targets must fail as a clone.");

        var weakText = NativeDisplayProfileCodec.Serialise(new NativeDisplayProfile(
            1,
            new[] { Monitor(@"\\.\DISPLAY1", Target("A"), 1, 0, 1, 1, 0, 0, 1920, 1080, 1, true) }))
            .Replace(Target("A"), @"\\.\DISPLAY1", StringComparison.Ordinal);
        AssertFalse(NativeDisplayProfileCodec.TryParse(weakText, out _, out _),
            "A saved DISPLAY number is not a strong physical target identity.");

        var duplicateText = NativeDisplayProfileCodec.Serialise(new NativeDisplayProfile(
            1,
            new[] { Monitor(@"\\.\DISPLAY1", Target("A"), 1, 0, 1, 1, 0, 0, 1920, 1080, 1, true) }))
            .Replace("PositionX=0", "PositionX=0\r\nPositionX=20", StringComparison.Ordinal);
        AssertFalse(NativeDisplayProfileCodec.TryParse(duplicateText, out _, out _),
            "Duplicate topology settings must fail closed.");
    }

    private static void EnableSelectionFailsClosed()
    {
        var targetPath = Target("A");
        var candidates = new[]
        {
            Path(0, targetPath, string.Empty, 1, 0, 1, 5, false, true),
            Path(1, targetPath, string.Empty, 1, 1, 1, 5, false, true)
        };
        var deterministic = NativeDisplayPathSelector.SelectEnablePath(
            targetPath,
            candidates,
            Array.Empty<(long, uint)>());
        AssertTrue(deterministic.Success, deterministic.ErrorMessage);
        AssertEqual(0, deterministic.Paths.Single().PathIndex,
            "Multiple free routes to one exact physical target must be selected deterministically.");

        var unique = NativeDisplayPathSelector.SelectEnablePath(
            targetPath,
            candidates,
            new[] { (1L, 0u) });
        AssertTrue(unique.Success, unique.ErrorMessage);
        AssertEqual(1, unique.Paths.Single().PathIndex, "Expected the only unoccupied route.");

        var friendly = NativeDisplayPathSelector.SelectEnablePath(
            "Office monitor",
            candidates,
            new[] { (1L, 0u) });
        AssertFalse(friendly.Success, "A friendly-name guess must never select an inactive path.");

        var absent = NativeDisplayPathSelector.SelectEnablePath(
            targetPath,
            candidates.Select(candidate => candidate with { IsAvailable = false }).ToList(),
            Array.Empty<(long, uint)>());
        AssertFalse(absent.Success, "A disconnected target must never be activated.");
    }

    private static void InvalidNativeProfileFailsClosed()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "MonitorSwitcher.Tests",
            Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            var path = System.IO.Path.Combine(directory, "invalid-native.cfg");
            System.IO.File.WriteAllText(path, string.Join(Environment.NewLine, new[]
            {
                "NativeProfileVersion =1",
                "[Monitor0]",
                @"Name=\\.\DISPLAY1",
                @"MonitorDevicePath=\\.\DISPLAY1",
                "SourceAdapterLuid=1",
                "SourceId=0",
                "TargetAdapterLuid=1",
                "TargetId=1",
                "PositionX=0",
                "PositionY=0",
                "Width=1920",
                "Height=1080",
                "BitsPerPixel=32",
                "DisplayOrientation=0",
                "Primary=1"
            }));

            var detected = new[]
            {
                new WorkMonitorSwitcher.Model.DetectedMonitor
                {
                    DeviceName = @"\\.\DISPLAY1",
                    NativeTargetPath = Target("A"),
                    StableKey = "SN:SERIAL-A",
                    SerialNumber = "SERIAL-A",
                    IsActive = true,
                    IsPresent = true
                }
            };
            var identities = new[]
            {
                new SavedLayoutIdentity
                {
                    LayoutDeviceName = @"\\.\DISPLAY1",
                    DeviceName = @"\\.\DISPLAY1",
                    NativeTargetPath = Target("A"),
                    StableKey = "SN:SERIAL-A",
                    SerialNumber = "SERIAL-A"
                }
            };

            AssertFalse(
                DisplayTopologyService.IsExactSavedMonitorSetActive(path, detected, identities),
                "A corrupt declared native profile must not be treated as a legacy DISPLAY-name profile.");
        }
        finally
        {
            System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    private static void ExactRouteSelectionIsUnique()
    {
        var a = Target("A");
        var b = Target("B");
        var requests = new[]
        {
            new NativePathRequest(a, 1, 0, 1, 5),
            new NativePathRequest(b, 1, 1, 1, 6)
        };
        var candidates = new[]
        {
            Path(0, a, string.Empty, 1, 0, 1, 5, false, true),
            Path(1, a, string.Empty, 1, 1, 1, 5, false, true),
            Path(2, b, string.Empty, 1, 0, 1, 6, false, true),
            Path(3, b, string.Empty, 1, 1, 1, 6, false, true)
        };

        var exact = NativeDisplayPathSelector.SelectExactPaths(requests, candidates);
        AssertTrue(exact.Success, exact.ErrorMessage);
        AssertEqual(2, exact.Paths.Count, "Expected one path per requested target.");
        AssertEqual(2, exact.Paths.Select(path => (path.SourceAdapterLuid, path.SourceId)).Distinct().Count(),
            "Exact restore must never assign two targets to one source.");

        var ambiguousRequests = requests.Select(request => request with
        {
            SavedSourceAdapterLuid = 9,
            SavedSourceId = 9,
            SavedTargetAdapterLuid = 9,
            SavedTargetId = 9
        }).ToList();
        var deterministic = NativeDisplayPathSelector.SelectExactPaths(ambiguousRequests, candidates);
        AssertTrue(deterministic.Success, deterministic.ErrorMessage);
        AssertEqual(2, deterministic.Paths.Count,
            "Equivalent source-route choices must still realise every exact physical target.");

        var activeConflictCandidates = new[]
        {
            Path(0, a, @"\\.\DISPLAY1", 1, 0, 1, 5, true, true),
            Path(1, a, string.Empty, 1, 1, 1, 5, false, true),
            Path(2, b, string.Empty, 1, 0, 1, 6, false, true)
        };
        var rerouted = NativeDisplayPathSelector.SelectExactPaths(requests, activeConflictCandidates);
        AssertTrue(rerouted.Success, rerouted.ErrorMessage);
        AssertEqual(1u, rerouted.Paths.Single(path => path.MonitorDevicePath == a).SourceId,
            "An active route must be allowed to move when another exact target has no other free source.");
    }

    private static void NativeFlagsAreCorrect()
    {
        const uint qdcAllPaths = 0x00000001;
        const uint topologySupplied = 0x00000010;
        const uint useSupplied = 0x00000020;
        const uint validate = 0x00000040;
        const uint apply = 0x00000080;
        const uint saveToDatabase = 0x00000200;
        const uint allowChanges = 0x00000400;
        const uint allowPathOrderChanges = 0x00002000;

        AssertEqual(qdcAllPaths, DisplayTopologyService.GetAllPathsQueryFlags(),
            "Expected QDC_ALL_PATHS for route discovery.");
        AssertEqual(topologySupplied | allowPathOrderChanges | validate,
            DisplayTopologyService.GetTopologyActivationValidateFlags(),
            "Expected topology database validation flags.");
        AssertEqual(topologySupplied | allowPathOrderChanges | apply,
            DisplayTopologyService.GetTopologyActivationApplyFlags(),
            "Expected topology database apply flags.");
        AssertEqual(useSupplied | allowChanges | validate,
            DisplayTopologyService.GetBestModeActivationValidateFlags(),
            "Expected safe best-mode validation fallback.");
        AssertEqual(useSupplied | allowChanges | saveToDatabase | apply,
            DisplayTopologyService.GetBestModeActivationApplyFlags(),
            "Expected best-mode fallback to persist.");
    }

    private static NativeDisplayProfileMonitor Monitor(
        string name, string path, long sourceAdapter, uint sourceId,
        long targetAdapter, uint targetId, int x, int y, int width, int height,
        uint rotation, bool primary)
        => new(name, path, sourceAdapter, sourceId, targetAdapter, targetId,
            x, y, width, height, rotation, primary, "Test display", 1, 2, 0);

    private static NativePathCandidate Path(
        int index, string path, string source, long sourceAdapter, uint sourceId,
        long targetAdapter, uint targetId, bool active, bool available)
        => new(index, path, source, sourceAdapter, sourceId, targetAdapter, targetId, active, available);

    private static string Target(string suffix)
        => $@"\\?\DISPLAY#TEST{suffix}#INSTANCE{suffix}#{{4d36e96e-e325-11ce-bfc1-08002be10318}}";

    private static void AssertTrue(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void AssertFalse(bool condition, string message) => AssertTrue(!condition, message);

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message} Expected '{expected}', got '{actual}'.");
    }
}
