/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

namespace DS4Windows.Switch2;

public enum Switch2JoyConJoinedCoordinatorFailure : byte
{
    None = 0,
    InvalidCoordinatorState,
    ProfileAdmissionRejected,
    PairRejected,
    ProfileMappingRejected,
}

/// <summary>
/// Result of one serialized joined-lane transaction. Loss and split explicitly
/// request that a caller clear any previously published joined profile frame.
/// </summary>
public readonly struct Switch2JoyConJoinedCoordinatorResult
{
    internal Switch2JoyConJoinedCoordinatorResult(
        Switch2JoyConJoinedCoordinatorFailure failure,
        in Switch2JoyConPairResult pairResult,
        Switch2JoyConProfileInputFailure profileFailure,
        in Switch2JoyConProfileInputFrame profileFrame)
    {
        Failure = failure;
        PairResult = pairResult;
        ProfileFailure = profileFailure;
        ProfileFrame = profileFrame;
    }

    public Switch2JoyConJoinedCoordinatorFailure Failure { get; }

    public Switch2JoyConPairResult PairResult { get; }

    public Switch2JoyConProfileInputFailure ProfileFailure { get; }

    public Switch2JoyConProfileInputFrame ProfileFrame { get; }

    public bool HasProfileFrame => Failure ==
        Switch2JoyConJoinedCoordinatorFailure.None &&
        PairResult.Disposition ==
            Switch2JoyConPairDisposition.JoinedSnapshot;

    public bool ClearsProfileOutput => Failure ==
        Switch2JoyConJoinedCoordinatorFailure.None &&
        PairResult.Disposition is Switch2JoyConPairDisposition.HalfLost or
            Switch2JoyConPairDisposition.Split;
}

/// <summary>
/// One immutable, value-owned transaction state for a joined Joy-Con lane.
/// The owner must serialize calls and publish only the returned next value.
/// </summary>
public readonly struct Switch2JoyConJoinedCoordinatorState
{
    internal Switch2JoyConJoinedCoordinatorState(
        in Switch2JoyConPairState pairState,
        in Switch2JoyConProfileMapperState mapperState)
    {
        PairState = pairState;
        MapperState = mapperState;
    }

    public Switch2JoyConPairState PairState { get; }

    public Switch2JoyConProfileMapperState MapperState { get; }

    public bool IsValid => PairState.IsValid && MapperState.IsValid &&
        MapperState.Mode == Switch2JoyConProfileMode.Joined &&
        PairState.PairEpoch == MapperState.PairEpoch;

    public static bool TryCreate(ulong pairEpoch,
        in Switch2InputSessionDescriptor leftDescriptor,
        in Switch2InputSessionDescriptor rightDescriptor,
        out Switch2JoyConJoinedCoordinatorState state)
    {
        if (!Switch2JoyConPairState.TryCreate(pairEpoch, out var pairState) ||
            !Switch2JoyConProfileInputMapper.TryCreateJoined(pairEpoch,
                leftDescriptor, rightDescriptor, out var mapperState))
        {
            state = default;
            return false;
        }

        state = new Switch2JoyConJoinedCoordinatorState(pairState,
            mapperState);
        return true;
    }
}

/// <summary>
/// Atomic pair/profile composition. It performs no discovery, association,
/// transport I/O, scheduling, publication, persistence, or allocation.
/// </summary>
public static class Switch2JoyConJoinedCoordinator
{
    public static bool TryProcess(
        in Switch2JoyConJoinedCoordinatorState state,
        in Switch2JoyConPairEvent pairEvent,
        in Switch2JoyConPairPolicy policy,
        out Switch2JoyConJoinedCoordinatorState next,
        out Switch2JoyConJoinedCoordinatorResult result)
    {
        if (!state.IsValid)
        {
            return Reject(state,
                Switch2JoyConJoinedCoordinatorFailure.InvalidCoordinatorState,
                default, Switch2JoyConProfileInputFailure.InvalidMapperState,
                out next, out result);
        }

        // Admission precedes pair staging so an unsupported or stale half can
        // never become latent state while waiting for its peer.
        if (pairEvent.Kind == Switch2JoyConPairEventKind.Input &&
            !Switch2JoyConProfileInputMapper.TryAdmitJoinedHalf(
                state.MapperState, pairEvent.Side, pairEvent.Frame,
                out Switch2JoyConProfileInputFailure admissionFailure))
        {
            return Reject(state,
                Switch2JoyConJoinedCoordinatorFailure.
                    ProfileAdmissionRejected,
                default, admissionFailure, out next, out result);
        }

        if (!Switch2JoyConPairReducer.TryReduce(state.PairState, pairEvent,
                policy, out Switch2JoyConPairState pairCandidate,
                out Switch2JoyConPairResult pairResult))
        {
            return Reject(state,
                Switch2JoyConJoinedCoordinatorFailure.PairRejected,
                pairResult, Switch2JoyConProfileInputFailure.None,
                out next, out result);
        }

        if (pairResult.Disposition ==
                Switch2JoyConPairDisposition.JoinedSnapshot)
        {
            if (!Switch2JoyConProfileInputMapper.TryMapJoined(
                    state.MapperState, pairResult.Snapshot,
                    out Switch2JoyConProfileMapperState mapperCandidate,
                    out Switch2JoyConProfileInputFrame profileFrame,
                    out Switch2JoyConProfileInputFailure mappingFailure))
            {
                return Reject(state,
                    Switch2JoyConJoinedCoordinatorFailure.
                        ProfileMappingRejected,
                    pairResult, mappingFailure, out next, out result);
            }

            next = new Switch2JoyConJoinedCoordinatorState(pairCandidate,
                mapperCandidate);
            result = new Switch2JoyConJoinedCoordinatorResult(
                Switch2JoyConJoinedCoordinatorFailure.None, pairResult,
                Switch2JoyConProfileInputFailure.None, profileFrame);
            return true;
        }

        // Waiting and stale-skew results retain the admitted pair observation
        // but intentionally do not move either mapper acceptance baseline.
        next = new Switch2JoyConJoinedCoordinatorState(pairCandidate,
            state.MapperState);
        result = new Switch2JoyConJoinedCoordinatorResult(
            Switch2JoyConJoinedCoordinatorFailure.None, pairResult,
            Switch2JoyConProfileInputFailure.None, default);
        return true;
    }

    private static bool Reject(
        in Switch2JoyConJoinedCoordinatorState state,
        Switch2JoyConJoinedCoordinatorFailure failure,
        in Switch2JoyConPairResult pairResult,
        Switch2JoyConProfileInputFailure profileFailure,
        out Switch2JoyConJoinedCoordinatorState next,
        out Switch2JoyConJoinedCoordinatorResult result)
    {
        next = state;
        result = new Switch2JoyConJoinedCoordinatorResult(failure,
            pairResult, profileFailure, default);
        return false;
    }
}
