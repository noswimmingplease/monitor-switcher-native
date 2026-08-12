using System;
using System.Collections.Generic;
using System.Linq;
using WorkMonitorSwitcher.Services;

internal static class NativeDetectionTests
{
    internal static void RunAll(Action<string, Action> register)
    {
        register("Native detection parses monitor interface paths", ParsesMonitorInterfacePaths);
        register("Native detection decodes EDID identity", DecodesEdidIdentity);
        register("Native detection consolidates arbitrary display counts", ConsolidatesArbitraryDisplayCounts);
        register("Native detection deduplicates alternative CCD paths", DeduplicatesAlternativePaths);
        register("Native detection rejects conflicting endpoint identities", RejectsConflictingEndpointIdentities);
        register("Native detection rejects multiple active endpoints for one monitor", RejectsMultipleActiveEndpoints);
        register("Native detection rejects active paths whose target is unavailable", RejectsUnavailableActiveTarget);
        register("Native detection rejects active paths without a strong target identity", RejectsUnidentifiedActiveTarget);
        register("Native detection rejects duplicate credible serial identities", RejectsDuplicateCredibleSerials);
        register("Native detection decodes source modes from each path's virtual-mode flag", DecodesVirtualSourceModeIndexPerPath);
    }

    private static void ParsesMonitorInterfacePaths()
    {
        const string path =
            @"\\?\DISPLAY#TST2703#5&00000001&0&UID1001#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

        AssertTrue(
            NativeDisplayDetection.TryParseMonitorDevicePath(path, out var hardware, out var instance),
            "Expected the documented monitor-interface path form to parse.");
        AssertEqual("TST2703", hardware);
        AssertEqual("5&00000001&0&UID1001", instance);

        AssertFalse(
            NativeDisplayDetection.TryParseMonitorDevicePath(
                @"\\?\DISPLAY#..#instance#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}",
                out _,
                out _),
            "Unsafe registry path segments must be rejected.");
    }

    private static void DecodesEdidIdentity()
    {
        var edid = new byte[128];
        byte[] header = { 0x00, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x00 };
        Array.Copy(header, edid, header.Length);

        // EISA manufacturer AOC and little-endian product 0x2703.
        edid[8] = 0x05;
        edid[9] = 0xe3;
        edid[10] = 0x03;
        edid[11] = 0x27;
        WriteDescriptor(edid, 54, 0xff, "TESTSERIAL001");
        WriteDescriptor(edid, 72, 0xfc, "Q27B3MA");
        edid[127] = unchecked((byte)(0 - edid.Take(127).Sum(value => value)));

        AssertTrue(
            NativeDisplayDetection.TryDecodeEdid(edid, out var identity),
            "Expected a valid base EDID block to decode.");
        AssertEqual("AOC", identity.Manufacturer);
        AssertEqual("2703", identity.ProductCode);
        AssertEqual("TESTSERIAL001", identity.SerialNumber);
        AssertEqual("Q27B3MA", identity.MonitorName);

        AssertFalse(
            NativeDisplayDetection.TryDecodeEdid(new byte[128], out _),
            "A block without the EDID header must fail closed.");
    }

    private static void ConsolidatesArbitraryDisplayCounts()
    {
        var candidates = Enumerable.Range(0, 12)
            .Select(index => Candidate(
                endpoint: $"GPU:TARGET:{index}",
                active: index % 2 == 0,
                available: true,
                source: index % 2 == 0 ? $@"\\.\DISPLAY{index + 1}" : string.Empty,
                path: MonitorPath($"MON{index:X4}", $"INSTANCE{index}"),
                serial: $"SERIAL-{index}",
                monitorId: $"MON{index:X4}"))
            .ToArray();

        AssertTrue(
            NativeDisplayDetection.TryConsolidateCandidates(candidates, out var monitors, out var error),
            error);
        AssertEqual(12, monitors.Count);
        AssertEqual(6, monitors.Count(monitor => monitor.IsActive));
        AssertTrue(
            monitors.Where(monitor => !monitor.IsActive)
                .All(monitor =>
                    monitor.DeviceName.Length == 0 &&
                    monitor.NativeTargetPath.StartsWith(@"\\?\DISPLAY#", StringComparison.OrdinalIgnoreCase)),
            "Inactive connected displays must retain their exact native target path without inventing a GDI source.");
    }

    private static void DeduplicatesAlternativePaths()
    {
        var path = MonitorPath("AOC2703", "INSTANCE-A");
        var candidates = new[]
        {
            Candidate("GPU:TARGET:1", false, true, @"\\.\DISPLAY4", path, "SERIAL-A", "AOC2703"),
            Candidate("GPU:TARGET:1", true, true, @"\\.\DISPLAY2", path, "SERIAL-A", "AOC2703")
        };

        AssertTrue(
            NativeDisplayDetection.TryConsolidateCandidates(candidates, out var monitors, out var error),
            error);
        AssertEqual(1, monitors.Count);
        AssertTrue(monitors[0].IsActive, "The active route must win over an inactive alternative.");
        AssertEqual(@"\\.\DISPLAY2", monitors[0].DeviceName);
        AssertEqual(path, monitors[0].NativeTargetPath);
        AssertEqual(
            @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Enum\DISPLAY\AOC2703\INSTANCE-A",
            monitors[0].MonitorKey);
    }

