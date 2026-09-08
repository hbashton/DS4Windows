using System.Text.Json;
using System.Text.Json.Nodes;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2BluetoothLabReceiverResultTests
{
    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void ExactCompletionAcceptsQueryOnlySetupFalseAndMatchingAudio(bool headset, bool audio)
    {
        var plan = Plan(headset, audio);
        JsonElement result = Element(Result(headset, audio));
        Assert.IsTrue(Switch2BluetoothLabReceiverResult.IsCompleted(result, plan));
        Assert.IsTrue(Switch2BluetoothLabReceiverResult.TryGetReceiverOperation(result, plan, out var operation));
        Assert.AreEqual(JsonValueKind.False, operation.GetProperty("SetupAcknowledged").ValueKind);
        Assert.AreEqual(JsonValueKind.False, operation.GetProperty("BluetoothPlaybackConfirmed").ValueKind);
    }

    [DataTestMethod]
    [DataRow("root")]
    [DataRow("operation")]
    [DataRow("commands")]
    [DataRow("audio")]
    [DataRow("response")]
    public void OuterSuccessCannotHideNestedOperationErrors(string location)
    {
        var root = Result(true, true);
        var operation = root["Receiver"].AsObject();
        JsonObject target = location switch
        {
            "root" => root,
            "operation" => operation,
            "commands" => operation["Receiver"].AsObject(),
            "audio" => operation["Audio"].AsObject(),
            _ => operation["Receiver"]["Responses"][0].AsObject()
        };
        target["Error"] = "rejected";
        Assert.IsFalse(Switch2BluetoothLabReceiverResult.IsCompleted(Element(root), Plan(true, true)));
    }

    [DataTestMethod]
    [DataRow("Status")]
    [DataRow("RestoreHeadset")]
    [DataRow("RestoreCommonInput")]
    [DataRow("LabAudioFenced")]
    public void HeadsetCleanupMustBePresentAndSuccessfulAtTheWindowRoot(string property)
    {
        var root = Result(true, false);
        root.Remove(property);
        // An inner lookalike is not evidence that the window was restored.
        root["Receiver"][property] = property == "LabAudioFenced"
            ? JsonValue.Create(false) : JsonValue.Create("Success");
        Assert.IsFalse(Switch2BluetoothLabReceiverResult.IsCompleted(Element(root), Plan(true, false)));
        root[property] = null;
        Assert.IsFalse(Switch2BluetoothLabReceiverResult.IsCompleted(Element(root), Plan(true, false)));
        root[property] = property == "LabAudioFenced"
            ? JsonValue.Create(true) : JsonValue.Create("ProtocolError");
        Assert.IsFalse(Switch2BluetoothLabReceiverResult.IsCompleted(Element(root), Plan(true, false)));
    }

    [TestMethod]
    public void FailedBeforeReadsDoNotInvalidateSuccessfulOwnedNotificationCleanup()
    {
        var root = Result(true, false);
        root["Before"] = new JsonObject
        {
            ["HeadsetStatus"] = "ProtocolError", ["InputStatus"] = "ProtocolError",
            ["OwnedCommonInputNotify"] = true,
            // Diagnostic schemas may grow; they must not become operation flags.
            ["Status"] = "ProtocolError", ["Error"] = "CCCD read rejected"
        };
        Assert.IsTrue(Switch2BluetoothLabReceiverResult.IsCompleted(Element(root), Plan(true, false)));
        root["Status"] = "ProtocolError";
        Assert.IsFalse(Switch2BluetoothLabReceiverResult.IsCompleted(Element(root), Plan(true, false)));
    }

    [DataTestMethod]
    [DataRow("missing-audio")]
    [DataRow("null-audio")]
    [DataRow("missing-sent")]
    [DataRow("partial")]
    [DataRow("extra")]
    [DataRow("string")]
    [DataRow("misplaced")]
    public void RequestedAudioRequiresItsOwnExactSentCount(string failure)
    {
        var root = Result(true, true);
        var operation = root["Receiver"].AsObject();
        var audio = operation["Audio"].AsObject();
        switch (failure)
        {
            case "missing-audio": operation.Remove("Audio"); break;
            case "null-audio": operation["Audio"] = null; break;
            case "missing-sent": audio.Remove("Sent"); break;
            case "partial": audio["Sent"] = 1; break;
            case "extra": audio["Sent"] = 3; break;
            case "string": audio["Sent"] = "2"; break;
            case "misplaced":
                operation.Remove("Audio");
                root["Before"] = new JsonObject { ["Audio"] = new JsonObject { ["Sent"] = 2 } };
                break;
        }
        Assert.IsFalse(Switch2BluetoothLabReceiverResult.IsCompleted(Element(root), Plan(true, true)));
    }

    [DataTestMethod]
    [DataRow("outer-ack")]
    [DataRow("inner-ack")]
    [DataRow("missing-inner")]
    [DataRow("null-inner")]
    [DataRow("missing-setup")]
    [DataRow("string-setup")]
    [DataRow("mismatched-setup")]
    public void BothOperationAndCommandCompletionFlagsMustMatchTheirActualPaths(string failure)
    {
        var root = Result(false, false);
        switch (failure)
        {
            case "outer-ack": root["CommandsAcknowledged"] = false; break;
            case "inner-ack": root["Receiver"]["CommandsAcknowledged"] = false; break;
            case "missing-inner": root.Remove("Receiver"); break;
            case "null-inner": root["Receiver"] = null; break;
            case "missing-setup": root.Remove("SetupAcknowledged"); break;
            case "string-setup": root["SetupAcknowledged"] = "false"; break;
            case "mismatched-setup": root["SetupAcknowledged"] = true; break;
        }
        Assert.IsFalse(Switch2BluetoothLabReceiverResult.IsCompleted(Element(root), Plan(false, false)));
    }

    [TestMethod]
    public void SetupAcknowledgementMustDescribeSetupInThisPlanNotPriorState()
    {
        var setupPlan = new Switch2BluetoothLabReceiverPlan
        {
            Id = "setup", Generation = 1,
            Commands = new[] { Convert.FromHexString("179101020007000080BB000002F000") }
        };
        var result = Result(false, false);
        Assert.IsFalse(Switch2BluetoothLabReceiverResult.IsCompleted(Element(result), setupPlan),
            "A plan containing setup cannot report two false setup flags and still complete.");
        result["SetupAcknowledged"] = true;
        result["Receiver"]["SetupAcknowledged"] = true;
        Assert.IsTrue(Switch2BluetoothLabReceiverResult.IsCompleted(Element(result), setupPlan));
        Assert.IsFalse(Switch2BluetoothLabReceiverResult.IsCompleted(Element(result), Plan(false, false)),
            "A query plan reports no setup of its own, even if the lease had prior setup.");
    }

    [TestMethod]
    public void WrongRootShapeAndUnexpectedAudioDoNotCountAsCompletion()
    {
        foreach (string json in new[] { "null", "[]", "true", "{}", "{\"Receiver\":null}" })
        {
            using var document = JsonDocument.Parse(json);
            Assert.IsFalse(Switch2BluetoothLabReceiverResult.IsCompleted(document.RootElement, Plan(true, false)));
            Assert.IsFalse(Switch2BluetoothLabReceiverResult.IsCompleted(document.RootElement, Plan(false, false)));
        }
        Assert.IsFalse(Switch2BluetoothLabReceiverResult.IsCompleted(Element(Result(true, false)), Plan(false, false)));
        Assert.IsFalse(Switch2BluetoothLabReceiverResult.IsCompleted(Element(Result(false, false)), Plan(true, false)));
        Assert.IsFalse(Switch2BluetoothLabReceiverResult.IsCompleted(Element(Result(false, true)), Plan(false, false)));
    }

    private static Switch2BluetoothLabReceiverPlan Plan(bool headset, bool audio) => new()
    {
        Id = "result-test", Generation = 1, HeadsetNotifications = headset,
        Commands = new[] { Convert.FromHexString("1891010100000000") },
        Audio = audio ? new Switch2BluetoothLabPlan
        {
            Id = "synthetic", Generation = 1,
            Packets = new[]
            {
                new Switch2BluetoothLabPacket { Payload = new byte[] { 0 } },
                new Switch2BluetoothLabPacket { OffsetMicroseconds = 5000, Payload = new byte[] { 0 } }
            }
        } : null
    };

    private static JsonObject Result(bool headset, bool audio)
    {
        var operation = new JsonObject
        {
            ["CommandsAcknowledged"] = true, ["SetupAcknowledged"] = false,
            ["Receiver"] = new JsonObject
            {
                ["CommandsAcknowledged"] = true, ["SetupAcknowledged"] = false,
                ["Responses"] = new JsonArray(new JsonObject { ["Acknowledged"] = true })
            },
            ["Audio"] = audio ? new JsonObject { ["Sent"] = 2 } : null,
            ["BluetoothPlaybackConfirmed"] = false
        };
        return !headset ? operation : new JsonObject
        {
            ["Status"] = "Success", ["RestoreHeadset"] = "Success", ["RestoreCommonInput"] = "Success",
            ["LabAudioFenced"] = false, ["Receiver"] = operation, ["BluetoothPlaybackConfirmed"] = false
        };
    }

    private static JsonElement Element(JsonObject value) => JsonSerializer.SerializeToElement(value);
}
