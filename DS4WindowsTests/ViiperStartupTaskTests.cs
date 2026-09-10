using System;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4Windows.Tests;

[TestClass]
public class ViiperStartupTaskTests
{
    private const string Canonical = @"C:\Program Files\DS4Windows\VIIPER\viiper.exe";
    private const string Legacy = @"C:\Users\Tester\AppData\Local\VIIPER\viiper.exe";
    private const string Portable = @"C:\Users\Tester\Desktop\RC452\viiper.exe";
    private const string Sid = "S-1-5-21-100-200-300-1001";
    private const string Marker = "DS4Windows managed startup task v1";

    [TestMethod]
    public void RegistrationWritesTheInstallerOwnershipMarkerAndHighPriority()
    {
        var store = new MemoryStore();
        Register(store);
        Assert.AreEqual(Marker, store.Task.Description);
        Assert.AreEqual(ProcessPriorityClass.High, store.Task.Priority);
        Assert.AreEqual(Canonical, store.Task.ExecutablePath);
        Assert.AreEqual(Sid, store.Task.PrincipalSid);
        Assert.IsFalse(store.UpdatedExisting);
        Assert.IsTrue(ViiperStartupTaskPolicy.IsValid(store.Task, Canonical, Sid));
    }

    [TestMethod]
    public void MarkedLowPriorityTaskIsUpdatedWithoutDeletingItsRecoveryState()
    {
        var store = new MemoryStore
        {
            Task = ManagedTask() with
            {
                Arguments = "server",
                Priority = ProcessPriorityClass.BelowNormal,
            },
        };
        Register(store);
        Assert.AreEqual(0, store.Deletes);
        Assert.IsTrue(store.UpdatedExisting);
        Assert.AreEqual(Marker, store.Task.Description);
        Assert.AreEqual(Canonical, store.Task.ExecutablePath);
    }

    [TestMethod]
    public void FailedTaskUpdateLeavesTheExistingRegistrationAvailable()
    {
        var original = ManagedTask() with { Priority = ProcessPriorityClass.BelowNormal };
        var store = new MemoryStore { Task = original, RejectWrite = true };
        Assert.ThrowsException<InvalidOperationException>(() => Register(store));
        Assert.AreSame(original, store.Task);
        Assert.AreEqual(0, store.Deletes);
    }

    [DataTestMethod]
    [DataRow("foreign-description")]
    [DataRow("foreign-account")]
    [DataRow("foreign-command")]
    [DataRow("extra-action")]
    [DataRow("foreign-trigger")]
    [DataRow("unmarked-portable")]
    [DataRow("unmarked-local-appdata")]
    [DataRow("unmarked-canonical")]
    public void ForeignRootTaskSurvivesRegistrationAndRemoval(string conflict)
    {
        var foreign = conflict switch
        {
            "foreign-description" => ManagedTask() with { Description = "Another application" },
            "foreign-account" => ManagedTask() with { PrincipalSid = "S-1-5-21-100-200-300-9999" },
            "foreign-command" => ManagedTask() with { Arguments = "server --foreign-switch" },
            "extra-action" => ManagedTask() with { ActionCount = 2 },
            "foreign-trigger" => ManagedTask() with { AllUsersLogon = false, LogonUserSid = null },
            "unmarked-portable" => ManagedTask() with
            {
                Description = null,
                ExecutablePath = Portable,
                WorkingDirectory = @"C:\Users\Tester\Desktop\RC452",
            },
            "unmarked-local-appdata" => ManagedTask() with
            {
                Description = null,
                ExecutablePath = Legacy,
                WorkingDirectory = @"C:\Users\Tester\AppData\Local\VIIPER",
                Arguments = "server",
                Priority = ProcessPriorityClass.BelowNormal,
            },
            "unmarked-canonical" => ManagedTask() with
            {
                Description = null,
                Arguments = "server",
                Priority = ProcessPriorityClass.BelowNormal,
            },
            _ => throw new InvalidOperationException(),
        };
        var store = new MemoryStore { Task = foreign };
        Assert.ThrowsException<InvalidOperationException>(() => Register(store));
        Assert.ThrowsException<InvalidOperationException>(() =>
            ViiperStartupTaskPolicy.Remove(Sid, store));
        Assert.AreSame(foreign, store.Task);
        Assert.AreEqual(0, store.Deletes);
        Assert.AreEqual(0, store.Writes);
    }

    [TestMethod]
    public void ValidityRequiresTheOwnershipMarkerEvenForTheCorrectExecutable()
    {
        Assert.IsTrue(ViiperStartupTaskPolicy.IsValid(ManagedTask(), Canonical, Sid));
        Assert.IsFalse(ViiperStartupTaskPolicy.IsValid(
            ManagedTask() with { Description = "Another application" }, Canonical, Sid));
        Assert.IsFalse(ViiperStartupTaskPolicy.IsValid(
            ManagedTask() with { Description = null }, Canonical, Sid));
        Assert.IsFalse(ViiperStartupTaskPolicy.IsValid(
            ManagedTask() with { PrincipalSid = null }, Canonical, null));
    }

