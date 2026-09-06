using System;

namespace DS4Windows.Switch2;

/// <summary>Optional post-mapper observer, not another raw event subscription.</summary>
internal sealed class Switch2UdpMotionObserver
{
    private readonly UdpMotionObservationWorker worker;
    private readonly Func<UdpServerSession> session;
    private readonly Func<int, UdpMotionObservationPolicy> policy;

    internal Switch2UdpMotionObserver(UdpMotionObservationWorker worker,
        Func<UdpServerSession> session, Func<int, UdpMotionObservationPolicy> policy = null)
    {
        this.worker = worker ?? throw new ArgumentNullException(nameof(worker));
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.policy = policy ?? (static slot => new UdpMotionObservationPolicy(
            Global.IsUsingUDPServerSmoothing(), Global.UDPServerSmoothingMincutoff,
            Global.UDPServerSmoothingBeta, Global.Switch2CemuhookYawSensitivity[slot]));
    }

    internal UdpMotionObservationWorker.Source Register(in InputControllerSlotToken token) => worker.Register(token);

    internal void Observe(UdpMotionObservationWorker.Source source, DS4State state, bool hasMotion)
    {
        if (source == null) return;
        DS4Device device = source.Token.Registration.Device;
        source.TryPublish(state, hasMotion, device.getBattery(), device.isCharging(),
            session(), policy(source.Token.Slot));
    }
}
