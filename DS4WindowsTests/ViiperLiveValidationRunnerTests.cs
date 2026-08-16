using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using DS4Windows;
using DS4Windows.ViiperLiveValidation;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4WindowsTests
{
    [TestClass]
    [DoNotParallelize]
    public class ViiperLiveValidationRunnerTests
    {
        [TestMethod]
        public void ValidationLeaseRequiresMatchingCanonicalDualConsent()
        {
            string nonce = new string('a',
                ViiperLiveValidationLease.NonceLength);
            string variable = ViiperLiveValidationLease
                .NonceEnvironmentVariable;
            string original = Environment.GetEnvironmentVariable(variable);
            try
            {
                Environment.SetEnvironmentVariable(variable, nonce);
                ViiperLiveValidationLease lease =
                    ViiperLiveValidationLease.Create(nonce);
                CollectionAssert.AreEqual(SHA256.HashData(
                    Encoding.ASCII.GetBytes(nonce)), lease.NonceFingerprint);

                Environment.SetEnvironmentVariable(variable,
                    new string('b', nonce.Length));
                Assert.ThrowsException<ViiperIdentityException>(() =>
                    ViiperLiveValidationLease.Create(nonce));
                Environment.SetEnvironmentVariable(variable,
                    nonce.ToUpperInvariant());
                Assert.ThrowsException<ViiperIdentityException>(() =>
                    ViiperLiveValidationLease.Create(
                        nonce.ToUpperInvariant()));
                Environment.SetEnvironmentVariable(variable, nonce[..^1]);
                Assert.ThrowsException<ViiperIdentityException>(() =>
                    ViiperLiveValidationLease.Create(nonce[..^1]));
            }
            finally
            {
                Environment.SetEnvironmentVariable(variable, original);
            }
        }

        [TestMethod]
        public void OrdinaryOutputDeviceCannotAcquireValidationHooks()
        {
            var device = new ViiperOutDevice(OutContType.ViiperDS4,
                ViiperVirtualDeviceType.DualShock4);
            Assert.ThrowsException<ViiperIdentityException>(() =>
                device.GetLiveValidationSnapshot(null));
            Assert.ThrowsException<ViiperIdentityException>(() =>
                device.SubmitLiveValidationMicrophonePcm(null,
                    new byte[320]));
            Assert.ThrowsException<ViiperIdentityException>(() =>
                device.InterruptLiveValidationTransport(null));
        }

        [TestMethod]
        public void RunnerUsesExactProductionPlayStationHandlers()
        {
            CollectionAssert.AreEqual(new[]
            {
                "dualshock4audioduplexv3",
                "dualsensecombinedaudioduplexv5",
                "dualsenseedgecombinedaudioduplexv5",
            }, ControllerSpec.All.Select(spec => spec.Handler).ToArray());
            CollectionAssert.AreEqual(new[]
            {
                "0x05c4", "0x0ce6", "0x0df2",
            }, ControllerSpec.All.Select(spec => spec.Pid).ToArray());
            CollectionAssert.AreEqual(new[]
            {
                "framed-v3", "framed-v5", "framed-v5",
            }, ControllerSpec.All.Select(spec => spec.StreamProtocol)
                .ToArray());
        }

        [TestMethod]
        public void RunnerInvokesOutputDeviceRatherThanDirectClient()
        {
            string root = FindRepositoryRoot();
            string runner = File.ReadAllText(Path.Combine(root, "tools",
                "DS4Windows.ViiperLiveValidation",
                "LiveValidationRunner.cs"));
            string outputDevice = File.ReadAllText(Path.Combine(root,
                "DS4Windows", "DS4Control", "Viiper",
                "ViiperOutDevice.cs"));
            StringAssert.Contains(runner, "new ViiperOutDevice(");
            Assert.IsFalse(runner.Contains("new ViiperClient(",
                StringComparison.Ordinal));
            StringAssert.Contains(outputDevice,
                "\"dualshock4audioduplexv3\", 0x05C4");
            StringAssert.Contains(outputDevice,
                "\"dualsensecombinedaudioduplexv5\"");
            StringAssert.Contains(outputDevice,
                "\"dualsenseedgecombinedaudioduplexv5\"");
        }

        [TestMethod]
        public void FeedbackWitnessMatchesIndependentProbeMarkers()
        {
            byte[] ds4 = LiveValidationRunner.ExpectedFeedback(
                ControllerSpec.All[0]);
            CollectionAssert.AreEqual(new byte[]
            {
                0x23, 0xA7, 0x11, 0x52, 0xC3, 0x04, 0x09,
            }, ds4);

            byte[] dualSense = LiveValidationRunner.ExpectedFeedback(
                ControllerSpec.All[1]);
            Assert.AreEqual(ViiperOutDevice.DualSenseAtomicFeedbackLength,
                dualSense.Length);
            Assert.AreEqual(0x22, dualSense[0]);
            Assert.AreEqual(0x88, dualSense[1]);
            Assert.AreEqual(0x21, dualSense[6]);
            Assert.AreEqual(0x55, dualSense[26]);
            Assert.AreEqual(0x02, dualSense[28]);
            Assert.AreEqual(0x24, dualSense[28 + 44]);
            Assert.IsTrue(dualSense.AsSpan(28 + 48).ToArray()
                .All(value => value == 0));
        }

        [TestMethod]
        public void LatencySummaryUsesBoundedLongTailGate()
        {
            var passing = Enumerable.Range(0, 32).Select(index =>
                new InputSampleEvidence
                {
                    LatencyMicroseconds = index == 31 ? 7900 : 3000,
                }).ToArray();
            LatencySummaryEvidence pass = ProbeRunner.Summarize(passing);
            Assert.IsTrue(pass.Passed);
            Assert.AreEqual(7900, pass.MaximumMicroseconds);

            passing[^1].LatencyMicroseconds = 20001;
            LatencySummaryEvidence fail = ProbeRunner.Summarize(passing);
            Assert.IsFalse(fail.Passed);
            Assert.AreEqual(20001, fail.MaximumMicroseconds);
        }

        [TestMethod]
        public void ProbeMetricReceiptIsSortedAndRejectsDuplicates()
        {
            const string metrics =
                "z=1 a=2 b=3 c=4 d=5 e=6 f=7 g=8";
            SortedDictionary<string, string> parsed =
                ProbeRunner.ParseMetrics(metrics);
            CollectionAssert.AreEqual(new[]
            {
                "a", "b", "c", "d", "e", "f", "g", "z",
            }, parsed.Keys.ToArray());
            Assert.ThrowsException<IOException>(() =>
                ProbeRunner.ParseMetrics(
                    "a=1 a=2 b=3 c=4 d=5 e=6 f=7 g=8"));
        }

        [TestMethod]
        public void OptionsAndFailureEvidenceRemainBoundedWithoutLiveState()
        {
            string nonce = new string('c', 64);
            LiveValidationOptions options = LiveValidationOptions.Parse(
                new[]
                {
                    "--nonce", nonce,
                    "--output", "evidence.json",
                    "--metadata", "metadata.json",
                    "--artifact-root", "artifacts",
                    "--samples", "32",
                    "--media-seconds", "1",
                });
            Assert.AreEqual(32, options.Samples);
            Assert.AreEqual(1, options.MediaSeconds);

            var evidence = new EvidenceDocument
            {
                CurrentStage = new string('s', 1024),
            };
            evidence.RecordFailure(new InvalidOperationException(
                new string('x', 10000)));
            evidence.Finalized = true;
            string json = EvidenceWriter.Serialize(evidence);
            Assert.AreEqual(2, evidence.SchemaVersion);
            Assert.IsTrue(Encoding.UTF8.GetByteCount(json) <
                EvidenceLimits.MaximumJsonBytes);
            Assert.AreEqual(256, evidence.Failures[0].Stage.Length);
            Assert.AreEqual(4096, evidence.Failures[0].Message.Length);
        }

        [TestMethod]
        public async Task ExistingMetadataOutputCollisionNeverOverwrites()
        {
            string root = Path.Combine(Path.GetTempPath(),
                "DS4Windows-runner-output-test-" +
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string metadata = Path.Combine(root,
                "ViiperNativeRuntimeMetadata.json");
            const string original =
                "{\"sentinel\":\"must-not-be-overwritten\"}\n";
            File.WriteAllText(metadata, original, Encoding.UTF8);
            try
            {
                RawRunnerResult result = await RunRunnerRawAsync(new[]
                {
                    "--nonce", "invalid",
                    "--output", metadata,
                });
                Assert.AreEqual(1, result.ExitCode);
                Assert.AreEqual(original,
                    File.ReadAllText(metadata, Encoding.UTF8));
                using JsonDocument document = JsonDocument.Parse(
                    result.StandardOutput);
                Assert.AreEqual("evidence-output-reservation",
                    document.RootElement.GetProperty("failureStage")
                        .GetString());
                Assert.AreEqual("failure",
                    document.RootElement.GetProperty("status").GetString());
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [TestMethod]
        public async Task RawChildStdoutExactlyMatchesBoundedEvidenceBytes()
        {
            string root = Path.Combine(Path.GetTempPath(),
                "DS4Windows-runner-raw-stdout-test-" +
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string output = Path.Combine(root, "evidence.json");
            try
            {
                RawRunnerResult result = await RunRunnerRawAsync(new[]
                {
                    "--nonce", new string('a', 64),
                    "--output", output,
                });
                Assert.AreEqual(1, result.ExitCode);
                byte[] evidenceBytes = File.ReadAllBytes(output);
                CollectionAssert.AreEqual(evidenceBytes,
                    result.StandardOutput);
                Assert.IsTrue(evidenceBytes.Length <=
                    EvidenceLimits.MaximumJsonBytes);
                Assert.AreEqual((byte)'\n', evidenceBytes[^1]);
                Assert.AreNotEqual((byte)'\r', evidenceBytes[^2]);

                string exactJson = "\"" + new string('x',
                    EvidenceLimits.MaximumJsonBytes - 3) + "\"";
                byte[] exactBoundary =
                    EvidenceWriter.EncodeFinalizedJson(exactJson);
                Assert.AreEqual(EvidenceLimits.MaximumJsonBytes,
                    exactBoundary.Length);
                Assert.AreEqual((byte)'\n', exactBoundary[^1]);
                string overflowJson = "\"" + new string('x',
                    EvidenceLimits.MaximumJsonBytes - 2) + "\"";
                Assert.ThrowsException<InvalidOperationException>(() =>
                    EvidenceWriter.EncodeFinalizedJson(overflowJson));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [TestMethod]
        public async Task EvidenceReservationIsCreateNewAndWriteOnce()
        {
            string root = Path.Combine(Path.GetTempPath(),
                "DS4Windows-runner-create-new-test-" +
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string output = Path.Combine(root, "evidence.json");
            try
            {
                using (EvidenceOutputReservation reservation =
                    EvidenceOutputReservation.Create(output))
                {
                    Assert.ThrowsException<IOException>(() =>
                        EvidenceOutputReservation.Create(output));
                    await reservation.WriteOnceAsync(
                        Encoding.UTF8.GetBytes("first\n"));
                    await Assert.ThrowsExceptionAsync<IOException>(() =>
                        reservation.WriteOnceAsync(
                            Encoding.UTF8.GetBytes("second\n")));
                }
                Assert.AreEqual("first\n", File.ReadAllText(output));
                Assert.ThrowsException<IOException>(() =>
                    EvidenceOutputReservation.Create(output));
                Assert.AreEqual("first\n", File.ReadAllText(output));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [TestMethod]
        public async Task ProbeLaunchRetainsLockedExactFileIdentity()
        {
            string root = Path.Combine(Path.GetTempPath(),
                "DS4Windows-runner-probe-lock-test-" +
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string command = Environment.GetEnvironmentVariable("ComSpec") ??
                Path.Combine(Environment.SystemDirectory, "cmd.exe");
            string probe = Path.Combine(root, "ExactProbe.exe");
            string replacement = Path.Combine(root, "replacement.exe");
            File.Copy(command, probe);
            File.Copy(command, replacement);
            try
            {
                FileBindingEvidence binding = SourceBindings.BindFile(
                    "input-probe", probe);
                using (var executable =
                    new ImmutableProbeExecutable(binding))
                {
                    Assert.ThrowsException<IOException>(() =>
                        File.WriteAllBytes(probe, new byte[] { 1, 2, 3 }));
                    Assert.ThrowsException<UnauthorizedAccessException>(() =>
                        File.Move(replacement, probe, overwrite: true));
                    ProbeResult result = await ProbeRunner.RunAsync(executable,
                        new[] { "/d", "/c", "echo", "EXACT" },
                        TimeSpan.FromSeconds(10), CancellationToken.None);
                    Assert.AreEqual(0, result.ExitCode);
                    StringAssert.Contains(result.StandardOutput, "EXACT");
                    Assert.AreEqual(1,
                        executable.Evidence.LaunchCount);
                    Assert.IsTrue(executable.Evidence.AllLaunchesExact);
                    Assert.AreEqual(
                        executable.Evidence.LockedFileIdentity,
                        executable.Evidence.LastProcessFileIdentity);
                    executable.Revalidate();
                }
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [TestMethod]
        public void InstalledRuntimeMustMatchRunningAndDriverStoreBytes()
        {
            BindingEvidence package = CreatePackageBindings();
            InstalledRuntimeEvidence installed =
                CreateInstalledRuntimeBindings(package);
            InstalledRuntimeBindings.ValidateExactPackage(package,
                installed);
            Assert.IsTrue(installed.ExactPackageMatch);

            installed.Broker.RunningImage.Sha256 = new string('f', 64);
            installed.Broker.RunningImage.ExactMatch = true;
            Assert.ThrowsException<ViiperIdentityException>(() =>
                InstalledRuntimeBindings.ValidateExactPackage(package,
                    installed));

            installed = CreateInstalledRuntimeBindings(package);
            installed.Driver.DriverStoreCat.Sha256 = new string('e', 64);
            installed.Driver.DriverStoreCat.ExactMatch = true;
            Assert.ThrowsException<ViiperIdentityException>(() =>
                InstalledRuntimeBindings.ValidateExactPackage(package,
                    installed));
        }

        [TestMethod]
        public void InstalledRuntimeChangeDuringValidationFailsClosed()
        {
            BindingEvidence package = CreatePackageBindings();
            InstalledRuntimeEvidence initial =
                CreateInstalledRuntimeBindings(package);
            InstalledRuntimeEvidence final =
                CreateInstalledRuntimeBindings(package);
            final.Broker.ProcessId++;
            Assert.ThrowsException<ViiperIdentityException>(() =>
                InstalledRuntimeBindings.RequireUnchanged(initial, final));

            final = CreateInstalledRuntimeBindings(package);
            final.Driver.PublishedInfName = "oem999.inf";
            Assert.ThrowsException<ViiperIdentityException>(() =>
                InstalledRuntimeBindings.RequireUnchanged(initial, final));
        }

        [TestMethod]
        public void NativeRuntimeAndLaptopHarnessKeepAuthoritativeContracts()
        {
            string root = FindRepositoryRoot();
            string native = File.ReadAllText(Path.Combine(root, "tools",
                "DS4Windows.ViiperLiveValidation",
                "InstalledRuntimeBindings.cs"));
            StringAssert.Contains(native, "QueryServiceStatusEx(");
            StringAssert.Contains(native,
                "SetupGetInfDriverStoreLocation(");
            StringAssert.Contains(native,
                "FileShare.Read, 128 * 1024");
            StringAssert.Contains(native,
                "InstalledRuntimeBindings.RequireUnchanged");

            string harness = File.ReadAllText(Path.Combine(root, "tools",
                "DS4Windows.ViiperLiveValidation",
                "Invoke-ViiperDs4WindowsLaptopValidation.ps1"));
            StringAssert.Contains(harness,
                "$IUnderstandThisExercisesLiveControllers");
            StringAssert.Contains(harness,
                "Refusing to overwrite existing evidence or input");
            StringAssert.Contains(harness,
                "DS4WINDOWS_VIIPER_LIVE_VALIDATION_NONCE");
            StringAssert.Contains(harness, "consentNonceSha256");
            StringAssert.Contains(harness, "-RunnerBinding $runnerBinding");
            StringAssert.Contains(harness,
                "New-ViiperLockedFileBinding -Path $output");
            StringAssert.Contains(harness,
                "New-ViiperStdoutEvidenceReceipt");
            StringAssert.Contains(harness,
                "Assert-ViiperStdoutEvidenceContinuity");
            string common = File.ReadAllText(Path.Combine(root, "tools",
                "DS4Windows.ViiperLiveValidation",
                "ViiperLaptopValidation.Common.psm1"));
            StringAssert.Contains(common,
                "Duplicate JSON property: " );
            StringAssert.Contains(common,
                "Evidence timestamps are malformed, inconsistent, or stale.");
            StringAssert.Contains(common,
                "Assert-ViiperEvidenceFileBinding");
            StringAssert.Contains(common,
                "Get-ViiperRequiredProperty $bindings 'installedRuntime'");
            StringAssert.Contains(common,
                "Locked evidence is not byte-identical to the exact child stdout receipt.");
        }

        [TestMethod]
        public void LaptopHarnessAdversarialPowerShellContractPasses()
        {
            string script = Path.Combine(FindRepositoryRoot(), "tools",
                "DS4Windows.ViiperLiveValidation",
                "Test-ViiperLaptopValidationHarness.ps1");
            var start = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (string argument in new[]
            {
                "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script,
            })
            {
                start.ArgumentList.Add(argument);
            }
            using System.Diagnostics.Process process =
                System.Diagnostics.Process.Start(start);
            Assert.IsNotNull(process);
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            Assert.IsTrue(process.WaitForExit(30000),
                "Harness contract test exceeded 30 seconds.");
            Assert.AreEqual(0, process.ExitCode, stderr);
            StringAssert.Contains(stdout, "\"status\":\"pass\"");
        }

        private static BindingEvidence CreatePackageBindings()
        {
            return new BindingEvidence
            {
                DriverPackageVersion = "0.1.0.38",
                PackageArtifacts = new List<FileBindingEvidence>
                {
                    PackageFile("broker", 100, new string('a', 64)),
                    PackageFile("driver-inf", 200, new string('b', 64)),
                    PackageFile("driver-cat", 300, new string('c', 64)),
                    PackageFile("driver-sys", 400, new string('d', 64)),
                },
            };
        }

        private static InstalledRuntimeEvidence CreateInstalledRuntimeBindings(
            BindingEvidence package)
        {
            FileBindingEvidence broker = package.PackageArtifacts.Single(
                file => file.Role == "broker");
            FileBindingEvidence inf = package.PackageArtifacts.Single(
                file => file.Role == "driver-inf");
            FileBindingEvidence cat = package.PackageArtifacts.Single(
                file => file.Role == "driver-cat");
            FileBindingEvidence sys = package.PackageArtifacts.Single(
                file => file.Role == "driver-sys");
            return new InstalledRuntimeEvidence
            {
                Broker = new BrokerServiceEvidence
                {
                    ServiceName = "VIIPERNativeBroker",
                    State = "running",
                    ProcessId = 42,
                    ServiceType = 0x10,
                    StartType = 2,
                    ServiceAccount = "LocalSystem",
                    ConfiguredImagePath = @"C:\Program Files\VIIPER\viiper.exe",
                    RunningImage = ObservedFile(
                        "installed-running-broker",
                        @"C:\Program Files\VIIPER\viiper.exe", broker),
                    ConfiguredImageIsRunningImage = true,
                    ExactPackageMatch = true,
                },
                Driver = new InstalledDriverEvidence
                {
                    HardwareId = @"ROOT\VIIPER\UDE",
                    InstanceId = @"ROOT\VIIPER\0000",
                    ServiceName = "ViiperUde",
                    ServiceState = "running",
                    ServiceType = 1,
                    ServiceStartType = 3,
                    Started = true,
                    ProblemCode = 0,
                    DriverVersion = "0.1.0.38",
                    PublishedInfName = "oem42.inf",
                    PublishedInf = ObservedFile("installed-published-inf",
                        @"C:\Windows\INF\oem42.inf", inf),
                    DriverStoreInf = ObservedFile(
                        "installed-driver-store-inf",
                        @"C:\Windows\System32\DriverStore\FileRepository\viiperude.inf_amd64_test\ViiperUde.inf",
                        inf),
                    DriverStoreCat = ObservedFile(
                        "installed-driver-store-cat",
                        @"C:\Windows\System32\DriverStore\FileRepository\viiperude.inf_amd64_test\ViiperUde.cat",
                        cat),
                    DriverStoreSys = ObservedFile(
                        "installed-driver-store-sys",
                        @"C:\Windows\System32\DriverStore\FileRepository\viiperude.inf_amd64_test\ViiperUde.sys",
                        sys),
                    LoadedServiceImage = ObservedFile(
                        "installed-driver-service-image",
                        @"C:\Windows\System32\DriverStore\FileRepository\viiperude.inf_amd64_test\ViiperUde.sys",
                        sys),
                    ExactPackageMatch = true,
                },
                ExactPackageMatch = true,
            };
        }

        private static FileBindingEvidence PackageFile(string role,
            long length, string sha256) => new()
        {
            Role = role,
            Path = role,
            Length = length,
            Sha256 = sha256,
            ExactMatch = true,
        };

        private static FileBindingEvidence ObservedFile(string role,
            string path, FileBindingEvidence package) => new()
        {
            Role = role,
            Path = path,
            Length = package.Length,
            Sha256 = package.Sha256,
            ExpectedLength = package.Length,
            ExpectedSha256 = package.Sha256,
            ExactMatch = true,
        };

        private static async Task<RawRunnerResult> RunRunnerRawAsync(
            IEnumerable<string> arguments)
        {
            string root = FindRepositoryRoot();
            string configuration = typeof(ViiperLiveValidationRunnerTests)
                .Assembly.Location.Split(Path.DirectorySeparatorChar)
                .Any(part => part == "Debug") ? "Debug" : "Release";
            string runner = Path.Combine(root, "tools",
                "DS4Windows.ViiperLiveValidation", "bin", "x64",
                configuration, "net8.0-windows10.0.19041.0", "win-x64",
                "DS4Windows.ViiperLiveValidation.exe");
            Assert.IsTrue(File.Exists(runner),
                $"The exact runner apphost was not built: '{runner}'.");
            var start = new System.Diagnostics.ProcessStartInfo
            {
                FileName = runner,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            start.Environment.Remove(
                ViiperLiveValidationLease.NonceEnvironmentVariable);
            foreach (string argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }
            using System.Diagnostics.Process process =
                System.Diagnostics.Process.Start(start);
            Assert.IsNotNull(process);
            using var stdout = new MemoryStream();
            Task stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(
                stdout);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                await stdoutTask;
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException(
                    "Raw runner stdout contract test exceeded 30 seconds.");
            }
            return new RawRunnerResult(process.ExitCode, stdout.ToArray(),
                await stderrTask);
        }

        private sealed record RawRunnerResult(int ExitCode,
            byte[] StandardOutput, string StandardError);

        private static string FindRepositoryRoot()
        {
            foreach (string startingPoint in new[]
            {
                Environment.CurrentDirectory,
                AppContext.BaseDirectory,
            })
            {
                DirectoryInfo cursor = new(startingPoint);
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
            return string.Empty;
        }
    }
}
