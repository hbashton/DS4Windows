using System;
using System.Collections.ObjectModel;
using System.Windows.Media;
using DS4Windows;

namespace DS4WinWPF.DS4Forms.ViewModels;

public class LightbarMacroViewModel
{
    public ObservableCollection<LightbarMacroElement> MacroList { get; set; }

    private Color _currentColor;
    public Color CurrentColor
    {
        get => _currentColor;
        set
        {
            _currentColor = value;
            CurrentColorString = value.ToString();
            CurrentColorStringChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public string CurrentColorString { get; private set; }

    public event EventHandler CurrentColorStringChanged;

    private uint _currentInterval;
    public uint CurrentInterval
    {
        get => _currentInterval;
        set
        {
            _currentInterval = value;
            CurrentIntervalChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler CurrentIntervalChanged;

    private LightbarMacroTrigger _macroTrigger;

    public LightbarMacroTrigger MacroTrigger
    {
        get => _macroTrigger;
        set
        {
            _macroTrigger = value;
            MacroTriggerChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public event EventHandler MacroTriggerChanged;

    public LightbarMacroViewModel()
    {
        CurrentColor = Color.FromRgb(255, 255, 255);
    }
}

public class LightbarMacroElement(DS4Color color, uint length)
{
    public DS4Color Color { get; init; } = color;
    public uint Length { get; init; } = length;

    public override string ToString()
    {
        return $"{Color.ToString()}, {Length} ms";
    }

    public override bool Equals(object obj)
    {
        var casted = (LightbarMacroElement)obj;
        if (casted is null) return false;
        return casted.Color.Equals(Color) && casted.Length.Equals(Length);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = 17;
            hash = hash * 23 + Color.GetHashCode();
            hash = hash * 23 + Length.GetHashCode();
            return hash;
        }
    }
}

public enum LightbarMacroTrigger
{
    Press,
    Release
}