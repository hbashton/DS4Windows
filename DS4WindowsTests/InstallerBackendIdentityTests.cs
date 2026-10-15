using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using DS4Windows;

namespace DS4WindowsTests;

[TestClass]
public class InstallerBackendIdentityTests
{
    [TestMethod]
    public void BootstrapperIdentityMatchesTheActualBundledBackend()
    {
        string binary = Path.Combine(Root(), "extras", ViiperSetupManager.BundledViiperName);
        using var stream = File.OpenRead(binary);
        string digest = Convert.ToHexString(SHA256.HashData(stream));
        string probe = Read("installer/DS4Windows.Bootstrapper/InfrastructureProbe.cs");
        Assert.AreEqual(digest, StringValue(probe, "ExpectedViiperHash"));
        Assert.AreEqual(digest, ViiperSetupManager.SupportedViiperSha256);
        Assert.AreEqual(FileVersionInfo.GetVersionInfo(binary).ProductVersion,
            StringValue(probe, "ExpectedViiperVersion"));
    }

    [TestMethod]
    public void InfrastructureDetectionUsesTheSamePackageMarkerAtEveryBoundary()
    {
        string marker = $"VIIPER-{ViiperSetupManager.SupportedViiperReleaseTag.TrimStart('v')}+USBIP-{ViiperSetupManager.RequiredUsbipVersion}";
        Assert.AreEqual(marker, StringValue(Read("installer/DS4Windows.Bootstrapper/InfrastructureProbe.cs"), "ExpectedMarker"));
        Assert.AreEqual(marker, StringValue(Read("installer/DS4Windows.SetupActions/Program.cs"), "InfrastructureVersion"));
        Assert.AreEqual(marker, StringValue(Read("extras/install-viiper-backend.ps1"), "$script:InfrastructureVersion"));
        StringAssert.Contains(Read("installer/DS4Windows.Bundle/Bundle.wxs"),
            $"InfrastructureInstalled = &quot;{marker}&quot;");
    }

    private static string StringValue(string source, string name)
    {
        Match match = Regex.Match(source, Regex.Escape(name) + "\\s*=\\s*\"([^\"]+)\"");
        Assert.IsTrue(match.Success, $"Missing package identity: {name}");
        return match.Groups[1].Value;
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(Root(), file));
    private static string Root([CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, ".."));
}
