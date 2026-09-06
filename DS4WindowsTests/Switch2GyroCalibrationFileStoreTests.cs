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
