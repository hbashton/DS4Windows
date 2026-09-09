using System;
using System.Collections.Generic;
using System.ComponentModel;
using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WinWPF.DS4Forms.ViewModels;

/// <summary>Cold profile editing only; runtime reads the existing profile fields.</summary>
public sealed class Switch2DualGyroEditorViewModel : INotifyPropertyChanged
{
    private readonly int device;
    private readonly (Switch2JoyConProfileButton Button, string Label)[] definitions;

    internal Switch2DualGyroEditorViewModel(int device,
        (Switch2JoyConProfileButton Button, string Label)[] buttonDefinitions)
    {
        this.device = device;
        definitions = buttonDefinitions;
        RefreshFromProfile();
    }

    public bool Enabled
    {
        get => Global.Switch2DualJoyConGyroFusionEnabled[device];
        set
        {
            Global.Switch2DualJoyConGyroFusionEnabled[device] = value;
            Raise(nameof(Enabled));
        }
    }

    public bool CanCombineBoth => Global.Switch2DualJoyConGyroMode[device] ==
        Switch2DualGyroMode.SingleSideToggle;

    public string ModeDescription => ModeIndex switch
    {
        1 => "Only one hand aims at a time, starting with your preferred hand.",
        2 => "Both hands start on. Pause either hand when you need to reposition it.",
        _ => "Your preferred hand leads. The other helps when moving the same way.",
    };

    public string ButtonActionDescription => (ModeIndex, ActivationModeIndex) switch
    {
        (2, 1) => "Both hands start on. Press a selected button to pause that Joy-Con; press again to resume its aiming.",
        (2, _) => "Both hands start on. Hold a selected button to pause that Joy-Con; release to resume its aiming. These are pause buttons, not hold-to-aim buttons.",
        (1, 1) => "Press a selected button on either Joy-Con to swap the aiming hand; press again to swap back. These buttons do not select their own hand.",
        (1, _) => "Hold a selected button on either Joy-Con to swap the aiming hand; release to swap back. These buttons do not select their own hand.",
        (_, 1) => "Press a selected button on either Joy-Con to swap the lead hand; press again to swap back. These buttons do not select their own hand.",
        _ => "Hold a selected button on either Joy-Con to swap the lead hand; release to swap back. These buttons do not select their own hand.",
    };

    public string ButtonSelectionHint => ModeIndex == 2
        ? "Any checked button on that Joy-Con works. Release all checked buttons to end a hold."
        : "Use one Joy-Con's swap buttons at a time. Release all checked buttons on it to end a hold.";

    public int ModeIndex
    {
        get => Global.Switch2DualJoyConGyroMode[device] switch
        {
            Switch2DualGyroMode.SwitchGyroSide => 1,
            Switch2DualGyroMode.SingleSideToggle => 2,
            _ => 0,
        };
        set
        {
            Global.Switch2DualJoyConGyroMode[device] = value switch
            {
                1 => Switch2DualGyroMode.SwitchGyroSide,
                2 => Switch2DualGyroMode.SingleSideToggle,
                _ => Switch2DualGyroMode.SwitchDominantSide,
            };
            if (Global.Switch2DualJoyConGyroMode[device] !=
                    Switch2DualGyroMode.SingleSideToggle &&
                Global.Switch2DualJoyConGyroDominantSide[device] ==
                    Switch2DualGyroDominantSide.None)
            {
                Global.Switch2DualJoyConGyroDominantSide[device] =
                    Switch2DualGyroDominantSide.Right;
                Raise(nameof(DominantSideIndex));
            }
            Raise(nameof(ModeIndex));
            Raise(nameof(ModeDescription));
            Raise(nameof(ButtonActionDescription));
            Raise(nameof(ButtonSelectionHint));
            Raise(nameof(CanCombineBoth));
        }
    }

