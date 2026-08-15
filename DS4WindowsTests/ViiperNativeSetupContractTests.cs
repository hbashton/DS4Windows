using DS4Windows;
using System.Text.Json;

namespace DS4WindowsTests
{
    [TestClass]
    public class ViiperNativeSetupContractTests
    {
        [TestMethod]
        public void ReadinessRequiresEveryManagedNativeProof()
        {
            ViiperPrerequisiteStatus status = ReadyStatus();
            Assert.IsTrue(status.Ready);
            Assert.IsFalse(status.UsbipInstalled);

            foreach (Action<ViiperPrerequisiteStatus> removeProof in new Action<ViiperPrerequisiteStatus>[]
            {
                value => value.MetadataEligible = false,
                value => value.BrokerInstalled = false,
                value => value.BrokerHashMatches = false,
                value => value.BrokerServiceInstalled = false,
                value => value.BrokerServiceConfigured = false,
                value => value.BrokerServiceRunning = false,
                value => value.CredentialReadable = false,
                value => value.AuthenticatedPingSucceeded = false,
                value => value.RuntimeContractCompatible = false,
            })
            {
                status = ReadyStatus();
                removeProof(status);
                Assert.IsFalse(status.Ready);
            }
        }

        [TestMethod]
        public void StatusCopyNamesNativeServiceAndLocalTestBoundary()
        {
            ViiperPrerequisiteStatus localTest = ReadyStatus();
            localTest.LocalTestMetadata = true;
            Assert.IsTrue(localTest.DisplayText.Contains("disposable-VM",
                StringComparison.Ordinal));

            ViiperPrerequisiteStatus stopped = ReadyStatus();
            stopped.BrokerServiceRunning = false;
            Assert.IsTrue(stopped.DisplayText.Contains(
                ViiperSetupManager.NativeBrokerServiceName,
                StringComparison.Ordinal));

            ViiperPrerequisiteStatus unauthenticated = ReadyStatus();
            unauthenticated.AuthenticatedPingSucceeded = false;
            Assert.IsTrue(unauthenticated.DisplayText.Contains(
                "authentication", StringComparison.OrdinalIgnoreCase));
        }

        [TestMethod]
        public void BundledMetadataIsExplicitLocalTestEvidenceWithExactArtifacts()
        {
            string root = FindRepositoryRoot();
            string path = Path.Combine(root, "extras",
                "ViiperNativeRuntimeMetadata.json");
            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllText(path));
            JsonElement metadata = document.RootElement;

            Assert.AreEqual(1,
                metadata.GetProperty("schemaVersion").GetInt32());
            Assert.AreEqual("local-test-evidence-only",
                metadata.GetProperty("releaseEligibility").GetString());
            Assert.AreEqual(
                ViiperSetupManager.LocalTestOptInEnvironment,
                metadata.GetProperty("localTestOptInEnvironment").GetString());
            Assert.IsTrue(metadata.GetProperty("requiredCapabilities")
                .GetUInt32() != 0);
            Assert.AreEqual(64, metadata.GetProperty(
                "loadedDriverBuildIdentity").GetString().Length);
            JsonElement brokerContract = metadata.GetProperty(
                "managedBroker");
            Assert.AreEqual(ViiperSetupManager.NativeBrokerServiceName,
                brokerContract.GetProperty("serviceName").GetString());
            Assert.AreEqual("LocalSystem", brokerContract.GetProperty(
                "serviceAccount").GetString());
            Assert.AreEqual("native-ude", brokerContract.GetProperty(
                "transport").GetString());
            Assert.AreEqual(ViiperSetupManager.ApiPort,
                brokerContract.GetProperty("apiPort").GetInt32());

