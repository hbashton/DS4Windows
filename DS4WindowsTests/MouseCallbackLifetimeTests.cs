using System.Reflection;
using System.Runtime.CompilerServices;
using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public sealed class MouseCallbackLifetimeTests
{
    private static readonly string[] TouchEvents =
    {
        "TouchButtonDown", "TouchButtonUp", "TouchesBegan", "TouchesMoved",
        "TouchesEnded", "TouchUnchanged", "PreTouchProcess"
    };

    [TestMethod]
    public void DirectPublicationRequiresActivationAndExactSenderWithoutRawSubscriptions()
    {
        using var fixture = new Fixture();
        var owner = new ControlServiceMouseCallbackSubscription(fixture.Mouse, fixture.Source, 0);
        Assert.IsFalse(owner.IsAcceptingCallbacks);
        Assert.IsFalse(owner.TryInvokeProjectedMotion(fixture.Source.SixAxis, fixture.Args));
        owner.ActivateDirectPublication();
        Assert.IsTrue(owner.IsAcceptingCallbacks);
        Assert.AreEqual(0, TouchEvents.Sum(name => Handlers(fixture.Source.Touchpad, name).Length));
        Assert.AreEqual(0, Handlers(fixture.Source.SixAxis, "SixAccelMoved").Length);
        fixture.Source.SixAxis.FireSixAxisEvent(fixture.Args);
        Assert.AreEqual(0, fixture.Mouse.Calls);
        Assert.IsFalse(owner.TryInvokeProjectedMotion(new DS4SixAxis(), fixture.Args));
        Assert.IsTrue(owner.TryInvokeProjectedMotion(fixture.Source.SixAxis, fixture.Args));
        Assert.AreEqual(1, fixture.Mouse.Calls);
        Assert.ThrowsException<InvalidOperationException>(() => owner.Subscribe());
        Assert.ThrowsException<InvalidOperationException>(() => owner.ActivateDirectPublication());
        Assert.IsTrue(owner.TryRetire(0));
        Assert.IsFalse(owner.IsAcceptingCallbacks);
        Assert.IsFalse(owner.TryInvokeProjectedMotion(fixture.Source.SixAxis, fixture.Args));
        Assert.AreEqual(1, fixture.Mouse.Calls);
    }

    [TestMethod]
    public void DirectInvocationDrainsAfterExceptionAndReentrantRetirement()
    {
        using var fixture = new Fixture();
        var owner = new ControlServiceMouseCallbackSubscription(fixture.Mouse, fixture.Source, 0);
        owner.ActivateDirectPublication();
        fixture.Mouse.Action = () => throw new InvalidOperationException("test");
        Assert.ThrowsException<InvalidOperationException>(() =>
            owner.TryInvokeProjectedMotion(fixture.Source.SixAxis, fixture.Args));
        Assert.IsFalse(ControlServiceMouseCallbackSubscription.IsInsideCallback);
        fixture.Mouse.Action = () =>
        {
            Assert.IsTrue(ControlServiceMouseCallbackSubscription.IsInsideCallback);
            Assert.IsFalse(owner.TryRetire(0));
        };
        Assert.IsTrue(owner.TryInvokeProjectedMotion(fixture.Source.SixAxis, fixture.Args));
        Assert.IsTrue(owner.TryRetire(0));
        Assert.IsFalse(ControlServiceMouseCallbackSubscription.IsInsideCallback);
    }

    [TestMethod]
    public void ClosedPartialSubscriptionCannotSatisfyRegistryIdempotentSuccess()
    {
        using var fixture = new Fixture();
        var registry = new ControlServiceMouseCallbackRegistry();
        Assert.IsTrue(registry.TryReplace(0, fixture.Mouse, fixture.Source, 1000));
        var old = (ControlServiceMouseCallbackSubscription)
            Handlers(fixture.Source.SixAxis, "SixAccelMoved")[0].Target;
        // Cold fault state: attachment was attempted, but final admission never
        // opened. Avoid relying on allocation failure in ordinary event adds.
        typeof(ControlServiceMouseCallbackSubscription).GetField("admission",
            BindingFlags.Instance | BindingFlags.NonPublic).SetValue(old, int.MinValue);
        Assert.IsFalse(old.IsRetired);
        Assert.IsFalse(old.IsAcceptingCallbacks);
        Assert.IsTrue(registry.TryReplace(0, fixture.Mouse, fixture.Source, 1000));
        var successor = (ControlServiceMouseCallbackSubscription)
            Handlers(fixture.Source.SixAxis, "SixAccelMoved")[0].Target;
        Assert.AreNotSame(old, successor);
        Assert.IsTrue(old.IsRetired);
        Assert.IsTrue(successor.IsAcceptingCallbacks);
        fixture.Source.SixAxis.FireSixAxisEvent(fixture.Args);
        Assert.AreEqual(1, fixture.Mouse.Calls);
        Assert.IsTrue(registry.TryRetireSource(fixture.Source, 1000));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NeutralPreparationClearsTransientGyroAndPreservesRequiredReleaseEdges(bool terminal)
    {
        using var fixture = new Fixture();
        Mouse mouse = fixture.Mouse;
        FieldInfo toggle = typeof(Mouse).GetField("currentToggleGyroStick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        toggle.SetValue(mouse, true);
        mouse.gyroSwipe.swipeLeft = mouse.gyroSwipe.swipeUp = true;
        mouse.gyroSwipe.previousSwipeRight = true;
        mouse.gyroSwipe.xActive = mouse.gyroSwipe.yActive = true;
        var data = Mapping.mapStickActionData[0];
        long epoch = data.CaptureEpoch();
        Assert.IsTrue(data.TrySubmit(epoch, GyroMouseStickInfo.OutputStick.RightStick,
            true, true, 255, 0, true));

        mouse.PrepareGyroNeutralReport(terminal);

        Assert.IsFalse(data.dirty);
        Assert.IsFalse(data.TrySubmit(epoch, GyroMouseStickInfo.OutputStick.RightStick,
            true, true, 255, 0, true));
        Assert.AreEqual(!terminal, (bool)toggle.GetValue(mouse),
            "A no-motion report must not turn off the user's toggle latch.");
        Assert.IsFalse(mouse.GyroMouseOutputActive);
        Assert.IsFalse(mouse.GyroMouseJoystickOutputActive);
        Assert.IsFalse(mouse.gyroSwipe.swipeLeft || mouse.gyroSwipe.swipeRight ||
            mouse.gyroSwipe.swipeUp || mouse.gyroSwipe.swipeDown);
        Assert.IsFalse(mouse.gyroSwipe.xActive || mouse.gyroSwipe.yActive);
        Assert.IsTrue(mouse.gyroSwipe.previousSwipeLeft && mouse.gyroSwipe.previousSwipeUp);
        Assert.AreEqual(terminal, mouse.gyroSwipe.previousSwipeRight);
        mouse.PrepareGyroNeutralReport(terminal);
        Assert.AreEqual(terminal, mouse.gyroSwipe.previousSwipeLeft,
            "Terminal retry keeps the release; ordinary no-motion consumes it once.");
    }

    [TestMethod]
    public void RetirementRemovesEveryExactHandlerAndRejectsCopiedDelegates()
    {
        using var fixture = new Fixture();
        var owner = new ControlServiceMouseCallbackSubscription(fixture.Mouse, fixture.Source, 0);
        int foreignCalls = 0;
        SixAxisHandler<SixAxisEventArgs> foreign = (_, _) => foreignCalls++;
        fixture.Source.SixAxis.SixAccelMoved += foreign;
        owner.Subscribe();
        Assert.AreEqual(9, TouchEvents.Sum(name => Handlers(fixture.Source.Touchpad, name).Length));
        var copied = CaptureAll(fixture.Source, owner);
        Assert.AreEqual(10, copied.Count);
        Assert.IsTrue(owner.TryRetire(1000));
        Assert.IsTrue(owner.IsRetired);
        foreach (string name in TouchEvents) Assert.AreEqual(0, Handlers(fixture.Source.Touchpad, name).Length);
        Assert.AreEqual(1, Handlers(fixture.Source.SixAxis, "SixAccelMoved").Length);
        foreach (var (callback, sender, args) in copied) callback.DynamicInvoke(sender, args);
        Assert.AreEqual(0, fixture.Mouse.Calls);
        fixture.Source.SixAxis.FireSixAxisEvent(fixture.Args);
        Assert.AreEqual(1, foreignCalls);
        fixture.Source.SixAxis.SixAccelMoved -= foreign;
    }

    [TestMethod]
    public void ExactSenderAndLogicalMouseSlotAreValidated()
    {
        using var fixture = new Fixture();
        Assert.ThrowsException<ArgumentException>(() =>
            new ControlServiceMouseCallbackSubscription(fixture.Mouse, fixture.Source, 1));
        var owner = new ControlServiceMouseCallbackSubscription(fixture.Mouse, fixture.Source, 0);
        owner.Subscribe();
        var callback = Six(fixture.Source, owner);
        callback(new DS4SixAxis(), fixture.Args);
        Assert.AreEqual(0, fixture.Mouse.Calls);
        callback(fixture.Source.SixAxis, fixture.Args);
        Assert.AreEqual(1, fixture.Mouse.Calls);
        Assert.IsTrue(owner.TryRetire(1000));
    }

    [TestMethod]
    public void FailedDrainKeepsTombstoneAndCannotInstallSuccessorUntilOldCallbackReturns()
    {
        using var fixture = new Fixture();
        var successor = new CountingMouse(0, fixture.Source);
        var registry = new ControlServiceMouseCallbackRegistry();
        Assert.IsTrue(registry.TryReplace(0, fixture.Mouse, fixture.Source, 1000));
        var copied = (SixAxisHandler<SixAxisEventArgs>)Handlers(fixture.Source.SixAxis, "SixAccelMoved")[0];
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        fixture.Mouse.Action = () => { entered.Set(); if (!release.Wait(5000)) throw new TimeoutException(); };
        var active = Task.Run(() => copied(fixture.Source.SixAxis, fixture.Args));
        try
        {
            Assert.IsTrue(entered.Wait(5000));
            Assert.IsFalse(registry.TryRetireSource(fixture.Source, 0));
            Assert.IsFalse(registry.TryReplace(0, successor, fixture.Source, 0));
            Assert.AreEqual(0, Handlers(fixture.Source.SixAxis, "SixAccelMoved").Length);
            copied(fixture.Source.SixAxis, fixture.Args);
            Assert.AreEqual(1, fixture.Mouse.Calls);
            Assert.AreEqual(0, successor.Calls);
        }
        finally { release.Set(); }
        Assert.IsTrue(active.Wait(5000));
        Assert.IsTrue(registry.TryReplace(0, successor, fixture.Source, 1000));
        copied(fixture.Source.SixAxis, fixture.Args);
        fixture.Source.SixAxis.FireSixAxisEvent(fixture.Args);
        Assert.AreEqual(1, fixture.Mouse.Calls);
        Assert.AreEqual(1, successor.Calls);
        Assert.IsTrue(registry.TryRetireSource(fixture.Source, 1000));
    }

    [TestMethod]
    public void StaleSourceAndOldMouseRetirementCannotTouchReusedSlotSuccessor()
    {
        using var fixture = new Fixture();
        DS4Device successorSource = CreateDevice(1);
        var successor = new CountingMouse(0, successorSource);
        var registry = new ControlServiceMouseCallbackRegistry();
        Assert.IsTrue(registry.TryReplace(0, fixture.Mouse, fixture.Source, 1000));
        var oldOwner = (ControlServiceMouseCallbackSubscription)
            Handlers(fixture.Source.SixAxis, "SixAccelMoved")[0].Target;
        Assert.IsTrue(registry.TryReplace(0, successor, successorSource, 1000));
        var newOwner = (ControlServiceMouseCallbackSubscription)
            Handlers(successorSource.SixAxis, "SixAccelMoved")[0].Target;
        Assert.IsTrue(newOwner.Generation > oldOwner.Generation);
        Assert.IsTrue(registry.TryRetireSource(fixture.Source, 1000));
        Assert.IsTrue(registry.TryRetireMouse(0, fixture.Mouse, 1000));
        successorSource.SixAxis.FireSixAxisEvent(fixture.Args);
        Assert.AreEqual(1, successor.Calls);
        Assert.IsFalse(newOwner.IsRetired);
        Assert.IsTrue(registry.TryRetireSource(successorSource, 1000));
    }

    [TestMethod]
    public void PrimaryRemovalRetiresSurvivingSecondarySourceBoundToItsMouse()
    {
        using var fixture = new Fixture();
        DS4Device secondary = CreateDevice(1);
        var registry = new ControlServiceMouseCallbackRegistry();
        Assert.IsTrue(registry.TryReplace(0, fixture.Mouse, secondary, 1000));
        var copied = (SixAxisHandler<SixAxisEventArgs>)Handlers(secondary.SixAxis, "SixAccelMoved")[0];
        Assert.IsTrue(registry.TryRetireSource(fixture.Source, 1000));
        copied(secondary.SixAxis, fixture.Args);
        Assert.AreEqual(0, fixture.Mouse.Calls);
        Assert.AreEqual(0, Handlers(secondary.SixAxis, "SixAccelMoved").Length);
    }

    [TestMethod]
    public void DrainedSecondaryRetiresPrimaryAccumulatorBeforeWeakerSuccessor()
    {
        using var fixture = new Fixture();
        DS4Device oldSecondary = CreateDevice(1), successorSecondary = CreateDevice(1);
        var registry = new ControlServiceMouseCallbackRegistry();
        var data = Mapping.mapStickActionData[0];
        Assert.IsTrue(registry.TryReplace(0, fixture.Mouse, oldSecondary, 1000));
        long oldEpoch = data.CaptureEpoch();
        data.TrySubmit(oldEpoch, GyroMouseStickInfo.OutputStick.RightStick,
            true, true, 255, 0, true);
        Assert.IsTrue(registry.TryRetireSource(oldSecondary, 1000));
        Assert.IsFalse(data.dirty);
        Assert.IsFalse(data.TrySubmit(oldEpoch, GyroMouseStickInfo.OutputStick.RightStick,
            true, true, 255, 0, true));
        var neutral = new DS4State();
        data.TryApplyCurrentGyro(data.CaptureEpoch(), neutral,
            GyroMouseStickInfo.OutputStick.RightStick, true, true);
        Assert.AreEqual((byte)128, neutral.RX);
        Assert.AreEqual((byte)128, neutral.RY);
        Assert.IsTrue(registry.TryReplace(0, fixture.Mouse, successorSecondary, 1000));
        long successorEpoch = data.CaptureEpoch();
        data.TrySubmit(successorEpoch, GyroMouseStickInfo.OutputStick.RightStick,
            true, true, 150, 100, true);
        Assert.IsTrue(registry.TryRetireSource(oldSecondary, 1000));
        Assert.AreEqual(successorEpoch, data.CaptureEpoch(), "Stale cleanup must not reset the successor.");
        var state = new DS4State();
        data.ApplyTo(state);
        Assert.AreEqual((byte)150, state.RX);
        Assert.AreEqual((byte)100, state.RY);
        Assert.IsTrue(registry.TryRetireSource(successorSecondary, 1000));
    }

    [TestMethod]
    public void CopiedPreTouchHandlerCannotMutateOldOrSuccessorMouseAfterReplacement()
    {
        using var fixture = new Fixture();
        var successor = new CountingMouse(0, fixture.Source);
        var registry = new ControlServiceMouseCallbackRegistry();
        Assert.IsTrue(registry.TryReplace(0, fixture.Mouse, fixture.Source, 1000));
        var stale = (DS4Touchpad.TouchHandler<EventArgs>)
            Handlers(fixture.Source.Touchpad, "PreTouchProcess")[0];
        Assert.IsTrue(registry.TryReplace(0, successor, fixture.Source, 1000));
        fixture.Mouse.leftDown = successor.leftDown = true;
        fixture.Mouse.priorLeftDown = successor.priorLeftDown = false;
        stale(fixture.Source.Touchpad, EventArgs.Empty);
        Assert.IsFalse(fixture.Mouse.priorLeftDown);
        Assert.IsFalse(successor.priorLeftDown);
        var active = (DS4Touchpad.TouchHandler<EventArgs>)
            Handlers(fixture.Source.Touchpad, "PreTouchProcess")[0];
        active(fixture.Source.Touchpad, EventArgs.Empty);
        Assert.IsFalse(fixture.Mouse.priorLeftDown);
        Assert.IsTrue(successor.priorLeftDown);
        Assert.IsTrue(registry.TryRetireSource(fixture.Source, 1000));
    }

    [TestMethod]
    public void ExceptionsReleaseAdmissionAndSelfRetirementNeverWaitsForItself()
    {
        using var fixture = new Fixture();
        var owner = new ControlServiceMouseCallbackSubscription(fixture.Mouse, fixture.Source, 0);
        owner.Subscribe();
        fixture.Mouse.Action = () => throw new InvalidOperationException("test");
        Assert.ThrowsException<InvalidOperationException>(() => fixture.Source.SixAxis.FireSixAxisEvent(fixture.Args));
        Assert.IsTrue(owner.TryRetire(1000));
        owner = new ControlServiceMouseCallbackSubscription(fixture.Mouse, fixture.Source, 0);
        owner.Subscribe();
        bool returned = false;
        fixture.Mouse.Action = () => { Assert.IsFalse(owner.TryRetire(5000)); returned = true; };
        fixture.Source.SixAxis.FireSixAxisEvent(fixture.Args);
        Assert.IsTrue(returned);
        Assert.IsTrue(owner.TryRetire(1000));
    }

    [TestMethod]
    public void ReentrantRegistryAndLifecycleCallsCannotDeadlockAnExternalCloser()
    {
        using var fixture = new Fixture();
        var registry = new ControlServiceMouseCallbackRegistry();
        var serviceGate = new object();
        var service = CreateService(fixture.Mouse, fixture.Source, registry, serviceGate);
        Assert.IsTrue(registry.TryReplace(0, fixture.Mouse, fixture.Source, 1000));
        var owner = (ControlServiceMouseCallbackSubscription)
            Handlers(fixture.Source.SixAxis, "SixAccelMoved")[0].Target;
        using var entered = new ManualResetEventSlim();
        using var invokeReentrancy = new ManualResetEventSlim();
        fixture.Mouse.Action = () =>
        {
            entered.Set();
            if (!invokeReentrancy.Wait(5000)) throw new TimeoutException();
            Assert.IsFalse(registry.TryRetireSource(fixture.Source, 1000));
            Assert.IsFalse(registry.TryReplace(0, fixture.Mouse, fixture.Source, 1000));
            Assert.IsFalse(service.Start());
            Assert.IsFalse(service.Stop());
            Assert.IsFalse(service.HotPlug());
            service.ShutDown();
            service.StopAndShutDown(false);
        };
        var callback = Task.Run(() => fixture.Source.SixAxis.FireSixAxisEvent(fixture.Args));
        Assert.IsTrue(entered.Wait(5000));
        var closer = Task.Run(() =>
        {
            lock (serviceGate) return registry.TryRetireSource(fixture.Source, 5000);
        });
        try
        {
            Assert.IsTrue(SpinWait.SpinUntil(() => owner.IsRetired, 5000));
        }
        finally { invokeReentrancy.Set(); }
        Assert.IsTrue(callback.Wait(5000));
        Assert.IsTrue(closer.Wait(5000));
        Assert.IsTrue(closer.Result);
    }

    [TestMethod]
    public void RealTouchPadOnIsIdempotentAndCannotCompleteReplacementBeforeDrain()
    {
        using var fixture = new Fixture();
        var successor = new CountingMouse(0, fixture.Source);
        var registry = new ControlServiceMouseCallbackRegistry();
        var service = CreateService(fixture.Mouse, fixture.Source, registry, new object());
        service.TouchPadOn(0, fixture.Source);
        service.TouchPadOn(0, fixture.Source);
        Assert.AreEqual(1, Handlers(fixture.Source.SixAxis, "SixAccelMoved").Length);
        var old = (ControlServiceMouseCallbackSubscription)
            Handlers(fixture.Source.SixAxis, "SixAccelMoved")[0].Target;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        fixture.Mouse.Action = () => { entered.Set(); if (!release.Wait(5000)) throw new TimeoutException(); };
        var callback = Task.Run(() => fixture.Source.SixAxis.FireSixAxisEvent(fixture.Args));
        Assert.IsTrue(entered.Wait(5000));
        service.touchPad[0] = successor;
        var replacement = Task.Run(() => service.TouchPadOn(0, fixture.Source));
        try
        {
            Assert.IsTrue(SpinWait.SpinUntil(() => old.IsRetired, 5000));
            Assert.IsFalse(replacement.IsCompleted);
            Assert.AreEqual(0, successor.Calls);
        }
        finally { release.Set(); }
        Assert.IsTrue(callback.Wait(5000));
        Assert.IsTrue(replacement.Wait(5000));
        fixture.Source.SixAxis.FireSixAxisEvent(fixture.Args);
        Assert.AreEqual(1, successor.Calls);
        Assert.IsTrue(registry.TryRetireSource(fixture.Source, 1000));
    }

    [TestMethod]
    public void ConcurrentCapturedCallbacksCannotEnterAfterRetirementCompletes()
    {
        using var fixture = new Fixture();
        var owner = new ControlServiceMouseCallbackSubscription(fixture.Mouse, fixture.Source, 0);
        owner.Subscribe();
        var callback = Six(fixture.Source, owner);
        using var start = new ManualResetEventSlim();
        var producer = Task.Run(() =>
        {
            if (!start.Wait(5000)) throw new TimeoutException();
            for (int i = 0; i < 10000; i++) callback(fixture.Source.SixAxis, fixture.Args);
        });
        start.Set();
        Assert.IsTrue(owner.TryRetire(5000));
        int calls = fixture.Mouse.Calls;
        for (int i = 0; i < 10000; i++) callback(fixture.Source.SixAxis, fixture.Args);
        Assert.IsTrue(producer.Wait(5000));
        Assert.AreEqual(calls, fixture.Mouse.Calls);
    }

    [TestMethod]
    public void WarmAtomicCallbackAdmissionAllocatesNothing()
    {
        using var fixture = new Fixture();
        var owner = new ControlServiceMouseCallbackSubscription(fixture.Mouse, fixture.Source, 0);
        owner.Subscribe();
        var callback = Six(fixture.Source, owner);
        for (int i = 0; i < 10000; i++) callback(fixture.Source.SixAxis, fixture.Args);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) callback(fixture.Source.SixAxis, fixture.Args);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.AreEqual(0L, allocated);
        Assert.IsTrue(owner.TryRetire(1000));
    }

    [TestMethod]
    public void ReentrantRemovalRevokesCallbacksButRetainsExactSlotForColdRetry()
    {
        using var fixture = new Fixture();
        var registry = new ControlServiceMouseCallbackRegistry();
        var service = CreateService(fixture.Mouse, fixture.Source, registry, new object());
        service.TouchPadOn(0, fixture.Source);
        var copied = (SixAxisHandler<SixAxisEventArgs>)Handlers(fixture.Source.SixAxis, "SixAccelMoved")[0];
        var remove = typeof(ControlService).GetMethod("On_DS4Removal",
            BindingFlags.Instance | BindingFlags.NonPublic).CreateDelegate<Action<object, EventArgs>>(service);
        fixture.Mouse.Action = () => remove(fixture.Source, EventArgs.Empty);
        copied(fixture.Source.SixAxis, fixture.Args);
        Assert.IsTrue(fixture.Source.IsRemoving);
        Assert.AreSame(fixture.Source, service.DS4Controllers[0]);
        Assert.AreSame(fixture.Mouse, service.touchPad[0]);
        copied(fixture.Source.SixAxis, fixture.Args);
        Assert.AreEqual(1, fixture.Mouse.Calls);
        Assert.ThrowsException<InvalidOperationException>(() => service.TouchPadOn(0, fixture.Source));
        Assert.IsTrue(registry.TryRetireSource(fixture.Source, 1000));
    }

    [TestMethod]
    public void SourceRevocationWhileReplacementWaitsCannotAcquireTheRegistryGate()
    {
        using var fixture = new Fixture();
        var successor = new CountingMouse(0, fixture.Source);
        var registry = new ControlServiceMouseCallbackRegistry();
        Assert.IsTrue(registry.TryReplace(0, fixture.Mouse, fixture.Source, 1000));
        var owner = (ControlServiceMouseCallbackSubscription)
            Handlers(fixture.Source.SixAxis, "SixAccelMoved")[0].Target;
        using var entered = new ManualResetEventSlim();
        using var revoke = new ManualResetEventSlim();
        fixture.Mouse.Action = () =>
        {
            entered.Set();
            if (!revoke.Wait(5000)) throw new TimeoutException();
            registry.RevokeSourceFromCallback(fixture.Source);
        };
        var callback = Task.Run(() => fixture.Source.SixAxis.FireSixAxisEvent(fixture.Args));
        Assert.IsTrue(entered.Wait(5000));
        var replacement = Task.Run(() => registry.TryReplace(0, successor, fixture.Source, 5000));
        try
        {
            Assert.IsTrue(SpinWait.SpinUntil(() => owner.IsRetired, 5000));
            Assert.IsFalse(replacement.IsCompleted);
        }
        finally { revoke.Set(); }
        Assert.IsTrue(callback.Wait(5000));
        Assert.IsTrue(replacement.Wait(5000));
        Assert.IsTrue(replacement.Result);
        fixture.Source.SixAxis.FireSixAxisEvent(fixture.Args);
        Assert.AreEqual(1, successor.Calls);
        Assert.IsTrue(registry.TryRetireSource(fixture.Source, 1000));
    }

    private static ControlService CreateService(Mouse mouse, DS4Device source,
        ControlServiceMouseCallbackRegistry registry, object lifecycleGate)
    {
        var service = (ControlService)RuntimeHelpers.GetUninitializedObject(typeof(ControlService));
        service.touchPad = new Mouse[Global.MAX_DS4_CONTROLLER_COUNT];
        service.DS4Controllers = new DS4Device[Global.MAX_DS4_CONTROLLER_COUNT];
        service.touchPad[0] = mouse;
        service.DS4Controllers[0] = mouse.BoundDevice;
        service.DS4Controllers[source.DeviceSlotNumber] = source;
        Set("mouseCallbackRegistry", registry);
        Set("mouseCallbackRetirementWarning", new int[Global.MAX_DS4_CONTROLLER_COUNT]);
        Set("serviceLifecycleLock", lifecycleGate);
        return service;
        void Set(string name, object value) => typeof(ControlService).GetField(name,
            BindingFlags.NonPublic | BindingFlags.Instance).SetValue(service, value);
    }

    private static Delegate[] Handlers(object source, string name) =>
        ((Delegate)source.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
            .GetValue(source))?.GetInvocationList() ?? Array.Empty<Delegate>();

    private static SixAxisHandler<SixAxisEventArgs> Six(DS4Device source,
        ControlServiceMouseCallbackSubscription owner) => (SixAxisHandler<SixAxisEventArgs>)
        Handlers(source.SixAxis, "SixAccelMoved").Single(handler => ReferenceEquals(handler.Target, owner));

    private static List<(Delegate callback, object sender, object args)> CaptureAll(
        DS4Device source, ControlServiceMouseCallbackSubscription owner)
    {
        var result = new List<(Delegate, object, object)>();
        foreach (string name in TouchEvents)
        foreach (Delegate callback in Handlers(source.Touchpad, name))
            if (ReferenceEquals(callback.Target, owner))
                result.Add((callback, source.Touchpad, name is "TouchUnchanged" or "PreTouchProcess" ?
                    EventArgs.Empty : new TouchpadEventArgs(DateTime.UnixEpoch, false, false, null)));
        result.Add((Six(source, owner), source.SixAxis, Args()));
        return result;
    }

    private static SixAxisEventArgs Args() => new(DateTime.UnixEpoch,
        new SixAxis(0, 0, 0, 0, 0, 0, 0.004));

    private static DS4Device CreateDevice(int slot)
    {
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(1, 1, Switch2Transport.Usb,
            out var runtime, out _));
        runtime.DeviceSlotNumber = slot;
        return runtime;
    }

    private sealed class CountingMouse : Mouse
    {
        internal int Calls;
        internal Action Action;
        internal CountingMouse(int slot, DS4Device source) : base(slot, source) { }
        public override void sixaxisMoved(DS4SixAxis sender, SixAxisEventArgs args)
        {
            Interlocked.Increment(ref Calls);
            Action?.Invoke();
        }
        public override void touchButtonDown(DS4Touchpad sender, TouchpadEventArgs args) => Calls++;
        public override void touchButtonUp(DS4Touchpad sender, TouchpadEventArgs args) => Calls++;
        public override void touchesBegan(DS4Touchpad sender, TouchpadEventArgs args) => Calls++;
        public override void touchesMoved(DS4Touchpad sender, TouchpadEventArgs args) => Calls++;
        public override void touchesEnded(DS4Touchpad sender, TouchpadEventArgs args) => Calls++;
        public override void touchUnchanged(DS4Touchpad sender, EventArgs args) => Calls++;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly Mapping.PostMapStickData previousData = Mapping.mapStickActionData[0];
        private readonly byte previousX = Mapping.gyroStickX[0], previousY = Mapping.gyroStickY[0];
        internal DS4Device Source { get; }
        internal CountingMouse Mouse { get; }
        internal SixAxisEventArgs Args { get; } = MouseCallbackLifetimeTests.Args();
        internal Fixture()
        {
            Mapping.mapStickActionData[0] = new Mapping.PostMapStickData();
            Source = CreateDevice(0);
            Mouse = new CountingMouse(0, Source);
        }
        public void Dispose()
        {
            Mapping.mapStickActionData[0] = previousData;
            Mapping.gyroStickX[0] = previousX; Mapping.gyroStickY[0] = previousY;
        }
    }
}
