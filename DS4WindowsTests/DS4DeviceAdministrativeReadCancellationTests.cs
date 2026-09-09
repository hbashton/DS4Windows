using System.Reflection;
using System.Runtime.CompilerServices;
using DS4Windows;
using DS4Windows.InputDevices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public sealed class DS4DeviceAdministrativeReadCancellationTests
{
    [DataTestMethod]
    [DataRow(false, ConnectionType.USB)]
    [DataRow(false, ConnectionType.BT)]
    [DataRow(true, ConnectionType.USB)]
    [DataRow(true, ConnectionType.BT)]
    public void PublicStopArmsCancellationBeforeJoiningThePendingRead(
        bool dualSense, ConnectionType connection)
    {
        using var fixture = new DeviceFixture(dualSense, connection);
        using var entered = new ManualResetEventSlim();
        var backend = new CancellationBackend(fixture.Device, entered, fixture.Release);
        bool expectedCancellation = false;
        HidDevice.ReadStatus completion = HidDevice.ReadStatus.Success;
        int completionError = 0;
        Thread reader = fixture.StartInput(() =>
        {
            using var pipeline = new PipelinedInputReportReader(
                new[] { new byte[64], new byte[64] }, backend);
            completion = pipeline.ReadNext(out _, out completionError, out _, out _);
            expectedCancellation = fixture.Device.IsExpectedAdministrativeInputReadCancellation(
                completion, completionError);
        });
        Assert.IsTrue(entered.Wait(2_000));
        fixture.Device.StopUpdate(); // Real public method; synthetic closed HID means no native I/O.
        Assert.IsTrue(reader.Join(2_000));
        Assert.IsNull(fixture.WorkerFailure);
        Assert.AreEqual(HidDevice.ReadStatus.ReadError, completion);
        Assert.AreEqual(995, completionError);
        Assert.IsTrue(expectedCancellation,
            "The read returned the shutdown cancellation while public Stop was joining it.");
        Assert.AreEqual(1, backend.WaitCalls);
        Assert.IsTrue(backend.Disposed, "The reader's normal disposal must still run.");

        // A cancellation credential is not blanket permission to suppress errors.
        foreach (HidDevice.ReadStatus status in Enum.GetValues<HidDevice.ReadStatus>())
            foreach (int error in new[] { 0, 5, 6, 31, 995, 1167 })
                Assert.AreEqual(status == HidDevice.ReadStatus.ReadError && error == 995,
                    fixture.Device.IsExpectedAdministrativeInputReadCancellation(status, error),
                    $"Unexpected classification after Stop: {status}/{error}.");

        fixture.ResetInputLifetime();
        Assert.IsFalse(fixture.Device.IsExpectedAdministrativeInputReadCancellation(
            HidDevice.ReadStatus.ReadError, 995), "A new input lifetime must not inherit cancellation authority.");
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Unexpected995AndNon995ErrorsRemainFailures(bool dualSense)
    {
        using var fixture = new DeviceFixture(dualSense, ConnectionType.BT);
        foreach (HidDevice.ReadStatus status in Enum.GetValues<HidDevice.ReadStatus>())
            foreach (int error in new[] { 0, 5, 6, 31, 995, 1167 })
                Assert.IsFalse(fixture.Device.IsExpectedAdministrativeInputReadCancellation(status, error));
        fixture.SetExitInput(true); // Exit alone can also originate in the physical-failure path.
        Assert.IsFalse(fixture.Device.IsExpectedAdministrativeInputReadCancellation(
            HidDevice.ReadStatus.ReadError, 995));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PrepareAbortSkipsCancellationOfAnActuallyRunningInputWorker(bool dualSense)
    {
        using var fixture = new DeviceFixture(dualSense, ConnectionType.USB);
        using var entered = new ManualResetEventSlim();
        Thread worker = fixture.StartInput(() =>
        {
            entered.Set();
            fixture.Release.Wait(2_000);
        });
        Assert.IsTrue(entered.Wait(2_000));
        fixture.Device.PrepareAbort();
        fixture.Device.StopUpdate();
        Assert.IsTrue(worker.IsAlive, "PrepareAbort must preserve the existing no-join contract.");
        Assert.IsTrue(fixture.ExitInput);
        Assert.IsFalse(fixture.Device.IsExpectedAdministrativeInputReadCancellation(
            HidDevice.ReadStatus.ReadError, 995));
        fixture.Release.Set();
        Assert.IsTrue(worker.Join(2_000));
    }

    [TestMethod]
    public void TypedBoundedStopDoesNotInventPublicCancellationAuthority()
    {
        using var fixture = new DeviceFixture(false, ConnectionType.USB);
        using var entered = new ManualResetEventSlim();
        fixture.StartInput(() =>
        {
            entered.Set();
            SpinWait.SpinUntil(() => fixture.ExitInput || fixture.Release.IsSet, 2_000);
        });
        Assert.IsTrue(entered.Wait(2_000));
        fixture.MarkInputCommitted();
        DS4DeviceWorkerLifecycleResult result = fixture.Device.TryStopUpdateBoundedCore(2_000);
        Assert.IsTrue(result.Succeeded, result.FailureKind.ToString());
        Assert.IsFalse(fixture.Device.IsExpectedAdministrativeInputReadCancellation(
            HidDevice.ReadStatus.ReadError, 995));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PublicStopCalledByItsInputWorkerDoesNotJoinItself(bool dualSense)
    {
        using var fixture = new DeviceFixture(dualSense, ConnectionType.USB);
        bool returnedWithoutIntervention;
        Thread worker = fixture.StartInput(fixture.Device.StopUpdate);
        returnedWithoutIntervention = worker.Join(1_000);
        // The pre-fix self-join can be interrupted safely on this fixture-owned
        // worker, allowing a failing regression to finish without an orphan.
        if (!returnedWithoutIntervention) worker.Interrupt();
        Assert.IsTrue(worker.Join(2_000));
        Assert.IsTrue(returnedWithoutIntervention, "StopUpdate attempted to join its own input worker.");
        Assert.IsNull(fixture.WorkerFailure);
        Assert.IsTrue(fixture.ExitInput);
        Assert.IsFalse(fixture.Device.IsExpectedAdministrativeInputReadCancellation(
            HidDevice.ReadStatus.ReadError, 995), "The self-stop path must not issue handle-wide cancellation.");
    }

    [TestMethod]
    public void PublicStopCalledByItsOutputWorkerDoesNotInterruptOrJoinItself()
    {
        using var fixture = new DeviceFixture(false, ConnectionType.USB);
        using var messages = new StringWriter();
        TextWriter previous = Console.Out;
        try
        {
            Console.SetOut(TextWriter.Synchronized(messages));
            Thread worker = fixture.StartOutput(fixture.Device.StopUpdate);
            Assert.IsTrue(worker.Join(2_000));
            Assert.IsNull(fixture.WorkerFailure);
            Assert.IsTrue(fixture.Device.ExitOutputThread);
            Assert.AreEqual(string.Empty, messages.ToString(),
                "A self-interrupt/self-join must not be caught and reported as a shutdown exception.");
        }
        finally { Console.SetOut(previous); }
    }

    [DataTestMethod]
    [DataRow(typeof(DS4Device))]
    [DataRow(typeof(DualSenseDevice))]
    public void ProductionStartOverrideCallsSharedLifetimeReset(Type type)
    {
        MethodInfo reset = typeof(DS4Device).GetMethod("ResetAdministrativeInputReadCancellation",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        byte[] body = type.GetMethod(nameof(DS4Device.StartUpdate))!.GetMethodBody()!.GetILAsByteArray()!;
        bool callsReset = Enumerable.Range(0, body.Length - 4).Any(index =>
            body[index] == 0x28 && BitConverter.ToInt32(body, index + 1) == reset.MetadataToken);
        Assert.IsTrue(callsReset, "The production StartUpdate override must reset the shared cancellation credential.");
    }

    private sealed class CancellationBackend : PipelinedInputReportReader.IReadBackend
    {
        private readonly DS4Device device;
        private readonly ManualResetEventSlim entered;
        private readonly ManualResetEventSlim release;
        internal int WaitCalls;
        internal bool Disposed;
        internal CancellationBackend(DS4Device device, ManualResetEventSlim entered, ManualResetEventSlim release)
        { this.device = device; this.entered = entered; this.release = release; }
        public bool TrySubmit(int bufferIndex, out bool completedSynchronously, out int winError)
        { completedSynchronously = false; winError = 0; return true; }
        public HidDevice.ReadStatus WaitForCompletion(uint timeout, out int winError)
        {
            WaitCalls++;
            entered.Set();
            SpinWait.SpinUntil(() => device.IsExpectedAdministrativeInputReadCancellation(
                HidDevice.ReadStatus.ReadError, 995) || release.IsSet, 1_000);
            winError = 995;
            return HidDevice.ReadStatus.ReadError;
        }
        public void CancelAndDrain() { }
        public void Dispose() => Disposed = true;
    }

    private sealed class DeviceFixture : IDisposable
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly List<Thread> workers = new();
        internal readonly ManualResetEventSlim Release = new();
        internal readonly DS4Device Device;
        internal Exception WorkerFailure;
        internal bool ExitInput => (bool)typeof(DS4Device).GetField("exitInputThread", Fields)!.GetValue(Device)!;
        internal DeviceFixture(bool dualSense, ConnectionType connection)
        {
            var hid = (HidDevice)RuntimeHelpers.GetUninitializedObject(typeof(HidDevice));
            // CancelIO acquires this lock, then sees IsOpen=false and does no P/Invoke.
            typeof(HidDevice).GetField("handleLock", Fields)!.SetValue(hid, new object());
            Device = dualSense ? new DualSenseDevice(hid, "Synthetic cancellation regression") :
                new DS4Device(hid, "Synthetic cancellation regression");
            typeof(DS4Device).GetField("conType", Fields)!.SetValue(Device, connection);
        }
        internal void SetExitInput(bool value) =>
            typeof(DS4Device).GetField("exitInputThread", Fields)!.SetValue(Device, value);
        internal void ResetInputLifetime() => typeof(DS4Device)
            .GetMethod("ResetAdministrativeInputReadCancellation", Fields)!.Invoke(Device, null);
        internal void MarkInputCommitted() => typeof(DS4Device)
            .GetMethod("MarkInputWorkerStartCommitted", Fields)!.Invoke(Device, null);
        internal Thread StartInput(Action action) => StartWorker("ds4Input", action);
        internal Thread StartOutput(Action action) => StartWorker("ds4Output", action);
        private Thread StartWorker(string field, Action action)
        {
            var worker = new Thread(() =>
            {
                try { action(); }
                catch (Exception error) { WorkerFailure = error; }
            }) { IsBackground = true, Name = "Bounded administrative cancellation fixture" };
            typeof(DS4Device).GetField(field, Fields)!.SetValue(Device, worker);
            workers.Add(worker);
            worker.Start();
            return worker;
        }
        public void Dispose()
        {
            Release.Set();
            foreach (Thread worker in workers)
            {
                if (!worker.Join(2_000)) worker.Interrupt();
                Assert.IsTrue(worker.Join(2_000), "Fixture worker must not survive cleanup.");
            }
            Device.ReadWaitEv.Dispose();
            Release.Dispose();
        }
    }
}
