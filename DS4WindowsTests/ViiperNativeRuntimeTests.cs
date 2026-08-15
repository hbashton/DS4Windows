using DS4Windows;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DS4WindowsTests
{
    [TestClass]
    public class ViiperNativeRuntimeTests
    {
        internal const string BuildIdentity =
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        internal const string ControllerInstance = @"ROOT\VIIPERUDE\0000";

        [TestMethod]
        public void ManagedTransportDefaultsNativeAndUsbipRequiresExplicitOptIn()
        {
            Assert.AreEqual(ViiperTransportMode.NativeUde,
                ViiperTransportSettings.Parse(null));
            Assert.AreEqual(ViiperTransportMode.NativeUde,
                ViiperTransportSettings.Parse("native-ude"));
            Assert.AreEqual(ViiperTransportMode.Usbip,
                ViiperTransportSettings.Parse("usbip"));
            Assert.ThrowsException<ViiperNativeMetadataException>(() =>
                ViiperTransportSettings.Parse("automatic"));
        }

        [TestMethod]
        public void MetadataEligibilityAndHexCapabilitiesFailClosed()
        {
            string directory = Path.Combine(Path.GetTempPath(),
                "ds4w-viiper-metadata-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string production = Path.Combine(directory, "production.json");
                File.WriteAllText(production, MetadataJson("production",
                    "0x0000003d"));
                ViiperNativeRuntimeMetadata parsed =
                    ViiperNativeRuntimeMetadata.Parse(production);
                Assert.AreEqual((ushort)14, parsed.AbiMinor);
                Assert.AreEqual(61u, parsed.RequiredCapabilities);
                Assert.AreEqual((ushort)0x0ce6,
                    parsed.ControllerApiContract[
                        "dualsensecombinedaudioduplexv5"]
                        .Ds4WindowsPidValue);

                string local = Path.Combine(directory, "local.json");
                File.WriteAllText(local, MetadataJson(
                    "local-test-evidence-only", "0x0000003d"));
                Assert.ThrowsException<ViiperNativeMetadataException>(() =>
                    ViiperNativeRuntimeMetadata.Parse(local, "0"));
                Assert.IsNotNull(ViiperNativeRuntimeMetadata.Parse(local,
                    "1"));

                string conflicting = Path.Combine(directory,
                    "conflicting.json");
                File.WriteAllText(conflicting, MetadataJson("production",
                    "0x0000003c"));
                Assert.ThrowsException<ViiperNativeMetadataException>(() =>
                    ViiperNativeRuntimeMetadata.Parse(conflicting));
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        [TestMethod]
        public void AuthenticatedPingIsComparedToDynamicMetadataIdentity()
        {
            var contract = new ViiperNativeRuntimeContract(CreateMetadata());
            ViiperNativeBackendIdentity identity = contract.ValidatePing(
                PingJson(controllerSessionId: "987654321"));
            Assert.AreEqual((ushort)14, identity.AbiMinor);
            Assert.AreEqual(61u, identity.Capabilities);
            Assert.AreEqual(987654321ul, identity.ControllerSessionId);

            Assert.ThrowsException<ViiperIdentityException>(() =>
                contract.ValidatePing(PingJson(transport: "usbip")));
            Assert.ThrowsException<ViiperIdentityException>(() =>
                contract.ValidatePing(PingJson(ready: false)));
            Assert.ThrowsException<ViiperIdentityException>(() =>
                contract.ValidatePing(PingJson(abiMinor: 13)));
            Assert.ThrowsException<ViiperIdentityException>(() =>
                contract.ValidatePing(PingJson(capabilities: 29)));
            Assert.ThrowsException<ViiperIdentityException>(() =>
                contract.ValidatePing(PingJson(packageVersion: "0.1.0.39")));
            Assert.ThrowsException<ViiperIdentityException>(() =>
                contract.ValidatePing(PingJson(buildIdentity:
                    new string('c', 64))));
            Assert.ThrowsException<ViiperIdentityException>(() =>
                contract.ValidatePing(PingJson(controllerSessionId: "01")));

            string valid = PingJson();
            string duplicate = valid.Insert(valid.LastIndexOf('}'),
                ",\"transport\":\"native-ude\"");
            Assert.ThrowsException<ViiperIdentityException>(() =>
                contract.ValidatePing(duplicate));
        }

        [TestMethod]
        public void ReconnectRejectsBackendGenerationAndControllerSessionChange()
        {
            var session = new ViiperNativeSession(
                new ViiperNativeRuntimeContract(CreateMetadata()),
                new MutableCredentialProvider("Ds4WNativeKey001"));
            session.AdmitPing(PingJson(controllerSessionId: "41"),
                reconnect: false);
            Assert.ThrowsException<ViiperIdentityException>(() =>
                session.AdmitPing(PingJson(controllerSessionId: "42"),
                    reconnect: true));
            Assert.ThrowsException<ViiperIdentityException>(() =>
                session.AdmitPing(PingJson(controllerSessionId: "41"),
                    reconnect: true));
        }

        [TestMethod]
        public void CredentialGenerationChangePermanentlyInvalidatesSession()
        {
            var provider = new MutableCredentialProvider("Ds4WNativeKey001");
            var session = new ViiperNativeSession(
                new ViiperNativeRuntimeContract(CreateMetadata()), provider);
            using (Stream authenticated = session.Authenticate(
                SuccessfulHandshakeTransport()))
            {
            }
            provider.Password = "Ds4WNativeKey002";
            Assert.ThrowsException<ViiperAuthenticationException>(() =>
                session.Authenticate(SuccessfulHandshakeTransport()));
            Assert.ThrowsException<ViiperIdentityException>(() =>
                session.Authenticate(SuccessfulHandshakeTransport()));
        }

        [TestMethod]
        public void TransientHandshakeEofDoesNotPoisonLaterAuthentication()
        {
            var session = new ViiperNativeSession(
                new ViiperNativeRuntimeContract(CreateMetadata()),
                new MutableCredentialProvider("Ds4WNativeKey001"));
            Assert.IsFalse(session.HasAuthenticatedConnection);
            Assert.ThrowsException<ViiperAuthenticationException>(() =>
                session.Authenticate(new DuplexMemoryStream(
                    Array.Empty<byte>())));
            Assert.IsFalse(session.HasAuthenticatedConnection);
            using Stream authenticated = session.Authenticate(
                SuccessfulHandshakeTransport());
            Assert.IsNotNull(authenticated);
            Assert.IsTrue(session.HasAuthenticatedConnection);
        }

        [TestMethod]
        public void NativeLifetimeNeverInvokesUsbipOwnershipCallbacks()
        {
            int removed = 0;
            int detached = 0;
            int unregistered = 0;
            int stale = 0;
            var identity = new ViiperVirtualDeviceIdentity
            {
                TransportMode = ViiperTransportMode.NativeUde,
                BusId = 17,
                DevId = "4",
                DeviceType = "dualsensecombinedaudioduplexv5",
                LogicalLifetimeId = "native-lifetime",
                NativePnpAnchor = new ViiperNativePnpAnchor
                {
                    NativeDeviceId = (17ul << 32) | 4,
                    NativeDeviceGeneration = 3,
                    ControllerSessionId = 99,
                    ControllerInstanceId = ControllerInstance,
                    Usb20PortNumber = 6,
                },
            };
            using (var lifetime = new ViiperVirtualDeviceLifetime(identity,
                captured =>
                {
                    Assert.AreSame(identity, captured);
                    removed++;
                }, (_, _) => detached++,
                _ => unregistered++, () => stale++))
            {
                Assert.AreEqual(-1, lifetime.UsbipPort);
                Assert.AreEqual(1,
                    lifetime.NextStreamIdentity().StreamGeneration);
            }
            Assert.AreEqual(1, removed);
            Assert.AreEqual(0, detached);
            Assert.AreEqual(0, unregistered);
            Assert.AreEqual(0, stale);
        }

        [TestMethod]
        public void SourceBoundControllerContractRejectsRemovedAliases()
        {
            var contract = new ViiperNativeRuntimeContract(CreateMetadata());
            Assert.AreEqual((ushort)0x05c4,
                contract.ValidateControllerRequest(
                    "dualshock4audioduplexv3", null));
            Assert.IsTrue(contract.HasExactControllerIdentity(
                "dualshock4audioduplexv3", "0x054c", "0x05c4"));
            Assert.ThrowsException<ViiperIdentityException>(() =>
                contract.ValidateControllerRequest(
                    "dualsensecombinedaudioduplexv4", null));
            Assert.ThrowsException<ViiperIdentityException>(() =>
                contract.ValidateControllerRequest(
                    "dualshock4audioduplexv3", 0x09cc));
            Assert.AreEqual("dualsensegamepadv5",
                ViiperStatePacketBuilder.GetViiperDeviceName(
                    ViiperVirtualDeviceType.DualSense));
            Assert.AreEqual("dualsenseedgegamepadv5",
                ViiperStatePacketBuilder.GetViiperDeviceName(
                    ViiperVirtualDeviceType.DualSenseEdge));
        }

        [TestMethod]
        public void V5RealtimeHapticsRequiresExactVersionTypeAndPayload()
        {
            Assert.IsTrue(ViiperOutDevice.IsValidV5RealtimeHapticsFrame(
                0x05, 0x84, ViiperOutDevice.DualSenseAtomicFeedbackLength));
            Assert.IsFalse(ViiperOutDevice.IsValidV5RealtimeHapticsFrame(
                0x04, 0x84, ViiperOutDevice.DualSenseAtomicFeedbackLength));
            Assert.IsFalse(ViiperOutDevice.IsValidV5RealtimeHapticsFrame(
                0x05, 0x83, ViiperOutDevice.DualSenseAtomicFeedbackLength));
            Assert.IsFalse(ViiperOutDevice.IsValidV5RealtimeHapticsFrame(
                0x05, 0x84,
                ViiperOutDevice.DualSenseAtomicFeedbackLength - 1));
        }

        internal static ViiperNativeRuntimeMetadata CreateMetadata()
        {
            var registrations = new Dictionary<string,
                ViiperNativeControllerRegistration>(StringComparer.Ordinal);
            AddRegistration(registrations, "xbox360", "0x045e",
                "0x028e", "fixed");
            AddRegistration(registrations, "ns2pro", "0x057e",
                "0x2069", "fixed");
            AddRegistration(registrations, "dualshock4", "0x054c",
                "0x05c4", "fixed", "0x09cc");
            AddRegistration(registrations, "dualshock4audioduplexv3",
                "0x054c", "0x05c4", "framed-v3", "0x09cc");
            AddRegistration(registrations,
                "dualshock4audioonlyduplexv3", "0x054c", "0x05c4",
                "framed-v3", "0x09cc");
            AddRegistration(registrations,
                "dualsensecombinedaudioduplexv5", "0x054c", "0x0ce6",
                "framed-v5");
            AddRegistration(registrations,
                "dualsenseaudioonlyduplexv5", "0x054c", "0x0ce6",
                "framed-v5");
            AddRegistration(registrations, "dualsensegamepadv5",
                "0x054c", "0x0ce6", "framed-v5");
            AddRegistration(registrations,
                "dualsenseedgecombinedaudioduplexv5", "0x054c",
                "0x0df2", "framed-v5");
            AddRegistration(registrations, "dualsenseedgegamepadv5",
                "0x054c", "0x0df2", "framed-v5");
            return new ViiperNativeRuntimeMetadata
            {
                SchemaVersion = 1,
                SourceRevision = new string('a', 40),
                ReleaseEligibility = "production",
                DriverPackageVersion = "0.1.0.38",
                AbiMajor = 1,
                AbiMinor = 14,
                RequiredCapabilities = 61,
                RequiredCapabilitiesHex = "0x0000003d",
                LoadedDriverBuildIdentity = BuildIdentity,
                ControllerApiContract = registrations,
            };
        }

        private static void AddRegistration(Dictionary<string,
            ViiperNativeControllerRegistration> registrations, string type,
            string vid, string clientPid, string streamProtocol,
            string defaultPid = null)
        {
            registrations.Add(type, new ViiperNativeControllerRegistration
            {
                Type = type,
                DefaultVid = vid,
                DefaultPid = defaultPid ?? clientPid,
                Ds4WindowsPid = clientPid,
                InterfaceProfile = "test-profile",
                StreamProtocol = streamProtocol,
            });
        }

        internal static string PingJson(string transport = "native-ude",
            bool ready = true, ushort abiMinor = 14,
            uint capabilities = 61,
            string packageVersion = "0.1.0.38",
            string buildIdentity = BuildIdentity,
            string controllerSessionId = "987654321")
        {
            return JsonSerializer.Serialize(new
            {
                server = "VIIPER",
                version = "0.1.0-test",
                transport,
                ready,
                nativeUde = new
                {
                    abiMajor = 1,
                    abiMinor,
                    capabilities,
                    expectedDriverPackageVersion = packageVersion,
                    loadedDriverBuildIdentity = buildIdentity,
                    controllerInstanceId = ControllerInstance,
                    controllerSessionId,
                },
            });
        }

        private static string MetadataJson(string eligibility,
            string capabilitiesHex)
        {
            return $$"""
            {
              "schemaVersion": 1,
              "releaseEligibility": "{{eligibility}}",
              "localTestOptInEnvironment": "DS4WINDOWS_VIIPER_ALLOW_LOCAL_TEST",
              "sourceRevision": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "driverPackageVersion": "0.1.0.38",
              "driverAbi": { "major": 1, "minor": 14 },
              "requiredCapabilities": 61,
              "requiredCapabilitiesHex": "{{capabilitiesHex}}",
              "loadedDriverBuildIdentity": "{{BuildIdentity}}",
              "managedBroker": {
                "serviceName": "VIIPERNativeBroker",
                "serviceAccount": "LocalSystem",
                "startMode": "automatic",
                "transport": "native-ude",
                "apiHost": "127.0.0.1",
                "apiPort": 3242,
                "credentialPath": "%ProgramData%/VIIPER/viiper.key.txt"
              },
              "controllerApiContract": {
                "schemaVersion": 1,
                "sourceRevision": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "implementation": "test fixture",
                "registrations": [{
                  "type": "dualsensecombinedaudioduplexv5",
                  "defaultVid": "0x054c",
                  "defaultPid": "0x0ce6",
                  "ds4WindowsPid": "0x0ce6",
                  "interfaceProfile": "hid-audio-duplex",
                  "streamProtocol": "framed-v5"
                }]
              }
            }
            """;
        }

        private static Stream SuccessfulHandshakeTransport()
        {
            return new DuplexMemoryStream(Encoding.ASCII.GetBytes("OK\0")
                .Concat(Enumerable.Range(0, 32).Select(value =>
                    (byte)value)).ToArray());
        }

        private sealed class MutableCredentialProvider :
            IViiperCredentialProvider
        {
            internal MutableCredentialProvider(string password)
            {
                Password = password;
            }

            internal string Password { get; set; }

            public ViiperCredential Read()
            {
                byte[] bytes = Encoding.ASCII.GetBytes(Password);
                return new ViiperCredential(Password, SHA256.HashData(bytes));
            }
        }

        private sealed class DuplexMemoryStream : Stream
        {
            private readonly byte[] input;
            private int offset;
            internal DuplexMemoryStream(byte[] input) => this.input = input;
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }
            public override void Flush() { }
            public override int Read(byte[] buffer, int bufferOffset,
                int count)
            {
                int length = Math.Min(count, input.Length - offset);
                if (length > 0)
                {
                    Buffer.BlockCopy(input, offset, buffer, bufferOffset,
                        length);
                    offset += length;
                }
                return length;
            }
            public override void Write(byte[] buffer, int offset, int count)
            {
            }
            public override long Seek(long offset, SeekOrigin origin) =>
                throw new NotSupportedException();
            public override void SetLength(long value) =>
                throw new NotSupportedException();
        }
    }
}
