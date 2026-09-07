namespace Switch2BluetoothAudioProbe;

// Diagnostic-only protocol helpers. No controller, audio, or Windows API access.
// Source: ndeadly/switch2_controller_research d1c5a7f, bluetooth_interface.md.
internal static class ProbeProtocol
{
    internal static readonly Guid ServiceUuid = new("ab7de9be-89fe-49ad-828f-118f09df7fd0");
    internal static readonly Guid HeadsetOutputUuid = new("cc483f51-9258-427d-a939-630c31f72b06");

    internal static byte[] BuildWakeManufacturerValue(ulong address)
    {
        if (address == 0 || address > 0xffffffffffff || address == 0xffffffffffff)
            throw new ArgumentOutOfRangeException(nameof(address), "An exact nonbroadcast 48-bit controller address is required.");
        // Company 0x0553 is supplied separately to WinRT, not duplicated here.
        byte[] value = { 3, 0, 1, 0, 0x80, 0, 0, 0, 0, 0, 0, 1 };
        for (int i = 0; i < 6; i++) value[5 + i] = (byte)(address >> (8 * i));
        return value;
    }

    internal static bool IsProAdvertisement(ReadOnlySpan<byte> value) =>
        value.Length == 24 && value[0] == 1 && value[1] == 0 && value[2] == 3 &&
        value[3] == 0x7e && value[4] == 0x05 && value[5] == 0x69 && value[6] == 0x20 &&
        value[7] == 0 && value[8] == 1 && (value[9] == 0 || value[9] == 0x81) &&
        value[16] == 0x0f && value[17..].IndexOfAnyExcept((byte)0) < 0;
}
