/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;

namespace DS4Windows.Switch2;

internal static class Switch2JoyConAutomaticPairingPolicy
{
    internal static bool TrySelectOldestCompatiblePair(
        ReadOnlySpan<Switch2JoyConPairCandidate> candidates,
        out int leftCandidateId, out int rightCandidateId)
    {
        leftCandidateId = 0;
        rightCandidateId = 0;
        ulong oldestLeft = ulong.MaxValue;
        ulong oldestRight = ulong.MaxValue;

        foreach (Switch2JoyConPairCandidate candidate in candidates)
        {
            if (candidate.Id <= 0 || candidate.ArrivalOrdinal == 0)
            {
                continue;
            }

            if (candidate.Model == Switch2ControllerModel.JoyCon2Left &&
                candidate.ArrivalOrdinal < oldestLeft)
            {
                oldestLeft = candidate.ArrivalOrdinal;
                leftCandidateId = candidate.Id;
            }
            else if (candidate.Model ==
                         Switch2ControllerModel.JoyCon2Right &&
                     candidate.ArrivalOrdinal < oldestRight)
            {
                oldestRight = candidate.ArrivalOrdinal;
                rightCandidateId = candidate.Id;
            }
        }

        if (leftCandidateId != 0 && rightCandidateId != 0)
        {
            return true;
        }
        leftCandidateId = 0;
        rightCandidateId = 0;
        return false;
    }
}

internal enum Switch2JoyConManualPairSelectionDisposition : byte
{
    InvalidCandidate = 0,
    Armed,
    Cancelled,
    IncompatibleSide,
    PairReady,
}

internal readonly struct Switch2JoyConManualPairSelectionResult
{
    internal Switch2JoyConManualPairSelectionResult(
        Switch2JoyConManualPairSelectionDisposition disposition,
        int leftCandidateId = 0, int rightCandidateId = 0)
    {
        Disposition = disposition;
        LeftCandidateId = leftCandidateId;
        RightCandidateId = rightCandidateId;
    }

    internal Switch2JoyConManualPairSelectionDisposition Disposition
    {
        get;
    }

    internal int LeftCandidateId { get; }
    internal int RightCandidateId { get; }
}

/// <summary>
/// UI-only two-click selection state. It retains no peer identity and cannot
/// mutate pair persistence; the coordinator revalidates both candidate IDs
/// before creating an explicit pair.
/// </summary>
internal sealed class Switch2JoyConManualPairSelection
{
    private int armedCandidateId;
    private Switch2ControllerModel armedModel;

    internal bool HasArmedCandidate => armedCandidateId > 0;

    internal bool IsArmed(in Switch2JoyConPairCandidate candidate) =>
        armedCandidateId == candidate.Id && armedModel == candidate.Model;

    internal Switch2JoyConManualPairSelectionResult Select(
        in Switch2JoyConPairCandidate candidate)
    {
        if (!IsCandidateValid(candidate))
        {
            return new Switch2JoyConManualPairSelectionResult(
                Switch2JoyConManualPairSelectionDisposition.
                    InvalidCandidate);
        }

        if (!HasArmedCandidate)
        {
            Arm(candidate);
            return new Switch2JoyConManualPairSelectionResult(
                Switch2JoyConManualPairSelectionDisposition.Armed);
        }

        if (IsArmed(candidate))
        {
            Clear();
            return new Switch2JoyConManualPairSelectionResult(
                Switch2JoyConManualPairSelectionDisposition.Cancelled);
        }

        if (armedModel == candidate.Model)
        {
            return new Switch2JoyConManualPairSelectionResult(
                Switch2JoyConManualPairSelectionDisposition.
                    IncompatibleSide);
        }

        int left = armedModel == Switch2ControllerModel.JoyCon2Left ?
            armedCandidateId : candidate.Id;
        int right = armedModel == Switch2ControllerModel.JoyCon2Right ?
            armedCandidateId : candidate.Id;
        Clear();
        return new Switch2JoyConManualPairSelectionResult(
            Switch2JoyConManualPairSelectionDisposition.PairReady,
            left, right);
    }

    internal bool Reconcile(
        ReadOnlySpan<Switch2JoyConPairCandidate> candidates)
    {
        if (!HasArmedCandidate)
        {
            return false;
        }
        foreach (Switch2JoyConPairCandidate candidate in candidates)
        {
            if (IsArmed(candidate))
            {
                return false;
            }
        }
        Clear();
        return true;
    }

    internal void Clear()
    {
        armedCandidateId = 0;
        armedModel = default;
    }

    private void Arm(in Switch2JoyConPairCandidate candidate)
    {
        armedCandidateId = candidate.Id;
        armedModel = candidate.Model;
    }

    private static bool IsCandidateValid(
        in Switch2JoyConPairCandidate candidate) => candidate.Id > 0 &&
        candidate.ArrivalOrdinal > 0 && candidate.Model is
            Switch2ControllerModel.JoyCon2Left or
            Switch2ControllerModel.JoyCon2Right;
}
