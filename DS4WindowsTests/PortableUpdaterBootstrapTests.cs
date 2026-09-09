using DS4Windows;
using System.IO;
using System.Text.Json;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace DS4WindowsTests;

[TestClass]
public sealed class PortableUpdaterBootstrapTests
{
    [DataTestMethod]
    [DataRow("v2.0.4")]
    [DataRow("v2.0.3")]
    public void OldUnsafeUpdaterIsNeverAccepted(string tag)
    {
        using var json = Release(tag);
        Assert.ThrowsException<InvalidOperationException>(() => PortableUpdaterBootstrap.ReadVerifiedAsset(json.RootElement));
    }

    [TestMethod]
    public void PublishedFixedUpdaterRequiresExactReleaseDigestSizeAndUrl()
    {
        using var json = Release();
        var asset = PortableUpdaterBootstrap.ReadVerifiedAsset(json.RootElement);
        Assert.AreEqual(PortableUpdaterBootstrap.MinimumVersion, asset.Version);
        Assert.AreEqual(new string('a', 64), asset.Sha256);
        Assert.AreEqual(72L, asset.Size);
    }

    [DataTestMethod]
    [DataRow("draft")]
    [DataRow("prerelease")]
    [DataRow("digest")]
    [DataRow("host")]
    [DataRow("wrongtag")]
    [DataRow("size")]
    [DataRow("duplicate")]
    public void InvalidOrAmbiguousReleaseCannotPrepareAnUpdater(string fault)
    {
        using var json = Release(fault: fault);
        Assert.ThrowsException<InvalidDataException>(() => PortableUpdaterBootstrap.ReadVerifiedAsset(json.RootElement));
    }

    [DataTestMethod]
    [DataRow("..\\other.exe")]
    [DataRow("C:\\Windows\\other.exe")]
    [DataRow("other.exe:stream")]
    public void LaunchArgumentsRejectForeignExecutablesBeforeAnyProcessStart(string executable)
    {
        var ticket = new PortableUpdaterTicket("unused", "unused", new(2, 0, 5, 0), new string('a', 64), 1);
        Assert.ThrowsException<ArgumentException>(() => PortableUpdaterBootstrap.CreateStartInfo(ticket,
            "VIIPERRC4.5.1", executable, 123, 456));
    }

    [TestMethod]
    public void LaunchWithoutVerifiedPreparationIsRejected()
    {
        Assert.ThrowsException<ArgumentException>(() => PortableUpdaterBootstrap.CreateStartInfo(null,
            "VIIPERRC4.5.1", "DS4Windows.exe", 123, 456));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ConcurrentDestinationCreationOrChangeIsPreserved(bool existed)
    {
        using var f = new DownloadFixture();
        if (existed) File.WriteAllText(f.Updater, "old updater");
        f.BeforeDownload = () => File.WriteAllText(f.Updater, "concurrent unrelated replacement");
        await Assert.ThrowsExceptionAsync<IOException>(() => PortableUpdaterBootstrap.PrepareAsync(f.Client, f.Root));
        Assert.AreEqual("concurrent unrelated replacement", File.ReadAllText(f.Updater));
        Assert.AreEqual(0, Directory.GetFiles(f.Root, ".DS4Updater-download-*").Length);
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task UnexpectedFinalResponseOriginNeverReplacesUpdater(bool metadata)
    {
        using var f = new DownloadFixture();
        File.WriteAllText(f.Updater, "keep existing updater");
        f.RedirectMetadata = metadata;
        f.RedirectDownload = !metadata;
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => PortableUpdaterBootstrap.PrepareAsync(f.Client, f.Root));
        Assert.AreEqual("keep existing updater", File.ReadAllText(f.Updater));
        Assert.AreEqual(metadata ? 1 : 2, f.RequestCount);
        Assert.AreEqual(0, Directory.GetFiles(f.Root, ".DS4Updater-download-*").Length);
    }

    [TestMethod]
    public async Task HashFailureLeavesOriginalUpdaterUntouched()
    {
        using var f = new DownloadFixture();
        File.WriteAllText(f.Updater, "keep existing updater");
        f.CorruptDownload = true;
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => PortableUpdaterBootstrap.PrepareAsync(f.Client, f.Root));
        Assert.AreEqual("keep existing updater", File.ReadAllText(f.Updater));
        Assert.AreEqual(0, Directory.GetFiles(f.Root, ".DS4Updater-download-*").Length);
    }

    [TestMethod]
    public async Task ValidDownloadIsVerifiedThenLaunchPinsBytesAndUsesOnlySafeArguments()
    {
        using var f = new DownloadFixture();
        var ticket = await PortableUpdaterBootstrap.PrepareAsync(f.Client, f.Root);
        CollectionAssert.AreEqual(f.Image, File.ReadAllBytes(f.Updater));
        bool called = false;
        Assert.IsFalse(PortableUpdaterBootstrap.Launch(ticket, "VIIPERRC4.5.1", "DS4Windows.exe", info =>
        {
            called = true;
            Assert.IsFalse(info.UseShellExecute);
            Assert.AreEqual(f.Root, info.WorkingDirectory);
            Assert.AreEqual("--portable-safe-v1", info.ArgumentList[0]);
            CollectionAssert.AreEqual(new[] { "--parentPid", "--parentStartUtcTicks", "--releaseTag", "--launchExe" },
                info.ArgumentList.Skip(1).Where((_, index) => index % 2 == 0).ToArray());
            Assert.ThrowsException<IOException>(() => File.WriteAllText(f.Updater, "tamper while pinned"));
            return false; // No child process is actually started in this test.
        }));
        Assert.IsTrue(called);
        CollectionAssert.AreEqual(f.Image, File.ReadAllBytes(f.Updater));
    }

