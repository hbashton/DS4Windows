using System;
using System.Collections.Generic;
using System.ComponentModel;
using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WinWPF.DS4Forms.ViewModels;

public sealed class Switch2ModeShiftEditorViewModel :
    INotifyPropertyChanged
{
    private readonly int device;
    private readonly (Switch2JoyConProfileButton Button, string Label)[]
        buttonDefinitions;

    internal Switch2ModeShiftEditorViewModel(int device,
        (Switch2JoyConProfileButton Button, string Label)[] buttonDefinitions)
    {
        this.device = device;
        this.buttonDefinitions = buttonDefinitions ?? Array.Empty<(
            Switch2JoyConProfileButton Button, string Label)>();
        MappingScopeChoices = new[]
        {
            "Gyro Mouse", "Gyro Mouse Joystick", "Motion steering",
        };
        RefreshButtonChoices();
    }

    public IReadOnlyList<string> MappingScopeChoices { get; }

    public int SelectedMappingScopeIndex
    {
        get => (int)Switch2ModeShift.ResolveEditingScope(device);
        set
        {
            Switch2ModeShiftScope normalized = value switch
            {
                1 => Switch2ModeShiftScope.MouseJoystick,
                2 => Switch2ModeShiftScope.Steering,
                _ => Switch2ModeShiftScope.Mouse,
            };
            if (Switch2ModeShift.ResolveEditingScope(device) == normalized)
            {
                return;
            }
            Switch2ModeShift.SetEditingScope(device, normalized);
            Raise(nameof(SelectedMappingScopeIndex));
            MappingScopeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public IReadOnlyList<Switch2IrGyroButtonChoice>
        HoldButtonChoices { get; private set; }

    public IReadOnlyList<Switch2IrGyroButtonChoice>
        ToggleButtonChoices { get; private set; }

    public bool AutoApplyGyroMouse
    {
        get => Settings.AutoApplyGyroMouse;
        set => Update(autoApplyGyroMouse: value);
    }

    public bool AutoApplyGyroMouseJoystick
    {
        get => Settings.AutoApplyGyroMouseJoystick;
        set => Update(autoApplyGyroMouseJoystick: value);
    }

    public bool AutoApplySteering
    {
        get => Settings.AutoApplySteering;
        set => Update(autoApplySteering: value);
    }

    private Switch2ModeShiftSettings Settings
    {
        get => Global.Switch2ModeShiftSettings[device];
        set => Global.Switch2ModeShiftSettings[device] =
            Switch2ModeShiftSettings.Normalize(value);
    }

    private void UpdateButton(bool hold,
        Switch2JoyConProfileButton button, bool selected)
    {
        Switch2ModeShiftSettings current = Settings;
        Switch2JoyConProfileButton holdButtons = current.HoldButtons;
        Switch2JoyConProfileButton toggleButtons = current.ToggleButtons;
        if (hold)
        {
            holdButtons = selected ? holdButtons | button :
                holdButtons & ~button;
            if (selected)
            {
                toggleButtons &= ~button;
            }
        }
        else
        {
            toggleButtons = selected ? toggleButtons | button :
                toggleButtons & ~button;
            if (selected)
            {
                holdButtons &= ~button;
            }
        }
        Settings = new(holdButtons, toggleButtons,
            current.AutoApplyGyroMouse,
            current.AutoApplyGyroMouseJoystick,
            current.AutoApplySteering);
        RefreshButtonChoices();
    }

    private void Update(bool? autoApplyGyroMouse = null,
        bool? autoApplyGyroMouseJoystick = null,
        bool? autoApplySteering = null)
    {
        Switch2ModeShiftSettings current = Settings;
        Settings = new(current.HoldButtons, current.ToggleButtons,
            autoApplyGyroMouse ?? current.AutoApplyGyroMouse,
            autoApplyGyroMouseJoystick ??
                current.AutoApplyGyroMouseJoystick,
            autoApplySteering ?? current.AutoApplySteering);
        Raise(nameof(AutoApplyGyroMouse));
        Raise(nameof(AutoApplyGyroMouseJoystick));
        Raise(nameof(AutoApplySteering));
    }

    internal void RefreshFromProfile()
    {
        RefreshButtonChoices();
        Raise(nameof(SelectedMappingScopeIndex));
        Raise(nameof(AutoApplyGyroMouse));
        Raise(nameof(AutoApplyGyroMouseJoystick));
        Raise(nameof(AutoApplySteering));
    }

    private void RefreshButtonChoices()
    {
        Switch2ModeShiftSettings settings = Settings;
        HoldButtonChoices = CreateChoices(settings.HoldButtons, hold: true);
        ToggleButtonChoices = CreateChoices(settings.ToggleButtons,
            hold: false);
        Raise(nameof(HoldButtonChoices));
        Raise(nameof(ToggleButtonChoices));
    }

    private IReadOnlyList<Switch2IrGyroButtonChoice> CreateChoices(
        Switch2JoyConProfileButton selected, bool hold)
    {
        var result = new List<Switch2IrGyroButtonChoice>(
            buttonDefinitions.Length);
        foreach ((Switch2JoyConProfileButton button, string label) in
            buttonDefinitions)
        {
            result.Add(new Switch2IrGyroButtonChoice(button, label,
                (selected & button) != 0,
                (changedButton, isSelected) => UpdateButton(hold,
                    changedButton, isSelected)));
        }
        return result;
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this,
        new PropertyChangedEventArgs(name));

    public event PropertyChangedEventHandler PropertyChanged;
    public event EventHandler MappingScopeChanged;
}
