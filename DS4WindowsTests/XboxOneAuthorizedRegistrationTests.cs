using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DS4Windows;

namespace DS4WindowsTests;

[TestClass]
public sealed class XboxOneAuthorizedRegistrationTests
{
    private const string Token =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    internal const string Alias = "x1-aaaaaaaaaaaaaaaaaaaaaaaaaa";

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void DormantFactoryReceiptBindsCanonicalIdentityAndPrivateToken(
        bool explicitDormantFields)
    {
        string json = ReceiptJson();
        if (explicitDormantFields)
            json = json.Replace("\"deviceSpecific\":{}",
                "\"deviceSpecific\":{},\"usbipPort\":0,\"usbipOwnerSerial\":\"\"");
        var registration = Parse(json);
        Assert.AreEqual(42U, registration.BusId);
        Assert.AreEqual("7", registration.DevId);
        Assert.AreEqual(Alias, registration.UsbipBusId);
        Assert.AreEqual(90000, registration.RemovalTimeoutMilliseconds);
        Assert.AreEqual(92000, registration.RemovalResponseTimeoutMilliseconds);
        Assert.AreEqual("bus/42/7/remove-authorized-xboxone",
            registration.RemovalPath);
        Assert.IsFalse(registration.ToString().Contains(Token),
            "The capability must not leak through default diagnostic formatting.");

        using JsonDocument request = JsonDocument.Parse(
            registration.SerializeRemovalRequest());
        Assert.AreEqual(2, request.RootElement.EnumerateObject().Count());
        Assert.AreEqual(1, request.RootElement.GetProperty("version").GetInt32());
        Assert.AreEqual(Token,
            request.RootElement.GetProperty("removalToken").GetString());
    }

    public static IEnumerable<object[]> InvalidReceipts()
    {
        yield return new object[] { "null" };
        yield return new object[] { "[]" };
        yield return new object[] { "{}" };
        string valid = ReceiptJson();
        foreach (string field in new[] { "busId", "devId", "vid", "pid",
            "type", "deviceSpecific", "removalToken", "usbipBusId",
            "removalTimeoutMilliseconds" })
        {
            using JsonDocument document = JsonDocument.Parse(valid);
            var fields = document.RootElement.EnumerateObject().ToArray();
            yield return new object[] { "{" + string.Join(",",
                fields.Where(p => p.Name != field).Select(p => p.ToString())) + "}" };
            JsonProperty property = fields.Single(p => p.Name == field);
            yield return new object[] { valid.Insert(1, property + ",") };
            yield return new object[] { valid.Replace("\"" + field + "\":",
                "\"" + char.ToUpperInvariant(field[0]) + field[1..] + "\":") };
        }
        foreach ((string before, string after) in new[]
        {
            ("\"busId\":42", "\"busId\":43"),
            ("\"busId\":42", "\"busId\":\"42\""),
            ("\"busId\":42", "\"busId\":0"),
            ("\"devId\":\"7\"", "\"devId\":\"07\""),
            ("\"devId\":\"7\"", "\"devId\":\"7/remove\""),
            ("\"devId\":\"7\"", "\"devId\":\"+7\""),
            ("\"devId\":\"7\"", "\"devId\":\"0\""),
            ("\"devId\":\"7\"", "\"devId\":\"65536\""),
            ("\"devId\":\"7\"", "\"devId\":7"),
            ("\"vid\":\"0xf00d\"", "\"vid\":\"0xF00D\""),
            ("\"pid\":\"0xbeed\"", "\"pid\":\"0xbeef\""),
            ("\"type\":\"xboxone\"", "\"type\":\"xbox360\""),
            ("\"deviceSpecific\":{}", "\"deviceSpecific\":null"),
            ("\"deviceSpecific\":{}", "\"deviceSpecific\":{},\"usbipPort\":1"),
            ("\"deviceSpecific\":{}", "\"deviceSpecific\":{},\"usbipPort\":-1"),
            ("\"deviceSpecific\":{}", "\"deviceSpecific\":{},\"usbipPort\":\"0\""),
            ("\"deviceSpecific\":{}", "\"deviceSpecific\":{},\"usbipOwnerSerial\":null"),
            ("\"deviceSpecific\":{}", "\"deviceSpecific\":{},\"usbipOwnerSerial\":\"DS4W00000000000\""),
            ("\"deviceSpecific\":{}", "\"deviceSpecific\":{},\"unexpected\":0"),
            ("\"deviceSpecific\":{}", "\"deviceSpecific\":{},\"usbipPort\":0,\"usbipPort\":0"),
            ("\"deviceSpecific\":{}", "\"deviceSpecific\":{},\"UsbipPort\":0"),
            ("\"deviceSpecific\":{}", "\"deviceSpecific\":{},\"usbipOwnerSerial\":\"\",\"usbipOwnerSerial\":\"\""),
            ("\"deviceSpecific\":{}", "\"deviceSpecific\":{},\"UsbipOwnerSerial\":\"\""),
            ("\"deviceSpecific\":{}", "\"deviceSpecific\":{},\"\\u0062usId\":42"),
            (Token, Token.ToUpperInvariant()),
            (Token, Token[..63]),
            (Token, Token + "0"),
            (Token, "g" + Token[1..]),
            (Token, " " + Token[1..]),
            (Alias, "42-7"),
            (Alias, Alias.ToUpperInvariant()),
            (Alias, Alias[..^1] + "b"),
            (Alias, Alias + "a"),
            ("\"removalTimeoutMilliseconds\":90000", "\"removalTimeoutMilliseconds\":0"),
            ("\"removalTimeoutMilliseconds\":90000", "\"removalTimeoutMilliseconds\":300001"),
            ("\"removalTimeoutMilliseconds\":90000", "\"removalTimeoutMilliseconds\":\"90000\""),
            ("\"" + Token + "\"", "null")
        })
            yield return new object[] { valid.Replace(before, after) };
    }

