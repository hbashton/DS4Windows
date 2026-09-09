using DS4Windows.InputDevices;

namespace DS4Windows;

/// <summary>
/// Fixed Xbox impulse vibration, independent of Trigger Lab's stored designs
/// and activation state. Uses the documented 0x26 ten-zone vibration program.
/// </summary>
internal static class XboxImpulseTriggerEffect
{
    internal static DualSenseDevice.TriggerEffectData Encode(byte magnitude)
    {
        DualSenseDevice.TriggerEffectData result = default;
        if (magnitude == 0)
        {
            result.ChangeRaw(0x05, 0, 0, 0, 0, 0, 0, 0);
            return result;
        }
        uint level = (uint)((magnitude * 8 + 254) / 255 - 1);
        uint strengths = 0;
        for (int zone = 0; zone < 10; zone++) strengths |= level << (3 * zone);
        result.ChangeRaw(0x26, 0xFF, 0x03, (byte)strengths,
            (byte)(strengths >> 8), (byte)(strengths >> 16),
            (byte)(strengths >> 24), 28);
        return result;
    }
}
