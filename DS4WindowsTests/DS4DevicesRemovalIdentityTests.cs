using System.Reflection;
using System.Runtime.CompilerServices;
using DS4Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public sealed class DS4DevicesRemovalIdentityTests
{
    [TestMethod]
    public void ExactCurrentDeviceRemovalClearsItsEntriesAndClosesItsHandle()
    {
        using var registry = new RegistryFixture();
        var device = CreateDevice("synthetic-path", "synthetic-serial");
        registry.Add(device);

        DS4Devices.RemoveDevice(device);

        Assert.AreEqual(0, registry.Paths.Count);
        Assert.AreEqual(0, registry.Serials.Count);
        Assert.AreEqual(0, registry.PathSet.Count);
        Assert.AreEqual(0, registry.SerialSet.Count);
        Assert.IsFalse(device.HidDevice.IsOpen);
        Assert.AreEqual(1L, CloseEpoch(device.HidDevice));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void DelayedSamePathRemovalPreservesReplacementAndItsSharedOrDistinctHandle(
        bool sharedHandle)
    {
        using var registry = new RegistryFixture();
        var removed = CreateDevice("synthetic-path", "synthetic-serial");
        var replacement = CreateDevice("synthetic-path", "synthetic-serial",
            sharedHandle ? removed.HidDevice : null);
        registry.Add(replacement);

        DS4Devices.On_Removal(removed, EventArgs.Empty);

        Assert.AreSame(replacement, registry.Paths[replacement.HidDevice.DevicePath]);
        Assert.AreSame(replacement, registry.Serials[replacement.MacAddress]);
        Assert.IsTrue(registry.PathSet.Contains(replacement.HidDevice.DevicePath));
        Assert.IsTrue(registry.SerialSet.Contains(replacement.MacAddress));
        Assert.IsTrue(replacement.HidDevice.IsOpen);
        Assert.AreEqual(0L, CloseEpoch(replacement.HidDevice));
        Assert.AreEqual(sharedHandle ? 0L : 1L, CloseEpoch(removed.HidDevice));
    }

    [TestMethod]
    public void SameSerialDifferentPathReplacementKeepsItsSerialOwnership()
    {
        using var registry = new RegistryFixture();
        var removed = CreateDevice("old-synthetic-path", "synthetic-serial");
        var replacement = CreateDevice("new-synthetic-path", "synthetic-serial");
        registry.Add(removed);
        registry.Add(replacement);

        DS4Devices.RemoveDevice(removed);

        Assert.IsFalse(registry.Paths.ContainsKey(removed.HidDevice.DevicePath));
        Assert.IsFalse(registry.PathSet.Contains(removed.HidDevice.DevicePath));
        Assert.AreSame(replacement, registry.Paths[replacement.HidDevice.DevicePath]);
        Assert.AreSame(replacement, registry.Serials[replacement.MacAddress]);
        Assert.IsTrue(registry.SerialSet.Contains(replacement.MacAddress));
        Assert.AreEqual(1L, CloseEpoch(removed.HidDevice));
        Assert.AreEqual(0L, CloseEpoch(replacement.HidDevice));
    }

    [TestMethod]
    public void SamePathDifferentSerialReplacementKeepsItsPathOwnership()
    {
        using var registry = new RegistryFixture();
        var removed = CreateDevice("synthetic-path", "old-synthetic-serial");
        var replacement = CreateDevice("synthetic-path", "new-synthetic-serial");
        registry.Add(removed);
        registry.Add(replacement);

        DS4Devices.RemoveDevice(removed);

        Assert.AreSame(replacement, registry.Paths[replacement.HidDevice.DevicePath]);
        Assert.IsTrue(registry.PathSet.Contains(replacement.HidDevice.DevicePath));
        Assert.IsFalse(registry.Serials.ContainsKey(removed.MacAddress));
        Assert.IsFalse(registry.SerialSet.Contains(removed.MacAddress));
        Assert.AreSame(replacement, registry.Serials[replacement.MacAddress]);
        Assert.IsTrue(registry.SerialSet.Contains(replacement.MacAddress));
        Assert.AreEqual(1L, CloseEpoch(removed.HidDevice));
        Assert.AreEqual(0L, CloseEpoch(replacement.HidDevice));
    }

    private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.NonPublic;

    private static DS4Device CreateDevice(string path, string serial, HidDevice shared = null)
    {
        var hid = shared ?? (HidDevice)RuntimeHelpers.GetUninitializedObject(typeof(HidDevice));
        if (shared == null)
        {
            typeof(HidDevice).GetField("_devicePath", InstanceFields)!.SetValue(hid, path);
            typeof(HidDevice).GetField("handleLock", InstanceFields)!.SetValue(hid, new object());
            // No native handle exists: CloseDevice only advances the logical
            // close epoch and clears managed state, without P/Invoke or HID IO.
            typeof(HidDevice).GetField("isOpen", InstanceFields)!.SetValue(hid, true);
        }
        var device = (DS4Device)RuntimeHelpers.GetUninitializedObject(typeof(DS4Device));
        typeof(DS4Device).GetField("hDevice", InstanceFields)!.SetValue(device, hid);
        typeof(DS4Device).GetField("Mac", InstanceFields)!.SetValue(device, serial);
        return device;
    }

    private static long CloseEpoch(HidDevice hid) =>
        (long)typeof(HidDevice).GetField("transferEpoch", InstanceFields)!.GetValue(hid)!;

    private sealed class RegistryFixture : IDisposable
    {
        internal readonly Dictionary<string, DS4Device> Paths = Get<Dictionary<string, DS4Device>>("Devices");
        internal readonly Dictionary<string, DS4Device> Serials = Get<Dictionary<string, DS4Device>>("serialDevices");
        internal readonly HashSet<string> PathSet = Get<HashSet<string>>("DevicePaths");
        internal readonly HashSet<string> SerialSet = Get<HashSet<string>>("deviceSerials");
        private readonly KeyValuePair<string, DS4Device>[] priorPaths;
        private readonly KeyValuePair<string, DS4Device>[] priorSerials;
        private readonly string[] priorPathSet;
        private readonly string[] priorSerialSet;

        internal RegistryFixture()
        {
            Monitor.Enter(Paths);
            priorPaths = Paths.ToArray();
            priorSerials = Serials.ToArray();
            priorPathSet = PathSet.ToArray();
            priorSerialSet = SerialSet.ToArray();
            Paths.Clear(); Serials.Clear(); PathSet.Clear(); SerialSet.Clear();
        }

        internal void Add(DS4Device device)
        {
            Paths[device.HidDevice.DevicePath] = device;
            Serials[device.MacAddress] = device;
            PathSet.Add(device.HidDevice.DevicePath);
            SerialSet.Add(device.MacAddress);
        }

        public void Dispose()
        {
            Paths.Clear(); Serials.Clear(); PathSet.Clear(); SerialSet.Clear();
            foreach (var entry in priorPaths) Paths.Add(entry.Key, entry.Value);
            foreach (var entry in priorSerials) Serials.Add(entry.Key, entry.Value);
            PathSet.UnionWith(priorPathSet);
            SerialSet.UnionWith(priorSerialSet);
            Monitor.Exit(Paths);
        }

        private static T Get<T>(string name) => (T)typeof(DS4Devices)
            .GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
    }
}
