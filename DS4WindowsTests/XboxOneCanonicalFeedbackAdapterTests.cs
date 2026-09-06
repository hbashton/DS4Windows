using DS4Windows;
using DS4Windows.InputDevices;

namespace DS4WindowsTests;

[TestClass]
public sealed class XboxOneCanonicalFeedbackAdapterTests
{
    [TestMethod]
    public void DualSenseProjectionKeepsAllFourChannelsIndependent()
    {
        var state = new ControllerFeedbackActuatorState(
            0x0101, 0x0202, 0x0303, 0x0404);

        XboxOneCanonicalFeedbackAdapter.ProjectPhysical(state, true,
            out byte heavy, out byte light, out byte leftImpulse,
            out byte rightImpulse);

        Assert.AreEqual((byte)1, heavy);
        Assert.AreEqual((byte)2, light);
        Assert.AreEqual((byte)3, leftImpulse);
        Assert.AreEqual((byte)4, rightImpulse);
    }

    [TestMethod]
    public void ConventionalProjectionUsesSideLocalMaximumDownmix()
    {
        var state = new ControllerFeedbackActuatorState(
            10 * 257, 40 * 257, 30 * 257, 20 * 257);

        XboxOneCanonicalFeedbackAdapter.ProjectPhysical(state, false,
            out byte heavy, out byte light, out byte leftImpulse,
            out byte rightImpulse);

        Assert.AreEqual((byte)30, heavy);
        Assert.AreEqual((byte)40, light);
        Assert.AreEqual((byte)0, leftImpulse);
        Assert.AreEqual((byte)0, rightImpulse);
    }

    [TestMethod]
    public void SessionRejectsWrongBindingReplayAndPostStopTraffic()
    {
        XboxOneAuthorizedFeedbackBinding binding = Binding();
        var target = new TestPhysicalDevice();
        Assert.IsTrue(XboxOnePhysicalFeedbackSession.TryCreate(binding,
            target, out var session));
        Assert.IsTrue(session.Targets(target));
        Assert.IsFalse(session.Targets(new TestPhysicalDevice()),
            "A replacement controller in the same slot must not inherit feedback.");
        byte[] apply = Frame(binding, sequence: 1,
            ControllerFeedbackCommand.Apply, bodyLow: 1);

        Assert.IsTrue(session.TryAccept(apply, 1_010, out var state,
            out bool terminal));
        Assert.AreEqual((ushort)1, state.BodyLow);
        Assert.IsFalse(terminal);
        Assert.IsFalse(session.TryAccept(apply, 1_010, out _, out _),
            "An equal-sequence replay must fail closed.");

        XboxOneAuthorizedFeedbackBinding wrong = Binding();
        wrong.OwnershipEpoch++;
        Assert.IsFalse(session.TryAccept(Frame(wrong, 2,
            ControllerFeedbackCommand.Apply, bodyLow: 2), 1_010,
            out _, out _));

        byte[] stop = Frame(binding, 2, ControllerFeedbackCommand.Stop);
        Assert.IsTrue(session.TryAccept(stop, 1_010, out state,
            out terminal));
        Assert.IsTrue(state.IsNeutral);
        Assert.IsTrue(terminal);
        Assert.IsFalse(session.TryAccept(Frame(binding, 3,
            ControllerFeedbackCommand.Apply, bodyLow: 3), 1_010,
            out _, out _));
    }

    [TestMethod]
    public void SessionRejectsExpiredAndMalformedFramesWithoutAdvancing()
    {
        XboxOneAuthorizedFeedbackBinding binding = Binding();
        var target = new TestPhysicalDevice();
        Assert.IsTrue(XboxOnePhysicalFeedbackSession.TryCreate(binding,
            target, out var session));
        byte[] expired = Frame(binding, 1,
            ControllerFeedbackCommand.Apply, bodyLow: 1,
            timestamp: 1_000, ttl: 100);
        Assert.IsFalse(session.TryAccept(expired, 1_100, out _, out _));

        byte[] malformed = (byte[])expired.Clone();
        malformed[20] = 1;
        Assert.IsFalse(session.TryAccept(malformed, 1_050, out _, out _));

        Assert.IsTrue(session.TryAccept(Frame(binding, 1,
            ControllerFeedbackCommand.Neutral, timestamp: 1_050), 1_050,
            out var neutral, out _));
        Assert.IsTrue(neutral.IsNeutral);
    }

    [TestMethod]
    public void WarmAcceptAndProjectionAllocateNothing()
    {
        XboxOneAuthorizedFeedbackBinding binding = Binding();
        var target = new TestPhysicalDevice();
        Assert.IsTrue(XboxOnePhysicalFeedbackSession.TryCreate(binding,
            target, out var session));
        bool succeeded = true;
        for (ulong sequence = 1; sequence <= 2_000; sequence++)
        {
            byte[] wire = Frame(binding, sequence,
                ControllerFeedbackCommand.Apply, bodyLow: 1);
            succeeded &= session.TryAccept(wire, 1_010, out var state,
                out _);
            XboxOneCanonicalFeedbackAdapter.ProjectPhysical(state, true,
                out _, out _, out _, out _);
        }

        // Frame construction is intentionally outside the measured hot path.
        byte[][] frames = new byte[20_000][];
        for (int index = 0; index < frames.Length; index++)
        {
            frames[index] = Frame(binding, (ulong)index + 2_001,
                ControllerFeedbackCommand.Apply, bodyLow: 1);
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        foreach (byte[] wire in frames)
        {
            succeeded &= session.TryAccept(wire, 1_010, out var state,
                out _);
            XboxOneCanonicalFeedbackAdapter.ProjectPhysical(state, true,
                out _, out _, out _, out _);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.IsTrue(succeeded);
        Assert.AreEqual(0L, allocated);
    }

    private static XboxOneAuthorizedFeedbackBinding Binding() => new()
    {
        Source = (byte)ControllerFeedbackSource.XboxOneVirtualDevice,
        PersonaGeneration = 4,
        DeviceGeneration = 5,
        TransportGeneration = 6,
        OwnershipEpoch = 7,
        TimeToLiveMicroseconds = 1_000,
    };

    private sealed class TestPhysicalDevice : DS4Device
    {
        internal TestPhysicalDevice()
            : base("Xbox feedback test controller", InputDeviceType.DS4,
                ConnectionType.USB)
        {
        }
    }

    private static byte[] Frame(XboxOneAuthorizedFeedbackBinding binding,
        ulong sequence, ControllerFeedbackCommand command,
        ushort bodyLow = 0, ulong timestamp = 1_000, ulong ttl = 1_000)
    {
        Assert.IsTrue(ControllerFeedbackFrame.TryCreate(
            ControllerFeedbackSource.XboxOneVirtualDevice, command,
            ControllerFeedbackActuators.All, bodyLow, 0, 0, 0, sequence,
            binding.DeviceGeneration, binding.TransportGeneration,
            binding.OwnershipEpoch, timestamp, ttl, out var frame));
        byte[] wire = new byte[ControllerFeedbackFrame.SerializedLength];
        Assert.IsTrue(frame.TryWriteTo(wire));
        return wire;
    }
}
