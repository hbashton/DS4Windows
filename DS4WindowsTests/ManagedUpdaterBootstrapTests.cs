using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using DS4Windows;

namespace DS4WindowsTests;

[TestClass]
public sealed class ManagedUpdaterBootstrapTests
{
    private string root;

    [TestInitialize]
    public void CreateFixture()
    {
        root = Path.Combine(Path.GetTempPath(), "DS4W-managed-updater-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
    }

    [TestCleanup]
    public void RemoveFixture()
    {
        if (Path.GetDirectoryName(root) == Path.TrimEndingDirectorySeparator(Path.GetTempPath()) &&
            Path.GetFileName(root).StartsWith("DS4W-managed-updater-", StringComparison.Ordinal))
            Directory.Delete(root, true);
    }

    private ManagedUpdaterTicket Ticket()
    {
        string cache = Path.Combine(root, "cache");
        Directory.CreateDirectory(cache);
        string executable = Path.Combine(cache, "DS4Updater.exe");
        // Arbitrary fixture bytes: these tests inject the launch boundary and
        // never execute them. Download/PE validation has separate real-image tests.
        File.WriteAllText(executable, "verified fixture bytes only");
        byte[] bytes = File.ReadAllBytes(executable);
        return new(Path.Combine(root, "installed"), new(cache, executable,
            ManagedUpdaterBootstrap.MinimumVersion, Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length));
    }

    [TestMethod]
    public void CachedUpdaterUsesExplicitInstalledHandoffAndNeverElevatesItsOwnCopy()
    {
        var ticket = Ticket();
        ProcessStartInfo info = ManagedUpdaterBootstrap.CreateStartInfo(ticket, "VIIPERRC4.6.5", "Game.Pad.exe", path => path);
        Assert.AreEqual(ticket.Image.FilePath, info.FileName);
        Assert.AreEqual(ticket.Image.Root, info.WorkingDirectory);
        Assert.IsFalse(info.UseShellExecute);
        Assert.AreEqual(string.Empty, info.Verb);
        CollectionAssert.AreEqual(new[] { "--managed-safe-v1", "--targetDirectory", ticket.InstallRoot,
            "--releaseTag", "VIIPERRC4.6.5", "--launchExe", "Game.Pad.exe" }, info.ArgumentList.ToArray());
    }

    [TestMethod]
    public void InstalledHandoffRequiresNewUpdaterAndUnchangedRegistration()
    {
        var ticket = Ticket();
        Assert.ThrowsException<ArgumentException>(() => ManagedUpdaterBootstrap.CreateStartInfo(
            ticket with { Image = ticket.Image with { Version = new(2, 0, 7, 0) } }, "VIIPERRC4.6.5", "DS4Windows.exe", path => path));
        Assert.ThrowsException<InvalidDataException>(() => ManagedUpdaterBootstrap.CreateStartInfo(
            ticket, "VIIPERRC4.6.5", "DS4Windows.exe", path => null));
        Assert.ThrowsException<InvalidDataException>(() => ManagedUpdaterBootstrap.CreateStartInfo(
            ticket, "VIIPERRC4.6.5", "DS4Windows.exe", path => path + "-other"));
    }

    [DataTestMethod]
    [DataRow("..\\foreign.exe")]
    [DataRow("C:\\foreign.exe")]
    [DataRow("foreign.exe:stream")]
    public void UnsafeApplicationNameNeverLaunchesUpdater(string name) =>
        Assert.ThrowsException<ArgumentException>(() => ManagedUpdaterBootstrap.CreateStartInfo(
            Ticket(), "VIIPERRC4.6.5", name, path => path));

    [TestMethod]
    public void HandoffPinsVerifiedImageAndPropagatesLaunchFailure()
    {
        var ticket = Ticket();
        bool called = false;
        Assert.IsFalse(ManagedUpdaterBootstrap.Launch(ticket, "VIIPERRC4.6.5", "DS4Windows.exe", info =>
        {
            called = true;
            Assert.ThrowsException<IOException>(() => File.WriteAllText(info.FileName, "changed"));
            return false;
        }, path => path));
        Assert.IsTrue(called);
        File.WriteAllText(ticket.Image.FilePath, "tampered");
        Assert.ThrowsException<InvalidDataException>(() => ManagedUpdaterBootstrap.Launch(ticket,
            "VIIPERRC4.6.5", "DS4Windows.exe", _ => throw new AssertFailedException("Must not launch changed bytes"), path => path));
    }
}
