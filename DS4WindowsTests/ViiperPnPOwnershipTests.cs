using DS4Windows;

namespace DS4WindowsTests
{
    [TestClass]
    public class ViiperPnPOwnershipTests
    {
        private const string NativeRoot = @"ROOT\VIIPERUDE\0000";
        private const ulong NativeSession = 0xA11CE;

        [TestMethod]
        public void NativeAncestryResolvesTheExactCompositeUsbParent()
        {
            ViiperPnPAncestryNode[] ancestry =
            {
                new(@"HID\VID_054C&PID_0CE6&MI_03\8&GAMEPAD",
                    @"USB\VID_054C&PID_0CE6&MI_03\7&INTERFACE",
                    new[] { @"HID_DEVICE_SYSTEM_GAME" }, string.Empty),
                new(@"USB\VID_054C&PID_0CE6&MI_03\7&INTERFACE",
                    @"USB\VID_054C&PID_0CE6\6&CONTROLLER_A",
                    new[] { @"USB\Class_03&SubClass_00" }, string.Empty),
                new(@"USB\VID_054C&PID_0CE6\6&CONTROLLER_A",
                    @"USB\ROOT_HUB30\5&HUB",
                    new[] { @"USB\VID_054C&PID_0CE6" },
                    "Port_#0002.Hub_#0001"),
                new(@"USB\ROOT_HUB30\5&HUB", NativeRoot,
                    new[] { @"USB\ROOT_HUB30" }, string.Empty),
                new(NativeRoot, @"HTREE\ROOT\0",
                    new[] { @"ROOT\VIIPERUDE" }, string.Empty),
            };

            Assert.IsTrue(Global.TryClassifyViiperPnPAncestry(ancestry,
                out ViiperPnPTopologyIdentity topology));
            Assert.AreEqual(ViiperPnPTransport.NativeUdeCx,
                topology.Transport);
            Assert.AreEqual(NativeRoot, topology.RootInstanceId);
            Assert.AreEqual(@"USB\VID_054C&PID_0CE6\6&CONTROLLER_A",
                topology.UsbDeviceInstanceId);
            Assert.AreEqual(2, topology.UsbPortNumber);
        }

        [TestMethod]
        public void SimilarRootNameIsNotClassifiedAsViiperNativeUde()
        {
            ViiperPnPAncestryNode[] ancestry =
            {
                new(@"HID\VID_054C&PID_0CE6&MI_03\8&GAMEPAD",
                    @"USB\VID_054C&PID_0CE6\6&CONTROLLER",
                    new[] { @"HID_DEVICE_SYSTEM_GAME" }, string.Empty),
                new(@"USB\VID_054C&PID_0CE6\6&CONTROLLER",
                    @"ROOT\VIIPERUDE_FAKE\0000",
                    new[] { @"USB\VID_054C&PID_0CE6" },
                    "Port_#0002.Hub_#0001"),
                new(@"ROOT\VIIPERUDE_FAKE\0000", @"HTREE\ROOT\0",
                    new[] { @"ROOT\UNRELATED" }, string.Empty),
            };

            Assert.IsFalse(Global.TryClassifyViiperPnPAncestry(ancestry,
                out ViiperPnPTopologyIdentity topology));
            Assert.AreEqual(ViiperPnPTransport.Unknown,
                topology.Transport);
        }

