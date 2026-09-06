using System;
using System.Collections.Generic;
using System.ComponentModel;
using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WinWPF.DS4Forms.ViewModels;

public sealed class Switch2GyroLockEditorViewModel : INotifyPropertyChanged
{
    private readonly int device;
    private readonly (Switch2JoyConProfileButton Button, string Label)[]
        buttonDefinitions;
    private int selectedModeIndex;

    internal Switch2GyroLockEditorViewModel(int device,
        (Switch2JoyConProfileButton Button, string Label)[] buttonDefinitions)
    {
        this.device = device;
        this.buttonDefinitions = buttonDefinitions ?? Array.Empty<(
            Switch2JoyConProfileButton Button, string Label)>();
        ModeChoices = new[] { "Gyro Mouse", "Gyro Mouse Joystick" };
        RefreshButtonChoices();
    }

    public IReadOnlyList<string> ModeChoices { get; }

    public int SelectedModeIndex
    {
        get => selectedModeIndex;
        set
        {
            int normalized = value == 1 ? 1 : 0;
            if (selectedModeIndex == normalized)
            {
                return;
            }
            selectedModeIndex = normalized;
            RefreshButtonChoices();
            Raise(nameof(SelectedModeIndex));
        }
    }

    public IReadOnlyList<Switch2IrGyroButtonChoice>
        HoldButtonChoices { get; private set; }

    public IReadOnlyList<Switch2IrGyroButtonChoice>
        ToggleButtonChoices { get; private set; }

    private GyroOutMode SelectedMode => selectedModeIndex == 1 ?
        GyroOutMode.MouseJoystick : GyroOutMode.Mouse;

    private Switch2GyroLockBindingTable Table
    {
        get
        {
            Switch2GyroLockBindingTable table =
                Global.Switch2GyroLockBindings[device];
            if (table == null)
            {
                table = new Switch2GyroLockBindingTable();
                Global.Switch2GyroLockBindings[device] = table;
            }
            return table;
        }
    }

    private void UpdateButton(bool hold,
        Switch2JoyConProfileButton button, bool selected)
    {
        Switch2GyroLockBinding current = Table.Get(SelectedMode);
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
        Table.TrySet(SelectedMode, new(holdButtons, toggleButtons));
        RefreshButtonChoices();
    }

    private void RefreshButtonChoices()
    {
        Switch2GyroLockBinding binding =
            Global.Switch2GyroLockBindings[device]?.Get(SelectedMode) ?? default;
        HoldButtonChoices = CreateChoices(binding.HoldButtons, hold: true);
        ToggleButtonChoices = CreateChoices(binding.ToggleButtons,
            hold: false);
        Raise(nameof(HoldButtonChoices));
        Raise(nameof(ToggleButtonChoices));
    }

    internal void RefreshFromProfile()
    {
        RefreshButtonChoices();
        Raise(nameof(SelectedModeIndex));
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
}
