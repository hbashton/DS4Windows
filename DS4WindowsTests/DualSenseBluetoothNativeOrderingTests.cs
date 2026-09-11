using System.Buffers.Binary;
using DS4Windows.InputDevices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Fixture = DS4Windows.Tests.DualSenseBluetoothNativeIdleRetryTests.Fixture;

namespace DS4Windows.Tests;

/// <summary>
/// Actual HelperHost receive/presentation and physical writer, with synthetic
/// native I/O. Native command assertions remain separate from the local latest
/// state latch and independently owned finite PCM/media generations.
/// </summary>
[TestClass]
[DoNotParallelize]
public class DualSenseBluetoothNativeOrderingTests
{
    [DataTestMethod]
    [DataRow("pulse-stop")]
    [DataRow("trigger-a-b")]
    [DataRow("trigger-a-b-a")]
    [DataRow("led-claim-release")]
    [DataRow("led-release-claim")]
    [DataRow("duplicate-rumble")]
    public void ActualHelperPreservesNativeBurstBeforeIdlePresentation(string kind)
    {
        using var fixture = new Fixture();
        byte[][] commands = Commands(kind);
        foreach (byte[] command in commands) fixture.ReceiveNativeCommand(command);
        Assert.AreEqual(0, fixture.Native.Reports.Count,
            "Both receives must return while the actual presenter is still unstarted.");
        fixture.StartIdle();
        WaitAndStop(fixture, commands.Length);
        AssertCommands(fixture.Native.Reports.ToArray(), commands, kind);
        AssertPresentedReceipts(fixture, commands.Length);
    }

    [DataTestMethod]
    [DataRow("pulse-stop")]
    [DataRow("trigger-a-b")]
    public void ActualHelperSpacedNativeCommandsRemainPositiveControls(string kind)
    {
        using var fixture = new Fixture();
        byte[][] commands = Commands(kind);
        fixture.ReceiveNativeCommand(commands[0]);
        fixture.StartIdle();
        Assert.IsTrue(SpinWait.SpinUntil(() => fixture.Native.Reports.Count >= 1, 2000));
        fixture.ReceiveNativeCommand(commands[1]);
        WaitAndStop(fixture, 2);
        AssertCommands(fixture.Native.Reports.ToArray(), commands, kind);
        AssertPresentedReceipts(fixture, commands.Length);
    }

    [DataTestMethod]
    [DataRow("pulse-stop")]
    [DataRow("trigger-a-b")]
    public void ActualHelperBusyHeadCannotBeReplacedByLaterNativeCommand(string kind)
    {
        using var fixture = new Fixture();
        byte[][] commands = Commands(kind);
        fixture.ReceiveNativeCommand(commands[0]);
        fixture.StartBusy();
        fixture.WaitForBusy();
        Assert.AreEqual(0, fixture.AcknowledgementCount,
            "A physical Busy cannot return an accepted command's credit.");
        fixture.ReceiveNativeCommand(commands[1]);
        fixture.Native.ReturnCredit();
        WaitAndStop(fixture, 2);
        AssertCommands(fixture.Native.Reports.ToArray(), commands, kind);
        AssertPresentedReceipts(fixture, commands.Length);
    }

    [DataTestMethod]
    [DataRow("pulse-stop")]
    [DataRow("trigger-a-b")]
    [DataRow("led-claim-release")]
    public void ActualHelperNativeBurstRemainsOrderedAcrossMediaPiggyback(string kind)
    {
        using var fixture = new Fixture();
        byte[][] commands = Commands(kind);
        fixture.QueueSpeakerReports(8);
        fixture.ReceiveNativeCommand(commands[0]);
        fixture.ReceiveNativeCommand(commands[1]);
        fixture.StartIdle();
        _ = SpinWait.SpinUntil(() => fixture.Native.Reports.Count(report => report[0] == 0x36) >= 8 &&
            fixture.NativeOwnershipSnapshot().Admissions == 0, 2000);
        fixture.Stop();
        byte[][] reports = fixture.Native.Reports.ToArray();
        byte[][] mediaReports = reports.Where(report => report[0] == 0x36).ToArray();
        Assert.AreEqual(8, mediaReports.Length,
            "Native ordering must not manufacture or discard a source media interval.");
        Assert.AreEqual((byte)0x36, reports[0][0],
            "The due startup media frame must carry the first native command.");
        Assert.IsTrue(reports.All(report => report[0] is 0x31 or 0x36));
        for (int index = 0; index < mediaReports.Length; index++)
            Assert.IsTrue(mediaReports[index].AsSpan(144, 200).ToArray().All(value => value == index + 1),
                "Native command piggyback must preserve every original speaker interval and its order.");
        byte[][] nativeReports = reports.Where(report => HasCommand(report, kind)).ToArray();
        AssertCommands(nativeReports, commands, kind);
        AssertPresentedReceipts(fixture, commands.Length);
    }

