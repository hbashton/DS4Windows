using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.Json;
using DS4Windows.DS4Control;
using DS4Windows.InputDevices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4WindowsTests
{
    [TestClass]
    public class DSXUdpServerTests
    {
        [TestMethod]
        public void TestTriggerEncoding_Off()
        {
            byte[] bytes = DSXUdpServer.EncodeTriggerPayload(0, Array.Empty<byte>());
            Assert.AreEqual(11, bytes.Length);
            Assert.AreEqual(0x05, bytes[0]);
        }

        [TestMethod]
        public void TestTriggerEncoding_Feedback()
        {
            // Mode 13 / 21 = Feedback: startPos=2, force=6
            byte[] bytes = DSXUdpServer.EncodeTriggerPayload(21, new byte[] { 2, 6 });
            Assert.AreEqual(11, bytes.Length);
            Assert.AreEqual(0x21, bytes[0]); // Mode 33 (Feedback)
            
            // Zone mask should start from zone 2
            ushort zoneMask = (ushort)(bytes[1] | (bytes[2] << 8));
            Assert.IsTrue((zoneMask & (1 << 2)) != 0);
            Assert.IsTrue((zoneMask & (1 << 0)) == 0);
        }

        [TestMethod]
        public void TestTriggerEncoding_Weapon()
        {
            // Mode 16 / 22 = Weapon: start=2, end=6, force=5
            byte[] bytes = DSXUdpServer.EncodeTriggerPayload(22, new byte[] { 2, 6, 5 });
            Assert.AreEqual(11, bytes.Length);
            Assert.AreEqual(0x25, bytes[0]); // Mode 37 (Weapon)
            ushort zoneMask = (ushort)(bytes[1] | (bytes[2] << 8));
            Assert.AreEqual((1 << 2) | (1 << 6), zoneMask);
            Assert.AreEqual(4, bytes[3]); // strength - 1
        }

        [TestMethod]
        public void TestTriggerEncoding_Vibration()
        {
            // Mode 8 / 23 = Vibration: pos=0, amp=5, freq=25
            byte[] bytes = DSXUdpServer.EncodeTriggerPayload(23, new byte[] { 0, 5, 25 });
            Assert.AreEqual(11, bytes.Length);
            Assert.AreEqual(0x26, bytes[0]); // Mode 38 (Vibration)
            Assert.AreEqual(25, bytes[9]); // frequency
        }

        [TestMethod]
        public void TestTriggerEncoding_Bow()
        {
            // Mode 14 = Bow: start=1, end=7, strength=5, snapForce=4
            byte[] bytes = DSXUdpServer.EncodeTriggerPayload(14, new byte[] { 1, 7, 5, 4 });
            Assert.AreEqual(11, bytes.Length);
            Assert.AreEqual(0x22, bytes[0]); // Mode 34 (Bow)
            ushort zoneMask = (ushort)(bytes[1] | (bytes[2] << 8));
            Assert.AreEqual((1 << 1) | (1 << 7), zoneMask);
        }

        [TestMethod]
        public void TestTriggerEncoding_Galloping()
        {
            // Mode 15 = Galloping: start=0, end=8, firstFoot=1, secondFoot=3, freq=12
            byte[] bytes = DSXUdpServer.EncodeTriggerPayload(15, new byte[] { 0, 8, 1, 3, 12 });
            Assert.AreEqual(11, bytes.Length);
            Assert.AreEqual(0x23, bytes[0]); // Mode 35 (Galloping)
            Assert.AreEqual(12, bytes[4]); // freq
        }

        [TestMethod]
        public void TestTriggerEncoding_Machine()
        {
            // Mode 18 = Machine: start=1, end=8, ampA=2, ampB=5, freq=15, period=4
            byte[] bytes = DSXUdpServer.EncodeTriggerPayload(18, new byte[] { 1, 8, 2, 5, 15, 4 });
            Assert.AreEqual(11, bytes.Length);
            Assert.AreEqual(0x27, bytes[0]); // Mode 39 (Machine)
            Assert.AreEqual(15, bytes[4]); // freq
            Assert.AreEqual(4, bytes[5]); // period
        }

        [TestMethod]
        public void TestJsonPacketParsing_TriggerUpdate()
        {
            using DSXUdpServer server = new DSXUdpServer();
            bool triggerUpdated = false;
            int receivedIdx = -1;
            TriggerId receivedTrigger = TriggerId.LeftTrigger;
            byte[] receivedBytes = null;

            server.OnTriggerUpdate += (controllerIndex, trigger, rawTriggerData) =>
            {
                triggerUpdated = true;
                receivedIdx = controllerIndex;
                receivedTrigger = trigger;
                receivedBytes = rawTriggerData;
            };

            string json = "{\"instructions\":[{\"type\":\"TriggerUpdate\",\"parameters\":[0,1,22,\"2,7,6\"]}]}";
            byte[] packetBytes = Encoding.UTF8.GetBytes(json);

            server.ProcessIncomingPacket(packetBytes, new IPEndPoint(IPAddress.Loopback, 12345));

            Assert.IsTrue(triggerUpdated);
            Assert.AreEqual(0, receivedIdx);
            Assert.AreEqual(TriggerId.RightTrigger, receivedTrigger);
            Assert.IsNotNull(receivedBytes);
            Assert.AreEqual(0x25, receivedBytes[0]); // Weapon mode
        }

        [TestMethod]
        public void TestJsonPacketParsing_NumericTypes()
        {
            using DSXUdpServer server = new DSXUdpServer();
            bool triggerUpdated = false;
            TriggerId receivedTrigger = TriggerId.RightTrigger;
            byte[] receivedBytes = null;

            server.OnTriggerUpdate += (controllerIndex, trigger, rawTriggerData) =>
            {
                triggerUpdated = true;
                receivedTrigger = trigger;
                receivedBytes = rawTriggerData;
            };

            // type 1 = TriggerUpdate, trigger 0 = Left Trigger, mode 21 = Feedback
            string json = "{\"instructions\":[{\"type\":1,\"parameters\":[0,0,21,0,8]}]}";
            byte[] packetBytes = Encoding.UTF8.GetBytes(json);

            server.ProcessIncomingPacket(packetBytes, new IPEndPoint(IPAddress.Loopback, 12345));

            Assert.IsTrue(triggerUpdated);
            Assert.AreEqual(TriggerId.LeftTrigger, receivedTrigger);
            Assert.IsNotNull(receivedBytes);
            Assert.AreEqual(0x21, receivedBytes[0]); // Feedback mode
        }

        [TestMethod]
        public void TestJsonPacketParsing_RGBUpdate()
        {
            using DSXUdpServer server = new DSXUdpServer();
            bool rgbUpdated = false;
            byte rVal = 0, gVal = 0, bVal = 0, aVal = 0;

            server.OnRGBUpdate += (controllerIndex, r, g, b, a) =>
            {
                rgbUpdated = true;
                rVal = r;
                gVal = g;
                bVal = b;
                aVal = a;
            };

            string json = "{\"instructions\":[{\"type\":\"RGBUpdate\",\"parameters\":[0,255,100,50,200]}]}";
            byte[] packetBytes = Encoding.UTF8.GetBytes(json);

            server.ProcessIncomingPacket(packetBytes, new IPEndPoint(IPAddress.Loopback, 12345));

            Assert.IsTrue(rgbUpdated);
            Assert.AreEqual(255, rVal);
            Assert.AreEqual(100, gVal);
            Assert.AreEqual(50, bVal);
            Assert.AreEqual(200, aVal);
        }

        [TestMethod]
        public void TestJsonPacketParsing_ResetUserSettings()
        {
            using DSXUdpServer server = new DSXUdpServer();
            bool resetCalled = false;
            int resetIdx = -1;

            server.OnResetUserSettings += (controllerIndex) =>
            {
                resetCalled = true;
                resetIdx = controllerIndex;
            };

            string json = "{\"instructions\":[{\"type\":7,\"parameters\":[2]}]}";
            byte[] packetBytes = Encoding.UTF8.GetBytes(json);

            server.ProcessIncomingPacket(packetBytes, new IPEndPoint(IPAddress.Loopback, 12345));

            Assert.IsTrue(resetCalled);
            Assert.AreEqual(2, resetIdx);
        }

        [TestMethod]
        public void TestStatusResponseSerialization()
        {
            var response = new DSXStatusResponse
            {
                Status = "Running",
                TimeReceived = "2026-09-01 12:00:00",
                isControllerConnected = true,
                BatteryLevel = 90,
                Devices = new List<DSXDeviceInfo>
                {
                    new DSXDeviceInfo
                    {
                        Index = 0,
                        MacAddress = "00:11:22:33:44:55",
                        DeviceType = 0,
                        ConnectionType = 1,
                        BatteryLevel = 90
                    }
                }
            };

            string json = JsonSerializer.Serialize(response);
            Assert.IsTrue(json.Contains("\"isControllerConnected\":true"));
            Assert.IsTrue(json.Contains("\"BatteryLevel\":90"));
            Assert.IsTrue(json.Contains("00:11:22:33:44:55"));
        }
    }
}
