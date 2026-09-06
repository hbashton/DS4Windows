using System;
using System.Collections.Generic;
using System.ComponentModel;
using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WinWPF.DS4Forms.ViewModels;

public sealed class Switch2GyroTriggerTuningEditorViewModel :
    INotifyPropertyChanged
{
    private readonly int device;
    private readonly (Switch2JoyConProfileButton Button, string Label)[]
        buttonDefinitions;
    private int selectedModeIndex;
    private int selectedTriggerIndex;

    internal Switch2GyroTriggerTuningEditorViewModel(int device,
        IReadOnlyList<string> triggerChoices,
        (Switch2JoyConProfileButton Button, string Label)[] buttonDefinitions)
    {
        this.device = device;
        this.buttonDefinitions = buttonDefinitions ?? Array.Empty<(
            Switch2JoyConProfileButton Button, string Label)>();
        var triggers = new List<string>(
            Switch2GyroTriggerTuningTable.TriggerCount);
        if (triggerChoices != null)
        {
            for (int i = 0; i < triggerChoices.Count &&
                i < Switch2GyroTriggerTuningTable.TriggerCount; i++)
            {
                triggers.Add(triggerChoices[i]);
            }
        }
        TriggerChoices = triggers;
        ModeChoices = new[] { "Gyro Mouse", "Gyro Mouse Joystick" };
        RefreshButtonChoices();
    }

    public IReadOnlyList<string> ModeChoices { get; }
    public IReadOnlyList<string> TriggerChoices { get; }

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
            RefreshAll();
        }
    }

    public int SelectedTriggerIndex
    {
        get => selectedTriggerIndex;
        set
        {
            int normalized = Math.Clamp(value, 0,
                Math.Max(0, Switch2GyroTriggerTuningTable.TriggerCount - 1));
            if (selectedTriggerIndex == normalized)
            {
                return;
            }
            selectedTriggerIndex = normalized;
            RefreshAll();
        }
    }

    public IReadOnlyList<Switch2IrGyroButtonChoice>
        DeadzoneButtonChoices { get; private set; }

    public IReadOnlyList<Switch2IrGyroButtonChoice>
        DampeningButtonChoices { get; private set; }

    public double DeadzoneAmount
    {
        get => GetTuning().DeadzoneAmount;
        set => UpdateTuning(deadzoneAmount: value);
    }

    public int PauseAfterPressedMilliseconds
    {
        get => GetTuning().PauseAfterPressedMilliseconds;
        set => UpdateTuning(pauseAfterPressedMilliseconds: value);
    }

    public int PauseAfterReleasedMilliseconds
    {
        get => GetTuning().PauseAfterReleasedMilliseconds;
        set => UpdateTuning(pauseAfterReleasedMilliseconds: value);
    }

    public int DeadzoneEffectAfterReleasedMilliseconds
    {
        get => GetTuning().DeadzoneEffectAfterReleasedMilliseconds;
        set => UpdateTuning(
            deadzoneEffectAfterReleasedMilliseconds: value);
    }

    public double DampeningAmountPercent
    {
        get => GetTuning().DampeningAmountPercent;
        set => UpdateTuning(dampeningAmountPercent: value);
    }

    public int DampeningEffectAfterReleasedMilliseconds
    {
        get => GetTuning().DampeningEffectAfterReleasedMilliseconds;
        set => UpdateTuning(
            dampeningEffectAfterReleasedMilliseconds: value);
    }

    private GyroOutMode SelectedMode => selectedModeIndex == 1 ?
        GyroOutMode.MouseJoystick : GyroOutMode.Mouse;

    private Switch2GyroTriggerTuningTable Table
    {
        get
        {
            Switch2GyroTriggerTuningTable table =
                Global.Switch2GyroTriggerTunings[device];
            if (table == null)
            {
                table = new Switch2GyroTriggerTuningTable();
                Global.Switch2GyroTriggerTunings[device] = table;
            }
            return table;
        }
    }

    private Switch2IrGyroTuning GetTuning() =>
        Global.Switch2GyroTriggerTunings[device]?.Get(SelectedMode,
            selectedTriggerIndex) ?? Switch2IrGyroTuning.Default;

    private void UpdateTuning(
        Switch2JoyConProfileButton? deadzoneButtons = null,
        double? deadzoneAmount = null,
        int? pauseAfterPressedMilliseconds = null,
        int? pauseAfterReleasedMilliseconds = null,
        int? deadzoneEffectAfterReleasedMilliseconds = null,
        Switch2JoyConProfileButton? dampeningButtons = null,
        double? dampeningAmountPercent = null,
        int? dampeningEffectAfterReleasedMilliseconds = null)
    {
        Switch2IrGyroTuning current = GetTuning();
        Table.TrySet(SelectedMode, selectedTriggerIndex, new(
            deadzoneButtons ?? current.DeadzoneButtons,
            deadzoneAmount ?? current.DeadzoneAmount,
            pauseAfterPressedMilliseconds ??
                current.PauseAfterPressedMilliseconds,
            pauseAfterReleasedMilliseconds ??
                current.PauseAfterReleasedMilliseconds,
            deadzoneEffectAfterReleasedMilliseconds ??
                current.DeadzoneEffectAfterReleasedMilliseconds,
            dampeningButtons ?? current.DampeningButtons,
            dampeningAmountPercent ?? current.DampeningAmountPercent,
            dampeningEffectAfterReleasedMilliseconds ??
                current.DampeningEffectAfterReleasedMilliseconds));
    }

    private void UpdateButton(bool deadzone,
        Switch2JoyConProfileButton button, bool selected)
    {
        Switch2IrGyroTuning current = GetTuning();
        Switch2JoyConProfileButton mask = deadzone ?
            current.DeadzoneButtons : current.DampeningButtons;
        mask = selected ? mask | button : mask & ~button;
        if (deadzone)
        {
            UpdateTuning(deadzoneButtons: mask);
        }
        else
        {
            UpdateTuning(dampeningButtons: mask);
        }
    }

    private void RefreshAll()
    {
        RefreshButtonChoices();
        Raise(nameof(SelectedModeIndex));
        Raise(nameof(SelectedTriggerIndex));
        Raise(nameof(DeadzoneAmount));
        Raise(nameof(PauseAfterPressedMilliseconds));
        Raise(nameof(PauseAfterReleasedMilliseconds));
        Raise(nameof(DeadzoneEffectAfterReleasedMilliseconds));
        Raise(nameof(DampeningAmountPercent));
        Raise(nameof(DampeningEffectAfterReleasedMilliseconds));
    }

    internal void RefreshFromProfile() => RefreshAll();

    private void RefreshButtonChoices()
    {
        Switch2IrGyroTuning tuning = GetTuning();
        DeadzoneButtonChoices = CreateButtonChoices(
            tuning.DeadzoneButtons, deadzone: true);
        DampeningButtonChoices = CreateButtonChoices(
            tuning.DampeningButtons, deadzone: false);
        Raise(nameof(DeadzoneButtonChoices));
        Raise(nameof(DampeningButtonChoices));
    }

    private IReadOnlyList<Switch2IrGyroButtonChoice> CreateButtonChoices(
        Switch2JoyConProfileButton selected, bool deadzone)
    {
        var result = new List<Switch2IrGyroButtonChoice>(
            buttonDefinitions.Length);
        foreach ((Switch2JoyConProfileButton button, string label) in
            buttonDefinitions)
        {
            result.Add(new Switch2IrGyroButtonChoice(button, label,
                (selected & button) != 0,
                (changedButton, isSelected) => UpdateButton(deadzone,
                    changedButton, isSelected)));
        }
        return result;
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this,
        new PropertyChangedEventArgs(name));

    public event PropertyChangedEventHandler PropertyChanged;
}
