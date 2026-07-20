using DS4Windows;

namespace DS4WindowsTests
{
    [TestClass]
    public class ViiperMicrophoneDisableRetryTests
    {
        [TestMethod]
        public void FailedDisableRemainsPendingUntilRetrySucceeds()
        {
            var tracker = new MicrophoneDisableRetryTracker<object>();
            object controller = new object();

            tracker.Schedule(controller, generation: 7, now: 100);
            Assert.IsTrue(tracker.TryBeginAttempt(7, now: 100,
                retryTicks: 50, out var first));
            tracker.CompleteAttempt(first, succeeded: false,
                nextAttemptTimestamp: 200);

            Assert.AreEqual(1, tracker.Count);
            Assert.IsFalse(tracker.TryBeginAttempt(7, now: 199,
                retryTicks: 50, out _));
            Assert.IsTrue(tracker.TryBeginAttempt(7, now: 200,
                retryTicks: 50, out var retry));
            tracker.CompleteAttempt(retry, succeeded: true);

            Assert.AreEqual(0, tracker.Count);
        }

        [TestMethod]
        public void ReactivationCancelsInFlightDisableWithoutResurrection()
        {
            var tracker = new MicrophoneDisableRetryTracker<object>();
            object controller = new object();

            tracker.Schedule(controller, generation: 3, now: 10);
            Assert.IsTrue(tracker.TryBeginAttempt(3, now: 10,
                retryTicks: 25, out var staleAttempt));

            tracker.Cancel(controller);
            tracker.CompleteAttempt(staleAttempt, succeeded: false);

            Assert.AreEqual(0, tracker.Count);
            Assert.IsFalse(tracker.TryBeginAttempt(3, now: 100,
                retryTicks: 25, out _));
        }

        [TestMethod]
        public void NewGenerationSupersedesOldAttemptToken()
        {
            var tracker = new MicrophoneDisableRetryTracker<object>();
            object controller = new object();

            tracker.Schedule(controller, generation: 11, now: 1);
            Assert.IsTrue(tracker.TryBeginAttempt(11, now: 1,
                retryTicks: 10, out var oldAttempt));

            tracker.Schedule(controller, generation: 12, now: 2);
            tracker.CompleteAttempt(oldAttempt, succeeded: true);

            Assert.AreEqual(1, tracker.Count);
            Assert.IsTrue(tracker.TryBeginAttempt(12, now: 2,
                retryTicks: 10, out var currentAttempt));
            Assert.AreSame(controller, currentAttempt.Target);
            tracker.CompleteAttempt(currentAttempt, succeeded: true);
            Assert.AreEqual(0, tracker.Count);
        }

        [TestMethod]
        public void DifferentWorkerGenerationCannotClaimStaleDisable()
        {
            var tracker = new MicrophoneDisableRetryTracker<object>();
            tracker.Schedule(new object(), generation: 4, now: 20);

            Assert.IsFalse(tracker.TryBeginAttempt(5, now: 20,
                retryTicks: 10, out _));
            Assert.AreEqual(0, tracker.Count);
        }
    }
}
