#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using WorkMonitorSwitcher;
using WorkMonitorSwitcher.Model;
using WorkMonitorSwitcher.Services;

internal static class UpdaterTests
{
    internal static IEnumerable<(string Name, Action Body)> GetTests()
    {
        yield return ("Updater validates redirects and rejects unexpected destinations", RedirectsAreValidated);
        yield return ("Updater 404 response is a no-release result with the GitHub API version", MissingReleaseIsReported);
        yield return ("Updater cancellation covers stalled headers and response bodies", StalledTransfersAreCancelled);
        yield return ("Updater cancellation and limits cover local package verification", LocalVerificationIsCancellableAndBounded);
        yield return ("Updater enforces response-body and archive extraction limits", TransferAndArchiveLimitsAreEnforced);
        yield return ("Updater stores and reuses one verified per-user release", SuccessfulDownloadIsReused);
        yield return ("Updater rejects rewritten cache metadata and tampered extracted files", TamperedCachedPackageIsRejected);
        yield return ("Updater reports temporary-directory cleanup failures", CleanupFailureIsReported);
        yield return ("Updater byte limits use accurate binary units", ByteLimitsUseBinaryUnits);
        yield return ("Settings preserves hidden preferred-primary metadata", SettingsPreservesPreferredPrimary);
    }

