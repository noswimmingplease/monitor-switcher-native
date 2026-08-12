using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WorkMonitorSwitcher.Model;

namespace WorkMonitorSwitcher.Services
{
    /// <summary>
    /// Facade over Windows Connecting and Configuring Displays (CCD).
    /// </summary>
    internal sealed class LayoutService
    {
        private readonly DisplayTopologyService _topologyService;
        private readonly Action<string>? _diagnosticsLog;

        public LayoutService(Action<string>? diagnosticsLog = null)
            : this(new DisplayTopologyService(), diagnosticsLog)
        {
        }

        internal LayoutService(
            DisplayTopologyService topologyService,
            Action<string>? diagnosticsLog = null)
        {
            _topologyService = topologyService ?? throw new ArgumentNullException(nameof(topologyService));
            _diagnosticsLog = diagnosticsLog;
        }

        public bool SaveLayout(string layoutPath)
            => SaveLayoutAsync(layoutPath).GetAwaiter().GetResult();

        public Task<bool> SaveLayoutAsync(
            string layoutPath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = _topologyService.CaptureLayoutToConfig(layoutPath);
            if (!result.Success)
                LogFailure("save native layout", result.ErrorMessage);
            return Task.FromResult(result.Success);
        }

        public bool LoadLayout(string layoutPath)
            => LoadLayoutAsync(layoutPath).GetAwaiter().GetResult();

        public Task<bool> LoadLayoutAsync(
            string layoutPath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var detection = NativeDisplayDetection.Detect(cancellationToken);
                return LoadLayoutAsync(
                    layoutPath,
                    detection.Monitors,
                    LayoutIdentityStore.Load(layoutPath),
                    cancellationToken);
            }
            catch (Exception ex)
            {
                LogFailure("read native monitor identities before restore", ex.Message);
                return Task.FromResult(false);
            }
        }

        public Task<bool> LoadLayoutAsync(
            string layoutPath,
            IReadOnlyCollection<DetectedMonitor>? detectedMonitors,
            IReadOnlyCollection<SavedLayoutIdentity>? savedIdentities,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = RestoreLayoutWithResult(
                layoutPath,
                detectedMonitors,
                savedIdentities);
            if (!result.Success)
                LogFailure("restore exact native layout", FormatResult(result));
            return Task.FromResult(result.Success);
        }

        internal DisplayTopologyResult RestoreLayoutWithResult(
            string layoutPath,
            IReadOnlyCollection<DetectedMonitor>? detectedMonitors,
            IReadOnlyCollection<SavedLayoutIdentity>? savedIdentities)
            => _topologyService.RestoreExactDisplaySetFromConfig(
                layoutPath,
                detectedMonitors,
                savedIdentities);

        public bool SetPrimary(string target)
            => SetPrimaryAsync(target, null).GetAwaiter().GetResult();

        public Task<bool> SetPrimaryAsync(
            string target,
            string? expectedTargetPath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!NativeDisplayProfileCodec.IsStrongTargetPath(expectedTargetPath))
            {
                LogFailure("set native primary monitor", "A unique current physical target was not supplied");
                return Task.FromResult(false);
            }
            var result = _topologyService.SetPrimaryDisplay(target, expectedTargetPath!);
            if (!result.Success)
                LogFailure("set native primary monitor", FormatResult(result));
            return Task.FromResult(result.Success);
        }

        internal static bool IsValidLayoutFile(string? path)
            => NativeDisplayProfileCodec.TryRead(path, out _, out _);

        internal static bool TryGetActiveLayoutDeviceNames(
            string? path,
            out IReadOnlyList<string> activeDeviceNames)
        {
            activeDeviceNames = Array.Empty<string>();
            if (!NativeDisplayProfileCodec.TryRead(path, out var profile, out _))
                return false;

            var names = new List<string>();
            foreach (var monitor in profile.Monitors)
                names.Add(monitor.LayoutDeviceName);
            activeDeviceNames = names;
            return names.Count > 0;
        }

        private static string FormatResult(DisplayTopologyResult result)
            => result.Details.Count == 0
                ? result.Message
                : $"{result.Message} {string.Join(" ", result.Details)}";

        private void LogFailure(string operation, string detail)
        {
            try
            {
                _diagnosticsLog?.Invoke($"Windows CCD {operation} failed: {detail}.");
            }
            catch
            {
                // Diagnostics must never change the operation result.
            }
        }
    }
}