    [TestMethod]
    public void ActualHelperClearCancelsEntireOldNativeBurst()
    {
        using var fixture = new Fixture();
        byte[][] old = Commands("pulse-stop");
        fixture.ReceiveNativeCommand(old[0]);
        fixture.ReceiveNativeCommand(old[1]);
        fixture.Clear();
        byte[] current = Rumble(17, 41);
        fixture.ReceiveNativeCommand(current);
        fixture.StartIdle();
        WaitAndStop(fixture, 1);
        AssertCommands(fixture.Native.Reports.ToArray(), new[] { current }, "pulse-stop");
        var receipts = fixture.DrainNativeAcknowledgements();
        CollectionAssert.AreEqual(new long[] { 1, 2, 3 }, receipts.Select(ack => ack.Id).ToArray());
        CollectionAssert.AreEqual(new[] { 1, 1, 2 }, receipts.Select(ack => ack.Generation).ToArray());
        CollectionAssert.AreEqual(new[]
        {
            DualSenseBluetoothAudioPacer.AcknowledgementDisposition.Cleared,
            DualSenseBluetoothAudioPacer.AcknowledgementDisposition.Cleared,
            DualSenseBluetoothAudioPacer.AcknowledgementDisposition.Presented
        }, receipts.Select(ack => ack.Disposition).ToArray());
    }

    [TestMethod]
    public void ActualHelperStopRetiresBusyHeadAndAcceptedNativeTail()
    {
        using var fixture = new Fixture();
        byte[][] commands = Commands("pulse-stop");
        fixture.ReceiveNativeCommand(commands[0]);
        fixture.StartBusy();
        fixture.WaitForBusy();
        fixture.ReceiveNativeCommand(commands[1]);
        fixture.Stop();
        fixture.Native.ReturnCredit();
        Assert.AreEqual(0, fixture.Native.Reports.Count);
    }

    [TestMethod]
    public void ActualHelperLocalStateRemainsLatestValueNotNativeFifo()
    {
        using var fixture = new Fixture();
        fixture.ReceiveLocalState(Rumble(91, 173));
        byte[] latest = Rumble(0, 0);
        fixture.ReceiveLocalState(latest);
        fixture.StartIdle();
        WaitAndStop(fixture, 1);
        AssertCommands(fixture.Native.Reports.ToArray(), new[] { latest }, "pulse-stop");
    }

    [TestMethod]
    public void ActualHelperIdenticalTriggerCommandsKeepExistingTransitionDeduplication()
    {
        using var fixture = new Fixture();
        byte[] command = Trigger(11);
        fixture.ReceiveNativeCommand(command);
        fixture.ReceiveNativeCommand(command);
        fixture.StartIdle();
        Assert.IsTrue(SpinWait.SpinUntil(() => fixture.AcknowledgementCount >= 2, 2000),
            "Both accepted identities need terminal receipts even when the second trigger strobe is redundant.");
        fixture.Stop();
        byte[][] strobes = fixture.Native.Reports.Where(report => HasCommand(report, "trigger-a-b")).ToArray();
        AssertCommands(strobes, new[] { command }, "trigger-a-b");
        AssertPresentedReceipts(fixture, 2);
    }

