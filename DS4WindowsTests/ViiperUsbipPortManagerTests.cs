using DS4Windows;
using System.Reflection;
using System.Text.Json;

namespace DS4WindowsTests
{
    [TestClass]
    public class ViiperUsbipPortManagerTests
    {
        [DataTestMethod]
        [DataRow("localhost:3241/1-1", false)]
        [DataRow("127.0.0.1:3241/1-1", false)]
        [DataRow("[::1]:3241/1-1", false)]
        [DataRow("localhost:3241/x1-protected", false)]
        [DataRow("remote.example:3241/1-1", true)]
        [DataRow("localhost:3240/1-1", true)]
        public void UsbipInputAdmissionSeparatesLocalBrokerFromExternalSources(
            string location, bool expected)
        {
            Assert.AreEqual(expected, ViiperUsbipPortManager.CanUseUsbipPortAsInput(
                63, Query));
            bool Query(string[] arguments, out string output, out string error)
            {
                CollectionAssert.AreEqual(new[] { "port" }, arguments);
                output = $"Port 63: device in use\n -> usbip://{location}\n -> serial 'foreign'\n";
                error = string.Empty;
                return true;
            }
        }

        [DataTestMethod]
        [DataRow("")]
        [DataRow("Port 62: device in use\n -> usbip://remote.example:3241/1-1")]
        [DataRow("Port 63: device in use\n -> usbip://not a valid location")]
        [DataRow("Port 63: device in use\n -> usbip://remote.example:3241/1-1\nPort 63: device in use\n -> usbip://remote.example:3241/2-1")]
        public void UsbipOrphanOrAmbiguousInputWaitsForResolvedDiscovery(string listing)
        {
            Assert.IsFalse(ViiperUsbipPortManager.CanUseUsbipPortAsInput(63, Query));
            bool Query(string[] arguments, out string output, out string error)
            {
                output = listing;
                error = string.Empty;
                return true;
            }
        }

        [TestMethod]
        public void FailedUsbipInputQueryDoesNotAdmitAnOrphanAsPhysical()
        {
            Assert.IsFalse(ViiperUsbipPortManager.CanUseUsbipPortAsInput(63, FailingPortQuery));
        }

        [TestMethod]
        public void KnownOutputCannotBeReadmittedFromAChangedPortListing()
        {
            ViiperUsbipPortManager.RegisterActivePort(63, "1-1");
            try
            {
                Assert.IsFalse(ViiperUsbipPortManager.CanUseUsbipPortAsInput(63, Query));
            }
            finally { ViiperUsbipPortManager.UnregisterActivePort(63); }
            static bool Query(string[] arguments, out string output, out string error)
            {
                output = error = string.Empty;
                Assert.Fail("An active output must not need a fallible external query.");
                return false;
            }
        }

        [DataTestMethod]
        [DataRow(false, 0, 0, 10)]
        [DataRow(true, 0, 0, 1)]
        [DataRow(false, 1, 0, 1)]
        [DataRow(false, 0, 1, 1)]
        [DataRow(true, 1, 1, 1)]
        public void PadReplacementDoesNotRepeatProvenStartupRecovery(
            bool recoveryCompleted, int legacyPorts, int xboxOnePorts, int expected)
        {
            Assert.AreEqual(expected, ViiperUsbipPortManager.RequiredStalePortCleanSnapshots(
                recoveryCompleted, legacyPorts, xboxOnePorts));
        }

        [DataTestMethod]
        [DataRow(1)]
        [DataRow(10)]
        public void RecoveryWaitsOnlyForRequiredCleanSnapshots(int required)
        {
            int queries = 0, waits = 0;
            ViiperUsbipPortManager.ReconcileStaleLocalViiperPorts(required,
                new HashSet<int>(), () =>
                {
                    queries++;
                    return Array.Empty<ViiperUsbipPortManager.UsbipPortBlock>();
                }, _ => Assert.Fail("No stale port was observed."), milliseconds =>
                {
                    Assert.AreEqual(100, milliseconds);
                    waits++;
                });
            Assert.AreEqual(required, queries);
            Assert.AreEqual(required - 1, waits);
        }

