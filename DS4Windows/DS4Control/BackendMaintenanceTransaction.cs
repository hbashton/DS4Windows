using System;
using System.Threading;

namespace DS4Windows;

// Cold lifecycle coordination only. No filesystem, network, UI or device work.
// Operation success means the caller verified the replacement broker is ready.
internal sealed class BackendMaintenanceTransaction
{
    private int active;
    private int startBlocked;
    private bool resumeRequested;

    internal bool IsActive => Volatile.Read(ref active) != 0;
    internal bool RequiresRepair => Volatile.Read(ref startBlocked) != 0;
    internal bool RejectControllerStart => IsActive || RequiresRepair;

    internal void Execute(object lifecycleGate, Func<bool> isRunning,
        Func<bool> stop, Action operation, Func<bool> start)
    {
        ArgumentNullException.ThrowIfNull(lifecycleGate);
        ArgumentNullException.ThrowIfNull(isRunning);
        ArgumentNullException.ThrowIfNull(stop);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(start);
        lock (lifecycleGate)
        {
            if (IsActive)
                throw new InvalidOperationException("Backend maintenance is already in progress.");
            Volatile.Write(ref active, 1);
            try
            {
                bool restoreRunning = isRunning() || resumeRequested;
                // Even a stopped service can have a retryable partial drain.
                if (!stop())
                    throw new InvalidOperationException("The controller service could not drain safely. VIIPER was not changed; retry after the controllers finish closing.");

                // Keep the original run intent across a failed repair. Its
                // retry sees a stopped service, not a new user choice to stop.
                resumeRequested = restoreRunning;
                // A failed action may have retired the old broker. Do not let
                // automatic or queued starts reconnect until repair succeeds.
                Volatile.Write(ref startBlocked, 1);
                operation();
                if (restoreRunning && !start())
                    throw new InvalidOperationException("VIIPER was repaired, but the controller service could not restart. Retry repair before reconnecting controllers.");
                resumeRequested = false;
                Volatile.Write(ref startBlocked, 0);
            }
            finally { Volatile.Write(ref active, 0); }
        }
    }
}
