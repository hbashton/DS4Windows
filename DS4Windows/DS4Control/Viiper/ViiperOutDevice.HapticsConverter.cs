using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using DS4Windows.InputDevices;

namespace DS4Windows;

internal static class ViiperHapticsConverter
{
    internal const string Legacy = "box16";
    internal const string SonyBluetooth = "sony-bt-wdl-sinc64-v1";

    internal static bool ShouldRequest(ViiperVirtualDeviceType type, bool gamepadOnly,
        bool physicalSonyVerified, ConnectionType connection, bool edgeCompatible) =>
        (type == ViiperVirtualDeviceType.DualSense || type == ViiperVirtualDeviceType.DualSenseEdge) &&
        !gamepadOnly && physicalSonyVerified && connection == ConnectionType.BT && edgeCompatible;

    // Older brokers ignore unknown create options. Only the broker's actual
    // selection, never our request or the raw-input alias, establishes the DSP.
    internal static string ParseSelection(JsonElement metadata, bool requested)
    {
        if (metadata.ValueKind == JsonValueKind.Undefined || metadata.ValueKind == JsonValueKind.Null)
            return Legacy;
        if (metadata.ValueKind != JsonValueKind.Object)
            throw new IOException("VIIPER returned invalid haptics converter metadata.");
        if (!metadata.TryGetProperty("hapticsConverter", out JsonElement value))
            return Legacy;
        if (value.ValueKind != JsonValueKind.String)
            throw new IOException("VIIPER returned an invalid haptics converter selection.");
        string selected = value.GetString();
        if (selected == Legacy || requested && selected == SonyBluetooth)
            return selected;
        throw new IOException("VIIPER returned an unsupported or unsolicited haptics converter.");
    }

    internal static bool CanReuse(string selected, bool previouslyRequested, bool desired) =>
        selected == SonyBluetooth ? desired : !desired || previouslyRequested;

    internal sealed class CreateOptions
    {
        [JsonPropertyName("hapticsConverter")]
        public string HapticsConverter { get; } = SonyBluetooth;
    }
}

public sealed partial class ViiperOutDevice
{
    private string activeHapticsConverter = ViiperHapticsConverter.Legacy;
    private bool requestedSonyBluetoothHaptics;
    private DualSenseDevice hapticsConverterTarget;

    internal string ActiveHapticsConverter => Volatile.Read(ref activeHapticsConverter);
    internal bool NegotiatesHapticsConverter => IsDualSenseType() && !gamepadOnly;

    // Cold create/rebind only. This may consult the existing cached physical
    // identity verifier; no PnP or management request runs in report publication.
    private bool WantsSonyBluetoothHaptics(DualSenseDevice target)
    {
        return NegotiatesHapticsConverter && target?.ConnectionType == ConnectionType.BT &&
            ViiperHapticsConverter.ShouldRequest(viiperType,
            gamepadOnly, IsCurrentPhysicalSonyDualSense(target), target.ConnectionType,
            viiperType != ViiperVirtualDeviceType.DualSenseEdge ||
                target.SubType == DualSenseDevice.DeviceSubType.DSEdge);
    }

    internal bool CanReuseForPhysicalController(int index) =>
        CanReuseHapticsConverterForTarget(ResolvePhysicalControllerTarget(index));

    private bool CanReuseHapticsConverterForTarget(DualSenseDevice target) =>
        !NegotiatesHapticsConverter || ViiperHapticsConverter.CanReuse(ActiveHapticsConverter,
            requestedSonyBluetoothHaptics, WantsSonyBluetoothHaptics(target));

    private bool IsHapticsConverterTargetCurrent(DS4Device target) =>
        ActiveHapticsConverter != ViiperHapticsConverter.SonyBluetooth ||
        ReferenceEquals(target, Volatile.Read(ref hapticsConverterTarget)) &&
        target?.ConnectionType == ConnectionType.BT;

    internal int GetHapticsCompatibleFeedbackLength(DS4Device target,
        int length, bool freshNativeOutput) => IsHapticsConverterTargetCurrent(target) ? length :
        freshNativeOutput ? Math.Min(length, DualSenseBluetoothHapticsReportOffset) : 0;

    private void ApproveHapticsConverterTarget(DualSenseDevice verifiedTarget)
    {
        Volatile.Write(ref hapticsConverterTarget,
            ActiveHapticsConverter == ViiperHapticsConverter.SonyBluetooth ?
                verifiedTarget : null);
    }
}
