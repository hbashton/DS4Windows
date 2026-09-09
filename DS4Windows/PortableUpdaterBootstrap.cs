using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DS4Windows;

internal sealed record PortableUpdaterTicket(string Root, string FilePath,
    Version Version, string Sha256, long Size);

/// <summary>
/// The portable app may exit only after launching a verified updater that
/// understands graceful broker shutdown and transactional package replacement.
/// In particular, 2.0.4 must never receive a portable update request.
/// </summary>
internal static class PortableUpdaterBootstrap
{
    internal static readonly Version MinimumVersion = new(2, 0, 5, 0);
    internal const long MaximumUpdaterBytes = 128L * 1024 * 1024;
    internal const string ReleaseApi = "https://api.github.com/repos/hbashton/DS4Updater/releases/latest";

    internal static async Task<PortableUpdaterTicket> PrepareAsync(HttpClient client,
        string directory, CancellationToken cancellationToken = default)
    {
        if (!Environment.Is64BitProcess)
            throw new InvalidOperationException("Safe portable updates currently require the x64 portable package.");
        string root = ValidatePackageRoot(directory);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        using var metadataResponse = await client.GetAsync(ReleaseApi,
            HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        metadataResponse.EnsureSuccessStatusCode();
        ValidateResponseOrigin(metadataResponse, ReleaseApi, metadata: true);
        if (metadataResponse.Content.Headers.ContentLength > 2 * 1024 * 1024)
            throw new InvalidDataException("The updater release response is too large.");
        using var metadataStream = await metadataResponse.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var metadataBuffer = new MemoryStream();
        await CopyBoundedAsync(metadataStream, metadataBuffer, 2 * 1024 * 1024, timeout.Token).ConfigureAwait(false);
        using var metadata = JsonDocument.Parse(metadataBuffer.ToArray());
        var asset = ReadVerifiedAsset(metadata.RootElement);
        string destination = Path.Combine(root, "DS4Updater.exe");
        var original = CaptureDestination(destination);
        if (File.Exists(destination) && Matches(destination, asset.Version, asset.Sha256, asset.Size))
            return new(root, destination, asset.Version, asset.Sha256, asset.Size);

        string temporary = Path.Combine(root, ".DS4Updater-download-" + Guid.NewGuid().ToString("N") + ".exe");
        bool ownsTemporary = false;
        try
        {
            using (var response = await client.GetAsync(asset.Url,
                       HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                ValidateResponseOrigin(response, asset.Url, metadata: false);
                if (response.Content.Headers.ContentLength is long length && length != asset.Size)
                    throw new InvalidDataException("The updater download length does not match its release.");
                using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    64 * 1024, FileOptions.Asynchronous);
                ownsTemporary = true;
                await CopyBoundedAsync(input, output, asset.Size, timeout.Token).ConfigureAwait(false);
                await output.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            if (!VerifyImage(temporary, asset.Version, asset.Sha256, asset.Size, out string mismatch))
                throw new InvalidDataException("The updater failed its release verification: " + mismatch + ". The existing updater was kept.");
            ValidatePackageRoot(root);
            if (CaptureDestination(destination) != original)
                throw new IOException("The existing updater changed during preparation. Its current file was kept; retry the update.");
            // Same-directory replacement happens only after all download and
            // identity checks; a locked updater causes a clean rejection.
            File.Move(temporary, destination, overwrite: original.Exists);
            return new(root, destination, asset.Version, asset.Sha256, asset.Size);
        }
        finally
        {
            if (ownsTemporary && File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal readonly record struct Asset(Version Version, string Url, string Sha256, long Size);

    internal static Asset ReadVerifiedAsset(JsonElement release)
    {
        if (!release.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False ||
            !release.TryGetProperty("prerelease", out var pre) || pre.ValueKind != JsonValueKind.False ||
            !release.TryGetProperty("tag_name", out var tagElement))
            throw new InvalidDataException("A published stable updater release is required.");
        string tag = tagElement.GetString();
        if (tag == null || !Regex.IsMatch(tag, @"^v\d+\.\d+\.\d+(?:\.\d+)?$", RegexOptions.CultureInvariant) ||
            !Version.TryParse(tag.Substring(1), out var parsed))
            throw new InvalidDataException("The updater release version is invalid.");
        Version version = NormalizeVersion(parsed);
        if (version < MinimumVersion)
            throw new InvalidOperationException("Portable updates require DS4Updater 2.0.5 or newer. Until that updater is published, download the portable ZIP and extract it into a new folder.");
        string expectedUrl = $"https://github.com/hbashton/DS4Updater/releases/download/{tag}/DS4Updater.exe";
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The updater release contains no download assets.");
        var matching = assets.EnumerateArray().Where(item => item.TryGetProperty("name", out var name) &&
            name.GetString() == "DS4Updater.exe").ToArray();
        if (matching.Length != 1) throw new InvalidDataException("The updater release has no unique x64 executable.");
        var asset = matching[0];
        if (!asset.TryGetProperty("browser_download_url", out var url) || url.GetString() != expectedUrl ||
            !asset.TryGetProperty("size", out var sizeElement) || !sizeElement.TryGetInt64(out long size) ||
            size <= 0 || size > MaximumUpdaterBytes ||
            !asset.TryGetProperty("digest", out var digestElement))
            throw new InvalidDataException("The updater release download metadata is incomplete or invalid.");
        string digest = digestElement.GetString();
        if (digest == null || !Regex.IsMatch(digest, "^sha256:[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("The updater release has no valid SHA-256 digest.");
        return new(version, expectedUrl, digest.Substring(7), size);
    }

    internal static ProcessStartInfo CreateStartInfo(PortableUpdaterTicket ticket, string releaseTag,
        string launchExe, int parentPid, long parentStartUtcTicks)
    {
        if (ticket == null || ticket.Version < MinimumVersion || parentPid <= 0 || parentStartUtcTicks <= 0 ||
            string.IsNullOrWhiteSpace(releaseTag) || releaseTag.Length > 100 ||
            !Regex.IsMatch(releaseTag, @"^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant) ||
            string.IsNullOrWhiteSpace(launchExe) || launchExe != Path.GetFileName(launchExe) ||
            launchExe.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !launchExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The portable update request is invalid.");
        string root = ValidatePackageRoot(ticket.Root);
        if (!string.Equals(ticket.FilePath, Path.Combine(root, "DS4Updater.exe"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The prepared updater does not belong to this portable folder.");
        var info = new ProcessStartInfo(ticket.FilePath) { UseShellExecute = false, WorkingDirectory = root };
        foreach (string value in new[] { "--portable-safe-v1", "--parentPid", parentPid.ToString(CultureInfo.InvariantCulture),
                     "--parentStartUtcTicks", parentStartUtcTicks.ToString(CultureInfo.InvariantCulture),
                     "--releaseTag", releaseTag, "--launchExe", launchExe }) info.ArgumentList.Add(value);
        return info;
    }

    internal static bool Launch(PortableUpdaterTicket ticket, string releaseTag, string launchExe,
        Func<ProcessStartInfo, bool> start = null)
    {
        using var parent = Process.GetCurrentProcess();
        var info = CreateStartInfo(ticket, releaseTag, launchExe, parent.Id, parent.StartTime.ToUniversalTime().Ticks);
        // Deny writes/replacement between the final hash check and CreateProcess.
        using var image = new FileStream(ticket.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (image.Length != ticket.Size || !Convert.ToHexString(SHA256.HashData(image)).Equals(ticket.Sha256,
                StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The prepared updater changed before launch.");
        if (start != null) return start(info);
        using var child = Process.Start(info);
        return child != null;
    }

    private static bool Matches(string path, Version version, string sha256, long size) =>
        VerifyImage(path, version, sha256, size, out _);

    internal static bool VerifyImage(string path, Version version, string sha256, long size, out string mismatch)
    {
        PortableLabContext.ValidateNoReparsePoints(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length != size)
        {
            mismatch = $"length mismatch (expected {size}, observed {file.Length})";
            return false;
        }
        if (!Convert.ToHexString(SHA256.HashData(file)).Equals(sha256, StringComparison.OrdinalIgnoreCase))
        {
            mismatch = "SHA-256 mismatch";
            return false;
        }
        // The Win32 version-resource API otherwise silently returns no version
        // for a valid image when the portable folder plus staging name exceeds
        // MAX_PATH. File streams already support that path; use the same exact
        // file through its extended native spelling for the resource lookup.
        string fullPath = Path.GetFullPath(path);
        string nativePath = fullPath.StartsWith(@"\\?\", StringComparison.Ordinal) ? fullPath :
            fullPath.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + fullPath.Substring(2) : @"\\?\" + fullPath;
        string actualText = FileVersionInfo.GetVersionInfo(nativePath).FileVersion;
        if (!Version.TryParse(actualText, out var actual) || NormalizeVersion(actual) != version)
        {
            mismatch = $"version mismatch (expected {version}, observed {actualText ?? "unavailable"})";
            return false;
        }
        mismatch = null;
        return true;
    }

    internal static string ValidatePackageRoot(string directory)
    {
        string root = PortableBrokerContext.ValidateRoot(directory);
        string marker = Path.Combine(root, PortableBrokerContext.MarkerFileName);
        string manifest = Path.Combine(root, ".ds4windows-managed-files.txt");
        PortableLabContext.ValidateNoReparsePoints(marker);
        PortableLabContext.ValidateNoReparsePoints(manifest);
        PortableLabContext.ValidateNoReparsePoints(Path.Combine(root, "DS4Updater.exe"));
        if (!File.Exists(marker) || new FileInfo(marker).Length > 256 ||
            File.ReadAllText(marker).TrimEnd('\r', '\n') != PortableBrokerContext.MarkerText || !File.Exists(manifest))
            throw new InvalidDataException("This folder is not a complete portable package. Download and extract a fresh portable ZIP.");
        return root;
    }

    private readonly record struct DestinationSnapshot(bool Exists, long Size, string Sha256);

    private static DestinationSnapshot CaptureDestination(string path)
    {
        PortableLabContext.ValidateNoReparsePoints(path);
        if (Directory.Exists(path)) throw new IOException("The updater path is a directory, not an owned executable.");
        if (!File.Exists(path)) return default;
        using var image = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (image.Length > MaximumUpdaterBytes) throw new IOException("The existing updater exceeds the supported size and was kept.");
        return new(true, image.Length, Convert.ToHexString(SHA256.HashData(image)));
    }

    private static void ValidateResponseOrigin(HttpResponseMessage response, string expectedUrl, bool metadata)
    {
        Uri uri = response.RequestMessage?.RequestUri;
        if (uri == null || uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443 ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidDataException("The updater response did not come from a trusted HTTPS origin.");
        if (uri.AbsoluteUri.Equals(expectedUrl, StringComparison.Ordinal)) return;
        // The shared client can follow redirects. Release metadata must stay
        // at its exact API URL; binary content may use GitHub's asset CDNs.
        if (!metadata && (uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("github-releases.githubusercontent.com", StringComparison.OrdinalIgnoreCase))) return;
        throw new InvalidDataException("The updater request redirected outside its approved GitHub origin.");
    }

    private static Version NormalizeVersion(Version version) =>
        new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));

    private static async Task CopyBoundedAsync(Stream input, Stream output, long limit, CancellationToken token)
    {
        byte[] buffer = new byte[64 * 1024];
        long total = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            total = checked(total + count);
            if (total > limit) throw new InvalidDataException("The download exceeded its expected size.");
            await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
        }
    }
}