        [TestMethod]
        public void TwoNativeControllersAndPhysicalSonyRemainDisjoint()
        {
            var table = new ViiperPnPOwnershipTable();
            int firstToken = table.AllocateToken();
            int secondToken = table.AllocateToken();
            var first = new ViiperPnPCorrelation(
                ViiperPnPTransport.NativeUdeCx, 0x100000001, 11,
                NativeSession,
                NativeRoot,
                @"USB\VID_054C&PID_0CE6\6&CONTROLLER_A", 1);
            var second = new ViiperPnPCorrelation(
                ViiperPnPTransport.NativeUdeCx, 0x100000002, 12,
                NativeSession,
                NativeRoot,
                @"USB\VID_054C&PID_0CE6\6&CONTROLLER_B", 2);

            Assert.IsTrue(table.Publish(firstToken, first));
            Assert.IsTrue(table.Publish(secondToken, second));

            var firstHid = new ViiperPnPTopologyIdentity(
                ViiperPnPTransport.NativeUdeCx, NativeRoot,
                @"USB\VID_054C&PID_0CE6\6&CONTROLLER_A", 1);
            var secondAudio = new ViiperPnPTopologyIdentity(
                ViiperPnPTransport.NativeUdeCx, NativeRoot,
                @"USB\VID_054C&PID_0CE6\6&CONTROLLER_B", 2);
            var physicalSony = new ViiperPnPTopologyIdentity(
                ViiperPnPTransport.Unknown,
                @"PCI\VEN_1022&DEV_43F7\USB_CONTROLLER",
                @"USB\VID_054C&PID_0CE6\5&PHYSICAL", 4);

            Assert.IsTrue(table.Matches(firstToken, firstHid));
            Assert.IsFalse(table.Matches(firstToken, secondAudio));
            Assert.IsTrue(table.Matches(secondToken, secondAudio));
            Assert.IsFalse(table.Matches(secondToken, firstHid));
            Assert.IsFalse(table.Matches(firstToken, physicalSony));
            Assert.IsFalse(table.Matches(secondToken, physicalSony));
        }

        [TestMethod]
        public void OwnHidRejectionRequiresARegisteredExactIdentity()
        {
            var table = new ViiperPnPOwnershipTable();
            int token = table.AllocateToken();
            var ownHid = new ViiperPnPTopologyIdentity(
                ViiperPnPTransport.NativeUdeCx, NativeRoot,
                @"USB\VID_054C&PID_09CC\6&OWN_DS4", 3);

            Assert.IsFalse(table.Matches(-1, ownHid));
            Assert.IsFalse(table.Matches(0, ownHid));
            Assert.IsFalse(table.Publish(token, new ViiperPnPCorrelation(
                ViiperPnPTransport.NativeUdeCx, 0x100000003, 0,
                NativeSession,
                NativeRoot,
                @"USB\VID_054C&PID_09CC\6&OWN_DS4", 3)));
            Assert.IsFalse(table.Publish(token, new ViiperPnPCorrelation(
                ViiperPnPTransport.NativeUdeCx, 0x100000003, 21, 0,
                NativeRoot,
                @"USB\VID_054C&PID_09CC\6&OWN_DS4", 3)));
            Assert.IsFalse(table.Matches(token, ownHid));

            Assert.IsTrue(table.Publish(token, new ViiperPnPCorrelation(
                ViiperPnPTransport.NativeUdeCx, 0x100000003, 21,
                NativeSession,
                NativeRoot,
                @"USB\VID_054C&PID_09CC\6&OWN_DS4", 3)));
            Assert.IsTrue(table.Matches(token, ownHid));
            Assert.IsFalse(table.Matches(token,
                new ViiperPnPTopologyIdentity(
                    ViiperPnPTransport.NativeUdeCx, NativeRoot,
                    @"USB\VID_054C&PID_09CC\6&OWN_DS4", 4)));

            var samePersonaDifferentDevice = new ViiperPnPTopologyIdentity(
                ViiperPnPTransport.NativeUdeCx, NativeRoot,
                @"USB\VID_054C&PID_09CC\6&OTHER_DS4", 4);
            Assert.IsFalse(table.Matches(token, samePersonaDifferentDevice));
        }

        [TestMethod]
        public void EndpointReconnectAdvancesGenerationAndRetiresOldAnchor()
        {
            var table = new ViiperPnPOwnershipTable();
            int token = table.AllocateToken();
            var generationSeven = new ViiperPnPCorrelation(
                ViiperPnPTransport.NativeUdeCx, 0x200000001, 7,
                NativeSession,
                NativeRoot, string.Empty, 1);
            var generationEight = new ViiperPnPCorrelation(
                ViiperPnPTransport.NativeUdeCx, 0x200000001, 8,
                NativeSession,
                NativeRoot, string.Empty, 2);

            Assert.IsTrue(table.Publish(token, generationSeven));
            Assert.IsTrue(table.Publish(token, generationEight));
            Assert.IsFalse(table.Publish(token, generationSeven));

            Assert.IsFalse(table.Matches(token,
                new ViiperPnPTopologyIdentity(
                    ViiperPnPTransport.NativeUdeCx, NativeRoot,
                    @"USB\VID_054C&PID_0DF2\6&EDGE_OLD", 1)));
            Assert.IsTrue(table.Matches(token,
                new ViiperPnPTopologyIdentity(
                    ViiperPnPTransport.NativeUdeCx, NativeRoot,
                    @"USB\VID_054C&PID_0DF2\6&EDGE_NEW", 2)));
        }

