using System;
using System.Text.Json;

namespace DS4Windows.Switch2;

/// <summary>Validates operation completion, not physical audio playback.</summary>
internal static class Switch2BluetoothLabReceiverResult
{
    internal static bool IsCompleted(JsonElement result, Switch2BluetoothLabReceiverPlan plan)
    {
        if (!TryGetReceiverOperation(result, plan, out JsonElement operation) ||
            !IsAcknowledgedOperation(operation, out bool setup) ||
            !operation.TryGetProperty("Receiver", out JsonElement commands) ||
            !IsAcknowledgedOperation(commands, out bool commandSetup) || setup != commandSetup ||
            setup != Array.Exists(plan.Commands, request => request[0] == 0x17))
            return false;

        // A successful outer wrapper cannot conceal a missing/partial stream.
        // Command-only plans legitimately have no setup ACK and a null Audio.
        if (plan.Audio == null)
            return !operation.TryGetProperty("Audio", out JsonElement absentAudio) ||
                absentAudio.ValueKind == JsonValueKind.Null;

        return operation.TryGetProperty("Audio", out JsonElement audio) &&
            audio.ValueKind == JsonValueKind.Object && OptionalCompletionFieldsAreValid(audio) &&
            audio.TryGetProperty("Sent", out JsonElement sent) && sent.ValueKind == JsonValueKind.Number &&
            sent.TryGetInt32(out int count) && count == plan.Audio.Packets.Length;
    }

    internal static bool TryGetReceiverOperation(JsonElement result, Switch2BluetoothLabReceiverPlan plan,
        out JsonElement operation)
    {
        operation = default;
        if (plan == null || result.ValueKind != JsonValueKind.Object || HasError(result) ||
            !OptionalCompletionFieldsAreValid(result)) return false;

        if (!plan.HeadsetNotifications)
        {
            operation = result;
            return true;
        }

        // Only the headset window owns these completion fields. Never accept
        // an identically named diagnostic or inner field as cleanup evidence.
        if (!IsSuccess(result, "Status") || !IsSuccess(result, "RestoreHeadset") ||
            !IsSuccess(result, "RestoreCommonInput") ||
            !result.TryGetProperty("LabAudioFenced", out JsonElement fenced) ||
            fenced.ValueKind != JsonValueKind.False ||
            !result.TryGetProperty("Receiver", out JsonElement receiver) ||
            receiver.ValueKind != JsonValueKind.Object) return false;
        operation = receiver;
        return true;
    }

    internal static bool HasError(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in value.EnumerateObject())
            {
                // CCCD reads can be rejected while the existing lease still
                // proves notification ownership. Before is diagnostic only.
                if (property.NameEquals("Before")) continue;
                if (property.NameEquals("Error") &&
                    property.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)) return true;
                if (HasError(property.Value)) return true;
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray())
                if (HasError(item)) return true;
        }
        return false;
    }

    private static bool IsAcknowledgedOperation(JsonElement operation, out bool setup)
    {
        setup = false;
        if (operation.ValueKind != JsonValueKind.Object || !OptionalCompletionFieldsAreValid(operation) ||
            !operation.TryGetProperty("CommandsAcknowledged", out JsonElement acknowledged) ||
            acknowledged.ValueKind != JsonValueKind.True ||
            !operation.TryGetProperty("SetupAcknowledged", out JsonElement configured) ||
            configured.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        setup = configured.ValueKind == JsonValueKind.True;
        return true;
    }

    private static bool OptionalCompletionFieldsAreValid(JsonElement value)
    {
        foreach (string name in new[] { "Status", "RestoreHeadset", "RestoreCommonInput" })
            if (value.TryGetProperty(name, out _) && !IsSuccess(value, name)) return false;
        return !value.TryGetProperty("LabAudioFenced", out JsonElement fenced) || fenced.ValueKind == JsonValueKind.False;
    }

    private static bool IsSuccess(JsonElement value, string name) =>
        value.TryGetProperty(name, out JsonElement status) &&
        status.ValueKind == JsonValueKind.String && status.GetString() == "Success";
}
