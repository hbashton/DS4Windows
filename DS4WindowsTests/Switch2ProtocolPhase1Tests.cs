using System.Buffers.Binary;
using System.Text.Json;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public class Switch2ProtocolPhase1Tests
{
    private const string SyntheticSourceId =
        "fact-c068bab8361245fda0c3bc794cf960a3";
    private const string GoldenStreamId =
        "stream-10f49da474a04584b2bdc232c28578e4";
    private const string SanitizedCaptureId =
        "capture-ce006a25b40d468e90570a6e811a54f6";

    private const Switch2GattProperty InputProperties =
        Switch2GattProperty.Read | Switch2GattProperty.Notify;

    [TestMethod]
    public void Common05DecodesCaptureBackedOffsetsWithoutNormalization()
    {
        byte[] body = BuildCommonBody(0x44332211);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0x04, 4),
            0x88776655);
        PackStick(body, 0x0A, 0x0ABC, 0x0123);
        PackStick(body, 0x0D, 0x0456, 0x0789);
        WriteUInt16(body, 0x10, 1001);
        WriteUInt16(body, 0x12, 1002);
        WriteUInt16(body, 0x14, 1003);
        WriteUInt16(body, 0x16, 1004);
        WriteInt16(body, 0x19, -1);
        WriteInt16(body, 0x1B, 0x1234);
        WriteInt16(body, 0x1D, -300);
        WriteUInt16(body, 0x1F, 4200);
        body[0x21] = 0x34;
        WriteUInt16(body, 0x22, 0x5678);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0x2A, 4),
            0xA1B2C3D4);
        WriteInt16(body, 0x2E, -123);
        WriteInt16(body, 0x30, 1);
        WriteInt16(body, 0x32, -2);
        WriteInt16(body, 0x34, 3);
        WriteInt16(body, 0x36, -4);
        WriteInt16(body, 0x38, 5);
        WriteInt16(body, 0x3A, -6);
        bool decoded = Switch2InputCodec.TryDecodeBluetoothLe(
            Switch2InputCodec.ServiceUuid,
            Switch2InputCodec.Common05CharacteristicUuid, InputProperties,
            body, Switch2ControllerModel.ProController2, out var result);

        Assert.IsTrue(decoded);
        Assert.IsTrue(result.IsCommon);
        Assert.AreEqual(Switch2InputReportKind.Common05, result.Kind);
        Assert.AreEqual(0x44332211u, result.Common.Counter);
        Assert.AreEqual(0x88776655u, result.Common.Buttons);
        Assert.AreEqual((ushort)0x0ABC, result.Common.LeftStick.X);
        Assert.AreEqual((ushort)0x0123, result.Common.LeftStick.Y);
        Assert.AreEqual((ushort)0x0456, result.Common.RightStick.X);
        Assert.AreEqual((ushort)0x0789, result.Common.RightStick.Y);
        Assert.AreEqual((ushort)1001, result.Common.MouseX);
        Assert.AreEqual((ushort)1002, result.Common.MouseY);
        Assert.AreEqual((ushort)1003, result.Common.MouseUnknown0Raw);
        Assert.AreEqual((ushort)1004, result.Common.MouseUnknown1Raw);
        Assert.AreEqual((short)-1, result.Common.Magnetometer.X);
        Assert.AreEqual((short)0x1234, result.Common.Magnetometer.Y);
        Assert.AreEqual((short)-300, result.Common.Magnetometer.Z);
        Assert.AreEqual((ushort)4200,
            result.Common.BatteryVoltageMillivolts);
        Assert.AreEqual((byte)0x34, result.Common.ChargingState);
        Assert.AreEqual((ushort)0x5678, result.Common.BatteryCurrentRaw);
        Assert.AreEqual(0xA1B2C3D4u, result.Common.MotionTimestamp);
        Assert.AreEqual((ushort)0xFF85, result.Common.TemperatureRawBits);
        Assert.AreEqual(new Switch2Vector3Raw(1, -2, 3),
            result.Common.Accelerometer);
        Assert.AreEqual(new Switch2Vector3Raw(-4, 5, -6),
            result.Common.Gyroscope);
        Assert.IsTrue(result.HasLeftStick);
        Assert.IsTrue(result.HasRightStick);
        Assert.IsFalse(result.HasMouseData,
            "Common mouse bytes are not an applicable Pro control.");
    }

    [TestMethod]
    public void UsbCommon05RequiresOneReportIdPlusExactlySixtyThreeBytes()
    {
        byte[] packet = BuildUsbCommon(Switch2ControllerModel.ProController2,
            123);

        Assert.IsTrue(Switch2InputCodec.TryDecodeUsb(packet,
            Switch2ControllerModel.ProController2, out var report));
        Assert.AreEqual(123u, report.Counter);

        for (int length = 0; length <= 80; length++)
        {
            if (length == Switch2InputCodec.UsbPacketLength)
            {
                continue;
            }

            Assert.IsFalse(Switch2InputCodec.TryDecodeUsb(
                new byte[length], Switch2ControllerModel.ProController2,
                out _), $"Length {length} must be rejected.");
        }
    }

    [TestMethod]
    public void Common05ApplicabilityRejectsPhantomJoyConControls()
    {
        byte[] packet = BuildUsbCommon(Switch2ControllerModel.JoyCon2Left, 1);
        Assert.IsTrue(Switch2InputCodec.TryDecodeUsb(packet,
            Switch2ControllerModel.JoyCon2Left, out var left));
        Assert.IsTrue(left.HasLeftStick);
        Assert.IsFalse(left.HasRightStick);
        Assert.IsTrue(left.HasMouseData);

        Assert.IsTrue(Switch2InputCodec.TryDecodeUsb(packet,
            Switch2ControllerModel.JoyCon2Right, out var right));
        Assert.IsFalse(right.HasLeftStick);
        Assert.IsTrue(right.HasRightStick);
        Assert.IsTrue(right.HasMouseData);
    }

    [TestMethod]
    public void BluetoothCommon05RejectsEveryNonExactLength()
    {
        for (int length = 0; length <= 80; length++)
        {
            if (length == Switch2InputCodec.BluetoothLeBodyLength)
            {
                continue;
            }

            Assert.IsFalse(Switch2InputCodec.TryDecodeBluetoothLe(
                Switch2InputCodec.ServiceUuid,
                Switch2InputCodec.Common05CharacteristicUuid,
                InputProperties, new byte[length],
                Switch2ControllerModel.ProController2, out _),
                $"Length {length} must be rejected.");
        }
    }

    [TestMethod]
    public void BluetoothIdentityRequiresExactUuidPropertiesAndModel()
    {
        byte[] body = BuildBasicBody(Switch2InputReportKind.JoyCon2Left07,
            counter: 1, motionLength: 0);

        Assert.IsFalse(Switch2InputCodec.TryDecodeBluetoothLe(
            Switch2InputCodec.ServiceUuid,
            Switch2InputCodec.JoyCon2Left07CharacteristicUuid,
            Switch2GattProperty.Notify, body,
            Switch2ControllerModel.JoyCon2Left, out _));
        Assert.IsFalse(Switch2InputCodec.TryDecodeBluetoothLe(
            Switch2InputCodec.ServiceUuid,
            Switch2InputCodec.JoyCon2Left07CharacteristicUuid,
            InputProperties, body, Switch2ControllerModel.JoyCon2Right,
            out _));
        Assert.IsFalse(Switch2InputCodec.TryDecodeBluetoothLe(
            Switch2InputCodec.ServiceUuid, Guid.Empty, InputProperties, body,
            Switch2ControllerModel.JoyCon2Left, out _));
        Assert.IsFalse(Switch2InputCodec.TryDecodeBluetoothLe(
            Guid.NewGuid(),
            Switch2InputCodec.JoyCon2Left07CharacteristicUuid,
            InputProperties, body, Switch2ControllerModel.JoyCon2Left,
            out _));
        Assert.IsTrue(Switch2InputCodec.TryDecodeBluetoothLe(
            Switch2InputCodec.ServiceUuid,
            Switch2InputCodec.JoyCon2Left07CharacteristicUuid,
            InputProperties, body, Switch2ControllerModel.JoyCon2Left,
            out _));

        Assert.IsTrue(Switch2InputCodec.TryResolveBluetoothLeInputIdentity(
            Switch2InputCodec.ProController2_09CharacteristicUuid,
            out var identity));
        Assert.AreEqual(Switch2InputReportKind.ProController2_09,
            identity.ReportKind);
        Assert.AreEqual(Switch2ControllerModel.ProController2,
            identity.FixedModel);
        Assert.IsTrue(identity.HasRequiredProperties(InputProperties));
    }

    [TestMethod]
    public void JoyConReportsDecodeOnlyBasicFieldsAndKeepMotionOpaque()
    {
        byte[] left = BuildBasicBody(
            Switch2InputReportKind.JoyCon2Left07, 0xFE, 30);
        left[1] = 0x26;
        WriteUInt16(left, 2, 0xA55A);
        PackStick(left, 5, 0x321, 0xFED);
        for (int index = 0; index < 30; index++)
        {
            left[0x10 + index] = (byte)(index + 1);
        }

        Assert.IsTrue(Switch2InputCodec.TryDecodeJoyCon2Left07(left,
            out var leftReport));
        Assert.AreEqual((byte)0xFE, leftReport.Counter);
        Assert.AreEqual((byte)0x26, leftReport.PowerInfo);
        Assert.AreEqual(0xA55Au, leftReport.Buttons);
        Assert.AreEqual((ushort)0x321, leftReport.PrimaryStick.X);
        Assert.AreEqual((ushort)0xFED, leftReport.PrimaryStick.Y);
        Assert.IsFalse(leftReport.HasSecondaryStick);
        Assert.AreEqual((byte)0x10, leftReport.Motion.BodyOffset);
        Assert.AreEqual((byte)40, leftReport.Motion.Capacity);
        Assert.AreEqual((byte)30, leftReport.Motion.DeclaredLength);
        Assert.IsFalse(leftReport.Motion.IsDecoded);
        Assert.IsTrue(leftReport.Motion.UsesObservedLength);

        Assert.IsTrue(Switch2InputCodec.TryDecodeBluetoothLe(
            Switch2InputCodec.ServiceUuid,
            Switch2InputCodec.JoyCon2Left07CharacteristicUuid,
            InputProperties, left, Switch2ControllerModel.JoyCon2Left,
            out var decodedLeft));
        Assert.IsTrue(decodedLeft.TrySliceOpaqueMotionBody(left,
            out ReadOnlySpan<byte> opaqueMotion));
        Assert.AreEqual(30, opaqueMotion.Length);
        Assert.AreEqual((byte)1, opaqueMotion[0]);
        Assert.AreEqual((byte)30, opaqueMotion[^1]);
        byte savedLength = left[0x0F];
        left[0x0F] = 29;
        Assert.IsFalse(decodedLeft.TrySliceOpaqueMotionBody(left, out _));
        left[0x0F] = savedLength;
        Assert.IsFalse(decodedLeft.TrySliceOpaqueMotionBody(new byte[64],
            out _), "Motion offsets use the 63-byte body coordinate system.");
        Assert.IsTrue(decodedLeft.HasLeftStick);
        Assert.IsFalse(decodedLeft.HasRightStick);

        byte[] right = BuildBasicBody(
            Switch2InputReportKind.JoyCon2Right08, 2, 17);
        Assert.IsTrue(Switch2InputCodec.TryDecodeJoyCon2Right08(right,
            out var rightReport));
        Assert.IsFalse(rightReport.Motion.UsesObservedLength,
            "Lengths within the bounded region are retained but not promoted " +
            "to observed protocol values.");
    }

    [TestMethod]
    public void Pro09DecodesTwoSticksAndKeepsPackedMotionOpaque()
    {
        byte[] body = BuildBasicBody(
            Switch2InputReportKind.ProController2_09, 0x7F, 40);
        body[1] = 0x1E;
        body[2] = 0x11;
        body[3] = 0x22;
        body[4] = 0x33;
        PackStick(body, 5, 0x101, 0x202);
        PackStick(body, 8, 0x303, 0x404);

        Assert.IsTrue(Switch2InputCodec.TryDecodeProController2_09(body,
            out var report));
        Assert.AreEqual(0x00332211u, report.Buttons);
        Assert.AreEqual((ushort)0x101, report.PrimaryStick.X);
        Assert.AreEqual((ushort)0x202, report.PrimaryStick.Y);
        Assert.AreEqual((ushort)0x303, report.SecondaryStick.X);
        Assert.AreEqual((ushort)0x404, report.SecondaryStick.Y);
        Assert.IsTrue(report.HasSecondaryStick);
        Assert.AreEqual((byte)0x0F, report.Motion.BodyOffset);
        Assert.AreEqual((byte)40, report.Motion.DeclaredLength);
        Assert.IsFalse(report.Motion.IsDecoded);
    }

    [TestMethod]
    public void ModelSpecificMotionLengthCannotExceedItsFortyByteRegion()
    {
        foreach (Switch2InputReportKind kind in new[]
        {
            Switch2InputReportKind.JoyCon2Left07,
            Switch2InputReportKind.JoyCon2Right08,
            Switch2InputReportKind.ProController2_09,
        })
        {
            byte[] body = BuildBasicBody(kind, 0, 41);
            bool decoded = kind switch
            {
                Switch2InputReportKind.JoyCon2Left07 =>
                    Switch2InputCodec.TryDecodeJoyCon2Left07(body, out _),
                Switch2InputReportKind.JoyCon2Right08 =>
                    Switch2InputCodec.TryDecodeJoyCon2Right08(body, out _),
                _ => Switch2InputCodec.TryDecodeProController2_09(body,
                    out _),
            };
            Assert.IsFalse(decoded, $"{kind} accepted an oversized region.");
        }
    }

    [TestMethod]
    public void AdvertisementParserSeparatesCompanyVendorAndPrivacyState()
    {
        byte[] selectedHost = { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66 };
        byte[] value = BuildAdvertisement(
            Switch2AdvertisementCodec.ProController2ProductId);

        Assert.IsTrue(Switch2AdvertisementCodec.TryDecode(
            Switch2AdvertisementCodec.NintendoBluetoothCompanyId, value,
            selectedHost, out var standard));
        Assert.AreEqual(Switch2ControllerModel.ProController2,
            standard.Model);
        Assert.AreEqual(Switch2AdvertisementCodec.ProController2ProductId,
            standard.ProductId);
        Assert.IsFalse(standard.HasRememberedHost);
        Assert.AreEqual(Switch2AdvertisedHost.None, standard.Host);
        Assert.IsFalse(standard.IsWake);

        selectedHost.Reverse().ToArray().CopyTo(value, 10);
        Assert.IsTrue(Switch2AdvertisementCodec.TryDecode(
            Switch2AdvertisementCodec.NintendoBluetoothCompanyId, value,
            selectedHost, out var reconnect));
        Assert.IsTrue(reconnect.HasRememberedHost);
        Assert.IsTrue(reconnect.IsForThisHost);
        Assert.IsTrue(reconnect.IsReconnect);

        byte[] otherHost = { 1, 2, 3, 4, 5, 6 };
        Assert.IsTrue(Switch2AdvertisementCodec.TryDecode(
            Switch2AdvertisementCodec.NintendoBluetoothCompanyId, value,
            otherHost, out var foreign));
        Assert.AreEqual(Switch2AdvertisedHost.ForeignHost, foreign.Host);

        value[9] = 0x81;
        Assert.IsTrue(Switch2AdvertisementCodec.TryDecode(
            Switch2AdvertisementCodec.NintendoBluetoothCompanyId, value,
            selectedHost, out var wake));
        Assert.IsTrue(wake.IsWake);
        Assert.IsFalse(wake.IsReconnect);

        Array.Clear(value, 10, 6);
        Assert.IsTrue(Switch2AdvertisementCodec.TryDecode(
            Switch2AdvertisementCodec.NintendoBluetoothCompanyId, value,
            selectedHost, out var wakeWithoutHost));
        Assert.AreEqual(Switch2AdvertisedHost.None, wakeWithoutHost.Host);

        Assert.IsTrue(Switch2AdvertisementCodec.TryDecode(
            Switch2AdvertisementCodec.NintendoBluetoothCompanyId, value,
            selectedHost, out var duplicate));
        Assert.AreEqual(wakeWithoutHost.Host, duplicate.Host);
    }

    [TestMethod]
    public void AdvertisementParserRejectsUnknownOrMalformedVersionOneData()
    {
        byte[] selectedHost = { 1, 2, 3, 4, 5, 6 };
        byte[] value = BuildAdvertisement(
            Switch2AdvertisementCodec.JoyCon2LeftProductId);

        Assert.IsFalse(Switch2AdvertisementCodec.TryDecode(0x057E, value,
            selectedHost,
            out _), "USB vendor ID is not the BLE company ID.");
        Assert.IsFalse(Switch2AdvertisementCodec.TryDecode(
            Switch2AdvertisementCodec.NintendoBluetoothCompanyId,
            value.AsSpan(0, value.Length - 1), selectedHost, out _));

        value[17] = 1;
        Assert.IsFalse(Switch2AdvertisementCodec.TryDecode(
            Switch2AdvertisementCodec.NintendoBluetoothCompanyId, value,
            selectedHost, out _));

        value = BuildAdvertisement(0xFFFF);
        Assert.IsFalse(Switch2AdvertisementCodec.TryDecode(
            Switch2AdvertisementCodec.NintendoBluetoothCompanyId, value,
            selectedHost, out _));
        Assert.IsFalse(Switch2AdvertisementCodec.TryDecode(
            Switch2AdvertisementCodec.NintendoBluetoothCompanyId, value,
            ReadOnlySpan<byte>.Empty, out _));
    }

    [TestMethod]
    public void CalibrationParserUsesSixPackedTwelveBitValues()
    {
        ushort[] expected = { 0x801, 0x802, 0x701, 0x702, 0x601, 0x602 };
        byte[] bytes = PackCalibration(expected);

        Assert.IsTrue(Switch2CalibrationCodec.TryDecodeStick(bytes,
            out var calibration));
        Assert.AreEqual(expected[0], calibration.NeutralX);
        Assert.AreEqual(expected[1], calibration.NeutralY);
        Assert.AreEqual(expected[2], calibration.PositiveRangeX);
        Assert.AreEqual(expected[3], calibration.PositiveRangeY);
        Assert.AreEqual(expected[4], calibration.NegativeRangeX);
        Assert.AreEqual(expected[5], calibration.NegativeRangeY);
        Assert.IsTrue(calibration.IsUsable);

        Assert.IsTrue(Switch2CalibrationCodec.TryGetFactoryStickMetadata(
            Switch2ControllerModel.ProController2, Switch2StickSide.Left,
            out var left));
        Assert.AreEqual(0x0130A8u, left.Address);
        Assert.AreEqual((byte)9, left.Length);
        Assert.IsTrue(Switch2CalibrationCodec.TryGetFactoryStickMetadata(
            Switch2ControllerModel.ProController2, Switch2StickSide.Right,
            out var right));
        Assert.AreEqual(0x0130E8u, right.Address);
        Assert.IsTrue(Switch2CalibrationCodec.TryGetFactoryStickMetadata(
            Switch2ControllerModel.JoyCon2Left, Switch2StickSide.Left, out _));
        Assert.IsFalse(Switch2CalibrationCodec.TryGetFactoryStickMetadata(
            Switch2ControllerModel.JoyCon2Left, Switch2StickSide.Right, out _));
        Assert.IsFalse(Switch2CalibrationCodec.TryGetFactoryStickMetadata(
            Switch2ControllerModel.JoyCon2Right, Switch2StickSide.Left, out _));
        Assert.IsTrue(Switch2CalibrationCodec.TryGetFactoryStickMetadata(
            Switch2ControllerModel.JoyCon2Right, Switch2StickSide.Right, out _));
        Assert.IsFalse(Switch2CalibrationCodec.SupportsLiveUserCalibration);
        Assert.IsFalse(Switch2CalibrationCodec.TryGetLiveUserStickAddress(
            Switch2ControllerModel.ProController2, Switch2StickSide.Left,
            out uint userAddress));
        Assert.AreEqual(0u, userAddress);

        Assert.IsTrue(Switch2CalibrationCodec.TryDecodeStick(new byte[9],
            out var allZero));
        Assert.IsFalse(allZero.IsUsable);
        byte[] allOnes = Enumerable.Repeat((byte)0xFF, 9).ToArray();
        Assert.IsTrue(Switch2CalibrationCodec.TryDecodeStick(allOnes,
            out var saturated));
        Assert.IsFalse(saturated.IsUsable);
    }

    [TestMethod]
    public void CalibrationPackingPropertyHoldsAcrossDeterministicFuzzSet()
    {
        var random = new Random(0x5202);
        var expected = new ushort[6];
        for (int iteration = 0; iteration < 10000; iteration++)
        {
            for (int value = 0; value < expected.Length; value++)
            {
                expected[value] = (ushort)random.Next(0x1000);
            }

            Assert.IsTrue(Switch2CalibrationCodec.TryDecodeStick(
                PackCalibration(expected), out var calibration));
            Assert.AreEqual(expected[0], calibration.NeutralX);
            Assert.AreEqual(expected[1], calibration.NeutralY);
            Assert.AreEqual(expected[2], calibration.PositiveRangeX);
            Assert.AreEqual(expected[3], calibration.PositiveRangeY);
            Assert.AreEqual(expected[4], calibration.NegativeRangeX);
            Assert.AreEqual(expected[5], calibration.NegativeRangeY);
        }

        for (int length = 0; length < 20; length++)
        {
            if (length == Switch2CalibrationCodec.StickCalibrationLength)
            {
                continue;
            }

            Assert.IsFalse(Switch2CalibrationCodec.TryDecodeStick(
                new byte[length], out _));
        }
    }

    [TestMethod]
    public void FixtureEnvelopeClonesSourceAndReturnedBytes()
    {
        byte[] packet = BuildUsbCommon(Switch2ControllerModel.ProController2,
            10);
        var fixture = UsbFixture("pro", 1, 100, packet);
        packet[0] = 0xFF;

        byte[] firstRead = fixture.CopyBytes();
        Assert.AreEqual((byte)Switch2InputReportKind.Common05,
            firstRead[0]);

        byte[] copy = fixture.CopyBytes();
        copy[0] = 0xEE;
        byte[] secondRead = fixture.CopyBytes();
        Assert.AreEqual((byte)Switch2InputReportKind.Common05,
            secondRead[0]);
        Assert.AreEqual(Switch2FixtureEnvelope.CurrentSchemaVersion,
            fixture.SchemaVersion);
        Assert.AreEqual(Switch2PacketDirection.Input, fixture.Direction);
    }

    [TestMethod]
    public void DerivedUsbBcd0201GoldenRetainsOnlyProtocolFields()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestData",
            "Switch2", "pro-controller2-usb-bcd0201-common05.json");
        string json = File.ReadAllText(path);
        Assert.IsFalse(json.Contains("devicePathSha256",
            StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("sourceSha256",
            StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("exactBytes",
            StringComparison.OrdinalIgnoreCase));

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        Assert.AreEqual(Switch2FixtureEnvelope.CurrentSchemaVersion,
            root.GetProperty("schemaVersion").GetInt32());
        Assert.AreEqual("derivedGolden",
            root.GetProperty("sourceKind").GetString());
        string derivedSourceId = root.GetProperty(
            "derivedSourceId").GetString();
        Assert.AreEqual(2, root.GetProperty(
            "derivationManifestVersion").GetInt32());
        string clockDomain = root.GetProperty("hostClockDomain").GetString();
        long frequency = root.GetProperty(
            "hostTimestampFrequency").GetInt64();
        var source = Switch2FixtureSource.DerivedGolden(derivedSourceId,
            root.GetProperty("derivationManifestVersion").GetUInt16());
        var fixtures = new List<Switch2FixtureEnvelope>();

        foreach (JsonElement record in root.GetProperty("records")
                     .EnumerateArray())
        {
            byte[] packet = Convert.FromHexString(
                record.GetProperty("derivedBytes").GetString());
            AssertDerivedGoldenHasOnlyRetainedFields(packet);
            fixtures.Add(Switch2FixtureEnvelope.CreateUsb(GoldenStreamId,
                source, Switch2ControllerModel.ProController2, "unknown",
                record.GetProperty("deviceGeneration").GetUInt64(), 0,
                clockDomain, frequency,
                record.GetProperty("hostTimestampTicks").GetInt64(),
                packet));
        }

        var collector = new ReplayCollector();
        Assert.IsTrue(Switch2ReplayEngine.TryReplay(fixtures,
            collector.OnEvent, out var failure), failure.Kind.ToString());
        Assert.AreEqual(2, collector.Events.Count);
        Assert.AreEqual(0x00052005u,
            collector.Events[0].Report.Common.Counter);
        Assert.AreEqual((ushort)0x07E1,
            collector.Events[0].Report.Common.LeftStick.X);
        Assert.AreEqual((ushort)0x0833,
            collector.Events[0].Report.Common.LeftStick.Y);
        Assert.AreEqual((ushort)0x0854,
            collector.Events[0].Report.Common.RightStick.X);
        Assert.AreEqual((ushort)0x0816,
            collector.Events[0].Report.Common.RightStick.Y);
        Assert.AreEqual(4u, collector.Events[1].CounterDelta);
        Assert.AreEqual(Switch2CounterSequenceKind.Forward,
            collector.Events[1].CounterSequence,
            "A live USB +4 is raw forward movement, not packet loss.");
    }

    private static void AssertDerivedGoldenHasOnlyRetainedFields(
        byte[] packet)
    {
        Assert.AreEqual(Switch2InputCodec.UsbPacketLength, packet.Length);

        for (int offset = 0x09; offset <= 0x0A; offset++)
        {
            Assert.AreEqual((byte)0, packet[offset],
                $"Uninterpreted packet byte 0x{offset:X2} must be zero.");
        }

        for (int offset = 0x11; offset < packet.Length; offset++)
        {
            Assert.AreEqual((byte)0, packet[offset],
                $"Environmental/opaque packet byte 0x{offset:X2} must be zero.");
        }
    }

    [TestMethod]
    public void ReplayPreservesEveryRecordAndComputesThirtyTwoBitWrap()
    {
        var fixtures = new[]
        {
            UsbFixture("pro", 1, 10,
                BuildUsbCommon(Switch2ControllerModel.ProController2,
                    uint.MaxValue - 1)),
            UsbFixture("pro", 1, 11,
                BuildUsbCommon(Switch2ControllerModel.ProController2, 0)),
            UsbFixture("pro", 1, 12,
                BuildUsbCommon(Switch2ControllerModel.ProController2, 1)),
        };
        var collector = new ReplayCollector();

        Assert.IsTrue(Switch2ReplayEngine.TryReplay(fixtures,
            collector.OnEvent, out var failure), failure.Kind.ToString());
        Assert.AreEqual(3, collector.Events.Count);
        Assert.AreEqual(0, collector.Events[0].SequenceIndex);
        Assert.AreEqual(1, collector.Events[1].SequenceIndex);
        Assert.AreEqual(2, collector.Events[2].SequenceIndex);
        Assert.IsFalse(collector.Events[0].HasCounterDelta);
        Assert.AreEqual(2u, collector.Events[1].CounterDelta);
        Assert.AreEqual(Switch2CounterSequenceKind.Forward,
            collector.Events[1].CounterSequence);
        Assert.AreEqual(1u, collector.Events[2].CounterDelta);
    }

    [TestMethod]
    public void ReplayComputesEightBitWrapAndResetsAtGenerationBoundary()
    {
        var fixtures = new[]
        {
            BleFixture("left", Switch2ControllerModel.JoyCon2Left, 3, 10,
                Switch2InputCodec.JoyCon2Left07CharacteristicUuid,
                BuildBasicBody(Switch2InputReportKind.JoyCon2Left07,
                    255, 0)),
            BleFixture("left", Switch2ControllerModel.JoyCon2Left, 3, 11,
                Switch2InputCodec.JoyCon2Left07CharacteristicUuid,
                BuildBasicBody(Switch2InputReportKind.JoyCon2Left07,
                    1, 0)),
            BleFixture("left", Switch2ControllerModel.JoyCon2Left, 4, 12,
                Switch2InputCodec.JoyCon2Left07CharacteristicUuid,
                BuildBasicBody(Switch2InputReportKind.JoyCon2Left07,
                    200, 0)),
        };
        var collector = new ReplayCollector();

        Assert.IsTrue(Switch2ReplayEngine.TryReplay(fixtures,
            collector.OnEvent, out _));
        Assert.IsFalse(collector.Events[0].HasCounterDelta);
        Assert.IsTrue(collector.Events[1].HasCounterDelta);
        Assert.AreEqual(2u, collector.Events[1].CounterDelta);
        Assert.AreEqual(Switch2CounterSequenceKind.Forward,
            collector.Events[1].CounterSequence);
        Assert.IsFalse(collector.Events[2].HasCounterDelta,
            "A reconnect generation must not inherit counter history.");
    }

    [TestMethod]
    public void ReplaySurfacesDuplicateAndBackwardCounterMovement()
    {
        var fixtures = new[]
        {
            UsbFixture("pro", 1, 10,
                BuildUsbCommon(Switch2ControllerModel.ProController2, 10)),
            UsbFixture("pro", 1, 11,
                BuildUsbCommon(Switch2ControllerModel.ProController2, 10)),
            UsbFixture("pro", 1, 12,
                BuildUsbCommon(Switch2ControllerModel.ProController2, 9)),
            UsbFixture("pro", 1, 13,
                BuildUsbCommon(Switch2ControllerModel.ProController2, 11)),
        };
        var collector = new ReplayCollector();

        Assert.IsTrue(Switch2ReplayEngine.TryReplay(fixtures,
            collector.OnEvent, out _));
        Assert.AreEqual(Switch2CounterSequenceKind.First,
            collector.Events[0].CounterSequence);
        Assert.AreEqual(Switch2CounterSequenceKind.Duplicate,
            collector.Events[1].CounterSequence);
        Assert.AreEqual(0u, collector.Events[1].CounterDelta);
        Assert.AreEqual(Switch2CounterSequenceKind.BackwardOrOutOfOrder,
            collector.Events[2].CounterSequence);
        Assert.AreEqual(uint.MaxValue, collector.Events[2].CounterDelta);
        Assert.AreEqual(1u, collector.Events[3].CounterDelta,
            "A reordered frame must not regress the sequence baseline.");
        Assert.AreEqual(Switch2CounterSequenceKind.Forward,
            collector.Events[3].CounterSequence);
    }

    [TestMethod]
    public void FixtureMetadataRejectsLikelyIdentifiersAndUnredactedSources()
    {
        Assert.ThrowsException<ArgumentException>(() =>
            Switch2FixtureSource.Synthetic("AA11BB22CC33"));
        Assert.ThrowsException<ArgumentException>(() =>
            Switch2FixtureSource.Synthetic(new string('A', 32)));
        Assert.ThrowsException<ArgumentException>(() =>
            Switch2FixtureSource.Synthetic("serial-number"));
        Assert.ThrowsException<ArgumentException>(() =>
            Switch2FixtureSource.Synthetic("fact-AA-BB-CC-DD-EE-FF"));
        Assert.ThrowsException<ArgumentException>(() =>
            Switch2FixtureSource.Synthetic(
                "fact-00000000000000000000000000000000"));
        Assert.ThrowsException<ArgumentException>(() =>
            Switch2FixtureSource.SanitizedCapture(SanitizedCaptureId, "BAD", 1));
        Assert.ThrowsException<ArgumentException>(() =>
            Switch2FixtureSource.SanitizedCapture(SanitizedCaptureId,
                new string('A', 64), 0));
        Assert.ThrowsException<ArgumentException>(() =>
            Switch2FixtureSource.DerivedGolden(
                "fact-ce006a25b40d468e90570a6e811a54f6", 2));
        Assert.ThrowsException<ArgumentException>(() =>
            Switch2FixtureSource.DerivedGolden(
                "golden-ce006a25b40d468e90570a6e811a54f6", 0));

        var derived = Switch2FixtureSource.DerivedGolden(
            "golden-ce006a25b40d468e90570a6e811a54f6", 2);
        Assert.AreEqual(Switch2FixtureEvidence.ProjectOwnedDerivedGolden,
            derived.Evidence);
        Assert.AreEqual((ushort)2, derived.DerivationManifestVersion);
        Assert.AreEqual(string.Empty, derived.SourceSha256);

        var source = Switch2FixtureSource.Synthetic(SyntheticSourceId);
        Assert.ThrowsException<ArgumentException>(() =>
            Switch2FixtureEnvelope.CreateUsb("stream-AA-BB-CC-DD-EE-FF",
                source, Switch2ControllerModel.ProController2, "unknown", 1,
                0, OpaqueClockId("test-qpc"), TimeSpan.TicksPerSecond, 1,
                BuildUsbCommon(Switch2ControllerModel.ProController2, 1)));
        Assert.ThrowsException<ArgumentException>(() =>
            Switch2FixtureEnvelope.CreateUsb("stream-AABB.CCDD.EEFF",
                source, Switch2ControllerModel.ProController2, "unknown", 1,
                0, OpaqueClockId("test-qpc"), TimeSpan.TicksPerSecond, 1,
                BuildUsbCommon(Switch2ControllerModel.ProController2, 1)));
        Assert.ThrowsException<ArgumentException>(() =>
            Switch2FixtureEnvelope.CreateUsb(OpaqueStreamId("pro"), source,
                Switch2ControllerModel.ProController2,
                "AA-BB-CC-DD-EE-FF", 1, 0, OpaqueClockId("test-qpc"),
                TimeSpan.TicksPerSecond, 1,
                BuildUsbCommon(Switch2ControllerModel.ProController2, 1)));
    }

    [TestMethod]
    public void ReplayRejectsTimestampAndGenerationRegression()
    {
        var timestampRegression = new[]
        {
            UsbFixture("pro", 1, 20,
                BuildUsbCommon(Switch2ControllerModel.ProController2, 1)),
            UsbFixture("pro", 1, 19,
                BuildUsbCommon(Switch2ControllerModel.ProController2, 2)),
        };
        var collector = new ReplayCollector();
        Assert.IsFalse(Switch2ReplayEngine.TryReplay(timestampRegression,
            collector.OnEvent, out var timestampFailure));
        Assert.AreEqual(Switch2ReplayFailureKind.TimestampRegression,
            timestampFailure.Kind);
        Assert.AreEqual(1, timestampFailure.FixtureIndex);

        var generationRegression = new[]
        {
            UsbFixture("pro", 2, 20,
                BuildUsbCommon(Switch2ControllerModel.ProController2, 1)),
            UsbFixture("pro", 1, 21,
                BuildUsbCommon(Switch2ControllerModel.ProController2, 2)),
        };
        collector = new ReplayCollector();
        Assert.IsFalse(Switch2ReplayEngine.TryReplay(generationRegression,
            collector.OnEvent, out var generationFailure));
        Assert.AreEqual(Switch2ReplayFailureKind.GenerationRegression,
            generationFailure.Kind);

        var frequencyMismatch = new[]
        {
            UsbFixture("clock-a", 1, 20,
                BuildUsbCommon(Switch2ControllerModel.ProController2, 1)),
            UsbFixture("clock-b", 1, 21,
                BuildUsbCommon(Switch2ControllerModel.ProController2, 2),
                frequency: TimeSpan.TicksPerSecond / 2),
        };
        collector = new ReplayCollector();
        Assert.IsFalse(Switch2ReplayEngine.TryReplay(frequencyMismatch,
            collector.OnEvent, out var clockFailure));
        Assert.AreEqual(Switch2ReplayFailureKind.ClockFrequencyMismatch,
            clockFailure.Kind);
    }

    [TestMethod]
    public void ReplayRejectsIdentityDriftWithinStreamGeneration()
    {
        string streamId = OpaqueStreamId("pro");
        string clockId = OpaqueClockId("test-qpc");
        var source = Switch2FixtureSource.Synthetic(SyntheticSourceId);
        var alternateSource = Switch2FixtureSource.Synthetic(
            "fact-ce006a25b40d468e90570a6e811a54f6");
        var baseline = Switch2FixtureEnvelope.CreateUsb(streamId, source,
            Switch2ControllerModel.ProController2, "unknown", 1, 0, clockId,
            TimeSpan.TicksPerSecond, 10,
            BuildUsbCommon(Switch2ControllerModel.ProController2, 1));
        Switch2FixtureEnvelope[] driftedIdentities =
        {
            Switch2FixtureEnvelope.CreateUsb(streamId, source,
                Switch2ControllerModel.JoyCon2Left, "unknown", 1, 0, clockId,
                TimeSpan.TicksPerSecond, 11,
                BuildUsbCommon(Switch2ControllerModel.JoyCon2Left, 2)),
            Switch2FixtureEnvelope.CreateBluetoothLe(streamId, source,
                Switch2ControllerModel.ProController2, "unknown", 1, 0,
                clockId, TimeSpan.TicksPerSecond, 11,
                Switch2InputCodec.ServiceUuid,
                Switch2InputCodec.Common05CharacteristicUuid,
                InputProperties, BuildCommonBody(2)),
            Switch2FixtureEnvelope.CreateUsb(streamId, alternateSource,
                Switch2ControllerModel.ProController2, "unknown", 1, 0,
                clockId, TimeSpan.TicksPerSecond, 11,
                BuildUsbCommon(Switch2ControllerModel.ProController2, 2)),
            Switch2FixtureEnvelope.CreateUsb(streamId, source,
                Switch2ControllerModel.ProController2, "fw-2.0", 1, 0,
                clockId, TimeSpan.TicksPerSecond, 11,
                BuildUsbCommon(Switch2ControllerModel.ProController2, 2)),
            Switch2FixtureEnvelope.CreateUsb(streamId, source,
                Switch2ControllerModel.ProController2, "unknown", 1, 0,
                OpaqueClockId("other-qpc"), TimeSpan.TicksPerSecond, 11,
                BuildUsbCommon(Switch2ControllerModel.ProController2, 2)),
            Switch2FixtureEnvelope.CreateUsb(streamId, source,
                Switch2ControllerModel.ProController2, "unknown", 1, 7,
                clockId, TimeSpan.TicksPerSecond, 11,
                BuildUsbCommon(Switch2ControllerModel.ProController2, 2)),
        };

        foreach (Switch2FixtureEnvelope drifted in driftedIdentities)
        {
            var collector = new ReplayCollector();
            Assert.IsFalse(Switch2ReplayEngine.TryReplay(
                new[] { baseline, drifted }, collector.OnEvent,
                out var failure));
            Assert.AreEqual(Switch2ReplayFailureKind.StreamIdentityMismatch,
                failure.Kind);
            Assert.AreEqual(1, failure.FixtureIndex);
        }
    }

    [TestMethod]
    public void ReplayStopsAtMalformedRecordWithoutCoalescingPastIt()
    {
        var fixtures = new[]
        {
            UsbFixture("pro", 1, 10,
                BuildUsbCommon(Switch2ControllerModel.ProController2, 1)),
            UsbFixture("pro", 1, 11, new byte[63]),
            UsbFixture("pro", 1, 12,
                BuildUsbCommon(Switch2ControllerModel.ProController2, 3)),
        };
        var collector = new ReplayCollector();

        Assert.IsFalse(Switch2ReplayEngine.TryReplay(fixtures,
            collector.OnEvent, out var failure));
        Assert.AreEqual(Switch2ReplayFailureKind.InvalidFramingOrReport,
            failure.Kind);
        Assert.AreEqual(1, failure.FixtureIndex);
        Assert.AreEqual(1, collector.Events.Count);
    }

    [TestMethod]
    public void ReplayRejectsTruncatedAndOversizedUsbAndBleFixtures()
    {
        foreach (int length in new[] { 0, 62, 63, 65, 127 })
        {
            var collector = new ReplayCollector();
            Assert.IsFalse(Switch2ReplayEngine.TryReplay(new[]
            {
                UsbFixture("pro", 1, 10, new byte[length]),
            }, collector.OnEvent, out var failure));
            Assert.AreEqual(Switch2ReplayFailureKind.InvalidFramingOrReport,
                failure.Kind);
        }

        foreach (int length in new[] { 0, 61, 62, 64, 127 })
        {
            var collector = new ReplayCollector();
            Assert.IsFalse(Switch2ReplayEngine.TryReplay(new[]
            {
                BleFixture("pro", Switch2ControllerModel.ProController2,
                    1, 10, Switch2InputCodec.Common05CharacteristicUuid,
                    new byte[length]),
            }, collector.OnEvent, out var failure));
            Assert.AreEqual(Switch2ReplayFailureKind.InvalidFramingOrReport,
                failure.Kind);
        }
    }

    [TestMethod]
    public void JoyConPairSkewRequiresSharedClockAndPairEpoch()
    {
        var collector = new ReplayCollector();
        var fixtures = new[]
        {
            BleFixture("left", Switch2ControllerModel.JoyCon2Left, 7, 100,
                Switch2InputCodec.JoyCon2Left07CharacteristicUuid,
                BuildBasicBody(Switch2InputReportKind.JoyCon2Left07, 1, 0),
                pairEpoch: 9),
            BleFixture("right", Switch2ControllerModel.JoyCon2Right, 7, 112,
                Switch2InputCodec.JoyCon2Right08CharacteristicUuid,
                BuildBasicBody(Switch2InputReportKind.JoyCon2Right08, 1, 0),
                pairEpoch: 9),
        };
        Assert.IsTrue(Switch2ReplayEngine.TryReplay(fixtures,
            collector.OnEvent, out _));

        Switch2ReplayEvent left = collector.Events[0];
        Switch2ReplayEvent right = collector.Events[1];
        Assert.IsTrue(Switch2JoyConPairSkewEvaluator.TryEvaluate(in left,
            in right, TimeSpan.FromTicks(10), out var skew));
        Assert.AreEqual(TimeSpan.FromTicks(12), skew.Skew);
        Assert.AreEqual(Switch2JoyConStaleSide.Left, skew.StaleSide);
        Assert.IsFalse(skew.IsWithinBudget);

        Assert.IsTrue(Switch2JoyConPairSkewEvaluator.TryEvaluate(in left,
            in right, TimeSpan.FromTicks(20), out var withinBudget));
        Assert.IsTrue(withinBudget.IsWithinBudget);
        Assert.AreEqual(Switch2JoyConStaleSide.None, withinBudget.StaleSide);

        var nextPairEpoch = BleFixture("right",
            Switch2ControllerModel.JoyCon2Right, 8, 113,
            Switch2InputCodec.JoyCon2Right08CharacteristicUuid,
            BuildBasicBody(Switch2InputReportKind.JoyCon2Right08, 2, 0),
            pairEpoch: 10);
        var nextCollector = new ReplayCollector();
        Assert.IsTrue(Switch2ReplayEngine.TryReplay(new[] { nextPairEpoch },
            nextCollector.OnEvent, out _));
        Switch2ReplayEvent nextRight = nextCollector.Events[0];
        Assert.IsFalse(Switch2JoyConPairSkewEvaluator.TryEvaluate(in left,
            in nextRight, TimeSpan.FromTicks(20), out _));

        var otherClock = BleFixture("right-other-clock",
            Switch2ControllerModel.JoyCon2Right, 1, 113,
            Switch2InputCodec.JoyCon2Right08CharacteristicUuid,
            BuildBasicBody(Switch2InputReportKind.JoyCon2Right08, 2, 0),
            pairEpoch: 9, clockDomain: "other-qpc");
        nextCollector = new ReplayCollector();
        Assert.IsTrue(Switch2ReplayEngine.TryReplay(new[] { otherClock },
            nextCollector.OnEvent, out _));
        nextRight = nextCollector.Events[0];
        Assert.IsFalse(Switch2JoyConPairSkewEvaluator.TryEvaluate(in left,
            in nextRight, TimeSpan.FromTicks(20), out _));
    }

    [TestMethod]
    public void ExactLengthDecodersSurviveDeterministicFuzzWithoutThrowing()
    {
        var random = new Random(0x2069);
        var common = new byte[63];
        var basic = new byte[63];

        for (int iteration = 0; iteration < 20000; iteration++)
        {
            random.NextBytes(common);
            Assert.IsTrue(Switch2InputCodec.TryDecodeCommon05(common,
                out _));

            random.NextBytes(basic);
            basic[0x0F] = (byte)random.Next(41);
            Assert.IsTrue(Switch2InputCodec.TryDecodeJoyCon2Left07(basic,
                out _));
            Assert.IsTrue(Switch2InputCodec.TryDecodeJoyCon2Right08(basic,
                out _));

            random.NextBytes(basic);
            basic[0x0E] = (byte)random.Next(41);
            Assert.IsTrue(Switch2InputCodec.TryDecodeProController2_09(basic,
                out _));
        }
    }

    [TestMethod]
    public void AllStrictInputCodecsAllocateNoManagedMemoryPerDecode()
    {
        byte[] packet = BuildUsbCommon(Switch2ControllerModel.ProController2,
            1);
        byte[] common = BuildCommonBody(1);
        byte[] left = BuildBasicBody(Switch2InputReportKind.JoyCon2Left07,
            1, 30);
        byte[] right = BuildBasicBody(Switch2InputReportKind.JoyCon2Right08,
            1, 30);
        byte[] pro = BuildBasicBody(Switch2InputReportKind.ProController2_09,
            1, 40);
        for (int warmup = 0; warmup < 1000; warmup++)
        {
            Switch2InputCodec.TryDecodeUsb(packet,
                Switch2ControllerModel.ProController2, out _);
            Switch2InputCodec.TryDecodeBluetoothLe(
                Switch2InputCodec.ServiceUuid,
                Switch2InputCodec.Common05CharacteristicUuid,
                InputProperties, common, Switch2ControllerModel.ProController2,
                out _);
            Switch2InputCodec.TryDecodeJoyCon2Left07(left, out _);
            Switch2InputCodec.TryDecodeJoyCon2Right08(right, out _);
            Switch2InputCodec.TryDecodeProController2_09(pro, out _);
            Switch2InputCodec.TryDecodeBluetoothLe(
                Switch2InputCodec.ServiceUuid,
                Switch2InputCodec.ProController2_09CharacteristicUuid,
                InputProperties, pro, Switch2ControllerModel.ProController2,
                out _);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool allDecoded = true;
        for (int iteration = 0; iteration < 10000; iteration++)
        {
            allDecoded &= Switch2InputCodec.TryDecodeUsb(packet,
                Switch2ControllerModel.ProController2, out _);
            allDecoded &= Switch2InputCodec.TryDecodeBluetoothLe(
                Switch2InputCodec.ServiceUuid,
                Switch2InputCodec.Common05CharacteristicUuid,
                InputProperties, common, Switch2ControllerModel.ProController2,
                out _);
            allDecoded &= Switch2InputCodec.TryDecodeJoyCon2Left07(left,
                out _);
            allDecoded &= Switch2InputCodec.TryDecodeJoyCon2Right08(right,
                out _);
            allDecoded &= Switch2InputCodec.TryDecodeProController2_09(pro,
                out _);
            allDecoded &= Switch2InputCodec.TryDecodeBluetoothLe(
                Switch2InputCodec.ServiceUuid,
                Switch2InputCodec.ProController2_09CharacteristicUuid,
                InputProperties, pro, Switch2ControllerModel.ProController2,
                out _);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.IsTrue(allDecoded);
        Assert.AreEqual(0L, allocated,
            "The pure packet hot codec should remain allocation-free.");
    }

    private static Switch2FixtureEnvelope UsbFixture(string streamId,
        ulong generation, long ticks, byte[] bytes,
        string clockDomain = "test-qpc",
        long frequency = TimeSpan.TicksPerSecond) =>
        Switch2FixtureEnvelope.CreateUsb(OpaqueStreamId(streamId),
            Switch2FixtureSource.Synthetic(SyntheticSourceId),
            Switch2ControllerModel.ProController2, "unknown",
            generation, 0, OpaqueClockId(clockDomain), frequency, ticks, bytes);

    private static Switch2FixtureEnvelope BleFixture(string streamId,
        Switch2ControllerModel model, ulong generation, long ticks,
        Guid characteristicUuid, byte[] bytes, ulong pairEpoch = 0,
        string clockDomain = "test-qpc") =>
        Switch2FixtureEnvelope.CreateBluetoothLe(OpaqueStreamId(streamId),
            Switch2FixtureSource.Synthetic(SyntheticSourceId), model,
            "unknown", generation, pairEpoch, OpaqueClockId(clockDomain),
            TimeSpan.TicksPerSecond, ticks, Switch2InputCodec.ServiceUuid,
            characteristicUuid, InputProperties, bytes);

    private static string OpaqueStreamId(string semanticName) =>
        semanticName switch
        {
            "pro" => "stream-9f9cee30fc1c4bbe98d9b5ca840580fa",
            "left" => "stream-f507a392b3294e879d3ef2a97c4d202c",
            "right" or "right-other-clock" =>
                "stream-fe2f829b99f04a57a3b79cf43687abd1",
            "clock-a" => "stream-61a92317611448b8a6b25a6a914b649c",
            "clock-b" => "stream-30a3713aeb484f49aacf26570130a8c4",
            _ => throw new ArgumentOutOfRangeException(nameof(semanticName)),
        };

    private static string OpaqueClockId(string semanticName) =>
        semanticName switch
        {
            "test-qpc" => "clock-e21cbea2a30b4c06a5634ff3d65bef6e",
            "other-qpc" => "clock-792fb7f239d146bf82e774a3fb21ab24",
            _ => throw new ArgumentOutOfRangeException(nameof(semanticName)),
        };

    private static byte[] BuildUsbCommon(Switch2ControllerModel model,
        uint counter)
    {
        var packet = new byte[64];
        packet[0] = (byte)Switch2InputReportKind.Common05;
        BuildCommonBody(counter).CopyTo(packet, 1);
        return packet;
    }

    private static byte[] BuildCommonBody(uint counter)
    {
        var body = new byte[63];
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0, 4), counter);
        return body;
    }

    private static byte[] BuildBasicBody(Switch2InputReportKind kind,
        byte counter, byte motionLength)
    {
        var body = new byte[63];
        body[0] = counter;
        if (kind == Switch2InputReportKind.ProController2_09)
        {
            body[0x0E] = motionLength;
        }
        else
        {
            body[0x0F] = motionLength;
        }
        return body;
    }

    private static byte[] BuildAdvertisement(ushort productId)
    {
        var value = new byte[24];
        value[0] = 0x01;
        value[1] = 0x00;
        value[2] = 0x03;
        WriteUInt16(value, 3, Switch2AdvertisementCodec.NintendoUsbVendorId);
        WriteUInt16(value, 5, productId);
        value[7] = 0x00;
        value[8] = 0x01;
        value[9] = 0x00;
        value[16] = 0x0F;
        return value;
    }

    private static void PackStick(byte[] destination, int offset,
        ushort x, ushort y)
    {
        destination[offset] = (byte)x;
        destination[offset + 1] = (byte)(((x >> 8) & 0x0F) |
            ((y & 0x0F) << 4));
        destination[offset + 2] = (byte)(y >> 4);
    }

    private static byte[] PackCalibration(IReadOnlyList<ushort> values)
    {
        Assert.AreEqual(6, values.Count);
        var bytes = new byte[9];
        PackStick(bytes, 0, values[0], values[1]);
        PackStick(bytes, 3, values[2], values[3]);
        PackStick(bytes, 6, values[4], values[5]);
        return bytes;
    }

    private static void WriteUInt16(byte[] destination, int offset,
        ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(
            destination.AsSpan(offset, 2), value);

    private static void WriteInt16(byte[] destination, int offset,
        short value) => BinaryPrimitives.WriteInt16LittleEndian(
            destination.AsSpan(offset, 2), value);

    private sealed class ReplayCollector
    {
        public List<Switch2ReplayEvent> Events { get; } = new();

        public void OnEvent(in Switch2ReplayEvent replayEvent) =>
            Events.Add(replayEvent);
    }
}
