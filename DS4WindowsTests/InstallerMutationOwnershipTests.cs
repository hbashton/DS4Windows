using System.Runtime.CompilerServices;
using DS4Windows.SetupActions;

namespace DS4WindowsTests;

[TestClass]
public sealed class InstallerMutationOwnershipTests
{
    [TestMethod]
    public void InstallPayloadCleanupRunsInsideInfrastructureOwnership()
    {
        string source = ReadSetupActions();
        string entry = Section(source, "private static int InstallOrRepair(",
            "private static int InstallOrRepairLocked(");
        Assert.IsFalse(entry.Contains("RemoveObsoleteBundledViiperPayloads(", StringComparison.Ordinal),
            "A losing installer must not delete payloads used by the active repair before returning 1618.");
        StringAssert.Contains(entry, "return RunWithSetupMutex(");
        string locked = Section(source, "private static int InstallOrRepairLocked(",
            "private static int Preflight()");
        StringAssert.Contains(locked, "RemoveObsoleteBundledViiperPayloads(");
        StringAssert.Contains(locked, "preserveCurrent: true");
    }

    [TestMethod]
    public void PostUninstallDirectoryCleanupRunsInsideInfrastructureOwnership()
    {
        string source = ReadSetupActions();
        string entry = Section(source, "private static int PostUninstallCleanup(",
            "private static int PostUninstallCleanupLocked(");
        StringAssert.Contains(entry, "return RunWithSetupMutex(() => PostUninstallCleanupLocked(installRoot));");
        Assert.IsFalse(entry.Contains("Directory.Delete(", StringComparison.Ordinal));
        string locked = Section(source, "private static int PostUninstallCleanupLocked(",
            "private static void RemoveObsoleteBundledViiperPayloads(");
        StringAssert.Contains(locked, "EnsureTreeHasNoReparsePoints(installRoot);");
        StringAssert.Contains(locked, "Directory.Delete(directory);");
        StringAssert.Contains(locked, "Directory.Delete(installRoot);");
    }

    [TestMethod]
    public void ProductionInstallRepairAndCleanupShareTheExistingGlobalMutex()
    {
        string source = ReadSetupActions();
        string gate = Section(source, "private static int RunWithSetupMutex(",
            "private static InteractiveUser ResolveInteractiveUser(");
        StringAssert.Contains(gate, "SetupMutationOwnership.Run(action, WriteFallbackLog)");
        Assert.AreEqual(@"Global\DS4Windows-VIIPER-Setup", SetupMutationOwnership.MutexName);
        StringAssert.Contains(source, "return RunWithSetupMutex(PreflightLocked);");
        StringAssert.Contains(source, "return RunWithSetupMutex(() => UninstallLocked(installRoot));");
    }

    [TestMethod]
    public void CompetingOwnerRejectsCleanupWithoutInvokingAnyMutationAndRetrySucceeds()
    {
        // An isolated name never acquires the real installer mutex. Dedicated
        // threads make ownership unambiguous; Mutex is thread-affine/reentrant.
        string name = "Local\\DS4Windows-Setup-Test-" + Guid.NewGuid().ToString("N");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int firstResult = -1;
        Exception firstError = null;
        var first = new Thread(() =>
        {
            try
            {
                firstResult = SetupMutationOwnership.Run(() =>
                {
                    entered.Set();
                    Assert.IsTrue(release.Wait(5000));
                    return 3010;
                }, null, name);
            }
            catch (Exception error) { firstError = error; }
        });
        first.Start();
        try
        {
            Assert.IsTrue(entered.Wait(5000));
            int mutationCalls = 0;
            string log = null;
            int rejected = SetupMutationOwnership.Run(() => ++mutationCalls,
                value => log = value, name);
            Assert.AreEqual(1618, rejected);
            Assert.AreEqual(0, mutationCalls);
            StringAssert.Contains(log, "1618");
        }
        finally
        {
            release.Set();
            Assert.IsTrue(first.Join(5000));
        }
        Assert.IsNull(firstError, firstError?.ToString());
        Assert.AreEqual(3010, firstResult);
        Assert.AreEqual(0, SetupMutationOwnership.Run(() => 0, null, name));
    }

    [TestMethod]
    public void FailingMutationReleasesOwnershipAndDoesNotMasqueradeAsSuccess()
    {
        string name = "Local\\DS4Windows-Setup-Test-" + Guid.NewGuid().ToString("N");
        var expected = new IOException("Synthetic transaction failure");
        Assert.AreSame(expected, Assert.ThrowsException<IOException>(() =>
            SetupMutationOwnership.Run(() => throw expected, null, name)));

        int result = -1;
        var retry = new Thread(() => result = SetupMutationOwnership.Run(() => 0, null, name));
        retry.Start();
        Assert.IsTrue(retry.Join(5000));
        Assert.AreEqual(0, result);
    }

    [TestMethod]
    public void AbandonedOwnerCanBeRecoveredWithoutLeakingTheRecoveryOwnership()
    {
        string name = "Local\\DS4Windows-Setup-Test-" + Guid.NewGuid().ToString("N");
        using var lifetime = new Mutex(false, name);
        var abandon = new Thread(() => lifetime.WaitOne());
        abandon.Start();
        Assert.IsTrue(abandon.Join(5000));
        Assert.AreEqual(0, SetupMutationOwnership.Run(() => 0, null, name));

        int result = -1;
        var next = new Thread(() => result = SetupMutationOwnership.Run(() => 0, null, name));
        next.Start();
        Assert.IsTrue(next.Join(5000));
        Assert.AreEqual(0, result);
    }

    private static string ReadSetupActions([CallerFilePath] string source = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source), "..",
            "installer", "DS4Windows.SetupActions", "Program.cs")));

    private static string Section(string source, string start, string end)
    {
        int begin = source.IndexOf(start, StringComparison.Ordinal);
        Assert.IsTrue(begin >= 0, start);
        int finish = source.IndexOf(end, begin + start.Length, StringComparison.Ordinal);
        Assert.IsTrue(finish > begin, end);
        return source[begin..finish];
    }
}
