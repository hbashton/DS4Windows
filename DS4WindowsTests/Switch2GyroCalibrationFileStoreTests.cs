using System.Numerics;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2GyroCalibrationFileStoreTests
{
    private static readonly byte[] InstallKey = Enumerable.Range(1, 32)
        .Select(value => (byte)value).ToArray();

    [TestMethod]
    public void OrderedBackgroundWritesRoundTripOpaquePeerAndNewestBias()
    {
        string root = NewTemporaryRoot();
        try
        {
            Assert.IsTrue(Switch2GyroCalibrationFileStore.TryOpen(root,
                out var store));
            Switch2PersistentPeerId peer = Peer(17,
                Switch2ControllerModel.ProController2,
                Switch2AdvertisementCodec.ProController2ProductId);
            Assert.IsTrue(Switch2GyroCalibrationRecord.TryCreate(
                new Vector3(0.25f, -0.125f, 0.0625f), out var first));
            Assert.IsTrue(Switch2GyroCalibrationRecord.TryCreate(
                new Vector3(0.5f, -0.25f, 0.125f), out var second));

            Assert.IsTrue(store.TryQueueStore(peer, first));
            Assert.IsTrue(store.TryQueueStore(peer, second));
            Assert.IsTrue(SpinWait.SpinUntil(() =>
                store.TryLoad(peer, out var loaded) && loaded.Equals(second),
                3_000));

            string[] records = Directory.GetFiles(Path.Combine(root,
                "GyroCalibration"), "*.gyro");
            Assert.AreEqual(1, records.Length);
            Assert.AreEqual(49L, new FileInfo(records[0]).Length);
            Assert.IsFalse(File.ReadAllText(records[0]).Contains(
                "ProController", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void ExternalNonSharingReaderRejectsReplacementWithoutDeletingEitherRecord()
    {
        string root = NewTemporaryRoot();
        try
        {
            string path = Path.Combine(root, "current.gyro");
            string temporary = Path.Combine(root, "next.tmp");
            byte[] oldBytes = Enumerable.Repeat((byte)7, 49).ToArray();
            byte[] newBytes = Enumerable.Repeat((byte)8, 49).ToArray();
            File.WriteAllBytes(path, oldBytes);
            File.WriteAllBytes(temporary, newBytes);
            using (FileStream reader = new(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Exception failure = null;
                try { Switch2GyroCalibrationFileStore.CommitCalibrationFile(temporary, path); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { failure = error; }
                Assert.IsNotNull(failure, "An external non-sharing handle must not be bypassed.");
            }
            CollectionAssert.AreEqual(oldBytes, File.ReadAllBytes(path));
            CollectionAssert.AreEqual(newBytes, File.ReadAllBytes(temporary));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(48)]
    [DataRow(50)]
    [DataRow(1_048_576)]
    public void WrongSizedRecordsAreRejectedBeforeDecode(int length)
    {
        string root = NewTemporaryRoot();
        try
        {
            Assert.IsTrue(Switch2GyroCalibrationFileStore.TryOpen(root, out var store));
            Switch2PersistentPeerId peer = Peer(31, Switch2ControllerModel.ProController2,
                Switch2AdvertisementCodec.ProController2ProductId);
            Span<byte> encoded = stackalloc byte[Switch2PersistentPeerId.EncodedLength];
            Assert.IsTrue(peer.TryWrite(encoded));
            string path = Path.Combine(root, "GyroCalibration", Convert.ToHexString(encoded) + ".gyro");
            File.WriteAllBytes(path, new byte[length]);
            Assert.IsFalse(store.TryLoad(peer, out _));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void OpenReaderAllowsDirectAtomicReplacement()
    {
        string root = NewTemporaryRoot();
        try
        {
            string path = Path.Combine(root, "current.gyro");
            string temporary = Path.Combine(root, "next.tmp");
            File.WriteAllBytes(path, new byte[49]);
            File.WriteAllBytes(temporary, new byte[49]);
            using FileStream reader = Switch2GyroCalibrationFileStore.OpenCalibrationRead(path);
            Switch2GyroCalibrationFileStore.CommitCalibrationFile(temporary, path);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void CalibrationReaderDoesNotBlockTheNextAtomicCommit()
    {
        string root = NewTemporaryRoot();
        try
        {
            Assert.IsTrue(Switch2GyroCalibrationFileStore.TryOpen(root, out var store));
            Switch2PersistentPeerId peer = Peer(29, Switch2ControllerModel.ProController2,
                Switch2AdvertisementCodec.ProController2ProductId);
            Assert.IsTrue(Switch2GyroCalibrationRecord.TryCreate(new Vector3(0.1f), out var first));
            Assert.IsTrue(Switch2GyroCalibrationRecord.TryCreate(new Vector3(0.2f), out var second));
            Assert.IsTrue(store.TryQueueStore(peer, first));
            Assert.IsTrue(SpinWait.SpinUntil(() => store.TryLoad(peer, out _), 3_000));
            string path = Directory.GetFiles(Path.Combine(root, "GyroCalibration"), "*.gyro").Single();
            // Hold the exact production read handle across the next queued
            // replacement. Scheduling cannot hide a missing FileShare.Delete.
            using FileStream heldReader = Switch2GyroCalibrationFileStore.OpenCalibrationRead(path);
            byte[] previousGeneration = new byte[49];
            heldReader.ReadExactly(previousGeneration);
            Assert.IsTrue(store.TryQueueStore(peer, second));
            Assert.IsTrue(SpinWait.SpinUntil(() => store.TryLoad(peer, out var loaded) && loaded.Equals(second), 3_000),
                "A calibration reader blocked atomic replacement and exhausted all queued write attempts.");
            heldReader.Position = 0;
            byte[] retainedGeneration = new byte[49];
            heldReader.ReadExactly(retainedGeneration);
            CollectionAssert.AreEqual(previousGeneration, retainedGeneration,
                "Existing readers must retain the old complete record while new readers see the new one.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void CorruptDigestDifferentPeerAndUnsafeBiasAreRejected()
    {
        string root = NewTemporaryRoot();
        try
        {
            Assert.IsTrue(Switch2GyroCalibrationFileStore.TryOpen(root,
                out var store));
            Switch2PersistentPeerId peer = Peer(22,
                Switch2ControllerModel.JoyCon2Left,
                Switch2AdvertisementCodec.JoyCon2LeftProductId);
            Assert.IsFalse(Switch2GyroCalibrationRecord.TryCreate(
                new Vector3(float.NaN, 0.0f, 0.0f), out _));
            Assert.IsFalse(Switch2GyroCalibrationRecord.TryCreate(
                new Vector3(3.0f, 0.0f, 0.0f), out _));
            Assert.IsTrue(Switch2GyroCalibrationRecord.TryCreate(
                new Vector3(0.2f, 0.1f, -0.1f), out var calibration));
            Assert.IsTrue(store.TryQueueStore(peer, calibration));
            string directory = Path.Combine(root, "GyroCalibration");
            Assert.IsTrue(SpinWait.SpinUntil(() =>
                Directory.GetFiles(directory, "*.gyro").Length == 1,
                3_000));
            Assert.IsFalse(store.TryLoad(Peer(23,
                Switch2ControllerModel.JoyCon2Left,
                Switch2AdvertisementCodec.JoyCon2LeftProductId), out _));

            string path = Directory.GetFiles(directory, "*.gyro").Single();
            byte[] bytes = File.ReadAllBytes(path);
            bytes[25] ^= 0x5A;
            File.WriteAllBytes(path, bytes);
            Assert.IsFalse(store.TryLoad(peer, out _));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Switch2PersistentPeerId Peer(byte identityByte,
        Switch2ControllerModel model, ushort productId)
    {
        byte[] identity = Enumerable.Repeat(identityByte, 16).ToArray();
        Assert.IsTrue(Switch2PersistentPeerId.TryDerive(InstallKey, identity,
            model, productId, out var peer));
        return peer;
    }

    private static string NewTemporaryRoot()
    {
        string path = Path.Combine(Path.GetTempPath(), "ds4w-s2-gyro-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
