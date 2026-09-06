/*
DS4Windows
Copyright (C) 2026 hbashton
SPDX-License-Identifier: GPL-3.0-or-later
*/

namespace DS4Windows.Switch2;

// A presentation restriction, not another feedback publisher or sequence owner.
internal readonly record struct Switch2XboxFeedbackPolicy(bool OutputEnabled,
    bool ImpulseEnabled)
{
    internal Switch2XboxFeedbackPolicy Intersect(Switch2XboxFeedbackPolicy other) =>
        new(OutputEnabled && other.OutputEnabled, ImpulseEnabled && other.ImpulseEnabled);

    internal static bool TryRefresh(ControllerFeedbackStateLanePump pump,
        Switch2HdRumbleDeliverySink sink, in ControllerFeedbackFrame frame,
        Switch2XboxFeedbackPolicy policy, ulong nowMicroseconds)
    {
        if (!sink.TryStageXboxOutputPolicy(frame, policy, out ulong revision))
            return false;

        // The first pass may finish an already-retained byte-identical USB write.
        // Only a subsequent presentation can apply the new restriction. Both
        // passes use the existing pump and retain its exact retry semantics.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (attempt != 0 && !ControllerFeedbackClock.TryGetTimestampMicroseconds(
                    out nowMicroseconds)) return false;
            _ = pump.TryRefreshCurrentPresentation(nowMicroseconds);
            var result = pump.PumpOnce(nowMicroseconds, sink, out _);
            if (result is not (ControllerFeedbackPumpDisposition.Delivered or
                ControllerFeedbackPumpDisposition.None)) return false;
            if (!frame.IsFreshAt(nowMicroseconds) ||
                sink.HasPresentedXboxOutputPolicy(frame, revision)) return true;
        }
        return false;
    }
}

// One cold-path wake request. Coalescing is restrictive only within the same
// source publication; a new game packet may legitimately start a new effect.
internal sealed record Switch2XboxFeedbackPolicyRequest(Switch2VirtualFeedbackSession Session,
    int DeviceIndex, long StreamGeneration, ulong PublicationRevision,
    Switch2XboxFeedbackPolicy Policy)
{
    internal bool SamePublication(Switch2XboxFeedbackPolicyRequest other) =>
        SameLifetime(other) && PublicationRevision == other.PublicationRevision;

    private bool SameLifetime(Switch2XboxFeedbackPolicyRequest other) =>
        other != null && ReferenceEquals(Session, other.Session) &&
        DeviceIndex == other.DeviceIndex && StreamGeneration == other.StreamGeneration;

    internal static void Enqueue(ref Switch2XboxFeedbackPolicyRequest pending,
        Switch2XboxFeedbackPolicyRequest request, bool retry = false,
        System.Predicate<Switch2XboxFeedbackPolicyRequest> isCurrent = null)
    {
        while (true)
        {
            var previous = System.Threading.Volatile.Read(ref pending);
            // Called only for cold profile work; the predicate is a bounded,
            // lock-free identity read. CAS failure revalidates the live owner.
            if (isCurrent != null && !isCurrent(request)) return;
            bool sameLifetime = request.SameLifetime(previous);
            if (sameLifetime && previous.PublicationRevision > request.PublicationRevision) return;
            bool same = request.SamePublication(previous);
            if (retry && previous != null && !sameLifetime) return;
            var next = same ? request with { Policy = request.Policy.Intersect(previous.Policy) } : request;
            if (ReferenceEquals(System.Threading.Interlocked.CompareExchange(
                    ref pending, next, previous), previous)) return;
        }
    }
}
