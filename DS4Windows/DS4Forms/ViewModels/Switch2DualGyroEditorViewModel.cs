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
