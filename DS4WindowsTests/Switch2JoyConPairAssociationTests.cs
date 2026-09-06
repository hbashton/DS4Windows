using System.Reflection;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2JoyConPairAssociationTests
{
    private static readonly byte[] InstallKey = Enumerable.Range(1, 32)
        .Select(value => (byte)value).ToArray();
    private static readonly byte[] OtherInstallKey = Enumerable.Range(33, 32)
        .Select(value => (byte)value).ToArray();
    private static readonly byte[] LeftOsIdentity = Enumerable.Range(80, 32)
        .Select(value => (byte)value).ToArray();
    private static readonly byte[] RightOsIdentity = Enumerable.Range(120, 32)
        .Select(value => (byte)value).ToArray();

    [TestMethod]
    public void PersistentPeerPseudonymIsStableAndDomainSeparated()
    {
        Switch2PersistentPeerId left = Peer(InstallKey, LeftOsIdentity,
            Switch2ControllerModel.JoyCon2Left);
        Assert.AreEqual(left, Peer(InstallKey, LeftOsIdentity,
            Switch2ControllerModel.JoyCon2Left));
        Assert.AreNotEqual(left, Peer(OtherInstallKey, LeftOsIdentity,
            Switch2ControllerModel.JoyCon2Left));
        Assert.AreNotEqual(left, Peer(InstallKey, RightOsIdentity,
            Switch2ControllerModel.JoyCon2Left));
        Assert.AreNotEqual(left, Peer(InstallKey, LeftOsIdentity,
            Switch2ControllerModel.JoyCon2Right));

        Assert.IsFalse(Switch2PersistentPeerId.TryDerive(new byte[32],
            LeftOsIdentity, Switch2ControllerModel.JoyCon2Left,
            Switch2AdvertisementCodec.JoyCon2LeftProductId, out _));
        Assert.IsFalse(Switch2PersistentPeerId.TryDerive(InstallKey,
            ReadOnlySpan<byte>.Empty,
            Switch2ControllerModel.JoyCon2Left,
            Switch2AdvertisementCodec.JoyCon2LeftProductId, out _));
        Assert.IsTrue(Switch2PersistentPeerId.TryDerive(InstallKey,
            LeftOsIdentity, Switch2ControllerModel.ProController2,
            Switch2AdvertisementCodec.ProController2ProductId,
            out Switch2PersistentPeerId pro));
        Assert.AreNotEqual(left, pro,
            "Controller model remains part of the pseudonym domain.");
        Assert.IsFalse(Switch2PersistentPeerId.TryDerive(InstallKey,
            LeftOsIdentity, Switch2ControllerModel.JoyCon2Left,
            Switch2AdvertisementCodec.JoyCon2RightProductId, out _));
        Assert.IsFalse(Switch2PersistentPeerId.TryDerive(InstallKey,
            new byte[Switch2PersistentPeerId.MaximumOsIdentityLength + 1],
            Switch2ControllerModel.JoyCon2Left,
            Switch2AdvertisementCodec.JoyCon2LeftProductId, out _));
    }

    [TestMethod]
    public void PairRecordRoundTripRetainsOnlyOpaqueIdentifiers()
    {
        Switch2PersistentPeerId left = Peer(InstallKey, LeftOsIdentity,
            Switch2ControllerModel.JoyCon2Left);
        Switch2PersistentPeerId right = Peer(InstallKey, RightOsIdentity,
            Switch2ControllerModel.JoyCon2Right);
        Switch2JoyConPairId pairId = PairId(1);
        Assert.IsTrue(Switch2JoyConPairRecord.TryCreate(7, pairId, left,
            right, out var record));
        var encoded = new byte[Switch2JoyConPairRecord.EncodedLength];
        Assert.IsTrue(record.TryWrite(encoded));
        Assert.IsTrue(Switch2JoyConPairRecord.TryRead(encoded,
            out var decoded));
        Assert.AreEqual(record.PairId, decoded.PairId);
        Assert.AreEqual(record.Revision, decoded.Revision);
        Assert.AreEqual(record.LeftPeerId, decoded.LeftPeerId);
        Assert.AreEqual(record.RightPeerId, decoded.RightPeerId);
        Assert.IsFalse(ContainsSequence(encoded, LeftOsIdentity));
        Assert.IsFalse(ContainsSequence(encoded, RightOsIdentity));
        Assert.IsFalse(ContainsSequence(encoded, InstallKey));

        Assert.IsFalse(Switch2JoyConPairRecord.TryCreate(0, pairId, left,
            right, out _));
        Assert.IsFalse(Switch2JoyConPairRecord.TryCreate(8, pairId, left,
            left, out _));
        encoded[0] = byte.MaxValue;
        Assert.IsFalse(Switch2JoyConPairRecord.TryRead(encoded, out _));
    }

    [TestMethod]
    public void PersistentEncodingsAreCanonicalExactLengthAndFailClosed()
    {
        Switch2PersistentPeerId left = Peer(InstallKey, LeftOsIdentity,
            Switch2ControllerModel.JoyCon2Left);
        Switch2PersistentPeerId right = Peer(InstallKey, RightOsIdentity,
            Switch2ControllerModel.JoyCon2Right);
        Switch2JoyConPairId pairId = PairId(3);
        Assert.IsTrue(Switch2JoyConPairRecord.TryCreate(11, pairId, left,
            right, out var record));

        var peerExact = new byte[Switch2PersistentPeerId.EncodedLength];
        Assert.IsTrue(left.TryWrite(peerExact));
        Assert.IsTrue(Switch2PersistentPeerId.TryRead(peerExact, out _));
        Assert.IsFalse(left.TryWrite(new byte[peerExact.Length - 1]));
        Assert.IsFalse(left.TryWrite(new byte[peerExact.Length + 1]));
        Assert.IsFalse(Switch2PersistentPeerId.TryRead(
            peerExact.AsSpan(0, peerExact.Length - 1), out _));
        Assert.IsFalse(Switch2PersistentPeerId.TryRead(
            new byte[peerExact.Length + 1], out _));
        Assert.IsFalse(Switch2PersistentPeerId.TryRead(
            new byte[peerExact.Length], out _));

        var pairExact = new byte[Switch2JoyConPairId.EncodedLength];
        Assert.IsTrue(pairId.TryWrite(pairExact));
        Assert.IsTrue(Switch2JoyConPairId.TryRead(pairExact, out _));
        Assert.IsFalse(pairId.TryWrite(new byte[pairExact.Length - 1]));
        Assert.IsFalse(pairId.TryWrite(new byte[pairExact.Length + 1]));
        Assert.IsFalse(Switch2JoyConPairId.TryRead(
            new byte[pairExact.Length + 1], out _));
        Assert.IsFalse(Switch2JoyConPairId.TryRead(
            new byte[pairExact.Length], out _));

        var recordExact = new byte[Switch2JoyConPairRecord.EncodedLength];
        Assert.IsTrue(record.TryWrite(recordExact));
        Assert.IsFalse(record.TryWrite(new byte[recordExact.Length - 1]));
        var oversized = Enumerable.Repeat((byte)0xa5,
            recordExact.Length + 1).ToArray();
        Assert.IsFalse(record.TryWrite(oversized));
        Assert.IsTrue(oversized.All(value => value == 0xa5),
            "A rejected destination must remain untouched.");
        Assert.IsFalse(Switch2JoyConPairRecord.TryRead(
            recordExact.AsSpan(0, recordExact.Length - 1), out _));
        Assert.IsFalse(Switch2JoyConPairRecord.TryRead(
            new byte[recordExact.Length + 1], out _));

        var zeroRevision = recordExact.ToArray();
        zeroRevision.AsSpan(1, sizeof(ulong)).Clear();
        Assert.IsFalse(Switch2JoyConPairRecord.TryRead(zeroRevision, out _));
        var duplicatePeer = recordExact.ToArray();
        const int firstPeerOffset = 1 + sizeof(ulong) +
            Switch2JoyConPairId.EncodedLength;
        duplicatePeer.AsSpan(firstPeerOffset,
                Switch2PersistentPeerId.EncodedLength)
            .CopyTo(duplicatePeer.AsSpan(firstPeerOffset +
                Switch2PersistentPeerId.EncodedLength));
        Assert.IsFalse(Switch2JoyConPairRecord.TryRead(duplicatePeer,
            out _));
    }

    [TestMethod]
    public void ExplicitPairAdmissionIsSideScanRevisionAndSingleUseFenced()
    {
        Switch2PersistentPeerId leftPeer = Peer(InstallKey, LeftOsIdentity,
            Switch2ControllerModel.JoyCon2Left);
        Switch2PersistentPeerId rightPeer = Peer(InstallKey, RightOsIdentity,
            Switch2ControllerModel.JoyCon2Right);
        Assert.IsTrue(Switch2JoyConPairRecord.TryCreate(9, PairId(2),
            leftPeer, rightPeer, out var record));
        Switch2BluetoothConnectionAdmission left = Admission(
            Switch2ControllerModel.JoyCon2Left, 40);
        Switch2BluetoothConnectionAdmission right = Admission(
            Switch2ControllerModel.JoyCon2Right, 40);

        Assert.IsFalse(Switch2JoyConPairConnectionAdmission.TryCreate(record,
            rightPeer, left, leftPeer, right, out _),
            "Swapped persistent peers cannot select a pair.");
        Assert.IsFalse(Switch2JoyConPairConnectionAdmission.TryCreate(record,
            leftPeer, left, rightPeer, Admission(
                Switch2ControllerModel.JoyCon2Right, 41), out _),
            "Admissions from distinct scans cannot be composed.");
        Assert.IsFalse(Switch2JoyConPairConnectionAdmission.TryCreate(record,
            leftPeer, Admission(Switch2ControllerModel.JoyCon2Right, 40),
            rightPeer, right, out _),
            "L/L or reversed model roles cannot be composed.");

        Assert.IsTrue(Switch2JoyConPairConnectionAdmission.TryCreate(record,
            leftPeer, left, rightPeer, right, out var pair));
        Assert.IsTrue(pair.IsValid);
        Assert.AreEqual(record.PairId, pair.PairId);
        Assert.AreEqual(9UL, pair.PairRecordRevision);
        Assert.AreEqual(40UL, pair.ScanGeneration);
        Assert.IsTrue(pair.TryConsume(out var consumedLeft,
            out var consumedRight));
        Assert.AreEqual(left, consumedLeft);
        Assert.AreEqual(right, consumedRight);
        Assert.IsFalse(pair.TryConsume(out _, out _));

        Type pairType = typeof(Switch2JoyConPairConnectionAdmission);
        Assert.IsFalse(pairType.GetFields(BindingFlags.Instance |
                BindingFlags.Public | BindingFlags.NonPublic)
            .Any(field => field.FieldType == typeof(
                Switch2PersistentPeerId)),
            "Runtime admission must not retain stable peer identifiers.");
    }

    [TestMethod]
    public void CopiedPairAdmissionHasExactlyOneConcurrentConsumer()
    {
        Switch2PersistentPeerId leftPeer = Peer(InstallKey, LeftOsIdentity,
            Switch2ControllerModel.JoyCon2Left);
        Switch2PersistentPeerId rightPeer = Peer(InstallKey, RightOsIdentity,
            Switch2ControllerModel.JoyCon2Right);
        Assert.IsTrue(Switch2JoyConPairRecord.TryCreate(13, PairId(4),
            leftPeer, rightPeer, out var record));
        Switch2BluetoothConnectionAdmission left = Admission(
            Switch2ControllerModel.JoyCon2Left, 70);
        Switch2BluetoothConnectionAdmission right = Admission(
            Switch2ControllerModel.JoyCon2Right, 70);
        Assert.IsTrue(Switch2JoyConPairConnectionAdmission.TryCreate(record,
            leftPeer, left, rightPeer, right, out var pair));

        int winners = 0;
        Parallel.For(0, 128, _ =>
        {
            Switch2JoyConPairConnectionAdmission copy = pair;
            if (copy.TryConsume(out var consumedLeft,
                    out var consumedRight))
            {
                Assert.AreEqual(left, consumedLeft);
                Assert.AreEqual(right, consumedRight);
                Interlocked.Increment(ref winners);
            }
        });

        Assert.AreEqual(1, winners);
        Assert.IsFalse(left.TryConsume());
        Assert.IsFalse(right.TryConsume());
    }

    [TestMethod]
    public void LostPhysicalHalfSpendsPairAuthorityWithoutConsumingOtherHalf()
    {
        Switch2PersistentPeerId leftPeer = Peer(InstallKey, LeftOsIdentity,
            Switch2ControllerModel.JoyCon2Left);
        Switch2PersistentPeerId rightPeer = Peer(InstallKey, RightOsIdentity,
            Switch2ControllerModel.JoyCon2Right);
        Assert.IsTrue(Switch2JoyConPairRecord.TryCreate(14, PairId(5),
            leftPeer, rightPeer, out var record));
        Switch2BluetoothConnectionAdmission left = Admission(
            Switch2ControllerModel.JoyCon2Left, 71);
        Switch2BluetoothConnectionAdmission right = Admission(
            Switch2ControllerModel.JoyCon2Right, 71);
        Assert.IsTrue(Switch2JoyConPairConnectionAdmission.TryCreate(record,
            leftPeer, left, rightPeer, right, out var pair));

        Assert.IsTrue(left.TryConsume());
        Assert.IsFalse(pair.TryConsume(out _, out _));
        Assert.IsTrue(right.TryConsume(),
            "A failed pair operation cannot consume its remaining half.");
        Assert.IsFalse(pair.TryConsume(out _, out _),
            "The failed one-shot pair authority cannot be replayed.");
    }

    private static Switch2PersistentPeerId Peer(byte[] key, byte[] identity,
        Switch2ControllerModel model)
    {
        ushort productId = model == Switch2ControllerModel.JoyCon2Left ?
            Switch2AdvertisementCodec.JoyCon2LeftProductId :
            Switch2AdvertisementCodec.JoyCon2RightProductId;
        Assert.IsTrue(Switch2PersistentPeerId.TryDerive(key, identity, model,
            productId, out var peerId));
        return peerId;
    }

    private static Switch2JoyConPairId PairId(byte seed)
    {
        var encoded = new byte[Switch2JoyConPairId.EncodedLength];
        encoded[0] = seed;
        Assert.IsTrue(Switch2JoyConPairId.TryRead(encoded, out var pairId));
        return pairId;
    }

    private static Switch2BluetoothConnectionAdmission Admission(
        Switch2ControllerModel model, ulong scanGeneration)
    {
        ushort productId = model switch
        {
            Switch2ControllerModel.JoyCon2Left =>
                Switch2AdvertisementCodec.JoyCon2LeftProductId,
            Switch2ControllerModel.JoyCon2Right =>
                Switch2AdvertisementCodec.JoyCon2RightProductId,
            _ => Switch2AdvertisementCodec.ProController2ProductId,
        };
        return new Switch2BluetoothConnectionAdmission(scanGeneration,
            model, productId);
    }

    private static bool ContainsSequence(ReadOnlySpan<byte> haystack,
        ReadOnlySpan<byte> needle)
    {
        if (needle.IsEmpty || needle.Length > haystack.Length)
        {
            return false;
        }
        for (int index = 0; index <= haystack.Length - needle.Length;
            index++)
        {
            if (haystack.Slice(index, needle.Length).SequenceEqual(needle))
            {
                return true;
            }
        }
        return false;
    }
}
