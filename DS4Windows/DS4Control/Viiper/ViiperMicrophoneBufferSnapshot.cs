/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Globalization;
using System.Text.Json;

namespace DS4Windows
{
    /// <summary>
    /// The latest diagnostic snapshot of VIIPER's virtual microphone jitter
    /// buffer. Every property is nullable so an older VIIPER build, a missing
    /// field, or one malformed field cannot interfere with capture-interface
    /// activity detection.
    /// </summary>
    internal sealed class ViiperMicrophoneBufferSnapshot
    {
        internal static ViiperMicrophoneBufferSnapshot Empty { get; } =
            new ViiperMicrophoneBufferSnapshot();

        internal long? QueuedBytes { get; private set; }
        internal long? TargetBytes { get; private set; }
        internal long? FilteredBytes { get; private set; }
        internal bool? Primed { get; private set; }
        internal ulong? Underruns { get; private set; }
        internal ulong? Reprimes { get; private set; }
        internal ulong? DroppedBytes { get; private set; }
        internal ulong? ZeroPackets { get; private set; }
        internal ulong? OverflowEvents { get; private set; }
        internal long? LowWaterBytes { get; private set; }
        internal long? HighWaterBytes { get; private set; }
        internal ulong? QueueFrames { get; private set; }
        internal long? QueueMinGapMicroseconds { get; private set; }
        internal long? QueueMaxGapMicroseconds { get; private set; }
        internal long? ReadMinGapMicroseconds { get; private set; }
        internal long? ReadMaxGapMicroseconds { get; private set; }

        internal static ViiperMicrophoneBufferSnapshot Parse(
            JsonElement deviceSpecific)
        {
            var snapshot = new ViiperMicrophoneBufferSnapshot();
            if (deviceSpecific.ValueKind != JsonValueKind.Object)
            {
                return snapshot;
            }

            snapshot.QueuedBytes = ReadInt64(deviceSpecific,
                "queuedMicrophoneBytes");
            snapshot.TargetBytes = ReadInt64(deviceSpecific,
                "microphoneQueueTargetBytes");
            snapshot.FilteredBytes = ReadInt64(deviceSpecific,
                "microphoneFilteredQueueBytes");
            snapshot.Primed = ReadBoolean(deviceSpecific,
                "microphoneQueuePrimed");
            snapshot.Underruns = ReadUInt64(deviceSpecific,
                "microphoneUnderruns");
            snapshot.Reprimes = ReadUInt64(deviceSpecific,
                "microphoneReprimes");
            snapshot.DroppedBytes = ReadUInt64(deviceSpecific,
                "microphoneDroppedBytes");
            snapshot.ZeroPackets = ReadUInt64(deviceSpecific,
                "microphoneZeroPackets");
            snapshot.OverflowEvents = ReadUInt64(deviceSpecific,
                "microphoneOverflowEvents");
            snapshot.LowWaterBytes = ReadInt64(deviceSpecific,
                "microphoneLowWaterBytes");
            snapshot.HighWaterBytes = ReadInt64(deviceSpecific,
                "microphoneHighWaterBytes");
            snapshot.QueueFrames = ReadUInt64(deviceSpecific,
                "microphoneQueueFrames");
            snapshot.QueueMinGapMicroseconds = ReadInt64(deviceSpecific,
                "microphoneQueueMinGapUS");
            snapshot.QueueMaxGapMicroseconds = ReadInt64(deviceSpecific,
                "microphoneQueueMaxGapUS");
            snapshot.ReadMinGapMicroseconds = ReadInt64(deviceSpecific,
                "microphoneReadMinGapUS");
            snapshot.ReadMaxGapMicroseconds = ReadInt64(deviceSpecific,
                "microphoneReadMaxGapUS");
            return snapshot;
        }

