using System;
using System.Collections.Generic;

namespace DS4Windows.Switch2;

public enum Switch2FixtureEvidence : byte
{
    SyntheticProtocolFact = 1,
    ProjectOwnedSanitizedCapture = 2,
    ProjectOwnedDerivedGolden = 3,
}

/// <summary>
/// Structured fixture provenance. Minimally sanitized captures require a
/// source digest and redaction revision; derived goldens instead require a
/// byte-transformation revision and cannot retain the raw-source digest.
/// Arbitrary provenance prose is excluded.
/// </summary>
public sealed class Switch2FixtureSource
{
    private Switch2FixtureSource(Switch2FixtureEvidence evidence,
        string sourceId, string sourceSha256, ushort redactionManifestVersion,
        ushort derivationManifestVersion)
    {
        if (evidence == Switch2FixtureEvidence.SyntheticProtocolFact)
        {
            if (!Switch2FixtureEnvelope.HasOpaqueIdPrefix(sourceId, "fact"))
            {
                throw new ArgumentException(
                    "Fact source ID must use the fact-<128-bit nonce> format.",
                    nameof(sourceId));
            }

            if (!string.IsNullOrEmpty(sourceSha256) ||
                redactionManifestVersion != 0 || derivationManifestVersion != 0)
            {
                throw new ArgumentException(
                    "Synthetic facts cannot claim a capture digest or redaction revision.");
            }
        }
        else if (evidence == Switch2FixtureEvidence.ProjectOwnedSanitizedCapture)
        {
            if (!Switch2FixtureEnvelope.HasOpaqueIdPrefix(sourceId, "capture"))
            {
                throw new ArgumentException(
                    "Capture source ID must use the capture-<128-bit nonce> format.",
                    nameof(sourceId));
            }

            if (!IsSha256(sourceSha256) || redactionManifestVersion == 0)
            {
                throw new ArgumentException(
                    "Sanitized captures require a SHA-256 digest and redaction revision.");
            }

            if (derivationManifestVersion != 0)
            {
                throw new ArgumentException(
                    "Sanitized captures cannot claim a derivation revision.");
            }
        }
        else if (evidence == Switch2FixtureEvidence.ProjectOwnedDerivedGolden)
        {
            if (!Switch2FixtureEnvelope.HasOpaqueIdPrefix(sourceId, "golden"))
            {
                throw new ArgumentException(
                    "Derived source ID must use the golden-<128-bit nonce> format.",
                    nameof(sourceId));
            }

            if (!string.IsNullOrEmpty(sourceSha256) ||
                redactionManifestVersion != 0 || derivationManifestVersion == 0)
            {
                throw new ArgumentException(
                    "Derived golden vectors require a derivation revision and cannot carry a capture digest or redaction revision.");
            }
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(evidence));
        }

