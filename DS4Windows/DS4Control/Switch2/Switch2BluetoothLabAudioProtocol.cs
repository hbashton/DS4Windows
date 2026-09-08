using System;

namespace DS4Windows.Switch2;

internal static class Switch2BluetoothLabAudioProtocol
{
    internal static readonly Guid OutputUuid = new("cc483f51-9258-427d-a939-630c31f72b06");
    internal static readonly Guid InputUuid = new("7492866c-ec3e-4619-8258-32755ffcc0f9");

    internal static bool IsAllowed(string command) => command is
        "status" or "inventory" or "headset-header" or "headset-observe" or "configure-audio" or "audio-state" or "run-plan" or "stop-probe" ||
        Switch2BluetoothLabTone.IsTone(command);

    // Exact observed volatile 0x17/0x02 request, not a generic command API.
    // ndeadly/switch2_controller_research d1c5a7f, commands.md; also observed
    // by switch2mac ea6719f. Payload interpretation and playback remain unproven.
    internal static byte[] CreateSetupRequest() =>
        Convert.FromHexString("179101020007000080BB000002F000");

    internal static bool IsSetupAcknowledged(ReadOnlySpan<byte> value) =>
        value.SequenceEqual(new byte[] { 0x17, 1, 1, 2, 0x10, 0x78, 0, 0 });

    // Exact observed 18/01 request; response payload semantics are unknown.
    internal static byte[] CreateStateRequest() => Convert.FromHexString("1891010100000000");
    internal static bool IsStateAcknowledged(ReadOnlySpan<byte> value) => value.Length == 16 &&
        value.Slice(0, 8).SequenceEqual(new byte[] { 0x18, 1, 1, 1, 0x10, 0x78, 0, 0 });
}
