using System.ComponentModel;
using System.Diagnostics;
using DS4Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4WindowsTests;

[TestClass]
public class ViiperProcessRepairTests
{
    private static readonly PortableBrokerProcessIdentity Own = new(10, 1000, @"C:\Portable DS4Windows\viiper.exe");
    private static readonly PortableBrokerProcessIdentity Other = new(20, 2000, @"C:\Other broker\viiper.exe");

    [TestMethod]
    public async Task DirectStopPreservesRequestedImageAndNeverElevatesUnnecessarily()
    {
        var captured = new[] { Own, Other };
        var host = new Host(captured);
        await ViiperProcessRepair.StopCoreAsync(captured, Own.ExecutablePath, false, host,
            (_, _) => throw new AssertFailedException("No elevation needed."));
        CollectionAssert.AreEqual(new[] { 20 }, host.Stopped.ToArray());
        CollectionAssert.AreEqual(new[] { Own }, host.Peers.ToArray());
    }

    [TestMethod]
    public async Task AccessDeniedElevatesOnlyOriginalIdentityAndVerifiesFinalStop()
    {
        var captured = new[] { Other };
        var host = new Host(captured) { DeniedPid = Other.ProcessId };
        int elevated = 0;
        await ViiperProcessRepair.StopCoreAsync(captured, null, false, host, (encoded, preserved) =>
        {
            elevated++;
            Assert.IsNull(preserved);
            PortableBrokerProcessIdentity[] decoded = ViiperManagedRepair.DecodeSnapshot(encoded);
            CollectionAssert.AreEqual(captured, decoded);
            host.DeniedPid = 0;
            PortableBrokerProcessHost.StopCapturedForRepair(decoded, processHost: host);
            return Task.CompletedTask;
        });
        Assert.AreEqual(1, elevated);
        CollectionAssert.AreEqual(new[] { 20 }, host.Stopped.ToArray());
        Assert.AreEqual(0, host.Peers.Count);
    }

    [TestMethod]
    public async Task PartialDirectStopRetainsOriginalSnapshotAcrossElevation()
    {
        var captured = new[] { Own, Other };
        var host = new Host(captured) { DeniedPid = Other.ProcessId };
        await ViiperProcessRepair.StopCoreAsync(captured, null, false, host, (encoded, preserved) =>
        {
            CollectionAssert.AreEqual(new[] { 10 }, host.Stopped.ToArray());
            var original = ViiperManagedRepair.DecodeSnapshot(encoded);
            CollectionAssert.AreEqual(captured, original);
            host.DeniedPid = 0;
            PortableBrokerProcessHost.StopCapturedForRepair(original, preserved, host);
            return Task.CompletedTask;
        });
        CollectionAssert.AreEqual(new[] { 10, 20 }, host.Stopped.ToArray());
    }

    [DataTestMethod]
    [DataRow(true, 5)]
    [DataRow(false, 87)]
    public async Task AdministratorDenialOrNonPermissionFailureDoesNotLaunchAnotherHelper(bool administrator, int code)
    {
        var captured = new[] { Other };
        var host = new Host(captured) { DeniedPid = Other.ProcessId, ErrorCode = code };
        var error = await Assert.ThrowsExceptionAsync<Win32Exception>(() =>
            ViiperProcessRepair.StopCoreAsync(captured, null, administrator, host,
                (_, _) => throw new AssertFailedException("Unexpected elevation.")));
        Assert.AreEqual(code, error.NativeErrorCode);
        Assert.AreEqual(0, host.Stopped.Count);
    }

    [TestMethod]
    public async Task NewcomerAfterAccessDeniedCannotBeAddedToElevationAuthority()
    {
        var captured = new[] { Other };
        var host = new Host(captured) { DeniedPid = Other.ProcessId };
        host.OnDenied = () => host.Peers.Add(Own);
        await Assert.ThrowsExceptionAsync<PortableBrokerStartupException>(() =>
            ViiperProcessRepair.StopCoreAsync(captured, null, false, host,
                (_, _) => throw new AssertFailedException("A changed snapshot must not be elevated.")));
        Assert.AreEqual(0, host.Stopped.Count);
    }

    [TestMethod]
    public async Task ReusedPidAfterAccessDeniedCannotBeAddedToElevationAuthority()
    {
        var captured = new[] { Other };
        var host = new Host(captured) { DeniedPid = Other.ProcessId };
        host.OnDenied = () => host.Peers[0] = Other with { StartTimeUtcTicks = Other.StartTimeUtcTicks + 1 };
        await Assert.ThrowsExceptionAsync<PortableBrokerStartupException>(() =>
            ViiperProcessRepair.StopCoreAsync(captured, null, false, host,
                (_, _) => throw new AssertFailedException("Reused PID is not authorized.")));
        Assert.AreEqual(0, host.Stopped.Count);
    }