        [TestMethod]
        public void SameGenerationCannotBeReboundToAnotherEndpoint()
        {
            var table = new ViiperPnPOwnershipTable();
            int token = table.AllocateToken();
            var original = new ViiperPnPCorrelation(
                ViiperPnPTransport.NativeUdeCx, 0x300000001, 4,
                NativeSession,
                NativeRoot,
                @"USB\VID_054C&PID_0CE6\6&ORIGINAL", 1);
            var conflicting = new ViiperPnPCorrelation(
                ViiperPnPTransport.NativeUdeCx, 0x300000001, 4,
                NativeSession,
                NativeRoot,
                @"USB\VID_054C&PID_0CE6\6&CONFLICT", 2);

            Assert.IsTrue(table.Publish(token, original));
            Assert.IsTrue(table.Publish(token, original));
            Assert.IsFalse(table.Publish(token, conflicting));
            Assert.IsTrue(table.Matches(token,
                new ViiperPnPTopologyIdentity(
                    ViiperPnPTransport.NativeUdeCx, NativeRoot,
                    original.UsbDeviceInstanceId, 1)));
        }

        [TestMethod]
        public void RegistryWithdrawsStaleOwnershipAfterConflictingRebind()
        {
            var source = new ViiperOutDevice(OutContType.ViiperDualSense,
                ViiperVirtualDeviceType.DualSense);
            var original = new ViiperPnPCorrelation(
                ViiperPnPTransport.NativeUdeCx, 0x310000001, 4,
                NativeSession,
                NativeRoot,
                @"USB\VID_054C&PID_0CE6\6&REGISTRY_ORIGINAL", 1);
            var conflicting = new ViiperPnPCorrelation(
                ViiperPnPTransport.NativeUdeCx, 0x310000001, 4,
                NativeSession,
                NativeRoot,
                @"USB\VID_054C&PID_0CE6\6&REGISTRY_CONFLICT", 2);

            try
            {
                int token = ViiperPnPOwnershipRegistry.AttachOrUpdate(source,
                    original);
                Assert.IsTrue(token > 0);
                Assert.AreEqual(-1,
                    ViiperPnPOwnershipRegistry.AttachOrUpdate(source,
                        conflicting));
                Assert.IsFalse(ViiperPnPOwnershipRegistry.Matches(token,
                    new ViiperPnPTopologyIdentity(
                        ViiperPnPTransport.NativeUdeCx, NativeRoot,
                        original.UsbDeviceInstanceId, 1)));
            }
            finally
            {
                ViiperPnPOwnershipRegistry.Detach(source);
            }
        }

        [TestMethod]
        public void ControllerSessionRestartRotatesTokenAndRetiresCollision()
        {
            var source = new ViiperOutDevice(OutContType.ViiperDualSense,
                ViiperVirtualDeviceType.DualSense);
            var firstSession = new ViiperPnPCorrelation(
                ViiperPnPTransport.NativeUdeCx, 0x320000001, 1,
                0x1111111111111111, NativeRoot, string.Empty, 2);
            var restartedSession = new ViiperPnPCorrelation(
                ViiperPnPTransport.NativeUdeCx, 0x320000001, 1,
                0x2222222222222222, NativeRoot, string.Empty, 2);
            var reusedTopology = new ViiperPnPTopologyIdentity(
                ViiperPnPTransport.NativeUdeCx, NativeRoot,
                @"USB\VID_054C&PID_0CE6\6&REUSED_AFTER_RESTART", 2);

            try
            {
                int firstToken = ViiperPnPOwnershipRegistry.AttachOrUpdate(
                    source, firstSession);
                int restartedToken = ViiperPnPOwnershipRegistry.
                    AttachOrUpdate(source, restartedSession);

                Assert.IsTrue(firstToken > 0);
                Assert.IsTrue(restartedToken > 0);
                Assert.AreNotEqual(firstToken, restartedToken);
                Assert.IsFalse(ViiperPnPOwnershipRegistry.Matches(
                    firstToken, reusedTopology));
                Assert.IsTrue(ViiperPnPOwnershipRegistry.Matches(
                    restartedToken, reusedTopology));
            }
            finally
            {
                ViiperPnPOwnershipRegistry.Detach(source);
            }
        }