    [TestMethod]
    public void ActualHelperEntireAdvertisedNativeCapacityIsOrderedWithoutMedia()
    {
        using var fixture = new Fixture();
        byte[][] commands = Enumerable.Range(1, DualSenseBluetoothAudioPacer.NativeCommandCapacity)
            .Select(index => Rumble((byte)index, (byte)(index + 80))).ToArray();
        foreach (byte[] command in commands) fixture.ReceiveNativeCommand(command);
        Assert.AreEqual(0, fixture.AcknowledgementCount,
            "Admission is not presentation and cannot release parent credit.");
        fixture.StartIdle();
        WaitAndStop(fixture, commands.Length);
        AssertCommands(fixture.Native.Reports.ToArray(), commands, "pulse-stop");
        AssertPresentedReceipts(fixture, commands.Length);
    }

    [TestMethod]
    public void ActualHelperNativeHeadDoesNotInheritLocalLatestStateMediaDelay()
    {
        using var fixture = new Fixture();
        fixture.QueueSpeakerReports(8);
        fixture.ReceiveLocalState(Rumble(0, 0));
        byte[] native = Rumble(91, 173);
        fixture.ReceiveNativeCommand(native);
        fixture.StartIdle();
        WaitAndStop(fixture, 1);
        byte[][] reports = fixture.Native.Reports.ToArray();
        Assert.IsTrue(reports.Length >= 1);
        Assert.AreEqual((byte)0x36, reports[0][0]);
        AssertCommands(new[] { reports[0] }, new[] { native }, "pulse-stop");
        AssertPresentedReceipts(fixture, 1);
    }

    [DataTestMethod]
    [DataRow("pulse-stop")]
    [DataRow("led-claim-release")]
    public void ActualHelperOlderOverlappingLocalStateCannotReplayAfterNativeCommand(string kind)
    {
        using var fixture = new Fixture();
        byte[][] commands = Commands(kind);
        fixture.ReceiveLocalState(commands[0]);
        fixture.ReceiveNativeCommand(commands[1]);
        fixture.QueueSpeakerReports(8);
        fixture.StartIdle();
        WaitAndStop(fixture, 8);
        byte[][] reports = fixture.Native.Reports.ToArray();
        Assert.AreEqual(8, reports.Length);
        AssertCommands(reports.Where(report => HasCommand(report, kind)).ToArray(),
            new[] { commands[1] }, kind);
        AssertPresentedReceipts(fixture, 1);
    }

    [TestMethod]
    public void ActualHelperNewerLocalRumbleStillFollowsEarlierNativeStop()
    {
        using var fixture = new Fixture();
        byte[] stop = Rumble(0, 0), newer = Rumble(91, 173);
        fixture.ReceiveNativeCommand(stop);
        fixture.ReceiveLocalState(newer);
        fixture.QueueSpeakerReports(8);
        fixture.StartIdle();
        WaitAndStop(fixture, 8);
        AssertCommands(fixture.Native.Reports.Where(report => HasCommand(report, "pulse-stop")).ToArray(),
            new[] { stop, newer }, "pulse-stop");
        AssertPresentedReceipts(fixture, 1);
    }

    [TestMethod]
    public void ActualHelperUnrelatedOlderLocalLedSurvivesNativeRumbleStop()
    {
        using var fixture = new Fixture();
        byte[] led = Led(release: false), stop = Rumble(0, 0);
        fixture.ReceiveLocalState(led);
        fixture.ReceiveNativeCommand(stop);
        fixture.QueueSpeakerReports(8);
        fixture.StartIdle();
        WaitAndStop(fixture, 8);
        byte[][] reports = fixture.Native.Reports.ToArray();
        AssertCommands(reports.Where(report => HasCommand(report, "pulse-stop")).ToArray(),
            new[] { stop }, "pulse-stop");
        AssertCommands(reports.Where(report => HasCommand(report, "led-claim-release")).ToArray(),
            new[] { led }, "led-claim-release");
        AssertPresentedReceipts(fixture, 1);
    }

