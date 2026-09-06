/*
DS4Windows
Copyright (C) 2026 hbashton
This program is free software under the GNU General Public License, version 3
or (at your option) any later version. See LICENSE for details.
*/

using DS4Windows.InputDevices;

namespace DS4Windows.Switch2;

/// <summary>Application overrides only; never replaces factory/SPI evidence.</summary>
internal readonly struct Switch2LocalStickCalibrationOverrides
{
    internal Switch2LocalStickCalibrationOverrides(in Switch2StickCalibration left,
        in Switch2StickCalibration right)
    {
        HasLeft = Switch2RawStickCalibrationFileStore.IsValid(left);
        HasRight = Switch2RawStickCalibrationFileStore.IsValid(right);
        Left = HasLeft ? left : default;
        Right = HasRight ? right : default;
    }

    internal bool HasLeft { get; }
    internal bool HasRight { get; }
    internal Switch2StickCalibration Left { get; }
    internal Switch2StickCalibration Right { get; }
}

/// <summary>
/// Immutable application snapshot, loaded off the report path and replaced by
/// exact live calibration receipts at the final publication boundary. Peer IDs
/// stay here and in cold persistence; projected input never contains them.
/// The existing input owner still supplies descriptor/publication admission.
/// </summary>
internal sealed class Switch2RawStickCalibrationBinding
{
    private readonly InputDeviceType type;
    private readonly Switch2Transport transport;
    private readonly ulong leftDevice, leftTransport, rightDevice, rightTransport;
    private readonly Switch2LocalStickCalibrationOverrides pro, left, right;

    private Switch2RawStickCalibrationBinding(InputDeviceType type, Switch2Transport transport,
        ulong leftDevice, ulong leftTransport, ulong rightDevice, ulong rightTransport,
        ISwitch2RawStickCalibrationStore store, Switch2PersistentPeerId leftPeer, Switch2PersistentPeerId rightPeer)
    {
        this.type = type; this.transport = transport;
        this.leftDevice = leftDevice; this.leftTransport = leftTransport;
        this.rightDevice = rightDevice; this.rightTransport = rightTransport;
        Store = store; LeftPeer = leftPeer; RightPeer = rightPeer;
        if (type == InputDeviceType.Switch2Pro)
            pro = new Switch2LocalStickCalibrationOverrides(
                Load(store, leftPeer, Switch2ControllerModel.ProController2, Switch2StickSide.Left),
                Load(store, leftPeer, Switch2ControllerModel.ProController2, Switch2StickSide.Right));
        else
        {
            left = new Switch2LocalStickCalibrationOverrides(
                Load(store, leftPeer, Switch2ControllerModel.JoyCon2Left, Switch2StickSide.Left), default);
            right = new Switch2LocalStickCalibrationOverrides(default,
                Load(store, rightPeer, Switch2ControllerModel.JoyCon2Right, Switch2StickSide.Right));
        }
    }

    internal ISwitch2RawStickCalibrationStore Store { get; }
    internal Switch2PersistentPeerId LeftPeer { get; }
    internal Switch2PersistentPeerId RightPeer { get; }
    internal bool HasLeft => pro.HasLeft || left.HasLeft;
    internal bool HasRight => pro.HasRight || right.HasRight;

    private Switch2RawStickCalibrationBinding(Switch2RawStickCalibrationBinding previous,
        Switch2StickSide side, Switch2StickCalibration? calibration)
    {
        type = previous.type; transport = previous.transport;
        leftDevice = previous.leftDevice; leftTransport = previous.leftTransport;
        rightDevice = previous.rightDevice; rightTransport = previous.rightTransport;
        Store = previous.Store; LeftPeer = previous.LeftPeer; RightPeer = previous.RightPeer;
        pro = previous.pro; left = previous.left; right = previous.right;
        var value = calibration.GetValueOrDefault();
        if (type == InputDeviceType.Switch2Pro)
            pro = new Switch2LocalStickCalibrationOverrides(
                side == Switch2StickSide.Left ? value : previous.pro.Left,
                side == Switch2StickSide.Right ? value : previous.pro.Right);
        else if (side == Switch2StickSide.Left)
            left = new Switch2LocalStickCalibrationOverrides(value, default);
        else
            right = new Switch2LocalStickCalibrationOverrides(default, value);
    }

    internal bool TryGetPeer(Switch2ControllerModel model, Switch2StickSide side,
        out Switch2PersistentPeerId peer)
    {
        peer = default;
        if (!Switch2RawStickCalibrationCollector.SupportsSide(model, side)) return false;
        if (type == InputDeviceType.Switch2Pro && model == Switch2ControllerModel.ProController2)
            peer = LeftPeer;
        else if ((type is InputDeviceType.Switch2JoyConLeft or InputDeviceType.Switch2JoyConJoined) &&
            model == Switch2ControllerModel.JoyCon2Left && side == Switch2StickSide.Left)
            peer = LeftPeer;
        else if ((type is InputDeviceType.Switch2JoyConRight or InputDeviceType.Switch2JoyConJoined) &&
            model == Switch2ControllerModel.JoyCon2Right && side == Switch2StickSide.Right)
            peer = RightPeer;
        return peer.IsValid;
    }

