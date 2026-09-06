using System.Runtime.ExceptionServices;
using DS4Windows;
using DS4Windows.Switch2;
using DS4WinWPF;
using DS4WinWPF.DS4Forms.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public sealed class ControllerListRetirementTests
{
    [DataTestMethod]
    [DataRow(Switch2Transport.Usb)]
    [DataRow(Switch2Transport.BluetoothLe)]
    public void TypedRetirementRemovesRowWithoutLegacyDeviceRemoval(Switch2Transport transport)
    {
        OnSta(() =>
        {
            var model = new ControllerListViewModel(new ProfileList());
            var device = Device(1001, transport);
            bool previousLinked = Global.linkedProfileCheck[2];
            try
            {
                model.AddController(device, 2);
                model.AddController(device, 2);
                Assert.AreEqual(1, model.ControllerCol.Count);
                Assert.AreSame(device, model.ControllerDict[2].Device);
                Assert.IsTrue(model.RemoveController(device, 2));
                Assert.AreEqual(0, model.ControllerCol.Count);
                Assert.AreEqual(0, model.ControllerDict.Count);
                Assert.IsFalse(model.RemoveController(device, 2));
            }
            finally { Global.linkedProfileCheck[2] = previousLinked; }
        });
    }

    [TestMethod]
    public void DelayedRemovalCannotEraseReusedSlotOrItsProfileState()
    {
        OnSta(() =>
        {
            var model = new ControllerListViewModel(new ProfileList());
            var oldDevice = Device(1003, Switch2Transport.Usb);
            var replacement = Device(1005, Switch2Transport.BluetoothLe);
            bool previousLinked = Global.linkedProfileCheck[3];
            try
            {
                model.AddController(oldDevice, 3);
                model.AddController(replacement, 3);
                Global.linkedProfileCheck[3] = true;
                Assert.AreEqual(1, model.ControllerCol.Count);
                Assert.AreSame(replacement, model.ControllerDict[3].Device);
                Assert.IsFalse(model.RemoveController(oldDevice, 3));
                Assert.IsTrue(Global.linkedProfileCheck[3]);
                Assert.AreSame(replacement, model.ControllerCol[0].Device);
                Assert.IsTrue(model.RemoveController(replacement, 3));
                Assert.IsFalse(Global.linkedProfileCheck[3]);
            }
            finally { Global.linkedProfileCheck[3] = previousLinked; }
        });
    }

    [TestMethod]
    public void RemovalMustMatchBothSparseSlotAndDeviceIdentity()
    {
        OnSta(() =>
        {
            var model = new ControllerListViewModel(new ProfileList());
            var first = Device(1007, Switch2Transport.Usb);
            var second = Device(1009, Switch2Transport.Usb);
            model.AddController(first, 2);
            model.AddController(second, 5);
            Assert.IsFalse(model.RemoveController(first, 5));
            Assert.IsFalse(model.RemoveController(second, 2));
            Assert.AreEqual(2, model.ControllerCol.Count);
            Assert.AreSame(first, model.ControllerDict[2].Device);
            Assert.AreSame(second, model.ControllerDict[5].Device);
        });
    }

    private static Switch2RuntimeInputDevice Device(ulong generation, Switch2Transport transport)
    {
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(generation, generation + 1,
            transport, out var device, out var failure), failure.ToString());
        return device;
    }

    private static void OnSta(Action action)
    {
        Exception failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(10)), "UI model test did not finish.");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
