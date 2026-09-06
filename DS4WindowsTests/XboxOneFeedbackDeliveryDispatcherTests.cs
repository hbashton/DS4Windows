using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4Windows.Tests;

[TestClass]
public sealed class XboxOneFeedbackDeliveryDispatcherTests
{
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void LocalPolicyWakeRetriesWithoutGamePacketOrFabricatedAcknowledgement(bool throwsFirst)
    {
        using var signal = new AutoResetEvent(false);
        using var refreshed = new ManualResetEventSlim(false);
        int refreshes = 0, deliveries = 0, acknowledgements = 0, faults = 0;
        using var dispatcher = new XboxOneFeedbackDeliveryDispatcher(
            (_, _) => { Interlocked.Increment(ref deliveries); return true; },
            (_, _) => Interlocked.Increment(ref acknowledgements),
            () => Interlocked.Increment(ref faults),
            localPolicySignal: signal, processLocalPolicy: () =>
            {
                if (Interlocked.Increment(ref refreshes) == 1)
                {
                    if (throwsFirst) throw new InvalidOperationException("Injected policy dependency failure.");
                    return false;
                }
                refreshed.Set();
                return true;
            });
        signal.Set();
        Assert.IsTrue(refreshed.Wait(1_000));
        Thread.Sleep(150);
        Assert.AreEqual(2, Volatile.Read(ref refreshes), "Successful policy work must return to an indefinite wait.");
        Assert.AreEqual(0, deliveries);
        Assert.AreEqual(0, acknowledgements);
        Assert.AreEqual(0, faults);
    }

