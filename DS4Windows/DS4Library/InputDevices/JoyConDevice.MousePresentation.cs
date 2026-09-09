using System;
using System.Threading;
using DS4Windows.Switch2;

namespace DS4Windows.InputDevices;

public partial class JoyConDevice : INintendoMousePresentation
{
    private readonly object nintendoMouseLifecycleGate = new();
    private NintendoMousePresentationLease nintendoMousePresentation = new();

    bool INintendoMousePresentation.TrySetHighRateMouseSource(Switch2ContinuousMouseSource source,
        bool active, double velocityX, double velocityY, long profileRevision) =>
        Volatile.Read(ref nintendoMousePresentation).TrySetSource(ProfileConnection, source, active, velocityX, velocityY, profileRevision);

    bool INintendoMousePresentation.TrySetHighRateMappingMouseSources(bool stickAssistActive,
        double stickAssistVelocityX, double stickAssistVelocityY, bool irActive,
        double irVelocityX, double irVelocityY, bool mappedStickActive,
        double mappedStickVelocityX, double mappedStickVelocityY, long profileRevision) =>
        !irActive && Volatile.Read(ref nintendoMousePresentation).TrySetMappingSources(ProfileConnection, stickAssistActive, stickAssistVelocityX,
            stickAssistVelocityY, mappedStickActive, mappedStickVelocityX, mappedStickVelocityY, profileRevision);

    // Cold topology path: caller pauses and drains group reports before this
    // clear, and may commit the transition only after the output fence succeeds.
    internal bool ClearNintendoMousePresentation(CancellationToken cancellationToken) =>
        Volatile.Read(ref nintendoMousePresentation).Clear(cancellationToken);

    // Service Stop/Start may reuse the physical object. Only the cold exact
    // registration path renews the terminal presenter, before StartUpdate.
    internal void StartNintendoMousePresentation()
    {
        lock (nintendoMouseLifecycleGate)
            if (nintendoMousePresentation.IsStopped)
                Volatile.Write(ref nintendoMousePresentation, new NintendoMousePresentationLease());
    }

    // Terminal physical shutdown. Never called while holding the report gate.
    internal void StopNintendoMousePresentation()
    {
        lock (nintendoMouseLifecycleGate) nintendoMousePresentation.Stop();
    }
}

internal sealed class NintendoMousePresentationLease
{
    private readonly object gate = new();
    private readonly Switch2HighRateMousePresenter presenter;
    private LegacyJoyConConnection connection;
    private LegacyJoyConGroup group;
    private bool stopped;

    internal NintendoMousePresentationLease(Action<int, int> output = null) => presenter = output == null ?
        new Switch2HighRateMousePresenter(CanPresent) : new Switch2HighRateMousePresenter(output, CanPresent);

    internal bool HasCurrentOwner => CanPresent();
    internal bool IsStopped => Volatile.Read(ref stopped);

    internal bool TrySetSource(LegacyJoyConConnection owner, Switch2ContinuousMouseSource source, bool active,
        double velocityX, double velocityY, long revision)
    {
        if (source == Switch2ContinuousMouseSource.Ir) return false;
        lock (gate)
            return TryAdmit(owner) && presenter.TrySetSource(source, active, velocityX, velocityY, revision);
    }

    internal bool TrySetMappingSources(LegacyJoyConConnection owner, bool stickActive, double stickX,
        double stickY, bool mappedActive, double mappedX, double mappedY, long revision)
    {
        lock (gate)
            return TryAdmit(owner) && presenter.TrySetMappingSources(stickActive, stickX, stickY,
                false, 0, 0, mappedActive, mappedX, mappedY, revision);
    }

    private bool TryAdmit(LegacyJoyConConnection current)
    {
        LegacyJoyConGroup currentGroup = current == null ? null : Volatile.Read(ref current.Group);
        if (stopped || currentGroup == null || !ReferenceEquals(currentGroup.Owner, current) ||
            !Volatile.Read(ref current.Connected) || Volatile.Read(ref current.Paused) || !Volatile.Read(ref currentGroup.Active)) return false;
        if (!ReferenceEquals(group, currentGroup) || !ReferenceEquals(connection, current))
        {
            presenter.ClearSources();
            Volatile.Write(ref connection, current);
            Volatile.Write(ref group, currentGroup);
        }
        return true;
    }

    private bool CanPresent()
    {
        LegacyJoyConConnection observedConnection = Volatile.Read(ref connection);
        LegacyJoyConGroup observedGroup = Volatile.Read(ref group);
        return observedConnection != null && observedGroup != null &&
            Volatile.Read(ref observedConnection.Connected) && !Volatile.Read(ref observedConnection.Paused) &&
            Volatile.Read(ref observedGroup.Active) && ReferenceEquals(observedGroup.Owner, observedConnection) &&
            ReferenceEquals(Volatile.Read(ref observedConnection.Group), observedGroup);
    }

    internal bool Clear(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            Volatile.Write(ref group, null);
            Volatile.Write(ref connection, null);
            presenter.ClearSources();
        }
        return presenter.FencePresentation(cancellationToken);
    }

    internal void Stop()
    {
        lock (gate)
        {
            stopped = true;
            Volatile.Write(ref group, null);
            Volatile.Write(ref connection, null);
            presenter.ClearSources();
        }
        presenter.Stop();
    }
}
