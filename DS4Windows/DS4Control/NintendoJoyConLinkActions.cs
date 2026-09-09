using System;
using DS4Windows.Switch2;

namespace DS4Windows;

/// <summary>UI selection identity; each backend still validates its own exact lifetime.</summary>
internal readonly record struct NintendoJoyConCandidate(
    Switch2JoyConPairCandidate Switch2, LegacyJoyConConnection Original = null)
{
    internal bool IsOriginal => Original != null;
    internal bool IsValid => IsOriginal ? Original.Connected : Switch2.Id > 0 &&
        Switch2.ArrivalOrdinal > 0 && Switch2.Model is Switch2ControllerModel.JoyCon2Left or Switch2ControllerModel.JoyCon2Right;
    internal bool IsLeft => IsOriginal ? Original.IsLeft : Switch2.Model == Switch2ControllerModel.JoyCon2Left;
    internal DS4Device Device => IsOriginal ? Original.Device :
        Switch2.SlotToken.IsValid ? Switch2.SlotToken.Registration.Device : null;
    internal ulong ArrivalOrdinal => IsOriginal ? Original.Generation : Switch2.ArrivalOrdinal;
    internal string Label => $"Joy-Con{(IsOriginal ? string.Empty : " 2")} ({(IsLeft ? "Left" : "Right")}) #{ArrivalOrdinal}";
}

internal readonly record struct NintendoJoyConJoined(InputControllerSlotToken Switch2,
    LegacyJoyConGroup Original = null)
{
    internal bool IsValid => Original != null ? Original.Active && Original.Joined : Switch2.IsValid;
}

internal readonly record struct NintendoJoyConSelectionResult(
    Switch2JoyConManualPairSelectionDisposition Disposition,
    NintendoJoyConCandidate Left = default, NintendoJoyConCandidate Right = default,
    NintendoJoyConCandidate Preferred = default);

/// <summary>One two-click interaction for both Joy-Con generations.</summary>
internal sealed class NintendoJoyConManualPairSelection
{
    private NintendoJoyConCandidate armed;
    internal bool IsArmed(in NintendoJoyConCandidate candidate) => armed.IsValid && armed.Equals(candidate);
    internal void Clear() => armed = default;

    internal bool Reconcile(ReadOnlySpan<NintendoJoyConCandidate> candidates)
    {
        if (!armed.IsValid) { Clear(); return false; }
        foreach (var candidate in candidates) if (IsArmed(candidate)) return false;
        Clear();
        return true;
    }

    internal NintendoJoyConSelectionResult Select(in NintendoJoyConCandidate candidate)
    {
        if (!candidate.IsValid) return new(Switch2JoyConManualPairSelectionDisposition.InvalidCandidate);
        if (!armed.IsValid)
        {
            armed = candidate;
            return new(Switch2JoyConManualPairSelectionDisposition.Armed);
        }
        if (IsArmed(candidate))
        {
            Clear();
            return new(Switch2JoyConManualPairSelectionDisposition.Cancelled);
        }
        if (armed.IsLeft == candidate.IsLeft || armed.IsOriginal != candidate.IsOriginal)
            return new(Switch2JoyConManualPairSelectionDisposition.IncompatibleSide);
        var preferred = armed;
        Clear();
        return new(Switch2JoyConManualPairSelectionDisposition.PairReady,
            preferred.IsLeft ? preferred : candidate, preferred.IsLeft ? candidate : preferred, preferred);
    }
}
