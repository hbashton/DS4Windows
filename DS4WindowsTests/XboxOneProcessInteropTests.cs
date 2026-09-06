using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using DS4Windows;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public class XboxOneProcessInteropTests
{
    public TestContext TestContext { get; set; }

    [DataTestMethod]
    [TestCategory("PortableProcessInterop")]
    [DataRow("active")]
    [DataRow("cancel")]
    [DataRow("deadline")]
    [DataRow("retained")]
    [DataRow("retained-no-impulse")]
    [DataRow("retained-stop-reject")]
    [DataRow("retained-stop-ack-drop")]
    [DataRow("retained-stop-ack-timeout")]
    public async Task RealGoApiAndDs4ClientRetireExactXboxActivation(string mode)
    {
        string root = Environment.GetEnvironmentVariable("DS4W_XBOX_INTEROP_ROOT");
        string digest = Environment.GetEnvironmentVariable("DS4W_XBOX_INTEROP_SHA256");
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(digest))
            Assert.Inconclusive("Opt-in process interop requires a separately built, hash-pinned Desktop test peer.");

        // Reuse the same test-only context activation as PortableLabContextTests;
        // production authentication must never fall back to an installed key.
        using PortableLabContext lab = PortableLabContext.Create(new[] { "--portable-lab", digest }, root);
        Assert.AreEqual("synthetic-xbox-lifecycle-interop-not-a-deployment-key", File.ReadAllText(lab.KeyPath).Trim());
        FieldInfo currentField = typeof(PortableLabContext).GetField("current", BindingFlags.Static | BindingFlags.NonPublic);
        object previousContext = currentField.GetValue(null);
        using var peer = new Process
        {
            StartInfo = new ProcessStartInfo(lab.ViiperPath)
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }
        };
        peer.StartInfo.ArgumentList.Add("-test.run=^TestXboxDS4WindowsInteropPeer$");
        peer.StartInfo.ArgumentList.Add("-test.v");
        peer.StartInfo.ArgumentList.Add("-test.timeout=35s");
        peer.StartInfo.Environment["DS4W_XBOX_INTEROP_PEER"] = "1";
        peer.StartInfo.Environment["DS4W_XBOX_INTEROP_MODE"] = mode;
        ViiperDeviceStream stream = null;
        Task<ViiperDeviceStream> creation = null;
        Task activation = Task.CompletedTask;
        Task disposal = Task.CompletedTask;
        using XboxOneInteropFeedbackTarget feedback = mode.StartsWith("retained", StringComparison.Ordinal)
            ? new(mapImpulse: mode != "retained-no-impulse",
                terminalFailure: mode == "retained-stop-reject" ? XboxOneInteropTerminalFailure.RejectWrite :
                    mode == "retained-stop-ack-drop" ? XboxOneInteropTerminalFailure.DropAcknowledgement :
                    mode == "retained-stop-ack-timeout" ? XboxOneInteropTerminalFailure.WithholdAcknowledgement :
                    XboxOneInteropTerminalFailure.None) : null;
        bool started = false;
        Task<string> errors = Task.FromResult(string.Empty);
        currentField.SetValue(null, lab);
        try
        {
            started = peer.Start();
            Assert.IsTrue(started);
            errors = peer.StandardError.ReadToEndAsync();
            string ready = await ReadMarker(peer.StandardOutput, "DS4W_XBOX_INTEROP_READY ");
            int port = int.Parse(ready["DS4W_XBOX_INTEROP_READY ".Length..], CultureInfo.InvariantCulture);
            Assert.IsTrue(port is > 0 and <= 65535);
            Assert.AreNotEqual(3241, port);
            Assert.AreNotEqual(3242, port);
            var client = new ViiperClient("127.0.0.1", port); // Real auth, not an injected substitute.
            creation = Task.Run(() => client.CreateAuthorizedXboxOneDeviceAndOpenStream(Request(feedback?.OwnershipEpoch)));
            stream = await creation.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsTrue(stream.IsXboxOneBrokerEnabled, "The real Go ConsumerReady ACK must be accepted.");
            Assert.AreEqual(-1, stream.UsbipPort);
            if (mode == "retained-stop-ack-timeout")
                Assert.AreEqual(6_000, stream.DeviceLifetime.XboxOneRegistration.RemovalTimeoutMilliseconds,
                    "The real advertised server deadline must fit inside this case's observation bound.");
            feedback?.Start(stream);
            activation = Task.Run(() => client.ActivateAuthorizedXboxOneDevice(stream));
            await ReadMarker(peer.StandardOutput, "DS4W_XBOX_INTEROP_ATTACH");
            if (mode == "active" || feedback != null)
            {
                await activation.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.AreEqual(31070, stream.UsbipPort);
                Assert.IsTrue(ViiperUsbipPortManager.IsActivePort(31070));
            }
            else if (mode == "deadline")
            {
                XboxOneManagementException rejected =
                    await Assert.ThrowsExceptionAsync<XboxOneManagementException>(async () =>
                    await activation.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.AreEqual(XboxOneManagementOperation.ActivatePersona, rejected.Operation);
                Assert.AreEqual(409, rejected.Status,
                    "The real Go peer rejects this expired native activation with its conflict response.");
                Assert.AreEqual("VIIPER rejected Xbox One activation (API status 409).", rejected.Message);
                Assert.IsNull(rejected.InnerException, "Remote error details must not escape through diagnostics.");
                Assert.AreEqual(-1, stream.UsbipPort, "A late native success cannot become a live client port.");
            }
            if (feedback != null)
            {
                byte[] packet = new byte[XboxOneEgressState.WireSize];
                XboxOneEgressState.FromLegacyMappedState(new DS4State
                {
                    Cross = true, L2 = 255, LX = 255, LY = 128, RX = 128, RY = 128
                }, -1).BuildInto(packet);
                await Task.Run(() => stream.WriteXboxOneInputAndWaitForAck(packet, packet.Length))
                    .WaitAsync(TimeSpan.FromSeconds(5));
                await peer.StandardInput.WriteLineAsync("INPUT");
                await peer.StandardInput.FlushAsync();
                await ReadMarker(peer.StandardOutput, "DS4W_XBOX_INTEROP_FEEDBACK_DONE");
                await feedback.AssertFourActuatorsAsync();
                XboxOneEgressState.Neutral.BuildInto(packet);
                await Task.Run(() => stream.WriteXboxOneInputAndWaitForAck(packet, packet.Length))
                    .WaitAsync(TimeSpan.FromSeconds(5));
                await peer.StandardInput.WriteLineAsync("RELEASE");
                await peer.StandardInput.FlushAsync();
                await ReadMarker(peer.StandardOutput, "DS4W_XBOX_INTEROP_RELEASED");
            }
            Assert.IsFalse(stream.IsTransportClosed);
            feedback?.BeginClose();
            disposal = Task.Run(stream.Dispose);
            if (feedback != null)
            {
                Assert.IsTrue(feedback.StopEntered.Wait(TimeSpan.FromSeconds(5)),
                    $"No canonical terminal Stop: removalCompleted={disposal.IsCompleted}, closed={stream.IsTransportClosed}, {feedback.Diagnostics}");
                Assert.IsFalse(disposal.IsCompleted, "Exact removal cannot finish before canonical Stop delivery/ACK.");
                Assert.IsFalse(stream.IsTransportClosed, "The broker must remain available for its exact Stop ACK.");
                Assert.AreEqual(4, feedback.EffectAcknowledgements);
                Assert.AreEqual(0, feedback.StopAcknowledgements);
                feedback.ReleaseStop.Set();
            }
            await disposal.WaitAsync(TimeSpan.FromSeconds(10));
            if (feedback != null)
            {
                if (feedback.ExpectsTerminalFailure) await feedback.AssertTerminalFailureAsync();
                else await feedback.AssertTerminalAsync();
            }
            if (mode == "cancel")
            {
                await Assert.ThrowsExceptionAsync<IOException>(async () =>
                    await activation.WaitAsync(TimeSpan.FromSeconds(2)));
                Assert.AreEqual(-1, stream.UsbipPort);
            }
            Assert.IsTrue(stream.IsTransportClosed);
            Assert.IsFalse(ViiperUsbipPortManager.IsActivePort(31070));
            // Native Windows ports were never created; only this process's
            // managed lease is inspected. Server asserts actual registry removal.
            await peer.StandardInput.WriteLineAsync("DONE");
            await peer.StandardInput.FlushAsync();
            string passed = await ReadMarker(peer.StandardOutput, "DS4W_XBOX_INTEROP_PASS ");
            Assert.AreEqual("DS4W_XBOX_INTEROP_PASS " + mode, passed);
            string output = await peer.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await peer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(0, peer.ExitCode, await errors);
            TestContext.WriteLine(passed + "\n" + output);
        }
        finally
        {
            try
            {
                if (creation != null)
                {
                    // WaitAsync bounds the assertion, not the underlying work.
                    // Close the owned peer on a startup timeout and join creation
                    // (including exact rollback) before restoring the key context.
                    // Production connect/management operations remain bounded;
                    // another observation timeout here would abandon that work.
                    try
                    {
                        if (!creation.IsCompleted && started && !peer.HasExited)
                            peer.Kill(entireProcessTree: false);
                    }
                    finally
                    {
                        try { stream ??= await creation; }
                        catch (IOException) { } // Startup failure was already reported.
                    }
                }
                feedback?.BeginClose();
                feedback?.ReleaseStop.Set();
                stream?.Dispose();
                await disposal.WaitAsync(TimeSpan.FromSeconds(10));
                try { await activation.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (IOException) { }
            }
            finally
            {
                try
                {
                    if (started && !peer.HasExited)
                    {
                        peer.Kill(entireProcessTree: false); // Only the exact test child we started.
                        await peer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    if (started)
                    {
                        TestContext.WriteLine(await peer.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5)));
                        TestContext.WriteLine(await errors.WaitAsync(TimeSpan.FromSeconds(5)));
                    }
                }
                finally
                {
                    // A failed observation deadline does not terminate cleanup
                    // or activation. After closing the exact child, join both
                    // owned operations before allowing the auth context to change.
                    try
                    {
                        try { await disposal; }
                        finally
                        {
                            try { await activation; }
                            catch (IOException) { }
                        }
                    }
                    finally { currentField.SetValue(null, previousContext); }
                }
            }
        }
    }

    private async Task<string> ReadMarker(StreamReader output, string marker)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (await output.ReadLineAsync(timeout.Token) is { } line)
        {
            if (line.StartsWith(marker, StringComparison.Ordinal)) return line;
            TestContext.WriteLine(line);
            if (line.StartsWith("--- FAIL:", StringComparison.Ordinal))
                throw new IOException("The isolated Go interop assertion failed: " + line);
        }
        throw new IOException("The isolated test peer exited before " + marker.TrimEnd());
    }

    private static XboxOneAuthorizedCreateRequestV1 Request(ulong? ownershipEpoch = null) =>
        XboxOneAuthorizedCreateRequestV1.CreateForFeedbackTarget(new XboxOneAuthorizedPersonaConfiguration
        {
            Version = 1,
            IdentityAuthorizationGranted = true,
            Identity = new XboxOneAuthorizedIdentity
            {
                VendorId = 0xf00d, ProductId = 0xbeef, DeviceReleaseBcd = 0x0102,
                DeviceId = 0x0000fffb01020304, FirmwareMajor = 1,
            },
            Usb = new XboxOneAuthorizedUsbConfiguration { MaxPower2mA = 50, OutIntervalMs = 4, InIntervalMs = 4 },
            Strings = new XboxOneAuthorizedIdentityStrings
            {
                Manufacturer = "VIIPER test", Product = "Isolated lifecycle test only",
                Serial = "0000fffb01020304a1b2c3d4e5f60708"
            },
        }, XboxOneInteropFeedbackTarget.DeviceGeneration, XboxOneInteropFeedbackTarget.TransportGeneration,
            ownershipEpoch ?? 0x8877665544332299);
}