            JsonElement controllerContract = metadata.GetProperty(
                "controllerApiContract");
            Assert.AreEqual(1, controllerContract.GetProperty(
                "schemaVersion").GetInt32());
            Assert.AreEqual(metadata.GetProperty("sourceRevision")
                .GetString(), controllerContract.GetProperty(
                    "sourceRevision").GetString());
            Dictionary<string, string> expectedControllers = new(
                StringComparer.Ordinal)
            {
                ["xbox360"] =
                    "xbox360|0x045e|0x028e|0x028e|xusb-composite|fixed",
                ["dualshock4"] =
                    "dualshock4|0x054c|0x09cc|0x05c4|hid-audio-duplex|fixed",
                ["dualshock4audioduplexv3"] =
                    "dualshock4|0x054c|0x09cc|0x05c4|hid-audio-duplex|framed-v3",
                ["dualshock4audioonlyduplexv3"] =
                    "dualshock4|0x054c|0x09cc|0x05c4|audio-duplex-only|framed-v3",
                ["dualsensecombinedaudioduplexv5"] =
                    "dualsense|0x054c|0x0ce6|0x0ce6|hid-audio-duplex|framed-v5",
                ["dualsenseaudioonlyduplexv5"] =
                    "dualsense|0x054c|0x0ce6|0x0ce6|audio-duplex-only|framed-v5",
                ["dualsensegamepadv5"] =
                    "dualsense|0x054c|0x0ce6|0x0ce6|hid-gamepad-only|framed-v5",
                ["dualsenseedgecombinedaudioduplexv5"] =
                    "dualsense-edge|0x054c|0x0df2|0x0df2|hid-audio-duplex|framed-v5",
                ["dualsenseedgegamepadv5"] =
                    "dualsense-edge|0x054c|0x0df2|0x0df2|hid-gamepad-only|framed-v5",
                ["ns2pro"] =
                    "switch2-pro|0x057e|0x2069|0x2069|hid-vendor-bulk|fixed",
            };
            HashSet<string> controllerTypes = new(
                StringComparer.Ordinal);
            foreach (JsonElement registration in controllerContract
                .GetProperty("registrations").EnumerateArray())
            {
                string type = registration.GetProperty("type").GetString();
                Assert.IsTrue(controllerTypes.Add(type), type);
                string signature = string.Join("|",
                    registration.GetProperty("persona").GetString(),
                    registration.GetProperty("defaultVid").GetString(),
                    registration.GetProperty("defaultPid").GetString(),
                    registration.GetProperty("ds4WindowsPid").GetString(),
                    registration.GetProperty("interfaceProfile").GetString(),
                    registration.GetProperty("streamProtocol").GetString());
                Assert.IsTrue(expectedControllers.TryGetValue(type,
                    out string expectedSignature), type);
                Assert.AreEqual(expectedSignature, signature, type);
            }
            Assert.AreEqual(expectedControllers.Count,
                controllerTypes.Count);