    public int DominantSideIndex
    {
        get => Global.Switch2DualJoyConGyroDominantSide[device] switch
        {
            Switch2DualGyroDominantSide.Left => 0,
            Switch2DualGyroDominantSide.None => 2,
            _ => 1,
        };
        set
        {
            Global.Switch2DualJoyConGyroDominantSide[device] = value switch
            {
                0 => Switch2DualGyroDominantSide.Left,
                2 when Global.Switch2DualJoyConGyroMode[device] ==
                    Switch2DualGyroMode.SingleSideToggle =>
                        Switch2DualGyroDominantSide.None,
                _ => Switch2DualGyroDominantSide.Right,
            };
            Raise(nameof(DominantSideIndex));
        }
    }

    public int ActivationModeIndex
    {
        get => Global.Switch2DualJoyConGyroActivationMode[device] ==
            Switch2DualGyroActivationMode.Toggle ? 1 : 0;
        set
        {
            Global.Switch2DualJoyConGyroActivationMode[device] = value == 1 ?
                Switch2DualGyroActivationMode.Toggle :
                Switch2DualGyroActivationMode.Hold;
            Raise(nameof(ActivationModeIndex));
            Raise(nameof(ButtonActionDescription));
        }
    }

    public IReadOnlyList<Switch2IrGyroButtonChoice> LeftActivationChoices { get; private set; }

    public IReadOnlyList<Switch2IrGyroButtonChoice> RightActivationChoices { get; private set; }

    internal void RefreshFromProfile()
    {
        // Profile loading and presets reuse this editor after replacing the
        // backing fields. Recreate checked states without calling setters or
        // writing any profile field, then notify live or rebound controls.
        LeftActivationChoices = CreateChoices(definitions, left: true);
        RightActivationChoices = CreateChoices(definitions, left: false);
        Raise(nameof(LeftActivationChoices));
        Raise(nameof(RightActivationChoices));
        Raise(nameof(Enabled));
        Raise(nameof(ModeIndex));
        Raise(nameof(ModeDescription));
        Raise(nameof(ButtonActionDescription));
        Raise(nameof(ButtonSelectionHint));
        Raise(nameof(CanCombineBoth));
        Raise(nameof(DominantSideIndex));
        Raise(nameof(ActivationModeIndex));
    }

    private IReadOnlyList<Switch2IrGyroButtonChoice> CreateChoices(
        (Switch2JoyConProfileButton Button, string Label)[] definitions, bool left)
    {
        var choices = new List<Switch2IrGyroButtonChoice>();
        foreach ((Switch2JoyConProfileButton button, string label) in definitions)
        {
            var oppositeSide = left ?
                Switch2JoyConProfileButton.RightIrSensor |
                    Switch2JoyConProfileButton.RightRailSL |
                    Switch2JoyConProfileButton.RightRailSR :
                Switch2JoyConProfileButton.LeftIrSensor |
                    Switch2JoyConProfileButton.LeftRailSL |
                    Switch2JoyConProfileButton.LeftRailSR;
            if ((button & oppositeSide) != 0)
            {
                continue;
            }
            var selected = left ? Global.Switch2DualJoyConGyroLeftActivationButton[device] :
                Global.Switch2DualJoyConGyroRightActivationButton[device];
            choices.Add(new Switch2IrGyroButtonChoice(button, label,
                (selected & button) != 0,
                (changed, isSelected) => UpdateButton(left, changed, isSelected)));
        }
        return choices;
    }

    private void UpdateButton(bool left, Switch2JoyConProfileButton button,
        bool selected)
    {
        var settings = left ? Global.Switch2DualJoyConGyroLeftActivationButton :
            Global.Switch2DualJoyConGyroRightActivationButton;
        settings[device] = selected ? settings[device] | button :
            settings[device] & ~button;
    }

    private void Raise(string property) => PropertyChanged?.Invoke(this,
        new PropertyChangedEventArgs(property));

    public event PropertyChangedEventHandler PropertyChanged;
}