    [DataTestMethod]
    [DataRow("pulse-stop")]
    [DataRow("trigger-a-b")]
    public void ActualHelperFinalNativeCommitUpdatesBothStaleTemplatesAndPreservesLocalMedia(string kind)
    {
        using var fixture = new Fixture();
        byte[][] commands = Commands(kind);
        fixture.QueueSpeakerReports(8);
        fixture.ReceiveTemplateShape(commands[0], 0x31);
        fixture.ReceiveTemplateShape(commands[0], 0x71);
        byte[] previous = fixture.TemplateSnapshot("previousTemplate");
        byte[] latest = fixture.TemplateSnapshot("latestTemplate");
        fixture.ReceiveNativeCommand(commands[1]);
        fixture.StartIdle();
        WaitAndStop(fixture, 8);
        byte[][] reports = fixture.Native.Reports.ToArray();
        Assert.AreEqual(8, reports.Length);
        AssertCommands(reports.Where(report => HasCommand(report, kind)).ToArray(),
            new[] { commands[1] }, kind);
        AssertCommittedTemplate(fixture.TemplateSnapshot("previousTemplate"), previous, commands[1]);
        AssertCommittedTemplate(fixture.TemplateSnapshot("latestTemplate"), latest, commands[1]);
        for (int index = 0; index < reports.Length; index++)
        {
            int state = StateOffset(reports[index]);
            if (kind == "pulse-stop")
            {
                Assert.AreEqual(commands[1][3], reports[index][state + 2]);
                Assert.AreEqual(commands[1][4], reports[index][state + 3]);
            }
            else CollectionAssert.AreEqual(commands[1].AsSpan(11, 11).ToArray(),
                reports[index].AsSpan(state + 10, 11).ToArray());
        }
        AssertPresentedReceipts(fixture, 1);
    }

    [TestMethod]
    public void ActualHelperAcknowledgementLoopKeepsNativeAndMediaWireContractsDistinct()
    {
        using var fixture = new Fixture();
        fixture.QueueSpeakerReports(8);
        fixture.ReceiveNativeCommand(Rumble(91, 173));
        fixture.StartAcknowledgements();
        fixture.StartIdle();
        WaitAndStop(fixture, 8);
        byte[] response = fixture.ResponseBytes();
        int offset = 0, nativeCount = 0, mediaCount = 0;
        while (offset < response.Length)
        {
            Assert.IsTrue(response.Length - offset >= 5, "No partial frame header may survive loop join.");
            byte kind = response[offset];
            int length = BinaryPrimitives.ReadInt32LittleEndian(response.AsSpan(offset + 1, 4));
            offset += 5;
            Assert.IsTrue(length >= 0 && response.Length - offset >= length);
            ReadOnlySpan<byte> payload = response.AsSpan(offset, length);
            if (kind == 0x83)
            {
                nativeCount++;
                Assert.AreEqual(13, length);
                Assert.AreEqual(1L, BinaryPrimitives.ReadInt64LittleEndian(payload));
                Assert.AreEqual(1, BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(8)));
                Assert.AreEqual((byte)DualSenseBluetoothAudioPacer.AcknowledgementDisposition.Presented, payload[12]);
            }
            else
            {
                Assert.AreEqual((byte)0x81, kind);
                mediaCount++;
                Assert.AreEqual(153, length);
                Assert.AreEqual((long)mediaCount, BinaryPrimitives.ReadInt64LittleEndian(payload));
                Assert.AreEqual((byte)DualSenseBluetoothAudioPacer.AcknowledgementDisposition.Presented, payload[8]);
            }
            offset += length;
        }
        Assert.AreEqual(1, nativeCount);
        Assert.AreEqual(8, mediaCount);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ActualHelperClearDuringAcceptedNativeClaimRetainsSlotUntilPhysicalReturn(bool media)
    {
        using var fixture = new Fixture();
        byte[] oldHead = Rumble(91, 173), oldTail = Rumble(0, 0), current = Rumble(17, 41);
        if (media) fixture.QueueSpeakerReports(8);
        fixture.ReceiveNativeCommand(oldHead);
        fixture.ReceiveNativeCommand(oldTail);
        (int PoolAvailable, int Pending, int Admissions, long ClaimedId) beforeClear = default,
            afterClear = default, afterNewAdmission = default;
        (long Id, int Generation, DualSenseBluetoothAudioPacer.AcknowledgementDisposition Disposition)[]
            duringClaimReceipts = null;
        byte[] newTemplate = null, beforeNewCommit = null;
        Exception callbackError = null;
        int callbackCount = 0;
        fixture.Native.DuringSubmit = () =>
        {
            try
            {
                callbackCount++;
                beforeClear = fixture.NativeOwnershipSnapshot();
                fixture.Clear();
                afterClear = fixture.NativeOwnershipSnapshot();
                duringClaimReceipts = fixture.NativeAcknowledgementsSnapshot();
                fixture.ReceiveTemplateShape(current, 0x77);
                newTemplate = fixture.TemplateSnapshot("latestTemplate");
                fixture.ReceiveNativeCommand(current);
                afterNewAdmission = fixture.NativeOwnershipSnapshot();
                fixture.Native.DuringSubmit = () =>
                {
                    try
                    {
                        callbackCount++;
                        beforeNewCommit = fixture.TemplateSnapshot("latestTemplate");
                    }
                    catch (Exception error) { callbackError = error; }
                };
            }
            catch (Exception error) { callbackError = error; }
        };
        fixture.StartIdle();
        WaitAndStop(fixture, 2);
        Assert.IsNull(callbackError, callbackError?.ToString());
        Assert.AreEqual(2, callbackCount);
        Assert.AreEqual((30, 2, 2, 1L), beforeClear);
        Assert.AreEqual((31, 0, 1, 1L), afterClear,
            "Clear may recycle only the unclaimed tail; the in-flight head still owns its slot and credit.");
        Assert.AreEqual((30, 1, 2, 1L), afterNewAdmission,
            "A new generation cannot reuse the old physical claim's slot or release its credit.");
        Assert.AreEqual(1, duringClaimReceipts.Length);
        Assert.AreEqual((2L, 1, DualSenseBluetoothAudioPacer.AcknowledgementDisposition.Cleared),
            duringClaimReceipts[0], "Only the unclaimed old tail can be acknowledged inside native submission.");
        CollectionAssert.AreEqual(newTemplate, beforeNewCommit,
            "The old successful write must not commit its quiescent state over the new generation's template.");
        byte[][] reports = fixture.Native.Reports.ToArray();
        Assert.AreEqual(media ? (byte)0x36 : (byte)0x31, reports[0][0]);
        Assert.AreEqual((byte)0x31, reports[1][0]);
        AssertCommands(reports, new[] { oldHead, current }, "pulse-stop");
        var receipts = fixture.DrainNativeAcknowledgements();
        CollectionAssert.AreEqual(new[] { 2L, 1L, 3L }, receipts.Select(ack => ack.Id).ToArray());
        CollectionAssert.AreEqual(new[] { 1, 1, 2 }, receipts.Select(ack => ack.Generation).ToArray());
        CollectionAssert.AreEqual(new[]
        {
            DualSenseBluetoothAudioPacer.AcknowledgementDisposition.Cleared,
            DualSenseBluetoothAudioPacer.AcknowledgementDisposition.Cleared,
            DualSenseBluetoothAudioPacer.AcknowledgementDisposition.Presented
        }, receipts.Select(ack => ack.Disposition).ToArray());
        Assert.AreEqual((32, 0, 0, 0L), fixture.NativeOwnershipSnapshot());
        AssertCommittedTemplate(fixture.TemplateSnapshot("latestTemplate"), newTemplate, current);
    }