    [DataTestMethod]
    [DynamicData(nameof(InvalidReceipts), DynamicDataSourceType.Method)]
    public void InvalidReceiptNeverCreatesCleanupAuthority(string json)
    {
        IOException error = Assert.ThrowsException<IOException>(() => Parse(json));
        Assert.IsFalse(error.Message.Contains(Token));
        Assert.IsFalse(error.Message.Contains("g" + Token[1..]));
    }

    [TestMethod]
    public void RetainedUsbAddressLimitAndExpectedIdentityAreEnforced()
    {
        using JsonDocument document = JsonDocument.Parse(ReceiptJson());
        Assert.ThrowsException<IOException>(() =>
            XboxOneAuthorizedRegistrationV1.ParseCreateResponse(
                document.RootElement, 0, 0xf00d, 0xbeed));
        Assert.ThrowsException<IOException>(() =>
            XboxOneAuthorizedRegistrationV1.ParseCreateResponse(
                document.RootElement, 65536, 0xf00d, 0xbeed));
        Assert.ThrowsException<IOException>(() =>
            XboxOneAuthorizedRegistrationV1.ParseCreateResponse(
                document.RootElement, 42, 0xf00e, 0xbeed));
    }

    public static IEnumerable<object[]> InvalidActivationResponses()
    {
        yield return new object[] { "null" };
        yield return new object[] { "[]" };
        yield return new object[] { "{}" };
        string valid = ActivationJson();
        foreach (string field in new[] { "version", "usbipBusId", "usbipPort" })
        {
            using JsonDocument document = JsonDocument.Parse(valid);
            JsonProperty[] fields = document.RootElement.EnumerateObject().ToArray();
            yield return new object[] { "{" + string.Join(",",
                fields.Where(p => p.Name != field).Select(p => p.ToString())) + "}" };
            yield return new object[] { valid.Insert(1,
                fields.Single(p => p.Name == field) + ",") };
            yield return new object[] { valid.Replace("\"" + field + "\":",
                "\"" + char.ToUpperInvariant(field[0]) + field[1..] + "\":") };
        }
        foreach ((string before, string after) in new[]
        {
            ("\"version\":1", "\"version\":2"),
            ("\"version\":1", "\"version\":\"1\""),
            (Alias, "42-7"),
            (Alias, Alias[..^1] + "e"),
            (Alias, Alias.ToUpperInvariant()),
            ("\"usbipPort\":31007", "\"usbipPort\":0"),
            ("\"usbipPort\":31007", "\"usbipPort\":-1"),
            ("\"usbipPort\":31007", "\"usbipPort\":2147483648"),
            ("\"usbipPort\":31007", "\"usbipPort\":\"31007\""),
            ("\"usbipPort\":31007", "\"usbipPort\":1.5"),
            ("\"usbipPort\":31007", "\"usbipPort\":true"),
            ("\"usbipOwnerSerial\":\"\"", "\"usbipOwnerSerial\":null"),
            ("\"usbipOwnerSerial\":\"\"", "\"usbipOwnerSerial\":\"foreign\""),
            ("\"usbipOwnerSerial\":\"\"", "\"UsbipOwnerSerial\":\"\""),
            ("\"usbipOwnerSerial\":\"\"", "\"usbipOwnerSerial\":\"\",\"usbipOwnerSerial\":\"\""),
            ("\"version\":1", "\"version\":1,\"unexpected\":0"),
            ("\"version\":1", "\"version\":1,\"\\u0076ersion\":1")
        })
            yield return new object[] { valid.Replace(before, after) };
    }

