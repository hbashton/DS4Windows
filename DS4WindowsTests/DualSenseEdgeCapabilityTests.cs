using System.Reflection;
using System.Runtime.CompilerServices;
using DS4Windows;
using DS4Windows.InputDevices;
using DS4WinWPF.DS4Control;

namespace DS4WindowsTests;

[TestClass]
public class DualSenseEdgeCapabilityTests
{
    [DataTestMethod]
    [DataRow(true, 0x0113u, true)]
    [DataRow(true, 0x0200u, true)]
    [DataRow(true, 0x0217u, true)]
    [DataRow(true, 0u, true)]
    [DataRow(false, 0x0215u, false)]
    [DataRow(false, 0x0223u, false)]
    [DataRow(false, 0x0224u, true)]
    [DataRow(false, 0x0630u, true)]
    [DataRow(false, 0u, true)]
    public void ImprovedRumbleUsesEdgeCapabilityNotBaseFirmwareNumber(
        bool edge, uint firmware, bool expected)
    {
        Assert.AreEqual(expected, DualSenseDevice.SupportsImprovedRumble(
            edge ? DualSenseDevice.DeviceSubType.DSEdge :
                DualSenseDevice.DeviceSubType.DualSense, firmware));
    }

    [DataTestMethod]
    [DataRow(true, 0x0113u, true)]
    [DataRow(true, 0x0217u, true)]
    [DataRow(false, 0x0223u, false)]
    [DataRow(false, 0x0224u, true)]
    [DataRow(false, 0u, true)]
    public void ProfileCannotBypassPhysicalRumbleCapability(bool edge, uint firmware, bool expected)
    {
        // The constructor and property mailbox start no readers, physical
        // writers, helper process, or device I/O on this dormant fixture.
        var hid = (HidDevice)RuntimeHelpers.GetUninitializedObject(typeof(HidDevice));
        var device = new DualSenseDevice(hid, "Edge capability fixture");
        typeof(DualSenseDevice).GetField("subType", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(device, edge ? DualSenseDevice.DeviceSubType.DSEdge : DualSenseDevice.DeviceSubType.DualSense);
        typeof(DualSenseDevice).GetField("updateVersion", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(device, firmware);
        device.UseAccurateRumble = false;
        Assert.IsFalse(device.UseAccurateRumble);
        device.UseRumble = true;
        device.UseAccurateRumble = true;
        Assert.AreEqual(expected, device.UseAccurateRumble);
        Assert.IsTrue(device.UseRumble, "Unsupported improved mode must retain legacy rumble.");
    }

    [TestMethod]
    public void EveryAuxiliaryButtonCombinationRoundTripsThroughCanonicalMapping()
    {
        var state = new DS4State();
        // Descending order also verifies released buttons clear a prior press.
        for (int bits = 255; bits >= 0; bits--)
        {
            DualSenseDevice.DecodeAuxiliaryButtons((byte)bits, state);
            Assert.AreEqual((bits & 1) != 0, state.PS);
            Assert.AreEqual((bits & 2) != 0, state.TouchButton);
            Assert.AreEqual((bits & 4) != 0, state.Mute);
            Assert.AreEqual((bits & 0x10) != 0, state.FnL);
            Assert.AreEqual((bits & 0x20) != 0, state.FnR);
            Assert.AreEqual((bits & 0x40) != 0, state.BLP);
            Assert.AreEqual((bits & 0x80) != 0, state.BRP);
            var mapped = ViiperStatePacketBuilder.BuildMappedState(state, -1);
            Assert.AreEqual((uint)(bits & 0xF7), mapped.Buttons >> 16);
            Assert.AreEqual(state.TouchButton, state.OutputTouchButton);
        }
    }
}
