using System.Reflection;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2JoyConPairAssociationServiceTests
{
    private static readonly byte[] InstallKey = Enumerable.Range(1, 32)
        .Select(value => (byte)value).ToArray();

    [TestMethod]
    public void ExplicitCreateLoadReplaceDeleteUsesMonotonicCasRevisions()
    {
        var store = new InMemoryTransactionalPairStore();
        var service = new Switch2JoyConPairAssociationService(store);
        Switch2JoyConAssociationPeer left = Peer(10,
            Switch2ControllerModel.JoyCon2Left);
        Switch2JoyConAssociationPeer right = Peer(40,
            Switch2ControllerModel.JoyCon2Right);

        Assert.IsTrue(service.TryCreateExplicitPair(left, right,
            out var created, out var createFailure));
        Assert.AreEqual(Switch2JoyConPairAssociationFailure.None,
            createFailure);
        Assert.IsTrue(created.PairId.IsValid);
        Assert.AreEqual(1UL, created.Revision);
        Assert.IsTrue(service.TryLoadExplicitPair(created.PairId, 1,
            out var loaded, out var loadFailure));
        Assert.AreEqual(Switch2JoyConPairAssociationFailure.None,
            loadFailure);
        Assert.AreEqual(created.PairId, loaded.PairId);

        Switch2JoyConAssociationPeer replacementRight = Peer(70,
            Switch2ControllerModel.JoyCon2Right);
        Assert.IsTrue(service.TryReplaceExplicitPair(created.PairId, 1,
            left, replacementRight, out var replaced,
            out var replaceFailure));
        Assert.AreEqual(Switch2JoyConPairAssociationFailure.None,
            replaceFailure);
        Assert.AreEqual(2UL, replaced.Revision);
        Assert.AreEqual(replacementRight.PersistentPeerId,
            replaced.RightPeerId);
        Assert.IsTrue(service.TryDeleteExplicitPair(created.PairId, 2,
            out var deleteFailure));
        Assert.AreEqual(Switch2JoyConPairAssociationFailure.None,
            deleteFailure);
        Assert.IsFalse(service.TryLoadExplicitPair(created.PairId, 2,
            out _, out var missingFailure));
        Assert.AreEqual(Switch2JoyConPairAssociationFailure.NotFound,
            missingFailure);
    }

    [TestMethod]
    public void TwoConcurrentReplacementsHaveExactlyOneCasWinner()
    {
        var store = new InMemoryTransactionalPairStore();
        var service = new Switch2JoyConPairAssociationService(store);
        Switch2JoyConAssociationPeer left = Peer(11,
            Switch2ControllerModel.JoyCon2Left);
        Assert.IsTrue(service.TryCreateExplicitPair(left, Peer(41,
                Switch2ControllerModel.JoyCon2Right), out var created,
            out _));
        using Barrier replaceBarrier = new(2);
        store.ReplaceBarrier = replaceBarrier;
        bool firstWon = false;
        bool secondWon = false;
        Switch2JoyConPairAssociationFailure firstFailure = default;
        Switch2JoyConPairAssociationFailure secondFailure = default;

        Task first = Task.Run(() => firstWon =
            service.TryReplaceExplicitPair(created.PairId, 1, left,
                Peer(71, Switch2ControllerModel.JoyCon2Right), out _,
                out firstFailure));
        Task second = Task.Run(() => secondWon =
            service.TryReplaceExplicitPair(created.PairId, 1, left,
                Peer(101, Switch2ControllerModel.JoyCon2Right), out _,
                out secondFailure));

        Assert.IsTrue(Task.WaitAll(new[] { first, second },
            TimeSpan.FromSeconds(2)));
        Assert.AreNotEqual(firstWon, secondWon);
        Assert.AreEqual(Switch2JoyConPairAssociationFailure.None,
            firstWon ? firstFailure : secondFailure);
        Assert.AreEqual(Switch2JoyConPairAssociationFailure.
                ConcurrentModification,
            firstWon ? secondFailure : firstFailure);
        Assert.IsTrue(service.TryLoadExplicitPair(created.PairId, 2,
            out var current, out _));
        Assert.AreEqual(2UL, current.Revision);
        Assert.AreEqual(1, store.Count);
    }

    [TestMethod]
    public void StaleLoadAndDeleteCannotObserveOrRemoveNewerRevision()
    {
        var store = new InMemoryTransactionalPairStore();
        var service = new Switch2JoyConPairAssociationService(store);
        Switch2JoyConAssociationPeer left = Peer(12,
            Switch2ControllerModel.JoyCon2Left);
        Assert.IsTrue(service.TryCreateExplicitPair(left, Peer(42,
                Switch2ControllerModel.JoyCon2Right), out var created,
            out _));
        Assert.IsTrue(service.TryReplaceExplicitPair(created.PairId, 1,
            left, Peer(72, Switch2ControllerModel.JoyCon2Right),
            out var replaced, out _));

        Assert.IsFalse(service.TryLoadExplicitPair(created.PairId, 1,
            out var staleRecord, out var loadFailure));
        Assert.IsFalse(staleRecord.IsValid);
        Assert.AreEqual(Switch2JoyConPairAssociationFailure.StaleRevision,
            loadFailure);
        Assert.IsFalse(service.TryDeleteExplicitPair(created.PairId, 1,
            out var deleteFailure));
        Assert.AreEqual(Switch2JoyConPairAssociationFailure.StaleRevision,
            deleteFailure);
        Assert.IsTrue(service.TryLoadExplicitPair(created.PairId,
            replaced.Revision, out _, out _));
        Assert.AreEqual(1, store.Count);
    }

    [TestMethod]
    public void MaximumRevisionReplacementFailsWithoutWrapOrStoreMutation()
    {
        var store = new InMemoryTransactionalPairStore();
        var service = new Switch2JoyConPairAssociationService(store);
        Switch2JoyConAssociationPeer left = Peer(13,
            Switch2ControllerModel.JoyCon2Left);
        Switch2JoyConAssociationPeer right = Peer(43,
            Switch2ControllerModel.JoyCon2Right);
        Switch2JoyConPairId pairId = PairId(7);
        Assert.IsTrue(Switch2JoyConPairRecord.TryCreate(ulong.MaxValue,
            pairId, left.PersistentPeerId, right.PersistentPeerId,
            out var exhausted));
        store.Seed(exhausted);
        int replacementsBefore = store.ReplaceCount;

        Assert.IsFalse(service.TryReplaceExplicitPair(pairId,
            ulong.MaxValue, left, Peer(73,
                Switch2ControllerModel.JoyCon2Right), out var result,
            out var failure));
        Assert.IsFalse(result.IsValid);
        Assert.AreEqual(Switch2JoyConPairAssociationFailure.
            RevisionExhausted, failure);
        Assert.AreEqual(replacementsBefore, store.ReplaceCount);
        Assert.IsTrue(service.TryLoadExplicitPair(pairId, ulong.MaxValue,
            out var unchanged, out _));
        Assert.AreEqual(ulong.MaxValue, unchanged.Revision);
    }

    [TestMethod]
    public void ExactLeftRightAndDistinctPeerRulesFailBeforeStoreAccess()
    {
        var store = new InMemoryTransactionalPairStore();
        var service = new Switch2JoyConPairAssociationService(store);
        Switch2PersistentPeerId firstId = PersistentPeer(14,
            Switch2ControllerModel.JoyCon2Left);
        Switch2PersistentPeerId secondId = PersistentPeer(44,
            Switch2ControllerModel.JoyCon2Right);
        Switch2JoyConAssociationPeer left = Bind(firstId,
            Switch2ControllerModel.JoyCon2Left);
        Switch2JoyConAssociationPeer anotherLeft = Bind(secondId,
            Switch2ControllerModel.JoyCon2Left);
        Switch2JoyConAssociationPeer right = Bind(secondId,
            Switch2ControllerModel.JoyCon2Right);
        Switch2JoyConAssociationPeer anotherRight = Bind(firstId,
            Switch2ControllerModel.JoyCon2Right);

        AssertCreateFailure(service, left, anotherLeft,
            Switch2JoyConPairAssociationFailure.InvalidPeerRoles);
        AssertCreateFailure(service, right, anotherRight,
            Switch2JoyConPairAssociationFailure.InvalidPeerRoles);
        AssertCreateFailure(service, left, Bind(firstId,
                Switch2ControllerModel.JoyCon2Right),
            Switch2JoyConPairAssociationFailure.DuplicatePeer);
        AssertCreateFailure(service, default, right,
            Switch2JoyConPairAssociationFailure.InvalidPeer);
        Assert.IsFalse(Switch2JoyConAssociationPeer.TryCreate(firstId,
            Switch2ControllerModel.JoyCon2Left,
            Switch2AdvertisementCodec.JoyCon2RightProductId, out _));
        Assert.IsFalse(Switch2JoyConAssociationPeer.TryCreate(default,
            Switch2ControllerModel.JoyCon2Left,
            Switch2AdvertisementCodec.JoyCon2LeftProductId, out _));
        Assert.AreEqual(0, store.LoadCount);
        Assert.AreEqual(0, store.ReplaceCount);
        Assert.AreEqual(0, store.DeleteCount);
    }

    [TestMethod]
    public void InvalidPairIdsAndExpectedRevisionsNeverReachTheStore()
    {
        var store = new InMemoryTransactionalPairStore();
        var service = new Switch2JoyConPairAssociationService(store);
        Switch2JoyConAssociationPeer left = Peer(17,
            Switch2ControllerModel.JoyCon2Left);
        Switch2JoyConAssociationPeer right = Peer(47,
            Switch2ControllerModel.JoyCon2Right);
        Switch2JoyConPairId pairId = PairId(8);

        Assert.IsFalse(service.TryLoadExplicitPair(default, 1, out _,
            out var invalidLoad));
        Assert.AreEqual(Switch2JoyConPairAssociationFailure.InvalidArgument,
            invalidLoad);
        Assert.IsFalse(service.TryLoadExplicitPair(pairId, 0, out _,
            out invalidLoad));
        Assert.AreEqual(Switch2JoyConPairAssociationFailure.InvalidArgument,
            invalidLoad);
        Assert.IsFalse(service.TryReplaceExplicitPair(default, 1, left,
            right, out _, out var invalidReplace));
        Assert.AreEqual(Switch2JoyConPairAssociationFailure.InvalidArgument,
            invalidReplace);
        Assert.IsFalse(service.TryReplaceExplicitPair(pairId, 0, left,
            right, out _, out invalidReplace));
        Assert.AreEqual(Switch2JoyConPairAssociationFailure.InvalidArgument,
            invalidReplace);
        Assert.IsFalse(service.TryDeleteExplicitPair(default, 1,
            out var invalidDelete));
        Assert.AreEqual(Switch2JoyConPairAssociationFailure.InvalidArgument,
            invalidDelete);
        Assert.IsFalse(service.TryDeleteExplicitPair(pairId, 0,
            out invalidDelete));
        Assert.AreEqual(Switch2JoyConPairAssociationFailure.InvalidArgument,
            invalidDelete);
        Assert.AreEqual(0, store.LoadCount);
        Assert.AreEqual(0, store.ReplaceCount);
        Assert.AreEqual(0, store.DeleteCount);
    }

    [TestMethod]
    public void StoreExceptionsAreContainedForEveryOperation()
    {
        Switch2JoyConAssociationPeer left = Peer(15,
            Switch2ControllerModel.JoyCon2Left);
        Switch2JoyConAssociationPeer right = Peer(45,
            Switch2ControllerModel.JoyCon2Right);
        var inner = new InMemoryTransactionalPairStore();
        var createFault = new FaultingPairStore(inner)
        {
            ThrowOnReplace = true,
        };
        var createService = new Switch2JoyConPairAssociationService(
            createFault);
        Assert.IsFalse(createService.TryCreateExplicitPair(left, right,
            out var failedCreate, out var createFailure));
        Assert.IsFalse(failedCreate.IsValid);
        Assert.AreEqual(Switch2JoyConPairAssociationFailure.StoreFault,
            createFailure);

        var service = new Switch2JoyConPairAssociationService(inner);
        Assert.IsTrue(service.TryCreateExplicitPair(left, right,
            out var created, out _));

        var loadService = new Switch2JoyConPairAssociationService(
            new FaultingPairStore(inner) { ThrowOnLoad = true });
        Assert.IsFalse(loadService.TryLoadExplicitPair(created.PairId, 1,
            out var failedLoad, out var loadFailure));
        Assert.IsFalse(failedLoad.IsValid);
        Assert.AreEqual(Switch2JoyConPairAssociationFailure.StoreFault,
            loadFailure);

        var replaceService = new Switch2JoyConPairAssociationService(
            new FaultingPairStore(inner) { ThrowOnReplace = true });
        Assert.IsFalse(replaceService.TryReplaceExplicitPair(created.PairId,
            1, left, Peer(75, Switch2ControllerModel.JoyCon2Right),
            out var failedReplace, out var replaceFailure));
        Assert.IsFalse(failedReplace.IsValid);
        Assert.AreEqual(Switch2JoyConPairAssociationFailure.StoreFault,
            replaceFailure);

        var deleteService = new Switch2JoyConPairAssociationService(
            new FaultingPairStore(inner) { ThrowOnDelete = true });
        Assert.IsFalse(deleteService.TryDeleteExplicitPair(created.PairId, 1,
            out var deleteFailure));
        Assert.AreEqual(Switch2JoyConPairAssociationFailure.StoreFault,
            deleteFailure);
        Assert.IsTrue(service.TryLoadExplicitPair(created.PairId, 1,
            out _, out _));
    }

    [TestMethod]
    public void AssociationObjectsNeverFormatSerializedIdentityMaterial()
    {
        var store = new InMemoryTransactionalPairStore();
        var service = new Switch2JoyConPairAssociationService(store);
        Switch2JoyConAssociationPeer left = Peer(16,
            Switch2ControllerModel.JoyCon2Left);
        Switch2JoyConAssociationPeer right = Peer(46,
            Switch2ControllerModel.JoyCon2Right);
        Assert.IsTrue(service.TryCreateExplicitPair(left, right,
            out var record, out _));
        var encoded = new byte[Switch2JoyConPairRecord.EncodedLength];
        Assert.IsTrue(record.TryWrite(encoded));
        var pairIdBytes = new byte[Switch2JoyConPairId.EncodedLength];
        Assert.IsTrue(record.PairId.TryWrite(pairIdBytes));
        var leftPeerBytes = new byte[Switch2PersistentPeerId.EncodedLength];
        Assert.IsTrue(record.LeftPeerId.TryWrite(leftPeerBytes));
        var rightPeerBytes = new byte[Switch2PersistentPeerId.EncodedLength];
        Assert.IsTrue(record.RightPeerId.TryWrite(rightPeerBytes));
        string[] serializedValues =
        {
            Convert.ToHexString(encoded),
            Convert.ToHexString(pairIdBytes),
            Convert.ToHexString(leftPeerBytes),
            Convert.ToHexString(rightPeerBytes),
        };

        Assert.AreEqual("Switch2JoyConAssociationPeer(Left)",
            left.ToString());
        Assert.AreEqual("Switch2JoyConAssociationPeer(Right)",
            right.ToString());
        Assert.AreEqual(nameof(Switch2JoyConPairAssociationService),
            service.ToString());
        Assert.AreEqual(typeof(Switch2JoyConPairRecord).FullName,
            record.ToString());
        Assert.AreEqual(typeof(Switch2JoyConPairId).FullName,
            record.PairId.ToString());
        Assert.AreEqual(typeof(Switch2PersistentPeerId).FullName,
            record.LeftPeerId.ToString());
        string[] formattedValues =
        {
            record.ToString(),
            record.PairId.ToString(),
            record.LeftPeerId.ToString(),
            record.RightPeerId.ToString(),
            left.ToString(),
            right.ToString(),
            service.ToString(),
        };
        foreach (string formatted in formattedValues)
        {
            Assert.IsFalse(serializedValues.Any(serialized =>
                    formatted.Contains(serialized,
                        StringComparison.OrdinalIgnoreCase)),
                $"Formatted association value leaked serialized material: " +
                formatted);
        }

        const BindingFlags fields = BindingFlags.Instance |
            BindingFlags.Public | BindingFlags.NonPublic;
        Assert.IsFalse(typeof(Switch2JoyConPairAssociationService)
            .GetFields(fields).Any(field => field.FieldType == typeof(byte[]) ||
                field.FieldType == typeof(string)));
        Assert.IsFalse(typeof(Switch2JoyConAssociationPeer)
            .GetFields(fields).Any(field => field.FieldType == typeof(byte[]) ||
                field.FieldType == typeof(string)));
    }

    private static void AssertCreateFailure(
        Switch2JoyConPairAssociationService service,
        in Switch2JoyConAssociationPeer left,
        in Switch2JoyConAssociationPeer right,
        Switch2JoyConPairAssociationFailure expected)
    {
        Assert.IsFalse(service.TryCreateExplicitPair(left, right,
            out var record, out var failure));
        Assert.IsFalse(record.IsValid);
        Assert.AreEqual(expected, failure);
    }

    private static Switch2JoyConAssociationPeer Peer(byte seed,
        Switch2ControllerModel model) => Bind(PersistentPeer(seed, model),
        model);

    private static Switch2PersistentPeerId PersistentPeer(byte seed,
        Switch2ControllerModel model)
    {
        var identity = Enumerable.Range(seed, 16)
            .Select(value => (byte)value).ToArray();
        ushort productId = ProductId(model);
        Assert.IsTrue(Switch2PersistentPeerId.TryDerive(InstallKey, identity,
            model, productId, out var peerId));
        return peerId;
    }

    private static Switch2JoyConAssociationPeer Bind(
        Switch2PersistentPeerId peerId, Switch2ControllerModel model)
    {
        Assert.IsTrue(Switch2JoyConAssociationPeer.TryCreate(peerId, model,
            ProductId(model), out var peer));
        return peer;
    }

    private static ushort ProductId(Switch2ControllerModel model) =>
        model switch
        {
            Switch2ControllerModel.JoyCon2Left =>
                Switch2AdvertisementCodec.JoyCon2LeftProductId,
            Switch2ControllerModel.JoyCon2Right =>
                Switch2AdvertisementCodec.JoyCon2RightProductId,
            _ => throw new ArgumentOutOfRangeException(nameof(model)),
        };

    private static Switch2JoyConPairId PairId(byte seed)
    {
        var encoded = new byte[Switch2JoyConPairId.EncodedLength];
        encoded[0] = seed;
        Assert.IsTrue(Switch2JoyConPairId.TryRead(encoded, out var pairId));
        return pairId;
    }

    private sealed class InMemoryTransactionalPairStore :
        ISwitch2JoyConPairStore
    {
        private readonly object sync = new();
        private readonly Dictionary<Switch2JoyConPairId,
            Switch2JoyConPairRecord> records = new();

        internal Barrier ReplaceBarrier { get; set; }

        internal int LoadCount { get; private set; }

        internal int ReplaceCount { get; private set; }

        internal int DeleteCount { get; private set; }

        internal int Count
        {
            get
            {
                lock (sync)
                {
                    return records.Count;
                }
            }
        }

        public bool TryLoad(Switch2JoyConPairId pairId,
            out Switch2JoyConPairRecord record)
        {
            lock (sync)
            {
                LoadCount++;
                return records.TryGetValue(pairId, out record);
            }
        }

        public bool TryReplace(in Switch2JoyConPairRecord record,
            ulong expectedPriorRevision)
        {
            Barrier barrier = expectedPriorRevision == 0 ? null :
                ReplaceBarrier;
            barrier?.SignalAndWait(TimeSpan.FromSeconds(2));
            lock (sync)
            {
                ReplaceCount++;
                bool found = records.TryGetValue(record.PairId,
                    out Switch2JoyConPairRecord current);
                if (expectedPriorRevision == 0)
                {
                    if (found || record.Revision != 1)
                    {
                        return false;
                    }
                }
                else if (!found || current.Revision !=
                        expectedPriorRevision ||
                    expectedPriorRevision == ulong.MaxValue ||
                    record.Revision != expectedPriorRevision + 1)
                {
                    return false;
                }
                records[record.PairId] = record;
                return true;
            }
        }

        public bool TryDelete(Switch2JoyConPairId pairId,
            ulong expectedRevision)
        {
            lock (sync)
            {
                DeleteCount++;
                if (!records.TryGetValue(pairId, out var current) ||
                    current.Revision != expectedRevision)
                {
                    return false;
                }
                return records.Remove(pairId);
            }
        }

        internal void Seed(in Switch2JoyConPairRecord record)
        {
            Assert.IsTrue(record.IsValid);
            lock (sync)
            {
                records.Add(record.PairId, record);
            }
        }
    }

    private sealed class FaultingPairStore : ISwitch2JoyConPairStore
    {
        private readonly ISwitch2JoyConPairStore inner;

        internal FaultingPairStore(ISwitch2JoyConPairStore inner)
        {
            this.inner = inner;
        }

        internal bool ThrowOnLoad { get; init; }

        internal bool ThrowOnReplace { get; init; }

        internal bool ThrowOnDelete { get; init; }

        public bool TryLoad(Switch2JoyConPairId pairId,
            out Switch2JoyConPairRecord record)
        {
            if (ThrowOnLoad)
            {
                throw new InvalidOperationException("Synthetic load fault.");
            }
            return inner.TryLoad(pairId, out record);
        }

        public bool TryReplace(in Switch2JoyConPairRecord record,
            ulong expectedPriorRevision)
        {
            if (ThrowOnReplace)
            {
                throw new InvalidOperationException(
                    "Synthetic replace fault.");
            }
            return inner.TryReplace(record, expectedPriorRevision);
        }

        public bool TryDelete(Switch2JoyConPairId pairId,
            ulong expectedRevision)
        {
            if (ThrowOnDelete)
            {
                throw new InvalidOperationException("Synthetic delete fault.");
            }
            return inner.TryDelete(pairId, expectedRevision);
        }
    }
}
