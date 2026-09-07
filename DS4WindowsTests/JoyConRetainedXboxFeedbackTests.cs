using DS4Windows;

namespace DS4WindowsTests;

[TestClass]
public sealed class JoyConRetainedXboxFeedbackTests
{
    private static XboxOneAuthorizedFeedbackBinding Binding() => new()
    {
        Source = (byte)ControllerFeedbackSource.XboxOneVirtualDevice, PersonaGeneration = 1,
        DeviceGeneration = 5, TransportGeneration = 6, OwnershipEpoch = 7, TimeToLiveMicroseconds = 250_000
    };

    [DataTestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public void RetainedXboxRoutePreservesCommandAmplitudesAndFreshness(int commandValue)
    {
        var command = (ControllerFeedbackCommand)commandValue;
        Assert.IsTrue(ControllerFeedbackClock.TryGetTimestampMicroseconds(out ulong now));
        var frame = Frame(command, now);
        Assert.IsTrue(ViiperOutDevice.TryTranslateRetainedXboxFeedback(frame, Binding(), 9, now,
            50, 60, 70, out var rebound, out bool suppressed));
        Assert.IsFalse(suppressed);
        Assert.AreEqual(50UL, rebound.DeviceGeneration);
        Assert.AreEqual(60UL, rebound.TransportGeneration);
        Assert.AreEqual(70UL, rebound.OwnershipEpoch);
        Assert.AreEqual(frame.Command, rebound.Command);
        Assert.AreEqual(frame.Sequence, rebound.Sequence);
        Assert.AreEqual(frame.BodyLow, rebound.BodyLow);
        Assert.AreEqual(frame.BodyHigh, rebound.BodyHigh);
        Assert.AreEqual(frame.LeftTrigger, rebound.LeftTrigger);
        Assert.AreEqual(frame.RightTrigger, rebound.RightTrigger);
        Assert.AreEqual(frame.TimestampMicroseconds, rebound.TimestampMicroseconds);
        Assert.AreEqual(frame.TimeToLiveMicroseconds, rebound.TimeToLiveMicroseconds);
    }

    [DataTestMethod]
    [DataRow("source")]
    [DataRow("device")]
    [DataRow("transport")]
    [DataRow("epoch")]
    [DataRow("ttl")]
    [DataRow("sequence")]
    [DataRow("expired")]
    [DataRow("future")]
    public void RetainedXboxRouteRejectsForeignStaleOrReplayedFrames(string failure)
    {
        Assert.IsTrue(ControllerFeedbackClock.TryGetTimestampMicroseconds(out ulong now));
        var binding = Binding();
        if (failure == "source") binding.Source = (byte)ControllerFeedbackSource.XboxSeriesVirtualDevice;
        if (failure == "device") binding.DeviceGeneration++;
        if (failure == "transport") binding.TransportGeneration++;
        if (failure == "epoch") binding.OwnershipEpoch++;
        if (failure == "ttl") binding.TimeToLiveMicroseconds--;
        ulong timestamp = failure == "expired" ? now - 500_000 : failure == "future" ? now + 500_000 : now;
        var frame = Frame(ControllerFeedbackCommand.Apply, timestamp);
        Assert.IsFalse(ViiperOutDevice.TryTranslateRetainedXboxFeedback(frame, binding,
            failure == "sequence" ? 10UL : 0, ulong.MaxValue, 50, 60, 70, out _, out _));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void HandoffConsumesButNeverReplaysQueuedRumble(bool paused)
    {
        Assert.IsTrue(ControllerFeedbackClock.TryGetTimestampMicroseconds(out ulong now));
        var frame = Frame(ControllerFeedbackCommand.Apply, now - 1000);
        Assert.IsTrue(ViiperOutDevice.TryTranslateRetainedXboxFeedback(frame, Binding(), 0,
            paused ? ulong.MaxValue : now, 50, 60, 70, out var rebound, out bool suppressed));
        Assert.IsTrue(suppressed);
        Assert.AreEqual(default, rebound);
    }

    private static ControllerFeedbackFrame Frame(ControllerFeedbackCommand command, ulong timestamp)
    {
        bool apply = command == ControllerFeedbackCommand.Apply;
        Assert.IsTrue(ControllerFeedbackFrame.TryCreate(ControllerFeedbackSource.XboxOneVirtualDevice, command,
            ControllerFeedbackActuators.All, (ushort)(apply ? 1 : 0), (ushort)(apply ? 2 : 0),
            (ushort)(apply ? 3 : 0), (ushort)(apply ? 4 : 0), 10, 5, 6, 7, timestamp, 250_000, out var frame));
        return frame;
    }
}