    [TestMethod]
    public void RegistrationCannotBeRetargetedToAPortableRuntime()
    {
        var store = new MemoryStore { Task = ManagedTask() };
        Assert.ThrowsException<InvalidOperationException>(() =>
            ViiperStartupTaskPolicy.Register(Portable, Sid, Canonical, store));
        Assert.AreEqual(0, store.Deletes);
        Assert.AreEqual(0, store.Writes);
        Assert.AreEqual(Canonical, store.Task.ExecutablePath);
    }

    [TestMethod]
    public void InstalledLaunchKeepsPortableRuntimePreferenceOutOfInstalledStartup()
    {
        string taskPath = null;
        string runtimePreference = null;
        ViiperStartupTaskPolicy.RefreshOnLaunch(false, Canonical,
            () => Portable, _ => true, () => true,
            path => runtimePreference = path,
            path => { taskPath = path; return true; });
        Assert.AreEqual(Canonical, taskPath);
        Assert.AreEqual(Portable, runtimePreference,
            "The in-session backend preference remains independent of installed startup.");
    }

    [TestMethod]
    public void MissingInstalledBackendDoesNotInstallAPortableStartupTask()
    {
        int registrations = 0;
        ViiperStartupTaskPolicy.RefreshOnLaunch(false, Canonical,
            () => Portable, path => path == Portable, () => true, _ => { },
            _ => { registrations++; return true; });
        Assert.AreEqual(0, registrations);
    }

    [TestMethod]
    public void PortableLaunchDoesNotInspectOrChangeInstalledStartup()
    {
        ViiperStartupTaskPolicy.RefreshOnLaunch(true, Canonical,
            () => throw new AssertFailedException("Runtime selection was called."),
            _ => throw new AssertFailedException("File verification was called."),
            () => throw new AssertFailedException("Startup inspection was called."),
            _ => Assert.Fail("Preference was changed."),
            _ => throw new AssertFailedException("Task registration was called."));
    }

    [TestMethod]
    public void DisabledStartupDoesNotRegisterOrRemoveATask()
    {
        ViiperStartupTaskPolicy.RefreshOnLaunch(false, Canonical,
            () => Portable, _ => true, () => false, _ => { },
            _ => throw new AssertFailedException("Task registration was called."));
    }

    [TestMethod]
    public void EnabledInstalledTaskForAnotherBackendUsesVerifiedSessionLaunch()
    {
        var installedTask = ManagedTask();
        bool taskReady = ViiperStartupTaskPolicy.IsValid(installedTask, Portable, Sid);
        Assert.IsFalse(taskReady);
        ProcessStartInfo startInfo = null;
        Assert.IsTrue(ViiperStartupTaskPolicy.TryStartVerifiedServer(
            () => (true, false), () => false,
            () =>
            {
                startInfo = ViiperSetupManager.CreateViiperServerStartInfo(
                    Portable, taskReady, false, @"C:\Windows\System32");
                return true;
            }));
        Assert.AreEqual(Portable, startInfo.FileName);
        Assert.AreEqual("runas", startInfo.Verb);
        Assert.AreEqual(ViiperSetupManager.ViiperServerArguments, startInfo.Arguments);
        Assert.AreEqual(ProcessWindowStyle.Hidden, startInfo.WindowStyle);
        Assert.AreEqual(Canonical, installedTask.ExecutablePath);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void AnotherOrUnverifiableRunningBrokerBlocksSessionFallback(bool selectedAlsoRunning)
    {
        Assert.IsFalse(ViiperStartupTaskPolicy.TryStartVerifiedServer(
            () => (false, selectedAlsoRunning),
            () => throw new AssertFailedException("A conflict cannot be reported as ready."),
            () => throw new AssertFailedException("A second broker must not be started.")));
    }

    [TestMethod]
    public void SelectedRunningBrokerIsProbedWithoutStartingAnother()
    {
        Assert.IsTrue(ViiperStartupTaskPolicy.TryStartVerifiedServer(
            () => (true, true), () => true,
            () => throw new AssertFailedException("The existing selected broker must be reused.")));
    }

    [TestMethod]
    public void MatchingInstalledTaskStillUsesItsElevatedTaskLaunch()
    {
        var startInfo = ViiperSetupManager.CreateViiperServerStartInfo(Canonical,
            ViiperStartupTaskPolicy.IsValid(ManagedTask(), Canonical, Sid),
            false, @"C:\Windows\System32");
        Assert.AreEqual(@"C:\Windows\System32\schtasks.exe", startInfo.FileName);
        StringAssert.Contains(startInfo.Arguments, @"\RunVIIPER");
        Assert.IsFalse(startInfo.UseShellExecute);
    }

    private static ViiperStartupTaskState ManagedTask() =>
        ViiperStartupTaskPolicy.Create(Canonical, Sid) with { Description = Marker };

    private static void Register(MemoryStore store) =>
        ViiperStartupTaskPolicy.Register(Canonical, Sid, Canonical, store);

    private sealed class MemoryStore : IViiperStartupTaskStore
    {
        internal ViiperStartupTaskState Task;
        internal int Deletes;
        internal int Writes;
        internal bool UpdatedExisting;
        internal bool RejectWrite;
        public ViiperStartupTaskState Read() => Task;
        public void Delete() { Deletes++; Task = null; }
        public void Write(ViiperStartupTaskState state, bool updateExisting)
        {
            Writes++;
            if (RejectWrite) throw new InvalidOperationException("Scheduler rejected the update.");
            UpdatedExisting = updateExisting;
            Task = state;
        }
    }
}
