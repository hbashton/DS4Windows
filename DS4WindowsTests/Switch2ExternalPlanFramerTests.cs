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

    [DataTestMethod]
    [DataRow(64)]
    [DataRow(112)]
    [DataRow(128)]
    [DataRow(256)]
    [DataRow(480)]
    [DataRow(509)]
    public void FixedLengthHypothesesKeepExplicitAudioLengthAndZeroPadding(int size)
    {
        var tone = Switch2BluetoothLabTone.Create("t:opus:2:5:80:raw");
        var source = new Switch2BluetoothLabPlan { Id = "stereo", Generation = 2,
            Packets = tone.Packets.Select((p, i) => new Switch2BluetoothLabPacket { OffsetMicroseconds = i * 5000, Payload = p }).ToArray() };
        foreach (string envelope in new[] { "len16le", "id0-len8", "seq8-len8" })
        {
            var result = PlanFramer.Apply(source, envelope + "-pad" + size);
            for (int i = 0; i < result.Packets.Length; i++)
            {
                var wire = result.Packets[i].Payload;
                Assert.AreEqual(size, wire.Length);
                int encodedLength = envelope == "len16le" ? wire[0] | (wire[1] << 8) : wire[1];
                Assert.AreEqual(50, encodedLength);
                CollectionAssert.AreEqual(source.Packets[i].Payload, wire.AsSpan(2, encodedLength).ToArray());
                Assert.IsTrue(wire.Skip(2 + encodedLength).All(b => b == 0));
            }
        }
        foreach (string invalid in new[] { "raw-pad64", "id0-pad64", "len16le-pad512", "seq8-len8-pad63" })
            Assert.ThrowsException<ArgumentException>(() => PlanFramer.Apply(source, invalid));
    }

    [TestMethod]
    public void GroupedPcmPreservesEverySampleAndUsesExplicitPairedDeadlines()
    {
        var tone = Switch2BluetoothLabTone.Create("t:pcm:2:2.5:0:raw");
        var source = new Switch2BluetoothLabPlan { Id = "t-pcm-2-2_5-0-raw", Generation = 2,
            Packets = tone.Packets.Select((p, i) => new Switch2BluetoothLabPacket { OffsetMicroseconds = i * 2500, Payload = p }).ToArray() };
        var grouped = PlanFramer.Apply(source, "pcm-pair5");
        Assert.AreEqual(240, grouped.Packets.Length);
        for (int i = 0; i < 240; i++)
        {
            Assert.AreEqual((i / 2) * 5000, grouped.Packets[i].OffsetMicroseconds);
            CollectionAssert.AreEqual(source.Packets[i].Payload, grouped.Packets[i].Payload);
        }
        Assert.IsFalse(grouped.HeadsetNotifications);
        var unsupported = new Switch2BluetoothLabPlan { Id = source.Id, Generation = 2, HeadsetNotifications = true, Packets = source.Packets };
        Assert.ThrowsException<InvalidDataException>(() => PlanFramer.Apply(unsupported, "pcm-pair5"));
        source.Packets[1].Payload[0] = 10;
        Assert.AreNotEqual(source.Packets[1].Payload[0], grouped.Packets[1].Payload[0]);
    }

    [DataTestMethod]
    [DataRow("pro-raw", 33, false)]
    [DataRow("pro-len8", 34, false)]
    [DataRow("proseq-raw", 33, true)]
    [DataRow("proseq-len8", 34, true)]
    [DataRow("proseq-len8-pad112", 34, true)]
    public void AdjacentProEnvelopeUsesLeadingByteAndSilentGroups(string framing, int prefix, bool counters)
    {
        var tone = Switch2BluetoothLabTone.Create("t:opus:2:5:80:raw");
        var source = new Switch2BluetoothLabPlan { Id = "stereo", Generation = 2,
            Packets = tone.Packets.Select((p, i) => new Switch2BluetoothLabPacket { OffsetMicroseconds = i * 5000, Payload = p }).ToArray() };
        var result = PlanFramer.Apply(source, framing);
        for (int i = 0; i < result.Packets.Length; i++)
        {
            var wire = result.Packets[i].Payload;
            Assert.AreEqual((byte)0, wire[0]);
            if (counters)
            {
                Assert.IsTrue(Switch2BluetoothHdRumbleCodec.TryDecodeProController(wire.AsSpan(0, 33), out var sequence,
                    out var left, out var right, out var failure));
                Assert.AreEqual((byte)(i & 15), sequence);
                Assert.AreEqual(default(Switch2HdRumbleGroup), left);
                Assert.AreEqual(default(Switch2HdRumbleGroup), right);
            }
            else Assert.IsTrue(wire.Take(33).All(b => b == 0));
            if (prefix == 34) Assert.AreEqual((byte)50, wire[33]);
            CollectionAssert.AreEqual(source.Packets[i].Payload, wire.AsSpan(prefix, 50).ToArray());
            Assert.IsTrue(wire.Skip(prefix + 50).All(b => b == 0));
            Assert.AreEqual(source.Packets[i].OffsetMicroseconds, result.Packets[i].OffsetMicroseconds);
        }
    }
}
