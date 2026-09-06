using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public class Switch2HdRumbleCodecTests
{
    [TestMethod]
    public void SubframePacksFourTenBitFieldsLittleEndian()
    {
        var subframe = new Switch2HdRumbleSubframe(
            0x001, 0x155, 0x2AA, 0x3FF);
        Span<byte> encoded = stackalloc byte[
            Switch2HdRumbleCodec.SubframeLength];

        Assert.IsTrue(Switch2HdRumbleCodec.TryEncode(subframe, encoded));
        CollectionAssert.AreEqual(Convert.FromHexString("0154A5EAFF"),
            encoded.ToArray());
        Assert.IsTrue(Switch2HdRumbleCodec.TryDecode(encoded,
            out var decoded));
        Assert.AreEqual(subframe, decoded);
        Assert.IsTrue(decoded.HasNonzeroAmplitude);
    }

    [TestMethod]
    public void LicensedSourceGoldenSubframesStayByteExact()
    {
        (Switch2HdRumbleSubframe Frame, string Hex)[] vectors =
        {
            (new(0, 0, 0, 0), "0000000000"),
            (new(0x3FF, 0x3FF, 0x3FF, 0x3FF), "FFFFFFFFFF"),
            (new(0x3FF, 0, 0, 0), "FF03000000"),
            (new(0, 0x3FF, 0, 0), "00FC0F0000"),
            (new(0, 0, 0x3FF, 0), "0000F03F00"),
            (new(0, 0, 0, 0x3FF), "000000C0FF"),
            (new(0x155, 0x2AA, 0x2AA, 0x155), "55A9AA6A55"),
            (new(0x187, 0, 0x112, 0), "8701201100"),
            (new(0x187, 453, 0x112, 453), "8715275171"),
        };
        Span<byte> encoded = stackalloc byte[
            Switch2HdRumbleCodec.SubframeLength];
        foreach ((Switch2HdRumbleSubframe frame, string hex) in vectors)
        {
            Assert.IsTrue(Switch2HdRumbleCodec.TryEncode(frame, encoded));
            Assert.IsTrue(encoded.SequenceEqual(Convert.FromHexString(hex)),
                hex);
        }
    }

    [TestMethod]
    public void EachPackedFieldRoundTripsEveryCode()
    {
        Span<byte> encoded = stackalloc byte[
            Switch2HdRumbleCodec.SubframeLength];
        for (int field = 0; field < 4; field++)
        {
            for (ushort code = 0;
                 code <= Switch2HdRumbleSubframe.MaximumCode; code++)
            {
                Switch2HdRumbleSubframe subframe = field switch
                {
                    0 => new(code, 0, 0, 0),
                    1 => new(0, code, 0, 0),
                    2 => new(0, 0, code, 0),
                    _ => new(0, 0, 0, code),
                };
                Assert.IsTrue(Switch2HdRumbleCodec.TryEncode(subframe,
                    encoded));
                Assert.IsTrue(Switch2HdRumbleCodec.TryDecode(encoded,
                    out var decoded));
                Assert.AreEqual(subframe, decoded);
            }
        }
    }

    [TestMethod]
    public void EveryFiveBytePatternRoundTripsLosslesslyInFuzzSet()
    {
        var random = new Random(0x2069_500);
        var source = new byte[Switch2HdRumbleCodec.SubframeLength];
        var encoded = new byte[Switch2HdRumbleCodec.SubframeLength];
        for (int iteration = 0; iteration < 20_000; iteration++)
        {
            random.NextBytes(source);
            Assert.IsTrue(Switch2HdRumbleCodec.TryDecode(source,
                out var decoded));
            Assert.IsTrue(Switch2HdRumbleCodec.TryEncode(decoded, encoded));
            CollectionAssert.AreEqual(source, encoded);
        }
    }

    [TestMethod]
    public void CodecRejectsEveryNonExactLengthAndOutOfRangeField()
    {
        var subframe = new Switch2HdRumbleSubframe(1, 2, 3, 4);
        for (int length = 0; length < 12; length++)
        {
            if (length == Switch2HdRumbleCodec.SubframeLength)
            {
                continue;
            }

            Assert.IsFalse(Switch2HdRumbleCodec.TryEncode(subframe,
                new byte[length]));
            Assert.IsFalse(Switch2HdRumbleCodec.TryDecode(new byte[length],
                out _));
        }

        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new Switch2HdRumbleSubframe(0x400, 0, 0, 0));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new Switch2HdRumbleSubframe(0, 0x400, 0, 0));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new Switch2HdRumbleSubframe(0, 0, 0x400, 0));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new Switch2HdRumbleSubframe(0, 0, 0, 0x400));
    }

    [TestMethod]
    public void GroupEncodesThreeIndependentFramesAndModuloCounter()
    {
        var group = new Switch2HdRumbleGroup(
            new Switch2HdRumbleSubframe(1, 2, 3, 4),
            new Switch2HdRumbleSubframe(5, 6, 7, 8),
            new Switch2HdRumbleSubframe(9, 10, 11, 12));
        Span<byte> encoded = stackalloc byte[
            Switch2HdRumbleGroupCodec.GroupLength];
        encoded.Fill(0xCC);

        Assert.IsTrue(Switch2HdRumbleGroupCodec.TryEncode(0x0F, group,
            encoded));
        Assert.AreEqual((byte)0x5F, encoded[0]);
        Assert.IsTrue(Switch2HdRumbleGroupCodec.TryDecode(encoded,
            out byte counter, out var decoded));
        Assert.AreEqual((byte)0x0F, counter);
        Assert.AreEqual(group, decoded);
        Assert.IsFalse(Switch2HdRumbleGroupCodec.TryEncode(0x10, group,
            encoded));
    }

    [TestMethod]
    public void UsbProReportCarriesIndependentGroupsUnderOneCounter()
    {
        var left = new Switch2HdRumbleGroup(
            new Switch2HdRumbleSubframe(1, 2, 3, 4),
            new Switch2HdRumbleSubframe(5, 6, 7, 8),
            new Switch2HdRumbleSubframe(9, 10, 11, 12));
        var right = new Switch2HdRumbleGroup(
            new Switch2HdRumbleSubframe(13, 14, 15, 16),
            new Switch2HdRumbleSubframe(17, 18, 19, 20),
            new Switch2HdRumbleSubframe(21, 22, 23, 24));
        Span<byte> report = stackalloc byte[
            Switch2UsbHdRumbleCodec.ReportLength];
        report.Fill(0xCC);

        Assert.IsTrue(Switch2UsbHdRumbleCodec.TryEncodeProController(7,
            left, right, report));
        Assert.AreEqual(Switch2UsbHdRumbleCodec.ProControllerReportId,
            report[0]);
        Assert.AreEqual((byte)0x57, report[1]);
        Assert.AreEqual((byte)0x57, report[17]);
        Assert.IsTrue(report.Slice(33).ToArray().All(value => value == 0));
        Assert.IsTrue(Switch2UsbHdRumbleCodec.TryDecodeProController(report,
            out byte counter, out var decodedLeft, out var decodedRight,
            out var failure), failure.ToString());
        Assert.AreEqual((byte)7, counter);
        Assert.AreEqual(left, decodedLeft);
        Assert.AreEqual(right, decodedRight);
        Assert.AreEqual(Switch2UsbHdRumbleDecodeFailure.None, failure);
    }

    [TestMethod]
    public void BluetoothProEnvelopeCarriesIndependentGroupsByteExactly()
    {
        var left = new Switch2HdRumbleGroup(
            new Switch2HdRumbleSubframe(1, 2, 3, 4),
            new Switch2HdRumbleSubframe(5, 6, 7, 8),
            new Switch2HdRumbleSubframe(9, 10, 11, 12));
        var right = new Switch2HdRumbleGroup(
            new Switch2HdRumbleSubframe(13, 14, 15, 16),
            new Switch2HdRumbleSubframe(17, 18, 19, 20),
            new Switch2HdRumbleSubframe(21, 22, 23, 24));
        Span<byte> bluetooth = stackalloc byte[
            Switch2BluetoothHdRumbleCodec.ProControllerPayloadLength];
        Span<byte> usb = stackalloc byte[
            Switch2UsbHdRumbleCodec.ReportLength];
        bluetooth.Fill(0xCC);

        Assert.IsTrue(
            Switch2BluetoothHdRumbleCodec.TryEncodeProController(7, left,
                right, bluetooth));
        Assert.IsTrue(Switch2UsbHdRumbleCodec.TryEncodeProController(7,
            left, right, usb));
        Assert.AreEqual(Switch2BluetoothHdRumbleCodec.Envelope,
            bluetooth[0]);
        Assert.IsTrue(bluetooth.Slice(1).SequenceEqual(usb.Slice(1, 32)));
        Assert.IsTrue(
            Switch2BluetoothHdRumbleCodec.TryDecodeProController(bluetooth,
                out byte counter, out var decodedLeft,
                out var decodedRight, out var failure), failure.ToString());
        Assert.AreEqual((byte)7, counter);
        Assert.AreEqual(left, decodedLeft);
        Assert.AreEqual(right, decodedRight);
        Assert.AreEqual(Switch2BluetoothHdRumbleDecodeFailure.None, failure);
    }

    [TestMethod]
    public void BluetoothJoyConEnvelopeCarriesOneGroupByteExactly()
    {
        var group = new Switch2HdRumbleGroup(
            new Switch2HdRumbleSubframe(0x0E1, 0x100, 0x1E1, 0x080),
            default, default);
        Span<byte> payload = stackalloc byte[
            Switch2BluetoothHdRumbleCodec.JoyConPayloadLength];

        Assert.IsTrue(Switch2BluetoothHdRumbleCodec.TryEncodeJoyCon(0x0F,
            group, payload));
        Assert.AreEqual("005FE100141E20",
            Convert.ToHexString(payload.Slice(0, 7)));
        Assert.IsTrue(Switch2BluetoothHdRumbleCodec.TryDecodeJoyCon(payload,
            out byte counter, out var decoded, out var failure),
            failure.ToString());
        Assert.AreEqual((byte)0x0F, counter);
        Assert.AreEqual(group, decoded);
    }

    [TestMethod]
    public void BluetoothDecoderRejectsEveryEnvelopeMutation()
    {
        var group = new Switch2HdRumbleGroup(
            new Switch2HdRumbleSubframe(1, 2, 3, 4), default, default);
        var payload = new byte[
            Switch2BluetoothHdRumbleCodec.ProControllerPayloadLength];
        Assert.IsTrue(
            Switch2BluetoothHdRumbleCodec.TryEncodeProController(1, group,
                group, payload));

        Assert.IsFalse(
            Switch2BluetoothHdRumbleCodec.TryDecodeProController(
                payload.AsSpan(0, payload.Length - 1), out _, out _, out _,
                out var failure));
        Assert.AreEqual(Switch2BluetoothHdRumbleDecodeFailure.InvalidLength,
            failure);

        payload[0] = 1;
        Assert.IsFalse(
            Switch2BluetoothHdRumbleCodec.TryDecodeProController(payload,
                out _, out _, out _, out failure));
        Assert.AreEqual(Switch2BluetoothHdRumbleDecodeFailure.InvalidEnvelope,
            failure);
        payload[0] = Switch2BluetoothHdRumbleCodec.Envelope;

        payload[17] = 0x41;
        Assert.IsFalse(
            Switch2BluetoothHdRumbleCodec.TryDecodeProController(payload,
                out _, out _, out _, out failure));
        Assert.AreEqual(
            Switch2BluetoothHdRumbleDecodeFailure.InvalidGroupHeader,
            failure);
        payload[17] = 0x52;
        Assert.IsFalse(
            Switch2BluetoothHdRumbleCodec.TryDecodeProController(payload,
                out _, out _, out _, out failure));
        Assert.AreEqual(
            Switch2BluetoothHdRumbleDecodeFailure.CounterMismatch, failure);
    }

    [TestMethod]
    public void BluetoothJoyConRequiresExactEnvelopeAndGroupHeader()
    {
        var group = new Switch2HdRumbleGroup(
            new Switch2HdRumbleSubframe(1, 2, 3, 4), default, default);
        var payload = new byte[
            Switch2BluetoothHdRumbleCodec.JoyConPayloadLength];
        Assert.IsTrue(Switch2BluetoothHdRumbleCodec.TryEncodeJoyCon(1,
            group, payload));

        for (int length = 0;
             length <= Switch2BluetoothHdRumbleCodec.JoyConPayloadLength + 1;
             length++)
        {
            if (length ==
                Switch2BluetoothHdRumbleCodec.JoyConPayloadLength)
            {
                continue;
            }

            Assert.IsFalse(
                Switch2BluetoothHdRumbleCodec.TryDecodeJoyCon(
                    new byte[length], out _, out _, out var failure));
            Assert.AreEqual(
                Switch2BluetoothHdRumbleDecodeFailure.InvalidLength,
                failure);
        }

        payload[0] = 1;
        Assert.IsFalse(Switch2BluetoothHdRumbleCodec.TryDecodeJoyCon(
            payload, out _, out _, out var envelopeFailure));
        Assert.AreEqual(
            Switch2BluetoothHdRumbleDecodeFailure.InvalidEnvelope,
            envelopeFailure);
        payload[0] = Switch2BluetoothHdRumbleCodec.Envelope;

        payload[1] = 0x41;
        Assert.IsFalse(Switch2BluetoothHdRumbleCodec.TryDecodeJoyCon(
            payload, out _, out _, out var headerFailure));
        Assert.AreEqual(
            Switch2BluetoothHdRumbleDecodeFailure.InvalidGroupHeader,
            headerFailure);
    }

    [TestMethod]
    public void BluetoothEncoderRejectsInvalidCounterAndClearsExactBuffer()
    {
        var group = new Switch2HdRumbleGroup(
            new Switch2HdRumbleSubframe(1, 2, 3, 4), default, default);
        var joyCon = Enumerable.Repeat((byte)0xCC,
            Switch2BluetoothHdRumbleCodec.JoyConPayloadLength).ToArray();
        var pro = Enumerable.Repeat((byte)0xCC,
            Switch2BluetoothHdRumbleCodec.ProControllerPayloadLength).
            ToArray();

        Assert.IsFalse(Switch2BluetoothHdRumbleCodec.TryEncodeJoyCon(
            0x10, group, joyCon));
        Assert.IsTrue(joyCon.All(value => value == 0));
        Assert.IsFalse(
            Switch2BluetoothHdRumbleCodec.TryEncodeProController(
                0x10, group, group, pro));
        Assert.IsTrue(pro.All(value => value == 0));
    }

    [TestMethod]
    public void UsbSdlCompatibilityMirrorsOnlyProHeaderAndFirstFrame()
    {
        var frame = new Switch2HdRumbleSubframe(0x187, 0, 0x112, 0);
        var report = Enumerable.Repeat((byte)0xCC,
            Switch2UsbHdRumbleCodec.ReportLength).ToArray();

        Assert.IsTrue(Switch2UsbHdRumbleCodec.TryEncodeSdlCompatibility(
            Switch2UsbHdRumbleCodec.ProControllerReportId, 0, frame,
            report));
        Assert.AreEqual("02508701201100",
            Convert.ToHexString(report.AsSpan(0, 7)));
        Assert.IsTrue(report.AsSpan(1, 6).SequenceEqual(
            report.AsSpan(17, 6)));
        Assert.IsTrue(report.AsSpan(7, 10).ToArray().All(value => value == 0));
        Assert.IsTrue(report.AsSpan(23).ToArray().All(value => value == 0));

        Array.Fill(report, (byte)0xCC);
        Assert.IsTrue(Switch2UsbHdRumbleCodec.TryEncodeSdlCompatibility(
            Switch2UsbHdRumbleCodec.JoyConReportId, 0x0F, frame, report));
        Assert.AreEqual(Switch2UsbHdRumbleCodec.JoyConReportId, report[0]);
        Assert.AreEqual((byte)0x5F, report[1]);
        Assert.IsTrue(report.AsSpan(7).ToArray().All(value => value == 0));
    }

    [TestMethod]
    public void UsbDecoderRejectsEachEnvelopeMutationWithSpecificFailure()
    {
        var group = new Switch2HdRumbleGroup(
            new Switch2HdRumbleSubframe(1, 2, 3, 4), default, default);
        var report = new byte[Switch2UsbHdRumbleCodec.ReportLength];
        Assert.IsTrue(Switch2UsbHdRumbleCodec.TryEncodeProController(1,
            group, group, report));

        Assert.IsFalse(Switch2UsbHdRumbleCodec.TryDecodeProController(
            report.AsSpan(0, 63), out _, out _, out _, out var failure));
        Assert.AreEqual(Switch2UsbHdRumbleDecodeFailure.InvalidLength,
            failure);

        byte original = report[0];
        report[0] = 0x01;
        Assert.IsFalse(Switch2UsbHdRumbleCodec.TryDecodeProController(report,
            out _, out _, out _, out failure));
        Assert.AreEqual(Switch2UsbHdRumbleDecodeFailure.InvalidReportId,
            failure);
        report[0] = original;

        original = report[17];
        report[17] = 0x41;
        Assert.IsFalse(Switch2UsbHdRumbleCodec.TryDecodeProController(report,
            out _, out _, out _, out failure));
        Assert.AreEqual(Switch2UsbHdRumbleDecodeFailure.InvalidGroupHeader,
            failure);
        report[17] = original;

        report[17] = 0x52;
        Assert.IsFalse(Switch2UsbHdRumbleCodec.TryDecodeProController(report,
            out _, out _, out _, out failure));
        Assert.AreEqual(Switch2UsbHdRumbleDecodeFailure.CounterMismatch,
            failure);
        report[17] = original;

        report[63] = 1;
        Assert.IsFalse(Switch2UsbHdRumbleCodec.TryDecodeProController(report,
            out _, out _, out _, out failure));
        Assert.AreEqual(Switch2UsbHdRumbleDecodeFailure.NonzeroReservedTail,
            failure);
    }

    [TestMethod]
    public void EncodeDecodeHotPathAllocatesNoManagedMemory()
    {
        var subframe = new Switch2HdRumbleSubframe(
            0x187, 0x155, 0x112, 0x2AA);
        var group = new Switch2HdRumbleGroup(subframe, default, default);
        Span<byte> encoded = stackalloc byte[
            Switch2HdRumbleCodec.SubframeLength];
        Span<byte> report = stackalloc byte[
            Switch2UsbHdRumbleCodec.ReportLength];
        Span<byte> bluetooth = stackalloc byte[
            Switch2BluetoothHdRumbleCodec.ProControllerPayloadLength];
        for (int warmup = 0; warmup < 1_000; warmup++)
        {
            Switch2HdRumbleCodec.TryEncode(subframe, encoded);
            Switch2HdRumbleCodec.TryDecode(encoded, out _);
            Switch2UsbHdRumbleCodec.TryEncodeProController(1, group, group,
                report);
            Switch2UsbHdRumbleCodec.TryDecodeProController(report, out _,
                out _, out _, out _);
            Switch2BluetoothHdRumbleCodec.TryEncodeProController(1, group,
                group, bluetooth);
            Switch2BluetoothHdRumbleCodec.TryDecodeProController(bluetooth,
                out _, out _, out _, out _);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool valid = true;
        for (int iteration = 0; iteration < 10_000; iteration++)
        {
            valid &= Switch2HdRumbleCodec.TryEncode(subframe, encoded);
            valid &= Switch2HdRumbleCodec.TryDecode(encoded,
                out var decoded) && decoded.Equals(subframe);
            valid &= Switch2UsbHdRumbleCodec.TryEncodeProController(1,
                group, group, report);
            valid &= Switch2UsbHdRumbleCodec.TryDecodeProController(report,
                out _, out var decodedLeft, out var decodedRight, out _) &&
                decodedLeft.Equals(group) && decodedRight.Equals(group);
            valid &= Switch2BluetoothHdRumbleCodec.TryEncodeProController(1,
                group, group, bluetooth);
            valid &= Switch2BluetoothHdRumbleCodec.TryDecodeProController(
                bluetooth, out _, out decodedLeft, out decodedRight, out _) &&
                decodedLeft.Equals(group) && decodedRight.Equals(group);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.IsTrue(valid);
        Assert.AreEqual(0L, allocated);
    }
}