        Evidence = evidence;
        SourceId = sourceId;
        SourceSha256 = sourceSha256?.ToUpperInvariant() ?? string.Empty;
        RedactionManifestVersion = redactionManifestVersion;
        DerivationManifestVersion = derivationManifestVersion;
    }

    public Switch2FixtureEvidence Evidence { get; }

    public string SourceId { get; }

    public string SourceSha256 { get; }

    public ushort RedactionManifestVersion { get; }

    public ushort DerivationManifestVersion { get; }

    public static Switch2FixtureSource Synthetic(string factId) =>
        new(Switch2FixtureEvidence.SyntheticProtocolFact, factId,
            string.Empty, 0, 0);

    public static Switch2FixtureSource SanitizedCapture(string captureId,
        string sourceSha256, ushort redactionManifestVersion) =>
        new(Switch2FixtureEvidence.ProjectOwnedSanitizedCapture, captureId,
            sourceSha256, redactionManifestVersion, 0);

    public static Switch2FixtureSource DerivedGolden(string derivedSourceId,
        ushort derivationManifestVersion) =>
        new(Switch2FixtureEvidence.ProjectOwnedDerivedGolden,
            derivedSourceId, string.Empty, 0, derivationManifestVersion);

    private static bool IsSha256(string value)
    {
        if (value == null || value.Length != 64)
        {
            return false;
        }

        foreach (char current in value)
        {
            if (!char.IsAsciiHexDigit(current))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Immutable, privacy-conscious input fixture. Construction clones packet bytes;
/// structured source metadata excludes arbitrary provenance text and common
/// address/serial/key label forms.
/// </summary>
public sealed class Switch2FixtureEnvelope
{
    public const int CurrentSchemaVersion = 1;

    private readonly byte[] bytes;

    private Switch2FixtureEnvelope(string streamId,
        Switch2FixtureSource source,
        Switch2ControllerModel model, string firmware,
        Switch2Transport transport, ulong generation, ulong pairEpoch,
        string hostClockDomain, long hostTimestampFrequency,
        long hostTimestampTicks, Guid? serviceUuid, Guid? characteristicUuid,
        Switch2GattProperty gattProperties, ReadOnlySpan<byte> packetBytes)
    {
        if (!HasOpaqueIdPrefix(streamId, "stream"))
        {
            throw new ArgumentException(
                "Stream ID must use the stream-<128-bit nonce> format.",
                nameof(streamId));
        }

        ArgumentNullException.ThrowIfNull(source);
        if (!IsSafeFirmware(firmware))
        {
            throw new ArgumentException(
                "Firmware must be a safe token or the literal 'unknown'.",
                nameof(firmware));
        }

        if (!HasOpaqueIdPrefix(hostClockDomain, "clock"))
        {
            throw new ArgumentException(
                "Host clock domain must use the clock-<128-bit nonce> format.",
                nameof(hostClockDomain));
        }

        if (hostTimestampFrequency <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(hostTimestampFrequency));
        }

        if (hostTimestampTicks < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(hostTimestampTicks));
        }

        if (generation == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }

        SchemaVersion = CurrentSchemaVersion;
        StreamId = streamId;
        Source = source;
        Model = model;
        Firmware = firmware;
        Transport = transport;
        Direction = Switch2PacketDirection.Input;
        Generation = generation;
        PairEpoch = pairEpoch;
        HostClockDomain = hostClockDomain;
        HostTimestampFrequency = hostTimestampFrequency;
        HostTimestampTicks = hostTimestampTicks;
        ServiceUuid = serviceUuid;
        CharacteristicUuid = characteristicUuid;
        GattProperties = gattProperties;
        bytes = packetBytes.ToArray();
    }

    public int SchemaVersion { get; }

    public string StreamId { get; }

    public Switch2FixtureSource Source { get; }

    public Switch2FixtureEvidence Evidence => Source.Evidence;

    public Switch2ControllerModel Model { get; }

    public string Firmware { get; }

    public Switch2Transport Transport { get; }

    public Switch2PacketDirection Direction { get; }

    public ulong Generation { get; }

    /// <summary>
    /// Caller-assigned epoch for an explicitly coordinated Joy-Con pair.
    /// This is distinct from each device's independent connection generation.
    /// Zero means the fixture is not assigned to a pair.
    /// </summary>
    public ulong PairEpoch { get; }

    public string HostClockDomain { get; }

    public long HostTimestampFrequency { get; }

    public long HostTimestampTicks { get; }

    public Guid? ServiceUuid { get; }

    public Guid? CharacteristicUuid { get; }

    public Switch2GattProperty GattProperties { get; }

    public int PacketLength => bytes.Length;

    public static Switch2FixtureEnvelope CreateUsb(string streamId,
        Switch2FixtureSource source,
        Switch2ControllerModel model, string firmware, ulong generation,
        ulong pairEpoch,
        string hostClockDomain, long hostTimestampFrequency,
        long hostTimestampTicks, ReadOnlySpan<byte> packetBytes) =>
        new(streamId, source, model, firmware,
            Switch2Transport.Usb, generation, pairEpoch, hostClockDomain,
            hostTimestampFrequency, hostTimestampTicks, null, null,
            Switch2GattProperty.None, packetBytes);

    public static Switch2FixtureEnvelope CreateBluetoothLe(string streamId,
        Switch2FixtureSource source,
        Switch2ControllerModel model, string firmware, ulong generation,
        ulong pairEpoch,
        string hostClockDomain, long hostTimestampFrequency,
        long hostTimestampTicks, Guid serviceUuid, Guid characteristicUuid,
        Switch2GattProperty gattProperties, ReadOnlySpan<byte> body) =>
        new(streamId, source, model, firmware,
            Switch2Transport.BluetoothLe, generation, pairEpoch,
            hostClockDomain,
            hostTimestampFrequency, hostTimestampTicks, serviceUuid,
            characteristicUuid, gattProperties, body);

    public byte[] CopyBytes() => (byte[])bytes.Clone();

    internal ReadOnlySpan<byte> PacketBytes => bytes;

    internal static bool HasOpaqueIdPrefix(string value, string prefix)
    {
        string expectedPrefix = prefix + "-";
        if (value == null ||
            !value.StartsWith(expectedPrefix, StringComparison.Ordinal) ||
            value.Length != expectedPrefix.Length + 32)
        {
            return false;
        }

        ReadOnlySpan<char> nonce = value.AsSpan(expectedPrefix.Length);
        bool anyNonZero = false;
        foreach (char current in nonce)
        {
            if (current is not (>= '0' and <= '9') and
                not (>= 'a' and <= 'f'))
            {
                return false;
            }

            anyNonZero |= current != '0';
        }

        return anyNonZero;
    }

    private static bool IsSafeFirmware(string value)
    {
        if (value == "unknown")
        {
            return true;
        }

        if (value == null || !value.StartsWith("fw-", StringComparison.Ordinal) ||
            value.Length > 27)
        {
            return false;
        }

        string[] components = value.Substring(3).Split('.');
        if (components.Length is < 1 or > 4)
        {
            return false;
        }

        foreach (string component in components)
        {
            if (component.Length is < 1 or > 5)
            {
                return false;
            }

            foreach (char current in component)
            {
                if (!char.IsAsciiDigit(current))
                {
                    return false;
                }
            }
        }

        return true;
    }
}

