using System.Reflection;
using DS4Windows;
using DS4Windows.InputDevices;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public class ControllerReadingsSnapshotTests
{
    [TestMethod]
    public void LegacyReadingsSkipMissingReadWindowAndRecoverWithoutWaiting()
    {
        var device = new LegacyTestDevice();
        var raw = new DS4State { LX = 21, L2 = 153 };
        var mapped = new DS4State { LX = 42, L2 = 255 };
        var rawCopy = new DS4StateOwnedSnapshot();
        var mappedCopy = new DS4StateOwnedSnapshot();
        Assert.IsFalse(device.TryCopyControllerReadings(raw, mapped, rawCopy, mappedCopy));
        device.ReadWaitEv.Set();
        Assert.IsTrue(device.TryCopyControllerReadings(raw, mapped, rawCopy, mappedCopy));
        Assert.AreEqual((byte)21, rawCopy.State.LX);
        Assert.AreEqual((byte)42, mappedCopy.State.LX);
        Assert.AreEqual((byte)153, rawCopy.State.L2);
        Assert.AreEqual((byte)255, mappedCopy.State.L2);
        Assert.IsTrue(device.ReadWaitEv.IsSet);
        device.IsRemoving = true;
        Assert.IsFalse(device.TryCopyControllerReadings(raw, mapped, rawCopy, mappedCopy));
        device.ReadWaitEv.Dispose();
    }

    [TestMethod]
    public void LegacyReadWindowIsReleasedEvenWhenCaptureThrows()
    {
        var device = new LegacyTestDevice();
        device.ReadWaitEv.Set();
        Assert.ThrowsException<ArgumentNullException>(() => device.TryCopyControllerReadings(
            null, new DS4State(), new DS4StateOwnedSnapshot(), new DS4StateOwnedSnapshot()));
        Assert.IsTrue(device.ReadWaitEv.IsSet);
        device.ReadWaitEv.Dispose();
    }

    [DataTestMethod]
    [DataRow(Switch2Transport.Usb)]
    [DataRow(Switch2Transport.BluetoothLe)]
    public void ProReadingsCapturePublishedAxesWithoutLegacyReadWindow(Switch2Transport transport)
    {
        var device = CreatePro(transport);
        var raw = new DS4State();
        var mapped = new DS4State();
        var rawCopy = new DS4StateOwnedSnapshot();
        var mappedCopy = new DS4StateOwnedSnapshot();
        Assert.IsFalse(device.TryCopyControllerReadings(raw, mapped, rawCopy, mappedCopy));
        device.StartUpdate();
        device.Report += (sender, _) =>
        {
            // Same ownership as the service's canonical pipeline; no UI or
            // real service, controller, virtual pad, or mouse IO is involved.
            sender.getRawCurrentState(raw);
            raw.CopyTo(mapped);
            mapped.LX = 100;
            mapped.L2 = 17;
        };
        var frame = Switch2RuntimeInputDeviceTests.CreateProFrame(98_001, 98_002,
            (uint)Switch2ProButton.LeftTrigger, leftX: 4095, rightY: 0,
            gyroscope: new Switch2Vector3Raw(123, 456, 789),
            accelerometer: new Switch2Vector3Raw(111, 222, 333),
            bluetoothLe: transport == Switch2Transport.BluetoothLe);
        Assert.IsTrue(device.TryPublishPro(frame));
        Assert.IsFalse(device.ReadWaitEv.IsSet,
            "The old UI waited forever on this unused HID signal.");
        Assert.IsTrue(device.TryCopyControllerReadings(raw, mapped, rawCopy, mappedCopy));
        Assert.AreEqual(raw.LX, rawCopy.State.LX);
        Assert.AreEqual((byte)255, rawCopy.State.LX);
        Assert.AreEqual(raw.RY, rawCopy.State.RY);
        Assert.AreEqual((byte)255, rawCopy.State.L2);
        Assert.AreEqual((byte)100, mappedCopy.State.LX);
        Assert.AreEqual((byte)17, mappedCopy.State.L2);
        Assert.AreEqual(raw.Motion.gyroYawFull, rawCopy.State.Motion.gyroYawFull);
        Assert.AreNotEqual(0, rawCopy.State.Motion.gyroYawFull);
        Assert.AreNotSame(raw.Motion, rawCopy.State.Motion);
        Assert.AreNotSame(mapped.Motion, mappedCopy.State.Motion);
        Assert.AreNotSame(rawCopy.State.Motion, mappedCopy.State.Motion);
        int yaw = rawCopy.State.Motion.gyroYawFull;
        raw.Motion.gyroYawFull = 12;
        mappedCopy.State.Motion.outputAccelX = 777;
        Assert.AreEqual(yaw, rawCopy.State.Motion.gyroYawFull);
        Assert.AreNotEqual(777, mapped.Motion.outputAccelX,
            "Editing dead-zone preview must not mutate report-owned motion.");
        Assert.IsTrue(device.TryPublishTerminalNeutral());
        Assert.IsFalse(device.TryCopyControllerReadings(raw, mapped, rawCopy, mappedCopy));
    }

    [TestMethod]
    public void BusyPublicationSkipsUiTickAndResumesAfterward()
    {
        var device = CreatePro();
        device.StartUpdate();
        var raw = new DS4State();
        var mapped = new DS4State();
        var rawCopy = new DS4StateOwnedSnapshot();
        var mappedCopy = new DS4StateOwnedSnapshot();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        device.Report += (_, _) =>
        {
            // Even the report owner cannot borrow partially mapped readings.
            Assert.IsFalse(device.TryCopyControllerReadings(raw, mapped, rawCopy, mappedCopy));
            entered.Set();
            Assert.IsTrue(release.Wait(3000));
        };
        var publication = Task.Run(() => device.TryPublishPro(
            Switch2RuntimeInputDeviceTests.CreateProFrame(98_001, 98_002, 0)));
        try
        {
            Assert.IsTrue(entered.Wait(1000));
            Assert.IsFalse(device.TryCopyControllerReadings(raw, mapped, rawCopy, mappedCopy));
            Assert.IsFalse(release.IsSet, "Readings must not resume the report or queue work.");
        }
        finally
        {
            release.Set();
            Assert.IsTrue(publication.Wait(2000));
        }
        Assert.IsTrue(publication.Result);
        Assert.IsTrue(device.TryCopyControllerReadings(raw, mapped, rawCopy, mappedCopy));
    }

    [TestMethod]
    public void ContendedRuntimeGateSkipsUiTick()
    {
        var device = CreatePro();
        device.StartUpdate();
        object gate = typeof(Switch2RuntimeInputDevice).GetField("publicationGate",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(device);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task owner = Task.Run(() =>
        {
            lock (gate)
            {
                entered.Set();
                Assert.IsTrue(release.Wait(3000));
            }
        });
        try
        {
            Assert.IsTrue(entered.Wait(1000));
            Assert.IsFalse(device.TryCopyControllerReadings(new DS4State(), new DS4State(),
                new DS4StateOwnedSnapshot(), new DS4StateOwnedSnapshot()));
        }
        finally
        {
            release.Set();
            Assert.IsTrue(owner.Wait(2000));
        }
    }

    [TestMethod]
    public void RepeatedReadingsHaveNoSteadyStateAllocations()
    {
        var device = CreatePro();
        device.StartUpdate();
        var raw = new DS4State();
        var mapped = new DS4State();
        var rawCopy = new DS4StateOwnedSnapshot();
        var mappedCopy = new DS4StateOwnedSnapshot();
        for (int i = 0; i < 100; i++)
            device.TryCopyControllerReadings(raw, mapped, rawCopy, mappedCopy);
        long before = GC.GetAllocatedBytesForCurrentThread();
        int copied = 0;
        for (int i = 0; i < 10_000; i++)
            if (device.TryCopyControllerReadings(raw, mapped, rawCopy, mappedCopy)) copied++;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.AreEqual(10_000, copied);
        Assert.AreEqual(0L, allocated);
    }

    [TestMethod]
    public void ReadingsUiUsesOwnedNonblockingCopyAndRearmsTimer()
    {
        string source = File.ReadAllText(FindSource("DS4Forms/ControllerReadingsControl.xaml.cs"));
        StringAssert.Contains(source, "ds.TryCopyControllerReadings(");
        Assert.IsFalse(source.Contains("ds.ReadWaitEv"));
        StringAssert.Contains(source, "private readonly DS4StateOwnedSnapshot baseSnapshot");
        StringAssert.Contains(source, "private readonly DS4StateOwnedSnapshot interSnapshot");
        StringAssert.Contains(source.Replace("\r\n", "\n"), "finally\n");
        StringAssert.Contains(source, "Interlocked.CompareExchange(ref readingInProgress");
        StringAssert.Contains(source, "!ReferenceEquals(ds, Program.rootHub.DS4Controllers[inputIndex])");
    }

    private static Switch2RuntimeInputDevice CreatePro(Switch2Transport transport = Switch2Transport.Usb)
    {
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(98_001, 98_002, transport,
            out var device, out var failure), failure.ToString());
        return device;
    }

    private static string FindSource(string relative)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null;
             directory = directory.Parent)
        {
            string path = Path.Combine(directory.FullName, "DS4Windows", relative);
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException(relative);
    }

    private sealed class LegacyTestDevice : DS4Device
    {
        internal LegacyTestDevice() : base("Readings test", InputDeviceType.DS4, ConnectionType.USB) { }
    }
}