        [DataTestMethod]
        [DataRow(1)]
        [DataRow(10)]
        public void RecoveryRechecksAfterDetachAndNeverDetachesProtectedImports(int required)
        {
            int queries = 0, waits = 0, detaches = 0;
            var active = new ViiperUsbipPortManager.UsbipPortBlock(2,
                " -> usbip://localhost:3241/1-2");
            var xbox = new ViiperUsbipPortManager.UsbipPortBlock(3,
                " -> usbip://localhost:3241/x1-0000000000000001");
            var foreign = new ViiperUsbipPortManager.UsbipPortBlock(4,
                " -> usbip://remote.example:3241/1-4");
            var stale = new ViiperUsbipPortManager.UsbipPortBlock(5,
                " -> usbip://localhost:3241/1-5");
            ViiperUsbipPortManager.ReconcileStaleLocalViiperPorts(required,
                new HashSet<int> { 2 }, () => ++queries == 1 ?
                    new[] { active, xbox, foreign, stale } : new[] { active, xbox, foreign },
                port => { Assert.AreEqual(5, port); detaches++; }, _ => waits++);
            Assert.AreEqual(1, detaches);
            Assert.AreEqual(required + 1, queries);
            Assert.AreEqual(required, waits);
        }

        [TestMethod]
        public void RecoveryCannotCertifyAStalePortThatNeverDisappears()
        {
            int queries = 0, waits = 0;
            var stale = new ViiperUsbipPortManager.UsbipPortBlock(5,
                " -> usbip://localhost:3241/1-5");
            Assert.ThrowsException<IOException>(() =>
                ViiperUsbipPortManager.ReconcileStaleLocalViiperPorts(1,
                    new HashSet<int>(), () => { queries++; return new[] { stale }; },
                    _ => { }, _ => waits++));
            Assert.AreEqual(32, queries);
            Assert.AreEqual(31, waits);
        }

        [TestMethod]
        public void RecoveryDoesNotTreatAFailedQueryAsAnEmptySnapshot()
        {
            Assert.ThrowsException<IOException>(() =>
                ViiperUsbipPortManager.ReconcileStaleLocalViiperPorts(1,
                    new HashSet<int>(), () => throw new IOException("Synthetic query failure"),
                    _ => Assert.Fail("No snapshot authorized a detach."),
                    _ => Assert.Fail("Query failure must propagate immediately.")));
        }

        [TestMethod]
        public void CanonicalUsbipInstallWinsOverPathCopy()
        {
            string canonical = Path.Combine(@"C:\Program Files", "USBip",
                "usbip.exe");
            string stalePathCopy = Path.Combine(@"C:\old-usbip", "usbip.exe");
            HashSet<string> existing = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase)
            {
                canonical,
                stalePathCopy,
            };

            string result = ViiperUsbipPortManager.FindUsbipPath(
                @"C:\Program Files", @"C:\Program Files",
                @"C:\Program Files (x86)", @"C:\old-usbip",
                existing.Contains);

            Assert.AreEqual(canonical, result);
        }

        [TestMethod]
        public void PathCopyIsUsedOnlyWhenCanonicalInstallIsMissing()
        {
            string pathCopy = Path.Combine(@"C:\portable-usbip", "usbip.exe");

            string result = ViiperUsbipPortManager.FindUsbipPath(
                @"C:\Program Files", @"C:\Program Files",
                @"C:\Program Files (x86)",
                @"C:\missing;""C:\portable-usbip""",
                candidate => string.Equals(candidate, pathCopy,
                    StringComparison.OrdinalIgnoreCase));

            Assert.AreEqual(pathCopy, result);
        }

