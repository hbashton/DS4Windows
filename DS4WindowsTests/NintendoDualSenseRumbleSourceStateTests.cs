using DS4Windows;

namespace DS4WindowsTests;

[TestClass]
public sealed class NintendoDualSenseRumbleSourceStateTests
{
    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    public void MediaCannotCarryCompatibilityAcrossLifetimeBoundaries(int boundary)
    {
        var state = new NintendoDualSenseRumbleSourceState();
        object owner = new();
        int slot = 0;
        long profile = 7, stream = 11;
        byte[] feedback = NativeFeedback();
        var compatibility = NintendoDualSenseRumbleSource.Compatibility with { BodyLow = 43 * 257 };
        Assert.AreEqual(compatibility, Resolve(true));
        switch (boundary)
        {
            case 0: owner = new(); break;
            case 1: slot++; break;
            case 2: profile++; break;
            case 3: stream++; break;
            case 4: state.Reset(); break;
        }
        Assert.AreEqual(NintendoDualSenseRumbleSource.Audio, Resolve(false));
        Assert.AreEqual(compatibility, Resolve(true));

        NintendoDualSenseRumbleSource Resolve(bool fresh) =>
            state.Resolve(owner, slot, profile, stream, feedback, fresh);
    }

    [TestMethod]
    public void UnknownCompactFallbackCannotReturnAfterNativeContract()
    {
        var state = new NintendoDualSenseRumbleSourceState();
        var owner = new object();
        var compact = new byte[] { 43, 0, 0, 0, 0, 0 };
        Assert.AreEqual(NintendoDualSenseRumbleSource.Legacy with { BodyLow = 43 * 257 },
            state.Resolve(owner, 0, 7, 11, compact, true));
        byte[] native = NativeFeedback();
        native[29] = 0x0C;
        Assert.AreEqual(NintendoDualSenseRumbleSource.Audio,
            state.Resolve(owner, 0, 7, 11, native, true));
        Assert.AreEqual(default(NintendoDualSenseRumbleSource),
            state.Resolve(owner, 0, 7, 11, compact, true));
        native[28] = 0x03;
        Assert.AreEqual(default(NintendoDualSenseRumbleSource),
            state.Resolve(owner, 0, 7, 11, native, true));
        native[28] = 0x02;
        native[29] = 0x03;
        Assert.AreEqual(NintendoDualSenseRumbleSource.Audio,
            state.Resolve(owner, 0, 7, 11, native, false));
    }

    [TestMethod]
    public void MediaUsesLastControlAmplitudesEvenWhenSelectorStaysEnabled()
    {
        var state = new NintendoDualSenseRumbleSourceState();
        var owner = new object();
        byte[] feedback = NativeFeedback();
        feedback[1] = 9;
        var running = state.Resolve(owner, 0, 7, 11, feedback, true);
        Assert.AreEqual((ushort)(43 * 257), running.BodyLow);
        Assert.AreEqual((ushort)(9 * 257), running.BodyHigh);
        feedback[0] = feedback[1] = 0;
        Assert.AreEqual(running, state.Resolve(owner, 0, 7, 11, feedback, false),
            "Silent media cannot erase an authoritative compatibility command.");
        Assert.AreEqual(NintendoDualSenseRumbleSource.Compatibility,
            state.Resolve(owner, 0, 7, 11, feedback, true));
        feedback[0] = 43;
        feedback[1] = 9;
        var stopped = state.Resolve(owner, 0, 7, 11, feedback, false);
        Assert.AreEqual(NintendoDualSenseRumbleSource.Compatibility, stopped,
            "A stale media snapshot cannot undo the zero-motor control command.");

        // Original Joy-Con control composition consumes the same authoritative
        // result with PCM disabled and compatibility-only synthesis permitted.
        Assert.IsTrue(ViiperOutDevice.TryBuildSwitch2DualSenseHdRumbleGroups(
            feedback, feedback.Length, 76, false, false,
            out var left, out var right, out _, false, false,
            includeCompatibilityOnly: true, rumbleSource: stopped));
        Assert.IsFalse(left.First.HasNonzeroAmplitude || left.Second.HasNonzeroAmplitude ||
            left.Third.HasNonzeroAmplitude || right.First.HasNonzeroAmplitude ||
            right.Second.HasNonzeroAmplitude || right.Third.HasNonzeroAmplitude);
    }

    private static byte[] NativeFeedback()
    {
        var result = new byte[ViiperOutDevice.DualSenseAtomicFeedbackLength];
        result[0] = 43;
        result[28] = 0x02;
        result[29] = 0x03;
        return result;
    }
}
