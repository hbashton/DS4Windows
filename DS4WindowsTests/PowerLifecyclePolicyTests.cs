using DS4WinWPF.DS4Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4WindowsTests
{
    [TestClass]
    public class PowerLifecyclePolicyTests
    {
        [TestMethod]
        public void RunningServiceRestartsExactlyOnceAfterDuplicateNotifications()
        {
            PowerLifecyclePolicy policy = new();

            PowerSuspendTransition firstSuspend = policy.Suspend(true);
            PowerSuspendTransition duplicateSuspend = policy.Suspend(false);
            PowerResumeTransition firstResume = policy.Resume();
            PowerResumeTransition duplicateResume = policy.Resume();

            Assert.IsTrue(firstSuspend.StopService);
            Assert.IsFalse(duplicateSuspend.StopService);
            Assert.IsTrue(firstResume.RestartService);
            Assert.IsFalse(duplicateResume.RestartService);
            Assert.IsTrue(policy.IsCurrent(firstResume.Generation),
                "A duplicate resume invalidated the pending restart lease.");
        }

        [TestMethod]
        public void NewSuspendInvalidatesDelayedResume()
        {
            PowerLifecyclePolicy policy = new();
            policy.Suspend(true);
            PowerResumeTransition resume = policy.Resume();

            policy.Suspend(false);

            Assert.IsFalse(policy.IsCurrent(resume.Generation));
        }

        [TestMethod]
        public void ServiceStoppedBeforeSuspendDoesNotStartOnResume()
        {
            PowerLifecyclePolicy policy = new();

            Assert.IsFalse(policy.Suspend(false).StopService);
            Assert.IsFalse(policy.Resume().RestartService);
        }

        [TestMethod]
        public void ClosedLifecycleRejectsLatePowerEvents()
        {
            PowerLifecyclePolicy policy = new();
            policy.Suspend(true);
            policy.Close();

            Assert.IsFalse(policy.Suspend(true).Accepted);
            Assert.IsFalse(policy.Resume().Accepted);
        }
    }
}