    private static void AssertCommittedTemplate(byte[] actual, byte[] previous, byte[] command)
    {
        byte[] expected = command.AsSpan(1, 47).ToArray();
        DualSenseDevice.ConsumeNativeGameStateValidity(expected, 0);
        expected[0] = (byte)(previous[13] & 0xF0);
        expected[1] = (byte)(previous[14] & 0x83);
        previous.AsSpan(17, 6).CopyTo(expected.AsSpan(4));
        expected[37] = previous[50];
        CollectionAssert.AreEqual(expected, actual.AsSpan(13, 47).ToArray(),
            "The final consumed command must remain cached without reviving validity or replacing local audio controls.");
        CollectionAssert.AreEqual(previous.AsSpan(78, 64).ToArray(), actual.AsSpan(78, 64).ToArray(),
            "An exact native control commit does not own finite haptics payloads.");
    }

    private static void AssertPresentedReceipts(Fixture fixture, int count)
    {
        var receipts = fixture.DrainNativeAcknowledgements();
        CollectionAssert.AreEqual(Enumerable.Range(1, count).Select(id => (long)id).ToArray(),
            receipts.Select(ack => ack.Id).ToArray(), "Exactly one terminal receipt per accepted native identity.");
        Assert.IsTrue(receipts.All(ack => ack.Generation == 1 &&
            ack.Disposition == DualSenseBluetoothAudioPacer.AcknowledgementDisposition.Presented));
    }

