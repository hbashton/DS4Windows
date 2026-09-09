using System;
using System.Threading;

namespace DS4Windows.Switch2;

// Fault-only evidence. Never stores controller addresses, packet bodies,
// exception messages, user paths or exception objects. Successful input does
// not construct or format a snapshot.
internal sealed record Switch2InputFailureSnapshot(
    Switch2ControllerModel Model,
    string Cause,
    string ExceptionType = null,
    int ExceptionHResult = 0)
{
    internal static Switch2InputFailureSnapshot Standalone(Switch2ControllerModel model,
        Switch2BluetoothRuntimeSinkFailure failure,
        Switch2ProProfileInputFailure pro = default,
        Switch2JoyConProfileInputFailure joyCon = default,
        Exception exception = null) => new(model,
            $"sink={failure}, proMapping={pro}, joyConMapping={joyCon}",
            exception?.GetType().FullName, exception?.HResult ?? 0);

    internal static Switch2InputFailureSnapshot Joined(Switch2ControllerModel model,
        Switch2JoyConJoinedRuntimeSinkFailure failure,
        Switch2JoyConJoinedCoordinatorFailure coordinator = default,
        Switch2JoyConPairRejection pair = default,
        Switch2JoyConProfileInputFailure profile = default,
        Exception exception = null) => new(model,
            $"sink={failure}, coordinator={coordinator}, pair={pair}, mapping={profile}",
            exception?.GetType().FullName, exception?.HResult ?? 0);

    internal string Describe() => ExceptionType == null ? Cause :
        $"{Cause}, exception={ExceptionType}, hresult=0x{ExceptionHResult:X8}";
}

internal sealed class Switch2InputFailureEvidence
{
    private Switch2InputFailureSnapshot first;
    internal Switch2InputFailureSnapshot First => Volatile.Read(ref first);
    internal void Record(Switch2InputFailureSnapshot failure)
    {
        if (failure != null) Interlocked.CompareExchange(ref first, failure, null);
    }
}

public sealed partial class Switch2RuntimeInputDevice
{
    private readonly Switch2InputFailureEvidence publicationFailureEvidence = new();
    internal Switch2InputFailureSnapshot FirstPublicationFailure => publicationFailureEvidence.First;

    private void RecordPublicationFailure(string stage, Exception exception)
    {
        if (publicationFailureEvidence.First != null) return;
        publicationFailureEvidence.Record(new(default, stage,
            exception.GetType().FullName, exception.HResult));
    }
}
