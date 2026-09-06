using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2BluetoothPlayerLedCodecTests
{
    [TestMethod]
    public void PlayerPatternsAndEnvelopeMatchPinnedDonor()
    {
        byte[] expectedPatterns =
        [
            0x01, 0x03, 0x07, 0x0F, 0x09, 0x05, 0x0D, 0x06,
        ];
        for (byte player = 1; player <= 8; player++)
        {
            byte[] request = new byte[
                Switch2BluetoothPlayerLedCodec.RequestLength];
            Assert.IsTrue(Switch2BluetoothPlayerLedCodec.TryWriteRequest(
                player, request, out var failure));
            Assert.AreEqual(Switch2BluetoothPlayerLedCodecFailure.None,
                failure);
            CollectionAssert.AreEqual(new byte[]
            {
                0x09, 0x91, 0x01, 0x07, 0x00, 0x04, 0x00, 0x00,
                expectedPatterns[player - 1], 0x00, 0x00, 0x00,
            }, request);
            Assert.IsTrue(
                Switch2BluetoothPlayerLedCodec.TryGetPlayerNumber(
                    expectedPatterns[player - 1], out byte decoded));
            Assert.AreEqual(player, decoded);
        }

        for (int candidate = 0; candidate <= byte.MaxValue; candidate++)
        {
            byte pattern = (byte)candidate;
            bool expected = expectedPatterns.Contains(pattern);
            Assert.AreEqual(expected,
                Switch2BluetoothPlayerLedCodec.TryGetPlayerNumber(pattern,
                    out byte decoded));
            if (expected)
            {
                Assert.IsTrue(Switch2BluetoothPlayerLedCodec.TryGetPattern(
                    decoded, out byte roundTrip));
                Assert.AreEqual(pattern, roundTrip);
            }
        }
    }

    [TestMethod]
    public void ExactFourSegmentMaskIsPreservedWithoutApproximation()
    {
        for (byte pattern = 0; pattern <= 0x0F; pattern++)
        {
            byte[] request = new byte[
                Switch2BluetoothPlayerLedCodec.RequestLength];
            Assert.IsTrue(Switch2BluetoothPlayerLedCodec.
                TryWritePatternRequest(pattern, request, out var failure));
            Assert.AreEqual(Switch2BluetoothPlayerLedCodecFailure.None,
                failure);
            Assert.AreEqual(pattern, request[8]);
            CollectionAssert.AreEqual(new byte[]
            {
                0x09, 0x91, 0x01, 0x07, 0x00, 0x04, 0x00, 0x00,
                pattern, 0x00, 0x00, 0x00,
            }, request);
        }

        Assert.IsFalse(Switch2BluetoothPlayerLedCodec.
            TryWritePatternRequest(0x10,
                new byte[Switch2BluetoothPlayerLedCodec.RequestLength],
                out _));
        Assert.IsFalse(Switch2BluetoothPlayerLedCodec.
            TryWritePatternRequest(0xFF,
                new byte[Switch2BluetoothPlayerLedCodec.RequestLength],
                out _));
    }

    [TestMethod]
    public void InvalidPlayerLengthAndResponseFailClosed()
    {
        Assert.IsFalse(Switch2BluetoothPlayerLedCodec.TryWriteRequest(0,
            new byte[Switch2BluetoothPlayerLedCodec.RequestLength], out _));
        Assert.IsFalse(Switch2BluetoothPlayerLedCodec.TryWriteRequest(9,
            new byte[Switch2BluetoothPlayerLedCodec.RequestLength], out _));
        Assert.IsFalse(Switch2BluetoothPlayerLedCodec.TryWriteRequest(1,
            new byte[Switch2BluetoothPlayerLedCodec.RequestLength - 1],
            out _));
        Assert.IsFalse(Switch2BluetoothPlayerLedCodec.TryValidateResponse(
            Convert.FromHexString("09010000000000"), out _));
        Assert.IsFalse(Switch2BluetoothPlayerLedCodec.TryValidateResponse(
            Convert.FromHexString("0A01000000000000"), out _));
        Assert.IsFalse(Switch2BluetoothPlayerLedCodec.TryValidateResponse(
            Convert.FromHexString("0900000000000000"), out _));
        Assert.IsTrue(Switch2BluetoothPlayerLedCodec.TryValidateResponse(
            Convert.FromHexString("0901000000000000"), out _));
    }
}
