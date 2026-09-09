using DS4Windows;
using DS4Windows.InputDevices;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace DS4WindowsTests;

[TestClass]
public sealed class LegacyJoyConUdpRoutingTests
{
    [TestMethod]
    public void OriginalMotionHookUsesOnlyLogicalPublicationAndHonorsPauseRemoval()
    {
        // No ControlService constructor, native handles, server or OS input.
        var service = (ControlService)RuntimeHelpers.GetUninitializedObject(typeof(ControlService));
        var device = (JoyConDevice)RuntimeHelpers.GetUninitializedObject(typeof(JoyConDevice));
        Invoke(service, "PrepareDevUDPMotion", device, 0);
        Assert.IsNotNull(device.MotionEvent);
        Assert.IsTrue(device.NintendoUdpMotionSubscribed);
        FieldInfo rawReports = typeof(DS4Device).GetField("Report", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(rawReports);
        Assert.IsNull(rawReports.GetValue(device), "Physical halves must not emit duplicate or secondary-slot observations.");

        int published = 0;
        device.MotionEvent = (sender, _) => { Assert.AreSame(device, sender); published++; };
        device.PublishNintendoUdpMotion();
        Assert.AreEqual(1, published);
        Invoke(service, "SetDevUDPMotionSubscription", device, false);
        device.PublishNintendoUdpMotion();
        Assert.AreEqual(1, published);
        Invoke(service, "SetDevUDPMotionSubscription", device, true);
        device.PublishNintendoUdpMotion();
        Assert.AreEqual(2, published);
        Invoke(service, "RemoveDevUDPMotion", device);
        device.PublishNintendoUdpMotion();
        Assert.AreEqual(2, published);
        Assert.IsNull(device.MotionEvent);
        Assert.IsFalse(device.NintendoUdpMotionSubscribed);
        Assert.IsNull(rawReports.GetValue(device));
    }

    private static void Invoke(ControlService service, string name, params object[] arguments) =>
        typeof(ControlService).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(service, arguments);
}
