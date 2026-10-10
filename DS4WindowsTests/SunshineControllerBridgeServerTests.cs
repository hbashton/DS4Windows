using System;
using System.IO;
using System.IO.Pipes;
using System.Threading.Tasks;
using DS4Windows.Sunshine;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4WindowsTests;

[TestClass]
public sealed class SunshineControllerBridgeServerTests
{
    [TestMethod]
    public async Task PipeHandshakeCarriesControllerAndFeedbackRecords()
    {
        string pipeName = "Vibeshine.ControllerBridge.Test." +
            Guid.NewGuid().ToString("N");
        var received = new TaskCompletionSource<SunshineControllerBridgePacket>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new SunshineControllerBridgeServer(
            packet => received.TrySetResult(packet),
            () => disconnected.TrySetResult(), pipeName);
        server.Start();

        using var client = new NamedPipeClientStream(".", pipeName,
            PipeDirection.InOut, PipeOptions.Asynchronous);
        client.Connect(5000);

        ulong sessionId = 0x1122334455667788;
        var hello = new SunshineControllerBridgePacket(
            SunshineControllerBridgePacketKind.Hello, sessionId, 0, 0, 0);
        await WritePacket(client, hello).WaitAsync(TimeSpan.FromSeconds(5));

        SunshineControllerBridgePacket accepted = await ReadPacket(client).
            WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(SunshineControllerBridgePacketKind.HelloAccepted,
            accepted.Kind);
        Assert.AreEqual(sessionId, accepted.SessionId);

        var arrival = new SunshineControllerBridgePacket(
            SunshineControllerBridgePacketKind.Arrival, sessionId, 2, 1, 4,
            controllerType: 3, capabilities: 0x12,
            supportedButtons: 0x00ABCDEF);
        await WritePacket(client, arrival).WaitAsync(TimeSpan.FromSeconds(5));
        SunshineControllerBridgePacket receivedArrival = await received.Task.
            WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(SunshineControllerBridgePacketKind.Arrival,
            receivedArrival.Kind);
        Assert.AreEqual((ushort)2, receivedArrival.ControllerId);
        Assert.AreEqual((byte)3, receivedArrival.ControllerType);
        Assert.AreEqual(0x00ABCDEFu, receivedArrival.SupportedButtons);

        var feedback = new SunshineControllerBridgePacket(
            SunshineControllerBridgePacketKind.Feedback, sessionId, 2, 1, 0,
            feedbackKind: (byte)SunshineControllerBridgeFeedbackKind.Rumble,
            lowRumble: 0x56, highRumble: 0xA7);
        Task<SunshineControllerBridgePacket> feedbackRead = ReadPacket(client);
        bool sent = await Task.Run(() => server.Send(feedback)).WaitAsync(
            TimeSpan.FromSeconds(5));
        Assert.IsTrue(sent);
        SunshineControllerBridgePacket receivedFeedback = await feedbackRead.
            WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(SunshineControllerBridgePacketKind.Feedback,
            receivedFeedback.Kind);
        Assert.AreEqual((byte)0x56, receivedFeedback.LowRumble);
        Assert.AreEqual((byte)0xA7, receivedFeedback.HighRumble);

        await Task.Run(client.Dispose).WaitAsync(TimeSpan.FromSeconds(5));
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task WritePacket(Stream stream,
        SunshineControllerBridgePacket packet)
    {
        byte[] record = new byte[SunshineControllerBridgePacket.RecordLength];
        packet.Write(record);
        await stream.WriteAsync(record);
        await stream.FlushAsync();
    }

    private static async Task<SunshineControllerBridgePacket> ReadPacket(
        Stream stream)
    {
        byte[] record = new byte[SunshineControllerBridgePacket.RecordLength];
        int offset = 0;
        while (offset < record.Length)
        {
            int read = await stream.ReadAsync(record.AsMemory(offset,
                record.Length - offset));
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
        Assert.IsTrue(SunshineControllerBridgePacket.TryRead(record,
            out SunshineControllerBridgePacket packet));
        return packet;
    }
}
