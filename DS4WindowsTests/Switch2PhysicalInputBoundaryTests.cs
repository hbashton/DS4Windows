using System.Buffers.Binary;
using System.Reflection;
using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public class Switch2PhysicalInputBoundaryTests
{
    private static readonly Guid ContainerAGuid =
        Guid.Parse("11389bd5-b4e1-4c46-a92e-d5fba62a5867");
    private static readonly Guid ContainerBGuid =
        Guid.Parse("e09b2e3f-b30c-4a88-9ea2-76154ba53f50");
    private static readonly Switch2PhysicalContainerIdentity ContainerA =
        CreateContainerIdentity(ContainerAGuid);
    private static readonly Switch2PhysicalContainerIdentity ContainerB =
        CreateContainerIdentity(ContainerBGuid);

    [TestMethod]
    public void ExactCompositeAdmissionBindsBothInterfacesToOneController()
    {
        Switch2ProUsbCompositeObservation observation = CreateObservation();

        Assert.IsTrue(Switch2PhysicalDeviceFactory.TryAdmitProUsb(observation,
            out Switch2PhysicalInputRegistration registration,
            out Switch2PhysicalAdmissionFailure failure), failure.ToString());
        Assert.IsTrue(registration.IsValid);
        Assert.AreEqual(Switch2PhysicalInputRegistration.CurrentVersion,
            registration.Version);
        Assert.AreEqual(ContainerA, registration.ContainerIdentity);
        Assert.AreEqual(Switch2ControllerModel.ProController2,
            registration.Model);
        Assert.AreEqual(Switch2Transport.Usb, registration.Transport);
        Assert.AreEqual(
            Switch2InputProtocolRevision.ProUsbCommon05Bcd0201,
            registration.ProtocolIdentity.ProtocolRevision);
        Assert.AreEqual((byte)0, registration.InputInterfaceNumber);
        Assert.AreEqual((byte)1, registration.CommandInterfaceNumber);
        Assert.AreEqual((ushort)64, registration.InputReportByteLength);

        Switch2ProUsbCompositeObservation reversed = CreateObservation(
            pipe0: BulkIn(), pipe1: BulkOut());
        Assert.IsTrue(Switch2PhysicalDeviceFactory.TryAdmitProUsb(reversed,
            out Switch2PhysicalInputRegistration reversedRegistration,
            out failure), failure.ToString());
        Assert.IsTrue(registration.Equals(reversedRegistration),
            "USB descriptor enumeration order must not change identity.");
    }

    [TestMethod]
    public void IdentityAndCompositeContainerAreFailClosed()
    {
        AssertRejected(CreateObservation(vendorId: 0x057F),
            Switch2PhysicalAdmissionFailure.UnrecognizedUsbIdentity);
        AssertRejected(CreateObservation(productId: 0x2068),
            Switch2PhysicalAdmissionFailure.UnrecognizedUsbIdentity);
        AssertRejected(CreateObservation(bcdDevice: 0x0200),
            Switch2PhysicalAdmissionFailure.UnrecognizedUsbIdentity);
        AssertRejected(CreateObservation(containerId: Guid.Empty,
                inputContainerId: Guid.Empty, commandContainerId: Guid.Empty),
            Switch2PhysicalAdmissionFailure.MissingContainerIdentity);
        AssertRejected(CreateObservation(inputContainerId: ContainerBGuid),
            Switch2PhysicalAdmissionFailure.InputContainerMismatch);
        AssertRejected(CreateObservation(commandContainerId: ContainerBGuid),
            Switch2PhysicalAdmissionFailure.CommandContainerMismatch);
        AssertRejected(CreateObservation(matchingInputInterfaceCount: 0),
            Switch2PhysicalAdmissionFailure.InputInterfaceMultiplicityMismatch);
        AssertRejected(CreateObservation(matchingInputInterfaceCount: 2),
            Switch2PhysicalAdmissionFailure.InputInterfaceMultiplicityMismatch);
        AssertRejected(CreateObservation(matchingCommandInterfaceCount: 0),
            Switch2PhysicalAdmissionFailure.
                CommandInterfaceMultiplicityMismatch);
        AssertRejected(CreateObservation(matchingCommandInterfaceCount: 2),
            Switch2PhysicalAdmissionFailure.
                CommandInterfaceMultiplicityMismatch);
    }

    [TestMethod]
    public void HidAdmissionRequiresExactDriverInterfaceUsageAndReportShape()
    {
        AssertRejected(CreateObservation(
                inputDriver: Switch2UsbBoundDriver.Unknown),
            Switch2PhysicalAdmissionFailure.InputDriverMismatch);
        AssertRejected(CreateObservation(inputInterfaceNumber: 1),
            Switch2PhysicalAdmissionFailure.InputInterfaceMismatch);
        AssertRejected(CreateObservation(inputAlternateSetting: 1),
            Switch2PhysicalAdmissionFailure.InputInterfaceMismatch);
        AssertRejected(CreateObservation(usagePage: 0xFF00),
            Switch2PhysicalAdmissionFailure.InputHidUsageMismatch);
        AssertRejected(CreateObservation(usage: 0x0004),
            Switch2PhysicalAdmissionFailure.InputHidUsageMismatch);
        AssertRejected(CreateObservation(inputReportLength: 63),
            Switch2PhysicalAdmissionFailure.InputReportShapeMismatch);
        AssertRejected(CreateObservation(outputReportLength: 65),
            Switch2PhysicalAdmissionFailure.InputReportShapeMismatch);
        AssertRejected(CreateObservation(featureReportLength: 1),
            Switch2PhysicalAdmissionFailure.InputReportShapeMismatch);
    }

    [TestMethod]
    public void CommandAdmissionRequiresExactWinUsbBulkTopology()
    {
        AssertRejected(CreateObservation(
                commandDriver: Switch2UsbBoundDriver.HidClass),
            Switch2PhysicalAdmissionFailure.CommandDriverMismatch);
        AssertRejected(CreateObservation(commandInterfaceNumber: 0),
            Switch2PhysicalAdmissionFailure.CommandInterfaceMismatch);
        AssertRejected(CreateObservation(commandAlternateSetting: 1),
            Switch2PhysicalAdmissionFailure.CommandInterfaceMismatch);
        AssertRejected(CreateObservation(endpointCount: 1),
            Switch2PhysicalAdmissionFailure.CommandEndpointCountMismatch);
        AssertRejected(CreateObservation(endpointCount: 3),
            Switch2PhysicalAdmissionFailure.CommandEndpointCountMismatch);
        AssertRejected(CreateObservation(pipe0: new Switch2UsbPipeObservation(
                0x01, Switch2UsbPipeTransferType.Bulk, 64, 0)),
            Switch2PhysicalAdmissionFailure.CommandPipeTopologyMismatch);
        AssertRejected(CreateObservation(pipe1: new Switch2UsbPipeObservation(
                0x82, Switch2UsbPipeTransferType.Interrupt, 64, 0)),
            Switch2PhysicalAdmissionFailure.CommandPipeTopologyMismatch);
        AssertRejected(CreateObservation(pipe0: new Switch2UsbPipeObservation(
                0x02, Switch2UsbPipeTransferType.Bulk, 512, 0)),
            Switch2PhysicalAdmissionFailure.CommandPipeTopologyMismatch);
        AssertRejected(CreateObservation(pipe1: new Switch2UsbPipeObservation(
                0x82, Switch2UsbPipeTransferType.Bulk, 64, 1)),
            Switch2PhysicalAdmissionFailure.CommandPipeTopologyMismatch);
        AssertRejected(CreateObservation(pipe0: BulkOut(), pipe1: BulkOut()),
            Switch2PhysicalAdmissionFailure.CommandPipeTopologyMismatch);
    }

    [TestMethod]
    public void EveryNumericDescriptorFieldHasOneExactAdmittedValue()
    {
        AssertSingleUShort(value => TryAdmit(CreateObservation(vendorId: value)),
            0x057E, "VID");
        AssertSingleUShort(value => TryAdmit(CreateObservation(productId: value)),
            0x2069, "PID");
        AssertSingleUShort(value => TryAdmit(CreateObservation(bcdDevice: value)),
            0x0201, "bcdDevice");
        AssertSingleByte(value => TryAdmit(CreateObservation(
            inputInterfaceNumber: value)), 0, "HID interface");
        AssertSingleByte(value => TryAdmit(CreateObservation(
            inputAlternateSetting: value)), 0, "HID alternate setting");
        AssertSingleByte(value => TryAdmit(CreateObservation(
            matchingInputInterfaceCount: value)), 1,
            "matching HID interface count");
        AssertSingleUShort(value => TryAdmit(CreateObservation(usagePage: value)),
            0x0001, "HID usage page");
        AssertSingleUShort(value => TryAdmit(CreateObservation(usage: value)),
            0x0005, "HID usage");
        AssertSingleUShort(value => TryAdmit(CreateObservation(
            inputReportLength: value)), 64, "HID input length");
        AssertSingleUShort(value => TryAdmit(CreateObservation(
            outputReportLength: value)), 64, "HID output length");
        AssertSingleUShort(value => TryAdmit(CreateObservation(
            featureReportLength: value)), 0, "HID feature length");
        AssertSingleByte(value => TryAdmit(CreateObservation(
            commandInterfaceNumber: value)), 1, "command interface");
        AssertSingleByte(value => TryAdmit(CreateObservation(
            commandAlternateSetting: value)), 0,
            "command alternate setting");
        AssertSingleByte(value => TryAdmit(CreateObservation(
            matchingCommandInterfaceCount: value)), 1,
            "matching command interface count");
        AssertSingleByte(value => TryAdmit(CreateObservation(
            endpointCount: value)), 2, "command endpoint count");
        AssertSingleByte(value => TryAdmit(CreateObservation(pipe0:
            new Switch2UsbPipeObservation(value,
                Switch2UsbPipeTransferType.Bulk, 64, 0))), 0x02,
            "bulk OUT address");
        AssertSingleByte(value => TryAdmit(CreateObservation(pipe1:
            new Switch2UsbPipeObservation(value,
                Switch2UsbPipeTransferType.Bulk, 64, 0))), 0x82,
            "bulk IN address");
        AssertSingleByte(value => TryAdmit(CreateObservation(pipe0:
            new Switch2UsbPipeObservation(0x02,
                (Switch2UsbPipeTransferType)value, 64, 0))),
            (byte)Switch2UsbPipeTransferType.Bulk,
            "bulk OUT transfer type");
        AssertSingleByte(value => TryAdmit(CreateObservation(pipe1:
            new Switch2UsbPipeObservation(0x82,
                (Switch2UsbPipeTransferType)value, 64, 0))),
            (byte)Switch2UsbPipeTransferType.Bulk,
            "bulk IN transfer type");
        AssertSingleUShort(value => TryAdmit(CreateObservation(pipe0:
            new Switch2UsbPipeObservation(0x02,
                Switch2UsbPipeTransferType.Bulk, value, 0))), 64,
            "bulk OUT maximum packet");
        AssertSingleUShort(value => TryAdmit(CreateObservation(pipe1:
            new Switch2UsbPipeObservation(0x82,
                Switch2UsbPipeTransferType.Bulk, value, 0))), 64,
            "bulk IN maximum packet");
        AssertSingleByte(value => TryAdmit(CreateObservation(pipe0:
            new Switch2UsbPipeObservation(0x02,
                Switch2UsbPipeTransferType.Bulk, 64, value))), 0,
            "bulk OUT interval");
        AssertSingleByte(value => TryAdmit(CreateObservation(pipe1:
            new Switch2UsbPipeObservation(0x82,
                Switch2UsbPipeTransferType.Bulk, 64, value))), 0,
            "bulk IN interval");
    }

    [TestMethod]
    public void AdapterProcessesOnlyItsExactRegistrationAndLifetime()
    {
        Switch2PhysicalInputRegistration registration = Admit(
            CreateObservation());
        Switch2PhysicalInputLifetime lifetime = CreateLifetime(registration,
            deviceGeneration: 3, transportGeneration: 7);
        Switch2InputCalibrationSnapshot calibration = CreateCalibration(3);
        Assert.IsTrue(Switch2PhysicalInputAdapter.TryCreate(lifetime,
            calibration, out Switch2PhysicalInputAdapter adapter,
            out Switch2PhysicalInputFailure createFailure),
            createFailure.Kind.ToString());

        byte[] packet = BuildUsbPacket(100, 0x02084081, 0x123, 0x456,
            0x789, 0xABC);
        Assert.IsTrue(adapter.TryProcess(lifetime, packet, 1_000,
            out Switch2CanonicalInputFrame frame,
            out Switch2PhysicalInputFailure processFailure),
            processFailure.Kind.ToString());
        Assert.IsTrue(processFailure.IsNone);
        Assert.AreEqual(ContainerA,
            adapter.Lifetime.Registration.ContainerIdentity);
        Assert.AreEqual(3UL, frame.DeviceGeneration);
        Assert.AreEqual(7UL, frame.TransportGeneration);
        Assert.AreEqual(100u, frame.DeviceCounterRaw);
        Assert.IsTrue(frame.TryGetLeftStick(out var left));
        Assert.AreEqual((ushort)0x123, left.Raw.X);
        Assert.AreEqual((ushort)0x456, left.Raw.Y);
        Assert.IsTrue(frame.TryGetRightStick(out var right));
        Assert.AreEqual((ushort)0x789, right.Raw.X);
        Assert.AreEqual((ushort)0xABC, right.Raw.Y);

        packet.AsSpan().Clear();
        Span<byte> retained = stackalloc byte[Switch2OwnedInputBody.Length];
        Assert.IsTrue(frame.TryCopyRawBody(retained));
        Assert.AreEqual((byte)100, retained[0]);
        Assert.AreEqual((byte)0x81, retained[4]);
    }

    [TestMethod]
    public void RejectedCrossControllerAndStaleObservationsDoNotAdvanceState()
    {
        Switch2PhysicalInputRegistration registration = Admit(
            CreateObservation());
        Switch2PhysicalInputLifetime lifetime = CreateLifetime(registration,
            1, 1);
        Assert.IsTrue(Switch2PhysicalInputAdapter.TryCreate(lifetime,
            CreateCalibration(1), out Switch2PhysicalInputAdapter adapter,
            out _));

        Switch2PhysicalInputRegistration otherRegistration = Admit(
            CreateObservation(containerId: ContainerBGuid,
                inputContainerId: ContainerBGuid,
                commandContainerId: ContainerBGuid));
        Switch2PhysicalInputLifetime other = CreateLifetime(otherRegistration,
            1, 1);
        byte[] first = BuildUsbPacket(10, 0, 1, 2, 3, 4);
        Assert.IsFalse(adapter.TryProcess(other, first, 100, out _,
            out Switch2PhysicalInputFailure crossController));
        Assert.AreEqual(Switch2PhysicalInputFailureKind.LifetimeMismatch,
            crossController.Kind);

        Switch2PhysicalInputLifetime stale = CreateLifetime(registration, 1, 2);
        Assert.IsFalse(adapter.TryProcess(stale, first, 100, out _,
            out Switch2PhysicalInputFailure staleFailure));
        Assert.AreEqual(Switch2PhysicalInputFailureKind.LifetimeMismatch,
            staleFailure.Kind);

        Assert.IsTrue(adapter.TryProcess(lifetime, first, 100,
            out Switch2CanonicalInputFrame accepted, out _));
        Assert.AreEqual(Switch2CounterSequenceKind.First,
            accepted.CounterSequence,
            "A rejected controller/lifetime must not mutate the session.");

        Assert.IsFalse(adapter.TryProcess(lifetime,
            first.AsSpan(0, first.Length - 1), 101, out _,
            out Switch2PhysicalInputFailure malformed));
        Assert.AreEqual(Switch2PhysicalInputFailureKind.SessionRejected,
            malformed.Kind);
        Assert.AreEqual(Switch2InputSessionFailure.InvalidFramingOrReport,
            malformed.SessionFailure);

        byte[] next = BuildUsbPacket(14, 0, 1, 2, 3, 4);
        Assert.IsTrue(adapter.TryProcess(lifetime, next, 102,
            out Switch2CanonicalInputFrame afterReject, out _));
        Assert.AreEqual(4u, afterReject.CounterDeltaRaw);
    }

    [TestMethod]
    public void AdapterAcceptsOnlyExactUsbLengthAndCommon05ReportId()
    {
        Switch2PhysicalInputRegistration registration = Admit(
            CreateObservation());
        Switch2PhysicalInputLifetime lifetime = CreateLifetime(registration,
            1, 1);
        byte[] exact = BuildUsbPacket(1, 0, 1, 2, 3, 4);

        for (int length = 0; length <= 128; length++)
        {
            Assert.IsTrue(Switch2PhysicalInputAdapter.TryCreate(lifetime,
                CreateCalibration(1), out Switch2PhysicalInputAdapter adapter,
                out _));
            byte[] candidate = new byte[length];
            exact.AsSpan(0, Math.Min(length, exact.Length)).CopyTo(candidate);
            bool accepted = adapter.TryProcess(lifetime, candidate, 100,
                out _, out _);
            Assert.AreEqual(length == Switch2InputCodec.UsbPacketLength,
                accepted, $"Unexpected length policy for {length} bytes.");
        }

        for (int reportId = byte.MinValue; reportId <= byte.MaxValue;
             reportId++)
        {
            Assert.IsTrue(Switch2PhysicalInputAdapter.TryCreate(lifetime,
                CreateCalibration(1), out Switch2PhysicalInputAdapter adapter,
                out _));
            byte[] candidate = (byte[])exact.Clone();
            candidate[0] = (byte)reportId;
            bool accepted = adapter.TryProcess(lifetime, candidate, 100,
                out _, out _);
            Assert.AreEqual(reportId ==
                (byte)Switch2InputReportKind.Common05, accepted,
                $"Unexpected report-ID policy for 0x{reportId:X2}.");
        }
    }

    [TestMethod]
    public void ResetRequiresSameCompositeRegistrationAndAdvancedGeneration()
    {
        Switch2PhysicalInputRegistration registration = Admit(
            CreateObservation());
        Switch2PhysicalInputLifetime initial = CreateLifetime(registration,
            5, 9);
        Assert.IsTrue(Switch2PhysicalInputAdapter.TryCreate(initial,
            CreateCalibration(5), out Switch2PhysicalInputAdapter adapter,
            out _));
        Assert.IsTrue(adapter.TryProcess(initial,
            BuildUsbPacket(200, 0, 1, 2, 3, 4), 1_000, out _, out _));

        Assert.IsFalse(adapter.TryReset(initial, CreateCalibration(5),
            out Switch2PhysicalInputFailure same));
        Assert.AreEqual(Switch2PhysicalInputFailureKind.SessionRejected,
            same.Kind);
        Assert.AreEqual(Switch2InputSessionFailure.GenerationNotAdvanced,
            same.SessionFailure);

        Switch2PhysicalInputRegistration otherRegistration = Admit(
            CreateObservation(containerId: ContainerBGuid,
                inputContainerId: ContainerBGuid,
                commandContainerId: ContainerBGuid));
        Switch2PhysicalInputLifetime wrongController = CreateLifetime(
            otherRegistration, 6, 1);
        Assert.IsFalse(adapter.TryReset(wrongController, CreateCalibration(6),
            out Switch2PhysicalInputFailure registrationFailure));
        Assert.AreEqual(Switch2PhysicalInputFailureKind.RegistrationMismatch,
            registrationFailure.Kind);

        Switch2PhysicalInputLifetime transportAdvance = CreateLifetime(
            registration, 5, 10);
        Assert.IsTrue(adapter.TryReset(transportAdvance,
            CreateCalibration(5), out _));
        Assert.IsTrue(adapter.TryProcess(transportAdvance,
            BuildUsbPacket(200, 0, 1, 2, 3, 4), 1_001,
            out Switch2CanonicalInputFrame restarted, out _));
        Assert.AreEqual(Switch2CounterSequenceKind.First,
            restarted.CounterSequence);
    }

    [TestMethod]
    public void BoundaryDoesNotDependOnLegacyHidDeviceOrLiveHandleTypes()
    {
        Type hidDevice = typeof(HidDevice);
        Type[] boundaryTypes =
        {
            typeof(Switch2PhysicalDeviceFactory),
            typeof(Switch2PhysicalInputRegistration),
            typeof(Switch2PhysicalInputLifetime),
            typeof(Switch2PhysicalInputAdapter),
            typeof(Switch2PhysicalContainerIdentity),
            typeof(Switch2ProUsbCompositeObservation),
            typeof(Switch2UsbHidInterfaceObservation),
            typeof(Switch2UsbCommandInterfaceObservation),
        };

        foreach (Type boundaryType in boundaryTypes)
        {
            foreach (FieldInfo field in boundaryType.GetFields(
                         BindingFlags.Instance | BindingFlags.Static |
                         BindingFlags.Public | BindingFlags.NonPublic |
                         BindingFlags.DeclaredOnly))
            {
                Assert.IsFalse(ContainsType(field.FieldType, hidDevice),
                    $"{boundaryType.Name}.{field.Name} depends on HidDevice.");
            }
            foreach (MethodInfo method in boundaryType.GetMethods(
                         BindingFlags.Instance | BindingFlags.Static |
                         BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                Assert.IsFalse(ContainsType(method.ReturnType, hidDevice),
                    $"{boundaryType.Name}.{method.Name} returns HidDevice.");
                foreach (ParameterInfo parameter in method.GetParameters())
                {
                    Assert.IsFalse(ContainsType(parameter.ParameterType,
                            hidDevice),
                        $"{boundaryType.Name}.{method.Name} accepts HidDevice.");
                }
            }
        }
    }

    [TestMethod]
    public void AdmissionAndAdapterHotPathsAllocateNoManagedMemory()
    {
        Switch2ProUsbCompositeObservation observation = CreateObservation();
        Switch2PhysicalInputRegistration registration = Admit(observation);
        Switch2PhysicalInputLifetime lifetime = CreateLifetime(registration,
            1, 1);
        Assert.IsTrue(Switch2PhysicalInputAdapter.TryCreate(lifetime,
            CreateCalibration(1), out Switch2PhysicalInputAdapter adapter,
            out _));
        byte[] packet = BuildUsbPacket(0, 0, 1, 2, 3, 4);

        for (int warmup = 0; warmup < 2_000; warmup++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1, 4),
                (uint)(warmup * 4));
            Switch2PhysicalDeviceFactory.TryAdmitProUsb(observation,
                out _, out _);
            adapter.TryProcess(lifetime, packet, warmup, out _, out _);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool succeeded = true;
        for (int iteration = 2_000; iteration < 22_000; iteration++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1, 4),
                (uint)(iteration * 4));
            succeeded &= Switch2PhysicalDeviceFactory.TryAdmitProUsb(
                observation, out _, out _);
            succeeded &= adapter.TryProcess(lifetime, packet, iteration,
                out _, out _);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.IsTrue(succeeded);
        Assert.AreEqual(0L, allocated,
            $"Physical admission/input path allocated {allocated} bytes.");
    }

    private static bool ContainsType(Type candidate, Type forbidden)
    {
        if (candidate == forbidden)
        {
            return true;
        }
        if (candidate.HasElementType)
        {
            return ContainsType(candidate.GetElementType(), forbidden);
        }
        if (!candidate.IsGenericType)
        {
            return false;
        }
        return candidate.GetGenericArguments().Any(argument =>
            ContainsType(argument, forbidden));
    }

    private static void AssertSingleByte(Func<byte, bool> admit,
        byte expected, string field)
    {
        int admitted = 0;
        int admittedValue = -1;
        for (int raw = byte.MinValue; raw <= byte.MaxValue; raw++)
        {
            if (admit((byte)raw))
            {
                admitted++;
                admittedValue = raw;
            }
        }
        Assert.AreEqual(1, admitted, field);
        Assert.AreEqual((int)expected, admittedValue, field);
    }

    private static void AssertSingleUShort(Func<ushort, bool> admit,
        ushort expected, string field)
    {
        int admitted = 0;
        int admittedValue = -1;
        for (int raw = ushort.MinValue; raw <= ushort.MaxValue; raw++)
        {
            if (admit((ushort)raw))
            {
                admitted++;
                admittedValue = raw;
            }
        }
        Assert.AreEqual(1, admitted, field);
        Assert.AreEqual((int)expected, admittedValue, field);
    }

    private static void AssertRejected(
        in Switch2ProUsbCompositeObservation observation,
        Switch2PhysicalAdmissionFailure expected)
    {
        Assert.IsFalse(Switch2PhysicalDeviceFactory.TryAdmitProUsb(observation,
            out Switch2PhysicalInputRegistration registration,
            out Switch2PhysicalAdmissionFailure failure));
        Assert.IsFalse(registration.IsValid);
        Assert.AreEqual(expected, failure);
    }

    private static bool TryAdmit(
        in Switch2ProUsbCompositeObservation observation) =>
        Switch2PhysicalDeviceFactory.TryAdmitProUsb(observation, out _, out _);

    private static Switch2PhysicalInputRegistration Admit(
        in Switch2ProUsbCompositeObservation observation)
    {
        Assert.IsTrue(Switch2PhysicalDeviceFactory.TryAdmitProUsb(observation,
            out Switch2PhysicalInputRegistration registration,
            out Switch2PhysicalAdmissionFailure failure), failure.ToString());
        return registration;
    }

    private static Switch2PhysicalInputLifetime CreateLifetime(
        in Switch2PhysicalInputRegistration registration,
        ulong deviceGeneration, ulong transportGeneration,
        long qpcFrequency = 10_000_000)
    {
        Assert.IsTrue(Switch2PhysicalInputLifetime.TryCreate(registration,
            deviceGeneration, transportGeneration, qpcFrequency,
            out Switch2PhysicalInputLifetime lifetime));
        return lifetime;
    }

    private static Switch2InputCalibrationSnapshot CreateCalibration(
        ulong deviceGeneration)
    {
        Assert.IsTrue(Switch2InputCalibrationSnapshot.TryCreateFallback(
            Switch2ControllerModel.ProController2, deviceGeneration,
            out Switch2InputCalibrationSnapshot calibration));
        return calibration;
    }

    private static Switch2UsbPipeObservation BulkOut() => new(0x02,
        Switch2UsbPipeTransferType.Bulk, 64, 0);

    private static Switch2UsbPipeObservation BulkIn() => new(0x82,
        Switch2UsbPipeTransferType.Bulk, 64, 0);

    private static Switch2ProUsbCompositeObservation CreateObservation(
        ushort vendorId = 0x057E, ushort productId = 0x2069,
        ushort bcdDevice = 0x0201, Guid? containerId = null,
        Guid? inputContainerId = null, Guid? commandContainerId = null,
        byte matchingInputInterfaceCount = 1,
        byte matchingCommandInterfaceCount = 1,
        Switch2UsbBoundDriver inputDriver = Switch2UsbBoundDriver.HidClass,
        byte inputInterfaceNumber = 0, byte inputAlternateSetting = 0,
        ushort usagePage = 0x0001, ushort usage = 0x0005,
        ushort inputReportLength = 64, ushort outputReportLength = 64,
        ushort featureReportLength = 0,
        Switch2UsbBoundDriver commandDriver = Switch2UsbBoundDriver.WinUsb,
        byte commandInterfaceNumber = 1, byte commandAlternateSetting = 0,
        byte endpointCount = 2, Switch2UsbPipeObservation? pipe0 = null,
        Switch2UsbPipeObservation? pipe1 = null)
    {
        Switch2PhysicalContainerIdentity root = CreateContainerIdentity(
            containerId ?? ContainerAGuid);
        Switch2PhysicalContainerIdentity inputContainer =
            CreateContainerIdentity(inputContainerId ??
                (containerId ?? ContainerAGuid));
        Switch2PhysicalContainerIdentity commandContainer =
            CreateContainerIdentity(commandContainerId ??
                (containerId ?? ContainerAGuid));
        var input = new Switch2UsbHidInterfaceObservation(
            inputContainer, inputInterfaceNumber,
            inputAlternateSetting, inputDriver, usagePage, usage,
            inputReportLength, outputReportLength, featureReportLength);
        Switch2UsbPipeObservation first = pipe0 ?? BulkOut();
        Switch2UsbPipeObservation second = pipe1 ?? BulkIn();
        var command = new Switch2UsbCommandInterfaceObservation(
            commandContainer, commandInterfaceNumber,
            commandAlternateSetting, commandDriver, endpointCount, first,
            second);
        return new Switch2ProUsbCompositeObservation(vendorId, productId,
            bcdDevice, root, matchingInputInterfaceCount,
            matchingCommandInterfaceCount, input, command);
    }

    private static Switch2PhysicalContainerIdentity CreateContainerIdentity(
        Guid value)
    {
        Switch2PhysicalContainerIdentity.TryCreate(value, out var identity);
        return identity;
    }

    private static byte[] BuildUsbPacket(uint counter, uint buttons,
        ushort leftX, ushort leftY, ushort rightX, ushort rightY)
    {
        var packet = new byte[Switch2InputCodec.UsbPacketLength];
        packet[0] = (byte)Switch2InputReportKind.Common05;
        Span<byte> body = packet.AsSpan(1);
        BinaryPrimitives.WriteUInt32LittleEndian(body, counter);
        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(4), buttons);
        PackStick(body.Slice(0x0A, 3), leftX, leftY);
        PackStick(body.Slice(0x0D, 3), rightX, rightY);
        return packet;
    }

    private static void PackStick(Span<byte> destination, ushort x, ushort y)
    {
        destination[0] = (byte)x;
        destination[1] = (byte)((x >> 8) | (y << 4));
        destination[2] = (byte)(y >> 4);
    }
}
