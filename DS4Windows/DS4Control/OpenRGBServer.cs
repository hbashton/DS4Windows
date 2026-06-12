/*
DS4Windows
Copyright (C) 2023  Travis Nickles

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <https://www.gnu.org/licenses/>.
*/

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using DS4Windows;

namespace DS4WinWPF.DS4Control
{
    // Lightweight OpenRGB SDK server (protocol v4).
    //
    // Add DS4Windows as a target in OpenRGB via Settings > SDK Client tab,
    // entering host=localhost port=6743. DS4 controller slots appear as
    // gamepad devices; OpenRGB can set lightbar colours which DS4LightBar
    // reads via TryGetColor(slot, out color).
    public sealed class OpenRGBServer : IDisposable
    {
        private static readonly Lazy<OpenRGBServer> _instance =
            new Lazy<OpenRGBServer>(() => new OpenRGBServer());
        public static OpenRGBServer Instance => _instance.Value;

        private const int PROTOCOL_VERSION = 4;
        private const int MAX_SLOTS = Global.MAX_DS4_CONTROLLER_COUNT;

        private readonly DS4Color[] pendingColors  = new DS4Color[MAX_SLOTS];
        private readonly bool[]     hasPendingColor = new bool[MAX_SLOTS];
        private readonly object     colorLock       = new object();

        private TcpListener listener;
        private volatile bool running;

        public bool IsRunning => running;

        private OpenRGBServer() { }

        public bool Start(int port = 6743)
        {
            if (running) Stop();
            try
            {
                listener = new TcpListener(IPAddress.Any, port);
                listener.Start();
                running = true;
                new Thread(AcceptLoop) { IsBackground = true, Name = "OpenRGBServerAccept" }.Start();
                return true;
            }
            catch
            {
                listener = null;
                return false;
            }
        }

        public void Stop()
        {
            running = false;
            try { listener?.Stop(); } catch { }
            listener = null;
            lock (colorLock)
            {
                for (int i = 0; i < MAX_SLOTS; i++)
                    hasPendingColor[i] = false;
            }
        }

        public bool TryGetColor(int slot, out DS4Color color)
        {
            if (slot < 0 || slot >= MAX_SLOTS || !running)
            {
                color = default;
                return false;
            }
            lock (colorLock)
            {
                if (!hasPendingColor[slot])
                {
                    color = default;
                    return false;
                }
                color = pendingColors[slot];
                return true;
            }
        }

        private void AcceptLoop()
        {
            while (running)
            {
                try
                {
                    TcpClient client = listener.AcceptTcpClient();
                    new Thread(() => HandleClient(client))
                    {
                        IsBackground = true,
                        Name = "OpenRGBServerClient"
                    }.Start();
                }
                catch
                {
                    if (!running) break;
                    Thread.Sleep(500);
                }
            }
        }

        private void HandleClient(TcpClient client)
        {
            try
            {
                client.ReceiveTimeout = 0;
                using NetworkStream stream = client.GetStream();

                while (running && client.Connected)
                {
                    byte[] header = ReadExact(stream, 16);
                    if (header == null) break;

                    if (header[0] != 'O' || header[1] != 'R' || header[2] != 'G' || header[3] != 'B')
                        break;

                    uint devIdx   = BitConverter.ToUInt32(header, 4);
                    uint pktId    = BitConverter.ToUInt32(header, 8);
                    uint dataSize = BitConverter.ToUInt32(header, 12);

                    byte[] payload = dataSize > 0 ? ReadExact(stream, (int)dataSize) : Array.Empty<byte>();
                    if (payload == null) break;

                    ProcessPacket(stream, devIdx, pktId, payload);
                }
            }
            catch { }
            finally { client.Close(); }
        }

        private void ProcessPacket(NetworkStream stream, uint devIdx, uint pktId, byte[] payload)
        {
            switch (pktId)
            {
                case 0: // REQUEST_CONTROLLER_COUNT
                    SendPacket(stream, 0, 0, BitConverter.GetBytes((uint)MAX_SLOTS));
                    break;

                case 1: // REQUEST_CONTROLLER_DATA
                    if (devIdx < MAX_SLOTS)
                    {
                        DS4Color current;
                        lock (colorLock)
                            current = hasPendingColor[devIdx]
                                ? pendingColors[devIdx]
                                : new DS4Color(0, 0, 255);
                        SendPacket(stream, devIdx, 1, BuildDeviceData((int)devIdx, current));
                    }
                    break;

                case 40: // REQUEST_PROTOCOL_VERSION
                    SendPacket(stream, 0, 40, BitConverter.GetBytes((uint)PROTOCOL_VERSION));
                    break;

                case 50: // SET_CLIENT_NAME
                    break;

                case 1050: // UPDATELEDS
                    if (devIdx < MAX_SLOTS && payload.Length >= 10)
                    {
                        int numColors = BitConverter.ToUInt16(payload, 4);
                        if (numColors > 0 && payload.Length >= 6 + numColors * 4)
                            SetSlotColor((int)devIdx, payload[6], payload[7], payload[8]);
                    }
                    break;

                case 1051: // UPDATEZONELEDS
                    if (devIdx < MAX_SLOTS && payload.Length >= 14)
                    {
                        int numColors = BitConverter.ToUInt16(payload, 8);
                        if (numColors > 0 && payload.Length >= 10 + numColors * 4)
                            SetSlotColor((int)devIdx, payload[10], payload[11], payload[12]);
                    }
                    break;

                case 1052: // UPDATESINGLELED
                    if (devIdx < MAX_SLOTS && payload.Length >= 8)
                        SetSlotColor((int)devIdx, payload[4], payload[5], payload[6]);
                    break;

                case 1100: // SETCUSTOMMODE
                    break;
            }
        }

