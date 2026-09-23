using System.Diagnostics;
using System.Text;
using DS4Windows;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public class PortableBrokerProcessTreeTests
{
    // Real, disposable parent/child processes demonstrate the lifecycle defect
    // without starting VIIPER, opening a driver, or touching installed programs.
    [TestMethod]
    public void StoppingOwnedBrokerAlsoStopsItsPendingPrerequisiteChild()
    {
        string root = Path.Combine(Path.GetTempPath(), "ds4-process-tree-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string pidFile = Path.Combine(root, "helper.pid");
        string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        Assert.IsTrue(File.Exists(powershell));
        string childCode = Convert.ToBase64String(Encoding.Unicode.GetBytes("[Threading.Thread]::Sleep(60000)"));
        static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        string parentCode = "$helper = Start-Process -FilePath " + Quote(powershell) +
            " -ArgumentList '-NoProfile','-NonInteractive','-EncodedCommand'," + Quote(childCode) +
            " -PassThru -WindowStyle Hidden; [IO.File]::WriteAllText(" + Quote(pidFile) +
            ",$helper.Id.ToString()); $helper.WaitForExit()";
        var start = new ProcessStartInfo(powershell)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(parentCode)) }) start.ArgumentList.Add(argument);
        IPortableBrokerProcess parent = null;
        Process helper = null;
        try
        {
            parent = new PortableBrokerProcessHost().Start(start);
            var elapsed = Stopwatch.StartNew();
            int helperId = 0;
            while (elapsed.ElapsedMilliseconds < 10_000)
            {
                if (File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile), out helperId)) break;
                Assert.IsTrue(parent.IsRunning, "The test parent exited before creating its helper.");
                Thread.Sleep(20);
            }
            Assert.IsTrue(helperId > 0, "The test helper did not start within its deadline.");
            helper = Process.GetProcessById(helperId);
            _ = helper.Handle; // Keep the exact helper object, never a later reused PID.
            Assert.IsFalse(helper.HasExited);
            parent.StopAndWait(5_000);
            Assert.IsFalse(parent.IsRunning);
            Assert.IsTrue(helper.WaitForExit(5_000),
                "Parent-only termination leaves the prerequisite helper orphaned.");
        }
        finally
        {
            // Cleanup is limited to handles created by this test. Even when
            // demonstrating the old defect, no name-based termination is used.
            try { if (parent?.IsRunning == true) parent.StopAndWait(5_000); }
            finally
            {
                parent?.Dispose();
                if (helper != null)
                {
                    if (!helper.HasExited) { helper.Kill(); helper.WaitForExit(5_000); }
                    helper.Dispose();
                }
                if (File.Exists(pidFile)) File.Delete(pidFile);
                Directory.Delete(root); // Nonrecursive: unexpected files are never removed.
            }
        }
    }
}
