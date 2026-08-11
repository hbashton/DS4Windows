using DS4Windows;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DS4WindowsTests
{
    [TestClass]
    public class ViiperNativeBackendContractTests
    {
        private static readonly string ExactNativePing = $$"""
            {
              "server":"VIIPER",
              "version":"0.1.0",
              "transport":"native-ude",
              "ready":true,
              "nativeUde":{
                "abiMajor":1,
                "abiMinor":10,
                "capabilities":13,
                "expectedDriverPackageVersion":"0.1.0.9",
                "loadedDriverBuildIdentity":"{{ViiperBackendContract.NativeLoadedDriverBuildIdentity}}",
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

        [TestMethod]
        public void PinnedLoadedDriverIdentityIsCanonicalLowercaseSha256()
        {
            Assert.AreEqual(
                "b24de107034ee4bbb366fb05b825eccb54819ff9f44c2f34d06e710e2fdc1ccb",
                ViiperBackendContract.NativeLoadedDriverBuildIdentity,
                "DS4Windows must pin the identity derived from final VIIPER HEAD 96612feff8b5e80f79d2c0c94aabaab31861a116.");
            Assert.IsTrue(ViiperBackendContract.IsCanonicalLowerHexSha256(
                ViiperBackendContract.NativeLoadedDriverBuildIdentity));
            Assert.AreEqual(64,
                ViiperBackendContract.NativeLoadedDriverBuildIdentity.Length);
            Assert.AreEqual(
                ViiperBackendContract.NativeLoadedDriverBuildIdentity,
                ViiperBackendContract.NativeLoadedDriverBuildIdentity.
                    ToLowerInvariant());
        }

        [TestMethod]
        public void UnauthenticatedNativeHealthFailsClosed()
        {
            StringAssert.Contains(
                Assert.ThrowsException<ViiperNativeContractException>(() =>
                    ViiperBackendContract.ParsePing(ExactNativePing)).Message,
                "authenticated");
        }

        [DataTestMethod]
        [DataRow("\"version\":\"0.1.0\"", "\"version\":\"0.1.1\"")]
        [DataRow("\"ready\":true", "\"ready\":false")]
        [DataRow("\"abiMajor\":1", "\"abiMajor\":2")]
        [DataRow("\"abiMinor\":10", "\"abiMinor\":9")]
        [DataRow("\"capabilities\":13", "\"capabilities\":15")]
        [DataRow("\"0.1.0.9\"", "\"0.1.0.8\"")]
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

        [TestMethod]
        public void MissingLoadedDriverIdentityFailsClosed()
        {
            string property =
                $"\"loadedDriverBuildIdentity\":\"{ViiperBackendContract.NativeLoadedDriverBuildIdentity}\",";
            string missing = ExactNativePing.Replace(property, string.Empty,
                StringComparison.Ordinal);

            Assert.AreNotEqual(ExactNativePing, missing,
                "The fixture did not remove the loaded-driver identity.");
            Assert.ThrowsException<ViiperNativeContractException>(() =>
                ViiperBackendContract.ParsePing(missing,
                    "test-credential"));
        }

        [TestMethod]
        public void MalformedUppercaseAndStaleLoadedDriverIdentitiesFailClosed()
        {
            string expected =
                ViiperBackendContract.NativeLoadedDriverBuildIdentity;
            string[] rejected =
            {
                string.Empty,
                "not-a-sha256-identity",
                expected.ToUpperInvariant(),
                $"{(expected[0] == '0' ? '1' : '0')}{expected[1..]}",
                $"g{expected[1..]}",
                expected[..^1],
                expected + "0",
                " " + expected,
            };

            foreach (string identity in rejected)
            {
                string drifted = ExactNativePing.Replace(expected, identity,
                    StringComparison.Ordinal);
                Assert.ThrowsException<ViiperNativeContractException>(() =>
                    ViiperBackendContract.ParsePing(drifted,
                        "test-credential"),
                    $"Rejected identity was accepted: {identity}");
            }
        }

        [DataTestMethod]
        [DataRow("null")]
        [DataRow("42")]
        [DataRow("{}")]
        [DataRow("[]")]
        public void NonStringLoadedDriverIdentityFailsAsNativeContract(
            string jsonValue)
        {
            string expected =
                ViiperBackendContract.NativeLoadedDriverBuildIdentity;
            string drifted = ExactNativePing.Replace(
                $"\"loadedDriverBuildIdentity\":\"{expected}\"",
                $"\"loadedDriverBuildIdentity\":{jsonValue}",
                StringComparison.Ordinal);

            Assert.ThrowsException<ViiperNativeContractException>(() =>
                ViiperBackendContract.ParsePing(drifted,
                    "test-credential"));
        }

        [TestMethod]
        public void MisCasedOrDuplicateLoadedDriverIdentityPropertyFailsClosed()
        {
            string expected =
                ViiperBackendContract.NativeLoadedDriverBuildIdentity;
            string exactProperty =
                $"\"loadedDriverBuildIdentity\":\"{expected}\"";
            string misCased = ExactNativePing.Replace(exactProperty,
                $"\"LoadedDriverBuildIdentity\":\"{expected}\"",
                StringComparison.Ordinal);
            string duplicate = ExactNativePing.Replace(exactProperty,
                $"{exactProperty},{exactProperty}",
                StringComparison.Ordinal);

            Assert.ThrowsException<ViiperNativeContractException>(() =>
                ViiperBackendContract.ParsePing(misCased,
                    "test-credential"));
            Assert.ThrowsException<ViiperNativeContractException>(() =>
                ViiperBackendContract.ParsePing(duplicate,
                    "test-credential"));
        }

        [TestMethod]
        public void StaleLoadedDriverIdentityCannotBecomeStartupReady()
        {
            string expected =
                ViiperBackendContract.NativeLoadedDriverBuildIdentity;
            string stale = ExactNativePing.Replace(expected,
                $"{(expected[0] == '0' ? '1' : '0')}{expected[1..]}",
                StringComparison.Ordinal);
            Exception failure = null;
            int discoveries = 0;

            bool ready = ViiperStartupDiscovery.
                TryPrepareBackendAndDiscover(
                    () => ViiperBackendContract.ParsePing(stale,
                        "test-credential"),
                    () => Assert.Fail(
                        "A stale native driver must not enter USB/IP preparation."),
                    () => discoveries++,
                    ex => failure = ex);

            Assert.IsFalse(ready);
            Assert.AreEqual(1, discoveries);
            Assert.IsInstanceOfType<ViiperNativeContractException>(failure);
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
        public void NativeReadinessDoesNotRequireLegacyPackageOrUsbipState()
        {
            var status = new ViiperPrerequisiteStatus
            {
                ViiperInstalled = false,
                ViiperPackageCurrent = false,
                ServerRunning = true,
                ViiperProcessConflict = true,
                BackendMode = ViiperBackendMode.NativeUde,
                NativeBrokerServiceRegistered = true,
                NativeBrokerServiceTrusted = true,
                UsbipInstalled = false,
                UsbipExecutableSafe = false,
                UsbipDriverFilesSafe = false,
                UsbipRuntimeReady = false,
                CitrixUsbMonitorConflict = true,
            };

            Assert.IsTrue(status.Ready);
            Assert.AreEqual("VIIPER native UDE ready", status.DisplayText);

            status.NativeBrokerServiceTrusted = false;
            Assert.IsFalse(status.Ready,
                "An authenticated endpoint cannot replace exact registered service identity.");
        }

        [TestMethod]
        public void UnhealthyRegisteredNativeServiceNeverUsesHealthyLegacyState()
        {
            var status = new ViiperPrerequisiteStatus
            {
                ViiperInstalled = true,
                ViiperPackageCurrent = true,
                ViiperProcessConflict = false,
                ViiperStartupTaskReady = true,
                ServerRunning = false,
                BackendMode = ViiperBackendMode.NativeUde,
                NativeBrokerServiceRegistered = true,
                NativeBrokerServiceTrusted = true,
                UsbipInstalled = true,
                UsbipExecutableSafe = true,
                UsbipDriverFilesSafe = true,
                UsbipRuntimeReady = true,
            };

            Assert.IsFalse(status.Ready);
            Assert.IsFalse(ViiperSetupManager.
                RequiresVerifiedViiperUpdate(status),
                "Native failure must not route repair through the legacy bundled backend.");
        }

        [TestMethod]
        public void LegacyReadinessPolicyRemainsUnchanged()
        {
            var status = new ViiperPrerequisiteStatus
            {
                ViiperInstalled = true,
                ViiperPackageCurrent = true,
                ViiperProcessConflict = false,
                ViiperStartupTaskReady = true,
                ServerRunning = true,
                BackendMode = ViiperBackendMode.LegacyUsbip,
                NativeBrokerServiceRegistered = true,
                NativeBrokerServiceTrusted = true,
                UsbipInstalled = true,
                UsbipExecutableSafe = true,
                UsbipDriverFilesSafe = true,
                UsbipRuntimeReady = true,
            };

            Assert.IsTrue(status.Ready);

            status.UsbipRuntimeReady = false;
            Assert.IsFalse(status.Ready);
        }

        [TestMethod]
        public void NativeServiceIdentityRequiresExactProtectedConfiguration()
        {
            const string executable =
                @"C:\Program Files\VIIPER\viiper.exe";
            const string credential =
                @"C:\ProgramData\VIIPER\viiper.key.txt";
            const string log =
                @"C:\ProgramData\VIIPER\viiper-native-broker.log";
            string imagePath =
                $"\"{executable}\" service --transport native-ude " +
                $"--key-file \"{credential}\" --log.file \"{log}\"";

            Assert.IsTrue(ViiperSetupManager.
                IsTrustedNativeBrokerServiceIdentity(imagePath,
                    "LocalSystem", 0x10, 2, 1,
                    "VIIPER Native UDE Broker", 0, executable,
                    credential, log, executablePathTrusted: true));
            Assert.IsFalse(ViiperSetupManager.
                IsTrustedNativeBrokerServiceIdentity(
                    imagePath.Replace("native-ude", "usbip",
                        StringComparison.Ordinal),
                    "LocalSystem", 0x10, 2, 1,
                    "VIIPER Native UDE Broker", 0, executable,
                    credential, log, executablePathTrusted: true));
            Assert.IsFalse(ViiperSetupManager.
                IsTrustedNativeBrokerServiceIdentity(imagePath,
                    "LocalService", 0x10, 2, 1,
                    "VIIPER Native UDE Broker", 0, executable,
                    credential, log, executablePathTrusted: true));
            Assert.IsFalse(ViiperSetupManager.
                IsTrustedNativeBrokerServiceIdentity(imagePath,
                    "LocalSystem", 0x10, 2, 1,
                    "VIIPER Native UDE Broker", 0, executable,
                    credential, log, executablePathTrusted: false));
        }

        [TestMethod]
        public void StartupRequiresTrustedRegisteredIdentityForNativeHealth()
        {
            var native = new ViiperBackendSelection(
                ViiperBackendMode.NativeUde, "test-credential",
                new ViiperNativeBrokerInstance(42, 123456));

            Assert.AreSame(native, ViiperSetupManager.
                ValidateStartupBackend(native,
                    nativeServiceRegistered: true,
                    nativeServiceTrusted: true));
            Assert.ThrowsException<ViiperNativeContractException>(() =>
                ViiperSetupManager.ValidateStartupBackend(native,
                    nativeServiceRegistered: false,
                    nativeServiceTrusted: false));
            Assert.ThrowsException<ViiperNativeContractException>(() =>
                ViiperSetupManager.ValidateStartupBackend(native,
                    nativeServiceRegistered: true,
                    nativeServiceTrusted: false));
        }

        [TestMethod]
        public void RegisteredNativeServiceRejectsLegacyStartupListener()
        {
            var legacy = new ViiperBackendSelection(
                ViiperBackendMode.LegacyUsbip);

            Assert.ThrowsException<ViiperNativeContractException>(() =>
                ViiperSetupManager.ValidateStartupBackend(legacy,
                    nativeServiceRegistered: true,
                    nativeServiceTrusted: true));
            Assert.AreSame(legacy, ViiperSetupManager.
                ValidateStartupBackend(legacy,
                    nativeServiceRegistered: false,
                    nativeServiceTrusted: false));
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

        [TestMethod]
        public void FailedBackendProbeStillRunsPhysicalDiscoveryAndCannotStart()
        {
            var events = new List<string>();

            bool ready = ViiperStartupDiscovery.
                TryPrepareBackendAndDiscover(
                    () =>
                    {
                        events.Add("probe");
                        throw new ViiperNativeContractException(
                            "native health proof failed");
                    },
                    () => events.Add("usbip"),
                    () => events.Add("discover"),
                    _ => events.Add("failure"));

            Assert.IsFalse(ready,
                "A failed native proof must not produce a running service.");
            CollectionAssert.AreEqual(new[]
            {
                "probe", "failure", "discover",
            }, events);
        }

        [TestMethod]
        public void LegacyBackendPreparationRemainsBeforePhysicalDiscovery()
        {
            var events = new List<string>();

            bool ready = ViiperStartupDiscovery.
                TryPrepareBackendAndDiscover(
                    () =>
                    {
                        events.Add("probe");
                        return new ViiperBackendSelection(
                            ViiperBackendMode.LegacyUsbip);
                    },
                    () => events.Add("usbip"),
                    () => events.Add("discover"));

            Assert.IsTrue(ready);
            CollectionAssert.AreEqual(new[]
            {
                "probe", "usbip", "discover",
            }, events);
        }

        [DataTestMethod]
        [DataRow(false, false, false, true, true)]
        [DataRow(true, false, false, false, true)]
        [DataRow(false, true, true, false, true)]
        [DataRow(false, true, false, false, false)]
        [DataRow(false, false, false, false, false)]
        public void RegisteredNativeServicePreventsLegacyFallback(
            bool nativeDetected, bool serverRunning, bool backendNative,
            bool serviceRegistered, bool expected)
        {
            var backend = new ViiperBackendSelection(backendNative
                ? ViiperBackendMode.NativeUde
                : ViiperBackendMode.LegacyUsbip);

            Assert.AreEqual(expected,
                ViiperSetupManager.IsNativeBackendAuthoritative(
                    nativeDetected, serverRunning, backend,
                    serviceRegistered));
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

        [TestMethod]
        public void NativeIdentityReplacementIsAtomicAndSkipsUsbipLifecycle()
        {
            int detach = 0;
            int unregister = 0;
            int stale = 0;
            int oldRemove = 0;
            int replacementRemove = 0;
            uint removedBus = 0;
            string removedDevice = null;
            var topology = new ViiperVirtualDeviceTopology(
                "dualshock4audioduplexv3", 0x05C4);
            var lifetime = new ViiperVirtualDeviceLifetime(
                new ViiperVirtualDeviceIdentity(12, "3"),
                ViiperBackendMode.NativeUde, -1,
                (_, _) => Interlocked.Increment(ref oldRemove),
                (_, _) => Interlocked.Increment(ref detach),
                _ => Interlocked.Increment(ref unregister),
                () => Interlocked.Increment(ref stale), topology);
            ViiperVirtualDeviceLifetime.State expected =
                lifetime.CaptureState();

            Assert.IsTrue(lifetime.TryReplaceNativeIdentity(expected,
                new ViiperVirtualDeviceIdentity(91, "17"),
                (bus, device) =>
                {
                    removedBus = bus;
                    removedDevice = device;
                    Interlocked.Increment(ref replacementRemove);
                }, null,
                out ViiperVirtualDeviceLifetime.State replacement));
            Assert.AreSame(replacement, lifetime.CaptureState());
            Assert.AreEqual((uint)91, lifetime.BusId);
            Assert.AreEqual("17", lifetime.DevId);
            Assert.AreEqual(1L, lifetime.Generation);
            Assert.AreEqual("dualshock4audioduplexv3",
                lifetime.Topology.DeviceName);
            Assert.AreEqual((ushort)0x05C4,
                lifetime.Topology.IdProduct);

            lifetime.Dispose();

            Assert.AreEqual(0, oldRemove,
                "A retired broker identity must not be cleaned after replacement.");
            Assert.AreEqual(1, replacementRemove);
            Assert.AreEqual((uint)91, removedBus);
            Assert.AreEqual("17", removedDevice);
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
            int writesAfterHandshake = transport.WriteCalls;
            byte[] payload = Encoding.UTF8.GetBytes("ping\0");
            authenticated.Write(payload, 0, payload.Length);

            Assert.AreEqual(writesAfterHandshake + 1,
                transport.WriteCalls,
                "One authenticated controller frame must use one underlying transport write.");

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
        public void ConcurrentEncryptedWritesRemainWholeViiperV1Records()
        {
            byte[] key = Enumerable.Range(0, 32)
                .Select(index => (byte)(index * 13 + 7)).ToArray();
            using var transport = new ScriptedDuplexStream(
                Array.Empty<byte>());
            using var authenticated = new ViiperAuthenticatedStream(
                transport, key);
            const int recordCount = 64;

            Parallel.For(0, recordCount, id =>
            {
                byte[] payload = new byte[sizeof(int)];
                BinaryPrimitives.WriteInt32BigEndian(payload, id);
                authenticated.Write(payload, 0, payload.Length);
            });

            Assert.AreEqual(recordCount, transport.WriteCalls,
                "Each controller frame must remain one underlying write under concurrency.");
            byte[] wire = transport.Written;
            var seen = new HashSet<int>();
            int offset = 0;
            for (ulong counter = 0; counter < recordCount; counter++)
            {
                Assert.IsTrue(wire.Length - offset >= sizeof(uint));
                int packetLength = checked((int)
                    BinaryPrimitives.ReadUInt32BigEndian(
                        wire.AsSpan(offset, sizeof(uint))));
                Assert.IsTrue(packetLength >= 12 + 16);
                Assert.IsTrue(wire.Length - offset >=
                    sizeof(uint) + packetLength);
                ReadOnlySpan<byte> packet = wire.AsSpan(
                    offset + sizeof(uint), packetLength);
                Assert.AreEqual(counter,
                    BinaryPrimitives.ReadUInt64BigEndian(
                        packet.Slice(4, sizeof(ulong))),
                    "Concurrent writes must serialize monotonically without nonce reuse.");
                int ciphertextLength = packetLength - 12 - 16;
                byte[] plaintext = new byte[ciphertextLength];
                using (var cipher = new ChaCha20Poly1305(key))
                {
                    cipher.Decrypt(packet.Slice(0, 12),
                        packet.Slice(12, ciphertextLength),
                        packet.Slice(12 + ciphertextLength, 16),
                        plaintext);
                }
                Assert.IsTrue(seen.Add(
                    BinaryPrimitives.ReadInt32BigEndian(plaintext)),
                    "A controller frame was duplicated.");
                offset += sizeof(uint) + packetLength;
            }

            Assert.AreEqual(recordCount, seen.Count);
            Assert.AreEqual(wire.Length, offset,
                "Authenticated records must not overlap or leave trailing bytes.");
        }

        [TestMethod]
        public void EncryptedWritesSupportEmptyAndChangingFrameSizes()
        {
            byte[] key = Enumerable.Range(0, 32)
                .Select(index => (byte)(index * 3 + 11)).ToArray();
            using var transport = new ScriptedDuplexStream(
                Array.Empty<byte>());
            using var authenticated = new ViiperAuthenticatedStream(
                transport, key);
            byte[][] payloads =
            {
                Enumerable.Range(0, 63).Select(index => (byte)index)
                    .ToArray(),
                Array.Empty<byte>(),
                Enumerable.Range(0, 547)
                    .Select(index => (byte)(index * 17)).ToArray(),
                new byte[] { 9, 8, 7 },
            };

            foreach (byte[] payload in payloads)
            {
                authenticated.Write(payload, 0, payload.Length);
            }

            Assert.AreEqual(payloads.Length, transport.WriteCalls);
            byte[] wire = transport.Written;
            int offset = 0;
            for (int counter = 0; counter < payloads.Length; counter++)
            {
                int packetLength = checked((int)
                    BinaryPrimitives.ReadUInt32BigEndian(
                        wire.AsSpan(offset, sizeof(uint))));
                ReadOnlySpan<byte> packet = wire.AsSpan(
                    offset + sizeof(uint), packetLength);
                Assert.AreEqual((ulong)counter,
                    BinaryPrimitives.ReadUInt64BigEndian(
                        packet.Slice(4, sizeof(ulong))));
                int ciphertextLength = packetLength - 12 - 16;
                Assert.AreEqual(payloads[counter].Length,
                    ciphertextLength);
                byte[] actual = new byte[ciphertextLength];
                using (var cipher = new ChaCha20Poly1305(key))
                {
                    cipher.Decrypt(packet.Slice(0, 12),
                        packet.Slice(12, ciphertextLength),
                        packet.Slice(12 + ciphertextLength, 16), actual);
                }
                CollectionAssert.AreEqual(payloads[counter], actual);
                offset += sizeof(uint) + packetLength;
            }
            Assert.AreEqual(wire.Length, offset);
        }

        [TestMethod]
        public void EncryptedWriteUsesFinalUniqueCounterThenExhausts()
        {
            byte[] key = Enumerable.Range(0, 32)
                .Select(index => (byte)(index * 5 + 17)).ToArray();
            using var transport = new ScriptedDuplexStream(
                Array.Empty<byte>());
            using var authenticated = new ViiperAuthenticatedStream(
                transport, key);
            SetPrivateField(authenticated, "sendCounter", ulong.MaxValue);
            byte[] payload = { 4, 3, 2, 1 };

            authenticated.Write(payload, 0, payload.Length);

            Assert.AreEqual(1, transport.WriteCalls);
            byte[] wire = transport.Written;
            Assert.AreEqual(ulong.MaxValue,
                BinaryPrimitives.ReadUInt64BigEndian(
                    wire.AsSpan(sizeof(uint) + 4, sizeof(ulong))),
                "The final 64-bit counter value is still a unique nonce.");
            StringAssert.Contains(
                Assert.ThrowsException<IOException>(() =>
                    authenticated.Write(payload, 0, payload.Length)).Message,
                "nonce space is exhausted");
            Assert.AreEqual(1, transport.WriteCalls,
                "Counter exhaustion must be detected before another transport write.");
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
            using var inner = new ChunkedReadStream(framed, 3);
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
            Assert.IsTrue(inner.ReadCalls > packet.Length / 3,
                "The test must exercise short reads in both the header and packet body.");
        }

        [TestMethod]
        public void ConcurrentEncryptedReadsRemainWholeAcrossShortReads()
        {
            byte[] key = Enumerable.Range(0, 32)
                .Select(index => (byte)(index * 7 + 19)).ToArray();
            const int recordCount = 64;
            byte[] framed = Enumerable.Range(0, recordCount)
                .SelectMany(id =>
                {
                    byte[] payload = new byte[sizeof(int)];
                    BinaryPrimitives.WriteInt32BigEndian(payload, id);
                    return BuildEncryptedPacket(key, payload, (ulong)id);
                }).ToArray();
            using var inner = new ChunkedReadStream(framed, 1);
            using var authenticated = new ViiperAuthenticatedStream(inner,
                key);
            using var start = new ManualResetEventSlim(false);
            var seen = new ConcurrentBag<int>();
            Task[] readers = Enumerable.Range(0, recordCount)
                .Select(_ => Task.Run(() =>
                {
                    start.Wait();
                    byte[] payload = new byte[sizeof(int)];
                    Assert.AreEqual(payload.Length,
                        authenticated.Read(payload, 0, payload.Length));
                    seen.Add(BinaryPrimitives.ReadInt32BigEndian(payload));
                })).ToArray();

            start.Set();
            Assert.IsTrue(Task.WaitAll(readers, TimeSpan.FromSeconds(10)),
                "Concurrent authenticated readers did not complete.");
            CollectionAssert.AreEquivalent(
                Enumerable.Range(0, recordCount).ToArray(),
                seen.ToArray());
            Assert.AreEqual(1, inner.MaximumConcurrentReads,
                "Authenticated reads must serialize record parsing and reusable-buffer access.");
        }

        [TestMethod]
        public void EncryptedReadSkipsEmptyRecordWithoutFalseEof()
        {
            byte[] key = Enumerable.Range(0, 32)
                .Select(index => (byte)(index * 5 + 1)).ToArray();
            byte[] expected = Encoding.UTF8.GetBytes("next-frame");
            byte[] framed = BuildEncryptedPacket(key, Array.Empty<byte>(), 0)
                .Concat(BuildEncryptedPacket(key, expected, 1)).ToArray();
            using var inner = new MemoryStream(framed);
            using var authenticated = new ViiperAuthenticatedStream(inner,
                key);
            byte[] actual = new byte[expected.Length];

            Assert.AreEqual(expected.Length,
                authenticated.Read(actual, 0, actual.Length),
                "An authenticated empty frame is not stream EOF.");
            CollectionAssert.AreEqual(expected, actual);
        }

        [TestMethod]
        public void AuthenticationFailureClearsReusedReceiveBuffers()
        {
            byte[] key = Enumerable.Range(0, 32)
                .Select(index => (byte)(index * 9 + 5)).ToArray();
            byte[] first = Enumerable.Range(0, 128)
                .Select(index => (byte)(index * 3 + 1)).ToArray();
            byte[] second = Enumerable.Range(0, 31)
                .Select(index => (byte)(index * 11 + 7)).ToArray();
            byte[] rejected = Enumerable.Range(0, 64)
                .Select(index => (byte)(index * 13 + 9)).ToArray();
            byte[] tampered = BuildEncryptedPacket(key, rejected, 2);
            tampered[^1] ^= 0x80;
            byte[] framed = BuildEncryptedPacket(key, first, 0)
                .Concat(BuildEncryptedPacket(key, second, 1))
                .Concat(tampered).ToArray();
            using var inner = new ChunkedReadStream(framed, 2);
            using var authenticated = new ViiperAuthenticatedStream(inner,
                key);

            byte[] actualFirst = new byte[first.Length];
            Assert.AreEqual(first.Length,
                authenticated.Read(actualFirst, 0, actualFirst.Length));
            CollectionAssert.AreEqual(first, actualFirst);
            byte[] packetBuffer = GetPrivateBuffer(authenticated,
                "receivePacket");
            byte[] plaintextBuffer = GetPrivateBuffer(authenticated,
                "receivePlaintext");

            byte[] actualSecond = new byte[second.Length];
            Assert.AreEqual(second.Length,
                authenticated.Read(actualSecond, 0, actualSecond.Length));
            CollectionAssert.AreEqual(second, actualSecond);
            Assert.AreSame(packetBuffer, GetPrivateBuffer(authenticated,
                "receivePacket"));
            Assert.AreSame(plaintextBuffer, GetPrivateBuffer(authenticated,
                "receivePlaintext"));

            plaintextBuffer.AsSpan(0, rejected.Length).Fill(0xA5);
            byte[] destination = Enumerable.Repeat((byte)0xCC,
                rejected.Length).ToArray();
            IOException failure = Assert.ThrowsException<IOException>(() =>
                authenticated.Read(destination, 0, destination.Length));

            StringAssert.Contains(failure.Message, "authentication failed");
            Assert.IsInstanceOfType<CryptographicException>(
                failure.InnerException);
            Assert.IsTrue(plaintextBuffer.All(value => value == 0),
                "Rejected plaintext must be cleared before the authentication error escapes.");
            Assert.IsTrue(packetBuffer.All(value => value == 0),
                "The reusable ciphertext/tag buffer must be cleared after every decrypt attempt.");
            Assert.IsTrue(destination.All(value => value == 0xCC),
                "Unauthenticated plaintext must never reach the caller buffer.");
        }

        [TestMethod]
        public void EncryptedWriteFailurePermanentlyFencesTheStream()
        {
            byte[] key = Enumerable.Range(0, 32)
                .Select(index => (byte)(index + 17)).ToArray();
            using var transport = new FailingWriteStream();
            using var authenticated = new ViiperAuthenticatedStream(
                transport, key);
            byte[] payload = { 1, 2, 3, 4 };

            StringAssert.Contains(
                Assert.ThrowsException<IOException>(() =>
                    authenticated.Write(payload, 0, payload.Length)).Message,
                "injected");
            StringAssert.Contains(
                Assert.ThrowsException<IOException>(() =>
                    authenticated.Write(payload, 0, payload.Length)).Message,
                "unusable");
            Assert.AreEqual(1, transport.WriteCalls,
                "A failed authenticated record must never be followed by another record on the same stream.");
            Assert.IsTrue(transport.IsDisposed,
                "The potentially truncated transport must close immediately.");
            Assert.IsTrue(GetPrivateBuffer(authenticated, "sendPacket")
                    .All(value => value == 0),
                "A record retained after a failed write must be cleared.");
        }

        [TestMethod]
        public void DisposeUnblocksAndJoinsPendingEncryptedIo()
        {
            byte[] key = Enumerable.Range(0, 32)
                .Select(index => (byte)(index + 23)).ToArray();
            var transport = new DisposeAwareBlockingStream();
            var authenticated = new ViiperAuthenticatedStream(transport,
                key);
            Task read = Task.Run(() =>
                Assert.ThrowsException<ObjectDisposedException>(() =>
                    authenticated.Read(new byte[1], 0, 1)));
            Task write = Task.Run(() =>
                Assert.ThrowsException<ObjectDisposedException>(() =>
                    authenticated.Write(new byte[] { 1, 2, 3 }, 0, 3)));
            Assert.IsTrue(transport.ReadStarted.Wait(
                TimeSpan.FromSeconds(2)));
            Assert.IsTrue(transport.WriteStarted.Wait(
                TimeSpan.FromSeconds(2)));
            byte[] pendingSend = GetPrivateBuffer(authenticated,
                "sendPacket");

            Task dispose = Task.Run(authenticated.Dispose);
            Assert.IsTrue(Task.WaitAll(new[] { read, write, dispose },
                TimeSpan.FromSeconds(5)),
                "Dispose deadlocked with pending authenticated I/O.");
            Assert.IsTrue(transport.IsDisposed);
            Assert.IsTrue(pendingSend.All(value => value == 0),
                "Dispose must clear the reusable send record after joining the write lane.");
            Assert.ThrowsException<ObjectDisposedException>(() =>
                authenticated.Read(new byte[1], 0, 1));
            Assert.ThrowsException<ObjectDisposedException>(() =>
                authenticated.Write(new byte[1], 0, 1));
        }

        [DataTestMethod]
        [DataRow(0)]
        [DataRow(1)]
        [DataRow(27)]
        [DataRow(2097153)]
        public void EncryptedReadRejectsOutOfBoundsPacketLength(int length)
        {
            byte[] key = Enumerable.Range(0, 32)
                .Select(index => (byte)(index + 29)).ToArray();
            byte[] header = new byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32BigEndian(header,
                checked((uint)length));
            using var inner = new MemoryStream(header);
            using var authenticated = new ViiperAuthenticatedStream(inner,
                key);

            StringAssert.Contains(
                Assert.ThrowsException<IOException>(() =>
                    authenticated.Read(new byte[1], 0, 1)).Message,
                "invalid encrypted packet length");
        }

        [TestMethod]
        public void EncryptedWriteHonorsExactMaximumPacketLength()
        {
            byte[] key = Enumerable.Range(0, 32)
                .Select(index => (byte)(index + 31)).ToArray();
            using var transport = new ScriptedDuplexStream(
                Array.Empty<byte>());
            using var authenticated = new ViiperAuthenticatedStream(
                transport, key);
            const int maximumPacketLength = 2 * 1024 * 1024;
            const int maximumPlaintextLength = maximumPacketLength - 12 - 16;
            byte[] maximum = Enumerable.Range(0, maximumPlaintextLength)
                .Select(index => (byte)(index * 17 + 3)).ToArray();

            authenticated.Write(maximum, 0, maximum.Length);

            Assert.AreEqual(1, transport.WriteCalls);
            byte[] wire = transport.Written;
            Assert.AreEqual(maximumPacketLength,
                checked((int)BinaryPrimitives.ReadUInt32BigEndian(wire)));
            Assert.AreEqual(sizeof(uint) + maximumPacketLength, wire.Length);
            byte[] oversized = new byte[maximumPlaintextLength + 1];

            StringAssert.Contains(
                Assert.ThrowsException<IOException>(() =>
                    authenticated.Write(oversized, 0,
                        oversized.Length)).Message,
                "too large");
            Assert.AreEqual(1, transport.WriteCalls,
                "An oversized record must be rejected before transport I/O.");
        }

        [TestMethod]
        public void EncryptedReadAcceptsExactMaximumPacketLength()
        {
            byte[] key = Enumerable.Range(0, 32)
                .Select(index => (byte)(index * 7 + 31)).ToArray();
            const int maximumPlaintextLength =
                2 * 1024 * 1024 - 12 - 16;
            byte[] expected = Enumerable.Range(0, maximumPlaintextLength)
                .Select(index => (byte)(index * 19 + 1)).ToArray();
            byte[] framed = BuildEncryptedPacket(key, expected);
            using var inner = new ChunkedReadStream(framed, 8191);
            using var authenticated = new ViiperAuthenticatedStream(inner,
                key);
            byte[] actual = new byte[expected.Length];

            Assert.AreEqual(actual.Length,
                authenticated.Read(actual, 0, actual.Length));
            CollectionAssert.AreEqual(expected, actual);
        }

        [TestMethod]
        public void EncryptedReadReturnsZeroAtCleanPacketBoundaryEof()
        {
            byte[] key = Enumerable.Range(0, 32)
                .Select(index => (byte)(index * 5 + 9)).ToArray();
            byte[] plaintext = Encoding.UTF8.GetBytes(
                "{\"server\":\"VIIPER\"}\n");
            byte[] framed = BuildEncryptedPacket(key, plaintext);
            using var inner = new MemoryStream(framed);
            using var authenticated = new ViiperAuthenticatedStream(inner,
                key);
            byte[] actual = new byte[plaintext.Length];

            Assert.AreEqual(plaintext.Length,
                authenticated.Read(actual, 0, actual.Length));
            Assert.AreEqual(0, authenticated.Read(new byte[1], 0, 1),
                "VIIPER closes each one-shot API connection after its complete encrypted response packet.");
            CollectionAssert.AreEqual(plaintext, actual);
        }

        [DataTestMethod]
        [DataRow(1)]
        [DataRow(2)]
        [DataRow(3)]
        public void EncryptedReadRejectsPartialPacketHeaderEof(int byteCount)
        {
            byte[] key = Enumerable.Range(0, 32)
                .Select(index => (byte)(index + 1)).ToArray();
            using var inner = new MemoryStream(
                new byte[] { 0, 0, 0, 28 }.Take(byteCount).ToArray());
            using var authenticated = new ViiperAuthenticatedStream(inner,
                key);

            StringAssert.Contains(
                Assert.ThrowsException<IOException>(() =>
                    authenticated.Read(new byte[1], 0, 1)).Message,
                "packet header");
        }

        [TestMethod]
        public void EncryptedReadRejectsPartialPacketBodyEof()
        {
            byte[] key = Enumerable.Range(0, 32)
                .Select(index => (byte)(index + 3)).ToArray();
            byte[] truncated = new byte[4 + 27];
            BinaryPrimitives.WriteUInt32BigEndian(truncated, 28);
            using var inner = new MemoryStream(truncated);
            using var authenticated = new ViiperAuthenticatedStream(inner,
                key);

            StringAssert.Contains(
                Assert.ThrowsException<IOException>(() =>
                    authenticated.Read(new byte[1], 0, 1)).Message,
                "closed early");
        }

        [TestMethod]
        public void AuthenticatedNativePingCreateListAndRemoveUseCloseDelimitedResponses()
        {
            const string password = "NativeCredential123";
            using var server = new AuthenticatedViiperProtocolServer(password);
            var client = new ViiperClient("127.0.0.1", server.Port,
                () => new[] { password }, () => server.BrokerInstance);
            ViiperDeviceStream device = null;

            try
            {
                device = client.CreateDeviceAndOpenStream("test-device");
                Assert.AreEqual(ViiperBackendMode.NativeUde,
                    device.BackendMode);
                Assert.AreEqual((uint)42, device.BusId);
                Assert.AreEqual("7", device.DevId);
                Assert.IsTrue(client.GetMicrophoneInterfaceActive(
                    device.BusId, device.DevId));
            }
            finally
            {
                device?.Dispose();
            }

            string[] requests = server.WaitForAuthenticatedRequests(8,
                TimeSpan.FromSeconds(5));
            CollectionAssert.AreEquivalent(new[]
            {
                "ping",
                "ping",
                "bus/create 0",
                "bus/42/add {\"type\":\"test-device\"}",
                "bus/42/7",
                "bus/42/list",
                "bus/42/remove 7",
                "bus/remove 42",
            }, requests);
            server.WaitForIdle(TimeSpan.FromSeconds(5));
            server.AssertNoErrors();
        }

        [TestMethod]
        public void AuthenticatedStaleLoadedDriverIdentityCreatesNoDevice()
        {
            const string password = "NativeCredential123";
            using var server = new AuthenticatedViiperProtocolServer(password);
            string expected =
                ViiperBackendContract.NativeLoadedDriverBuildIdentity;
            server.PingResponse = server.PingResponseForTest.Replace(expected,
                $"{(expected[0] == '0' ? '1' : '0')}{expected[1..]}",
                StringComparison.Ordinal);
            var client = new ViiperClient("127.0.0.1", server.Port,
                () => new[] { password }, () => server.BrokerInstance);

            Assert.ThrowsException<ViiperNativeContractException>(() =>
                client.CreateDeviceAndOpenStream("test-device"));
            Assert.AreEqual(0, server.BusCreateCount,
                "A stale loaded kernel identity must fail before any bus/device operation.");

            server.WaitForIdle(TimeSpan.FromSeconds(5));
            server.AssertNoErrors();
        }

        [TestMethod]
        public void BrokerProcessChangeDuringHealthProofFailsBeforeCreation()
        {
            const string password = "NativeCredential123";
            using var server = new AuthenticatedViiperProtocolServer(password);
            int instanceReads = 0;
            var client = new ViiperClient("127.0.0.1", server.Port,
                () => new[] { password }, () =>
                {
                    int read = Interlocked.Increment(ref instanceReads);
                    return read == 1
                        ? new ViiperNativeBrokerInstance(1001, 100001)
                        : new ViiperNativeBrokerInstance(1002, 100002);
                });

            StringAssert.Contains(
                Assert.ThrowsException<ViiperNativeContractException>(() =>
                    client.CreateDeviceAndOpenStream("test-device")).Message,
                "changed");
            Assert.AreEqual(0, server.BusCreateCount,
                "A health proof crossing a broker process boundary cannot authorize any topology mutation.");
            server.WaitForIdle(TimeSpan.FromSeconds(5));
            server.AssertNoErrors();
        }

        [TestMethod]
        public void SameBrokerStreamReconnectReusesExactNativeTopology()
        {
            const string password = "NativeCredential123";
            using var server = new AuthenticatedViiperProtocolServer(password);
            var client = new ViiperClient("127.0.0.1", server.Port,
                () => new[] { password }, () => server.BrokerInstance);
            ViiperDeviceStream initial = null;
            ViiperDeviceStream replacement = null;

            try
            {
                initial = client.CreateDeviceAndOpenStream(
                    "dualshock4audioduplexv3", 0x05C4);
                initial.CloseTransport();

                replacement = client.RecoverDeviceStream(initial);

                Assert.AreEqual(initial.BusId, replacement.BusId);
                Assert.AreEqual(initial.DevId, replacement.DevId);
                Assert.AreEqual(0L,
                    replacement.DeviceLifetimeGeneration);
                Assert.AreEqual(1, server.BusCreateCount,
                    "VIIPER keeps the disconnected native child alive for its reconnect grace; recovery must not publish a duplicate bus.");
            }
            finally
            {
                replacement?.Dispose();
                initial?.Dispose();
            }

            server.WaitForIdle(TimeSpan.FromSeconds(5));
            server.AssertNoErrors();
        }

        [TestMethod]
        public async Task ConcurrentGraceReconnectsNeverCreateAnotherTopology()
        {
            const string password = "NativeCredential123";
            using var server = new AuthenticatedViiperProtocolServer(password);
            var client = new ViiperClient("127.0.0.1", server.Port,
                () => new[] { password }, () => server.BrokerInstance);
            ViiperDeviceStream initial = client.CreateDeviceAndOpenStream(
                "dualsensecombinedaudioduplexv5");
            initial.CloseTransport();
            ViiperDeviceStream[] recovered = null;

            try
            {
                recovered = await Task.WhenAll(
                    Task.Run(() => client.RecoverDeviceStream(initial)),
                    Task.Run(() => client.RecoverDeviceStream(initial)));

                Assert.IsTrue(recovered.All(stream =>
                    stream.BusId == initial.BusId &&
                    stream.DevId == initial.DevId &&
                    stream.DeviceLifetimeGeneration == 0));
                Assert.AreEqual(1, server.BusCreateCount,
                    "Concurrent failures inside VIIPER's reconnect grace must reuse the one live native child.");
            }
            finally
            {
                if (recovered != null)
                {
                    foreach (ViiperDeviceStream stream in recovered)
                    {
                        stream.CloseTransport();
                    }
                    recovered[0].Dispose();
                }
                initial.Dispose();
            }

            server.WaitForIdle(TimeSpan.FromSeconds(5));
            server.AssertNoErrors();
        }

        [TestMethod]
        public void AmbiguousNativeTopologyProbeFailsClosedWithoutCreation()
        {
            const string password = "NativeCredential123";
            using var server = new AuthenticatedViiperProtocolServer(password);
            var client = new ViiperClient("127.0.0.1", server.Port,
                () => new[] { password }, () => server.BrokerInstance);
            ViiperDeviceStream initial = client.CreateDeviceAndOpenStream(
                "dualshock4audioduplexv3", 0x05C4);
            initial.CloseTransport();
            server.ReplaceDeviceTopology("dualshock4audioduplexv3",
                0x09CC);

            try
            {
                StringAssert.Contains(
                    Assert.ThrowsException<IOException>(() =>
                        client.RecoverDeviceStream(initial)).Message,
                    "unexpected product ID");
                Assert.AreEqual(1, server.BusCreateCount,
                    "An occupied but mismatched identity is ambiguous and must never authorize another native bus.");
            }
            finally
            {
                initial.Dispose();
            }

            server.WaitForIdle(TimeSpan.FromSeconds(5));
            server.AssertNoErrors();
        }

        [TestMethod]
        public void NativeTopologyProbeServerErrorFailsClosedWithoutCreation()
        {
            const string password = "NativeCredential123";
            using var server = new AuthenticatedViiperProtocolServer(password);
            var client = new ViiperClient("127.0.0.1", server.Port,
                () => new[] { password }, () => server.BrokerInstance);
            ViiperDeviceStream initial = client.CreateDeviceAndOpenStream(
                "test-device");
            initial.CloseTransport();
            server.FailOneBusList();

            try
            {
                StringAssert.Contains(
                    Assert.ThrowsException<IOException>(() =>
                        client.RecoverDeviceStream(initial)).Message,
                    "503");
                Assert.AreEqual(1, server.BusCreateCount,
                    "A failed identity probe is not proof that the old native child is absent.");
            }
            finally
            {
                initial.Dispose();
            }

            server.WaitForIdle(TimeSpan.FromSeconds(5));
            server.AssertNoErrors();
        }

        [TestMethod]
        public void BrokerDeathRecreatesExactNativeTopologyAndLifetimeIdentity()
        {
            const string password = "NativeCredential123";
            using var server = new AuthenticatedViiperProtocolServer(password);
            var client = new ViiperClient("127.0.0.1", server.Port,
                () => new[] { password }, () => server.BrokerInstance);
            ViiperDeviceStream initial = null;
            ViiperDeviceStream replacement = null;

            try
            {
                initial = client.CreateDeviceAndOpenStream(
                    "dualshock4audioduplexv3", 0x05C4);
                ViiperVirtualDeviceLifetime lifetime =
                    initial.DeviceLifetime;
                initial.CloseTransport();
                server.SimulateBrokerRestart();

                replacement = client.RecoverDeviceStream(initial);

                Assert.AreEqual((uint)42, initial.BusId,
                    "The retired stream keeps the identity of the route it opened.");
                Assert.AreEqual("7", initial.DevId);
                Assert.AreEqual((uint)42, replacement.BusId,
                    "A real broker restart reuses its first free bus ID.");
                Assert.AreEqual("7", replacement.DevId);
                Assert.AreEqual((uint)42, lifetime.BusId);
                Assert.AreEqual("7", lifetime.DevId);
                Assert.AreEqual(1L, lifetime.Generation);
                Assert.AreEqual(0L, initial.DeviceLifetimeGeneration);
                Assert.AreEqual(1L, replacement.DeviceLifetimeGeneration);
                Assert.AreEqual("dualshock4audioduplexv3",
                    lifetime.Topology.DeviceName);
                Assert.AreEqual((ushort)0x05C4,
                    lifetime.Topology.IdProduct);

                string[] requests = server.WaitForAuthenticatedRequests(10,
                    TimeSpan.FromSeconds(5));
                Assert.AreEqual(4, requests.Count(request =>
                    string.Equals(request, "ping",
                        StringComparison.Ordinal)),
                    "Initial creation and recovery each pin two authenticated proofs inside one stable broker-process interval.");
                Assert.AreEqual(2, server.BusCreateCount);
                CollectionAssert.Contains(requests,
                    "bus/42/add {\"type\":\"dualshock4audioduplexv3\",\"idProduct\":1476}");
                CollectionAssert.Contains(requests, "bus/42/7");
            }
            finally
            {
                replacement?.Dispose();
                initial?.Dispose();
            }

            server.WaitForIdle(TimeSpan.FromSeconds(5));
            server.AssertNoErrors();
        }

        [TestMethod]
        public void BrokerRestartIdReuseNeverMergesTwoControllerLifetimes()
        {
            const string password = "NativeCredential123";
            using var server = new AuthenticatedViiperProtocolServer(password);
            var firstClient = new ViiperClient("127.0.0.1", server.Port,
                () => new[] { password }, () => server.BrokerInstance);
            var secondClient = new ViiperClient("127.0.0.1", server.Port,
                () => new[] { password }, () => server.BrokerInstance);
            ViiperDeviceStream first = firstClient.CreateDeviceAndOpenStream(
                "dualshock4audioduplexv3", 0x05C4);
            ViiperDeviceStream second = secondClient.CreateDeviceAndOpenStream(
                "dualshock4audioduplexv3", 0x05C4);
            first.CloseTransport();
            second.CloseTransport();
            server.SimulateBrokerRestart();
            ViiperDeviceStream recoveredSecond = null;
            ViiperDeviceStream recoveredFirst = null;

            try
            {
                // Recovering old bus 43 first creates new bus 42/dev 7,
                // exactly colliding with the first lifetime's old numeric
                // identity and topology.
                recoveredSecond = secondClient.RecoverDeviceStream(second);
                Assert.AreEqual((uint)42, recoveredSecond.BusId);
                Assert.AreEqual("7", recoveredSecond.DevId);

                recoveredFirst = firstClient.RecoverDeviceStream(first);

                Assert.AreEqual((uint)43, recoveredFirst.BusId,
                    "A matching numeric identity from a different broker process belongs to the already-recovered second controller and must not be reopened.");
                Assert.AreEqual("8", recoveredFirst.DevId);
                Assert.AreNotSame(recoveredSecond.DeviceLifetime,
                    recoveredFirst.DeviceLifetime);
                Assert.AreEqual(4, server.BusCreateCount,
                    "Two initial and two post-restart controller topologies are required; neither recovery may merge into the other.");
            }
            finally
            {
                recoveredFirst?.Dispose();
                recoveredSecond?.Dispose();
                first.Dispose();
                second.Dispose();
            }

            server.WaitForIdle(TimeSpan.FromSeconds(5));
            server.AssertNoErrors();
        }

        [TestMethod]
        public void StaleLifetimeCleanupCannotDeleteReusedBrokerIdentity()
        {
            const string password = "NativeCredential123";
            using var server = new AuthenticatedViiperProtocolServer(password);
            var staleClient = new ViiperClient("127.0.0.1", server.Port,
                () => new[] { password }, () => server.BrokerInstance);
            var liveClient = new ViiperClient("127.0.0.1", server.Port,
                () => new[] { password }, () => server.BrokerInstance);
            ViiperDeviceStream stale = staleClient.CreateDeviceAndOpenStream(
                "dualshock4audioduplexv3", 0x05C4);
            ViiperDeviceStream live = liveClient.CreateDeviceAndOpenStream(
                "dualshock4audioduplexv3", 0x05C4);
            stale.CloseTransport();
            live.CloseTransport();
            server.SimulateBrokerRestart();
            ViiperDeviceStream recoveredLive =
                liveClient.RecoverDeviceStream(live);

            try
            {
                Assert.IsTrue(server.HasTopology(42, "7"));
                stale.Dispose();
                Assert.IsTrue(server.HasTopology(42, "7"),
                    "Disposing old bus 42/dev 7 after restart must not remove the new controller that reused that identity.");
                Assert.IsFalse(server.Requests.Contains(
                    "bus/42/remove 7"));
            }
            finally
            {
                recoveredLive.Dispose();
                live.Dispose();
            }

            server.WaitForIdle(TimeSpan.FromSeconds(5));
            server.AssertNoErrors();
        }

        [TestMethod]
        public void NativeRecoveryRejectsHealthyUsbipTransportWithoutCreation()
        {
            const string password = "NativeCredential123";
            using var server = new AuthenticatedViiperProtocolServer(password);
            var client = new ViiperClient("127.0.0.1", server.Port,
                () => new[] { password }, () => server.BrokerInstance);
            ViiperDeviceStream initial = null;

            try
            {
                initial = client.CreateDeviceAndOpenStream("test-device");
                initial.CloseTransport();
                server.PingResponse =
                    AuthenticatedViiperProtocolServer.LegacyPing;

                StringAssert.Contains(
                    Assert.ThrowsException<IOException>(() =>
                        client.RecoverDeviceStream(initial)).Message,
                    "authenticated native UDE health");
                Assert.AreEqual(1, server.BusCreateCount,
                    "A USB/IP health response must not recreate a native lifetime.");
                Assert.AreEqual((uint)42,
                    initial.DeviceLifetime.BusId);
                Assert.AreEqual(0L,
                    initial.DeviceLifetime.Generation);
            }
            finally
            {
                initial?.Dispose();
            }

            server.WaitForIdle(TimeSpan.FromSeconds(5));
            server.AssertNoErrors();
        }

        [TestMethod]
        public async Task ConcurrentNativeFailuresCreateOneReplacementTopology()
        {
            const string password = "NativeCredential123";
            using var server = new AuthenticatedViiperProtocolServer(password);
            var client = new ViiperClient("127.0.0.1", server.Port,
                () => new[] { password }, () => server.BrokerInstance);
            ViiperDeviceStream initial = client.CreateDeviceAndOpenStream(
                "dualsensecombinedaudioduplexv5");
            initial.CloseTransport();
            server.SimulateBrokerRestart();
            ViiperDeviceStream[] recovered = null;

            try
            {
                Task<ViiperDeviceStream> writerFailure = Task.Run(() =>
                    client.RecoverDeviceStream(initial));
                Task<ViiperDeviceStream> feedbackFailure = Task.Run(() =>
                    client.RecoverDeviceStream(initial));
                recovered = await Task.WhenAll(writerFailure,
                    feedbackFailure);

                Assert.AreEqual(2, server.BusCreateCount,
                    "Only initial creation plus one native recovery topology are allowed.");
                Assert.AreEqual(1L,
                    initial.DeviceLifetime.Generation);
                Assert.IsTrue(recovered.All(stream =>
                    stream.BusId == 42 && stream.DevId == "7" &&
                    stream.DeviceLifetimeGeneration == 1));
            }
            finally
            {
                if (recovered != null)
                {
                    foreach (ViiperDeviceStream stream in recovered)
                    {
                        stream.CloseTransport();
                    }
                    recovered[0].Dispose();
                }
                initial.Dispose();
            }

            server.WaitForIdle(TimeSpan.FromSeconds(5));
            server.AssertNoErrors();
        }

        [TestMethod]
        public void RetiredStreamAfterSecondBrokerRestartRecreatesCurrentLifetime()
        {
            const string password = "NativeCredential123";
            using var server = new AuthenticatedViiperProtocolServer(password);
            var client = new ViiperClient("127.0.0.1", server.Port,
                () => new[] { password }, () => server.BrokerInstance);
            ViiperDeviceStream initial = client.CreateDeviceAndOpenStream(
                "dualsensecombinedaudioduplexv5");
            initial.CloseTransport();
            server.SimulateBrokerRestart();
            ViiperDeviceStream firstRecovery =
                client.RecoverDeviceStream(initial);
            firstRecovery.CloseTransport();
            server.SimulateBrokerRestart();
            ViiperDeviceStream secondRecovery = null;

            try
            {
                // A delayed writer can still report the original generation
                // after the generation-1 feedback recovery was published.
                secondRecovery = client.RecoverDeviceStream(initial);

                Assert.AreEqual(2L,
                    initial.DeviceLifetime.Generation);
                Assert.AreEqual(2L,
                    secondRecovery.DeviceLifetimeGeneration);
                Assert.AreEqual(3, server.BusCreateCount,
                    "The retired generation must not bypass recreation after the replacement broker also restarted.");
            }
            finally
            {
                secondRecovery?.Dispose();
                firstRecovery.Dispose();
                initial.Dispose();
            }

            server.WaitForIdle(TimeSpan.FromSeconds(5));
            server.AssertNoErrors();
        }

        [TestMethod]
        public void FailedNativeRecreateRollsBackPartialBus()
        {
            const string password = "NativeCredential123";
            using var server = new AuthenticatedViiperProtocolServer(password);
            var client = new ViiperClient("127.0.0.1", server.Port,
                () => new[] { password }, () => server.BrokerInstance);
            ViiperDeviceStream initial = client.CreateDeviceAndOpenStream(
                "dualshock4audioduplexv3", 0x05C4);
            initial.CloseTransport();
            server.SimulateBrokerRestart();
            server.FailOneDeviceAdd();

            try
            {
                Assert.ThrowsException<IOException>(() =>
                    client.RecoverDeviceStream(initial));
                Assert.AreEqual((uint)42,
                    initial.DeviceLifetime.BusId);
                Assert.AreEqual("7", initial.DeviceLifetime.DevId);
                Assert.AreEqual(0L,
                    initial.DeviceLifetime.Generation);
                Assert.IsTrue(SpinWait.SpinUntil(() =>
                    server.Requests.Contains("bus/remove 42"),
                    TimeSpan.FromSeconds(5)),
                    "The partial replacement bus was not removed.");
                Assert.IsFalse(server.Requests.Any(request =>
                    request.StartsWith("bus/42/remove ",
                        StringComparison.Ordinal)),
                    "A failed add did not create a device to remove.");
            }
            finally
            {
                initial.Dispose();
            }

            server.WaitForIdle(TimeSpan.FromSeconds(5));
            server.AssertNoErrors();
        }

        [TestMethod]
        public async Task DisposalRaceRejectsCommitAndCleansRecreatedDevice()
        {
            const string password = "NativeCredential123";
            using var server = new AuthenticatedViiperProtocolServer(password);
            var client = new ViiperClient("127.0.0.1", server.Port,
                () => new[] { password }, () => server.BrokerInstance);
            ViiperDeviceStream initial = client.CreateDeviceAndOpenStream(
                "dualshock4audioduplexv3", 0x05C4);
            initial.CloseTransport();
            server.SimulateBrokerRestart();
            server.BlockOneDeviceAdd();
            Task<ViiperDeviceStream> recovery = Task.Run(() =>
                client.RecoverDeviceStream(initial));

            server.WaitForBlockedDeviceAdd(TimeSpan.FromSeconds(5));
            initial.DeviceLifetime.Dispose();
            server.ReleaseBlockedDeviceAdd();

            await Assert.ThrowsExceptionAsync<ObjectDisposedException>(
                async () => await recovery);
            Assert.IsTrue(initial.DeviceLifetime.IsDisposed);
            Assert.AreEqual(0L, initial.DeviceLifetime.Generation,
                "A disposed lifetime must never publish the replacement identity.");
            Assert.IsTrue(SpinWait.SpinUntil(() =>
                server.Requests.Contains("bus/42/remove 7") &&
                server.Requests.Contains("bus/remove 42"),
                TimeSpan.FromSeconds(5)),
                "The recreated device and bus were not rolled back after disposal won the race.");

            initial.Dispose();
            server.WaitForIdle(TimeSpan.FromSeconds(5));
            server.AssertNoErrors();
        }

        private static byte[] BuildEncryptedPacket(byte[] key,
            byte[] plaintext, ulong counter = 0)
        {
            byte[] nonce = new byte[12];
            BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4),
                counter);
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
            return framed;
        }

        private static byte[] GetPrivateBuffer(
            ViiperAuthenticatedStream stream, string name) =>
            (byte[])GetPrivateField(name).GetValue(stream);

        private static void SetPrivateField(ViiperAuthenticatedStream stream,
            string name, object value) =>
            GetPrivateField(name).SetValue(stream, value);

        private static FieldInfo GetPrivateField(string name) =>
            typeof(ViiperAuthenticatedStream).GetField(name,
                BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException(
                $"Missing ViiperAuthenticatedStream field '{name}'.");

        private sealed class ChunkedReadStream : Stream
        {
            private readonly MemoryStream readable;
            private readonly int maximumReadSize;
            private int activeReads;
            private int maximumConcurrentReads;
            private int readCalls;

            internal ChunkedReadStream(byte[] bytes, int maximumReadSize)
            {
                if (maximumReadSize <= 0)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(maximumReadSize));
                }
                readable = new MemoryStream(bytes, writable: false);
                this.maximumReadSize = maximumReadSize;
            }

            internal int MaximumConcurrentReads =>
                Volatile.Read(ref maximumConcurrentReads);

            internal int ReadCalls => Volatile.Read(ref readCalls);

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                int concurrent = Interlocked.Increment(ref activeReads);
                UpdateMaximum(ref maximumConcurrentReads, concurrent);
                Interlocked.Increment(ref readCalls);
                try
                {
                    Thread.Yield();
                    return readable.Read(buffer, offset,
                        Math.Min(count, maximumReadSize));
                }
                finally
                {
                    Interlocked.Decrement(ref activeReads);
                }
            }

            private static void UpdateMaximum(ref int target, int value)
            {
                int current;
                while (value > (current = Volatile.Read(ref target)) &&
                    Interlocked.CompareExchange(ref target, value,
                        current) != current)
                {
                }
            }

            public override void Write(byte[] buffer, int offset,
                int count) => throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) =>
                throw new NotSupportedException();

            public override void SetLength(long value) =>
                throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    readable.Dispose();
                }
                base.Dispose(disposing);
            }
        }

        private sealed class FailingWriteStream : Stream
        {
            private int disposed;

            internal bool IsDisposed => Volatile.Read(ref disposed) != 0;
            internal int WriteCalls { get; private set; }
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => !IsDisposed;
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
                throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count)
            {
                WriteCalls++;
                throw new IOException("injected partial transport write");
            }
            public override long Seek(long offset, SeekOrigin origin) =>
                throw new NotSupportedException();
            public override void SetLength(long value) =>
                throw new NotSupportedException();
            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    Interlocked.Exchange(ref disposed, 1);
                }
                base.Dispose(disposing);
            }
        }

        private sealed class DisposeAwareBlockingStream : Stream
        {
            private readonly ManualResetEventSlim release = new(false);
            private int disposed;

            internal ManualResetEventSlim ReadStarted { get; } = new(false);
            internal ManualResetEventSlim WriteStarted { get; } = new(false);
            internal bool IsDisposed => Volatile.Read(ref disposed) != 0;
            public override bool CanRead => !IsDisposed;
            public override bool CanSeek => false;
            public override bool CanWrite => !IsDisposed;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }
            public override void Flush()
            {
            }
            public override int Read(byte[] buffer, int offset, int count)
            {
                ReadStarted.Set();
                release.Wait();
                throw new ObjectDisposedException(
                    nameof(DisposeAwareBlockingStream));
            }
            public override void Write(byte[] buffer, int offset, int count)
            {
                WriteStarted.Set();
                release.Wait();
                throw new ObjectDisposedException(
                    nameof(DisposeAwareBlockingStream));
            }
            public override long Seek(long offset, SeekOrigin origin) =>
                throw new NotSupportedException();
            public override void SetLength(long value) =>
                throw new NotSupportedException();
            protected override void Dispose(bool disposing)
            {
                if (disposing && Interlocked.Exchange(ref disposed, 1) == 0)
                {
                    release.Set();
                }
                base.Dispose(disposing);
            }
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
            internal int WriteCalls { get; private set; }
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
            public override void Write(byte[] buffer, int offset, int count)
            {
                WriteCalls++;
                written.Write(buffer, offset, count);
            }
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

        private sealed class AuthenticatedViiperProtocolServer : IDisposable
        {
            private static readonly string NativePing =
                "{\"server\":\"VIIPER\",\"version\":\"0.1.0\",\"transport\":\"native-ude\",\"ready\":true,\"nativeUde\":{\"abiMajor\":1,\"abiMinor\":10,\"capabilities\":13,\"expectedDriverPackageVersion\":\"0.1.0.9\",\"loadedDriverBuildIdentity\":\"" +
                ViiperBackendContract.NativeLoadedDriverBuildIdentity +
                "\",\"maxDevices\":32,\"maxDescriptorBytes\":262144,\"maxTransferBytes\":1048576,\"maxIsoPackets\":1024,\"maxPendingOperations\":4096}}";
            internal const string LegacyPing =
                "{\"server\":\"VIIPER\",\"version\":\"0.1.0\",\"transport\":\"usbip\",\"ready\":true}";
            private readonly TcpListener listener;
            private readonly CancellationTokenSource shutdown = new();
            private readonly ConcurrentDictionary<int, TcpClient> clients =
                new();
            private readonly ConcurrentQueue<string> requests = new();
            private readonly ConcurrentQueue<Exception> errors = new();
            private readonly Task acceptLoop;
            private readonly byte[] key;
            private readonly ConcurrentDictionary<uint, byte> buses =
                new();
            private readonly ConcurrentDictionary<uint, TestDevice> devices =
                new();
            private readonly ManualResetEventSlim blockedAddEntered =
                new(false);
            private readonly ManualResetEventSlim releaseBlockedAdd =
                new(true);
            private int nextClientId;
            private int activeHandlers;
            private int nextBusId = 41;
            private int nextDeviceId = 6;
            private int brokerEpoch = 1;
            private int failNextAdd;
            private int blockNextAdd;
            private int failNextBusList;
            private string pingResponse = NativePing;
            private volatile bool disposed;

            internal AuthenticatedViiperProtocolServer(string password)
            {
                key = Rfc2898DeriveBytes.Pbkdf2(
                    Encoding.UTF8.GetBytes(password),
                    Encoding.ASCII.GetBytes("VIIPER-Key-v1"), 100000,
                    HashAlgorithmName.SHA256, 32);
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
                acceptLoop = AcceptLoopAsync();
            }

            internal int Port { get; }

            internal ViiperNativeBrokerInstance BrokerInstance
            {
                get
                {
                    int epoch = Volatile.Read(ref brokerEpoch);
                    return new ViiperNativeBrokerInstance(
                        checked((uint)(1000 + epoch)),
                        checked(100000L + epoch));
                }
            }

            internal string PingResponse
            {
                set => Volatile.Write(ref pingResponse, value);
            }

            internal string PingResponseForTest =>
                Volatile.Read(ref pingResponse);

            internal string[] Requests => requests.ToArray();

            internal int BusCreateCount => Requests.Count(request =>
                string.Equals(request, "bus/create 0",
                    StringComparison.Ordinal));

            internal bool HasTopology(uint busId, string devId) =>
                devices.TryGetValue(busId, out TestDevice device) &&
                string.Equals(device.DevId, devId,
                    StringComparison.Ordinal);

            internal void SimulateBrokerRestart()
            {
                Interlocked.Increment(ref brokerEpoch);
                devices.Clear();
                buses.Clear();
                Interlocked.Exchange(ref nextBusId, 41);
                Interlocked.Exchange(ref nextDeviceId, 6);
            }

            internal void ReplaceDeviceTopology(string type,
                ushort? idProduct)
            {
                foreach (KeyValuePair<uint, TestDevice> entry in devices)
                {
                    devices[entry.Key] = new TestDevice(entry.Value.DevId,
                        type, idProduct);
                }
            }

            internal void FailOneBusList()
            {
                Interlocked.Exchange(ref failNextBusList, 1);
            }

            internal void FailOneDeviceAdd()
            {
                Interlocked.Exchange(ref failNextAdd, 1);
            }

            internal void BlockOneDeviceAdd()
            {
                blockedAddEntered.Reset();
                releaseBlockedAdd.Reset();
                Interlocked.Exchange(ref blockNextAdd, 1);
            }

            internal void WaitForBlockedDeviceAdd(TimeSpan timeout)
            {
                Assert.IsTrue(blockedAddEntered.Wait(timeout),
                    "Timed out waiting for the recreated device add request.");
            }

            internal void ReleaseBlockedDeviceAdd() =>
                releaseBlockedAdd.Set();

            internal string[] WaitForAuthenticatedRequests(int count,
                TimeSpan timeout)
            {
                bool completed = SpinWait.SpinUntil(
                    () => requests.Count >= count || !errors.IsEmpty,
                    timeout);
                Assert.IsTrue(completed,
                    $"Timed out waiting for {count} authenticated VIIPER requests; received {requests.Count}.");
                AssertNoErrors();
                return requests.ToArray();
            }

            internal void WaitForIdle(TimeSpan timeout)
            {
                Assert.IsTrue(SpinWait.SpinUntil(
                    () => Volatile.Read(ref activeHandlers) == 0,
                    timeout),
                    $"VIIPER protocol server still has {Volatile.Read(ref activeHandlers)} active handlers.");
            }

            internal void AssertNoErrors()
            {
                if (errors.TryDequeue(out Exception error))
                {
                    Assert.Fail(error.ToString());
                }
            }

            private async Task AcceptLoopAsync()
            {
                try
                {
                    while (!shutdown.IsCancellationRequested)
                    {
                        TcpClient client = await listener.
                            AcceptTcpClientAsync(shutdown.Token);
                        int clientId = Interlocked.Increment(
                            ref nextClientId);
                        clients[clientId] = client;
                        Interlocked.Increment(ref activeHandlers);
                        _ = Task.Run(() => HandleClient(clientId, client));
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (ObjectDisposedException) when (
                    shutdown.IsCancellationRequested)
                {
                }
                catch (SocketException) when (shutdown.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    errors.Enqueue(ex);
                }
            }

            private void HandleClient(int clientId, TcpClient client)
            {
                try
                {
                    using (client)
                    {
                        client.NoDelay = true;
                        NetworkStream network = client.GetStream();
                        byte[] prefix = new byte[5];
                        ReadExactly(network, prefix, 0, prefix.Length);
                        if (!prefix.SequenceEqual(
                            Encoding.ASCII.GetBytes("eVI1\0")))
                        {
                            ReadUnauthenticatedRequest(network, prefix);
                            byte[] unauthorized = Encoding.UTF8.GetBytes(
                                "{\"status\":401,\"title\":\"Unauthorized\",\"detail\":\"authentication required\"}\n");
                            network.Write(unauthorized, 0,
                                unauthorized.Length);
                            return;
                        }

                        byte[] clientNonce = new byte[32];
                        byte[] clientProof = new byte[32];
                        ReadExactly(network, clientNonce, 0,
                            clientNonce.Length);
                        ReadExactly(network, clientProof, 0,
                            clientProof.Length);
                        VerifyClientProof(clientNonce, clientProof);

                        byte[] serverNonce = RandomNumberGenerator.
                            GetBytes(32);
                        byte[] handshakeResponse = Encoding.ASCII.
                            GetBytes("OK\0").Concat(serverNonce).ToArray();
                        network.Write(handshakeResponse, 0,
                            handshakeResponse.Length);

                        byte[] sessionInput = key.Concat(serverNonce)
                            .Concat(clientNonce)
                            .Concat(Encoding.ASCII.GetBytes(
                                "VIIPER-Session-v1")).ToArray();
                        byte[] sessionKey = SHA256.HashData(sessionInput);
                        using var authenticated =
                            new ViiperAuthenticatedStream(network,
                                sessionKey);
                        string request = ReadNullTerminated(authenticated);
                        requests.Enqueue(request);

                        if (IsOpenDeviceStreamRequest(request))
                        {
                            // Device streams remain open. The client closes
                            // this connection before issuing final API cleanup.
                            byte[] drain = new byte[64];
                            while (authenticated.Read(drain, 0,
                                drain.Length) > 0)
                            {
                            }
                            return;
                        }

                        string response = BuildResponse(request);
                        byte[] responseBytes = Encoding.UTF8.GetBytes(
                            response + "\n");
                        authenticated.Write(responseBytes, 0,
                            responseBytes.Length);
                        authenticated.Flush();
                        // Match VIIPER handleConn: one response packet is
                        // followed immediately by a connection close.
                    }
                }
                catch (Exception ex) when (disposed &&
                    (ex is IOException || ex is ObjectDisposedException ||
                     ex is SocketException))
                {
                }
                catch (Exception ex)
                {
                    errors.Enqueue(ex);
                }
                finally
                {
                    clients.TryRemove(clientId, out _);
                    Interlocked.Decrement(ref activeHandlers);
                }
            }

            private bool IsOpenDeviceStreamRequest(string request)
            {
                string[] parts = request.Split('/');
                return parts.Length == 3 &&
                    string.Equals(parts[0], "bus",
                        StringComparison.Ordinal) &&
                    uint.TryParse(parts[1], out uint busId) &&
                    devices.TryGetValue(busId, out TestDevice device) &&
                    string.Equals(parts[2], device.DevId,
                        StringComparison.Ordinal);
            }

            private string BuildResponse(string request)
            {
                if (string.Equals(request, "ping",
                    StringComparison.Ordinal))
                {
                    return Volatile.Read(ref pingResponse);
                }

                if (string.Equals(request, "bus/create 0",
                    StringComparison.Ordinal))
                {
                    int busId = Interlocked.Increment(ref nextBusId);
                    buses[(uint)busId] = 0;
                    return $"{{\"busId\":{busId}}}";
                }

                if (string.Equals(request, "bus/list",
                    StringComparison.Ordinal))
                {
                    if (Interlocked.Exchange(ref failNextBusList, 0) == 1)
                    {
                        return "{\"status\":503,\"title\":\"Unavailable\",\"detail\":\"injected topology probe failure\"}";
                    }
                    string ids = string.Join(",", buses.Keys.OrderBy(
                        busId => busId));
                    return $"{{\"buses\":[{ids}]}}";
                }

                if (TryParseBusRoute(request, "/add ", out uint addBus,
                        out string addPayload))
                {
                    if (Interlocked.Exchange(ref failNextAdd, 0) == 1)
                    {
                        return "{\"status\":500,\"title\":\"Create Failed\",\"detail\":\"injected device creation failure\"}";
                    }
                    if (Interlocked.Exchange(ref blockNextAdd, 0) == 1)
                    {
                        blockedAddEntered.Set();
                        if (!releaseBlockedAdd.Wait(
                                TimeSpan.FromSeconds(5)))
                        {
                            throw new TimeoutException(
                                "Timed out waiting to release the blocked add response.");
                        }
                    }

                    string devId = Interlocked.Increment(
                        ref nextDeviceId).ToString();
                    using JsonDocument document = JsonDocument.Parse(
                        addPayload);
                    string type = document.RootElement.GetProperty("type")
                        .GetString();
                    ushort? idProduct = document.RootElement.TryGetProperty(
                            "idProduct", out JsonElement product)
                        ? product.GetUInt16()
                        : null;
                    devices[addBus] = new TestDevice(devId, type,
                        idProduct);
                    return $"{{\"devId\":\"{devId}\"}}";
                }

                if (TryParseBusRoute(request, "/list", out uint listBus,
                        out _))
                {
                    if (!buses.ContainsKey(listBus))
                    {
                        return "{\"status\":404,\"title\":\"Not Found\",\"detail\":\"bus not found\"}";
                    }
                    if (!devices.TryGetValue(listBus,
                            out TestDevice device))
                    {
                        return "{\"devices\":[]}";
                    }

                    string pid = device.IdProduct.HasValue
                        ? $"0x{device.IdProduct.Value:x4}"
                        : "0x0000";
                    return $"{{\"devices\":[{{\"devId\":\"{device.DevId}\",\"type\":\"{device.Type}\",\"pid\":\"{pid}\",\"deviceSpecific\":{{\"microphoneInterfaceActive\":true}}}}]}}";
                }

                if (TryParseBusRoute(request, "/remove ",
                        out uint removeBus, out string removeDevice))
                {
                    if (devices.TryGetValue(removeBus,
                            out TestDevice current) &&
                        string.Equals(current.DevId, removeDevice,
                            StringComparison.Ordinal))
                    {
                        devices.TryRemove(removeBus, out _);
                    }
                    return string.Empty;
                }

                const string removeBusPrefix = "bus/remove ";
                if (request.StartsWith(removeBusPrefix,
                        StringComparison.Ordinal) &&
                    uint.TryParse(request[removeBusPrefix.Length..],
                        out uint removedBus))
                {
                    devices.TryRemove(removedBus, out _);
                    buses.TryRemove(removedBus, out _);
                    return string.Empty;
                }

                return "{\"status\":404,\"title\":\"Not Found\",\"detail\":\"unexpected test route\"}";
            }

            private static bool TryParseBusRoute(string request,
                string routeMarker, out uint busId, out string remainder)
            {
                busId = 0;
                remainder = null;
                const string prefix = "bus/";
                if (!request.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return false;
                }

                int marker = request.IndexOf(routeMarker,
                    prefix.Length, StringComparison.Ordinal);
                if (marker < 0 || !uint.TryParse(
                        request.AsSpan(prefix.Length,
                            marker - prefix.Length), out busId))
                {
                    return false;
                }

                remainder = request[(marker + routeMarker.Length)..];
                return true;
            }

            private sealed class TestDevice
            {
                internal TestDevice(string devId, string type,
                    ushort? idProduct)
                {
                    DevId = devId;
                    Type = type;
                    IdProduct = idProduct;
                }

                internal string DevId { get; }

                internal string Type { get; }

                internal ushort? IdProduct { get; }
            }

            private void VerifyClientProof(byte[] clientNonce,
                byte[] clientProof)
            {
                byte[] proofInput = Encoding.ASCII.GetBytes(
                    "VIIPER-Auth-v1").Concat(clientNonce).ToArray();
                using var hmac = new HMACSHA256(key);
                byte[] expected = hmac.ComputeHash(proofInput);
                if (!CryptographicOperations.FixedTimeEquals(expected,
                    clientProof))
                {
                    throw new IOException(
                        "The test client sent an invalid VIIPER proof.");
                }
            }

            private static void ReadUnauthenticatedRequest(Stream stream,
                byte[] prefix)
            {
                if (prefix.Contains((byte)0))
                {
                    return;
                }

                byte[] one = new byte[1];
                do
                {
                    ReadExactly(stream, one, 0, 1);
                }
                while (one[0] != 0);
            }

            private static string ReadNullTerminated(Stream stream)
            {
                using var request = new MemoryStream();
                byte[] one = new byte[1];
                while (true)
                {
                    int read = stream.Read(one, 0, 1);
                    if (read == 0)
                    {
                        throw new IOException(
                            "Authenticated VIIPER request closed before its terminator.");
                    }
                    if (one[0] == 0)
                    {
                        return Encoding.UTF8.GetString(request.ToArray());
                    }
                    request.WriteByte(one[0]);
                }
            }

            private static void ReadExactly(Stream stream, byte[] buffer,
                int offset, int count)
            {
                int total = 0;
                while (total < count)
                {
                    int read = stream.Read(buffer, offset + total,
                        count - total);
                    if (read == 0)
                    {
                        throw new IOException(
                            "VIIPER test connection closed early.");
                    }
                    total += read;
                }
            }

            public void Dispose()
            {
                if (disposed)
                {
                    return;
                }
                disposed = true;
                releaseBlockedAdd.Set();
                shutdown.Cancel();
                listener.Stop();
                foreach (TcpClient client in clients.Values)
                {
                    client.Dispose();
                }
                SpinWait.SpinUntil(
                    () => Volatile.Read(ref activeHandlers) == 0,
                    TimeSpan.FromSeconds(2));
                try
                {
                    acceptLoop.GetAwaiter().GetResult();
                }
                catch
                {
                }
                CryptographicOperations.ZeroMemory(key);
                blockedAddEntered.Dispose();
                releaseBlockedAdd.Dispose();
                shutdown.Dispose();
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
