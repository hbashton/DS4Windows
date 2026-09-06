using DS4Windows.Switch2;

namespace DS4Windows;

/// <summary>
/// Cold profile work captures identity before preparation, then reacquires
/// admission only for the synchronous mutation. This is not a retained lease.
/// Sources not yet on the shared registration table keep their existing
/// reference/removal checks; a failed capture for a table-owned source must
/// never downgrade to that compatibility path.
/// </summary>
internal readonly struct ControllerProfileActionTarget
{
    private readonly ControlService service;
    private readonly DS4Device source;
    private readonly int slot;
    private readonly InputControllerRegistrationTable table;
    private readonly InputControllerSlotToken token;

    internal DS4Device Source => source;
    internal int Slot => slot;
    internal bool IsExactTargetFor(ControlService candidate) => table != null &&
        token.IsValid && ReferenceEquals(service, candidate);

    private ControllerProfileActionTarget(ControlService service,
        DS4Device source, int slot, InputControllerRegistrationTable table,
        InputControllerSlotToken token)
    {
        this.service = service;
        this.source = source;
        this.slot = slot;
        this.table = table;
        this.token = token;
    }

    internal static bool TryCapture(ControlService service,
        InputControllerRegistrationTable table, int slot, DS4Device source,
        out ControllerProfileActionTarget target)
    {
        target = default;
        if (source == null || service?.DS4Controllers == null ||
            (uint)slot >= service.DS4Controllers.Length ||
            !ReferenceEquals(service.DS4Controllers[slot], source) || source.IsRemoving)
            return false;

        bool registered = source is Switch2RuntimeInputDevice ||
            source.WorkerLifecycleSupport == DS4DeviceWorkerLifecycleSupport.SupportedLegacyHid;
        InputControllerSlotToken token = default;
        if (registered && (table == null ||
            !table.TryCaptureAttachedToken(slot, source, out token, out _)))
            return false;

        target = new ControllerProfileActionTarget(service, source, slot,
            registered ? table : null, token);
        return true;
    }

    // The caller must already own the source's synchronous publication pause,
    // or be executing on its serialized queue outside a Report callback. Never
    // wait here: a report-thread caller must not wait for its own report lease.
    internal bool TryAcquire(out InputControllerActionLease lease)
        => TryAcquire(out lease, out _);

    internal bool TryAcquire(out InputControllerActionLease lease,
        out InputControllerSlotTableFailure failure)
    {
        lease = default;
        failure = InputControllerSlotTableFailure.WrongSender;
        if (!MatchesSource())
            return false;
        if (table != null && !table.TryAcquireActionLease(token, 0, out lease, out failure))
            return false;
        if (MatchesSource())
        {
            failure = InputControllerSlotTableFailure.None;
            return true;
        }
        lease.Dispose();
        failure = InputControllerSlotTableFailure.WrongSender;
        return false;
    }

    private bool MatchesSource() => source != null &&
        service?.DS4Controllers != null && (uint)slot < service.DS4Controllers.Length &&
        ReferenceEquals(service.DS4Controllers[slot], source) && !source.IsRemoving;
}
