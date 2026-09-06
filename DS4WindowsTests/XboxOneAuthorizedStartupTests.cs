using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DS4Windows;

namespace DS4WindowsTests;

[TestClass]
public sealed class XboxOneAuthorizedStartupTests
{
    [DataTestMethod]
    [DataRow((int)XboxOneManagementOperation.CreateBus, "bus creation")]
    [DataRow((int)XboxOneManagementOperation.CreatePersona, "persona creation")]
    [DataRow((int)XboxOneManagementOperation.ActivatePersona, "activation")]
    [DataRow((int)XboxOneManagementOperation.RemovePersona, "removal")]
    public void ManagementErrorPreservesOnlyLocalStageAndValidatedStatus(
        int operationValue, string expectedStage)
    {
        var operation = (XboxOneManagementOperation)operationValue;
        using JsonDocument error = JsonDocument.Parse(
            "{\"status\":409,\"title\":\"private-token-secret\",\"detail\":\"request-and-capability-secret\"}");
        var rejected = Assert.ThrowsException<XboxOneManagementException>(() =>
            ViiperClient.ThrowIfXboxOneManagementError(error.RootElement, operation));
        Assert.AreEqual(operation, rejected.Operation);
        Assert.AreEqual(409, rejected.Status);
        StringAssert.Contains(rejected.Message, expectedStage);
        StringAssert.Contains(rejected.Message, "409");
        Assert.IsFalse(rejected.ToString().Contains("private-token-secret"));
        Assert.IsFalse(rejected.ToString().Contains("request-and-capability-secret"));
        Assert.IsNull(rejected.InnerException);
    }

