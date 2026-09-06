using System;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace DS4Windows.Switch2;

/// <summary>
/// A scan-local keyed pseudonym for a BLE peer. It deliberately is not a
/// Bluetooth address and must not be persisted across discovery sessions.
/// </summary>
public readonly struct Switch2BluetoothPeerToken :
    IEquatable<Switch2BluetoothPeerToken>
{
    public const int SessionKeyLength = 32;

    private readonly ulong scanGeneration;
    private readonly ulong low;
    private readonly ulong high;

    private Switch2BluetoothPeerToken(ulong scanGeneration, ulong low,
        ulong high)
    {
        this.scanGeneration = scanGeneration;
        this.low = low;
        this.high = high;
    }

    public bool IsValid => scanGeneration != 0 && (low | high) != 0;

    internal bool IsForScanGeneration(ulong generation) =>
        IsValid && scanGeneration == generation;

    /// <summary>
    /// Derives a token from a process-private random key, the scan generation,
    /// and the 48-bit address supplied by the OS. The raw address and full
    /// digest buffer are not retained; the token contains only the scan fence
    /// and the first 128 bits of the keyed digest.
    /// </summary>
    public static bool TryDerive(ReadOnlySpan<byte> sessionKey,
        ulong scanGeneration, ulong bluetoothAddress,
        out Switch2BluetoothPeerToken token)
    {
        const ulong AddressMask = 0x0000FFFFFFFFFFFFUL;
        if (sessionKey.Length != SessionKeyLength ||
            !ContainsNonzeroByte(sessionKey) || scanGeneration == 0 ||
            bluetoothAddress == 0 || (bluetoothAddress & ~AddressMask) != 0)
        {
            token = default;
            return false;
        }

        Span<byte> input = stackalloc byte[14];
        Span<byte> digest = stackalloc byte[32];
        try
        {
            BinaryPrimitives.WriteUInt64LittleEndian(input,
                scanGeneration);
            input[8] = (byte)bluetoothAddress;
            input[9] = (byte)(bluetoothAddress >> 8);
            input[10] = (byte)(bluetoothAddress >> 16);
            input[11] = (byte)(bluetoothAddress >> 24);
            input[12] = (byte)(bluetoothAddress >> 32);
            input[13] = (byte)(bluetoothAddress >> 40);
            HMACSHA256.HashData(sessionKey, input, digest);

            ulong tokenLow = BinaryPrimitives.ReadUInt64LittleEndian(digest);
            ulong tokenHigh = BinaryPrimitives.ReadUInt64LittleEndian(
                digest.Slice(8));
            if ((tokenLow | tokenHigh) == 0)
            {
                token = default;
                return false;
            }

            token = new Switch2BluetoothPeerToken(scanGeneration, tokenLow,
                tokenHigh);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    private static bool ContainsNonzeroByte(ReadOnlySpan<byte> value)
    {
        byte aggregate = 0;
        foreach (byte current in value)
        {
            aggregate |= current;
        }
        return aggregate != 0;
    }

    public bool Equals(Switch2BluetoothPeerToken other) =>
        scanGeneration == other.scanGeneration && low == other.low &&
        high == other.high;

    public override bool Equals(object obj) =>
        obj is Switch2BluetoothPeerToken other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(scanGeneration, low,
        high);

    public static bool operator ==(Switch2BluetoothPeerToken left,
        Switch2BluetoothPeerToken right) => left.Equals(right);

    public static bool operator !=(Switch2BluetoothPeerToken left,
        Switch2BluetoothPeerToken right) => !left.Equals(right);
}

public enum Switch2BluetoothObservationDisposition : byte
{
    Rejected = 0,
    RequiresExplicitAssociation,
    RememberedThisHost,
    IgnoredForeignHost,
    Duplicate,
    StaleObservation,
    IdentityConflict,
    CapacityExceeded,
    IgnoredWake,
    AssociationInProgress,
}

/// <summary>
/// Privacy-minimized result of admitting one advertisement observation. The
/// opaque peer token is useful only inside the active scan generation.
/// </summary>
public readonly struct Switch2BluetoothCandidateObservation
{
    internal Switch2BluetoothCandidateObservation(
        Switch2BluetoothObservationDisposition disposition,
        ulong scanGeneration, Switch2BluetoothPeerToken peerToken,
        Switch2ControllerModel model, ushort productId, bool isWake,
        long observedQpc)
    {
        Disposition = disposition;
        ScanGeneration = scanGeneration;
        PeerToken = peerToken;
        Model = model;
        ProductId = productId;
        IsWake = isWake;
        ObservedQpc = observedQpc;
    }

    public Switch2BluetoothObservationDisposition Disposition { get; }

    public ulong ScanGeneration { get; }

    public Switch2BluetoothPeerToken PeerToken { get; }

    public Switch2ControllerModel Model { get; }

    public ushort ProductId { get; }

    public bool IsWake { get; }

    public long ObservedQpc { get; }

    public bool IsConnectionCandidate => Disposition is
        Switch2BluetoothObservationDisposition.RequiresExplicitAssociation or
        Switch2BluetoothObservationDisposition.RememberedThisHost;
}

/// <summary>
/// Bounded, generation-fenced advertisement admission. It performs no Windows
/// discovery, connection, pairing, association, GATT access, or output I/O.
/// </summary>
public sealed class Switch2BluetoothCandidateRegistry
{
    public const int MaximumCapacity = 16;

    private readonly object sync = new();
    private readonly CandidateEntry[] entries;
    private ulong scanGeneration;
    private int count;
    private bool scanActive;

    public Switch2BluetoothCandidateRegistry(int capacity = 8)
    {
        if (capacity is < 1 or > MaximumCapacity)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        entries = new CandidateEntry[capacity];
    }

    public int Capacity => entries.Length;

    public int Count
    {
        get
        {
            lock (sync)
            {
                return count;
            }
        }
    }

    public ulong ScanGeneration
    {
        get
        {
            lock (sync)
            {
                return scanGeneration;
            }
        }
    }

    public bool IsScanActive
    {
        get
        {
            lock (sync)
            {
                return scanActive;
            }
        }
    }

    /// <summary>
    /// Starts a strictly newer scan and retires every token from the previous
    /// scan. Reusing or rolling back a generation is rejected.
    /// </summary>
    public bool TryBeginScan(ulong generation)
    {
        lock (sync)
        {
            if (generation == 0 || generation <= scanGeneration)
            {
                return false;
            }

            Array.Clear(entries, 0, entries.Length);
            count = 0;
            scanGeneration = generation;
            scanActive = true;
            return true;
        }
    }

    /// <summary>
    /// Retires the active scan and clears every registry-owned token while
    /// preserving the monotonic generation fence. A stale or repeated end is
    /// rejected and cannot retire a newer scan.
    /// </summary>
    public bool TryEndScan(ulong generation)
    {
        lock (sync)
        {
            if (!scanActive || generation == 0 ||
                generation != scanGeneration)
            {
                return false;
            }

            Array.Clear(entries, 0, entries.Length);
            count = 0;
            scanActive = false;
            return true;
        }
    }

    /// <summary>
    /// Admits an already-decoded advertisement. Repeated observations for the
    /// same peer are idempotent. A current changed identity on the same token
    /// is quarantined for the remainder of the scan; an older callback cannot
    /// mutate newer state. Different tokens are never merged merely because
    /// their product IDs match.
    /// </summary>
    public Switch2BluetoothCandidateObservation Observe(ulong generation,
        Switch2BluetoothPeerToken peerToken, long observedQpc,
        in Switch2Advertisement advertisement)
    {
        lock (sync)
        {
            if (!scanActive || generation == 0 ||
                generation != scanGeneration ||
                !peerToken.IsForScanGeneration(generation) ||
                observedQpc < 0 ||
                !IsValidAdvertisement(advertisement))
            {
                return CreateObservation(
                    Switch2BluetoothObservationDisposition.Rejected,
                    generation, peerToken, advertisement, observedQpc);
            }

            int emptyIndex = -1;
            for (int index = 0; index < entries.Length; index++)
            {
                ref CandidateEntry entry = ref entries[index];
                if (!entry.InUse)
                {
                    if (emptyIndex < 0)
                    {
                        emptyIndex = index;
                    }
                    continue;
                }

                if (entry.PeerToken != peerToken)
                {
                    continue;
                }

                if (entry.Quarantined)
                {
                    return CreateObservation(
                        Switch2BluetoothObservationDisposition.IdentityConflict,
                        generation, peerToken, advertisement, observedQpc);
                }

                if (observedQpc < entry.LastObservedQpc)
                {
                    return CreateObservation(
                        Switch2BluetoothObservationDisposition.StaleObservation,
                        generation, peerToken, advertisement, observedQpc);
                }

                // Wake semantics and automatic reconnect remain unevidenced.
                // Keep the hint visible, but do not let it update identity or
                // recency state. A previously quarantined token was handled
                // above and remains quarantined.
                if (advertisement.IsWake)
                {
                    return CreateObservation(
                        Switch2BluetoothObservationDisposition.IgnoredWake,
                        generation, peerToken, advertisement, observedQpc);
                }

                if (entry.Model != advertisement.Model ||
                    entry.ProductId != advertisement.ProductId)
                {
                    entry.Quarantined = true;
                    return CreateObservation(
                        Switch2BluetoothObservationDisposition.IdentityConflict,
                        generation, peerToken, advertisement, observedQpc);
                }

                // The controller can start advertising the selected host as
                // soon as it accepts Commit, while the temporary owner is
                // still draining its callback and CCCD. Hold that exact
                // transition without adopting or quarantining it. The command
                // owner will explicitly commit or reject the admission after
                // cleanup reaches a known result.
                if (entry.ConnectionAdmissionIssued &&
                    !entry.AssociationCommitAuthorized &&
                    entry.Host == Switch2AdvertisedHost.None &&
                    advertisement.Host == Switch2AdvertisedHost.ThisHost)
                {
                    return CreateObservation(
                        Switch2BluetoothObservationDisposition.
                            AssociationInProgress,
                        generation, peerToken, advertisement, observedQpc);
                }

                // A clean command-0x15 association authorizes exactly one
                // expected None -> ThisHost identity transition. The command
                // owner records that authorization only after its Commit
                // response and cleanup both complete. No other host change is
                // inferred from advertisement bytes.
                if (entry.AssociationCommitAuthorized &&
                    entry.Host == Switch2AdvertisedHost.None &&
                    advertisement.Host == Switch2AdvertisedHost.ThisHost)
                {
                    entry.Host = Switch2AdvertisedHost.ThisHost;
                    entry.LastObservedQpc = observedQpc;
                    entry.ConnectionAdmissionIssued = false;
                    entry.ConnectionCandidatePublished = true;
                    entry.ActiveAdmission = default;
                    entry.AssociationCommitAuthorized = false;
                    return CreateObservation(
                        Switch2BluetoothObservationDisposition.
                            RememberedThisHost,
                        generation, peerToken, advertisement, observedQpc);
                }

                if (entry.Host != advertisement.Host)
                {
                    entry.Quarantined = true;
                    return CreateObservation(
                        Switch2BluetoothObservationDisposition.IdentityConflict,
                        generation, peerToken, advertisement, observedQpc);
                }

                entry.LastObservedQpc = observedQpc;
                if (advertisement.Host == Switch2AdvertisedHost.ThisHost &&
                    !entry.ConnectionAdmissionIssued &&
                    !entry.ConnectionCandidatePublished)
                {
                    entry.ConnectionCandidatePublished = true;
                    return CreateObservation(
                        Switch2BluetoothObservationDisposition.
                            RememberedThisHost,
                        generation, peerToken, advertisement, observedQpc);
                }
                return CreateObservation(
                    Switch2BluetoothObservationDisposition.Duplicate,
                    generation, peerToken, advertisement, observedQpc);
            }

            if (advertisement.IsWake)
            {
                return CreateObservation(
                    Switch2BluetoothObservationDisposition.IgnoredWake,
                    generation, peerToken, advertisement, observedQpc);
            }

            if (advertisement.Host == Switch2AdvertisedHost.ForeignHost)
            {
                return CreateObservation(
                    Switch2BluetoothObservationDisposition.IgnoredForeignHost,
                    generation, peerToken, advertisement, observedQpc);
            }

            if (emptyIndex < 0)
            {
                return CreateObservation(
                    Switch2BluetoothObservationDisposition.CapacityExceeded,
                    generation, peerToken, advertisement, observedQpc);
            }

            entries[emptyIndex] = new CandidateEntry(peerToken,
                advertisement.Model, advertisement.ProductId,
                advertisement.Host, observedQpc);
            count++;

            Switch2BluetoothObservationDisposition disposition =
                advertisement.Host == Switch2AdvertisedHost.None ?
                    Switch2BluetoothObservationDisposition.
                        RequiresExplicitAssociation :
                    Switch2BluetoothObservationDisposition.RememberedThisHost;
            return CreateObservation(disposition, generation, peerToken,
                advertisement, observedQpc);
        }
    }

    /// <summary>
    /// Converts only a current-scan, non-quarantined
    /// <see cref="Switch2BluetoothObservationDisposition.RememberedThisHost"/>
    /// observation into a read-only connection admission. The validation and
    /// registry lookup share the scan lock, so ending or replacing the scan
    /// cannot race an old token into a new connection lifetime.
    /// </summary>
    internal bool TryCreateRememberedConnectionAdmission(
        in Switch2BluetoothCandidateObservation observation,
        out Switch2BluetoothConnectionAdmission admission)
        => TryCreateConnectionAdmission(observation,
            Switch2BluetoothObservationDisposition.RememberedThisHost,
            Switch2AdvertisedHost.ThisHost, out admission);

    /// <summary>
    /// Burns the one current-scan admission for a zero-host candidate before
    /// controller-side command-0x15 association begins. A failed or ambiguous
    /// ceremony cannot retry through the same advertisement capability.
    /// </summary>
    internal bool TryCreateAssociationConnectionAdmission(
        in Switch2BluetoothCandidateObservation observation,
        out Switch2BluetoothConnectionAdmission admission)
        => TryCreateConnectionAdmission(observation,
            Switch2BluetoothObservationDisposition.RequiresExplicitAssociation,
            Switch2AdvertisedHost.None, out admission);

    /// <summary>
    /// Records the exact successful association capability after the command
    /// ceremony's Commit response and cleanup are both known complete. This
    /// does not itself promote or connect the peer. It authorizes only the
    /// next matching advertisement to move from no remembered host to this
    /// scan's selected host; every uncommitted or contradictory transition
    /// remains an identity conflict.
    /// </summary>
    internal bool TryCommitSuccessfulAssociation(
        in Switch2BluetoothCandidateObservation observation)
    {
        lock (sync)
        {
            if (!scanActive || observation.Disposition !=
                    Switch2BluetoothObservationDisposition.
                        RequiresExplicitAssociation ||
                observation.IsWake || observation.ScanGeneration == 0 ||
                observation.ScanGeneration != scanGeneration ||
                !observation.PeerToken.IsForScanGeneration(scanGeneration))
            {
                return false;
            }

            for (int index = 0; index < entries.Length; index++)
            {
                ref CandidateEntry entry = ref entries[index];
                if (!entry.InUse || entry.PeerToken != observation.PeerToken)
                {
                    continue;
                }

                if (entry.Quarantined || !entry.ConnectionAdmissionIssued ||
                    entry.AssociationCommitAuthorized ||
                    entry.Host != Switch2AdvertisedHost.None ||
                    entry.Model != observation.Model ||
                    entry.ProductId != observation.ProductId)
                {
                    return false;
                }

                entry.AssociationCommitAuthorized = true;
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Terminally rejects the exact consumed association admission after any
    /// command, timeout, cancellation, or cleanup failure. A host transition
    /// observed while the command was in flight can never be promoted by a
    /// later advertisement or copied observation.
    /// </summary>
    internal bool TryRejectAssociation(
        in Switch2BluetoothCandidateObservation observation)
    {
        lock (sync)
        {
            if (!scanActive || observation.Disposition !=
                    Switch2BluetoothObservationDisposition.
                        RequiresExplicitAssociation ||
                observation.ScanGeneration == 0 ||
                observation.ScanGeneration != scanGeneration ||
                !observation.PeerToken.IsForScanGeneration(scanGeneration))
            {
                return false;
            }

            for (int index = 0; index < entries.Length; index++)
            {
                ref CandidateEntry entry = ref entries[index];
                if (!entry.InUse || entry.PeerToken != observation.PeerToken)
                {
                    continue;
                }
                if (entry.Quarantined || !entry.ConnectionAdmissionIssued ||
                    entry.AssociationCommitAuthorized ||
                    entry.Host != Switch2AdvertisedHost.None ||
                    entry.Model != observation.Model ||
                    entry.ProductId != observation.ProductId)
                {
                    return false;
                }

                entry.Quarantined = true;
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Retires the exact remembered-host admission after its complete input
    /// lease has reached an unambiguous teardown boundary. The next matching
    /// advertisement may then publish one fresh connection candidate. A
    /// copied observation, structurally similar admission, stale admission,
    /// or ambiguous teardown cannot release a live reservation.
    /// </summary>
    internal bool TryReleaseRememberedConnection(
        in Switch2BluetoothConnectionAdmission admission)
    {
        if (!admission.IsValid)
        {
            return false;
        }

        lock (sync)
        {
            // Retiring or replacing the scan already erased every entry. The
            // old lease still owns a valid release proof, but there is no
            // current-scan authority left to mutate or rearm.
            if (!scanActive || admission.ScanGeneration != scanGeneration)
            {
                return true;
            }

            for (int index = 0; index < entries.Length; index++)
            {
                ref CandidateEntry entry = ref entries[index];
                if (!entry.InUse || entry.Quarantined ||
                    !entry.ConnectionAdmissionIssued ||
                    entry.Host != Switch2AdvertisedHost.ThisHost ||
                    entry.Model != admission.Model ||
                    entry.ProductId != admission.ProductId ||
                    !entry.ActiveAdmission.Equals(admission))
                {
                    continue;
                }

                entry.ConnectionAdmissionIssued = false;
                entry.ConnectionCandidatePublished = false;
                entry.LastReleasedAdmission = admission;
                entry.ActiveAdmission = default;
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Defers an unconsumed remembered candidate while an earlier runtime for
    /// the same scan-private peer is still completing registration teardown.
    /// The next matching advertisement may republish it. This never revives a
    /// consumed admission or clears quarantine.
    /// </summary>
    internal bool TryDeferRememberedConnectionCandidate(
        in Switch2BluetoothCandidateObservation observation)
    {
        lock (sync)
        {
            if (!scanActive || observation.Disposition !=
                    Switch2BluetoothObservationDisposition.
                        RememberedThisHost ||
                observation.IsWake || observation.ScanGeneration == 0 ||
                observation.ScanGeneration != scanGeneration ||
                !observation.PeerToken.IsForScanGeneration(scanGeneration))
            {
                return false;
            }

            for (int index = 0; index < entries.Length; index++)
            {
                ref CandidateEntry entry = ref entries[index];
                if (!entry.InUse || entry.PeerToken != observation.PeerToken)
                {
                    continue;
                }

                if (entry.Quarantined || entry.ConnectionAdmissionIssued ||
                    !entry.ConnectionCandidatePublished ||
                    entry.Host != Switch2AdvertisedHost.ThisHost ||
                    entry.Model != observation.Model ||
                    entry.ProductId != observation.ProductId)
                {
                    return false;
                }

                entry.ConnectionCandidatePublished = false;
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Quarantines an unconsumed remembered candidate when its prior exact
    /// registration lifetime is itself quarantined or cannot be inspected.
    /// A new scan generation is required before that peer can connect again.
    /// </summary>
    internal bool TryRejectRememberedConnectionCandidate(
        in Switch2BluetoothCandidateObservation observation)
    {
        lock (sync)
        {
            if (!scanActive || observation.Disposition !=
                    Switch2BluetoothObservationDisposition.
                        RememberedThisHost ||
                observation.IsWake || observation.ScanGeneration == 0 ||
                observation.ScanGeneration != scanGeneration ||
                !observation.PeerToken.IsForScanGeneration(scanGeneration))
            {
                return false;
            }

            for (int index = 0; index < entries.Length; index++)
            {
                ref CandidateEntry entry = ref entries[index];
                if (!entry.InUse || entry.PeerToken != observation.PeerToken)
                {
                    continue;
                }

                if (entry.Quarantined || entry.ConnectionAdmissionIssued ||
                    !entry.ConnectionCandidatePublished ||
                    entry.Host != Switch2AdvertisedHost.ThisHost ||
                    entry.Model != observation.Model ||
                    entry.ProductId != observation.ProductId)
                {
                    return false;
                }

                entry.Quarantined = true;
                entry.ConnectionCandidatePublished = false;
                return true;
            }

            return false;
        }
    }

    private bool TryCreateConnectionAdmission(
        in Switch2BluetoothCandidateObservation observation,
        Switch2BluetoothObservationDisposition expectedDisposition,
        Switch2AdvertisedHost expectedHost,
        out Switch2BluetoothConnectionAdmission admission)
    {
        lock (sync)
        {
            if (!scanActive || observation.Disposition !=
                    expectedDisposition ||
                observation.IsWake || observation.ScanGeneration == 0 ||
                observation.ScanGeneration != scanGeneration ||
                !observation.PeerToken.IsForScanGeneration(scanGeneration))
            {
                admission = default;
                return false;
            }

            for (int index = 0; index < entries.Length; index++)
            {
                ref CandidateEntry entry = ref entries[index];
                if (!entry.InUse || entry.PeerToken != observation.PeerToken)
                {
                    continue;
                }

                if (entry.Quarantined || entry.ConnectionAdmissionIssued ||
                    !entry.ConnectionCandidatePublished ||
                    entry.Host != expectedHost ||
                    entry.Model != observation.Model ||
                    entry.ProductId != observation.ProductId)
                {
                    admission = default;
                    return false;
                }

                admission = new Switch2BluetoothConnectionAdmission(
                    scanGeneration, entry.Model, entry.ProductId);
                entry.ConnectionAdmissionIssued = true;
                entry.ConnectionCandidatePublished = false;
                entry.ActiveAdmission = admission;
                entry.LastReleasedAdmission = default;
                return true;
            }

            admission = default;
            return false;
        }
    }

    // A logical Joy-Con split/join uses a fresh admission only AFTER the exact
    // former native lease and host slot have retired. It never revives the old
    // admission, clears quarantine, or authorizes a different scan/peer.
    internal bool TryCreateReplacementAdmission(
        in Switch2BluetoothConnectionAdmission released,
        out Switch2BluetoothConnectionAdmission admission)
    {
        admission = default;
        lock (sync)
        {
            if (!scanActive || !released.IsValid || released.ScanGeneration != scanGeneration)
                return false;
            for (int index = 0; index < entries.Length; index++)
            {
                ref CandidateEntry entry = ref entries[index];
                if (!entry.InUse || entry.Quarantined || entry.ConnectionAdmissionIssued ||
                    entry.Host != Switch2AdvertisedHost.ThisHost ||
                    !entry.LastReleasedAdmission.Equals(released)) continue;
                admission = new Switch2BluetoothConnectionAdmission(scanGeneration, entry.Model, entry.ProductId);
                entry.ActiveAdmission = admission;
                entry.LastReleasedAdmission = default;
                entry.ConnectionAdmissionIssued = true;
                entry.ConnectionCandidatePublished = false;
                return true;
            }
            return false;
        }
    }

    private static bool IsValidAdvertisement(
        in Switch2Advertisement advertisement) =>
        advertisement.Host is >= Switch2AdvertisedHost.None and
            <= Switch2AdvertisedHost.ForeignHost &&
        (advertisement.Model, advertisement.ProductId) switch
        {
            (Switch2ControllerModel.JoyCon2Right,
                Switch2AdvertisementCodec.JoyCon2RightProductId) => true,
            (Switch2ControllerModel.JoyCon2Left,
                Switch2AdvertisementCodec.JoyCon2LeftProductId) => true,
            (Switch2ControllerModel.ProController2,
                Switch2AdvertisementCodec.ProController2ProductId) => true,
            _ => false,
        };

    private static Switch2BluetoothCandidateObservation CreateObservation(
        Switch2BluetoothObservationDisposition disposition,
        ulong generation, Switch2BluetoothPeerToken peerToken,
        in Switch2Advertisement advertisement, long observedQpc) =>
        new(disposition, generation, peerToken, advertisement.Model,
            advertisement.ProductId, advertisement.IsWake, observedQpc);

    private struct CandidateEntry
    {
        internal CandidateEntry(Switch2BluetoothPeerToken peerToken,
            Switch2ControllerModel model, ushort productId,
            Switch2AdvertisedHost host, long lastObservedQpc)
        {
            PeerToken = peerToken;
            Model = model;
            ProductId = productId;
            Host = host;
            LastObservedQpc = lastObservedQpc;
            InUse = true;
            Quarantined = false;
            ConnectionAdmissionIssued = false;
            ConnectionCandidatePublished = true;
            ActiveAdmission = default;
            AssociationCommitAuthorized = false;
        }

        internal Switch2BluetoothPeerToken PeerToken;
        internal Switch2ControllerModel Model;
        internal ushort ProductId;
        internal Switch2AdvertisedHost Host;
        internal long LastObservedQpc;
        internal bool InUse;
        internal bool Quarantined;
        internal bool ConnectionAdmissionIssued;
        internal bool ConnectionCandidatePublished;
        internal Switch2BluetoothConnectionAdmission ActiveAdmission;
        internal Switch2BluetoothConnectionAdmission LastReleasedAdmission;
        internal bool AssociationCommitAuthorized;
    }
}
