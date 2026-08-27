using System.Buffers.Binary;
using DS4Windows;
using DS4Windows.InputDevices;
using DS4WinWPF.DS4Control;

namespace DS4WindowsTests
{
    [TestClass]
    public class DualSenseRawInputStatusTests
    {
        [DataTestMethod]
        [DataRow(0, 64, false)]
        [DataRow(0, 64, true)]
        [DataRow(1, 78, false)]
        [DataRow(1, 78, true)]
        public void PhysicalStatusExtractionNormalizesUsbAndBluetoothOffsets(
            int reportOffset, int reportLength, bool isEdgeLayout)
        {
            byte[] report = new byte[reportLength];
            report[0] = reportOffset == 0 ? (byte)0x01 : (byte)0x31;
            BinaryPrimitives.WriteUInt32LittleEndian(
                report.AsSpan(28 + reportOffset, 4), 0x44332211u);
            report[41 + reportOffset] = 0x51;
            report[42 + reportOffset] = 0x62;
            report[43 + reportOffset] = 0x73;
            BinaryPrimitives.WriteUInt32LittleEndian(
                report.AsSpan(44 + reportOffset, 4), 0x88776655u);
            report[48 + reportOffset] = 0xA5;
            BinaryPrimitives.WriteUInt32LittleEndian(
                report.AsSpan(49 + reportOffset, 4), 0xCCBBAA99u);
            report[53 + reportOffset] = 0xDD;
            report[54 + reportOffset] = 0xEE;
            report[55 + reportOffset] = 0xF0;

            Assert.IsTrue(DualSenseDevice.TryExtractPhysicalInputStatus(
                report, reportOffset, isEdgeLayout,
                out DualSenseRawInputStatus status));
            AssertStatus(status, isEdgeLayout,
                sensorTimestamp: 0x44332211u,
                touchTimestamp: 0x51, rightFeedback: 0x62,
                leftFeedback: 0x73, hostTimestamp: 0x88776655u,
                effectModes: 0xA5, deviceTimestamp: 0xCCBBAA99u,
                battery: 0xDD, connection: 0xEE, raw55: 0xF0);
        }

        [TestMethod]
        public void InvalidPhysicalStatusRangeClearsObservation()
        {
            Assert.IsFalse(DualSenseDevice.TryExtractPhysicalInputStatus(
                new byte[55], 0, out DualSenseRawInputStatus tooShort));
            Assert.IsFalse(tooShort.IsValid);
            Assert.IsFalse(DualSenseDevice.TryExtractPhysicalInputStatus(
                new byte[78], 2, out DualSenseRawInputStatus badOffset));
            Assert.IsFalse(badOffset.IsValid);
        }

        [TestMethod]
        public void SameReportStatusSurvivesAllMappingScratchCopies()
        {
            DualSenseRawInputStatus expected = CreateStatus(0x10, 0x20,
                0xA5, 0x12345678u, isEdgeLayout: true);
            DS4State source = new()
            {
                Cross = true,
                ReportTimeStamp = DateTime.UtcNow,
                DualSenseRawInputStatus = expected,
            };

            DS4State constructed = new(source);
            DS4State copied = new();
            source.CopyTo(copied);
            DS4State extras = new();
            source.CopyExtrasTo(extras);

            Debouncer debouncer = new(TimeSpan.FromMilliseconds(10));
            debouncer.AddDebouncer(nameof(DS4State.Cross));
            DS4State debounced = debouncer.ProcessInput(source);

            Assert.AreEqual(expected, constructed.DualSenseRawInputStatus);
            Assert.AreEqual(expected, copied.DualSenseRawInputStatus);
            Assert.AreEqual(expected, extras.DualSenseRawInputStatus);
            Assert.AreEqual(expected, debounced.DualSenseRawInputStatus);
            Assert.AreEqual(expected, ViiperStatePacketBuilder.
                BuildMappedState(extras, -1).RawInputStatus);
        }

        internal static DualSenseRawInputStatus CreateStatus(
            byte rightFeedback, byte leftFeedback, byte effectModes,
            uint sensorTimestamp, uint hostTimestamp = 0x0A0B0C0Du,
            uint deviceTimestamp = 0x01020304u,
            byte touchTimestamp = 0x55, byte battery = 0x66,
            byte connection = 0x77, byte raw55 = 0x88,
            bool isEdgeLayout = false)
        {
            return new DualSenseRawInputStatus
            {
                IsValid = true,
                IsEdgeLayout = isEdgeLayout,
                SensorTimestamp = sensorTimestamp,
                TouchTimestamp = touchTimestamp,
                RightTriggerFeedback = rightFeedback,
                LeftTriggerFeedback = leftFeedback,
                HostTimestamp = hostTimestamp,
                TriggerEffectModes = effectModes,
                DeviceTimestamp = deviceTimestamp,
                BatteryStatus = battery,
                ConnectionStatus = connection,
                Raw55 = raw55,
            };
        }

        internal static void AssertStatus(
            in DualSenseRawInputStatus actual, bool isEdgeLayout,
            uint sensorTimestamp,
            byte touchTimestamp, byte rightFeedback, byte leftFeedback,
            uint hostTimestamp, byte effectModes, uint deviceTimestamp,
            byte battery, byte connection, byte raw55)
        {
            Assert.IsTrue(actual.IsValid);
            Assert.AreEqual(isEdgeLayout, actual.IsEdgeLayout);
            Assert.AreEqual(sensorTimestamp, actual.SensorTimestamp);
            Assert.AreEqual(touchTimestamp, actual.TouchTimestamp);
            Assert.AreEqual(rightFeedback, actual.RightTriggerFeedback);
            Assert.AreEqual(leftFeedback, actual.LeftTriggerFeedback);
            Assert.AreEqual(hostTimestamp, actual.HostTimestamp);
            Assert.AreEqual(effectModes, actual.TriggerEffectModes);
            Assert.AreEqual(deviceTimestamp, actual.DeviceTimestamp);
            Assert.AreEqual(battery, actual.BatteryStatus);
            Assert.AreEqual(connection, actual.ConnectionStatus);
            Assert.AreEqual(raw55, actual.Raw55);
        }
    }
}