        [TestMethod]
        public void PortQueryFailureIsNotReportedAsAnEmptySnapshot()
        {
            bool result = ViiperUsbipPortManager.TryGetImportedPorts(
                FailingPortQuery, out IReadOnlyList<
                    ViiperUsbipPortManager.UsbipPortBlock> ports,
                out string error);

            Assert.IsFalse(result);
            Assert.AreEqual(0, ports.Count);
            Assert.AreEqual("ABI mismatch, unexpected input size", error);
        }

        [TestMethod]
        public void SuccessfulEmptyPortListingRemainsAResolvedSnapshot()
        {
            bool result = ViiperUsbipPortManager.TryGetImportedPorts(
                EmptyPortQuery, out IReadOnlyList<
                    ViiperUsbipPortManager.UsbipPortBlock> ports,
                out string error);

            Assert.IsTrue(result);
            Assert.AreEqual(0, ports.Count);
            Assert.AreEqual(string.Empty, error);
        }

        [TestMethod]
        public void ParserPreservesEachPortAndOfficialOwnershipLine()
        {
            string listing = string.Join(Environment.NewLine, new[]
            {
                "Imported USB devices",
                "====================",
                "Port 01: device in use at High Speed(480Mbps)",
                "         Sony Corp. : DualSense wireless controller (054c:0ce6)",
                "           -> usbip://localhost:3241/7-3",
                "           -> remote bus/dev 007/003",
                "           -> serial 'DS4W0123456789A'",
                "Port 12: device in use at High Speed(480Mbps)",
                "         Sony Corp. : Wireless Controller (054c:09cc)",
                "           -> usbip://localhost:3241/8-4",
                "           -> remote bus/dev 008/004",
                "           -> serial 'DS4WIN1234567'",
            });

            IReadOnlyList<ViiperUsbipPortManager.UsbipPortBlock> ports =
                ViiperUsbipPortManager.ParseImportedPorts(listing);

            Assert.AreEqual(2, ports.Count);
            Assert.AreEqual(1, ports[0].Port);
            Assert.AreEqual(12, ports[1].Port);
            Assert.IsTrue(ViiperUsbipPortManager.
                IsDs4WindowsOwnedLocalPort(ports[0], "7-3"));
            Assert.IsFalse(ViiperUsbipPortManager.
                IsDs4WindowsOwnedLocalPort(ports[1], "8-4"));
        }

        [DataTestMethod]
        [DataRow("localhost")]
        [DataRow("127.0.0.1")]
        [DataRow("[::1]")]
        public void ExactLocalTupleAndDs4wTokenAreOwned(string host)
        {
            ViiperUsbipPortManager.UsbipPortBlock port = CreatePort(
                host, "1-1", "DS4W0123456789A");

            Assert.IsTrue(ViiperUsbipPortManager.
                IsDs4WindowsOwnedLocalPort(port, "1-1"));
            Assert.IsFalse(ViiperUsbipPortManager.
                IsDs4WindowsOwnedLocalPort(port, "1-10"));
        }

        [TestMethod]
        public void Pinned0977UntaggedPortsUseExactViiperEndpointOwnership()
        {
            ViiperUsbipPortManager.UsbipPortBlock untagged = CreatePort(
                "localhost", "1-1", null);
            ViiperUsbipPortManager.UsbipPortBlock nativeTransport = CreatePort(
                "localhost", "1-1", "DS4WIN1234567");
            ViiperUsbipPortManager.UsbipPortBlock remoteDs4w = CreatePort(
                "192.0.2.20", "1-1", "DS4W0123456789A");

            Assert.IsTrue(ViiperUsbipPortManager.
                IsDs4WindowsOwnedLocalPort(untagged, "1-1"));
            Assert.IsFalse(ViiperUsbipPortManager.
                IsDs4WindowsOwnedLocalPort(nativeTransport, "1-1"));
            Assert.IsFalse(ViiperUsbipPortManager.
                IsDs4WindowsOwnedLocalPort(remoteDs4w, "1-1"));
        }