public enum Switch2ReplayFailureKind : byte
{
    None = 0,
    NullFixture,
    UnsupportedSchema,
    ClockFrequencyMismatch,
    TimestampRegression,
    GenerationRegression,
    StreamIdentityMismatch,
    UnsupportedTransport,
    InvalidFramingOrReport,
}

public readonly struct Switch2ReplayFailure
{
    internal Switch2ReplayFailure(int fixtureIndex,
        Switch2ReplayFailureKind kind)
    {
        FixtureIndex = fixtureIndex;
        Kind = kind;
    }

    public int FixtureIndex { get; }

    public Switch2ReplayFailureKind Kind { get; }
}

public readonly struct Switch2ReplayEvent
{
    internal Switch2ReplayEvent(int sequenceIndex,
        Switch2FixtureEnvelope fixture, Switch2DecodedInputReport report,
        bool hasCounterDelta, uint counterDelta,
        Switch2CounterSequenceKind counterSequence)
    {
        SequenceIndex = sequenceIndex;
        Fixture = fixture;
        Report = report;
        HasCounterDelta = hasCounterDelta;
        CounterDelta = counterDelta;
        CounterSequence = counterSequence;
    }

    public int SequenceIndex { get; }

    public Switch2FixtureEnvelope Fixture { get; }

    public Switch2DecodedInputReport Report { get; }

    public bool HasCounterDelta { get; }

    /// <summary>
    /// Unsigned modular delta at the report's native 8- or 32-bit width.
    /// </summary>
    public uint CounterDelta { get; }

    /// <summary>
    /// A half-range modular ordering classification. Forward deltas are raw
    /// counter movement, not a count of packets lost or presented.
    /// </summary>
    public Switch2CounterSequenceKind CounterSequence { get; }
}

public delegate void Switch2ReplayHandler(in Switch2ReplayEvent replayEvent);

