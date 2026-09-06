using System.Reflection;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2BluetoothDiscoveryBoundaryTests
{
    private static readonly byte[] SessionKey = Enumerable.Range(0, 32)
        .Select(value => (byte)(value + 1)).ToArray();

    [TestMethod]
    public void PeerTokensAreKeyedScanScopedAndNeverExposeAnAddress()
    {
        Assert.IsTrue(Switch2BluetoothPeerToken.TryDerive(SessionKey, 7,
            0x112233445566, out var first));
        Assert.IsTrue(Switch2BluetoothPeerToken.TryDerive(SessionKey, 7,
            0x112233445566, out var duplicate));
        Assert.AreEqual(first, duplicate);

        Assert.IsTrue(Switch2BluetoothPeerToken.TryDerive(SessionKey, 8,
            0x112233445566, out var nextScan));
        Assert.AreNotEqual(first, nextScan);
        Assert.IsTrue(Switch2BluetoothPeerToken.TryDerive(SessionKey, 7,
            0x112233445567, out var rotatedAddress));
        Assert.AreNotEqual(first, rotatedAddress);

        byte[] otherKey = (byte[])SessionKey.Clone();
        otherKey[0] ^= 0x80;
        Assert.IsTrue(Switch2BluetoothPeerToken.TryDerive(otherKey, 7,
            0x112233445566, out var otherSession));
        Assert.AreNotEqual(first, otherSession);

        string[] publicMembers = typeof(Switch2BluetoothPeerToken)
            .GetMembers(BindingFlags.Instance | BindingFlags.Public)
            .Select(member => member.Name).ToArray();
        CollectionAssert.DoesNotContain(publicMembers, "Address");
        CollectionAssert.DoesNotContain(publicMembers, "BluetoothAddress");
        CollectionAssert.DoesNotContain(publicMembers, "Value");
        Assert.IsFalse(first.ToString().Contains("112233445566",
            StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void PeerTokenRejectsMalformedInputs()
    {
        Assert.IsFalse(Switch2BluetoothPeerToken.TryDerive(
            SessionKey.AsSpan(0, 31), 1, 1, out _));
        Assert.IsFalse(Switch2BluetoothPeerToken.TryDerive(new byte[32], 1,
            1, out _), "An all-zero key is not a private session key.");
        Assert.IsFalse(Switch2BluetoothPeerToken.TryDerive(SessionKey, 0, 1,
            out _));
        Assert.IsFalse(Switch2BluetoothPeerToken.TryDerive(SessionKey, 1, 0,
            out _));
        Assert.IsFalse(Switch2BluetoothPeerToken.TryDerive(SessionKey, 1,
            0x0001000000000000, out _));
    }

    [TestMethod]
    public void AdvertisementCodecRejectsEveryNonExactCapturedLength()
    {
        byte[] value = BuildAdvertisement(
            Switch2AdvertisementCodec.ProController2ProductId, null);
        for (int length = 0; length <= 64; length++)
        {
            byte[] candidate = new byte[length];
            value.AsSpan(0, Math.Min(length, value.Length)).CopyTo(candidate);
            bool decoded = Switch2AdvertisementCodec.TryDecode(
                Switch2AdvertisementCodec.NintendoBluetoothCompanyId,
                candidate, LocalHost, out _);
            Assert.AreEqual(length == value.Length, decoded,
                $"Unexpected decision for manufacturer length {length}.");
        }
    }

    [TestMethod]
    public void AdvertisementCodecEnforcesLittleEndianIdentityFields()
    {
        byte[] value = BuildAdvertisement(
            Switch2AdvertisementCodec.ProController2ProductId, null);

        (value[3], value[4]) = (value[4], value[3]);
        Assert.IsFalse(Switch2AdvertisementCodec.TryDecode(
            Switch2AdvertisementCodec.NintendoBluetoothCompanyId, value,
            LocalHost, out _), "A byte-swapped USB VID must fail closed.");

        value = BuildAdvertisement(
            Switch2AdvertisementCodec.ProController2ProductId, null);
        (value[5], value[6]) = (value[6], value[5]);
        Assert.IsFalse(Switch2AdvertisementCodec.TryDecode(
            Switch2AdvertisementCodec.NintendoBluetoothCompanyId, value,
            LocalHost, out _), "A byte-swapped product ID must fail closed.");
    }

    [TestMethod]
    public void RegistrySeparatesAssociationReconnectAndForeignHost()
    {
        var registry = new Switch2BluetoothCandidateRegistry();
        Assert.IsTrue(registry.TryBeginScan(10));

        Switch2Advertisement unassociated = DecodeAdvertisement(
            Switch2AdvertisementCodec.JoyCon2LeftProductId,
            rememberedHost: null);
        Switch2BluetoothCandidateObservation first = registry.Observe(10,
            Token(10, 1), 100, unassociated);
        Assert.AreEqual(
            Switch2BluetoothObservationDisposition.RequiresExplicitAssociation,
            first.Disposition);
        Assert.IsTrue(first.IsConnectionCandidate);

        Switch2Advertisement remembered = DecodeAdvertisement(
            Switch2AdvertisementCodec.JoyCon2RightProductId,
            rememberedHost: LocalHost);
        Switch2BluetoothCandidateObservation second = registry.Observe(10,
            Token(10, 2), 101, remembered);
        Assert.AreEqual(
            Switch2BluetoothObservationDisposition.RememberedThisHost,
            second.Disposition);

        Switch2Advertisement foreign = DecodeAdvertisement(
            Switch2AdvertisementCodec.ProController2ProductId,
            rememberedHost: ForeignHost);
        Switch2BluetoothCandidateObservation ignored = registry.Observe(10,
            Token(10, 3), 102, foreign);
        Assert.AreEqual(
            Switch2BluetoothObservationDisposition.IgnoredForeignHost,
            ignored.Disposition);
        Assert.IsFalse(ignored.IsConnectionCandidate);
        Assert.AreEqual(2, registry.Count,
            "Foreign-host advertisements must not consume capacity.");

        Assert.IsTrue(registry.TryCreateAssociationConnectionAdmission(first,
            out Switch2BluetoothConnectionAdmission associationAdmission));
        Assert.AreEqual(first.Model, associationAdmission.Model);
        Assert.AreEqual(first.ProductId, associationAdmission.ProductId);
        Assert.IsFalse(registry.TryCreateAssociationConnectionAdmission(first,
            out _), "The association capability is one shot.");
        Assert.IsFalse(registry.TryCreateRememberedConnectionAdmission(first,
            out _), "A zero-host candidate is never a reconnect admission.");

        Assert.IsTrue(registry.TryCreateRememberedConnectionAdmission(second,
            out Switch2BluetoothConnectionAdmission rememberedAdmission));
        Assert.AreEqual(second.Model, rememberedAdmission.Model);
        Assert.IsFalse(registry.TryCreateAssociationConnectionAdmission(second,
            out _), "A remembered peer cannot enter the association path.");
    }

    [TestMethod]
    public void CleanRememberedReleaseReissuesOnlyAfterExactAdmissionRetires()
    {
        var registry = new Switch2BluetoothCandidateRegistry();
        Assert.IsTrue(registry.TryBeginScan(18));
        Switch2BluetoothPeerToken peer = Token(18, 1);
        Switch2Advertisement local = DecodeAdvertisement(
            Switch2AdvertisementCodec.ProController2ProductId, LocalHost);

        Switch2BluetoothCandidateObservation first = registry.Observe(18,
            peer, 1, local);
        Assert.AreEqual(
            Switch2BluetoothObservationDisposition.RememberedThisHost,
            first.Disposition);
        Assert.IsTrue(registry.TryCreateRememberedConnectionAdmission(first,
            out Switch2BluetoothConnectionAdmission firstAdmission));
        Assert.IsTrue(firstAdmission.TryConsume());
        Assert.AreEqual(Switch2BluetoothObservationDisposition.Duplicate,
            registry.Observe(18, peer, 2, local).Disposition,
            "An active lease must not republish its connection candidate.");

        var unrelatedAdmission = new Switch2BluetoothConnectionAdmission(
            firstAdmission.ScanGeneration, firstAdmission.Model,
            firstAdmission.ProductId);
        Assert.IsFalse(registry.TryReleaseRememberedConnection(
            unrelatedAdmission),
            "Matching public fields cannot substitute for reservation identity.");
        Assert.IsTrue(registry.TryReleaseRememberedConnection(firstAdmission));
        Assert.IsFalse(registry.TryReleaseRememberedConnection(firstAdmission),
            "One clean teardown proof can rearm at most once.");

        Switch2BluetoothCandidateObservation reconnect = registry.Observe(18,
            peer, 3, local);
        Assert.AreEqual(
            Switch2BluetoothObservationDisposition.RememberedThisHost,
            reconnect.Disposition);
        Assert.IsTrue(registry.TryCreateRememberedConnectionAdmission(
            reconnect, out Switch2BluetoothConnectionAdmission successor));
        Assert.IsTrue(successor.TryConsume());
        Assert.IsFalse(registry.TryReleaseRememberedConnection(firstAdmission),
            "A retired admission cannot release its successor.");
        Assert.IsTrue(registry.TryReleaseRememberedConnection(successor));
    }

    [TestMethod]
    public void ReconnectCandidateCanDeferOnceOrQuarantineFailClosed()
    {
        var registry = new Switch2BluetoothCandidateRegistry();
        Assert.IsTrue(registry.TryBeginScan(19));
        Switch2BluetoothPeerToken peer = Token(19, 1);
        Switch2Advertisement local = DecodeAdvertisement(
            Switch2AdvertisementCodec.ProController2ProductId, LocalHost);

        Switch2BluetoothCandidateObservation first = registry.Observe(19,
            peer, 1, local);
        Assert.IsTrue(registry.TryCreateRememberedConnectionAdmission(first,
            out Switch2BluetoothConnectionAdmission admission));
        Assert.IsTrue(admission.TryConsume());
        Assert.IsTrue(registry.TryReleaseRememberedConnection(admission));

        Switch2BluetoothCandidateObservation reconnect = registry.Observe(19,
            peer, 2, local);
        Assert.IsTrue(registry.TryDeferRememberedConnectionCandidate(
            reconnect));
        Assert.IsFalse(registry.TryDeferRememberedConnectionCandidate(
            reconnect), "One publication can be deferred only once.");

        Switch2BluetoothCandidateObservation republished = registry.Observe(19,
            peer, 3, local);
        Assert.AreEqual(
            Switch2BluetoothObservationDisposition.RememberedThisHost,
            republished.Disposition);
        Assert.IsTrue(registry.TryRejectRememberedConnectionCandidate(
            republished));
        Assert.AreEqual(
            Switch2BluetoothObservationDisposition.IdentityConflict,
            registry.Observe(19, peer, 4, local).Disposition);
        Assert.IsFalse(registry.TryCreateRememberedConnectionAdmission(
            republished, out _));
    }

    [TestMethod]
    public void DuplicateAndRotatedPeerTokensRemainDistinctAndBounded()
    {
        var registry = new Switch2BluetoothCandidateRegistry(2);
        Assert.IsTrue(registry.TryBeginScan(4));
        Switch2Advertisement advertisement = DecodeAdvertisement(
            Switch2AdvertisementCodec.JoyCon2LeftProductId,
            rememberedHost: null);

        Switch2BluetoothPeerToken original = Token(4, 1);
        Assert.AreEqual(
            Switch2BluetoothObservationDisposition.RequiresExplicitAssociation,
            registry.Observe(4, original, 10, advertisement).Disposition);
        Assert.AreEqual(Switch2BluetoothObservationDisposition.Duplicate,
            registry.Observe(4, original, 10, advertisement).Disposition);
        Assert.AreEqual(
            Switch2BluetoothObservationDisposition.StaleObservation,
            registry.Observe(4, original, 9, advertisement).Disposition);

        Switch2BluetoothPeerToken rotated = Token(4, 2);
        Assert.AreEqual(
            Switch2BluetoothObservationDisposition.RequiresExplicitAssociation,
            registry.Observe(4, rotated, 11, advertisement).Disposition,
            "A rotated OS address cannot safely be merged by product ID.");
        Assert.AreEqual(2, registry.Count);

        Assert.AreEqual(
            Switch2BluetoothObservationDisposition.CapacityExceeded,
            registry.Observe(4, Token(4, 3), 12, advertisement).Disposition);
    }

    [TestMethod]
    public void TokenIdentityConflictQuarantinesCandidateForTheScan()
    {
        var registry = new Switch2BluetoothCandidateRegistry();
        Assert.IsTrue(registry.TryBeginScan(2));
        Switch2BluetoothPeerToken peer = Token(2, 1);
        Switch2Advertisement left = DecodeAdvertisement(
            Switch2AdvertisementCodec.JoyCon2LeftProductId, null);
        Switch2Advertisement right = DecodeAdvertisement(
            Switch2AdvertisementCodec.JoyCon2RightProductId, null);

        registry.Observe(2, peer, 1, left);
        Assert.AreEqual(
            Switch2BluetoothObservationDisposition.IdentityConflict,
            registry.Observe(2, peer, 2, right).Disposition);
        Assert.AreEqual(
            Switch2BluetoothObservationDisposition.IdentityConflict,
            registry.Observe(2, peer, 3, left).Disposition,
            "A later matching packet must not silently clear quarantine.");
        Assert.AreEqual(1, registry.Count);
    }

    [TestMethod]
    public void ChangedRememberedHostQuarantinesExistingPeer()
    {
        var registry = new Switch2BluetoothCandidateRegistry();
        Assert.IsTrue(registry.TryBeginScan(6));
        Switch2BluetoothPeerToken peer = Token(6, 1);
        Switch2Advertisement local = DecodeAdvertisement(
            Switch2AdvertisementCodec.ProController2ProductId, LocalHost);
        Switch2Advertisement foreign = DecodeAdvertisement(
            Switch2AdvertisementCodec.ProController2ProductId, ForeignHost);

        Assert.AreEqual(
            Switch2BluetoothObservationDisposition.RememberedThisHost,
            registry.Observe(6, peer, 1, local).Disposition);
        Assert.AreEqual(
            Switch2BluetoothObservationDisposition.IdentityConflict,
            registry.Observe(6, peer, 2, foreign).Disposition,
            "A host-classification change must retire the local candidate.");
        Assert.AreEqual(
            Switch2BluetoothObservationDisposition.IdentityConflict,
            registry.Observe(6, peer, 3, local).Disposition);
    }

    [TestMethod]
    public void CleanAssociationCommitPromotesExactlyOneHostTransition()
    {
        var registry = new Switch2BluetoothCandidateRegistry();
        Assert.IsTrue(registry.TryBeginScan(18));
        Switch2BluetoothPeerToken peer = Token(18, 1);
        Switch2Advertisement unassociated = DecodeAdvertisement(
            Switch2AdvertisementCodec.ProController2ProductId, null);
        Switch2Advertisement local = DecodeAdvertisement(
            Switch2AdvertisementCodec.ProController2ProductId, LocalHost);

        Switch2BluetoothCandidateObservation candidate = registry.Observe(
            18, peer, 1, unassociated);
        Assert.IsFalse(registry.TryCommitSuccessfulAssociation(candidate),
            "Commit cannot precede consumption of the association admission.");
        Assert.IsTrue(registry.TryCreateAssociationConnectionAdmission(
            candidate, out _));

        Assert.AreEqual(
            Switch2BluetoothObservationDisposition.AssociationInProgress,
            registry.Observe(18, peer, 2, local).Disposition,
            "A post-Commit advertisement cannot outrun command-owner cleanup.");
        Assert.IsTrue(registry.TryCommitSuccessfulAssociation(candidate));
        Assert.IsFalse(registry.TryCommitSuccessfulAssociation(candidate),
            "The clean command commit is a one-shot capability.");

        Assert.AreEqual(Switch2BluetoothObservationDisposition.Duplicate,
            registry.Observe(18, peer, 3, unassociated).Disposition,
            "A repeated zero-host advertisement must retain the pending commit.");
        Switch2BluetoothCandidateObservation promoted = registry.Observe(
            18, peer, 4, local);
        Assert.AreEqual(
            Switch2BluetoothObservationDisposition.RememberedThisHost,
            promoted.Disposition);
        Assert.IsTrue(registry.TryCreateRememberedConnectionAdmission(
            promoted, out _));
        Assert.IsFalse(registry.TryCreateRememberedConnectionAdmission(
            promoted, out _));
        Assert.AreEqual(Switch2BluetoothObservationDisposition.Duplicate,
            registry.Observe(18, peer, 5, local).Disposition);
    }

    [TestMethod]
    public void FailedAssociationRejectsAnInFlightHostTransition()
    {
        var registry = new Switch2BluetoothCandidateRegistry();
        Assert.IsTrue(registry.TryBeginScan(20));
        Switch2BluetoothPeerToken peer = Token(20, 1);
        Switch2Advertisement unassociated = DecodeAdvertisement(
            Switch2AdvertisementCodec.ProController2ProductId, null);
        Switch2Advertisement local = DecodeAdvertisement(
            Switch2AdvertisementCodec.ProController2ProductId, LocalHost);
        Switch2BluetoothCandidateObservation candidate = registry.Observe(
            20, peer, 1, unassociated);
        Assert.IsTrue(registry.TryCreateAssociationConnectionAdmission(
            candidate, out _));
        Assert.AreEqual(
            Switch2BluetoothObservationDisposition.AssociationInProgress,
            registry.Observe(20, peer, 2, local).Disposition);

        Assert.IsTrue(registry.TryRejectAssociation(candidate));
        Assert.IsFalse(registry.TryCommitSuccessfulAssociation(candidate));
        Assert.AreEqual(
            Switch2BluetoothObservationDisposition.IdentityConflict,
            registry.Observe(20, peer, 3, local).Disposition);
    }

    [TestMethod]
    public void UncommittedAssociationHostTransitionRemainsQuarantined()
    {
        var registry = new Switch2BluetoothCandidateRegistry();
        Assert.IsTrue(registry.TryBeginScan(19));
        Switch2BluetoothPeerToken peer = Token(19, 1);
        Switch2Advertisement unassociated = DecodeAdvertisement(
            Switch2AdvertisementCodec.JoyCon2RightProductId, null);
        Switch2Advertisement local = DecodeAdvertisement(
            Switch2AdvertisementCodec.JoyCon2RightProductId, LocalHost);

        registry.Observe(19, peer, 1, unassociated);
        Assert.AreEqual(
            Switch2BluetoothObservationDisposition.IdentityConflict,
            registry.Observe(19, peer, 2, local).Disposition);
        Assert.AreEqual(
            Switch2BluetoothObservationDisposition.IdentityConflict,
            registry.Observe(19, peer, 3, unassociated).Disposition);
    }

    [TestMethod]
    public void NewScanRetiresPriorTokensAndRejectsGenerationRollback()
    {
        var registry = new Switch2BluetoothCandidateRegistry();
        Assert.IsTrue(registry.TryBeginScan(3));
        Switch2Advertisement advertisement = DecodeAdvertisement(
            Switch2AdvertisementCodec.ProController2ProductId, LocalHost);
        Switch2BluetoothPeerToken oldToken = Token(3, 1);
        registry.Observe(3, oldToken, 1, advertisement);

        Assert.IsTrue(registry.TryBeginScan(4));
        Assert.AreEqual(0, registry.Count);
        Assert.AreEqual(Switch2BluetoothObservationDisposition.Rejected,
            registry.Observe(3, oldToken, 2, advertisement).Disposition);
        Assert.AreEqual(Switch2BluetoothObservationDisposition.Rejected,
            registry.Observe(4, oldToken, 2, advertisement).Disposition,
            "Changing only the generation argument cannot revive an old token.");
        Assert.IsFalse(registry.TryBeginScan(4));
        Assert.IsFalse(registry.TryBeginScan(2));
        Assert.AreEqual(4UL, registry.ScanGeneration);
    }

    [TestMethod]
    public void EndingScanClearsCandidatesAndRejectsLateObservations()
    {
        var registry = new Switch2BluetoothCandidateRegistry();
        Assert.IsTrue(registry.TryBeginScan(7));
        Assert.IsTrue(registry.IsScanActive);
        Switch2Advertisement advertisement = DecodeAdvertisement(
            Switch2AdvertisementCodec.ProController2ProductId, LocalHost);
        Switch2BluetoothPeerToken token = Token(7, 1);
        registry.Observe(7, token, 1, advertisement);
        Assert.AreEqual(1, registry.Count);

        Assert.IsFalse(registry.TryEndScan(6));
        Assert.IsTrue(registry.TryEndScan(7));
        Assert.IsFalse(registry.IsScanActive);
        Assert.AreEqual(0, registry.Count);
        Assert.AreEqual(7UL, registry.ScanGeneration,
            "Ending a scan must preserve the rollback fence.");
        Assert.AreEqual(Switch2BluetoothObservationDisposition.Rejected,
            registry.Observe(7, token, 2, advertisement).Disposition);
        Assert.IsFalse(registry.TryEndScan(7));
        Assert.IsFalse(registry.TryBeginScan(7));
        Assert.IsTrue(registry.TryBeginScan(8));
    }

    [TestMethod]
    public void StaleIdentityConflictCannotQuarantineNewerCandidate()
    {
        var registry = new Switch2BluetoothCandidateRegistry();
        Assert.IsTrue(registry.TryBeginScan(12));
        Switch2BluetoothPeerToken peer = Token(12, 1);
        Switch2Advertisement left = DecodeAdvertisement(
            Switch2AdvertisementCodec.JoyCon2LeftProductId, null);
        Switch2Advertisement right = DecodeAdvertisement(
            Switch2AdvertisementCodec.JoyCon2RightProductId, null);

        registry.Observe(12, peer, 20, left);
        Assert.AreEqual(
            Switch2BluetoothObservationDisposition.StaleObservation,
            registry.Observe(12, peer, 19, right).Disposition);
        Assert.AreEqual(Switch2BluetoothObservationDisposition.Duplicate,
            registry.Observe(12, peer, 21, left).Disposition,
            "An older conflicting callback cannot quarantine newer identity state.");
        Assert.AreEqual(
            Switch2BluetoothObservationDisposition.IdentityConflict,
            registry.Observe(12, peer, 22, right).Disposition,
            "A current conflicting identity must still quarantine the token.");
    }

    [TestMethod]
    public void WakeHintIsNeverPromotedToConnectionCandidate()
    {
        var registry = new Switch2BluetoothCandidateRegistry();
        Assert.IsTrue(registry.TryBeginScan(13));
        Switch2Advertisement wake = DecodeAdvertisement(
            Switch2AdvertisementCodec.ProController2ProductId, LocalHost,
            isWake: true);

        Switch2BluetoothCandidateObservation observation = registry.Observe(
            13, Token(13, 1), 1, wake);
        Assert.AreEqual(Switch2BluetoothObservationDisposition.IgnoredWake,
            observation.Disposition);
        Assert.IsTrue(observation.IsWake);
        Assert.IsFalse(observation.IsConnectionCandidate);
        Assert.AreEqual(0, registry.Count);
    }

    [TestMethod]
    public void WakeHintCannotAdvanceRecencyOrBypassQuarantine()
    {
        var registry = new Switch2BluetoothCandidateRegistry();
        Assert.IsTrue(registry.TryBeginScan(15));
        Switch2BluetoothPeerToken peer = Token(15, 1);
        Switch2Advertisement normal = DecodeAdvertisement(
            Switch2AdvertisementCodec.ProController2ProductId, LocalHost);
        Switch2Advertisement wake = DecodeAdvertisement(
            Switch2AdvertisementCodec.ProController2ProductId, LocalHost,
            isWake: true);
        Switch2Advertisement conflicting = DecodeAdvertisement(
            Switch2AdvertisementCodec.JoyCon2RightProductId, LocalHost);

        registry.Observe(15, peer, 10, normal);
        Assert.AreEqual(Switch2BluetoothObservationDisposition.StaleObservation,
            registry.Observe(15, peer, 9, wake).Disposition,
            "Wake handling must not hide stale callback ordering.");
        Assert.AreEqual(Switch2BluetoothObservationDisposition.IgnoredWake,
            registry.Observe(15, peer, 20, wake).Disposition);
        Assert.AreEqual(Switch2BluetoothObservationDisposition.Duplicate,
            registry.Observe(15, peer, 11, normal).Disposition,
            "An ignored wake hint must not advance candidate recency.");

        Assert.AreEqual(
            Switch2BluetoothObservationDisposition.IdentityConflict,
            registry.Observe(15, peer, 12, conflicting).Disposition);
        Assert.AreEqual(
            Switch2BluetoothObservationDisposition.IdentityConflict,
            registry.Observe(15, peer, 21, wake).Disposition,
            "Wake handling must not bypass an existing quarantine.");
    }

    [TestMethod]
    public void ConcurrentDuplicateAdmissionKeepsOneCandidate()
    {
        var registry = new Switch2BluetoothCandidateRegistry();
        Assert.IsTrue(registry.TryBeginScan(14));
        Switch2Advertisement advertisement = DecodeAdvertisement(
            Switch2AdvertisementCodec.ProController2ProductId, LocalHost);
        Switch2BluetoothPeerToken token = Token(14, 1);
        int admitted = 0;
        int conflicts = 0;

        Parallel.For(0, 512, index =>
        {
            Switch2BluetoothObservationDisposition disposition = registry
                .Observe(14, token, index, advertisement).Disposition;
            if (disposition == Switch2BluetoothObservationDisposition
                    .RememberedThisHost)
            {
                Interlocked.Increment(ref admitted);
            }
            else if (disposition is
                Switch2BluetoothObservationDisposition.IdentityConflict or
                Switch2BluetoothObservationDisposition.CapacityExceeded)
            {
                Interlocked.Increment(ref conflicts);
            }
        });

        Assert.AreEqual(1, admitted);
        Assert.AreEqual(0, conflicts);
        Assert.AreEqual(1, registry.Count);
        Assert.AreEqual(Switch2BluetoothObservationDisposition.StaleObservation,
            registry.Observe(14, token, 510, advertisement).Disposition);
        Assert.AreEqual(Switch2BluetoothObservationDisposition.Duplicate,
            registry.Observe(14, token, 512, advertisement).Disposition);
    }

    [TestMethod]
    public void ConcurrentScanRotationCannotResurrectRetiredToken()
    {
        var registry = new Switch2BluetoothCandidateRegistry();
        Assert.IsTrue(registry.TryBeginScan(16));
        Switch2Advertisement advertisement = DecodeAdvertisement(
            Switch2AdvertisementCodec.ProController2ProductId, LocalHost);
        Switch2BluetoothPeerToken retired = Token(16, 1);
        bool rotated = false;

        Parallel.Invoke(
            () =>
            {
                for (int index = 0; index < 2_000; index++)
                {
                    registry.Observe(16, retired, index, advertisement);
                }
            },
            () => rotated = registry.TryBeginScan(17));

        Assert.IsTrue(rotated);
        Assert.AreEqual(17UL, registry.ScanGeneration);
        Assert.AreEqual(0, registry.Count,
            "Rotation must clear every admission that won the old-generation race.");
        Assert.AreEqual(Switch2BluetoothObservationDisposition.Rejected,
            registry.Observe(17, retired, 2_001, advertisement).Disposition,
            "The old token must remain unusable with the new generation argument.");
        Assert.AreEqual(
            Switch2BluetoothObservationDisposition.RememberedThisHost,
            registry.Observe(17, Token(17, 1), 2_001, advertisement)
                .Disposition);
    }

    [TestMethod]
    public void DuplicateObservationPathAllocatesNothingAfterWarmup()
    {
        var registry = new Switch2BluetoothCandidateRegistry();
        Assert.IsTrue(registry.TryBeginScan(5));
        Switch2Advertisement advertisement = DecodeAdvertisement(
            Switch2AdvertisementCodec.ProController2ProductId, LocalHost);
        Switch2BluetoothPeerToken token = Token(5, 1);
        registry.Observe(5, token, 1, advertisement);
        for (int index = 0; index < 256; index++)
        {
            registry.Observe(5, token, index + 2, advertisement);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 20_000; index++)
        {
            registry.Observe(5, token, index + 258, advertisement);
        }
        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.AreEqual(0L, after - before);
    }

    [TestMethod]
    public void InvalidRegistryInputsFailClosed()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new Switch2BluetoothCandidateRegistry(0));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new Switch2BluetoothCandidateRegistry(
                Switch2BluetoothCandidateRegistry.MaximumCapacity + 1));

        var registry = new Switch2BluetoothCandidateRegistry();
        Assert.IsFalse(registry.TryBeginScan(0));
        Assert.IsTrue(registry.TryBeginScan(1));
        Switch2Advertisement valid = DecodeAdvertisement(
            Switch2AdvertisementCodec.ProController2ProductId, null);
        Assert.AreEqual(Switch2BluetoothObservationDisposition.Rejected,
            registry.Observe(1, default, 0, valid).Disposition);
        Assert.AreEqual(Switch2BluetoothObservationDisposition.Rejected,
            registry.Observe(1, Token(1, 1), -1, valid).Disposition);
        Assert.AreEqual(Switch2BluetoothObservationDisposition.Rejected,
            registry.Observe(1, Token(1, 1), 0, default).Disposition);
    }

    private static readonly byte[] LocalHost =
        { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66 };

    private static readonly byte[] ForeignHost =
        { 0x21, 0x22, 0x23, 0x24, 0x25, 0x26 };

    private static Switch2BluetoothPeerToken Token(ulong generation,
        ulong address)
    {
        Assert.IsTrue(Switch2BluetoothPeerToken.TryDerive(SessionKey,
            generation, address, out Switch2BluetoothPeerToken token));
        return token;
    }

    private static Switch2Advertisement DecodeAdvertisement(ushort productId,
        byte[] rememberedHost, bool isWake = false)
    {
        byte[] value = BuildAdvertisement(productId, rememberedHost, isWake);
        Assert.IsTrue(Switch2AdvertisementCodec.TryDecode(
            Switch2AdvertisementCodec.NintendoBluetoothCompanyId, value,
            LocalHost, out Switch2Advertisement advertisement));
        return advertisement;
    }

    private static byte[] BuildAdvertisement(ushort productId,
        byte[] rememberedHost, bool isWake = false)
    {
        byte[] value = new byte[Switch2AdvertisementCodec.ManufacturerValueLength];
        value[0] = 0x01;
        value[2] = 0x03;
        value[3] = 0x7E;
        value[4] = 0x05;
        value[5] = (byte)productId;
        value[6] = (byte)(productId >> 8);
        value[8] = 0x01;
        value[9] = isWake ? (byte)0x81 : (byte)0x00;
        value[16] = 0x0F;
        if (rememberedHost is not null)
        {
            rememberedHost.Reverse().ToArray().CopyTo(value, 10);
        }
        return value;
    }
}