    [TestMethod]
    public async Task FalseHelperSuccessCannotResumeWithTargetStillRunning()
    {
        var captured = new[] { Other };
        var host = new Host(captured) { DeniedPid = Other.ProcessId };
        await Assert.ThrowsExceptionAsync<IOException>(() =>
            ViiperProcessRepair.StopCoreAsync(captured, null, false, host, (_, _) => Task.CompletedTask));
        CollectionAssert.AreEqual(captured, host.Peers.ToArray());
    }

    [TestMethod]
    public async Task FailedOrCancelledElevationPropagatesWithoutChangingAuthority()
    {
        var captured = new[] { Other };
        var host = new Host(captured) { DeniedPid = Other.ProcessId };
        await Assert.ThrowsExceptionAsync<IOException>(() =>
            ViiperProcessRepair.StopCoreAsync(captured, null, false, host,
                (_, _) => throw new IOException("simulated UAC cancellation")));
        Assert.AreEqual(0, host.Stopped.Count);
    }

    [TestMethod]
    public async Task NewcomerAfterElevatedStopCannotBeAcceptedAsSuccess()
    {
        var captured = new[] { Other };
        var host = new Host(captured) { DeniedPid = Other.ProcessId };
        await Assert.ThrowsExceptionAsync<PortableBrokerStartupException>(() =>
            ViiperProcessRepair.StopCoreAsync(captured, null, false, host, (_, _) =>
            {
                host.Peers.Clear();
                host.Peers.Add(Own);
                return Task.CompletedTask;
            }));
        CollectionAssert.AreEqual(new[] { Own }, host.Peers.ToArray());
    }

    [TestMethod]
    public async Task EmptyOrPreservedOnlySnapshotDoesNotRequestElevation()
    {
        foreach (var captured in new[] { Array.Empty<PortableBrokerProcessIdentity>(), new[] { Own } })
        {
            var host = new Host(captured);
            await ViiperProcessRepair.StopCoreAsync(captured, Own.ExecutablePath, false, host,
                (_, _) => throw new AssertFailedException("No targets means no UAC."));
            Assert.AreEqual(0, host.Stopped.Count);
        }
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow("relative/viiper.exe")]
    [DataRow(@"C:\Other\not-viiper.exe")]
    [DataRow(@"\\server\share\viiper.exe")]
    public async Task InvalidPreservedPathIsRejectedBeforeProcessInspection(string path)
    {
        var host = new Host(new[] { Own });
        await Assert.ThrowsExceptionAsync<ArgumentException>(() =>
            ViiperProcessRepair.StopCoreAsync(new[] { Own }, path, false, host,
                (_, _) => throw new AssertFailedException()));
        Assert.AreEqual(0, host.Snapshots);
    }

    [TestMethod]
    public void StopHelperDispatchIsStrictAndSeparateFromFileRepair()
    {
        Assert.IsFalse(ViiperProcessRepair.TryRunHelper(new[] { ViiperManagedRepair.HelperArgument }, out _));
        Assert.IsTrue(ViiperProcessRepair.TryRunHelper(new[] { ViiperProcessRepair.HelperArgument }, out int missing));
        Assert.AreNotEqual(0, missing);
        Assert.IsTrue(ViiperProcessRepair.TryRunHelper(new[] { ViiperProcessRepair.HelperArgument, "invalid-base64", "-" }, out int invalid));
        Assert.AreNotEqual(0, invalid);
    }

    private sealed class Host(IEnumerable<PortableBrokerProcessIdentity> initial) : IPortableBrokerProcessHost
    {
        internal readonly List<PortableBrokerProcessIdentity> Peers = initial.ToList();
        internal readonly List<int> Stopped = new();
        internal int DeniedPid, ErrorCode = 5, Snapshots;
        internal Action OnDenied;
        public IReadOnlyList<PortableBrokerProcessIdentity> Snapshot() { Snapshots++; return Peers.ToArray(); }
        public void StopForRepair(PortableBrokerProcessIdentity identity, int timeoutMilliseconds)
        {
            Assert.IsTrue(timeoutMilliseconds is > 0 and <= 5000);
            if (identity.ProcessId == DeniedPid)
            {
                OnDenied?.Invoke();
                throw new Win32Exception(ErrorCode);
            }
            Assert.IsTrue(Peers.Remove(identity));
            Stopped.Add(identity.ProcessId);
        }
        public IReadOnlyList<string> ReadArguments(PortableBrokerProcessIdentity identity) => throw new AssertFailedException("No argument discovery.");
        public IPortableBrokerProcess Start(ProcessStartInfo startInfo) => throw new AssertFailedException("Never start a broker here.");
    }
}