/// <summary>
/// Synchronous replay intentionally preserves every fixture in list order. It
/// never coalesces, schedules, sleeps, publishes runtime state or performs I/O.
/// </summary>
public static class Switch2ReplayEngine
{
    public static bool TryReplay(
        IReadOnlyList<Switch2FixtureEnvelope> fixtures,
        Switch2ReplayHandler handler, out Switch2ReplayFailure failure)
    {
        ArgumentNullException.ThrowIfNull(fixtures);
        ArgumentNullException.ThrowIfNull(handler);

        var counters = new Dictionary<CounterKey, uint>();
        var generations = new Dictionary<string, ulong>(StringComparer.Ordinal);
        var clocks = new Dictionary<string, HostClockState>(StringComparer.Ordinal);
        var identities = new Dictionary<StreamGenerationKey, StreamIdentity>();
        int fixtureCount = fixtures.Count;

        for (int index = 0; index < fixtureCount; index++)
        {
            Switch2FixtureEnvelope fixture = fixtures[index];
            if (fixture == null)
            {
                failure = new Switch2ReplayFailure(index,
                    Switch2ReplayFailureKind.NullFixture);
                return false;
            }

            if (fixture.SchemaVersion !=
                Switch2FixtureEnvelope.CurrentSchemaVersion)
            {
                failure = new Switch2ReplayFailure(index,
                    Switch2ReplayFailureKind.UnsupportedSchema);
                return false;
            }

            if (clocks.TryGetValue(fixture.HostClockDomain,
                    out HostClockState previousClock) &&
                fixture.HostTimestampFrequency != previousClock.Frequency)
            {
                failure = new Switch2ReplayFailure(index,
                    Switch2ReplayFailureKind.ClockFrequencyMismatch);
                return false;
            }

            if (clocks.TryGetValue(fixture.HostClockDomain, out previousClock) &&
                fixture.HostTimestampTicks < previousClock.TimestampTicks)
            {
                failure = new Switch2ReplayFailure(index,
                    Switch2ReplayFailureKind.TimestampRegression);
                return false;
            }

            clocks[fixture.HostClockDomain] = new HostClockState(
                fixture.HostTimestampFrequency, fixture.HostTimestampTicks);

            if (generations.TryGetValue(fixture.StreamId,
                    out ulong previousGeneration) &&
                fixture.Generation < previousGeneration)
            {
                failure = new Switch2ReplayFailure(index,
                    Switch2ReplayFailureKind.GenerationRegression);
                return false;
            }

            generations[fixture.StreamId] = fixture.Generation;

            var streamGeneration = new StreamGenerationKey(fixture.StreamId,
                fixture.Generation);
            var identity = new StreamIdentity(fixture);
            if (identities.TryGetValue(streamGeneration,
                    out StreamIdentity previousIdentity) &&
                !identity.Equals(previousIdentity))
            {
                failure = new Switch2ReplayFailure(index,
                    Switch2ReplayFailureKind.StreamIdentityMismatch);
                return false;
            }

            identities[streamGeneration] = identity;

            if (!TryDecode(fixture, out Switch2DecodedInputReport report,
                out Switch2ReplayFailureKind decodeFailure))
            {
                failure = new Switch2ReplayFailure(index, decodeFailure);
                return false;
            }

            var key = new CounterKey(fixture.StreamId, fixture.Generation,
                report.Model, report.Kind);
            bool hasCounterDelta = counters.TryGetValue(key,
                out uint previousCounter);
            uint counterDelta = 0;
            Switch2CounterSequenceKind counterSequence =
                Switch2CounterSequenceKind.First;
            if (hasCounterDelta)
            {
                counterSequence = Switch2CounterSequence.Classify(
                    report.Counter, previousCounter,
                    report.CounterWidthBits, out counterDelta);
            }

            if (Switch2CounterSequence.UsesArrivalOrdering(report.Model,
                    fixture.Transport, report.Kind) || counterSequence !=
                Switch2CounterSequenceKind.BackwardOrOutOfOrder)
            {
                counters[key] = report.Counter;
            }
            var replayEvent = new Switch2ReplayEvent(index, fixture, report,
                hasCounterDelta, counterDelta, counterSequence);
            handler(in replayEvent);
        }

        failure = default;
        return true;
    }

