using System.Buffers.Binary;
using System.Numerics;
using System.Reflection;
using DS4Windows;
using DS4Windows.InputDevices;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public class Switch2RuntimeInputDeviceTests
{
    private const Switch2GattProperty InputProperties =
        Switch2GattProperty.Read | Switch2GattProperty.Notify;

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void OutputTransitionIsScopedAndNeverReplaysPreTransitionSnapshot(bool throws)
    {
        var device = CreateProDevice(90_010, 90_011);
        device.StartUpdate();
        int reports = 0;
        device.Report += (_, _) => reports++;
        bool observedActive = false;
        device.queueEvent(() => device.RunVirtualOutputTransition(() =>
        {
            observedActive = device.IsVirtualOutputTransitionActive;
            device.RunVirtualOutputTransition(() => { });
            Assert.IsTrue(device.IsVirtualOutputTransitionActive,
                "A nested handoff must not clear the outer scope.");
            if (throws) throw new InvalidOperationException("Synthetic attach failure");
        }));
        Assert.AreEqual(!throws, device.TryPublishPro(CreateProFrame(90_010, 90_011, 0)));
        Assert.IsTrue(observedActive);
        Assert.IsFalse(device.IsVirtualOutputTransitionActive);
        Assert.AreEqual(0, reports, "The pre-attach snapshot belongs to the old output.");
        Assert.IsTrue(device.TryPublishPro(CreateProFrame(90_010, 90_011, 0, counter: 2, timestamp: 2)));
        Assert.AreEqual(1, reports);
    }

    [TestMethod]
    public void OutputTransitionRequiresTheColdPublicationOwner()
    {
        var device = CreateProDevice(90_012, 90_013);
        device.StartUpdate();
        Assert.ThrowsException<InvalidOperationException>(() =>
            device.RunVirtualOutputTransition(() => { }));
        bool rejectedFromReport = false;
        device.Report += (_, _) =>
        {
            try { device.RunVirtualOutputTransition(() => { }); }
            catch (InvalidOperationException) { rejectedFromReport = true; }
        };
        Assert.IsTrue(device.TryPublishPro(CreateProFrame(90_012, 90_013, 0)));
        Assert.IsTrue(rejectedFromReport);
        Assert.IsFalse(device.IsVirtualOutputTransitionActive);
    }

    [TestMethod]
    public void BoundedPauseNeverQueuesReentrantOrTimedOutProfileWork()
    {
        var device = CreateProDevice(90_001, 90_002);
        bool actionRan = false;
        device.StartUpdate();
        device.Report += (_, _) => Assert.IsFalse(
            device.TryHaltReportingRunAction(() => actionRan = true));
        Assert.IsTrue(device.TryPublishPro(CreateProFrame(90_001, 90_002, 0)));
        Assert.IsFalse(actionRan, "Rejected work must not replay after the callback.");

        var blocked = CreateProDevice(90_003, 90_004);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        blocked.StartUpdate();
        blocked.Report += (_, _) =>
        {
            entered.Set();
            Assert.IsTrue(release.Wait(3000));
        };
        Task<bool> publication = Task.Run(() => blocked.TryPublishPro(CreateProFrame(90_003, 90_004, 0)));
        try
        {
            Assert.IsTrue(entered.Wait(1000));
            Assert.IsFalse(blocked.TryHaltReportingRunAction(() => actionRan = true));
        }
        finally
        {
            release.Set();
            Assert.IsTrue(publication.Wait(2000));
        }
        Assert.IsFalse(actionRan, "Timed-out work must not replay on the report thread.");
        Assert.IsTrue(blocked.TryHaltReportingRunAction(() => actionRan = true));
        Assert.IsTrue(actionRan);
    }

    [TestMethod]
    public void PhysicalReportsDriveAliveAndMonotonicObservationInterval()
    {
        Switch2RuntimeInputDevice device = CreateProDevice(1001, 1002);
        Assert.IsFalse(device.IsAlive());
        device.StartUpdate();
        Assert.IsFalse(device.IsAlive(), "Activation is not physical input.");
        Assert.IsFalse(device.TryPublishPro(CreateProFrame(1003, 1002, 0)));
        Assert.IsFalse(device.IsAlive(), "Foreign generations are not life.");
        device.Report += (sender, args) =>
        {
            if (((Switch2RuntimeReportEventArgs)args).Kind ==
                Switch2RuntimeReportKind.Regular)
            {
                Assert.IsTrue(sender.IsAlive(),
                    "Existing Report subscribers must observe live input.");
            }
        };
        Assert.IsTrue(device.TryPublishPro(CreateProFrame(1001, 1002, 0,
            timestamp: 100)));
        Assert.IsTrue(device.IsAlive());
        Assert.AreEqual(0.0, device.Latency);
        Assert.IsTrue(device.TryPublishPro(CreateProFrame(1001, 1002, 0,
            counter: 2, timestamp: 20_100)));
        Assert.AreEqual(2.0, device.Latency, 0.000001);
        Assert.IsTrue(device.TryPublishPro(CreateProFrame(1001, 1002, 0,
            counter: 3, timestamp: 20_100)));
        Assert.AreEqual(2.0, device.Latency, 0.000001,
            "A duplicate timestamp must not inflate the observed rate.");
        Assert.IsTrue(device.TryPublishPro(CreateProFrame(1001, 1002, 0,
            counter: 4, timestamp: 10_100)));
        Assert.AreEqual(0.0, device.Latency,
            "A regressed clock starts a new observation window.");
        Assert.IsTrue(device.TryPublishPro(CreateProFrame(1001, 1002, 0,
            counter: 5, timestamp: 30_100)));
        Assert.AreEqual(2.0, device.Latency, 0.000001);
        Assert.IsTrue(device.TryPublishTerminalNeutral());
        Assert.IsFalse(device.IsAlive());
        Assert.AreEqual(2.0, device.Latency, 0.000001,
            "Synthetic terminal neutral is not a physical timing sample.");
    }

    [TestMethod]
    public void StandaloneAndJoinedJoyConsPublishTheirObservedCadence()
    {
        Switch2RuntimeInputDevice standalone = CreateStandaloneDevice(
            Switch2ControllerModel.JoyCon2Right, 1011, 1012);
        standalone.StartUpdate();
        Assert.IsTrue(standalone.TryPublishStandaloneJoyCon(MapStandalone(
            Switch2JoyConProfileMode.StandaloneVerticalRight, 0, 1011, 1012,
            timestamp: 100)));
        Assert.IsTrue(standalone.TryPublishStandaloneJoyCon(MapStandalone(
            Switch2JoyConProfileMode.StandaloneVerticalRight, 0, 1011, 1012,
            counter: 2, timestamp: 20_100)));
        Assert.IsTrue(standalone.IsAlive());
        Assert.AreEqual(2.0, standalone.Latency, 0.000001);

        Switch2RuntimeInputDevice joined = CreateJoinedDevice(1020, 1021,
            1022, 1023, 1024, 1025);
        joined.StartUpdate();
        Assert.IsTrue(joined.TryPublishJoinedJoyCon(MapJoined(1021, 1022,
            1023, 1024, 1025, 0, 0, timestamp: 100)));
        Assert.IsTrue(joined.TryPublishJoinedJoyCon(MapJoined(1021, 1022,
            1023, 1024, 1025, 0, 0, counter: 2, timestamp: 10_100)));
        Assert.IsTrue(joined.IsAlive());
        Assert.AreEqual(1.0, joined.Latency, 0.000001);
        joined.StopUpdate();
        standalone.StopUpdate();
        Assert.IsFalse(joined.IsAlive());
        Assert.IsFalse(standalone.IsAlive());
    }

    [TestMethod]
    public void BluetoothDisconnectRequestIsBoundedCoalescedAndRetryable()
    {
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(1, 2,
            Switch2Transport.BluetoothLe, out var acceptedDevice,
            out var acceptedFailure), acceptedFailure.ToString());
        int acceptedCalls = 0;
        Assert.IsTrue(acceptedDevice.TryBindBluetoothDisconnectRequest(
            generation =>
            {
                Assert.AreEqual(1UL, generation);
                Interlocked.Increment(ref acceptedCalls);
                return true;
            }));
        Assert.IsFalse(acceptedDevice.DisconnectBT(),
            "An unpublished runtime cannot reserve teardown.");
        acceptedDevice.StartUpdate();
        Assert.IsTrue(acceptedDevice.DisconnectBT());
        Assert.IsTrue(acceptedDevice.DisconnectWireless(callRemoval: true));
        Assert.AreEqual(1, Volatile.Read(ref acceptedCalls));
        Assert.IsTrue(acceptedDevice.IsDisconnecting);

        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(3, 4,
            Switch2Transport.BluetoothLe, out var rejectedDevice,
            out var rejectedFailure), rejectedFailure.ToString());
        int rejectedCalls = 0;
        Assert.IsTrue(rejectedDevice.TryBindBluetoothDisconnectRequest(_ =>
        {
            Interlocked.Increment(ref rejectedCalls);
            throw new InvalidOperationException("hostile lifecycle observer");
        }));
        rejectedDevice.StartUpdate();
        Assert.IsFalse(rejectedDevice.DisconnectBT());
        Assert.IsFalse(rejectedDevice.DisconnectBT(),
            "A rejected reservation must be retryable without escaping.");
        Assert.AreEqual(2, Volatile.Read(ref rejectedCalls));
        Assert.IsFalse(rejectedDevice.IsDisconnecting);

        Switch2RuntimeInputDevice usb = CreateProDevice(5, 6);
        Assert.IsFalse(usb.TryBindBluetoothDisconnectRequest(_ => true));
        Assert.IsFalse(usb.DisconnectBT());
    }

    [TestMethod]
    public void ProfileIdleDisconnectUsesMonotonicPhysicalInputActivity()
    {
        const long qpcFrequency = 10_000_000;
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(7, 8,
            Switch2Transport.BluetoothLe, out var device, out var failure),
            failure.ToString());
        int requests = 0;
        Assert.IsTrue(device.TryBindBluetoothDisconnectRequest(_ =>
        {
            Interlocked.Increment(ref requests);
            return true;
        }));
        device.setIdleTimeout(1);
        device.Report += (_, _) => { };
        device.StartUpdate();

        Assert.IsTrue(device.TryPublishPro(CreateProFrame(7, 8, 0,
            counter: 1, timestamp: 100, bluetoothLe: true)));
        Assert.IsTrue(device.TryPublishPro(CreateProFrame(7, 8, 0,
            counter: 2, timestamp: qpcFrequency + 99,
            bluetoothLe: true)));
        Assert.AreEqual(0, Volatile.Read(ref requests));

        Assert.IsTrue(device.TryPublishPro(CreateProFrame(7, 8,
            (uint)Switch2ProButton.FaceSouth, counter: 3,
            timestamp: qpcFrequency + 100, bluetoothLe: true)));
        Assert.IsTrue(device.TryPublishPro(CreateProFrame(7, 8, 0,
            counter: 4, timestamp: qpcFrequency * 2 + 99,
            bluetoothLe: true)));
        Assert.AreEqual(0, Volatile.Read(ref requests),
            "Any physical button must restart the profile idle interval.");

        Assert.IsTrue(device.TryPublishPro(CreateProFrame(7, 8, 0,
            counter: 5, leftX: 0xFFF,
            timestamp: qpcFrequency * 2 + 100, bluetoothLe: true)));
        Assert.IsTrue(device.TryPublishPro(CreateProFrame(7, 8, 0,
            counter: 6, timestamp: qpcFrequency * 3 + 99,
            bluetoothLe: true)));
        Assert.AreEqual(0, Volatile.Read(ref requests),
            "Physical stick activity must restart the profile idle interval.");

        Assert.IsTrue(device.TryPublishPro(CreateProFrame(7, 8, 0,
            counter: 7, timestamp: qpcFrequency * 3 + 100,
            bluetoothLe: true)));
        Assert.AreEqual(1, Volatile.Read(ref requests));
        Assert.IsTrue(device.IsDisconnecting);
    }

    [TestMethod]
    public void JoyConIdleDisconnectCoversStandaloneAndJoinedRuntimes()
    {
        const long qpcFrequency = 10_000_000;
        Switch2RuntimeInputDevice standalone = CreateStandaloneDevice(
            Switch2ControllerModel.JoyCon2Right, 9, 10);
        int standaloneRequests = 0;
        Assert.IsTrue(standalone.TryBindBluetoothDisconnectRequest(_ =>
        {
            Interlocked.Increment(ref standaloneRequests);
            return true;
        }));
        standalone.setIdleTimeout(1);
        standalone.Report += (_, _) => { };
        standalone.StartUpdate();
        Assert.IsTrue(standalone.TryPublishStandaloneJoyCon(MapStandalone(
            Switch2JoyConProfileMode.StandaloneVerticalRight, 0, 9, 10,
            timestamp: 100)));
        Assert.IsTrue(standalone.TryPublishStandaloneJoyCon(MapStandalone(
            Switch2JoyConProfileMode.StandaloneVerticalRight, 0, 9, 10,
            counter: 2, timestamp: qpcFrequency + 100)));
        Assert.AreEqual(1, Volatile.Read(ref standaloneRequests));

        Switch2RuntimeInputDevice joined = CreateJoinedDevice(11, 12,
            13, 14, 15, 16);
        int joinedRequests = 0;
        Assert.IsTrue(joined.TryBindBluetoothDisconnectRequest(_ =>
        {
            Interlocked.Increment(ref joinedRequests);
            return true;
        }));
        joined.setIdleTimeout(1);
        joined.Report += (_, _) => { };
        joined.StartUpdate();
        Assert.IsTrue(joined.TryPublishJoinedJoyCon(MapJoined(12, 13, 14,
            15, 16, 0, 0, timestamp: 100)));
        Assert.IsTrue(joined.TryPublishJoinedJoyCon(MapJoined(12, 13, 14,
            15, 16, 0, 0, counter: 2,
            timestamp: qpcFrequency + 100)));
        Assert.AreEqual(1, Volatile.Read(ref joinedRequests));
    }

    [TestMethod]
    [DoNotParallelize]
    public void ThreeWayAutoDisconnectSeparatesInactiveAbsoluteAndOff()
    {
        const long qpcFrequency = 10_000_000;
        int slot = Global.TEST_PROFILE_INDEX;
        Switch2AutoDisconnectMode previousMode =
            Global.Switch2AutoDisconnectMode[slot];
        long previousTimeout =
            Global.Switch2AutoDisconnectTimeoutSeconds[slot];
        int previousIdle = Global.IdleDisconnectTimeout[slot];
        try
        {
            Global.Switch2AutoDisconnectTimeoutSeconds[slot] = 1;
            Global.IdleDisconnectTimeout[slot] = 1;

            Global.Switch2AutoDisconnectMode[slot] =
                Switch2AutoDisconnectMode.Absolute;
            Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(31, 32,
                Switch2Transport.BluetoothLe, out var absolute,
                out var absoluteFailure), absoluteFailure.ToString());
            absolute.DeviceSlotNumber = slot;
            int absoluteRequests = 0;
            Assert.IsTrue(absolute.TryBindBluetoothDisconnectRequest(_ =>
            {
                Interlocked.Increment(ref absoluteRequests);
                return true;
            }));
            absolute.Report += (_, _) => { };
            absolute.StartUpdate();
            Assert.IsTrue(absolute.TryPublishPro(CreateProFrame(31, 32,
                (uint)Switch2ProButton.FaceSouth, counter: 1,
                timestamp: 100, bluetoothLe: true)));
            Assert.IsTrue(absolute.TryPublishPro(CreateProFrame(31, 32,
                (uint)Switch2ProButton.FaceSouth, counter: 2,
                timestamp: qpcFrequency + 99, bluetoothLe: true)));
            Assert.AreEqual(0, Volatile.Read(ref absoluteRequests),
                "Physical activity must not restart Absolute mode.");
            Assert.IsTrue(absolute.TryPublishPro(CreateProFrame(31, 32,
                (uint)Switch2ProButton.FaceSouth, counter: 3,
                timestamp: qpcFrequency + 100, bluetoothLe: true)));
            Assert.AreEqual(1, Volatile.Read(ref absoluteRequests));

            Global.Switch2AutoDisconnectMode[slot] =
                Switch2AutoDisconnectMode.Inactive;
            Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(33, 34,
                Switch2Transport.BluetoothLe, out var inactive,
                out var inactiveFailure), inactiveFailure.ToString());
            inactive.DeviceSlotNumber = slot;
            int inactiveRequests = 0;
            Assert.IsTrue(inactive.TryBindBluetoothDisconnectRequest(_ =>
            {
                Interlocked.Increment(ref inactiveRequests);
                return true;
            }));
            inactive.Report += (_, _) => { };
            inactive.StartUpdate();
            Assert.IsTrue(inactive.TryPublishPro(CreateProFrame(33, 34, 0,
                counter: 1, timestamp: 100, bluetoothLe: true)));
            Assert.IsTrue(inactive.TryPublishPro(CreateProFrame(33, 34,
                (uint)Switch2ProButton.FaceWest, counter: 2,
                timestamp: qpcFrequency, bluetoothLe: true)));
            Assert.IsTrue(inactive.TryPublishPro(CreateProFrame(33, 34, 0,
                counter: 3, timestamp: qpcFrequency * 2 - 1,
                bluetoothLe: true)));
            Assert.AreEqual(0, Volatile.Read(ref inactiveRequests));
            Assert.IsTrue(inactive.TryPublishPro(CreateProFrame(33, 34, 0,
                counter: 4, timestamp: qpcFrequency * 2,
                bluetoothLe: true)));
            Assert.AreEqual(1, Volatile.Read(ref inactiveRequests));

            Global.Switch2AutoDisconnectMode[slot] =
                Switch2AutoDisconnectMode.Off;
            Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(35, 36,
                Switch2Transport.BluetoothLe, out var off,
                out var offFailure), offFailure.ToString());
            off.DeviceSlotNumber = slot;
            int offRequests = 0;
            Assert.IsTrue(off.TryBindBluetoothDisconnectRequest(_ =>
            {
                Interlocked.Increment(ref offRequests);
                return true;
            }));
            off.setIdleTimeout(1);
            off.Report += (_, _) => { };
            off.StartUpdate();
            Assert.IsTrue(off.TryPublishPro(CreateProFrame(35, 36, 0,
                counter: 1, timestamp: 100, bluetoothLe: true)));
            Assert.IsTrue(off.TryPublishPro(CreateProFrame(35, 36, 0,
                counter: 2, timestamp: qpcFrequency * 10,
                bluetoothLe: true)));
            Assert.AreEqual(0, Volatile.Read(ref offRequests),
                "Explicit Off must override the legacy Idle Disconnect value for Switch 2.");
        }
        finally
        {
            Global.Switch2AutoDisconnectMode[slot] = previousMode;
            Global.Switch2AutoDisconnectTimeoutSeconds[slot] =
                previousTimeout;
            Global.IdleDisconnectTimeout[slot] = previousIdle;
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public void AbsoluteAutoDisconnectCoversJoinedJoyConsAndRejectsUsb()
    {
        const long qpcFrequency = 10_000_000;
        int slot = Global.TEST_PROFILE_INDEX;
        Switch2AutoDisconnectMode previousMode =
            Global.Switch2AutoDisconnectMode[slot];
        long previousTimeout =
            Global.Switch2AutoDisconnectTimeoutSeconds[slot];
        try
        {
            Global.Switch2AutoDisconnectMode[slot] =
                Switch2AutoDisconnectMode.Absolute;
            Global.Switch2AutoDisconnectTimeoutSeconds[slot] = 1;

            Switch2RuntimeInputDevice joined = CreateJoinedDevice(41, 42,
                43, 44, 45, 46);
            joined.DeviceSlotNumber = slot;
            int requests = 0;
            Assert.IsTrue(joined.TryBindBluetoothDisconnectRequest(_ =>
            {
                Interlocked.Increment(ref requests);
                return true;
            }));
            joined.Report += (_, _) => { };
            joined.StartUpdate();
            Assert.IsTrue(joined.TryPublishJoinedJoyCon(MapJoined(42,
                43, 44, 45, 46, 1u << 16, 1u << 4,
                counter: 1, timestamp: 100)));
            Assert.IsTrue(joined.TryPublishJoinedJoyCon(MapJoined(42,
                43, 44, 45, 46, 1u << 16, 1u << 4,
                counter: 2, timestamp: qpcFrequency + 100)));
            Assert.AreEqual(1, Volatile.Read(ref requests));

            Switch2RuntimeInputDevice usb = CreateProDevice(47, 48);
            usb.DeviceSlotNumber = slot;
            usb.Report += (_, _) => { };
            usb.StartUpdate();
            Assert.IsTrue(usb.TryPublishPro(CreateProFrame(47, 48, 0,
                counter: 1, timestamp: 100)));
            Assert.IsTrue(usb.TryPublishPro(CreateProFrame(47, 48, 0,
                counter: 2, timestamp: qpcFrequency * 10)));
            Assert.IsFalse(usb.IsDisconnecting);
        }
        finally
        {
            Global.Switch2AutoDisconnectMode[slot] = previousMode;
            Global.Switch2AutoDisconnectTimeoutSeconds[slot] =
                previousTimeout;
        }
    }

    [TestMethod]
    public void RuntimeTypesAreAppendOnlyAndLegacyFactoryRejectsThem()
    {
        CollectionAssert.AreEqual(new uint[] { 0, 1, 2, 3, 4, 5, 6 },
            new[]
            {
                (uint)InputDeviceType.DS4,
                (uint)InputDeviceType.SwitchPro,
                (uint)InputDeviceType.JoyConL,
                (uint)InputDeviceType.JoyConR,
                (uint)InputDeviceType.JoyConGrip,
                (uint)InputDeviceType.DualSense,
                (uint)InputDeviceType.DS3,
            });
        CollectionAssert.AreEqual(new uint[] { 7, 8, 9, 10 },
            new[]
            {
                (uint)InputDeviceType.Switch2Pro,
                (uint)InputDeviceType.Switch2JoyConLeft,
                (uint)InputDeviceType.Switch2JoyConRight,
                (uint)InputDeviceType.Switch2JoyConJoined,
            });

        foreach (InputDeviceType runtimeType in new[]
        {
            InputDeviceType.Switch2Pro,
            InputDeviceType.Switch2JoyConLeft,
            InputDeviceType.Switch2JoyConRight,
            InputDeviceType.Switch2JoyConJoined,
        })
        {
            NotSupportedException exception =
                Assert.ThrowsException<NotSupportedException>(() =>
                    InputDeviceFactory.CreateDevice(runtimeType, null,
                        "must reject"));
            StringAssert.Contains(exception.Message, "runtime-owned");
        }
    }

    [TestMethod]
    public void NoHidInstancesCoexistAndOutputStateRemainsTransportFenced()
    {
        Switch2RuntimeInputDevice first = CreateProDevice(11, 21);
        Switch2RuntimeInputDevice second = CreateProDevice(12, 22);

        Assert.AreNotSame(first, second);
        Assert.AreEqual(DS4Device.BLANK_SERIAL, first.MacAddress);
        Assert.AreEqual(first.MacAddress, second.MacAddress);
        Assert.IsNull(first.HidDevice);
        Assert.IsFalse(first.HasHidInterface);
        Assert.IsFalse(first.AllowsPersistentIdentity);
        Assert.IsFalse(first.SupportsPhysicalOutput);
        Assert.IsFalse(first.IsHidExclusive);
        Assert.IsFalse(first.isHidExclusive());
        Assert.AreEqual(ConnectionType.USB, first.ConnectionType);
        Assert.AreEqual(InputDeviceType.Switch2Pro, first.DeviceType);
        Assert.IsTrue(first.FeatureSet.HasFlag(VidPidFeatureSet.NoOutputData));
        Assert.IsFalse(first.FeatureSet.HasFlag(
            VidPidFeatureSet.NoBatteryReading));
        Assert.IsFalse(first.FeatureSet.HasFlag(VidPidFeatureSet.NoGyroCalib));
        Assert.AreEqual(99, first.getBattery());
        Assert.IsFalse(first.isCharging());
        Assert.IsFalse(first.Switch2BatteryStatus.IsValid);

        VidPidFeatureSet immutableFeatures = first.FeatureSet;
        first.FeatureSet = VidPidFeatureSet.DefaultDS4;
        Assert.AreEqual(immutableFeatures, first.ModifyFeatureSetFlag(
            VidPidFeatureSet.NoOutputData, false));
        Assert.AreEqual(immutableFeatures, first.FeatureSet);

        first.PostInit();
        first.RefreshCalibration();
        first.StartUpdate();
        Assert.AreEqual(Switch2RuntimeInputDeviceState.Active,
            first.RuntimeState);
        Assert.IsNotNull(first.getRawCurrentState());
        Assert.IsNotNull(first.getRawPreviousState());

        first.RightLightFastRumble = byte.MaxValue;
        first.LeftHeavySlowRumble = byte.MaxValue;
        first.LightBarColor = new DS4Color(1, 2, 3);
        first.setRumble(byte.MaxValue, byte.MaxValue);
        first.SetRumblePreview(true, byte.MaxValue, true, byte.MaxValue);
        first.ClearRumblePreview();
        DS4HapticState haptic = default;
        DS4LightbarState lightbar = default;
        DS4ForceFeedbackState rumble = default;
        first.SetHapticState(ref haptic);
        first.SetLightbarState(ref lightbar);
        first.SetRumbleState(ref rumble);
        Assert.AreEqual(byte.MaxValue, first.RightLightFastRumble);
        Assert.AreEqual(byte.MaxValue, first.LeftHeavySlowRumble);
        Assert.AreEqual(byte.MaxValue, first.getLeftHeavySlowRumble());
        Assert.IsFalse(first.DisconnectWireless(callRemoval: true));
        Assert.IsFalse(first.DisconnectBT(callRemoval: true));
        Assert.IsFalse(first.DisconnectDongle(remove: true));
        Assert.IsFalse(first.IsDisconnecting);

        int order = 0;
        int observedOrder = 0;
        first.queueEvent(() => order = 1);
        first.Report += (_, _) => observedOrder = order;
        Assert.IsTrue(first.TryPublishPro(CreateProFrame(11, 21,
            (uint)Switch2ProButton.FaceWest)));
        Assert.AreEqual(1, observedOrder);
        first.HaltReportingRunAction(() => order = 2);
        Assert.AreEqual(2, order);
        first.removeReportHandlers();
        Assert.IsTrue(first.TryPublishPro(CreateProFrame(11, 21,
            (uint)Switch2ProButton.FaceEast, counter: 2,
            timestamp: 2)));
        Assert.AreEqual(1, observedOrder);
    }

    [TestMethod]
    public void ExistingGyroCalibrationCommandResetsSwitch2QpcEstimator()
    {
        Switch2RuntimeInputDevice device = CreateProDevice(811, 812);
        device.Report += (_, _) => { };
        device.StartUpdate();
        for (int index = 0; index < 20; index++)
        {
            Assert.IsTrue(device.TryPublishPro(CreateProFrame(811, 812, 0,
                counter: (uint)(index + 1),
                timestamp: index * 100_000L,
                accelerometer: new Switch2Vector3Raw(0, 4096, 0),
                gyroscope: new Switch2Vector3Raw(8, -4, 2))));
        }

        Assert.IsTrue(
            device.ContinuousGyroCalibrationElapsedMilliseconds > 0);
        device.ResetContinuousGyroCalibration();
        Assert.AreEqual(0L,
            device.ContinuousGyroCalibrationElapsedMilliseconds);
    }

    [TestMethod]
    public void GyroBiasCommitPersistsAndReconnectAdoptsBeforeActivation()
    {
        byte[] installKey = Enumerable.Repeat((byte)0x42,
            Switch2PersistentPeerId.InstallKeyLength).ToArray();
        byte[] identity = Enumerable.Repeat((byte)0x61, 16).ToArray();
        Assert.IsTrue(Switch2PersistentPeerId.TryDerive(installKey, identity,
            Switch2ControllerModel.ProController2,
            Switch2AdvertisementCodec.ProController2ProductId,
            out Switch2PersistentPeerId peerId));
        var persistence = new InMemoryGyroCalibrationStore();
        Switch2RuntimeInputDevice device = CreateProDevice(901, 902);
        Assert.IsTrue(device.TryBindGyroCalibrationPersistence(persistence,
            peerId));
        Assert.IsFalse(device.TryBindGyroCalibrationPersistence(persistence,
            peerId));
        device.Report += (_, _) => { };
        device.StartUpdate();

        var accelerometer = new Switch2Vector3Raw(0, 4096, 0);
        var gyroscope = new Switch2Vector3Raw(7, -4, 2);
        for (int index = 0; index <= 502; index++)
        {
            Assert.IsTrue(device.TryPublishPro(CreateProFrame(901, 902, 0,
                counter: (uint)(index + 1), timestamp: index * 100_000L,
                accelerometer: accelerometer, gyroscope: gyroscope)));
        }
        Assert.IsTrue(device.HasLeftCalibratedGyroBias);
        Assert.IsTrue(persistence.TryLoad(peerId, out var stored));
        Assert.AreEqual(7.0f /
            Switch2ProMotionProjection.NativeGyroLsbPerDegreeSecond,
            stored.BiasDps.X, 0.0001f);

        Switch2RuntimeInputDevice replacement = CreateProDevice(903, 904);
        Assert.IsTrue(replacement.TryBindGyroCalibrationPersistence(
            persistence, peerId));
        Assert.IsTrue(replacement.HasLeftCalibratedGyroBias);
        Assert.AreEqual(0L,
            replacement.ContinuousGyroCalibrationElapsedMilliseconds,
            "A persisted bias must not restart calibration until the user requests it.");
    }

    [TestMethod]
    public void JoinedAndStandaloneGyroBiasesBindToExactPhysicalSides()
    {
        byte[] key = Enumerable.Repeat((byte)0x31,
            Switch2PersistentPeerId.InstallKeyLength).ToArray();
        Assert.IsTrue(Switch2PersistentPeerId.TryDerive(key,
            Enumerable.Repeat((byte)0x11, 16).ToArray(),
            Switch2ControllerModel.JoyCon2Left,
            Switch2AdvertisementCodec.JoyCon2LeftProductId,
            out var leftPeer));
        Assert.IsTrue(Switch2PersistentPeerId.TryDerive(key,
            Enumerable.Repeat((byte)0x22, 16).ToArray(),
            Switch2ControllerModel.JoyCon2Right,
            Switch2AdvertisementCodec.JoyCon2RightProductId,
            out var rightPeer));
        Assert.IsTrue(Switch2GyroCalibrationRecord.TryCreate(
            new Vector3(0.2f, 0.0f, 0.0f), out var leftCalibration));
        Assert.IsTrue(Switch2GyroCalibrationRecord.TryCreate(
            new Vector3(0.0f, -0.3f, 0.0f), out var rightCalibration));
        var store = new InMemoryGyroCalibrationStore();
        Assert.IsTrue(store.TryQueueStore(leftPeer, leftCalibration));
        Assert.IsTrue(store.TryQueueStore(rightPeer, rightCalibration));

        Switch2RuntimeInputDevice joined = CreateJoinedDevice(905, 906,
            907, 908, 909, 910);
        Assert.IsTrue(joined.TryBindGyroCalibrationPersistence(store,
            leftPeer, rightPeer));
        Assert.IsTrue(joined.HasLeftCalibratedGyroBias);
        Assert.IsTrue(joined.HasRightCalibratedGyroBias);

        Switch2RuntimeInputDevice right = CreateStandaloneDevice(
            Switch2ControllerModel.JoyCon2Right, 911, 912);
        Assert.IsTrue(right.TryBindGyroCalibrationPersistence(store,
            default, rightPeer));
        Assert.IsFalse(right.HasLeftCalibratedGyroBias);
        Assert.IsTrue(right.HasRightCalibratedGyroBias);

        Switch2RuntimeInputDevice pro = CreateProDevice(913, 914);
        Assert.IsFalse(pro.TryBindGyroCalibrationPersistence(store,
            default, rightPeer));
    }

    [TestMethod]
    public void ProMagnetometerCalibrationNeutralizesInputAndAdoptsFullFit()
    {
        Switch2RuntimeInputDevice device = CreateProDevice(813, 814);
        byte[] installKey = Enumerable.Repeat((byte)0x31,
            Switch2PersistentPeerId.InstallKeyLength).ToArray();
        byte[] identity = Enumerable.Repeat((byte)0x73, 16).ToArray();
        Assert.IsTrue(Switch2PersistentPeerId.TryDerive(installKey, identity,
            Switch2ControllerModel.ProController2,
            Switch2AdvertisementCodec.ProController2ProductId,
            out Switch2PersistentPeerId peerId));
        var persistence = new InMemoryMagnetometerCalibrationStore();
        Assert.IsTrue(device.TryBindMagnetometerCalibrationPersistence(
            persistence, peerId));
        device.Report += (_, _) => { };
        device.StartUpdate();

        Assert.IsTrue(device.StartMagnetometerCalibration());
        Assert.IsTrue(device.IsMagnetometerCalibrationActive);
        for (int index = 0; index < 1_000; index++)
        {
            Vector3 unit = CalibrationSpherePoint(index, 1_000);
            var magnetometer = new Switch2Vector3Raw(
                (short)MathF.Round(120.0f + unit.X * 920.0f),
                (short)MathF.Round(-80.0f + unit.Y * 680.0f),
                (short)MathF.Round(45.0f + unit.Z * 810.0f));
            Assert.IsTrue(device.TryPublishPro(CreateProFrame(813, 814,
                (uint)Switch2ProButton.FaceWest,
                counter: (uint)(index + 1), timestamp: index + 1,
                accelerometer: new Switch2Vector3Raw(0, 4096, 0),
                magnetometer: magnetometer)));
            Assert.IsFalse(device.getCurrentStateRef().Square,
                "Calibration reports must not drive the game.");
        }
        Assert.AreEqual(1_000,
            device.LeftMagnetometerCalibrationSampleCount);

        Assert.IsTrue(device.TryCompleteMagnetometerCalibration(
            out var quality, out _, out bool persisted));
        Assert.IsTrue(persisted);
        Assert.IsFalse(device.IsMagnetometerCalibrationActive);
        Assert.AreEqual(
            Switch2MagnetometerCalibrationModel.FullEllipsoidV1,
            quality.AdoptedModel);
        Assert.AreEqual(Switch2MagnetometerCalibrationFitFailure.None,
            quality.FullFitFailure);

        Assert.IsTrue(device.TryPublishPro(CreateProFrame(813, 814,
            (uint)Switch2ProButton.FaceWest, counter: 1_001,
            timestamp: 1_001,
            accelerometer: new Switch2Vector3Raw(0, 4096, 0),
            magnetometer: new Switch2Vector3Raw(1_000, 0, 0))));
        Assert.IsTrue(device.getCurrentStateRef().Square,
            "Completing calibration must restore ordinary publication.");

        Switch2RuntimeInputDevice replacement = CreateProDevice(817, 818);
        Assert.IsTrue(replacement.TryBindMagnetometerCalibrationPersistence(
            persistence, peerId));
        Assert.IsTrue(replacement.HasLeftMagnetometerCalibration,
            "A reconnect must adopt the controller-specific fit before activation.");
    }

    [TestMethod]
    public void MagnetometerCalibrationCancelRestoresInputImmediately()
    {
        Switch2RuntimeInputDevice device = CreateStandaloneDevice(
            Switch2ControllerModel.JoyCon2Left, 815, 816);
        device.Report += (_, _) => { };
        device.StartUpdate();
        Assert.IsTrue(device.StartMagnetometerCalibration());
        Assert.IsTrue(device.TryPublishStandaloneJoyCon(MapStandalone(
            Switch2JoyConProfileMode.StandaloneVerticalLeft,
            1u << 17, 815, 816,
            magnetometer: new Switch2Vector3Raw(500, 400, 300))));
        Assert.IsFalse(device.getCurrentStateRef().DpadUp);

        device.CancelMagnetometerCalibration();
        Assert.IsFalse(device.IsMagnetometerCalibrationActive);
        Assert.IsTrue(device.TryPublishStandaloneJoyCon(MapStandalone(
            Switch2JoyConProfileMode.StandaloneVerticalLeft,
            1u << 17, 815, 816,
            magnetometer: new Switch2Vector3Raw(500, 400, 300))));
        Assert.IsTrue(device.getCurrentStateRef().DpadUp);
    }

    [TestMethod]
    public void ProjectedMotionUsesExistingGyroMappingSeamBeforeReport()
    {
        Switch2RuntimeInputDevice pro = CreateProDevice(821, 822);
        Assert.IsTrue(pro.PrimaryDevice);
        Assert.IsTrue(pro.OutputMapGyro,
            "Switch 2 must enter the existing profile gyro pipeline.");

        int motionEvents = 0;
        int motionEventsSeenByReport = -1;
        SixAxisEventArgs firstEnvelope = null;
        SixAxisEventArgs lastEnvelope = null;
        SixAxis firstAxis = null;
        DateTime firstTimestamp = default;
        pro.SixAxis.SixAccelMoved += (_, args) =>
        {
            motionEvents++;
            firstEnvelope ??= args;
            lastEnvelope = args;
            firstAxis ??= args.sixAxis;
            firstTimestamp = args.timeStamp;
            Assert.AreSame(pro.getCurrentStateRef().Motion, args.sixAxis);
        };
        pro.Report += (_, args) =>
        {
            if (((Switch2RuntimeReportEventArgs)args).Kind ==
                Switch2RuntimeReportKind.Regular)
            {
                motionEventsSeenByReport = motionEvents;
            }
        };
        pro.StartUpdate();

        Assert.IsTrue(pro.TryPublishPro(CreateProFrame(821, 822, 0,
            timestamp: 100_000,
            accelerometer: new Switch2Vector3Raw(0, 4096, 0),
            gyroscope: new Switch2Vector3Raw(100, 200, 300))));
        Assert.AreEqual(1, motionEvents);
        Assert.AreEqual(1, motionEventsSeenByReport,
            "Gyro mapping must run before ordinary Report mapping.");
        Assert.AreNotEqual(default, firstTimestamp);
        Assert.AreSame(firstAxis, pro.getCurrentStateRef().Motion);

        Assert.IsTrue(pro.TryPublishPro(CreateProFrame(821, 822, 0,
            counter: 2, timestamp: 120_000,
            accelerometer: new Switch2Vector3Raw(0, 4096, 0),
            gyroscope: new Switch2Vector3Raw(200, 300, 400))));
        Assert.AreEqual(2, motionEvents);
        Assert.AreSame(firstEnvelope, lastEnvelope,
            "The high-rate event envelope must be reused.");
        Assert.AreNotSame(firstAxis, pro.getCurrentStateRef().Motion,
            "Projection retains the previous sample in a second SixAxis.");
        Assert.AreEqual(0.002, pro.getCurrentStateRef().elapsedTime, 0.000001);
        Assert.AreEqual(2_000UL, pro.getCurrentStateRef().totalMicroSec);

        int proEventsBeforeTerminal = motionEvents;
        Assert.IsTrue(pro.TryPublishTerminalNeutral());
        Assert.AreEqual(proEventsBeforeTerminal, motionEvents,
            "Terminal neutral is lifecycle output, not a motion sample.");

        AssertJoyConMotionSeam(CreateStandaloneDevice(
            Switch2ControllerModel.JoyCon2Left, 823, 824),
            MapStandalone(Switch2JoyConProfileMode.StandaloneHorizontalLeft,
                0, 823, 824));
        AssertJoyConMotionSeam(CreateJoinedDevice(825, 826, 827, 828,
            829, 830), MapJoined(826, 827, 828, 829, 830, 0, 0));
    }

    [TestMethod]
    public void ThrowingGyroObserverRejectsFrameWithoutStrandingRuntime()
    {
        Switch2RuntimeInputDevice device = CreateProDevice(831, 832);
        int reports = 0;
        device.SixAxis.SixAccelMoved += (_, _) =>
            throw new InvalidOperationException("Synthetic gyro fault.");
        device.Report += (_, _) => reports++;
        device.StartUpdate();

        Assert.AreEqual(Switch2RuntimePublicationResult.SubscriberRejected,
            device.TryPublishProDetailed(CreateProFrame(831, 832, 0)));
        Assert.AreEqual(1, reports,
            "A gyro observer fault must not suppress Report subscribers.");
        Assert.AreEqual(Switch2RuntimePublicationResult.SubscriberRejected,
            device.TryPublishProDetailed(CreateProFrame(831, 832, 0,
                counter: 2, timestamp: 2)));
        Assert.AreEqual(2, reports);
        Assert.IsTrue(device.TryPublishTerminalNeutral(),
            "A gyro fault must not strand terminal-neutral delivery.");
    }

    [TestMethod]
    public void BatteryBandsPublishAfterInputAndJoinedUsesLowestHalf()
    {
        Switch2RuntimeInputDevice pro = CreateProDevice(901, 902);
        int reports = 0;
        int changes = 0;
        int batterySeenByReport = -1;
        int reportsSeenByBattery = -1;
        pro.Report += (sender, _) =>
        {
            reports++;
            batterySeenByReport = sender.getBattery();
        };
        pro.BatteryChanged += (_, _) =>
        {
            changes++;
            reportsSeenByBattery = reports;
        };
        pro.StartUpdate();

        Assert.IsTrue(pro.TryPublishPro(CreateProFrame(901, 902, 0,
            batteryVoltageMillivolts: 3000, batteryCurrentRaw: 0x1234,
            batteryOpaque23Raw: 0x56)));
        Assert.AreEqual(1, reports);
        Assert.AreEqual(1, changes);
        Assert.AreEqual(1, reportsSeenByBattery);
        Assert.AreEqual(Switch2BatteryStatus.LowCompatibilityPercentage,
            batterySeenByReport);
        Assert.AreEqual(Switch2BatteryBand.Low,
            pro.Switch2BatteryStatus.Band);
        Assert.AreEqual((ushort)3000,
            pro.Switch2BatteryStatus.VoltageMillivolts);
        Assert.AreEqual((ushort)0x1234,
            pro.Switch2BatteryStatus.CurrentRaw);
        Assert.AreEqual((byte)0x56,
            pro.Switch2BatteryStatus.Opaque23Raw);
        Assert.IsFalse(pro.isCharging());

        Assert.IsTrue(pro.TryPublishPro(CreateProFrame(901, 902, 0,
            counter: 2, timestamp: 2, batteryVoltageMillivolts: 3100)));
        Assert.AreEqual(2, reports);
        Assert.AreEqual(1, changes,
            "Raw changes inside one visible band must not churn the UI.");
        Assert.AreEqual((ushort)3100,
            pro.Switch2BatteryStatus.VoltageMillivolts);

        Assert.IsTrue(pro.TryPublishPro(CreateProFrame(901, 902, 0,
            counter: 3, timestamp: 3, batteryVoltageMillivolts: 0)));
        Assert.AreEqual(1, changes);
        Assert.AreEqual((ushort)3100,
            pro.Switch2BatteryStatus.VoltageMillivolts,
            "Invalid telemetry must not erase the last valid status.");

        Assert.IsTrue(pro.TryPublishPro(CreateProFrame(901, 902, 0,
            counter: 4, timestamp: 4, batteryVoltageMillivolts: 3200)));
        Assert.AreEqual(2, changes);
        Assert.AreEqual(Switch2BatteryStatus.MediumCompatibilityPercentage,
            pro.getBattery());

        Switch2RuntimeInputDevice left = CreateStandaloneDevice(
            Switch2ControllerModel.JoyCon2Left, 903, 904);
        left.StartUpdate();
        Assert.IsTrue(left.TryPublishStandaloneJoyCon(MapStandalone(
            Switch2JoyConProfileMode.StandaloneHorizontalLeft, 0, 903, 904,
            batteryVoltageMillivolts: 3400)));
        Assert.AreEqual(Switch2BatteryBand.High,
            left.Switch2BatteryStatus.Band);
        Assert.IsTrue(left.LeftSwitch2BatteryStatus.IsValid);
        Assert.IsFalse(left.RightSwitch2BatteryStatus.IsValid);

        Switch2RuntimeInputDevice joined = CreateJoinedDevice(905, 906,
            907, 908, 909, 910);
        int joinedChanges = 0;
        joined.BatteryChanged += (_, _) => joinedChanges++;
        joined.StartUpdate();
        Assert.IsTrue(joined.TryPublishJoinedJoyCon(MapJoined(906, 907, 908,
            909, 910, 0, 0, leftBatteryVoltageMillivolts: 3400,
            rightBatteryVoltageMillivolts: 3000)));
        Assert.AreEqual(1, joinedChanges);
        Assert.AreEqual(Switch2BatteryBand.Low,
            joined.Switch2BatteryStatus.Band);
        Assert.AreEqual((ushort)3400,
            joined.LeftSwitch2BatteryStatus.VoltageMillivolts);
        Assert.AreEqual((ushort)3000,
            joined.RightSwitch2BatteryStatus.VoltageMillivolts);
        Assert.AreEqual(Switch2BatteryStatus.LowCompatibilityPercentage,
            joined.getBattery());
        Assert.IsFalse(joined.isCharging());
    }

    [TestMethod]
    public void RegistrationUsesExactReferenceGenerationAndOwner()
    {
        Switch2RuntimeInputDevice device = CreateProDevice(30, 40);
        var owner = new TestOwner(device, 30,
            InputControllerOwnershipKind.Switch2Runtime);

        Assert.IsTrue(InputControllerRegistration.TryCreate(device, 30,
            InputControllerOwnershipKind.Switch2Runtime,
            hasHidInterface: false, hasPersistentIdentity: false, owner,
            out var registration, out var failure), failure.ToString());
        Assert.IsTrue(registration.IsValid);
        Assert.IsTrue(registration.IsOwnerAuthenticated);
        Assert.AreSame(device, registration.Device);
        Assert.AreSame(owner, registration.Owner);
        Assert.AreEqual(30UL, registration.Generation);
        Assert.IsFalse(registration.HasHidInterface);
        Assert.IsFalse(registration.HasPersistentIdentity);

        InputControllerRegistration copy = registration;
        Assert.AreEqual(registration, copy);
        Assert.IsTrue(registration == copy);
        var otherOwner = new TestOwner(device, 30,
            InputControllerOwnershipKind.Switch2Runtime);
        Assert.IsTrue(InputControllerRegistration.TryCreate(device, 30,
            InputControllerOwnershipKind.Switch2Runtime, false, false,
            otherOwner, out var otherRegistration, out _));
        Assert.AreNotEqual(registration, otherRegistration);

        Assert.IsFalse(InputControllerRegistration.TryCreate(device, 0,
            InputControllerOwnershipKind.Switch2Runtime, false, false, owner,
            out _, out failure));
        Assert.AreEqual(InputControllerRegistrationFailure.InvalidGeneration,
            failure);
        var invalidKindOwner = new TestOwner(device, 30,
            (InputControllerOwnershipKind)byte.MaxValue);
        Assert.IsFalse(InputControllerRegistration.TryCreate(device, 30,
            (InputControllerOwnershipKind)byte.MaxValue, false, false,
            invalidKindOwner, out _, out failure));
        Assert.AreEqual(InputControllerRegistrationFailure.InvalidArgument,
            failure);
        Assert.IsFalse(InputControllerRegistration.TryCreate(device, 30,
            InputControllerOwnershipKind.Switch2Runtime, true, false, owner,
            out _, out failure));
        Assert.AreEqual(InputControllerRegistrationFailure.CapabilityMismatch,
            failure);
        Assert.IsFalse(InputControllerRegistration.TryCreate(device, 30,
            InputControllerOwnershipKind.Switch2Runtime, false, true, owner,
            out _, out failure));
        Assert.AreEqual(InputControllerRegistrationFailure.
            PersistentIdentityNotAllowed, failure);

        Assert.IsFalse(registration.TryStopAndQuiesce(-1,
            out var operationFailure));
        Assert.AreEqual(InputControllerOwnerOperationFailure.InvalidTimeout,
            operationFailure);
        Assert.IsTrue(registration.TryStopAndQuiesce(123,
            out operationFailure));
        Assert.AreEqual(InputControllerOwnerOperationFailure.None,
            operationFailure);
        Assert.AreEqual(123, owner.LastTimeoutMilliseconds);
        Assert.IsTrue(registration.TryRemove(out operationFailure));
        Assert.AreEqual(InputControllerOwnerOperationFailure.None,
            operationFailure);

        owner.Authenticated = false;
        Assert.IsFalse(registration.IsOwnerAuthenticated);
        Assert.IsFalse(registration.TryRemove(out operationFailure));
        Assert.AreEqual(InputControllerOwnerOperationFailure.
            OwnerAuthenticationFailed, operationFailure);
    }

    [TestMethod]
    public void ThrowingOwnerCallbacksAlwaysFailClosedWithStableDiagnostics()
    {
        Switch2RuntimeInputDevice device = CreateProDevice(31, 41);
        var owner = new TestOwner(device, 31,
            InputControllerOwnershipKind.Switch2Runtime)
        {
            ThrowOnKind = true,
        };
        Assert.IsFalse(InputControllerRegistration.TryCreate(device, 31,
            InputControllerOwnershipKind.Switch2Runtime, false, false, owner,
            out _, out var createFailure));
        Assert.AreEqual(InputControllerRegistrationFailure.OwnerThrew,
            createFailure);

        owner.ThrowOnKind = false;
        owner.ThrowOnAuthenticate = true;
        Assert.IsFalse(InputControllerRegistration.TryCreate(device, 31,
            InputControllerOwnershipKind.Switch2Runtime, false, false, owner,
            out _, out createFailure));
        Assert.AreEqual(InputControllerRegistrationFailure.OwnerThrew,
            createFailure);

        owner.ThrowOnAuthenticate = false;
        Assert.IsTrue(InputControllerRegistration.TryCreate(device, 31,
            InputControllerOwnershipKind.Switch2Runtime, false, false, owner,
            out var registration, out _));
        owner.ThrowOnAuthenticate = true;
        Assert.IsFalse(registration.IsOwnerAuthenticated);
        Assert.IsFalse(registration.TryStopAndQuiesce(1,
            out var operationFailure));
        Assert.AreEqual(InputControllerOwnerOperationFailure.OwnerThrew,
            operationFailure);
        owner.ThrowOnAuthenticate = false;
        owner.ThrowOnStop = true;
        Assert.IsFalse(registration.TryStopAndQuiesce(1,
            out operationFailure));
        Assert.AreEqual(InputControllerOwnerOperationFailure.OwnerThrew,
            operationFailure);
        owner.ThrowOnStop = false;
        owner.ThrowOnRemove = true;
        Assert.IsFalse(registration.TryRemove(out operationFailure));
        Assert.AreEqual(InputControllerOwnerOperationFailure.OwnerThrew,
            operationFailure);
        owner.ThrowOnRemove = false;
        owner.ThrowOnKind = true;
        Assert.IsFalse(registration.IsValid);
        Assert.IsFalse(registration.TryRemove(out operationFailure));
        Assert.AreEqual(InputControllerOwnerOperationFailure.OwnerThrew,
            operationFailure);
    }

    [TestMethod]
    public void ProPublicationRejectsForeignLifetimeAndPreservesReportOrder()
    {
        Switch2RuntimeInputDevice device = CreateProDevice(50, 60);
        var reports = new List<(DS4State Current, DS4State Previous)>();
        device.Report += (sender, _) => reports.Add((
            sender.getRawCurrentState(), sender.getRawPreviousState()));

        Switch2ProProfileInputFrame first = CreateProFrame(50, 60,
            (uint)(Switch2ProButton.FaceWest | Switch2ProButton.C),
            leftX: 0xFFF);
        Assert.IsFalse(device.TryPublishPro(first));
        device.StartUpdate();
        Assert.IsFalse(device.TryPublishPro(CreateProFrame(51, 60, 0)));
        Assert.IsFalse(device.TryPublishPro(CreateProFrame(50, 61, 0)));
        Assert.IsFalse(device.TryPublishPro(CreateProFrame(50, 60, 0,
            bluetoothLe: true)));
        Assert.AreEqual(0, reports.Count);

        Assert.IsTrue(device.TryPublishPro(first));
        Assert.IsTrue(device.TryPublishPro(CreateProFrame(50, 60,
            (uint)Switch2ProButton.FaceEast, counter: 2,
            rightX: 0, timestamp: 2)));
        Assert.AreEqual(2, reports.Count);
        Assert.AreEqual(1U, reports[0].Current.PacketCounter);
        Assert.IsTrue(reports[0].Current.Square);
        Assert.IsTrue(reports[0].Current.Switch2RawInputStatus.CButton);
        Assert.AreEqual(0U, reports[0].Previous.PacketCounter);
        Assert.IsFalse(reports[0].Previous.Square);
        Assert.AreEqual(2U, reports[1].Current.PacketCounter);
        Assert.IsTrue(reports[1].Current.Circle);
        Assert.AreEqual(1U, reports[1].Previous.PacketCounter);
        Assert.IsTrue(reports[1].Previous.Square);
        Assert.AreEqual(2U, device.getRawPreviousState().PacketCounter);
        Assert.AreEqual(2U, device.LastPublishedPacketCounter);

        Switch2RuntimeInputDevice exceptionDevice = CreateProDevice(70, 80);
        int laterSubscriberCalls = 0;
        Switch2RuntimeReportEventArgs laterEnvelope = null;
        exceptionDevice.Report += (_, _) => throw new InvalidOperationException();
        exceptionDevice.Report += (_, args) =>
        {
            laterSubscriberCalls++;
            laterEnvelope = args as Switch2RuntimeReportEventArgs;
        };
        exceptionDevice.StartUpdate();
        Assert.IsFalse(exceptionDevice.TryPublishPro(CreateProFrame(70, 80,
            0)));
        Assert.AreEqual(1, laterSubscriberCalls,
            "One bad subscriber must not suppress the mapping subscriber.");
        Assert.IsNotNull(laterEnvelope);
        Assert.AreEqual(Switch2RuntimeReportKind.Regular,
            laterEnvelope.Kind);
        Assert.AreEqual(70UL, laterEnvelope.RuntimeGeneration);
    }

    [TestMethod]
    public void JoyConModelGenerationAndPairEpochGatesRejectForeignFrames()
    {
        Switch2RuntimeInputDevice left = CreateStandaloneDevice(
            Switch2ControllerModel.JoyCon2Left, 101, 201);
        left.StartUpdate();
        Assert.IsFalse(left.TryPublishStandaloneJoyCon(MapStandalone(
            Switch2JoyConProfileMode.StandaloneHorizontalRight, 0,
            101, 201)));
        Assert.IsFalse(left.TryPublishStandaloneJoyCon(MapStandalone(
            Switch2JoyConProfileMode.StandaloneHorizontalLeft, 0,
            102, 201)));
        Assert.IsFalse(left.TryPublishStandaloneJoyCon(MapStandalone(
            Switch2JoyConProfileMode.StandaloneHorizontalLeft, 0,
            101, 202)));
        Assert.IsTrue(left.TryPublishStandaloneJoyCon(MapStandalone(
            Switch2JoyConProfileMode.StandaloneHorizontalLeft, 1u << 16,
            101, 201)));

        Switch2RuntimeInputDevice joined = CreateJoinedDevice(300, 400,
            1, 2, 3, 4);
        joined.StartUpdate();
        Assert.IsFalse(joined.TryPublishJoinedJoyCon(MapJoined(401,
            1, 2, 3, 4, 0, 0)));
        Assert.IsFalse(joined.TryPublishJoinedJoyCon(MapJoined(400,
            9, 2, 3, 4, 0, 0)));
        Assert.IsTrue(joined.TryPublishJoinedJoyCon(MapJoined(400,
            1, 2, 3, 4, 0, 0)));
        Assert.IsFalse(joined.TryPublishStandaloneJoyCon(MapStandalone(
            Switch2JoyConProfileMode.StandaloneHorizontalLeft, 0, 1, 2)));

        Switch2RuntimeInputDevice pro = CreateProDevice(1, 2);
        pro.StartUpdate();
        Assert.IsFalse(pro.TryPublishJoinedJoyCon(MapJoined(400,
            1, 2, 3, 4, 0, 0)));
        Assert.IsFalse(pro.TryPublishPro(default));
    }

    [DataTestMethod]
    [DataRow(Switch2ControllerModel.JoyCon2Left)]
    [DataRow(Switch2ControllerModel.JoyCon2Right)]
    public void StandaloneRuntimeAcceptsEitherProfileOrientationWithoutRebind(
        Switch2ControllerModel model)
    {
        const ulong deviceGeneration = 611;
        const ulong transportGeneration = 733;
        Switch2RuntimeInputDevice runtime = CreateStandaloneDevice(model,
            deviceGeneration, transportGeneration);
        runtime.StartUpdate();

        Switch2JoyConProfileMode vertical =
            Switch2JoyConProfileInputMapper.StandaloneModeFor(model,
                Switch2JoyConHoldMode.Vertical);
        Switch2JoyConProfileMode horizontal =
            Switch2JoyConProfileInputMapper.StandaloneModeFor(model,
                Switch2JoyConHoldMode.Horizontal);
        Assert.IsTrue(runtime.TryPublishStandaloneJoyCon(MapStandalone(
            vertical, 0, deviceGeneration, transportGeneration)));
        Assert.IsTrue(runtime.TryPublishStandaloneJoyCon(MapStandalone(
            horizontal, 0, deviceGeneration, transportGeneration)));
        Assert.AreEqual(Switch2RuntimeInputDeviceState.Active,
            runtime.RuntimeState);
        Assert.AreEqual(deviceGeneration, runtime.RuntimeGeneration);
        Assert.IsTrue(runtime.HasExactStandaloneBluetoothBinding(model,
            deviceGeneration, transportGeneration));

        Switch2ControllerModel opposite = model ==
            Switch2ControllerModel.JoyCon2Left ?
                Switch2ControllerModel.JoyCon2Right :
                Switch2ControllerModel.JoyCon2Left;
        Assert.IsFalse(runtime.TryPublishStandaloneJoyCon(MapStandalone(
            Switch2JoyConProfileInputMapper.StandaloneModeFor(opposite,
                Switch2JoyConHoldMode.Vertical), 0, deviceGeneration,
            transportGeneration)));
    }

    [TestMethod]
    public void JoinedPublicationPreservesBusyFrameLifecycleAndSubscriberResults()
    {
        Switch2RuntimeInputDevice joined = CreateJoinedDevice(300, 400,
            1, 2, 3, 4);
        Switch2JoyConProfileInputFrame valid = MapJoined(400,
            1, 2, 3, 4, 0, 0);
        Assert.AreEqual(Switch2RuntimePublicationResult.LifecycleClosed,
            joined.TryPublishJoinedJoyConDetailed(valid));
        joined.StartUpdate();
        Assert.AreEqual(Switch2RuntimePublicationResult.FrameRejected,
            joined.TryPublishJoinedJoyConDetailed(MapJoined(401,
                1, 2, 3, 4, 0, 0)));

        using ManualResetEventSlim subscriberEntered = new(false);
        using ManualResetEventSlim releaseSubscriber = new(false);
        joined.Report += (_, _) =>
        {
            subscriberEntered.Set();
            releaseSubscriber.Wait();
        };
        Switch2RuntimePublicationResult firstResult = default;
        Task first = Task.Run(() => firstResult =
            joined.TryPublishJoinedJoyConDetailed(valid));
        Assert.IsTrue(subscriberEntered.Wait(1_000));
        Assert.AreEqual(Switch2RuntimePublicationResult.PublicationBusy,
            joined.TryPublishJoinedJoyConDetailed(valid));
        releaseSubscriber.Set();
        Assert.IsTrue(first.Wait(1_000));
        Assert.AreEqual(Switch2RuntimePublicationResult.Published,
            firstResult);

        Switch2RuntimeInputDevice rejected = CreateJoinedDevice(301, 401,
            11, 12, 13, 14);
        rejected.Report += (_, _) =>
            throw new InvalidOperationException("Synthetic subscriber fault.");
        rejected.StartUpdate();
        Assert.AreEqual(Switch2RuntimePublicationResult.SubscriberRejected,
            rejected.TryPublishJoinedJoyConDetailed(MapJoined(401,
                11, 12, 13, 14, 0, 0)));
        Assert.IsTrue(rejected.TryPublishTerminalNeutral());
        Assert.AreEqual(Switch2RuntimePublicationResult.LifecycleClosed,
            rejected.TryPublishJoinedJoyConDetailed(MapJoined(401,
                11, 12, 13, 14, 0, 0)));
    }

    [TestMethod]
    public void CAndRailSidecarsSurviveTheExistingStateReportPipeline()
    {
        Switch2RuntimeInputDevice pro = CreateProDevice(5, 6);
        DS4State proState = null;
        pro.Report += (sender, _) => proState = sender.getRawCurrentState();
        pro.StartUpdate();
        Assert.IsTrue(pro.TryPublishPro(CreateProFrame(5, 6,
            (uint)(Switch2ProButton.C | Switch2ProButton.LeftPaddle |
                Switch2ProButton.RightPaddle))));
        Assert.IsTrue(proState.Switch2RawInputStatus.IsValid);
        Assert.IsTrue(proState.Switch2RawInputStatus.CButton);
        Assert.IsTrue(proState.BLP);
        Assert.IsTrue(proState.BRP);
        Assert.IsFalse(proState.Mute);

        Switch2RuntimeInputDevice joined = CreateJoinedDevice(10, 11,
            12, 13, 14, 15);
        DS4State joinedState = null;
        joined.Report += (sender, _) => joinedState =
            sender.getRawCurrentState();
        joined.StartUpdate();
        Assert.IsTrue(joined.TryPublishJoinedJoyCon(MapJoined(11,
            12, 13, 14, 15, (1u << 21) | (1u << 20),
            (1u << 14) | (1u << 5) | (1u << 4))));
        Assert.IsTrue(joinedState.Switch2JoyConRawInputStatus.IsValid);
        Assert.IsTrue(joinedState.Switch2JoyConRawInputStatus.CButton);
        Assert.IsTrue(joinedState.Switch2JoyConRawInputStatus.LeftRailSL);
        Assert.IsTrue(joinedState.Switch2JoyConRawInputStatus.LeftRailSR);
        Assert.IsTrue(joinedState.Switch2JoyConRawInputStatus.RightRailSL);
        Assert.IsTrue(joinedState.Switch2JoyConRawInputStatus.RightRailSR);
        Assert.IsFalse(joinedState.Switch2JoyConRawInputStatus.LeftPaddle1);
        Assert.IsFalse(joinedState.Switch2JoyConRawInputStatus.RightPaddle1);
        Assert.IsFalse(joinedState.BLP);
        Assert.IsFalse(joinedState.BRP);
        Assert.IsFalse(joinedState.Mute);

        Switch2RuntimeInputDevice miniLeft = CreateStandaloneDevice(
            Switch2ControllerModel.JoyCon2Left, 20, 21);
        miniLeft.StartUpdate();
        Assert.IsTrue(miniLeft.TryPublishStandaloneJoyCon(MapStandalone(
            Switch2JoyConProfileMode.StandaloneHorizontalLeft,
            (1u << 22) | (1u << 23), 20, 21)));
        DS4State miniState = miniLeft.getRawCurrentState();
        Assert.IsTrue(miniState.Switch2JoyConRawInputStatus.LeftPaddle1);
        Assert.IsTrue(miniState.Switch2JoyConRawInputStatus.LeftPaddle2);

        Switch2RuntimeInputDevice miniRight = CreateStandaloneDevice(
            Switch2ControllerModel.JoyCon2Right, 22, 23);
        miniRight.StartUpdate();
        Assert.IsTrue(miniRight.TryPublishStandaloneJoyCon(MapStandalone(
            Switch2JoyConProfileMode.StandaloneHorizontalRight,
            (1u << 6) | (1u << 7) | (1u << 14), 22, 23)));
        miniState = miniRight.getRawCurrentState();
        Assert.IsTrue(miniState.Switch2JoyConRawInputStatus.CButton);
        Assert.IsTrue(miniState.Switch2JoyConRawInputStatus.RightPaddle1);
        Assert.IsTrue(miniState.Switch2JoyConRawInputStatus.RightPaddle2);
    }

    [TestMethod]
    [DoNotParallelize]
    public void ProRuntimeAppliesLiveProfileFaceButtonLayout()
    {
        int slot = Global.TEST_PROFILE_INDEX;
        Switch2FaceButtonLayout previous =
            Global.Switch2FaceButtonLayout[slot];
        try
        {
            Global.Switch2FaceButtonLayout[slot] =
                Switch2FaceButtonLayout.Xbox;
            Switch2RuntimeInputDevice device = CreateProDevice(290, 291);
            device.DeviceSlotNumber = slot;
            DS4State observed = null;
            device.Report += (sender, _) => observed =
                sender.getRawCurrentState();
            device.StartUpdate();

            Assert.IsTrue(device.TryPublishPro(CreateProFrame(290, 291,
                (uint)Switch2ProButton.FaceSouth, counter: 1,
                timestamp: 1)));
            Assert.IsTrue(observed.Cross);
            Assert.IsFalse(observed.Circle);

            Global.Switch2FaceButtonLayout[slot] =
                Switch2FaceButtonLayout.Nintendo;
            Assert.IsTrue(device.TryPublishPro(CreateProFrame(290, 291,
                (uint)Switch2ProButton.FaceSouth, counter: 2,
                timestamp: 2)));
            Assert.IsFalse(observed.Cross);
            Assert.IsTrue(observed.Circle);
            Assert.AreEqual((uint)Switch2ProButton.FaceSouth,
                observed.Switch2RawInputStatus.RawButtonBits);
        }
        finally
        {
            Global.Switch2FaceButtonLayout[slot] = previous;
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public void JoinedRuntimeAppliesLiveDjgModeAndKeepsTriggerVisible()
    {
        int slot = Global.TEST_PROFILE_INDEX;
        bool previousEnabled = Global.Switch2DualJoyConGyroFusionEnabled[slot];
        Switch2DualGyroMode previousMode =
            Global.Switch2DualJoyConGyroMode[slot];
        Switch2DualGyroDominantSide previousDominant =
            Global.Switch2DualJoyConGyroDominantSide[slot];
        Switch2DualGyroActivationMode previousActivation =
            Global.Switch2DualJoyConGyroActivationMode[slot];
        Switch2JoyConProfileButton previousLeft =
            Global.Switch2DualJoyConGyroLeftActivationButton[slot];
        Switch2JoyConProfileButton previousRight =
            Global.Switch2DualJoyConGyroRightActivationButton[slot];
        try
        {
            Global.Switch2DualJoyConGyroFusionEnabled[slot] = true;
            Global.Switch2DualJoyConGyroMode[slot] =
                Switch2DualGyroMode.SwitchGyroSide;
            Global.Switch2DualJoyConGyroDominantSide[slot] =
                Switch2DualGyroDominantSide.Right;
            Global.Switch2DualJoyConGyroActivationMode[slot] =
                Switch2DualGyroActivationMode.Toggle;
            Global.Switch2DualJoyConGyroLeftActivationButton[slot] =
                Switch2JoyConProfileButton.LeftRailSL;
            Global.Switch2DualJoyConGyroRightActivationButton[slot] =
                Switch2JoyConProfileButton.None;

            Switch2RuntimeInputDevice joined = CreateJoinedDevice(300, 400,
                1, 2, 3, 4);
            joined.DeviceSlotNumber = slot;
            DS4State observed = null;
            joined.Report += (sender, _) => observed =
                sender.getRawCurrentState();
            joined.StartUpdate();
            Assert.IsTrue(joined.TryPublishJoinedJoyCon(MapJoined(400,
                1, 2, 3, 4, 0, 0,
                leftGyroscope: new Switch2Vector3Raw(16384, 0, 0),
                rightGyroscope: new Switch2Vector3Raw(0, 8192, 0))));
            Assert.AreEqual(0.0, observed.Motion.angVelPitch, 0.01);
            Assert.AreEqual(500.0, observed.Motion.angVelRoll, 0.01);

            Assert.IsTrue(joined.TryPublishJoinedJoyCon(MapJoined(400,
                1, 2, 3, 4, 1u << 21, 0,
                leftGyroscope: new Switch2Vector3Raw(16384, 0, 0),
                rightGyroscope: new Switch2Vector3Raw(0, 8192, 0))));
            Assert.AreEqual(1000.0, observed.Motion.angVelPitch, 0.01);
            Assert.AreEqual(0.0, observed.Motion.angVelRoll, 0.01);
            Assert.IsTrue(observed.Switch2JoyConRawInputStatus.LeftRailSL,
                "DJG observes the sidecar; it does not consume game input.");
        }
        finally
        {
            Global.Switch2DualJoyConGyroFusionEnabled[slot] = previousEnabled;
            Global.Switch2DualJoyConGyroMode[slot] = previousMode;
            Global.Switch2DualJoyConGyroDominantSide[slot] = previousDominant;
            Global.Switch2DualJoyConGyroActivationMode[slot] =
                previousActivation;
            Global.Switch2DualJoyConGyroLeftActivationButton[slot] =
                previousLeft;
            Global.Switch2DualJoyConGyroRightActivationButton[slot] =
                previousRight;
        }
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    [DoNotParallelize]
    public void JoinedRuntimeUsesStationaryOwnIrAndOverlappingButtons(bool left)
    {
        const int slot = Global.TEST_PROFILE_INDEX;
        using var settings = new DjgProfileScope(slot);
        Global.Switch2DualJoyConGyroActivationMode[slot] =
            Switch2DualGyroActivationMode.Hold;
        var physical = left ? Switch2JoyConProfileButton.LeftShoulder :
            Switch2JoyConProfileButton.RightShoulder;
        var ir = left ? Switch2JoyConProfileButton.LeftIrSensor :
            Switch2JoyConProfileButton.RightIrSensor;
        var bindings = left ? Global.Switch2DualJoyConGyroLeftActivationButton :
            Global.Switch2DualJoyConGyroRightActivationButton;
        bindings[slot] = physical | ir;
        // Independent pointer/movement verification is intentionally disabled.
        Global.Switch2JoyConIrMouseEnabled[slot] = false;

        var device = CreateJoinedDevice(300, 400, 1, 2, 3, 4);
        device.DeviceSlotNumber = slot;
        device.StartUpdate();
        DS4State observed = null;
        device.Report += (sender, _) => observed = sender.getRawCurrentState();
        uint counter = 0;
        void Publish(ushort distance, bool button, ushort oppositeDistance = 0)
        {
            counter++;
            var frame = MapJoined(400, 1, 2, 3, 4,
                left && button ? 1u << 22 : 0,
                !left && button ? 1u << 6 : 0,
                leftGyroscope: new Switch2Vector3Raw(16384, 0, 0),
                rightGyroscope: new Switch2Vector3Raw(0, 8192, 0),
                counter: counter, timestamp: 100 + counter * 40_000,
                leftIrDistance: left ? distance : oppositeDistance,
                rightIrDistance: left ? oppositeDistance : distance);
            Assert.IsTrue(device.TryPublishJoinedJoyCon(frame));
            Assert.AreEqual(frame.LeftSource.RawButtonBits,
                observed.Switch2JoyConRawInputStatus.LeftRawButtonBits);
            Assert.AreEqual(frame.RightSource.RawButtonBits,
                observed.Switch2JoyConRawInputStatus.RightRawButtonBits);
        }
        void AssertRightGyro() => Assert.AreEqual(500.0,
            observed.Motion.angVelRoll, 0.01);
        void AssertLeftGyro() => Assert.AreEqual(1000.0,
            observed.Motion.angVelPitch, 0.01);

        Publish(0, false);
        AssertRightGyro();
        Publish(0, false, oppositeDistance: 500);
        AssertRightGyro();
        Publish(500, false);
        AssertLeftGyro();
        Publish(500, true);
        AssertLeftGyro();
        Publish(0, true);
        AssertLeftGyro();
        Assert.IsTrue(left ? observed.L1 : observed.R1);
        Publish(0, false);
        AssertRightGyro();

        // Editing the threshold while stationary baselines the new held state.
        Publish(1200, false);
        AssertRightGyro();
        var thresholds = left ? Global.Switch2JoyConLeftIrMouseActivationThreshold :
            Global.Switch2JoyConRightIrMouseActivationThreshold;
        thresholds[slot] = Switch2IrActivationThreshold.Balanced;
        Publish(1200, false);
        AssertRightGyro();
        Publish(0, false);
        AssertRightGyro();
        Publish(1200, false);
        AssertLeftGyro();
    }

    [TestMethod]
    [DoNotParallelize]
    public void JoinedRuntimeRebasesIdenticalDjgSettingsOnProfileRevision()
    {
        const int slot = 0;
        using var settings = new DjgProfileScope(slot);
        Global.Switch2DualJoyConGyroLeftActivationButton[slot] =
            Switch2JoyConProfileButton.LeftIrSensor;
        var device = CreateJoinedDevice(300, 400, 1, 2, 3, 4);
        device.DeviceSlotNumber = slot;
        device.StartUpdate();
        DS4State observed = null;
        device.Report += (sender, _) => observed = sender.getRawCurrentState();
        uint counter = 0;
        void Publish(ushort distance)
        {
            counter++;
            Assert.IsTrue(device.TryPublishJoinedJoyCon(MapJoined(400,
                1, 2, 3, 4, 0, 0,
                leftGyroscope: new Switch2Vector3Raw(16384, 0, 0),
                rightGyroscope: new Switch2Vector3Raw(0, 8192, 0),
                counter: counter, timestamp: counter * 40_000,
                leftIrDistance: distance)));
        }
        Publish(0);
        Publish(500);
        Assert.AreEqual(1000.0, observed.Motion.angVelPitch, 0.01);
        Global.BeginProfileSwitchRevision(slot);
        Publish(500);
        Assert.AreEqual(500.0, observed.Motion.angVelRoll, 0.01);
        Publish(0);
        Assert.AreEqual(500.0, observed.Motion.angVelRoll, 0.01);
        Publish(500);
        Assert.AreEqual(1000.0, observed.Motion.angVelPitch, 0.01);
    }

    [TestMethod]
    public void DjgIrObservationRejectsForeignBitsAbsentSourcesAndThresholdEdges()
    {
        var descriptor = CreateCommonDescriptor(
            Switch2ControllerModel.JoyCon2Left, 1, 2);
        foreach (var sample in new (ushort Distance, ushort Roughness, bool Active)[]
        {
            (0, 0, false), (999, 3999, true), (1000, 3999, false),
            (999, 4000, false), (ushort.MaxValue, 0, false),
        })
        {
            var canonical = CreateCommonFrame(descriptor, 1, 0, 2048, 2048,
                100, irDistance: sample.Distance, irRoughness: sample.Roughness);
            Assert.IsTrue(canonical.TryGetLeftStick(out var stick));
            var source = new Switch2JoyConProfileSide(canonical, stick,
                Switch2JoyConProfileButton.LeftIrSensor |
                Switch2JoyConProfileButton.RightIrSensor |
                Switch2JoyConProfileButton.LeftPaddle1, uint.MaxValue);
            var observed = Switch2DualJoyConGyroMode.ObserveActivationButtons(
                source, Switch2JoyConSide.Left, Switch2IrActivationThreshold.Strict);
            Assert.AreEqual(Switch2JoyConProfileButton.LeftPaddle1 |
                (sample.Active ? Switch2JoyConProfileButton.LeftIrSensor :
                    Switch2JoyConProfileButton.None), observed);
            Assert.AreEqual(Switch2JoyConProfileButton.None,
                Switch2DualJoyConGyroMode.ObserveActivationButtons(source,
                    Switch2JoyConSide.Right, Switch2IrActivationThreshold.Strict));
            Assert.AreEqual(Switch2JoyConProfileButton.LeftPaddle1,
                Switch2DualJoyConGyroMode.ObserveActivationButtons(source,
                    Switch2JoyConSide.Left, (Switch2IrActivationThreshold)255));
            Assert.IsTrue((source.Buttons & Switch2JoyConProfileButton.RightIrSensor) != 0,
                "Observation must not mutate the original source.");
        }
        Assert.AreEqual(Switch2JoyConProfileButton.None,
            Switch2DualJoyConGyroMode.ObserveActivationButtons(default,
                Switch2JoyConSide.Left, Switch2IrActivationThreshold.Relaxed));
    }

    [TestMethod]
    public void WarmDjgIrObservationAndModeResolutionAllocateNothing()
    {
        var frame = MapJoined(400, 1, 2, 3, 4, 0, 0,
            leftIrDistance: 500, rightIrDistance: 500);
        Assert.IsTrue(Switch2DualGyroConfiguration.TryCreate(true,
            Switch2DualGyroMode.SingleSideToggle,
            Switch2DualGyroDominantSide.None, Switch2DualGyroActivationMode.Hold,
            Switch2JoyConProfileButton.LeftIrSensor | Switch2JoyConProfileButton.LeftPaddle1,
            Switch2JoyConProfileButton.RightIrSensor | Switch2JoyConProfileButton.RightPaddle1,
            out var configuration));
        Switch2DualGyroModeState state = default;
        bool valid = true;
        for (int pass = 0; pass < 2; pass++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < (pass == 0 ? 2_000 : 20_000); index++)
            {
                var left = Switch2DualJoyConGyroMode.ObserveActivationButtons(
                    frame.LeftSource, Switch2JoyConSide.Left, configuration.LeftIrThreshold);
                var right = Switch2DualJoyConGyroMode.ObserveActivationButtons(
                    frame.RightSource, Switch2JoyConSide.Right, configuration.RightIrThreshold);
                valid &= Switch2DualJoyConGyroMode.TryResolve(ref state, frame.PairEpoch,
                    index % 2 == 0 ? left : Switch2JoyConProfileButton.None,
                    index % 3 == 0 ? right : Switch2JoyConProfileButton.None,
                    configuration, out _);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            if (pass == 1)
            {
                Assert.AreEqual(0L, allocated);
            }
        }
        Assert.IsTrue(valid);
    }

    private sealed class DjgProfileScope : IDisposable
    {
        private readonly int slot;
        private readonly bool enabled, pointer;
        private readonly Switch2DualGyroMode mode;
        private readonly Switch2DualGyroDominantSide dominant;
        private readonly Switch2DualGyroActivationMode activation;
        private readonly Switch2JoyConProfileButton left, right;
        private readonly Switch2IrActivationThreshold leftThreshold, rightThreshold;

        internal DjgProfileScope(int slot)
        {
            this.slot = slot;
            enabled = Global.Switch2DualJoyConGyroFusionEnabled[slot];
            mode = Global.Switch2DualJoyConGyroMode[slot];
            dominant = Global.Switch2DualJoyConGyroDominantSide[slot];
            activation = Global.Switch2DualJoyConGyroActivationMode[slot];
            left = Global.Switch2DualJoyConGyroLeftActivationButton[slot];
            right = Global.Switch2DualJoyConGyroRightActivationButton[slot];
            leftThreshold = Global.Switch2JoyConLeftIrMouseActivationThreshold[slot];
            rightThreshold = Global.Switch2JoyConRightIrMouseActivationThreshold[slot];
            pointer = Global.Switch2JoyConIrMouseEnabled[slot];
            Global.Switch2DualJoyConGyroFusionEnabled[slot] = true;
            Global.Switch2DualJoyConGyroMode[slot] = Switch2DualGyroMode.SwitchGyroSide;
            Global.Switch2DualJoyConGyroDominantSide[slot] = Switch2DualGyroDominantSide.Right;
            Global.Switch2DualJoyConGyroActivationMode[slot] = Switch2DualGyroActivationMode.Toggle;
            Global.Switch2DualJoyConGyroLeftActivationButton[slot] = Switch2JoyConProfileButton.None;
            Global.Switch2DualJoyConGyroRightActivationButton[slot] = Switch2JoyConProfileButton.None;
            Global.Switch2JoyConLeftIrMouseActivationThreshold[slot] = Switch2IrActivationThreshold.Strict;
            Global.Switch2JoyConRightIrMouseActivationThreshold[slot] = Switch2IrActivationThreshold.Strict;
        }

        public void Dispose()
        {
            Global.Switch2DualJoyConGyroFusionEnabled[slot] = enabled;
            Global.Switch2DualJoyConGyroMode[slot] = mode;
            Global.Switch2DualJoyConGyroDominantSide[slot] = dominant;
            Global.Switch2DualJoyConGyroActivationMode[slot] = activation;
            Global.Switch2DualJoyConGyroLeftActivationButton[slot] = left;
            Global.Switch2DualJoyConGyroRightActivationButton[slot] = right;
            Global.Switch2JoyConLeftIrMouseActivationThreshold[slot] = leftThreshold;
            Global.Switch2JoyConRightIrMouseActivationThreshold[slot] = rightThreshold;
            Global.Switch2JoyConIrMouseEnabled[slot] = pointer;
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public void ProRuntimeReadsLiveHorizonProfileIntoMotionProjection()
    {
        int slot = Global.TEST_PROFILE_INDEX;
        bool previous = Global.Switch2HorizonStabilizationEnabled[slot];
        try
        {
            Global.Switch2HorizonStabilizationEnabled[slot] = true;
            Switch2RuntimeInputDevice device = CreateProDevice(310, 410);
            device.DeviceSlotNumber = slot;
            DS4State observed = null;
            device.Report += (sender, _) => observed =
                sender.getRawCurrentState();
            device.StartUpdate();

            Assert.IsTrue(device.TryPublishPro(CreateProFrame(310, 410, 0,
                counter: 1, timestamp: 1,
                accelerometer: new Switch2Vector3Raw(0, 0, 4096),
                gyroscope: new Switch2Vector3Raw(160, 80, 320))));
            Assert.IsTrue(device.TryPublishPro(CreateProFrame(310, 410, 0,
                counter: 2, timestamp: 100_001,
                accelerometer: new Switch2Vector3Raw(0, 0, 4096),
                gyroscope: new Switch2Vector3Raw(160, 80, 320))));

            Assert.IsNotNull(observed);
            Assert.AreEqual(0.0, observed.Motion.angVelRoll, 0.0001);
            Assert.IsTrue(Math.Abs(observed.Motion.angVelYaw) > 0.0);
            Assert.IsTrue(Math.Abs(observed.Motion.angVelPitch) > 0.0);
        }
        finally
        {
            Global.Switch2HorizonStabilizationEnabled[slot] = previous;
        }
    }

    [TestMethod]
    public void TerminalNeutralCompletesExactlyOnceAndRejectsStaleInput()
    {
        Switch2RuntimeInputDevice device = CreateProDevice(90, 91);
        var reports = new List<DS4State>();
        var envelopes = new List<Switch2RuntimeReportEventArgs>();
        device.Report += (sender, args) =>
        {
            reports.Add(sender.getRawCurrentState());
            envelopes.Add(args as Switch2RuntimeReportEventArgs);
        };
        device.StartUpdate();
        Switch2ProProfileInputFrame pressed = CreateProFrame(90, 91,
            (uint)(Switch2ProButton.FaceWest | Switch2ProButton.LeftTrigger |
                Switch2ProButton.C));
        Assert.IsTrue(device.TryPublishPro(pressed));

        Assert.AreEqual(Switch2TerminalNeutralRequestResult.AcceptedCompleted,
            device.RequestTerminalNeutral());
        Assert.IsTrue(device.TerminalNeutralCompleted);
        Assert.IsTrue(device.TerminalNeutralReported);
        Assert.IsTrue(device.TryWaitForTerminalNeutralCompletion(0));
        Assert.AreEqual(2, reports.Count);
        Assert.AreEqual(2, envelopes.Count);
        Assert.IsNotNull(envelopes[0]);
        Assert.IsNotNull(envelopes[1]);
        Assert.AreEqual(Switch2RuntimeReportKind.Regular, envelopes[0].Kind);
        Assert.AreEqual(Switch2RuntimeReportKind.TerminalNeutral,
            envelopes[1].Kind);
        Assert.AreEqual(90UL, envelopes[0].RuntimeGeneration);
        Assert.AreEqual(90UL, envelopes[1].RuntimeGeneration);
        DS4State neutral = reports[1];
        Assert.AreEqual(2U, neutral.PacketCounter);
        Assert.AreEqual((byte)128, neutral.LX);
        Assert.AreEqual((byte)128, neutral.LY);
        Assert.IsFalse(neutral.Square);
        Assert.IsFalse(neutral.L2Btn);
        Assert.AreEqual((byte)0, neutral.L2);
        Assert.IsFalse(neutral.Switch2RawInputStatus.IsValid);
        Assert.IsFalse(neutral.Switch2JoyConRawInputStatus.IsValid);
        Assert.AreEqual(Switch2RuntimeInputDeviceState.Terminal,
            device.RuntimeState);
        Assert.AreEqual(Switch2TerminalNeutralRequestResult.
            RejectedAlreadyReserved, device.RequestTerminalNeutral());
        device.StopUpdate();
        Assert.IsFalse(device.TryPublishPro(pressed));
        Assert.AreEqual(2, reports.Count);
        Assert.AreEqual(1, envelopes.Count(envelope => envelope.Kind ==
            Switch2RuntimeReportKind.TerminalNeutral));

        Switch2RuntimeInputDevice noSubscriber = CreateProDevice(92, 93);
        noSubscriber.StartUpdate();
        Assert.AreEqual(Switch2TerminalNeutralRequestResult.AcceptedCompleted,
            noSubscriber.RequestTerminalNeutral());
        Assert.IsTrue(noSubscriber.TerminalNeutralCompleted);
        Assert.IsFalse(noSubscriber.TerminalNeutralReported);

        Switch2RuntimeInputDevice throwing = CreateProDevice(94, 95);
        int goodSubscriberCalls = 0;
        Switch2RuntimeReportEventArgs goodTerminalEnvelope = null;
        throwing.Report += (_, _) => throw new InvalidOperationException();
        throwing.Report += (_, args) =>
        {
            goodSubscriberCalls++;
            goodTerminalEnvelope = args as Switch2RuntimeReportEventArgs;
        };
        throwing.StartUpdate();
        throwing.StopUpdate();
        Assert.IsTrue(throwing.TerminalNeutralCompleted);
        Assert.IsFalse(throwing.TerminalNeutralReported);
        Assert.AreEqual(1, goodSubscriberCalls);
        Assert.IsNotNull(goodTerminalEnvelope);
        Assert.AreEqual(Switch2RuntimeReportKind.TerminalNeutral,
            goodTerminalEnvelope.Kind);
        Assert.AreEqual(94UL, goodTerminalEnvelope.RuntimeGeneration);
    }

    [TestMethod]
    [Timeout(5000)]
    public void TerminalRequestFromConcurrentAndReentrantCallbacksCannotDeadlock()
    {
        Switch2RuntimeInputDevice concurrent = CreateProDevice(110, 120);
        int concurrentReports = 0;
        var concurrentKinds = new Switch2RuntimeReportKind[2];
        var concurrentGenerations = new ulong[2];
        bool terminalCallCompletedInsideReport = false;
        Switch2TerminalNeutralRequestResult concurrentResult = default;
        concurrent.Report += (_, args) =>
        {
            int reportIndex = Interlocked.Increment(ref concurrentReports) - 1;
            var envelope = args as Switch2RuntimeReportEventArgs;
            if (envelope != null && reportIndex < concurrentKinds.Length)
            {
                concurrentKinds[reportIndex] = envelope.Kind;
                concurrentGenerations[reportIndex] =
                    envelope.RuntimeGeneration;
            }
            if (reportIndex != 0)
            {
                return;
            }

            Task request = Task.Run(() => concurrentResult =
                concurrent.RequestTerminalNeutral());
            terminalCallCompletedInsideReport = request.Wait(1000);
        };
        concurrent.StartUpdate();
        Assert.IsTrue(concurrent.TryPublishPro(CreateProFrame(110, 120,
            (uint)Switch2ProButton.FaceWest)));
        Assert.IsTrue(terminalCallCompletedInsideReport,
            "Report ran under the ownership lock and blocked concurrent stop.");
        Assert.AreEqual(Switch2TerminalNeutralRequestResult.AcceptedPending,
            concurrentResult);
        Assert.AreEqual(2, concurrentReports);
        CollectionAssert.AreEqual(new[]
        {
            Switch2RuntimeReportKind.Regular,
            Switch2RuntimeReportKind.TerminalNeutral,
        }, concurrentKinds);
        CollectionAssert.AreEqual(new ulong[] { 110, 110 },
            concurrentGenerations);
        Assert.IsTrue(concurrent.TerminalNeutralCompleted);
        Assert.IsTrue(concurrent.TryWaitForTerminalNeutralCompletion(0));

        Switch2RuntimeInputDevice reentrant = CreateProDevice(130, 140);
        int reentrantReports = 0;
        bool reentrantWaitRejected = false;
        bool reentrantHaltActionRan = false;
        Switch2TerminalNeutralRequestResult reentrantResult = default;
        reentrant.Report += (_, _) =>
        {
            if (++reentrantReports != 1)
            {
                return;
            }

            reentrant.HaltReportingRunAction(() =>
                reentrantHaltActionRan = true);
            reentrantResult = reentrant.RequestTerminalNeutral();
            reentrantWaitRejected = !reentrant.
                TryWaitForTerminalNeutralCompletion(1000);
        };
        reentrant.StartUpdate();
        Assert.IsTrue(reentrant.TryPublishPro(CreateProFrame(130, 140,
            (uint)Switch2ProButton.FaceWest)));
        Assert.AreEqual(Switch2TerminalNeutralRequestResult.AcceptedPending,
            reentrantResult);
        Assert.IsTrue(reentrantWaitRejected);
        Assert.IsTrue(reentrantHaltActionRan,
            "A reentrant profile action must drain after subscribers.");
        Assert.AreEqual(2, reentrantReports);
        Assert.IsTrue(reentrant.TerminalNeutralCompleted);

        Switch2RuntimeInputDevice repeated = CreateProDevice(150, 160);
        int repeatedReports = 0;
        Switch2RuntimeReportEventArgs repeatedEnvelope = null;
        repeated.Report += (_, args) =>
        {
            repeatedEnvelope = args as Switch2RuntimeReportEventArgs;
            Interlocked.Increment(ref repeatedReports);
        };
        repeated.StartUpdate();
        Task<bool>[] requests = Enumerable.Range(0, 16).Select(_ =>
            Task.Run(repeated.TryPublishTerminalNeutral)).ToArray();
        Task.WaitAll(requests);
        Assert.AreEqual(1, requests.Count(task => task.Result));
        Assert.AreEqual(1, repeatedReports);
        Assert.IsTrue(repeated.TerminalNeutralCompleted);
        Assert.IsNotNull(repeatedEnvelope);
        Assert.AreEqual(Switch2RuntimeReportKind.TerminalNeutral,
            repeatedEnvelope.Kind);
        Assert.AreEqual(150UL, repeatedEnvelope.RuntimeGeneration);

        Switch2RuntimeInputDevice halted = CreateProDevice(170, 180);
        int haltedReports = 0;
        Switch2TerminalNeutralRequestResult haltedResult = default;
        halted.Report += (_, _) => haltedReports++;
        halted.StartUpdate();
        halted.HaltReportingRunAction(() => haltedResult =
            halted.RequestTerminalNeutral());
        Assert.AreEqual(Switch2TerminalNeutralRequestResult.AcceptedPending,
            haltedResult);
        Assert.AreEqual(1, haltedReports);
        Assert.IsTrue(halted.TerminalNeutralCompleted);

        Switch2RuntimeInputDevice timed = CreateProDevice(190, 200);
        using var subscriberEntered = new ManualResetEventSlim(false);
        using var releaseSubscriber = new ManualResetEventSlim(false);
        bool timedActionRan = false;
        timed.Report += (_, _) =>
        {
            subscriberEntered.Set();
            releaseSubscriber.Wait(2000);
        };
        timed.StartUpdate();
        Task<bool> blockedPublication = Task.Run(() => timed.TryPublishPro(
            CreateProFrame(190, 200, 0)));
        Assert.IsTrue(subscriberEntered.Wait(1000));
        timed.HaltReportingRunAction(() => timedActionRan = true);
        releaseSubscriber.Set();
        Assert.IsTrue(blockedPublication.Wait(1000));
        Assert.IsTrue(blockedPublication.Result);
        Assert.IsTrue(timedActionRan,
            "A timed-out profile action must be queued, not discarded.");
    }

    [TestMethod]
    public void ReportEnvelopeIsSealedImmutableGenerationBoundAndPreallocated()
    {
        Type envelopeType = typeof(Switch2RuntimeReportEventArgs);
        Assert.IsTrue(envelopeType.IsSealed);
        Assert.AreEqual(0, envelopeType.GetConstructors(
            BindingFlags.Instance | BindingFlags.Public).Length);
        Assert.IsFalse(envelopeType.GetProperty(
            nameof(Switch2RuntimeReportEventArgs.Kind)).CanWrite);
        Assert.IsFalse(envelopeType.GetProperty(
            nameof(Switch2RuntimeReportEventArgs.RuntimeGeneration)).CanWrite);
        EventInfo reportEvent = typeof(Switch2RuntimeInputDevice).GetEvent(
            nameof(Switch2RuntimeInputDevice.Report));
        Assert.IsNotNull(reportEvent);
        Assert.AreEqual(typeof(DS4Device.ReportHandler<EventArgs>),
            reportEvent.EventHandlerType);
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new Switch2RuntimeReportEventArgs(
                Switch2RuntimeReportKind.Regular, 0));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new Switch2RuntimeReportEventArgs(
                (Switch2RuntimeReportKind)byte.MaxValue, 1));

        Switch2RuntimeInputDevice device = CreateProDevice(210, 220);
        var envelopes = new List<Switch2RuntimeReportEventArgs>();
        device.Report += (_, args) => envelopes.Add(
            args as Switch2RuntimeReportEventArgs);
        device.StartUpdate();
        Switch2ProProfileInputFrame frame = CreateProFrame(210, 220, 0);
        Assert.IsTrue(device.TryPublishPro(frame));
        Assert.IsTrue(device.TryPublishPro(frame));
        device.StopUpdate();

        Assert.AreEqual(3, envelopes.Count);
        Assert.IsTrue(envelopes.All(envelope => envelope != null));
        Assert.AreEqual(Switch2RuntimeReportKind.Regular, envelopes[0].Kind);
        Assert.AreEqual(Switch2RuntimeReportKind.Regular, envelopes[1].Kind);
        Assert.AreEqual(Switch2RuntimeReportKind.TerminalNeutral,
            envelopes[2].Kind);
        Assert.AreEqual(210UL, envelopes[0].RuntimeGeneration);
        Assert.AreEqual(210UL, envelopes[1].RuntimeGeneration);
        Assert.AreEqual(210UL, envelopes[2].RuntimeGeneration);
        Assert.AreSame(envelopes[0], envelopes[1],
            "Regular reports must reuse the per-device envelope.");
        Assert.AreNotSame(envelopes[0], envelopes[2]);
    }

    [TestMethod]
    public void SelfRequeuedActionCannotStarveRegularOrTerminalReport()
    {
        Switch2RuntimeInputDevice device = CreateProDevice(225, 226);
        int keepRequeueing = 1;
        int actionCount = 0;
        int regularCount = 0;
        int terminalCount = 0;
        Action selfRequeue = null;
        selfRequeue = () =>
        {
            Interlocked.Increment(ref actionCount);
            if (Volatile.Read(ref keepRequeueing) != 0)
            {
                device.queueEvent(selfRequeue);
            }
        };
        device.queueEvent(selfRequeue);
        device.Report += (_, args) =>
        {
            if (((Switch2RuntimeReportEventArgs)args).Kind ==
                Switch2RuntimeReportKind.Regular)
            {
                Interlocked.Increment(ref regularCount);
            }
            else
            {
                Interlocked.Increment(ref terminalCount);
            }
        };
        device.StartUpdate();

        Task<bool> regular = Task.Run(() => device.TryPublishPro(
            CreateProFrame(225, 226, (uint)Switch2ProButton.FaceWest)));
        if (!regular.Wait(TimeSpan.FromSeconds(1)))
        {
            // Lets an old unbounded implementation unwind so the failed test
            // cannot leave an infinite ThreadPool action behind.
            Volatile.Write(ref keepRequeueing, 0);
            Assert.IsTrue(regular.Wait(TimeSpan.FromSeconds(1)),
                "The unbounded action drain did not unwind after requeue stopped.");
            Assert.Fail("A self-requeued action starved the regular Report.");
        }
        Assert.IsTrue(regular.Result);
        Assert.AreEqual(1, regularCount);
        Assert.AreEqual(1, Volatile.Read(ref actionCount),
            "A pre-report self-requeue must remain for a future publication.");

        Task<Switch2TerminalNeutralRequestResult> terminal = Task.Run(
            device.RequestTerminalNeutral);
        if (!terminal.Wait(TimeSpan.FromSeconds(1)))
        {
            Volatile.Write(ref keepRequeueing, 0);
            Assert.IsTrue(terminal.Wait(TimeSpan.FromSeconds(1)),
                "The unbounded action drain did not unwind after requeue stopped.");
            Assert.Fail("A self-requeued action starved terminal stop delivery.");
        }
        Volatile.Write(ref keepRequeueing, 0);

        Assert.AreEqual(Switch2TerminalNeutralRequestResult.AcceptedCompleted,
            terminal.Result);
        Assert.AreEqual(1, terminalCount);
        Assert.AreEqual(2, Volatile.Read(ref actionCount));
        Assert.IsTrue(device.TerminalNeutralCompleted);
        Assert.IsTrue(device.TerminalNeutralReported);
    }

    [TestMethod]
    public void WarmedRegularAndTerminalEnvelopeDeliveryAllocatesNothing()
    {
        Switch2RuntimeInputDevice device = CreateProDevice(230, 240);
        Switch2RuntimeReportEventArgs lastEnvelope = null;
        SixAxisEventArgs lastMotionEnvelope = null;
        device.Report += (_, args) => lastEnvelope =
            args as Switch2RuntimeReportEventArgs;
        device.SixAxis.SixAccelMoved += (_, args) =>
            lastMotionEnvelope = args;
        device.StartUpdate();
        Switch2ProProfileInputFrame frame = CreateProFrame(230, 240, 0);

        for (int index = 0; index < 32; index++)
        {
            Assert.IsTrue(device.TryPublishPro(frame));
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long beforeRegular = GC.GetAllocatedBytesForCurrentThread();
        bool succeeded = true;
        for (int index = 0; index < 1_000; index++)
        {
            succeeded &= device.TryPublishPro(frame);
        }
        long regularAllocated = GC.GetAllocatedBytesForCurrentThread() -
            beforeRegular;

        long beforeTerminal = GC.GetAllocatedBytesForCurrentThread();
        Switch2TerminalNeutralRequestResult terminalResult =
            device.RequestTerminalNeutral();
        long terminalAllocated = GC.GetAllocatedBytesForCurrentThread() -
            beforeTerminal;

        Assert.IsTrue(succeeded);
        Assert.AreEqual(0L, regularAllocated);
        Assert.AreEqual(0L, terminalAllocated);
        Assert.AreEqual(Switch2TerminalNeutralRequestResult.AcceptedCompleted,
            terminalResult);
        Assert.IsNotNull(lastEnvelope);
        Assert.IsNotNull(lastMotionEnvelope);
        Assert.AreEqual(Switch2RuntimeReportKind.TerminalNeutral,
            lastEnvelope.Kind);
        Assert.AreEqual(230UL, lastEnvelope.RuntimeGeneration);
    }

    [TestMethod]
    public void RuntimeFoundationIsDormantAndDeclaresNoPhysicalTransportOutput()
    {
        Type runtime = typeof(Switch2RuntimeInputDevice);
        Assert.AreEqual(typeof(DS4Device), runtime.BaseType);
        Assert.IsFalse(typeof(SwitchProDevice).IsAssignableFrom(runtime));
        Assert.IsFalse(typeof(JoyConDevice).IsAssignableFrom(runtime));

        foreach (FieldInfo field in runtime.GetFields(BindingFlags.Instance |
            BindingFlags.NonPublic | BindingFlags.Public |
            BindingFlags.DeclaredOnly))
        {
            string typeName = field.FieldType.FullName ?? string.Empty;
            Assert.IsFalse(typeName.Contains(nameof(HidDevice),
                StringComparison.Ordinal));
            Assert.IsFalse(typeName.Contains("SafeHandle",
                StringComparison.Ordinal));
            Assert.IsFalse(typeName.Contains("Windows.Devices",
                StringComparison.Ordinal));
            Assert.IsFalse(typeName.Contains("OutputDevice",
                StringComparison.Ordinal));
        }

        string[] requiredOverrides =
        {
            nameof(DS4Device.DisconnectWireless),
            nameof(DS4Device.DisconnectBT),
            nameof(DS4Device.DisconnectDongle),
            nameof(DS4Device.RefreshCalibration),
            nameof(DS4Device.SetHapticState),
            nameof(DS4Device.SetLightbarState),
            nameof(DS4Device.SetRumbleState),
            nameof(DS4Device.SetRumblePreview),
            nameof(DS4Device.ClearRumblePreview),
            nameof(DS4Device.setRumble),
        };
        foreach (string methodName in requiredOverrides)
        {
            Assert.IsTrue(runtime.GetMethods(BindingFlags.Instance |
                BindingFlags.Public | BindingFlags.DeclaredOnly).Any(
                    method => method.Name == methodName),
                $"Missing fail-closed runtime override: {methodName}");
        }

        AssertTypeDoesNotReferenceRuntime(typeof(ControlService), runtime);
        AssertTypeDoesNotReferenceRuntime(typeof(DS4Devices), runtime);
        FieldInfo knownDevicesField = typeof(DS4Devices).GetField(
            "knownDevices", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(knownDevicesField);
        var knownDevices = (VidPidInfo[])knownDevicesField.GetValue(null);
        Assert.IsFalse(knownDevices.Any(entry => entry.vid == 0x057E &&
            entry.pid == 0x2069));
    }

    private static void AssertTypeDoesNotReferenceRuntime(Type inspected,
        Type runtime)
    {
        Assert.IsFalse(inspected.GetFields(BindingFlags.Instance |
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Any(field => field.FieldType == runtime));
        Assert.IsFalse(inspected.GetMethods(BindingFlags.Instance |
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Any(method => method.ReturnType == runtime ||
                method.GetParameters().Any(parameter =>
                    parameter.ParameterType == runtime)));
    }

    private static Switch2RuntimeInputDevice CreateProDevice(
        ulong deviceGeneration, ulong transportGeneration,
        Switch2Transport transport = Switch2Transport.Usb)
    {
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(deviceGeneration,
            transportGeneration, transport, out var device, out var failure),
            failure.ToString());
        return device;
    }

    private static void AssertJoyConMotionSeam(
        Switch2RuntimeInputDevice device,
        in Switch2JoyConProfileInputFrame frame)
    {
        int motionEvents = 0;
        int observedByReport = -1;
        device.SixAxis.SixAccelMoved += (_, args) =>
        {
            motionEvents++;
            Assert.AreSame(device.getCurrentStateRef().Motion, args.sixAxis);
        };
        device.Report += (_, args) =>
        {
            if (((Switch2RuntimeReportEventArgs)args).Kind ==
                Switch2RuntimeReportKind.Regular)
            {
                observedByReport = motionEvents;
            }
        };
        device.StartUpdate();

        bool published = device.DeviceType ==
            InputDeviceType.Switch2JoyConJoined ?
            device.TryPublishJoinedJoyCon(frame) :
            device.TryPublishStandaloneJoyCon(frame);
        Assert.IsTrue(published);
        Assert.AreEqual(1, motionEvents);
        Assert.AreEqual(1, observedByReport);
        Assert.IsTrue(device.OutputMapGyro);
    }

    private static Switch2RuntimeInputDevice CreateStandaloneDevice(
        Switch2ControllerModel model, ulong deviceGeneration,
        ulong transportGeneration)
    {
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreateStandaloneJoyCon(model,
            deviceGeneration, transportGeneration, out var device,
            out var failure), failure.ToString());
        return device;
    }

    private static Switch2RuntimeInputDevice CreateJoinedDevice(
        ulong runtimeGeneration, ulong pairEpoch, ulong leftDeviceGeneration,
        ulong leftTransportGeneration, ulong rightDeviceGeneration,
        ulong rightTransportGeneration)
    {
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreateJoinedJoyCon(
            runtimeGeneration, pairEpoch, leftDeviceGeneration,
            leftTransportGeneration, rightDeviceGeneration,
            rightTransportGeneration, out var device, out var failure),
            failure.ToString());
        return device;
    }

    internal static Switch2ProProfileInputFrame CreateProFrame(
        ulong deviceGeneration, ulong transportGeneration, uint buttons,
        uint counter = 1, ushort leftX = 0x800, ushort leftY = 0x800,
        ushort rightX = 0x800, ushort rightY = 0x800, long timestamp = 1,
        bool bluetoothLe = false, ushort batteryVoltageMillivolts = 0,
        ushort batteryCurrentRaw = 0, byte batteryOpaque23Raw = 0,
        Switch2Vector3Raw accelerometer = default,
        Switch2Vector3Raw gyroscope = default,
        Switch2Vector3Raw magnetometer = default)
    {
        Switch2InputProtocolIdentity identity;
        if (bluetoothLe)
        {
            Assert.IsTrue(Switch2InputProtocolIdentity.TryCreateBluetoothLe(
                Switch2InputCodec.ServiceUuid,
                Switch2InputCodec.Common05CharacteristicUuid,
                InputProperties, Switch2ControllerModel.ProController2,
                out identity));
        }
        else
        {
            Assert.IsTrue(Switch2InputProtocolIdentity.
                TryCreateProController2Usb(
                    Switch2InputProtocolIdentity.NintendoUsbVendorId,
                    Switch2InputProtocolIdentity.ProController2UsbProductId,
                    Switch2InputProtocolIdentity.
                        AuditedProController2UsbBcdDevice, out identity));
        }
        Assert.IsTrue(Switch2InputSessionDescriptor.TryCreate(identity,
            deviceGeneration, transportGeneration, 10_000_000,
            out var descriptor));
        Assert.IsTrue(Switch2InputCalibrationSnapshot.TryCreateFallback(
            Switch2ControllerModel.ProController2, deviceGeneration,
            out var calibration));
        var session = new Switch2InputSession(descriptor, calibration);
        byte[] packet = BuildCommonPacket(counter, buttons, leftX, leftY,
            rightX, rightY, batteryVoltageMillivolts, batteryCurrentRaw,
            batteryOpaque23Raw, accelerometer, gyroscope);
        WriteMagnetometer(packet, magnetometer);
        ReadOnlySpan<byte> report = bluetoothLe ? packet.AsSpan(1) : packet;
        Assert.IsTrue(session.TryProcess(descriptor, report, timestamp,
            out var canonical, out var sessionFailure),
            sessionFailure.ToString());
        Assert.IsTrue(Switch2ProProfileInputMapper.TryMap(canonical,
            out var frame, out var mapFailure), mapFailure.ToString());
        return frame;
    }

    private static Switch2JoyConProfileInputFrame MapStandalone(
        Switch2JoyConProfileMode mode, uint buttons, ulong deviceGeneration,
        ulong transportGeneration, ushort batteryVoltageMillivolts = 0,
        Switch2Vector3Raw magnetometer = default, uint counter = 1,
        ushort physicalX = 0x800, ushort physicalY = 0x800,
        long timestamp = 100)
    {
        Switch2ControllerModel model =
            Switch2JoyConProfileInputMapper.IsStandaloneLeftMode(mode) ?
            Switch2ControllerModel.JoyCon2Left :
            Switch2ControllerModel.JoyCon2Right;
        Switch2InputSessionDescriptor descriptor = CreateCommonDescriptor(
            model, deviceGeneration, transportGeneration);
        Switch2CanonicalInputFrame canonical = CreateCommonFrame(descriptor,
            counter, buttons, physicalX, physicalY, timestamp,
            batteryVoltageMillivolts, magnetometer: magnetometer);
        Assert.IsTrue(Switch2JoyConProfileInputMapper.TryCreateStandalone(mode,
            descriptor, out var state));
        Assert.IsTrue(Switch2JoyConProfileInputMapper.TryMapStandalone(state,
            canonical, out _, out var mapped, out var failure),
            failure.ToString());
        return mapped;
    }

    private static Switch2JoyConProfileInputFrame MapJoined(ulong pairEpoch,
        ulong leftDeviceGeneration, ulong leftTransportGeneration,
        ulong rightDeviceGeneration, ulong rightTransportGeneration,
        uint leftButtons, uint rightButtons,
        ushort leftBatteryVoltageMillivolts = 0,
        ushort rightBatteryVoltageMillivolts = 0,
        Switch2Vector3Raw leftGyroscope = default,
        Switch2Vector3Raw rightGyroscope = default, uint counter = 1,
        long timestamp = 100, ushort leftIrDistance = 0,
        ushort rightIrDistance = 0, ushort leftIrRoughness = 0,
        ushort rightIrRoughness = 0)
    {
        Switch2InputSessionDescriptor leftDescriptor = CreateCommonDescriptor(
            Switch2ControllerModel.JoyCon2Left, leftDeviceGeneration,
            leftTransportGeneration);
        Switch2InputSessionDescriptor rightDescriptor = CreateCommonDescriptor(
            Switch2ControllerModel.JoyCon2Right, rightDeviceGeneration,
            rightTransportGeneration);
        Switch2CanonicalInputFrame left = CreateCommonFrame(leftDescriptor,
            counter, leftButtons, 0x800, 0x800, timestamp,
            leftBatteryVoltageMillivolts, leftGyroscope,
            irDistance: leftIrDistance, irRoughness: leftIrRoughness);
        Switch2CanonicalInputFrame right = CreateCommonFrame(rightDescriptor,
            counter, rightButtons, 0x800, 0x800, timestamp,
            rightBatteryVoltageMillivolts, rightGyroscope,
            irDistance: rightIrDistance, irRoughness: rightIrRoughness);
        var snapshot = new Switch2JoyConPairSnapshot(pairEpoch, left, right, 0);
        Assert.IsTrue(Switch2JoyConProfileInputMapper.TryCreateJoined(pairEpoch,
            leftDescriptor, rightDescriptor, out var state));
        Assert.IsTrue(Switch2JoyConProfileInputMapper.TryMapJoined(state,
            snapshot, out _, out var mapped, out var failure),
            failure.ToString());
        return mapped;
    }

    private static Switch2InputSessionDescriptor CreateCommonDescriptor(
        Switch2ControllerModel model, ulong deviceGeneration,
        ulong transportGeneration)
    {
        Assert.IsTrue(Switch2InputProtocolIdentity.TryCreateBluetoothLe(
            Switch2InputCodec.ServiceUuid,
            Switch2InputCodec.Common05CharacteristicUuid, InputProperties,
            model, out var identity));
        Assert.IsTrue(Switch2InputSessionDescriptor.TryCreate(identity,
            deviceGeneration, transportGeneration, 10_000_000,
            out var descriptor));
        return descriptor;
    }

    private static Switch2CanonicalInputFrame CreateCommonFrame(
        in Switch2InputSessionDescriptor descriptor, uint counter,
        uint buttons, ushort physicalX, ushort physicalY, long timestamp,
        ushort batteryVoltageMillivolts = 0,
        Switch2Vector3Raw gyroscope = default,
        Switch2Vector3Raw magnetometer = default,
        ushort irDistance = 0, ushort irRoughness = 0)
    {
        Switch2ControllerModel model = descriptor.Identity.Model;
        Assert.IsTrue(Switch2InputCalibrationSnapshot.TryCreateFallback(model,
            descriptor.DeviceGeneration, out var calibration));
        var session = new Switch2InputSession(descriptor, calibration);
        byte[] packet = BuildCommonPacket(counter, buttons,
            model == Switch2ControllerModel.JoyCon2Left ? physicalX :
                (ushort)0xBAD,
            model == Switch2ControllerModel.JoyCon2Left ? physicalY :
                (ushort)0xBAD,
            model == Switch2ControllerModel.JoyCon2Right ? physicalX :
                (ushort)0xBAD,
            model == Switch2ControllerModel.JoyCon2Right ? physicalY :
            (ushort)0xBAD, batteryVoltageMillivolts,
            gyroscope: gyroscope);
        WriteMagnetometer(packet, magnetometer);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(1 + 0x14, 2),
            irRoughness);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(1 + 0x16, 2),
            irDistance);
        Assert.IsTrue(session.TryProcess(descriptor, packet.AsSpan(1),
            timestamp, out var frame, out var failure), failure.ToString());
        return frame;
    }

    private static byte[] BuildCommonPacket(uint counter, uint buttons,
        ushort leftX, ushort leftY, ushort rightX, ushort rightY,
        ushort batteryVoltageMillivolts = 0, ushort batteryCurrentRaw = 0,
        byte batteryOpaque23Raw = 0,
        Switch2Vector3Raw accelerometer = default,
        Switch2Vector3Raw gyroscope = default)
    {
        var packet = new byte[Switch2InputCodec.UsbPacketLength];
        packet[0] = (byte)Switch2InputReportKind.Common05;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1, 4), counter);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(5, 4), buttons);
        PackStick(packet, 1 + 0x0A, leftX, leftY);
        PackStick(packet, 1 + 0x0D, rightX, rightY);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(1 + 0x1F, 2),
            batteryVoltageMillivolts);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(1 + 0x21, 2),
            batteryCurrentRaw);
        packet[1 + 0x23] = batteryOpaque23Raw;
        BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(1 + 0x30, 2),
            accelerometer.X);
        BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(1 + 0x32, 2),
            accelerometer.Y);
        BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(1 + 0x34, 2),
            accelerometer.Z);
        BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(1 + 0x36, 2),
            gyroscope.X);
        BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(1 + 0x38, 2),
            gyroscope.Y);
        BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(1 + 0x3A, 2),
            gyroscope.Z);
        return packet;
    }

    private static void PackStick(byte[] destination, int offset, ushort x,
        ushort y)
    {
        Assert.IsTrue(x <= 0x0FFF && y <= 0x0FFF);
        destination[offset] = (byte)x;
        destination[offset + 1] = (byte)(((x >> 8) & 0x0F) |
            ((y & 0x0F) << 4));
        destination[offset + 2] = (byte)(y >> 4);
    }

    private static void WriteMagnetometer(byte[] packet,
        in Switch2Vector3Raw magnetometer)
    {
        BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(1 + 0x19, 2),
            magnetometer.X);
        BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(1 + 0x1B, 2),
            magnetometer.Y);
        BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(1 + 0x1D, 2),
            magnetometer.Z);
    }

    private static Vector3 CalibrationSpherePoint(int index, int count)
    {
        const float goldenAngle = 2.39996322972865332f;
        float y = 1.0f - 2.0f * ((index + 0.5f) / count);
        float radial = MathF.Sqrt(MathF.Max(0.0f, 1.0f - y * y));
        float angle = goldenAngle * index;
        return new Vector3(MathF.Cos(angle) * radial, y,
            MathF.Sin(angle) * radial);
    }

    private sealed class InMemoryMagnetometerCalibrationStore :
        ISwitch2MagnetometerCalibrationStore
    {
        private readonly Dictionary<Switch2PersistentPeerId,
            Switch2MagnetometerCalibration> values = new();

        public bool TryLoad(Switch2PersistentPeerId peerId,
            out Switch2MagnetometerCalibration calibration) =>
            values.TryGetValue(peerId, out calibration);

        public bool TryStore(Switch2PersistentPeerId peerId,
            in Switch2MagnetometerCalibration calibration)
        {
            if (!peerId.IsValid || !calibration.IsValid)
            {
                return false;
            }
            values[peerId] = calibration;
            return true;
        }
    }

    private sealed class InMemoryGyroCalibrationStore :
        ISwitch2GyroCalibrationStore
    {
        private readonly Dictionary<Switch2PersistentPeerId,
            Switch2GyroCalibrationRecord> values = new();

        public bool TryLoad(Switch2PersistentPeerId peerId,
            out Switch2GyroCalibrationRecord calibration) =>
            values.TryGetValue(peerId, out calibration);

        public bool TryQueueStore(Switch2PersistentPeerId peerId,
            in Switch2GyroCalibrationRecord calibration)
        {
            if (!peerId.IsValid || !calibration.IsValid)
            {
                return false;
            }
            values[peerId] = calibration;
            return true;
        }
    }

    private sealed class TestOwner : IInputControllerRegistrationOwner
    {
        private readonly DS4Device expectedDevice;
        private readonly ulong expectedGeneration;
        private readonly InputControllerOwnershipKind kind;

        public TestOwner(DS4Device expectedDevice, ulong expectedGeneration,
            InputControllerOwnershipKind kind)
        {
            this.expectedDevice = expectedDevice;
            this.expectedGeneration = expectedGeneration;
            this.kind = kind;
        }

        public bool Authenticated { get; set; } = true;
        public bool ThrowOnKind { get; set; }
        public bool ThrowOnAuthenticate { get; set; }
        public bool ThrowOnStop { get; set; }
        public bool ThrowOnRemove { get; set; }
        public int LastTimeoutMilliseconds { get; private set; } = -1;

        public InputControllerOwnershipKind Kind => ThrowOnKind ?
            throw new InvalidOperationException() : kind;

        public bool Authenticates(DS4Device device, ulong generation)
        {
            if (ThrowOnAuthenticate)
            {
                throw new InvalidOperationException();
            }
            return Authenticated && ReferenceEquals(expectedDevice, device) &&
                expectedGeneration == generation;
        }

        public bool TryStopAndQuiesce(DS4Device device, ulong generation,
            int timeoutMilliseconds,
            out InputControllerOwnerOperationFailure failure)
        {
            if (ThrowOnStop)
            {
                throw new InvalidOperationException();
            }
            LastTimeoutMilliseconds = timeoutMilliseconds;
            bool accepted = Authenticates(device, generation);
            failure = accepted ? InputControllerOwnerOperationFailure.None :
                InputControllerOwnerOperationFailure.StopRejected;
            return accepted;
        }

        public bool TryRemove(DS4Device device, ulong generation,
            out InputControllerOwnerOperationFailure failure)
        {
            if (ThrowOnRemove)
            {
                throw new InvalidOperationException();
            }
            bool accepted = Authenticates(device, generation);
            failure = accepted ? InputControllerOwnerOperationFailure.None :
                InputControllerOwnerOperationFailure.RemoveRejected;
            return accepted;
        }
    }
}
