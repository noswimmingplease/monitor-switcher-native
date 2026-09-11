#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace WorkMonitorSwitcher.Services
{
    internal sealed class AppUpdateService : IDisposable
    {
        internal static readonly Uri LatestReleaseApiUri =
            new("https://api.github.com/repos/noswimmingplease/monitor-switcher-native/releases/latest");

        internal const long MaxReleaseApiBytes = 2L * 1024 * 1024;
        internal const long MaxChecksumBytes = 16L * 1024;
        internal const long MaxAppArchiveBytes = 300L * 1024 * 1024;
        internal const long MaxAppExpandedBytes = 750L * 1024 * 1024;
        internal const int MaxAppArchiveEntries = 5000;
        private const ushort PeMachineAmd64 = 0x8664;
        private const string ReleaseManifestFileName = ".monitorswitcher-release.json";
        private const string ReleaseArchiveFileName = ".monitorswitcher-release.zip";
        private const int AppOwnedPackageEntryCount = 2;

        private readonly HttpClient _httpClient;
        private readonly bool _ownsHttpClient;
        private readonly string _updatesRoot;
        private readonly Action<string, SemanticVersion, CancellationToken> _packageValidator;
        private readonly Func<string, string?> _cleanupDirectory;

        internal AppUpdateService(
            HttpClient httpClient,
            string localAppDataRoot,
            Action<string, SemanticVersion, CancellationToken>? packageValidator = null,
            Func<string, string?>? cleanupDirectory = null,
            bool ownsHttpClient = false)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            if (string.IsNullOrWhiteSpace(localAppDataRoot))
                throw new ArgumentException("A per-user local application-data directory is required.", nameof(localAppDataRoot));

            _updatesRoot = GetUpdatesRoot(localAppDataRoot);
            _packageValidator = packageValidator ?? ValidateMonitorSwitcherPackage;
            _cleanupDirectory = cleanupDirectory ?? TryDeleteDirectory;
            _ownsHttpClient = ownsHttpClient;
        }

        internal static AppUpdateService CreateDefault()
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                CheckCertificateRevocationList = true
            };
            var client = new HttpClient(handler)
            {
                // The Settings form owns the whole-operation timeout. Keeping the
                // transport timeout infinite means one token covers headers and bodies.
                Timeout = Timeout.InfiniteTimeSpan
            };
            return new AppUpdateService(
                client,
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ownsHttpClient: true);
        }

        internal string UpdatesRoot => _updatesRoot;

        internal async Task<AppUpdateResult> CheckAndDownloadAsync(
            string currentVersionText,
            IProgress<AppUpdateProgress>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new AppUpdateProgress("Checking GitHub releases…"));

            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApiUri);
            AddGitHubHeaders(request, includeApiVersion: true);

            byte[] releaseJson;
            try
            {
                releaseJson = await DownloadBytesAsync(
                    request,
                    MaxReleaseApiBytes,
                    IsExpectedReleaseApiUri,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return AppUpdateResult.NoPublishedRelease();
            }

            var release = JsonSerializer.Deserialize<GitHubRelease>(releaseJson)
                ?? throw new InvalidDataException("Unable to read the latest release details.");
            if (release.Draft || release.Prerelease)
                throw new InvalidDataException("GitHub returned a draft or pre-release instead of the latest stable release.");
            if (!SemanticVersion.TryParseReleaseTag(release.TagName, out SemanticVersion releaseVersion))
                throw new InvalidDataException($"The release tag '{release.TagName}' is not a stable semantic version such as v1.2.3.");
            if (!SemanticVersion.TryParse(currentVersionText, out SemanticVersion currentVersion))
                throw new InvalidDataException($"The installed app version '{currentVersionText}' is not a valid semantic version.");
            if (releaseVersion.CompareTo(currentVersion) <= 0)
                return AppUpdateResult.Current(currentVersion.ToString(), releaseVersion.ToString());

            string expectedAssetName = $"MonitorSwitcher-{release.TagName}-win-x64.zip";
            string expectedChecksumName = expectedAssetName + ".sha256";
            GitHubReleaseAsset asset = FindUniqueReleaseAsset(release.Assets, expectedAssetName, required: true)!;
            GitHubReleaseAsset checksumAsset = FindUniqueReleaseAsset(release.Assets, expectedChecksumName, required: true)!;
            ValidateReleaseAsset(asset, release.TagName, expectedAssetName, MaxAppArchiveBytes);
            ValidateReleaseAsset(checksumAsset, release.TagName, expectedChecksumName, MaxChecksumBytes);

            progress?.Report(new AppUpdateProgress("Downloading the published checksum…", 0, checksumAsset.Size));
            using var checksumRequest = new HttpRequestMessage(
                HttpMethod.Get,
                new Uri(checksumAsset.DownloadUrl, UriKind.Absolute));
            AddGitHubHeaders(checksumRequest, includeApiVersion: false);
            byte[] publishedChecksum = await DownloadBytesAsync(
                checksumRequest,
                MaxChecksumBytes,
                IsSafeGitHubDownloadRedirectUri,
                cancellationToken).ConfigureAwait(false);
            if (publishedChecksum.LongLength != checksumAsset.Size)
            {
                throw new InvalidDataException(
                    $"Downloaded size mismatch for {expectedChecksumName}: GitHub reported " +
                    $"{checksumAsset.Size} bytes but received {publishedChecksum.LongLength} bytes.");
            }
            string checksumText = Encoding.UTF8.GetString(publishedChecksum);
            if (!TryParsePublishedSha256(checksumText, expectedAssetName, out string publishedArchiveHash))
                throw new InvalidDataException("The published SHA-256 sidecar has an unexpected format or filename.");
            VerifyGitHubDigestWhenPresent(publishedArchiveHash, asset.Digest);

            Directory.CreateDirectory(_updatesRoot);
            string finalDirectory = GetVersionDirectoryPath(_updatesRoot, release.TagName);
            if (Directory.Exists(finalDirectory))
            {
                progress?.Report(new AppUpdateProgress("Verifying the existing download…"));
                ValidateReusablePackage(
                    finalDirectory,
                    release.TagName,
                    releaseVersion,
                    publishedArchiveHash,
                    cancellationToken);
                return AppUpdateResult.Reused(releaseVersion.ToString(), finalDirectory);
            }

            string stagingRoot = Path.Combine(_updatesRoot, $".staging-{Guid.NewGuid():N}");
            AppUpdateResult? result = null;
            Exception? operationFailure = null;
            string? cleanupWarning = null;

            try
            {
                Directory.CreateDirectory(stagingRoot);
                string assetPath = Path.Combine(stagingRoot, expectedAssetName);
                progress?.Report(new AppUpdateProgress("Downloading the release archive…", 0, asset.Size));
                await DownloadFileAsync(
                    new Uri(asset.DownloadUrl, UriKind.Absolute),
                    assetPath,
                    MaxAppArchiveBytes,
                    IsSafeGitHubDownloadRedirectUri,
                    progress,
                    "Downloading the release archive…",
                    cancellationToken).ConfigureAwait(false);
                VerifyExpectedDownloadSize(assetPath, asset.Size, expectedAssetName);

                string checksumPath = Path.Combine(stagingRoot, expectedChecksumName);
                cancellationToken.ThrowIfCancellationRequested();
                await File.WriteAllBytesAsync(
                    checksumPath,
                    publishedChecksum,
                    cancellationToken).ConfigureAwait(false);

                progress?.Report(new AppUpdateProgress("Verifying the download…"));
                string archiveHash = VerifyPublishedSha256(
                    assetPath,
                    checksumPath,
                    expectedAssetName,
                    cancellationToken);
                VerifyGitHubDigestWhenPresent(archiveHash, asset.Digest);

                string packageDirectory = Path.Combine(stagingRoot, "package");
                progress?.Report(new AppUpdateProgress("Extracting the verified package…"));
                ExtractZipSafely(
                    assetPath,
                    packageDirectory,
                    MaxAppArchiveEntries - AppOwnedPackageEntryCount,
                    MaxAppExpandedBytes - MaxReleaseApiBytes - MaxAppArchiveBytes,
                    MaxAppArchiveBytes,
                    cancellationToken);
                _packageValidator(packageDirectory, releaseVersion, cancellationToken);
                WriteReleaseManifest(
                    packageDirectory,
                    release.TagName,
                    releaseVersion.ToString(),
                    archiveHash,
                    cancellationToken);
                RetainVerifiedArchive(assetPath, packageDirectory, cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    Directory.Move(packageDirectory, finalDirectory);
                }
                catch (IOException) when (Directory.Exists(finalDirectory))
                {
                    // Another instance may have completed the same verified release.
                    ValidateReusablePackage(
                        finalDirectory,
                        release.TagName,
                        releaseVersion,
                        publishedArchiveHash,
                        cancellationToken);
                    result = AppUpdateResult.Reused(releaseVersion.ToString(), finalDirectory);
                }

                result ??= AppUpdateResult.Ready(releaseVersion.ToString(), finalDirectory);
            }
            catch (Exception ex)
            {
                operationFailure = ex;
            }
            finally
            {
                cleanupWarning = _cleanupDirectory(stagingRoot);
            }

            if (operationFailure != null)
            {
                if (!string.IsNullOrWhiteSpace(cleanupWarning))
                {
                    throw new IOException(
                        $"{operationFailure.Message}{Environment.NewLine}" +
                        $"The temporary update directory could not be removed: {cleanupWarning}",
                        operationFailure);
                }

                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(operationFailure).Throw();
            }

            if (result == null)
                throw new InvalidOperationException("The update operation did not produce a result.");
            return result with { CleanupWarning = cleanupWarning };
        }

        internal async Task<byte[]> DownloadBytesAsync(
            HttpRequestMessage request,
            long maximumBytes,
            Func<Uri, bool> isAllowedFinalUri,
            CancellationToken cancellationToken)
        {
            using HttpResponseMessage response = await SendWithValidatedRedirectsAsync(
                request,
                isAllowedFinalUri,
                cancellationToken).ConfigureAwait(false);
            ValidateDownloadResponse(response, maximumBytes, isAllowedFinalUri);

            await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var destination = new MemoryStream();
            await CopyStreamWithLimitAsync(
                source,
                destination,
                maximumBytes,
                progress: null,
                progressMessage: string.Empty,
                totalBytes: response.Content.Headers.ContentLength,
                cancellationToken).ConfigureAwait(false);
            return destination.ToArray();
        }

        private async Task DownloadFileAsync(
            Uri sourceUri,
            string destinationPath,
            long maximumBytes,
            Func<Uri, bool> isAllowedFinalUri,
            IProgress<AppUpdateProgress>? progress,
            string progressMessage,
            CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, sourceUri);
            AddGitHubHeaders(request, includeApiVersion: false);
            using HttpResponseMessage response = await SendWithValidatedRedirectsAsync(
                request,
                isAllowedFinalUri,
                cancellationToken).ConfigureAwait(false);
            ValidateDownloadResponse(response, maximumBytes, isAllowedFinalUri);

            string? destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
                Directory.CreateDirectory(destinationDirectory);

            await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var destination = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                useAsync: true);
            await CopyStreamWithLimitAsync(
                source,
                destination,
                maximumBytes,
                progress,
                progressMessage,
                response.Content.Headers.ContentLength,
                cancellationToken).ConfigureAwait(false);
        }

        internal async Task<HttpResponseMessage> SendWithValidatedRedirectsAsync(
            HttpRequestMessage initialRequest,
            Func<Uri, bool> isAllowedUri,
            CancellationToken cancellationToken)
        {
            const int maximumRedirects = 5;
            HttpRequestMessage currentRequest = initialRequest;
            bool ownsCurrentRequest = false;

            try
            {
                for (int redirectCount = 0; ; redirectCount++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Uri? currentUri = currentRequest.RequestUri;
                    if (currentUri == null || !isAllowedUri(currentUri))
                        throw new InvalidDataException("The download address is not permitted.");

                    HttpResponseMessage response = await _httpClient.SendAsync(
                        currentRequest,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken).ConfigureAwait(false);
                    if (!IsRedirectStatusCode(response.StatusCode))
                        return response;

                    try
                    {
                        if (redirectCount >= maximumRedirects)
                            throw new InvalidDataException("The download exceeded the permitted redirect count.");

                        Uri? location = response.Headers.Location;
                        if (location == null)
                            throw new InvalidDataException("The download returned a redirect without a destination.");

                        Uri nextUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                        if (!isAllowedUri(nextUri))
                            throw new InvalidDataException("The download was redirected to an unexpected address.");

                        var nextRequest = new HttpRequestMessage(HttpMethod.Get, nextUri);
                        foreach (var header in initialRequest.Headers)
                            nextRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);

                        if (ownsCurrentRequest)
                            currentRequest.Dispose();
                        currentRequest = nextRequest;
                        ownsCurrentRequest = true;
                    }
                    finally
                    {
                        response.Dispose();
                    }
                }
            }
            finally
            {
                if (ownsCurrentRequest)
                    currentRequest.Dispose();
            }
        }

        internal static async Task CopyStreamWithLimitAsync(
            Stream source,
            Stream destination,
            long maximumBytes,
            IProgress<AppUpdateProgress>? progress,
            string progressMessage,
            long? totalBytes,
            CancellationToken cancellationToken)
        {
            var buffer = new byte[81920];
            long copiedBytes = 0;
            while (true)
            {
                int bytesRead = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (bytesRead == 0)
                    break;

                copiedBytes = checked(copiedBytes + bytesRead);
                if (copiedBytes > maximumBytes)
                    throw new InvalidDataException($"The download exceeded the {FormatByteLimit(maximumBytes)} limit.");

                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                progress?.Report(new AppUpdateProgress(progressMessage, copiedBytes, totalBytes));
            }

            if (copiedBytes == 0)
                throw new InvalidDataException("The server returned an empty download.");
        }

        internal static string GetUpdatesRoot(string localAppDataRoot)
            => Path.Combine(Path.GetFullPath(localAppDataRoot), "MonitorSwitcher", "Updates");

        internal static string GetVersionDirectoryPath(string updatesRoot, string releaseTag)
        {
            if (!SemanticVersion.TryParseReleaseTag(releaseTag, out _))
                throw new InvalidDataException("The release tag cannot be used as an update-directory name.");
            return Path.Combine(Path.GetFullPath(updatesRoot), $"MonitorSwitcher-{releaseTag}-win-x64");
        }

        internal static string FormatByteLimit(long bytes)
        {
            if (bytes < 0)
                throw new ArgumentOutOfRangeException(nameof(bytes));
            if (bytes < 1024)
                return $"{bytes} B";
            if (bytes < 1024L * 1024)
                return $"{bytes / 1024d:0.#} KiB";
            if (bytes < 1024L * 1024 * 1024)
                return $"{bytes / (1024d * 1024d):0.#} MiB";
            return $"{bytes / (1024d * 1024d * 1024d):0.#} GiB";
        }

        internal static int? CompareSemanticVersions(string? left, string? right)
        {
            if (!SemanticVersion.TryParse(left, out SemanticVersion leftVersion) ||
                !SemanticVersion.TryParse(right, out SemanticVersion rightVersion))
            {
                return null;
            }

            return Math.Sign(leftVersion.CompareTo(rightVersion));
        }

        internal static bool IsExpectedGitHubReleaseAssetUri(Uri uri, string releaseTag, string assetName)
        {
            if (!SemanticVersion.TryParseReleaseTag(releaseTag, out _) || !IsSafeFileName(assetName))
                return false;

            var expectedUri = new Uri(
                $"https://github.com/noswimmingplease/monitor-switcher-native/releases/download/{releaseTag}/{assetName}");
            return IsExactHttpsUri(uri, expectedUri);
        }

        internal static bool TryParsePublishedSha256(
            string? checksumText,
            string expectedAssetName,
            out string expectedHash)
        {
            expectedHash = string.Empty;
            if (checksumText == null || !IsSafeFileName(expectedAssetName))
                return false;

            string[] fields = checksumText.Trim().TrimStart('\uFEFF')
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 2 ||
                !IsSha256(fields[0]) ||
                !fields[1].TrimStart('*').Equals(expectedAssetName, StringComparison.Ordinal))
            {
                return false;
            }

            expectedHash = fields[0];
            return true;
        }

        internal static bool IsSafeArchivePath(string? entryFullName)
        {
            if (string.IsNullOrWhiteSpace(entryFullName))
                return false;

            string normalised = entryFullName.Replace('\\', '/');
            string trimmed = normalised.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(trimmed) || normalised.StartsWith("/", StringComparison.Ordinal))
                return false;

            return trimmed.Split('/').All(IsSafePathSegment);
        }

        internal static void ExtractZipSafely(
            string archivePath,
            string destinationDirectory,
            int maximumEntries,
            long maximumExpandedBytes,
            long maximumEntryBytes,
            CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(destinationDirectory);
            string destinationRoot = Path.GetFullPath(destinationDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

            using var archive = ZipFile.OpenRead(archivePath);
            if (archive.Entries.Count == 0 || archive.Entries.Count > maximumEntries)
                throw new InvalidDataException($"The archive entry count is outside the allowed range (maximum {maximumEntries}).");

            long totalExpandedBytes = 0;
            var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string rawPath = entry.FullName.Replace('\\', '/');
                bool isDirectory = rawPath.EndsWith("/", StringComparison.Ordinal);
                string trimmedPath = rawPath.TrimEnd('/');

                if (!IsSafeArchivePath(rawPath))
                    throw new InvalidDataException($"The archive contains an unsafe path: {entry.FullName}");
                if (IsReservedReleasePath(trimmedPath))
                    throw new InvalidDataException("The archive contains a path reserved for MonitorSwitcher update verification.");
                if (IsZipLinkOrReparsePoint(entry))
                    throw new InvalidDataException($"The archive contains a link or reparse point: {entry.FullName}");

                string[] segments = trimmedPath.Split('/');
                string destinationPath = Path.GetFullPath(Path.Combine(destinationRoot, Path.Combine(segments)));
                if (!destinationPath.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"The archive path escapes the extraction directory: {entry.FullName}");
                if (!destinations.Add(destinationPath))
                    throw new InvalidDataException($"The archive contains duplicate paths: {entry.FullName}");

                if (isDirectory)
                {
                    Directory.CreateDirectory(destinationPath);
                    continue;
                }

                if (entry.Length < 0 || entry.Length > maximumEntryBytes)
                    throw new InvalidDataException($"Archive entry {entry.FullName} is larger than allowed.");
                totalExpandedBytes = checked(totalExpandedBytes + entry.Length);
                if (totalExpandedBytes > maximumExpandedBytes)
                    throw new InvalidDataException("The archive expands beyond the allowed size.");

                string? parentDirectory = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrWhiteSpace(parentDirectory))
                    Directory.CreateDirectory(parentDirectory);

                using Stream source = entry.Open();
                using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                CopyArchiveEntryWithLimit(
                    source,
                    destination,
                    entry.Length,
                    maximumEntryBytes,
                    cancellationToken);
            }
        }

        private static void AddGitHubHeaders(HttpRequestMessage request, bool includeApiVersion)
        {
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("MonitorSwitcher", "1.0"));
            if (includeApiVersion)
            {
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            }
        }

        private static bool IsExpectedReleaseApiUri(Uri uri)
            => IsExactHttpsUri(uri, LatestReleaseApiUri);

        private static bool IsSafeGitHubDownloadRedirectUri(Uri uri)
        {
            if (!IsSafeHttpsUri(uri) || !string.IsNullOrEmpty(uri.Fragment))
                return false;

            return uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                   uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
                   uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsExactHttpsUri(Uri actual, Uri expected)
            => IsSafeHttpsUri(actual) &&
               actual.Host.Equals(expected.Host, StringComparison.OrdinalIgnoreCase) &&
               actual.AbsolutePath.Equals(expected.AbsolutePath, StringComparison.Ordinal) &&
               string.IsNullOrEmpty(actual.Query) &&
               string.IsNullOrEmpty(actual.Fragment);

        private static bool IsSafeHttpsUri(Uri uri)
            => uri.IsAbsoluteUri &&
               uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
               string.IsNullOrEmpty(uri.UserInfo) &&
               (uri.IsDefaultPort || uri.Port == 443);

        private static bool IsRedirectStatusCode(HttpStatusCode statusCode)
            => statusCode is HttpStatusCode.MovedPermanently or
                HttpStatusCode.Redirect or
                HttpStatusCode.RedirectMethod or
                HttpStatusCode.TemporaryRedirect or
                HttpStatusCode.PermanentRedirect;

        private static void ValidateDownloadResponse(
            HttpResponseMessage response,
            long maximumBytes,
            Func<Uri, bool> isAllowedFinalUri)
        {
            Uri? finalUri = response.RequestMessage?.RequestUri;
            if (finalUri == null || !isAllowedFinalUri(finalUri))
                throw new InvalidDataException("The download was redirected to an unexpected address.");

            response.EnsureSuccessStatusCode();
            long? contentLength = response.Content.Headers.ContentLength;
            if (contentLength == 0)
                throw new InvalidDataException("The server returned an empty download.");
            if (contentLength > maximumBytes)
                throw new InvalidDataException(
                    $"The server reported a download larger than the {FormatByteLimit(maximumBytes)} limit.");
        }

        private static GitHubReleaseAsset? FindUniqueReleaseAsset(
            IEnumerable<GitHubReleaseAsset> assets,
            string expectedName,
            bool required)
        {
            GitHubReleaseAsset[] matches = assets
                .Where(asset => asset.Name.Equals(expectedName, StringComparison.Ordinal))
                .ToArray();
            if (matches.Length > 1)
                throw new InvalidDataException($"Release {expectedName} is listed more than once.");
            if (matches.Length == 0 && required)
                throw new InvalidDataException($"The release is missing the expected asset {expectedName}.");
            return matches.SingleOrDefault();
        }

        private static void ValidateReleaseAsset(
            GitHubReleaseAsset asset,
            string releaseTag,
            string expectedName,
            long maximumBytes)
        {
            if (!IsSafeFileName(asset.Name) || !asset.Name.Equals(expectedName, StringComparison.Ordinal))
                throw new InvalidDataException("The release contains an unsafe or unexpected asset name.");
            if (asset.Size < 1 || asset.Size > maximumBytes)
                throw new InvalidDataException($"The reported size of {expectedName} is outside the allowed range.");
            if (!Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out Uri? downloadUri))
                throw new InvalidDataException($"The download address for {expectedName} is invalid.");
            if (!IsExpectedGitHubReleaseAssetUri(downloadUri, releaseTag, expectedName))
                throw new InvalidDataException($"The download address for {expectedName} is not the expected GitHub release path.");
        }

        private static bool IsSafeFileName(string value)
            => !string.IsNullOrWhiteSpace(value) &&
               value.Equals(Path.GetFileName(value), StringComparison.Ordinal) &&
               !value.Contains('/') &&
               !value.Contains('\\') &&
               IsSafePathSegment(value);

        private static void VerifyExpectedDownloadSize(string path, long expectedBytes, string displayName)
        {
            long actualBytes = new FileInfo(path).Length;
            if (actualBytes != expectedBytes)
            {
                throw new InvalidDataException(
                    $"Downloaded size mismatch for {displayName}: GitHub reported {expectedBytes} bytes but received {actualBytes} bytes.");
            }
        }

        private static string VerifyPublishedSha256(
            string assetPath,
            string checksumPath,
            string expectedAssetName,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string checksumText = File.ReadAllText(checksumPath, Encoding.UTF8);
            if (!TryParsePublishedSha256(checksumText, expectedAssetName, out string expectedHash))
                throw new InvalidDataException("The published SHA-256 sidecar has an unexpected format or filename.");

            string actualHash = ComputeSha256(assetPath, cancellationToken);
            if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded update does not match its published SHA-256 checksum.");
            return actualHash;
        }

        private static void VerifyGitHubDigestWhenPresent(string actualHash, string? publishedDigest)
        {
            if (string.IsNullOrWhiteSpace(publishedDigest))
                return;

            const string prefix = "sha256:";
            if (!publishedDigest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !IsSha256(publishedDigest[prefix.Length..]))
            {
                throw new InvalidDataException("GitHub returned an unsupported or malformed release-asset digest.");
            }
            if (!actualHash.Equals(publishedDigest[prefix.Length..], StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded update does not match GitHub's release-asset digest.");
        }

        private static bool IsSha256(string? value)
            => value is { Length: 64 } && value.All(Uri.IsHexDigit);

        internal static string ComputeSha256(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.SequentialScan);
            return ComputeSha256(stream, cancellationToken);
        }

        internal static string ComputeSha256(Stream stream, CancellationToken cancellationToken)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = stream.Read(buffer, 0, buffer.Length);
                if (count == 0)
                    break;
                hash.AppendData(buffer, 0, count);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return Convert.ToHexString(hash.GetHashAndReset());
        }

        private static void CopyArchiveEntryWithLimit(
            Stream source,
            Stream destination,
            long declaredLength,
            long maximumBytes,
            CancellationToken cancellationToken)
        {
            var buffer = new byte[81920];
            long written = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = source.Read(buffer, 0, buffer.Length);
                if (count == 0)
                    break;

                written = checked(written + count);
                if (written > maximumBytes || written > declaredLength)
                    throw new InvalidDataException("An archive entry exceeded its declared or allowed size.");
                destination.Write(buffer, 0, count);
            }

            if (written != declaredLength)
                throw new InvalidDataException("An archive entry did not match its declared size.");
        }

        private static bool IsZipLinkOrReparsePoint(ZipArchiveEntry entry)
        {
            uint attributes = unchecked((uint)entry.ExternalAttributes);
            uint unixFileType = (attributes >> 16) & 0xF000;
            return unixFileType == 0xA000 ||
                   (attributes & (uint)FileAttributes.ReparsePoint) != 0;
        }

        private static bool IsSafePathSegment(string segment)
        {
            if (string.IsNullOrWhiteSpace(segment) ||
                segment is "." or ".." ||
                segment.Length > 255 ||
                segment.EndsWith(' ') ||
                segment.EndsWith('.') ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                return false;
            }

            string deviceName = segment.Split('.')[0];
            if (deviceName.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
                deviceName.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                deviceName.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
                deviceName.Equals("NUL", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (deviceName.Length == 4 &&
                (deviceName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                 deviceName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                deviceName[3] is >= '1' and <= '9')
            {
                return false;
            }
            return true;
        }

        private static bool IsReservedReleasePath(string normalisedPath)
            => normalisedPath.Split('/').Any(segment =>
                segment.Equals(ReleaseManifestFileName, StringComparison.OrdinalIgnoreCase) ||
                segment.Equals(ReleaseArchiveFileName, StringComparison.OrdinalIgnoreCase));

        private static void ValidateMonitorSwitcherPackage(
            string packageDirectory,
            SemanticVersion expectedVersion,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string[] requiredFiles =
            {
                "MonitorSwitcher.exe",
                "MonitorSwitcher.dll",
                "MonitorSwitcher.deps.json",
                "MonitorSwitcher.runtimeconfig.json",
                "LICENSE",
                "README.md",
                "RELEASE_NOTES.md",
                "THIRD-PARTY-NOTICES.md",
                "DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt"
            };
            foreach (string requiredFile in requiredFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string requiredPath = Path.Combine(packageDirectory, requiredFile);
                if (!File.Exists(requiredPath) || new FileInfo(requiredPath).Length == 0)
                    throw new InvalidDataException($"The update package is missing {requiredFile}.");
            }

            string packageRoot = Path.GetFullPath(packageDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string[] executableExtensions = { ".exe", ".com", ".cpl", ".msi", ".msp", ".msix", ".scr" };
            foreach (string file in EnumeratePackageFilesSafely(packageRoot, cancellationToken))
            {
                string relativePath = Path.GetRelativePath(packageRoot, file).Replace('\\', '/');
                string extension = Path.GetExtension(file);
                if (!executableExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                    continue;

                bool allowed = relativePath.Equals("MonitorSwitcher.exe", StringComparison.OrdinalIgnoreCase) ||
                               relativePath.Equals("createdump.exe", StringComparison.OrdinalIgnoreCase);
                if (!allowed)
                    throw new InvalidDataException($"The update package contains an unexpected executable: {relativePath}");
            }

            string executablePath = Path.Combine(packageDirectory, "MonitorSwitcher.exe");
            cancellationToken.ThrowIfCancellationRequested();
            ValidatePeMachine(executablePath, PeMachineAmd64);
            cancellationToken.ThrowIfCancellationRequested();
            FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(executablePath);
            if (!string.Equals(versionInfo.ProductName, "MonitorSwitcher", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The update executable does not identify itself as MonitorSwitcher.");
            if (!SemanticVersion.TryParse(versionInfo.ProductVersion, out SemanticVersion packagedVersion) ||
                packagedVersion.CompareTo(expectedVersion) != 0)
            {
                throw new InvalidDataException(
                    $"The update executable version '{versionInfo.ProductVersion}' does not match release {expectedVersion}.");
            }
        }

        private static void ValidatePeMachine(string executablePath, ushort expectedMachine)
        {
            using var stream = new FileStream(executablePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            if (stream.Length < 64 || reader.ReadUInt16() != 0x5A4D)
                throw new InvalidDataException($"{Path.GetFileName(executablePath)} is not a valid Windows PE file.");

            stream.Position = 0x3C;
            int peOffset = reader.ReadInt32();
            if (peOffset < 64 || peOffset > stream.Length - 6)
                throw new InvalidDataException($"{Path.GetFileName(executablePath)} has an invalid PE header.");
            stream.Position = peOffset;
            if (reader.ReadUInt32() != 0x00004550 || reader.ReadUInt16() != expectedMachine)
                throw new InvalidDataException($"{Path.GetFileName(executablePath)} is not the expected x64 Windows executable.");
        }

        private void WriteReleaseManifest(
            string packageDirectory,
            string releaseTag,
            string releaseVersion,
            string archiveSha256,
            CancellationToken cancellationToken)
        {
            var files = EnumeratePackageFilesSafely(
                    packageDirectory,
                    cancellationToken,
                    MaxAppArchiveEntries - AppOwnedPackageEntryCount,
                    MaxAppExpandedBytes - MaxReleaseApiBytes - MaxAppArchiveBytes)
                .Select(path => new ReleaseManifestFile
                {
                    Path = Path.GetRelativePath(packageDirectory, path).Replace('\\', '/'),
                    Length = new FileInfo(path).Length,
                    Sha256 = ComputeSha256(path, cancellationToken)
                })
                .OrderBy(file => file.Path, StringComparer.Ordinal)
                .ToList();
            if (files.Count == 0 || files.Count > MaxAppArchiveEntries - AppOwnedPackageEntryCount)
                throw new InvalidDataException("The extracted update has an invalid file count.");

            var manifest = new ReleaseManifest
            {
                TagName = releaseTag,
                Version = releaseVersion,
                ArchiveSha256 = archiveSha256,
                Files = files
            };
            string manifestPath = Path.Combine(packageDirectory, ReleaseManifestFileName);
            cancellationToken.ThrowIfCancellationRequested();
            File.WriteAllText(
                manifestPath,
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            if (new FileInfo(manifestPath).Length > MaxReleaseApiBytes)
                throw new InvalidDataException("The verified release manifest exceeds its permitted size.");
        }

        private static void RetainVerifiedArchive(
            string archivePath,
            string packageDirectory,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string retainedArchivePath = Path.Combine(packageDirectory, ReleaseArchiveFileName);
            if (File.Exists(retainedArchivePath) || Directory.Exists(retainedArchivePath))
                throw new InvalidDataException("The extracted update collides with MonitorSwitcher's reserved release archive path.");

            File.Move(archivePath, retainedArchivePath);
            FileAttributes attributes = File.GetAttributes(retainedArchivePath);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The retained release archive is a link or reparse point.");
            File.SetAttributes(retainedArchivePath, attributes | FileAttributes.Hidden);
            cancellationToken.ThrowIfCancellationRequested();
        }

        private void ValidateReusablePackage(
            string packageDirectory,
            string releaseTag,
            SemanticVersion releaseVersion,
            string expectedArchiveSha256,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Dictionary<string, ReleaseManifestFile> expectedFiles =
                ReadAuthoritativeArchiveFileMap(packageDirectory, expectedArchiveSha256, cancellationToken);
            ValidateLocalReleaseManifest(
                packageDirectory,
                releaseTag,
                releaseVersion,
                expectedArchiveSha256,
                expectedFiles,
                cancellationToken);

            string[] actualFiles = EnumeratePackageFilesSafely(packageDirectory, cancellationToken)
                .Where(path => !IsTopLevelAppOwnedPackageFile(packageDirectory, path))
                .ToArray();
            if (actualFiles.Length != expectedFiles.Count)
                throw new InvalidDataException("The existing update no longer matches its retained verified archive.");
            foreach (string actualPath in actualFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string relativePath = Path.GetRelativePath(packageDirectory, actualPath).Replace('\\', '/');
                if (!expectedFiles.TryGetValue(relativePath, out ReleaseManifestFile? expected) ||
                    new FileInfo(actualPath).Length != expected.Length ||
                    !ComputeSha256(actualPath, cancellationToken).Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("The existing update no longer matches its retained verified archive.");
                }
            }

            _packageValidator(packageDirectory, releaseVersion, cancellationToken);
        }

        private static Dictionary<string, ReleaseManifestFile> ReadAuthoritativeArchiveFileMap(
            string packageDirectory,
            string expectedArchiveSha256,
            CancellationToken cancellationToken)
        {
            string archivePath = Path.Combine(packageDirectory, ReleaseArchiveFileName);
            if (!File.Exists(archivePath) ||
                (File.GetAttributes(archivePath) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    "The retained verified archive is missing or unsafe. Remove this download before trying again.");
            }

            using var archiveStream = new FileStream(
                archivePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.SequentialScan);
            if (archiveStream.Length is < 1 or > MaxAppArchiveBytes)
                throw new InvalidDataException("The retained release archive is outside the permitted size range.");

            string actualArchiveSha256 = ComputeSha256(archiveStream, cancellationToken);
            if (!actualArchiveSha256.Equals(expectedArchiveSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "The retained release archive no longer matches its freshly downloaded published checksum.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            archiveStream.Position = 0;
            using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);
            int maximumArchiveEntries = MaxAppArchiveEntries - AppOwnedPackageEntryCount;
            if (archive.Entries.Count == 0 || archive.Entries.Count > maximumArchiveEntries)
            {
                throw new InvalidDataException(
                    $"The retained archive entry count is outside the allowed range (maximum {maximumArchiveEntries}).");
            }

            long maximumExpandedBytes = MaxAppExpandedBytes - MaxReleaseApiBytes - MaxAppArchiveBytes;
            long totalExpandedBytes = 0;
            var archivePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var expectedFiles = new Dictionary<string, ReleaseManifestFile>(StringComparer.OrdinalIgnoreCase);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string rawPath = entry.FullName.Replace('\\', '/');
                bool isDirectory = rawPath.EndsWith("/", StringComparison.Ordinal);
                string trimmedPath = rawPath.TrimEnd('/');

                if (!IsSafeArchivePath(rawPath))
                    throw new InvalidDataException($"The retained archive contains an unsafe path: {entry.FullName}");
                if (IsReservedReleasePath(trimmedPath))
                    throw new InvalidDataException("The retained archive contains an app-reserved update-verification path.");
                if (IsZipLinkOrReparsePoint(entry))
                    throw new InvalidDataException($"The retained archive contains a link or reparse point: {entry.FullName}");
                if (!archivePaths.Add(trimmedPath))
                    throw new InvalidDataException($"The retained archive contains duplicate paths: {entry.FullName}");
                if (isDirectory)
                    continue;

                if (entry.Length < 0 || entry.Length > MaxAppArchiveBytes)
                    throw new InvalidDataException($"Retained archive entry {entry.FullName} is larger than allowed.");
                totalExpandedBytes = checked(totalExpandedBytes + entry.Length);
                if (totalExpandedBytes > maximumExpandedBytes)
                    throw new InvalidDataException("The retained archive expands beyond the allowed size.");

                using Stream entryStream = entry.Open();
                expectedFiles.Add(trimmedPath, new ReleaseManifestFile
                {
                    Path = trimmedPath,
                    Length = entry.Length,
                    Sha256 = ComputeArchiveEntrySha256(
                        entryStream,
                        entry.Length,
                        MaxAppArchiveBytes,
                        cancellationToken)
                });
            }

            if (expectedFiles.Count == 0)
                throw new InvalidDataException("The retained archive contains no application files.");
            return expectedFiles;
        }

        private static string ComputeArchiveEntrySha256(
            Stream source,
            long declaredLength,
            long maximumBytes,
            CancellationToken cancellationToken)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long hashedBytes = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = source.Read(buffer, 0, buffer.Length);
                if (count == 0)
                    break;

                hashedBytes = checked(hashedBytes + count);
                if (hashedBytes > maximumBytes || hashedBytes > declaredLength)
                    throw new InvalidDataException("An archive entry exceeded its declared or allowed size.");
                hash.AppendData(buffer, 0, count);
            }

            if (hashedBytes != declaredLength)
                throw new InvalidDataException("An archive entry did not match its declared size.");
            cancellationToken.ThrowIfCancellationRequested();
            return Convert.ToHexString(hash.GetHashAndReset());
        }

        private static void ValidateLocalReleaseManifest(
            string packageDirectory,
            string releaseTag,
            SemanticVersion releaseVersion,
            string expectedArchiveSha256,
            IReadOnlyDictionary<string, ReleaseManifestFile> authoritativeFiles,
            CancellationToken cancellationToken)
        {
            string manifestPath = Path.Combine(packageDirectory, ReleaseManifestFileName);
            if (!File.Exists(manifestPath) ||
                (File.GetAttributes(manifestPath) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    "The local release manifest is missing or unsafe. Remove this download before trying again.");
            }

            ReleaseManifest manifest;
            try
            {
                byte[] manifestBytes = ReadSmallFileWithLimit(
                    manifestPath,
                    MaxReleaseApiBytes,
                    cancellationToken);
                manifest = JsonSerializer.Deserialize<ReleaseManifest>(manifestBytes)
                    ?? throw new InvalidDataException("The saved release manifest is empty.");
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("The saved release manifest is invalid.", ex);
            }

            if (!string.Equals(manifest.TagName, releaseTag, StringComparison.Ordinal) ||
                !string.Equals(manifest.Version, releaseVersion.ToString(), StringComparison.Ordinal) ||
                !IsSha256(manifest.ArchiveSha256) ||
                !manifest.ArchiveSha256.Equals(expectedArchiveSha256, StringComparison.OrdinalIgnoreCase) ||
                manifest.Files == null ||
                manifest.Files.Count != authoritativeFiles.Count)
            {
                throw new InvalidDataException("The saved release manifest does not match the retained verified archive.");
            }

            var manifestFiles = new Dictionary<string, ReleaseManifestFile>(StringComparer.OrdinalIgnoreCase);
            foreach (ReleaseManifestFile? file in manifest.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (file == null ||
                    !IsSafeArchivePath(file.Path) ||
                    file.Path.EndsWith("/", StringComparison.Ordinal) ||
                    IsReservedReleasePath(file.Path.Replace('\\', '/').TrimEnd('/')) ||
                    !IsSha256(file.Sha256) ||
                    file.Length < 0 ||
                    !manifestFiles.TryAdd(file.Path.Replace('\\', '/'), file))
                {
                    throw new InvalidDataException("The saved release manifest contains an invalid file entry.");
                }
            }

            foreach ((string path, ReleaseManifestFile expected) in authoritativeFiles)
            {
                if (!manifestFiles.TryGetValue(path, out ReleaseManifestFile? saved) ||
                    saved.Length != expected.Length ||
                    !saved.Sha256.Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("The saved release manifest does not match the retained verified archive.");
                }
            }
        }

        private static byte[] ReadSmallFileWithLimit(
            string path,
            long maximumBytes,
            CancellationToken cancellationToken)
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.SequentialScan);
            if (stream.Length is < 2 || stream.Length > maximumBytes || stream.Length > int.MaxValue)
                throw new InvalidDataException("The saved release manifest is outside the permitted size range.");

            var bytes = new byte[(int)stream.Length];
            int offset = 0;
            while (offset < bytes.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = stream.Read(bytes, offset, bytes.Length - offset);
                if (count == 0)
                    throw new InvalidDataException("The saved release manifest changed while it was being read.");
                offset += count;
            }
            if (stream.ReadByte() != -1)
                throw new InvalidDataException("The saved release manifest changed while it was being read.");
            cancellationToken.ThrowIfCancellationRequested();
            return bytes;
        }

        private static bool IsTopLevelAppOwnedPackageFile(string packageDirectory, string filePath)
        {
            string relativePath = Path.GetRelativePath(packageDirectory, filePath).Replace('\\', '/');
            return relativePath.Equals(ReleaseManifestFileName, StringComparison.OrdinalIgnoreCase) ||
                   relativePath.Equals(ReleaseArchiveFileName, StringComparison.OrdinalIgnoreCase);
        }

        internal static IEnumerable<string> EnumeratePackageFilesSafely(
            string packageDirectory,
            CancellationToken cancellationToken,
            int maximumEntries = MaxAppArchiveEntries,
            long maximumExpandedBytes = MaxAppExpandedBytes)
        {
            if (maximumEntries < 1)
                throw new ArgumentOutOfRangeException(nameof(maximumEntries));
            if (maximumExpandedBytes < 0)
                throw new ArgumentOutOfRangeException(nameof(maximumExpandedBytes));

            string root = Path.GetFullPath(packageDirectory);
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The extracted update root is a link or reparse point.");

            var pending = new Stack<string>();
            pending.Push(root);
            int entryCount = 0;
            long totalBytes = 0;
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string directory = pending.Pop();
                foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    entryCount++;
                    if (entryCount > maximumEntries)
                        throw new InvalidDataException("The extracted update contains too many files or directories.");

                    FileAttributes attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("The extracted update contains a link or reparse point.");
                    if ((attributes & FileAttributes.Directory) != 0)
                        pending.Push(entry);
                    else
                    {
                        totalBytes = checked(totalBytes + new FileInfo(entry).Length);
                        if (totalBytes > maximumExpandedBytes)
                            throw new InvalidDataException("The extracted update exceeds the permitted expanded size.");
                        yield return entry;
                    }
                }
            }
        }

        private static string? TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        public void Dispose()
        {
            if (_ownsHttpClient)
                _httpClient.Dispose();
        }

        internal readonly struct SemanticVersion : IComparable<SemanticVersion>
        {
            private readonly int _major;
            private readonly int _minor;
            private readonly int _patch;
            private readonly string[] _preRelease;
            private readonly bool _hasBuildMetadata;

            private SemanticVersion(
                int major,
                int minor,
                int patch,
                string[] preRelease,
                bool hasBuildMetadata)
            {
                _major = major;
                _minor = minor;
                _patch = patch;
                _preRelease = preRelease;
                _hasBuildMetadata = hasBuildMetadata;
            }

            public static bool TryParseReleaseTag(string? value, out SemanticVersion version)
            {
                version = default;
                if (string.IsNullOrWhiteSpace(value) || !value.StartsWith('v'))
                    return false;
                if (!TryParse(value, out SemanticVersion parsed) ||
                    parsed._preRelease.Length != 0 ||
                    parsed._hasBuildMetadata ||
                    !value.Equals($"v{parsed}", StringComparison.Ordinal))
                {
                    return false;
                }
                version = parsed;
                return true;
            }

            public static bool TryParse(string? value, out SemanticVersion version)
            {
                version = default;
                if (string.IsNullOrWhiteSpace(value) || !value.Equals(value.Trim(), StringComparison.Ordinal))
                    return false;

                string text = value;
                if (text.StartsWith('v'))
                    text = text[1..];

                int plusIndex = text.IndexOf('+');
                string? build = null;
                if (plusIndex >= 0)
                {
                    if (text.IndexOf('+', plusIndex + 1) >= 0)
                        return false;
                    build = text[(plusIndex + 1)..];
                    text = text[..plusIndex];
                }

                int dashIndex = text.IndexOf('-');
                string? preRelease = null;
                if (dashIndex >= 0)
                {
                    preRelease = text[(dashIndex + 1)..];
                    text = text[..dashIndex];
                }

                string[] core = text.Split('.');
                if (core.Length != 3 ||
                    !TryParseCoreNumber(core[0], out int major) ||
                    !TryParseCoreNumber(core[1], out int minor) ||
                    !TryParseCoreNumber(core[2], out int patch))
                {
                    return false;
                }

                string[] preReleaseParts = string.IsNullOrEmpty(preRelease)
                    ? Array.Empty<string>()
                    : preRelease.Split('.');
                if (preRelease != null &&
                    (preReleaseParts.Any(part => !IsValidIdentifier(part)) ||
                     preReleaseParts.Any(part => IsNumericIdentifier(part) && part.Length > 1 && part[0] == '0')))
                {
                    return false;
                }
                if (build != null && build.Split('.').Any(part => !IsValidIdentifier(part)))
                    return false;

                version = new SemanticVersion(major, minor, patch, preReleaseParts, build != null);
                return true;
            }

            public int CompareTo(SemanticVersion other)
            {
                int comparison = _major.CompareTo(other._major);
                if (comparison != 0) return comparison;
                comparison = _minor.CompareTo(other._minor);
                if (comparison != 0) return comparison;
                comparison = _patch.CompareTo(other._patch);
                if (comparison != 0) return comparison;

                string[] left = _preRelease ?? Array.Empty<string>();
                string[] right = other._preRelease ?? Array.Empty<string>();
                if (left.Length == 0 || right.Length == 0)
                    return left.Length == right.Length ? 0 : left.Length == 0 ? 1 : -1;

                int commonLength = Math.Min(left.Length, right.Length);
                for (int index = 0; index < commonLength; index++)
                {
                    comparison = CompareIdentifier(left[index], right[index]);
                    if (comparison != 0) return comparison;
                }
                return left.Length.CompareTo(right.Length);
            }

            public override string ToString()
            {
                string core = $"{_major}.{_minor}.{_patch}";
                return (_preRelease?.Length ?? 0) == 0
                    ? core
                    : $"{core}-{string.Join('.', _preRelease ?? Array.Empty<string>())}";
            }

            private static bool TryParseCoreNumber(string value, out int number)
            {
                number = 0;
                return IsNumericIdentifier(value) &&
                       (value.Length == 1 || value[0] != '0') &&
                       int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number);
            }

            private static bool IsValidIdentifier(string value)
                => value.Length > 0 && value.All(character =>
                    (character >= '0' && character <= '9') ||
                    (character >= 'A' && character <= 'Z') ||
                    (character >= 'a' && character <= 'z') ||
                    character == '-');

            private static bool IsNumericIdentifier(string value)
                => value.Length > 0 && value.All(character => character >= '0' && character <= '9');

            private static int CompareIdentifier(string left, string right)
            {
                bool leftNumeric = IsNumericIdentifier(left);
                bool rightNumeric = IsNumericIdentifier(right);
                if (leftNumeric != rightNumeric)
                    return leftNumeric ? -1 : 1;
                if (!leftNumeric)
                    return string.Compare(left, right, StringComparison.Ordinal);
                int lengthComparison = left.Length.CompareTo(right.Length);
                return lengthComparison != 0
                    ? lengthComparison
                    : string.Compare(left, right, StringComparison.Ordinal);
            }
        }

        private sealed class GitHubRelease
        {
            [JsonPropertyName("tag_name")]
            public string TagName { get; set; } = string.Empty;

            [JsonPropertyName("draft")]
            public bool Draft { get; set; }

            [JsonPropertyName("prerelease")]
            public bool Prerelease { get; set; }

            [JsonPropertyName("assets")]
            public List<GitHubReleaseAsset> Assets { get; set; } = new();
        }

        private sealed class GitHubReleaseAsset
        {
            [JsonPropertyName("name")]
            public string Name { get; set; } = string.Empty;

            [JsonPropertyName("browser_download_url")]
            public string DownloadUrl { get; set; } = string.Empty;

            [JsonPropertyName("size")]
            public long Size { get; set; }

            [JsonPropertyName("digest")]
            public string? Digest { get; set; }
        }

        private sealed class ReleaseManifest
        {
            public string TagName { get; set; } = string.Empty;
            public string Version { get; set; } = string.Empty;
            public string ArchiveSha256 { get; set; } = string.Empty;
            public List<ReleaseManifestFile> Files { get; set; } = new();
        }

        private sealed class ReleaseManifestFile
        {
            public string Path { get; set; } = string.Empty;
            public long Length { get; set; }
            public string Sha256 { get; set; } = string.Empty;
        }
    }

    internal enum AppUpdateStatus
    {
        NoPublishedRelease,
        Current,
        Ready,
        Reused
    }

    internal sealed record AppUpdateResult(
        AppUpdateStatus Status,
        string CurrentVersion,
        string ReleaseVersion,
        string? PackageDirectory,
        string? CleanupWarning)
    {
        internal static AppUpdateResult NoPublishedRelease()
            => new(AppUpdateStatus.NoPublishedRelease, string.Empty, string.Empty, null, null);

        internal static AppUpdateResult Current(string currentVersion, string releaseVersion)
            => new(AppUpdateStatus.Current, currentVersion, releaseVersion, null, null);

        internal static AppUpdateResult Ready(string releaseVersion, string packageDirectory)
            => new(AppUpdateStatus.Ready, string.Empty, releaseVersion, packageDirectory, null);

        internal static AppUpdateResult Reused(string releaseVersion, string packageDirectory)
            => new(AppUpdateStatus.Reused, string.Empty, releaseVersion, packageDirectory, null);
    }

    internal sealed record AppUpdateProgress(string Message, long BytesReceived = 0, long? TotalBytes = null)
    {
        internal string DisplayText
        {
            get
            {
                if (BytesReceived <= 0)
                    return Message;
                string received = AppUpdateService.FormatByteLimit(BytesReceived);
                return TotalBytes is > 0
                    ? $"{Message} {received} of {AppUpdateService.FormatByteLimit(TotalBytes.Value)}"
                    : $"{Message} {received}";
            }
        }
    }
}
