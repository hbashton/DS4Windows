using System;
using DS4Windows.Switch2;
using static DS4Windows.Global;

namespace DS4Windows;

public partial class ControlService
{
    private JoyConOutputHandoff joyConOutputHandoff;

    internal ISwitch2JoyConOutputHandoff BeginJoyConOutputHandoff(InputControllerSlotToken token)
    {
        JoyConOutputHandoff handoff;
        lock (switch2RuntimeRegistrationService.LifecycleGate)
        {
            if (!token.IsValid || (uint)token.Slot >= outputDevices.Length)
                throw new InvalidOperationException("The selected Joy-Con is no longer available.");
            int slot = token.Slot;
            var snapshot = inputRegistrationTable.GetSnapshot()[slot];
            var owner = switch2ProfileOutputOwners?[slot];
            if (snapshot.Token != token || snapshot.State != InputControllerSlotState.Attached ||
                snapshot.ActionActive || owner?.CanHandoff != true || joyConOutputHandoff != null ||
                !ReferenceEquals(DS4Controllers[slot], token.Registration.Device))
                throw new InvalidOperationException("The selected Joy-Con is changing. Try Link again.");
            OutputDevice output = outputDevices[slot];
            if (!ReferenceEquals(owner.PreparedOutput, output) ||
                output != null && !outputslotMan.IsExactBoundOutput(output, slot))
                throw new InvalidOperationException("The selected Joy-Con output is not ready.");
            handoff = new JoyConOutputHandoff(this, owner, token, output);
            owner.Handoff = handoff;
            joyConOutputHandoff = handoff;
        }
        try
        {
            // Stop accepting old physical feedback before retiring that owner.
            // The virtual USB device and its input writer remain connected.
            if (handoff.Output is ViiperOutDevice viiper && !viiper.TryPauseJoyConFeedback())
                throw new InvalidOperationException("Joy-Con feedback did not drain in time.");
            return handoff;
        }
        catch { handoff.Dispose(); throw; }
    }

    private sealed class JoyConOutputHandoff : ISwitch2JoyConOutputHandoff
    {
        private readonly ControlService service;
        private readonly Switch2ControlServiceProfileStageInverse previousOwner;
        private readonly InputControllerSlotToken previousToken;
        private Switch2RuntimeInputDevice successor;
        private bool held, adopted, disposed;

        internal JoyConOutputHandoff(ControlService service,
            Switch2ControlServiceProfileStageInverse owner,
            InputControllerSlotToken token, OutputDevice output)
        { this.service = service; previousOwner = owner; previousToken = token; Output = output; }

        public int InputSlot => previousToken.Slot;
        internal OutputDevice Output { get; }

        internal void HoldAfterNeutral(OutputDevice output)
        {
            if (disposed || held || !ReferenceEquals(Output, output) ||
                output != null && !service.outputslotMan.TryHoldBoundOutput(output, service.outputDevices, InputSlot))
                throw new InvalidOperationException("The exact Joy-Con output could not be retained.");
            held = true;
        }

        public void PrepareSuccessor(Switch2RuntimeInputDevice device)
        {
            lock (service.switch2RuntimeRegistrationService.LifecycleGate)
            {
                if (disposed || !held || successor != null || device == null)
                    throw new InvalidOperationException("The retained Joy-Con output has no clean predecessor release.");
                successor = device;
            }
        }

        internal bool MatchesSuccessor(int slot, DS4Device device) =>
            !disposed && held && !adopted && InputSlot == slot && ReferenceEquals(successor, device);

        internal void Adopt(Switch2ControlServiceProfileStageInverse owner, DS4Device device)
        {
            if (!MatchesSuccessor(InputSlot, device) ||
                Output != null && !service.outputslotMan.TryAdoptHeldOutput(Output, service.outputDevices, InputSlot,
                    $"{device.DisplayName} [{device.MacAddress}]"))
                throw new InvalidOperationException("The retained Joy-Con output changed before adoption.");
            owner.PreparedOutput = Output;
            adopted = true;
            useDInputOnly[InputSlot] = Output == null;
            activeOutDevType[InputSlot] = Output == null ? OutContType.None :
                service.outputslotMan.GetOutSlotDevice(Output).CurrentType;
            if (Output is ViiperOutDevice viiper)
                viiper.ResumeJoyConFeedback(InputSlot, successor);
            if (Output != null)
                service.LogDebug($"Kept virtual {activeOutDevType[InputSlot].ToDisplayName()} controller in output slot #{service.outputslotMan.GetOutSlotDevice(Output).Index + 1} for the Joy-Con transition.");
        }

        public void Dispose()
        {
            lock (service.switch2RuntimeRegistrationService.LifecycleGate)
            {
                if (disposed) return;
                if (held && !adopted && Output != null)
                    service.outputslotMan.RetireHeldOutput(Output, service.outputDevices, InputSlot);
                else if (!held && Output is ViiperOutDevice viiper)
                    viiper.CancelJoyConFeedbackPause();
                disposed = true;
                previousOwner.Handoff = null;
                if (ReferenceEquals(service.joyConOutputHandoff, this)) service.joyConOutputHandoff = null;
            }
        }
    }
}