    [DataTestMethod]
    [DataRow("{\"status\":\"secret\"}")]
    [DataRow("{\"status\":200}")]
    [DataRow("{\"status\":9999999999999999999}")]
    [DataRow("{\"status\":409,\"status\":500}")]
    [DataRow("{\"detail\":\"secret\"}")]
    public void MalformedErrorStatusIsNotEchoedOrAcceptedAsSuccess(string json)
    {
        using JsonDocument error = JsonDocument.Parse(json);
        var rejected = Assert.ThrowsException<XboxOneManagementException>(() =>
            ViiperClient.ThrowIfXboxOneManagementError(error.RootElement,
                XboxOneManagementOperation.ActivatePersona));
        Assert.AreEqual(0, rejected.Status);
        Assert.AreEqual("VIIPER rejected Xbox One activation (invalid API error response).", rejected.Message);
        Assert.IsNull(rejected.InnerException);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CreateStageApiFailureIsSanitizedAndNeverAuthorizesCleanup(bool afterBusCreation)
    {
        using var fixture = new StartupFixture();
        Task<ViiperDeviceStream> creation = Task.Run(() =>
            fixture.Client.CreateAuthorizedXboxOneDeviceAndOpenStream(Request()));
        if (afterBusCreation)
            await fixture.Reply("bus/create", "{\"busId\":42}");
        await fixture.Reply(afterBusCreation ? "bus/42/add-authorized-xboxone" : "bus/create",
            "{\"status\":403,\"title\":\"secret\",\"detail\":\"secret\"}");
        var rejected = await Assert.ThrowsExceptionAsync<XboxOneManagementException>(async () =>
            await creation.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(afterBusCreation ? XboxOneManagementOperation.CreatePersona :
            XboxOneManagementOperation.CreateBus, rejected.Operation);
        Assert.AreEqual(403, rejected.Status);
        Assert.IsFalse(rejected.ToString().Contains("secret"));
        Assert.IsNull(rejected.InnerException);
        Assert.IsFalse(fixture.Listener.Pending());
    }

    [TestMethod]
    public async Task RemovalApiFailureKeepsSanitizedOperationWithoutNumericFallback()
    {
        using var fixture = new StartupFixture();
        Task<bool> removal = Task.Run(() => fixture.Client.RemoveAuthorizedXboxOneRegistration(Registration()));
        await fixture.Reply("bus/42/7/remove-authorized-xboxone",
            "{\"status\":409,\"detail\":\"secret\"}", requireAuthority: true);
        var rejected = await Assert.ThrowsExceptionAsync<XboxOneManagementException>(async () =>
            await removal.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(XboxOneManagementOperation.RemovePersona, rejected.Operation);
        Assert.AreEqual(409, rejected.Status);
        Assert.IsFalse(rejected.ToString().Contains("secret"));
        Assert.IsNull(rejected.InnerException);
        Assert.IsFalse(fixture.Listener.Pending());
    }

    [TestMethod]
    public async Task CreateOpenActivateAndDisposeUseOnlyCapturedAuthority()
    {
        using var fixture = new StartupFixture();
        ViiperDeviceStream stream = await fixture.Create();
        Assert.AreEqual(XboxOneAuthorizedRegistrationTests.Alias,
            stream.DeviceLifetime.XboxOneRegistration.UsbipBusId);
        Task activation = Task.Run(() => fixture.Client.ActivateAuthorizedXboxOneDevice(stream));
        await fixture.Reply("bus/42/7/activate-authorized-xboxone",
            ActivationJson(), requireAuthority: true);
        await activation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(31007, stream.UsbipPort);
        Assert.IsTrue(ViiperUsbipPortManager.IsActivePort(31007));

        Task disposal = Task.Run(stream.Dispose);
        await fixture.Reply("bus/42/7/remove-authorized-xboxone",
            "{\"version\":1,\"removed\":true}", requireAuthority: true);
        await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        stream.Dispose();
        Assert.IsTrue(stream.IsTransportClosed);
        Assert.IsFalse(ViiperUsbipPortManager.IsActivePort(31007));
        Assert.IsFalse(fixture.Listener.Pending(), "No port/device/bus fallback or duplicate removal.");
    }

    [DataTestMethod]
    [DataRow("missing-token")]
    [DataRow("missing-alias")]
    [DataRow("foreign-bus")]
    [DataRow("missing-budget")]
    [DataRow("empty")]
    public async Task InvalidCreateReceiptCannotAuthorizeAnyCleanup(string failure)
    {
        using var fixture = new StartupFixture();
        Task<ViiperDeviceStream> creation = Task.Run(() =>
            fixture.Client.CreateAuthorizedXboxOneDeviceAndOpenStream(Request()));
        await fixture.Reply("bus/create", "{\"busId\":42}");
        string receipt = XboxOneAuthorizedRegistrationTests.ReceiptJson();
        receipt = failure switch
        {
            "missing-token" => receipt.Replace("\"removalToken\"", "\"otherToken\""),
            "missing-alias" => receipt.Replace("\"usbipBusId\"", "\"otherAlias\""),
            "foreign-bus" => receipt.Replace("\"busId\":42", "\"busId\":43"),
            "missing-budget" => receipt.Replace("\"removalTimeoutMilliseconds\"", "\"otherBudget\""),
            _ => ""
        };
        await fixture.Reply("bus/42/add-authorized-xboxone", receipt);
        await Assert.ThrowsExceptionAsync<IOException>(async () =>
            await creation.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsFalse(fixture.Listener.Pending(), "An unverified receipt grants no removal authority.");
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow("{}")]
    [DataRow("null")]
    [DataRow("[]")]
    [DataRow("{\"busId\":0}")]
    [DataRow("{\"busId\":65536}")]
    [DataRow("{\"busId\":-1}")]
    [DataRow("{\"busId\":\"42\"}")]
    [DataRow("{\"busId\":42,\"busId\":43}")]
    [DataRow("{\"busId\":42,\"\\u0062usId\":42}")]
    [DataRow("{\"BusId\":42}")]
    [DataRow("{\"busId\":42,\"extra\":1}")]
    public async Task InvalidBusReceiptCannotCreateOrRemoveDevice(string receipt)
    {
        using var fixture = new StartupFixture();
        Task<ViiperDeviceStream> creation = Task.Run(() =>
            fixture.Client.CreateAuthorizedXboxOneDeviceAndOpenStream(Request()));
        await fixture.Reply("bus/create", receipt);
        await Assert.ThrowsExceptionAsync<IOException>(async () =>
            await creation.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsFalse(fixture.Listener.Pending());
    }

    [DataTestMethod]
    [DataRow("lost")]
    [DataRow("foreign-alias")]
    [DataRow("duplicate-port")]
    [DataRow("conflict")]
    [DataRow("oversize")]
    public async Task FailedActivationRetainsBrokerForExactRollbackOnly(string failure)
    {
        using var fixture = new StartupFixture();
        ViiperDeviceStream stream = await fixture.Create();
        string response = failure switch
        {
            "foreign-alias" => ActivationJson().Replace(
                XboxOneAuthorizedRegistrationTests.Alias,
                XboxOneAuthorizedRegistrationTests.Alias[..^1] + "e"),
            "duplicate-port" => ActivationJson().Insert(1, "\"usbipPort\":31008,"),
            "conflict" => "{\"status\":409,\"title\":\"Conflict\"}",
            "oversize" => new string(' ', 1024),
            _ => ""
        };
        Task activation = Task.Run(() => fixture.Client.ActivateAuthorizedXboxOneDevice(stream));
        await fixture.Reply("bus/42/7/activate-authorized-xboxone", response, requireAuthority: true);
        if (failure == "conflict")
        {
            var rejected = await Assert.ThrowsExceptionAsync<XboxOneManagementException>(async () =>
                await activation.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(XboxOneManagementOperation.ActivatePersona, rejected.Operation);
            Assert.AreEqual(409, rejected.Status);
            Assert.IsNull(rejected.InnerException);
        }
        else
            await Assert.ThrowsExceptionAsync<IOException>(async () =>
                await activation.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(-1, stream.UsbipPort);
        Assert.IsFalse(stream.IsTransportClosed);
        Assert.IsFalse(ViiperUsbipPortManager.IsActivePort(31007));
        // Connect's catch invokes Disconnect; emulate its exact lifetime-first
        // rollback with the feedback channel still available for terminal Stop.
        Task rollback = Task.Run(stream.Dispose);
        await fixture.Reply("bus/42/7/remove-authorized-xboxone",
            "{\"version\":1,\"removed\":false}", requireAuthority: true);
        await rollback.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(stream.IsTransportClosed);
        Assert.IsFalse(fixture.Listener.Pending());
    }

    [TestMethod]
    public async Task ActivationResponseAfterDisposalCannotBindOrRemoveAgain()
    {
        using var fixture = new StartupFixture();
        ViiperDeviceStream stream = await fixture.Create();
        Task activation = Task.Run(() => fixture.Client.ActivateAuthorizedXboxOneDevice(stream));
        using TcpClient pending = await fixture.Accept();
        AssertAuthority(await ReadRequest(pending.GetStream()),
            "bus/42/7/activate-authorized-xboxone");
        Task disposal = Task.Run(stream.Dispose);
        await fixture.Reply("bus/42/7/remove-authorized-xboxone",
            "{\"version\":1,\"removed\":true}", requireAuthority: true);
        await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsExceptionAsync<IOException>(async () =>
            await activation.WaitAsync(TimeSpan.FromSeconds(5)));
        // A peer may still try to send its late success. Local retirement
        // already closed this management connection, so receipt is impossible.
        try
        {
            await pending.GetStream().WriteAsync(Encoding.UTF8.GetBytes(ActivationJson()));
            pending.Client.Shutdown(SocketShutdown.Send);
        }
        catch (Exception error) when (error is IOException || error is SocketException) { }
        Assert.AreEqual(-1, stream.UsbipPort);
        Assert.IsFalse(ViiperUsbipPortManager.IsActivePort(31007));
        Assert.IsFalse(fixture.Listener.Pending());
    }

    [TestMethod]
    public void ExactRegistrationCannotReopenByNumericAddress()
    {
        using var fixture = new StartupFixture();
        using var lifetime = new ViiperVirtualDeviceLifetime(Registration(), _ => true);
        Assert.ThrowsException<InvalidOperationException>(() =>
            fixture.Client.OpenExistingDeviceStream(42, "7", -1, lifetime));
        Assert.IsFalse(fixture.Listener.Pending());
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DisposalCancelsPendingActivationButPreservesBrokerUntilExactCleanup(
        bool duringAuthentication)
    {
        int authenticationCalls = 0;
        var authenticationEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new StartupFixture(transport =>
        {
            if (Interlocked.Increment(ref authenticationCalls) == 4 && duringAuthentication)
            {
                authenticationEntered.TrySetResult(true);
                _ = transport.ReadByte(); // Held until cancellation closes this exact socket.
            }
            return transport;
        });
        ViiperDeviceStream stream = await fixture.Create();
        Task activation = Task.Run(() => fixture.Client.ActivateAuthorizedXboxOneDevice(stream));
        using TcpClient pending = await fixture.Accept();
        if (duringAuthentication)
            await authenticationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        else
            AssertAuthority(await ReadRequest(pending.GetStream()),
                "bus/42/7/activate-authorized-xboxone");
        Task disposal = Task.Run(stream.Dispose);
        using TcpClient cleanup = await fixture.Accept();
        AssertAuthority(await ReadRequest(cleanup.GetStream()),
            "bus/42/7/remove-authorized-xboxone");
        try
        {
            Assert.IsFalse(stream.IsTransportClosed,
                "Exact removal must still be able to receive its broker Stop acknowledgement.");
            Assert.AreSame(activation, await Task.WhenAny(activation, Task.Delay(1000)),
                "Retiring the exact lifetime must cancel its waiting management request, not wait for a reply.");
            await Assert.ThrowsExceptionAsync<IOException>(async () => await activation);
            Assert.IsTrue(await Task.Run(() =>
                ViiperUsbipPortManager.WithNativePortMutationLock(() => true))
                .WaitAsync(TimeSpan.FromSeconds(2)),
                "The canceled request must release the shared attach gate before cleanup replies.");
            Assert.AreEqual(0, await pending.GetStream().ReadAsync(new byte[1])
                .AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.AreEqual(-1, stream.UsbipPort);
            Assert.IsFalse(ViiperUsbipPortManager.IsActivePort(31007));
            Assert.IsFalse(disposal.IsCompleted);
        }
        finally
        {
            // Also release the old, defective implementation after a red
            // assertion, so no test worker survives the fixture.
            pending.Dispose();
            await cleanup.GetStream().WriteAsync("{\"version\":1,\"removed\":true}"u8.ToArray());
            cleanup.Client.Shutdown(SocketShutdown.Send);
            await disposal.WaitAsync(TimeSpan.FromSeconds(5));
            try { await activation.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (IOException) { }
        }
        Assert.IsTrue(stream.IsTransportClosed);
        Assert.IsFalse(fixture.Listener.Pending());
    }

    [TestMethod]
    public void ActivationRequestRetirementCannotCancelSuccessorRequest()
    {
        int removals = 0;
        using var lifetime = new ViiperVirtualDeviceLifetime(Registration(), _ => { removals++; return true; });
        XboxOneActivationRequest old = lifetime.BeginXboxOneActivationRequest();
        Assert.ThrowsException<InvalidOperationException>(() => lifetime.BeginXboxOneActivationRequest());
        old.Dispose();
        using XboxOneActivationRequest current = lifetime.BeginXboxOneActivationRequest();
        old.Cancel();
        old.Dispose();
        Assert.IsFalse(current.Token.IsCancellationRequested);
        Assert.ThrowsException<InvalidOperationException>(() => lifetime.BeginXboxOneActivationRequest());
        lifetime.Dispose();
        Assert.IsTrue(current.Token.IsCancellationRequested);
        Assert.AreEqual(1, removals);
        Assert.ThrowsException<ObjectDisposedException>(() => lifetime.BeginXboxOneActivationRequest());
    }

    [TestMethod]
    public async Task RetiredActivationCanLeaveAttachQueueWithoutWaitingForAnotherController()
    {
        using var fixture = new StartupFixture();
        ViiperDeviceStream stream = await fixture.Create();
        using var gateHeld = new ManualResetEventSlim();
        using var releaseOwner = new ManualResetEventSlim();
        Task owner = Task.Run(() => ViiperUsbipPortManager.WithNativePortMutationLock(() =>
        {
            gateHeld.Set();
            releaseOwner.Wait();
            return true;
        }));
        var activation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try { fixture.Client.ActivateAuthorizedXboxOneDevice(stream); activation.TrySetResult(true); }
            catch (Exception error) { activation.TrySetException(error); }
        }) { IsBackground = true };
        Task disposal = Task.CompletedTask;
        try
        {
            Assert.IsTrue(gateHeld.Wait(TimeSpan.FromSeconds(5)));
            worker.Start();
            Assert.IsTrue(SpinWait.SpinUntil(() =>
                (worker.ThreadState & ThreadState.WaitSleepJoin) != 0,
                TimeSpan.FromSeconds(5)), "Activation must actually be waiting behind the other owner.");
            Assert.IsFalse(fixture.Listener.Pending(), "No native activation request may escape the attach gate.");
            disposal = Task.Run(stream.Dispose);
            await fixture.Reply("bus/42/7/remove-authorized-xboxone",
                "{\"version\":1,\"removed\":true}", requireAuthority: true);
            await disposal.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreSame(activation.Task, await Task.WhenAny(activation.Task, Task.Delay(1000)),
                "A canceled waiter must exit without waiting for the unrelated controller's attach.");
            await Assert.ThrowsExceptionAsync<IOException>(async () => await activation.Task);
            Assert.IsFalse(owner.IsCompleted, "Canceling a waiter cannot release or cancel the actual owner.");
            Assert.IsFalse(fixture.Listener.Pending(), "Cancellation must not submit a late activation.");
        }
        finally
        {
            releaseOwner.Set();
            await owner.WaitAsync(TimeSpan.FromSeconds(5));
            await disposal.WaitAsync(TimeSpan.FromSeconds(5));
            if ((worker.ThreadState & ThreadState.Unstarted) == 0)
            {
                Assert.IsTrue(worker.Join(TimeSpan.FromSeconds(5)));
                try { await activation.Task; }
                catch (Exception error) when (error is IOException || error is ObjectDisposedException) { }
            }
        }
    }

    [TestMethod]
    public async Task ActivationRequestCompletionJoinsCancellationBeforeSourceDisposal()
    {
        int removals = 0;
        using var lifetime = new ViiperVirtualDeviceLifetime(Registration(), _ => { removals++; return true; });
        using XboxOneActivationRequest request = lifetime.BeginXboxOneActivationRequest();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using CancellationTokenRegistration callback = request.Token.Register(() =>
        {
            entered.Set();
            release.Wait();
        });
        Task retirement = Task.Run(lifetime.Dispose);
        Task completion = Task.CompletedTask;
        try
        {
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
            completion = Task.Run(request.Dispose);
            Assert.AreNotSame(completion, await Task.WhenAny(completion, Task.Delay(100)),
                "The source cannot be disposed while its retirement callback is running.");
            Assert.AreEqual(0, Volatile.Read(ref removals));
        }
        finally
        {
            release.Set();
            await Task.WhenAll(retirement, completion).WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.AreEqual(1, removals);
        request.Cancel(); // A stale retirement snapshot after completion is inert.
    }

    [TestMethod]
    public async Task BrokerReadyFailureRollsBackOnlyVerifiedRegistration()
    {
        using var fixture = new StartupFixture();
        Task<ViiperDeviceStream> creation = Task.Run(() =>
            fixture.Client.CreateAuthorizedXboxOneDeviceAndOpenStream(Request()));
        await fixture.Reply("bus/create", "{\"busId\":42}");
        await fixture.Reply("bus/42/add-authorized-xboxone",
            XboxOneAuthorizedRegistrationTests.ReceiptJson());
        using TcpClient broker = await fixture.Accept();
        AssertAuthority(await ReadRequest(broker.GetStream()),
            "bus/42/7/stream-authorized-xboxone");
        await broker.GetStream().ReadExactlyAsync(new byte[16]);
        broker.Client.Shutdown(SocketShutdown.Send);
        await fixture.Reply("bus/42/7/remove-authorized-xboxone",
            "{\"version\":1,\"removed\":false}", requireAuthority: true);
        await Assert.ThrowsExceptionAsync<IOException>(async () =>
            await creation.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsFalse(fixture.Listener.Pending());
    }

    [TestMethod]
    public async Task BrokerAuthenticationFailureClosesTransportAndUsesExactRollback()
    {
        int authenticationCalls = 0;
        using var fixture = new StartupFixture(stream =>
        {
            if (Interlocked.Increment(ref authenticationCalls) == 3)
                throw new IOException("Simulated broker authentication failure.");
            return stream;
        });
        Task<ViiperDeviceStream> creation = Task.Run(() =>
            fixture.Client.CreateAuthorizedXboxOneDeviceAndOpenStream(Request()));
        await fixture.Reply("bus/create", "{\"busId\":42}");
        await fixture.Reply("bus/42/add-authorized-xboxone",
            XboxOneAuthorizedRegistrationTests.ReceiptJson());
        using TcpClient broker = await fixture.Accept();
        Assert.AreEqual(0, await broker.GetStream().ReadAsync(new byte[1])
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        await fixture.Reply("bus/42/7/remove-authorized-xboxone",
            "{\"version\":1,\"removed\":true}", requireAuthority: true);
        await Assert.ThrowsExceptionAsync<IOException>(async () =>
            await creation.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(4, authenticationCalls);
        Assert.IsFalse(fixture.Listener.Pending());
    }

    [TestMethod]
    public async Task DribblingConsumerReadyCannotExtendAbsoluteStartupDeadline()
    {
        using var fixture = new StartupFixture();
        Task<ViiperDeviceStream> creation = Task.Run(() =>
            fixture.Client.CreateAuthorizedXboxOneDeviceAndOpenStream(Request()));
        await fixture.Reply("bus/create", "{\"busId\":42}");
        await fixture.Reply("bus/42/add-authorized-xboxone",
            XboxOneAuthorizedRegistrationTests.ReceiptJson());
        using TcpClient broker = await fixture.Accept();
        AssertAuthority(await ReadRequest(broker.GetStream()),
            "bus/42/7/stream-authorized-xboxone");
        byte[] ready = new byte[16];
        await broker.GetStream().ReadExactlyAsync(ready).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        ready[5] = 0x81;
        using var cancelWrites = new CancellationTokenSource();
        Task producer = Task.Run(async () =>
        {
            try
            {
                // Every read makes progress well within the socket timeout,
                // but the valid frame cannot finish before the absolute bound.
                foreach (byte value in ready)
                {
                    await broker.GetStream().WriteAsync(new[] { value }, cancelWrites.Token);
                    await Task.Delay(600, cancelWrites.Token);
                }
            }
            catch (Exception error) when (error is IOException ||
                error is OperationCanceledException || error is ObjectDisposedException)
            { }
        });
        try
        {
            await fixture.Reply("bus/42/7/remove-authorized-xboxone",
                "{\"version\":1,\"removed\":true}", requireAuthority: true,
                acceptTimeout: TimeSpan.FromSeconds(12));
            await Assert.ThrowsExceptionAsync<IOException>(async () =>
                await creation.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.IsFalse(fixture.Listener.Pending());
        }
        finally
        {
            cancelWrites.Cancel();
            await producer.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [TestMethod]
    public async Task ConcurrentStreamDisposeJoinsExactCleanupBeforeClosingBroker()
    {
        using var fixture = new StartupFixture();
        ViiperDeviceStream stream = await fixture.Create();
        Task first = Task.Run(stream.DisposeDeviceLifetimeBeforeTransportClose);
        using TcpClient cleanup = await fixture.Accept();
        AssertAuthority(await ReadRequest(cleanup.GetStream()),
            "bus/42/7/remove-authorized-xboxone");
        using var entered = new ManualResetEventSlim();
        Task second = Task.Run(() => { entered.Set(); stream.Dispose(); });
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
        Assert.AreNotSame(second, await Task.WhenAny(second, Task.Delay(100)),
            "Concurrent disposal must join the first Stop/cleanup request.");
        Assert.IsFalse(stream.IsTransportClosed);
        await cleanup.GetStream().WriteAsync(
            "{\"version\":1,\"removed\":true}"u8.ToArray());
        cleanup.Client.Shutdown(SocketShutdown.Send);
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(stream.IsTransportClosed);
        Assert.IsFalse(fixture.Listener.Pending());
    }

    [TestMethod]
    public void OldPortLeaseCannotUnregisterOrDetachItsSuccessor()
    {
        const int port = 31008;
        using ViiperXboxOnePortLease old = ViiperUsbipPortManager.RegisterXboxOnePort(
            port, XboxOneAuthorizedRegistrationTests.Alias);
        using ViiperXboxOnePortLease successor = ViiperUsbipPortManager.RegisterXboxOnePort(
            port, XboxOneAuthorizedRegistrationTests.Alias[..^1] + "e");
        old.Dispose();
        ViiperUsbipPortManager.UnregisterActivePort(port);
        // This returns before any native command because the port is protected.
        ViiperUsbipPortManager.DetachRegisteredPort(port, "stale test lifetime");
        Assert.IsTrue(ViiperUsbipPortManager.IsActivePort(port));
        successor.Dispose();
        Assert.IsFalse(ViiperUsbipPortManager.IsActivePort(port));
    }

    [DataTestMethod]
    [DataRow("x1-aaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [DataRow("X1-AAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [DataRow("x1-malformed")]
    public void ProtectedNamespaceNeverEntersLegacyPortCleanup(string alias)
    {
        var port = new ViiperUsbipPortManager.UsbipPortBlock(9,
            "Port 9: <Port in Use>\n    -> usbip://127.0.0.1:3241/" + alias + "\n");
        Assert.IsFalse(ViiperUsbipPortManager.IsDs4WindowsOwnedLocalPort(port, null));
        Assert.IsFalse(ViiperUsbipPortManager.IsDs4WindowsOwnedLocalPort(port, alias));
    }

    [TestMethod]
    public void PortBindingRequiresSameAliasAndLiveExactLifetime()
    {
        var registration = Registration();
        int removals = 0;
        using var lifetime = new ViiperVirtualDeviceLifetime(registration, _ => { removals++; return true; });
        using var foreign = ViiperUsbipPortManager.RegisterXboxOnePort(31009,
            XboxOneAuthorizedRegistrationTests.Alias[..^1] + "e");
        Assert.ThrowsException<InvalidOperationException>(() => lifetime.BindXboxOnePort(foreign));
        Assert.ThrowsException<InvalidOperationException>(() => lifetime.BindUsbipPort(31009));
        lifetime.Dispose();
        using var matching = ViiperUsbipPortManager.RegisterXboxOnePort(31010, registration.UsbipBusId);
        Assert.ThrowsException<ObjectDisposedException>(() => lifetime.BindXboxOnePort(matching));
        lifetime.Dispose();
        Assert.AreEqual(1, removals);
    }

    private static XboxOneAuthorizedRegistrationV1 Registration()
    {
        using JsonDocument receipt = JsonDocument.Parse(XboxOneAuthorizedRegistrationTests.ReceiptJson());
        return XboxOneAuthorizedRegistrationV1.ParseCreateResponse(receipt.RootElement, 42, 0xf00d, 0xbeed);
    }

    private static string ActivationJson() => JsonSerializer.Serialize(new
    {
        version = 1, usbipPort = 31007,
        usbipBusId = XboxOneAuthorizedRegistrationTests.Alias, usbipOwnerSerial = ""
    });

    private static XboxOneAuthorizedCreateRequestV1 Request() => new()
    {
        Version = 1, IdentityAuthorizationGranted = true,
        Identity = new XboxOneAuthorizedIdentity { VendorId = 0xf00d, ProductId = 0xbeed }
    };

    private static void AssertAuthority(string wire, string path)
    {
        StringAssert.StartsWith(wire, path + " ");
        using JsonDocument payload = JsonDocument.Parse(wire[(path.Length + 1)..]);
        Assert.AreEqual(2, payload.RootElement.EnumerateObject().Count());
        Assert.AreEqual(1, payload.RootElement.GetProperty("version").GetInt32());
        using JsonDocument receipt = JsonDocument.Parse(XboxOneAuthorizedRegistrationTests.ReceiptJson());
        Assert.AreEqual(receipt.RootElement.GetProperty("removalToken").GetString(),
            payload.RootElement.GetProperty("removalToken").GetString());
    }

    private static async Task<string> ReadRequest(Stream stream)
    {
        var text = new StringBuilder();
        byte[] one = new byte[1];
        while (text.Length < 4096)
        {
            await stream.ReadExactlyAsync(one).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            if (one[0] == 0) return text.ToString();
            text.Append((char)one[0]);
        }
        throw new IOException("Unexpected request size.");
    }

    private sealed class StartupFixture : IDisposable
    {
        internal TcpListener Listener { get; } = new(IPAddress.Loopback, 0);
        internal ViiperClient Client { get; }
        private TcpClient broker;
        internal StartupFixture(Func<Stream, Stream> authenticate = null)
        {
            Listener.Start();
            Client = new ViiperClient("127.0.0.1", ((IPEndPoint)Listener.LocalEndpoint).Port,
                authenticate ?? (stream => stream));
        }
        internal Task<TcpClient> Accept() => Listener.AcceptTcpClientAsync()
            .WaitAsync(TimeSpan.FromSeconds(5));
        internal async Task Reply(string path, string response, bool requireAuthority = false,
            TimeSpan? acceptTimeout = null)
        {
            using TcpClient accepted = await Listener.AcceptTcpClientAsync()
                .WaitAsync(acceptTimeout ?? TimeSpan.FromSeconds(5));
            string request = await ReadRequest(accepted.GetStream());
            if (requireAuthority) AssertAuthority(request, path);
            else StringAssert.StartsWith(request, path + " ");
            await accepted.GetStream().WriteAsync(Encoding.UTF8.GetBytes(response));
            accepted.Client.Shutdown(SocketShutdown.Send);
        }
        internal async Task<ViiperDeviceStream> Create()
        {
            Task<ViiperDeviceStream> creation = Task.Run(() =>
                Client.CreateAuthorizedXboxOneDeviceAndOpenStream(Request()));
            await Reply("bus/create", "{\"busId\":42}");
            await Reply("bus/42/add-authorized-xboxone", XboxOneAuthorizedRegistrationTests.ReceiptJson());
            broker = await Accept();
            AssertAuthority(await ReadRequest(broker.GetStream()), "bus/42/7/stream-authorized-xboxone");
            byte[] ready = new byte[16];
            await broker.GetStream().ReadExactlyAsync(ready).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            CollectionAssert.AreEqual("X1BR"u8.ToArray(), ready[..4]);
            Assert.AreEqual((byte)1, ready[4]);
            Assert.AreEqual((byte)1, ready[5]);
            CollectionAssert.AreEqual(new byte[10], ready[6..]);
            ready[5] = 0x81;
            await broker.GetStream().WriteAsync(ready);
            return await creation.WaitAsync(TimeSpan.FromSeconds(5));
        }
        public void Dispose() { broker?.Dispose(); Listener.Stop(); }
    }
}
