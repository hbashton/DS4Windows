using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public class ViiperSwitch2RuntimeStatusTests
{
    [DataTestMethod]
    [DataRow(3000, 1)]
    [DataRow(3200, 5)]
    [DataRow(3400, 9)]
    public void PhysicalBatteryBandMapsToDeclaredVirtualLevel(
        int millivolts, int expectedLevel)
    {
        Switch2BatteryStatus battery = DecodeBattery((ushort)millivolts);

        Assert.IsTrue(ViiperSwitch2RuntimeStatusV1.TryCreate(battery,
            ConnectionType.BT, out var wireless));
        Assert.AreEqual(ViiperSwitch2RuntimeStatusV1.ContractVersion,
            wireless.Version);
        Assert.AreEqual((byte)expectedLevel, wireless.BatteryLevel);
        Assert.AreEqual((ushort)millivolts, wireless.BatteryVolts);
        Assert.IsFalse(wireless.Charging,
            "Opaque current must not be reinterpreted as charging.");
        Assert.IsFalse(wireless.ExternalPower);

        Assert.IsTrue(ViiperSwitch2RuntimeStatusV1.TryCreate(battery,
            ConnectionType.USB, out var wired));
        Assert.IsTrue(wired.ExternalPower,
            "The physical USB transport establishes external power.");
    }

    [TestMethod]
    public void StatusAndCreationMetadataUseSeparateExactContracts()
    {
        Switch2BatteryStatus battery = DecodeBattery(3175);
        Assert.IsTrue(ViiperSwitch2RuntimeStatusV1.TryCreate(battery,
            ConnectionType.BT, out var status));

        string statusJson = ViiperClient.SerializeNS2ProRuntimeStatusV1(
            status);
        using JsonDocument statusDocument = JsonDocument.Parse(statusJson);
        JsonElement statusRoot = statusDocument.RootElement;
        Assert.AreEqual(1, statusRoot.GetProperty("version").GetInt32());
        Assert.AreEqual(5, statusRoot.GetProperty("batteryLevel").GetInt32());
        Assert.AreEqual(3175,
            statusRoot.GetProperty("batteryVolts").GetInt32());
        Assert.IsFalse(statusRoot.GetProperty("charging").GetBoolean());
        Assert.IsFalse(statusRoot.GetProperty("externalPower").GetBoolean());
        Assert.AreEqual(5, statusRoot.EnumerateObject().Count(),
            "Runtime status v1 must stay narrow and complete.");

        string createJson = ViiperClient.SerializeDeviceCreateRequest(
            "ns2pro", deviceSpecific: status.ToCreationMetadata());
        using JsonDocument createDocument = JsonDocument.Parse(createJson);
        JsonElement metadata = createDocument.RootElement.GetProperty(
            "deviceSpecific");
        Assert.AreEqual(5,
            metadata.GetProperty("battery_level").GetInt32());
        Assert.AreEqual(3175,
            metadata.GetProperty("battery_volts").GetInt32());
        Assert.IsFalse(metadata.TryGetProperty("version", out _));
        Assert.IsFalse(metadata.TryGetProperty("serial_number", out _),
            "Power mirroring must never take ownership of virtual identity.");
    }

    [TestMethod]
    public void IncompleteStatusCannotBeSerialized()
    {
        Assert.ThrowsException<ArgumentException>(() =>
            ViiperClient.SerializeNS2ProRuntimeStatusV1(default));
    }

    [TestMethod]
    public async Task ClientTargetsVersionedStatusEndpointAndRequiresAck()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Switch2BatteryStatus battery = DecodeBattery(3400);
        Assert.IsTrue(ViiperSwitch2RuntimeStatusV1.TryCreate(battery,
            ConnectionType.BT, out var status));

        try
        {
            Task<TcpClient> accept = listener.AcceptTcpClientAsync();
            var client = new ViiperClient("127.0.0.1", port,
                stream => stream);
            Task update = Task.Run(() =>
                client.UpdateNS2ProRuntimeStatusV1(42, "7", status));
            using TcpClient accepted = await accept.WaitAsync(
                TimeSpan.FromSeconds(2));
            NetworkStream serverStream = accepted.GetStream();
            byte[] request = await ReadNullTerminatedAsync(serverStream);
            string wire = Encoding.UTF8.GetString(request);
            StringAssert.StartsWith(wire,
                "bus/42/7/ns2pro-status-v1 ");
            using JsonDocument payload = JsonDocument.Parse(
                wire[(wire.IndexOf(' ') + 1)..]);
            Assert.AreEqual(1,
                payload.RootElement.GetProperty("version").GetInt32());
            Assert.AreEqual(9,
                payload.RootElement.GetProperty("batteryLevel").GetInt32());

            byte[] response = Encoding.UTF8.GetBytes(
                "{\"version\":1,\"updated\":true}\n");
            await serverStream.WriteAsync(response);
            accepted.Client.Shutdown(SocketShutdown.Send);
            await update.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            listener.Stop();
        }
    }

    private static Switch2BatteryStatus DecodeBattery(ushort millivolts)
    {
        var body = new byte[Switch2InputCodec.BluetoothLeBodyLength];
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0x1F, 2),
            millivolts);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0x21, 2),
            0x1234);
        body[0x23] = 0x56;
        Assert.IsTrue(Switch2InputCodec.TryDecodeCommon05(body,
            out var report));
        Assert.IsTrue(Switch2BatteryStatus.TryCreate(report,
            out var battery));
        return battery;
    }

    private static async Task<byte[]> ReadNullTerminatedAsync(Stream stream)
    {
        using var output = new MemoryStream();
        var one = new byte[1];
        while (true)
        {
            int read = await stream.ReadAsync(one);
            Assert.AreEqual(1, read, "Status request closed before NUL.");
            if (one[0] == 0)
            {
                return output.ToArray();
            }
            output.WriteByte(one[0]);
        }
    }
}