    [TestMethod]
    public void LocalPolicyCannotOverlapDeliveryOrItsAcknowledgement()
    {
        using var signal = new AutoResetEvent(false);
        using var ackEntered = new ManualResetEventSlim(false);
        using var releaseAck = new ManualResetEventSlim(false);
        using var policyDone = new ManualResetEventSlim(false);
        int policies = 0;
        using var dispatcher = new XboxOneFeedbackDeliveryDispatcher(
            (_, _) => true, (_, _) =>
            {
                ackEntered.Set();
                if (!releaseAck.Wait(2_000)) throw new TimeoutException();
            }, () => { }, localPolicySignal: signal, processLocalPolicy: () =>
            {
                Interlocked.Increment(ref policies);
                policyDone.Set();
                return true;
            });
        try
        {
            Assert.IsTrue(dispatcher.TryEnqueue(new byte[ControllerFeedbackFrame.SerializedLength], 1));
            Assert.IsTrue(ackEntered.Wait(1_000));
            signal.Set();
            Assert.IsFalse(policyDone.Wait(50));
            Assert.AreEqual(0, Volatile.Read(ref policies));
        }
        finally { releaseAck.Set(); }
        Assert.IsTrue(policyDone.Wait(1_000));
        Assert.AreEqual(1, Volatile.Read(ref policies));
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void SuccessorDuringAckWaitsAndCannotActuateAfterFailedAck(bool ackSucceeds)
    {
        using var ackEntered = new ManualResetEventSlim(false);
        using var releaseAck = new ManualResetEventSlim(false);
        int deliveries = 0;
        int faults = 0;
        byte[] payload = new byte[ControllerFeedbackFrame.SerializedLength];
        using var dispatcher = new XboxOneFeedbackDeliveryDispatcher(
            (_, _) => { Interlocked.Increment(ref deliveries); return true; },
            (correlation, _) =>
            {
                if (correlation != 1) return;
                ackEntered.Set();
                if (!releaseAck.Wait(2_000) || !ackSucceeds)
                    throw new System.IO.IOException("Injected ACK failure.");
            }, () => Interlocked.Increment(ref faults));
        try
        {
            Assert.IsTrue(dispatcher.TryEnqueue(payload, 1));
            Assert.IsTrue(ackEntered.Wait(1_000));
            Assert.IsTrue(dispatcher.TryEnqueue(payload, 2));
            Assert.IsFalse(dispatcher.TryEnqueue(payload, 3), "Only one ACK successor may wait.");
            Assert.AreEqual(1, Volatile.Read(ref deliveries));
            Assert.IsFalse(dispatcher.WaitForIdle(0));
        }
        finally { releaseAck.Set(); }
        Assert.IsTrue(dispatcher.WaitForIdle(1_000));
        Assert.AreEqual(ackSucceeds ? 2 : 1, Volatile.Read(ref deliveries));
        Assert.AreEqual(ackSucceeds ? 0 : 1, Volatile.Read(ref faults));
        Assert.IsFalse(dispatcher.TryEnqueue(payload, 2), "A prior correlation cannot be replayed.");
    }

    [TestMethod]
    public void AcknowledgedSuccessorCanArriveBeforePriorCompletionReturns()
    {
        using var firstCompletion = new ManualResetEventSlim(false);
        using var releaseCompletion = new ManualResetEventSlim(false);
        using var secondDelivered = new ManualResetEventSlim(false);
        byte[] first = Enumerable.Repeat((byte)0x31, ControllerFeedbackFrame.SerializedLength).ToArray();
        byte[] second = Enumerable.Repeat((byte)0x52, ControllerFeedbackFrame.SerializedLength).ToArray();
        int deliveries = 0;
        using var dispatcher = new XboxOneFeedbackDeliveryDispatcher(
            (payload, _) =>
            {
                int number = Interlocked.Increment(ref deliveries);
                CollectionAssert.AreEqual(number == 1 ? first : second, payload);
                if (number == 2) secondDelivered.Set();
                return true;
            }, (_, _) => { }, () => Assert.Fail("Valid successor faulted."),
            completed: (payload, _, correlation, _, _) =>
            {
                if (correlation == 1)
                {
                    firstCompletion.Set();
                    Assert.IsTrue(releaseCompletion.Wait(2_000));
                    CollectionAssert.AreEqual(first, payload, "Successor overwrote the prior callback's payload.");
                }
            });
        try
        {
            Assert.IsTrue(dispatcher.TryEnqueue(first, 1));
            Assert.IsTrue(firstCompletion.Wait(1_000));
            Assert.IsTrue(dispatcher.TryEnqueue(second, 2),
                "The broker already received the prior ACK; its next value is not an overlapping delivery.");
            Assert.IsFalse(dispatcher.WaitForIdle(0));
        }
        finally { releaseCompletion.Set(); }
        Assert.IsTrue(secondDelivered.Wait(1_000));
        Assert.IsTrue(dispatcher.WaitForIdle(1_000));
    }

    [TestMethod]
    public async Task PhysicalDeliveryCannotBlockBrokerReaderAndAckFollowsIt()
    {
        using var deliveryEntered = new ManualResetEventSlim(false);
        using var releaseDelivery = new ManualResetEventSlim(false);
        using var acknowledged = new ManualResetEventSlim(false);
        ulong acknowledgedCorrelation = 0;
        bool acknowledgedStatus = false;
        int faults = 0;
        byte[] expected = Enumerable.Range(0,
                ControllerFeedbackFrame.SerializedLength)
            .Select(index => (byte)(index * 13 + 7)).ToArray();

        using var dispatcher = new XboxOneFeedbackDeliveryDispatcher(
            (payload, length) =>
            {
                CollectionAssert.AreEqual(expected,
                    payload.Take(length).ToArray());
                deliveryEntered.Set();
                Assert.IsTrue(releaseDelivery.Wait(TimeSpan.FromSeconds(2)));
                return true;
            },
            (correlation, accepted) =>
            {
                acknowledgedCorrelation = correlation;
                acknowledgedStatus = accepted;
                acknowledged.Set();
            },
            () => Interlocked.Increment(ref faults));

        Task<bool> enqueue = Task.Run(() => dispatcher.TryEnqueue(expected, 19));
        Assert.IsTrue(await enqueue.WaitAsync(TimeSpan.FromMilliseconds(500)),
            "Broker-reader handoff waited for the physical write.");
        Assert.IsTrue(deliveryEntered.Wait(TimeSpan.FromSeconds(1)));
        Assert.IsFalse(acknowledged.IsSet,
            "Feedback was acknowledged before physical delivery completed.");
        Assert.IsFalse(dispatcher.TryEnqueue(expected, 20),
            "A second outstanding feedback value must fail closed.");

        releaseDelivery.Set();
        Assert.IsTrue(acknowledged.Wait(TimeSpan.FromSeconds(1)));
        Assert.IsTrue(dispatcher.WaitForIdle(1_000));
        Assert.AreEqual(19ul, acknowledgedCorrelation);
        Assert.IsTrue(acknowledgedStatus);
        Assert.AreEqual(0, Volatile.Read(ref faults));
    }

    [TestMethod]
    public void RejectedPhysicalDeliveryIsAckedRejectedAndFaultsExactStream()
    {
        using var acknowledged = new ManualResetEventSlim(false);
        bool acknowledgedStatus = true;
        int faults = 0;
        byte[] feedback = new byte[ControllerFeedbackFrame.SerializedLength];

        using var dispatcher = new XboxOneFeedbackDeliveryDispatcher(
            (_, _) => false,
            (correlation, accepted) =>
            {
                Assert.AreEqual(23ul, correlation);
                acknowledgedStatus = accepted;
                acknowledged.Set();
            },
            () => Interlocked.Increment(ref faults));

        Assert.IsTrue(dispatcher.TryEnqueue(feedback, 23));
        Assert.IsTrue(acknowledged.Wait(TimeSpan.FromSeconds(1)));
        Assert.IsTrue(dispatcher.WaitForIdle(1_000));
        Assert.IsFalse(acknowledgedStatus);
        Assert.AreEqual(1, Volatile.Read(ref faults));
        Assert.IsFalse(dispatcher.TryEnqueue(feedback, 24));
    }

    [TestMethod]
    public void InvalidFeedbackNeverReachesDeliveryOrAck()
    {
        int deliveries = 0;
        int acknowledgements = 0;
        int faults = 0;
        using var dispatcher = new XboxOneFeedbackDeliveryDispatcher(
            (_, _) =>
            {
                Interlocked.Increment(ref deliveries);
                return true;
            },
            (_, _) => Interlocked.Increment(ref acknowledgements),
            () => Interlocked.Increment(ref faults));

        Assert.IsFalse(dispatcher.TryEnqueue(
            new byte[ControllerFeedbackFrame.SerializedLength - 1], 1));
        Assert.IsFalse(dispatcher.TryEnqueue(
            new byte[ControllerFeedbackFrame.SerializedLength], 0));
        Assert.AreEqual(0, Volatile.Read(ref deliveries));
        Assert.AreEqual(0, Volatile.Read(ref acknowledgements));
        Assert.AreEqual(0, Volatile.Read(ref faults));
    }

    [TestMethod]
    public void CompletionRunsOnlyAfterBrokerAcknowledgementReturns()
    {
        byte[] payload = Enumerable.Range(0,
                ControllerFeedbackFrame.SerializedLength)
            .Select(index => (byte)(index * 5 + 1)).ToArray();
        using var completion = new ManualResetEventSlim(false);
        bool acknowledgeReturned = false;
        bool observedAcknowledged = false;

        using var dispatcher = new XboxOneFeedbackDeliveryDispatcher(
            (_, _) => true,
            (_, _) => acknowledgeReturned = true,
            () => Assert.Fail("Successful delivery must not fault."),
            completed: (completedPayload, completedLength, correlation,
                delivered, acknowledged) =>
            {
                Assert.IsTrue(acknowledgeReturned,
                    "Completion ran before the broker ACK write returned.");
                CollectionAssert.AreEqual(payload,
                    completedPayload.Take(completedLength).ToArray());
                Assert.AreEqual(91ul, correlation);
                Assert.IsTrue(delivered);
                observedAcknowledged = acknowledged;
                completion.Set();
            });

        Assert.IsTrue(dispatcher.TryEnqueue(payload, 91));
        Assert.IsTrue(completion.Wait(TimeSpan.FromSeconds(1)));
        Assert.IsTrue(dispatcher.WaitForIdle(1_000));
        Assert.IsTrue(observedAcknowledged);
    }
}
