using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2JoyConHoldModeFileStoreTests
{
    private static readonly byte[] InstallKey = Enumerable.Range(1, 32)
        .Select(value => (byte)value).ToArray();

    [TestMethod]
    public void FixedRecordRoundTripsOpaquePeerAndExactOrientation()
    {
        string root = NewTemporaryRoot();
        try
        {
            Assert.IsTrue(Switch2JoyConHoldModeFileStore.TryOpen(root,
                out var store));
            Switch2PersistentPeerId peer = Peer(17,
                Switch2ControllerModel.JoyCon2Left,
                Switch2AdvertisementCodec.JoyCon2LeftProductId);

            Assert.IsTrue(store.TryStore(peer,
                Switch2JoyConHoldMode.Horizontal));
            Assert.IsTrue(store.TryLoad(peer, out var actual));
            Assert.AreEqual(Switch2JoyConHoldMode.Horizontal, actual);
            string[] records = Directory.GetFiles(Path.Combine(root,
                "JoyConHoldMode"), "*.hold");
            Assert.AreEqual(1, records.Length);
            Assert.AreEqual(38L, new FileInfo(records[0]).Length);
            Assert.IsFalse(File.ReadAllText(records[0]).Contains(
                "JoyCon", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void DigestCorruptionDifferentPeerAndInvalidModeAreRejected()
    {
        string root = NewTemporaryRoot();
        try
        {
            Assert.IsTrue(Switch2JoyConHoldModeFileStore.TryOpen(root,
                out var store));
            Switch2PersistentPeerId peer = Peer(22,
                Switch2ControllerModel.JoyCon2Right,
                Switch2AdvertisementCodec.JoyCon2RightProductId);
            Assert.IsFalse(store.TryStore(peer,
                (Switch2JoyConHoldMode)byte.MaxValue));
            Assert.IsTrue(store.TryStore(peer,
                Switch2JoyConHoldMode.Vertical));
            Assert.IsFalse(store.TryLoad(Peer(23,
                Switch2ControllerModel.JoyCon2Right,
                Switch2AdvertisementCodec.JoyCon2RightProductId), out _));

            string record = Directory.GetFiles(Path.Combine(root,
                "JoyConHoldMode"), "*.hold").Single();
            byte[] bytes = File.ReadAllBytes(record);
            bytes[21] = (byte)Switch2JoyConHoldMode.Horizontal;
            File.WriteAllBytes(record, bytes);
            Assert.IsFalse(store.TryLoad(peer, out _));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void StandaloneRuntimeLoadsAndReplacesControllerOverride()
    {
        string root = NewTemporaryRoot();
        try
        {
            Assert.IsTrue(Switch2JoyConHoldModeFileStore.TryOpen(root,
                out var store));
            Switch2PersistentPeerId peer = Peer(41,
                Switch2ControllerModel.JoyCon2Left,
                Switch2AdvertisementCodec.JoyCon2LeftProductId);
            Assert.IsTrue(store.TryStore(peer,
                Switch2JoyConHoldMode.Horizontal));
            Assert.IsTrue(Switch2RuntimeInputDevice.TryCreateStandaloneJoyCon(
                Switch2ControllerModel.JoyCon2Left, 1, 2,
                out var runtime, out _));

            Assert.IsTrue(runtime.TryBindJoyConHoldModePersistence(store,
                peer));
            Assert.IsTrue(runtime.HasJoyConHoldModeOverride);
            Assert.AreEqual(Switch2JoyConHoldMode.Horizontal,
                runtime.ResolveStandaloneJoyConHoldMode(
                    Switch2JoyConHoldMode.Vertical));
            Assert.IsFalse(runtime.TryBindJoyConHoldModePersistence(store,
                peer), "A runtime may bind its opaque identity once.");

            runtime.StartUpdate();
            Assert.IsTrue(runtime.TrySetStandaloneJoyConHoldMode(
                Switch2JoyConHoldMode.Vertical, out bool persisted));
            Assert.IsTrue(persisted);
            Assert.AreEqual(Switch2JoyConHoldMode.Vertical,
                runtime.ResolveStandaloneJoyConHoldMode(
                    Switch2JoyConHoldMode.Horizontal));

            Assert.IsTrue(Switch2RuntimeInputDevice.TryCreateStandaloneJoyCon(
                Switch2ControllerModel.JoyCon2Left, 3, 4,
                out var replacement, out _));
            Assert.IsTrue(replacement.TryBindJoyConHoldModePersistence(store,
                peer));
            Assert.AreEqual(Switch2JoyConHoldMode.Vertical,
                replacement.ResolveStandaloneJoyConHoldMode(
                    Switch2JoyConHoldMode.Horizontal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void JoinedAndProRuntimesRejectStandaloneOrientationOwnership()
    {
        string root = NewTemporaryRoot();
        try
        {
            Assert.IsTrue(Switch2JoyConHoldModeFileStore.TryOpen(root,
                out var store));
            Switch2PersistentPeerId peer = Peer(51,
                Switch2ControllerModel.JoyCon2Left,
                Switch2AdvertisementCodec.JoyCon2LeftProductId);
            Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(1, 2,
                Switch2Transport.BluetoothLe, out var pro, out _));
            Assert.IsTrue(Switch2RuntimeInputDevice.TryCreateJoinedJoyCon(
                3, 4, 5, 6, 7, 8, out var joined, out _));

            Assert.IsFalse(pro.TryBindJoyConHoldModePersistence(store, peer));
            Assert.IsFalse(joined.TryBindJoyConHoldModePersistence(store,
                peer));
            Assert.IsFalse(pro.TrySetStandaloneJoyConHoldMode(
                Switch2JoyConHoldMode.Horizontal, out _));
            Assert.IsFalse(joined.TrySetStandaloneJoyConHoldMode(
                Switch2JoyConHoldMode.Horizontal, out _));
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
        string path = Path.Combine(Path.GetTempPath(), "ds4w-s2-hold-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
