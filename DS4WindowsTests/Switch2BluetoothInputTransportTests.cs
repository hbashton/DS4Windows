using System.Buffers.Binary;
using System.Reflection;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2BluetoothInputTransportTests
{
    private const ulong DeviceGeneration = 11;
    private const ulong TransportGeneration = 17;
    private const long QpcFrequency = 10_000_000;

    private static readonly byte[] SessionKey = Enumerable.Range(0, 32)
        .Select(value => (byte)(value + 1)).ToArray();

    [DataTestMethod]
    [DataRow(Switch2ControllerModel.ProController2)]
    [DataRow(Switch2ControllerModel.JoyCon2Left)]
    [DataRow(Switch2ControllerModel.JoyCon2Right)]
    public void SlowVirtualStartupRetainsLatestUnpublishedStateWithoutOverflow(Switch2ControllerModel model)
    {
        var admission = Admission(model, 901);
        var lease = new FakeLease(admission, ExactGatt(admission));
        var sink = new RecordingSink(recordCounters: true);
        Assert.IsTrue(TryPrepareOwner(admission, lease, sink, 16,
            out var owner, out var credential, out _));
        byte[] body = Body(0);
        try
        {
            // The real b62 failure filled 16 slots while Xbox startup parked
            // publication. Simulate four seconds of 250Hz input without sleep.
            for (uint counter = 1; counter <= 1000; counter++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(body, counter);
                lease.Notify(body, counter);
            }
            Assert.IsTrue(owner.IsPrepared, owner.EndReason.ToString());
            Assert.AreEqual(1, owner.QueuedCount);
            Assert.AreEqual(0L, owner.OverflowCount);
            Assert.AreEqual(0, lease.UnsubscribeCount);
            Assert.AreEqual(0, sink.ProPublished + sink.JoyConPublished);
            Assert.IsTrue(owner.TryCommitPrepared(credential, out var failure), failure.ToString());
            Assert.AreEqual(Switch2BluetoothInputDrainDisposition.Published, owner.DrainOne());
            Assert.AreEqual(1000u, sink.LastCounter);
            lease.Notify(Body(1001), 1001);
            lease.Notify(Body(1002), 1002);
            owner.DrainOne();
            owner.DrainOne();
            CollectionAssert.AreEqual(new uint[] { 1000, 1001, 1002 }, sink.Counters.ToArray());
        }
        finally { owner.Stop(); }
    }

    [DataTestMethod]
    [DataRow(Switch2ControllerModel.ProController2)]
    [DataRow(Switch2ControllerModel.JoyCon2Left)]
    [DataRow(Switch2ControllerModel.JoyCon2Right)]
    public void VirtualOutputTransitionKeepsLatestStateThenResumesOrderedInput(Switch2ControllerModel model)
    {
        var admission = Admission(model, 902);
        var lease = new FakeLease(admission, ExactGatt(admission));
        var sink = new RecordingSink(recordCounters: true);
        Assert.IsTrue(TryPrepareOwner(admission, lease, sink, 2,
            out var owner, out var credential, out _));
        Assert.IsTrue(owner.TryCommitPrepared(credential, out _));
        try
        {
            lease.Notify(Body(1), 1);
            Assert.AreEqual(Switch2BluetoothInputDrainDisposition.Published, owner.DrainOne());
            // Start with a nonzero FIFO head and two pending old-output frames.
            lease.Notify(Body(2), 2);
            lease.Notify(Body(3), 3);
            sink.IsVirtualOutputTransitionActive = true;
            for (uint counter = 4; counter <= 1000; counter++)
                lease.Notify(Body(counter), counter);
            Assert.AreEqual(0L, owner.OverflowCount);
            Assert.AreEqual(1, owner.QueuedCount);
            lease.Notify(Body(999), 999); // A stale callback cannot replace it.
            sink.IsVirtualOutputTransitionActive = false;
            Assert.AreEqual(Switch2BluetoothInputDrainDisposition.Published, owner.DrainOne());
            Assert.AreEqual(1000u, sink.LastCounter);
            lease.Notify(Body(1001), 1001);
            lease.Notify(Body(1002), 1002);
            owner.DrainOne();
            owner.DrainOne();
            CollectionAssert.AreEqual(new uint[] { 1, 1000, 1001, 1002 }, sink.Counters.ToArray());
            // Outside the explicit transition, overflow still fails closed.
            lease.Notify(Body(1003), 1003);
            lease.Notify(Body(1004), 1004);
            lease.Notify(Body(1005), 1005);
            Assert.AreEqual(1L, owner.OverflowCount);
            Assert.AreEqual(Switch2BluetoothInputEndReason.QueueOverflow, owner.EndReason);
        }
        finally { owner.Stop(); }
    }

    [TestMethod]
    public void ConnectionAdmissionIsCurrentScanAndRememberedThisHostOnly()
    {
        var registry = new Switch2BluetoothCandidateRegistry();
        Assert.IsTrue(registry.TryBeginScan(4));
        Switch2BluetoothPeerToken unassociatedToken = Token(4, 1);
        var unassociated = new Switch2Advertisement(
            Switch2ControllerModel.JoyCon2Left,
            Switch2AdvertisementCodec.JoyCon2LeftProductId, false,
            Switch2AdvertisedHost.None);
        Switch2BluetoothCandidateObservation explicitAssociation = registry
            .Observe(4, unassociatedToken, 1, unassociated);
        Assert.AreEqual(Switch2BluetoothObservationDisposition
            .RequiresExplicitAssociation, explicitAssociation.Disposition);
        Assert.IsFalse(registry.TryCreateRememberedConnectionAdmission(
            explicitAssociation, out _));

        Switch2BluetoothPeerToken rememberedToken = Token(4, 2);
        var remembered = new Switch2Advertisement(
            Switch2ControllerModel.ProController2,
            Switch2AdvertisementCodec.ProController2ProductId, false,
            Switch2AdvertisedHost.ThisHost);
        Switch2BluetoothCandidateObservation candidate = registry.Observe(4,
            rememberedToken, 2, remembered);
        Assert.IsTrue(registry.TryCreateRememberedConnectionAdmission(
            candidate, out Switch2BluetoothConnectionAdmission admission));
        Assert.IsTrue(admission.IsValid);
        Assert.IsFalse(registry.TryCreateRememberedConnectionAdmission(
            candidate, out _),
            "One scan observation must not authorize two connection owners.");

        Assert.IsTrue(registry.TryEndScan(4));
        Assert.IsFalse(registry.TryCreateRememberedConnectionAdmission(
            candidate, out _), "Ending a scan must retire its authority.");
        Assert.IsTrue(registry.TryBeginScan(5));
        Assert.IsFalse(registry.TryCreateRememberedConnectionAdmission(
            candidate, out _), "An old token cannot cross a scan generation.");
    }

    [TestMethod]
    public void JoyConPairCanWaitForUserWhileKeepingOnlyEachLatestState()
    {
        var sink = new RecordingSink(recordCounters: true);
        PrepareJoyConPair(902, 1, sink, out var leftLease, out var left,
            out var leftCredential, out var rightLease, out var right,
            out var rightCredential);
        for (uint counter = 1; counter <= 1000; counter++)
        {
            leftLease.Notify(Body(counter), counter);
            rightLease.Notify(Body(counter + 1000), counter);
        }
        Assert.IsTrue(left.IsPrepared && right.IsPrepared);
        Assert.AreEqual(1, left.QueuedCount);
        Assert.AreEqual(1, right.QueuedCount);
        Assert.AreEqual(0L, left.OverflowCount + right.OverflowCount);
        Assert.AreEqual(0, sink.JoyConPublished + sink.JoyConLost);
        Assert.IsTrue(Switch2BluetoothInputOwner.TryCommitPreparedPair(
            left, leftCredential, right, rightCredential, out var failure), failure.ToString());
        left.DrainOne();
        right.DrainOne();
        CollectionAssert.AreEqual(new uint[] { 1000, 2000 }, sink.Counters.ToArray());
        left.Stop();
        right.Stop();
    }

    [TestMethod]
    public void OlderPrecommitCallbackCannotReplaceNewerInitialState()
    {
        var admission = Admission(Switch2ControllerModel.ProController2, 903);
        var lease = new FakeLease(admission, ExactGatt(admission));
        var sink = new RecordingSink();
        Assert.IsTrue(TryPrepareOwner(admission, lease, sink, 1,
            out var owner, out var credential, out _));
        lease.Notify(Body(20), 20);
        lease.Notify(Body(10), 10);
        Assert.AreEqual(1L, owner.RejectedNotificationCount);
        Assert.IsTrue(owner.TryCommitPrepared(credential, out _));
        owner.DrainOne();
        Assert.AreEqual(20u, sink.LastCounter);
        owner.Stop();
    }

    [TestMethod]
    public void ConnectionAdmissionIsAtomicAndSingleUseWithinTheScan()
    {
        var registry = new Switch2BluetoothCandidateRegistry();
        Assert.IsTrue(registry.TryBeginScan(6));
        Switch2BluetoothPeerToken token = Token(6, 44);
        var advertisement = new Switch2Advertisement(
            Switch2ControllerModel.ProController2,
            Switch2AdvertisementCodec.ProController2ProductId, false,
            Switch2AdvertisedHost.ThisHost);
        Switch2BluetoothCandidateObservation observation = registry.Observe(
            6, token, 1, advertisement);
        int admitted = 0;

        Parallel.For(0, 64, iteration =>
        {
            if (registry.TryCreateRememberedConnectionAdmission(observation,
                    out _))
            {
                Interlocked.Increment(ref admitted);
            }
        });

        Assert.AreEqual(1, admitted);
    }

    [TestMethod]
    public void JoyConPairAdmissionConsumptionIsAtomicAndSideExact()
    {
        Switch2BluetoothConnectionAdmission left = Admission(
            Switch2ControllerModel.JoyCon2Left, 60);
        Switch2BluetoothConnectionAdmission right = Admission(
            Switch2ControllerModel.JoyCon2Right, 60);
        Assert.IsTrue(Switch2BluetoothConnectionAdmission.TryConsumePair(
            left, right));
        Assert.IsFalse(left.TryConsume());
        Assert.IsFalse(right.TryConsume());
        Assert.IsFalse(Switch2BluetoothConnectionAdmission.TryConsumePair(
            left, right));

        Switch2BluetoothConnectionAdmission wrongLeft = Admission(
            Switch2ControllerModel.JoyCon2Right, 61);
        Switch2BluetoothConnectionAdmission wrongRight = Admission(
            Switch2ControllerModel.JoyCon2Left, 61);
        Assert.IsFalse(Switch2BluetoothConnectionAdmission.TryConsumePair(
            wrongLeft, wrongRight));
        Switch2BluetoothConnectionAdmission otherScanRight = Admission(
            Switch2ControllerModel.JoyCon2Right, 62);
        Assert.IsFalse(Switch2BluetoothConnectionAdmission.TryConsumePair(
            wrongRight, otherScanRight));
        Assert.IsTrue(wrongLeft.TryConsume());
        Assert.IsTrue(wrongRight.TryConsume());
        Assert.IsTrue(otherScanRight.TryConsume(),
            "Role/scan validation failures must consume no admission.");
    }

    [TestMethod]
    public void StandaloneVersusPairConsumptionHasOneLinearizedWinner()
    {
        for (int iteration = 0; iteration < 64; iteration++)
        {
            ulong scanGeneration = (ulong)iteration + 100;
            Switch2BluetoothConnectionAdmission left = Admission(
                Switch2ControllerModel.JoyCon2Left, scanGeneration);
            Switch2BluetoothConnectionAdmission right = Admission(
                Switch2ControllerModel.JoyCon2Right, scanGeneration);
            using Barrier start = new(2);
            bool standaloneWon = false;
            bool pairWon = false;
            Task standalone = Task.Run(() =>
            {
                start.SignalAndWait();
                standaloneWon = left.TryConsume();
            });
            Task pair = Task.Run(() =>
            {
                start.SignalAndWait();
                pairWon = Switch2BluetoothConnectionAdmission.
                    TryConsumePair(left, right);
            });
            Assert.IsTrue(Task.WaitAll(new[] { standalone, pair },
                TimeSpan.FromSeconds(2)));
            Assert.AreNotEqual(standaloneWon, pairWon);
            Assert.AreEqual(standaloneWon, right.TryConsume(),
                "The losing pair attempt must not consume the right half.");
        }
    }

    [TestMethod]
    public void CompetingPairsSharingOneHalfConsumeOnlyTheWinningPair()
    {
        for (int iteration = 0; iteration < 64; iteration++)
        {
            ulong scanGeneration = (ulong)iteration + 200;
            Switch2BluetoothConnectionAdmission left = Admission(
                Switch2ControllerModel.JoyCon2Left, scanGeneration);
            Switch2BluetoothConnectionAdmission firstRight = Admission(
                Switch2ControllerModel.JoyCon2Right, scanGeneration);
            Switch2BluetoothConnectionAdmission secondRight = Admission(
                Switch2ControllerModel.JoyCon2Right, scanGeneration);
            using Barrier start = new(2);
            bool firstWon = false;
            bool secondWon = false;
            Task first = Task.Run(() =>
            {
                start.SignalAndWait();
                firstWon = Switch2BluetoothConnectionAdmission.
                    TryConsumePair(left, firstRight);
            });
            Task second = Task.Run(() =>
            {
                start.SignalAndWait();
                secondWon = Switch2BluetoothConnectionAdmission.
                    TryConsumePair(left, secondRight);
            });

            Assert.IsTrue(Task.WaitAll(new[] { first, second },
                TimeSpan.FromSeconds(2)));
            Assert.AreNotEqual(firstWon, secondWon);
            Assert.IsFalse(left.TryConsume());
            Assert.AreEqual(secondWon, firstRight.TryConsume(),
                "The first right half is free only when the second pair won.");
            Assert.AreEqual(firstWon, secondRight.TryConsume(),
                "The second right half is free only when the first pair won.");
        }
    }

    [TestMethod]
    public void IssuedAdmissionAndLeaseSurfaceRetainNoPeerIdentityMaterial()
    {
        Switch2BluetoothConnectionAdmission admission = Admission(
            Switch2ControllerModel.ProController2, 61);
        var lease = new FakeLease(admission, ExactGatt(admission));

        AssertNoPeerIdentityMaterial(
            typeof(Switch2BluetoothConnectionAdmission));
        AssertNoPeerIdentityMaterial(typeof(ISwitch2BluetoothInputLease));
        AssertNoPeerIdentityMaterial(lease.GetType());
    }

    [TestMethod]
    public void IssuedConnectionAdmissionCreatesAtMostOneInputOwner()
    {
        Switch2BluetoothConnectionAdmission admission = Admission(
            Switch2ControllerModel.ProController2, 7);
        int owners = 0;
        int expectedRejections = 0;
        int unexpected = 0;
        Switch2BluetoothInputOwner winner = null;

        Parallel.For(0, 64, _ =>
        {
            var lease = new FakeLease(admission, ExactGatt(admission));
            if (TryCreateOwner(admission, lease, new RecordingSink(), 2,
                    out Switch2BluetoothInputOwner created,
                    out Switch2BluetoothInputStartFailure failure))
            {
                Interlocked.CompareExchange(ref winner, created, null);
                Interlocked.Increment(ref owners);
            }
            else if (failure == Switch2BluetoothInputStartFailure.
                AdmissionAlreadyConsumed)
            {
                Interlocked.Increment(ref expectedRejections);
            }
            else
            {
                Interlocked.Increment(ref unexpected);
            }
        });

        Assert.AreEqual(1, owners);
        Assert.AreEqual(63, expectedRejections);
        Assert.AreEqual(0, unexpected);
        Assert.IsNotNull(winner);
        Assert.IsTrue(winner.Stop());
    }

    [TestMethod]
    public void QuarantinedIdentityCannotBecomeAConnectionAdmission()
    {
        var registry = new Switch2BluetoothCandidateRegistry();
        Assert.IsTrue(registry.TryBeginScan(9));
        Switch2BluetoothPeerToken token = Token(9, 1);
        var local = new Switch2Advertisement(
            Switch2ControllerModel.ProController2,
            Switch2AdvertisementCodec.ProController2ProductId, false,
            Switch2AdvertisedHost.ThisHost);
        Switch2BluetoothCandidateObservation candidate = registry.Observe(9,
            token, 1, local);
        var conflict = new Switch2Advertisement(
            Switch2ControllerModel.JoyCon2Right,
            Switch2AdvertisementCodec.JoyCon2RightProductId, false,
            Switch2AdvertisedHost.ThisHost);
        Assert.AreEqual(Switch2BluetoothObservationDisposition.IdentityConflict,
            registry.Observe(9, token, 2, conflict).Disposition);
        Assert.IsFalse(registry.TryCreateRememberedConnectionAdmission(
            candidate, out _));
    }

    [TestMethod]
    public void LeaseSurfaceCannotPairWriteCommandsReadNvmOrSendOutput()
    {
        string[] methodNames = typeof(ISwitch2BluetoothInputLease)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => !method.IsSpecialName)
            .Select(method => method.Name).ToArray();
        CollectionAssert.AreEquivalent(new[]
        {
            nameof(ISwitch2BluetoothInputLease.TrySubscribeCccdNotify),
            nameof(ISwitch2BluetoothInputLease.TryUnsubscribeCccdNone),
        }, methodNames);
        string surface = string.Join('|', methodNames);
        Assert.IsFalse(surface.Contains("Pair", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(surface.Contains("Nvm", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(surface.Contains("Command", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(surface.Contains("Output", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(surface.Contains("CharacteristicWrite",
            StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void EveryPhysicalModelUsesOnlyExactCommon05GattShape()
    {
        foreach (Switch2ControllerModel model in new[]
        {
            Switch2ControllerModel.JoyCon2Left,
            Switch2ControllerModel.JoyCon2Right,
            Switch2ControllerModel.ProController2,
        })
        {
            Switch2BluetoothConnectionAdmission admission = Admission(model,
                scanGeneration: (ulong)model + 20);
            var lease = new FakeLease(admission, ExactGatt(admission));
            var sink = new RecordingSink();
            Assert.IsTrue(TryCreateOwner(admission, lease, sink, 4,
                out Switch2BluetoothInputOwner owner, out var failure),
                $"{model}: {failure}");
            Assert.IsTrue(owner.IsActive);
            Assert.AreEqual(1, lease.SubscribeCount);
            Assert.IsTrue(owner.Stop());
        }
    }

    [TestMethod]
    public void GattMultiplicityIdentityAndPropertiesFailBeforeSubscription()
    {
        Switch2BluetoothConnectionAdmission admission = Admission(
            Switch2ControllerModel.ProController2, 31);
        Switch2BluetoothGattSnapshot valid = ExactGatt(admission);
        Switch2GattProperty exact = Switch2GattProperty.Read |
            Switch2GattProperty.Notify;
        Switch2BluetoothGattSnapshot[] invalid =
        {
            new(admission.ScanGeneration, 0, 1, valid.ServiceUuid,
                valid.CharacteristicUuid, exact),
            new(admission.ScanGeneration, 2, 1, valid.ServiceUuid,
                valid.CharacteristicUuid, exact),
            new(admission.ScanGeneration, 1, 0, valid.ServiceUuid,
                valid.CharacteristicUuid, exact),
            new(admission.ScanGeneration, 1, 2, valid.ServiceUuid,
                valid.CharacteristicUuid, exact),
            new(admission.ScanGeneration + 1, 1, 1, valid.ServiceUuid,
                valid.CharacteristicUuid, exact),
            new(admission.ScanGeneration, 1, 1, Guid.NewGuid(),
                valid.CharacteristicUuid, exact),
            new(admission.ScanGeneration, 1, 1, valid.ServiceUuid,
                Switch2InputCodec.ProController2_09CharacteristicUuid, exact),
            new(admission.ScanGeneration, 1, 1, valid.ServiceUuid,
                valid.CharacteristicUuid, Switch2GattProperty.Notify),
            new(admission.ScanGeneration, 1, 1, valid.ServiceUuid,
                valid.CharacteristicUuid, exact | Switch2GattProperty.Write),
        };

        foreach (Switch2BluetoothGattSnapshot snapshot in invalid)
        {
            var lease = new FakeLease(admission, snapshot);
            Assert.IsFalse(TryCreateOwner(admission, lease,
                new RecordingSink(), 4, out _, out var failure));
            Assert.AreEqual(Switch2BluetoothInputStartFailure.InvalidGattShape,
                failure);
            Assert.AreEqual(0, lease.SubscribeCount);
        }
    }

    [TestMethod]
    public void LeaseAndCalibrationMustMatchExactConnectionLifetime()
    {
        Switch2BluetoothConnectionAdmission admission = Admission(
            Switch2ControllerModel.ProController2, 40);
        Switch2BluetoothConnectionAdmission other = Admission(
            Switch2ControllerModel.ProController2, 41);
        var wrongLease = new FakeLease(other, ExactGatt(other));
        Assert.IsFalse(TryCreateOwner(admission, wrongLease,
            new RecordingSink(), 4, out _, out var leaseFailure));
        Assert.AreEqual(
            Switch2BluetoothInputStartFailure.LeaseIdentityMismatch,
            leaseFailure);

        var lease = new FakeLease(admission, ExactGatt(admission));
        Assert.IsTrue(Switch2InputCalibrationSnapshot.TryCreateFallback(
            Switch2ControllerModel.ProController2, DeviceGeneration + 1,
            out Switch2InputCalibrationSnapshot staleCalibration));
        Assert.IsFalse(Switch2BluetoothInputOwner.TryCreate(admission, lease,
            new RecordingSink(), DeviceGeneration, TransportGeneration,
            QpcFrequency, staleCalibration, 4, out _, out var calibrationFailure));
        Assert.AreEqual(Switch2BluetoothInputStartFailure.InvalidCalibration,
            calibrationFailure);
        Assert.AreEqual(0, lease.SubscribeCount);
    }

    [TestMethod]
    public void InvalidAdmissionLifetimeAndCapacityFailBeforeSubscription()
    {
        Switch2BluetoothConnectionAdmission admission = Admission(
            Switch2ControllerModel.ProController2, 45);
        var lease = new FakeLease(admission, ExactGatt(admission));
        Assert.IsTrue(Switch2InputCalibrationSnapshot.TryCreateFallback(
            admission.Model, DeviceGeneration,
            out Switch2InputCalibrationSnapshot calibration));

        Assert.IsFalse(Switch2BluetoothInputOwner.TryCreate(default, lease,
            new RecordingSink(), DeviceGeneration, TransportGeneration,
            QpcFrequency, calibration, 1, out _, out var admissionFailure));
        Assert.AreEqual(Switch2BluetoothInputStartFailure.InvalidAdmission,
            admissionFailure);
        Assert.IsFalse(Switch2BluetoothInputOwner.TryCreate(admission, lease,
            new RecordingSink(), 0, TransportGeneration, QpcFrequency,
            calibration, 1, out _, out var generationFailure));
        Assert.AreEqual(Switch2BluetoothInputStartFailure.InvalidArgument,
            generationFailure);
        Assert.IsFalse(Switch2BluetoothInputOwner.TryCreate(admission, lease,
            new RecordingSink(), DeviceGeneration, TransportGeneration,
            QpcFrequency, calibration, 0, out _, out var zeroCapacity));
        Assert.AreEqual(Switch2BluetoothInputStartFailure.InvalidArgument,
            zeroCapacity);
        Assert.IsFalse(Switch2BluetoothInputOwner.TryCreate(admission, lease,
            new RecordingSink(), DeviceGeneration, TransportGeneration,
            QpcFrequency, calibration,
            Switch2BluetoothInputOwner.MaximumQueueCapacity + 1, out _,
            out var excessiveCapacity));
        Assert.AreEqual(Switch2BluetoothInputStartFailure.InvalidArgument,
            excessiveCapacity);
        Assert.AreEqual(0, lease.SubscribeCount);
    }

    [TestMethod]
    public void NotificationIsCopiedBeforeCallbackReturnsAndRoutedByModel()
    {
        foreach (Switch2ControllerModel model in new[]
        {
            Switch2ControllerModel.JoyCon2Left,
            Switch2ControllerModel.JoyCon2Right,
            Switch2ControllerModel.ProController2,
        })
        {
            Switch2BluetoothConnectionAdmission admission = Admission(model,
                (ulong)model + 50);
            var lease = new FakeLease(admission, ExactGatt(admission));
            var sink = new RecordingSink();
            Assert.IsTrue(TryCreateOwner(admission, lease, sink, 4,
                out Switch2BluetoothInputOwner owner, out _));
            byte[] body = Body(0x12345678);
            body[4] = 0xA5;
            lease.Notify(body, 100);
            body[0] = 0;
            body[4] = 0;

            Assert.AreEqual(Switch2BluetoothInputDrainDisposition.Published,
                owner.DrainOne());
            Assert.AreEqual(0x12345678u, sink.LastCounter);
            Assert.AreEqual((byte)0x78, sink.LastRawFirstByte);
            Assert.AreEqual((byte)0xA5, sink.LastRawButtonByte);
            Assert.AreEqual(model == Switch2ControllerModel.ProController2 ?
                1 : 0, sink.ProPublished);
            Assert.AreEqual(model == Switch2ControllerModel.ProController2 ?
                0 : 1, sink.JoyConPublished);
            owner.Stop();
        }
    }

    [TestMethod]
    public void MalformedWrongRouteAndStaleCallbacksCannotReachSession()
    {
        Switch2BluetoothConnectionAdmission admission = Admission(
            Switch2ControllerModel.ProController2, 61);
        var lease = new FakeLease(admission, ExactGatt(admission));
        var sink = new RecordingSink();
        Assert.IsTrue(TryCreateOwner(admission, lease, sink, 4,
            out Switch2BluetoothInputOwner owner, out _));
        lease.Disconnect(TransportGeneration + 1);
        Assert.IsTrue(owner.IsActive,
            "A stale disconnect cannot retire the current generation.");

        for (int length = 0; length <= 64; length++)
        {
            if (length != Switch2InputCodec.BluetoothLeBodyLength)
            {
                lease.Notify(new byte[length], 1);
            }
        }
        lease.Notify(Body(1), 1, TransportGeneration + 1);
        lease.Notify(Body(1), 1, TransportGeneration,
            serviceUuid: Guid.NewGuid());
        lease.Notify(Body(1), 1, TransportGeneration,
            characteristicUuid: Guid.NewGuid());
        lease.Notify(Body(1), -1);
        Assert.AreEqual(0, owner.QueuedCount);
        Assert.AreEqual(68L, owner.RejectedNotificationCount);

        lease.Notify(Body(1), 20);
        Assert.AreEqual(Switch2BluetoothInputDrainDisposition.Published,
            owner.DrainOne());
        lease.Notify(Body(2), 19);
        Assert.AreEqual(Switch2BluetoothInputDrainDisposition.Rejected,
            owner.DrainOne());
        Assert.AreEqual(Switch2InputSessionFailure.TimestampRegression,
            owner.LastSessionFailure);
        Assert.AreEqual(1, sink.ProPublished);
        owner.Stop();
    }

    [TestMethod]
    public void BoundedQueuePreservesOrderWithoutOverwriting()
    {
        Switch2BluetoothConnectionAdmission admission = Admission(
            Switch2ControllerModel.ProController2, 70);
        var lease = new FakeLease(admission, ExactGatt(admission));
        var sink = new RecordingSink(recordCounters: true);
        Assert.IsTrue(TryCreateOwner(admission, lease, sink, 3,
            out Switch2BluetoothInputOwner owner, out _));

        lease.Notify(Body(10), 10);
        lease.Notify(Body(11), 11);
        lease.Notify(Body(12), 12);
        Assert.AreEqual(3, owner.QueuedCount);
        Assert.AreEqual(Switch2BluetoothInputDrainDisposition.Published,
            owner.DrainOne());
        Assert.AreEqual(Switch2BluetoothInputDrainDisposition.Published,
            owner.DrainOne());
        Assert.AreEqual(Switch2BluetoothInputDrainDisposition.Published,
            owner.DrainOne());
        CollectionAssert.AreEqual(new uint[] { 10, 11, 12 },
            sink.Counters.ToArray());
        owner.Stop();
    }

    [TestMethod]
    public void OverflowFailsClosedAndEmitsExactlyOneClear()
    {
        Switch2BluetoothConnectionAdmission admission = Admission(
            Switch2ControllerModel.ProController2, 71);
        var lease = new FakeLease(admission, ExactGatt(admission));
        var sink = new RecordingSink();
        Assert.IsTrue(TryCreateOwner(admission, lease, sink, 2,
            out Switch2BluetoothInputOwner owner, out _));

        lease.Notify(Body(1), 1);
        lease.Notify(Body(2), 2);
        lease.Notify(Body(3), 3);
        Assert.IsFalse(owner.IsActive);
        Assert.AreEqual(0, owner.QueuedCount);
        Assert.AreEqual(1L, owner.OverflowCount);
        Assert.AreEqual(Switch2BluetoothInputEndReason.QueueOverflow,
            owner.EndReason);
        Assert.AreEqual(1, lease.UnsubscribeCount);
        Assert.AreEqual(1, sink.ProCleared);
        Assert.AreEqual(0, sink.ProPublished,
            "Overflow must not publish either an overwritten or partial queue.");

        lease.Notify(Body(4), 4);
        lease.Disconnect();
        Assert.AreEqual(1L, owner.RejectedNotificationCount);
        Assert.AreEqual(1, sink.ProCleared);
        Assert.IsFalse(owner.Stop());
    }

    [TestMethod]
    public void DisconnectInvalidatesBeforeUnsubscribeAndLateCallback()
    {
        Switch2BluetoothConnectionAdmission admission = Admission(
            Switch2ControllerModel.ProController2, 80);
        var lease = new FakeLease(admission, ExactGatt(admission))
        {
            NotifyDuringUnsubscribe = Body(99),
        };
        var sink = new RecordingSink();
        Assert.IsTrue(TryCreateOwner(admission, lease, sink, 4,
            out Switch2BluetoothInputOwner owner, out _));
        lease.Notify(Body(1), 1);
        lease.Disconnect();

        Assert.IsFalse(owner.IsActive);
        Assert.AreEqual(0, owner.QueuedCount);
        Assert.AreEqual(1L, owner.RejectedNotificationCount,
            "The callback invoked by unsubscribe must see a retired generation.");
        Assert.AreEqual(1, lease.UnsubscribeCount);
        Assert.AreEqual(1, sink.ProCleared);
        Assert.AreEqual(0, sink.ProPublished);
        Assert.AreEqual(Switch2BluetoothInputEndReason.Disconnected,
            sink.LastEndReason);
        lease.Disconnect();
        Assert.AreEqual(1, sink.ProCleared);
    }

    [TestMethod]
    public void JoyConDisconnectEmitsExactSideLossOnce()
    {
        foreach ((Switch2ControllerModel model, Switch2StickSide side) in new[]
        {
            (Switch2ControllerModel.JoyCon2Left, Switch2StickSide.Left),
            (Switch2ControllerModel.JoyCon2Right, Switch2StickSide.Right),
        })
        {
            Switch2BluetoothConnectionAdmission admission = Admission(model,
                (ulong)model + 90);
            var lease = new FakeLease(admission, ExactGatt(admission));
            var sink = new RecordingSink();
            Assert.IsTrue(TryCreateOwner(admission, lease, sink, 4,
                out Switch2BluetoothInputOwner owner, out _));
            lease.Disconnect();
            lease.Disconnect();
            Assert.AreEqual(1, sink.JoyConLost);
            Assert.AreEqual(side, sink.LastLostSide);
            Assert.AreEqual(0, sink.ProCleared);
            Assert.IsFalse(owner.Stop());
        }
    }

    [TestMethod]
    public void SubscribeFailureAndInlineDisconnectPublishNothing()
    {
        Switch2BluetoothConnectionAdmission admission = Admission(
            Switch2ControllerModel.ProController2, 100);
        var failed = new FakeLease(admission, ExactGatt(admission))
        {
            SubscribeResult = false,
            NotificationsDuringSubscribe = new[] { Body(7) },
        };
        var sink = new RecordingSink();
        Assert.IsFalse(TryCreateOwner(admission, failed, sink, 4, out _,
            out var failedReason));
        Assert.AreEqual(Switch2BluetoothInputStartFailure.SubscriptionFailed,
            failedReason);
        Assert.AreEqual(1, failed.UnsubscribeCount,
            "A false setup result receives a fail-closed CCCD None attempt.");
        Assert.AreEqual(0, sink.ProCleared);

        Switch2BluetoothConnectionAdmission interruptedAdmission = Admission(
            Switch2ControllerModel.ProController2, 101);
        var interrupted = new FakeLease(interruptedAdmission,
            ExactGatt(interruptedAdmission))
        {
            DisconnectDuringSubscribe = true,
        };
        Assert.IsFalse(TryCreateOwner(interruptedAdmission, interrupted, sink,
            4, out _, out var interruptedReason));
        Assert.AreEqual(
            Switch2BluetoothInputStartFailure.SubscriptionInterrupted,
            interruptedReason);
        Assert.AreEqual(1, interrupted.UnsubscribeCount);
        Assert.AreEqual(0, sink.ProCleared,
            "A lifetime that never became active has nothing to clear.");

        Switch2BluetoothConnectionAdmission throwingAdmission = Admission(
            Switch2ControllerModel.ProController2, 102);
        var throwing = new FakeLease(throwingAdmission,
            ExactGatt(throwingAdmission))
        {
            ThrowDuringSubscribe = true,
        };
        Assert.IsFalse(TryCreateOwner(throwingAdmission, throwing, sink, 4,
            out _, out var throwingReason));
        Assert.AreEqual(Switch2BluetoothInputStartFailure.SubscriptionFailed,
            throwingReason);
        Assert.AreEqual(1, throwing.UnsubscribeCount);
        Assert.AreEqual(0, sink.ProCleared);
    }

    [TestMethod]
    public void InlineNotificationStaysParkedUntilExactCommit()
    {
        Switch2BluetoothConnectionAdmission admission = Admission(
            Switch2ControllerModel.ProController2, 103);
        var lease = new FakeLease(admission, ExactGatt(admission))
        {
            NotificationsDuringSubscribe = new[] { Body(77) },
        };
        var sink = new RecordingSink();

        Assert.IsTrue(TryPrepareOwner(admission, lease, sink, 2,
            out Switch2BluetoothInputOwner owner,
            out Switch2BluetoothInputPrepareCredential credential,
            out var prepareFailure), prepareFailure.ToString());
        Assert.IsTrue(owner.IsPrepared);
        Assert.IsFalse(owner.IsActive);
        Assert.AreEqual(1, owner.QueuedCount);
        Assert.AreEqual(Switch2BluetoothInputDrainDisposition.Inactive,
            owner.DrainOne());
        Assert.AreEqual(1, owner.QueuedCount,
            "A parked drain must not consume the queued notification.");
        Assert.AreEqual(0, sink.ProPublished);

        Assert.IsTrue(owner.TryCommitPrepared(credential,
            out var commitFailure), commitFailure.ToString());
        Assert.IsTrue(owner.IsActive);
        Assert.IsFalse(owner.IsPrepared);
        Assert.AreEqual(Switch2BluetoothInputDrainDisposition.Published,
            owner.DrainOne());
        Assert.AreEqual(77u, sink.LastCounter);
        Assert.IsFalse(owner.TryCommitPrepared(credential,
            out var secondCommit));
        Assert.AreEqual(Switch2BluetoothInputActivationFailure.AlreadyConsumed,
            secondCommit);
        Assert.IsTrue(owner.Stop());
        Assert.AreEqual(1, sink.ProCleared);
        Assert.AreEqual(1, lease.UnsubscribeCount);
    }

    [TestMethod]
    public void PreparedAbortIsSilentSingleUseForEveryPhysicalModel()
    {
        foreach (Switch2ControllerModel model in new[]
        {
            Switch2ControllerModel.JoyCon2Left,
            Switch2ControllerModel.JoyCon2Right,
            Switch2ControllerModel.ProController2,
        })
        {
            Switch2BluetoothConnectionAdmission admission = Admission(model,
                130 + (ulong)model);
            var lease = new FakeLease(admission, ExactGatt(admission));
            var sink = new RecordingSink();
            Assert.IsTrue(TryPrepareOwner(admission, lease, sink, 2,
                out Switch2BluetoothInputOwner owner,
                out Switch2BluetoothInputPrepareCredential credential,
                out var prepareFailure), $"{model}: {prepareFailure}");
            lease.Notify(Body(1), 1);

            Assert.IsTrue(owner.TryAbortPrepared(credential,
                out var abortFailure), $"{model}: {abortFailure}");
            Assert.IsFalse(owner.IsPrepared);
            Assert.IsFalse(owner.IsActive);
            Assert.AreEqual(0, owner.QueuedCount);
            Assert.AreEqual(Switch2BluetoothInputEndReason.ActivationAborted,
                owner.EndReason);
            Assert.AreEqual(1, lease.UnsubscribeCount);
            Assert.AreEqual(0, sink.ProPublished);
            Assert.AreEqual(0, sink.JoyConPublished);
            Assert.AreEqual(0, sink.ProCleared);
            Assert.AreEqual(0, sink.JoyConLost);
            Assert.IsFalse(owner.TryCommitPrepared(credential,
                out var commitAfterAbort));
            Assert.AreEqual(
                Switch2BluetoothInputActivationFailure.AlreadyConsumed,
                commitAfterAbort);
            Assert.IsFalse(owner.TryAbortPrepared(credential,
                out var secondAbort));
            Assert.AreEqual(
                Switch2BluetoothInputActivationFailure.AlreadyConsumed,
                secondAbort);
        }
    }

    [TestMethod]
    public void ForgedCrossOwnerAndStaleCredentialsCannotActivate()
    {
        Switch2BluetoothConnectionAdmission firstAdmission = Admission(
            Switch2ControllerModel.ProController2, 140);
        Switch2BluetoothConnectionAdmission secondAdmission = Admission(
            Switch2ControllerModel.ProController2, 141);
        var firstLease = new FakeLease(firstAdmission,
            ExactGatt(firstAdmission));
        var secondLease = new FakeLease(secondAdmission,
            ExactGatt(secondAdmission));
        Assert.IsTrue(TryPrepareOwner(firstAdmission, firstLease,
            new RecordingSink(), 1, out Switch2BluetoothInputOwner first,
            out Switch2BluetoothInputPrepareCredential firstCredential,
            out _));
        Assert.IsTrue(TryPrepareOwner(secondAdmission, secondLease,
            new RecordingSink(), 1, out Switch2BluetoothInputOwner second,
            out Switch2BluetoothInputPrepareCredential secondCredential,
            out _));

        Assert.IsFalse(first.TryCommitPrepared(default,
            out var defaultFailure));
        Assert.AreEqual(Switch2BluetoothInputActivationFailure.InvalidCredential,
            defaultFailure);
        Assert.IsFalse(first.TryCommitPrepared(secondCredential,
            out var crossOwnerFailure));
        Assert.AreEqual(Switch2BluetoothInputActivationFailure.InvalidCredential,
            crossOwnerFailure);

        var forgedFence = new Switch2BluetoothInputPrepareCredential(first,
            new object(), firstAdmission.ScanGeneration, DeviceGeneration,
            TransportGeneration);
        Assert.IsFalse(first.TryCommitPrepared(forgedFence,
            out var staleFailure));
        Assert.AreEqual(Switch2BluetoothInputActivationFailure.StaleCredential,
            staleFailure);
        Assert.IsTrue(first.IsPrepared,
            "Rejected credentials must not consume the authentic credential.");

        Assert.IsTrue(first.TryAbortPrepared(firstCredential, out _));
        Assert.IsTrue(second.TryAbortPrepared(secondCredential, out _));
        Assert.AreEqual(1, firstLease.UnsubscribeCount);
        Assert.AreEqual(1, secondLease.UnsubscribeCount);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    [DataRow(6)]
    [DataRow(7)]
    [DataRow(8)]
    public void PairPreparePreflightFailureConsumesNeitherAdmission(int scenario)
    {
        Switch2JoyConPairConnectionAdmission pair = PairAdmission(
            1_000UL + (ulong)scenario, out var leftAdmission,
            out var rightAdmission);
        var leftLease = new FakeLease(leftAdmission,
            ExactGatt(leftAdmission));
        var rightLease = new FakeLease(rightAdmission,
            ExactGatt(rightAdmission));
        ISwitch2BluetoothInputLease leftInput = leftLease;
        ISwitch2BluetoothInputLease rightInput = rightLease;
        ISwitch2BluetoothCanonicalInputSink sink = new RecordingSink();
        ulong leftDeviceGeneration = 21;
        ulong leftTransportGeneration = 31;
        ulong rightDeviceGeneration = 22;
        ulong rightTransportGeneration = 32;
        long qpcFrequency = QpcFrequency;
        int leftCapacity = 2;
        int rightCapacity = 2;
        Assert.IsTrue(Switch2InputCalibrationSnapshot.TryCreateFallback(
            Switch2ControllerModel.JoyCon2Left, leftDeviceGeneration,
            out var leftCalibration));
        Assert.IsTrue(Switch2InputCalibrationSnapshot.TryCreateFallback(
            Switch2ControllerModel.JoyCon2Right, rightDeviceGeneration,
            out var rightCalibration));

        switch (scenario)
        {
            case 0:
                sink = null;
                break;
            case 1:
                rightLease = new FakeLease(rightAdmission,
                    new Switch2BluetoothGattSnapshot(
                        rightAdmission.ScanGeneration, 1, 0,
                        Switch2InputCodec.ServiceUuid,
                        Switch2InputCodec.Common05CharacteristicUuid,
                        Switch2GattProperty.Read |
                            Switch2GattProperty.Notify));
                rightInput = rightLease;
                break;
            case 2:
                Switch2BluetoothConnectionAdmission foreignRight = Admission(
                    Switch2ControllerModel.JoyCon2Right,
                    rightAdmission.ScanGeneration);
                rightLease = new FakeLease(foreignRight,
                    ExactGatt(foreignRight));
                rightInput = rightLease;
                break;
            case 3:
                rightDeviceGeneration = 0;
                break;
            case 4:
                rightCalibration = default;
                break;
            case 5:
                rightCapacity = 0;
                break;
            case 6:
                qpcFrequency = 0;
                break;
            case 7:
                rightInput = new NoReleaseProofLease(rightLease);
                break;
            case 8:
                rightInput = leftLease;
                break;
            default:
                Assert.Fail("Unhandled preflight scenario.");
                break;
        }

        Assert.IsFalse(Switch2BluetoothInputOwner.TryPreparePair(pair,
            leftInput, rightInput, sink, leftDeviceGeneration,
            leftTransportGeneration, leftCalibration, leftCapacity,
            rightDeviceGeneration, rightTransportGeneration,
            rightCalibration, rightCapacity, qpcFrequency, out var result));
        Assert.IsFalse(result.IsPrepared);
        Assert.IsFalse(result.AdmissionsConsumedByThisCall);
        Assert.IsFalse(result.CleanupEvidence.IsValid);
        Assert.AreEqual(0, leftLease.SubscribeCount);
        Assert.AreEqual(0, leftLease.UnsubscribeCount);
        Assert.AreEqual(0, rightLease.SubscribeCount);
        Assert.AreEqual(0, rightLease.UnsubscribeCount);
        Assert.IsTrue(pair.TryConsume(out var consumedLeft,
            out var consumedRight),
            "Every preflight rejection must leave both capabilities intact.");
        Assert.AreEqual(leftAdmission, consumedLeft);
        Assert.AreEqual(rightAdmission, consumedRight);
    }

    [TestMethod]
    public void PairPrepareContainsLeaseInspectionFaultBeforeAdmissionConsume()
    {
        Switch2JoyConPairConnectionAdmission pair = PairAdmission(1_020,
            out var leftAdmission, out var rightAdmission);
        var leftLease = new FakeLease(leftAdmission,
            ExactGatt(leftAdmission));
        var rightLease = new FakeLease(rightAdmission,
            ExactGatt(rightAdmission));
        Assert.IsTrue(Switch2InputCalibrationSnapshot.TryCreateFallback(
            Switch2ControllerModel.JoyCon2Left, 21,
            out var leftCalibration));
        Assert.IsTrue(Switch2InputCalibrationSnapshot.TryCreateFallback(
            Switch2ControllerModel.JoyCon2Right, 22,
            out var rightCalibration));

        Assert.IsFalse(Switch2BluetoothInputOwner.TryPreparePair(pair,
            leftLease, new ThrowingInspectionLease(), new RecordingSink(),
            21, 31, leftCalibration, 2, 22, 32, rightCalibration, 2,
            QpcFrequency, out var result));
        Assert.AreEqual(Switch2BluetoothInputPairPrepareFailure.PreflightFailed,
            result.Failure);
        Assert.AreEqual(Switch2BluetoothInputPairSideFailure.
            LeaseInspectionFailed, result.RightFailure);
        Assert.IsFalse(result.AdmissionsConsumedByThisCall);
        Assert.IsTrue(pair.TryConsume(out _, out _));
    }

    [TestMethod]
    public void CopiedPairPrepareAdmissionHasExactlyOnePreparedWinner()
    {
        Switch2JoyConPairConnectionAdmission pair = PairAdmission(1_030,
            out var leftAdmission, out var rightAdmission);
        var leftLease = new FakeLease(leftAdmission,
            ExactGatt(leftAdmission));
        var rightLease = new FakeLease(rightAdmission,
            ExactGatt(rightAdmission));
        var sink = new RecordingSink();
        using var start = new ManualResetEventSlim(false);
        Switch2BluetoothInputPairPrepareResult firstResult = default;
        Switch2BluetoothInputPairPrepareResult secondResult = default;
        Task<bool> first = Task.Run(() =>
        {
            start.Wait();
            return TryPreparePair(pair, leftLease, rightLease, sink,
                out firstResult);
        });
        Task<bool> second = Task.Run(() =>
        {
            start.Wait();
            return TryPreparePair(pair, leftLease, rightLease, sink,
                out secondResult);
        });

        start.Set();
        Assert.IsTrue(Task.WaitAll(new Task[] { first, second },
            TimeSpan.FromSeconds(2)));
        Assert.AreNotEqual(first.Result, second.Result);
        Switch2BluetoothInputPairPrepareResult winner = first.Result ?
            firstResult : secondResult;
        Switch2BluetoothInputPairPrepareResult loser = first.Result ?
            secondResult : firstResult;
        Assert.IsTrue(winner.IsPrepared);
        Assert.IsTrue(winner.AdmissionsConsumedByThisCall);
        Assert.AreEqual(Switch2BluetoothInputPairPrepareFailure.
            AdmissionUnavailable, loser.Failure);
        Assert.IsFalse(loser.AdmissionsConsumedByThisCall);
        Assert.AreEqual(1, leftLease.SubscribeCount);
        Assert.AreEqual(1, rightLease.SubscribeCount);
        Assert.IsTrue(Switch2BluetoothInputOwner.TryAbortPreparedPair(
            winner.LeftOwner, winner.LeftCredential, winner.RightOwner,
            winner.RightCredential, out _));
        Assert.AreEqual(1, leftLease.UnsubscribeCount);
        Assert.AreEqual(1, rightLease.UnsubscribeCount);
        Assert.AreEqual(0, sink.JoyConLost);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public void PairPrepareSubscriptionFailureRetiresBothAndRetainsProofs(
        int scenario)
    {
        Switch2JoyConPairConnectionAdmission pair = PairAdmission(
            1_040UL + (ulong)scenario, out var leftAdmission,
            out var rightAdmission);
        var leftLease = new FakeLease(leftAdmission,
            ExactGatt(leftAdmission))
        {
            SubscribeResult = scenario != 0,
            ThrowDuringSubscribe = scenario == 1,
        };
        var rightLease = new FakeLease(rightAdmission,
            ExactGatt(rightAdmission))
        {
            SubscribeResult = scenario != 2,
            ThrowDuringSubscribe = scenario == 3,
        };
        var sink = new RecordingSink();

        Assert.IsFalse(TryPreparePair(pair, leftLease, rightLease, sink,
            out var result));
        Assert.IsTrue(result.AdmissionsConsumedByThisCall);
        Assert.IsFalse(result.IsPrepared);
        Assert.AreEqual(Switch2BluetoothInputPairPrepareFailure.
            SubscriptionFailed, result.Failure);
        Assert.IsTrue(result.CleanupEvidence.IsValid);
        Assert.AreEqual(1, leftLease.UnsubscribeCount);
        Assert.AreEqual(1, rightLease.UnsubscribeCount,
            "Second-side cleanup is mandatory even if it was not subscribed.");
        Assert.AreEqual(0, sink.JoyConLost);
        if (scenario < 2)
        {
            Assert.AreEqual(1, leftLease.SubscribeCount);
            Assert.AreEqual(0, rightLease.SubscribeCount);
            Assert.AreEqual(Switch2BluetoothInputPairSideFailure.NotAttempted,
                result.RightFailure);
        }
        else
        {
            Assert.AreEqual(1, leftLease.SubscribeCount);
            Assert.AreEqual(1, rightLease.SubscribeCount);
            Assert.AreEqual(Switch2BluetoothInputPairSideFailure.None,
                result.LeftFailure);
        }
        Switch2BluetoothInputPairSideFailure expectedSideFailure =
            scenario is 0 or 2 ?
                Switch2BluetoothInputPairSideFailure.SubscriptionRejected :
                Switch2BluetoothInputPairSideFailure.SubscriptionFaulted;
        Assert.AreEqual(expectedSideFailure, scenario < 2 ?
            result.LeftFailure : result.RightFailure);
        Assert.AreEqual(Switch2BluetoothInputLeaseReleaseResult.Released,
            result.CleanupEvidence.Left.WaitForRelease(100));
        Assert.AreEqual(Switch2BluetoothInputLeaseReleaseResult.Released,
            result.CleanupEvidence.Right.WaitForRelease(100));
        Assert.AreEqual(31UL, leftLease.LastReleaseWaitGeneration);
        Assert.AreEqual(32UL, rightLease.LastReleaseWaitGeneration);
    }

    [TestMethod]
    public void CleanupFaultOnFirstLeaseCannotSuppressSecondLeaseAttempt()
    {
        Switch2JoyConPairConnectionAdmission pair = PairAdmission(1_050,
            out var leftAdmission, out var rightAdmission);
        var leftLease = new FakeLease(leftAdmission,
            ExactGatt(leftAdmission))
        {
            SubscribeResult = false,
            ThrowDuringUnsubscribe = true,
        };
        var rightLease = new FakeLease(rightAdmission,
            ExactGatt(rightAdmission));

        Assert.IsFalse(TryPreparePair(pair, leftLease, rightLease,
            new RecordingSink(), out var result));
        Assert.AreEqual(Switch2BluetoothInputCleanupRequestResult.Faulted,
            result.CleanupEvidence.Left.RequestResult);
        Assert.AreEqual(Switch2BluetoothInputCleanupRequestResult.Rejected,
            result.CleanupEvidence.Right.RequestResult);
        Assert.AreEqual(1, leftLease.UnsubscribeCount);
        Assert.AreEqual(1, rightLease.UnsubscribeCount);
    }

    [TestMethod]
    public void InlineLeftDisconnectBeforeRightRetiresWholePair()
    {
        Switch2JoyConPairConnectionAdmission pair = PairAdmission(
            1_060UL, out var leftAdmission,
            out var rightAdmission);
        var leftLease = new FakeLease(leftAdmission,
            ExactGatt(leftAdmission))
        {
            DisconnectDuringSubscribe = true,
        };
        var rightLease = new FakeLease(rightAdmission,
            ExactGatt(rightAdmission));
        var sink = new RecordingSink();

        Assert.IsFalse(TryPreparePair(pair, leftLease, rightLease, sink,
            out var result, capacity: 1));
        Assert.AreEqual(Switch2BluetoothInputPairPrepareFailure.
            SubscriptionInterrupted, result.Failure);
        Assert.AreEqual(Switch2BluetoothInputPairSideFailure.
            SubscriptionInterrupted, result.LeftFailure);
        Assert.AreEqual(Switch2BluetoothInputPairSideFailure.NotAttempted,
            result.RightFailure);
        Assert.IsTrue(result.AdmissionsConsumedByThisCall);
        Assert.AreEqual(0, rightLease.SubscribeCount);
        Assert.AreEqual(1, leftLease.UnsubscribeCount);
        Assert.AreEqual(1, rightLease.UnsubscribeCount);
        Assert.AreEqual(0, sink.JoyConPublished);
        Assert.AreEqual(0, sink.JoyConLost);
    }

    [TestMethod]
    public void PairPrepareQueuesInlineInputThenSupportsAtomicCommitAndAbort()
    {
        Switch2JoyConPairConnectionAdmission commitPair = PairAdmission(1_070,
            out var commitLeftAdmission, out var commitRightAdmission);
        var commitLeftLease = new FakeLease(commitLeftAdmission,
            ExactGatt(commitLeftAdmission))
        {
            NotificationsDuringSubscribe = new[] { Body(1) },
        };
        var commitRightLease = new FakeLease(commitRightAdmission,
            ExactGatt(commitRightAdmission))
        {
            NotificationsDuringSubscribe = new[] { Body(1) },
        };
        var sink = new RecordingSink();
        Assert.IsTrue(TryPreparePair(commitPair, commitLeftLease,
            commitRightLease, sink, out var prepared));
        Assert.IsTrue(prepared.IsPrepared);
        Assert.IsFalse(prepared.LeftOwner.IsActive);
        Assert.IsFalse(prepared.RightOwner.IsActive);
        Assert.AreEqual(1, prepared.LeftOwner.QueuedCount);
        Assert.AreEqual(1, prepared.RightOwner.QueuedCount);
        Assert.AreEqual(0, sink.JoyConPublished);
        Assert.IsTrue(Switch2BluetoothInputOwner.TryCommitPreparedPair(
            prepared.LeftOwner, prepared.LeftCredential,
            prepared.RightOwner, prepared.RightCredential,
            out var commitFailure), commitFailure.ToString());
        Assert.IsTrue(prepared.LeftOwner.IsActive);
        Assert.IsTrue(prepared.RightOwner.IsActive);
        Assert.AreEqual(Switch2BluetoothInputDrainDisposition.Published,
            prepared.LeftOwner.DrainOne());
        Assert.AreEqual(Switch2BluetoothInputDrainDisposition.Published,
            prepared.RightOwner.DrainOne());
        Assert.AreEqual(2, sink.JoyConPublished);
        Assert.IsTrue(prepared.LeftOwner.Stop());
        Assert.IsTrue(prepared.RightOwner.Stop());

        Switch2JoyConPairConnectionAdmission abortPair = PairAdmission(1_071,
            out var abortLeftAdmission, out var abortRightAdmission);
        var abortLeftLease = new FakeLease(abortLeftAdmission,
            ExactGatt(abortLeftAdmission));
        var abortRightLease = new FakeLease(abortRightAdmission,
            ExactGatt(abortRightAdmission));
        Assert.IsTrue(TryPreparePair(abortPair, abortLeftLease,
            abortRightLease, sink, out var abortPrepared));
        Assert.IsTrue(Switch2BluetoothInputOwner.TryAbortPreparedPair(
            abortPrepared.LeftOwner, abortPrepared.LeftCredential,
            abortPrepared.RightOwner, abortPrepared.RightCredential,
            out var abortFailure), abortFailure.ToString());
        Assert.IsFalse(abortPrepared.LeftOwner.IsActive);
        Assert.IsFalse(abortPrepared.RightOwner.IsActive);
        Assert.AreEqual(1, abortLeftLease.UnsubscribeCount);
        Assert.AreEqual(1, abortRightLease.UnsubscribeCount);
    }

    [TestMethod]
    public void CrossThreadSubscribeCallbacksObserveFullyBoundPair()
    {
        Switch2JoyConPairConnectionAdmission pair = PairAdmission(1_071,
            out var leftAdmission, out var rightAdmission);
        var leftLease = new FakeLease(leftAdmission, ExactGatt(leftAdmission))
        {
            InvokeSubscribeCallbacksOnWorker = true,
            NotificationsDuringSubscribe = new[] { Body(1) },
        };
        var rightLease = new FakeLease(rightAdmission,
            ExactGatt(rightAdmission))
        {
            InvokeSubscribeCallbacksOnWorker = true,
            NotificationsDuringSubscribe = new[] { Body(2) },
        };

        Assert.IsTrue(TryPreparePair(pair, leftLease, rightLease,
            new RecordingSink(), out var prepared));
        Assert.AreEqual(1, prepared.LeftOwner.QueuedCount);
        Assert.AreEqual(1, prepared.RightOwner.QueuedCount);
        Assert.AreEqual(0L, prepared.LeftOwner.RejectedNotificationCount);
        Assert.AreEqual(0L, prepared.RightOwner.RejectedNotificationCount);
        Assert.IsTrue(Switch2BluetoothInputOwner.TryAbortPreparedPair(
            prepared.LeftOwner, prepared.LeftCredential,
            prepared.RightOwner, prepared.RightCredential, out _));
    }

    [TestMethod]
    public void PairFenceRejectsSingleCommitAndCrossPairCredentialMixing()
    {
        Switch2JoyConPairConnectionAdmission firstPair = PairAdmission(1_075,
            out var firstLeftAdmission, out var firstRightAdmission);
        var firstLeftLease = new FakeLease(firstLeftAdmission,
            ExactGatt(firstLeftAdmission));
        var firstRightLease = new FakeLease(firstRightAdmission,
            ExactGatt(firstRightAdmission));
        Assert.IsTrue(TryPreparePair(firstPair, firstLeftLease,
            firstRightLease, new RecordingSink(), out var first));
        Switch2JoyConPairConnectionAdmission secondPair = PairAdmission(1_076,
            out var secondLeftAdmission, out var secondRightAdmission);
        var secondLeftLease = new FakeLease(secondLeftAdmission,
            ExactGatt(secondLeftAdmission));
        var secondRightLease = new FakeLease(secondRightAdmission,
            ExactGatt(secondRightAdmission));
        Assert.IsTrue(TryPreparePair(secondPair, secondLeftLease,
            secondRightLease, new RecordingSink(), out var second));

        Assert.IsFalse(first.LeftOwner.TryCommitPrepared(
            first.LeftCredential, out var singleFailure));
        Assert.AreEqual(Switch2BluetoothInputActivationFailure.
            PairOperationRequired, singleFailure);
        Assert.IsFalse(first.LeftOwner.TryAbortPrepared(
            first.LeftCredential, out var singleAbortFailure));
        Assert.AreEqual(Switch2BluetoothInputActivationFailure.
            PairOperationRequired, singleAbortFailure);
        Assert.IsFalse(Switch2BluetoothInputOwner.TryCommitPreparedPair(
            first.LeftOwner, first.LeftCredential, second.RightOwner,
            second.RightCredential, out var crossPairFailure));
        Assert.AreEqual(Switch2BluetoothInputActivationFailure.
            InvalidCredential, crossPairFailure);
        Assert.IsTrue(first.LeftOwner.IsPrepared);
        Assert.IsTrue(first.RightOwner.IsPrepared);
        Assert.IsTrue(second.LeftOwner.IsPrepared);
        Assert.IsTrue(second.RightOwner.IsPrepared);

        Assert.IsTrue(Switch2BluetoothInputOwner.TryAbortPreparedPair(
            first.LeftOwner, first.LeftCredential, first.RightOwner,
            first.RightCredential, out _));
        Assert.IsTrue(Switch2BluetoothInputOwner.TryAbortPreparedPair(
            second.LeftOwner, second.LeftCredential, second.RightOwner,
            second.RightCredential, out _));
    }

    [TestMethod]
    public void RejectedPairPreflightWarmPathAllocatesZeroBytes()
    {
        Switch2JoyConPairConnectionAdmission pair = PairAdmission(1_080,
            out var leftAdmission, out var rightAdmission);
        var leftLease = new FakeLease(leftAdmission,
            ExactGatt(leftAdmission));
        var rightLease = new FakeLease(rightAdmission,
            ExactGatt(rightAdmission));
        var sink = new RecordingSink();
        Assert.IsTrue(Switch2InputCalibrationSnapshot.TryCreateFallback(
            Switch2ControllerModel.JoyCon2Left, 21,
            out var leftCalibration));
        Assert.IsTrue(Switch2InputCalibrationSnapshot.TryCreateFallback(
            Switch2ControllerModel.JoyCon2Right, 22,
            out var rightCalibration));

        for (int index = 0; index < 1_000; index++)
        {
            Assert.IsFalse(Switch2BluetoothInputOwner.TryPreparePair(pair,
                leftLease, rightLease, sink, 21, 31, leftCalibration, 0,
                22, 32, rightCalibration, 2, QpcFrequency, out _));
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 10_000; index++)
        {
            _ = Switch2BluetoothInputOwner.TryPreparePair(pair, leftLease,
                rightLease, sink, 21, 31, leftCalibration, 0, 22, 32,
                rightCalibration, 2, QpcFrequency, out _);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.AreEqual(0L, allocated);
        Assert.IsTrue(pair.TryConsume(out _, out _));
    }

    [TestMethod]
    public void PairCommitValidatesBothBeforeEitherHalfActivates()
    {
        var sink = new RecordingSink();
        PrepareJoyConPair(150, 2, sink, out FakeLease leftLease,
            out Switch2BluetoothInputOwner left,
            out Switch2BluetoothInputPrepareCredential leftCredential,
            out FakeLease rightLease, out Switch2BluetoothInputOwner right,
            out Switch2BluetoothInputPrepareCredential rightCredential);

        var forgedRight = new Switch2BluetoothInputPrepareCredential(right,
            new object(), rightCredential.ScanGeneration,
            rightCredential.DeviceGeneration,
            rightCredential.TransportGeneration);
        Assert.IsFalse(Switch2BluetoothInputOwner.TryCommitPreparedPair(left,
            leftCredential, right, forgedRight, out var staleFailure));
        Assert.AreEqual(Switch2BluetoothInputActivationFailure.StaleCredential,
            staleFailure);
        Assert.IsTrue(left.IsPrepared);
        Assert.IsTrue(right.IsPrepared);
        Assert.IsFalse(left.IsActive);
        Assert.IsFalse(right.IsActive);

        Switch2BluetoothConnectionAdmission foreignAdmission = Admission(
            Switch2ControllerModel.JoyCon2Right, 152);
        var foreignLease = new FakeLease(foreignAdmission,
            ExactGatt(foreignAdmission));
        Assert.IsTrue(TryPrepareOwner(foreignAdmission, foreignLease, sink, 2,
            out Switch2BluetoothInputOwner foreignRight,
            out Switch2BluetoothInputPrepareCredential foreignCredential,
            out _));
        Assert.IsFalse(Switch2BluetoothInputOwner.TryCommitPreparedPair(left,
            leftCredential, right, foreignCredential, out var crossFailure));
        Assert.AreEqual(Switch2BluetoothInputActivationFailure.InvalidCredential,
            crossFailure);
        Assert.IsTrue(left.IsPrepared);
        Assert.IsTrue(right.IsPrepared);

        Assert.IsFalse(Switch2BluetoothInputOwner.TryCommitPreparedPair(right,
            rightCredential, left, leftCredential, out var reversedFailure));
        Assert.AreEqual(Switch2BluetoothInputActivationFailure.InvalidCredential,
            reversedFailure);
        Assert.IsTrue(left.IsPrepared);
        Assert.IsTrue(right.IsPrepared);

        Assert.IsTrue(Switch2BluetoothInputOwner.TryCommitPreparedPair(left,
            leftCredential, right, rightCredential, out var commitFailure),
            commitFailure.ToString());
        Assert.IsTrue(left.IsActive);
        Assert.IsTrue(right.IsActive);
        Assert.IsTrue(left.ActivationCommitted);
        Assert.IsTrue(right.ActivationCommitted);

        Assert.IsTrue(left.Stop());
        Assert.IsTrue(right.Stop());
        Assert.IsTrue(foreignRight.TryAbortPrepared(foreignCredential, out _));
        Assert.AreEqual(1, leftLease.UnsubscribeCount);
        Assert.AreEqual(1, rightLease.UnsubscribeCount);
        Assert.AreEqual(1, foreignLease.UnsubscribeCount);
        Assert.AreEqual(2, sink.JoyConLost);
    }

    [TestMethod]
    public void PairAbortValidatesBothThenAttemptsBothReleasePaths()
    {
        var sink = new RecordingSink();
        PrepareJoyConPair(180, 2, sink, out FakeLease leftLease,
            out Switch2BluetoothInputOwner left,
            out Switch2BluetoothInputPrepareCredential leftCredential,
            out FakeLease rightLease, out Switch2BluetoothInputOwner right,
            out Switch2BluetoothInputPrepareCredential rightCredential);
        leftLease.ThrowDuringUnsubscribe = true;

        var forgedRight = new Switch2BluetoothInputPrepareCredential(right,
            new object(), rightCredential.ScanGeneration,
            rightCredential.DeviceGeneration,
            rightCredential.TransportGeneration);
        Assert.IsFalse(Switch2BluetoothInputOwner.TryAbortPreparedPair(left,
            leftCredential, right, forgedRight, out var staleFailure));
        Assert.AreEqual(Switch2BluetoothInputActivationFailure.StaleCredential,
            staleFailure);
        Assert.IsTrue(left.IsPrepared);
        Assert.IsTrue(right.IsPrepared);
        Assert.AreEqual(0, leftLease.UnsubscribeCount);
        Assert.AreEqual(0, rightLease.UnsubscribeCount);

        Assert.IsTrue(Switch2BluetoothInputOwner.TryAbortPreparedPair(left,
            leftCredential, right, rightCredential, out var abortFailure),
            abortFailure.ToString());
        Assert.IsFalse(left.IsPrepared);
        Assert.IsFalse(right.IsPrepared);
        Assert.IsFalse(left.IsActive);
        Assert.IsFalse(right.IsActive);
        Assert.AreEqual(Switch2BluetoothInputEndReason.ActivationAborted,
            left.EndReason);
        Assert.AreEqual(Switch2BluetoothInputEndReason.ActivationAborted,
            right.EndReason);
        Assert.AreEqual(1, leftLease.UnsubscribeCount,
            "The throwing left release was attempted once.");
        Assert.AreEqual(1, rightLease.UnsubscribeCount,
            "The right release must still run after the left throws.");
        Assert.AreEqual(0, sink.JoyConLost,
            "Unpublished pair abort must not emit canonical loss.");

        Assert.IsFalse(Switch2BluetoothInputOwner.TryAbortPreparedPair(left,
            leftCredential, right, rightCredential, out var repeatedFailure));
        Assert.AreEqual(Switch2BluetoothInputActivationFailure.AlreadyConsumed,
            repeatedFailure);
    }

    [TestMethod]
    public void ConcurrentPairCommitAndAbortHaveExactlyOneWinner()
    {
        for (int iteration = 0; iteration < 64; iteration++)
        {
            var sink = new RecordingSink();
            PrepareJoyConPair(400 + (ulong)iteration * 2, 2, sink,
                out FakeLease leftLease,
                out Switch2BluetoothInputOwner left,
                out Switch2BluetoothInputPrepareCredential leftCredential,
                out FakeLease rightLease,
                out Switch2BluetoothInputOwner right,
                out Switch2BluetoothInputPrepareCredential rightCredential);
            Switch2BluetoothInputPrepareCredential commitLeft = leftCredential;
            Switch2BluetoothInputPrepareCredential commitRight =
                rightCredential;
            Switch2BluetoothInputPrepareCredential abortLeft = leftCredential;
            Switch2BluetoothInputPrepareCredential abortRight = rightCredential;
            using var start = new ManualResetEventSlim(false);
            Task<(bool Success,
                Switch2BluetoothInputActivationFailure Failure)> commit =
                Task.Run(() =>
                {
                    start.Wait();
                    bool success = Switch2BluetoothInputOwner.
                        TryCommitPreparedPair(left, commitLeft, right,
                            commitRight, out var failure);
                    return (success, failure);
                });
            Task<(bool Success,
                Switch2BluetoothInputActivationFailure Failure)> abort =
                Task.Run(() =>
                {
                    start.Wait();
                    bool success = Switch2BluetoothInputOwner.
                        TryAbortPreparedPair(left, abortLeft, right,
                            abortRight, out var failure);
                    return (success, failure);
                });

            start.Set();
            Assert.IsTrue(Task.WaitAll(new Task[] { commit, abort },
                TimeSpan.FromSeconds(2)), $"iteration {iteration}");
            Assert.AreNotEqual(commit.Result.Success, abort.Result.Success,
                $"iteration {iteration}");
            if (commit.Result.Success)
            {
                Assert.AreEqual(
                    Switch2BluetoothInputActivationFailure.AlreadyConsumed,
                    abort.Result.Failure);
                Assert.IsTrue(left.IsActive);
                Assert.IsTrue(right.IsActive);
                Assert.IsTrue(left.Stop());
                Assert.IsTrue(right.Stop());
                Assert.AreEqual(2, sink.JoyConLost);
            }
            else
            {
                Assert.AreEqual(
                    Switch2BluetoothInputActivationFailure.AlreadyConsumed,
                    commit.Result.Failure);
                Assert.IsFalse(left.IsActive);
                Assert.IsFalse(right.IsActive);
                Assert.AreEqual(0, sink.JoyConLost);
            }
            Assert.AreEqual(1, leftLease.UnsubscribeCount);
            Assert.AreEqual(1, rightLease.UnsubscribeCount);
        }
    }

    [TestMethod]
    public void PairCommitAfterPrecommitDisconnectNeverActivatesSurvivingHalf()
    {
        for (int mode = 0; mode < 2; mode++)
        {
            for (int lostSide = 0; lostSide < 2; lostSide++)
            {
                var sink = new RecordingSink();
                ulong scan = 600 + (ulong)(mode * 4 + lostSide * 2);
                PrepareJoyConPair(scan, 1, sink,
                    out FakeLease leftLease,
                    out Switch2BluetoothInputOwner left,
                    out Switch2BluetoothInputPrepareCredential leftCredential,
                    out FakeLease rightLease,
                    out Switch2BluetoothInputOwner right,
                    out Switch2BluetoothInputPrepareCredential rightCredential);
                FakeLease lostLease = lostSide == 0 ? leftLease : rightLease;
                if (mode == 0)
                {
                    lostLease.Disconnect();
                }
                else
                {
                    lostLease.Notify(Body(1), 1);
                    lostLease.Notify(Body(2), 2);
                    Assert.IsTrue(left.IsPrepared && right.IsPrepared);
                    lostLease.Disconnect();
                }

                Assert.IsFalse(Switch2BluetoothInputOwner.
                    TryCommitPreparedPair(left, leftCredential, right,
                        rightCredential, out var failure));
                Assert.AreEqual(
                    Switch2BluetoothInputActivationFailure.AlreadyConsumed,
                    failure);
                Assert.IsFalse(left.IsPrepared);
                Assert.IsFalse(right.IsPrepared);
                Assert.IsFalse(left.IsActive);
                Assert.IsFalse(right.IsActive);
                Assert.AreEqual(0, sink.JoyConLost);

                Assert.IsFalse(Switch2BluetoothInputOwner.
                    TryAbortPreparedPair(left, leftCredential, right,
                        rightCredential, out var pairAbortFailure));
                Assert.AreEqual(
                    Switch2BluetoothInputActivationFailure.AlreadyConsumed,
                    pairAbortFailure);
                Assert.IsFalse(left.TryAbortPrepared(leftCredential,
                    out var singleLeftAbort));
                Assert.AreEqual(Switch2BluetoothInputActivationFailure.
                    PairOperationRequired, singleLeftAbort);
                Assert.IsFalse(right.TryAbortPrepared(rightCredential,
                    out var singleRightAbort));
                Assert.AreEqual(Switch2BluetoothInputActivationFailure.
                    PairOperationRequired, singleRightAbort);
                Assert.AreEqual(1, leftLease.UnsubscribeCount);
                Assert.AreEqual(1, rightLease.UnsubscribeCount);
            }
        }
    }

    [TestMethod]
    public void PairCommitReleasesInlineCallbacksOnlyAfterBothHalvesAreActive()
    {
        Switch2JoyConPairConnectionAdmission pair = PairAdmission(700,
            out var leftAdmission, out var rightAdmission);
        var leftLease = new FakeLease(leftAdmission, ExactGatt(leftAdmission))
        {
            NotificationsDuringSubscribe = new[] { Body(11) },
        };
        var rightLease = new FakeLease(rightAdmission, ExactGatt(rightAdmission))
        {
            NotificationsDuringSubscribe = new[] { Body(12) },
        };
        var sink = new PairActivationObservingSink();
        Assert.IsTrue(TryPreparePair(pair, leftLease, rightLease, sink,
            out var prepared));
        Switch2BluetoothInputOwner left = prepared.LeftOwner;
        Switch2BluetoothInputPrepareCredential leftCredential =
            prepared.LeftCredential;
        Switch2BluetoothInputOwner right = prepared.RightOwner;
        Switch2BluetoothInputPrepareCredential rightCredential =
            prepared.RightCredential;
        sink.Left = left;
        sink.Right = right;
        Assert.AreEqual(1, left.QueuedCount);
        Assert.AreEqual(1, right.QueuedCount);
        Assert.IsTrue(Switch2BluetoothInputDrainPump.TryCreate(left,
            out var leftPump, out _));
        Assert.IsTrue(Switch2BluetoothInputDrainPump.TryCreate(right,
            out var rightPump, out _));
        Assert.IsTrue(leftPump.TryStartParked(1_000, out _));
        Assert.IsTrue(rightPump.TryStartParked(1_000, out _));

        Assert.IsTrue(Switch2BluetoothInputOwner.TryCommitPreparedPair(left,
            leftCredential, right, rightCredential, out var failure),
            failure.ToString());
        Assert.IsTrue(SpinWait.SpinUntil(() => sink.Published == 2, 1_000));
        Assert.AreEqual(0, sink.PartialActivationObservations);
        Assert.IsTrue(left.IsActive);
        Assert.IsTrue(right.IsActive);

        Assert.IsTrue(left.Stop());
        Assert.IsTrue(right.Stop());
        Assert.IsTrue(leftPump.TryStopAndJoin(1_000, out _));
        Assert.IsTrue(rightPump.TryStopAndJoin(1_000, out _));
        Assert.AreEqual(1, leftLease.UnsubscribeCount);
        Assert.AreEqual(1, rightLease.UnsubscribeCount);
    }

    [TestMethod]
    public void DisconnectAfterUnpublishedBurstRetiresWithoutCanonicalLoss()
    {
        foreach (Switch2ControllerModel model in new[]
        {
            Switch2ControllerModel.JoyCon2Left,
            Switch2ControllerModel.JoyCon2Right,
            Switch2ControllerModel.ProController2,
        })
        {
            for (int mode = 0; mode < 2; mode++)
            {
                Switch2BluetoothConnectionAdmission admission = Admission(
                    model, 160 + (ulong)model * 10 + (ulong)mode);
                var lease = new FakeLease(admission, ExactGatt(admission));
                var sink = new RecordingSink();
                Assert.IsTrue(TryPrepareOwner(admission, lease, sink, 1,
                    out Switch2BluetoothInputOwner owner,
                    out Switch2BluetoothInputPrepareCredential credential,
                    out _));

                if (mode == 0)
                {
                    lease.Disconnect();
                    Assert.AreEqual(
                        Switch2BluetoothInputEndReason.Disconnected,
                        owner.EndReason);
                }
                else
                {
                    lease.Notify(Body(1), 1);
                    lease.Notify(Body(2), 2);
                    Assert.IsTrue(owner.IsPrepared);
                    Assert.AreEqual(0L, owner.OverflowCount);
                    lease.Disconnect();
                    Assert.AreEqual(
                        Switch2BluetoothInputEndReason.Disconnected,
                        owner.EndReason);
                }

                Assert.IsFalse(owner.IsActive);
                Assert.IsFalse(owner.IsPrepared);
                Assert.AreEqual(0, owner.QueuedCount);
                Assert.AreEqual(1, lease.UnsubscribeCount);
                Assert.AreEqual(0, sink.ProPublished);
                Assert.AreEqual(0, sink.JoyConPublished);
                Assert.AreEqual(0, sink.ProCleared);
                Assert.AreEqual(0, sink.JoyConLost);
                Assert.IsFalse(owner.TryCommitPrepared(credential,
                    out var commitFailure));
                Assert.AreEqual(
                    Switch2BluetoothInputActivationFailure.AlreadyConsumed,
                    commitFailure);
            }
        }
    }

    [TestMethod]
    public void InlineBurstRetainsLatestStateAndAbortsWithoutCanonicalLoss()
    {
        Switch2BluetoothConnectionAdmission admission = Admission(
            Switch2ControllerModel.ProController2, 200);
        var lease = new FakeLease(admission, ExactGatt(admission))
        {
            NotificationsDuringSubscribe = new[] { Body(1), Body(2) },
        };
        var sink = new RecordingSink();

        Assert.IsTrue(TryPrepareOwner(admission, lease, sink, 1, out var owner,
            out var credential, out var failure));
        Assert.AreEqual(
            Switch2BluetoothInputStartFailure.None,
            failure);
        Assert.IsTrue(owner.IsPrepared);
        Assert.AreEqual(1, owner.QueuedCount);
        Assert.AreEqual(0L, owner.OverflowCount);
        Assert.IsTrue(owner.TryAbortPrepared(credential, out _));
        Assert.AreEqual(1, lease.UnsubscribeCount);
        Assert.AreEqual(0, sink.ProPublished);
        Assert.AreEqual(0, sink.ProCleared);
        Assert.AreEqual(0, sink.JoyConLost);
    }

    [TestMethod]
    public void ConcurrentCommitAndAbortHaveExactlyOneWinner()
    {
        for (int iteration = 0; iteration < 64; iteration++)
        {
            Switch2BluetoothConnectionAdmission admission = Admission(
                Switch2ControllerModel.ProController2,
                300 + (ulong)iteration);
            var lease = new FakeLease(admission, ExactGatt(admission));
            var sink = new RecordingSink();
            Assert.IsTrue(TryPrepareOwner(admission, lease, sink, 1,
                out Switch2BluetoothInputOwner owner,
                out Switch2BluetoothInputPrepareCredential credential,
                out _));
            Switch2BluetoothInputPrepareCredential commitCredential =
                credential;
            Switch2BluetoothInputPrepareCredential abortCredential =
                credential;
            using var start = new ManualResetEventSlim(false);
            Task<(bool Success,
                Switch2BluetoothInputActivationFailure Failure)> commit =
                Task.Run(() =>
                {
                    start.Wait();
                    bool success = owner.TryCommitPrepared(commitCredential,
                        out var failure);
                    return (success, failure);
                });
            Task<(bool Success,
                Switch2BluetoothInputActivationFailure Failure)> abort =
                Task.Run(() =>
                {
                    start.Wait();
                    bool success = owner.TryAbortPrepared(abortCredential,
                        out var failure);
                    return (success, failure);
                });

            start.Set();
            Assert.IsTrue(Task.WaitAll(new Task[] { commit, abort },
                TimeSpan.FromSeconds(2)));
            Assert.AreNotEqual(commit.Result.Success, abort.Result.Success,
                $"iteration {iteration}");
            if (commit.Result.Success)
            {
                Assert.AreEqual(
                    Switch2BluetoothInputActivationFailure.AlreadyConsumed,
                    abort.Result.Failure);
                Assert.IsTrue(owner.IsActive);
                Assert.IsTrue(owner.Stop());
                Assert.AreEqual(1, sink.ProCleared);
            }
            else
            {
                Assert.AreEqual(
                    Switch2BluetoothInputActivationFailure.AlreadyConsumed,
                    commit.Result.Failure);
                Assert.IsFalse(owner.IsActive);
                Assert.AreEqual(0, sink.ProCleared);
            }
            Assert.AreEqual(1, lease.UnsubscribeCount);
        }
    }

    [TestMethod]
    public void ConcurrentCallbacksAndDisconnectCannotPublishAfterClear()
    {
        Switch2BluetoothConnectionAdmission admission = Admission(
            Switch2ControllerModel.ProController2, 110);
        var lease = new FakeLease(admission, ExactGatt(admission));
        var sink = new RecordingSink();
        Assert.IsTrue(TryCreateOwner(admission, lease, sink,
            Switch2BluetoothInputOwner.MaximumQueueCapacity,
            out Switch2BluetoothInputOwner owner, out _));
        byte[] body = Body(1);

        Parallel.Invoke(
            () =>
            {
                for (int index = 0; index < 2_000; index++)
                {
                    lease.Notify(body, index);
                }
            },
            () =>
            {
                for (int index = 0; index < 2_000; index++)
                {
                    owner.DrainOne();
                }
            },
            () =>
            {
                for (int index = 0; index < 64; index++)
                {
                    lease.Disconnect();
                }
            });

        Assert.IsFalse(owner.IsActive);
        Assert.AreEqual(1, sink.ProCleared);
        Assert.AreEqual(0, sink.PublishedAfterClear);
        Assert.AreEqual(1, lease.UnsubscribeCount);
    }

    [TestMethod]
    public void ReentrantStopDefersClearUntilSelectedPublicationReturns()
    {
        Switch2BluetoothConnectionAdmission admission = Admission(
            Switch2ControllerModel.ProController2, 115);
        var lease = new FakeLease(admission, ExactGatt(admission));
        var sink = new ReentrantStopSink();
        Assert.IsTrue(TryCreateOwner(admission, lease, sink, 2,
            out Switch2BluetoothInputOwner owner, out _));
        sink.Owner = owner;
        lease.Notify(Body(1), 1);

        Task<Switch2BluetoothInputDrainDisposition> drain = Task.Run(
            owner.DrainOne);
        Assert.IsTrue(drain.Wait(TimeSpan.FromSeconds(2)),
            "A sink which stops its owner must not deadlock DrainOne.");
        Assert.AreEqual(Switch2BluetoothInputDrainDisposition.Published,
            drain.Result);
        Assert.IsTrue(sink.StopAccepted);
        Assert.AreEqual(1, sink.Published);
        Assert.AreEqual(1, sink.Cleared);
        Assert.IsFalse(sink.ClearObservedInsidePublish);
        Assert.AreEqual(1, lease.UnsubscribeCount);
    }

    [TestMethod]
    public void ConcurrentDrainIsBusyAndDisconnectClearWaitsForPublication()
    {
        Switch2BluetoothConnectionAdmission admission = Admission(
            Switch2ControllerModel.ProController2, 116);
        var lease = new FakeLease(admission, ExactGatt(admission));
        using var sink = new BlockingPublicationSink();
        Assert.IsTrue(TryCreateOwner(admission, lease, sink, 2,
            out Switch2BluetoothInputOwner owner, out _));
        lease.Notify(Body(1), 1);
        lease.Notify(Body(2), 2);

        Task<Switch2BluetoothInputDrainDisposition> first = Task.Run(
            owner.DrainOne);
        Assert.IsTrue(sink.WaitUntilEntered(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(Switch2BluetoothInputDrainDisposition.Busy,
            owner.DrainOne());
        lease.Disconnect();
        Assert.AreEqual(0, sink.Cleared,
            "Clear must follow, never overtake, selected publication.");

        sink.Release();
        Assert.IsTrue(first.Wait(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(Switch2BluetoothInputDrainDisposition.Published,
            first.Result);
        Assert.AreEqual(1, sink.Published);
        Assert.AreEqual(1, sink.Cleared);
        Assert.AreEqual(0, sink.PublishedAfterClear);
        Assert.AreEqual(0, owner.QueuedCount);
    }

    [TestMethod]
    public void ThrowingPublicationRetiresGenerationFailClosed()
    {
        Switch2BluetoothConnectionAdmission admission = Admission(
            Switch2ControllerModel.ProController2, 117);
        var lease = new FakeLease(admission, ExactGatt(admission));
        var sink = new ThrowingPublicationSink
        {
            ThrowOnClear = true,
        };
        Assert.IsTrue(TryCreateOwner(admission, lease, sink, 1,
            out Switch2BluetoothInputOwner owner, out _));
        lease.Notify(Body(1), 1);

        Assert.AreEqual(Switch2BluetoothInputDrainDisposition.Rejected,
            owner.DrainOne());
        Assert.IsFalse(owner.IsActive);
        Assert.AreEqual(Switch2BluetoothInputEndReason.SinkFailure,
            owner.EndReason);
        Assert.AreEqual(1, sink.ClearAttempts);
        Assert.AreEqual(1L, owner.RetirementCallbackFailureCount);
        Assert.AreEqual(1, lease.UnsubscribeCount);
    }

    [TestMethod]
    public void WarmNotificationAndDrainPathAllocatesNothing()
    {
        Switch2BluetoothConnectionAdmission admission = Admission(
            Switch2ControllerModel.ProController2, 120);
        var lease = new FakeLease(admission, ExactGatt(admission));
        var sink = new CountingSink();
        Assert.IsTrue(TryCreateOwner(admission, lease, sink, 1,
            out Switch2BluetoothInputOwner owner, out _));
        byte[] body = Body(0);
        for (int index = 0; index < 256; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(body, (uint)index);
            lease.Notify(body, index);
            owner.DrainOne();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 20_000; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(body,
                (uint)(index + 256));
            lease.Notify(body, index + 256);
            owner.DrainOne();
        }
        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.AreEqual(0L, after - before);
        Assert.AreEqual(20_256, sink.Published);
        owner.Stop();
    }

    [TestMethod]
    public void DrainPumpParksLatestInitialStateThenDeliversActiveInputInFifoOrder()
    {
        Switch2BluetoothConnectionAdmission admission = Admission(
            Switch2ControllerModel.ProController2, 121);
        var lease = new FakeLease(admission, ExactGatt(admission))
        {
            NotificationsDuringSubscribe = new[]
            {
                Body(1), Body(2), Body(3),
            },
        };
        var sink = new RecordingSink(recordCounters: true);
        Assert.IsTrue(TryPrepareOwner(admission, lease, sink, 32,
            out var owner, out var credential, out _));
        Assert.IsTrue(Switch2BluetoothInputDrainPump.TryCreate(owner,
            out var pump, out var createFailure), createFailure.ToString());
        int attention = 0;
        Assert.IsTrue(pump.TrySetLifecycleAttentionHandler(_ => attention++));
        Assert.IsTrue(pump.TryStartParked(1_000, out var startFailure),
            startFailure.ToString());

        Assert.AreEqual(Switch2BluetoothInputDrainPumpState.Parked,
            pump.State);
        Assert.AreEqual(1, owner.QueuedCount);
        Assert.AreEqual(0, sink.ProPublished,
            "A proven worker park cannot drain a Prepared owner.");
        Assert.IsTrue(owner.TryCommitPrepared(credential, out var activation),
            activation.ToString());
        Assert.IsTrue(SpinWait.SpinUntil(() => pump.PublishedCount == 1,
            1_000));

        for (uint counter = 4; counter <= 20; counter++)
        {
            lease.Notify(Body(counter), counter);
        }
        Assert.IsTrue(SpinWait.SpinUntil(() => pump.PublishedCount == 18,
            1_000));
        Assert.IsTrue(pump.TryStopAndJoin(1_000, out var stopFailure),
            stopFailure.ToString());
        CollectionAssert.AreEqual(Enumerable.Range(3, 18)
            .Select(value => (uint)value).ToArray(), sink.Counters.ToArray());
        Assert.AreEqual(18L, owner.PublishedCount);
        Assert.AreEqual(1, sink.ProCleared);
        Assert.AreEqual(0, sink.PublishedAfterClear);
        Assert.AreEqual(0, attention,
            "An explicit stop is not lifecycle-attention evidence.");
    }

    [TestMethod]
    public void WaitingPumpHasNoLostWakeAndRejectsStaleCallbacks()
    {
        Switch2BluetoothConnectionAdmission admission = Admission(
            Switch2ControllerModel.ProController2, 122);
        var lease = new FakeLease(admission, ExactGatt(admission));
        var sink = new RecordingSink(recordCounters: true);
        Assert.IsTrue(TryPrepareOwner(admission, lease, sink, 2,
            out var owner, out var credential, out _));
        Assert.IsTrue(Switch2BluetoothInputDrainPump.TryCreate(owner,
            out var pump, out _));
        Switch2BluetoothInputDrainPumpAttention attention = null;
        Assert.IsTrue(pump.TrySetLifecycleAttentionHandler(
            evidence => attention = evidence));
        Assert.IsTrue(pump.TryStartParked(1_000, out _));
        Assert.IsTrue(owner.TryCommitPrepared(credential, out _));
        Assert.IsTrue(SpinWait.SpinUntil(() => pump.State ==
            Switch2BluetoothInputDrainPumpState.Running, 1_000));

        lease.Notify(Body(99), 1, generation: TransportGeneration + 1);
        lease.Notify(Body(99), 1,
            characteristicUuid: Switch2InputCodec.
                ProController2_09CharacteristicUuid);
        Assert.AreEqual(2L, owner.RejectedNotificationCount);
        Assert.AreEqual(0L, pump.PublishedCount);

        for (uint counter = 1; counter <= 100; counter++)
        {
            lease.Notify(Body(counter), counter);
            long expected = counter;
            Assert.IsTrue(SpinWait.SpinUntil(() =>
                pump.PublishedCount == expected, 1_000),
                $"Lost wake at counter {counter}.");
        }

        lease.Disconnect();
        Assert.IsTrue(pump.TryStopAndJoin(1_000, out var stopFailure),
            stopFailure.ToString());
        lease.Notify(Body(101), 101);
        Assert.AreEqual(100, sink.ProPublished);
        Assert.AreEqual(1, sink.ProCleared);
        Assert.AreEqual(0, sink.PublishedAfterClear);
        Assert.IsNotNull(attention);
        Assert.AreEqual(DeviceGeneration, attention.DeviceGeneration);
        Assert.AreEqual(TransportGeneration, attention.TransportGeneration);
        Assert.AreEqual(Switch2BluetoothInputEndReason.Disconnected,
            attention.EndReason);
        Assert.AreEqual(Switch2BluetoothInputDrainPumpAttentionKind.
            OwnerRetired, attention.Kind);
    }

    [TestMethod]
    public void PreparedAbortDisconnectAndBurstStopJoinWithoutClear()
    {
        // Exact abort.
        Switch2BluetoothConnectionAdmission admission = Admission(
            Switch2ControllerModel.ProController2, 123);
        var lease = new FakeLease(admission, ExactGatt(admission));
        var sink = new RecordingSink();
        Assert.IsTrue(TryPrepareOwner(admission, lease, sink, 2,
            out var owner, out var credential, out _));
        Assert.IsTrue(Switch2BluetoothInputDrainPump.TryCreate(owner,
            out var pump, out _));
        int attention = 0;
        Assert.IsTrue(pump.TrySetLifecycleAttentionHandler(_ => attention++));
        Assert.IsTrue(pump.TryStartParked(1_000, out _));
        Assert.IsTrue(owner.TryAbortPrepared(credential, out _));
        Assert.IsTrue(pump.TryStopAndJoin(1_000, out _));
        Assert.AreEqual(0, sink.ProCleared);
        Assert.AreEqual(0, attention);

        // Prepared disconnect.
        admission = Admission(Switch2ControllerModel.ProController2, 124);
        lease = new FakeLease(admission, ExactGatt(admission));
        sink = new RecordingSink();
        Assert.IsTrue(TryPrepareOwner(admission, lease, sink, 2,
            out owner, out _, out _));
        Assert.IsTrue(Switch2BluetoothInputDrainPump.TryCreate(owner,
            out pump, out _));
        Assert.IsTrue(pump.TrySetLifecycleAttentionHandler(_ => attention++));
        Assert.IsTrue(pump.TryStartParked(1_000, out _));
        lease.Disconnect();
        Assert.IsTrue(pump.TryStopAndJoin(1_000, out _));
        Assert.AreEqual(0, sink.ProCleared);
        Assert.AreEqual(0, attention);

        // A prepared burst remains parked; explicit stop still drains silently.
        admission = Admission(Switch2ControllerModel.ProController2, 125);
        lease = new FakeLease(admission, ExactGatt(admission));
        sink = new RecordingSink();
        Assert.IsTrue(TryPrepareOwner(admission, lease, sink, 1,
            out owner, out credential, out _));
        Assert.IsTrue(Switch2BluetoothInputDrainPump.TryCreate(owner,
            out pump, out _));
        Assert.IsTrue(pump.TrySetLifecycleAttentionHandler(_ => attention++));
        Assert.IsTrue(pump.TryStartParked(1_000, out _));
        lease.Notify(Body(1), 1);
        lease.Notify(Body(2), 2);
        Assert.IsTrue(owner.IsPrepared);
        Assert.AreEqual(0L, owner.OverflowCount);
        Assert.IsTrue(owner.TryAbortPrepared(credential, out _));
        Assert.IsTrue(pump.TryStopAndJoin(1_000, out _));
        Assert.AreEqual(Switch2BluetoothInputEndReason.ActivationAborted,
            owner.EndReason);
        Assert.AreEqual(0, sink.ProCleared);
        Assert.AreEqual(0, attention,
            "No Prepared lifetime can manufacture lifecycle attention.");
    }

    [TestMethod]
    public void BlockedPublicationStopTimesOutQuarantinesAndRetryJoins()
    {
        Switch2BluetoothConnectionAdmission admission = Admission(
            Switch2ControllerModel.ProController2, 126);
        var lease = new FakeLease(admission, ExactGatt(admission));
        using var sink = new BlockingPublicationSink();
        Assert.IsTrue(TryPrepareOwner(admission, lease, sink, 2,
            out var owner, out var credential, out _));
        Assert.IsTrue(Switch2BluetoothInputDrainPump.TryCreate(owner,
            out var pump, out _));
        Assert.IsTrue(pump.TryStartParked(1_000, out _));
        Assert.IsTrue(owner.TryCommitPrepared(credential, out _));
        lease.Notify(Body(1), 1);
        Assert.IsTrue(sink.WaitUntilEntered(TimeSpan.FromSeconds(1)));

        Assert.IsFalse(pump.TryStopAndJoin(10, out var timeout));
        Assert.AreEqual(Switch2BluetoothInputDrainPumpFailure.
            WorkerExitTimedOut, timeout);
        Assert.IsTrue(pump.RequiresQuarantine,
            "An unjoined worker must remain retained as ambiguous teardown.");
        sink.Release();
        Assert.IsTrue(SpinWait.SpinUntil(() => owner.DrainPumpExited, 1_000));
        Assert.IsTrue(pump.RequiresQuarantine,
            "The worker cannot clear a prior timeout without an actual join.");
        Assert.AreEqual(Switch2BluetoothInputDrainPumpState.Quarantined,
            pump.State);
        Assert.IsTrue(pump.TryStopAndJoin(1_000, out var retry),
            retry.ToString());
        Assert.IsFalse(pump.RequiresQuarantine);
        Assert.AreEqual(1, sink.Published);
        Assert.AreEqual(1, sink.Cleared);
        Assert.AreEqual(0, sink.PublishedAfterClear);
        Assert.AreEqual(Switch2BluetoothInputEndReason.Stopped,
            owner.EndReason);
    }

    [TestMethod]
    public void BlockedDisconnectAndOverflowPreserveTerminalOrderingAndAttention()
    {
        // Disconnect during publication.
        Switch2BluetoothConnectionAdmission admission = Admission(
            Switch2ControllerModel.ProController2, 127);
        var lease = new FakeLease(admission, ExactGatt(admission));
        using (var sink = new BlockingPublicationSink())
        {
            Assert.IsTrue(TryPrepareOwner(admission, lease, sink, 2,
                out var owner, out var credential, out _));
            Assert.IsTrue(Switch2BluetoothInputDrainPump.TryCreate(owner,
                out var pump, out _));
            Switch2BluetoothInputDrainPumpAttention attention = null;
            Assert.IsTrue(pump.TrySetLifecycleAttentionHandler(
                evidence => attention = evidence));
            Assert.IsTrue(pump.TryStartParked(1_000, out _));
            Assert.IsTrue(owner.TryCommitPrepared(credential, out _));
            lease.Notify(Body(1), 1);
            Assert.IsTrue(sink.WaitUntilEntered(TimeSpan.FromSeconds(1)));
            lease.Disconnect();
            sink.Release();
            Assert.IsTrue(pump.TryStopAndJoin(1_000, out _));
            Assert.AreEqual(1, sink.Published);
            Assert.AreEqual(1, sink.Cleared);
            Assert.AreEqual(0, sink.PublishedAfterClear);
            Assert.IsNotNull(attention);
            Assert.AreEqual(Switch2BluetoothInputEndReason.Disconnected,
                attention.EndReason);
        }

        // Overflow while one publication is blocked.
        admission = Admission(Switch2ControllerModel.ProController2, 128);
        lease = new FakeLease(admission, ExactGatt(admission));
        using (var sink = new BlockingPublicationSink())
        {
            Assert.IsTrue(TryPrepareOwner(admission, lease, sink, 1,
                out var owner, out var credential, out _));
            Assert.IsTrue(Switch2BluetoothInputDrainPump.TryCreate(owner,
                out var pump, out _));
            Switch2BluetoothInputDrainPumpAttention attention = null;
            Assert.IsTrue(pump.TrySetLifecycleAttentionHandler(
                evidence => attention = evidence));
            Assert.IsTrue(pump.TryStartParked(1_000, out _));
            Assert.IsTrue(owner.TryCommitPrepared(credential, out _));
            lease.Notify(Body(1), 1);
            Assert.IsTrue(sink.WaitUntilEntered(TimeSpan.FromSeconds(1)));
            lease.Notify(Body(2), 2);
            lease.Notify(Body(3), 3);
            sink.Release();
            Assert.IsTrue(pump.TryStopAndJoin(1_000, out _));
            Assert.AreEqual(1, sink.Published);
            Assert.AreEqual(1, sink.Cleared);
            Assert.AreEqual(0, sink.PublishedAfterClear);
            Assert.IsNotNull(attention);
            Assert.AreEqual(Switch2BluetoothInputEndReason.QueueOverflow,
                attention.EndReason);
        }
    }

    [TestMethod]
    public void BlockingAttentionCannotMasqueradeAsJoinedWorkerExit()
    {
        Switch2BluetoothConnectionAdmission admission = Admission(
            Switch2ControllerModel.ProController2, 137);
        var lease = new FakeLease(admission, ExactGatt(admission));
        var sink = new RecordingSink();
        using var attentionEntered = new ManualResetEventSlim();
        using var releaseAttention = new ManualResetEventSlim();
        Assert.IsTrue(TryPrepareOwner(admission, lease, sink, 2,
            out var owner, out var credential, out _));
        Assert.IsTrue(Switch2BluetoothInputDrainPump.TryCreate(owner,
            out var pump, out _));
        Assert.IsTrue(pump.TrySetLifecycleAttentionHandler(_ =>
        {
            attentionEntered.Set();
            releaseAttention.Wait();
        }));
        Assert.IsTrue(pump.TryStartParked(1_000, out _));
        Assert.IsTrue(owner.TryCommitPrepared(credential, out _));

        lease.Disconnect();
        Assert.IsTrue(attentionEntered.Wait(TimeSpan.FromSeconds(1)));
        Assert.IsFalse(pump.TryStopAndJoin(10, out var timeout));
        Assert.AreEqual(Switch2BluetoothInputDrainPumpFailure.
            WorkerExitTimedOut, timeout);
        Assert.IsTrue(pump.RequiresQuarantine);
        Assert.IsFalse(owner.DrainPumpExited,
            "The owner fence cannot report exit while attention still runs.");

        releaseAttention.Set();
        Assert.IsTrue(SpinWait.SpinUntil(() => owner.DrainPumpExited, 1_000));
        Assert.IsTrue(pump.RequiresQuarantine,
            "Returning from attention is not a retrying control-thread join.");
        Assert.IsTrue(pump.TryStopAndJoin(1_000, out var retry),
            retry.ToString());
        Assert.IsTrue(owner.DrainPumpExited);
        Assert.IsFalse(pump.RequiresQuarantine);
        Assert.AreEqual(1, sink.ProCleared);
        Assert.AreEqual(0, sink.PublishedAfterClear);
    }

    [TestMethod]
    public void ThrowingAttentionIsObservableAndCannotPreventWorkerExit()
    {
        Switch2BluetoothConnectionAdmission admission = Admission(
            Switch2ControllerModel.ProController2, 138);
        var lease = new FakeLease(admission, ExactGatt(admission));
        var sink = new RecordingSink();
        Assert.IsTrue(TryPrepareOwner(admission, lease, sink, 2,
            out var owner, out var credential, out _));
        Assert.IsTrue(Switch2BluetoothInputDrainPump.TryCreate(owner,
            out var pump, out _));
        Assert.IsTrue(pump.TrySetLifecycleAttentionHandler(
            static _ => throw new InvalidOperationException(
                "Synthetic attention failure.")));
        Assert.IsTrue(pump.TryStartParked(1_000, out _));
        Assert.IsTrue(owner.TryCommitPrepared(credential, out _));

        lease.Disconnect();
        Assert.IsTrue(SpinWait.SpinUntil(() => owner.DrainPumpExited, 1_000));
        Assert.AreEqual(1L, pump.LifecycleAttentionFailureCount);
        Assert.IsTrue(pump.TryStopAndJoin(1_000, out var stopFailure),
            stopFailure.ToString());
        Assert.AreEqual(1, sink.ProCleared);
        Assert.AreEqual(Switch2BluetoothInputEndReason.Disconnected,
            owner.EndReason);
    }

    [TestMethod]
    public void SinkAndUnexpectedWorkerFailuresRaisePreallocatedAttention()
    {
        Switch2BluetoothConnectionAdmission admission = Admission(
            Switch2ControllerModel.ProController2, 129);
        var lease = new FakeLease(admission, ExactGatt(admission));
        var throwing = new ThrowingPublicationSink();
        Assert.IsTrue(TryPrepareOwner(admission, lease, throwing, 2,
            out var owner, out var credential, out _));
        Assert.IsTrue(Switch2BluetoothInputDrainPump.TryCreate(owner,
            out var pump, out _));
        Switch2BluetoothInputDrainPumpAttention attention = null;
        Assert.IsTrue(pump.TrySetLifecycleAttentionHandler(
            evidence => attention = evidence));
        Assert.IsTrue(pump.TryStartParked(1_000, out _));
        Assert.IsTrue(owner.TryCommitPrepared(credential, out _));
        lease.Notify(Body(1), 1);
        Assert.IsTrue(SpinWait.SpinUntil(() => pump.State ==
            Switch2BluetoothInputDrainPumpState.Stopped, 1_000));
        Assert.IsTrue(pump.TryStopAndJoin(1_000, out _));
        Assert.AreEqual(Switch2BluetoothInputDrainPumpFailure.SinkRejected,
            pump.TerminalFailure);
        Assert.IsNotNull(attention);
        Assert.AreEqual(Switch2BluetoothInputEndReason.SinkFailure,
            attention.EndReason);
        Assert.AreEqual(Switch2BluetoothInputDrainPumpFailure.SinkRejected,
            attention.PumpFailure);

        admission = Admission(Switch2ControllerModel.ProController2, 130);
        lease = new FakeLease(admission, ExactGatt(admission));
        var sink = new RecordingSink();
        Assert.IsTrue(TryPrepareOwner(admission, lease, sink, 2,
            out owner, out credential, out _));
        Assert.IsTrue(Switch2BluetoothInputDrainPump.TryCreateCore(owner,
            static thread => thread.Start(), beforeWorkerPark: null,
            afterActivation: () => throw new InvalidOperationException(
                "Synthetic worker failure."), out pump, out _));
        attention = null;
        Assert.IsTrue(pump.TrySetLifecycleAttentionHandler(
            evidence => attention = evidence));
        Assert.IsTrue(pump.TryStartParked(1_000, out _));
        Assert.IsTrue(owner.TryCommitPrepared(credential, out _));
        Assert.IsTrue(SpinWait.SpinUntil(() => pump.State ==
            Switch2BluetoothInputDrainPumpState.Stopped, 1_000));
        Assert.IsTrue(pump.TryStopAndJoin(1_000, out _));
        Assert.AreEqual(Switch2BluetoothInputDrainPumpFailure.
            UnexpectedWorkerFailure, pump.TerminalFailure);
        Assert.IsNotNull(attention);
        Assert.AreEqual(Switch2BluetoothInputDrainPumpAttentionKind.
            UnexpectedWorkerFailure, attention.Kind);
        Assert.AreEqual(Switch2BluetoothInputEndReason.Stopped,
            attention.EndReason);
        Assert.AreEqual(1, sink.ProCleared);
    }

    [TestMethod]
    public void StartParkPreparedStopAndSelfJoinFailuresRemainFailClosed()
    {
        // Worker start rejection leaves exact abort authority intact.
        Switch2BluetoothConnectionAdmission admission = Admission(
            Switch2ControllerModel.ProController2, 131);
        var lease = new FakeLease(admission, ExactGatt(admission));
        var sink = new RecordingSink();
        Assert.IsTrue(TryPrepareOwner(admission, lease, sink, 2,
            out var owner, out var credential, out _));
        Assert.IsTrue(Switch2BluetoothInputDrainPump.TryCreateCore(owner,
            _ => throw new InvalidOperationException("start failed"),
            beforeWorkerPark: null, afterActivation: null,
            out var pump, out _));
        Assert.IsFalse(pump.TryStartParked(1_000, out var startFailure));
        Assert.AreEqual(Switch2BluetoothInputDrainPumpFailure.
            WorkerStartRejected, startFailure);
        Assert.IsFalse(pump.TrySetLifecycleAttentionHandler(_ => { }),
            "An attempted start permanently closes the pre-start handler seam.");
        Assert.IsFalse(owner.TryCommitPrepared(credential, out _));
        Assert.IsFalse(pump.TryStopAndJoin(10, out var preparedStop));
        Assert.AreEqual(Switch2BluetoothInputDrainPumpFailure.InvalidState,
            preparedStop,
            "Generic stop cannot strand a still-Prepared owner.");
        Assert.IsTrue(owner.TryAbortPrepared(credential, out _));
        Assert.IsTrue(pump.TryStopAndJoin(1_000, out _));

        // Park timeout retains the still-running worker until a proven join.
        admission = Admission(Switch2ControllerModel.ProController2, 132);
        lease = new FakeLease(admission, ExactGatt(admission));
        sink = new RecordingSink();
        Assert.IsTrue(TryPrepareOwner(admission, lease, sink, 2,
            out owner, out credential, out _));
        var parkEntered = new ManualResetEventSlim();
        var releasePark = new ManualResetEventSlim();
        Assert.IsTrue(Switch2BluetoothInputDrainPump.TryCreateCore(owner,
            static thread => thread.Start(), () =>
            {
                parkEntered.Set();
                releasePark.Wait();
            }, afterActivation: null, out pump, out _));
        Assert.IsFalse(pump.TryStartParked(10, out var parkFailure));
        Assert.AreEqual(Switch2BluetoothInputDrainPumpFailure.
            WorkerParkTimedOut, parkFailure);
        Assert.IsTrue(parkEntered.IsSet);
        Assert.IsTrue(pump.RequiresQuarantine);
        Assert.IsFalse(owner.TryCommitPrepared(credential, out _));
        Assert.IsTrue(owner.TryAbortPrepared(credential, out _));
        releasePark.Set();
        Assert.IsTrue(SpinWait.SpinUntil(() => owner.DrainPumpExited, 1_000));
        Assert.IsTrue(pump.RequiresQuarantine,
            "A late park exit cannot self-clear a timed-out start.");
        Assert.IsTrue(pump.TryStopAndJoin(1_000, out _));
        parkEntered.Dispose();
        releasePark.Dispose();

        // A report callback running on the worker cannot join that worker.
        admission = Admission(Switch2ControllerModel.ProController2, 133);
        lease = new FakeLease(admission, ExactGatt(admission));
        var selfJoin = new SelfJoinSink();
        Assert.IsTrue(TryPrepareOwner(admission, lease, selfJoin, 2,
            out owner, out credential, out _));
        Assert.IsTrue(Switch2BluetoothInputDrainPump.TryCreate(owner,
            out pump, out _));
        selfJoin.Pump = pump;
        Assert.IsTrue(pump.TryStartParked(1_000, out _));
        Assert.IsTrue(owner.TryCommitPrepared(credential, out _));
        lease.Notify(Body(1), 1);
        Assert.IsTrue(SpinWait.SpinUntil(() => pump.PublishedCount == 1,
            1_000));
        Assert.IsFalse(selfJoin.JoinAccepted);
        Assert.AreEqual(Switch2BluetoothInputDrainPumpFailure.SelfJoinRejected,
            selfJoin.JoinFailure);
        Assert.IsTrue(pump.TryStopAndJoin(1_000, out _));
    }

    [TestMethod]
    public void NeverStartedPumpNeedsExactAbortAndThenMarksOwnerExited()
    {
        Switch2BluetoothConnectionAdmission admission = Admission(
            Switch2ControllerModel.ProController2, 134);
        var lease = new FakeLease(admission, ExactGatt(admission));
        Assert.IsTrue(TryPrepareOwner(admission, lease, new RecordingSink(), 2,
            out var owner, out var credential, out _));
        Assert.IsTrue(Switch2BluetoothInputDrainPump.TryCreate(owner,
            out var pump, out _));
        Assert.IsFalse(pump.TryStopAndJoin(10, out var prepared));
        Assert.AreEqual(Switch2BluetoothInputDrainPumpFailure.InvalidState,
            prepared);
        Assert.IsTrue(owner.TryAbortPrepared(credential, out _));
        Assert.IsTrue(pump.TryStopAndJoin(1_000, out _));
        Assert.AreEqual(Switch2BluetoothInputDrainPumpState.Stopped,
            pump.State);
        Assert.IsTrue(owner.DrainPumpExited);
        Assert.IsFalse(pump.RequiresQuarantine);
    }

    [TestMethod]
    public void HostileOwnerWaitRejectionRaisesExactWorkerFailureAttention()
    {
        Switch2BluetoothConnectionAdmission admission = Admission(
            Switch2ControllerModel.ProController2, 136);
        var lease = new FakeLease(admission, ExactGatt(admission));
        var sink = new RecordingSink();
        Assert.IsTrue(TryPrepareOwner(admission, lease, sink, 2,
            out var owner, out var credential, out _));
        object exactFence = null;
        Assert.IsTrue(Switch2BluetoothInputDrainPump.TryCreateCore(owner,
            static thread => thread.Start(), beforeWorkerPark: null,
            afterActivation: () =>
            {
                Assert.IsTrue(owner.TryMarkDrainPumpExited(exactFence));
            }, out var pump, out _));
        exactFence = typeof(Switch2BluetoothInputDrainPump).GetField(
            "ownerFence", BindingFlags.Instance | BindingFlags.NonPublic)
            .GetValue(pump);
        Switch2BluetoothInputDrainPumpAttention attention = null;
        Assert.IsTrue(pump.TrySetLifecycleAttentionHandler(
            evidence => attention = evidence));
        Assert.IsTrue(pump.TryStartParked(1_000, out _));
        Assert.IsFalse(pump.TrySetLifecycleAttentionHandler(_ => { }),
            "Lifecycle attention is install-once and pre-start only.");
        Assert.IsTrue(owner.TryCommitPrepared(credential, out _));
        Assert.IsTrue(SpinWait.SpinUntil(() => pump.State ==
            Switch2BluetoothInputDrainPumpState.Stopped, 1_000));
        Assert.IsTrue(pump.TryStopAndJoin(1_000, out _));
        Assert.AreEqual(Switch2BluetoothInputDrainPumpFailure.
            OwnerWaitRejected, pump.TerminalFailure);
        Assert.IsNotNull(attention);
        Assert.AreEqual(Switch2BluetoothInputDrainPumpAttentionKind.
            WorkerFailure, attention.Kind);
        Assert.AreEqual(Switch2BluetoothInputDrainPumpFailure.
            OwnerWaitRejected, attention.PumpFailure);
        Assert.AreEqual(DeviceGeneration, attention.DeviceGeneration);
        Assert.AreEqual(TransportGeneration, attention.TransportGeneration);
        Assert.AreEqual(Switch2BluetoothInputEndReason.Stopped,
            attention.EndReason);
        Assert.AreEqual(1, sink.ProCleared);
    }

    [TestMethod]
    public void WarmDrainPumpPathAllocatesNothingOnWorkerThread()
    {
        const int warmup = 1_000;
        const int measured = 10_000;
        Switch2BluetoothConnectionAdmission admission = Admission(
            Switch2ControllerModel.ProController2, 135);
        var lease = new FakeLease(admission, ExactGatt(admission));
        var sink = new WorkerAllocationSink(warmup, measured);
        Assert.IsTrue(TryPrepareOwner(admission, lease, sink, 64,
            out var owner, out var credential, out _));
        Assert.IsTrue(Switch2BluetoothInputDrainPump.TryCreate(owner,
            out var pump, out _));
        Assert.IsTrue(pump.TryStartParked(1_000, out _));
        Assert.IsTrue(owner.TryCommitPrepared(credential, out _));

        byte[] body = Body(0);
        int total = warmup + measured;
        var producerBackpressure = new SpinWait();
        for (int index = 0; index < total; index++)
        {
            while (owner.QueuedCount >= 48)
            {
                producerBackpressure.SpinOnce();
            }
            BinaryPrimitives.WriteUInt32LittleEndian(body, (uint)index);
            lease.Notify(body, index);
        }
        Assert.IsTrue(SpinWait.SpinUntil(() =>
            pump.PublishedCount == total, 5_000));

        Assert.IsTrue(pump.TryStopAndJoin(1_000, out _));
        Assert.AreEqual(0L, sink.AllocatedBytes);
        Assert.AreEqual(total, sink.Published);
    }

    [TestMethod]
    public void CommitNotificationAndDisconnectRacesRepeatWithoutLatePublish()
    {
        for (int iteration = 0; iteration < 16; iteration++)
        {
            Switch2BluetoothConnectionAdmission admission = Admission(
                Switch2ControllerModel.ProController2,
                (ulong)(140 + iteration));
            var lease = new FakeLease(admission, ExactGatt(admission));
            var sink = new RecordingSink();
            Assert.IsTrue(TryPrepareOwner(admission, lease, sink, 4,
                out var owner, out var credential, out _));
            Assert.IsTrue(Switch2BluetoothInputDrainPump.TryCreate(owner,
                out var pump, out _));
            Assert.IsTrue(pump.TryStartParked(1_000, out _));
            var release = new ManualResetEventSlim();
            Task<bool> commit = Task.Run(() =>
            {
                release.Wait();
                return owner.TryCommitPrepared(credential, out _);
            });
            Task notify = Task.Run(() =>
            {
                release.Wait();
                lease.Notify(Body(1), 1);
            });
            release.Set();
            Assert.IsTrue(commit.GetAwaiter().GetResult());
            notify.GetAwaiter().GetResult();
            Assert.IsTrue(SpinWait.SpinUntil(() => pump.PublishedCount == 1,
                1_000));

            Task late = Task.Run(() => lease.Notify(Body(2), 2));
            Task disconnect = Task.Run(() => lease.Disconnect());
            Task.WaitAll(late, disconnect);
            Assert.IsTrue(pump.TryStopAndJoin(1_000, out _));
            Assert.AreEqual(1, sink.ProCleared);
            Assert.AreEqual(0, sink.PublishedAfterClear);
            Assert.IsTrue(sink.ProPublished is 1 or 2);
            release.Dispose();
        }
    }

    private static bool TryPreparePair(
        in Switch2JoyConPairConnectionAdmission pair,
        FakeLease leftLease, FakeLease rightLease,
        ISwitch2BluetoothCanonicalInputSink sink,
        out Switch2BluetoothInputPairPrepareResult result,
        int capacity = 2)
    {
        Assert.IsTrue(Switch2InputCalibrationSnapshot.TryCreateFallback(
            Switch2ControllerModel.JoyCon2Left, 21,
            out var leftCalibration));
        Assert.IsTrue(Switch2InputCalibrationSnapshot.TryCreateFallback(
            Switch2ControllerModel.JoyCon2Right, 22,
            out var rightCalibration));
        return Switch2BluetoothInputOwner.TryPreparePair(pair, leftLease,
            rightLease, sink, 21, 31, leftCalibration, capacity, 22, 32,
            rightCalibration, capacity, QpcFrequency, out result);
    }

    private static Switch2JoyConPairConnectionAdmission PairAdmission(
        ulong scanGeneration,
        out Switch2BluetoothConnectionAdmission leftAdmission,
        out Switch2BluetoothConnectionAdmission rightAdmission)
    {
        leftAdmission = Admission(Switch2ControllerModel.JoyCon2Left,
            scanGeneration);
        rightAdmission = Admission(Switch2ControllerModel.JoyCon2Right,
            scanGeneration);
        byte[] leftIdentity = Enumerable.Range(20, 16)
            .Select(value => (byte)value).ToArray();
        byte[] rightIdentity = Enumerable.Range(80, 16)
            .Select(value => (byte)value).ToArray();
        Assert.IsTrue(Switch2PersistentPeerId.TryDerive(SessionKey,
            leftIdentity, Switch2ControllerModel.JoyCon2Left,
            Switch2AdvertisementCodec.JoyCon2LeftProductId,
            out var leftPeer));
        Assert.IsTrue(Switch2PersistentPeerId.TryDerive(SessionKey,
            rightIdentity, Switch2ControllerModel.JoyCon2Right,
            Switch2AdvertisementCodec.JoyCon2RightProductId,
            out var rightPeer));
        var pairIdBytes = new byte[Switch2JoyConPairId.EncodedLength];
        pairIdBytes[0] = 1;
        Assert.IsTrue(Switch2JoyConPairId.TryRead(pairIdBytes,
            out var pairId));
        Assert.IsTrue(Switch2JoyConPairRecord.TryCreate(1, pairId, leftPeer,
            rightPeer, out var record));
        Assert.IsTrue(Switch2JoyConPairConnectionAdmission.TryCreate(record,
            leftPeer, leftAdmission, rightPeer, rightAdmission,
            out var pair));
        return pair;
    }

    private static bool TryCreateOwner(
        in Switch2BluetoothConnectionAdmission admission, FakeLease lease,
        ISwitch2BluetoothCanonicalInputSink sink, int capacity,
        out Switch2BluetoothInputOwner owner,
        out Switch2BluetoothInputStartFailure failure)
    {
        Assert.IsTrue(Switch2InputCalibrationSnapshot.TryCreateFallback(
            admission.Model, DeviceGeneration,
            out Switch2InputCalibrationSnapshot calibration));
        return Switch2BluetoothInputOwner.TryCreate(admission, lease, sink,
            DeviceGeneration, TransportGeneration, QpcFrequency, calibration,
            capacity, out owner, out failure);
    }

    private static bool TryPrepareOwner(
        in Switch2BluetoothConnectionAdmission admission, FakeLease lease,
        ISwitch2BluetoothCanonicalInputSink sink, int capacity,
        out Switch2BluetoothInputOwner owner,
        out Switch2BluetoothInputPrepareCredential credential,
        out Switch2BluetoothInputStartFailure failure)
    {
        Assert.IsTrue(Switch2InputCalibrationSnapshot.TryCreateFallback(
            admission.Model, DeviceGeneration,
            out Switch2InputCalibrationSnapshot calibration));
        return Switch2BluetoothInputOwner.TryPrepare(admission, lease, sink,
            DeviceGeneration, TransportGeneration, QpcFrequency, calibration,
            capacity, out owner, out credential, out failure);
    }

    private static void PrepareJoyConPair(ulong firstScanGeneration,
        int capacity, ISwitch2BluetoothCanonicalInputSink sink,
        out FakeLease leftLease, out Switch2BluetoothInputOwner left,
        out Switch2BluetoothInputPrepareCredential leftCredential,
        out FakeLease rightLease, out Switch2BluetoothInputOwner right,
        out Switch2BluetoothInputPrepareCredential rightCredential)
    {
        Switch2JoyConPairConnectionAdmission pair = PairAdmission(
            firstScanGeneration, out var leftAdmission,
            out var rightAdmission);
        leftLease = new FakeLease(leftAdmission, ExactGatt(leftAdmission));
        rightLease = new FakeLease(rightAdmission, ExactGatt(rightAdmission));
        Assert.IsTrue(TryPreparePair(pair, leftLease, rightLease, sink,
            out var prepared, capacity), prepared.Failure.ToString());
        left = prepared.LeftOwner;
        leftCredential = prepared.LeftCredential;
        right = prepared.RightOwner;
        rightCredential = prepared.RightCredential;
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
            Switch2ControllerModel.ProController2 =>
                Switch2AdvertisementCodec.ProController2ProductId,
            _ => throw new ArgumentOutOfRangeException(nameof(model)),
        };
        var registry = new Switch2BluetoothCandidateRegistry();
        Assert.IsTrue(registry.TryBeginScan(scanGeneration));
        Switch2BluetoothPeerToken token = Token(scanGeneration,
            (ulong)model + 1);
        var advertisement = new Switch2Advertisement(model, productId, false,
            Switch2AdvertisedHost.ThisHost);
        Switch2BluetoothCandidateObservation observation = registry.Observe(
            scanGeneration, token, 1, advertisement);
        Assert.IsTrue(registry.TryCreateRememberedConnectionAdmission(
            observation, out Switch2BluetoothConnectionAdmission admission));
        return admission;
    }

    private static Switch2BluetoothGattSnapshot ExactGatt(
        in Switch2BluetoothConnectionAdmission admission) => new(
            admission.ScanGeneration, 1, 1, Switch2InputCodec.ServiceUuid,
            Switch2InputCodec.Common05CharacteristicUuid,
            Switch2GattProperty.Read | Switch2GattProperty.Notify);

    private static Switch2BluetoothPeerToken Token(ulong scanGeneration,
        ulong address)
    {
        Assert.IsTrue(Switch2BluetoothPeerToken.TryDerive(SessionKey,
            scanGeneration, address,
            out Switch2BluetoothPeerToken token));
        return token;
    }

    private static void AssertNoPeerIdentityMaterial(Type type)
    {
        const BindingFlags flags = BindingFlags.Instance |
            BindingFlags.Public | BindingFlags.NonPublic;
        MemberInfo[] members = type.GetMembers(flags);

        Assert.IsFalse(type.GetFields(flags).Any(field =>
                field.FieldType == typeof(Switch2BluetoothPeerToken)),
            $"{type.Name} must not retain a scan peer token.");
        Assert.IsFalse(members.Any(member =>
                member.Name.Contains("PeerToken", StringComparison.Ordinal) ||
                member.Name.Contains("BluetoothAddress",
                    StringComparison.Ordinal) ||
                member.Name.Contains("SessionKey", StringComparison.Ordinal)),
            $"{type.Name} must not expose or retain peer address/key material.");
    }

    private static byte[] Body(uint counter)
    {
        byte[] body = new byte[Switch2InputCodec.BluetoothLeBodyLength];
        BinaryPrimitives.WriteUInt32LittleEndian(body, counter);
        return body;
    }

    private sealed class NoReleaseProofLease : ISwitch2BluetoothInputLease
    {
        private readonly ISwitch2BluetoothInputLease inner;

        internal NoReleaseProofLease(ISwitch2BluetoothInputLease inner)
        {
            this.inner = inner;
        }

        public Switch2BluetoothConnectionAdmission Admission =>
            inner.Admission;

        public Switch2BluetoothGattSnapshot GattSnapshot =>
            inner.GattSnapshot;

        public bool TrySubscribeCccdNotify(ulong transportGeneration,
            Switch2BluetoothInputNotification notification,
            Switch2BluetoothInputDisconnected disconnected) =>
            inner.TrySubscribeCccdNotify(transportGeneration, notification,
                disconnected);

        public bool TryUnsubscribeCccdNone(ulong transportGeneration) =>
            inner.TryUnsubscribeCccdNone(transportGeneration);
    }

    private sealed class ThrowingInspectionLease :
        ISwitch2BluetoothInputLease,
        ISwitch2BluetoothInputLeaseReleaseProof
    {
        public Switch2BluetoothConnectionAdmission Admission =>
            throw new InvalidOperationException("Synthetic admission fault.");

        public Switch2BluetoothGattSnapshot GattSnapshot => default;

        public bool TrySubscribeCccdNotify(ulong transportGeneration,
            Switch2BluetoothInputNotification notification,
            Switch2BluetoothInputDisconnected disconnected) =>
            throw new InvalidOperationException();

        public bool TryUnsubscribeCccdNone(ulong transportGeneration) =>
            throw new InvalidOperationException();

        public Switch2BluetoothInputLeaseReleaseResult WaitForRelease(
            ulong transportGeneration, int timeoutMilliseconds) =>
            Switch2BluetoothInputLeaseReleaseResult.Rejected;
    }

    private sealed class FakeLease : ISwitch2BluetoothInputLease,
        ISwitch2BluetoothInputLeaseReleaseProof
    {
        private Switch2BluetoothInputNotification notification;
        private Switch2BluetoothInputDisconnected disconnected;
        private ulong subscribedGeneration;

        internal FakeLease(in Switch2BluetoothConnectionAdmission admission,
            in Switch2BluetoothGattSnapshot gattSnapshot)
        {
            Admission = admission;
            GattSnapshot = gattSnapshot;
        }

        public Switch2BluetoothConnectionAdmission Admission { get; }

        public Switch2BluetoothGattSnapshot GattSnapshot { get; }

        internal bool SubscribeResult { get; init; } = true;

        internal bool DisconnectDuringSubscribe { get; init; }

        internal bool InvokeSubscribeCallbacksOnWorker { get; init; }

        internal bool ThrowDuringSubscribe { get; init; }

        internal bool ThrowDuringUnsubscribe { get; set; }

        internal byte[][] NotificationsDuringSubscribe { get; init; } =
            Array.Empty<byte[]>();

        internal byte[] NotifyDuringUnsubscribe { get; init; }

        internal int SubscribeCount { get; private set; }

        internal int UnsubscribeCount { get; private set; }

        internal Switch2BluetoothInputLeaseReleaseResult ReleaseResult
        {
            get;
            init;
        } = Switch2BluetoothInputLeaseReleaseResult.Released;

        internal int ReleaseWaitCount { get; private set; }

        internal ulong LastReleaseWaitGeneration { get; private set; }

        public bool TrySubscribeCccdNotify(ulong transportGeneration,
            Switch2BluetoothInputNotification notification,
            Switch2BluetoothInputDisconnected disconnected)
        {
            SubscribeCount++;
            subscribedGeneration = transportGeneration;
            this.notification = notification;
            this.disconnected = disconnected;
            void InvokeCallbacks()
            {
                for (int index = 0;
                     index < NotificationsDuringSubscribe.Length; index++)
                {
                    notification(transportGeneration,
                        Switch2InputCodec.ServiceUuid,
                        Switch2InputCodec.Common05CharacteristicUuid,
                        NotificationsDuringSubscribe[index], index + 1);
                }
                if (DisconnectDuringSubscribe)
                {
                    disconnected(transportGeneration);
                }
            }
            if (InvokeSubscribeCallbacksOnWorker)
            {
                Task.Run(InvokeCallbacks).GetAwaiter().GetResult();
            }
            else
            {
                InvokeCallbacks();
            }
            if (ThrowDuringSubscribe)
            {
                throw new InvalidOperationException("Synthetic setup fault.");
            }
            return SubscribeResult;
        }

        public bool TryUnsubscribeCccdNone(ulong transportGeneration)
        {
            UnsubscribeCount++;
            if (ThrowDuringUnsubscribe)
            {
                throw new InvalidOperationException(
                    "Synthetic unsubscribe fault.");
            }
            if (NotifyDuringUnsubscribe is not null)
            {
                notification(transportGeneration, Switch2InputCodec.ServiceUuid,
                    Switch2InputCodec.Common05CharacteristicUuid,
                    NotifyDuringUnsubscribe, 500);
            }
            return transportGeneration == subscribedGeneration;
        }

        public Switch2BluetoothInputLeaseReleaseResult WaitForRelease(
            ulong transportGeneration, int timeoutMilliseconds)
        {
            ReleaseWaitCount++;
            LastReleaseWaitGeneration = transportGeneration;
            return timeoutMilliseconds < 0 ?
                Switch2BluetoothInputLeaseReleaseResult.Invalid :
                ReleaseResult;
        }

        internal void Notify(byte[] body, long qpc,
            ulong? generation = null,
            Guid? serviceUuid = null, Guid? characteristicUuid = null) =>
            notification?.Invoke(generation ?? subscribedGeneration,
                serviceUuid ?? Switch2InputCodec.ServiceUuid,
                characteristicUuid ??
                    Switch2InputCodec.Common05CharacteristicUuid, body, qpc);

        internal void Disconnect(ulong? generation = null) =>
            disconnected?.Invoke(generation ?? subscribedGeneration);
    }

    private sealed class PairActivationObservingSink :
        ISwitch2BluetoothCanonicalInputSink
    {
        private int published;
        private int partialActivationObservations;

        internal Switch2BluetoothInputOwner Left { get; set; }

        internal Switch2BluetoothInputOwner Right { get; set; }

        internal int Published => Volatile.Read(ref published);

        internal int PartialActivationObservations => Volatile.Read(
            ref partialActivationObservations);

        public void PublishPro(in Switch2CanonicalInputFrame frame) =>
            throw new InvalidOperationException();

        public void PublishJoyCon(in Switch2CanonicalInputFrame frame)
        {
            if (Left == null || Right == null || !Left.IsActive ||
                !Right.IsActive)
            {
                Interlocked.Increment(ref partialActivationObservations);
            }
            Interlocked.Increment(ref published);
        }

        public void ClearPro(ulong deviceGeneration,
            ulong transportGeneration, Switch2BluetoothInputEndReason reason) =>
            throw new InvalidOperationException();

        public void LoseJoyConHalf(Switch2StickSide side,
            ulong deviceGeneration, ulong transportGeneration,
            Switch2BluetoothInputEndReason reason)
        {
        }
    }

    private class RecordingSink : ISwitch2BluetoothCanonicalInputSink
    {
        public bool IsVirtualOutputTransitionActive { get; set; }
        private readonly object sync = new();
        private readonly bool recordCounters;
        private bool cleared;

        internal RecordingSink(bool recordCounters = false)
        {
            this.recordCounters = recordCounters;
        }

        internal int ProPublished { get; private set; }
        internal int JoyConPublished { get; private set; }
        internal int ProCleared { get; private set; }
        internal int JoyConLost { get; private set; }
        internal int PublishedAfterClear { get; private set; }
        internal uint LastCounter { get; private set; }
        internal byte LastRawFirstByte { get; private set; }
        internal byte LastRawButtonByte { get; private set; }
        internal Switch2StickSide LastLostSide { get; private set; }
        internal Switch2BluetoothInputEndReason LastEndReason { get; private set; }
        internal List<uint> Counters { get; } = new();

        public void PublishPro(in Switch2CanonicalInputFrame frame)
        {
            lock (sync)
            {
                ProPublished++;
                Capture(frame);
            }
        }

        public void PublishJoyCon(in Switch2CanonicalInputFrame frame)
        {
            lock (sync)
            {
                JoyConPublished++;
                Capture(frame);
            }
        }

        public void ClearPro(ulong deviceGeneration,
            ulong transportGeneration, Switch2BluetoothInputEndReason reason)
        {
            lock (sync)
            {
                ProCleared++;
                cleared = true;
                LastEndReason = reason;
            }
        }

        public void LoseJoyConHalf(Switch2StickSide side,
            ulong deviceGeneration, ulong transportGeneration,
            Switch2BluetoothInputEndReason reason)
        {
            lock (sync)
            {
                JoyConLost++;
                LastLostSide = side;
                cleared = true;
                LastEndReason = reason;
            }
        }

        private void Capture(in Switch2CanonicalInputFrame frame)
        {
            if (cleared)
            {
                PublishedAfterClear++;
            }
            LastCounter = frame.DeviceCounterRaw;
            LastRawFirstByte = frame.RawBody[0];
            LastRawButtonByte = frame.RawBody[4];
            if (recordCounters)
            {
                Counters.Add(frame.DeviceCounterRaw);
            }
        }
    }

    private sealed class CountingSink :
        ISwitch2BluetoothCanonicalInputSink
    {
        internal int Published { get; private set; }

        public void PublishPro(in Switch2CanonicalInputFrame frame) =>
            Published++;

        public void PublishJoyCon(in Switch2CanonicalInputFrame frame) =>
            Published++;

        public void ClearPro(ulong deviceGeneration,
            ulong transportGeneration, Switch2BluetoothInputEndReason reason)
        {
        }

        public void LoseJoyConHalf(Switch2StickSide side,
            ulong deviceGeneration, ulong transportGeneration,
            Switch2BluetoothInputEndReason reason)
        {
        }
    }

    private sealed class ReentrantStopSink :
        ISwitch2BluetoothCanonicalInputSink
    {
        internal Switch2BluetoothInputOwner Owner { get; set; }
        internal int Published { get; private set; }
        internal int Cleared { get; private set; }
        internal bool StopAccepted { get; private set; }
        internal bool ClearObservedInsidePublish { get; private set; }
        private bool publishing;

        public void PublishPro(in Switch2CanonicalInputFrame frame)
        {
            publishing = true;
            Published++;
            StopAccepted = Owner.Stop();
            ClearObservedInsidePublish = Cleared != 0;
            publishing = false;
        }

        public void PublishJoyCon(in Switch2CanonicalInputFrame frame) =>
            throw new InvalidOperationException();

        public void ClearPro(ulong deviceGeneration,
            ulong transportGeneration, Switch2BluetoothInputEndReason reason)
        {
            Assert.IsFalse(publishing);
            Cleared++;
        }

        public void LoseJoyConHalf(Switch2StickSide side,
            ulong deviceGeneration, ulong transportGeneration,
            Switch2BluetoothInputEndReason reason) =>
            throw new InvalidOperationException();
    }

    private sealed class BlockingPublicationSink :
        ISwitch2BluetoothCanonicalInputSink, IDisposable
    {
        private readonly ManualResetEventSlim entered = new(false);
        private readonly ManualResetEventSlim release = new(false);
        private bool cleared;

        internal int Published { get; private set; }
        internal int Cleared { get; private set; }
        internal int PublishedAfterClear { get; private set; }

        public void PublishPro(in Switch2CanonicalInputFrame frame)
        {
            entered.Set();
            release.Wait();
            Published++;
            if (cleared)
            {
                PublishedAfterClear++;
            }
        }

        public void PublishJoyCon(in Switch2CanonicalInputFrame frame) =>
            throw new InvalidOperationException();

        public void ClearPro(ulong deviceGeneration,
            ulong transportGeneration, Switch2BluetoothInputEndReason reason)
        {
            cleared = true;
            Cleared++;
        }

        public void LoseJoyConHalf(Switch2StickSide side,
            ulong deviceGeneration, ulong transportGeneration,
            Switch2BluetoothInputEndReason reason) =>
            throw new InvalidOperationException();

        internal bool WaitUntilEntered(TimeSpan timeout) =>
            entered.Wait(timeout);

        internal void Release() => release.Set();

        public void Dispose()
        {
            release.Set();
            entered.Dispose();
            release.Dispose();
        }
    }

    private sealed class ThrowingPublicationSink :
        ISwitch2BluetoothCanonicalInputSink
    {
        internal int ClearAttempts { get; private set; }
        internal bool ThrowOnClear { get; init; }

        public void PublishPro(in Switch2CanonicalInputFrame frame) =>
            throw new InvalidOperationException("Synthetic sink failure.");

        public void PublishJoyCon(in Switch2CanonicalInputFrame frame) =>
            throw new InvalidOperationException("Synthetic sink failure.");

        public void ClearPro(ulong deviceGeneration,
            ulong transportGeneration, Switch2BluetoothInputEndReason reason)
        {
            ClearAttempts++;
            if (ThrowOnClear)
            {
                throw new InvalidOperationException(
                    "Synthetic clear failure.");
            }
        }

        public void LoseJoyConHalf(Switch2StickSide side,
            ulong deviceGeneration, ulong transportGeneration,
            Switch2BluetoothInputEndReason reason)
        {
            ClearAttempts++;
            if (ThrowOnClear)
            {
                throw new InvalidOperationException(
                    "Synthetic clear failure.");
            }
        }
    }

    private sealed class SelfJoinSink :
        ISwitch2BluetoothCanonicalInputSink
    {
        internal Switch2BluetoothInputDrainPump Pump { get; set; }

        internal bool JoinAccepted { get; private set; }

        internal Switch2BluetoothInputDrainPumpFailure JoinFailure
        {
            get;
            private set;
        }

        public void PublishPro(in Switch2CanonicalInputFrame frame)
        {
            JoinAccepted = Pump.TryStopAndJoin(10, out var failure);
            JoinFailure = failure;
        }

        public void PublishJoyCon(in Switch2CanonicalInputFrame frame) =>
            PublishPro(frame);

        public void ClearPro(ulong deviceGeneration,
            ulong transportGeneration, Switch2BluetoothInputEndReason reason)
        {
        }

        public void LoseJoyConHalf(Switch2StickSide side,
            ulong deviceGeneration, ulong transportGeneration,
            Switch2BluetoothInputEndReason reason)
        {
        }
    }

    private sealed class WorkerAllocationSink :
        ISwitch2BluetoothCanonicalInputSink
    {
        private readonly int warmup;
        private readonly int measured;
        private long allocationStart;

        internal WorkerAllocationSink(int warmup, int measured)
        {
            this.warmup = warmup;
            this.measured = measured;
        }

        internal int Published { get; private set; }

        internal long AllocatedBytes { get; private set; }

        public void PublishPro(in Switch2CanonicalInputFrame frame)
        {
            Published++;
            if (Published == warmup)
            {
                allocationStart = GC.GetAllocatedBytesForCurrentThread();
            }
            else if (Published == warmup + measured)
            {
                AllocatedBytes = GC.GetAllocatedBytesForCurrentThread() -
                    allocationStart;
            }
        }

        public void PublishJoyCon(in Switch2CanonicalInputFrame frame) =>
            PublishPro(frame);

        public void ClearPro(ulong deviceGeneration,
            ulong transportGeneration, Switch2BluetoothInputEndReason reason)
        {
        }

        public void LoseJoyConHalf(Switch2StickSide side,
            ulong deviceGeneration, ulong transportGeneration,
            Switch2BluetoothInputEndReason reason)
        {
        }
    }
}
