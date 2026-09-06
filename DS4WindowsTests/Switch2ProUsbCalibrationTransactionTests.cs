using System.Diagnostics;
using DS4Windows.Switch2;

namespace DS4WinWPF.DS4WindowsTests;

[TestClass]
public sealed class Switch2ProUsbCalibrationTransactionTests
{
    [TestMethod]
    public void CompletedStartupReadsFourExactRecordsAndAdoptsUserOverride()
    {
        Switch2PhysicalInputLifetime lifetime = CreateLifetime(31, 37);
        var lease = new CalibrationLease(lifetime);
        Switch2ProUsbStartupTransaction startup = CompleteStartup(lease,
            lifetime);

        Assert.IsTrue(Switch2ProUsbCalibrationTransaction.TryCreate(lease,
            lifetime, startup, out var transaction, out var createFailure),
            createFailure.ToString());
        Assert.IsTrue(transaction.TryRead(5_000, out var result),
            $"{result.Failure}/{result.RetirementFailure}");
        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(Switch2CalibrationAdoptionStatus.AdoptedFactory,
            result.Calibration.Left.Status,
            "An unmarked user record must preserve factory calibration.");
        Assert.AreEqual(Switch2CalibrationAdoptionStatus.AdoptedUser,
            result.Calibration.Right.Status);
        Assert.AreEqual((ushort)0x820,
            result.Calibration.Right.EffectiveCalibration.NeutralX);
        Assert.AreEqual(9, lease.ExecutionCount);
        CollectionAssert.AreEqual(new[]
        {
            "0291000400080000097E0000A8300100",
            "0291000400080000097E0000E8300100",
            "02910004000800000B7E000040C01F00",
            "02910004000800000B7E000080C01F00",
        }, lease.Requests.Skip(5).Select(Convert.ToHexString).ToArray());
        Assert.AreEqual(0, lease.RetirementCount);
    }

    [TestMethod]
    public void CalibrationRequiresTheExactCompletedStartupLifetime()
    {
        Switch2PhysicalInputLifetime lifetime = CreateLifetime(41, 43);
        var lease = new CalibrationLease(lifetime);
        Assert.IsTrue(Switch2ProUsbStartupTransaction.TryCreate(lease,
            lifetime, out var startup, out _));

        Assert.IsFalse(Switch2ProUsbCalibrationTransaction.TryCreate(lease,
            lifetime, startup, out _, out var failure));
        Assert.AreEqual(
            Switch2ProUsbCalibrationReadFailure.StartupNotCompleted,
            failure);
    }

    [TestMethod]
    public void ProvenUnconsumedCalibrationReadAllowsCenteredFallbackOnly()
    {
        Switch2PhysicalInputLifetime lifetime = CreateLifetime(47, 53);
        var lease = new CalibrationLease(lifetime);
        Switch2ProUsbStartupTransaction startup = CompleteStartup(lease,
            lifetime);
        lease.CalibrationMode = CalibrationCommandMode.ProvenNotConsumed;
        Assert.IsTrue(Switch2ProUsbCalibrationTransaction.TryCreate(lease,
            lifetime, startup, out var transaction, out _));

        Assert.IsFalse(transaction.TryRead(5_000, out var result));
        Assert.AreEqual(
            Switch2ProUsbCalibrationReadFailure.ProvenNotConsumed,
            result.Failure);
        Assert.IsTrue(result.CanUseCenteredFallback);
        Assert.IsFalse(result.RequiresQuarantine);
        Assert.AreEqual(0, lease.RetirementCount);
    }

    [TestMethod]
    public void MalformedCalibrationProofRetiresTheSharedCommandLane()
    {
        Switch2PhysicalInputLifetime lifetime = CreateLifetime(59, 61);
        var lease = new CalibrationLease(lifetime);
        Switch2ProUsbStartupTransaction startup = CompleteStartup(lease,
            lifetime);
        lease.CalibrationMode = CalibrationCommandMode.WrongProof;
        Assert.IsTrue(Switch2ProUsbCalibrationTransaction.TryCreate(lease,
            lifetime, startup, out var transaction, out _));

        Assert.IsFalse(transaction.TryRead(5_000, out var result));
        Assert.AreEqual(
            Switch2ProUsbCalibrationReadFailure.WrongResponseProof,
            result.Failure);
        Assert.IsTrue(result.RequiresQuarantine);
        Assert.AreEqual(1, lease.RetirementCount);
        Assert.AreEqual(
            Switch2ProUsbStartupRetirementReason.CommandOutcomeUncertain,
            lease.LastRetirementReason);
    }

    private static Switch2ProUsbStartupTransaction CompleteStartup(
        CalibrationLease lease, in Switch2PhysicalInputLifetime lifetime)
    {
        Assert.IsTrue(Switch2ProUsbStartupTransaction.TryCreate(lease,
            lifetime, out var startup, out var failure), failure.ToString());
        for (int index = 0;
                index < Switch2ProUsbStartupTransaction.RequiredStepCount;
                index++)
        {
            Assert.IsTrue(startup.TryAdvance(50, 50, out var result),
                result.CommandFailure.ToString());
        }
        return startup;
    }

