using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2JoyConPairFileStoreTests
{
    [TestMethod]
    public void DpapiKeyAndOpaquePairRecordSurviveReopen()
    {
        string root = Path.Combine(Path.GetTempPath(),
            "ds4windows-switch2-pair-store-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.IsTrue(Switch2JoyConPairFileStore.TryOpen(root,
                out Switch2JoyConPairFileStore store,
                out Switch2PersistentPeerIdentityDeriver deriver));
            Switch2PersistentPeerId leftId;
            Switch2PersistentPeerId rightId;
            using (deriver)
            {
                Assert.IsTrue(deriver.TryDerive(
                    new FakeDevice("stable-left-device-id"),
                    Switch2ControllerModel.JoyCon2Left,
                    Switch2AdvertisementCodec.JoyCon2LeftProductId,
                    out leftId));
                Assert.IsTrue(deriver.TryDerive(
                    new FakeDevice("stable-right-device-id"),
                    Switch2ControllerModel.JoyCon2Right,
                    Switch2AdvertisementCodec.JoyCon2RightProductId,
                    out rightId));
            }
            Assert.IsTrue(Switch2JoyConAssociationPeer.TryCreate(leftId,
                Switch2ControllerModel.JoyCon2Left,
                Switch2AdvertisementCodec.JoyCon2LeftProductId,
                out Switch2JoyConAssociationPeer left));
            Assert.IsTrue(Switch2JoyConAssociationPeer.TryCreate(rightId,
                Switch2ControllerModel.JoyCon2Right,
                Switch2AdvertisementCodec.JoyCon2RightProductId,
                out Switch2JoyConAssociationPeer right));
            var association = new Switch2JoyConPairAssociationService(store);
            Assert.IsTrue(association.TryCreateExplicitPair(left, right,
                out Switch2JoyConPairRecord created, out _));
            Assert.IsTrue(store.TryLoadAll(out var records));
            Assert.AreEqual(1, records.Length);
            Assert.AreEqual(created.PairId, records[0].PairId);

            Assert.IsTrue(Switch2JoyConPairFileStore.TryOpen(root,
                out Switch2JoyConPairFileStore reopened,
                out Switch2PersistentPeerIdentityDeriver reopenedDeriver));
            using (reopenedDeriver)
            {
                Assert.IsTrue(reopenedDeriver.TryDerive(
                    new FakeDevice("stable-left-device-id"),
                    Switch2ControllerModel.JoyCon2Left,
                    Switch2AdvertisementCodec.JoyCon2LeftProductId,
                    out Switch2PersistentPeerId reopenedLeft));
                Assert.AreEqual(leftId, reopenedLeft);
            }
            Assert.IsTrue(reopened.TryLoad(created.PairId,
                out Switch2JoyConPairRecord loaded));
            Assert.AreEqual(created.Revision, loaded.Revision);

            byte[] persisted = Directory.GetFiles(root, "*",
                    SearchOption.AllDirectories).
                SelectMany(File.ReadAllBytes).ToArray();
            Assert.IsFalse(Contains(persisted,
                "stable-left-device-id"u8.ToArray()));
            Assert.IsFalse(Contains(persisted,
                "stable-right-device-id"u8.ToArray()));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void CatalogEnforcesRevisionCasAndExactDeletion()
    {
        string root = Path.Combine(Path.GetTempPath(),
            "ds4windows-switch2-pair-store-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.IsTrue(Switch2JoyConPairFileStore.TryOpen(root,
                out Switch2JoyConPairFileStore store, out var deriver));
            using (deriver)
            {
                Assert.IsTrue(deriver.TryDerive(new FakeDevice("left-a"),
                    Switch2ControllerModel.JoyCon2Left,
                    Switch2AdvertisementCodec.JoyCon2LeftProductId,
                    out Switch2PersistentPeerId leftId));
                Assert.IsTrue(deriver.TryDerive(new FakeDevice("right-a"),
                    Switch2ControllerModel.JoyCon2Right,
                    Switch2AdvertisementCodec.JoyCon2RightProductId,
                    out Switch2PersistentPeerId rightId));
                Assert.IsTrue(Switch2JoyConPairRecord.TryCreate(1,
                    Switch2JoyConPairId.CreateRandom(), leftId, rightId,
                    out Switch2JoyConPairRecord first));
                Assert.IsTrue(store.TryReplace(first, 0));
                Assert.IsFalse(store.TryReplace(first, 0));
                Assert.IsTrue(Switch2JoyConPairRecord.TryCreate(2,
                    first.PairId, leftId, rightId,
                    out Switch2JoyConPairRecord second));
                Assert.IsFalse(store.TryReplace(second, 9));
                Assert.IsTrue(store.TryReplace(second, 1));
                Assert.IsFalse(store.TryDelete(first.PairId, 1));
                Assert.IsTrue(store.TryDelete(first.PairId, 2));
                Assert.IsFalse(store.TryLoad(first.PairId, out _));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static bool Contains(byte[] haystack, byte[] needle)
    {
        for (int index = 0; index <= haystack.Length - needle.Length; index++)
        {
            if (haystack.AsSpan(index, needle.Length).SequenceEqual(needle))
            {
                return true;
            }
        }
        return false;
    }

    private sealed class FakeDevice : ISwitch2BluetoothWindowsDevice
    {
        private readonly byte[] identity;

        internal FakeDevice(string identity) =>
            this.identity = System.Text.Encoding.UTF8.GetBytes(identity);

        public bool IsConnected => true;

        public bool TryCopyStableAssociationIdentity(Span<byte> destination,
            out int bytesWritten)
        {
            bytesWritten = 0;
            if (destination.Length < identity.Length)
            {
                return false;
            }
            identity.CopyTo(destination);
            bytesWritten = identity.Length;
            return true;
        }

        public void AttachDisconnectedHandler(
            Switch2BluetoothWindowsDisconnectedHandler disconnected)
        {
        }

        public Task DetachDisconnectedHandlerAndDrainAsync() =>
            Task.CompletedTask;

        public ValueTask<Switch2BluetoothWindowsGattQuery<
            ISwitch2BluetoothWindowsGattService>>
            GetServicesForUuidUncachedAsync(Guid serviceUuid,
                CancellationToken cancellationToken) => throw new
                NotSupportedException();

        public void Dispose()
        {
        }
    }
}