            using JsonDocument template = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(root, "extras",
                    "ViiperControllerApiContract.json")));
            Assert.AreEqual(
                JsonSerializer.Serialize(template.RootElement),
                JsonSerializer.Serialize(controllerContract));

            HashSet<string> roles = new HashSet<string>(
                StringComparer.Ordinal);
            foreach (JsonElement artifact in metadata.GetProperty(
                "artifacts").EnumerateArray())
            {
                Assert.IsTrue(roles.Add(artifact.GetProperty("role")
                    .GetString()));
                Assert.IsTrue(artifact.GetProperty("length").GetInt64() > 0);
                Assert.AreEqual(64,
                    artifact.GetProperty("sha256").GetString().Length);
                Assert.IsTrue(artifact.GetProperty("relativePath")
                    .GetString().StartsWith("viiper-native-package/",
                        StringComparison.Ordinal));
            }

            foreach (string required in new[]
            {
                "broker", "driver-helper", "submission-manifest",
                "driver-inf", "driver-sys", "driver-cat",
                "signed-driver-inf", "signed-driver-sys",
                "signed-driver-cat", "local-test-package-lock",
                "local-test-certificate-evidence",
            })
            {
                Assert.IsTrue(roles.Contains(required), required);
            }
        }

        [TestMethod]
        public void NativePackageManagerIsOfflineExactAndParameterized()
        {
            string root = FindRepositoryRoot();
            string source = File.ReadAllText(Path.Combine(root, "extras",
                "manage-viiper-native-package.ps1"));

            foreach (string required in new[]
            {
                "native-package-install",
                "'uninstall', '--yes'",
                "--expected-broker-sha-256",
                "--expected-helper-sha-256",
                "--expected-manifest-sha-256",
                "--expected-inf-sha-256",
                "--expected-sys-sha-256",
                "--expected-cat-sha-256",
                "--target-user-sid",
                "--driver-validation-mode",
                "DS4WINDOWS_VIIPER_NATIVE_RESULT",
                "'not-started'",
                "'safely-settled'",
                "'unverified-see-transaction-log'",
                "AcknowledgeDisposableTestMachine",
                "$driverPackageVersion",
                "$driverABIMajor",
                "$driverCapabilities",
                "controllerApiContract",
                "dualsensecombinedaudioduplexv5",
                "dualsenseedgecombinedaudioduplexv5",
            })
            {
                StringAssert.Contains(source, required);
            }

            foreach (string forbidden in new[]
            {
                "usbip-win2", "RunVIIPER", "Invoke-WebRequest",
                "Invoke-RestMethod", "api.github.com",
            })
            {
                Assert.IsFalse(source.Contains(forbidden,
                    StringComparison.OrdinalIgnoreCase), forbidden);
            }
        }

        [TestMethod]
        public void ReleaseWorkflowRequiresProductionRuntimeBeforePublish()
        {
            string root = FindRepositoryRoot();
            string release = File.ReadAllText(Path.Combine(root, ".github",
                "workflows", "release.yml"));
            StringAssert.Contains(release,
                "Test-ViiperNativePackageContract.ps1 -RequireProduction");

            string project = File.ReadAllText(Path.Combine(root,
                "DS4Windows", "DS4WinWPF.csproj"));
            StringAssert.Contains(project,
                "ViiperNativeRuntimeMetadata.json");
            StringAssert.Contains(project,
                "manage-viiper-native-package.ps1");
            Assert.IsFalse(project.Contains("install-viiper-backend.ps1",
                StringComparison.OrdinalIgnoreCase));

            string generator = File.ReadAllText(Path.Combine(root,
                "extras", "New-ViiperNativeRuntimeMetadata.ps1"));
            StringAssert.Contains(generator, "$ControllerContractPath");
            StringAssert.Contains(generator,
                "[string]$submission.driverPackageVersion");
            Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(
                generator, @"(?<![0-9])0\.1\.0\.[0-9]+(?![0-9])"));
        }

        private static ViiperPrerequisiteStatus ReadyStatus() => new()
        {
            MetadataFound = true,
            MetadataEligible = true,
            PackageBundleFound = true,
            BrokerInstalled = true,
            BrokerHashMatches = true,
            BrokerServiceInstalled = true,
            BrokerServiceConfigured = true,
            BrokerServiceRunning = true,
            CredentialReadable = true,
            AuthenticatedPingSucceeded = true,
            RuntimeContractCompatible = true,
            SetupScriptFound = true,
        };

        private static string FindRepositoryRoot()
        {
            foreach (string startingPoint in new[]
            {
                Environment.CurrentDirectory,
                AppContext.BaseDirectory,
            })
            {
                DirectoryInfo cursor = new DirectoryInfo(startingPoint);
                while (cursor != null)
                {
                    if (File.Exists(Path.Combine(cursor.FullName,
                        "DS4WindowsWPF.sln")))
                    {
                        return cursor.FullName;
                    }
                    cursor = cursor.Parent;
                }
            }
            Assert.Fail("Could not locate the DS4Windows repository root.");
            return null;
        }
    }
}
