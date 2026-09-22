using System.Diagnostics;
using System.Reflection;
using DS4Windows.InputDevices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Fixture = DS4Windows.Tests.DualSenseBluetoothNativeIdleRetryTests.Fixture;
using Policy = DS4Windows.InputDevices.DualSenseBluetoothAudioPacer.NativeRumbleUpdatePolicy;

namespace DS4Windows.Tests;

[TestClass]
[DoNotParallelize]
public class DualSenseBluetoothSettingsRefreshRumbleTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string CapturedRefresh = "020C570000000000000000050000000000000000000005000000000000000000000000000000000000000000040000FF";

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ActualHelperProtectedRefreshPreservesAcceptedMotorTupleAndSingleTriggerLedUpdate(bool improved)
    {
        using var fixture = new Fixture();
        byte[] on = Rumble(improved, 43, 76);
        StartOn(fixture, on);
        fixture.ReceiveNativeCommand(Refresh(), Policy.SettingsRefresh);
        WaitNative(fixture);
        fixture.QueueSpeakerReports(8);
        WaitMedia(fixture, 8);
        fixture.Stop();

        byte[][] reports = fixture.Native.Reports.ToArray();
        foreach (byte[] report in reports) AssertTuple(report, on, "accepted protected refresh");
        Assert.AreEqual(1, reports.Count(r => (r[Offset(r)] & 0x0C) != 0), "Trigger-Off is presented once, not replayed by idle carriers.");
        Assert.AreEqual(1, reports.Count(r => (r[Offset(r) + 1] & 0x14) != 0), "Visible LED state is presented once.");
        byte[] refresh = reports.Single(r => (r[Offset(r)] & 0x0C) != 0);
        int offset = Offset(refresh);
        Assert.AreEqual((byte)5, refresh[offset + 10]);
        Assert.AreEqual((byte)5, refresh[offset + 21]);
        CollectionAssert.AreEqual(new byte[] { 4, 0, 0, 255 }, refresh.AsSpan(offset + 43, 4).ToArray());
        AssertMedia(fixture, expectedBlocks: Array.Empty<byte[]>(), expectedGain: 0);
        Assert.AreEqual(2, fixture.DrainNativeAcknowledgements().Length);
    }

    [TestMethod]
    public void ActualHelperColdProtectedRefreshDoesNotInventMotorOwnership()
    {
        using var fixture = new Fixture();
        fixture.ReceiveNativeCommand(Refresh(), Policy.SettingsRefresh);
        fixture.StartIdle();
        WaitNative(fixture);
        fixture.QueueSpeakerReports(8);
        WaitMedia(fixture, 8);
        fixture.Stop();
        foreach (byte[] report in fixture.Native.Reports) AssertTuple(report, Zero(), "no accepted ON");
    }

    [DataTestMethod]
    [DataRow(1, 0, 43)]
    [DataRow(2, 0, 43)]
    [DataRow(2, 4, 0)]
    public void ActualHelperProtectsOnlyRecognizedActiveContinuousRumble(int mode, int enhanced, int motor)
    {
        using var fixture = new Fixture();
        byte[] previous = Zero(); previous[1] = (byte)mode; previous[39] = (byte)enhanced; previous[3] = (byte)motor;
        StartOn(fixture, previous);
        fixture.ReceiveNativeCommand(Refresh(), Policy.SettingsRefresh);
        WaitNative(fixture);
        fixture.Stop();
        AssertTuple(fixture.Native.Reports.Last(), Zero(), "inactive/unsupported tuple must not be protected");
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void ActualHelperExplicitStopAndUnclassifiedNativeUpdateRemainAuthoritative(int shape)
    {
        using var fixture = new Fixture();
        StartOn(fixture, Rumble(true, 43, 76));
        fixture.ReceiveNativeCommand(Refresh(), Policy.SettingsRefresh);
        WaitNative(fixture);
        byte[] command = shape switch
        {
            1 => Rumble(true, 0, 0),
            2 => Refresh(),
            _ => Zero()
        };
        int first = fixture.Native.Reports.Count;
        fixture.ReceiveNativeCommand(command); // Unclassified native data is not a compatibility request.
        WaitNative(fixture);
        AssertTuple(fixture.Native.Reports.ElementAt(first), command, "exact authoritative command");
        fixture.QueueSpeakerReports(8);
        WaitMedia(fixture, 8);
        fixture.Stop();
        foreach (byte[] report in fixture.Native.Reports.Skip(first)) AssertMotorsOff(report);
    }

    [TestMethod]
    public void ActualHelperRejectsUnknownPolicyBeforeNativeAdmission()
    {
        using var fixture = new Fixture();
        var error = Assert.ThrowsException<TargetInvocationException>(() =>
            fixture.ReceiveNativeCommand(Refresh(), (Policy)2));
        Assert.IsInstanceOfType(error.InnerException, typeof(InvalidDataException));
        Assert.AreEqual(0, fixture.NativeOwnershipSnapshot().Admissions);
        Assert.AreEqual(0, fixture.Native.Reports.Count);
    }

    [TestMethod]
    public void ActualHelperRejectsOldShortNativeEnvelopeBeforeAdmission()
    {
        using var fixture = new Fixture();
        object host = Host(fixture);
        byte[] oldPayload = new byte[DualSenseBluetoothAudioPacer.GameStateAndTemplatePayloadLength - 1];
        var error = Assert.ThrowsException<TargetInvocationException>(() =>
            host.GetType().GetMethod("ReceiveGameStateAndTemplate", Private)!.Invoke(host, new object[] { oldPayload, oldPayload.Length }));
        Assert.IsInstanceOfType(error.InnerException, typeof(InvalidDataException));
        Assert.AreEqual(0, fixture.NativeOwnershipSnapshot().Admissions);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ActualHelperFreshPcmIncludingAuthoredZeroTakesOverProtectedRumbleOnThatCarrier(bool authoredZero)
    {
        using var fixture = new Fixture();
        StartOn(fixture, Rumble(true, 43, 76));
        fixture.ReceiveNativeCommand(Refresh(), Policy.SettingsRefresh);
        WaitNative(fixture);
        byte[] pcm = Block(authoredZero ? (byte)0 : (byte)73);
        Publish(fixture, pcm);
        fixture.QueueSpeakerReports(8, audioGain: 0x57);
        WaitMedia(fixture, 8);
        fixture.Stop();
        foreach (byte[] report in Media(fixture)) AssertTuple(report, Zero(), "real PCM handoff");
        AssertMedia(fixture, new[] { pcm }, 0x57);
        Assert.AreEqual(1L, Ring(fixture).PresentedCount);
    }

    [TestMethod]
    public void ActualHelperPcmQueuedBeforeAcceptedOnCannotCancelThatOwnerAfterRefresh()
    {
        using var fixture = new Fixture();
        byte[] oldPcm = Block(51);
        Publish(fixture, oldPcm);
        byte[] on = Rumble(true, 43, 76);
        StartOn(fixture, on);
        fixture.ReceiveNativeCommand(Refresh(), Policy.SettingsRefresh);
        WaitNative(fixture);
        fixture.QueueSpeakerReports(8);
        WaitMedia(fixture, 8);
        fixture.Stop();
        foreach (byte[] report in Media(fixture)) AssertTuple(report, on, "PCM predating explicit motor owner");
        AssertMedia(fixture, new[] { oldPcm }, 0);
    }

    [TestMethod]
    public void ActualHelperPcmAfterAcceptedOnButBeforeRefreshIsEligibleForProvisionalHandoff()
    {
        using var fixture = new Fixture();
        StartOn(fixture, Rumble(true, 43, 76));
        byte[] pcm = Block(61);
        Publish(fixture, pcm);
        fixture.ReceiveNativeCommand(Refresh(), Policy.SettingsRefresh);
        WaitNative(fixture);
        fixture.QueueSpeakerReports(8);
        WaitMedia(fixture, 8);
        fixture.Stop();
        foreach (byte[] report in Media(fixture)) AssertTuple(report, Zero(), "post-owner real PCM");
        AssertMedia(fixture, new[] { pcm }, 0);
    }

    [TestMethod]
    public void ActualHelperUnownedLocalTriggerKeepsProvisionalPcmHandoffArmed()
    {
        using var fixture = new Fixture();
        StartOn(fixture, Rumble(true, 43, 76));
        fixture.ReceiveNativeCommand(Refresh(), Policy.SettingsRefresh);
        WaitNative(fixture);
        int count = fixture.Native.Reports.Count;
        fixture.ReceiveLocalState(Trigger());
        WaitReports(fixture, count + 1);
        byte[] pcm = Block(91);
        Publish(fixture, pcm);
        fixture.QueueSpeakerReports(8);
        WaitMedia(fixture, 8);
        fixture.Stop();
        foreach (byte[] report in Media(fixture)) AssertTuple(report, Zero(), "unowned local trigger does not become motor owner");
        AssertMedia(fixture, new[] { pcm }, 0);
    }

    [TestMethod]
    public void ActualHelperExplicitLocalMotorCommandSupersedesProvisionalHandoff()
    {
        using var fixture = new Fixture();
        StartOn(fixture, Rumble(true, 43, 76));
        fixture.ReceiveNativeCommand(Refresh(), Policy.SettingsRefresh);
        WaitNative(fixture);
        byte[] pcm = Block(107);
        Publish(fixture, pcm);
        byte[] local = Rumble(false, 23, 59);
        int count = fixture.Native.Reports.Count;
        fixture.ReceiveLocalState(local);
        WaitReports(fixture, count + 1);
        fixture.QueueSpeakerReports(8);
        WaitMedia(fixture, 8);
        fixture.Stop();
        foreach (byte[] report in fixture.Native.Reports.Skip(count)) AssertTuple(report, local, "fresh explicit local motor owner wins");
        AssertMedia(fixture, new[] { pcm }, 0);
    }

    [TestMethod]
    public void ActualHelperExplicitNativeOnWinsOnTheSameCarrierAsEligiblePcm()
    {
        using var fixture = new Fixture();
        StartOn(fixture, Rumble(true, 43, 76));
        fixture.ReceiveNativeCommand(Refresh(), Policy.SettingsRefresh);
        WaitNative(fixture);
        byte[] pcm = Block(117);
        Publish(fixture, pcm);
        byte[] next = Rumble(false, 19, 31);
        int count = fixture.Native.Reports.Count;
        object host = Host(fixture);
        lock (host.GetType().GetField("stateLock", Private)!.GetValue(host)!)
        {
            // Make both heads visible together at the real startup media
            // boundary. Remove only the native rate-limit clock condition;
            // do not replace the renderer, writer or PCM ownership machinery.
            fixture.QueueSpeakerReports(8);
            fixture.ReceiveNativeCommand(next);
            host.GetType().GetField("lastControllerStateSubmissionQpc", Private)!.SetValue(host, 0L);
        }
        WaitNative(fixture);
        WaitMedia(fixture, 8);
        fixture.Stop();
        byte[][] reports = fixture.Native.Reports.ToArray();
        int acceptedNext = Array.FindIndex(reports, count, r => r[Offset(r) + 2] == next[3] && r[Offset(r) + 3] == next[4]);
        Assert.IsTrue(acceptedNext >= count, "The exact next native ON must be presented.");
        Assert.AreEqual((byte)0x36, reports[acceptedNext][0], "This regression must exercise actual same-carrier arbitration.");
        CollectionAssert.AreEqual(pcm, reports[acceptedNext].AsSpan(78, 64).ToArray());
        foreach (byte[] report in reports.Skip(acceptedNext)) AssertTuple(report, next, "explicit native ON priority over older queued PCM");
        AssertMedia(fixture, new[] { pcm }, 0);
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void ActualHelperBusyPcmHandoffIsTransactionalAndNewLocalOwnershipWins(int localAction)
    {
        using var fixture = new Fixture();
        byte[] on = Rumble(true, 43, 76);
        StartOn(fixture, on);
        fixture.Native.HoldInitialSubmission = true;
        fixture.ReceiveNativeCommand(Refresh(), Policy.SettingsRefresh);
        WaitNative(fixture);
        byte[] pcm = Block(121);
        Publish(fixture, pcm);
        object host = Host(fixture);
        byte[] backup = (byte[])host.GetType().GetField("physicalStatePayloadBackup", Private)!.GetValue(host)!;
        Array.Fill(backup, (byte)0xCC);
        int composed = 0;
        FieldInfo hook = host.GetType().GetField("BeforeMediaPhysicalWriteTestHook", Private)!;
        hook.SetValue(host, (Action)(() =>
        {
            // Normal preflight credit has already succeeded. Withhold that
            // same synthetic slot only after the real PCM takeover renderer
            // has composed the candidate, so TryWrite itself returns Busy.
            hook.SetValue(host, null);
            fixture.Native.GetType().GetMethod("Signal", Private)!.Invoke(fixture.Native, new object[] { false });
            Volatile.Write(ref composed, 1);
        }));
        // Complete the accepted refresh's synthetic pending I/O first. The
        // one-shot hook above, not the predecessor, creates the intended Busy.
        fixture.Native.ReturnCredit();
        fixture.QueueSpeakerReports(8);
        try
        {
            bool rolledBack = SpinWait.SpinUntil(() =>
            {
                if (Volatile.Read(ref composed) == 0 || !Ring(fixture).HasPreparedGeneration ||
                    backup[2] != 0 || backup[3] != 0 || (backup[0] & 3) != 0) return false;
                lock (host.GetType().GetField("stateLock", Private)!.GetValue(host)!)
                {
                    object reservoir = host.GetType().GetField("reservoir", Private)!.GetValue(host)!;
                    return (int)reservoir.GetType().GetProperty("Count")!.GetValue(reservoir)! == 8;
                }
            }, 2000);
            Assert.IsTrue(rolledBack,
                "The actual post-composition Busy write must roll the claimed front report back into the FIFO. " +
                $"composed={Volatile.Read(ref composed)}, prepared={Ring(fixture).HasPreparedGeneration}, " +
                $"physical={fixture.Native.Reports.Count}, backup={Convert.ToHexString(backup)}");
            Assert.AreEqual(0L, Ring(fixture).PresentedCount, "A Busy attempt must not spend the rear block.");
            Assert.AreEqual(0, Media(fixture).Length, "A Busy attempt must not spend a front carrier.");
            lock (host.GetType().GetField("stateLock", Private)!.GetValue(host)!)
            {
                Assert.IsTrue((bool)host.GetType().GetField("acceptedSettingsRefreshRumbleRetained", Private)!.GetValue(host)!,
                    "The attempted PCM handoff must not commit before physical acceptance.");
            }
            if (localAction != 0)
                fixture.ReceiveLocalState(localAction == 1 ? Rumble(false, 29, 37) : Rumble(false, 0, 0));
        }
        finally { hook.SetValue(host, null); fixture.Native.ReturnCredit(); }
        WaitMedia(fixture, 8);
        byte[] explicitLocal = localAction == 0 ? null :
            localAction == 1 ? Rumble(false, 29, 37) : Rumble(false, 0, 0);
        if (explicitLocal != null)
        {
            // The existing local lane can owe all eight priming media reports.
            // Receiving the eighth front report is not proof that the following
            // explicit local motor command has reached physical admission yet.
            int reportsAheadAtMediaEnd = fixture.ReportsAhead;
            bool localDelivered = SpinWait.SpinUntil(() => fixture.Native.Reports.Any(report =>
                MatchesTuple(report, explicitLocal)), 2000);
            Assert.IsTrue(localDelivered,
                $"Explicit local {(localAction == 1 ? "ON" : "flagged zero")} must be physically delivered; " +
                $"reportsAheadAtMediaEnd={reportsAheadAtMediaEnd}, reportsAheadNow={fixture.ReportsAhead}, " +
                "reports=" + string.Join(";", fixture.Native.Reports.Select(Convert.ToHexString)));
        }
        fixture.Stop();
        Assert.AreEqual(1L, Ring(fixture).PresentedCount);
        AssertMedia(fixture, new[] { pcm }, 0);
        if (localAction == 0)
            foreach (byte[] report in Media(fixture)) AssertTuple(report, Zero(), "accepted PCM retry");
        else
        {
            if (localAction == 2)
                foreach (byte[] report in Media(fixture)) AssertMotorsOff(report);
            byte[][] reports = fixture.Native.Reports.ToArray();
            int accepted = Array.FindIndex(reports, r => MatchesTuple(r, explicitLocal));
            Assert.IsTrue(accepted >= 0, "New explicit local motor command must reach the physical writer.");
            foreach (byte[] report in reports.Skip(accepted)) AssertTuple(report, explicitLocal, "new local owner after Busy");
        }
    }

    [TestMethod]
    public void ActualHelperBusyRefreshRetainsExactQueueAndCannotProtectBeforeOnAcceptance()
    {
        using var fixture = new Fixture();
        byte[] on = Rumble(true, 43, 76);
        fixture.ReceiveNativeCommand(on);
        fixture.ReceiveNativeCommand(Refresh(), Policy.SettingsRefresh);
        fixture.StartBusy();
        fixture.WaitForBusy();
        Assert.AreEqual(2, fixture.NativeOwnershipSnapshot().Admissions);
        Assert.AreEqual(2, fixture.NativeOwnershipSnapshot().Pending);
        Assert.AreEqual(0, fixture.Native.Reports.Count);
        fixture.Native.ReturnCredit();
        WaitNative(fixture);
        fixture.Stop();
        Assert.AreEqual(2, fixture.Native.Reports.Count);
        foreach (byte[] report in fixture.Native.Reports) AssertTuple(report, on, "retry FIFO accepted owner");
        Assert.AreEqual(2, fixture.DrainNativeAcknowledgements().Length);
    }

    [TestMethod]
    public void ActualHelperClearDiscardsPendingRefreshAndNeverRevivesPriorMotorOwner()
    {
        using var fixture = new Fixture();
        fixture.ReceiveNativeCommand(Rumble(true, 43, 76));
        fixture.ReceiveNativeCommand(Refresh(), Policy.SettingsRefresh);
        long clearedClaim = 0;
        fixture.Native.DuringCompletionProbe = () =>
        {
            // Clear while the old real writer attempt is in flight, but its
            // completion callback has explicitly withheld physical credit.
            // Do not race ReturnCredit against a subsequently reclaimed head.
            clearedClaim = fixture.NativeOwnershipSnapshot().ClaimedId;
            fixture.Clear();
            fixture.ReceiveNativeCommand(Refresh(), Policy.SettingsRefresh);
        };
        fixture.StartBusy();
        fixture.WaitForBusy();
        Assert.AreEqual(1L, clearedClaim, "The clear must overlap the already claimed old ON.");
        Assert.IsTrue(SpinWait.SpinUntil(() => fixture.NativeOwnershipSnapshot().ClaimedId == 0 &&
            fixture.NativeAcknowledgementsSnapshot().Count(r =>
                r.Disposition == DualSenseBluetoothAudioPacer.AcknowledgementDisposition.Cleared) == 2, 2000),
            "The rejected old claim must retire before physical credit returns.");
        fixture.Native.ReturnCredit();
        WaitNative(fixture);
        fixture.Stop();
        var receipts = fixture.DrainNativeAcknowledgements();
        string evidence = "Reports=" + string.Join(";", fixture.Native.Reports.Select(Convert.ToHexString)) +
            "; ACKs=" + string.Join(";", receipts.Select(r => $"{r.Id}/{r.Generation}/{r.Disposition}"));
        Assert.AreEqual(1, fixture.Native.Reports.Count, evidence);
        AssertTuple(fixture.Native.Reports.Single(), Zero(), "new lifecycle has no accepted motor owner");
        Assert.AreEqual(2, receipts.Count(r => r.Disposition == DualSenseBluetoothAudioPacer.AcknowledgementDisposition.Cleared));
        Assert.AreEqual(1, receipts.Count(r => r.Disposition == DualSenseBluetoothAudioPacer.AcknowledgementDisposition.Presented));
    }

    private static void StartOn(Fixture fixture, byte[] on)
    {
        fixture.ReceiveNativeCommand(on); fixture.StartIdle(); WaitNative(fixture);
        AssertTuple(fixture.Native.Reports.Single(), on, "initial explicit owner");
    }
    private static void WaitNative(Fixture fixture) => Assert.IsTrue(SpinWait.SpinUntil(() => fixture.NativeOwnershipSnapshot().Admissions == 0, 2000), "Native FIFO did not finish.");
    private static void WaitReports(Fixture fixture, int count) => Assert.IsTrue(SpinWait.SpinUntil(() => fixture.Native.Reports.Count >= count, 2000), "Physical synthetic submission did not arrive.");
    private static void WaitMedia(Fixture fixture, int count) => Assert.IsTrue(SpinWait.SpinUntil(() => fixture.Native.Reports.Count(r => r[0] == 0x36) >= count, 2000), "Finite media did not finish.");
    private static DualSenseRealtimeHapticsSharedRing Ring(Fixture fixture) => (DualSenseRealtimeHapticsSharedRing)typeof(Fixture).GetField("realtime", Private)!.GetValue(fixture)!;
    private static object Host(Fixture fixture) => typeof(Fixture).GetField("host", Private)!.GetValue(fixture)!;
    private static void Publish(Fixture fixture, byte[] data) => Assert.IsTrue(Ring(fixture).Publish(data, 0, 1, long.MaxValue, Stopwatch.GetTimestamp()));
    private static byte[][] Media(Fixture fixture) => fixture.Native.Reports.Where(r => r[0] == 0x36).ToArray();
    private static byte[] Block(byte value) => Enumerable.Repeat(value, 64).ToArray();
    private static byte[] Refresh() => Convert.FromHexString(CapturedRefresh);
    private static byte[] Zero() { byte[] result = new byte[48]; result[0] = 2; return result; }
    private static byte[] Trigger() { byte[] result = Zero(); result[1] = 8; result[22] = 0x21; result[23] = 0x56; return result; }
    private static byte[] Rumble(bool improved, byte light, byte heavy)
    {
        byte[] result = Zero(); result[1] = improved ? (byte)2 : (byte)3; result[39] = improved ? (byte)4 : (byte)0;
        result[3] = light; result[4] = heavy; return result;
    }
    private static int Offset(byte[] report) => report[0] == 0x36 ? 13 : 3;
    private static bool MatchesTuple(byte[] report, byte[] command)
    {
        int offset = Offset(report);
        return (report[offset] & 3) == (command[1] & 3) &&
            (report[offset + 38] & 4) == (command[39] & 4) &&
            report[offset + 2] == command[3] && report[offset + 3] == command[4];
    }
    private static void AssertTuple(byte[] report, byte[] command, string context)
    {
        int offset = Offset(report);
        Assert.AreEqual(command[1] & 3, report[offset] & 3, context + ": mode");
        Assert.AreEqual(command[39] & 4, report[offset + 38] & 4, context + ": improved selector");
        Assert.AreEqual(command[3], report[offset + 2], context + ": light");
        Assert.AreEqual(command[4], report[offset + 3], context + ": heavy");
    }
    private static void AssertMotorsOff(byte[] report)
    {
        int offset = Offset(report); Assert.AreEqual((byte)0, report[offset + 2]); Assert.AreEqual((byte)0, report[offset + 3]);
    }
    private static void AssertMedia(Fixture fixture, byte[][] expectedBlocks, byte expectedGain)
    {
        byte[][] media = Media(fixture);
        Assert.AreEqual(8, media.Length);
        for (int index = 0; index < media.Length; index++)
        {
            CollectionAssert.AreEqual(index < expectedBlocks.Length ? expectedBlocks[index] : new byte[64], media[index].AsSpan(78, 64).ToArray(), "PCM bytes/order and final silence");
            Assert.IsTrue(media[index].AsSpan(144, 200).ToArray().All(b => b == index + 1), "Finite front speaker order changed.");
            Assert.AreEqual(expectedGain, media[index][50], "Audio gain changed.");
        }
    }
}
