using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2ImpulseReleaseEnvelopeTests
{
    [TestMethod]
    public void StopDecaysLinearlyAndExpiresAtNinetyMilliseconds()
    {
        Switch2ImpulseReleaseEnvelope envelope = default;
        envelope.Update(ushort.MaxValue, 0, 1_000_000,
            out ushort left, out ushort right);
        Assert.AreEqual(ushort.MaxValue, left);
        Assert.AreEqual((ushort)0, right);
        Assert.IsFalse(envelope.HasPendingRelease);

        envelope.Update(0, 0, 1_010_000, out left, out right);
        Assert.AreEqual(ushort.MaxValue, left);
        Assert.IsTrue(envelope.HasPendingRelease);

        envelope.Resolve(1_055_000, out left, out right);
        Assert.AreEqual((ushort)32_767, left);
        Assert.AreEqual((ushort)0, right);

        envelope.Resolve(1_099_999, out left, out right);
        Assert.AreEqual((ushort)0, left);
        Assert.IsTrue(envelope.HasPendingRelease);

        envelope.Resolve(1_100_000, out left, out right);
        Assert.AreEqual((ushort)0, left);
        Assert.IsFalse(envelope.HasPendingRelease);
    }

    [TestMethod]
    public void SidesReleaseIndependentlyAndNewValueReplacesRelease()
    {
        Switch2ImpulseReleaseEnvelope envelope = default;
        envelope.Update(40_000, 20_000, 100_000, out _, out _);
        envelope.Update(0, 20_000, 110_000,
            out ushort left, out ushort right);
        Assert.AreEqual((ushort)40_000, left);
        Assert.AreEqual((ushort)20_000, right);

        envelope.Update(0, 0, 140_000, out left, out right);
        Assert.AreEqual((ushort)26_666, left);
        Assert.AreEqual((ushort)20_000, right);

        envelope.Update(50_000, 0, 155_000, out left, out right);
        Assert.AreEqual((ushort)50_000, left);
        Assert.AreEqual((ushort)16_666, right);
        Assert.IsTrue(envelope.HasPendingRelease);

        envelope.Resolve(230_000, out left, out right);
        Assert.AreEqual((ushort)50_000, left);
        Assert.AreEqual((ushort)0, right);
        Assert.IsFalse(envelope.HasPendingRelease);
    }

    [TestMethod]
    public void RepeatedZeroDoesNotRestartReleaseAndClearIsTerminal()
    {
        Switch2ImpulseReleaseEnvelope envelope = default;
        envelope.Update(30_000, 0, 500_000, out _, out _);
        envelope.Update(0, 0, 510_000, out _, out _);
        envelope.Update(0, 0, 550_000,
            out ushort left, out _);
        Assert.AreEqual((ushort)16_666, left);

        envelope.Update(0, 0, 580_000, out left, out _);
        Assert.AreEqual((ushort)6_666, left);
        envelope.Clear();
        envelope.Resolve(580_000, out left, out ushort right);
        Assert.AreEqual((ushort)0, left);
        Assert.AreEqual((ushort)0, right);
        Assert.IsFalse(envelope.HasPendingRelease);
    }
}
