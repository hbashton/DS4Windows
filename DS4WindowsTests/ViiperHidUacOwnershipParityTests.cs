using DS4Windows;

namespace DS4WindowsTests
{
    [TestClass]
    public class ViiperHidUacOwnershipParityTests
    {
        private const string NativeRoot = @"ROOT\VIIPERUDE\0000";
        private const ulong NativeSession = 0xB16B00B5;

        // VIIPER's composite PlayStation descriptors use MI_00 for UAC
        // control, MI_01 for render, MI_02 for capture, and MI_03 for HID.
        // DS4Windows explicitly supports both DS4 product IDs, plus DS5 and
        // Edge, so ownership must remain persona/PID-neutral.
        [DataTestMethod]
        [DataRow("05C4")]
        [DataRow("09CC")]
        [DataRow("0CE6")]
        [DataRow("0DF2")]
        public void CompositeHidRenderAndCaptureInterfacesShareExactOwner(
            string productId)
        {
            const int port = 2;
            string usbDevice =
                $@"USB\VID_054C&PID_{productId}\6&VIIPER_A";
            ViiperPnPTopologyIdentity hid = ResolveViiperInterface(
                productId, "03", "HID", usbDevice, port);
            ViiperPnPTopologyIdentity render = ResolveViiperInterface(
                productId, "01", "RENDER", usbDevice, port);
            ViiperPnPTopologyIdentity capture = ResolveViiperInterface(
                productId, "02", "CAPTURE", usbDevice, port);

            Assert.IsTrue(hid.IsSameUsbDevice(render));
            Assert.IsTrue(hid.IsSameUsbDevice(capture));

            var table = new ViiperPnPOwnershipTable();
            int token = table.AllocateToken();
            Assert.IsTrue(table.Publish(token, new ViiperPnPCorrelation(
                ViiperPnPTransport.NativeUdeCx, 0x100000001, 17,
                NativeSession,
                NativeRoot, string.Empty, port)));

            Assert.IsTrue(table.Matches(token, hid));
            Assert.IsTrue(table.Matches(token, render));
            Assert.IsTrue(table.Matches(token, capture));

            ViiperPnPTopologyIdentity samePersonaOtherPort =
                ResolveViiperInterface(productId, "01", "OTHER",
                    $@"USB\VID_054C&PID_{productId}\6&VIIPER_B", 3);
            Assert.IsFalse(table.Matches(token, samePersonaOtherPort));
        }

        [DataTestMethod]
        [DataRow("09CC")]
        [DataRow("0CE6")]
        [DataRow("0DF2")]
        public void PhysicalSonyHidAndUacShareTheirUsbParentButAreNeverOwned(
            string productId)
        {
            string usbDevice =
                $@"USB\VID_054C&PID_{productId}\5&PHYSICAL";
            ViiperPnPTopologyIdentity hid = ResolvePhysicalInterface(
                productId, "03", "HID", usbDevice, 4);
            ViiperPnPTopologyIdentity render = ResolvePhysicalInterface(
                productId, "01", "RENDER", usbDevice, 4);
            ViiperPnPTopologyIdentity capture = ResolvePhysicalInterface(
                productId, "02", "CAPTURE", usbDevice, 4);

            Assert.AreEqual(ViiperPnPTransport.Unknown, hid.Transport);
            Assert.IsTrue(hid.IsSameUsbDevice(render));
            Assert.IsTrue(hid.IsSameUsbDevice(capture));

            var table = new ViiperPnPOwnershipTable();
            int token = table.AllocateToken();
            Assert.IsTrue(table.Publish(token, new ViiperPnPCorrelation(
                ViiperPnPTransport.NativeUdeCx, 0x100000002, 18,
                NativeSession,
                NativeRoot, string.Empty, 4)));
            Assert.IsFalse(table.Matches(token, hid));
            Assert.IsFalse(table.Matches(token, render));
            Assert.IsFalse(table.Matches(token, capture));
        }

