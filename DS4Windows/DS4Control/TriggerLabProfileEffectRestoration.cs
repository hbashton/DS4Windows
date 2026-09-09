using System;
using DS4Windows.InputDevices;

namespace DS4Windows;

/// <summary>
/// Restores only profile trigger effects after a preview or a Lab edit. Does
/// not save a profile, change output devices, or reset other controller state.
/// </summary>
internal static class TriggerLabProfileEffectRestoration
{
    internal static void ApplyToDevice(DualSenseDevice device,
        TriggerLabProfileSettings settings, TriggerOutputSettings left,
        TriggerOutputSettings right)
    {
        if (device == null) return;
        Apply(settings, left, right,
            (trigger, effect, active) => TriggerLabEffectEncoder.ApplyToDevice(
                device, trigger, effect, active),
            device.PrepareTriggerEffect);
    }

    // Callbacks keep the production decision independently testable without
    // constructing a controller, opening HID, or writing a user's profile.
    internal static void Apply(TriggerLabProfileSettings settings,
        TriggerOutputSettings left, TriggerOutputSettings right,
        Action<TriggerId, TriggerLabEffectSettings, bool> applyLab,
        Action<TriggerId, TriggerEffects, TriggerEffectSettings> applyLegacy)
    {
        ArgumentNullException.ThrowIfNull(applyLab);
        ArgumentNullException.ThrowIfNull(applyLegacy);
        // Match ControlService.CheckProfileOptions: an active Lab design owns
        // the pair, including explicit Off on its inactive side. Without that
        // override, restore the ordinary per-trigger profile settings instead
        // of overwriting them with Lab Off. Do not normalize/mutate a design
        // while restoring physical output.
        if (settings?.HasActiveOverride == true)
        {
            applyLab(TriggerId.LeftTrigger, settings.Left, settings.LeftActive);
            applyLab(TriggerId.RightTrigger, settings.Right, settings.RightActive);
        }
        else
        {
            ArgumentNullException.ThrowIfNull(left);
            ArgumentNullException.ThrowIfNull(right);
            applyLegacy(TriggerId.LeftTrigger, left.TriggerEffect, left.TrigEffectSettings);
            applyLegacy(TriggerId.RightTrigger, right.TriggerEffect, right.TrigEffectSettings);
        }
    }
}
