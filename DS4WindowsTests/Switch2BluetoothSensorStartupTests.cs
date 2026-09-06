using DS4Windows.Switch2;

namespace DS4WindowsTests;

public sealed partial class Switch2BluetoothWindowsAdapterTests
{
    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task JoyConDuplexInitializesSensorsBeforeInputAndRejectsFailedStartup(
        bool right, bool reject)
    {
        var watcher = new FakeWatcher();
        var platform = new FakePlatform(watcher);
        var device = FakeDevice.ValidDuplexPro();
        device.Service.OutputCharacteristic = FakeCharacteristic.Output(right ?
            Switch2BluetoothHdRumblePhysicalWriter.JoyCon2RightCharacteristicUuid :
            Switch2BluetoothHdRumblePhysicalWriter.JoyCon2LeftCharacteristicUuid);
        var writes = new List<string>();
        device.Service.CommandCharacteristic.WriteOverride = (request, _, _) =>
        {
            Assert.AreEqual(0, device.Service.Characteristic.EnableCalls,
                "Sensor startup must finish before report subscription is published.");
            writes.Add(Convert.ToHexString(request.Span));
            device.Service.ResponseCharacteristic.Emit(new byte[]
                { 0x0C, reject ? (byte)4 : (byte)1, 0, 0, 0, 0, 0, 0 }, 1);
            return ValueTask.FromResult(true);
        };
        platform.EnqueueDevice(device);
        using var identity = new Switch2PersistentPeerIdentityDeriver(
            Enumerable.Range(1, Switch2PersistentPeerId.InstallKeyLength).
                Select(value => (byte)value).ToArray());
        var adapter = CreateAdapter(platform, new Switch2BluetoothCandidateRegistry(),
            identityDeriver: identity);
        var observations = new List<Switch2BluetoothCandidateObservation>();
        Assert.IsTrue(adapter.TryStartScan(1, LocalHost, observations.Add, out _));
        watcher.Emit(0x112233445566, BuildAdvertisement(right ?
            Switch2AdvertisementCodec.JoyCon2RightProductId :
            Switch2AdvertisementCodec.JoyCon2LeftProductId), 1);
        var opened = await adapter.OpenRememberedDuplexAsync(observations[0]);
        if (reject)
        {
            Assert.AreEqual(Switch2BluetoothWindowsOpenFailure.SensorInitializationFailed, opened.Failure);
            Assert.AreEqual(0, device.Service.Characteristic.EnableCalls);
            Assert.IsTrue(device.Disposed);
            Assert.AreEqual(1, writes.Count);
        }
        else
        {
            Assert.IsTrue(opened.Succeeded, opened.Failure.ToString());
            Assert.IsTrue(opened.Lease.JoyConSensorsInitialized);
            CollectionAssert.AreEqual(new[]
                { "0C9101020004000094000000", "0C9101040004000094000000" }, writes);
            await RetireLease(opened.Lease, 99);
        }
        Assert.IsTrue(await adapter.EndScanAsync(1));
    }
}
