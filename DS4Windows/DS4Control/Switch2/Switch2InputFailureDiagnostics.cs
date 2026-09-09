using System;
using System.Threading;

namespace DS4Windows.Switch2;

// A lifecycle-only observer: no per-report callback, packet logging or I/O.
// Immutable evidence is captured before queuing and is safe after teardown.
internal sealed class Switch2InputFailureDiagnostics
{
    private readonly Action<string> diagnostic;
    private readonly Action<Action> schedule;
    private int reported;

    internal Switch2InputFailureDiagnostics(Action<string> diagnostic,
        Action<Action> schedule = null)
    {
        this.diagnostic = diagnostic;
        this.schedule = schedule ?? (static action =>
            ThreadPool.QueueUserWorkItem(static state => ((Action)state)(), action));
    }

    internal static void Observe(Switch2BluetoothRuntimeOwner owner, Action<string> diagnostic)
    {
        if (diagnostic == null) return;
        var observer = new Switch2InputFailureDiagnostics(diagnostic);
        owner.LifecycleAttention += (_, attention) => observer.TryReport(
            attention.Model.ToString(), owner.Registration.Generation,
            attention.EndReason, attention.PumpFailure,
            owner.Sink.FirstInputFailure, owner.RuntimeDevice.FirstPublicationFailure);
    }

    internal static void Observe(Switch2JoyConJoinedRuntimeOwner owner, Action<string> diagnostic)
    {
        if (diagnostic == null) return;
        var observer = new Switch2InputFailureDiagnostics(diagnostic);
        owner.LifecycleAttention += (_, attention) => observer.TryReport(
            $"joined Joy-Con {attention.Side}", attention.RuntimeGeneration,
            attention.EndReason, attention.PumpFailure,
            owner.Sink.FirstInputFailure, owner.RuntimeDevice.FirstPublicationFailure);
    }

    internal bool TryReport(string role, ulong runtimeGeneration,
        Switch2BluetoothInputEndReason endReason,
        Switch2BluetoothInputDrainPumpFailure pumpFailure,
        Switch2InputFailureSnapshot sink, Switch2InputFailureSnapshot runtime)
    {
        bool unexpected = endReason is Switch2BluetoothInputEndReason.Disconnected or
            Switch2BluetoothInputEndReason.QueueOverflow or Switch2BluetoothInputEndReason.SinkFailure ||
            pumpFailure != Switch2BluetoothInputDrainPumpFailure.None;
        if (diagnostic == null || !unexpected ||
            Interlocked.CompareExchange(ref reported, 1, 0) != 0)
            return false;
        try
        {
            schedule(() =>
            {
                try
                {
                    diagnostic($"Switch 2 {role} input stopped: runtime={runtimeGeneration}, " +
                        $"end={endReason}, pump={pumpFailure}; " +
                        (sink?.Describe() ?? (endReason == Switch2BluetoothInputEndReason.SinkFailure ?
                            "sink evidence unavailable" : "no input-sink rejection recorded")) +
                        (runtime == null ? "." : $"; runtimeCallback={runtime.Describe()}."));
                }
                catch { /* Diagnostic observers cannot change controller ownership. */ }
            });
            return true;
        }
        catch
        {
            // FirstInputFailure remains available even if dispatch is unavailable.
            return false;
        }
    }
}
