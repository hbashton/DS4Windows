using System.Buffers.Binary;
using DS4Windows.InputDevices;

namespace DS4WindowsTests
{
    [TestClass]
    public class DualSenseBluetoothPadForgeSpeakerTransportTests
    {
        [TestMethod]
        public void SpeakerReportMatchesSony0x35Protocol()
        {
            byte[] opus = CreateOpusFrame();
            byte[] report = new byte[
                DualSenseBluetoothPadForgeSpeakerTransport.ReportLength];
            int sequence = 3;
            byte packetCounter = 0x7A;

            DualSenseBluetoothPadForgeSpeakerTransport.BuildReport(
                report, opus, opus.Length, headsetOnly: false,
                microphoneEnabled: false, ref sequence, ref packetCounter);

            Assert.AreEqual((byte)0x35, report[0]);
            Assert.AreEqual((byte)0x30, report[1]);
            Assert.AreEqual((byte)0x91, report[2]);
            Assert.AreEqual((byte)0x07, report[3]);
            Assert.AreEqual((byte)0xFE, report[4]);
            Assert.AreEqual((byte)0xFF, report[9]);
            Assert.AreEqual((byte)0x7A, report[10]);
            Assert.AreEqual((byte)0x93, report[11]);
            Assert.AreEqual((byte)200, report[12]);
            CollectionAssert.AreEqual(opus,
                report.AsSpan(13, opus.Length).ToArray());
            Assert.IsTrue(report.AsSpan(213, 117).ToArray().All(
                value => value == 0));
            Assert.AreEqual(4, sequence);
            Assert.AreEqual((byte)0x7B, packetCounter);
            AssertSonyCrc(report);
        }

        [TestMethod]
        public void HeadsetAndMicrophoneBitsDoNotChangeMediaGrammar()
        {
            byte[] opus = CreateOpusFrame();
            byte[] report = new byte[
                DualSenseBluetoothPadForgeSpeakerTransport.ReportLength];
            int sequence = 15;
            byte packetCounter = 0xFF;

            DualSenseBluetoothPadForgeSpeakerTransport.BuildReport(
                report, opus, opus.Length, headsetOnly: true,
                microphoneEnabled: true, ref sequence, ref packetCounter);

            Assert.AreEqual((byte)0xF0, report[1]);
            Assert.AreEqual((byte)0xFF, report[4]);
            Assert.AreEqual((byte)0x96, report[11]);
            Assert.AreEqual(0, sequence);
            Assert.AreEqual((byte)0x00, packetCounter);
            AssertSonyCrc(report);
        }

        private static byte[] CreateOpusFrame()
        {
            return Enumerable.Range(0,
                    DualSenseBluetoothPadForgeSpeakerTransport.OpusFrameLength)
                .Select(value => (byte)(value * 17 + 3))
                .ToArray();
        }

        private static void AssertSonyCrc(byte[] report)
        {
            uint expected =
                DualSenseBluetoothAudioReportPatcher.ComputeSonyCrc(report,
                    report.Length - sizeof(uint));
            uint actual = BinaryPrimitives.ReadUInt32LittleEndian(
                report.AsSpan(report.Length - sizeof(uint)));
            Assert.AreEqual(expected, actual);
        }
    }
}
