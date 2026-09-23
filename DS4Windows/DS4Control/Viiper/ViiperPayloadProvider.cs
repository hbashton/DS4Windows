/*
DS4Windows
Copyright (C) 2026 hbashton
SPDX-License-Identifier: GPL-3.0-or-later
*/

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace DS4Windows
{
    /// <summary>A caller-owned verified source. This never installs or executes it.</summary>
    internal sealed class ViiperPayloadLease : IDisposable
    {
        private IDisposable pin;
        private Action cleanup;
        private int disposed;

        internal ViiperPayloadLease(string path, IDisposable pin = null,
            Action cleanup = null)
        {
            Path = path ?? throw new ArgumentNullException(nameof(path));
            this.pin = pin;
            this.cleanup = cleanup;
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try { pin?.Dispose(); }
            finally
            {
                pin = null;
                Action finish = cleanup;
                cleanup = null;
                finish?.Invoke();
            }
        }
    }

    /// <summary>
    /// Acquires only the broker identity compiled into this application. A
    /// verified private copy stays pinned until its consumer has finished.
    /// No target, installed directory, registry or process is modified here.
    /// </summary>
    internal sealed class ViiperPayloadProvider
    {
        internal const long MaximumPayloadBytes = 32L * 1024 * 1024;
        internal static readonly TimeSpan AcquisitionTimeout = TimeSpan.FromSeconds(30);
        private const int BufferSize = 64 * 1024;
        private const int MaximumRedirects = 4;
        private static readonly HttpClient ProductionClient = new(
            new HttpClientHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None
            })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        private readonly HttpClient client;
        private readonly string stagingParent;
        private readonly string fileName;
        private readonly byte[] expectedHash;
        private readonly long maximumBytes;
        private readonly TimeSpan timeout;
        internal Uri DownloadUri { get; }

        internal ViiperPayloadProvider() : this(ProductionClient, System.IO.Path.GetTempPath(),
            ViiperSetupManager.BundledViiperName, ViiperSetupManager.SupportedViiperReleaseTag,
            ViiperSetupManager.SupportedViiperSha256, MaximumPayloadBytes, AcquisitionTimeout)
        {
        }

        // An explicit internal seam for offline tests; production always uses
        // the parameterless constructor and immutable compiled constants.
        internal ViiperPayloadProvider(HttpClient client, string stagingParent,
            string fileName, string releaseTag, string sha256,
            long maximumBytes = MaximumPayloadBytes, TimeSpan? timeout = null)
        {
            this.client = client ?? throw new ArgumentNullException(nameof(client));
            if (string.IsNullOrEmpty(fileName) ||
                fileName.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 ||
                !fileName.StartsWith("VIIPER-", StringComparison.Ordinal) ||
                !fileName.EndsWith("-x64.exe", StringComparison.Ordinal) ||
                string.IsNullOrEmpty(releaseTag) || !releaseTag.StartsWith("v", StringComparison.Ordinal) ||
                fileName != "VIIPER-" + releaseTag.Substring(1) + "-x64.exe")
                throw new ArgumentException("The pinned release and flat payload name must agree.");
            if (sha256 == null || sha256.Length != 64)
                throw new ArgumentException("An exact SHA256 is required.", nameof(sha256));
            expectedHash = Convert.FromHexString(sha256);
            if (maximumBytes < 1 || maximumBytes > MaximumPayloadBytes)
                throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            this.timeout = timeout ?? AcquisitionTimeout;
            if (this.timeout <= TimeSpan.Zero || this.timeout > AcquisitionTimeout)
                throw new ArgumentOutOfRangeException(nameof(timeout));
            this.stagingParent = FullLocalPath(stagingParent);
            this.fileName = fileName;
            this.maximumBytes = maximumBytes;
            DownloadUri = new Uri("https://github.com/hbashton/VIIPER/releases/download/" +
                Uri.EscapeDataString(releaseTag) + "/" + Uri.EscapeDataString(fileName));
        }

        internal async Task<ViiperPayloadLease> AcquireAsync(string packageRoot,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string root = FullLocalPath(packageRoot);
            AssertOrdinaryAncestors(root);
            AssertOrdinaryAncestors(stagingParent);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            CancellationToken token = deadline.Token;
            string stage = System.IO.Path.Combine(stagingParent,
                "DS4Windows.ViiperPayload." + Guid.NewGuid().ToString("N"));
            string payload = System.IO.Path.Combine(stage, fileName);
            Directory.CreateDirectory(stage);
            bool handedOff = false;
            bool ownsPayload = false;
            FileStream pin = null;
            try
            {
                AssertOrdinaryAncestors(stage);
                string local = System.IO.Path.Combine(root, "extras", fileName);
                try
                {
                    AssertOrdinaryAncestors(local);
                    using var source = new FileStream(local, FileMode.Open, FileAccess.Read,
                        FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    if (source.Length == 0 || source.Length > maximumBytes)
                        throw new InvalidDataException("Bundled broker has an invalid size.");
                    pin = await CopyAndVerifyAsync(source, payload, source.Length, token,
                        () => ownsPayload = true).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException || error is InvalidDataException || error is UnauthorizedAccessException)
                {
                    // Missing, unreadable or invalid bundled content is never
                    // adopted. Only the exact official pinned payload may replace it.
                    pin?.Dispose();
                    pin = null;
                    token.ThrowIfCancellationRequested();
                    if (ownsPayload) File.Delete(payload);
                    ownsPayload = false;
                }

                if (pin == null)
                {
                    using HttpResponseMessage response = await GetResponseAsync(token).ConfigureAwait(false);
                    long? declaredSize = response.Content.Headers.ContentLength;
                    if (declaredSize.HasValue && (declaredSize.Value < 1 || declaredSize.Value > maximumBytes))
                        throw new InvalidDataException("Official broker response exceeds the size limit or is empty.");
                    using Stream body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                    pin = await CopyAndVerifyAsync(body, payload, declaredSize, token,
                        () => ownsPayload = true).ConfigureAwait(false);
                }

                var lease = new ViiperPayloadLease(payload, pin, () => CleanStage(payload, stage, true));
                handedOff = true;
                return lease;
            }
            finally
            {
                if (!handedOff)
                {
                    pin?.Dispose();
                    CleanStage(payload, stage, ownsPayload);
                }
            }
        }

        private async Task<HttpResponseMessage> GetResponseAsync(CancellationToken token)
        {
            Uri current = DownloadUri;
            for (int redirects = 0; ; redirects++)
            {
                token.ThrowIfCancellationRequested();
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                request.Headers.UserAgent.ParseAdd("DS4Windows-ViiperPayload/1");
                request.Headers.Accept.ParseAdd("application/octet-stream");
                HttpResponseMessage response = await client.SendAsync(request,
                    HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                bool keep = false;
                try
                {
                    // Also defend the injected-client seam against a handler
                    // which has performed redirects itself.
                    if (!AllowedDownloadLocation(response.RequestMessage?.RequestUri ?? current))
                        throw new InvalidDataException("Broker download redirected outside approved HTTPS hosts.");
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        keep = true;
                        return response;
                    }
                    int status = (int)response.StatusCode;
                    if (status is not (301 or 302 or 303 or 307 or 308) ||
                        response.Headers.Location == null || redirects >= MaximumRedirects)
                        throw new HttpRequestException("The pinned official VIIPER payload is unavailable.",
                            null, response.StatusCode);
                    Uri next = response.Headers.Location.IsAbsoluteUri
                        ? response.Headers.Location : new Uri(current, response.Headers.Location);
                    if (!AllowedDownloadLocation(next))
                        throw new InvalidDataException("Broker redirect is not an approved HTTPS release host.");
                    current = next;
                }
                finally
                {
                    if (!keep) response.Dispose();
                }
            }
        }

        private static bool AllowedDownloadLocation(Uri uri) =>
            uri != null && uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps &&
            uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
            (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
             uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
             uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase));

        private async Task<FileStream> CopyAndVerifyAsync(Stream source, string payload,
            long? declaredSize, CancellationToken token, Action created)
        {
            FileStream pin = null;
            try
            {
                var buffer = new byte[BufferSize];
                long bytes = 0;
                int count;
                using (var output = new FileStream(payload, FileMode.CreateNew, FileAccess.Write,
                           FileShare.None, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    created();
                    while ((count = await source.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
                    {
                        if (count > maximumBytes - bytes)
                            throw new InvalidDataException("Broker stream exceeds the strict size limit.");
                        await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                        bytes += count;
                    }
                    await output.FlushAsync(token).ConfigureAwait(false);
                }
                if (bytes == 0 || (declaredSize.HasValue && bytes != declaredSize.Value))
                    throw new InvalidDataException("Broker stream length does not match its metadata.");
                AssertOrdinaryAncestors(payload);
                // Verify after acquiring the final read-only pin. A replacement
                // in the write-close/read-open interval is therefore hashed too.
                // Its sharing mode permits ordinary readers but no writes/deletes.
                pin = new FileStream(payload, FileMode.Open, FileAccess.Read,
                    FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                byte[] actual = await SHA256.HashDataAsync(pin, token).ConfigureAwait(false);
                if (pin.Length != bytes || !CryptographicOperations.FixedTimeEquals(actual, expectedHash))
                    throw new InvalidDataException("VIIPER payload does not match the compiled SHA256.");
                token.ThrowIfCancellationRequested();
                return pin;
            }
            catch
            {
                pin?.Dispose();
                throw;
            }
        }

        private static string FullLocalPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathFullyQualified(path) ||
                path.StartsWith(@"\\", StringComparison.Ordinal))
                throw new ArgumentException("An absolute local filesystem path is required.", nameof(path));
            return System.IO.Path.GetFullPath(path);
        }

        private static void AssertOrdinaryAncestors(string path)
        {
            for (string node = path; !string.IsNullOrEmpty(node); node = System.IO.Path.GetDirectoryName(node))
            {
                try
                {
                    if ((File.GetAttributes(node) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Broker payload paths cannot contain reparse points.");
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
        }

        private static void CleanStage(string payload, string stage, bool ownsPayload)
        {
            // Never recursively delete: only this request's file and then its
            // empty generated directory. Cleanup failure cannot undo a repair.
            try
            {
                AssertOrdinaryAncestors(stage);
                if (ownsPayload) File.Delete(payload);
                Directory.Delete(stage, recursive: false);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
