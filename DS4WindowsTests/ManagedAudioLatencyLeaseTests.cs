using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4Windows.Tests
{
    [TestClass]
    public class ManagedAudioLatencyLeaseTests
    {
        [DataTestMethod]
        [DataRow(ProcessPriorityClass.Idle)]
        [DataRow(ProcessPriorityClass.BelowNormal)]
        [DataRow(ProcessPriorityClass.Normal)]
        [DataRow(ProcessPriorityClass.AboveNormal)]
        public void ActiveMediaPromotesLowerProcessClasses(
            ProcessPriorityClass requestedPriority)
        {
            Assert.AreEqual(ProcessPriorityClass.High,
                ManagedAudioLatencyLease.ResolveEffectiveProcessPriority(
                    requestedPriority, mediaActive: true));
        }

        [DataTestMethod]
        [DataRow(ProcessPriorityClass.High)]
        [DataRow(ProcessPriorityClass.RealTime)]
        public void ActiveMediaPreservesHigherProcessClasses(
            ProcessPriorityClass requestedPriority)
        {
            Assert.AreEqual(requestedPriority,
                ManagedAudioLatencyLease.ResolveEffectiveProcessPriority(
                    requestedPriority, mediaActive: true));
        }

        [TestMethod]
        public void InactiveMediaPreservesRequestedProcessClass()
        {
            Assert.AreEqual(ProcessPriorityClass.Normal,
                ManagedAudioLatencyLease.ResolveEffectiveProcessPriority(
                    ProcessPriorityClass.Normal, mediaActive: false));
        }
    }
}
