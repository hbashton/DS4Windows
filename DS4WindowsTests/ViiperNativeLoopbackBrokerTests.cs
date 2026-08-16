using DS4Windows;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DS4WindowsTests
{
    [TestClass]
    public class ViiperNativeLoopbackBrokerTests
    {
        private const string Password = "Ds4WNativeKey001";

        [TestMethod]
        public async Task AuthenticatedPingAddAndStreamUseNativeIdentityWithoutUsbip()
        {
            await using var broker = new FrozenNativeBroker(Password);
            var client = new ViiperClient("127.0.0.1", broker.Port,
                ViiperTransportMode.NativeUde,
                ViiperNativeRuntimeTests.CreateMetadata(),
                new FixedCredentialProvider(Password));

            ViiperDeviceStream stream = client.CreateDeviceAndOpenStream(
                "dualsensecombinedaudioduplexv5");
            ViiperVirtualDeviceIdentity identity =
                stream.VirtualDeviceIdentity;
            Assert.AreEqual(ViiperTransportMode.NativeUde,
                identity.TransportMode);
            Assert.AreEqual(-1, identity.LegacyUsbipPort);
            Assert.AreEqual(42u, identity.BusId);
            Assert.AreEqual("7", identity.DevId);
            Assert.AreEqual((42ul << 32) | 7,
                identity.NativePnpAnchor.NativeDeviceId);
            Assert.AreEqual(23u,
                identity.NativePnpAnchor.NativeDeviceGeneration);
            Assert.AreEqual(555ul,
                identity.NativePnpAnchor.ControllerSessionId);
            Assert.AreEqual(8u,
                identity.NativePnpAnchor.UdecxUsbPortNumber);

            byte[] state = ViiperStatePacketBuilder.BuildNeutral(
                ViiperVirtualDeviceType.DualSense);
            stream.WriteFrame(0x05, 0x01, state);
            stream.Dispose();
            await broker.Completion.WaitAsync(TimeSpan.FromSeconds(10));

            byte[] frame = broker.StreamFrame;
            Assert.IsNotNull(frame);
            CollectionAssert.AreEqual(new byte[] { (byte)'V', (byte)'P',
                (byte)'C', (byte)'M', 0x05, 0x01 }, frame.Take(6).ToArray());
            CollectionAssert.AreEqual(state, frame.Skip(16).ToArray());
            Assert.IsTrue(broker.RemovedDevice);
            Assert.IsTrue(broker.RemovedBus);
            Assert.IsTrue(broker.ConditionalRemoveRequested);
            Assert.IsFalse(
                broker.UnexpectedConnectionAfterConditionalRemove);
        }

        [TestMethod]
        public async Task NativeAddIdentityMismatchDoesNotGuessCleanupAndFencesSession()
        {
            await using var broker = new FrozenNativeBroker(Password,
                returnMismatchedAddIdentity: true);
            var client = new ViiperClient("127.0.0.1", broker.Port,
                ViiperTransportMode.NativeUde,
                ViiperNativeRuntimeTests.CreateMetadata(),
                new FixedCredentialProvider(Password));

            Assert.ThrowsException<ViiperIdentityException>(() =>
                client.CreateDeviceAndOpenStream(
                    "dualsensecombinedaudioduplexv5"));
            await broker.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsFalse(broker.ConditionalRemoveRequested);
            Assert.IsFalse(broker.RemovedDevice);
            Assert.IsFalse(broker.RemovedBus);

            ViiperIdentityException fenced =
                Assert.ThrowsException<ViiperIdentityException>(() =>
                    client.ValidateNativeBackend());
            StringAssert.Contains(fenced.Message, "permanently invalid");
        }

        [TestMethod]
        public async Task NativeAddExplicitNullUsbipFieldDoesNotGuessCleanup()
        {
            await using var broker = new FrozenNativeBroker(Password,
                returnForbiddenUsbipField: true);
            var client = new ViiperClient("127.0.0.1", broker.Port,
                ViiperTransportMode.NativeUde,
                ViiperNativeRuntimeTests.CreateMetadata(),
                new FixedCredentialProvider(Password));

            Assert.ThrowsException<ViiperIdentityException>(() =>
                client.CreateDeviceAndOpenStream(
                    "dualsensecombinedaudioduplexv5"));
            await broker.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsFalse(broker.ConditionalRemoveRequested);
            Assert.IsFalse(broker.RemovedDevice);
            Assert.IsFalse(broker.RemovedBus);
        }

        [TestMethod]
        public async Task StaleNativeReceiptPreservesSuccessorWithoutLegacyRetry()
        {
            await using var broker = new FrozenNativeBroker(Password,
                returnStaleRemoveConflict: true);
            var client = new ViiperClient("127.0.0.1", broker.Port,
                ViiperTransportMode.NativeUde,
                ViiperNativeRuntimeTests.CreateMetadata(),
                new FixedCredentialProvider(Password));

            using ViiperDeviceStream stream =
                client.CreateDeviceAndOpenStream(
                    "dualsensecombinedaudioduplexv5");
            byte[] state = ViiperStatePacketBuilder.BuildNeutral(
                ViiperVirtualDeviceType.DualSense);
            stream.WriteFrame(0x05, 0x01, state);
            stream.Dispose();
            await broker.Completion.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.IsTrue(broker.ConditionalRemoveRequested);
            Assert.IsTrue(broker.SuccessorPreserved);
            Assert.IsFalse(broker.RemovedDevice);
            Assert.IsFalse(broker.RemovedBus);
            Assert.IsFalse(
                broker.UnexpectedConnectionAfterConditionalRemove);
        }

        private sealed class FixedCredentialProvider :
            IViiperCredentialProvider
        {
            private readonly string password;
            internal FixedCredentialProvider(string password) =>
                this.password = password;
            public ViiperCredential Read()
            {
                byte[] bytes = Encoding.ASCII.GetBytes(password);
                return new ViiperCredential(password, SHA256.HashData(bytes));
            }
        }

        private sealed class FrozenNativeBroker : IAsyncDisposable
        {
            private readonly string password;
            private readonly bool returnMismatchedAddIdentity;
            private readonly bool returnForbiddenUsbipField;
            private readonly bool returnStaleRemoveConflict;
            private readonly TcpListener listener;
            private readonly Task completion;

            internal FrozenNativeBroker(string password,
                bool returnMismatchedAddIdentity = false,
                bool returnForbiddenUsbipField = false,
                bool returnStaleRemoveConflict = false)
            {
                this.password = password;
                this.returnMismatchedAddIdentity =
                    returnMismatchedAddIdentity;
                this.returnForbiddenUsbipField =
                    returnForbiddenUsbipField;
                this.returnStaleRemoveConflict =
                    returnStaleRemoveConflict;
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
                completion = Task.Run(Run);
            }

            internal int Port { get; }
            internal Task Completion => completion;
            internal byte[] StreamFrame { get; private set; }
            internal bool ConditionalRemoveRequested { get; private set; }
            internal bool RemovedDevice { get; private set; }
            internal bool RemovedBus { get; private set; }
            internal bool SuccessorPreserved { get; private set; }
            internal bool UnexpectedConnectionAfterConditionalRemove
                { get; private set; }

            private async Task Run()
            {
                bool rejectAdd = returnMismatchedAddIdentity ||
                    returnForbiddenUsbipField;
                int connectionCount = rejectAdd ? 3 : 5;
                for (int connection = 0; connection < connectionCount;
                    connection++)
                {
                    using TcpClient tcp = await listener.AcceptTcpClientAsync();
                    NetworkStream transport = tcp.GetStream();
                    byte[] sessionKey = await Authenticate(transport,
                        connection);
                    byte[] request = await ReadRecord(transport, sessionKey,
                        expectedPrefix: 0, expectedCounter: 0);
                    string text = Encoding.UTF8.GetString(request)
                        .TrimEnd('\0');

                    switch (connection)
                    {
                        case 0:
                            Assert.AreEqual("ping", text);
                            await WriteResponse(transport, sessionKey,
                                ViiperNativeRuntimeTests.PingJson(
                                    controllerSessionId: "555"));
                            break;
                        case 1:
                            Assert.AreEqual("bus/create 0", text);
                            await WriteResponse(transport, sessionKey,
                                "{\"busId\":42}");
                            break;
                        case 2:
                            StringAssert.StartsWith(text, "bus/42/add ");
                            using (JsonDocument requestJson =
                                JsonDocument.Parse(text.Substring(
                                    "bus/42/add ".Length)))
                            {
                                Assert.AreEqual(
                                    "dualsensecombinedaudioduplexv5",
                                    requestJson.RootElement.GetProperty(
                                        "type").GetString());
                                Assert.AreEqual(0x0ce6,
                                    requestJson.RootElement.GetProperty(
                                        "idProduct").GetInt32());
                            }
                            string addResponse = JsonSerializer.Serialize(new
                                {
                                    busId = 42,
                                    devId = "7",
                                    vid = "0x054c",
                                    pid = "0x0ce6",
                                    type =
                                        "dualsensecombinedaudioduplexv5",
                                    transport = "native-ude",
                                    deviceSpecific = new
                                    {
                                        serial_number =
                                            "E55700GTD1190A500",
                                    },
                                    nativeUde = new
                                    {
                                        deviceId = ((42ul << 32) | 7)
                                            .ToString(),
                                        deviceGeneration = 23,
                                        controllerSessionId =
                                            returnMismatchedAddIdentity ?
                                                "556" : "555",
                                        controllerInstanceId =
                                            ViiperNativeRuntimeTests
                                                .ControllerInstance,
                                        usb20PortNumber = 0,
                                        usb30PortNumber = 8,
                                    },
                                });
                            if (returnForbiddenUsbipField)
                            {
                                addResponse = addResponse.Insert(
                                    addResponse.Length - 1,
                                    ",\"usbipPort\":null");
                            }
                            await WriteResponse(transport, sessionKey,
                                addResponse);
                            break;
                        case 3:
                            Assert.AreEqual("bus/42/7", text);
                            StreamFrame = await ReadRecord(transport,
                                sessionKey, expectedPrefix: 0,
                                expectedCounter: 1);
                            break;
                        case 4:
                            ValidateConditionalRemove(text);
                            ConditionalRemoveRequested = true;
                            if (returnStaleRemoveConflict)
                            {
                                SuccessorPreserved = true;
                                await WriteResponse(transport, sessionKey,
                                    "{\"status\":409,\"title\":\"native receipt mismatch\",\"detail\":\"successor preserved\"}");
                            }
                            else
                            {
                                RemovedDevice = true;
                                RemovedBus = true;
                                await WriteResponse(transport, sessionKey,
                                    "{\"busId\":42,\"devId\":\"7\"}");
                            }
                            break;
                    }
                    CryptographicOperations.ZeroMemory(sessionKey);
                }

                if (!rejectAdd)
                {
                    Task<TcpClient> unexpected =
                        listener.AcceptTcpClientAsync();
                    Task completed = await Task.WhenAny(unexpected,
                        Task.Delay(250));
                    if (ReferenceEquals(completed, unexpected))
                    {
                        using TcpClient ignored = await unexpected;
                        UnexpectedConnectionAfterConditionalRemove = true;
                    }
                }
            }

            private static void ValidateConditionalRemove(string text)
            {
                const string prefix = "bus/42/remove-native ";
                StringAssert.StartsWith(text, prefix);
                using JsonDocument document = JsonDocument.Parse(
                    text.Substring(prefix.Length));
                JsonElement root = document.RootElement;
                Assert.AreEqual(3, root.EnumerateObject().Count());
                Assert.AreEqual("7", root.GetProperty("devId").GetString());
                Assert.AreEqual("native-ude",
                    root.GetProperty("transport").GetString());
                JsonElement native = root.GetProperty("nativeUde");
                Assert.AreEqual(6, native.EnumerateObject().Count());
                Assert.AreEqual(((42ul << 32) | 7).ToString(),
                    native.GetProperty("deviceId").GetString());
                Assert.AreEqual(23u,
                    native.GetProperty("deviceGeneration").GetUInt32());
                Assert.AreEqual("555",
                    native.GetProperty("controllerSessionId").GetString());
                Assert.AreEqual(ViiperNativeRuntimeTests.ControllerInstance,
                    native.GetProperty("controllerInstanceId").GetString());
                Assert.AreEqual(0u,
                    native.GetProperty("usb20PortNumber").GetUInt32());
                Assert.AreEqual(8u,
                    native.GetProperty("usb30PortNumber").GetUInt32());
            }

            private async Task<byte[]> Authenticate(NetworkStream transport,
                int connection)
            {
                byte[] handshake = new byte[
                    Encoding.UTF8.GetByteCount(
                        ViiperAuthProtocol.HandshakeMagic) + 64];
                await ReadExactly(transport, handshake);
                byte[] magic = Encoding.UTF8.GetBytes(
                    ViiperAuthProtocol.HandshakeMagic);
                CollectionAssert.AreEqual(magic,
                    handshake.Take(magic.Length).ToArray());
                byte[] clientNonce = handshake.Skip(magic.Length).Take(32)
                    .ToArray();
                byte[] receivedTag = handshake.Skip(magic.Length + 32)
                    .ToArray();
                byte[] passwordKey = ViiperAuthProtocol.DerivePasswordKey(
                    password);
                byte[] authData = Combine(Encoding.UTF8.GetBytes(
                    ViiperAuthProtocol.AuthenticationContext), clientNonce);
                byte[] expectedTag = HMACSHA256.HashData(passwordKey,
                    authData);
                Assert.IsTrue(CryptographicOperations.FixedTimeEquals(
                    expectedTag, receivedTag));

                byte[] serverNonce = Enumerable.Range(0, 32).Select(value =>
                    (byte)(value + connection + 1)).ToArray();
                await transport.WriteAsync(Combine(
                    Encoding.ASCII.GetBytes("OK\0"), serverNonce));
                byte[] sessionKey = ViiperAuthProtocol.DeriveSessionKey(
                    passwordKey, serverNonce, clientNonce);
                CryptographicOperations.ZeroMemory(passwordKey);
                return sessionKey;
            }

            private static async Task<byte[]> ReadRecord(
                NetworkStream transport, byte[] key, uint expectedPrefix,
                ulong expectedCounter)
            {
                byte[] header = new byte[4];
                await ReadExactly(transport, header);
                int length = checked((int)
                    BinaryPrimitives.ReadUInt32BigEndian(header));
                Assert.IsTrue(length >= 28 && length <= 2 * 1024 * 1024);
                byte[] record = new byte[length];
                await ReadExactly(transport, record);
                Assert.AreEqual(expectedPrefix,
                    BinaryPrimitives.ReadUInt32BigEndian(record));
                Assert.AreEqual(expectedCounter,
                    BinaryPrimitives.ReadUInt64BigEndian(record.AsSpan(4)));
                byte[] plaintext = new byte[length - 28];
                using var cipher = new ChaCha20Poly1305(key);
                cipher.Decrypt(record.AsSpan(0, 12),
                    record.AsSpan(12, plaintext.Length),
                    record.AsSpan(length - 16), plaintext);
                return plaintext;
            }

            private static async Task WriteResponse(NetworkStream transport,
                byte[] key, string response)
            {
                byte[] plaintext = Encoding.UTF8.GetBytes(response + "\n");
                byte[] nonce = new byte[12];
                BinaryPrimitives.WriteUInt32BigEndian(nonce, 1);
                byte[] ciphertext = new byte[plaintext.Length];
                byte[] tag = new byte[16];
                using (var cipher = new ChaCha20Poly1305(key))
                {
                    cipher.Encrypt(nonce, plaintext, ciphertext, tag);
                }
                byte[] record = Combine(nonce, ciphertext, tag);
                byte[] header = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(header,
                    (uint)record.Length);
                await transport.WriteAsync(Combine(header, record));
            }

            private static async Task ReadExactly(Stream stream,
                byte[] buffer)
            {
                int offset = 0;
                while (offset < buffer.Length)
                {
                    int read = await stream.ReadAsync(buffer.AsMemory(offset));
                    if (read == 0)
                    {
                        throw new EndOfStreamException();
                    }
                    offset += read;
                }
            }

            private static byte[] Combine(params byte[][] pieces)
            {
                byte[] result = new byte[pieces.Sum(piece => piece.Length)];
                int offset = 0;
                foreach (byte[] piece in pieces)
                {
                    Buffer.BlockCopy(piece, 0, result, offset, piece.Length);
                    offset += piece.Length;
                }
                return result;
            }

            public async ValueTask DisposeAsync()
            {
                listener.Stop();
                try
                {
                    await completion.WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch when (!completion.IsCompletedSuccessfully)
                {
                }
            }
        }
    }
}
