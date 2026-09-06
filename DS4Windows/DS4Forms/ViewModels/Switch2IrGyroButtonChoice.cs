using System;
using System.ComponentModel;
using DS4Windows.Switch2;

namespace DS4WinWPF.DS4Forms.ViewModels;

public sealed class Switch2IrGyroButtonChoice : INotifyPropertyChanged
{
    private readonly Action<Switch2JoyConProfileButton, bool> changed;
    private bool selected;

    internal Switch2IrGyroButtonChoice(Switch2JoyConProfileButton button,
        string label, bool selected,
        Action<Switch2JoyConProfileButton, bool> changed)
    {
        Button = button;
        Label = label;
        this.selected = selected;
        this.changed = changed;
    }

    public Switch2JoyConProfileButton Button { get; }

    public string Label { get; }

    public bool IsSelected
    {
        get => selected;
        set
        {
            if (selected == value)
            {
                return;
            }
            selected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(
                nameof(IsSelected)));
            changed?.Invoke(Button, value);
        }
    }

    public event PropertyChangedEventHandler PropertyChanged;
}
