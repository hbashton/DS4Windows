using System.Diagnostics;
using System.Reflection;
using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public sealed class Switch2AudioHapticsUsbTests
{
    [TestMethod]
    public void LocalAudioGroupsUseTheExistingAdoptedUsbWriterAndTerminalRetirement()
    {
        using var f = new UsbFixture();
        Assert.IsTrue(f.Feedback.TryCreateAudioLane(out var lane));
        ulong now = Now();
        Assert.IsTrue(f.Feedback.TryPublishAudio(lane, f.Left, f.Right,
            AudioHapticsMode.Replace, now, now));
        Assert.AreEqual(1, f.ReportCount);
        f.AssertReport(0, f.Left, f.Right);
        Assert.AreEqual(1, f.AdoptionCount, "Audio cannot acquire a second physical output owner.");
        Assert.AreEqual(0, f.DirectWriteCount, "Audio uses the adopted bounded writer, not the raw lease.");

        Assert.AreEqual(Switch2ProUsbOwnedFeedbackQuiescenceOutcome.ExactNeutralAndQuiescent,
            f.Feedback.TryNeutralizeAndQuiesce(f.Authority, 100).Outcome);
        Assert.AreEqual(2, f.ReportCount);
        f.AssertReport(1, default, default);
        Assert.IsFalse(f.Feedback.TryPublishAudio(lane, f.Left, f.Right,
            AudioHapticsMode.Mix, now, now));
        Assert.IsFalse(f.Feedback.RequiresRumbleMaintenance);
        Assert.AreEqual(ControllerFeedbackPumpDisposition.None,
            f.Feedback.TryServiceRumbleMaintenance(now + 48_000, null));
        Assert.AreEqual(2, f.ReportCount);
    }

    [TestMethod]
    public void UsbAudioCaptureExpiresAt24MillisecondsWithoutMaintenanceReplay()
    {
        using var f = new UsbFixture();
        Assert.IsTrue(f.Feedback.TryCreateAudioLane(out var lane));
        ulong captured = Now();
        Assert.IsTrue(f.Feedback.TryPublishAudio(lane, f.Left, f.Right,
            AudioHapticsMode.Mix, captured, captured));
        Assert.AreEqual(1, f.ReportCount);
        f.AssertReport(0, f.Left, f.Right);
        Assert.IsTrue(f.Feedback.RequiresRumbleMaintenance, "The finite owner still needs expiry service.");

        _ = f.Feedback.TryServiceRumbleMaintenance(captured + 12_000, null);
        _ = f.Feedback.TryServiceRumbleMaintenance(captured + 23_999, null);
        Assert.AreEqual(1, f.ReportCount, "A finite audio window is not a held oscillator keepalive.");
        Assert.AreEqual(ControllerFeedbackPumpDisposition.Delivered,
            f.Feedback.TryServiceRumbleMaintenance(captured + 24_000, null));
        Assert.AreEqual(2, f.ReportCount);
        f.AssertReport(1, default, default);
        Assert.IsFalse(f.Feedback.RequiresRumbleMaintenance);
        Assert.AreEqual(ControllerFeedbackPumpDisposition.None,
            f.Feedback.TryServiceRumbleMaintenance(captured + 48_000, null));
        Assert.AreEqual(2, f.ReportCount, "Expiry cannot renew or replay the captured samples.");
    }

    [TestMethod]
    public void PendingHandleWithdrawalDrainsThroughOuterUsbPumpAfterOperationContention()
    {
        using var f = new UsbFixture(attachRuntime: true);
        Assert.IsTrue(f.Runtime.TryCreateAudioHapticsOutput(out var output));
        Assert.IsTrue(output.IsReady);
        Assert.IsTrue(output.TryWrite(f.Samples, AudioHapticsMode.Mix, Stopwatch.GetTimestamp()));
        Assert.AreEqual(1, f.ReportCount);
        Assert.IsTrue(DualSenseHapticsTranslator.TryTranslatePcmToSwitch2Groups(f.Samples,
            out var expectedLeft, out var expectedRight));
        f.AssertReport(0, expectedLeft, expectedRight);

        FieldInfo operation = typeof(Switch2ProUsbOwnedFeedbackActivationLifetime).GetField(
            "operationActive", BindingFlags.Instance | BindingFlags.NonPublic)!;
        operation.SetValue(f.Feedback, 1);
        try
        {
            output.Dispose();
            Assert.IsFalse(output.IsReady);
            Assert.IsFalse(output.TryWrite(f.Samples, AudioHapticsMode.Mix, Stopwatch.GetTimestamp()));
            Assert.IsTrue(f.WithdrawalPending, "Busy disposal revokes the token and retains only withdrawal intent.");
            Assert.AreEqual(1, f.ReportCount);
            Assert.AreEqual(Switch2RumbleMaintenanceDisposition.Contended, f.Service(Now()).Disposition);
            Assert.IsTrue(f.WithdrawalPending);
            Assert.AreEqual(1, f.ReportCount, "The active USB operation fence forbids recursive output.");
        }
        finally { operation.SetValue(f.Feedback, 0); }

        Assert.AreEqual(Switch2RumbleMaintenanceDisposition.Idle, f.Service(Now()).Disposition);
        Assert.IsFalse(f.WithdrawalPending,
            "RenewLocalRumbleLeases must withdraw the lane directly; reacquiring operationActive would strand this intent.");
        Assert.AreEqual(2, f.ReportCount, "The outer canonical pump writes exactly one withdrawal neutral.");
        f.AssertReport(1, default, default);
        Assert.AreEqual(Switch2RumbleMaintenanceDisposition.Idle, f.Service(Now() + 12_000).Disposition);
        Assert.AreEqual(2, f.ReportCount);
        Assert.AreEqual(1, f.AdoptionCount);
        Assert.AreEqual(0, f.DirectWriteCount);
    }

    private sealed class UsbFixture : IDisposable
    {
        private readonly bool previousOutput = Global.EnableOutputDataToDS4[0];
        private readonly object lease;
        internal readonly Switch2ProUsbOwnedFeedbackActivationLifetime Feedback;
        internal readonly Switch2ProUsbOwnedCompositeAuthority Authority;
        internal readonly Switch2RuntimeInputDevice Runtime;
        internal readonly Switch2HdRumbleGroup Left = new(new(101, 201, 301, 401),
            new(102, 202, 302, 402), new(103, 203, 303, 403));
        internal readonly Switch2HdRumbleGroup Right = new(new(111, 211, 311, 411),
            new(112, 212, 312, 412), new(113, 213, 313, 413));
        internal readonly byte[] Samples = Enumerable.Range(0, 64)
            .Select(index => unchecked((byte)(sbyte)(index % 4 < 2 ? 80 : -80))).ToArray();

        internal UsbFixture(bool attachRuntime = false)
        {
            // Reuse the established, entirely synthetic composite/adoption
            // fixture without exposing or duplicating its private transport.
            // No HID, WinUSB, apps, or OS device enumeration are involved.
            object composition = typeof(Switch2ProUsbOwnedFeedbackActivationLifetimeTests)
                .GetMethod("CreateCommitted", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, new object[] { Guid.NewGuid(), 1, null })!;
            lease = Property<object>(composition, "Lease");
            Feedback = Property<Switch2ProUsbOwnedFeedbackActivationLifetime>(composition, "Feedback");
            Authority = Property<Switch2ProUsbOwnedCompositeAuthority>(composition, "Authority");
            if (!attachRuntime) return;
            Global.EnableOutputDataToDS4[0] = true;
            Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(Authority.DeviceGeneration,
                Authority.TransportGeneration, Switch2Transport.Usb, out Runtime, out _));
            Assert.IsTrue(Runtime.TryAttachUsbFeedbackLifetime(Authority, Feedback));
            Runtime.DeviceSlotNumber = 0;
            ((DS4Device)Runtime).StartUpdate();
            // Drive the actual maintenance callback deterministically rather
            // than scheduling a background timer in this no-hardware fixture.
            ((Switch2RumbleMaintenanceWorker)Field(Runtime, "rumbleMaintenanceWorker")).Stop();
            Feedback.SetRumbleMaintenanceWake(null);
        }

        internal int ReportCount => Property<int>(lease, "ReportCount");
        internal int AdoptionCount => (int)Field(lease, "AdoptionCount");
        internal int DirectWriteCount => (int)Field(lease, "DirectWriteCount");
        internal bool WithdrawalPending => (bool)Field(Runtime, "audioHapticsWithdrawalPending");
        internal Switch2RumbleMaintenanceResult Service(ulong now) =>
            (Switch2RumbleMaintenanceResult)typeof(Switch2RuntimeInputDevice)
                .GetMethod("ServiceRumbleMaintenance", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(Runtime, new object[] { now })!;

        internal void AssertReport(int index, Switch2HdRumbleGroup left, Switch2HdRumbleGroup right)
        {
            byte[] report = (byte[])lease.GetType().GetMethod("ReportAt",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(lease, new object[] { index })!;
            Assert.IsTrue(Switch2UsbHdRumbleCodec.TryDecodeProController(report,
                out byte counter, out var actualLeft, out var actualRight, out _));
            Assert.AreEqual((byte)index, counter);
            Assert.AreEqual(left, actualLeft);
            Assert.AreEqual(right, actualRight);
        }

        public void Dispose()
        {
            try
            {
                Runtime?.TryPublishTerminalNeutral();
                Assert.AreEqual(Switch2ProUsbOwnedFeedbackQuiescenceOutcome.ExactNeutralAndQuiescent,
                    Feedback.TryNeutralizeAndQuiesce(Authority, 100).Outcome);
            }
            finally
            {
                Feedback.SetRumbleMaintenanceWake(null);
                Global.EnableOutputDataToDS4[0] = previousOutput;
                ((ManualResetEventSlim)Field(lease, "WriteEntered")).Dispose();
                ((ManualResetEventSlim)Field(lease, "AllowWrite")).Dispose();
            }
        }
    }

    private static ulong Now()
    {
        Assert.IsTrue(ControllerFeedbackClock.TryGetTimestampMicroseconds(out ulong now));
        return now;
    }

    private static T Property<T>(object instance, string name) => (T)instance.GetType()
        .GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static object Field(object instance, string name) => instance.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
}
