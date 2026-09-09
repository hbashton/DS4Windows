using System.Runtime.CompilerServices;
using System.Xml.Serialization;
using DS4Windows;
using DS4Windows.InputDevices;
using DS4Windows.Switch2;
using DS4WinWPF;
using DS4WinWPF.DS4Control.DTOXml;
using DS4WinWPF.DS4Forms.ViewModels;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public sealed class Switch2ProfileIdentityTests
{
    [DataTestMethod]
    [DataRow(InputDeviceType.Switch2Pro, Switch2Transport.Usb, "S2-USB-PRO-")]
    [DataRow(InputDeviceType.Switch2Pro, Switch2Transport.BluetoothLe, "S2-BT-PRO-")]
    [DataRow(InputDeviceType.Switch2JoyConLeft, Switch2Transport.BluetoothLe, "S2-BT-L-")]
    [DataRow(InputDeviceType.Switch2JoyConRight, Switch2Transport.BluetoothLe, "S2-BT-R-")]
    [DataRow(InputDeviceType.Switch2JoyConJoined, Switch2Transport.BluetoothLe, "S2-BT-PAIR-")]
    public void FullIdentitySurvivesRuntimeGenerationChangesWithoutPretendingToBeAMac(
        InputDeviceType model, Switch2Transport transport, string prefix)
    {
        var first = Create(model, transport, 100);
        var reconnect = Create(model, transport, 200);
        var left = model == InputDeviceType.Switch2JoyConRight ? default : Peer(1);
        var right = model is InputDeviceType.Switch2JoyConRight or InputDeviceType.Switch2JoyConJoined
            ? Peer(2) : default;
        Assert.IsNull(first.ProfileLinkId);
        Assert.AreEqual("ID unavailable", first.DisplayIdentity);
        Assert.IsTrue(first.TryBindProfileIdentity(left, right));
        Assert.IsTrue(reconnect.TryBindProfileIdentity(left, right));
        Assert.AreEqual(first.ProfileLinkId, reconnect.ProfileLinkId);
        StringAssert.StartsWith(first.ProfileLinkId, prefix);
        Assert.AreEqual(prefix.Length + (model == InputDeviceType.Switch2JoyConJoined ? 65 : 32),
            first.ProfileLinkId.Length, "All 128 bits per peer must survive formatting.");
        Assert.AreEqual("ID " + first.ProfileLinkId, first.DisplayIdentity);
        Assert.AreEqual(DS4Device.BLANK_SERIAL, first.MacAddress);
        Assert.AreEqual(DS4Device.BLANK_SERIAL, first.getMacAddress());
        Assert.IsFalse(first.isValidSerial());
        Assert.IsFalse(first.AllowsPersistentIdentity, "Legacy HID identity authority must stay unchanged.");
        Assert.IsNull(first.HidDevice);
        Assert.IsFalse(first.TryBindProfileIdentity(Peer(3), right), "Identity is immutable once bound.");
        string id = first.ProfileLinkId;
        first.StartUpdate();
        Assert.IsFalse(first.TryBindProfileIdentity(left, right), "Published runtimes cannot change profile identity.");
        Assert.AreEqual(id, first.ProfileLinkId);
    }

    [TestMethod]
    public void PeerMembershipModelAndTransportDoNotAlias()
    {
        Assert.IsTrue(Switch2ProfileIdentity.TryCreate(InputDeviceType.Switch2Pro,
            Switch2Transport.Usb, Peer(1), default, out string usb));
        Assert.IsTrue(Switch2ProfileIdentity.TryCreate(InputDeviceType.Switch2Pro,
            Switch2Transport.BluetoothLe, Peer(1), default, out string bluetooth));
        Assert.AreNotEqual(usb, bluetooth, "There is no proven USB-to-BLE physical identity bridge.");
        Assert.IsTrue(Switch2ProfileIdentity.TryCreate(InputDeviceType.Switch2Pro,
            Switch2Transport.BluetoothLe, Peer(2), default, out string other));
        Assert.AreNotEqual(bluetooth, other);
        Assert.IsTrue(Switch2ProfileIdentity.TryCreate(InputDeviceType.Switch2JoyConJoined,
            Switch2Transport.BluetoothLe, Peer(1), Peer(2), out string pair));
        Assert.IsTrue(Switch2ProfileIdentity.TryCreate(InputDeviceType.Switch2JoyConJoined,
            Switch2Transport.BluetoothLe, Peer(1), Peer(3), out string replacement));
        Assert.AreNotEqual(pair, replacement);
        Assert.IsTrue(Switch2ProfileIdentity.TryCreate(InputDeviceType.Switch2JoyConLeft,
            Switch2Transport.BluetoothLe, Peer(1), default, out string standalone));
        Assert.AreNotEqual(pair, standalone);
        Assert.IsTrue(Switch2ProfileIdentity.TryCreate(InputDeviceType.Switch2JoyConJoined,
            Switch2Transport.BluetoothLe, Peer(1), Peer(2), out string restoredPair));
        Assert.AreEqual(pair, restoredPair);
    }

    [TestMethod]
    public void InvalidOrAmbiguousIdentitiesStayUnavailable()
    {
        foreach (var input in new[]
        {
            (InputDeviceType.Switch2Pro, Switch2Transport.Usb, default(Switch2PersistentPeerId), default(Switch2PersistentPeerId)),
            (InputDeviceType.Switch2Pro, Switch2Transport.Usb, Peer(1), Peer(2)),
            (InputDeviceType.Switch2JoyConRight, Switch2Transport.BluetoothLe, Peer(1), default),
            (InputDeviceType.Switch2JoyConLeft, Switch2Transport.BluetoothLe, default, Peer(1)),
            (InputDeviceType.Switch2JoyConJoined, Switch2Transport.BluetoothLe, Peer(1), Peer(1)),
            (InputDeviceType.Switch2JoyConLeft, Switch2Transport.Usb, Peer(1), default),
            (InputDeviceType.DS4, Switch2Transport.BluetoothLe, Peer(1), default),
        })
        {
            Assert.IsFalse(Switch2ProfileIdentity.TryCreate(input.Item1, input.Item2,
                input.Item3, input.Item4, out string id));
            Assert.IsNull(id);
        }
        var device = Create(InputDeviceType.Switch2Pro, Switch2Transport.Usb, 100);
        device.StartUpdate();
        Assert.IsFalse(device.TryBindProfileIdentity(Peer(1)));
        Assert.IsNull(device.ProfileLinkId);
    }

    [TestMethod]
    public void ExistingInstallLocalDerivationIsStableButInstallScoped()
    {
        byte[] key = Enumerable.Repeat((byte)0xA6, Switch2PersistentPeerId.InstallKeyLength).ToArray();
        ReadOnlySpan<byte> identity = "test-only-stable-windows-peer"u8;
        Assert.IsTrue(Switch2PersistentPeerId.TryDerive(key, identity,
            Switch2ControllerModel.ProController2, Switch2AdvertisementCodec.ProController2ProductId, out var first));
        Assert.IsTrue(Switch2PersistentPeerId.TryDerive(key, identity,
            Switch2ControllerModel.ProController2, Switch2AdvertisementCodec.ProController2ProductId, out var reconnect));
        Assert.AreEqual(first, reconnect);
        key[0] ^= 1;
        Assert.IsTrue(Switch2PersistentPeerId.TryDerive(key, identity,
            Switch2ControllerModel.ProController2, Switch2AdvertisementCodec.ProController2ProductId, out var anotherInstall));
        Assert.AreNotEqual(first, anotherInstall);
    }

    [TestMethod]
    public void ProfileLinksRoundTripFullIdsAndRemainIndependentOfLegacyMacLinks()
    {
        var pro = Create(InputDeviceType.Switch2Pro, Switch2Transport.BluetoothLe, 100);
        var pair = Create(InputDeviceType.Switch2JoyConJoined, Switch2Transport.BluetoothLe, 200);
        Assert.IsTrue(pro.TryBindProfileIdentity(Peer(1)));
        Assert.IsTrue(pair.TryBindProfileIdentity(Peer(2), Peer(3)));
        var previous = Global.store.linkedProfiles;
        try
        {
            Global.store.linkedProfiles = new Dictionary<string, string>();
            Global.changeLinkedProfile(pro.ProfileLinkId, "Pro game");
            Global.changeLinkedProfile(pair.ProfileLinkId, "Joy-Con game");
            Global.changeLinkedProfile("01:23:45:67:89:AB", "Legacy game");
            var dto = new LinkedProfilesDTO();
            dto.MapFrom(Global.store);
            var serializer = new XmlSerializer(typeof(LinkedProfilesDTO));
            using var text = new StringWriter();
            serializer.Serialize(text, dto);
            StringAssert.Contains(text.ToString(), pro.ProfileLinkId);
            StringAssert.Contains(text.ToString(), pair.ProfileLinkId);
            using var reader = new StringReader(text.ToString());
            var restored = (LinkedProfilesDTO)serializer.Deserialize(reader);
            Global.store.linkedProfiles = new Dictionary<string, string>();
            restored.MapTo(Global.store);
            Assert.IsTrue(Global.containsLinkedProfile(pro.ProfileLinkId));
            Assert.AreEqual("Pro game", Global.getLinkedProfile(pro.ProfileLinkId));
            Assert.AreEqual("Joy-Con game", Global.getLinkedProfile(pair.ProfileLinkId));
            Assert.AreEqual("Legacy game", Global.getLinkedProfile("01:23:45:67:89:AB"));
            Global.changeLinkedProfile(pro.ProfileLinkId, "Updated game");
            Global.removeLinkedProfile(pair.ProfileLinkId);
            Assert.IsFalse(Global.containsLinkedProfile(pair.ProfileLinkId));
            Assert.AreEqual("Updated game", Global.getLinkedProfile(pro.ProfileLinkId));
            Assert.AreEqual("Legacy game", Global.getLinkedProfile("01:23:45:67:89:AB"));
        }
        finally { Global.store.linkedProfiles = previous; }
    }

    [TestMethod]
    public void DisplayAndLinkAvailabilityUseTheBoundIdentityWithoutSavingAnything()
    {
        var device = Create(InputDeviceType.Switch2Pro, Switch2Transport.BluetoothLe, 100);
        var vm = new CompositeDeviceModel(device, 0, string.Empty, new ProfileList());
        bool previous = Global.linkedProfileCheck[0];
        try
        {
            Global.linkedProfileCheck[0] = false;
            Assert.IsFalse(vm.CanLinkProfile);
            vm.LinkedProfile = true;
            Assert.IsFalse(Global.linkedProfileCheck[0], "Unavailable identity cannot create a blank-key link.");
            Assert.IsTrue(device.TryBindProfileIdentity(Peer(1)));
            Assert.IsTrue(vm.CanLinkProfile);
            StringAssert.Contains(vm.IdText, device.ProfileLinkId);
            StringAssert.Contains(new DeviceListItem(device).IdText, device.ProfileLinkId);
            Assert.IsFalse(vm.IdText.Contains(DS4Device.BLANK_SERIAL));
        }
        finally { Global.linkedProfileCheck[0] = previous; }
        var legacy = new LegacyIdentityDevice("01:23:45:67:89:AB");
        Assert.AreEqual(legacy.MacAddress, legacy.ProfileLinkId);
        Assert.AreEqual(legacy.MacAddress, legacy.DisplayIdentity);
        Assert.IsNull(new LegacyIdentityDevice(null).ProfileLinkId);
        Assert.IsNull(new LegacyIdentityDevice(string.Empty).ProfileLinkId);
        Assert.IsNull(new LegacyIdentityDevice(DS4Device.BLANK_SERIAL).ProfileLinkId);
    }

    [DataTestMethod]
    [DataRow(InputDeviceType.Switch2JoyConJoined, null, false)]
    [DataRow(InputDeviceType.Switch2JoyConJoined, "Retained game", true)]
    [DataRow(InputDeviceType.Switch2JoyConJoined, "Different game", false)]
    [DataRow(InputDeviceType.Switch2JoyConLeft, null, false)]
    [DataRow(InputDeviceType.Switch2JoyConLeft, "Retained game", true)]
    [DataRow(InputDeviceType.Switch2JoyConLeft, "Different game", false)]
    [DataRow(InputDeviceType.Switch2JoyConRight, null, false)]
    [DataRow(InputDeviceType.Switch2JoyConRight, "Retained game", true)]
    [DataRow(InputDeviceType.Switch2JoyConRight, "Different game", false)]
    public void HandoffLinkStateUsesSuccessorAndRetainedProfileWithoutCreatingOrChangingLinks(
        InputDeviceType model, string successorProfile, bool expected)
    {
        var device = Create(model, Switch2Transport.BluetoothLe, 100);
        var left = model == InputDeviceType.Switch2JoyConRight ? default : Peer(1);
        var right = model == InputDeviceType.Switch2JoyConLeft ? default : Peer(2);
        Assert.IsTrue(device.TryBindProfileIdentity(left, right));
        var previousLinks = Global.store.linkedProfiles;
        string previousProfile = Global.ProfilePath[0];
        bool previousLinkState = Global.linkedProfileCheck[0];
        try
        {
            Global.store.linkedProfiles = new Dictionary<string, string>();
            Global.changeLinkedProfile("01:23:45:67:89:AB", "Legacy game");
            if (successorProfile != null)
                Global.changeLinkedProfile(device.ProfileLinkId, successorProfile);
            var expectedLinks = Global.store.linkedProfiles.ToArray();
            Global.ProfilePath[0] = "Retained game";
            foreach (bool predecessorLinked in new[] { false, true })
            {
                Global.linkedProfileCheck[0] = predecessorLinked;
                Global.linkedProfileCheck[0] =
                    ControlService.IsRetainedProfileLinked(device, Global.ProfilePath[0]);
                Assert.AreEqual(expected, Global.linkedProfileCheck[0]);
                Assert.AreEqual("Retained game", Global.ProfilePath[0], "Handoff cannot switch the retained profile.");
                CollectionAssert.AreEquivalent(expectedLinks, Global.store.linkedProfiles.ToArray(),
                    "Handoff cannot migrate, create, overwrite, or remove any profile link.");
            }
            Assert.IsTrue(ControlService.IsRetainedProfileLinked(
                new LegacyIdentityDevice("01:23:45:67:89:AB"), "Legacy game"));
            Assert.IsFalse(ControlService.IsRetainedProfileLinked(
                new LegacyIdentityDevice(DS4Device.BLANK_SERIAL), "Retained game"));
            Assert.IsFalse(ControlService.IsRetainedProfileLinked(device, null));
            Assert.IsFalse(ControlService.IsRetainedProfileLinked(null, "Retained game"));
        }
        finally
        {
            Global.store.linkedProfiles = previousLinks;
            Global.ProfilePath[0] = previousProfile;
            Global.linkedProfileCheck[0] = previousLinkState;
        }
    }

    [TestMethod]
    public void LaterLegacyIdentityUpdatesRefreshDisplayAndLinkAvailability()
    {
        var device = new LegacyIdentityDevice(DS4Device.BLANK_SERIAL);
        var vm = new CompositeDeviceModel(device, 0, string.Empty, new ProfileList());
        int textChanges = 0, availabilityChanges = 0, linkChanges = 0;
        vm.IdTextChanged += (_, _) => textChanges++;
        vm.CanLinkProfileChanged += (_, _) => availabilityChanges++;
        vm.LinkedProfileChanged += (_, _) => linkChanges++;
        Assert.IsFalse(vm.CanLinkProfile);
        device.SetTestMac("01:23:45:67:89:AB");
        vm.RequestUpdatedIdentity();
        Assert.AreEqual(1, textChanges);
        Assert.AreEqual(1, availabilityChanges);
        Assert.AreEqual(1, linkChanges);
        Assert.IsTrue(vm.CanLinkProfile);
        StringAssert.Contains(vm.IdText, device.MacAddress);
        device.SetTestMac(DS4Device.BLANK_SERIAL);
        vm.RequestUpdatedIdentity();
        Assert.IsFalse(vm.CanLinkProfile);
        Assert.AreEqual(2, availabilityChanges);
        StringAssert.Contains(Source("DS4Forms/ViewModels/ControllerListViewModel.cs"),
            "device.MacAddressChanged += (sender, e) => RequestUpdatedIdentity();");
    }

    [TestMethod]
    public void OptionsDialogHandlesMultipleUnidentifiedSwitch2AndMixedLegacyEntries()
    {
        var first = Create(InputDeviceType.Switch2Pro, Switch2Transport.Usb, 100);
        var second = Create(InputDeviceType.Switch2JoyConLeft, Switch2Transport.BluetoothLe, 200);
        var legacy = new LegacyIdentityDevice("01:23:45:67:89:AB");
        var service = (ControlService)RuntimeHelpers.GetUninitializedObject(typeof(ControlService));
        service.DS4Controllers = new DS4Device[] { first, legacy, null, second };
        var vm = new ControllerRegDeviceOptsViewModel(new ControlServiceDeviceOptions(), service);
        Assert.AreEqual(3, vm.CurrentInputDevices.Count);
        foreach (int index in new[] { 0, 2, -1, 9 })
        {
            vm.ControllerSelectedIndex = index;
            Assert.AreEqual(0, vm.FindTabOptionsIndex());
            vm.FindFittingDataContext();
            Assert.IsNull(vm.DataContextObject);
        }
        vm.ControllerSelectedIndex = 1;
        Assert.AreEqual(1, vm.FindTabOptionsIndex());
        vm.FindFittingDataContext();
        Assert.IsInstanceOfType(vm.DataContextObject, typeof(DS4ControllerOptionsWrapper));
        Assert.AreSame(legacy.optionsStore, vm.CurrentDS4Options);
        vm.ControllerSelectedIndex = 2;
        vm.FindFittingDataContext();
        Assert.IsNull(vm.DataContextObject, "Switch2 selection cannot retain another controller's options context.");
        var saved = new List<DS4Device>();
        vm.SaveControllerConfigs(saved.Add);
        CollectionAssert.AreEqual(new DS4Device[] { legacy }, saved.ToArray(),
            "Legacy saves are preserved, but transport-owned/no-store devices cannot reach legacy config persistence.");
    }

    [TestMethod]
    public void ProductionBindsIdentityBeforeProfileSelectionAndNeverSubstitutesAMac()
    {
        string bluetooth = Source("DS4Control/Switch2/Switch2BluetoothProductionCoordinator.cs");
        Assert.AreEqual(3, bluetooth.Split("TryBindProfileIdentity(").Length - 1);
        StringAssert.Contains(bluetooth, "TryBindProfileIdentity(left.PeerId, right.PeerId)");
        string usb = Source("DS4Control/Switch2/Switch2ProUsbProductionCoordinator.cs");
        StringAssert.Contains(usb, "identityDeriver.TryDerive(registration.ContainerIdentity");
        Assert.IsTrue(usb.IndexOf("TryBindProfileIdentity(") < usb.IndexOf("registrations.TryAttachOwnedUsb("));
        string service = Source("DS4Control/ControlService.cs");
        Assert.AreEqual(2, service.Split("device.ProfileLinkId is string profileId").Length - 1);
        Assert.IsFalse(service.Contains("containsLinkedProfile(device.getMacAddress())"));
        int retainedLink = service.IndexOf("IsRetainedProfileLinked(device, ProfilePath[slot])");
        Assert.IsTrue(retainedLink > service.IndexOf("bool keepsOutput ="));
        Assert.IsTrue(retainedLink < service.IndexOf("if (!useAutoProfile)", retainedLink));
        StringAssert.Contains(service.Substring(service.LastIndexOf("if (keepsOutput)", retainedLink),
            retainedLink - service.LastIndexOf("if (keepsOutput)", retainedLink)),
            "Global.linkedProfileCheck[slot] =");
        string list = Source("DS4Forms/ViewModels/ControllerListViewModel.cs");
        Assert.IsFalse(list.Contains("changeLinkedProfile(device.getMacAddress()"));
        Assert.IsFalse(list.Contains("removeLinkedProfile(device.getMacAddress()"));
        string options = Source("DS4Forms/ViewModels/ControllerRegDeviceOptsViewModel.cs");
        Assert.IsFalse(options.Contains("inputDeviceSettings"));
        StringAssert.Contains(options, "if (item.Device.optionsStore != null)");
        string runtime = Source("DS4Control/Switch2/Switch2RuntimeInputDevice.Identity.cs");
        Assert.IsFalse(runtime.Contains("Mac ="));
        Assert.IsFalse(runtime.Contains("CreateRandom"));
        Assert.IsFalse(runtime.Contains("deviceGeneration"));
        Assert.IsFalse(runtime.Contains("transportGeneration"));
    }

    private static Switch2PersistentPeerId Peer(byte value)
    {
        byte[] encoded = Enumerable.Repeat(value, Switch2PersistentPeerId.EncodedLength).ToArray();
        Assert.IsTrue(Switch2PersistentPeerId.TryRead(encoded, out var peer));
        return peer;
    }

    private static Switch2RuntimeInputDevice Create(InputDeviceType model, Switch2Transport transport, ulong generation)
    {
        Switch2RuntimeInputDevice device;
        Switch2RuntimeInputDeviceCreateFailure failure;
        bool created = model switch
        {
            InputDeviceType.Switch2Pro => Switch2RuntimeInputDevice.TryCreatePro(generation, generation + 1,
                transport, out device, out failure),
            InputDeviceType.Switch2JoyConJoined => Switch2RuntimeInputDevice.TryCreateJoinedJoyCon(generation,
                generation + 1, generation + 2, generation + 3, generation + 4, generation + 5, out device, out failure),
            _ => Switch2RuntimeInputDevice.TryCreateStandaloneJoyCon(model == InputDeviceType.Switch2JoyConLeft
                ? Switch2ControllerModel.JoyCon2Left : Switch2ControllerModel.JoyCon2Right,
                generation, generation + 1, out device, out failure),
        };
        Assert.IsTrue(created, failure.ToString());
        return device;
    }

    private static string Source(string relative, [CallerFilePath] string testPath = "") =>
        File.ReadAllText(Path.Combine(Path.GetDirectoryName(testPath), "..", "DS4Windows", relative));

    private sealed class LegacyIdentityDevice : DS4Device
    {
        internal LegacyIdentityDevice(string mac) : base("Legacy test", InputDeviceType.DS4, ConnectionType.USB)
        {
            Mac = mac;
            optionsStore = new DS4ControllerOptions(InputDeviceType.DS4);
        }

        internal void SetTestMac(string mac) => Mac = mac;
    }
}
