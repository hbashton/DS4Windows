using DS4Windows.InputDevices;
using System.Diagnostics;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Fixture = DS4Windows.Tests.DualSenseBluetoothNativeIdleRetryTests.Fixture;

namespace DS4Windows.Tests;

[TestClass]
[DoNotParallelize]
public class DualSenseBluetoothNativeRumbleModeTests
{
    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void ActualHelperRetainsNativeRumbleModeOnEveryFollowingMediaFrame(
        bool improved, bool idleFirst)
    {
        using var fixture = new Fixture();
        byte[] command = Rumble(improved, 76, 43);
        fixture.ReceiveNativeCommand(command);
        if (idleFirst)
        {
            fixture.StartIdle();
            Assert.IsTrue(SpinWait.SpinUntil(() => fixture.NativeOwnershipSnapshot().Admissions == 0, 2000));
            Assert.AreEqual((byte)0x31, fixture.Native.Reports.Single()[0]);
            fixture.QueueSpeakerReports(8);
        }
        else
        {
            fixture.QueueSpeakerReports(8);
            fixture.StartIdle();
        }

        Assert.IsTrue(SpinWait.SpinUntil(() => fixture.Native.Reports.Count(r => r[0] == 0x36) == 8, 2000));
        fixture.Stop();
        byte[][] media = fixture.Native.Reports.Where(r => r[0] == 0x36).ToArray();
        for (int index = 0; index < media.Length; index++)
        {
            AssertRumble(media[index], command, $"media interval {index}");
            Assert.IsTrue(media[index].AsSpan(144, 200).ToArray().All(b => b == index + 1),
                "The finite speaker payload must remain byte-exact.");
            Assert.IsTrue(media[index].AsSpan(78, 64).ToArray().All(b => b == 0),
                "Ordinary rumble does not require game PCM and must not invent PCM.");
        }
        Assert.AreEqual(1, fixture.DrainNativeAcknowledgements().Length,
            "A continuous mode on media must not manufacture new native command identities.");
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ActualHelperSdlLedUpdateRetainsModeWithoutRepeatingLedOrTriggerStrobes(bool improved)
    {
        using var fixture = new Fixture();
        byte[] command = Rumble(improved, 76, 43);
        command[1] |= 0x0C;
        command[2] = 0x14;
        command[11] = command[22] = 0x05;
        command[44] = 3;
        command[45] = 71;
        command[46] = 83;
        command[47] = 97;
        fixture.ReceiveNativeCommand(command);
        fixture.QueueSpeakerReports(8);
        fixture.StartIdle();
        WaitMedia(fixture, 8);
        fixture.Stop();
        byte[][] reports = fixture.Native.Reports.ToArray();
        foreach (byte[] report in reports) AssertRumble(report, command, "SDL LED update carrier");
        Assert.AreEqual(1, reports.Count(r => (r[StateOffset(r)] & 0x0C) != 0));
        Assert.AreEqual(1, reports.Count(r => (r[StateOffset(r) + 1] & 0x14) != 0));
        Assert.AreEqual(1, fixture.DrainNativeAcknowledgements().Length);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ActualHelperNativeZeroModeIsAuthoritativeIncludingLedOnlyPacket(bool ledOnly)
    {
        using var fixture = new Fixture();
        StartRumble(fixture, Rumble(true, 76, 43));
        byte[] stop = new byte[48];
        stop[0] = 2;
        if (ledOnly) { stop[2] = 4; stop[45] = 57; }
        fixture.ReceiveNativeCommand(stop);
        Assert.IsTrue(SpinWait.SpinUntil(() => fixture.NativeOwnershipSnapshot().Admissions == 0, 2000));
        fixture.QueueSpeakerReports(8);
        WaitMedia(fixture, 8);
        fixture.Stop();
        foreach (byte[] report in fixture.Native.Reports.Skip(1)) AssertRumble(report, stop, "explicit audio-mode stop");
        Assert.AreEqual(2, fixture.DrainNativeAcknowledgements().Length);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ActualHelperUnownedLocalTriggerOrLedDoesNotChangeAcceptedNativeMode(bool led)
    {
        using var fixture = new Fixture();
        byte[] rumble = Rumble(true, 76, 43);
        StartRumble(fixture, rumble);
        byte[] local = new byte[48];
        local[0] = 2;
        if (led) { local[2] = 4; local[45] = 57; }
        else { local[1] = 4; local[11] = 0x21; local[12] = 0x67; }
        fixture.ReceiveLocalState(local);
        Assert.IsTrue(SpinWait.SpinUntil(() => fixture.Native.Reports.Count >= 2, 2000));
        fixture.QueueSpeakerReports(8);
        WaitMedia(fixture, 8);
        fixture.Stop();
        foreach (byte[] report in fixture.Native.Reports) AssertRumble(report, rumble, "unowned local update");
    }

    [TestMethod]
    public void ActualHelperStaleNativeTemplateCannotOverwriteNewerAcceptedLocalRumble()
    {
        using var fixture = new Fixture();
        byte[] native = Rumble(true, 76, 43), local = Rumble(false, 31, 59);
        StartRumble(fixture, native);
        fixture.ReceiveLocalState(local);
        Assert.IsTrue(SpinWait.SpinUntil(() => fixture.Native.Reports.Count >= 2, 2000));
        fixture.ReceiveTemplateShape(native, 0x57);
        fixture.QueueSpeakerReports(8, audioGain: 0x57);
        WaitMedia(fixture, 8);
        fixture.Stop();
        foreach (byte[] report in fixture.Native.Reports.Skip(1)) AssertRumble(report, local, "accepted local owner");
        Assert.IsTrue(fixture.Native.Reports.Where(r => r[0] == 0x36).All(r => r[50] == 0x57),
            "The physical mode fence does not alter independent audio/haptic gain.");
    }

    [TestMethod]
    public void ActualHelperFutureNativeModeCannotCrossMicrophoneMediaBoundary()
    {
        using var fixture = new Fixture();
        byte[] first = Rumble(true, 76, 43), next = Rumble(false, 31, 59);
        StartRumble(fixture, first);
        fixture.SetCommittedMicrophoneEnabled();
        fixture.ReceiveMicrophoneDisable();
        fixture.ReceiveNativeCommand(next);
        fixture.ReceiveTemplateShape(next, 0x57);
        fixture.QueueSpeakerReports(8);
        WaitMedia(fixture, 8);
        Assert.IsTrue(SpinWait.SpinUntil(() => fixture.NativeOwnershipSnapshot().Admissions == 0, 2000));
        fixture.Stop();
        byte[][] reports = fixture.Native.Reports.ToArray();
        int mic = Array.FindIndex(reports, r => r[0] == 0x32);
        Assert.IsTrue(mic >= 3, "Two physical media frames must precede the microphone transition.");
        foreach (byte[] report in reports.Take(mic)) AssertRumble(report, first, "before mic boundary");
        int nextHead = Array.FindIndex(reports, mic + 1,
            r => r[0] is 0x31 or 0x36 && r[StateOffset(r) + 2] == next[3]);
        Assert.IsTrue(nextHead > mic);
        foreach (byte[] report in reports.Skip(mic + 1).Take(nextHead - mic - 1))
            AssertRumble(report, first, "before exact next head");
        foreach (byte[] report in reports.Skip(nextHead)) AssertRumble(report, next, "after exact next head");
    }

    [TestMethod]
    public void ActualHelperClearDoesNotReviveModeFromStaleTemplate()
    {
        using var fixture = new Fixture();
        byte[] rumble = Rumble(true, 76, 43);
        StartRumble(fixture, rumble);
        fixture.Clear();
        fixture.ReceiveTemplateShape(rumble, 0x57);
        byte[] local = new byte[48];
        local[0] = 2; local[1] = 4; local[11] = 5;
        fixture.ReceiveLocalState(local);
        Assert.IsTrue(SpinWait.SpinUntil(() => fixture.Native.Reports.Count >= 2, 2000));
        fixture.Stop();
        AssertRumble(fixture.Native.Reports.Last(), new byte[48], "cleared lifecycle");
    }

    [TestMethod]
    public void ActualHelperNativeZeroModeRetiresOlderPendingLocalPulse()
    {
        using var fixture = new Fixture();
        fixture.ReceiveLocalState(Rumble(false, 76, 43));
        byte[] stop = new byte[48]; stop[0] = 2;
        fixture.ReceiveNativeCommand(stop);
        fixture.QueueSpeakerReports(8);
        fixture.StartIdle();
        WaitMedia(fixture, 8);
        fixture.Stop();
        foreach (byte[] report in fixture.Native.Reports) AssertRumble(report, stop, "newer native zero-mode stop");
        Assert.AreEqual(1, fixture.DrainNativeAcknowledgements().Length);
    }

    [TestMethod]
    public void ActualHelperNativeStopReturnsToExactFinitePcmWithoutChangingGain()
    {
        using var fixture = new Fixture();
        StartRumble(fixture, Rumble(true, 76, 43));
        byte[] stop = new byte[48]; stop[0] = 2;
        fixture.ReceiveNativeCommand(stop);
        Assert.IsTrue(SpinWait.SpinUntil(() => fixture.NativeOwnershipSnapshot().Admissions == 0, 2000));
        fixture.ReceiveTemplateShape(stop, 0x57);
        var realtime = (DualSenseRealtimeHapticsSharedRing)typeof(Fixture)
            .GetField("realtime", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(fixture);
        for (int index = 1; index <= 3; index++)
            Assert.IsTrue(realtime.Publish(Enumerable.Repeat((byte)(index * 37), 64).ToArray(),
                0, 1, long.MaxValue, Stopwatch.GetTimestamp()));
        fixture.QueueSpeakerReports(8, audioGain: 0x57);
        WaitMedia(fixture, 8);
        fixture.Stop();
        byte[][] media = fixture.Native.Reports.Where(r => r[0] == 0x36).ToArray();
        for (int index = 0; index < media.Length; index++)
        {
            AssertRumble(media[index], stop, "native PCM carrier");
            Assert.AreEqual((byte)0x57, media[index][50], "Unchanged audio/haptic gain.");
            byte expected = index < 3 ? (byte)((index + 1) * 37) : (byte)0;
            Assert.IsTrue(media[index].AsSpan(78, 64).ToArray().All(b => b == expected),
                "Every PCM generation must present once, then silence; mode retention must not modify amplitude.");
            Assert.IsTrue(media[index].AsSpan(144, 200).ToArray().All(b => b == index + 1));
        }
        Assert.AreEqual(3L, realtime.PresentedCount);
    }

    private static void StartRumble(Fixture fixture, byte[] command)
    {
        fixture.ReceiveNativeCommand(command);
        fixture.StartIdle();
        Assert.IsTrue(SpinWait.SpinUntil(() => fixture.NativeOwnershipSnapshot().Admissions == 0, 2000));
        AssertRumble(fixture.Native.Reports.Single(), command, "initial idle acceptance");
    }

    private static void WaitMedia(Fixture fixture, int count) =>
        Assert.IsTrue(SpinWait.SpinUntil(() => fixture.Native.Reports.Count(r => r[0] == 0x36) >= count, 2000));

    private static int StateOffset(byte[] report) => report[0] == 0x36 ? 13 : 3;

    private static byte[] Rumble(bool improved, byte light, byte heavy)
    {
        byte[] command = new byte[48];
        command[0] = 0x02;
        command[1] = improved ? (byte)0x02 : (byte)0x03;
        command[3] = light;
        command[4] = heavy;
        command[39] = improved ? (byte)0x04 : (byte)0;
        return command;
    }

    private static void AssertRumble(byte[] report, byte[] command, string context)
    {
        int offset = StateOffset(report);
        Assert.AreEqual(command[1] & 3, report[offset] & 3, context + ": continuous rumble mode");
        Assert.AreEqual(command[39] & 4, report[offset + 38] & 4, context + ": improved rumble mode");
        Assert.AreEqual(command[3], report[offset + 2], context + ": light motor");
        Assert.AreEqual(command[4], report[offset + 3], context + ": heavy motor");
    }
}
