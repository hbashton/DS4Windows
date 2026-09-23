using System.Buffers.Binary;
using DS4Windows;

namespace DS4WindowsTests;

[TestClass]
public class ViiperCanonicalMotionCalibrationTests
{
    [DataTestMethod]
    [DataRow(ViiperVirtualDeviceType.DualSense)]
    [DataRow(ViiperVirtualDeviceType.DualSenseEdge)]
    public void CanonicalMotionSerializesAtSixteenGyroUnitsAnd8192AccelUnits(ViiperVirtualDeviceType type)
    {
        Assert.AreEqual(16, SixAxis.GYRO_RES_IN_DEG_SEC);
        Assert.AreEqual(8192, SixAxis.ACC_RES_PER_G);
        var state = new DS4State
        {
            Motion = new SixAxis(0, 0, 0, 0, 0, 8192, 0.001)
            {
                gyroPitchFull = 100 * SixAxis.GYRO_RES_IN_DEG_SEC,
                gyroYawFull = 200 * SixAxis.GYRO_RES_IN_DEG_SEC,
                gyroRollFull = -50 * SixAxis.GYRO_RES_IN_DEG_SEC,
                accelXFull = 0, accelYFull = 0, accelZFull = SixAxis.ACC_RES_PER_G,
            },
        };
        byte[] packet = ViiperStatePacketBuilder.Build(type, state, -1);
        // The identical fixed motion vector is consumed by VIIPER's
        // TestVirtualSonyCalibrationMatchesDS4WindowsCanonicalMotion, through
        // its real V5 decoder, USB encoder, and feature05 calibration response.
        CollectionAssert.AreEqual(Convert.FromHexString("400680F32003000000000020"), packet.AsSpan(21, 12).ToArray());
        Assert.AreEqual((short)1600, BinaryPrimitives.ReadInt16LittleEndian(packet.AsSpan(21)));
        Assert.AreEqual((short)-3200, BinaryPrimitives.ReadInt16LittleEndian(packet.AsSpan(23)));
        Assert.AreEqual((short)800, BinaryPrimitives.ReadInt16LittleEndian(packet.AsSpan(25)));
    }
}
