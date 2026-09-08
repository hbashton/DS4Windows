using System;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public class Switch2ProHeadsetControlsTests
{
    [DataTestMethod]
    [DataRow(0, Switch2ProButton.FaceSouth)]
    [DataRow(1, Switch2ProButton.FaceEast)]
    [DataRow(2, Switch2ProButton.FaceWest)]
    [DataRow(3, Switch2ProButton.FaceNorth)]
    [DataRow(4, Switch2ProButton.RightShoulder)]
    [DataRow(5, Switch2ProButton.RightTrigger)]
    [DataRow(6, Switch2ProButton.Start)]
    [DataRow(7, Switch2ProButton.RightStick)]
    [DataRow(8, Switch2ProButton.DpadDown)]
    [DataRow(9, Switch2ProButton.DpadRight)]
    [DataRow(10, Switch2ProButton.DpadLeft)]
    [DataRow(11, Switch2ProButton.DpadUp)]
    [DataRow(12, Switch2ProButton.LeftShoulder)]
    [DataRow(13, Switch2ProButton.LeftTrigger)]
    [DataRow(14, Switch2ProButton.Back)]
    [DataRow(15, Switch2ProButton.LeftStick)]
    [DataRow(16, Switch2ProButton.Guide)]
    [DataRow(17, Switch2ProButton.Capture)]
    [DataRow(18, Switch2ProButton.RightPaddle)]
    [DataRow(19, Switch2ProButton.LeftPaddle)]
    [DataRow(20, Switch2ProButton.C)]
    public void EveryDocumentedBitMapsToItsCanonicalSemantic(int bit, Switch2ProButton expected)
    {
        byte[] body = Body();
        uint raw = 1u << bit;
        WriteButtons(body, raw);
        Assert.IsTrue(Switch2ProHeadsetControlsCodec.TryDecode(body, out var controls));
        Assert.AreEqual(raw, controls.RawButtonBits);
        Assert.AreEqual(expected, controls.Buttons);
        Assert.AreEqual(0u, controls.UnknownButtonBits);
    }

    [TestMethod]
    public void AllUnknownCombinationsStayRawWithoutManufacturingControls()
    {
        byte[] body = Body();
        for (uint unknown = 0; unknown < 8; unknown++)
        {
            uint unknownBits = unknown << 21;
            WriteButtons(body, unknownBits);
            Assert.IsTrue(Switch2ProHeadsetControlsCodec.TryDecode(body, out var controls));
            Assert.AreEqual(unknownBits, controls.RawButtonBits);
            Assert.AreEqual(unknownBits, controls.UnknownButtonBits);
            Assert.AreEqual(Switch2ProButton.None, controls.Buttons);

            WriteButtons(body, unknownBits | (1u << 0) | (1u << 19));
            Assert.IsTrue(Switch2ProHeadsetControlsCodec.TryDecode(body, out controls));
            Assert.AreEqual(Switch2ProButton.FaceSouth | Switch2ProButton.LeftPaddle, controls.Buttons);
            Assert.AreEqual(unknownBits, controls.UnknownButtonBits);
        }
    }

    [TestMethod]
    public void AllKnownButtonsComposeWithoutCommon05RawBitCasting()
    {
        byte[] body = Body();
        WriteButtons(body, 0xFFFFFF);
        Assert.IsTrue(Switch2ProHeadsetControlsCodec.TryDecode(body, out var controls));
        Assert.AreEqual(0xFFFFFFu, controls.RawButtonBits);
        Assert.AreEqual(0xE00000u, controls.UnknownButtonBits);
        Assert.AreEqual((Switch2ProButton)Switch2ProUsbInputProjection.KnownButtonMask, controls.Buttons);
        Assert.AreNotEqual((Switch2ProButton)controls.RawButtonBits, controls.Buttons);
    }

    [DataTestMethod]
    [DataRow(0, 0)]
    [DataRow(0, 30)]
    [DataRow(0, 40)]
    [DataRow(50, 0)]
    [DataRow(50, 30)]
    [DataRow(50, 40)]
    public void EveryEvidencedLengthPairAdmitsTheSameControls(int audioLength, int motionLength)
    {
        byte[] body = Body();
        body[14] = (byte)audioLength;
        body[65] = (byte)motionLength;
        WriteButtons(body, 0x12AA55);
        Assert.IsTrue(Switch2ProHeadsetControlsCodec.TryDecode(body, out var controls));
        Assert.AreEqual(0x12AA55u, controls.RawButtonBits);
        Assert.AreEqual(new Switch2StickRaw(0x234, 0xBCA), controls.LeftStick);
        Assert.AreEqual(new Switch2StickRaw(0x678, 0xDEF), controls.RightStick);
    }

    [TestMethod]
    public void AllOtherDeclaredLengthsRejectAndClearTheResult()
    {
        byte[] body = Body();
        for (int length = 0; length <= byte.MaxValue; length++)
        {
            body[14] = (byte)length;
            body[65] = 30;
            if (length is not (0 or 50))
            {
                Assert.IsFalse(Switch2ProHeadsetControlsCodec.TryDecode(body, out var invalid));
                Assert.AreEqual(0u, invalid.RawButtonBits);
                Assert.AreEqual(default(Switch2StickRaw), invalid.LeftStick);
            }
            body[14] = 50;
            body[65] = (byte)length;
            if (length is not (0 or 30 or 40))
            {
                Assert.IsFalse(Switch2ProHeadsetControlsCodec.TryDecode(body, out var invalid));
                Assert.AreEqual(0u, invalid.RawButtonBits);
                Assert.AreEqual(default(Switch2StickRaw), invalid.RightStick);
            }
        }
    }

    [TestMethod]
    public void TruncationsOversizeAndOtherReportBodiesReject()
    {
        byte[] body = Body();
        for (int length = 0; length < 112; length++)
            Assert.IsFalse(Switch2ProHeadsetControlsCodec.TryDecode(body.AsSpan(0, length), out _));
        for (int length = 113; length <= 256; length++)
            Assert.IsFalse(Switch2ProHeadsetControlsCodec.TryDecode(new byte[length], out _));
        Assert.IsFalse(Switch2ProHeadsetControlsCodec.TryDecode(new byte[63], out _));
        Assert.IsFalse(Switch2ProHeadsetControlsCodec.TryDecode(new byte[64], out _));
    }

    [TestMethod]
    public void CounterAndPowerPreserveAllEightBitsWithoutInventedRateOrBatterySemantics()
    {
        byte[] body = Body();
        for (int value = 0; value <= byte.MaxValue; value++)
        {
            body[0] = (byte)value;
            body[1] = (byte)(byte.MaxValue - value);
            Assert.IsTrue(Switch2ProHeadsetControlsCodec.TryDecode(body, out var controls));
            Assert.AreEqual((byte)value, controls.Counter);
            Assert.AreEqual((byte)(byte.MaxValue - value), controls.PowerInfo);
        }
    }

    [TestMethod]
    public void TwelveBitSticksRemainExactAndReuseTheExistingAxisProjection()
    {
        byte[] body = Body();
        foreach (ushort value in new ushort[] { 0, 1, 0x7FF, 0x800, 0xFFE, 0xFFF })
        {
            PackStick(body.AsSpan(5, 3), value, (ushort)(0xFFF - value));
            PackStick(body.AsSpan(8, 3), (ushort)(0xFFF - value), value);
            Assert.IsTrue(Switch2ProHeadsetControlsCodec.TryDecode(body, out var controls));
            Assert.AreEqual(value, controls.LeftStick.X);
            Assert.AreEqual((ushort)(0xFFF - value), controls.LeftStick.Y);
            Assert.AreEqual((ushort)(0xFFF - value), controls.RightStick.X);
            Assert.AreEqual(value, controls.RightStick.Y);
            Assert.IsTrue(Switch2ProfileAxisProjection.TryMap(controls.LeftStick.X, controls.LeftStick.X - 0x800,
                0x800, 0x7FF, false, out var x));
            Assert.AreEqual(value, x.RawValue);
            if (value == 0) Assert.AreEqual(short.MinValue, x.SignedValue);
            if (value == 0x800) Assert.AreEqual((short)0, x.SignedValue);
            if (value == 0xFFF) Assert.AreEqual(short.MaxValue, x.SignedValue);
        }
    }

    [TestMethod]
    public void MicrophoneMotionAndReservedBytesDoNotBecomeControlsOrGetRetained()
    {
        byte[] body = Body();
        WriteButtons(body, 0x12AA55);
        Assert.IsTrue(Switch2ProHeadsetControlsCodec.TryDecode(body, out var first));
        body.AsSpan(15, 50).Fill(0xFF);
        body.AsSpan(66, 46).Fill(0xA5);
        body[11] ^= 0xFF;
        body[12] ^= 0xFF;
        body[13] ^= 0xFF;
        Assert.IsTrue(Switch2ProHeadsetControlsCodec.TryDecode(body, out var second));
        Assert.AreEqual(first.Counter, second.Counter);
        Assert.AreEqual(first.PowerInfo, second.PowerInfo);
        Assert.AreEqual(first.RawButtonBits, second.RawButtonBits);
        Assert.AreEqual(first.Buttons, second.Buttons);
        Assert.AreEqual(first.LeftStick, second.LeftStick);
        Assert.AreEqual(first.RightStick, second.RightStick);
        Array.Fill(body, (byte)0);
        Assert.AreEqual((byte)42, first.Counter);
        Assert.AreEqual((byte)0x24, first.PowerInfo);
        Assert.AreEqual(0x12AA55u, first.RawButtonBits);
        Assert.AreEqual(new Switch2StickRaw(0x234, 0xBCA), first.LeftStick);
        Assert.AreEqual(new Switch2StickRaw(0x678, 0xDEF), first.RightStick);
        Assert.IsFalse(System.Runtime.CompilerServices.RuntimeHelpers.IsReferenceOrContainsReferences<Switch2ProHeadsetControls>());
    }

    [TestMethod]
    public void WarmValidAndInvalidDecodingAllocatesNothing()
    {
        byte[] body = Body();
        for (int index = 0; index < 1000; index++) Switch2ProHeadsetControlsCodec.TryDecode(body, out _);
        long before = GC.GetAllocatedBytesForCurrentThread();
        uint checksum = 0;
        for (int index = 0; index < 10000; index++)
        {
            body[2] = (byte)index;
            body[65] = 30;
            Switch2ProHeadsetControlsCodec.TryDecode(body, out var controls);
            checksum += controls.RawButtonBits;
            body[65] = 41;
            Switch2ProHeadsetControlsCodec.TryDecode(body, out _);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.AreEqual(0L, allocated);
        Assert.IsTrue(checksum > 0);
    }

    private static byte[] Body()
    {
        var body = new byte[112];
        body[0] = 42;
        body[1] = 0x24;
        body[5] = 0x34; body[6] = 0xA2; body[7] = 0xBC;
        body[8] = 0x78; body[9] = 0xF6; body[10] = 0xDE;
        body[14] = 50;
        body[65] = 30;
        return body;
    }

    private static void WriteButtons(byte[] body, uint raw)
    {
        body[2] = (byte)raw;
        body[3] = (byte)(raw >> 8);
        body[4] = (byte)(raw >> 16);
    }

    private static void PackStick(Span<byte> target, ushort x, ushort y)
    {
        target[0] = (byte)x;
        target[1] = (byte)((x >> 8) | ((y & 0xF) << 4));
        target[2] = (byte)(y >> 4);
    }
}