    private static Switch2PhysicalInputLifetime CreateLifetime(
        ulong deviceGeneration, ulong transportGeneration)
    {
        Assert.IsTrue(Switch2PhysicalContainerIdentity.TryCreate(
            new Guid("3154455A-5F1A-4B59-960A-9412E065C86E"),
            out var container));
        var input = new Switch2UsbHidInterfaceObservation(container, 0, 0,
            Switch2UsbBoundDriver.HidClass, 0x0001, 0x0005, 64, 64, 0);
        var command = new Switch2UsbCommandInterfaceObservation(container, 1,
            0, Switch2UsbBoundDriver.WinUsb, 2,
            new Switch2UsbPipeObservation(0x02,
                Switch2UsbPipeTransferType.Bulk, 64, 0),
            new Switch2UsbPipeObservation(0x82,
                Switch2UsbPipeTransferType.Bulk, 64, 0));
        var observation = new Switch2ProUsbCompositeObservation(0x057E,
            0x2069, 0x0201, container, 1, 1, input, command);
        Assert.IsTrue(Switch2PhysicalDeviceFactory.TryAdmitProUsb(observation,
            out var registration, out var admission), admission.ToString());
        Assert.IsTrue(Switch2PhysicalInputLifetime.TryCreate(registration,
            deviceGeneration, transportGeneration, Stopwatch.Frequency,
            out var lifetime));
        return lifetime;
    }

    private static byte[] BuildCalibration(ushort neutralX,
        ushort neutralY)
    {
        var record = new byte[Switch2CalibrationCodec.StickCalibrationLength];
        PackStick(record, 0, neutralX, neutralY);
        PackStick(record, 3, 0x700, 0x700);
        PackStick(record, 6, 0x700, 0x700);
        return record;
    }

    private static byte[] BuildUserCalibration(ushort neutralX,
        ushort neutralY)
    {
        var record = new byte[
            Switch2CalibrationCodec.UserStickCalibrationLength];
        record[0] = 0xB2;
        record[1] = 0xA1;
        BuildCalibration(neutralX, neutralY).CopyTo(record, 2);
        return record;
    }

    private static void PackStick(byte[] destination, int offset, ushort x,
        ushort y)
    {
        destination[offset] = (byte)x;
        destination[offset + 1] = (byte)(((x >> 8) & 0x0F) |
            ((y & 0x0F) << 4));
        destination[offset + 2] = (byte)(y >> 4);
    }

    private enum CalibrationCommandMode : byte
    {
        Exact = 0,
        ProvenNotConsumed,
        WrongProof,
    }

    private sealed class CalibrationLease :
        ISwitch2ProUsbCalibrationCommandLease
    {
        private readonly List<byte[]> requests = new();

        internal CalibrationLease(in Switch2PhysicalInputLifetime lifetime)
        {
            Lifetime = lifetime;
        }

        public Switch2PhysicalInputLifetime Lifetime { get; }

        internal CalibrationCommandMode CalibrationMode { get; set; }

        internal int ExecutionCount { get; private set; }

        internal int RetirementCount { get; private set; }

        internal Switch2ProUsbStartupRetirementReason LastRetirementReason
        {
            get;
            private set;
        }

        internal IReadOnlyList<byte[]> Requests => requests;

        public Switch2ProUsbStartupCommandCompletion Execute(
            in Switch2ProUsbStartupCommandClaim claim,
            ReadOnlySpan<byte> exactRequest, int timeoutMilliseconds)
        {
            ExecutionCount++;
            requests.Add(exactRequest.ToArray());
            if (claim.Step <=
                Switch2ProUsbStartupStep.SelectCommonInputReport)
            {
                Switch2ProUsbStartupResponseProofKind proof = claim.Step
                    switch
                    {
                        Switch2ProUsbStartupStep.EnableUsbHidReports or
                            Switch2ProUsbStartupStep.
                                SelectCommonInputReport =>
                            Switch2ProUsbStartupResponseProofKind.
                                InitializationResponseValidatedByCodec,
                        Switch2ProUsbStartupStep.SetPlayerLed =>
                            Switch2ProUsbStartupResponseProofKind.
                                PlayerLedResponseValidatedByCodec,
                        _ => Switch2ProUsbStartupResponseProofKind.
                            FeatureResponseValidatedByCodec,
                    };
                return Switch2ProUsbStartupCommandCompletion.ExactResponse(
                    claim, claim.Step, proof);
            }
            if (CalibrationMode ==
                CalibrationCommandMode.ProvenNotConsumed)
            {
                return Switch2ProUsbStartupCommandCompletion.
                    ProvenNotConsumed(claim, claim.Step);
            }

            byte[] payload = claim.Step switch
            {
                Switch2ProUsbStartupStep.ReadFactoryPrimaryCalibration =>
                    BuildCalibration(0x800, 0x801),
                Switch2ProUsbStartupStep.ReadFactorySecondaryCalibration =>
                    BuildCalibration(0x810, 0x811),
                Switch2ProUsbStartupStep.ReadUserPrimaryCalibration =>
                    Enumerable.Repeat((byte)0xFF,
                        Switch2CalibrationCodec.UserStickCalibrationLength).
                        ToArray(),
                Switch2ProUsbStartupStep.ReadUserSecondaryCalibration =>
                    BuildUserCalibration(0x820, 0x821),
                _ => Array.Empty<byte>(),
            };
            Switch2ProUsbStartupResponseProofKind calibrationProof =
                CalibrationMode == CalibrationCommandMode.WrongProof ?
                    Switch2ProUsbStartupResponseProofKind.
                        FeatureResponseValidatedByCodec :
                    Switch2ProUsbStartupResponseProofKind.
                        CalibrationReadResponseValidatedByCodec;
            return Switch2ProUsbStartupCommandCompletion.ExactResponse(claim,
                claim.Step, calibrationProof, payload);
        }

        public Switch2ProUsbStartupRetirementCompletion Retire(
            in Switch2ProUsbStartupRetirementClaim claim,
            int timeoutMilliseconds)
        {
            RetirementCount++;
            LastRetirementReason = claim.Reason;
            return Switch2ProUsbStartupRetirementCompletion.Released(claim,
                claim.Reason);
        }
    }
}