        internal string ToLogFields()
        {
            return
                $"virtualMicQueuedBytes={Format(QueuedBytes)} " +
                $"virtualMicTargetBytes={Format(TargetBytes)} " +
                $"virtualMicFilteredBytes={Format(FilteredBytes)} " +
                $"virtualMicPrimed={Format(Primed)} " +
                $"virtualMicUnderruns={Format(Underruns)} " +
                $"virtualMicReprimes={Format(Reprimes)} " +
                $"virtualMicDroppedBytes={Format(DroppedBytes)} " +
                $"virtualMicZeroPackets={Format(ZeroPackets)} " +
                $"virtualMicOverflowEvents={Format(OverflowEvents)} " +
                $"virtualMicWaterBytes={Format(LowWaterBytes)}/{Format(HighWaterBytes)} " +
                $"virtualMicQueueFrames={Format(QueueFrames)} " +
                $"virtualMicQueueGapUs={Format(QueueMinGapMicroseconds)}/{Format(QueueMaxGapMicroseconds)} " +
                $"virtualMicPacketReadGapUs={Format(ReadMinGapMicroseconds)}/{Format(ReadMaxGapMicroseconds)}";
        }

        private static long? ReadInt64(JsonElement parent, string name)
        {
            if (!TryGetProperty(parent, name, out JsonElement value))
            {
                return null;
            }

            if (value.ValueKind == JsonValueKind.Number &&
                value.TryGetInt64(out long numeric))
            {
                return numeric;
            }

            return value.ValueKind == JsonValueKind.String &&
                long.TryParse(value.GetString(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out long parsed) ?
                parsed : null;
        }

        private static ulong? ReadUInt64(JsonElement parent, string name)
        {
            if (!TryGetProperty(parent, name, out JsonElement value))
            {
                return null;
            }

            if (value.ValueKind == JsonValueKind.Number &&
                value.TryGetUInt64(out ulong numeric))
            {
                return numeric;
            }

            return value.ValueKind == JsonValueKind.String &&
                ulong.TryParse(value.GetString(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out ulong parsed) ?
                parsed : null;
        }

        private static bool? ReadBoolean(JsonElement parent, string name)
        {
            if (!TryGetProperty(parent, name, out JsonElement value))
            {
                return null;
            }

            if (value.ValueKind == JsonValueKind.True ||
                value.ValueKind == JsonValueKind.False)
            {
                return value.GetBoolean();
            }

            if (value.ValueKind == JsonValueKind.String &&
                bool.TryParse(value.GetString(), out bool parsed))
            {
                return parsed;
            }

            if (value.ValueKind == JsonValueKind.Number &&
                value.TryGetInt32(out int numeric) &&
                (numeric == 0 || numeric == 1))
            {
                return numeric == 1;
            }

            return null;
        }

        private static bool TryGetProperty(JsonElement parent, string name,
            out JsonElement value)
        {
            if (parent.TryGetProperty(name, out value))
            {
                return true;
            }

            foreach (JsonProperty property in parent.EnumerateObject())
            {
                if (string.Equals(property.Name, name,
                    StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }

            value = default;
            return false;
        }

        private static string Format(long? value)
        {
            return value?.ToString(CultureInfo.InvariantCulture) ?? "n/a";
        }

        private static string Format(ulong? value)
        {
            return value?.ToString(CultureInfo.InvariantCulture) ?? "n/a";
        }

        private static string Format(bool? value)
        {
            return value.HasValue ?
                (value.Value ? "true" : "false") : "n/a";
        }
    }

    internal sealed class ViiperMicrophoneInterfaceStatus
    {
        internal ViiperMicrophoneInterfaceStatus(bool isActive,
            ViiperMicrophoneBufferSnapshot buffer)
        {
            IsActive = isActive;
            Buffer = buffer ?? ViiperMicrophoneBufferSnapshot.Empty;
        }

        internal bool IsActive { get; }
        internal ViiperMicrophoneBufferSnapshot Buffer { get; }
    }
}