        [DataTestMethod]
        [DataRow("DS4W0123456789A", true)]
        [DataRow("DS4Wabcdef12345", true)]
        [DataRow("ds4W0123456789A", false)]
        [DataRow("DS4W0123456789", false)]
        [DataRow("DS4W0123456789AB", false)]
        [DataRow("DS4W0123456789G", false)]
        [DataRow("PADS0123456789A", false)]
        public void OwnershipSerialMustMatchThe0978Contract(string serial,
            bool expected)
        {
            Assert.AreEqual(expected,
                ViiperUsbipPortManager.IsDs4WindowsOwnershipSerial(serial));
        }

        [DataTestMethod]
        [DataRow(1, "DS4W0123456789A", true)]
        [DataRow(255, "DS4WABCDEF01234", true)]
        [DataRow(0, "DS4W0123456789A", false)]
        [DataRow(-1, "DS4W0123456789A", false)]
        [DataRow(1, null, true)]
        [DataRow(1, "DS4WIN1234567", false)]
        public void CreateTrustRequiresPositiveNativePortAndDs4wToken(
            int port, string serial, bool expected)
        {
            Assert.AreEqual(expected,
                ViiperUsbipPortManager.IsTrustedCreateResponse(port, serial));
        }

        [TestMethod]
        public void DeviceCreateResponseUsesExactViiperUsbipFieldNames()
        {
            Type responseType = typeof(ViiperClient).GetNestedType(
                "ViiperDeviceResponse", BindingFlags.NonPublic);
            Assert.IsNotNull(responseType);

            PropertyInfo portProperty = responseType.GetProperty("UsbipPort");
            PropertyInfo serialProperty = responseType.GetProperty(
                "UsbipOwnerSerial");
            Assert.IsNotNull(portProperty);
            Assert.IsNotNull(serialProperty);

            object response = JsonSerializer.Deserialize(
                "{\"devId\":\"3\",\"usbipPort\":7," +
                "\"usbipOwnerSerial\":\"DS4W0123456789A\"}", responseType);
            Assert.AreEqual(7, portProperty.GetValue(response));
            Assert.AreEqual("DS4W0123456789A",
                serialProperty.GetValue(response));

            object legacyNames = JsonSerializer.Deserialize(
                "{\"devId\":\"3\",\"usbPort\":9," +
                "\"ownerSerial\":\"DS4WABCDEF01234\"}", responseType);
            Assert.AreEqual(0, portProperty.GetValue(legacyNames));
            Assert.IsNull(serialProperty.GetValue(legacyNames));
        }

        private static ViiperUsbipPortManager.UsbipPortBlock CreatePort(
            string host, string remoteBusId, string serial)
        {
            List<string> lines = new List<string>
            {
                "Port 01: device in use at High Speed(480Mbps)",
                "         Sony Corp. : DualSense wireless controller (054c:0ce6)",
                $"           -> usbip://{host}:3241/{remoteBusId}",
                "           -> remote bus/dev 001/001",
            };
            if (serial != null)
            {
                lines.Add($"           -> serial '{serial}'");
            }

            return new ViiperUsbipPortManager.UsbipPortBlock(1,
                string.Join(Environment.NewLine, lines));
        }

        private static bool FailingPortQuery(string[] arguments,
            out string output, out string error)
        {
            CollectionAssert.AreEqual(new[] { "port" }, arguments);
            output = string.Empty;
            error = " ABI mismatch, unexpected input size ";
            return false;
        }

        private static bool EmptyPortQuery(string[] arguments,
            out string output, out string error)
        {
            CollectionAssert.AreEqual(new[] { "port" }, arguments);
            output = "Imported USB devices\r\n====================\r\n";
            error = string.Empty;
            return true;
        }
    }
}
