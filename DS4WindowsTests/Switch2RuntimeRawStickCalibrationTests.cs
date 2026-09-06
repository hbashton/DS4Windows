using DS4Windows;
using DS4Windows.Switch2;
using Source = DS4WindowsTests.Switch2RawStickCalibrationCollectorTests.Fixture;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2RuntimeRawStickCalibrationTests
{
    [TestMethod]
    public void PreCancelledBeginCannotReserveNeutralOrCapture()
    {
        using var f = new Fixture();
        f.Publish(3700, 3450);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        int reports = f.Reports;
        Assert.IsFalse(f.Runtime.TryBeginRawStickCalibration(Switch2StickSide.Left, false, out var receipt, cancellation.Token));
        Assert.IsNull(receipt);
        Assert.AreEqual(reports, f.Reports);
        Assert.IsTrue(HasDeflection(f.Runtime.getCurrentStateRef()));
    }

    [TestMethod]
    public async Task BeginCancellationRevokesReceiptBeforeBlockedReportReturnsAndAfterReturn()
    {
        using var f = new Fixture();
        f.Publish(2100, 2000);
        using var cancellation = new CancellationTokenSource();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Switch2RawStickCalibrationOperation pending = null;
        int callbacks = 0;
        f.Runtime.Report += (_, _) =>
        {
            if (Interlocked.Increment(ref callbacks) != 1) return;
            entered.Set();
            if (!release.Wait(5000)) throw new TimeoutException();
        };
        var beginning = Task.Run(() => f.Runtime.TryBeginRawStickCalibration(
            Switch2StickSide.Left, false, out pending, cancellation.Token));
        try
        {
            Assert.IsTrue(entered.Wait(1000));
            Assert.IsTrue(f.Runtime.TryGetRawStickCalibrationProgress(pending, out _));
            cancellation.Cancel();
            Assert.IsFalse(f.Runtime.TryGetRawStickCalibrationProgress(pending, out _),
                "The receipt must be revoked before the unrelated Report callback returns.");
            release.Set();
            Assert.IsFalse(await beginning.WaitAsync(TimeSpan.FromSeconds(2)));
            f.Publish(3700, 3450);
            Assert.IsTrue(HasDeflection(f.Runtime.getCurrentStateRef()));

            using var handoff = new CancellationTokenSource();
            Assert.IsTrue(f.Runtime.TryBeginRawStickCalibration(Switch2StickSide.Left, false, out var receipt, handoff.Token));
            handoff.Cancel(); // Runtime Begin returned, but no UI has claimed it.
            f.Publish(3700, 3450);
            Assert.IsTrue(HasDeflection(f.Runtime.getCurrentStateRef()), "The next physical frame must cancel before suppression.");
            Assert.AreEqual(Switch2RawStickCalibrationCommitResult.InvalidOperation,
                await f.Runtime.CompleteRawStickCalibrationAsync(receipt));
            Assert.AreEqual(0, f.Store.Writes);
        }
        finally { release.Set(); await beginning.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DoNotParallelize]
    public async Task QueuedColdMutationRechecksCancelSlotAndProfileBeforeTouchingStorage(int invalidation)
    {
        using var f = new Fixture(loaded: true);
        f.Runtime.DeviceSlotNumber = 0;
        f.Publish(2100, 2000);
        Assert.IsTrue(f.Runtime.TryBeginRawStickCalibration(Switch2StickSide.Left, true, out var operation));
        Task<Switch2RawStickCalibrationCommitResult> saving;
        lock (f.Store.SerializationGate)
        {
            saving = f.Runtime.CompleteRawStickCalibrationAsync(operation);
            Assert.IsTrue(SpinWait.SpinUntil(() =>
                f.Runtime.TryGetRawStickCalibrationProgress(operation, out var progress) && progress.Saving, 1000));
            if (invalidation == 0) Assert.IsTrue(f.Runtime.CancelRawStickCalibration(operation));
            else if (invalidation == 1) f.Runtime.DeviceSlotNumber = 1;
            else Global.BeginProfileSwitchRevision(0);
        }
        Assert.AreEqual(Switch2RawStickCalibrationCommitResult.InvalidOperation,
            await saving.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(0, f.Store.Removes);
        Assert.IsTrue(f.Runtime.HasLocalLeftStickCalibration);
    }

    [TestMethod]
    public async Task SuccessorLoadWaitsForAlreadyEnteredRetiredMutation()
    {
        using var f = new Fixture(loaded: true);
        f.Publish(2100, 2000);
        Assert.IsTrue(f.Runtime.TryBeginRawStickCalibration(Switch2StickSide.Left, true, out var reset));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        f.Store.BeforeMutation = () => { entered.Set(); if (!release.Wait(5000)) throw new TimeoutException(); };
        var saving = f.Runtime.CompleteRawStickCalibrationAsync(reset);
        Task<bool> loading = null;
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(2, 2, Switch2Transport.Usb, out var successor, out _));
        try
        {
            Assert.IsTrue(entered.Wait(1000));
            f.Runtime.StopUpdate();
            loading = Task.Run(() => successor.TryBindRawStickCalibrationPersistence(f.Store, f.Source.Peer));
            Assert.IsFalse(loading.Wait(100), "The new runtime cannot load a pre-mutation snapshot.");
            release.Set();
            Assert.AreEqual(Switch2RawStickCalibrationCommitResult.StoredNotApplied,
                await saving.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.IsTrue(await loading.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.IsFalse(successor.HasLocalLeftStickCalibration);
        }
        finally
        {
            release.Set();
            await saving.WaitAsync(TimeSpan.FromSeconds(5));
            if (loading != null) await loading.WaitAsync(TimeSpan.FromSeconds(5));
            successor.StopUpdate();
        }
    }

    [TestMethod]
    public async Task RetiredSaveCannotUndoSuccessorResetOfSamePhysicalStick()
    {
        var store = new RecordingStore();
        using var retired = new Fixture(store: store);
        using var successor = new Fixture(store: store);
        retired.Publish(2100, 2000);
        successor.Publish(2100, 2000);
        Assert.IsTrue(retired.Runtime.TryBeginRawStickCalibration(Switch2StickSide.Left, false, out var oldCapture));
        retired.RotateAndCenter(oldCapture);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int calls = 0;
        store.BeforeMutation = () =>
        {
            if (Interlocked.Increment(ref calls) != 1) return;
            entered.Set();
            if (!release.Wait(5000)) throw new TimeoutException();
        };
        var oldSave = retired.Runtime.CompleteRawStickCalibrationAsync(oldCapture);
        Task<Switch2RawStickCalibrationCommitResult> newReset = null;
        try
        {
            Assert.IsTrue(entered.Wait(1000));
            retired.Runtime.StopUpdate();
            Assert.IsTrue(successor.Runtime.TryBeginRawStickCalibration(Switch2StickSide.Left, true, out var reset));
            newReset = successor.Runtime.CompleteRawStickCalibrationAsync(reset);
            Assert.IsFalse(newReset.Wait(100), "Successor mutation must serialize with already-entered predecessor I/O.");
            release.Set();
            Assert.AreEqual(Switch2RawStickCalibrationCommitResult.StoredNotApplied,
                await oldSave.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.AreEqual(Switch2RawStickCalibrationCommitResult.AppliedAndStored,
                await newReset.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.IsFalse(store.TryLoad(successor.Source.Peer, Switch2ControllerModel.ProController2,
                Switch2StickSide.Left, out _), "A retired late save cannot resurrect a calibration removed by its successor.");
        }
        finally
        {
            release.Set();
            await oldSave.WaitAsync(TimeSpan.FromSeconds(5));
            if (newReset != null) await newReset.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task CancellingBeginWithStuckMouseOutputRevokesCaptureAndResumesInputBeforeOutputReturns()
    {
        using var moved = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var neutral = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var previous = Global.outputKBMHandler;
        Global.outputKBMHandler = new MouseRecorder(() =>
        {
            moved.Set();
            if (!release.Wait(5000)) throw new TimeoutException();
        });
        using var f = new Fixture();
        Task<bool> beginning = null;
        Switch2RawStickCalibrationOperation pending = null;
        try
        {
            f.Publish(3700, 3450);
            Assert.IsTrue(f.Runtime.TrySetHighRateMouseSource(Switch2ContinuousMouseSource.Gyro, true, 100_000, 0, 0));
            Assert.IsTrue(moved.Wait(1000));
            f.Runtime.Report += (_, _) => neutral.Set();
            beginning = Task.Run(() => f.Runtime.TryBeginRawStickCalibration(
                Switch2StickSide.Left, false, out pending, cancellation.Token));
            Assert.IsTrue(neutral.Wait(1000));
            Assert.IsFalse(beginning.Wait(50), "A successful Begin still requires the existing output fence.");
            Assert.IsTrue(f.Runtime.TryGetRawStickCalibrationProgress(pending, out _));

            cancellation.Cancel();
            Assert.IsFalse(await beginning.WaitAsync(TimeSpan.FromSeconds(1)),
                "A cancelled Begin must not wait for the stuck external output call.");
            Assert.IsFalse(release.IsSet);
            Assert.IsFalse(f.Runtime.TryGetRawStickCalibrationProgress(pending, out _));
            f.Publish(3700, 3450);
            Assert.IsTrue(HasDeflection(f.Runtime.getCurrentStateRef()), "Mapped input resumes before mouse output returns.");
            Assert.IsTrue(f.Runtime.TrySetHighRateMouseSource(Switch2ContinuousMouseSource.Gyro, true, 100_000, 0, 0),
                "Cancellation must release source admission as well as raw input suppression.");
            Assert.AreEqual(Switch2RawStickCalibrationCommitResult.InvalidOperation,
                await f.Runtime.CompleteRawStickCalibrationAsync(pending));
            Assert.AreEqual(0, f.Store.Writes);
        }
        finally
        {
            release.Set();
            if (beginning != null) await beginning.WaitAsync(TimeSpan.FromSeconds(5));
            f.Runtime.StopUpdate();
            Global.outputKBMHandler = previous;
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task CalibrationBeginFencesActiveGyroMouseAndRejectsResurrectionUntilCancel()
    {
        using var moved = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var neutral = new ManualResetEventSlim();
        var previous = Global.outputKBMHandler;
        int count = 0;
        Global.outputKBMHandler = new MouseRecorder(() =>
        {
            Interlocked.Increment(ref count);
            moved.Set();
            if (!release.Wait(5000)) throw new TimeoutException();
        });
        using var f = new Fixture();
        Task<Switch2RawStickCalibrationOperation> beginning = null;
        try
        {
            f.Publish(3700, 3450);
            Assert.IsTrue(f.Runtime.TrySetHighRateMouseSource(Switch2ContinuousMouseSource.Gyro, true, 100_000, 0, 0));
            Assert.IsTrue(moved.Wait(1000));
            f.Runtime.Report += (_, _) => neutral.Set();
            beginning = Task.Run(() =>
            {
                Assert.IsTrue(f.Runtime.TryBeginRawStickCalibration(Switch2StickSide.Left, false, out var operation));
                return operation;
            });
            Assert.IsTrue(neutral.Wait(1000));
            Assert.IsFalse(beginning.Wait(50), "Begin cannot acknowledge release while a prior mouse output is still in flight.");
            release.Set();
            var capture = await beginning.WaitAsync(TimeSpan.FromSeconds(2));
            int atBegin = Volatile.Read(ref count);
            Assert.IsFalse(f.Runtime.TrySetHighRateMouseSource(Switch2ContinuousMouseSource.Gyro, true, 100_000, 0, 0));
            Assert.IsFalse(f.Runtime.TrySetHighRateMappingMouseSources(true, 100_000, 0, true, 100_000, 0, true, 100_000, 0, 0));
            await Task.Delay(50);
            Assert.AreEqual(atBegin, Volatile.Read(ref count));
            Assert.IsTrue(f.Runtime.CancelRawStickCalibration(capture));
            moved.Reset();
            Assert.IsTrue(f.Runtime.TrySetHighRateMouseSource(Switch2ContinuousMouseSource.Gyro, true, 100_000, 0, 0));
            Assert.IsTrue(moved.Wait(1000), "Calibration must not permanently stop the presenter.");
        }
        finally
        {
            release.Set();
            if (beginning != null) await beginning.WaitAsync(TimeSpan.FromSeconds(5));
            f.Runtime.StopUpdate();
            Global.outputKBMHandler = previous;
        }
    }

    [DataTestMethod]
    [DataRow(Switch2ControllerModel.ProController2, true, Switch2StickSide.Left, false)]
    [DataRow(Switch2ControllerModel.ProController2, true, Switch2StickSide.Right, false)]
    [DataRow(Switch2ControllerModel.ProController2, false, Switch2StickSide.Left, false)]
    [DataRow(Switch2ControllerModel.ProController2, false, Switch2StickSide.Right, false)]
    [DataRow(Switch2ControllerModel.JoyCon2Left, false, Switch2StickSide.Left, false)]
    [DataRow(Switch2ControllerModel.JoyCon2Left, false, Switch2StickSide.Left, true)]
    [DataRow(Switch2ControllerModel.JoyCon2Right, false, Switch2StickSide.Right, false)]
    [DataRow(Switch2ControllerModel.JoyCon2Right, false, Switch2StickSide.Right, true)]
    public async Task CaptureNeutralizesThenSavesAndAppliesRawPhysicalSide(
        Switch2ControllerModel model, bool usb, Switch2StickSide side, bool horizontal)
    {
        using var f = new Fixture(model, usb, side, horizontal);
        f.Publish(3700, 3450);
        Assert.IsTrue(HasDeflection(f.Runtime.getCurrentStateRef()));
        int before = f.Reports;
        Assert.IsTrue(f.Runtime.TryBeginRawStickCalibration(side, false, out var operation));
        Assert.AreEqual(before + 1, f.Reports, "Start must publish an immediate mapped release.");
        Assert.IsFalse(HasDeflection(f.Runtime.getCurrentStateRef()));
        Assert.IsFalse(f.Runtime.StartMagnetometerCalibration());
        Assert.AreEqual(Switch2RawStickCalibrationCommitResult.NotReady,
            await f.Runtime.CompleteRawStickCalibrationAsync(operation));
        f.RotateAndCenter(operation);
        Assert.AreEqual(Switch2RawStickCalibrationCommitResult.AppliedAndStored,
            await f.Runtime.CompleteRawStickCalibrationAsync(operation));
        Assert.IsFalse(f.Runtime.TryGetRawStickCalibrationProgress(operation, out _));
        Assert.IsTrue(f.Store.TryLoad(f.Source.Peer, model, side, out var saved));
        Assert.AreEqual((ushort)2100, saved.NeutralX);
        Assert.AreEqual((ushort)2000, saved.NeutralY);
        Assert.AreEqual((ushort)1800, saved.NegativeRangeX);
        Assert.AreEqual((ushort)1600, saved.PositiveRangeX);
        f.Publish(2100, 2000);
        Assert.IsFalse(HasDeflection(f.Runtime.getCurrentStateRef()),
            "Local physical center must map to logical center in every supported orientation.");
        Assert.AreEqual(side == Switch2StickSide.Left, f.Runtime.HasLocalLeftStickCalibration);
        Assert.AreEqual(side == Switch2StickSide.Right, f.Runtime.HasLocalRightStickCalibration);
        Assert.AreEqual(1, f.Store.Writes);
    }

    [TestMethod]
    public async Task ResetReprojectsAlreadyCalibratedQueuedFrameFromOriginalFactoryEvidence()
    {
        using var f = new Fixture(loaded: true);
        var canonical = f.Source.Frame(2100, 2000);
        Assert.IsTrue(Switch2ProProfileInputMapper.TryMap(canonical, out var factory, out _));
        Assert.IsTrue(Switch2RawStickCalibrationBinding.TryLoad(DS4Windows.InputDevices.InputDeviceType.Switch2Pro,
            Switch2Transport.Usb, 1, 1, 0, 0, f.Store, f.Source.Peer, default, out var binding));
        var queued = binding.ApplyPro(factory);
        Assert.IsTrue(queued.HasLocalLeftCalibration);
        Assert.AreEqual((short)0, queued.LeftX.SignedValue);
        Assert.IsTrue(f.Runtime.TryPublishPro(queued));
        Assert.IsTrue(f.Runtime.TryBeginRawStickCalibration(Switch2StickSide.Left, true, out var reset));
        Assert.AreEqual(Switch2RawStickCalibrationCommitResult.AppliedAndStored,
            await f.Runtime.CompleteRawStickCalibrationAsync(reset));
        Assert.IsTrue(f.Runtime.TryPublishPro(queued));
        Assert.AreEqual(factory.LeftX.SignedValue, f.Runtime.getCurrentStateRef().LXAxis.ToSigned16());
        Assert.AreNotEqual((short)0, factory.LeftX.SignedValue);
        Assert.IsFalse(f.Runtime.HasLocalLeftStickCalibration);
        Assert.IsFalse(f.Store.TryLoad(f.Source.Peer, Switch2ControllerModel.ProController2,
            Switch2StickSide.Left, out _));
        Assert.IsTrue(binding.TryWithCalibration(Switch2ControllerModel.ProController2,
            Switch2StickSide.Left, null, out var cleared));
        var restored = cleared.ApplyPro(queued);
        Assert.IsFalse(restored.HasLocalLeftCalibration);
        Assert.AreEqual(factory.LeftCalibrationStatus, restored.LeftCalibrationStatus);
        Assert.AreEqual(factory.RawStickObservation.Calibration, restored.RawStickObservation.Calibration);
        Assert.AreEqual(queued.DeviceCounterRaw, restored.DeviceCounterRaw);
    }

    [TestMethod]
    public async Task JoinedResetRecalibratesUnchangedHalfAndPreservesOtherPhysicalPeer()
    {
        var left = new Source(Switch2ControllerModel.JoyCon2Left, false, Switch2StickSide.Left);
        var right = new Source(Switch2ControllerModel.JoyCon2Right, false, Switch2StickSide.Right, generation: 2);
        var store = new RecordingStore();
        store.Seed(left.Peer, Switch2ControllerModel.JoyCon2Left, Switch2StickSide.Left,
            Switch2RawStickCalibrationBindingTests.Calibration);
        store.Seed(right.Peer, Switch2ControllerModel.JoyCon2Right, Switch2StickSide.Right,
            new Switch2StickCalibration(1800, 1900, 1500, 1400, 1300, 1200));
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreateJoinedJoyCon(3, 7, 1, 1, 2, 2, out var runtime, out _));
        Assert.IsTrue(runtime.TryBindRawStickCalibrationPersistence(store, left.Peer, right.Peer));
        Assert.IsTrue(Switch2JoyConJoinedRuntimeInputSink.TryCreateBound(7, left.Descriptor, right.Descriptor,
            runtime, new Switch2JoyConPairPolicy(1_000_000), 1000, Switch2RuntimeTerminalScheduler.Instance,
            out var sink, out _, out _));
        runtime.Report += (_, _) => { };
        runtime.StartUpdate();
        try
        {
            sink.PublishJoyCon(left.Frame(2100, 2000));
            var heldRight = right.Frame(1800, 1900);
            sink.PublishJoyCon(heldRight);
            Assert.IsFalse(HasDeflection(runtime.getCurrentStateRef()));
            Assert.IsTrue(runtime.TryBeginRawStickCalibration(Switch2StickSide.Right, true, out var reset));
            Assert.AreEqual(Switch2RawStickCalibrationCommitResult.AppliedAndStored,
                await runtime.CompleteRawStickCalibrationAsync(reset));
            sink.PublishJoyCon(left.Frame(2100, 2000));
            var state = runtime.getCurrentStateRef();
            Assert.AreEqual((short)0, state.LXAxis.ToSigned16());
            Assert.AreNotEqual((short)0, state.RXAxis.ToSigned16());
            Assert.IsTrue(runtime.HasLocalLeftStickCalibration);
            Assert.IsFalse(runtime.HasLocalRightStickCalibration);
            Assert.AreEqual(heldRight.DeviceCounterRaw, state.Switch2JoyConRawInputStatus.RightDeviceCounterRaw);
            Assert.AreEqual(1, store.Removes);

            Assert.IsTrue(runtime.TryBeginRawStickCalibration(Switch2StickSide.Right, false, out var capture));
            for (int i = 0; i < 10; i++) sink.PublishJoyCon(left.Frame(2100, 2000));
            Assert.IsTrue(runtime.TryGetRawStickCalibrationProgress(capture, out var progress));
            Assert.AreEqual(0.0, progress.RotationProgress,
                "Repeating the unchanged right half cannot advance right-stick calibration.");
            for (int i = 0; i < 230; i++)
            {
                sink.PublishJoyCon(left.Frame(2100, 2000));
                sink.PublishJoyCon(right.Frame(i % 2 == 0 ? (ushort)300 : (ushort)3700,
                    i % 2 == 0 ? (ushort)450 : (ushort)3450));
                Assert.IsFalse(HasDeflection(runtime.getCurrentStateRef()));
            }
            for (int i = 0; i < 121; i++)
            {
                sink.PublishJoyCon(left.Frame(2100, 2000));
                sink.PublishJoyCon(right.Frame(2100, 2000));
            }
            Assert.IsTrue(runtime.TryGetRawStickCalibrationProgress(capture, out progress));
            Assert.AreEqual(Switch2RawStickCalibrationStage.Ready, progress.Stage);
            Assert.AreEqual(Switch2RawStickCalibrationCommitResult.AppliedAndStored,
                await runtime.CompleteRawStickCalibrationAsync(capture));
            sink.PublishJoyCon(left.Frame(2100, 2000));
            Assert.IsFalse(HasDeflection(runtime.getCurrentStateRef()));
            Assert.IsTrue(store.TryLoad(right.Peer, Switch2ControllerModel.JoyCon2Right,
                Switch2StickSide.Right, out var savedRight));
            Assert.AreEqual((ushort)2100, savedRight.NeutralX);
        }
        finally { runtime.StopUpdate(); }
    }

    [TestMethod]
    public async Task SlowDiskDoesNotHoldInputGateAndConcurrentMutationCannotReorder()
    {
        using var f = new Fixture(loaded: true);
        f.Publish(2100, 2000);
        Assert.IsTrue(f.Runtime.TryBeginRawStickCalibration(Switch2StickSide.Left, true, out var operation));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        f.Store.BeforeMutation = () => { entered.Set(); if (!release.Wait(3000)) throw new TimeoutException(); };
        var saving = f.Runtime.CompleteRawStickCalibrationAsync(operation);
        try
        {
            Assert.IsTrue(entered.Wait(1000));
            await Task.Run(() => f.Publish(3700, 3450)).WaitAsync(TimeSpan.FromSeconds(1));
            Assert.IsFalse(HasDeflection(f.Runtime.getCurrentStateRef()));
            Assert.AreEqual(Switch2RawStickCalibrationCommitResult.Busy,
                await f.Runtime.CompleteRawStickCalibrationAsync(operation).WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.IsFalse(f.Runtime.TryBeginRawStickCalibration(Switch2StickSide.Left, false, out _));
            Assert.IsTrue(f.Runtime.CancelRawStickCalibration(operation));
            Assert.IsFalse(f.Runtime.TryBeginRawStickCalibration(Switch2StickSide.Left, false, out _),
                "Cancellation must not release mutation serialization before the outstanding write ends.");
            release.Set();
            Assert.AreEqual(Switch2RawStickCalibrationCommitResult.StoredNotApplied,
                await saving.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.IsTrue(f.Runtime.HasLocalLeftStickCalibration);
            Assert.AreEqual(1, f.Store.Removes);
        }
        finally { release.Set(); await saving.WaitAsync(TimeSpan.FromSeconds(4)); }
    }

    [TestMethod]
    public async Task FailedStoragePreservesLiveBindingAndAllowsRetry()
    {
        using var f = new Fixture(loaded: true);
        f.Publish(2100, 2000);
        Assert.IsTrue(f.Runtime.TryBeginRawStickCalibration(Switch2StickSide.Left, true, out var operation));
        f.Store.FailMutation = true;
        Assert.AreEqual(Switch2RawStickCalibrationCommitResult.StorageFailed,
            await f.Runtime.CompleteRawStickCalibrationAsync(operation));
        Assert.IsTrue(f.Runtime.HasLocalLeftStickCalibration);
        Assert.IsTrue(f.Runtime.TryGetRawStickCalibrationProgress(operation, out var progress));
        Assert.IsFalse(progress.Saving);
        f.Store.FailMutation = false;
        Assert.AreEqual(Switch2RawStickCalibrationCommitResult.AppliedAndStored,
            await f.Runtime.CompleteRawStickCalibrationAsync(operation));
        Assert.IsFalse(f.Runtime.HasLocalLeftStickCalibration);
    }

    [TestMethod]
    public async Task SuccessfulDiskWriteWaitsForInFlightPublicationBeforeAdoption()
    {
        using var f = new Fixture(loaded: true);
        f.Publish(2100, 2000);
        Assert.IsTrue(f.Runtime.TryBeginRawStickCalibration(Switch2StickSide.Left, true, out var operation));
        using var storeEntered = new ManualResetEventSlim();
        using var releaseStore = new ManualResetEventSlim();
        using var reportEntered = new ManualResetEventSlim();
        using var releaseReport = new ManualResetEventSlim();
        f.Store.BeforeMutation = () =>
        {
            storeEntered.Set();
            if (!releaseStore.Wait(3000)) throw new TimeoutException("Test storage was not released.");
        };
        f.Runtime.Report += (_, args) =>
        {
            if (((Switch2RuntimeReportEventArgs)args).Kind != Switch2RuntimeReportKind.Regular) return;
            reportEntered.Set();
            if (!releaseReport.Wait(3000)) throw new TimeoutException("Test report was not released.");
        };
        var saving = f.Runtime.CompleteRawStickCalibrationAsync(operation);
        Task publication = null;
        try
        {
            Assert.IsTrue(storeEntered.Wait(1000));
            publication = Task.Run(() => f.Publish(3700, 3450));
            Assert.IsTrue(reportEntered.Wait(1000));
            releaseStore.Set();
            Assert.IsFalse(saving.Wait(100), "Adoption must wait for the already-reserved report to finish.");
            Assert.IsTrue(f.Runtime.HasLocalLeftStickCalibration);
            releaseReport.Set();
            await publication.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.AreEqual(Switch2RawStickCalibrationCommitResult.AppliedAndStored,
                await saving.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.IsFalse(f.Runtime.HasLocalLeftStickCalibration);
        }
        finally
        {
            releaseStore.Set(); releaseReport.Set();
            await saving.WaitAsync(TimeSpan.FromSeconds(4));
            if (publication != null) await publication.WaitAsync(TimeSpan.FromSeconds(4));
        }
    }

    [TestMethod]
    public async Task DisconnectDuringStorageCannotApplyItsLateResult()
    {
        using var f = new Fixture(loaded: true);
        f.Publish(2100, 2000);
        Assert.IsTrue(f.Runtime.TryBeginRawStickCalibration(Switch2StickSide.Left, true, out var operation));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        f.Store.BeforeMutation = () => { entered.Set(); if (!release.Wait(3000)) throw new TimeoutException(); };
        var saving = f.Runtime.CompleteRawStickCalibrationAsync(operation);
        try
        {
            Assert.IsTrue(entered.Wait(1000));
            f.Runtime.StopUpdate();
            release.Set();
            Assert.AreEqual(Switch2RawStickCalibrationCommitResult.StoredNotApplied,
                await saving.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.AreEqual(Switch2RuntimeInputDeviceState.Terminal, f.Runtime.RuntimeState);
            Assert.IsTrue(f.Runtime.HasLocalLeftStickCalibration,
                "A late disk result must not replace the retired runtime binding.");
        }
        finally { release.Set(); await saving.WaitAsync(TimeSpan.FromSeconds(4)); }
    }

    [DataTestMethod]
    [DataRow(Switch2ControllerModel.ProController2, true, Switch2StickSide.Left, false)]
    [DataRow(Switch2ControllerModel.JoyCon2Right, false, Switch2StickSide.Right, true)]
    public void WarmDecoderMapperAndLateCalibrationPublicationAllocateNothing(
        Switch2ControllerModel model, bool usb, Switch2StickSide side, bool horizontal)
    {
        using var f = new Fixture(model, usb, side, horizontal, loaded: true);
        for (int i = 0; i < 2_000; i++) f.Publish(2101, 2001);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20_000; i++) f.Publish(2101, 2001);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.AreEqual(0L, allocated);
        Assert.AreEqual(0, f.Store.Writes);
        Assert.AreEqual(0, f.Store.Removes);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ClosedOrReboundRuntimeRejectsStaleOperationBeforeStorage(bool changeSlot)
    {
        using var f = new Fixture(loaded: true);
        f.Publish(2100, 2000);
        Assert.IsTrue(f.Runtime.TryBeginRawStickCalibration(Switch2StickSide.Left, true, out var operation));
        if (changeSlot) f.Runtime.DeviceSlotNumber = f.Runtime.DeviceSlotNumber == 0 ? 1 : 0;
        else f.Runtime.StopUpdate();
        Assert.AreEqual(Switch2RawStickCalibrationCommitResult.InvalidOperation,
            await f.Runtime.CompleteRawStickCalibrationAsync(operation));
        Assert.AreEqual(0, f.Store.Removes);
        Assert.IsFalse(f.Runtime.TryGetRawStickCalibrationProgress(operation, out _));
    }

    private static bool HasDeflection(DS4State state) =>
        state.LXAxis.ToSigned16() != 0 || state.LYAxis.ToSigned16() != 0 ||
        state.RXAxis.ToSigned16() != 0 || state.RYAxis.ToSigned16() != 0;

    internal sealed class Fixture : IDisposable
    {
        internal readonly Source Source;
        internal readonly RecordingStore Store;
        internal readonly Switch2RuntimeInputDevice Runtime;
        private readonly Switch2BluetoothRuntimeInputSink sink;
        internal int Reports;

        internal Fixture(Switch2ControllerModel model = Switch2ControllerModel.ProController2,
            bool usb = true, Switch2StickSide side = Switch2StickSide.Left,
            bool horizontal = false, bool loaded = false, RecordingStore store = null)
        {
            Store = store ?? new RecordingStore();
            Source = new Source(model, usb, side);
            if (loaded) Store.Seed(Source.Peer, model, side, Switch2RawStickCalibrationBindingTests.Calibration);
            bool created = model == Switch2ControllerModel.ProController2 ?
                Switch2RuntimeInputDevice.TryCreatePro(1, 1, Source.Descriptor.Identity.Transport, out Runtime, out _) :
                Switch2RuntimeInputDevice.TryCreateStandaloneJoyCon(model, 1, 1, out Runtime, out _);
            Assert.IsTrue(created);
            bool rightOnly = model == Switch2ControllerModel.JoyCon2Right;
            Assert.IsTrue(Runtime.TryBindRawStickCalibrationPersistence(Store,
                rightOnly ? default : Source.Peer, rightOnly ? Source.Peer : default));
            if (!usb)
                Assert.IsTrue(Switch2BluetoothRuntimeInputSink.TryCreateBound(Source.Descriptor,
                    Runtime, 1000, Switch2RuntimeTerminalScheduler.Instance, out sink, out _, out _));
            if (model != Switch2ControllerModel.ProController2)
                Assert.IsTrue(Runtime.TrySetStandaloneJoyConHoldMode(horizontal ?
                    Switch2JoyConHoldMode.Horizontal : Switch2JoyConHoldMode.Vertical, out _));
            Runtime.Report += (_, _) => Reports++;
            Runtime.StartUpdate();
        }

        internal void Publish(ushort x, ushort y)
        {
            var frame = Source.Frame(x, y);
            if (Source.Descriptor.Identity.Transport == Switch2Transport.Usb)
            {
                Assert.IsTrue(Switch2ProProfileInputMapper.TryMap(frame, out var profile, out _));
                Assert.IsTrue(Runtime.TryPublishPro(profile));
            }
            else if (Source.Descriptor.Identity.Model == Switch2ControllerModel.ProController2) sink.PublishPro(frame);
            else sink.PublishJoyCon(frame);
        }

        internal void RotateAndCenter(Switch2RawStickCalibrationOperation operation)
        {
            for (int i = 0; i < 230; i++)
            {
                Publish(i % 2 == 0 ? (ushort)300 : (ushort)3700,
                    i % 2 == 0 ? (ushort)450 : (ushort)3450);
                Assert.IsFalse(HasDeflection(Runtime.getCurrentStateRef()));
            }
            for (int i = 0; i < 121; i++) Publish(2100, 2000);
            Assert.IsTrue(Runtime.TryGetRawStickCalibrationProgress(operation, out var progress));
            Assert.AreEqual(Switch2RawStickCalibrationStage.Ready, progress.Stage);
        }

        public void Dispose() => Runtime.StopUpdate();
    }

    internal sealed class RecordingStore : ISwitch2RawStickCalibrationStore
    {
        public object SerializationGate { get; } = new();
        private readonly Dictionary<(Switch2PersistentPeerId, Switch2ControllerModel, Switch2StickSide), Switch2StickCalibration> values = new();
        private readonly object gate = new();
        internal Action BeforeMutation;
        internal bool FailMutation;
        internal int Writes, Removes;
        internal void Seed(Switch2PersistentPeerId peer, Switch2ControllerModel model,
            Switch2StickSide side, Switch2StickCalibration value)
        { lock (gate) values[(peer, model, side)] = value; }
        public bool TryLoad(Switch2PersistentPeerId peer, Switch2ControllerModel model,
            Switch2StickSide side, out Switch2StickCalibration value)
        { lock (gate) return values.TryGetValue((peer, model, side), out value); }
        public bool TryStore(Switch2PersistentPeerId peer, Switch2ControllerModel model,
            Switch2StickSide side, in Switch2StickCalibration value)
        {
            BeforeMutation?.Invoke();
            if (FailMutation) return false;
            lock (gate) { Writes++; values[(peer, model, side)] = value; return true; }
        }
        public bool TryRemove(Switch2PersistentPeerId peer, Switch2ControllerModel model, Switch2StickSide side)
        {
            BeforeMutation?.Invoke();
            if (FailMutation) return false;
            lock (gate) { Removes++; values.Remove((peer, model, side)); return true; }
        }
    }

    private sealed class MouseRecorder(Action move) : DS4Windows.DS4Control.VirtualKBMBase
    {
        public override bool Connect() => true;
        public override bool Disconnect() => true;
        public override void MoveRelativeMouse(int x, int y) => move();
        public override void MoveAbsoluteMouse(double x, double y) { }
        public override void PerformMouseWheelEvent(int vertical, int horizontal) { }
        public override void PerformMouseButtonEvent(uint mouseButton) { }
        public override void PerformMouseButtonPress(uint mouseButton) { }
        public override void PerformMouseButtonRelease(uint mouseButton) { }
        public override void PerformKeyPress(uint key) { }
        public override void PerformKeyPressAlt(uint key) { }
        public override void PerformKeyRelease(uint key) { }
        public override void PerformKeyReleaseAlt(uint key) { }
        public override string GetDisplayName() => "No system input";
        public override string GetIdentifier() => "test";
        public override string GetFullDisplayName() => GetDisplayName();
    }
}