    private static void WaitAndStop(Fixture fixture, int expected)
    {
        // Do not assert on the wait result: stop/join first, then report the
        // actual immutable physical-write witnesses in the ordering assertion.
        _ = SpinWait.SpinUntil(() => fixture.Native.Reports.Count >= expected, 2000);
        fixture.Stop();
    }

    private static bool HasCommand(byte[] report, string kind)
    {
        int state = StateOffset(report);
        return kind.StartsWith("led-", StringComparison.Ordinal) ?
            (report[state + 1] & 0x0C) != 0 :
            kind.StartsWith("trigger-", StringComparison.Ordinal) ? (report[state] & 4) != 0 :
            (report[state] & 3) == 3;
    }

    private static void AssertCommands(byte[][] reports, byte[][] commands, string kind)
    {
        Assert.AreEqual(commands.Length, reports.Length,
            $"Every accepted exact command needs its own ordered presentation; observed {Describe(reports)}.");
        for (int index = 0; index < commands.Length; index++)
        {
            byte[] report = reports[index], command = commands[index];
            int state = StateOffset(report);
            if (kind.StartsWith("led-", StringComparison.Ordinal))
            {
                Assert.AreEqual(command[2] & 0x0C, report[state + 1] & 0x0C,
                    $"LED command {index} lost its exact claim/release validity.");
                if ((command[2] & 4) != 0)
                    CollectionAssert.AreEqual(command.AsSpan(45, 3).ToArray(), report.AsSpan(state + 44, 3).ToArray());
            }
            else if (kind.StartsWith("trigger-", StringComparison.Ordinal))
            {
                Assert.AreEqual(4, report[state] & 4, $"Trigger command {index} lost its validity.");
                CollectionAssert.AreEqual(command.AsSpan(11, 11).ToArray(), report.AsSpan(state + 10, 11).ToArray());
            }
            else
            {
                Assert.AreEqual(3, report[state] & 3, $"Rumble command {index} lost its validity.");
                Assert.AreEqual(command[3], report[state + 2]);
                Assert.AreEqual(command[4], report[state + 3]);
            }
        }
    }

    private static string Describe(byte[][] reports) => string.Join("; ", reports.Select(report =>
    {
        int state = StateOffset(report);
        return $"id={report[0]:X2},flags={report[state]:X2}/{report[state + 1]:X2},motors={report[state + 2]}/{report[state + 3]},trigger={report[state + 10]:X2}";
    }));

    private static int StateOffset(byte[] report) => report[0] switch
    {
        0x31 => 3,
        0x36 => 13,
        _ => throw new AssertFailedException($"Unexpected physical report {report[0]:X2}.")
    };

    private static byte[][] Commands(string kind) => kind switch
    {
        "pulse-stop" => new[] { Rumble(91, 173), Rumble(0, 0) },
        "duplicate-rumble" => new[] { Rumble(91, 173), Rumble(91, 173) },
        "trigger-a-b" => new[] { Trigger(11), Trigger(61) },
        "trigger-a-b-a" => new[] { Trigger(11), Trigger(61), Trigger(11) },
        "led-claim-release" => new[] { Led(release: false), Led(release: true) },
        "led-release-claim" => new[] { Led(release: true), Led(release: false) },
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static byte[] Rumble(byte light, byte heavy)
    {
        byte[] command = new byte[48];
        command[0] = 0x02;
        command[1] = 3;
        command[3] = light;
        command[4] = heavy;
        return command;
    }

    private static byte[] Trigger(byte seed)
    {
        byte[] command = new byte[48];
        command[0] = 0x02;
        command[1] = 4;
        command[11] = 0x21;
        for (int index = 12; index < 22; index++) command[index] = (byte)(index + seed);
        return command;
    }

    private static byte[] Led(bool release)
    {
        byte[] command = new byte[48];
        command[0] = 0x02;
        command[2] = release ? (byte)8 : (byte)4;
        if (!release)
        {
            command[45] = 0x31;
            command[46] = 0x72;
            command[47] = 0xB4;
        }
        return command;
    }
}
