using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DS4Windows.DS4Control;

namespace DS4Windows;

internal enum GuardedProfileSwitchStatus
{
    InvalidRequest, Superseded, PreparationFailed, ContextChanged,
    // Includes a refused/expired publication pause, whose API does not expose
    // whether its source is busy or inactive. Never implies safe replay of
    // this consumed work item; a caller must recapture and submit fresh work.
    AdmissionBusy, SourceUnavailable, Applied, AppliedWithCompletionError, ApplyFailed,
}

internal readonly record struct GuardedProfileSwitchResult(
    GuardedProfileSwitchStatus Status, long Revision = 0, string Error = null)
{
    internal bool Applied => Status is GuardedProfileSwitchStatus.Applied or
        GuardedProfileSwitchStatus.AppliedWithCompletionError;
}

internal delegate bool GuardedProfileRevisionClaim(long expectedRevision, out long revision);

/// <summary>
/// One named regular selection for the existing coalescing worker. Construction
/// captures names/root/token; it performs no I/O or live mutation. The context
/// guard must use only lock-free owned catalog/runtime snapshot reads: no
/// monitors, dispatcher access, external I/O, or re-entry into profile APIs.
/// It is rechecked under profile/publication/action/KBM admission boundaries.
/// Selection persistence and dispatcher synchronization remain the caller's
/// responsibility AFTER an applied result, with fresh source/revision checks.
/// </summary>
internal sealed class GuardedNamedProfileLoad
{
    private readonly ControllerProfileActionTarget target;
    private readonly ControlService service;
    private readonly string name, expectedName, path;
    private readonly long expectedRevision;
    private readonly bool launchProgram;
    private readonly Func<bool> contextGuard;
    private int executionStarted;
    private readonly TaskCompletionSource<GuardedProfileSwitchResult> completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private GuardedNamedProfileLoad(ControllerProfileActionTarget target,
        ControlService service, string name, string expectedName, long expectedRevision,
        bool launchProgram, Func<bool> contextGuard)
    {
        this.target = target;
        this.service = service;
        this.name = name;
        this.expectedName = expectedName;
        this.expectedRevision = expectedRevision;
        this.launchProgram = launchProgram;
        this.contextGuard = contextGuard;
        path = Path.Combine(Global.appdatapath, "Profiles", name + ".xml");
    }

    internal Task<GuardedProfileSwitchResult> Completion => completion.Task;

    internal static bool TryCreate(ControllerProfileActionTarget target,
        ControlService service, string name, string expectedName, long expectedRevision,
        bool launchProgram, Func<bool> contextGuard, out GuardedNamedProfileLoad request)
    {
        request = null;
        if (!target.IsExactTargetFor(service) || (uint)target.Slot >= Global.MAX_DS4_CONTROLLER_COUNT ||
            contextGuard == null || expectedName == null || expectedRevision < 0 ||
            expectedRevision == long.MaxValue || string.IsNullOrWhiteSpace(name) ||
            name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.EndsWith('.') || name.EndsWith(' '))
            return false;
        request = new(target, service, name, expectedName, expectedRevision, launchProgram, contextGuard);
        return true;
    }

    internal void Complete(GuardedProfileSwitchResult result) => completion.TrySetResult(result);

    internal GuardedProfileSwitchResult Execute(Func<bool> isLatest, GuardedProfileRevisionClaim claimRevision)
    {
        if (Interlocked.Exchange(ref executionStarted, 1) != 0)
            return new(GuardedProfileSwitchStatus.InvalidRequest, Error: "A named request cannot execute twice.");
        int slot = target.Slot;
        long revision = 0;
        bool applied = false;
        try
        {
            if (!isLatest() || Global.ReadProfileSwitchRevision(slot) != expectedRevision)
                return new(GuardedProfileSwitchStatus.Superseded);
            if (!PreparedProfileLoad.TryPrepare(path, slot, out var prepared, out _, out string error))
                return new(GuardedProfileSwitchStatus.PreparationFailed, Error: error);

            using var mutation = ProfileMutationGate.Enter(slot);
            GuardedProfileSwitchStatus? ValidateContext()
            {
                if (!isLatest() || Global.ReadProfileSwitchRevision(slot) != expectedRevision)
                    return GuardedProfileSwitchStatus.Superseded;
                if (Global.useTempProfile[slot] ||
                    !string.Equals(Global.ProfilePath[slot], expectedName, StringComparison.Ordinal) || !contextGuard())
                    return GuardedProfileSwitchStatus.ContextChanged;
                return null;
            }
            if (ValidateContext() is { } invalidContext)
                return new(invalidContext);

            var result = new GuardedProfileSwitchResult(GuardedProfileSwitchStatus.AdmissionBusy);
            target.Source.TryHaltReportingRunAction(() =>
            {
                if (!target.TryAcquire(out var lease, out var failure))
                {
                    result = new(failure is InputControllerSlotTableFailure.Busy or
                        InputControllerSlotTableFailure.TimedOut ? GuardedProfileSwitchStatus.AdmissionBusy :
                        GuardedProfileSwitchStatus.SourceUnavailable);
                    return;
                }
                using (lease)
                {
                    service.TryRunWithStableProfileKbmMapping(() =>
                    {
                        if (ValidateContext() is { } invalid)
                        {
                            result = new(invalid);
                            return;
                        }
                        if (!claimRevision(expectedRevision, out revision))
                        {
                            result = new(GuardedProfileSwitchStatus.Superseded);
                            return;
                        }
                        applied = Global.store.ApplyPreparedProfileNew(prepared, launchProgram, service,
                            out _, transitionRevision: revision, completeColdSideEffects: false, actionTarget: target);
                        if (!applied)
                        {
                            result = new(GuardedProfileSwitchStatus.Superseded, revision);
                            return;
                        }
                        Global.ProfilePath[slot] = name;
                        Global.tempprofilename[slot] = string.Empty;
                        Global.tempprofileDistance[slot] = false;
                        Global.useTempProfile[slot] = false;
                        result = new(GuardedProfileSwitchStatus.Applied, revision);
                    });
                }
            });
            if (applied && !Global.store.CompletePreparedProfileLoad(prepared, launchProgram))
                return new(GuardedProfileSwitchStatus.AppliedWithCompletionError, revision,
                    "Profile applied, but the migrated profile could not be saved.");
            return result;
        }
        catch (Exception ex)
        {
            // No name-only rollback: application/resource failure after reset
            // can leave partial settings. Preserve that distinction for callers.
            return new(applied ? GuardedProfileSwitchStatus.AppliedWithCompletionError :
                GuardedProfileSwitchStatus.ApplyFailed, revision, ex.Message);
        }
    }
}
