using DS4Windows;

namespace DS4WindowsTests;

[TestClass]
public class ViiperStartupReadinessTests
{
    [TestMethod]
    public void SlowValidPrerequisitesDoNotLoseTheOwnedBrokerAtEightSeconds()
    {
        long now = 0;
        int probes = 0;
        bool Probe(int timeout, out string failure)
        {
            probes++;
            Assert.IsTrue(timeout is > 0 and <= 1000);
            failure = "Connect: SocketException";
            if (now >= 20_100) return true;
            now += timeout;
            return false;
        }

        Assert.IsTrue(ViiperStartupReadiness.Wait(Probe, out _, () => now, ms => now += ms));
        Assert.IsTrue(now > 20_000 && now < ViiperStartupReadiness.BudgetMilliseconds);
        Assert.IsTrue(probes > 1);
    }

    [TestMethod]
    public void FastStartupReturnsWithoutAddingASleep()
    {
        bool Probe(int timeout, out string failure) { failure = null; return true; }
        Assert.IsTrue(ViiperStartupReadiness.Wait(Probe, out _, () => 0,
            _ => Assert.Fail("A successful authenticated readiness check is immediate.")));
    }

    [TestMethod]
    public void DeadlineIsFiniteAndRetryHasANewBudget()
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            long now = 0;
            bool Probe(int timeout, out string failure)
            {
                Assert.IsTrue(timeout <= ViiperStartupReadiness.BudgetMilliseconds - now);
                now += timeout;
                failure = "ReadPing: timeout";
                return false;
            }
            Assert.IsFalse(ViiperStartupReadiness.Wait(Probe, out string failure, () => now, ms => now += ms));
            Assert.AreEqual((long)ViiperStartupReadiness.BudgetMilliseconds, now);
            Assert.AreEqual("ReadPing: timeout", failure);
        }
    }

    [TestMethod]
    public void ReplyAfterDeadlineDoesNotAuthorizeOutput()
    {
        long now = 0;
        bool Probe(int timeout, out string failure)
        {
            now = ViiperStartupReadiness.BudgetMilliseconds + 1;
            failure = null;
            return true;
        }
        Assert.IsFalse(ViiperStartupReadiness.Wait(Probe, out _, () => now,
            _ => Assert.Fail("Expired startup must not sleep again.")));
    }

    [TestMethod]
    public void LostOwnerOrEarlyExitStopsPollingImmediately()
    {
        int probes = 0;
        bool Probe(int timeout, out string failure)
        {
            failure = null;
            probes++;
            throw new PortableBrokerStartupException("Synthetic owned child exited.");
        }
        Assert.ThrowsException<PortableBrokerStartupException>(() =>
            ViiperStartupReadiness.Wait(Probe, out _, () => 0,
                _ => Assert.Fail("A dead or changed owner must not be retried.")));
        Assert.AreEqual(1, probes);
    }
}
