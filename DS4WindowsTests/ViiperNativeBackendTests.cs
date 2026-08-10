using DS4Windows;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace DS4WindowsTests
{
    [TestClass]
    public class ViiperNativeBackendContractTests
    {
        private const string ExactNativePing = """
            {
              "server":"VIIPER",
              "version":"0.1.0",
              "transport":"native-ude",
              "ready":true,
              "nativeUde":{
                "abiMajor":1,
                "abiMinor":8,
                "capabilities":13,
                "expectedDriverPackageVersion":"0.1.0.0",
                "maxDevices":32,
                "maxDescriptorBytes":262144,
                "maxTransferBytes":1048576,
                "maxIsoPackets":1024,
                "maxPendingOperations":4096
              }
            }
            """;

        [TestMethod]
        public void ExactNativeHealthProofSelectsNativeUde()
        {
            ViiperBackendSelection selection =
                ViiperBackendContract.ParsePing(ExactNativePing,
                    "test-credential");

            Assert.AreEqual(ViiperBackendMode.NativeUde, selection.Mode);
            Assert.IsTrue(selection.IsNative);
            Assert.IsTrue(selection.UsesAuthentication);
        }

        [DataTestMethod]
        [DataRow("\"ready\":true", "\"ready\":false")]
        [DataRow("\"abiMajor\":1", "\"abiMajor\":2")]
        [DataRow("\"abiMinor\":8", "\"abiMinor\":7")]
        [DataRow("\"capabilities\":13", "\"capabilities\":15")]
        [DataRow("\"0.1.0.0\"", "\"0.1.0.1\"")]
        [DataRow("\"maxDevices\":32", "\"maxDevices\":31")]
        [DataRow("\"maxDescriptorBytes\":262144",
            "\"maxDescriptorBytes\":262143")]
        [DataRow("\"maxTransferBytes\":1048576",
            "\"maxTransferBytes\":1048575")]
        [DataRow("\"maxIsoPackets\":1024",
            "\"maxIsoPackets\":1023")]
        [DataRow("\"maxPendingOperations\":4096",
            "\"maxPendingOperations\":4095")]
        public void NativeHealthDriftFailsClosed(string current,
            string replacement)
        {
            string drifted = ExactNativePing.Replace(current, replacement,
                StringComparison.Ordinal);

            Assert.ThrowsException<ViiperNativeContractException>(() =>
                ViiperBackendContract.ParsePing(drifted));
        }

        [DataTestMethod]
        [DataRow("{\"server\":\"VIIPER\",\"version\":\"0.1.0\"}")]
        [DataRow("{\"server\":\"VIIPER\",\"version\":\"0.1.0\"," +
            "\"transport\":\"usbip\",\"ready\":true}")]
        public void HistoricalAndExplicitUsbipPingsSelectLegacy(string ping)
        {
            ViiperBackendSelection selection =
                ViiperBackendContract.ParsePing(ping);

            Assert.AreEqual(ViiperBackendMode.LegacyUsbip,
                selection.Mode);
            Assert.IsFalse(selection.IsNative);
        }

        [DataTestMethod]
        [DataRow("{\"server\":\"VIIPER\",\"version\":\"0.1.0\"," +
            "\"transport\":\"native-ude\",\"ready\":true}")]
        [DataRow("{\"server\":\"VIIPER\",\"version\":\"0.1.0\"," +
            "\"transport\":\"future\",\"ready\":true}")]
        [DataRow("{\"server\":\"VIIPER\",\"version\":\"0.1.0\"," +
            "\"nativeUde\":{}}")]
        public void IncompleteOrUnknownTransportNeverDowngradesToLegacy(
            string ping)
        {
            try
            {
                ViiperBackendContract.ParsePing(ping);
                Assert.Fail("An incomplete or unknown transport was accepted.");
            }
            catch (IOException)
            {
            }
        }

        [TestMethod]
        public void NativeReadinessDoesNotRequireUsbipState()
        {
            var status = new ViiperPrerequisiteStatus
            {
                ViiperInstalled = true,
                ViiperPackageCurrent = true,
                ServerRunning = true,
                ViiperProcessConflict = false,
                BackendMode = ViiperBackendMode.NativeUde,
                UsbipInstalled = false,
                UsbipExecutableSafe = false,
                UsbipDriverFilesSafe = false,
                UsbipRuntimeReady = false,
                CitrixUsbMonitorConflict = true,
            };

            Assert.IsTrue(status.Ready);
            Assert.AreEqual("VIIPER native UDE ready", status.DisplayText);

            status.BackendMode = ViiperBackendMode.LegacyUsbip;
            Assert.IsFalse(status.Ready);
        }

        [TestMethod]
        public void NativePolicySkipsEveryUsbipOnlyOperation()
        {
            string[] operations =
            {
                "detach", "find", "register", "unregister", "wait",
            };
            var invoked = new ConcurrentBag<string>();

            foreach (string operation in operations)
            {
                ViiperBackendPolicy.RunUsbipOnly(
                    ViiperBackendMode.NativeUde,
                    () => invoked.Add(operation));
            }
            Assert.AreEqual(0, invoked.Count);

            foreach (string operation in operations)
            {
                ViiperBackendPolicy.RunUsbipOnly(
                    ViiperBackendMode.LegacyUsbip,
                    () => invoked.Add(operation));
            }
            CollectionAssert.AreEquivalent(operations, invoked.ToArray());
        }
    }

    [TestClass]
    public class ViiperNativeLifetimeTests
    {
        [TestMethod]
        public void NativeReconnectAndFinalCleanupUseTransportNeutralIdentity()
        {
            int detach = 0;
            int unregister = 0;
            int stale = 0;
            int remove = 0;
            uint removedBus = 0;
            string removedDevice = null;
            var identity = new ViiperVirtualDeviceIdentity(51, "7");
            var lifetime = new ViiperVirtualDeviceLifetime(identity,
                ViiperBackendMode.NativeUde, -1,
                (bus, device) =>
                {
                    removedBus = bus;
                    removedDevice = device;
                    Interlocked.Increment(ref remove);
                },
                (_, _) => Interlocked.Increment(ref detach),
                _ => Interlocked.Increment(ref unregister),
                () => Interlocked.Increment(ref stale));

            var first = new ViiperDeviceStream(new MemoryStream(),
                new CountingDisposable(), lifetime);
            first.CloseTransport();

            Assert.IsFalse(lifetime.IsDisposed,
                "A TCP interruption must preserve the native device lifetime for reconnect.");
            Assert.AreEqual(0, remove);

            var replacement = new ViiperDeviceStream(new MemoryStream(),
                new CountingDisposable(), lifetime);
            Assert.AreEqual((uint)51, replacement.BusId);
            Assert.AreEqual("7", replacement.DevId);
            Assert.AreEqual(-1, replacement.UsbipPort);
            Assert.AreEqual(ViiperBackendMode.NativeUde,
                replacement.BackendMode);
            Assert.IsTrue(replacement.DeviceLifetime.Identity.Matches(51,
                "7"));

            replacement.Dispose();
            first.Dispose();

            Assert.AreEqual(1, remove,
                "All stream generations share one final API cleanup.");
            Assert.AreEqual((uint)51, removedBus);
            Assert.AreEqual("7", removedDevice);
            Assert.AreEqual(0, detach);
            Assert.AreEqual(0, unregister);
            Assert.AreEqual(0, stale);
        }

        private sealed class CountingDisposable : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    [TestClass]
    public class ViiperAuthenticatedTransportTests
    {
        [TestMethod]
        public void AuthenticationHandshakeAndEncryptedWriteMatchViiperV1()
        {
            const string password = "NativeCredential123";
            byte[] serverNonce = Enumerable.Range(0, 32)
                .Select(index => (byte)(index * 7 + 3)).ToArray();
            byte[] response = Encoding.ASCII.GetBytes("OK\0")
                .Concat(serverNonce).ToArray();
            var transport = new ScriptedDuplexStream(response);

            using Stream authenticated = ViiperAuthentication.Authenticate(
                transport, password);
            byte[] payload = Encoding.UTF8.GetBytes("ping\0");
            authenticated.Write(payload, 0, payload.Length);

            byte[] written = transport.Written;
            const int handshakeLength = 5 + 32 + 32;
            CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("eVI1\0"),
                written.Take(5).ToArray());
            byte[] clientNonce = written.Skip(5).Take(32).ToArray();
            byte[] clientProof = written.Skip(37).Take(32).ToArray();
            byte[] key = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password),
                Encoding.ASCII.GetBytes("VIIPER-Key-v1"), 100000,
                HashAlgorithmName.SHA256, 32);
            using (var hmac = new HMACSHA256(key))
            {
                byte[] proofInput = Encoding.ASCII.GetBytes(
                    "VIIPER-Auth-v1").Concat(clientNonce).ToArray();
                CollectionAssert.AreEqual(hmac.ComputeHash(proofInput),
                    clientProof);
            }

            byte[] frame = written.Skip(handshakeLength).ToArray();
            int packetLength = checked((int)
                BinaryPrimitives.ReadUInt32BigEndian(frame));
            Assert.AreEqual(frame.Length - sizeof(uint), packetLength);
            byte[] nonce = frame.Skip(4).Take(12).ToArray();
            CollectionAssert.AreEqual(new byte[12], nonce,
                "The first VIIPER encrypted write uses counter zero.");
            byte[] sessionInput = key.Concat(serverNonce)
                .Concat(clientNonce)
                .Concat(Encoding.ASCII.GetBytes("VIIPER-Session-v1"))
                .ToArray();
            byte[] sessionKey = SHA256.HashData(sessionInput);
            int ciphertextLength = packetLength - 12 - 16;
            byte[] plaintext = new byte[ciphertextLength];
            using (var cipher = new ChaCha20Poly1305(sessionKey))
            {
                cipher.Decrypt(nonce,
                    frame.AsSpan(4 + 12, ciphertextLength),
                    frame.AsSpan(4 + 12 + ciphertextLength, 16), plaintext);
            }
            CollectionAssert.AreEqual(payload, plaintext);
        }

        [TestMethod]
        public void EncryptedReadPreservesPartialStreamReads()
        {
            byte[] key = Enumerable.Range(0, 32)
                .Select(index => (byte)(index * 11 + 1)).ToArray();
            byte[] nonce = new byte[12];
            BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), 9);
            byte[] plaintext = Enumerable.Range(0, 257)
                .Select(index => (byte)(index * 17 + 5)).ToArray();
            byte[] ciphertext = new byte[plaintext.Length];
            byte[] tag = new byte[16];
            using (var cipher = new ChaCha20Poly1305(key))
            {
                cipher.Encrypt(nonce, plaintext, ciphertext, tag);
            }
            byte[] packet = nonce.Concat(ciphertext).Concat(tag).ToArray();
            byte[] framed = new byte[4 + packet.Length];
            BinaryPrimitives.WriteUInt32BigEndian(framed,
                (uint)packet.Length);
            Buffer.BlockCopy(packet, 0, framed, 4, packet.Length);
            using var inner = new MemoryStream(framed);
            using var authenticated = new ViiperAuthenticatedStream(inner,
                key);
            byte[] actual = new byte[plaintext.Length];
            int total = 0;
            while (total < actual.Length)
            {
                int read = authenticated.Read(actual, total,
                    Math.Min(13, actual.Length - total));
                Assert.IsTrue(read > 0);
                total += read;
            }

            CollectionAssert.AreEqual(plaintext, actual);
        }

        private sealed class ScriptedDuplexStream : Stream
        {
            private readonly MemoryStream readable;
            private readonly MemoryStream written = new MemoryStream();

            internal ScriptedDuplexStream(byte[] response)
            {
                readable = new MemoryStream(response, writable: false);
            }

            internal byte[] Written => written.ToArray();
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }
            public override void Flush()
            {
            }
            public override int Read(byte[] buffer, int offset, int count) =>
                readable.Read(buffer, offset, count);
            public override void Write(byte[] buffer, int offset, int count) =>
                written.Write(buffer, offset, count);
            public override long Seek(long offset, SeekOrigin origin) =>
                throw new NotSupportedException();
            public override void SetLength(long value) =>
                throw new NotSupportedException();
            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    readable.Dispose();
                    written.Dispose();
                }
                base.Dispose(disposing);
            }
        }
    }

    [TestClass]
    public class ViiperNativeChildSuppressionTests
    {
        [TestMethod]
        public void ExactRootViiperUdeAncestorIsResolvedForSonyChild()
        {
            const string hid = @"HID\VID_054C&PID_0CE6&MI_03\8&1";
            const string usb = @"USB\VID_054C&PID_0CE6\VIIPER";
            const string root = @"ROOT\VIIPER\UDE\0000";
            var parents = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase)
            {
                [hid] = usb,
                [usb] = root,
                [root] = @"HTREE\ROOT\0",
            };
            var hardwareIds = new Dictionary<string, string[]>(
                StringComparer.OrdinalIgnoreCase)
            {
                [hid] = new[] { @"HID\VID_054C&PID_0CE6" },
                [usb] = new[] { @"USB\VID_054C&PID_0CE6" },
                [root] = new[] { @"ROOT\VIIPER\UDE" },
            };

            bool native = Global.HasHardwareIdInAncestry(hid,
                @"ROOT\VIIPER\UDE",
                id => hardwareIds.GetValueOrDefault(id),
                id => parents.GetValueOrDefault(id));

            Assert.IsTrue(native);
            Assert.IsTrue(DS4Devices.ShouldSuppressViiperSonyInput(
                0x054C, 0x0CE6, nativeUdeAncestor: native,
                ownedLegacyUsbipPath: false));
        }

        [TestMethod]
        public void PhysicalSonyAndUnownedLegacyPathsRemainAccepted()
        {
            Assert.IsFalse(DS4Devices.ShouldSuppressViiperSonyInput(
                0x054C, 0x0CE6, nativeUdeAncestor: false,
                ownedLegacyUsbipPath: false));
            Assert.IsTrue(DS4Devices.ShouldSuppressViiperSonyInput(
                0x054C, 0x09CC, nativeUdeAncestor: false,
                ownedLegacyUsbipPath: true));
            Assert.IsFalse(DS4Devices.ShouldSuppressViiperSonyInput(
                0x1234, 0x0CE6, nativeUdeAncestor: true,
                ownedLegacyUsbipPath: false));
        }

        [TestMethod]
        public void SimilarOrCyclicAncestryDoesNotSpoofNativeRoot()
        {
            var parents = new Dictionary<string, string>
            {
                ["child"] = "root",
                ["root"] = "child",
            };
            var hardwareIds = new Dictionary<string, string[]>
            {
                ["child"] = new[] { @"HID\VID_054C&PID_0DF2" },
                ["root"] = new[] { @"ROOT\VIIPER\UDE_FAKE" },
            };

            Assert.IsFalse(Global.HasHardwareIdInAncestry("child",
                @"ROOT\VIIPER\UDE",
                id => hardwareIds.GetValueOrDefault(id),
                id => parents.GetValueOrDefault(id)));
        }
    }
}
