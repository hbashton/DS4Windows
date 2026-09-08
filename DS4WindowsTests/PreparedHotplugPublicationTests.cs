using System.Runtime.CompilerServices;
using DS4Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4WindowsTests;

[TestClass]
public class PreparedHotplugPublicationTests
{
    [TestMethod]
    public void FailedPreparationCannotAnnounceAControllerAfterItsRemoval()
    {
        var service = CreateService();
        var device = CreateDevice();
        int notifications = 0;
        service.HotplugController += (_, _, _) => notifications++;
        service.DS4Controllers[0] = device;
        // Mirrors the existing synchronous Removal callback during StartUpdate.
        service.DS4Controllers[0] = null;
        service.PublishPreparedHotplug(device, 0);
        Assert.AreEqual(0, notifications);
    }

    [TestMethod]
    public void ReplacedOrAbsentDeviceCannotPublishIntoNewGeneration()
    {
        var service = CreateService();
        var retired = CreateDevice();
        var replacement = CreateDevice();
        int notifications = 0;
        service.HotplugController += (_, _, _) => notifications++;
        service.PublishPreparedHotplug(null, 0);
        service.DS4Controllers[0] = replacement;
        service.PublishPreparedHotplug(retired, 0);
        Assert.AreEqual(0, notifications);
    }

    [TestMethod]
    public void UnchangedPreparedDevicePublishesOnceEvenBeforeWorkerLiveness()
    {
        var service = CreateService();
        var device = CreateDevice();
        int notifications = 0;
        service.HotplugController += (sender, published, index) =>
        {
            Assert.AreSame(service, sender);
            Assert.AreSame(device, published);
            Assert.AreEqual(0, index);
            notifications++;
        };
        service.DS4Controllers[0] = device;
        service.PublishPreparedHotplug(device, 0);
        Assert.AreEqual(1, notifications);
    }

    private static DS4Device CreateDevice() =>
        (DS4Device)RuntimeHelpers.GetUninitializedObject(typeof(DS4Device));

    private static ControlService CreateService()
    {
        // No service constructor, device enumeration, virtual pads or driver IO.
        var service = (ControlService)RuntimeHelpers.GetUninitializedObject(typeof(ControlService));
        service.DS4Controllers = new DS4Device[Global.MAX_DS4_CONTROLLER_COUNT];
        return service;
    }
}
