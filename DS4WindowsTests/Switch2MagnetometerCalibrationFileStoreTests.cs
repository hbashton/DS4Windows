using System.Numerics;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2MagnetometerCalibrationFileStoreTests
{
    private static readonly byte[] InstallKey = Enumerable.Range(1, 32)
        .Select(value => (byte)value).ToArray();

    [TestMethod]
    public void FixedRecordRoundTripsOnlyOpaquePeerAndValidatedTransform()
    {
        string root = NewTemporaryRoot();
        try
        {
            Assert.IsTrue(Switch2MagnetometerCalibrationFileStore.TryOpen(
                root, out var store));
            Switch2PersistentPeerId peer = Peer(17,
                Switch2ControllerModel.ProController2,
                Switch2AdvertisementCodec.ProController2ProductId);
            Switch2MagnetometerCalibration expected = Calibration();

            Assert.IsTrue(store.TryStore(peer, expected));
            Assert.IsTrue(store.TryLoad(peer, out var actual));
            Assert.AreEqual(expected, actual);
            string[] records = Directory.GetFiles(Path.Combine(root,
                "Magnetometer"), "*.mag");
            Assert.AreEqual(1, records.Length);
            Assert.AreEqual(90L, new FileInfo(records[0]).Length);
            Assert.IsFalse(File.ReadAllText(records[0]).Contains(
                "ProController", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void DigestCorruptionAndDifferentPeerAreRejected()
    {
        string root = NewTemporaryRoot();
        try
        {
            Assert.IsTrue(Switch2MagnetometerCalibrationFileStore.TryOpen(
                root, out var store));
            Switch2PersistentPeerId peer = Peer(22,
                Switch2ControllerModel.JoyCon2Left,
                Switch2AdvertisementCodec.JoyCon2LeftProductId);
            Assert.IsTrue(store.TryStore(peer, Calibration()));
            Assert.IsFalse(store.TryLoad(Peer(23,
                Switch2ControllerModel.JoyCon2Left,
                Switch2AdvertisementCodec.JoyCon2LeftProductId), out _));

            string record = Directory.GetFiles(Path.Combine(root,
                "Magnetometer"), "*.mag").Single();
            byte[] bytes = File.ReadAllBytes(record);
            bytes[30] ^= 0x5A;
            File.WriteAllBytes(record, bytes);
            Assert.IsFalse(store.TryLoad(peer, out _));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void RuntimeLoadsCalibrationBeforeActivation()
    {
        string root = NewTemporaryRoot();
        try
        {
            Assert.IsTrue(Switch2MagnetometerCalibrationFileStore.TryOpen(
                root, out var store));
            Switch2PersistentPeerId peer = Peer(41,
                Switch2ControllerModel.ProController2,
                Switch2AdvertisementCodec.ProController2ProductId);
            Assert.IsTrue(store.TryStore(peer, Calibration()));
            Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(1, 2,
                Switch2Transport.Usb, out var runtime, out _));
            Assert.IsFalse(runtime.HasLeftMagnetometerCalibration);

            Assert.IsTrue(runtime.TryBindMagnetometerCalibrationPersistence(
                store, peer));
            Assert.IsTrue(runtime.HasLeftMagnetometerCalibration);
            Assert.IsFalse(runtime.TryBindMagnetometerCalibrationPersistence(
                store, peer), "A runtime may bind persistent identity once.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Switch2MagnetometerCalibration Calibration()
    {
        Assert.IsTrue(Switch2MagnetometerMatrix3x3.TryCreate(
            1.10f, 0.02f, 0.0f,
            0.02f, 0.90f, 0.03f,
            0.0f, 0.03f, 1.00f,
            out var matrix, out _));
        Assert.IsTrue(Switch2MagnetometerCalibration.TryCreate(
            new Vector3(100.0f, -50.0f, 25.0f), matrix, 800.0f,
            Switch2MagnetometerCalibrationModel.FullEllipsoidV1,
            out var calibration));
        return calibration;
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
        string path = Path.Combine(Path.GetTempPath(), "ds4w-s2-mag-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
