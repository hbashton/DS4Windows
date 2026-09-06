using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DS4Windows.Switch2;
using DS4Windows.Switch2.Verification;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Switch2UsbHardwareVerify.Tests;

[TestClass]
public sealed class VerificationPureTests
{
    private static readonly Guid SyntheticContainerA =
        new("11111111-2222-3333-4444-555555555555");
    private static readonly Guid SyntheticContainerB =
        new("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly Guid SyntheticInterfaceA =
        new("12345678-1234-1234-1234-1234567890ab");
    private static readonly Guid SyntheticInterfaceB =
        new("87654321-4321-4321-4321-ba0987654321");

    [TestMethod]
    public void FixedPlanIsBoundedAndValid()
    {
        Assert.IsTrue(VerificationPlan.TryValidate(out string failure),
            failure);
        Assert.AreEqual(14, VerificationPlan.HapticFrameCount);
        Assert.AreEqual(12, VerificationPlan.HapticCadenceMilliseconds);
        Assert.IsTrue(VerificationPlan.HapticFrameCount *
            VerificationPlan.HapticCadenceMilliseconds <= 250);
        Assert.IsTrue(VerificationPlan.BasisAmplitude > 0);
        Assert.IsTrue(VerificationPlan.BasisAmplitude <
            VerificationPlan.SdlClampAmplitudeCode);
        Assert.AreEqual((ushort)0, VerificationPlan.StopAmplitude);
        Assert.AreEqual(2, VerificationPlan.StopMaximumAttempts);
        Assert.IsTrue(VerificationPlan.HapticFrameCount +
            VerificationPlan.StopMaximumAttempts <= 16);
        Assert.IsTrue(VerificationPlan.SessionRevalidationTimeoutMilliseconds >
            0);
        Assert.IsTrue(VerificationPlan.ChannelDisposeTimeoutMilliseconds > 0);
        Assert.IsTrue(VerificationPlan.CommandOperationTimeoutMilliseconds >
            VerificationPlan.CommandTimeoutMilliseconds);
        Assert.IsTrue(VerificationPlan.CommandOperationTimeoutMilliseconds <
            VerificationPlan.LedCleanupTimeoutMilliseconds);
        Assert.AreEqual(15_000,
            VerificationPlan.InputCaptureTimeoutMilliseconds);
        Assert.IsFalse(VerificationPlan.LiveHapticMutationSafetyGateOpen,
            "Live nonzero haptics stay blocked until physical neutralization is provable after a noncooperative write.");
        Assert.IsTrue(VerificationPlan.InputCaptureTimeoutMilliseconds >=
            (VerificationPlan.InputWarmupReportCount +
                VerificationPlan.InputReportCount) * 4 * 2);
    }

    [TestMethod]
    public void HapticAndLedCleanupBudgetsAreIndependent()
    {
        using CancellationTokenSource haptic =
            CleanupBudgetFactory.CreateHaptic();
        using CancellationTokenSource led = CleanupBudgetFactory.CreateLed();

        Assert.AreNotSame(haptic, led);
        haptic.Cancel();
        Assert.IsTrue(haptic.IsCancellationRequested);
        Assert.IsFalse(led.IsCancellationRequested,
            "Haptic cleanup exhaustion must not starve LED AllOff.");
        Assert.IsTrue(VerificationPlan.LedCleanupTimeoutMilliseconds >
            2 * VerificationPlan.CommandTimeoutMilliseconds);
    }

    [TestMethod]
    public void HapticsCannotBeginUntilBothExclusiveLeasesExist()
    {
        var gate = new OutputLeaseGate();
        Assert.IsFalse(gate.CanBeginHaptics);
        Assert.ThrowsException<HardwareVerificationException>(
            gate.RequireBoth);

        gate.RegisterCommandLease();
        Assert.IsFalse(gate.CanBeginHaptics);
        Assert.ThrowsException<HardwareVerificationException>(
            gate.RequireBoth);

        gate.RegisterHidLease();
        Assert.IsTrue(gate.CanBeginHaptics);
        gate.RequireBoth();
    }

    [TestMethod]
    public void AmbiguousTimedOutBasisAndStopAttemptsNeverReuseCounters()
    {
        var sequence = new HapticAttemptSequence();
        var basisCounters = new List<byte>();
        for (int index = 0; index < VerificationPlan.HapticFrameCount; index++)
        {
            Assert.IsTrue(sequence.TryReserve(out byte counter));
            basisCounters.Add(counter);
        }

        // The final basis completion is intentionally never acknowledged:
        // this models a timeout after an ambiguously delivered HID write.
        Assert.IsTrue(sequence.TryReserve(out byte firstStop));
        // The first stop completion is also left ambiguous before retry.
        Assert.IsTrue(sequence.TryReserve(out byte secondStop));

        Assert.IsFalse(basisCounters.Contains(firstStop));
        Assert.IsFalse(basisCounters.Contains(secondStop));
        Assert.AreNotEqual(firstStop, secondStop);
        Assert.AreEqual(16, sequence.Reservations);
        Assert.IsFalse(sequence.TryReserve(out _),
            "The procedure fails closed instead of wrapping and reusing a value.");
    }

    [TestMethod]
    public void BasisAndStopReportsAreByteExactAndUseSameControls()
    {
        var basis = new byte[VerificationPlan.HidReportLength];
        var stop = new byte[VerificationPlan.HidReportLength];

        Assert.IsTrue(VerificationPlan.TryWriteHapticReport(0,
            VerificationPlan.BasisSubframe, basis));
        Assert.IsTrue(VerificationPlan.TryWriteHapticReport(0,
            VerificationPlan.StopSubframe, stop));

        Assert.AreEqual("02508701211110",
            Convert.ToHexString(basis.AsSpan(0, 7)));
        Assert.AreEqual("02508701201100",
            Convert.ToHexString(stop.AsSpan(0, 7)));
        Assert.IsTrue(basis.AsSpan(1, 16).SequenceEqual(
            basis.AsSpan(17, 16)));
        Assert.IsTrue(stop.AsSpan(1, 16).SequenceEqual(
            stop.AsSpan(17, 16)));
        Assert.IsTrue(basis.AsSpan(7, 10).ToArray().All(value => value == 0));
        Assert.IsTrue(stop.AsSpan(7, 10).ToArray().All(value => value == 0));
        Assert.IsTrue(basis.AsSpan(23).ToArray().All(value => value == 0));
        Assert.IsTrue(stop.AsSpan(23).ToArray().All(value => value == 0));
        Assert.AreEqual(VerificationPlan.BasisSubframe.Oscillator0ControlCode,
            VerificationPlan.StopSubframe.Oscillator0ControlCode);
        Assert.AreEqual(VerificationPlan.BasisSubframe.Oscillator1ControlCode,
            VerificationPlan.StopSubframe.Oscillator1ControlCode);
    }

    [TestMethod]
    public void ExactBulkTopologyIsAcceptedAndMutationsAreRejected()
    {
        PipeFact[] exact =
        [
            new(0x02, NativePipeType.Bulk, 64, 0),
            new(0x82, NativePipeType.Bulk, 64, 0),
        ];
        Assert.IsTrue(PipeTopologyValidator.TryValidate(1, 0, exact,
            out PipeFact bulkOut, out PipeFact bulkIn));
        Assert.AreEqual((byte)0x02, bulkOut.PipeId);
        Assert.AreEqual((byte)0x82, bulkIn.PipeId);

        Assert.IsFalse(PipeTopologyValidator.TryValidate(0, 0, exact,
            out _, out _));
        Assert.IsFalse(PipeTopologyValidator.TryValidate(1, 1, exact,
            out _, out _));
        Assert.IsFalse(PipeTopologyValidator.TryValidate(1, 0,
            [exact[0]], out _, out _));
        Assert.IsFalse(PipeTopologyValidator.TryValidate(1, 0,
            [exact[0], new(0x81, NativePipeType.Bulk, 64, 0)],
            out _, out _));
        Assert.IsFalse(PipeTopologyValidator.TryValidate(1, 0,
            [exact[0], new(0x82, NativePipeType.Interrupt, 64, 0)],
            out _, out _));
        Assert.IsFalse(PipeTopologyValidator.TryValidate(1, 0,
            [exact[0], new(0x82, NativePipeType.Bulk, 512, 0)],
            out _, out _));
        Assert.IsFalse(PipeTopologyValidator.TryValidate(1, 0,
            [exact[0], new(0x82, NativePipeType.Bulk, 64, 1)],
            out _, out _));
        Assert.IsTrue(ActiveAlternateSettingValidator.IsExactDefault(0));
        Assert.IsFalse(ActiveAlternateSettingValidator.IsExactDefault(1));
    }

    [TestMethod]
    public void ExactHidCapsAreAcceptedAndEveryFieldMutationIsRejected()
    {
        var exact = new HidCapsFact(0x01, 0x05, 64, 64, 0);
        Assert.IsTrue(HidCapsValidator.IsExact(exact));
        Assert.IsFalse(HidCapsValidator.IsExact(exact with
        {
            UsagePage = 0x02,
        }));
        Assert.IsFalse(HidCapsValidator.IsExact(exact with
        {
            Usage = 0x04,
        }));
        Assert.IsFalse(HidCapsValidator.IsExact(exact with
        {
            InputReportByteLength = 63,
        }));
        Assert.IsFalse(HidCapsValidator.IsExact(exact with
        {
            OutputReportByteLength = 63,
        }));
        Assert.IsFalse(HidCapsValidator.IsExact(exact with
        {
            FeatureReportByteLength = 1,
        }));
    }

    [TestMethod]
    public void SyntheticTestOnlyIdentityRulesFailClosed()
    {
        const string hidInstance =
            @"USB\VID_057E&PID_2069&MI_00\TEST_ONLY_INSTANCE";
        const string hidHardware =
            "USB\\VID_057E&PID_2069&REV_0201&MI_00\0";
        const string winUsbInstance =
            @"USB\VID_057E&PID_2069&MI_01\TEST_ONLY_INSTANCE";
        const string winUsbHardware =
            "USB\\VID_057E&PID_2069&REV_0201&MI_01\0";

        Assert.IsTrue(TargetIdentityRules.IsHidCollection(hidInstance,
            hidHardware, SyntheticContainerA, 0x057E, 0x2069, 0x0201));
        Assert.IsFalse(TargetIdentityRules.IsHidCollection(hidInstance,
            hidHardware, SyntheticContainerA, 0x057E, 0x2069, 0x0200));
        Assert.IsTrue(TargetIdentityRules.IsHidParent(hidInstance,
            hidHardware, "HidUsb", SyntheticContainerA,
            SyntheticContainerA));
        Assert.IsFalse(TargetIdentityRules.IsHidParent(hidInstance,
            hidHardware, "WinUSB", SyntheticContainerA,
            SyntheticContainerA));
        Assert.IsFalse(TargetIdentityRules.IsHidParent(hidInstance,
            hidHardware, "HidUsb", SyntheticContainerB,
            SyntheticContainerA));

        Assert.IsTrue(TargetIdentityRules.IsWinUsbNode(winUsbInstance,
            winUsbHardware, "WinUSB", SyntheticContainerA,
            SyntheticContainerA));
        Assert.IsFalse(TargetIdentityRules.IsWinUsbNode(winUsbInstance,
            winUsbHardware, "WinUSB", SyntheticContainerB,
            SyntheticContainerA));
        Assert.IsFalse(TargetIdentityRules.IsWinUsbNode(hidInstance,
            hidHardware, "WinUSB", SyntheticContainerA,
            SyntheticContainerA));
    }

    [TestMethod]
    public void SessionIdentityHasNoFalseGenerationAndRejectsEveryMutation()
    {
        var hid = new DeviceInterfaceToken(10,
            @"HID\TEST_ONLY", SyntheticContainerA,
            @"\\?\hid#test_only", "HidUsb");
        var winUsb = new DeviceInterfaceToken(11,
            @"USB\TEST_ONLY", SyntheticContainerA,
            @"\\?\usb#test_only", "WinUSB");
        var expected = new TargetDeviceSessionIdentity(hid, winUsb);
        var exact = new TargetDeviceSessionIdentity(hid with { },
            winUsb with { });

        Assert.IsTrue(expected.SameIdentity(exact));
        TargetSessionIdentityValidator.RequireSame(expected, exact);

        DeviceInterfaceToken[] changedHidTokens =
        [
            hid with { DevInst = 99 },
            hid with { InstanceId = @"HID\CHANGED" },
            hid with { ContainerId = SyntheticContainerB },
            hid with { InterfacePath = @"\\?\hid#changed" },
            hid with { Service = "Changed" },
        ];
        foreach (DeviceInterfaceToken changedHid in changedHidTokens)
        {
            HardwareVerificationException failure =
                Assert.ThrowsException<HardwareVerificationException>(() =>
                    TargetSessionIdentityValidator.RequireSame(expected,
                        expected with { Hid = changedHid }));
            Assert.AreEqual(VerificationFailureCode.HidIdentityChanged,
                failure.Code);
        }

        DeviceInterfaceToken[] changedWinUsbTokens =
        [
            winUsb with { DevInst = 12 },
            winUsb with { InstanceId = @"USB\CHANGED" },
            winUsb with { ContainerId = SyntheticContainerB },
            winUsb with { InterfacePath = @"\\?\usb#changed" },
            winUsb with { Service = "Changed" },
        ];
        foreach (DeviceInterfaceToken changedWinUsb in changedWinUsbTokens)
        {
            HardwareVerificationException failure =
                Assert.ThrowsException<HardwareVerificationException>(() =>
                    TargetSessionIdentityValidator.RequireSame(expected,
                        expected with { WinUsb = changedWinUsb }));
            Assert.AreEqual(VerificationFailureCode.WinUsbIdentityChanged,
                failure.Code);
        }
    }

    [TestMethod]
    public void InjectedSetupEnumerationFailuresCannotBecomeCompletion()
    {
        SetupEnumerationGate.RequireClassSet(true,
            VerificationFailureCode.WinUsbInterfaceClassSetOpenFailed);
        HardwareVerificationException openFailure =
            Assert.ThrowsException<HardwareVerificationException>(() =>
                SetupEnumerationGate.RequireClassSet(false,
                    VerificationFailureCode
                        .WinUsbInterfaceClassSetOpenFailed));
        Assert.AreEqual(
            VerificationFailureCode.WinUsbInterfaceClassSetOpenFailed,
            openFailure.Code);

        Assert.IsFalse(SetupEnumerationGate.IsComplete(true, 0,
            VerificationFailureCode.WinUsbInterfaceIterationFailed));
        Assert.IsTrue(SetupEnumerationGate.IsComplete(false,
            SetupEnumerationGate.ErrorNoMoreItems,
            VerificationFailureCode.WinUsbInterfaceIterationFailed));
        HardwareVerificationException iterationFailure =
            Assert.ThrowsException<HardwareVerificationException>(() =>
                SetupEnumerationGate.IsComplete(false, 5,
                    VerificationFailureCode
                        .WinUsbInterfaceIterationFailed));
        Assert.AreEqual(VerificationFailureCode.WinUsbInterfaceIterationFailed,
            iterationFailure.Code);
    }

    [TestMethod]
    public void UnrelatedHidIsSkippedButTargetClaimRequiresHardwareIds()
    {
        Assert.IsFalse(WindowsTargetDiscovery.IsPotentialHidTarget(
            @"HID\UNRELATED\TEST_ONLY", null));
        HardwareVerificationException missing =
            Assert.ThrowsException<HardwareVerificationException>(() =>
                WindowsTargetDiscovery.IsPotentialHidTarget(
                    @"USB\VID_057E&PID_2069&MI_00\TEST_ONLY", null));
        Assert.AreEqual(
            VerificationFailureCode.DeviceRegistryPropertyReadFailed,
            missing.Code);
        HardwareVerificationException inconsistent =
            Assert.ThrowsException<HardwareVerificationException>(() =>
                WindowsTargetDiscovery.IsPotentialHidTarget(
                    @"USB\VID_057E&PID_2069&MI_00\TEST_ONLY",
                    @"USB\VID_1234&PID_5678&MI_00"));
        Assert.AreEqual(
            VerificationFailureCode.DeviceRegistryPropertyReadFailed,
            inconsistent.Code);
        Assert.IsTrue(WindowsTargetDiscovery.IsPotentialHidTarget(
            @"USB\VID_057E&PID_2069&MI_00\TEST_ONLY",
            @"USB\VID_057E&PID_2069&REV_0201&MI_00"));
        Assert.IsFalse(WindowsTargetDiscovery.IsPotentialHidTarget(
            @"USB\VID_057E&PID_2069&MI_000\TEST_ONLY", null),
            "The interface marker is an exact component, not a substring.");
    }

    [TestMethod]
    public void TwoTargetClaimsCannotBecomeUniqueWhenOnePropertyReadFails()
    {
        int admitted = 0;
        if (WindowsTargetDiscovery.IsPotentialHidTarget(
                @"USB\VID_057E&PID_2069&MI_00\FIRST",
                @"USB\VID_057E&PID_2069&REV_0201&MI_00"))
        {
            admitted++;
        }

        HardwareVerificationException failure =
            Assert.ThrowsException<HardwareVerificationException>(() =>
                WindowsTargetDiscovery.IsPotentialHidTarget(
                    @"USB\VID_057E&PID_2069&MI_00\SECOND", null));
        Assert.AreEqual(1, admitted);
        Assert.AreEqual(
            VerificationFailureCode.DeviceRegistryPropertyReadFailed,
            failure.Code,
            "The second unreadable target must abort, not leave a false unique count.");
    }

    [TestMethod]
    public void TargetRegistryStringsRequireExactTypeAndTermination()
    {
        byte[] exactMulti = Encoding.Unicode.GetBytes(
            @"USB\VID_057E&PID_2069&REV_0201&MI_00" + "\0\0");
        Assert.IsTrue(DeviceRegistryStringValue.TryDecode(7, 7, exactMulti,
            out string decoded));
        Assert.AreEqual(@"USB\VID_057E&PID_2069&REV_0201&MI_00", decoded);
        Assert.IsFalse(DeviceRegistryStringValue.TryDecode(1, 7, exactMulti,
            out _));
        Assert.IsFalse(DeviceRegistryStringValue.TryDecode(7, 7,
            exactMulti.AsSpan(0, exactMulti.Length - sizeof(char)), out _));
        Assert.IsFalse(DeviceRegistryStringValue.TryDecode(7, 7,
            Encoding.Unicode.GetBytes("first\0\0second\0\0"), out _));
    }

    [TestMethod]
    public void DeviceInterfaceGuidRegistryValueAcceptsStrictMultipleGuidList()
    {
        byte[] bytes = RegistryMultiString(SyntheticInterfaceA,
            SyntheticInterfaceB);
        Assert.IsTrue(DeviceInterfaceGuidRegistryValue.TryParse(7, bytes,
            out Guid[] parsed));
        CollectionAssert.AreEqual(new[]
        {
            SyntheticInterfaceA,
            SyntheticInterfaceB,
        }, parsed);
    }

    [TestMethod]
    public void DeviceInterfaceGuidRegistryValueAcceptsStrictSingleGuid()
    {
        byte[] bytes = Encoding.Unicode.GetBytes(
            SyntheticInterfaceA.ToString("B") + "\0");
        Assert.IsTrue(DeviceInterfaceGuidRegistryValue.TryParseSingle(1,
            bytes, out Guid parsed));
        Assert.AreEqual(SyntheticInterfaceA, parsed);

        Assert.IsFalse(DeviceInterfaceGuidRegistryValue.TryParseSingle(7,
            bytes, out _));
        Assert.IsFalse(DeviceInterfaceGuidRegistryValue.TryParseSingle(1,
            Encoding.Unicode.GetBytes(
                SyntheticInterfaceA.ToString("B") + "\0extra\0"), out _));
        Assert.IsFalse(DeviceInterfaceGuidRegistryValue.TryParseSingle(1,
            Encoding.Unicode.GetBytes("not-a-guid\0"), out _));
    }

    [TestMethod]
    public void OnlyActiveNonRemovedInterfaceFlagsAreAdmitted()
    {
        Assert.IsTrue(DeviceInterfaceFlags.IsActive(0x00000001));
        Assert.IsTrue(DeviceInterfaceFlags.IsActive(0x00000003));
        Assert.IsFalse(DeviceInterfaceFlags.IsActive(0));
        Assert.IsFalse(DeviceInterfaceFlags.IsActive(0x00000004));
        Assert.IsFalse(DeviceInterfaceFlags.IsActive(0x00000005));
    }

    [TestMethod]
    public void DeviceInterfaceGuidRegistryValueRejectsMissingWrongTypeAndMalformed()
    {
        byte[] valid = RegistryMultiString(SyntheticInterfaceA);
        Assert.IsFalse(DeviceInterfaceGuidRegistryValue.TryParse(1, valid,
            out _), "REG_SZ must not be accepted as REG_MULTI_SZ.");
        Assert.IsFalse(DeviceInterfaceGuidRegistryValue.TryParse(7, [],
            out _), "A missing value is invalid.");
        Assert.IsFalse(DeviceInterfaceGuidRegistryValue.TryParse(7,
            Encoding.Unicode.GetBytes($"{{{SyntheticInterfaceA}}}\0"),
            out _), "The required double terminator is missing.");
        Assert.IsFalse(DeviceInterfaceGuidRegistryValue.TryParse(7,
            Encoding.Unicode.GetBytes("not-a-guid\0\0"), out _));
        Assert.IsFalse(DeviceInterfaceGuidRegistryValue.TryParse(7,
            RegistryMultiString(SyntheticInterfaceA, SyntheticInterfaceA),
            out _), "Duplicate interface GUIDs fail closed.");
    }

    [TestMethod]
    public void InputRateMathUsesOnlyCompletionCadence()
    {
        InputRateObservation observation =
            InputRateObservation.FromCompletionTicks([0, 4, 8, 12], 1000);
        Assert.AreEqual(4, observation.ExactReports);
        Assert.AreEqual(250.0, observation.ReportsPerSecond, 0.0001);
        Assert.AreEqual(4.0, observation.MeanIntervalMilliseconds, 0.0001);
        Assert.AreEqual(4.0, observation.P50IntervalMilliseconds, 0.0001);
        Assert.AreEqual(4.0, observation.P95IntervalMilliseconds, 0.0001);
        Assert.AreEqual(4.0, observation.P99IntervalMilliseconds, 0.0001);
    }

    [TestMethod]
    public void Common05Uint32CounterAcceptsForwardPlusFourAndWrap()
    {
        uint[] counters = [0xFFFFFFF8, 0xFFFFFFFC, 0, 4, 8];
        Assert.IsTrue(Common05CounterObservation.TryAnalyze(counters,
            out Common05CounterObservation observation));
        Assert.AreEqual(4, observation.ForwardMovements);
        Assert.AreEqual((uint)4, observation.MinimumDelta);
        Assert.AreEqual((uint)4, observation.MaximumDelta);
        Assert.AreEqual(4, observation.PlusFourMovements);
        Assert.IsTrue(observation.WrapObserved);
    }

    [TestMethod]
    public void Common05Uint32CounterRejectsDuplicateAndBackwardMovement()
    {
        Assert.IsFalse(Common05CounterObservation.TryAnalyze([4u, 4u],
            out _));
        Assert.IsFalse(Common05CounterObservation.TryAnalyze([8u, 4u],
            out _));
        Assert.IsFalse(Common05CounterObservation.TryAnalyze([1u], out _));
    }

    [TestMethod]
    public void BacklogLikeTimingIsReportedOnlyAsHostCompletionCadence()
    {
        InputRateObservation observation =
            InputRateObservation.FromCompletionTicks([0, 1, 2, 3], 1000);
        Assert.AreEqual(1000.0, observation.ReportsPerSecond, 0.0001);
        Assert.AreEqual(1.0, observation.MeanIntervalMilliseconds, 0.0001);

        long[] queuedTail = Enumerable.Range(0,
            VerificationPlan.InputWarmupLiveTailIntervals + 1)
            .Select(index => (long)index).ToArray();
        Assert.IsFalse(WarmupDrainValidator.HasLiveTail(queuedTail, 10_000));

        long[] liveTail = Enumerable.Range(0,
            VerificationPlan.InputWarmupLiveTailIntervals + 1)
            .Select(index => (long)index * 40).ToArray();
        Assert.IsTrue(WarmupDrainValidator.HasLiveTail(liveTail, 10_000));
    }

    [TestMethod]
    public void AmbiguousCommandFailureRequiresFreshCleanupChannel()
    {
        var state = new CommandTransactionState();
        Assert.IsTrue(state.TryBegin());
        state.MarkFaulted(); // injected ambiguous write/read completion

        Assert.AreEqual(CommandTransactionPhase.Faulted, state.Phase);
        Assert.IsFalse(state.TryBegin(),
            "A faulted session can never issue AllOff.");
        Assert.IsTrue(CommandCleanupPolicy.RequiresFreshChannel(true,
            state.Phase));

        var reopened = new CommandTransactionState();
        Assert.IsTrue(reopened.TryBegin(),
            "Cleanup uses a distinct newly opened session.");
        reopened.CompleteSuccess();
        Assert.AreEqual(CommandTransactionPhase.Ready, reopened.Phase);
    }

    [TestMethod]
    public void AmbiguousHidWriteRequiresFreshCleanupChannel()
    {
        var session = new HidOutputSessionState();
        Assert.IsFalse(HapticCleanupPolicy.RequiresFreshChannel(true,
            session.IsFaulted));

        session.MarkAmbiguousFailure(); // injected timed-out basis write

        Assert.IsTrue(session.IsFaulted);
        Assert.IsTrue(HapticCleanupPolicy.RequiresFreshChannel(true,
            session.IsFaulted));
        Assert.IsFalse(HapticCleanupPolicy.RequiresFreshChannel(false,
            session.IsFaulted));
    }

    [TestMethod]
    public async Task InjectedAsyncHidWriteCancellationPoisonsActualWrapper()
    {
        var sequence = new HapticAttemptSequence();
        Assert.IsTrue(sequence.TryReserve(out byte basisCounter));
        var report = new byte[VerificationPlan.HidReportLength];
        Assert.IsTrue(VerificationPlan.TryWriteHapticReport(basisCounter,
            VerificationPlan.BasisSubframe, report));

        var attempt = new HidOutputAttempt((_, token) =>
            ValueTask.FromCanceled(token));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        HardwareVerificationException failure =
            await Assert.ThrowsExceptionAsync<HardwareVerificationException>(
                () => attempt.WriteReportAsync(report, cancellation.Token));
        Assert.AreEqual(VerificationFailureCode.Cancelled, failure.Code);
        Assert.IsTrue(attempt.IsFaulted);
        Assert.IsTrue(HapticCleanupPolicy.RequiresFreshChannel(true,
            attempt.IsFaulted));

        Assert.IsTrue(sequence.TryReserve(out byte stopCounter));
        Assert.AreNotEqual(basisCounter, stopCounter,
            "The cleanup report reserves a fresh counter after ambiguity.");
    }

    [TestMethod]
    public async Task CleanupOrderStartsIndependentHapticBudgetAfterLedArm()
    {
        var order = new List<string>();
        var ledBudget = new CancellationTokenSource();
        var hapticBudget = new CancellationTokenSource();
        bool hapticFactoryCalled = false;

        await CleanupOrderCoordinator.RunLedThenHapticAsync(
            token =>
            {
                order.Add("led");
                Assert.AreEqual(ledBudget.Token, token);
                Assert.IsFalse(hapticFactoryCalled);
                return Task.CompletedTask;
            }, () => ledBudget,
            token =>
            {
                order.Add("haptic");
                Assert.AreEqual(hapticBudget.Token, token);
                Assert.IsTrue(hapticFactoryCalled);
                return Task.CompletedTask;
            }, () =>
            {
                hapticFactoryCalled = true;
                return hapticBudget;
            });

        CollectionAssert.AreEqual(new[] { "led", "haptic" }, order);
    }

    [TestMethod]
    public async Task ExpiredLedBudgetCannotSuppressHapticCleanupArm()
    {
        var ledBudget = new CancellationTokenSource();
        var hapticBudget = new CancellationTokenSource();
        bool hapticAttempted = false;

        await CleanupOrderCoordinator.RunLedThenHapticAsync(
            async token =>
            {
                ledBudget.Cancel();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }, () => ledBudget,
            token =>
            {
                Assert.IsFalse(token.IsCancellationRequested);
                hapticAttempted = true;
                return Task.CompletedTask;
            }, () => hapticBudget);

        Assert.IsTrue(hapticAttempted);
    }

    [TestMethod]
    public async Task NoncooperativeLedCallbackYieldsWithoutReturningOwnership()
    {
        var ledStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lateLedReleased = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int lateReleaseCount = 0;
        bool hapticAttempted = false;
        bool lateTokenRegistrationSucceeded = false;

        try
        {
            CleanupOrderResult outcome =
                await CleanupOrderCoordinator.RunLedThenHapticAsync(
                    async token =>
                    {
                        ledStarted.SetResult();
                        await releaseLed.Task;
                        using CancellationTokenRegistration registration =
                            token.Register(static () => { });
                        lateTokenRegistrationSucceeded = true;
                    }, () => new CancellationTokenSource(30),
                    () =>
                    {
                        Interlocked.Increment(ref lateReleaseCount);
                        lateLedReleased.TrySetResult();
                        return Task.CompletedTask;
                    },
                    _ =>
                    {
                        hapticAttempted = true;
                        return Task.CompletedTask;
                    }, () => new CancellationTokenSource(500),
                    () => Task.CompletedTask)
                    .WaitAsync(TimeSpan.FromSeconds(1));

            await ledStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.IsFalse(outcome.LedArmFinished);
            Assert.IsFalse(outcome.CommandOwnershipReturned,
                "A still-running LED callback owns its command channel; final dispose or reopen is forbidden.");
            Assert.IsTrue(outcome.HapticArmFinished);
            Assert.IsTrue(outcome.HidOwnershipReturned);
            Assert.IsTrue(hapticAttempted,
                "A noncooperative LED callback must not suppress the independent haptic arm.");
            Assert.IsFalse(lateLedReleased.Task.IsCompleted,
                "The late owner cannot dispose while the callback still owns the channel.");
            releaseLed.TrySetResult();
            await lateLedReleased.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.AreEqual(1, lateReleaseCount);
            Assert.IsTrue(lateTokenRegistrationSucceeded,
                "The abandoned callback retains its CTS until it returns.");
        }
        finally
        {
            releaseLed.TrySetResult();
        }
    }

    [TestMethod]
    public async Task WholeInputDeadlineStartsCleanupAfterSlowValidReports()
    {
        int validReports = 0;
        var releaseCapture = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lateHidReleased = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int lateReleaseCount = 0;
        try
        {
            HardwareVerificationException failure =
                await Assert.ThrowsExceptionAsync<HardwareVerificationException>(
                    () => InputCaptureDeadline.RunAsync(async _ =>
                    {
                        while (!releaseCapture.Task.IsCompleted)
                        {
                            await Task.Delay(5);
                            Interlocked.Increment(ref validReports);
                        }
                        return validReports;
                    }, () =>
                    {
                        Interlocked.Increment(ref lateReleaseCount);
                        lateHidReleased.TrySetResult();
                        return Task.CompletedTask;
                    }, CancellationToken.None,
                    TimeSpan.FromMilliseconds(60)));

            bool cleanupStarted = false;
            CleanupOrderResult cleanup =
                await CleanupOrderCoordinator.RunLedThenHapticAsync(
                    _ =>
                    {
                        cleanupStarted = true;
                        return Task.CompletedTask;
                    }, () => new CancellationTokenSource(500),
                    _ => Task.CompletedTask,
                    () => new CancellationTokenSource(500));

            Assert.AreEqual(
                VerificationFailureCode.InputCapturePhaseTimedOut,
                failure.Code);
            Assert.IsFalse(failure.ResourceOwnershipReturned,
                "A noncooperative capture retains its HID channel after the hard deadline.");
            Assert.IsTrue(validReports > 0,
                "The injection models individually valid but cumulatively slow reports.");
            Assert.IsTrue(cleanupStarted);
            Assert.IsTrue(cleanup.CommandOwnershipReturned);
            Assert.IsFalse(lateHidReleased.Task.IsCompleted,
                "The late owner must wait for capture completion before disposal.");
            releaseCapture.TrySetResult();
            await lateHidReleased.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.AreEqual(1, lateReleaseCount);
        }
        finally
        {
            releaseCapture.TrySetResult();
        }
    }

    [TestMethod]
    public async Task UserCancellationDuringInputRemainsCancelled()
    {
        using var userCancellation = new CancellationTokenSource(30);
        HardwareVerificationException failure =
            await Assert.ThrowsExceptionAsync<HardwareVerificationException>(
                () => InputCaptureDeadline.RunAsync<int>(async token =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return 0;
                }, () => Task.CompletedTask, userCancellation.Token,
                TimeSpan.FromSeconds(1)));
        Assert.AreEqual(VerificationFailureCode.Cancelled, failure.Code);
    }

