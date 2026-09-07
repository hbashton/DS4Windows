using Switch2BluetoothAudioProbe;

namespace DS4WindowsTests;

[TestClass]
public class Switch2BluetoothAudioProbeTests
{
    [TestMethod]
    public void WakePayloadTargetsOnlyTheExactAddressInDocumentedOrder()
    {
        CollectionAssert.AreEqual(Convert.FromHexString("030001008066554433221101"),
            ProbeProtocol.BuildWakeManufacturerValue(0x112233445566));
        CollectionAssert.AreEqual(Convert.FromHexString("030001008001000000000001"),
            ProbeProtocol.BuildWakeManufacturerValue(1));
    }

    [TestMethod]
    public void InvalidOrBroadcastWakeTargetsAreRejected()
    {
        foreach (ulong address in new[] { 0UL, 0xffffffffffffUL, 0x1000000000000UL, ulong.MaxValue })
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => ProbeProtocol.BuildWakeManufacturerValue(address));
    }

    [TestMethod]
    public void HeadphoneOutputIsNotTheRumbleCharacteristic()
    {
        Assert.AreEqual(new Guid("cc483f51-9258-427d-a939-630c31f72b06"), ProbeProtocol.HeadsetOutputUuid);
        Assert.AreNotEqual(DS4Windows.Switch2.Switch2BluetoothHdRumblePhysicalWriter.ProController2CharacteristicUuid,
            ProbeProtocol.HeadsetOutputUuid);
    }

    [TestMethod]
    public void AdvertisementValidationRejectsOtherModelsAndMalformedFrames()
    {
        byte[] value = Convert.FromHexString("0100037E056920000100" + "000000000000" + "0F00000000000000");
        Assert.IsTrue(ProbeProtocol.IsProAdvertisement(value));
        value[9] = 0x81;
        Assert.IsTrue(ProbeProtocol.IsProAdvertisement(value));
        value[10] = 0x12; // Remembered-host bytes are opaque, never output.
        Assert.IsTrue(ProbeProtocol.IsProAdvertisement(value));
        for (int length = 0; length < 24; length++)
            Assert.IsFalse(ProbeProtocol.IsProAdvertisement(value.AsSpan(0, length)));
        Assert.IsFalse(ProbeProtocol.IsProAdvertisement(new byte[25]));
        foreach (int index in new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 16, 17, 23 })
        {
            byte prior = value[index];
            value[index] ^= 0xff;
            Assert.IsFalse(ProbeProtocol.IsProAdvertisement(value), $"Field {index}");
            value[index] = prior;
        }
    }
}
