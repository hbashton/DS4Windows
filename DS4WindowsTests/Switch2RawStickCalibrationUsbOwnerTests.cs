using DS4Windows.Switch2;
using DS4WindowsTests;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4Windows.Tests;

public partial class Switch2ProUsbRuntimeOwnerTests
{
    [TestMethod]
    public void RawStickCalibrationTraversesUsbOwnerAndExistingXboxAxisEncoding()
    {
        FakeLease lease = new() { CompleteSynchronously = false, MaximumSuccessfulBegins = 1 };
        CreateOwner(lease, out var owner, out var registration);
        var peerSource = new Switch2RawStickCalibrationCollectorTests.Fixture();
        var store = Switch2RawStickCalibrationBindingTests.StoreFor(peerSource);
        Assert.IsTrue(owner.RuntimeInputDevice.TryBindRawStickCalibrationPersistence(store, peerSource.Peer));
        owner.RuntimeInputDevice.Report += (_, _) => { };
        try
        {
            Assert.IsTrue(owner.TryActivate(registration, out _));
            var frame = CreateProFrame(DeviceGeneration, TransportGeneration, counter: 11,
                buttons: (uint)Switch2ProButton.FaceWest);
            Assert.IsTrue(owner.TryPublish(frame));
            var state = owner.RuntimeInputDevice.getCurrentStateRef();
            Assert.IsTrue(state.Square);
            Assert.AreEqual((ushort)2048, state.Switch2RawInputStatus.LeftStickXRaw);
            Assert.AreEqual((short)-947, state.LXAxis.ToSigned16(),
                "The raw source is 52 counts below the local center with 1800 counts of negative travel.");
            var xbox = XboxOneEgressState.FromLegacyMappedState(state, -1);
            Assert.AreEqual((short)-947, xbox.LeftStickX);
            Assert.IsFalse(owner.RuntimeInputDevice.TryBindRawStickCalibrationPersistence(store, peerSource.Peer),
                "Startup adoption cannot silently replace a live binding.");
        }
        finally
        {
            Assert.IsTrue(registration.TryStopAndQuiesce(1000, out _));
            Assert.IsTrue(registration.TryRemove(out _));
        }
    }
}
