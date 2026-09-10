using System;
using System.Buffers.Binary;
using System.Reflection;
using System.Threading;
using DS4Windows.InputDevices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Fixture = DS4Windows.Tests.DualSenseBluetoothNativeIdleRetryTests.Fixture;

namespace DS4Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class DualSenseBluetoothGenerationWrapTests
{
    private const BindingFlags InstancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;

    [DataTestMethod]
    [DataRow(1, 2)]
    [DataRow(int.MaxValue, int.MinValue)]
    [DataRow(int.MinValue, int.MinValue + 1)]
    [DataRow(-1, 1)]
    public void ActualNextGenerationKeepsFutureSharedRingFrameUntilLifecycleAcceptance(int previous, int expected)
    {
        int next = (int)typeof(DualSenseBluetoothAudioPacer).GetMethod("NextGeneration",
            BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { previous });
        using var producer = DualSenseRealtimeHapticsSharedRing.CreateOwner(
            "Local\\DS4GenerationWrap-" + Guid.NewGuid().ToString("N"), 4);
        using var consumer = DualSenseRealtimeHapticsSharedRing.OpenConsumer(producer.MapName,
            producer.SpaceAvailableName, producer.StopRequestedName, producer.Capacity);
        byte[] pulse = new byte[DualSenseRealtimeHapticsSharedRing.PayloadLength];
        Array.Fill(pulse, (byte)0x41);
        byte[] report = new byte[DualSenseBluetoothAudioPacer.ReportLength];
        consumer.AcceptGeneration(previous, silenceFutureReports: true);
        Assert.IsTrue(producer.Publish(pulse, 0, next, long.MaxValue, 1));

        Assert.IsFalse(consumer.PrepareForPresentation(report, 100));
        Assert.AreEqual(1, consumer.Count,
            "New PCM may arrive before its pipe reset: signed serial comparison must retain, not discard, it.");
        Assert.AreEqual(expected, next);
        consumer.AcceptGeneration(next, silenceFutureReports: true);
        Assert.IsTrue(consumer.PrepareForPresentation(report, 200));
        CollectionAssert.AreEqual(pulse, report.AsSpan(
            DualSenseBluetoothAudioPacer.RealtimeHapticsDataOffset, pulse.Length).ToArray());
        consumer.CommitPrepared();
        Assert.AreEqual(0, consumer.Count);
        Assert.AreEqual(1L, consumer.PresentedCount);

        Assert.IsTrue(producer.Publish(pulse, 0, previous, long.MaxValue, 3));
        Assert.IsFalse(consumer.PrepareForPresentation(report, 300));
        Assert.AreEqual(0, consumer.Count, "The previous signed generation must still be discarded as stale.");
        Assert.AreEqual(1L, consumer.PresentedCount);
    }

    [DataTestMethod]
    [DataRow(1)]
    [DataRow(int.MinValue)]
    [DataRow(-1)]
    public void ActualHelperPresentsAndAcknowledgesSignedNonzeroNativeGeneration(int generation)
    {
        using var fixture = new Fixture();
        object host = typeof(Fixture).GetField("host", InstancePrivate).GetValue(fixture);
        byte[] reset = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(reset, generation);
        host.GetType().GetMethod("ReceiveResetControllerStateTransitions", InstancePrivate)
            .Invoke(host, new object[] { reset, reset.Length });
        typeof(Fixture).GetField("nativeGeneration", InstancePrivate).SetValue(fixture, generation);
        fixture.ReceiveNative(37, 83);
        fixture.StartIdle();
        Assert.IsTrue(SpinWait.SpinUntil(() => fixture.Native.Reports.Count >= 1 &&
            fixture.AcknowledgementCount >= 1, 2000),
            "Wait for terminal acknowledgement before Stop can cancel an active physical claim.");
        fixture.Stop();
        byte[][] reports = fixture.Native.Reports.ToArray();
        Assert.AreEqual(1, reports.Length);
        Assert.AreEqual((byte)0x31, reports[0][0]);
        Assert.AreEqual(3, reports[0][3] & 3);
        Assert.AreEqual((byte)37, reports[0][5]);
        Assert.AreEqual((byte)83, reports[0][6]);
        var receipts = fixture.DrainNativeAcknowledgements();
        Assert.AreEqual(1, receipts.Length);
        Assert.AreEqual(1L, receipts[0].Id);
        Assert.AreEqual(generation, receipts[0].Generation);
        Assert.AreEqual(DualSenseBluetoothAudioPacer.AcknowledgementDisposition.Presented,
            receipts[0].Disposition);
    }
}
