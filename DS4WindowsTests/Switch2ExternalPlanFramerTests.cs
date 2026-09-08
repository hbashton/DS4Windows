using DS4Windows.Switch2;
using System.IO;

namespace DS4WindowsTests;

[TestClass]
public class Switch2ExternalPlanFramerTests
{
    [DataTestMethod]
    [DataRow("id0", 1)]
    [DataRow("seq8", 1)]
    [DataRow("id0-seq8", 2)]
    [DataRow("len16le", 2)]
    [DataRow("id0-len8", 2)]
    [DataRow("seq8-len8", 2)]
    public void ExternalEnvelopesPreservePreviouslyDecodedAudioAndSchedule(string framing, int prefix)
    {
        var tone = Switch2BluetoothLabTone.Create("t:opus:1:20:20:raw");
        var source = new Switch2BluetoothLabPlan { Id = "mono", Generation = 2, HeadsetNotifications = true,
            Packets = tone.Packets.Select((p, i) => new Switch2BluetoothLabPacket { OffsetMicroseconds = i * 20000, Payload = p }).ToArray() };
        var framed = PlanFramer.Apply(source, framing);
        Assert.AreEqual(source.Generation, framed.Generation);
        Assert.IsTrue(framed.HeadsetNotifications);
        Assert.AreNotEqual(source.Fingerprint(), framed.Fingerprint());
        for (int i = 0; i < framed.Packets.Length; i++)
        {
            Assert.AreEqual(source.Packets[i].OffsetMicroseconds, framed.Packets[i].OffsetMicroseconds);
            CollectionAssert.AreEqual(source.Packets[i].Payload, framed.Packets[i].Payload.AsSpan(prefix).ToArray());
            byte[] expected = framing switch
            {
                "id0" => new byte[] { 0 }, "seq8" => new byte[] { (byte)i },
                "id0-seq8" => new byte[] { 0, (byte)i }, "len16le" => new byte[] { 50, 0 },
                "id0-len8" => new byte[] { 0, 50 }, _ => new byte[] { (byte)i, 50 }
            };
            CollectionAssert.AreEqual(expected, framed.Packets[i].Payload.AsSpan(0, prefix).ToArray());
        }
        byte original = source.Packets[0].Payload[0];
        framed.Packets[0].Payload[prefix] ^= 0xff;
        Assert.AreEqual(original, source.Packets[0].Payload[0]);
    }

    [TestMethod]
    public void FramerRejectsOverflowWithoutMutatingSource()
    {
        var source = new Switch2BluetoothLabPlan { Id = "limit", Generation = 2,
            Packets = new[] { new Switch2BluetoothLabPacket { Payload = new byte[509] } } };
        string digest = source.Fingerprint();
        Assert.ThrowsException<InvalidDataException>(() => PlanFramer.Apply(source, "id0"));
        Assert.ThrowsException<ArgumentException>(() => PlanFramer.Apply(source, "random-command"));
        Assert.AreEqual(digest, source.Fingerprint());
        source = new Switch2BluetoothLabPlan { Id = "length", Generation = 2,
            Packets = new[] { new Switch2BluetoothLabPacket { Payload = new byte[256] } } };
        Assert.ThrowsException<InvalidDataException>(() => PlanFramer.Apply(source, "seq8-len8"));
    }
}
