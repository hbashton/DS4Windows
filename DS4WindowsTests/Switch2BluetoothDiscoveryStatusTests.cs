using DS4Windows.Switch2;
using DS4WinWPF.DS4Forms.ViewModels;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2BluetoothDiscoveryStatusTests
{
    [TestMethod]
    public void RememberedRecoveryNeverClaimsAssociationOrActivationSucceeded()
    {
        Assert.AreEqual("Reconnect selected", Switch2BluetoothDiscoveryPresentation.ActionLabel(true));
        Assert.AreEqual("Associate selected", Switch2BluetoothDiscoveryPresentation.ActionLabel(false));
        StringAssert.Contains(Switch2BluetoothDiscoveryPresentation.DescribeReconnect(
            Switch2BluetoothWindowsAssociationResult.Failed(
                Switch2BluetoothWindowsAssociationFailure.SlotActivationRejected)),
            "slot could not activate");
        StringAssert.Contains(Switch2BluetoothDiscoveryPresentation.DescribeReconnect(
            Switch2BluetoothWindowsAssociationResult.Reconnected()), "association was preserved");
        Assert.AreEqual(default(Switch2BluetoothAssociationStep),
            Switch2BluetoothWindowsAssociationResult.Reconnected().LastCompletedStep);
    }
    [DataTestMethod]
    [DataRow((int)Switch2BluetoothDiscoveryState.Stopped, "is stopped")]
    [DataRow((int)Switch2BluetoothDiscoveryState.Starting, "Starting Bluetooth")]
    [DataRow((int)Switch2BluetoothDiscoveryState.Scanning, "discovery is active")]
    [DataRow((int)Switch2BluetoothDiscoveryState.Unavailable, "usable Bluetooth adapter")]
    [DataRow((int)Switch2BluetoothDiscoveryState.StartFailed, "could not start")]
    [DataRow((int)Switch2BluetoothDiscoveryState.Interrupted, "stopped unexpectedly")]
    [DataRow((int)Switch2BluetoothDiscoveryState.Stopping, "cleaning up")]
    [DataRow((int)Switch2BluetoothDiscoveryState.CleanupFailed, "did not complete safely")]
    public void PresentationDistinguishesEveryDiscoveryState(
        int stateValue, string detail)
    {
        var state = (Switch2BluetoothDiscoveryState)stateValue;
        var status = new Switch2BluetoothDiscoveryStatus(state);
        string description = Switch2BluetoothDiscoveryPresentation.Describe(status, 0);
        StringAssert.Contains(description, detail);
        Assert.AreEqual(state == Switch2BluetoothDiscoveryState.Scanning, status.CanAssociate);
        if (!status.CanAssociate)
            Assert.IsFalse(description.Contains("Hold the controller sync button"),
                "An unavailable scan must not be presented as an empty active scan.");
    }

    [DataTestMethod]
    [DataRow(0, "No new controllers found yet")]
    [DataRow(1, "1 controller available")]
    [DataRow(2, "2 controllers available")]
    public void ActiveScanDescribesItsCandidateCount(int count, string expected)
    {
        StringAssert.Contains(Switch2BluetoothDiscoveryPresentation.Describe(
            new(Switch2BluetoothDiscoveryState.Scanning), count), expected);
    }

    [TestMethod]
    public void FailedStartRetainsTheConcreteFailureReason()
    {
        var status = new Switch2BluetoothDiscoveryStatus(
            Switch2BluetoothDiscoveryState.StartFailed,
            Switch2BluetoothWindowsScanStartFailure.WatcherConfigurationFailed);
        StringAssert.Contains(Switch2BluetoothDiscoveryPresentation.Describe(status, 3),
            "WatcherConfigurationFailed");
        Assert.IsFalse(status.CanAssociate);
    }

    [TestMethod]
    public void UnknownStateDoesNotAdmitAssociation()
    {
        var status = new Switch2BluetoothDiscoveryStatus((Switch2BluetoothDiscoveryState)255);
        Assert.IsFalse(status.CanAssociate);
        StringAssert.Contains(Switch2BluetoothDiscoveryPresentation.Describe(status, 1),
            "status is unavailable");
        StringAssert.Contains(Switch2BluetoothDiscoveryPresentation.Describe(null, 0),
            "status is unavailable");
    }

    [TestMethod]
    public void OldHostLookupCannotOverwriteANewerAttempt()
    {
        var startup = new Switch2BluetoothDiscoveryStartupState();
        Assert.AreEqual(Switch2BluetoothDiscoveryState.Stopped, startup.Snapshot.State);
        var first = startup.Begin();
        var second = startup.Begin();
        Assert.AreNotSame(first, second, "Starting identity must be unique per attempt.");
        Assert.IsFalse(startup.TryComplete(first, Switch2BluetoothDiscoveryState.Unavailable));
        Assert.AreSame(second, startup.Snapshot);
        Assert.IsTrue(startup.TryComplete(second, Switch2BluetoothDiscoveryState.Stopped));
        Assert.IsFalse(startup.TryComplete(second, Switch2BluetoothDiscoveryState.StartFailed));
        Assert.AreEqual(Switch2BluetoothDiscoveryState.Stopped, startup.Snapshot.State);
    }

    [DataTestMethod]
    [DataRow((int)Switch2BluetoothDiscoveryState.Stopping)]
    [DataRow((int)Switch2BluetoothDiscoveryState.Stopped)]
    [DataRow((int)Switch2BluetoothDiscoveryState.CleanupFailed)]
    public void LateHostLookupCannotOverwriteShutdown(int stateValue)
    {
        var state = (Switch2BluetoothDiscoveryState)stateValue;
        var startup = new Switch2BluetoothDiscoveryStartupState();
        var attempt = startup.Begin();
        startup.Set(state);
        Assert.IsFalse(startup.TryComplete(attempt, Switch2BluetoothDiscoveryState.Unavailable));
        Assert.AreEqual(state, startup.Snapshot.State);
    }
}
