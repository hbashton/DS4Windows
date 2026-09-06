using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public class Switch2BluetoothAssociationCodecTests
{
    private static readonly byte[] HostAddress =
        Convert.FromHexString("FFEEDDCCBBAA");

    [TestMethod]
    public void FourAssociationRequestsMatchProvenDonorVectors()
    {
        var vectors = new[]
        {
            (Switch2BluetoothAssociationStep.SetHostAddress,
                "15910101000E00000002AABBCCDDEEFFAABBCCDDEEFF"),
            (Switch2BluetoothAssociationStep.WriteLongTermKeyPart1,
                "159101040011000000EABD4713893542C679EE07F2532C6C31"),
            (Switch2BluetoothAssociationStep.WriteLongTermKeyPart2,
                "15910102001100000040B08A5FCD1F9B41125CACC63F38A073"),
            (Switch2BluetoothAssociationStep.Commit,
                "159101030001000000"),
        };

        foreach ((Switch2BluetoothAssociationStep step, string hex) in
                 vectors)
        {
            byte[] expected = Convert.FromHexString(hex);
            Assert.IsTrue(Switch2BluetoothAssociationCodec.
                TryGetRequestLength(step, out int length));
            Assert.AreEqual(expected.Length, length);
            var actual = Enumerable.Repeat((byte)0xCC, length).ToArray();
            Assert.IsTrue(Switch2BluetoothAssociationCodec.TryWriteRequest(
                step, HostAddress, actual, out var failure));
            Assert.AreEqual(Switch2BluetoothAssociationCodecFailure.None,
                failure);
            CollectionAssert.AreEqual(expected, actual, step.ToString());
        }
    }

    [DataTestMethod]
    [DataRow("112233445566", "665544332211")]
    [DataRow("FFEEDDCCBBAA", "AABBCCDDEEFF")]
    public void CanonicalHostUsesLittleEndianWireOrderAndMatchesReconnect(
        string canonicalHex, string wireHex)
    {
        // Switch2Connect utils.py parses the displayed MAC as a hex integer;
        // controller.py pair() serializes that integer as six little-endian
        // bytes, twice. These are synthetic addresses, not captured identities.
        byte[] host = Convert.FromHexString(canonicalHex);
        byte[] expectedWire = Convert.FromHexString(wireHex);
        byte[] request = new byte[Switch2BluetoothAssociationCodec.SetHostAddressRequestLength];
        Assert.IsTrue(Switch2BluetoothAssociationCodec.TryWriteRequest(
            Switch2BluetoothAssociationStep.SetHostAddress, host, request, out _));
        CollectionAssert.AreEqual(expectedWire, request[10..16]);
        CollectionAssert.AreEqual(expectedWire, request[16..22]);

        // A controller advertising the host bytes it was given must classify
        // as this host, not silently become a foreign-host reconnect.
        byte[] advertisement = Convert.FromHexString(
            "0100037E0569200001000000000000000F00000000000000");
        request.AsSpan(10, 6).CopyTo(advertisement.AsSpan(10, 6));
        Assert.IsTrue(Switch2AdvertisementCodec.TryDecode(
            Switch2AdvertisementCodec.NintendoBluetoothCompanyId,
            advertisement, host, out var decoded));
        Assert.AreEqual(Switch2AdvertisedHost.ThisHost, decoded.Host);
        Assert.IsTrue(decoded.IsReconnect);
        CollectionAssert.AreEqual(Convert.FromHexString(canonicalHex), host,
            "Encoding must not reverse the caller's canonical address in place.");
    }

    [TestMethod]
    public void InvalidStepHostOrLengthCannotPartiallyWrite()
    {
        var untouched = Enumerable.Repeat((byte)0xCC,
            Switch2BluetoothAssociationCodec.MaximumRequestLength).ToArray();
        byte[] snapshot = (byte[])untouched.Clone();

        Assert.IsFalse(Switch2BluetoothAssociationCodec.TryWriteRequest(
            (Switch2BluetoothAssociationStep)0x99, HostAddress, untouched,
            out var failure));
        Assert.AreEqual(Switch2BluetoothAssociationCodecFailure.InvalidStep,
            failure);
        CollectionAssert.AreEqual(snapshot, untouched);

        foreach (byte[] invalidHost in new[]
                 {
                     Array.Empty<byte>(),
                     new byte[5],
                     new byte[6],
                     Enumerable.Repeat((byte)0xFF, 6).ToArray(),
                 })
        {
            Assert.IsFalse(Switch2BluetoothAssociationCodec.TryWriteRequest(
                Switch2BluetoothAssociationStep.WriteLongTermKeyPart1,
                invalidHost, untouched, out failure));
            Assert.AreEqual(
                Switch2BluetoothAssociationCodecFailure.InvalidHostAddress,
                failure);
            CollectionAssert.AreEqual(snapshot, untouched);
        }

        Assert.IsFalse(Switch2BluetoothAssociationCodec.TryWriteRequest(
            Switch2BluetoothAssociationStep.Commit, HostAddress, untouched,
            out failure));
        Assert.AreEqual(Switch2BluetoothAssociationCodecFailure.InvalidLength,
            failure);
        CollectionAssert.AreEqual(snapshot, untouched);
    }

    [TestMethod]
    public void ResponseValidationUsesOnlyEstablishedCommandAndStatusFacts()
    {
        byte[] minimum = Convert.FromHexString("1501A5A5A5A5A5A5");
        Assert.IsTrue(Switch2BluetoothAssociationCodec.TryValidateResponse(
            minimum, out var failure));
        Assert.AreEqual(Switch2BluetoothAssociationCodecFailure.None,
            failure);

        byte[] withPayload = minimum.Concat(new byte[] { 1, 2, 3 }).ToArray();
        Assert.IsTrue(Switch2BluetoothAssociationCodec.TryValidateResponse(
            withPayload, out failure));

        Assert.IsFalse(Switch2BluetoothAssociationCodec.TryValidateResponse(
            minimum.AsSpan(0, 7), out failure));
        Assert.AreEqual(Switch2BluetoothAssociationCodecFailure.InvalidLength,
            failure);

        minimum[0] = 0x14;
        Assert.IsFalse(Switch2BluetoothAssociationCodec.TryValidateResponse(
            minimum, out failure));
        Assert.AreEqual(Switch2BluetoothAssociationCodecFailure.InvalidCommand,
            failure);
        minimum[0] = 0x15;
        minimum[1] = 0;
        Assert.IsFalse(Switch2BluetoothAssociationCodec.TryValidateResponse(
            minimum, out failure));
        Assert.AreEqual(Switch2BluetoothAssociationCodecFailure.InvalidStatus,
            failure);
    }

    [TestMethod]
    public void UuidsAndClosedStepDomainAreExact()
    {
        Assert.AreEqual(new Guid(
                "ab7de9be-89fe-49ad-828f-118f09df7fd0"),
            Switch2BluetoothAssociationCodec.ServiceUuid);
        Assert.AreEqual(new Guid(
                "649d4ac9-8eb7-4e6c-af44-1ea54fe5f005"),
            Switch2BluetoothAssociationCodec.CommandWriteCharacteristicUuid);
        Assert.AreEqual(new Guid(
                "c765a961-d9d8-4d36-a20a-5315b111836a"),
            Switch2BluetoothAssociationCodec.
                CommandResponseCharacteristicUuid);
        CollectionAssert.AreEqual(new byte[] { 1, 4, 2, 3 },
            new[]
            {
                Switch2BluetoothAssociationStep.SetHostAddress,
                Switch2BluetoothAssociationStep.WriteLongTermKeyPart1,
                Switch2BluetoothAssociationStep.WriteLongTermKeyPart2,
                Switch2BluetoothAssociationStep.Commit,
            }.Select(value => (byte)value).ToArray());
    }
}
