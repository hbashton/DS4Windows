using DS4Windows;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
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
                }, out ViiperVirtualDeviceLifetime.State replacement));
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

        [TestMethod]
        public void FinalControllerStateReplaysOncePerPublishedStream()
        {
            long replayedGeneration = 4;
            byte[] pending = null;
            byte[] failed = { 1, 2, 3 };
            byte[] final = { 9, 8, 7 };

            Assert.IsTrue(ViiperFinalStateReplayPolicy.TryPrepare(
                ref replayedGeneration, 5, ref pending, final, failed));
            Assert.AreSame(final, pending,
                "The latest desired state supersedes the packet that failed before recovery.");
            Assert.IsFalse(ViiperFinalStateReplayPolicy.TryPrepare(
                ref replayedGeneration, 5, ref pending, final, failed),
                "Concurrent failures from the retired generation must not queue a duplicate replay.");

            byte[] newerPending = { 4, 5, 6 };
            pending = newerPending;
            Assert.IsTrue(ViiperFinalStateReplayPolicy.TryPrepare(
                ref replayedGeneration, 6, ref pending, final, failed));
            Assert.AreSame(newerPending, pending,
                "A state queued during recovery remains the final replay.");
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
                () => new[] { password });
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

            string[] requests = server.WaitForAuthenticatedRequests(7,
                TimeSpan.FromSeconds(5));
            CollectionAssert.AreEquivalent(new[]
            {
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
        public void BrokerDeathRecreatesExactNativeTopologyAndLifetimeIdentity()
        {
            const string password = "NativeCredential123";
            using var server = new AuthenticatedViiperProtocolServer(password);
            var client = new ViiperClient("127.0.0.1", server.Port,
                () => new[] { password });
            ViiperDeviceStream initial = null;
            ViiperDeviceStream replacement = null;

            try
            {
                initial = client.CreateDeviceAndOpenStream(
                    "dualshock4audioduplexv3", 0x05C4);
                ViiperVirtualDeviceLifetime lifetime =
                    initial.DeviceLifetime;
                initial.CloseTransport();

                replacement = client.RecoverDeviceStream(initial);

                Assert.AreEqual((uint)42, initial.BusId,
                    "The retired stream keeps the identity of the route it opened.");
                Assert.AreEqual("7", initial.DevId);
                Assert.AreEqual((uint)43, replacement.BusId);
                Assert.AreEqual("8", replacement.DevId);
                Assert.AreEqual((uint)43, lifetime.BusId);
                Assert.AreEqual("8", lifetime.DevId);
                Assert.AreEqual(1L, lifetime.Generation);
                Assert.AreEqual(0L, initial.DeviceLifetimeGeneration);
                Assert.AreEqual(1L, replacement.DeviceLifetimeGeneration);
                Assert.AreEqual("dualshock4audioduplexv3",
                    lifetime.Topology.DeviceName);
                Assert.AreEqual((ushort)0x05C4,
                    lifetime.Topology.IdProduct);

                string[] requests = server.WaitForAuthenticatedRequests(8,
                    TimeSpan.FromSeconds(5));
                Assert.AreEqual(2, requests.Count(request =>
                    string.Equals(request, "ping",
                        StringComparison.Ordinal)),
                    "Initial creation and recovery each require an authenticated health proof.");
                Assert.AreEqual(2, server.BusCreateCount);
                CollectionAssert.Contains(requests,
                    "bus/43/add {\"type\":\"dualshock4audioduplexv3\",\"idProduct\":1476}");
                CollectionAssert.Contains(requests, "bus/43/8");
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
        public void NativeRecoveryRejectsHealthyUsbipTransportWithoutCreation()
        {
            const string password = "NativeCredential123";
            using var server = new AuthenticatedViiperProtocolServer(password);
            var client = new ViiperClient("127.0.0.1", server.Port,
                () => new[] { password });
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
                () => new[] { password });
            ViiperDeviceStream initial = client.CreateDeviceAndOpenStream(
                "dualsensecombinedaudioduplexv5");
            initial.CloseTransport();
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
                    stream.BusId == 43 && stream.DevId == "8" &&
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
        public void FailedNativeRecreateRollsBackPartialBus()
        {
            const string password = "NativeCredential123";
            using var server = new AuthenticatedViiperProtocolServer(password);
            var client = new ViiperClient("127.0.0.1", server.Port,
                () => new[] { password });
            ViiperDeviceStream initial = client.CreateDeviceAndOpenStream(
                "dualshock4audioduplexv3", 0x05C4);
            initial.CloseTransport();
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
                    server.Requests.Contains("bus/remove 43"),
                    TimeSpan.FromSeconds(5)),
                    "The partial replacement bus was not removed.");
                Assert.IsFalse(server.Requests.Any(request =>
                    request.StartsWith("bus/43/remove ",
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
                () => new[] { password });
            ViiperDeviceStream initial = client.CreateDeviceAndOpenStream(
                "dualshock4audioduplexv3", 0x05C4);
            initial.CloseTransport();
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
                server.Requests.Contains("bus/43/remove 8") &&
                server.Requests.Contains("bus/remove 43"),
                TimeSpan.FromSeconds(5)),
                "The recreated device and bus were not rolled back after disposal won the race.");

            initial.Dispose();
            server.WaitForIdle(TimeSpan.FromSeconds(5));
            server.AssertNoErrors();
        }

        private static byte[] BuildEncryptedPacket(byte[] key,
            byte[] plaintext)
        {
            byte[] nonce = new byte[12];
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

        private sealed class AuthenticatedViiperProtocolServer : IDisposable
        {
            private const string NativePing = "{\"server\":\"VIIPER\",\"version\":\"0.1.0\",\"transport\":\"native-ude\",\"ready\":true,\"nativeUde\":{\"abiMajor\":1,\"abiMinor\":8,\"capabilities\":13,\"expectedDriverPackageVersion\":\"0.1.0.0\",\"maxDevices\":32,\"maxDescriptorBytes\":262144,\"maxTransferBytes\":1048576,\"maxIsoPackets\":1024,\"maxPendingOperations\":4096}}";
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
            private readonly ConcurrentDictionary<uint, string> devices =
                new();
            private readonly ManualResetEventSlim blockedAddEntered =
                new(false);
            private readonly ManualResetEventSlim releaseBlockedAdd =
                new(true);
            private int nextClientId;
            private int activeHandlers;
            private int nextBusId = 41;
            private int nextDeviceId = 6;
            private int failNextAdd;
            private int blockNextAdd;
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

            internal string PingResponse
            {
                set => Volatile.Write(ref pingResponse, value);
            }

            internal string[] Requests => requests.ToArray();

            internal int BusCreateCount => Requests.Count(request =>
                string.Equals(request, "bus/create 0",
                    StringComparison.Ordinal));

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
                    devices.TryGetValue(busId, out string devId) &&
                    string.Equals(parts[2], devId,
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
                    return $"{{\"busId\":{busId}}}";
                }

                if (TryParseBusRoute(request, "/add ", out uint addBus,
                        out _))
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
                    devices[addBus] = devId;
                    return $"{{\"devId\":\"{devId}\"}}";
                }

                if (TryParseBusRoute(request, "/list", out uint listBus,
                        out _))
                {
                    return devices.TryGetValue(listBus, out string devId)
                        ? $"{{\"devices\":[{{\"devId\":\"{devId}\",\"deviceSpecific\":{{\"microphoneInterfaceActive\":true}}}}]}}"
                        : "{\"status\":404,\"title\":\"Not Found\",\"detail\":\"bus not found\"}";
                }

                if (TryParseBusRoute(request, "/remove ",
                        out uint removeBus, out string removeDevice))
                {
                    if (devices.TryGetValue(removeBus, out string current) &&
                        string.Equals(current, removeDevice,
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