        [TestMethod]
        public void ControllerSessionParticipatesInEqualityHashAndTokenFence()
        {
            var table = new ViiperPnPOwnershipTable();
            int token = table.AllocateToken();
            var firstSession = new ViiperPnPCorrelation(
                ViiperPnPTransport.NativeUdeCx, 0x325000001, 1,
                0x1111111122222222, NativeRoot, string.Empty, 2);
            var restartedSession = new ViiperPnPCorrelation(
                ViiperPnPTransport.NativeUdeCx, 0x325000001, 1,
                0x3333333344444444, NativeRoot, string.Empty, 2);

            Assert.IsTrue(firstSession.IsExact);
            Assert.IsTrue(restartedSession.IsExact);
            Assert.AreNotEqual(firstSession, restartedSession);
            Assert.AreNotEqual(firstSession.GetHashCode(),
                restartedSession.GetHashCode());
            Assert.IsTrue(table.Publish(token, firstSession));
            Assert.IsFalse(table.Publish(token, restartedSession));
            Assert.IsTrue(table.TryGet(token,
                out ViiperPnPCorrelation published));
            Assert.AreEqual(firstSession, published);
        }

        [TestMethod]
        public void NewNativeDeviceIdRotatesTokenWithinControllerSession()
        {
            var source = new ViiperOutDevice(OutContType.ViiperDualSenseEdge,
                ViiperVirtualDeviceType.DualSenseEdge);
            var firstDevice = new ViiperPnPCorrelation(
                ViiperPnPTransport.NativeUdeCx, 0x330000001, 9,
                NativeSession, NativeRoot, string.Empty, 1);
            var replacementDevice = new ViiperPnPCorrelation(
                ViiperPnPTransport.NativeUdeCx, 0x330000002, 1,
                NativeSession, NativeRoot, string.Empty, 3);

            try
            {
                int firstToken = ViiperPnPOwnershipRegistry.AttachOrUpdate(
                    source, firstDevice);
                int replacementToken = ViiperPnPOwnershipRegistry.
                    AttachOrUpdate(source, replacementDevice);

                Assert.IsTrue(firstToken > 0);
                Assert.IsTrue(replacementToken > 0);
                Assert.AreNotEqual(firstToken, replacementToken);
                Assert.IsFalse(ViiperPnPOwnershipRegistry.Matches(firstToken,
                    new ViiperPnPTopologyIdentity(
                        ViiperPnPTransport.NativeUdeCx, NativeRoot,
                        @"USB\VID_054C&PID_0DF2\6&OLD_DEVICE", 1)));
                Assert.IsTrue(ViiperPnPOwnershipRegistry.Matches(
                    replacementToken, new ViiperPnPTopologyIdentity(
                        ViiperPnPTransport.NativeUdeCx, NativeRoot,
                        @"USB\VID_054C&PID_0DF2\6&NEW_DEVICE", 3)));
            }
            finally
            {
                ViiperPnPOwnershipRegistry.Detach(source);
            }
        }

        [TestMethod]
        public void LegacyUsbIpRequiresExplicitOwnerSerialAndExactPort()
        {
            var table = new ViiperPnPOwnershipTable();
            int token = table.AllocateToken();
            const string legacyRoot = @"ROOT\USB\0001";

            Assert.IsFalse(table.Publish(token, new ViiperPnPCorrelation(
                ViiperPnPTransport.LegacyUsbIp, 0, 0, 0, legacyRoot,
                string.Empty, 5)));
            Assert.IsTrue(table.Publish(token, new ViiperPnPCorrelation(
                ViiperPnPTransport.LegacyUsbIp, 0, 0, 0, legacyRoot,
                string.Empty, 5, "VIIPER-ABBA-OWNER")));
            Assert.IsTrue(table.Matches(token,
                new ViiperPnPTopologyIdentity(
                    ViiperPnPTransport.LegacyUsbIp, legacyRoot,
                    @"USB\VID_054C&PID_0CE6\6&LEGACY", 5)));
            Assert.IsFalse(table.Matches(token,
                new ViiperPnPTopologyIdentity(
                    ViiperPnPTransport.LegacyUsbIp, legacyRoot,
                    @"USB\VID_054C&PID_0CE6\6&UNRELATED", 6)));
        }
    }
}