    private static void RejectsConflictingEndpointIdentities()
    {
        var candidates = new[]
        {
            Candidate("GPU:TARGET:1", false, true, string.Empty, MonitorPath("AOC2703", "A"), "SERIAL-A", "AOC2703"),
            Candidate("GPU:TARGET:1", false, true, string.Empty, MonitorPath("AOC2703", "B"), "SERIAL-B", "AOC2703")
        };

        AssertFalse(
            NativeDisplayDetection.TryConsolidateCandidates(candidates, out var monitors, out var error),
            "Conflicting identities for one target must not be guessed.");
        AssertEqual(0, monitors.Count);
        AssertTrue(error.Contains("conflicting", StringComparison.OrdinalIgnoreCase), error);
    }

    private static void RejectsMultipleActiveEndpoints()
    {
        var path = MonitorPath("AOC2703", "INSTANCE-A");
        var candidates = new[]
        {
            Candidate("GPU:TARGET:1", true, true, @"\\.\DISPLAY1", path, "SERIAL-A", "AOC2703"),
            Candidate("GPU:TARGET:2", true, true, @"\\.\DISPLAY2", path, "SERIAL-A", "AOC2703")
        };

        AssertFalse(
            NativeDisplayDetection.TryConsolidateCandidates(candidates, out _, out var error),
            "Multiple active target endpoints for one interface path must be rejected.");
        AssertTrue(error.Contains("ambiguous", StringComparison.OrdinalIgnoreCase), error);
    }

    private static void RejectsUnavailableActiveTarget()
    {
        var candidates = new[]
        {
            Candidate(
                "GPU:TARGET:1",
                active: true,
                available: false,
                source: @"\\.\DISPLAY1",
                path: MonitorPath("AOC2703", "INSTANCE-A"),
                serial: "SERIAL-A",
                monitorId: "AOC2703")
        };

        AssertFalse(
            NativeDisplayDetection.TryConsolidateCandidates(candidates, out var monitors, out var error),
            "An active path whose target is unavailable must make the native snapshot unreliable.");
        AssertEqual(0, monitors.Count);
        AssertTrue(error.Contains("no longer available", StringComparison.OrdinalIgnoreCase), error);
    }

    private static void RejectsUnidentifiedActiveTarget()
    {
        var candidates = new[]
        {
            Candidate(
                "GPU:TARGET:1",
                active: true,
                available: true,
                source: @"\\.\DISPLAY1",
                path: string.Empty,
                serial: string.Empty,
                monitorId: "AOC2703")
        };

        AssertFalse(
            NativeDisplayDetection.TryConsolidateCandidates(candidates, out var monitors, out var error),
            "An active path without a strong monitor device path must make the native snapshot unreliable.");
        AssertEqual(0, monitors.Count);
        AssertTrue(error.Contains("strong monitor device path", StringComparison.OrdinalIgnoreCase), error);
    }

    private static void RejectsDuplicateCredibleSerials()
    {
        var candidates = new[]
        {
            Candidate("GPU:TARGET:1", true, true, @"\\.\DISPLAY1",
                MonitorPath("AOC2703", "INSTANCE-A"), "DUPLICATE-SERIAL", "AOC2703"),
            Candidate("GPU:TARGET:2", true, true, @"\\.\DISPLAY2",
                MonitorPath("AOC2703", "INSTANCE-B"), "DUPLICATE-SERIAL", "AOC2703")
        };

        AssertFalse(
            NativeDisplayDetection.TryConsolidateCandidates(candidates, out _, out var error),
            "Duplicate credible EDID serials must fail closed instead of changing stable keys with population.");
        AssertTrue(error.Contains("same EDID serial", StringComparison.OrdinalIgnoreCase), error);
    }

    private static void DecodesVirtualSourceModeIndexPerPath()
    {
        const uint supportVirtualMode = 0x00000008;
        const uint cloneGroupId = 0x1234;
        const uint sourceModeIndex = 7;
        uint virtualModeUnion = (sourceModeIndex << 16) | cloneGroupId;

        AssertEqual(
            sourceModeIndex,
            NativeDisplayDetection.DecodeSourceModeIndex(virtualModeUnion, supportVirtualMode));
        AssertEqual(
            virtualModeUnion,
            NativeDisplayDetection.DecodeSourceModeIndex(virtualModeUnion, pathFlags: 0));
        AssertEqual(
            uint.MaxValue,
            NativeDisplayDetection.DecodeSourceModeIndex(uint.MaxValue, supportVirtualMode));
    }

    private static NativeDisplayCandidate Candidate(
        string endpoint,
        bool active,
        bool available,
        string source,
        string path,
        string serial,
        string monitorId)
    {
        NativeDisplayDetection.TryParseMonitorDevicePath(path, out var hardware, out var instance);
        return new NativeDisplayCandidate(
            endpoint,
            active,
            available,
            source,
            monitorId,
            path,
            $"DISPLAY\\{hardware}\\{instance}",
            serial,
            monitorId,
            1,
            0);
    }

    private static string MonitorPath(string hardware, string instance)
        => $@"\\?\DISPLAY#{hardware}#{instance}#{{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}}";

    private static void WriteDescriptor(byte[] edid, int offset, byte tag, string text)
    {
        edid[offset] = 0;
        edid[offset + 1] = 0;
        edid[offset + 2] = 0;
        edid[offset + 3] = tag;
        edid[offset + 4] = 0;
        var bytes = System.Text.Encoding.ASCII.GetBytes(text.PadRight(13, ' ').Substring(0, 13));
        Array.Copy(bytes, 0, edid, offset + 5, bytes.Length);
    }

    private static void AssertTrue(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void AssertFalse(bool condition, string message)
        => AssertTrue(!condition, message);

    private static void AssertEqual<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }
}
