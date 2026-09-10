using DS4WinWPF;
using DS4WinWPF.DS4Forms.ViewModels;
using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public class StartupRegistrationTests
{
    [DataTestMethod]
    [DataRow(false, false, false, false, false)]
    [DataRow(true, true, true, true, false)]
    [DataRow(true, true, true, false, true)]
    [DataRow(true, false, true, false, false)]
    [DataRow(true, true, false, false, false)]
    [DataRow(true, false, false, false, false)]
    public void AutomaticRepairNeverEnablesDisabledOrForeignTasks(
        bool exists, bool enabled, bool owned, bool exact, bool expected)
    {
        Assert.AreEqual(expected, StartupRegistrationPolicy.ShouldRepairTask(
            exists, enabled, owned, exact));
    }

    [TestMethod]
    public void ExplicitDisableRemovesAnOwnedReadOnlyShortcut()
    {
        WithShortcut(path =>
        {
            File.SetAttributes(path, FileAttributes.ReadOnly);

            StartupRegistrationPolicy.RemoveShortcut(path, _ => true);

            Assert.IsFalse(File.Exists(path),
                "Disabling startup must not silently leave an active shortcut.");
        });
    }

    [TestMethod]
    public void ExplicitDisableDoesNotDeleteAForeignShortcut()
    {
        WithShortcut(path =>
        {
            Assert.ThrowsException<InvalidOperationException>(() =>
                StartupRegistrationPolicy.RemoveShortcut(path, _ => false));
            Assert.IsTrue(File.Exists(path));
        });
    }

    [DataTestMethod]
    [DataRow("DS4Windows managed startup task v1", true, true, false, true)]
    [DataRow("DS4Windows managed startup task v1", false, true, true, false)]
    [DataRow("DS4Windows managed startup task v1", true, false, true, false)]
    [DataRow("", true, true, true, true)]
    [DataRow("", true, true, false, false)]
    [DataRow("", false, true, true, false)]
    [DataRow("Another program", true, true, true, false)]
    public void TaskOwnershipRequiresCurrentUserAndRecognizedRegistration(
        string description, bool currentUser, bool contract, bool legacyProduct, bool expected)
    {
        Assert.AreEqual(expected, StartupRegistrationPolicy.OwnsTask(
            description, currentUser, contract, legacyProduct));
    }

    [DataTestMethod]
    [DataRow(true, "S-1-5-21-123", "S-1-5-21-123", true)]
    [DataRow(false, "S-1-5-21-123", "S-1-5-21-123", false)]
    [DataRow(true, "S-1-5-21-123", "S-1-5-21-456", false)]
    [DataRow(true, "", "", false)]
    [DataRow(true, null, null, false)]
    public void ElevatedHelperRequiresTheSameNamedAccount(
        bool elevated, string currentSid, string requestedSid, bool expected)
    {
        Assert.AreEqual(expected, StartupRegistrationPolicy.AuthorizesTaskHelper(
            elevated, currentSid, requestedSid));
    }

    [TestMethod]
    public void OpeningSettingsOnlyReadsStartupState()
    {
        var access = new FakeStartupAccess { State = new(false, true) };
        SettingsViewModel view = access.CreateView();

        Assert.IsTrue(view.RunAtStartup);
        Assert.IsTrue(view.RunStartTask);
        Assert.AreEqual(0, access.Changes);
        Assert.AreEqual(0, access.Refreshes);
    }

    [TestMethod]
    public void SilentRemovalFailureCannotLookDisabled()
    {
        var access = new FakeStartupAccess { State = new(true, false), IgnoreWrites = true };
        SettingsViewModel view = access.CreateView();

        view.RunAtStartup = false;

        Assert.IsTrue(view.RunAtStartup);
        Assert.AreEqual(1, access.Errors.Count);
        Assert.AreEqual(0, access.Refreshes);
        Assert.IsTrue(access.CreateView().RunAtStartup);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void DeniedOrCanceledTaskRemovalKeepsTheVerifiedSetting(bool canceled)
    {
        var access = new FakeStartupAccess
        {
            State = new(false, true),
            WriteFailure = canceled ? new Win32Exception(1223) : new UnauthorizedAccessException("denied"),
        };
        SettingsViewModel view = access.CreateView();

        view.RunAtStartup = false;

        Assert.IsTrue(view.RunAtStartup);
        Assert.IsTrue(view.RunStartTask);
        Assert.AreEqual(1, access.Errors.Count);
        Assert.AreEqual(0, access.Refreshes);
    }

    [TestMethod]
    public void SuccessfulDisableStaysOffWhenSettingsAreReopened()
    {
        var access = new FakeStartupAccess { State = new(true, true) };
        SettingsViewModel view = access.CreateView();

        view.RunAtStartup = false;

        Assert.IsFalse(view.RunAtStartup);
        Assert.IsFalse(access.CreateView().RunAtStartup);
        Assert.AreEqual(1, access.Changes);
        Assert.AreEqual(1, access.Refreshes);
        Assert.AreEqual(0, access.Errors.Count);
    }

    [TestMethod]
    public void RadioUncheckAndChangeNotificationsDoNotWriteStartupEntries()
    {
        var access = new FakeStartupAccess { State = new(true, false) };
        SettingsViewModel view = access.CreateView();
        view.RunStartProg = false; // WPF unchecks the old radio before selecting the new one.
        Assert.AreEqual(0, access.Changes);
        view.RunAtStartupChanged += (_, _) => view.RunAtStartup = false;

        view.RunStartTask = true;

        Assert.IsTrue(view.RunAtStartup);
        Assert.IsTrue(view.RunStartTask);
        Assert.IsFalse(view.RunStartProg);
        Assert.AreEqual(1, access.Changes);
        Assert.AreEqual(1, access.Refreshes);
    }

    [TestMethod]
    public void FailedReinspectionRetainsLastKnownStateAndReportsFailure()
    {
        var access = new FakeStartupAccess { State = new(false, true), FailReadAfterWrite = true };
        SettingsViewModel view = access.CreateView();

        view.RunAtStartup = false;

        Assert.IsTrue(view.RunAtStartup);
        Assert.AreEqual(1, access.Errors.Count);
        Assert.AreEqual(0, access.Refreshes);
    }

    [TestMethod]
    public void FailedBackendRefreshDoesNotUndoVerifiedApplicationDisable()
    {
        var access = new FakeStartupAccess { State = new(false, true), FailRefresh = true };
        SettingsViewModel view = access.CreateView();

        view.RunAtStartup = false;

        Assert.IsFalse(view.RunAtStartup);
        Assert.AreEqual(1, access.Errors.Count);
        Assert.AreEqual(1, access.Refreshes);
    }

    [TestMethod]
    public void RejectedDisableRefreshesTheBoundCheckboxWithoutOpeningAWindow()
    {
        Exception failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var access = new FakeStartupAccess { State = new(true, false), IgnoreWrites = true };
                SettingsViewModel view = access.CreateView();
                var checkbox = new CheckBox();
                BindingOperations.SetBinding(checkbox, ToggleButton.IsCheckedProperty,
                    new Binding(nameof(SettingsViewModel.RunAtStartup))
                    {
                        Source = view,
                        Mode = BindingMode.TwoWay,
                        UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
                    });
                Assert.AreEqual(true, checkbox.IsChecked);

                checkbox.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

                Assert.IsTrue(view.RunAtStartup);
                Assert.AreEqual(true, checkbox.IsChecked,
                    "A rejected removal must not leave the visible checkbox unchecked.");
                Assert.AreEqual(1, access.Changes);
                BindingOperations.ClearAllBindings(checkbox);
            }
            catch (Exception error) { failure = error; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(10)), "Isolated binding check timed out.");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [TestMethod]
    public void PassiveViiperStartupRefreshCannotRemoveAPendingDisabledTask()
    {
        // Source-bound wiring check only: do not construct TaskService or
        // inspect/change the machine's startup entries in this test process.
        string body = ReadViiperStartupMethod(
            "public static void RefreshSelectedStartupTaskOnLaunch()",
            "public static void RefreshSelectedStartupTaskAfterRunAtStartupChange()");
        Assert.IsFalse(body.Contains("RemoveViiperStartupTask", StringComparison.Ordinal));
        Assert.IsFalse(body.Contains("DeleteViiperStartupTask", StringComparison.Ordinal));
        StringAssert.Contains(body, "ViiperStartupTaskPolicy.RefreshOnLaunch(");
        DS4Windows.ViiperStartupTaskPolicy.RefreshOnLaunch(false,
            @"C:\Program Files\DS4Windows\VIIPER\viiper.exe",
            () => @"C:\Program Files\DS4Windows\VIIPER\viiper.exe",
            _ => true, () => false, _ => { },
            _ => throw new AssertFailedException(
                "Passive repair must stop before changing a task when startup is not enabled."));
    }

    [TestMethod]
    public void ExplicitViiperStartupDisableChecksRemovalAndSurfacesFailure()
    {
        string body = ReadViiperStartupMethod(
            "public static void RefreshSelectedStartupTaskAfterRunAtStartupChange()",
            "public static bool LaunchInstaller(");
        StringAssert.Matches(body, new Regex(
            @"if\s*\(!DS4WinWPF\.StartupMethods\.IsRunAtStartupEnabled\(\)\)\s*\{\s*" +
            @"if\s*\(!RemoveViiperStartupTask\(requestElevation:\s*true\)\)\s*" +
            @"throw new IOException\("));
        StringAssert.Contains(body, "RefreshSelectedStartupTaskOnLaunch();");
    }

    private static string ReadViiperStartupMethod(string signature, string nextSignature)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory != null; directory = directory.Parent)
        {
            string path = Path.Combine(directory.FullName, "DS4Windows", "DS4Control",
                "Viiper", "ViiperSetupManager.cs");
            if (!File.Exists(path)) continue;
            string source = File.ReadAllText(path);
            int start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, "The startup entry point was not found.");
            int end = source.IndexOf(nextSignature, start + signature.Length, StringComparison.Ordinal);
            Assert.IsTrue(end > start, "The next method boundary was not found.");
            // Ignore explanatory comments without flattening statements.
            return Regex.Replace(source[start..end], @"//[^\r\n]*", string.Empty);
        }
        throw new AssertFailedException("The VIIPER startup source was not found.");
    }

    private sealed class FakeStartupAccess
    {
        internal StartupRegistrationState State;
        internal bool IgnoreWrites;
        internal bool FailReadAfterWrite;
        internal bool FailRefresh;
        internal Exception WriteFailure;
        internal int Changes;
        internal int Refreshes;
        internal List<string> Errors = new();

        internal SettingsViewModel CreateView() => new(Read, Change, Refresh, Errors.Add);

        private StartupRegistrationState Read()
        {
            if (FailReadAfterWrite && Changes > 0) throw new IOException("inspection failed");
            return State;
        }

        private void Change(StartupRegistrationMode mode)
        {
            Changes++;
            if (WriteFailure != null) throw WriteFailure;
            if (!IgnoreWrites)
                State = new(mode == StartupRegistrationMode.Program, mode == StartupRegistrationMode.Task);
        }

        private void Refresh()
        {
            Refreshes++;
            if (FailRefresh) throw new IOException("backend refresh failed");
        }
    }

    private static void WithShortcut(Action<string> action)
    {
        string directory = Path.Combine(Path.GetTempPath(),
            "ds4w-startup-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "isolated-test.lnk");
        File.WriteAllText(path, "No actual Windows shortcut or startup entry.");
        try { action(path); }
        finally
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
            Directory.Delete(directory);
        }
    }
}