    [TestMethod]
    public async Task NoncooperativeHapticPhaseAbandonsUntilLateRelease()
    {
        var releaseWrite = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lateReleased = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            HardwareVerificationException failure =
                await Assert.ThrowsExceptionAsync<HardwareVerificationException>(
                    () => HapticPhaseDeadline.RunAsync(
                        _ => releaseWrite.Task,
                        () =>
                        {
                            lateReleased.TrySetResult();
                            return Task.CompletedTask;
                        }, CancellationToken.None));
            Assert.AreEqual(VerificationFailureCode.HapticPhaseTimedOut,
                failure.Code);
            Assert.AreEqual(AbandonedResourceOwnership.HapticOutputHid,
                failure.AbandonedResource);
            Assert.IsFalse(lateReleased.Task.IsCompleted);

            releaseWrite.TrySetResult();
            await lateReleased.Task.WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            releaseWrite.TrySetResult();
        }
    }

    [TestMethod]
    public async Task NoncooperativeCommandOperationTransfersOneLateOwner()
    {
        var releaseOperation = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lateReleased = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int lateReleaseCount = 0;

        try
        {
            HardwareVerificationException failure =
                await Assert.ThrowsExceptionAsync<HardwareVerificationException>(
                    () => CommandOperationDeadline.RunAsync(
                        _ => releaseOperation.Task,
                        () =>
                        {
                            Interlocked.Increment(ref lateReleaseCount);
                            lateReleased.TrySetResult();
                            return Task.CompletedTask;
                        }, CancellationToken.None,
                        TimeSpan.FromMilliseconds(40)));

            Assert.AreEqual(
                VerificationFailureCode.CommandOperationTimedOut,
                failure.Code);
            Assert.AreEqual(AbandonedResourceOwnership.CommandOutputWinUsb,
                failure.AbandonedResource);
            Assert.IsFalse(failure.ResourceOwnershipReturned);
            Assert.IsFalse(lateReleased.Task.IsCompleted,
                "The command channel remains owned by the blocked operation.");

            releaseOperation.TrySetResult(7);
            await lateReleased.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.AreEqual(1, lateReleaseCount);
        }
        finally
        {
            releaseOperation.TrySetResult(7);
        }
    }

    [TestMethod]
    public async Task SynchronousNativeCommandStallCannotDefeatHardDeadline()
    {
        var operationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseOperation = new ManualResetEventSlim(false);
        var lateReleased = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int lateReleaseCount = 0;

        try
        {
            Task<int> run = CommandOperationDeadline.RunAsync(_ =>
            {
                operationStarted.TrySetResult();
                releaseOperation.Wait();
                return Task.FromResult(7);
            }, () =>
            {
                Interlocked.Increment(ref lateReleaseCount);
                lateReleased.TrySetResult();
                return Task.CompletedTask;
            }, CancellationToken.None, TimeSpan.FromMilliseconds(40));

            await operationStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            HardwareVerificationException failure =
                await Assert.ThrowsExceptionAsync<HardwareVerificationException>(
                    () => run.WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.AreEqual(
                VerificationFailureCode.CommandOperationTimedOut,
                failure.Code);
            Assert.AreEqual(AbandonedResourceOwnership.CommandOutputWinUsb,
                failure.AbandonedResource);
            Assert.IsFalse(failure.ResourceOwnershipReturned);
            Assert.IsFalse(lateReleased.Task.IsCompleted,
                "The late disposer cannot race a still-blocked native call.");
        }
        finally
        {
            releaseOperation.Set();
        }

        await lateReleased.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.AreEqual(1, lateReleaseCount);
    }

    [TestMethod]
    public async Task BlockingCommandCancellationCallbackCannotDefeatDeadline()
    {
        var operationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOperation = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lateReleased = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCallback = new ManualResetEventSlim(false);
        int lateReleaseCount = 0;

        try
        {
            Task<int> run = CommandOperationDeadline.RunAsync(async token =>
            {
                using CancellationTokenRegistration registration =
                    token.Register(() =>
                    {
                        callbackStarted.TrySetResult();
                        releaseCallback.Wait();
                    });
                operationStarted.TrySetResult();
                return await releaseOperation.Task;
            }, () =>
            {
                Interlocked.Increment(ref lateReleaseCount);
                lateReleased.TrySetResult();
                return Task.CompletedTask;
            }, CancellationToken.None, TimeSpan.FromMilliseconds(40));

            await operationStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            HardwareVerificationException failure =
                await Assert.ThrowsExceptionAsync<HardwareVerificationException>(
                    () => run.WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.AreEqual(
                VerificationFailureCode.CommandOperationTimedOut,
                failure.Code);
            Assert.AreEqual(AbandonedResourceOwnership.CommandOutputWinUsb,
                failure.AbandonedResource);
            await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.IsFalse(lateReleased.Task.IsCompleted);
        }
        finally
        {
            releaseCallback.Set();
            releaseOperation.TrySetResult(1);
        }

        await lateReleased.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.AreEqual(1, lateReleaseCount);
    }

    [TestMethod]
    public async Task AbandonedAllOffCommandCannotRaceOuterArmDisposal()
    {
        var releaseOperation = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var innerLateReleased = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        object? commandLease = new();
        int innerReleaseCount = 0;
        int outerReleaseCount = 0;

        try
        {
            CleanupOrderResult outcome =
                await CleanupOrderCoordinator.RunLedThenHapticAsync(
                    async _ =>
                    {
                        object captured = commandLease!;
                        try
                        {
                            await CommandOperationDeadline.RunAsync(
                                _ => releaseOperation.Task,
                                () =>
                                {
                                    Interlocked.Increment(
                                        ref innerReleaseCount);
                                    innerLateReleased.TrySetResult();
                                    return Task.CompletedTask;
                                }, CancellationToken.None,
                                TimeSpan.FromMilliseconds(40));
                        }
                        catch (HardwareVerificationException exception) when (
                            !exception.ResourceOwnershipReturned)
                        {
                            Assert.IsNotNull(captured);
                            commandLease = null;
                        }
                    }, () => new CancellationTokenSource(500),
                    () =>
                    {
                        if (commandLease is not null)
                        {
                            Interlocked.Increment(ref outerReleaseCount);
                            commandLease = null;
                        }
                        return Task.CompletedTask;
                    }, _ => Task.CompletedTask,
                    () => new CancellationTokenSource(500),
                    () => Task.CompletedTask);

            Assert.IsTrue(outcome.CommandOwnershipReturned,
                "The inner handoff returned a null lease to the outer arm.");
            Assert.IsNull(commandLease);
            Assert.AreEqual(0, outerReleaseCount,
                "The outer arm must never dispose the inner late owner's lease.");
            Assert.AreEqual(0, innerReleaseCount);

            releaseOperation.TrySetResult(1);
            await innerLateReleased.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.AreEqual(1, innerReleaseCount);
            Assert.AreEqual(0, outerReleaseCount);
        }
        finally
        {
            releaseOperation.TrySetResult(1);
        }
    }

    [TestMethod]
    public async Task CompletionDeadlineRaceHasExactlyOneHidOwner()
    {
        var complete = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lateReleased = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int lateReleaseCount = 0;

        Task<int> run = InputCaptureDeadline.RunAsync(_ => complete.Task,
            () =>
            {
                Interlocked.Increment(ref lateReleaseCount);
                lateReleased.TrySetResult();
                return Task.CompletedTask;
            }, CancellationToken.None, TimeSpan.FromMilliseconds(40));
        await Task.Delay(40);
        complete.TrySetResult(7);

        try
        {
            Assert.AreEqual(7, await run);
            Assert.AreEqual(0, lateReleaseCount,
                "Main won completion; the late disposer must not run.");
        }
        catch (HardwareVerificationException failure)
        {
            Assert.AreEqual(
                VerificationFailureCode.InputCapturePhaseTimedOut,
                failure.Code);
            Assert.AreEqual(AbandonedResourceOwnership.InputCaptureHid,
                failure.AbandonedResource);
            await lateReleased.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.AreEqual(1, lateReleaseCount,
                "The late owner won and must be the sole disposer.");
        }
    }

    [TestMethod]
    public async Task TimedOutOldWriterReleaseNeverOpensReplacementWriter()
    {
        var releaseStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var budget = new CancellationTokenSource();
        int replacementOpenCount = 0;

        Task<BoundedReplacementResult<object>> pending =
            BoundedNativeOperation.TryReplaceAsync(new object(),
                async _ =>
                {
                    releaseStarted.SetResult();
                    await allowRelease.Task;
                },
                () =>
                {
                    Interlocked.Increment(ref replacementOpenCount);
                    return new object();
                }, _ => Task.CompletedTask, budget.Token);

        await releaseStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        budget.Cancel();
        BoundedReplacementResult<object> result =
            await pending.WaitAsync(TimeSpan.FromSeconds(1));
        allowRelease.SetResult();

        Assert.AreEqual(BoundedOperationStatus.TimedOut,
            result.ReleaseStatus);
        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(0, replacementOpenCount,
            "No second output writer may open before old-writer disposal completes.");
    }

    [TestMethod]
    public async Task TimedOutWriterOpenDisposesAnyLateAcquiredLease()
    {
        var acquireStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseAcquire = new ManualResetEventSlim(false);
        var lateLeaseReleased = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var budget = new CancellationTokenSource();

        Task<BoundedAcquireResult<object>> pending =
            BoundedNativeOperation.TryAcquireAsync(() =>
            {
                acquireStarted.SetResult();
                releaseAcquire.Wait();
                return new object();
            }, _ =>
            {
                lateLeaseReleased.SetResult();
                return Task.CompletedTask;
            }, budget.Token);

        try
        {
            await acquireStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            budget.Cancel();
            BoundedAcquireResult<object> result =
                await pending.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.AreEqual(BoundedOperationStatus.TimedOut, result.Status);
            Assert.IsNull(result.Resource);
            Assert.IsTrue(result.LateReleaseUnconfirmed);
        }
        finally
        {
            releaseAcquire.Set();
        }

        await lateLeaseReleased.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [TestMethod]
    public async Task HungPostMutationRevalidationYieldsToBothCleanupArms()
    {
        var revalidationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseRevalidation = new ManualResetEventSlim(false);
        using var revalidationBudget = new CancellationTokenSource();

        Task<BoundedOperationStatus> pendingRevalidation =
            BoundedNativeOperation.TryRunAsync(() =>
            {
                revalidationStarted.SetResult();
                releaseRevalidation.Wait();
            }, revalidationBudget.Token);

        try
        {
            await revalidationStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            revalidationBudget.Cancel();
            Assert.AreEqual(BoundedOperationStatus.TimedOut,
                await pendingRevalidation.WaitAsync(TimeSpan.FromSeconds(1)));

            var order = new List<string>();
            await CleanupOrderCoordinator.RunLedThenHapticAsync(
                _ =>
                {
                    order.Add("led");
                    return Task.CompletedTask;
                }, () => new CancellationTokenSource(),
                _ =>
                {
                    order.Add("haptic");
                    return Task.CompletedTask;
                }, () => new CancellationTokenSource());
            CollectionAssert.AreEqual(new[] { "led", "haptic" }, order);
        }
        finally
        {
            releaseRevalidation.Set();
        }
    }

    [TestMethod]
    public void StalePlayerOneResponseCannotMatchAllOffResponseTuple()
    {
        byte[] stalePlayerOneAck =
            Convert.FromHexString("0901000110780000");
        Assert.IsFalse(Switch2UsbCommandCodec.TryValidatePlayerLedResponse(
            stalePlayerOneAck, Switch2PlayerLedCommand.AllOff, out _));

        byte[] exactAllOffAck =
            Convert.FromHexString("0901000610780000");
        Assert.IsTrue(Switch2UsbCommandCodec.TryValidatePlayerLedResponse(
            exactAllOffAck, Switch2PlayerLedCommand.AllOff,
            out Switch2UsbCommandResponseStyle originalStyle, out _));
        Assert.AreEqual(
            Switch2UsbCommandResponseStyle.OriginalCapture10_78,
            originalStyle);

        byte[] initializedAllOffAck =
            Convert.FromHexString("0901000600F80000");
        Assert.IsTrue(Switch2UsbCommandCodec.TryValidatePlayerLedResponse(
            initializedAllOffAck, Switch2PlayerLedCommand.AllOff,
            out Switch2UsbCommandResponseStyle initializedStyle, out _));
        Assert.AreEqual(
            Switch2UsbCommandResponseStyle.InitializedHardware00_F8,
            initializedStyle);
        initializedAllOffAck[5] = 0x78;
        Assert.IsFalse(Switch2UsbCommandCodec.TryValidatePlayerLedResponse(
            initializedAllOffAck, Switch2PlayerLedCommand.AllOff, out _),
            "Header-style bytes are admitted only as an exact pair.");

        byte[] packetWithTrailingByte =
            Convert.FromHexString("0901000610780000FF");
        Assert.IsFalse(CommandResponseAdmission.TryAdmit(
            packetWithTrailingByte, (uint)packetWithTrailingByte.Length,
            Switch2UsbCommandCodec.PlayerLedResponseLength, out _));
        Assert.IsTrue(CommandResponseAdmission.TryAdmit(exactAllOffAck,
            (uint)exactAllOffAck.Length,
            Switch2UsbCommandCodec.PlayerLedResponseLength,
            out byte[] admitted));
        CollectionAssert.AreEqual(exactAllOffAck, admitted);
    }

    [TestMethod]
    public void VolatileUsbStartupCodecIsExactClosedAndTransportSpecific()
    {
        Span<byte> request = stackalloc byte[
            Switch2UsbCommandCodec.InitializationRequestLength];
        Assert.IsTrue(Switch2UsbCommandCodec.TryWriteInitializationRequest(
            Switch2UsbInitializationStep.EnableUsbHidReports, request,
            out var failure));
        Assert.IsTrue(request.SequenceEqual(Convert.FromHexString(
            "039100030004000001000000")));
        Assert.IsTrue(Switch2UsbCommandCodec
            .TryValidateInitializationResponse(Convert.FromHexString(
                "0301000300F8000001000000"),
                Switch2UsbInitializationStep.EnableUsbHidReports,
                out failure));
        Assert.AreEqual(Switch2UsbCommandFailure.None, failure);

        Assert.IsTrue(Switch2UsbCommandCodec.TryWriteInitializationRequest(
            Switch2UsbInitializationStep.SelectCommonInputReport, request,
            out failure));
        Assert.IsTrue(request.SequenceEqual(Convert.FromHexString(
            "0391000A0004000005000000")));
        Assert.IsTrue(Switch2UsbCommandCodec
            .TryValidateInitializationResponse(Convert.FromHexString(
                "0301000A00F80000"),
                Switch2UsbInitializationStep.SelectCommonInputReport,
                out failure));

        byte[] wrongTransport = Convert.FromHexString(
            "0301010A00F80000");
        Assert.IsFalse(Switch2UsbCommandCodec
            .TryValidateInitializationResponse(wrongTransport,
                Switch2UsbInitializationStep.SelectCommonInputReport,
                out failure));
        Assert.AreEqual(Switch2UsbCommandFailure.InvalidTransport, failure);

        request.Fill(0xCC);
        Assert.IsFalse(Switch2UsbCommandCodec.TryWriteInitializationRequest(
            (Switch2UsbInitializationStep)0x0D, request, out failure));
        Assert.AreEqual(Switch2UsbCommandFailure.InvalidSubcommand, failure);
        Assert.IsTrue(request.ToArray().All(value => value == 0xCC));
    }

    [TestMethod]
    public void FeatureRequestsAdmitOnlySetEnableAndMask27()
    {
        Span<byte> request = stackalloc byte[
            Switch2UsbCommandCodec.FeatureRequestLength];
        const Switch2UsbFeatureMask Mask =
            Switch2UsbFeatureMask.ButtonsSticksImuAndRumble;

        Assert.IsTrue(Switch2UsbCommandCodec.TryWriteFeatureRequest(
            Switch2UsbFeatureStep.SetFeatureMask, Mask, request,
            out var failure));
        Assert.IsTrue(request.SequenceEqual(Convert.FromHexString(
            "0C9100020004000027000000")));
        Assert.IsTrue(Switch2UsbCommandCodec.TryValidateFeatureRequest(request,
            Switch2UsbFeatureStep.SetFeatureMask, Mask, out failure));

        Assert.IsTrue(Switch2UsbCommandCodec.TryWriteFeatureRequest(
            Switch2UsbFeatureStep.EnableFeatures, Mask, request,
            out failure));
        Assert.IsTrue(request.SequenceEqual(Convert.FromHexString(
            "0C9100040004000027000000")));

        request.Fill(0xCC);
        Assert.IsFalse(Switch2UsbCommandCodec.TryWriteFeatureRequest(
            Switch2UsbFeatureStep.SetFeatureMask,
            (Switch2UsbFeatureMask)0xA7, request, out failure));
        Assert.AreEqual(Switch2UsbCommandFailure.InvalidRequestPayload,
            failure);
        Assert.IsTrue(request.ToArray().All(value => value == 0xCC));
    }

    [TestMethod]
    public void BatteryCodecAdmitsTwoPinnedHeaderStylesButNoMixedTuple()
    {
        byte[] originalCapture = Convert.FromHexString(
            "0B01000310780000A50E0000");
        byte[] initializedHardware = Convert.FromHexString(
            "0B01000300F80000A50E0000");

        Assert.IsTrue(Switch2UsbCommandCodec
            .TryParseGetBatteryVoltageResponse(originalCapture,
                out ushort originalVoltage,
                out Switch2UsbCommandResponseStyle originalStyle,
                out var failure));
        Assert.AreEqual((ushort)0x0EA5, originalVoltage);
        Assert.AreEqual(
            Switch2UsbCommandResponseStyle.OriginalCapture10_78,
            originalStyle);
        Assert.AreEqual(Switch2UsbCommandFailure.None, failure);
        Assert.IsTrue(Switch2UsbCommandCodec
            .TryParseGetBatteryVoltageResponse(initializedHardware,
                out ushort initializedVoltage,
                out Switch2UsbCommandResponseStyle initializedStyle,
                out failure));
        Assert.AreEqual(originalVoltage, initializedVoltage);
        Assert.AreEqual(
            Switch2UsbCommandResponseStyle.InitializedHardware00_F8,
            initializedStyle);

        originalCapture[5] = 0xF8;
        Assert.IsFalse(Switch2UsbCommandCodec
            .TryParseGetBatteryVoltageResponse(originalCapture, out _,
                out failure));
        Assert.AreEqual(Switch2UsbCommandFailure.InvalidAcknowledgement,
            failure);
        initializedHardware[5] = 0x78;
        Assert.IsFalse(Switch2UsbCommandCodec
            .TryParseGetBatteryVoltageResponse(initializedHardware, out _,
                out failure));
        Assert.AreEqual(Switch2UsbCommandFailure.InvalidAcknowledgement,
            failure);
    }

    [TestMethod]
    public void LedAllOffAttemptRemainsAmbiguousUntilExactlyConfirmed()
    {
        var result = new LedResult();

        LedCleanupResultTransition.MarkAttempted(result);

        Assert.IsTrue(result.AllOffMutationAttempted);
        Assert.IsTrue(result.AllOffMutationDeliveryAmbiguous);
        Assert.IsFalse(result.AllOffExactResponseShapeAndTuple);

        LedCleanupResultTransition.MarkConfirmed(result,
            Switch2UsbCommandResponseStyle.InitializedHardware00_F8);

        Assert.IsTrue(result.AllOffMutationAttempted);
        Assert.IsFalse(result.AllOffMutationDeliveryAmbiguous);
        Assert.IsTrue(result.AllOffExactResponseShapeAndTuple);
        Assert.AreEqual(
            Switch2UsbCommandResponseStyle.InitializedHardware00_F8,
            result.AllOffResponseStyle);
    }

    [TestMethod]
    public void LedCleanupCopyPreservesUnconfirmedDeliveryAmbiguity()
    {
        var sourceLed = new LedResult();
        LedCleanupResultTransition.MarkAttempted(sourceLed);
        var sourceCleanup = new CleanupResult
        {
            PlayerLedAllOffSucceeded = false,
            CommandChannelReopened = true,
        };
        var destinationLed = new LedResult();
        var destinationCleanup = new CleanupResult();

        Program.ApplyLedCleanupResult(sourceLed, sourceCleanup,
            destinationLed, destinationCleanup);

        Assert.IsTrue(destinationLed.AllOffMutationAttempted);
        Assert.IsTrue(destinationLed.AllOffMutationDeliveryAmbiguous);
        Assert.IsFalse(destinationLed.AllOffExactResponseShapeAndTuple);
        Assert.IsFalse(destinationCleanup.PlayerLedAllOffSucceeded);
        Assert.IsTrue(destinationCleanup.CommandChannelReopened);
    }

    [DataTestMethod]
    [DataRow(false, true, "HapticMutationSafetyGateClosed")]
    [DataRow(false, false, "CleanupIncomplete")]
    [DataRow(true, false, "CleanupIncomplete")]
    [DataRow(true, true, null)]
    public void FinalOutcomeNeverMasksIncompleteCleanup(
        bool procedureSucceeded, bool cleanupSucceeded,
        string? expectedFailure)
    {
        var result = new VerificationResult
        {
            VerifierAssemblySha256 = new string('A', 64),
            ProcedureFailureCode = procedureSucceeded ? null :
                VerificationFailureCode.HapticMutationSafetyGateClosed
                    .ToString(),
        };

        Program.FinalizeOutcome(result, procedureSucceeded,
            cleanupSucceeded);

        Assert.AreEqual(procedureSucceeded && cleanupSucceeded,
            result.Success);
        Assert.AreEqual(expectedFailure, result.FailureCode);
        Assert.AreEqual(procedureSucceeded ? null :
            "HapticMutationSafetyGateClosed", result.ProcedureFailureCode);
    }

    [TestMethod]
    public void VerifierAssemblyDigestUsesClosedUpperHexDomain()
    {
        var result = new VerificationResult
        {
            VerifierAssemblySha256 = new string('A', 64),
            ProcedureFailureCode =
                VerificationFailureCode.UnexpectedFailure.ToString(),
        };
        Program.FinalizeOutcome(result, procedureSucceeded: false,
            cleanupSucceeded: true);
        string json = result.ToJson();
        Assert.IsTrue(VerificationPrivacyValidator
            .IsPrivacySafeClosedSchemaJson(json));
        Assert.IsFalse(VerificationPrivacyValidator
            .IsPrivacySafeClosedSchemaJson(json.Replace(new string('A', 64),
                new string('a', 64), StringComparison.Ordinal)));
        Assert.IsFalse(VerificationPrivacyValidator
            .IsPrivacySafeClosedSchemaJson(json.Replace(new string('A', 64),
                new string('A', 63), StringComparison.Ordinal)));
    }

    [TestMethod]
    public void JsonSchemaCannotCarryPrivateIdentityProperties()
    {
        var result = new VerificationResult
        {
            VerifierAssemblySha256 = new string('A', 64),
            ProcedureFailureCode =
                VerificationFailureCode.UnexpectedFailure.ToString(),
            FailureNativeErrorCode = 5,
            CommandResponseFailureDetail =
                Switch2UsbCommandFailure.InvalidAcknowledgement.ToString(),
            CommandTransferFailureStage =
                DS4Windows.Switch2.Verification.CommandTransferFailureStage
                    .ResponseAdmission.ToString(),
            CommandObservedResponseLength = 12,
            CommandObservedResponseHeaderByte4 = 0,
            CommandObservedResponseAcknowledgement = 0xF8,
        };
        Program.FinalizeOutcome(result, procedureSucceeded: false,
            cleanupSucceeded: true);
        string json = result.ToJson();
        Assert.IsTrue(
            VerificationPrivacyValidator.IsPrivacySafeClosedSchemaJson(
                json));
        Assert.IsFalse(json.Contains("TEST_ONLY_INSTANCE",
            StringComparison.Ordinal));
        Assert.IsFalse(json.Contains(SyntheticContainerA.ToString(),
            StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains(@"\\?\hid#", StringComparison.Ordinal));

        using JsonDocument document = JsonDocument.Parse(json);
        Assert.AreEqual(4, document.RootElement.GetProperty("SchemaVersion")
            .GetInt32());
        Assert.AreEqual("fixed-switch2-pro-usb-bcd0201-sole-writer-v2",
            document.RootElement.GetProperty("Procedure").GetString());
        Assert.AreEqual(new string('A', 64), document.RootElement.GetProperty(
            "VerifierAssemblySha256").GetString());
        Assert.AreEqual("0x057E", document.RootElement.GetProperty("Target")
            .GetProperty("VendorId").GetString());
        JsonElement target = document.RootElement.GetProperty("Target");
        Assert.IsTrue(target.GetProperty("SoleHidWriterAdmissionRequired")
            .GetBoolean());
        Assert.IsFalse(target.GetProperty("SoleHidWriterAdmissionSucceeded")
            .GetBoolean());
        Assert.IsFalse(target.TryGetProperty("DeviceGeneration", out _));
        StringAssert.Contains(target.GetProperty("RunPrecondition").GetString()
            ?? string.Empty, "no unplug");
        StringAssert.Contains(
            document.RootElement.GetProperty("SuccessScope").GetString() ??
                string.Empty,
            "not transaction-correlated");
        JsonElement battery = document.RootElement.GetProperty("Battery");
        Assert.IsTrue(battery.TryGetProperty(
            "ExactResponseShapeAndTuple", out _));
        Assert.IsFalse(battery.TryGetProperty("ExactAcknowledgement", out _));
        JsonElement initialization = document.RootElement.GetProperty(
            "VolatileInitialization");
        Assert.IsFalse(initialization.GetProperty(
            "EnableUsbHidReportsAttempted").GetBoolean());
        Assert.IsFalse(initialization.GetProperty(
            "SelectCommonInputReportAttempted").GetBoolean());
        JsonElement haptic = document.RootElement.GetProperty("Haptic");
        Assert.IsTrue(haptic.GetProperty(
            "NonzeroMutationBlockedBySafetyGate").GetBoolean());
        Assert.IsFalse(haptic.GetProperty("NonzeroMutationAttempted")
            .GetBoolean());
        Assert.AreEqual(0, haptic.GetProperty("ZeroAmplitudeWritesAttempted")
            .GetInt32());
        JsonElement led = document.RootElement.GetProperty("Led");
        Assert.IsFalse(led.GetProperty("Player1MutationAttempted")
            .GetBoolean());
        Assert.IsFalse(led.GetProperty("AllOffMutationAttempted")
            .GetBoolean());
        Assert.IsTrue(document.RootElement.GetProperty("Limitations")
            .GetArrayLength() >= 5);
        Assert.AreEqual(5, document.RootElement.GetProperty(
            "FailureNativeErrorCode").GetInt32());
        Assert.AreEqual("InvalidAcknowledgement",
            document.RootElement.GetProperty("CommandResponseFailureDetail")
                .GetString());
        Assert.AreEqual("ResponseAdmission",
            document.RootElement.GetProperty("CommandTransferFailureStage")
                .GetString());
        Assert.AreEqual(12, document.RootElement.GetProperty(
            "CommandObservedResponseLength").GetInt32());
        Assert.AreEqual(0, document.RootElement.GetProperty(
            "CommandObservedResponseHeaderByte4").GetInt32());
        Assert.AreEqual(0xF8, document.RootElement.GetProperty(
            "CommandObservedResponseAcknowledgement").GetInt32());
    }

    [TestMethod]
    public void PrivacyDefenseRejectsUnrecognizedAndIdentifierShapedValues()
    {
        Assert.IsFalse(
            VerificationPrivacyValidator.IsPrivacySafeClosedSchemaJson(
                "{\"Note\":\"Switch2UsbHardwareVerify\"}"),
            "An unknown property is rejected even when its value is allowed.");
        Assert.IsFalse(
            VerificationPrivacyValidator.IsPrivacySafeClosedSchemaJson(
                "{\"Tool\":\"unrecognized fixed-schema string\"}"));
        Assert.IsFalse(
            VerificationPrivacyValidator.IsPrivacySafeClosedSchemaJson(
                "{\"Tool\":\"\\\\\\\\?\\\\hid#private\"}"));
        Assert.IsFalse(
            VerificationPrivacyValidator.IsPrivacySafeClosedSchemaJson(
                "{\"Tool\":\"USB\\\\VID_057E&PID_2069&MI_00\\\\private\"}"));
        Assert.IsFalse(
            VerificationPrivacyValidator.IsPrivacySafeClosedSchemaJson(
                "{\"Tool\":\"00:11:22:33:44:55\"}"));
    }

    [TestMethod]
    public void ClosedSchemaRejectsWrongShapeTypesRangesAndNoncanonicalJson()
    {
        string json = CreateCanonicalFailureResult().ToJson();
        const string Schema = "  \"SchemaVersion\": 4,";

        Assert.IsTrue(VerificationPrivacyValidator
            .IsPrivacySafeClosedSchemaJson(json));
        Assert.IsFalse(VerificationPrivacyValidator
            .IsPrivacySafeClosedSchemaJson("123"));
        Assert.IsFalse(VerificationPrivacyValidator
            .IsPrivacySafeClosedSchemaJson(json.Replace(
                Schema, string.Empty, StringComparison.Ordinal)),
            "A required property cannot be omitted.");
        Assert.IsFalse(VerificationPrivacyValidator
            .IsPrivacySafeClosedSchemaJson(json.Replace(Schema,
                Schema + "\n" + Schema, StringComparison.Ordinal)),
            "Duplicate properties are rejected even when their values agree.");
        Assert.IsFalse(VerificationPrivacyValidator
            .IsPrivacySafeClosedSchemaJson(json.Replace(Schema,
                "  \"SchemaVersion\": \"4\",",
                StringComparison.Ordinal)));
        Assert.IsFalse(VerificationPrivacyValidator
            .IsPrivacySafeClosedSchemaJson(json.Replace(
                "  \"Battery\": {", "  \"Battery\": {\n" +
                "    \"FailureNativeErrorCode\": 5,\n",
                StringComparison.Ordinal)),
            "A globally allowed property is not allowed in the wrong object.");
        Assert.IsFalse(VerificationPrivacyValidator
            .IsPrivacySafeClosedSchemaJson(json.Replace(
                "    \"ExactReports\": 0,",
                "    \"ExactReports\": 999,",
                StringComparison.Ordinal)));
        Assert.IsFalse(VerificationPrivacyValidator
            .IsPrivacySafeClosedSchemaJson(json + "\n"),
            "Only the source-generated canonical representation is admitted.");
    }

    [TestMethod]
    public void ClosedSchemaRejectsExplicitNullWithoutThrowing()
    {
        JsonNode root = JsonNode.Parse(
            CreateCanonicalFailureResult().ToJson())!;
        root["Target"] = null;
        string explicitNull = root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
        });

        Assert.IsFalse(VerificationPrivacyValidator
            .IsPrivacySafeClosedSchemaJson(explicitNull));
        Assert.IsFalse(VerificationPrivacyValidator
            .IsPrivacySafeClosedSchemaJson(null));
    }

    [TestMethod]
    public void ClosedSafetyGateSchemaRejectsImpossibleSuccessClaim()
    {
        var result = new VerificationResult
        {
            VerifierAssemblySha256 = new string('A', 64),
        };
        Program.FinalizeOutcome(result, procedureSucceeded: true,
            cleanupSucceeded: true);

        Assert.IsFalse(VerificationPrivacyValidator
            .IsPrivacySafeClosedSchemaJson(result.ToJson()));
    }

    [TestMethod]
    public void SuccessfulCommandResponseStyleSerializesAsClosedName()
    {
        VerificationResult result = CreateCanonicalFailureResult();
        result.VolatileInitialization.EnableUsbHidReportsAttempted = true;
        result.VolatileInitialization
            .EnableUsbHidReportsExactResponseShapeAndTuple = true;
        result.VolatileInitialization.SelectCommonInputReportAttempted = true;
        result.VolatileInitialization
            .SelectCommonInputReportExactResponseShapeAndTuple = true;
        result.Battery.ExactResponseShapeAndTuple = true;
        result.Battery.RawVoltage = 3_533;
        result.Battery.ResponseStyle =
            Switch2UsbCommandResponseStyle.InitializedHardware00_F8;

        string json = result.ToJson();

        StringAssert.Contains(json,
            "\"ResponseStyle\": \"InitializedHardware00_F8\"");
        Assert.IsTrue(VerificationPrivacyValidator
            .IsPrivacySafeClosedSchemaJson(json));
    }

    [TestMethod]
    public void CanonicalSchemaAdmitsCompletedGateClosedMechanismState()
    {
        VerificationResult result = CreatePrePlayerOneSuccessResult();
        result.Led.Player1MutationAttempted = true;
        result.Led.Player1ExactResponseShapeAndTuple = true;
        result.Led.Player1ResponseStyle =
            Switch2UsbCommandResponseStyle.InitializedHardware00_F8;
        result.Cleanup.PlayerLedAllOffRequired = true;
        LedCleanupResultTransition.MarkAttempted(result.Led);
        LedCleanupResultTransition.MarkConfirmed(result.Led,
            Switch2UsbCommandResponseStyle.InitializedHardware00_F8);
        result.Cleanup.PlayerLedAllOffSucceeded = true;
        result.ProcedureFailureCode = VerificationFailureCode
            .HapticMutationSafetyGateClosed.ToString();
        Program.FinalizeOutcome(result, procedureSucceeded: false,
            cleanupSucceeded: true);

        Assert.IsTrue(VerificationPrivacyValidator
            .IsPrivacySafeClosedSchemaJson(result.ToJson()));
    }

    [TestMethod]
    public void CanonicalSchemaAdmitsBlockedPlayerOneNeutralizationState()
    {
        VerificationResult result = CreatePrePlayerOneSuccessResult();
        result.Led.Player1MutationAttempted = true;
        result.Led.Player1MutationDeliveryAmbiguous = true;
        result.Cleanup.PlayerLedAllOffRequired = true;
        result.Cleanup.CommandOutputOwnershipAbandoned = true;
        result.Cleanup.PlayerLedCommandOwnershipAbandoned = true;
        result.Cleanup.PlayerLedNeutralizationBlockedByOwnership = true;
        result.Cleanup.LateOutputHandleReleaseUnconfirmed = true;
        result.ProcedureFailureCode = VerificationFailureCode
            .CommandOperationTimedOut.ToString();
        Program.FinalizeOutcome(result, procedureSucceeded: false,
            cleanupSucceeded: false);

        string json = result.ToJson();
        Assert.IsTrue(VerificationPrivacyValidator
            .IsPrivacySafeClosedSchemaJson(json));
        StringAssert.Contains(json, "\"FailureCode\": \"CleanupIncomplete\"");
        StringAssert.Contains(json,
            "\"ProcedureFailureCode\": \"CommandOperationTimedOut\"");
    }

    [TestMethod]
    public void DisposalTimeoutIsUnconfirmedNotConfirmedFailure()
    {
        var commandTimeout = new CleanupResult();
        Program.RecordCommandChannelDisposalStatus(
            BoundedOperationStatus.TimedOut, commandTimeout);
        Assert.IsFalse(commandTimeout.CommandChannelDisposeFailure);
        Assert.IsTrue(commandTimeout.LateOutputHandleReleaseUnconfirmed);

        var commandFailure = new CleanupResult();
        Program.RecordCommandChannelDisposalStatus(
            BoundedOperationStatus.Failed, commandFailure);
        Assert.IsTrue(commandFailure.CommandChannelDisposeFailure);
        Assert.IsFalse(commandFailure.LateOutputHandleReleaseUnconfirmed);

        var hidTimeout = new CleanupResult();
        Program.RecordHidChannelDisposalStatus(
            BoundedOperationStatus.TimedOut, hidTimeout);
        Assert.IsFalse(hidTimeout.HidChannelDisposeFailure);
        Assert.IsTrue(hidTimeout.LateOutputHandleReleaseUnconfirmed);

        var inputTimeout = new CleanupResult();
        Program.RecordInputChannelDisposalStatus(
            BoundedOperationStatus.TimedOut, inputTimeout);
        Assert.IsFalse(inputTimeout.InputChannelDisposeFailure);
        Assert.IsTrue(inputTimeout.LateInputHandleReleaseUnconfirmed);

        var inputFailure = new CleanupResult();
        Program.RecordInputChannelDisposalStatus(
            BoundedOperationStatus.Failed, inputFailure);
        Assert.IsTrue(inputFailure.InputChannelDisposeFailure);
        Assert.IsFalse(inputFailure.LateInputHandleReleaseUnconfirmed);

        var blockedNeutralization = new CleanupResult
        {
            PlayerLedAllOffRequired = true,
        };
        Program.RecordCommandReplacementStatus(
            BoundedOperationStatus.TimedOut,
            lateAcquisitionReleaseUnconfirmed: false,
            blockedNeutralization);
        Assert.IsTrue(
            blockedNeutralization.CommandOutputOwnershipAbandoned);
        Assert.IsTrue(
            blockedNeutralization.PlayerLedCommandOwnershipAbandoned);
        Assert.IsTrue(blockedNeutralization
            .PlayerLedNeutralizationBlockedByOwnership);
        Assert.IsTrue(
            blockedNeutralization.LateOutputHandleReleaseUnconfirmed);
        Assert.IsFalse(blockedNeutralization.CommandChannelDisposeFailure);
    }

    [TestMethod]
    public void OutputArgumentsHaveNoRawCommandSurface()
    {
        Assert.IsTrue(OutputOptions.TryParse([], out OutputOptions standard));
        Assert.IsNull(standard.OutputPath);
        Assert.IsTrue(OutputOptions.TryParse(
            ["--output", "test-only-result.json"], out OutputOptions file));
        Assert.IsTrue(Path.IsPathFullyQualified(file.OutputPath!));
        Assert.IsFalse(OutputOptions.TryParse(["--count", "1"], out _));
        Assert.IsFalse(OutputOptions.TryParse(["--output", "result.txt"],
            out _));
        Assert.IsFalse(OutputOptions.TryParse(
            ["--raw-command", "0B91000300000000"], out _));
    }

    private static byte[] RegistryMultiString(params Guid[] values)
    {
        string text = string.Join('\0', values.Select(value =>
            value.ToString("B"))) + "\0\0";
        return Encoding.Unicode.GetBytes(text);
    }

    private static VerificationResult CreateCanonicalFailureResult()
    {
        var result = new VerificationResult
        {
            VerifierAssemblySha256 = new string('A', 64),
            ProcedureFailureCode =
                VerificationFailureCode.UnexpectedFailure.ToString(),
        };
        Program.FinalizeOutcome(result, procedureSucceeded: false,
            cleanupSucceeded: true);
        return result;
    }

    private static VerificationResult CreatePrePlayerOneSuccessResult()
    {
        var result = new VerificationResult
        {
            VerifierAssemblySha256 = new string('A', 64),
        };
        result.Target.SoleHidWriterAdmissionSucceeded = true;
        result.BulkTopology.Validated = true;
        result.PipePolicy.Validated = true;
        result.VolatileInitialization.EnableUsbHidReportsAttempted = true;
        result.VolatileInitialization
            .EnableUsbHidReportsExactResponseShapeAndTuple = true;
        result.VolatileInitialization.SelectCommonInputReportAttempted = true;
        result.VolatileInitialization
            .SelectCommonInputReportExactResponseShapeAndTuple = true;
        result.Battery.RawVoltage = 3_533;
        result.Battery.ResponseStyle =
            Switch2UsbCommandResponseStyle.InitializedHardware00_F8;
        result.Battery.ExactResponseShapeAndTuple = true;
        result.InputRate.ExactReports = VerificationPlan.InputReportCount;
        result.InputRate.ObservedReportsPerSecond = 250;
        result.InputRate.MeanIntervalMilliseconds = 4;
        result.InputRate.P50IntervalMilliseconds = 4;
        result.InputRate.P95IntervalMilliseconds = 4.02;
        result.InputRate.P99IntervalMilliseconds = 4.1;
        result.InputRate.CounterForwardMovements =
            VerificationPlan.InputReportCount - 1;
        result.InputRate.CounterMinimumDelta = 4;
        result.InputRate.CounterMaximumDelta = 4;
        result.InputRate.CounterPlusFourMovements =
            VerificationPlan.InputReportCount - 1;
        return result;
    }
}