    private static void RedirectsAreValidated()
    {
        int requestCount = 0;
        using var handler = new StubHttpHandler((request, _) =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                return Task.FromResult(Response(
                    request,
                    HttpStatusCode.Redirect,
                    Array.Empty<byte>(),
                    new Uri("https://release-assets.githubusercontent.com/verified.bin")));
            }

            return Task.FromResult(Response(request, HttpStatusCode.OK, new byte[] { 1 }));
        });
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var service = new AppUpdateService(client, NewTempDirectory());
        using var initialRequest = new HttpRequestMessage(HttpMethod.Get, "https://github.com/original.bin");
        using HttpResponseMessage response = service.SendWithValidatedRedirectsAsync(
                initialRequest,
                uri => uri.Scheme == Uri.UriSchemeHttps &&
                       (uri.Host == "github.com" || uri.Host == "release-assets.githubusercontent.com"),
                CancellationToken.None)
            .GetAwaiter().GetResult();
        Equal(HttpStatusCode.OK, response.StatusCode, "Expected the permitted redirect to be followed.");
        Equal(2, requestCount, "Expected exactly one redirect.");

        using var badHandler = new StubHttpHandler((request, _) => Task.FromResult(Response(
            request,
            HttpStatusCode.Redirect,
            Array.Empty<byte>(),
            new Uri("https://example.com/untrusted.bin"))));
        using var badClient = new HttpClient(badHandler) { Timeout = Timeout.InfiniteTimeSpan };
        using var badService = new AppUpdateService(badClient, NewTempDirectory());
        using var badRequest = new HttpRequestMessage(HttpMethod.Get, "https://github.com/original.bin");
        Throws<InvalidDataException>(() => badService.SendWithValidatedRedirectsAsync(
                badRequest,
                uri => uri.Scheme == Uri.UriSchemeHttps &&
                       (uri.Host == "github.com" || uri.Host == "release-assets.githubusercontent.com"),
                CancellationToken.None)
            .GetAwaiter().GetResult());
    }

    private static void MissingReleaseIsReported()
    {
        string? apiVersion = null;
        using var handler = new StubHttpHandler((request, _) =>
        {
            apiVersion = request.Headers.TryGetValues("X-GitHub-Api-Version", out IEnumerable<string>? values)
                ? values.SingleOrDefault()
                : null;
            return Task.FromResult(Response(request, HttpStatusCode.NotFound, Encoding.UTF8.GetBytes("{}")));
        });
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        string root = NewTempDirectory();
        try
        {
            using var service = new AppUpdateService(client, root);
            AppUpdateResult result = service.CheckAndDownloadAsync("0.1.0", null, CancellationToken.None)
                .GetAwaiter().GetResult();
            Equal(AppUpdateStatus.NoPublishedRelease, result.Status, "Expected HTTP 404 to mean no release is published.");
            Equal("2022-11-28", apiVersion, "Expected the pinned GitHub REST API version header.");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static void StalledTransfersAreCancelled()
    {
        using var stalledHandler = new StubHttpHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Unreachable");
        });
        using var stalledClient = new HttpClient(stalledHandler) { Timeout = Timeout.InfiniteTimeSpan };
        string root = NewTempDirectory();
        try
        {
            using var service = new AppUpdateService(stalledClient, root);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            Throws<OperationCanceledException>(() => service.CheckAndDownloadAsync("0.1.0", null, cancellation.Token)
                .GetAwaiter().GetResult());

            using var stalledBody = new CancellationAwareStalledStream();
            using var destination = new MemoryStream();
            using var bodyCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            Throws<OperationCanceledException>(() => AppUpdateService.CopyStreamWithLimitAsync(
                    stalledBody,
                    destination,
                    1024,
                    null,
                    string.Empty,
                    null,
                    bodyCancellation.Token)
                .GetAwaiter().GetResult());
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static void LocalVerificationIsCancellableAndBounded()
    {
        using (var cancellation = new CancellationTokenSource())
        using (var stream = new CancellingReadStream(new byte[200_000], cancellation))
        {
            Throws<OperationCanceledException>(() =>
                AppUpdateService.ComputeSha256(stream, cancellation.Token));
        }

        string root = NewTempDirectory();
        try
        {
            File.WriteAllBytes(Path.Combine(root, "one.bin"), new byte[] { 1, 2 });
            File.WriteAllBytes(Path.Combine(root, "two.bin"), new byte[] { 3 });

            Throws<InvalidDataException>(() => AppUpdateService.EnumeratePackageFilesSafely(
                root,
                CancellationToken.None,
                maximumEntries: 1,
                maximumExpandedBytes: 100).ToList());
            Throws<InvalidDataException>(() => AppUpdateService.EnumeratePackageFilesSafely(
                root,
                CancellationToken.None,
                maximumEntries: 10,
                maximumExpandedBytes: 1).ToList());
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static void TransferAndArchiveLimitsAreEnforced()
    {
        using (var source = new MemoryStream(new byte[11]))
        using (var destination = new MemoryStream())
        {
            Throws<InvalidDataException>(() => AppUpdateService.CopyStreamWithLimitAsync(
                    source,
                    destination,
                    10,
                    null,
                    string.Empty,
                    11,
                    CancellationToken.None)
                .GetAwaiter().GetResult());
        }

        string root = NewTempDirectory();
        try
        {
            string duplicateArchive = Path.Combine(root, "duplicate.zip");
            WriteZip(duplicateArchive, ("same.txt", new byte[] { 1 }), ("same.txt", new byte[] { 2 }));
            Throws<InvalidDataException>(() => AppUpdateService.ExtractZipSafely(
                duplicateArchive,
                Path.Combine(root, "duplicate-out"),
                10,
                100,
                100,
                CancellationToken.None));

            string traversalArchive = Path.Combine(root, "traversal.zip");
            WriteZip(traversalArchive, ("../outside.txt", new byte[] { 1 }));
            Throws<InvalidDataException>(() => AppUpdateService.ExtractZipSafely(
                traversalArchive,
                Path.Combine(root, "traversal-out"),
                10,
                100,
                100,
                CancellationToken.None));

            string countArchive = Path.Combine(root, "count.zip");
            WriteZip(countArchive, ("one.txt", new byte[] { 1 }), ("two.txt", new byte[] { 2 }));
            Throws<InvalidDataException>(() => AppUpdateService.ExtractZipSafely(
                countArchive,
                Path.Combine(root, "count-out"),
                1,
                100,
                100,
                CancellationToken.None));

            string expandedArchive = Path.Combine(root, "expanded.zip");
            WriteZip(expandedArchive, ("large.bin", new byte[11]));
            Throws<InvalidDataException>(() => AppUpdateService.ExtractZipSafely(
                expandedArchive,
                Path.Combine(root, "expanded-out"),
                10,
                10,
                100,
                CancellationToken.None));

            string reservedManifestArchive = Path.Combine(root, "reserved-manifest.zip");
            WriteZip(reservedManifestArchive, (".monitorswitcher-release.json", new byte[] { 1 }));
            Throws<InvalidDataException>(() => AppUpdateService.ExtractZipSafely(
                reservedManifestArchive,
                Path.Combine(root, "reserved-manifest-out"),
                10,
                100,
                100,
                CancellationToken.None));

            string reservedArchiveArchive = Path.Combine(root, "reserved-archive.zip");
            WriteZip(reservedArchiveArchive, ("nested/.monitorswitcher-release.zip", new byte[] { 1 }));
            Throws<InvalidDataException>(() => AppUpdateService.ExtractZipSafely(
                reservedArchiveArchive,
                Path.Combine(root, "reserved-archive-out"),
                10,
                100,
                100,
                CancellationToken.None));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static void SuccessfulDownloadIsReused()
    {
        const string tag = "v9.9.9";
        string root = NewTempDirectory();
        string oldInstall = Path.Combine(root, "read-only-old-install");
        Directory.CreateDirectory(oldInstall);
        File.WriteAllText(Path.Combine(oldInstall, "MonitorSwitcher.exe"), "old installation is not writable");
        File.SetAttributes(oldInstall, File.GetAttributes(oldInstall) | FileAttributes.ReadOnly);

        byte[] archive = CreateZipBytes(("payload.txt", Encoding.UTF8.GetBytes("verified payload")));
        string assetName = $"MonitorSwitcher-{tag}-win-x64.zip";
        byte[] checksum = Encoding.UTF8.GetBytes(
            $"{Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant()}  {assetName}");
        byte[] releaseJson = CreateReleaseJson(tag, assetName, archive, checksum);
        int archiveRequests = 0;
        using var handler = ReleaseHandler(releaseJson, archive, checksum, () => archiveRequests++);
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

        try
        {
            using var service = new AppUpdateService(
                client,
                root,
                (directory, _, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    True(File.Exists(Path.Combine(directory, "payload.txt")), "Expected extracted payload.");
                });
            AppUpdateResult first = service.CheckAndDownloadAsync("0.1.0", null, CancellationToken.None)
                .GetAwaiter().GetResult();
            Equal(AppUpdateStatus.Ready, first.Status, "Expected the mocked release to be downloaded.");
            True(first.PackageDirectory != null, "Expected a per-user package directory.");
            True(first.PackageDirectory!.StartsWith(service.UpdatesRoot, StringComparison.OrdinalIgnoreCase),
                "Expected the release beneath LocalAppData, not beside the old installation.");
            True(!Directory.Exists(Path.Combine(first.PackageDirectory, "package")),
                "Expected the application files directly in the version directory, without nested installs.");
            True(File.Exists(Path.Combine(first.PackageDirectory, ".monitorswitcher-release.json")),
                "Expected a local release manifest for cache metadata.");
            string retainedArchive = Path.Combine(first.PackageDirectory, ".monitorswitcher-release.zip");
            True(File.Exists(retainedArchive), "Expected the verified release archive to be retained for safe reuse.");
            True((File.GetAttributes(retainedArchive) & FileAttributes.Hidden) != 0,
                "Expected the retained verification archive to be hidden from the normal package view.");
            Equal(
                Convert.ToHexString(SHA256.HashData(archive)),
                AppUpdateService.ComputeSha256(retainedArchive, CancellationToken.None),
                "Expected the retained archive to match the downloaded release exactly.");
            Equal(2, archiveRequests, "Expected one archive and one checksum request.");

            AppUpdateResult second = service.CheckAndDownloadAsync("0.1.0", null, CancellationToken.None)
                .GetAwaiter().GetResult();
            Equal(AppUpdateStatus.Reused, second.Status, "Expected the verified version directory to be reused.");
            Equal(first.PackageDirectory, second.PackageDirectory, "Expected no duplicate release directory.");
            Equal(3, archiveRequests,
                "Expected reuse to refresh only the small published checksum without downloading the archive again.");

            File.SetAttributes(retainedArchive, FileAttributes.Normal);
            using (var retainedArchiveStream = new FileStream(
                       retainedArchive,
                       FileMode.Open,
                       FileAccess.ReadWrite,
                       FileShare.None))
            {
                int firstByte = retainedArchiveStream.ReadByte();
                True(firstByte >= 0, "Expected a non-empty retained archive.");
                retainedArchiveStream.Position = 0;
                retainedArchiveStream.WriteByte((byte)(firstByte ^ 0xFF));
            }
            Throws<InvalidDataException>(() => service.CheckAndDownloadAsync(
                    "0.1.0",
                    null,
                    CancellationToken.None)
                .GetAwaiter().GetResult());
            Equal(4, archiveRequests,
                "Expected a fresh checksum request to detect the corrupted retained archive without redownloading it.");

            Equal("old installation is not writable", File.ReadAllText(Path.Combine(oldInstall, "MonitorSwitcher.exe")),
                "Expected the existing installation to remain untouched.");
            True(!Directory.Exists(Path.Combine(oldInstall, "updates")),
                "Expected no staging or update directory beside the existing installation.");
        }
        finally
        {
            File.SetAttributes(oldInstall, FileAttributes.Directory);
            DeleteDirectory(root);
        }
    }

    private static void TamperedCachedPackageIsRejected()
    {
        const string tag = "v9.9.7";
        const string version = "9.9.7";
        string root = NewTempDirectory();
        byte[] verifiedPayload = Encoding.UTF8.GetBytes("verified payload");
        byte[] archive = CreateZipBytes(("payload.txt", verifiedPayload));
        string assetName = $"MonitorSwitcher-{tag}-win-x64.zip";
        string archiveHash = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
        byte[] checksum = Encoding.UTF8.GetBytes($"{archiveHash}  {assetName}");
        byte[] releaseJson = CreateReleaseJson(tag, assetName, archive, checksum);
        int assetRequests = 0;
        using var handler = ReleaseHandler(releaseJson, archive, checksum, () => assetRequests++);
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

        try
        {
            using var service = new AppUpdateService(
                client,
                root,
                (directory, _, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    True(File.Exists(Path.Combine(directory, "payload.txt")), "Expected extracted payload.");
                });
            AppUpdateResult first = service.CheckAndDownloadAsync("0.1.0", null, CancellationToken.None)
                .GetAwaiter().GetResult();
            True(first.PackageDirectory != null, "Expected the first verified package directory.");
            string packageDirectory = first.PackageDirectory!;

            byte[] tamperedPayload = Encoding.UTF8.GetBytes("tampered payload");
            File.WriteAllBytes(Path.Combine(packageDirectory, "payload.txt"), tamperedPayload);
            var forgedManifest = new
            {
                TagName = tag,
                Version = version,
                ArchiveSha256 = archiveHash,
                Files = new[]
                {
                    new
                    {
                        Path = "payload.txt",
                        Length = tamperedPayload.LongLength,
                        Sha256 = Convert.ToHexString(SHA256.HashData(tamperedPayload))
                    }
                }
            };
            File.WriteAllText(
                Path.Combine(packageDirectory, ".monitorswitcher-release.json"),
                JsonSerializer.Serialize(forgedManifest));

            Throws<InvalidDataException>(() => service.CheckAndDownloadAsync(
                    "0.1.0",
                    null,
                    CancellationToken.None)
                .GetAwaiter().GetResult());
            Equal(3, assetRequests,
                "Expected cache verification to refresh the checksum but not redownload the archive before rejecting tampering.");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static void CleanupFailureIsReported()
    {
        const string tag = "v9.9.8";
        string root = NewTempDirectory();
        byte[] archive = CreateZipBytes(("payload.txt", new byte[] { 1, 2, 3 }));
        string assetName = $"MonitorSwitcher-{tag}-win-x64.zip";
        byte[] checksum = Encoding.UTF8.GetBytes(
            $"{Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant()}  {assetName}");
        byte[] releaseJson = CreateReleaseJson(tag, assetName, archive, checksum);
        using var handler = ReleaseHandler(releaseJson, archive, checksum, () => { });
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

        try
        {
            using var service = new AppUpdateService(
                client,
                root,
                (_, _, cancellationToken) => cancellationToken.ThrowIfCancellationRequested(),
                _ => "simulated access denied");
            AppUpdateResult result = service.CheckAndDownloadAsync("0.1.0", null, CancellationToken.None)
                .GetAwaiter().GetResult();
            Contains(result.CleanupWarning, "access denied", "Expected cleanup failure details in the successful result.");
            True(Directory.EnumerateDirectories(service.UpdatesRoot, ".staging-*", SearchOption.TopDirectoryOnly).Any(),
                "Expected the simulated cleanup failure to leave the staging directory for diagnosis.");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static void ByteLimitsUseBinaryUnits()
    {
        Equal("512 B", AppUpdateService.FormatByteLimit(512), "Expected byte-scale limits to remain visible.");
        Equal("16 KiB", AppUpdateService.FormatByteLimit(16 * 1024), "Expected the checksum limit not to display as 0 MB.");
        Equal("1.5 MiB", AppUpdateService.FormatByteLimit(3 * 1024 * 1024 / 2), "Expected fractional MiB formatting.");
    }

    private static void SettingsPreservesPreferredPrimary()
    {
        RunOnSta(() =>
        {
            var preferred = new AliasViewRow
            {
                StableKey = "SN:PREFERRED",
                ShortKey = "SN:PREFERRED",
                Alias = "Preferred",
                IsPreferredPrimary = true
            };
            using var handler = new StubHttpHandler((request, _) =>
                Task.FromResult(Response(request, HttpStatusCode.NotFound, Encoding.UTF8.GetBytes("{}"))));
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            string root = NewTempDirectory();
            try
            {
                using var service = new AppUpdateService(client, root);
                using var form = new AliasSettingsForm(
                    new List<AliasViewRow> { preferred },
                    darkMode: true,
                    alwaysOnTop: false,
                    minimizeToTray: true,
                    startupRegistrationState: StartupRegistrationState.Missing,
                    confirmBeforeDisable: true,
                    restoreLayoutOnStartup: false,
                    layoutProfiles: new List<string> { "Default" },
                    selectedLayoutProfile: "Default",
                    diagnosticsText: string.Empty,
                    updateService: service);

                True(preferred.IsPreferredPrimary,
                    "Expected opening Settings not to clear hidden preferred-primary metadata.");
                True(!form.MinimizeBox, "Expected Settings not to expose an unsafe minimise button.");
                Control? generalTab = Descendants(form)
                    .FirstOrDefault(control => control.Text == "General" && control.AccessibleRole == AccessibleRole.PageTab);
                True(generalTab?.AccessibleName?.Contains("selected", StringComparison.OrdinalIgnoreCase) == true,
                    "Expected the selected custom navigation tab to expose its selected state.");
                True(generalTab?.AccessibilityObject.State.HasFlag(AccessibleStates.Selected) == true,
                    "Expected the selected page tab to expose the MSAA selected state.");
                True(generalTab?.AccessibilityObject.State.HasFlag(AccessibleStates.Selectable) == true,
                    "Expected the custom page tab to expose selectable semantics.");
            }
            finally
            {
                DeleteDirectory(root);
            }
        });
    }

    private static StubHttpHandler ReleaseHandler(
        byte[] releaseJson,
        byte[] archive,
        byte[] checksum,
        Action assetRequested)
        => new((request, _) =>
        {
            string uri = request.RequestUri?.AbsoluteUri ?? string.Empty;
            if (uri == AppUpdateService.LatestReleaseApiUri.AbsoluteUri)
                return Task.FromResult(Response(request, HttpStatusCode.OK, releaseJson));
            if (uri.EndsWith(".zip", StringComparison.Ordinal))
            {
                assetRequested();
                return Task.FromResult(Response(request, HttpStatusCode.OK, archive));
            }
            if (uri.EndsWith(".sha256", StringComparison.Ordinal))
            {
                assetRequested();
                return Task.FromResult(Response(request, HttpStatusCode.OK, checksum));
            }
            return Task.FromResult(Response(request, HttpStatusCode.NotFound, Encoding.UTF8.GetBytes("{}")));
        });

    private static byte[] CreateReleaseJson(string tag, string assetName, byte[] archive, byte[] checksum)
    {
        var release = new
        {
            tag_name = tag,
            draft = false,
            prerelease = false,
            assets = new object[]
            {
                new
                {
                    name = assetName,
                    browser_download_url = $"https://github.com/Ci303/monitor-switcher-native/releases/download/{tag}/{assetName}",
                    size = archive.LongLength,
                    digest = $"sha256:{Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant()}"
                },
                new
                {
                    name = assetName + ".sha256",
                    browser_download_url = $"https://github.com/Ci303/monitor-switcher-native/releases/download/{tag}/{assetName}.sha256",
                    size = checksum.LongLength,
                    digest = (string?)null
                }
            }
        };
        return JsonSerializer.SerializeToUtf8Bytes(release);
    }

    private static HttpResponseMessage Response(
        HttpRequestMessage request,
        HttpStatusCode status,
        byte[] content,
        Uri? location = null)
    {
        var response = new HttpResponseMessage(status)
        {
            RequestMessage = request,
            Content = new ByteArrayContent(content)
        };
        if (location != null)
            response.Headers.Location = location;
        return response;
    }

    private static byte[] CreateZipBytes(params (string Name, byte[] Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, byte[] content) in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(name);
                using Stream destination = entry.Open();
                destination.Write(content);
            }
        }
        return stream.ToArray();
    }

    private static void WriteZip(string path, params (string Name, byte[] Content)[] entries)
        => File.WriteAllBytes(path, CreateZipBytes(entries));

    private static string NewTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "MonitorSwitcher-updater-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            foreach (string item in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
                File.SetAttributes(item, FileAttributes.Normal);
            Directory.Delete(path, recursive: true);
        }
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (Control descendant in Descendants(child))
                yield return descendant;
        }
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null)
            throw new InvalidOperationException("The STA Settings test failed.", failure);
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message} Expected: {expected}; actual: {actual}.");
    }

    private static void Contains(string? actual, string expected, string message)
    {
        if (actual == null || !actual.Contains(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{message} Actual: {actual ?? "<null>"}.");
    }

    private static void Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _response;

        internal StubHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response)
            => _response = response;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => _response(request, cancellationToken);
    }

    private sealed class CancellationAwareStalledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private sealed class CancellingReadStream : MemoryStream
    {
        private readonly CancellationTokenSource _cancellation;
        private bool _cancelled;

        internal CancellingReadStream(byte[] buffer, CancellationTokenSource cancellation)
            : base(buffer, writable: false)
            => _cancellation = cancellation;

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = base.Read(buffer, offset, count);
            if (!_cancelled && read > 0)
            {
                _cancelled = true;
                _cancellation.Cancel();
            }
            return read;
        }
    }
}