    [DataTestMethod]
    [DynamicData(nameof(InvalidActivationResponses), DynamicDataSourceType.Method)]
    public void ActivationAcknowledgementCannotBindForeignOrAmbiguousIdentity(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        IOException error = Assert.ThrowsException<IOException>(() =>
            Parse(ReceiptJson()).ParseActivationResponse(document.RootElement));
        Assert.IsFalse(error.ToString().Contains(Token));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ActivationAcceptsExactAliasWithOptionalEmptySerial(bool includeSerial)
    {
        string json = ActivationJson();
        if (!includeSerial)
            json = json.Replace(",\"usbipOwnerSerial\":\"\"", "");
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.AreEqual(31007, Parse(ReceiptJson()).ParseActivationResponse(document.RootElement));
    }

    internal static string ActivationJson() => $$"""
        {"version":1,"usbipPort":31007,"usbipBusId":"{{Alias}}","usbipOwnerSerial":""}
        """;

    [DataTestMethod]
    [DataRow("{}")]
    [DataRow("null")]
    [DataRow("[]")]
    [DataRow("{\"version\":1}")]
    [DataRow("{\"removed\":true}")]
    [DataRow("{\"version\":2,\"removed\":true}")]
    [DataRow("{\"version\":\"1\",\"removed\":true}")]
    [DataRow("{\"version\":1,\"removed\":\"true\"}")]
    [DataRow("{\"version\":1,\"removed\":true,\"removed\":false}")]
    [DataRow("{\"version\":1,\"version\":1,\"removed\":true}")]
    [DataRow("{\"Version\":1,\"removed\":true}")]
    [DataRow("{\"version\":1,\"Removed\":true}")]
    [DataRow("{\"version\":1,\"removed\":true,\"unexpected\":0}")]
    public void RemovalAcknowledgementHasClosedVersionedSchema(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.ThrowsException<IOException>(() =>
            XboxOneAuthorizedRegistrationV1.ParseRemovalResponse(document.RootElement));
    }

    [DataTestMethod]
    [DataRow("{\"version\":1,\"removed\":true}", true, false)]
    [DataRow("{\"removed\":false,\"version\":1}", false, false)]
    [DataRow("{\"version\":1,\"removed\":true,\"removed\":false}", false, true)]
    [DataRow("{\"version\":2,\"removed\":true}", false, true)]
    [DataRow("", false, true)]
    [DataRow("{\"status\":409,\"title\":\"Conflict\",\"detail\":\"Retirement failed\"}", false, true)]
    [DataRow("echo-token", false, true)]
    [DataRow("malformed-echo-token", false, true)]
    [DataRow("oversize", false, true)]
    public async Task ClientSendsExactCapabilityAndNeverNumericFallback(
        string response, bool expectedRemoved, bool expectFailure)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new ViiperClient("127.0.0.1",
            ((IPEndPoint)listener.LocalEndpoint).Port, stream => stream);
        if (response == "echo-token")
            response = JsonSerializer.Serialize(new
            {
                status = 409, title = "Conflict", detail = Token
            });
        else if (response == "malformed-echo-token")
            response = "{\"" + Token + "\":";
        else if (response == "oversize")
            response = new string(' ', 1024);
        Task<bool> removal = Task.Run(() =>
            client.RemoveAuthorizedXboxOneRegistration(Parse(ReceiptJson())));
        using TcpClient accepted = await listener.AcceptTcpClientAsync()
            .WaitAsync(TimeSpan.FromSeconds(5));
        NetworkStream server = accepted.GetStream();
        string wire = await ReadRequest(server).WaitAsync(TimeSpan.FromSeconds(5));
        const string prefix = "bus/42/7/remove-authorized-xboxone ";
        StringAssert.StartsWith(wire, prefix);
        using JsonDocument request = JsonDocument.Parse(wire[prefix.Length..]);
        Assert.AreEqual(2, request.RootElement.EnumerateObject().Count());
        Assert.AreEqual(1, request.RootElement.GetProperty("version").GetInt32());
        Assert.AreEqual(Token,
            request.RootElement.GetProperty("removalToken").GetString());
        await server.WriteAsync(Encoding.UTF8.GetBytes(response));
        accepted.Client.Shutdown(SocketShutdown.Send);
        if (expectFailure)
        {
            try
            {
                await removal.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Fail("An invalid/missing response must not acknowledge removal.");
            }
            catch (IOException error)
            {
                Assert.IsFalse(error.ToString().Contains(Token));
            }
        }
        else
            Assert.AreEqual(expectedRemoved,
                await removal.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.IsFalse(listener.Pending(),
            "No address-only remove, bus/remove, retry, or other request is allowed.");
    }

    [TestMethod]
    public void NullAuthorityAndInvalidBudgetNeverOpenAConnection()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new ViiperClient("127.0.0.1",
            ((IPEndPoint)listener.LocalEndpoint).Port, stream => stream);
        Assert.ThrowsException<ArgumentNullException>(() =>
            client.RemoveAuthorizedXboxOneRegistration(null));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            client.RemoveAuthorizedXboxOneRegistration(Parse(ReceiptJson()), 0));
        Assert.IsFalse(listener.Pending());
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LostOrDribblingResponseCannotExtendDeadlineOrRetry(bool reset)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new ViiperClient("127.0.0.1",
            ((IPEndPoint)listener.LocalEndpoint).Port, stream => stream);
        Task<bool> removal = Task.Run(() =>
            client.RemoveAuthorizedXboxOneRegistration(Parse(ReceiptJson()), 500));
        using TcpClient accepted = await listener.AcceptTcpClientAsync()
            .WaitAsync(TimeSpan.FromSeconds(5));
        NetworkStream server = accepted.GetStream();
        await ReadRequest(server).WaitAsync(TimeSpan.FromSeconds(5));
        if (reset)
        {
            accepted.Client.LingerState = new LingerOption(true, 0);
            accepted.Dispose();
        }
        else
        {
            // Keep producing valid JSON whitespace more frequently than the
            // socket read timeout. Only an absolute deadline ends the request.
            using var writes = new CancellationTokenSource();
            Task producer = Task.Run(async () =>
            {
                try
                {
                    while (!writes.IsCancellationRequested)
                    {
                        await server.WriteAsync(new byte[] { (byte)' ' }, writes.Token);
                        await Task.Delay(25, writes.Token);
                    }
                }
                catch (Exception error) when (error is IOException ||
                    error is OperationCanceledException || error is ObjectDisposedException)
                { }
            });
            try
            {
                await Assert.ThrowsExceptionAsync<IOException>(async () =>
                    await removal.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            finally
            {
                writes.Cancel();
                await producer.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        IOException failure = await Assert.ThrowsExceptionAsync<IOException>(async () =>
            await removal.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsFalse(failure.ToString().Contains(Token));
        Assert.IsFalse(listener.Pending());
    }

    private static XboxOneAuthorizedRegistrationV1 Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return XboxOneAuthorizedRegistrationV1.ParseCreateResponse(
            document.RootElement, 42, 0xf00d, 0xbeed);
    }

    internal static string ReceiptJson() => $$"""
        {"busId":42,"devId":"7","vid":"0xf00d","pid":"0xbeed","type":"xboxone","deviceSpecific":{},"removalToken":"{{Token}}","usbipBusId":"{{Alias}}","removalTimeoutMilliseconds":90000}
        """;

    private static async Task<string> ReadRequest(Stream stream)
    {
        var request = new StringBuilder();
        var value = new byte[1];
        while (request.Length < 1024)
        {
            Assert.AreEqual(1, await stream.ReadAsync(value));
            if (value[0] == 0)
                return request.ToString();
            request.Append((char)value[0]);
        }
        Assert.Fail("Unexpectedly large management request.");
        return string.Empty;
    }
}