    [TestMethod]
    public void LongPortablePathKeepsVersionIdentityAndReportsSpecificVerificationFailures()
    {
        using var f = new DownloadFixture();
        string folder = Path.Combine(f.Root, new string('p', 80));
        Directory.CreateDirectory(folder);
        string image = Path.Combine(folder, ".DS4Updater-download-" + Guid.NewGuid().ToString("N") + ".exe");
        Assert.IsTrue(image.Length > 260);
        File.WriteAllBytes(image, f.Image);
        string hash = Convert.ToHexString(SHA256.HashData(f.Image));
        var version = Version.Parse(FileVersionInfo.GetVersionInfo(Path.Combine(AppContext.BaseDirectory, "DS4Windows.exe")).FileVersion!);
        Assert.IsTrue(PortableUpdaterBootstrap.VerifyImage(image, version, hash, f.Image.LongLength, out var failure), failure);
        Assert.IsFalse(PortableUpdaterBootstrap.VerifyImage(image, version, hash, f.Image.LongLength + 1, out failure));
        StringAssert.Contains(failure, "length mismatch");
        Assert.IsFalse(PortableUpdaterBootstrap.VerifyImage(image, version, new string('0', 64), f.Image.LongLength, out failure));
        Assert.AreEqual("SHA-256 mismatch", failure);
        Assert.IsFalse(PortableUpdaterBootstrap.VerifyImage(image, new Version(999, 0, 0, 0), hash, f.Image.LongLength, out failure));
        StringAssert.Contains(failure, "version mismatch");
        StringAssert.Contains(failure, "observed " + version);
    }

    [TestMethod]
    public void MarkerWhitespaceChangeDoesNotBypassPortableContextIdentity()
    {
        using var f = new DownloadFixture();
        File.WriteAllText(Path.Combine(f.Root, PortableBrokerContext.MarkerFileName), " " + PortableBrokerContext.MarkerText);
        Assert.ThrowsException<InvalidDataException>(() => PortableUpdaterBootstrap.ValidatePackageRoot(f.Root));
    }

    private sealed class DownloadFixture : IDisposable
    {
        internal readonly string Root = Path.Combine(AppContext.BaseDirectory, "updater-bootstrap-" + Guid.NewGuid().ToString("N"));
        internal string Updater => Path.Combine(Root, "DS4Updater.exe");
        // A real version-resource-bearing file, never executed as a process.
        private static readonly string SourceImage = Path.Combine(AppContext.BaseDirectory, "DS4Windows.exe");
        internal readonly byte[] Image = File.ReadAllBytes(SourceImage);
        private readonly string imageVersion = FileVersionInfo.GetVersionInfo(SourceImage).FileVersion;
        internal readonly HttpClient Client;
        internal Action BeforeDownload;
        internal bool RedirectMetadata, RedirectDownload, CorruptDownload;
        internal int RequestCount;
        internal DownloadFixture()
        {
            Assert.IsTrue(Version.TryParse(imageVersion, out var version));
            Assert.IsTrue(version >= PortableUpdaterBootstrap.MinimumVersion);
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, PortableBrokerContext.MarkerFileName), PortableBrokerContext.MarkerText);
            File.WriteAllText(Path.Combine(Root, ".ds4windows-managed-files.txt"), "DS4Windows.exe\n");
            Client = new HttpClient(new Handler(this));
        }
        private sealed class Handler(DownloadFixture fixture) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                bool metadata = ++fixture.RequestCount == 1;
                string tag = "v" + fixture.imageVersion;
                string url = $"https://github.com/hbashton/DS4Updater/releases/download/{tag}/DS4Updater.exe";
                HttpContent content;
                if (metadata)
                    content = new StringContent(JsonSerializer.Serialize(new
                    {
                        tag_name = tag, draft = false, prerelease = false,
                        assets = new[] { new { name = "DS4Updater.exe", browser_download_url = url,
                            size = fixture.Image.LongLength, digest = "sha256:" + Convert.ToHexString(SHA256.HashData(fixture.Image)) } }
                    }), Encoding.UTF8, "application/json");
                else
                {
                    fixture.BeforeDownload?.Invoke();
                    byte[] image = (byte[])fixture.Image.Clone();
                    if (fixture.CorruptDownload) image[^1] ^= 1;
                    content = new ByteArrayContent(image);
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = metadata && fixture.RedirectMetadata || !metadata && fixture.RedirectDownload ?
                        new HttpRequestMessage(HttpMethod.Get, "https://untrusted.invalid/download") : request,
                    Content = content
                });
            }
        }
        public void Dispose()
        {
            Client.Dispose();
            if (Path.GetDirectoryName(Root) == Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory) &&
                Path.GetFileName(Root).StartsWith("updater-bootstrap-", StringComparison.Ordinal))
                Directory.Delete(Root, true);
        }
    }

    private static JsonDocument Release(string tag = "v2.0.5", string fault = null)
    {
        var asset = new
        {
            name = "DS4Updater.exe",
            browser_download_url = fault == "host" ? "https://example.com/DS4Updater.exe" :
                $"https://github.com/hbashton/DS4Updater/releases/download/{(fault == "wrongtag" ? "v2.0.4" : tag)}/DS4Updater.exe",
            size = fault == "size" ? -1L : 72L,
            digest = fault == "digest" ? null : "sha256:" + new string('a', 64)
        };
        return JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            tag_name = tag, draft = fault == "draft", prerelease = fault == "prerelease",
            assets = fault == "duplicate" ? new[] { asset, asset } : new[] { asset }
        }));
    }
}
