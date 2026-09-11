using System.Buffers.Binary;
using System.Reflection;
using DS4Windows.InputDevices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelperFixture = DS4Windows.Tests.DualSenseBluetoothNativeIdleRetryTests.Fixture;

namespace DS4Windows.Tests;

/// <summary>
/// Reproduces the caller/helper circular wait when a native burst fills the
/// command credits before the initial eight speaker reports have arrived.
/// Uses the existing isolated fixtures: no controller handles or app launch.
/// </summary>
[TestClass]
[DoNotParallelize]
public class DualSenseBluetoothNativePrimeLivenessTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [DataTestMethod]
    [DataRow(0, false, false)]
    [DataRow(1, false, false)]
    [DataRow(7, false, false)]
    [DataRow(8, false, false)]
    [DataRow(1, true, false)]
    [DataRow(7, true, false)]
    [DataRow(8, true, false)]
    [DataRow(1, true, true)]
    [DataRow(7, true, true)]
    [DataRow(8, true, true)]
    public void NativeCreditPressureMustProgressBeforeSpeakerPrimeCompletes(int queuedSpeakerReports,
        bool microphoneDisablePending, bool afterClear)
    {
        using var helper = new HelperFixture();
        Type parentFixtureType = typeof(DualSenseBluetoothNativeBackpressureTests)
            .GetNestedType("Fixture", BindingFlags.NonPublic);
        using var parent = (IDisposable)Activator.CreateInstance(parentFixtureType, true);
        var device = (DualSenseDevice)Field(parent, "Device");
        var pacer = (DualSenseBluetoothAudioPacer)Field(parent, "Pacer");
        var mailbox = (DualSensePhysicalOutputStateMailbox)Field(device, "physicalOutputStateMailbox");
        mailbox.SetEnableSpeakerOutput(true);
        if (afterClear)
        {
            Assert.IsTrue(pacer.Clear());
            ForwardCommands(pacer, helper);
        }

        // These frames arrive before the native burst, so their media position
        // cannot be recovered by replacing or cancelling a later native command.
        QueueSpeakerReports(helper, queuedSpeakerReports, afterClear ? 2 : 1);
        for (int index = 0; index < DualSenseBluetoothAudioPacer.NativeCommandCapacity; index++)
        {
            Invoke(parent, "QueueNative", (byte)(index + 1), (byte)(index + 81));
            Assert.AreEqual(DualSenseDevice.PhysicalOutputCommandProcessResult.Published,
                device.ProcessNextPhysicalOutputCommand());
        }
        ForwardCommands(pacer, helper);
        Assert.AreEqual(32, helper.NativeOwnershipSnapshot().Admissions);
        if (microphoneDisablePending)
        {
            helper.SetCommittedMicrophoneEnabled();
            helper.ReceiveMicrophoneDisable();
            Assert.AreEqual(2, helper.ReportsAhead);
        }

        Invoke(parent, "QueueNative", (byte)37, (byte)83);
        Assert.AreEqual(DualSenseDevice.PhysicalOutputCommandProcessResult.Published,
            device.ProcessNextPhysicalOutputCommand());
        Assert.IsTrue((long)Field(device, "pendingBluetoothNativeGameRevision") > 0);
        if (microphoneDisablePending)
        {
            for (int index = queuedSpeakerReports; index < 8; index++)
            {
                byte[] opus = Enumerable.Repeat((byte)(index + 1), 200).ToArray();
                Assert.IsTrue(device.SetBluetoothSpeakerAudioFrame(opus, opus.Length),
                    "Independent speaker media must fill prime while the retained native command waits for credit.");
            }
            ForwardCommands(pacer, helper);
        }
        Invoke(parent, "AssertNoRecovery");

        helper.StartIdle();
        _ = SpinWait.SpinUntil(() => helper.NativeOwnershipSnapshot().Admissions == 0, 2000);
        var state = helper.NativeOwnershipSnapshot();
        helper.Stop();
        byte[][] reports = helper.Native.Reports.ToArray();

        Assert.AreEqual(0, state.Admissions,
            $"Native commands cannot release any credit with {queuedSpeakerReports} queued speaker reports: " +
            $"{state.Pending} native commands remain, {reports.Length} physical reports were submitted. " +
            "The caller cannot publish the remaining prime frames until a native credit returns.");
        if (microphoneDisablePending)
        {
            Assert.IsTrue(reports.Length >= 3);
            for (int index = 0; index < 2; index++)
            {
                Assert.AreEqual((byte)0x36, reports[index][0]);
                Assert.AreEqual(0, reports[index][4] & 1);
                Assert.AreEqual(0, reports[index][13] & 3,
                    "Native commands must remain behind the actual two-media-frame microphone boundary.");
            }
            Assert.AreEqual((byte)0x32, reports[2][0]);
        }
        byte[][] nativeReports = reports.Where(report => report[0] is 0x31 or 0x36 &&
            (report[report[0] == 0x31 ? 3 : 13] & 3) == 3).ToArray();
        nativeReports = DualSenseBluetoothNativeOrderingTests.RumbleTransitions(nativeReports);
        Assert.AreEqual(32, nativeReports.Length);
        Assert.AreEqual(32, helper.DrainNativeAcknowledgements().Length,
            "Continuous mode on media must not manufacture native command completions.");
        for (int index = 0; index < nativeReports.Length; index++)
        {
            int stateOffset = nativeReports[index][0] == 0x31 ? 3 : 13;
            Assert.AreEqual((byte)(index + 1), nativeReports[index][stateOffset + 2]);
        }
        Assert.AreEqual((byte)32, helper.TemplateSnapshot("latestTemplate")[15],
            "The retained thirty-third command cannot leak through the independent media template.");
    }

    [DataTestMethod]
    [DataRow(1, false, false, false)]
    [DataRow(7, false, false, false)]
    [DataRow(1, false, true, false)]
    [DataRow(7, false, true, false)]
    [DataRow(1, true, false, false)]
    [DataRow(7, true, false, false)]
    [DataRow(1, true, true, false)]
    [DataRow(7, true, true, false)]
    [DataRow(1, false, false, true)]
    [DataRow(7, false, false, true)]
    [DataRow(1, false, true, true)]
    [DataRow(7, false, true, true)]
    [DataRow(1, true, false, true)]
    [DataRow(7, true, false, true)]
    [DataRow(1, true, true, true)]
    [DataRow(7, true, true, true)]
    public void NativeAndLocalTriggersDoNotNeedPartialPrimeToFinish(int speakerReports,
        bool afterClear, bool physicalBusy, bool native)
    {
        using var helper = new HelperFixture();
        if (afterClear) helper.Clear();
        byte[] trigger = Trigger();
        if (native) helper.ReceiveNativeCommand(trigger);
        else helper.ReceiveLocalState(trigger);
        QueueSpeakerReports(helper, speakerReports, afterClear ? 2 : 1);
        if (physicalBusy)
        {
            helper.StartBusy();
            helper.WaitForBusy();
            Assert.AreEqual(0, helper.ReportsAhead,
                "A partial prime cannot satisfy a media-fairness debt after Busy.");
            helper.Native.ReturnCredit();
        }
        else helper.StartIdle();
        _ = SpinWait.SpinUntil(() => helper.Native.Reports.Count > 0, 2000);
        helper.Stop();
        byte[][] reports = helper.Native.Reports.ToArray();
        Assert.AreEqual(1, reports.Length,
            "The pending trigger must progress without spending any unprimed media.");
        Assert.AreEqual((byte)0x31, reports[0][0]);
        Assert.AreEqual(4, reports[0][3] & 4);
        CollectionAssert.AreEqual(trigger.AsSpan(11, 11).ToArray(), reports[0].AsSpan(13, 11).ToArray());
    }

    [DataTestMethod]
    [DataRow(1, false, false)]
    [DataRow(7, false, false)]
    [DataRow(1, true, false)]
    [DataRow(7, true, false)]
    [DataRow(1, false, true)]
    [DataRow(7, false, true)]
    [DataRow(1, true, true)]
    [DataRow(7, true, true)]
    public void LocalTriggerReceivedAfterPartialPrimeMustProgress(int speakerReports, bool physicalBusy,
        bool afterClear)
    {
        using var helper = new HelperFixture();
        if (afterClear) helper.Clear();
        QueueSpeakerReports(helper, speakerReports, afterClear ? 2 : 1);
        helper.ReceiveLocalState(Trigger());
        if (physicalBusy)
        {
            helper.StartBusy();
            helper.WaitForBusy();
            Assert.AreEqual(0, helper.ReportsAhead);
            helper.Native.ReturnCredit();
        }
        else helper.StartIdle();
        _ = SpinWait.SpinUntil(() => helper.Native.Reports.Count > 0, 2000);
        helper.Stop();
        Assert.AreEqual(1, helper.Native.Reports.Count,
            "A local Trigger Lab command cannot rely on future source audio to finish prime.");
    }

    [TestMethod]
    public void SteadyNativeControlWaitsForBothDisableMediaFramesAndMicrophoneStatus()
    {
        using var helper = new HelperFixture();
        object host = Field(helper, "host");
        helper.QueueSpeakerReports(8);
        Invoke(host, "ReceiveMicrophoneStatus", new byte[] { 1 }, 1);
        helper.StartIdle();
        Assert.IsTrue(SpinWait.SpinUntil(() => helper.Native.Reports.Count >= 9, 2000));
        lock (Field(host, "stateLock"))
        {
            helper.ReceiveMicrophoneDisable();
            helper.ReceiveNativeCommand(Trigger());
            helper.QueueSpeakerReports(8);
        }
        _ = SpinWait.SpinUntil(() => helper.NativeOwnershipSnapshot().Admissions == 0 &&
            helper.Native.Reports.Count(report => report[0] == 0x36) >= 16, 2000);
        helper.Stop();
        byte[][] reports = helper.Native.Reports.Skip(9).ToArray();
        Assert.IsTrue(reports.Length >= 9);
        for (int index = 0; index < 2; index++)
        {
            Assert.AreEqual((byte)0x36, reports[index][0]);
            Assert.AreEqual(0, reports[index][4] & 1);
            Assert.AreEqual(0, reports[index][13] & 4);
        }
        Assert.AreEqual((byte)0x32, reports[2][0],
            "A native control slot between media deadlines cannot overtake the ready microphone status.");
        Assert.AreEqual(8, reports.Count(report => report[0] == 0x36));
        Assert.AreEqual(1, reports.Count(report => report[0] is 0x31 or 0x36 &&
            (report[report[0] == 0x31 ? 3 : 13] & 4) != 0));
    }

    [TestMethod]
    public void ProtectedMediaAfterNativeDrainCannotReplayOlderOrExposeFutureTrigger()
    {
        using var helper = new HelperFixture();
        using var parent = CreateParentFixture();
        var device = (DualSenseDevice)Field(parent, "Device");
        var pacer = (DualSenseBluetoothAudioPacer)Field(parent, "Pacer");
        var mailbox = (DualSensePhysicalOutputStateMailbox)Field(device, "physicalOutputStateMailbox");
        mailbox.SetEnableSpeakerOutput(true);
        byte[] admitted = Trigger(11), newerLocal = Trigger(61), future = Trigger(91);
        for (int index = 0; index < 32; index++) QueueNativeCommand(device, admitted);
        ForwardCommands(pacer, helper);
        helper.StartIdle();
        Assert.IsTrue(SpinWait.SpinUntil(() => helper.NativeOwnershipSnapshot().Admissions == 0, 2000));

        // The helper has consumed all native commands, but their ACKs have not
        // reached the parent. A newer explicit local trigger is now physical.
        Assert.IsTrue(pacer.UpdateControllerState(MediaWithState(newerLocal)));
        ForwardCommands(pacer, helper);
        Assert.IsTrue(SpinWait.SpinUntil(() => helper.Native.Reports.Count >= 33, 2000));
        QueueNativeCommand(device, future);
        Assert.IsTrue((long)Field(device, "pendingBluetoothNativeGameRevision") > 0);

        for (int index = 0; index < 8; index++)
        {
            byte[] opus = Enumerable.Repeat((byte)(index + 1), 200).ToArray();
            Assert.IsTrue(device.SetBluetoothSpeakerAudioFrame(opus, opus.Length));
        }
        // Forward only after the native queue is empty: the helper's active
        // FIFO fence cannot hide a future-state leak in these media templates.
        ForwardCommands(pacer, helper);
        _ = SpinWait.SpinUntil(() => helper.Native.Reports.Count >= 41, 2000);
        helper.Stop();
        byte[][] reports = helper.Native.Reports.ToArray();
        Assert.AreEqual(41, reports.Length);
        byte[][] triggerUpdates = reports.Where(report =>
            (report[report[0] == 0x31 ? 3 : 13] & 4) != 0).ToArray();
        Assert.AreEqual(2, triggerUpdates.Length,
            "Media must neither replay the older admitted trigger nor expose the future retained trigger.");
        CollectionAssert.AreEqual(admitted.AsSpan(11, 11).ToArray(), triggerUpdates[0].AsSpan(13, 11).ToArray());
        CollectionAssert.AreEqual(newerLocal.AsSpan(11, 11).ToArray(), triggerUpdates[1].AsSpan(13, 11).ToArray());
        CollectionAssert.AreEqual(admitted.AsSpan(11, 11).ToArray(),
            helper.TemplateSnapshot("latestTemplate").AsSpan(23, 11).ToArray());
        for (int index = 0; index < 8; index++)
        {
            Assert.AreEqual((byte)0x36, reports[index + 33][0]);
            Assert.AreEqual(0, reports[index + 33][13] & 0x0F);
            Assert.IsTrue(reports[index + 33].AsSpan(144, 200).ToArray().All(value => value == index + 1));
        }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ProtectedSpeakerAdmissionRejectsLastNativeStateFromEarlierGeneration(bool clear)
    {
        using var parent = CreateParentFixture();
        var pacer = (DualSenseBluetoothAudioPacer)Field(parent, "Pacer");
        byte[] oldState = MediaWithState(Trigger(11));
        byte[] quiescent = oldState.ToArray();
        DualSenseDevice.ConsumeNativeGameStateValidity(quiescent, 13);
        Assert.IsTrue(pacer.UpdateGameStateAndTemplate(oldState, quiescent, long.MaxValue));
        Assert.IsTrue(clear ? pacer.Clear() : pacer.ResetControllerStateTransitions());
        byte[] nextMedia = MediaWithState(Trigger(61));
        byte[] unchanged = nextMedia.ToArray();
        Assert.IsFalse(pacer.TryQueueSpeakerReportBeforePendingNativeState(nextMedia, long.MaxValue));
        CollectionAssert.AreEqual(unchanged, nextMedia,
            "A generation rejection must not import retired native state into the new media source.");

        quiescent = nextMedia.ToArray();
        DualSenseDevice.ConsumeNativeGameStateValidity(quiescent, 13);
        Assert.IsTrue(pacer.UpdateGameStateAndTemplate(nextMedia, quiescent, long.MaxValue));
        byte[] futureMedia = MediaWithState(Trigger(91));
        futureMedia[13] |= 0xF0;
        futureMedia[14] |= 0x83;
        futureMedia.AsSpan(17, 6).Fill(0x5A);
        futureMedia[50] = 0x6B;
        Assert.IsTrue(pacer.TryQueueSpeakerReportBeforePendingNativeState(futureMedia, long.MaxValue));
        CollectionAssert.AreEqual(Trigger(61).AsSpan(11, 11).ToArray(), futureMedia.AsSpan(23, 11).ToArray());
        Assert.AreEqual((byte)0xF0, futureMedia[13]);
        Assert.AreEqual((byte)0x83, futureMedia[14]);
        Assert.AreEqual((byte)0, futureMedia[51]);
        Assert.IsTrue(futureMedia.AsSpan(17, 6).ToArray().All(value => value == 0x5A));
        Assert.AreEqual((byte)0x6B, futureMedia[50]);
        Assert.AreEqual(DualSenseBluetoothAudioReportPatcher.ComputeSonyCrc(futureMedia, futureMedia.Length - 4),
            BinaryPrimitives.ReadUInt32LittleEndian(futureMedia.AsSpan(futureMedia.Length - 4)));
    }

    [TestMethod]
    public void ProtectedSpeakerAdmissionCannotUseReplacementPacer()
    {
        using var parent = CreateParentFixture();
        using var replacement = CreateParentFixture();
        var device = (DualSenseDevice)Field(parent, "Device");
        var oldPacer = (DualSenseBluetoothAudioPacer)Field(parent, "Pacer");
        var newPacer = (DualSenseBluetoothAudioPacer)Field(replacement, "Pacer");
        Invoke(parent, "InstallPacer", newPacer);
        object[] args = { MediaWithState(Trigger()), long.MaxValue, false, oldPacer };
        Assert.IsFalse((bool)typeof(DualSenseDevice).GetMethod("TryQueueBluetoothAudioPacerReport", Flags)
            .Invoke(device, args), "A replacement helper cannot inherit the old owner's bypass admission.");
        Assert.IsTrue((bool)args[2]);
    }

    private static IDisposable CreateParentFixture() => (IDisposable)Activator.CreateInstance(
        typeof(DualSenseBluetoothNativeBackpressureTests).GetNestedType("Fixture", BindingFlags.NonPublic), true);

    private static void QueueNativeCommand(DualSenseDevice device, byte[] report)
    {
        Assert.IsTrue(device.WriteRawOutputReportFromGame(report, 0, report.Length));
        Assert.AreEqual(DualSenseDevice.PhysicalOutputCommandProcessResult.Published,
            device.ProcessNextPhysicalOutputCommand());
    }

    private static byte[] MediaWithState(byte[] command)
    {
        byte[] report = new byte[DualSenseBluetoothAudioPacer.ReportLength];
        report[0] = 0x36;
        report[11] = 0x90;
        report[12] = 63;
        command.AsSpan(1, 47).CopyTo(report.AsSpan(13));
        report[76] = 0x92;
        report[77] = 64;
        report[142] = 0x93;
        report[143] = 200;
        return report;
    }

    private static byte[] Trigger(byte seed = 11)
    {
        byte[] command = new byte[48];
        command[0] = 0x02;
        command[1] = 4;
        command[11] = 0x21;
        for (int index = 12; index < 22; index++) command[index] = (byte)(index + seed);
        return command;
    }

    private static void QueueSpeakerReports(HelperFixture helper, int count, int epoch)
    {
        object host = Field(helper, "host");
        for (int index = 0; index < count; index++)
        {
            byte[] report = new byte[DualSenseBluetoothAudioPacer.ReportLength];
            report[0] = 0x36;
            report[11] = 0x90;
            report[12] = 63;
            report[76] = 0x92;
            report[77] = 64;
            report[142] = 0x93;
            report[143] = 200;
            report.AsSpan(144, 200).Fill((byte)(index + 1));
            byte[] payload = new byte[20 + report.Length];
            BinaryPrimitives.WriteInt64LittleEndian(payload, index + 1);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(8), epoch);
            BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(12), long.MaxValue);
            report.CopyTo(payload, 20);
            Invoke(host, "ReceiveQueuedReport", payload, payload.Length);
        }
    }

    private static void ForwardCommands(DualSenseBluetoothAudioPacer pacer, HelperFixture helper)
    {
        object host = Field(helper, "host");
        lock (Field(pacer, "stateLock"))
        {
            var outbound = (DualSenseBluetoothAudioPacerRing<DualSenseBluetoothAudioPacer.OutboundCommand>)
                Field(pacer, "outboundCommands");
            while (outbound.TryDequeue(out var command))
            {
                string receiver = command.Kind switch
                {
                    DualSenseBluetoothAudioPacer.MessageKind.UpdateGameStateAndTemplate => "ReceiveGameStateAndTemplate",
                    DualSenseBluetoothAudioPacer.MessageKind.QueueReport => "ReceiveQueuedReport",
                    DualSenseBluetoothAudioPacer.MessageKind.UpdateControllerState => "ReceiveControllerState",
                    DualSenseBluetoothAudioPacer.MessageKind.Clear => "ReceiveClear",
                    _ => throw new AssertFailedException($"Unexpected command {command.Kind}.")
                };
                Invoke(host, receiver, command.Payload.Buffer, command.PayloadLength);
                // Match sender lease return, leaving each parent native credit
                // outstanding until the helper supplies its terminal receipt.
                Invoke(pacer, "ReleaseOutboundCommandLocked", command);
            }
        }
    }

    private static object Field(object value, string name)
    {
        for (Type type = value.GetType(); type != null; type = type.BaseType)
        {
            FieldInfo field = type.GetField(name, Flags | BindingFlags.DeclaredOnly);
            if (field != null) return field.GetValue(value);
        }
        throw new MissingFieldException(value.GetType().FullName, name);
    }

    private static object Invoke(object value, string name, params object[] args) =>
        value.GetType().GetMethod(name, Flags).Invoke(value, args);
}
