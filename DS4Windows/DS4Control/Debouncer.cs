using System;
using System.Collections.Generic;
using DS4Windows;

namespace DS4WinWPF.DS4Control;

/// <summary>
/// Per-controller button debouncing with setup-time typed field selection.
/// The report path reuses one scratch state and performs no reflection,
/// dictionary enumeration, boxing, or managed allocation.
/// </summary>
public sealed class Debouncer
{
    private readonly List<Entry> entries = new(24);
    private readonly DS4State scratchState = new();
    private TimeSpan duration;

    public Debouncer(TimeSpan duration)
    {
        this.duration = duration;
    }

    public void AddDebouncer(string name)
    {
        Field field = ParseField(name);
        for (int index = 0; index < entries.Count; index++)
        {
            if (entries[index].Field == field)
            {
                entries[index] = new Entry(field,
                    new DebouncerInstance(duration));
                return;
            }
        }
        entries.Add(new Entry(field, new DebouncerInstance(duration)));
    }

    public DS4State ProcessInput(DS4State currentState)
    {
        ArgumentNullException.ThrowIfNull(currentState);
        if (duration == TimeSpan.Zero)
        {
            return currentState;
        }

        currentState.CopyTo(scratchState);
        DateTime timestamp = currentState.ReportTimeStamp;
        for (int index = 0; index < entries.Count; index++)
        {
            Entry entry = entries[index];
            bool reading = Read(scratchState, entry.Field);
            Write(scratchState, entry.Field,
                entry.Instance.ProcessInput(reading, timestamp));
        }
        return scratchState;
    }

    public void SetDuration(TimeSpan newDuration)
    {
        duration = newDuration;
        for (int index = 0; index < entries.Count; index++)
        {
            entries[index].Instance.Duration = newDuration;
        }
    }

    private static Field ParseField(string name) => name switch
    {
        nameof(DS4State.Cross) => Field.Cross,
        nameof(DS4State.Triangle) => Field.Triangle,
        nameof(DS4State.Circle) => Field.Circle,
        nameof(DS4State.Square) => Field.Square,
        nameof(DS4State.R3) => Field.R3,
        nameof(DS4State.L3) => Field.L3,
        nameof(DS4State.Options) => Field.Options,
        nameof(DS4State.Share) => Field.Share,
        nameof(DS4State.R2Btn) => Field.R2Btn,
        nameof(DS4State.L2Btn) => Field.L2Btn,
        nameof(DS4State.R1) => Field.R1,
        nameof(DS4State.L1) => Field.L1,
        nameof(DS4State.PS) => Field.PS,
        nameof(DS4State.TouchButton) => Field.TouchButton,
        nameof(DS4State.Capture) => Field.Capture,
        nameof(DS4State.SideL) => Field.SideL,
        nameof(DS4State.SideR) => Field.SideR,
        nameof(DS4State.DpadUp) => Field.DpadUp,
        nameof(DS4State.DpadDown) => Field.DpadDown,
        nameof(DS4State.DpadLeft) => Field.DpadLeft,
        nameof(DS4State.DpadRight) => Field.DpadRight,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name,
            "Only typed DS4 button fields can be debounced."),
    };

    private static bool Read(DS4State state, Field field) => field switch
    {
        Field.Cross => state.Cross,
        Field.Triangle => state.Triangle,
        Field.Circle => state.Circle,
        Field.Square => state.Square,
        Field.R3 => state.R3,
        Field.L3 => state.L3,
        Field.Options => state.Options,
        Field.Share => state.Share,
        Field.R2Btn => state.R2Btn,
        Field.L2Btn => state.L2Btn,
        Field.R1 => state.R1,
        Field.L1 => state.L1,
        Field.PS => state.PS,
        Field.TouchButton => state.TouchButton,
        Field.Capture => state.Capture,
        Field.SideL => state.SideL,
        Field.SideR => state.SideR,
        Field.DpadUp => state.DpadUp,
        Field.DpadDown => state.DpadDown,
        Field.DpadLeft => state.DpadLeft,
        Field.DpadRight => state.DpadRight,
        _ => false,
    };

    private static void Write(DS4State state, Field field, bool value)
    {
        switch (field)
        {
            case Field.Cross: state.Cross = value; break;
            case Field.Triangle: state.Triangle = value; break;
            case Field.Circle: state.Circle = value; break;
            case Field.Square: state.Square = value; break;
            case Field.R3: state.R3 = value; break;
            case Field.L3: state.L3 = value; break;
            case Field.Options: state.Options = value; break;
            case Field.Share: state.Share = value; break;
            case Field.R2Btn: state.R2Btn = value; break;
            case Field.L2Btn: state.L2Btn = value; break;
            case Field.R1: state.R1 = value; break;
            case Field.L1: state.L1 = value; break;
            case Field.PS: state.PS = value; break;
            case Field.TouchButton: state.TouchButton = value; break;
            case Field.Capture: state.Capture = value; break;
            case Field.SideL: state.SideL = value; break;
            case Field.SideR: state.SideR = value; break;
            case Field.DpadUp: state.DpadUp = value; break;
            case Field.DpadDown: state.DpadDown = value; break;
            case Field.DpadLeft: state.DpadLeft = value; break;
            case Field.DpadRight: state.DpadRight = value; break;
        }
    }

    private enum Field : byte
    {
        Cross, Triangle, Circle, Square, R3, L3, Options, Share,
        R2Btn, L2Btn, R1, L1, PS, TouchButton, Capture, SideL, SideR,
        DpadUp, DpadDown, DpadLeft, DpadRight,
    }

    private readonly struct Entry
    {
        internal Entry(Field field, DebouncerInstance instance)
        {
            Field = field;
            Instance = instance;
        }

        internal Field Field { get; }
        internal DebouncerInstance Instance { get; }
    }

    private sealed class DebouncerInstance
    {
        private bool previousState;
        private bool currentlyDebouncing;
        private DateTime debounceStartTime;

        internal DebouncerInstance(TimeSpan duration)
        {
            Duration = duration;
        }

        internal TimeSpan Duration { get; set; }

        internal bool ProcessInput(bool input, DateTime timestamp)
        {
            if (currentlyDebouncing)
            {
                return Debounce(input, timestamp);
            }
            if (previousState != input)
            {
                currentlyDebouncing = true;
                debounceStartTime = timestamp;
                return Debounce(input, timestamp);
            }

            previousState = input;
            return input;
        }

        private bool Debounce(bool reading, DateTime timestamp)
        {
            if (timestamp - debounceStartTime < Duration)
            {
                return true;
            }
            currentlyDebouncing = false;
            return reading;
        }
    }
}
