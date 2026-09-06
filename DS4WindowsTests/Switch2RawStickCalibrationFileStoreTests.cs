using System.Buffers.Binary;
using System.Security.Cryptography;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2RawStickCalibrationFileStoreTests
{
    [TestMethod]
    public void ReopenedCanonicalPathSharesColdMutationGate()
    {
        using var f = new Fixture();
        Assert.IsTrue(Switch2RawStickCalibrationFileStore.TryOpen(Path.Combine(f.Root, "."), out var same));
        Assert.IsTrue(Switch2RawStickCalibrationFileStore.TryOpen(f.Root.ToUpperInvariant(), out var caseVariant));
        Assert.AreSame(f.Store.SerializationGate, same.SerializationGate);
        Assert.AreSame(f.Store.SerializationGate, caseVariant.SerializationGate);
        using var other = new Fixture();
        Assert.AreNotSame(f.Store.SerializationGate, other.Store.SerializationGate);
    }

    private const Switch2ControllerModel Pro = Switch2ControllerModel.ProController2;
    private const Switch2StickSide Left = Switch2StickSide.Left, Right = Switch2StickSide.Right;
    private static readonly Switch2StickCalibration Calibration = new(2100, 2000, 1600, 1450, 1800, 1550);

    [TestMethod]
    public void ExactLengthRecordRetainsRawPrecisionPeerModelAndSide()
    {
        var peer = Peer(1);
        byte[] record = Encode(peer);
        Assert.AreEqual(51, record.Length);
        CollectionAssert.AreEqual("S2S1"u8.ToArray(), record[..4]);
        Assert.AreEqual((byte)1, record[4]);
        Assert.AreEqual((byte)Pro, record[21]);
        Assert.AreEqual((byte)Left, record[22]);
        ushort[] fields = { 2100, 2000, 1600, 1450, 1800, 1550 };
        for (int i = 0; i < fields.Length; i++)
            Assert.AreEqual(fields[i], BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(23 + 2 * i)));
        Assert.IsTrue(Switch2RawStickCalibrationFileStore.TryDecode(record, peer, Pro, Left, out var read));
        Assert.AreEqual(Calibration, read);
        Assert.IsFalse(Switch2RawStickCalibrationFileStore.TryDecode(record, Peer(2), Pro, Left, out _));
        Assert.IsFalse(Switch2RawStickCalibrationFileStore.TryDecode(record, peer, Pro, Right, out _));
        Assert.IsFalse(Switch2RawStickCalibrationFileStore.TryDecode(record, peer, Switch2ControllerModel.JoyCon2Left, Left, out _));
        for (int length = 0; length < record.Length; length++)
            Assert.IsFalse(Switch2RawStickCalibrationFileStore.TryDecode(record.AsSpan(0, length), peer, Pro, Left, out _));
        Assert.IsFalse(Switch2RawStickCalibrationFileStore.TryDecode(record.Concat(new byte[] { 0 }).ToArray(), peer, Pro, Left, out _));
    }

    [TestMethod]
    public void EveryCorruptedByteAndValidDigestWithInvalidTravelAreRejected()
    {
        var peer = Peer(1);
        byte[] record = Encode(peer);
        for (int index = 0; index < record.Length; index++)
        {
            record[index] ^= 1;
            Assert.IsFalse(Switch2RawStickCalibrationFileStore.TryDecode(record, peer, Pro, Left, out _), $"Byte {index}");
            record[index] ^= 1;
        }
        foreach ((int index, ushort value) in new[] { (23, (ushort)0), (27, (ushort)255), (31, (ushort)3000), (27, (ushort)4095) })
        {
            record = Encode(peer);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(index), value);
            SHA256.HashData(record.AsSpan(0, 35)).AsSpan(0, 16).CopyTo(record.AsSpan(35));
            Assert.IsFalse(Switch2RawStickCalibrationFileStore.TryDecode(record, peer, Pro, Left, out _));
        }
    }

    [TestMethod]
    public void IndependentSticksRoundTripAndResetCannotRemoveAnotherPeerSideOrModel()
    {
        using var f = new Fixture();
        var peer = Peer(1);
        var other = Peer(2);
        var changed = new Switch2StickCalibration(2051, 2039, 1401, 1399, 1397, 1403);
        Assert.IsTrue(f.Store.TryStore(peer, Pro, Left, Calibration));
        Assert.IsTrue(f.Store.TryStore(peer, Pro, Right, changed));
        Assert.IsTrue(f.Store.TryStore(other, Pro, Left, changed));
        Assert.IsTrue(f.Store.TryLoad(peer, Pro, Left, out var read));
        Assert.AreEqual(Calibration, read);
        Assert.AreEqual(3, Directory.GetFiles(f.Directory, "*.stick").Length);
        Assert.IsTrue(Directory.GetFiles(f.Directory, "*.stick").All(path => new FileInfo(path).Length == 51));
        Assert.AreEqual(0, Directory.GetFiles(f.Directory, "*.tmp").Length);
        Assert.IsTrue(f.Store.TryRemove(peer, Switch2ControllerModel.JoyCon2Left, Left));
        Assert.IsTrue(f.Store.TryLoad(peer, Pro, Left, out _));
        Assert.IsTrue(f.Store.TryRemove(peer, Pro, Left));
        Assert.IsFalse(f.Store.TryLoad(peer, Pro, Left, out _));
        Assert.IsTrue(f.Store.TryLoad(peer, Pro, Right, out read));
        Assert.AreEqual(changed, read);
        Assert.IsTrue(f.Store.TryLoad(other, Pro, Left, out _));
        Assert.IsTrue(f.Store.TryRemove(peer, Pro, Left));
    }

    [TestMethod]
    public void ReopenUsesNewestRecordAndRejectedReplacementPreservesExistingCalibration()
    {
        using var f = new Fixture();
        var peer = Peer(1);
        var changed = new Switch2StickCalibration(2051, 2039, 1401, 1399, 1397, 1403);
        Assert.IsTrue(f.Store.TryStore(peer, Pro, Left, Calibration));
        Assert.IsTrue(f.Store.TryStore(peer, Pro, Left, changed));
        Assert.IsFalse(f.Store.TryStore(peer, Pro, Left, default));
        Assert.IsTrue(Switch2RawStickCalibrationFileStore.TryOpen(f.Root, out var reopened));
        Assert.IsTrue(reopened.TryLoad(peer, Pro, Left, out var read));
        Assert.AreEqual(changed, read);
        Assert.IsFalse(reopened.TryStore(default, Pro, Left, Calibration));
        Assert.IsFalse(reopened.TryStore(peer, Switch2ControllerModel.JoyCon2Left, Right, Calibration));
        Assert.AreEqual(1, Directory.GetFiles(f.Directory, "*.stick").Length);
    }

    [TestMethod]
    public void WrongSizeOnDiskAndWrongSideRenamedFileCannotBeAdopted()
    {
        using var f = new Fixture();
        var peer = Peer(1);
        Assert.IsTrue(f.Store.TryStore(peer, Pro, Left, Calibration));
        string left = Directory.GetFiles(f.Directory, "*.stick").Single();
        Assert.IsTrue(f.Store.TryStore(peer, Pro, Right, Calibration));
        string right = Directory.GetFiles(f.Directory, "*.stick").Single(path => path != left);
        File.Copy(left, right, overwrite: true);
        Assert.IsFalse(f.Store.TryLoad(peer, Pro, Right, out _));
        File.WriteAllBytes(left, new byte[4096]);
        Assert.IsFalse(f.Store.TryLoad(peer, Pro, Left, out _));
    }

    private static byte[] Encode(Switch2PersistentPeerId peer)
    {
        byte[] record = new byte[Switch2RawStickCalibrationFileStore.RecordLength];
        Assert.IsTrue(Switch2RawStickCalibrationFileStore.TryEncode(peer, Pro, Left, Calibration, record));
        return record;
    }

    private static Switch2PersistentPeerId Peer(byte id)
    {
        Assert.IsTrue(Switch2PersistentPeerId.TryDerive(Enumerable.Repeat((byte)1, 32).ToArray(),
            new byte[] { id }, Pro, Switch2AdvertisementCodec.ProController2ProductId, out var peer));
        return peer;
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "DS4Windows-StickCalibration-" + Guid.NewGuid().ToString("N"));
        internal string Directory => Path.Combine(Root, "StickCalibration");
        internal readonly Switch2RawStickCalibrationFileStore Store;
        internal Fixture() => Assert.IsTrue(Switch2RawStickCalibrationFileStore.TryOpen(Root, out Store));
        public void Dispose() => System.IO.Directory.Delete(Root, recursive: true);
    }
}