        [TestMethod]
        public void TwoIdenticalNativePersonasKeepHidAndAudioOnTheirOwnPorts()
        {
            const string productId = "0CE6";
            ViiperPnPTopologyIdentity firstHid = ResolveViiperInterface(
                productId, "03", "FIRST_HID",
                @"USB\VID_054C&PID_0CE6\6&FIRST", 1);
            ViiperPnPTopologyIdentity firstRender = ResolveViiperInterface(
                productId, "01", "FIRST_RENDER",
                @"USB\VID_054C&PID_0CE6\6&FIRST", 1);
            ViiperPnPTopologyIdentity secondHid = ResolveViiperInterface(
                productId, "03", "SECOND_HID",
                @"USB\VID_054C&PID_0CE6\6&SECOND", 2);
            ViiperPnPTopologyIdentity secondCapture = ResolveViiperInterface(
                productId, "02", "SECOND_CAPTURE",
                @"USB\VID_054C&PID_0CE6\6&SECOND", 2);

            var table = new ViiperPnPOwnershipTable();
            int firstToken = table.AllocateToken();
            int secondToken = table.AllocateToken();
            Assert.IsTrue(table.Publish(firstToken, new ViiperPnPCorrelation(
                ViiperPnPTransport.NativeUdeCx, 0x200000001, 21,
                NativeSession,
                NativeRoot, string.Empty, 1)));
            Assert.IsTrue(table.Publish(secondToken, new ViiperPnPCorrelation(
                ViiperPnPTransport.NativeUdeCx, 0x200000002, 22,
                NativeSession,
                NativeRoot, string.Empty, 2)));

            Assert.IsTrue(table.Matches(firstToken, firstHid));
            Assert.IsTrue(table.Matches(firstToken, firstRender));
            Assert.IsFalse(table.Matches(firstToken, secondHid));
            Assert.IsFalse(table.Matches(firstToken, secondCapture));
            Assert.IsTrue(table.Matches(secondToken, secondHid));
            Assert.IsTrue(table.Matches(secondToken, secondCapture));
            Assert.IsFalse(table.Matches(secondToken, firstHid));
            Assert.IsFalse(table.Matches(secondToken, firstRender));
        }

        private static ViiperPnPTopologyIdentity ResolveViiperInterface(
            string productId, string interfaceNumber, string childSuffix,
            string usbDevice, int port)
        {
            ViiperPnPAncestryNode[] ancestry = BuildInterfaceAncestry(
                productId, interfaceNumber, childSuffix, usbDevice, port,
                @"USB\ROOT_HUB30\5&VIIPER_HUB", NativeRoot,
                new[] { @"ROOT\VIIPERUDE" });
            Assert.IsTrue(Global.TryClassifyViiperPnPAncestry(ancestry,
                out ViiperPnPTopologyIdentity topology));
            return topology;
        }

        private static ViiperPnPTopologyIdentity ResolvePhysicalInterface(
            string productId, string interfaceNumber, string childSuffix,
            string usbDevice, int port)
        {
            const string physicalRoot =
                @"PCI\VEN_1022&DEV_43F7\4&PHYSICAL_CONTROLLER";
            ViiperPnPAncestryNode[] ancestry = BuildInterfaceAncestry(
                productId, interfaceNumber, childSuffix, usbDevice, port,
                @"USB\ROOT_HUB30\5&PHYSICAL_HUB", physicalRoot,
                new[] { @"PCI\VEN_1022&DEV_43F7" });
            Assert.IsFalse(Global.TryClassifyViiperPnPAncestry(ancestry,
                out ViiperPnPTopologyIdentity topology));
            Assert.IsTrue(topology.IsUsbDeviceResolved);
            return topology;
        }

        private static ViiperPnPAncestryNode[] BuildInterfaceAncestry(
            string productId, string interfaceNumber, string childSuffix,
            string usbDevice, int port, string hub, string root,
            string[] rootHardwareIds)
        {
            string usbInterface =
                $@"USB\VID_054C&PID_{productId}&MI_{interfaceNumber}\7&{childSuffix}";
            bool hid = string.Equals(interfaceNumber, "03",
                StringComparison.Ordinal);
            string child = hid ?
                $@"HID\VID_054C&PID_{productId}&MI_03\8&{childSuffix}" :
                $@"SWD\MMDEVAPI\{{0.0.0.00000000}}.{childSuffix}";
            string[] interfaceHardwareIds = hid ?
                new[] { @"USB\Class_03&SubClass_00" } :
                new[] { @"USB\Class_01&SubClass_02" };

            return new[]
            {
                new ViiperPnPAncestryNode(child, usbInterface,
                    Array.Empty<string>(), string.Empty),
                new ViiperPnPAncestryNode(usbInterface, usbDevice,
                    interfaceHardwareIds, string.Empty),
                new ViiperPnPAncestryNode(usbDevice, hub,
                    new[] { $@"USB\VID_054C&PID_{productId}" },
                    $"Port_#{port:0000}.Hub_#0001"),
                new ViiperPnPAncestryNode(hub, root,
                    new[] { @"USB\ROOT_HUB30" }, string.Empty),
                new ViiperPnPAncestryNode(root, @"HTREE\ROOT\0",
                    rootHardwareIds, string.Empty),
            };
        }
    }
}
