using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2BluetoothMemoryReadCodecTests
{
    [TestMethod]
    public void FactoryStickReadMatchesDonorWireShape()
    {
        var request = new byte[Switch2BluetoothMemoryReadCodec.RequestLength];

        Assert.IsTrue(Switch2BluetoothMemoryReadCodec.TryWriteRequest(9,
            Switch2CalibrationCodec.PrimaryFactoryStickAddress, request,
            out Switch2BluetoothMemoryReadCodecFailure failure));

        Assert.AreEqual(Switch2BluetoothMemoryReadCodecFailure.None, failure);
        CollectionAssert.AreEqual(Convert.FromHexString(
            "0291010400080000097E0000A8300100"), request);
    }

    [TestMethod]
    public void ResponseRequiresExactLengthAndAddressEcho()
    {
        byte[] response = Convert.FromHexString(
            "020100000000000009000000A8300100A10B2C3D4E5F607182");
        var payload = new byte[9];

        Assert.IsTrue(Switch2BluetoothMemoryReadCodec.TryCopyResponsePayload(
            response, 9, Switch2CalibrationCodec.PrimaryFactoryStickAddress,
            payload, out Switch2BluetoothMemoryReadCodecFailure failure));
        Assert.AreEqual(Switch2BluetoothMemoryReadCodecFailure.None, failure);
        CollectionAssert.AreEqual(Convert.FromHexString("A10B2C3D4E5F607182"),
            payload);

        Assert.IsFalse(Switch2BluetoothMemoryReadCodec.TryCopyResponsePayload(
            response, 9, Switch2CalibrationCodec.SecondaryFactoryStickAddress,
            payload, out failure));
        Assert.AreEqual(Switch2BluetoothMemoryReadCodecFailure.
            MismatchedAddress, failure);
    }
}