    private static bool TryDecode(Switch2FixtureEnvelope fixture,
        out Switch2DecodedInputReport report,
        out Switch2ReplayFailureKind failure)
    {
        switch (fixture.Transport)
        {
            case Switch2Transport.Usb:
                if (Switch2InputCodec.TryDecodeUsb(fixture.PacketBytes,
                    fixture.Model, out report))
                {
                    failure = Switch2ReplayFailureKind.None;
                    return true;
                }
                failure = Switch2ReplayFailureKind.InvalidFramingOrReport;
                return false;
            case Switch2Transport.BluetoothLe:
                report = default;
                if (fixture.ServiceUuid.HasValue &&
                    fixture.CharacteristicUuid.HasValue &&
                    Switch2InputCodec.TryDecodeBluetoothLe(
                        fixture.ServiceUuid.Value,
                        fixture.CharacteristicUuid.Value,
                        fixture.GattProperties, fixture.PacketBytes,
                        fixture.Model, out report))
                {
                    failure = Switch2ReplayFailureKind.None;
                    return true;
                }
                failure = Switch2ReplayFailureKind.InvalidFramingOrReport;
                return false;
            default:
                report = default;
                failure = Switch2ReplayFailureKind.UnsupportedTransport;
                return false;
        }
    }

    private readonly struct CounterKey : IEquatable<CounterKey>
    {
        public CounterKey(string streamId, ulong generation,
            Switch2ControllerModel model, Switch2InputReportKind kind)
        {
            StreamId = streamId;
            Generation = generation;
            Model = model;
            Kind = kind;
        }

        private string StreamId { get; }

        private ulong Generation { get; }

        private Switch2ControllerModel Model { get; }

        private Switch2InputReportKind Kind { get; }

        public bool Equals(CounterKey other) =>
            Generation == other.Generation && Model == other.Model &&
            Kind == other.Kind &&
            string.Equals(StreamId, other.StreamId,
                StringComparison.Ordinal);

        public override bool Equals(object obj) =>
            obj is CounterKey other && Equals(other);

        public override int GetHashCode() =>
            HashCode.Combine(StringComparer.Ordinal.GetHashCode(StreamId),
                Generation, Model, Kind);
    }

    private readonly struct HostClockState
    {
        public HostClockState(long frequency, long timestampTicks)
        {
            Frequency = frequency;
            TimestampTicks = timestampTicks;
        }

        public long Frequency { get; }

        public long TimestampTicks { get; }
    }

    private readonly struct StreamGenerationKey :
        IEquatable<StreamGenerationKey>
    {
        public StreamGenerationKey(string streamId, ulong generation)
        {
            StreamId = streamId;
            Generation = generation;
        }

        private string StreamId { get; }

        private ulong Generation { get; }

        public bool Equals(StreamGenerationKey other) =>
            Generation == other.Generation &&
            string.Equals(StreamId, other.StreamId, StringComparison.Ordinal);

        public override bool Equals(object obj) =>
            obj is StreamGenerationKey other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(
            StringComparer.Ordinal.GetHashCode(StreamId), Generation);
    }

    private readonly struct StreamIdentity : IEquatable<StreamIdentity>
    {
        public StreamIdentity(Switch2FixtureEnvelope fixture)
        {
            Model = fixture.Model;
            Transport = fixture.Transport;
            Firmware = fixture.Firmware;
            Evidence = fixture.Source.Evidence;
            SourceId = fixture.Source.SourceId;
            SourceSha256 = fixture.Source.SourceSha256;
            RedactionManifestVersion =
                fixture.Source.RedactionManifestVersion;
            DerivationManifestVersion =
                fixture.Source.DerivationManifestVersion;
            HostClockDomain = fixture.HostClockDomain;
            HostTimestampFrequency = fixture.HostTimestampFrequency;
            PairEpoch = fixture.PairEpoch;
        }

        private Switch2ControllerModel Model { get; }

        private Switch2Transport Transport { get; }

        private string Firmware { get; }

        private Switch2FixtureEvidence Evidence { get; }

        private string SourceId { get; }

        private string SourceSha256 { get; }

        private ushort RedactionManifestVersion { get; }

        private ushort DerivationManifestVersion { get; }

        private string HostClockDomain { get; }

        private long HostTimestampFrequency { get; }

        private ulong PairEpoch { get; }

        public bool Equals(StreamIdentity other) => Model == other.Model &&
            Transport == other.Transport && Evidence == other.Evidence &&
            RedactionManifestVersion == other.RedactionManifestVersion &&
            DerivationManifestVersion == other.DerivationManifestVersion &&
            HostTimestampFrequency == other.HostTimestampFrequency &&
            PairEpoch == other.PairEpoch &&
            string.Equals(Firmware, other.Firmware, StringComparison.Ordinal) &&
            string.Equals(SourceId, other.SourceId, StringComparison.Ordinal) &&
            string.Equals(SourceSha256, other.SourceSha256,
                StringComparison.Ordinal) &&
            string.Equals(HostClockDomain, other.HostClockDomain,
                StringComparison.Ordinal);

        public override bool Equals(object obj) =>
            obj is StreamIdentity other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(
            HashCode.Combine(Model, Transport, Firmware, Evidence),
            HashCode.Combine(SourceId, SourceSha256,
                RedactionManifestVersion, DerivationManifestVersion),
            HostClockDomain,
            HostTimestampFrequency, PairEpoch);
    }
}

