using System;
using System.Threading;
using DS4Windows.InputDevices;

namespace DS4Windows.Switch2;

public sealed partial class Switch2RuntimeInputDevice
{
    private string profileLinkId;

    public override string ProfileLinkId => Volatile.Read(ref profileLinkId);
    public override string DisplayIdentity => ProfileLinkId is string id
        ? $"ID {id}" : "ID unavailable";

    // Bind once before slot/profile publication. These are existing install-
    // local pseudonyms, never Bluetooth addresses, device paths or bond keys.
    internal bool TryBindProfileIdentity(Switch2PersistentPeerId leftPeerId,
        Switch2PersistentPeerId rightPeerId = default)
    {
        lock (publicationGate)
        {
            if (runtimeState != Switch2RuntimeInputDeviceState.Created ||
                publicationInProgress || profileLinkId != null ||
                !Switch2ProfileIdentity.TryCreate(DeviceType, transport,
                    leftPeerId, rightPeerId, out string id))
                return false;
            Volatile.Write(ref profileLinkId, id);
            return true;
        }
    }
}

internal static class Switch2ProfileIdentity
{
    internal static bool TryCreate(InputDeviceType model, Switch2Transport transport,
        Switch2PersistentPeerId left, Switch2PersistentPeerId right, out string id)
    {
        id = null;
        if (transport != Switch2Transport.BluetoothLe &&
            (transport != Switch2Transport.Usb || model != InputDeviceType.Switch2Pro))
            return false;
        string role = model switch
        {
            InputDeviceType.Switch2Pro when left.IsValid && !right.IsValid => "PRO",
            InputDeviceType.Switch2JoyConLeft when left.IsValid && !right.IsValid => "L",
            InputDeviceType.Switch2JoyConRight when !left.IsValid && right.IsValid => "R",
            InputDeviceType.Switch2JoyConJoined when left.IsValid && right.IsValid && left != right => "PAIR",
            _ => null,
        };
        if (role == null) return false;
        Span<byte> first = stackalloc byte[Switch2PersistentPeerId.EncodedLength];
        if (!(left.IsValid ? left : right).TryWrite(first)) return false;
        string prefix = $"S2-{(transport == Switch2Transport.Usb ? "USB" : "BT")}-{role}-";
        id = prefix + Convert.ToHexString(first);
        if (model == InputDeviceType.Switch2JoyConJoined)
        {
            Span<byte> second = stackalloc byte[Switch2PersistentPeerId.EncodedLength];
            if (!right.TryWrite(second)) { id = null; return false; }
            // Preserve both complete peer IDs in left/right order. Pair IDs
            // and epochs change during auto-linking; physical membership does not.
            id += "-" + Convert.ToHexString(second);
        }
        return true;
    }
}