        private void SetSlotColor(int slot, byte r, byte g, byte b)
        {
            lock (colorLock)
            {
                pendingColors[slot]   = new DS4Color(r, g, b);
                hasPendingColor[slot] = true;
            }
        }

        private static void SendPacket(NetworkStream stream, uint devIdx, uint pktId, byte[] data)
        {
            byte[] header = new byte[16];
            header[0] = (byte)'O'; header[1] = (byte)'R'; header[2] = (byte)'G'; header[3] = (byte)'B';
            BitConverter.GetBytes(devIdx).CopyTo(header, 4);
            BitConverter.GetBytes(pktId).CopyTo(header, 8);
            BitConverter.GetBytes((uint)(data?.Length ?? 0)).CopyTo(header, 12);
            stream.Write(header, 0, 16);
            if (data != null && data.Length > 0)
                stream.Write(data, 0, data.Length);
        }

        private static byte[] ReadExact(NetworkStream stream, int count)
        {
            byte[] buf = new byte[count];
            int read = 0;
            while (read < count)
            {
                int n = stream.Read(buf, read, count - read);
                if (n == 0) return null;
                read += n;
            }
            return buf;
        }

        private static byte[] BuildDeviceData(int slot, DS4Color currentColor)
        {
            using MemoryStream ms = new MemoryStream();
            using BinaryWriter w  = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

            w.Write((uint)0);       // total data_size placeholder
            w.Write((uint)14);      // device type: gamepad

            WriteString(w, $"DS4 Slot {slot + 1}");
            WriteString(w, "Sony");
            WriteString(w, "DualShock 4 Lightbar");
            WriteString(w, "1.0");
            WriteString(w, "");
            WriteString(w, "");

            w.Write((int)0);        // active_mode index
            w.Write((uint)1);       // num_modes
            WriteMode(w);
            w.Write((uint)1);       // num_zones
            WriteZone(w);
            w.Write((uint)1);       // num_leds
            WriteLed(w);

            w.Write((uint)1);       // num_colors
            w.Write(currentColor.red);
            w.Write(currentColor.green);
            w.Write(currentColor.blue);
            w.Write((byte)0);       // alpha

            byte[] result = ms.ToArray();
            BitConverter.GetBytes((uint)(result.Length - 4)).CopyTo(result, 0);
            return result;
        }

        private static void WriteMode(BinaryWriter w)
        {
            WriteString(w, "Static");
            w.Write((int)0);        // value
            w.Write((uint)0x20);    // flags: MODE_FLAG_HAS_PER_LED_COLOR
            w.Write((uint)0);       // speed_min
            w.Write((uint)0);       // speed_max
            w.Write((uint)0);       // brightness_min
            w.Write((uint)0);       // brightness_max
            w.Write((uint)0);       // colors_min
            w.Write((uint)0);       // colors_max
            w.Write((uint)0);       // speed
            w.Write((uint)0);       // brightness
            w.Write((uint)0);       // direction
            w.Write((uint)1);       // color_mode: COLOR_MODE_PER_LED
            w.Write((ushort)0);     // num embedded mode colors
        }

        private static void WriteZone(BinaryWriter w)
        {
            WriteString(w, "Lightbar");
            w.Write((uint)0);       // type: ZONE_TYPE_SINGLE
            w.Write((uint)1);       // leds_min
            w.Write((uint)1);       // leds_max
            w.Write((uint)1);       // num_leds
            w.Write((ushort)0);     // matrix_len (no matrix)
        }

        private static void WriteLed(BinaryWriter w)
        {
            WriteString(w, "Lightbar");
            w.Write((uint)0);       // value
        }

        private static void WriteString(BinaryWriter w, string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                w.Write((ushort)0);
                return;
            }
            byte[] bytes = Encoding.UTF8.GetBytes(s);
            w.Write((ushort)(bytes.Length + 1));
            w.Write(bytes);
            w.Write((byte)0);
        }

        public void Dispose() => Stop();
    }
}