public readonly struct Switch2JoyConPairSkew
{
    internal Switch2JoyConPairSkew(TimeSpan skew, TimeSpan maximumSkew,
        Switch2JoyConStaleSide staleSide)
    {
        Skew = skew;
        MaximumSkew = maximumSkew;
        StaleSide = staleSide;
    }

    public TimeSpan Skew { get; }

    public TimeSpan MaximumSkew { get; }

    public Switch2JoyConStaleSide StaleSide { get; }

    public bool IsWithinBudget => Skew <= MaximumSkew;
}

public static class Switch2JoyConPairSkewEvaluator
{
    public static bool TryEvaluate(in Switch2ReplayEvent left,
        in Switch2ReplayEvent right, TimeSpan maximumSkew,
        out Switch2JoyConPairSkew skew)
    {
        if (maximumSkew < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumSkew));
        }

        if (left.Report.Model != Switch2ControllerModel.JoyCon2Left ||
            right.Report.Model != Switch2ControllerModel.JoyCon2Right)
        {
            skew = default;
            return false;
        }

        if (!string.Equals(left.Fixture.HostClockDomain,
                right.Fixture.HostClockDomain, StringComparison.Ordinal) ||
            left.Fixture.HostTimestampFrequency !=
                right.Fixture.HostTimestampFrequency ||
            left.Fixture.PairEpoch == 0 ||
            left.Fixture.PairEpoch != right.Fixture.PairEpoch)
        {
            skew = default;
            return false;
        }

        long leftTicks = left.Fixture.HostTimestampTicks;
        long rightTicks = right.Fixture.HostTimestampTicks;
        long difference = leftTicks >= rightTicks ?
            leftTicks - rightTicks : rightTicks - leftTicks;
        if (!TryToTimeSpanTicks(difference,
                left.Fixture.HostTimestampFrequency, out long timeSpanTicks))
        {
            skew = default;
            return false;
        }

        bool overBudget = timeSpanTicks > maximumSkew.Ticks;
        Switch2JoyConStaleSide staleSide = !overBudget || difference == 0 ?
            Switch2JoyConStaleSide.None :
            leftTicks < rightTicks ? Switch2JoyConStaleSide.Left :
            Switch2JoyConStaleSide.Right;

        skew = new Switch2JoyConPairSkew(
            TimeSpan.FromTicks(timeSpanTicks), maximumSkew, staleSide);
        return true;
    }

    private static bool TryToTimeSpanTicks(long hostTicks, long frequency,
        out long timeSpanTicks)
    {
        ulong wholeSeconds = (ulong)(hostTicks / frequency);
        ulong remainder = (ulong)(hostTicks % frequency);
        UInt128 converted = (UInt128)wholeSeconds * TimeSpan.TicksPerSecond +
            (UInt128)remainder * TimeSpan.TicksPerSecond / (ulong)frequency;
        if (converted > long.MaxValue)
        {
            timeSpanTicks = 0;
            return false;
        }

        timeSpanTicks = (long)converted;
        return true;
    }
}