    internal bool TryWithCalibration(Switch2ControllerModel model, Switch2StickSide side,
        Switch2StickCalibration? calibration, out Switch2RawStickCalibrationBinding updated)
    {
        updated = null;
        if (!TryGetPeer(model, side, out _) ||
            (calibration.HasValue && !Switch2RawStickCalibrationFileStore.IsValid(calibration.Value))) return false;
        updated = new Switch2RawStickCalibrationBinding(this, side, calibration);
        return true;
    }

    internal static bool TryLoad(InputDeviceType type, Switch2Transport transport,
        ulong leftDevice, ulong leftTransport, ulong rightDevice, ulong rightTransport,
        ISwitch2RawStickCalibrationStore store, Switch2PersistentPeerId leftPeer,
        Switch2PersistentPeerId rightPeer, out Switch2RawStickCalibrationBinding binding)
    {
        binding = null;
        bool shape = type switch
        {
            InputDeviceType.Switch2Pro or InputDeviceType.Switch2JoyConLeft =>
                leftPeer.IsValid && !rightPeer.IsValid && leftDevice != 0 && leftTransport != 0,
            InputDeviceType.Switch2JoyConRight => !leftPeer.IsValid && rightPeer.IsValid && rightDevice != 0 && rightTransport != 0,
            InputDeviceType.Switch2JoyConJoined => leftPeer.IsValid && rightPeer.IsValid && leftPeer != rightPeer &&
                leftDevice != 0 && leftTransport != 0 && rightDevice != 0 && rightTransport != 0,
            _ => false,
        };
        if (store == null || !shape ||
            (transport != Switch2Transport.BluetoothLe && !(type == InputDeviceType.Switch2Pro && transport == Switch2Transport.Usb)))
            return false;
        // One cold snapshot across both sides, also ordered against late writes
        // belonging to a retired runtime. No runtime gate is held by callers.
        lock (store.SerializationGate)
            binding = new Switch2RawStickCalibrationBinding(type, transport, leftDevice, leftTransport,
                rightDevice, rightTransport, store, leftPeer, rightPeer);
        return true;
    }

    internal Switch2CanonicalInputFrame Apply(in Switch2CanonicalInputFrame frame)
    {
        if (frame.Version != Switch2CanonicalInputFrame.CurrentVersion || !frame.Descriptor.IsValid ||
            frame.Transport != transport) return frame;
        Switch2LocalStickCalibrationOverrides calibration = default;
        if (type == InputDeviceType.Switch2Pro && frame.Model == Switch2ControllerModel.ProController2 &&
            frame.DeviceGeneration == leftDevice && frame.TransportGeneration == leftTransport)
            calibration = pro;
        else if ((type is InputDeviceType.Switch2JoyConLeft or InputDeviceType.Switch2JoyConJoined) &&
            frame.Model == Switch2ControllerModel.JoyCon2Left && frame.DeviceGeneration == leftDevice &&
            frame.TransportGeneration == leftTransport) calibration = left;
        else if ((type is InputDeviceType.Switch2JoyConRight or InputDeviceType.Switch2JoyConJoined) &&
            frame.Model == Switch2ControllerModel.JoyCon2Right && frame.DeviceGeneration == rightDevice &&
            frame.TransportGeneration == rightTransport) calibration = right;
        return calibration.HasLeft || calibration.HasRight ? frame.WithLocalStickCalibration(calibration) : frame;
    }

    internal Switch2ProProfileInputFrame ApplyPro(in Switch2ProProfileInputFrame frame) =>
        type == InputDeviceType.Switch2Pro && frame.HasValidRawStickObservation &&
        frame.Transport == transport && frame.DeviceGeneration == leftDevice &&
        frame.TransportGeneration == leftTransport ? frame.WithLocalStickCalibration(pro) : frame;

    internal Switch2JoyConProfileInputFrame ApplyJoyCon(in Switch2JoyConProfileInputFrame frame)
    {
        bool hasLeft = type is InputDeviceType.Switch2JoyConLeft or InputDeviceType.Switch2JoyConJoined;
        bool hasRight = type is InputDeviceType.Switch2JoyConRight or InputDeviceType.Switch2JoyConJoined;
        if (transport != Switch2Transport.BluetoothLe ||
            (!hasLeft && !hasRight) || frame.LeftSource.IsPresent != hasLeft ||
            frame.RightSource.IsPresent != hasRight ||
            (hasLeft && (!frame.LeftSource.HasValidRawStickObservation ||
                frame.LeftSource.DeviceGeneration != leftDevice ||
                frame.LeftSource.TransportGeneration != leftTransport)) ||
            (hasRight && (!frame.RightSource.HasValidRawStickObservation ||
                frame.RightSource.DeviceGeneration != rightDevice ||
                frame.RightSource.TransportGeneration != rightTransport))) return frame;
        return frame.WithLocalStickCalibration(left, right);
    }

    private static Switch2StickCalibration Load(ISwitch2RawStickCalibrationStore store,
        Switch2PersistentPeerId peer, Switch2ControllerModel model, Switch2StickSide side)
    {
        if (!peer.IsValid) return default;
        try
        {
            return store.TryLoad(peer, model, side, out var value) &&
                Switch2RawStickCalibrationFileStore.IsValid(value) ? value : default;
        }
        catch { return default; } // Optional calibration cannot prevent gameplay.
    }
}
