using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public class Switch2UsbCommandCodecTests
{
    private static readonly byte[] BatteryRequest =
        Convert.FromHexString("0B91000300000000");

    private static readonly byte[] CapturedBatteryResponse =
        Convert.FromHexString("0B01000310780000A50E0000");

    [TestMethod]
    public void CalibrationReadsAcceptBothObservedExactUsbResponseTuples()
    {
        (Switch2UsbCalibrationRead Read, string RequestHex,
            string ResponseHeaderHex, int PayloadLength)[] cases =
        {
            (Switch2UsbCalibrationRead.FactoryPrimary,
                "0291000400080000097E0000A8300100",
                "020100041078000009000000A8300100", 9),
            (Switch2UsbCalibrationRead.FactorySecondary,
                "0291000400080000097E0000E8300100",
                "020100041078000009000000E8300100", 9),
            (Switch2UsbCalibrationRead.UserPrimary,
                "02910004000800000B7E000040C01F00",
                "02010004107800000B00000040C01F00", 11),
            (Switch2UsbCalibrationRead.UserSecondary,
                "02910004000800000B7E000080C01F00",
                "02010004107800000B00000080C01F00", 11),
        };

        foreach (var item in cases)
        {
            var request = new byte[
                Switch2UsbCommandCodec.CalibrationReadRequestLength];
            Assert.IsTrue(Switch2UsbCommandCodec.
                TryWriteCalibrationReadRequest(item.Read, request,
                    out var failure));
            Assert.IsTrue(request.SequenceEqual(Convert.FromHexString(
                item.RequestHex)));
            Assert.IsTrue(Switch2UsbCommandCodec.
                TryValidateCalibrationReadRequest(request, item.Read,
                    out failure));
            Assert.IsTrue(Switch2UsbCommandCodec.
                TryGetCalibrationReadResponseLength(item.Read,
                    out int responseLength));
            Assert.AreEqual(16 + item.PayloadLength, responseLength);

            byte[] payload = Enumerable.Range(1, item.PayloadLength).
                Select(value => (byte)value).ToArray();
            byte[] response = Convert.FromHexString(item.ResponseHeaderHex).
                Concat(payload).ToArray();
            byte[] copied = new byte[item.PayloadLength];
            Assert.IsTrue(Switch2UsbCommandCodec.
                TryCopyCalibrationReadResponse(response, item.Read, copied,
                    out failure));
            CollectionAssert.AreEqual(payload, copied);
            Assert.AreEqual(Switch2UsbCommandFailure.None, failure);

            byte[] initializedResponse = response.ToArray();
            initializedResponse[4] = 0x00;
            initializedResponse[5] = 0xF8;
            Assert.IsTrue(Switch2UsbCommandCodec.
                TryCopyCalibrationReadResponse(initializedResponse,
                    item.Read, copied, out failure),
                $"Initialized hardware response tuple rejected for {item.Read}.");
            CollectionAssert.AreEqual(payload, copied);
            Assert.AreEqual(Switch2UsbCommandFailure.None, failure);

            response[2] = 0x01;
            Assert.IsFalse(Switch2UsbCommandCodec.
                TryCopyCalibrationReadResponse(response, item.Read, copied,
                    out failure), "Bluetooth transport byte must not be " +
                    $"accepted for {item.Read} USB.");
            Assert.AreEqual(Switch2UsbCommandFailure.InvalidTransport,
                failure);
        }

        Span<byte> rejected = stackalloc byte[
            Switch2UsbCommandCodec.CalibrationReadRequestLength];
        rejected.Fill(0xCC);
        Assert.IsFalse(Switch2UsbCommandCodec.
            TryWriteCalibrationReadRequest(
                (Switch2UsbCalibrationRead)0x7F, rejected, out var rejectedFailure));
        Assert.AreEqual(Switch2UsbCommandFailure.InvalidSubcommand,
            rejectedFailure);
        Assert.IsTrue(AllEqual(rejected, 0xCC),
            "A rejected read kind must not mutate the destination.");
    }

    [TestMethod]
    public void BatteryVoltageRequestAndResponseMatchPinnedCapture()
    {
        Span<byte> request = stackalloc byte[
            Switch2UsbCommandCodec.RequestLength];
        request.Fill(0xCC);

        Assert.IsTrue(
            Switch2UsbCommandCodec.TryWriteGetBatteryVoltageRequest(request,
                out var failure));
        Assert.AreEqual(Switch2UsbCommandFailure.None, failure);
        Assert.IsTrue(request.SequenceEqual(BatteryRequest));
        Assert.IsTrue(
            Switch2UsbCommandCodec.TryValidateGetBatteryVoltageRequest(
                request, out failure));
        Assert.AreEqual(Switch2UsbCommandFailure.None, failure);

        Assert.IsTrue(
            Switch2UsbCommandCodec.TryParseGetBatteryVoltageResponse(
                CapturedBatteryResponse, out ushort rawVoltage, out failure));
        Assert.AreEqual((ushort)0x0EA5, rawVoltage);
        Assert.AreEqual(Switch2UsbCommandFailure.None, failure);

        byte[] initializedHardwareResponse =
            Convert.FromHexString("0B01000300F80000A50E0000");
        Assert.IsTrue(
            Switch2UsbCommandCodec.TryParseGetBatteryVoltageResponse(
                initializedHardwareResponse, out rawVoltage, out failure));
        Assert.AreEqual((ushort)0x0EA5, rawVoltage);
        Assert.AreEqual(Switch2UsbCommandFailure.None, failure);

        initializedHardwareResponse[5] = 0x78;
        Assert.IsFalse(
            Switch2UsbCommandCodec.TryParseGetBatteryVoltageResponse(
                initializedHardwareResponse, out _, out failure));
        Assert.AreEqual(Switch2UsbCommandFailure.InvalidAcknowledgement,
            failure);
    }

    [TestMethod]
    public void VolatileInitializationRequestsAndResponsesMatchPinnedUsbForms()
    {
        Span<byte> request = stackalloc byte[
            Switch2UsbCommandCodec.InitializationRequestLength];

        Assert.IsTrue(Switch2UsbCommandCodec.TryWriteInitializationRequest(
            Switch2UsbInitializationStep.EnableUsbHidReports, request,
            out var failure));
        Assert.AreEqual(Switch2UsbCommandFailure.None, failure);
        Assert.IsTrue(request.SequenceEqual(Convert.FromHexString(
            "039100030004000001000000")));
        Assert.IsTrue(Switch2UsbCommandCodec.TryValidateInitializationRequest(
            request, Switch2UsbInitializationStep.EnableUsbHidReports,
            out failure));
        Assert.IsTrue(
            Switch2UsbCommandCodec.TryGetInitializationResponseLength(
                Switch2UsbInitializationStep.EnableUsbHidReports,
                out int responseLength));
        Assert.AreEqual(
            Switch2UsbCommandCodec.EnableUsbHidReportsResponseLength,
            responseLength);
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
        Assert.IsTrue(Switch2UsbCommandCodec.TryValidateInitializationRequest(
            request, Switch2UsbInitializationStep.SelectCommonInputReport,
            out failure));
        Assert.IsTrue(
            Switch2UsbCommandCodec.TryGetInitializationResponseLength(
                Switch2UsbInitializationStep.SelectCommonInputReport,
                out responseLength));
        Assert.AreEqual(Switch2UsbCommandCodec.InitializationAckResponseLength,
            responseLength);
        Assert.IsTrue(Switch2UsbCommandCodec
            .TryValidateInitializationResponse(Convert.FromHexString(
                "0301000A00F80000"),
                Switch2UsbInitializationStep.SelectCommonInputReport,
                out failure));
        Assert.AreEqual(Switch2UsbCommandFailure.None, failure);
    }

    [TestMethod]
    public void FeatureSetAndEnableRequestsAreExactAndClosed()
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
        Assert.IsTrue(Switch2UsbCommandCodec.TryValidateFeatureRequest(request,
            Switch2UsbFeatureStep.EnableFeatures, Mask, out failure));

        request.Fill(0xCC);
        Assert.IsFalse(Switch2UsbCommandCodec.TryWriteFeatureRequest(
            (Switch2UsbFeatureStep)0x03, Mask, request, out failure));
        Assert.AreEqual(Switch2UsbCommandFailure.InvalidSubcommand, failure);
        Assert.IsTrue(AllEqual(request, 0xCC));

        Assert.IsFalse(Switch2UsbCommandCodec.TryWriteFeatureRequest(
            Switch2UsbFeatureStep.SetFeatureMask,
            (Switch2UsbFeatureMask)0xA7, request, out failure));
        Assert.AreEqual(Switch2UsbCommandFailure.InvalidRequestPayload,
            failure);
        Assert.IsTrue(AllEqual(request, 0xCC));
    }

    [TestMethod]
    public void FeatureResponsesMatchThePinnedBcd0201UsbTuples()
    {
        byte[] setMask = Convert.FromHexString(
            "0C01000200F8000000000000");
        byte[] enable = Convert.FromHexString(
            "0C01000400F8000000000000");

        Assert.IsTrue(Switch2UsbCommandCodec.TryValidateFeatureResponse(
            setMask, Switch2UsbFeatureStep.SetFeatureMask,
            out Switch2UsbCommandFailure failure));
        Assert.AreEqual(Switch2UsbCommandFailure.None, failure);
        Assert.IsTrue(Switch2UsbCommandCodec.TryValidateFeatureResponse(
            enable, Switch2UsbFeatureStep.EnableFeatures, out failure));
        Assert.AreEqual(Switch2UsbCommandFailure.None, failure);

        Assert.IsFalse(Switch2UsbCommandCodec.TryValidateFeatureResponse(
            setMask, Switch2UsbFeatureStep.EnableFeatures, out failure));
        Assert.AreEqual(Switch2UsbCommandFailure.InvalidSubcommand, failure);
        Assert.IsFalse(Switch2UsbCommandCodec.TryValidateFeatureResponse(
            setMask, (Switch2UsbFeatureStep)0x03, out failure));
        Assert.AreEqual(Switch2UsbCommandFailure.InvalidSubcommand, failure);
    }

    [TestMethod]
    public void EveryFeatureResponseByteMutationIsRejected()
    {
        foreach ((Switch2UsbFeatureStep step, string hex) in new[]
                 {
                     (Switch2UsbFeatureStep.SetFeatureMask,
                         "0C01000200F8000000000000"),
                     (Switch2UsbFeatureStep.EnableFeatures,
                         "0C01000400F8000000000000"),
                 })
        {
            byte[] response = Convert.FromHexString(hex);
            byte[] candidate = new byte[response.Length];
            for (int offset = 0; offset < response.Length; offset++)
            {
                for (int value = byte.MinValue; value <= byte.MaxValue;
                     value++)
                {
                    if (value == response[offset])
                    {
                        continue;
                    }
                    response.CopyTo(candidate, 0);
                    candidate[offset] = (byte)value;
                    Assert.IsFalse(Switch2UsbCommandCodec.
                        TryValidateFeatureResponse(candidate, step, out _),
                        $"Mutation at byte {offset} to 0x{value:X2} was admitted for {step}.");
                }
            }
        }
    }

    [TestMethod]
    public void InitializationCodecRejectsCrossStepAndMalformedTuples()
    {
        byte[] enableRequest = Convert.FromHexString(
            "039100030004000001000000");
        byte[] enableResponse = Convert.FromHexString(
            "0301000300F8000001000000");

        Assert.IsFalse(Switch2UsbCommandCodec.TryValidateInitializationRequest(
            enableRequest,
            Switch2UsbInitializationStep.SelectCommonInputReport,
            out var failure));
        Assert.AreEqual(Switch2UsbCommandFailure.InvalidSubcommand, failure);

        enableResponse[8] = 0;
        Assert.IsFalse(Switch2UsbCommandCodec
            .TryValidateInitializationResponse(enableResponse,
                Switch2UsbInitializationStep.EnableUsbHidReports,
                out failure));
        Assert.AreEqual(Switch2UsbCommandFailure.InvalidResponsePayload,
            failure);

        Span<byte> untouched = stackalloc byte[
            Switch2UsbCommandCodec.InitializationRequestLength];
        untouched.Fill(0xCC);
        Assert.IsFalse(Switch2UsbCommandCodec.TryWriteInitializationRequest(
            (Switch2UsbInitializationStep)0x0D, untouched, out failure));
        Assert.AreEqual(Switch2UsbCommandFailure.InvalidSubcommand, failure);
        Assert.IsTrue(AllEqual(untouched, 0xCC));
        Assert.IsFalse(
            Switch2UsbCommandCodec.TryGetInitializationResponseLength(
                (Switch2UsbInitializationStep)0x0D, out int responseLength));
        Assert.AreEqual(0, responseLength);
    }

    [TestMethod]
    public void PlayerLedAllowlistWritesAndValidatesSixExactCaptureTuples()
    {
        Switch2PlayerLedCommand[] commands =
        {
            Switch2PlayerLedCommand.Player1Only,
            Switch2PlayerLedCommand.Player2Only,
            Switch2PlayerLedCommand.Player3Only,
            Switch2PlayerLedCommand.Player4Only,
            Switch2PlayerLedCommand.AllOn,
            Switch2PlayerLedCommand.AllOff,
        };

        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 5, 6 },
            Enum.GetValues<Switch2PlayerLedCommand>()
                .Select(command => (byte)command).ToArray());

        Span<byte> request = stackalloc byte[
            Switch2UsbCommandCodec.RequestLength];
        Span<byte> expectedRequest = stackalloc byte[
            Switch2UsbCommandCodec.RequestLength];
        Span<byte> response = stackalloc byte[
            Switch2UsbCommandCodec.PlayerLedResponseLength];
        foreach (Switch2PlayerLedCommand command in commands)
        {
            request.Fill(0xCC);
            Assert.IsTrue(Switch2UsbCommandCodec.TryWritePlayerLedRequest(
                command, request, out var failure));
            Assert.AreEqual(Switch2UsbCommandFailure.None, failure);
            expectedRequest[0] = 0x09;
            expectedRequest[1] = 0x91;
            expectedRequest[2] = 0x00;
            expectedRequest[3] = (byte)command;
            expectedRequest[4] = 0x00;
            expectedRequest[5] = 0x00;
            expectedRequest[6] = 0x00;
            expectedRequest[7] = 0x00;
            Assert.IsTrue(request.SequenceEqual(expectedRequest));
            Assert.IsTrue(Switch2UsbCommandCodec.TryDecodePlayerLedRequest(
                request, out Switch2PlayerLedCommand decoded, out failure));
            Assert.AreEqual(command, decoded);
            Assert.IsTrue(Switch2UsbCommandCodec.TryValidatePlayerLedRequest(
                request, command, out failure));
            Assert.AreEqual(Switch2UsbCommandFailure.None, failure);

            response[0] = 0x09;
            response[1] = 0x01;
            response[2] = 0x00;
            response[3] = (byte)command;
            response[4] = 0x10;
            response[5] = 0x78;
            response[6] = 0x00;
            response[7] = 0x00;
            Assert.IsTrue(
                Switch2UsbCommandCodec.TryValidatePlayerLedResponse(response,
                    command, out failure));
            Assert.AreEqual(Switch2UsbCommandFailure.None, failure);

            response[4] = 0x00;
            response[5] = 0xF8;
            Assert.IsTrue(
                Switch2UsbCommandCodec.TryValidatePlayerLedResponse(response,
                    command, out failure));
            Assert.AreEqual(Switch2UsbCommandFailure.None, failure);
        }
    }

    [TestMethod]
    public void InvalidPlayerLedEnumCastsCannotEnterCodec()
    {
        Span<byte> destination = stackalloc byte[
            Switch2UsbCommandCodec.RequestLength];
        Span<byte> request = stackalloc byte[]
        {
            0x09, 0x91, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00,
        };
        Span<byte> response = stackalloc byte[]
        {
            0x09, 0x01, 0x00, 0x01, 0x10, 0x78, 0x00, 0x00,
        };

        for (int value = byte.MinValue; value <= byte.MaxValue; value++)
        {
            if (value is >= 1 and <= 6)
            {
                continue;
            }

            destination.Fill(0xCC);
            var invalid = (Switch2PlayerLedCommand)(byte)value;
            Assert.IsFalse(Switch2UsbCommandCodec.TryWritePlayerLedRequest(
                invalid, destination, out var failure));
            Assert.AreEqual(Switch2UsbCommandFailure.InvalidSubcommand,
                failure);
            Assert.IsTrue(AllEqual(destination, 0xCC));
            Assert.IsFalse(
                Switch2UsbCommandCodec.TryValidatePlayerLedRequest(request,
                    invalid, out failure));
            Assert.AreEqual(Switch2UsbCommandFailure.InvalidSubcommand,
                failure);
            Assert.IsFalse(
                Switch2UsbCommandCodec.TryValidatePlayerLedResponse(response,
                    invalid, out failure));
            Assert.AreEqual(Switch2UsbCommandFailure.InvalidSubcommand,
                failure);
        }
    }

    [TestMethod]
    public void EveryWrongLengthIsRejectedAndFailedWritesAreUntouched()
    {
        for (int length = 0; length <= 20; length++)
        {
            if (length != Switch2UsbCommandCodec.RequestLength)
            {
                var destination = Enumerable.Repeat((byte)0xCC, length)
                    .ToArray();
                Assert.IsFalse(
                    Switch2UsbCommandCodec.TryWriteGetBatteryVoltageRequest(
                        destination, out var failure));
                Assert.AreEqual(Switch2UsbCommandFailure.InvalidLength,
                    failure);
                Assert.IsTrue(AllEqual(destination, 0xCC));
                Assert.IsFalse(
                    Switch2UsbCommandCodec.TryWritePlayerLedRequest(
                        Switch2PlayerLedCommand.Player1Only, destination,
                        out failure));
                Assert.AreEqual(Switch2UsbCommandFailure.InvalidLength,
                    failure);
                Assert.IsTrue(AllEqual(destination, 0xCC));
                Assert.IsFalse(
                    Switch2UsbCommandCodec.TryValidateGetBatteryVoltageRequest(
                        destination, out failure));
                Assert.AreEqual(Switch2UsbCommandFailure.InvalidLength,
                    failure);
                Assert.IsFalse(
                    Switch2UsbCommandCodec.TryValidatePlayerLedRequest(
                        destination, Switch2PlayerLedCommand.Player1Only,
                        out failure));
                Assert.AreEqual(Switch2UsbCommandFailure.InvalidLength,
                    failure);
                Assert.IsFalse(
                    Switch2UsbCommandCodec.TryValidatePlayerLedResponse(
                        destination, Switch2PlayerLedCommand.Player1Only,
                        out failure));
                Assert.AreEqual(Switch2UsbCommandFailure.InvalidLength,
                    failure);
            }

            if (length != Switch2UsbCommandCodec.BatteryVoltageResponseLength)
            {
                Assert.IsFalse(
                    Switch2UsbCommandCodec.TryParseGetBatteryVoltageResponse(
                        new byte[length], out ushort rawVoltage,
                        out var failure));
                Assert.AreEqual((ushort)0, rawVoltage);
                Assert.AreEqual(Switch2UsbCommandFailure.InvalidLength,
                    failure);
            }
        }
    }

    [TestMethod]
    public void BatteryRequestReportsEachHeaderFailureSpecifically()
    {
        AssertBatteryRequestFailure(0, 0x0A,
            Switch2UsbCommandFailure.InvalidCommand);
        AssertBatteryRequestFailure(1, 0x01,
            Switch2UsbCommandFailure.InvalidDirection);
        AssertBatteryRequestFailure(2, 0x01,
            Switch2UsbCommandFailure.InvalidTransport);
        AssertBatteryRequestFailure(3, 0x04,
            Switch2UsbCommandFailure.InvalidSubcommand);
        AssertBatteryRequestFailure(4, 0x01,
            Switch2UsbCommandFailure.UnexpectedCapturedHeaderByte4);
        AssertBatteryRequestFailure(5, 0x01,
            Switch2UsbCommandFailure.InvalidRequestDataLength);
        AssertBatteryRequestFailure(6, 0x01,
            Switch2UsbCommandFailure.NonzeroHeaderReserved);
        AssertBatteryRequestFailure(7, 0x01,
            Switch2UsbCommandFailure.NonzeroHeaderReserved);
    }

    [TestMethod]
    public void BatteryResponseReportsEachHeaderAndPayloadFailureSpecifically()
    {
        AssertBatteryResponseFailure(0, 0x0A,
            Switch2UsbCommandFailure.InvalidCommand);
        AssertBatteryResponseFailure(1, 0x91,
            Switch2UsbCommandFailure.InvalidDirection);
        AssertBatteryResponseFailure(2, 0x01,
            Switch2UsbCommandFailure.InvalidTransport);
        AssertBatteryResponseFailure(3, 0x04,
            Switch2UsbCommandFailure.InvalidSubcommand);
        AssertBatteryResponseFailure(4, 0x00,
            Switch2UsbCommandFailure.InvalidAcknowledgement);
        AssertBatteryResponseFailure(5, 0xF8,
            Switch2UsbCommandFailure.InvalidAcknowledgement);
        AssertBatteryResponseFailure(6, 0x01,
            Switch2UsbCommandFailure.NonzeroHeaderReserved);
        AssertBatteryResponseFailure(7, 0x01,
            Switch2UsbCommandFailure.NonzeroHeaderReserved);
        AssertBatteryResponseFailure(10, 0x01,
            Switch2UsbCommandFailure.NonzeroPayloadReserved);
        AssertBatteryResponseFailure(11, 0x01,
            Switch2UsbCommandFailure.NonzeroPayloadReserved);
    }

    [TestMethod]
    public void EveryBatteryFixedByteMutationIsRejected()
    {
        Span<byte> candidate = stackalloc byte[
            Switch2UsbCommandCodec.BatteryVoltageResponseLength];

        for (int offset = 0; offset < BatteryRequest.Length; offset++)
        {
            for (int value = byte.MinValue; value <= byte.MaxValue; value++)
            {
                if (value == BatteryRequest[offset])
                {
                    continue;
                }

                BatteryRequest.CopyTo(candidate);
                candidate[offset] = (byte)value;
                Assert.IsFalse(
                    Switch2UsbCommandCodec.TryValidateGetBatteryVoltageRequest(
                        candidate.Slice(0, BatteryRequest.Length), out _));
            }
        }

        ReadOnlySpan<byte> fixedResponseOffsets =
            stackalloc byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 10, 11 };
        foreach (byte offset in fixedResponseOffsets)
        {
            for (int value = byte.MinValue; value <= byte.MaxValue; value++)
            {
                if (value == CapturedBatteryResponse[offset])
                {
                    continue;
                }

                CapturedBatteryResponse.CopyTo(candidate);
                candidate[offset] = (byte)value;
                Assert.IsFalse(
                    Switch2UsbCommandCodec.TryParseGetBatteryVoltageResponse(
                        candidate, out _, out _));
            }
        }
    }

    [TestMethod]
    public void BatteryRawUInt16PayloadAcceptsEveryValueLittleEndian()
    {
        Span<byte> response = stackalloc byte[
            Switch2UsbCommandCodec.BatteryVoltageResponseLength];
        CapturedBatteryResponse.CopyTo(response);

        for (int value = ushort.MinValue; value <= ushort.MaxValue; value++)
        {
            response[8] = (byte)value;
            response[9] = (byte)(value >> 8);
            if (!Switch2UsbCommandCodec.TryParseGetBatteryVoltageResponse(
                    response, out ushort decoded, out var failure) ||
                decoded != (ushort)value ||
                failure != Switch2UsbCommandFailure.None)
            {
                Assert.Fail($"Raw voltage 0x{value:X4} did not round-trip.");
            }
        }
    }

    [TestMethod]
    public void EveryPlayerLedRequestAndResponseByteMutationIsRejected()
    {
        Span<byte> request = stackalloc byte[
            Switch2UsbCommandCodec.RequestLength];
        Span<byte> response = stackalloc byte[
            Switch2UsbCommandCodec.PlayerLedResponseLength];
        Span<byte> candidate = stackalloc byte[
            Switch2UsbCommandCodec.RequestLength];

        for (int commandValue = 1; commandValue <= 6; commandValue++)
        {
            var command = (Switch2PlayerLedCommand)commandValue;
            Assert.IsTrue(Switch2UsbCommandCodec.TryWritePlayerLedRequest(
                command, request, out _));
            response[0] = 0x09;
            response[1] = 0x01;
            response[2] = 0x00;
            response[3] = (byte)command;
            response[4] = 0x10;
            response[5] = 0x78;
            response[6] = 0x00;
            response[7] = 0x00;

            for (int offset = 0; offset < request.Length; offset++)
            {
                for (int value = byte.MinValue; value <= byte.MaxValue;
                     value++)
                {
                    if (value != request[offset])
                    {
                        request.CopyTo(candidate);
                        candidate[offset] = (byte)value;
                        Assert.IsFalse(
                            Switch2UsbCommandCodec
                                .TryValidatePlayerLedRequest(candidate,
                                    command, out _));
                    }

                    if (value != response[offset])
                    {
                        response.CopyTo(candidate);
                        candidate[offset] = (byte)value;
                        Assert.IsFalse(
                            Switch2UsbCommandCodec
                                .TryValidatePlayerLedResponse(candidate,
                                    command, out _));
                    }
                }
            }
        }
    }

    [TestMethod]
    public void CodecHotPathAllocatesNoManagedMemoryAfterWarmup()
    {
        Span<byte> batteryRequest = stackalloc byte[
            Switch2UsbCommandCodec.RequestLength];
        Span<byte> batteryResponse = stackalloc byte[
            Switch2UsbCommandCodec.BatteryVoltageResponseLength];
        Span<byte> ledRequest = stackalloc byte[
            Switch2UsbCommandCodec.RequestLength];
        Span<byte> ledResponse = stackalloc byte[]
        {
            0x09, 0x01, 0x00, 0x06, 0x10, 0x78, 0x00, 0x00,
        };
        CapturedBatteryResponse.CopyTo(batteryResponse);
        const Switch2PlayerLedCommand LedCommand =
            Switch2PlayerLedCommand.AllOff;

        for (int warmup = 0; warmup < 1_000; warmup++)
        {
            Switch2UsbCommandCodec.TryWriteGetBatteryVoltageRequest(
                batteryRequest, out _);
            Switch2UsbCommandCodec.TryValidateGetBatteryVoltageRequest(
                batteryRequest, out _);
            Switch2UsbCommandCodec.TryParseGetBatteryVoltageResponse(
                batteryResponse, out _, out _);
            Switch2UsbCommandCodec.TryWritePlayerLedRequest(LedCommand,
                ledRequest, out _);
            Switch2UsbCommandCodec.TryValidatePlayerLedRequest(ledRequest,
                LedCommand, out _);
            Switch2UsbCommandCodec.TryValidatePlayerLedResponse(ledResponse,
                LedCommand, out _);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool valid = true;
        for (int iteration = 0; iteration < 10_000; iteration++)
        {
            valid &= Switch2UsbCommandCodec
                .TryWriteGetBatteryVoltageRequest(batteryRequest, out _);
            valid &= Switch2UsbCommandCodec
                .TryValidateGetBatteryVoltageRequest(batteryRequest, out _);
            valid &= Switch2UsbCommandCodec
                .TryParseGetBatteryVoltageResponse(batteryResponse,
                    out ushort rawVoltage, out _) && rawVoltage == 0x0EA5;
            valid &= Switch2UsbCommandCodec.TryWritePlayerLedRequest(
                LedCommand, ledRequest, out _);
            valid &= Switch2UsbCommandCodec.TryValidatePlayerLedRequest(
                ledRequest, LedCommand, out _);
            valid &= Switch2UsbCommandCodec.TryValidatePlayerLedResponse(
                ledResponse, LedCommand, out _);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.IsTrue(valid);
        Assert.AreEqual(0L, allocated);
    }

    private static void AssertBatteryRequestFailure(int offset, byte value,
        Switch2UsbCommandFailure expected)
    {
        byte[] candidate = (byte[])BatteryRequest.Clone();
        candidate[offset] = value;
        Assert.IsFalse(
            Switch2UsbCommandCodec.TryValidateGetBatteryVoltageRequest(
                candidate, out var failure));
        Assert.AreEqual(expected, failure);
    }

    private static void AssertBatteryResponseFailure(int offset, byte value,
        Switch2UsbCommandFailure expected)
    {
        byte[] candidate = (byte[])CapturedBatteryResponse.Clone();
        candidate[offset] = value;
        Assert.IsFalse(
            Switch2UsbCommandCodec.TryParseGetBatteryVoltageResponse(candidate,
                out ushort rawVoltage, out var failure));
        Assert.AreEqual((ushort)0, rawVoltage);
        Assert.AreEqual(expected, failure);
    }

    private static bool AllEqual(ReadOnlySpan<byte> source, byte expected)
    {
        foreach (byte value in source)
        {
            if (value != expected)
            {
                return false;
            }
        }
        return true;
    }
}
