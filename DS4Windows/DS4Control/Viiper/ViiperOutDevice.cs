/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace DS4Windows
{
    public enum ViiperVirtualDeviceType
    {
        Xbox360,
        DualShock4,
        DualSense,
        DualSenseEdge,
        Switch2Pro,
    }

    public sealed class ViiperOutDevice : OutputDevice
    {
        private const string DefaultHost = "127.0.0.1";
        private const int DefaultPort = 3242;

        private readonly OutContType outputType;
        private readonly ViiperVirtualDeviceType viiperType;
        private readonly ViiperClient client;
        private readonly object streamWriteLock = new object();
        private ViiperDeviceStream deviceStream;
        private Thread feedbackThread;
        private int lastInputDeviceIndex = -1;
        private int submitFailureLogged;

        public ViiperOutDevice(OutContType outputType, ViiperVirtualDeviceType viiperType)
        {
            this.outputType = outputType;
            this.viiperType = viiperType;
            client = new ViiperClient(DefaultHost, DefaultPort);
        }

        public override void Connect()
        {
            ViiperPrerequisiteStatus status = ViiperSetupManager.GetStatus(tryStartServer: true);
            if (!status.Ready)
            {
                throw new IOException(
                    $"{status.DisplayText}. Use Settings > VIIPER Virtual Controller Support to install or repair VIIPER and usbip-win2.");
            }

            deviceStream = client.CreateDeviceAndOpenStream(viiperType);
            Volatile.Write(ref submitFailureLogged, 0);
            Volatile.Write(ref lastInputDeviceIndex, -1);
            connected = true;
            ResetState();
            StartFeedbackReader();
        }

        public override void Disconnect()
        {
            connected = false;
            ViiperDeviceStream stream = Interlocked.Exchange(ref deviceStream, null);
            stream?.Dispose();
            if (feedbackThread != null && feedbackThread.IsAlive)
            {
                feedbackThread.Join(200);
            }

            feedbackThread = null;
        }

        public override void ConvertandSendReport(DS4State state, int device)
        {
            Volatile.Write(ref lastInputDeviceIndex, device);
            if (!connected)
            {
                return;
            }

            try
            {
                WriteState(ViiperStatePacketBuilder.Build(viiperType, state, device));
            }
            catch (IOException ex)
            {
                LogSubmitFailure(ex.Message);
            }
            catch (SocketException ex)
            {
                LogSubmitFailure(ex.Message);
            }
            catch (ObjectDisposedException ex)
            {
                LogSubmitFailure(ex.Message);
            }
        }

        public override void ResetState(bool submit = true)
        {
            if (!submit || !connected)
            {
                return;
            }

            try
            {
                WriteState(ViiperStatePacketBuilder.Build(viiperType, new DS4State(), -1));
            }
            catch (IOException ex)
            {
                LogSubmitFailure(ex.Message);
            }
            catch (SocketException ex)
            {
                LogSubmitFailure(ex.Message);
            }
            catch (ObjectDisposedException ex)
            {
                LogSubmitFailure(ex.Message);
            }
        }

        public override string GetDeviceType() => outputType.ToString();

        public override void RemoveFeedbacks()
        {
        }

        public override void RemoveFeedback(int inIdx)
        {
            _ = inIdx;
        }

        public static bool IsViiperType(OutContType type)
        {
            return type == OutContType.ViiperX360 ||
                type == OutContType.ViiperDS4 ||
                type == OutContType.ViiperDualSense ||
                type == OutContType.ViiperDualSenseEdge ||
                type == OutContType.ViiperSwitch2Pro;
        }

        private void WriteState(byte[] data)
        {
            ViiperDeviceStream stream = deviceStream;
            if (stream == null)
            {
                throw new ObjectDisposedException(nameof(ViiperDeviceStream));
            }

            lock (streamWriteLock)
            {
                stream.Write(data);
            }
        }

        private void StartFeedbackReader()
        {
            int length = ViiperStatePacketBuilder.GetFeedbackLength(viiperType);
            if (length <= 0)
            {
                return;
            }

            feedbackThread = new Thread(() => FeedbackReadLoop(length))
            {
                IsBackground = true,
                Name = $"VIIPER {viiperType} feedback",
            };
            feedbackThread.Start();
        }

        private void FeedbackReadLoop(int feedbackLength)
        {
            byte[] buffer = new byte[feedbackLength];
            try
            {
                while (connected)
                {
                    ViiperDeviceStream stream = deviceStream;
                    if (stream == null)
                    {
                        return;
                    }

                    stream.ReadExactly(buffer, 0, feedbackLength);
                    ApplyFeedback(buffer);
                }
            }
            catch (IOException)
            {
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void ApplyFeedback(byte[] feedback)
        {
            int deviceIndex = Volatile.Read(ref lastInputDeviceIndex);
            if (deviceIndex < 0 ||
                Program.rootHub == null ||
                deviceIndex >= Program.rootHub.DS4Controllers.Length ||
                !Global.EnableOutputDataToDS4[deviceIndex])
            {
                return;
            }

            DS4Device device = Program.rootHub.DS4Controllers[deviceIndex];
            if (device == null)
            {
                return;
            }

            switch (viiperType)
            {
                case ViiperVirtualDeviceType.Xbox360:
                    if (feedback.Length >= 2)
                    {
                        Program.rootHub.SetDevRumble(device, feedback[0], feedback[1], deviceIndex);
                    }
                    break;

                case ViiperVirtualDeviceType.DualShock4:
                    if (feedback.Length >= 7)
                    {
                        Program.rootHub.SetDevRumble(device, feedback[1], feedback[0], deviceIndex);
                        ApplyLightbar(device, feedback[2], feedback[3], feedback[4], feedback[5], feedback[6]);
                    }
                    break;

                case ViiperVirtualDeviceType.DualSense:
                case ViiperVirtualDeviceType.DualSenseEdge:
                    if (feedback.Length >= 6)
                    {
                        Program.rootHub.SetDevRumble(device, feedback[1], feedback[0], deviceIndex);
                        ApplyLightbar(device, feedback[2], feedback[3], feedback[4], 0, 0);
                    }
                    break;

                case ViiperVirtualDeviceType.Switch2Pro:
                    if (feedback.Length >= 34)
                    {
                        byte left = MaxByte(feedback, 0, 16);
                        byte right = MaxByte(feedback, 16, 16);
                        Program.rootHub.SetDevRumble(device, left, right, deviceIndex);
                    }
                    break;
            }
        }

        private static void ApplyLightbar(DS4Device device, byte red, byte green, byte blue, byte flashOn, byte flashOff)
        {
            DS4LightbarState lightState = new DS4LightbarState
            {
                LightBarColor = new DS4Color(red, green, blue),
                LightBarFlashDurationOn = flashOn,
                LightBarFlashDurationOff = flashOff,
            };
            device.SetLightbarState(ref lightState);
        }

        private static byte MaxByte(byte[] data, int start, int count)
        {
            byte result = 0;
            int end = Math.Min(data.Length, start + count);
            for (int i = start; i < end; i++)
            {
                if (data[i] > result)
                {
                    result = data[i];
                }
            }

            return result;
        }

        private void LogSubmitFailure(string message)
        {
            connected = false;
            Disconnect();
            if (Interlocked.Exchange(ref submitFailureLogged, 1) == 1)
            {
                return;
            }

            AppLogger.LogToGui($"VIIPER {viiperType} output stopped: {message}", true);
        }
    }

    internal sealed class ViiperClient
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        };

        private readonly string host;
        private readonly int port;

        public ViiperClient(string host, int port)
        {
            this.host = host;
            this.port = port;
        }

        public ViiperDeviceStream CreateDeviceAndOpenStream(ViiperVirtualDeviceType deviceType)
        {
            ViiperBusCreateResponse bus = SendRequest<ViiperBusCreateResponse>("bus/create", "0");
            string payload = JsonSerializer.Serialize(new ViiperDeviceCreateRequest
            {
                Type = ViiperStatePacketBuilder.GetViiperDeviceName(deviceType),
            }, JsonOptions);

            ViiperDeviceResponse device = SendRequest<ViiperDeviceResponse>($"bus/{bus.BusId}/add", payload);
            try
            {
                return OpenStream(bus.BusId, device.DevId);
            }
            catch
            {
                TryRemoveDevice(bus.BusId, device.DevId);
                throw;
            }
        }

        private ViiperDeviceStream OpenStream(uint busId, string devId)
        {
            TcpClient tcp = Connect();
            try
            {
                NetworkStream stream = tcp.GetStream();
                byte[] request = Encoding.UTF8.GetBytes($"bus/{busId}/{devId}\0");
                stream.Write(request, 0, request.Length);
                return new ViiperDeviceStream(tcp, busId, devId, RemoveDevice);
            }
            catch
            {
                tcp.Dispose();
                throw;
            }
        }

        private void RemoveDevice(uint busId, string devId)
        {
            TryRemoveDevice(busId, devId);
        }

        private void TryRemoveDevice(uint busId, string devId)
        {
            try
            {
                SendRequestRaw($"bus/{busId}/remove", devId);
            }
            catch
            {
            }
        }

        private T SendRequest<T>(string path, string payload = null)
        {
            string raw = SendRequestRaw(path, payload);
            if (string.IsNullOrWhiteSpace(raw))
            {
                throw new IOException("VIIPER returned an empty response.");
            }

            ViiperApiError apiError = JsonSerializer.Deserialize<ViiperApiError>(raw, JsonOptions);
            if (apiError != null && (apiError.Status != 0 || !string.IsNullOrEmpty(apiError.Title)))
            {
                throw new IOException($"VIIPER API error {apiError.Status} {apiError.Title}: {apiError.Detail}");
            }

            return JsonSerializer.Deserialize<T>(raw, JsonOptions);
        }

        private string SendRequestRaw(string path, string payload = null)
        {
            using TcpClient tcp = Connect();
            NetworkStream stream = tcp.GetStream();
            string request = string.IsNullOrEmpty(payload) ? path : $"{path} {payload}";
            byte[] requestBytes = Encoding.UTF8.GetBytes(request + "\0");
            stream.Write(requestBytes, 0, requestBytes.Length);

            using MemoryStream response = new MemoryStream();
            byte[] buffer = new byte[1024];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                response.Write(buffer, 0, read);
            }

            return Encoding.UTF8.GetString(response.ToArray()).TrimEnd('\n');
        }

        private TcpClient Connect()
        {
            TcpClient tcp = new TcpClient
            {
                NoDelay = true,
                SendTimeout = 1000,
                ReceiveTimeout = 5000,
            };

            IAsyncResult result = tcp.BeginConnect(host, port, null, null);
            if (!result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(3)))
            {
                tcp.Dispose();
                throw new IOException($"Could not connect to VIIPER at {host}:{port}. Start VIIPER server with its API listening on port {port}.");
            }

            try
            {
                tcp.EndConnect(result);
            }
            catch (SocketException ex)
            {
                tcp.Dispose();
                throw new IOException($"Could not connect to VIIPER at {host}:{port}: {ex.Message}", ex);
            }

            return tcp;
        }

        private sealed class ViiperBusCreateResponse
        {
            [JsonPropertyName("busId")]
            public uint BusId { get; set; }
        }

        private sealed class ViiperDeviceResponse
        {
            [JsonPropertyName("devId")]
            public string DevId { get; set; }
        }

        private sealed class ViiperDeviceCreateRequest
        {
            [JsonPropertyName("type")]
            public string Type { get; set; }
        }

        private sealed class ViiperApiError
        {
            [JsonPropertyName("status")]
            public int Status { get; set; }

            [JsonPropertyName("title")]
            public string Title { get; set; }

            [JsonPropertyName("detail")]
            public string Detail { get; set; }
        }
    }

    internal sealed class ViiperDeviceStream : IDisposable
    {
        private readonly TcpClient tcp;
        private readonly NetworkStream stream;
        private readonly uint busId;
        private readonly string devId;
        private readonly Action<uint, string> removeDevice;
        private bool disposed;

        public ViiperDeviceStream(TcpClient tcp, uint busId, string devId, Action<uint, string> removeDevice)
        {
            this.tcp = tcp;
            this.stream = tcp.GetStream();
            this.busId = busId;
            this.devId = devId;
            this.removeDevice = removeDevice;
        }

        public void Write(byte[] data)
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(ViiperDeviceStream));
            }

            stream.Write(data, 0, data.Length);
        }

        public void ReadExactly(byte[] buffer, int offset, int count)
        {
            int total = 0;
            while (total < count)
            {
                int read = stream.Read(buffer, offset + total, count - total);
                if (read == 0)
                {
                    throw new IOException("VIIPER device stream closed.");
                }

                total += read;
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            try
            {
                stream.Dispose();
            }
            catch
            {
            }

            try
            {
                tcp.Dispose();
            }
            catch
            {
            }

            removeDevice?.Invoke(busId, devId);
        }
    }

    internal static class ViiperStatePacketBuilder
    {
        private const int X360PacketSize = 20;
        private const int DS4PacketSize = 31;
        private const int DualSensePacketSize = 33;
        private const int Switch2PacketSize = 24;
        private const float X360RecipInputPosResolution = 1 / 127f;
        private const float X360RecipInputNegResolution = 1 / 128f;
        private const int X360OutputResolution = 32767 - (-32768);

        public static string GetViiperDeviceName(ViiperVirtualDeviceType type)
        {
            return type switch
            {
                ViiperVirtualDeviceType.Xbox360 => "xbox360",
                ViiperVirtualDeviceType.DualShock4 => "dualshock4",
                ViiperVirtualDeviceType.DualSense => "dualsense",
                ViiperVirtualDeviceType.DualSenseEdge => "dualsenseedge",
                ViiperVirtualDeviceType.Switch2Pro => "ns2pro",
                _ => "xbox360",
            };
        }

        public static int GetFeedbackLength(ViiperVirtualDeviceType type)
        {
            return type switch
            {
                ViiperVirtualDeviceType.Xbox360 => 2,
                ViiperVirtualDeviceType.DualShock4 => 7,
                ViiperVirtualDeviceType.DualSense => 6,
                ViiperVirtualDeviceType.DualSenseEdge => 6,
                ViiperVirtualDeviceType.Switch2Pro => 34,
                _ => 0,
            };
        }

        public static byte[] Build(ViiperVirtualDeviceType type, DS4State state, int device)
        {
            return type switch
            {
                ViiperVirtualDeviceType.Xbox360 => BuildXbox360(state, device),
                ViiperVirtualDeviceType.DualShock4 => BuildDualShock4(state, device),
                ViiperVirtualDeviceType.DualSense => BuildDualSense(state, device),
                ViiperVirtualDeviceType.DualSenseEdge => BuildDualSense(state, device),
                ViiperVirtualDeviceType.Switch2Pro => BuildSwitch2Pro(state, device),
                _ => BuildXbox360(state, device),
            };
        }

        private static byte[] BuildXbox360(DS4State state, int device)
        {
            byte[] packet = new byte[X360PacketSize];
            uint buttons = 0;
            if (state.DpadUp) buttons |= 0x0001;
            if (state.DpadDown) buttons |= 0x0002;
            if (state.DpadLeft) buttons |= 0x0004;
            if (state.DpadRight) buttons |= 0x0008;
            if (state.Options) buttons |= 0x0010;
            if (state.Share) buttons |= 0x0020;
            if (state.L3) buttons |= 0x0040;
            if (state.R3) buttons |= 0x0080;
            if (state.L1) buttons |= 0x0100;
            if (state.R1) buttons |= 0x0200;
            if (state.PS) buttons |= 0x0400;
            if (state.Cross) buttons |= 0x1000;
            if (state.Circle) buttons |= 0x2000;
            if (state.Square) buttons |= 0x4000;
            if (state.Triangle) buttons |= 0x8000;

            byte l2 = state.L2;
            byte r2 = state.R2;
            short lx = AxisScaleX360(state.LX, false);
            short ly = AxisScaleX360(state.LY, true);
            short rx = AxisScaleX360(state.RX, false);
            short ry = AxisScaleX360(state.RY, true);

            ApplySteeringWheelX360(state, device, ref l2, ref r2, ref lx, ref ly, ref rx, ref ry);

            WriteUInt32(packet, 0, buttons);
            packet[4] = l2;
            packet[5] = r2;
            WriteInt16(packet, 6, lx);
            WriteInt16(packet, 8, ly);
            WriteInt16(packet, 10, rx);
            WriteInt16(packet, 12, ry);
            return packet;
        }

        private static byte[] BuildDualShock4(DS4State state, int device)
        {
            byte[] packet = new byte[DS4PacketSize];
            byte lx = state.LX;
            byte ly = state.LY;
            byte rx = state.RX;
            byte ry = state.RY;
            byte l2 = state.L2;
            byte r2 = state.R2;
            ApplySteeringWheelByteAxes(state, device, ref l2, ref r2, ref lx, ref ly, ref rx, ref ry);

            packet[0] = ToSignedAxisByte(lx);
            packet[1] = ToSignedAxisByte(ly);
            packet[2] = ToSignedAxisByte(rx);
            packet[3] = ToSignedAxisByte(ry);
            WriteUInt16(packet, 4, BuildDualShock4Buttons(state));
            packet[6] = BuildDPadBits(state);
            packet[7] = l2;
            packet[8] = r2;
            WriteTouch(packet, 9, state.TrackPadTouch0, 1920, 942);
            WriteTouch(packet, 14, state.TrackPadTouch1, 1920, 942);
            WriteMotion(packet, 19, state);
            return packet;
        }

        private static byte[] BuildDualSense(DS4State state, int device)
        {
            byte[] packet = new byte[DualSensePacketSize];
            byte lx = state.LX;
            byte ly = state.LY;
            byte rx = state.RX;
            byte ry = state.RY;
            byte l2 = state.L2;
            byte r2 = state.R2;
            ApplySteeringWheelByteAxes(state, device, ref l2, ref r2, ref lx, ref ly, ref rx, ref ry);

            packet[0] = ToSignedAxisByte(lx);
            packet[1] = ToSignedAxisByte(ly);
            packet[2] = ToSignedAxisByte(rx);
            packet[3] = ToSignedAxisByte(ry);
            WriteUInt32(packet, 4, BuildDualSenseButtons(state));
            packet[8] = BuildDPadBits(state);
            packet[9] = l2;
            packet[10] = r2;
            WriteTouch(packet, 11, state.TrackPadTouch0, 1920, 1080);
            WriteTouch(packet, 16, state.TrackPadTouch1, 1920, 1080);
            WriteMotion(packet, 21, state);
            return packet;
        }

        private static byte[] BuildSwitch2Pro(DS4State state, int device)
        {
            byte[] packet = new byte[Switch2PacketSize];
            ushort lx = ScaleSwitchAxis(state.LX);
            ushort ly = ScaleSwitchAxis(state.LY);
            ushort rx = ScaleSwitchAxis(state.RX);
            ushort ry = ScaleSwitchAxis(state.RY);
            ApplySteeringWheelSwitchAxes(state, device, ref lx, ref ly, ref rx, ref ry);

            WriteUInt32(packet, 0, BuildSwitch2Buttons(state));
            WriteUInt16(packet, 4, lx);
            WriteUInt16(packet, 6, ly);
            WriteUInt16(packet, 8, rx);
            WriteUInt16(packet, 10, ry);
            WriteInt16(packet, 12, ClampShort(state.Motion?.accelXFull ?? 0));
            WriteInt16(packet, 14, ClampShort(state.Motion?.accelYFull ?? 0));
            WriteInt16(packet, 16, ClampShort(state.Motion?.accelZFull ?? 0));
            WriteInt16(packet, 18, ClampShort(state.Motion?.gyroYawFull ?? 0));
            WriteInt16(packet, 20, ClampShort(state.Motion?.gyroPitchFull ?? 0));
            WriteInt16(packet, 22, ClampShort(state.Motion?.gyroRollFull ?? 0));
            return packet;
        }

        private static ushort BuildDualShock4Buttons(DS4State state)
        {
            ushort buttons = 0;
            if (state.Square) buttons |= 0x0010;
            if (state.Cross) buttons |= 0x0020;
            if (state.Circle) buttons |= 0x0040;
            if (state.Triangle) buttons |= 0x0080;
            if (state.L1) buttons |= 0x0100;
            if (state.R1) buttons |= 0x0200;
            if (state.L2Btn || state.L2 > 0) buttons |= 0x0400;
            if (state.R2Btn || state.R2 > 0) buttons |= 0x0800;
            if (state.Share) buttons |= 0x1000;
            if (state.Options) buttons |= 0x2000;
            if (state.L3) buttons |= 0x4000;
            if (state.R3) buttons |= 0x8000;
            if (state.PS) buttons |= 0x0001;
            if (state.OutputTouchButton || state.TouchButton) buttons |= 0x0002;
            return buttons;
        }

        private static uint BuildDualSenseButtons(DS4State state)
        {
            uint buttons = 0;
            if (state.Square) buttons |= 0x00000010;
            if (state.Cross) buttons |= 0x00000020;
            if (state.Circle) buttons |= 0x00000040;
            if (state.Triangle) buttons |= 0x00000080;
            if (state.L1) buttons |= 0x00000100;
            if (state.R1) buttons |= 0x00000200;
            if (state.L2Btn || state.L2 > 0) buttons |= 0x00000400;
            if (state.R2Btn || state.R2 > 0) buttons |= 0x00000800;
            if (state.Share) buttons |= 0x00001000;
            if (state.Options) buttons |= 0x00002000;
            if (state.L3) buttons |= 0x00004000;
            if (state.R3) buttons |= 0x00008000;
            if (state.PS) buttons |= 0x00010000;
            if (state.OutputTouchButton || state.TouchButton) buttons |= 0x00020000;
            if (state.Mute) buttons |= 0x00040000;
            if (state.FnL) buttons |= 0x00100000;
            if (state.FnR) buttons |= 0x00200000;
            if (state.BLP) buttons |= 0x00400000;
            if (state.BRP) buttons |= 0x00800000;
            return buttons;
        }

        private static uint BuildSwitch2Buttons(DS4State state)
        {
            uint buttons = 0;
            if (state.Cross) buttons |= 1u << 0;
            if (state.Circle) buttons |= 1u << 1;
            if (state.Square) buttons |= 1u << 2;
            if (state.Triangle) buttons |= 1u << 3;
            if (state.R1) buttons |= 1u << 4;
            if (state.R2Btn || state.R2 > 0) buttons |= 1u << 5;
            if (state.Options) buttons |= 1u << 6;
            if (state.R3) buttons |= 1u << 7;
            if (state.DpadDown) buttons |= 1u << 8;
            if (state.DpadRight) buttons |= 1u << 9;
            if (state.DpadLeft) buttons |= 1u << 10;
            if (state.DpadUp) buttons |= 1u << 11;
            if (state.L1) buttons |= 1u << 12;
            if (state.L2Btn || state.L2 > 0) buttons |= 1u << 13;
            if (state.Share) buttons |= 1u << 14;
            if (state.L3) buttons |= 1u << 15;
            if (state.PS) buttons |= 1u << 16;
            if (state.Capture) buttons |= 1u << 17;
            if (state.FnR || state.BRP || state.SideR) buttons |= 1u << 18;
            if (state.FnL || state.BLP || state.SideL) buttons |= 1u << 19;
            if (state.Mute) buttons |= 1u << 21;
            return buttons;
        }

        private static byte BuildDPadBits(DS4State state)
        {
            byte dpad = 0;
            if (state.DpadUp) dpad |= 0x01;
            if (state.DpadDown) dpad |= 0x02;
            if (state.DpadLeft) dpad |= 0x04;
            if (state.DpadRight) dpad |= 0x08;
            return dpad;
        }

        private static void WriteTouch(byte[] packet, int offset, DS4State.TrackPadTouch touch, int maxX, int maxY)
        {
            ushort x = (ushort)Math.Clamp(touch.X, 0, maxX);
            ushort y = (ushort)Math.Clamp(touch.Y, 0, maxY);
            WriteUInt16(packet, offset, x);
            WriteUInt16(packet, offset + 2, y);
            packet[offset + 4] = touch.IsActive ? (byte)1 : (byte)0;
        }

        private static void WriteMotion(byte[] packet, int offset, DS4State state)
        {
            WriteInt16(packet, offset, ClampShort(state.Motion?.gyroYawFull ?? 0));
            WriteInt16(packet, offset + 2, ClampShort(state.Motion?.gyroPitchFull ?? 0));
            WriteInt16(packet, offset + 4, ClampShort(state.Motion?.gyroRollFull ?? 0));
            WriteInt16(packet, offset + 6, ClampShort(state.Motion?.accelXFull ?? 0));
            WriteInt16(packet, offset + 8, ClampShort(state.Motion?.accelYFull ?? 0));
            WriteInt16(packet, offset + 10, ClampShort(state.Motion?.accelZFull ?? 0));
        }

        private static void ApplySteeringWheelX360(DS4State state, int device, ref byte l2, ref byte r2, ref short lx, ref short ly, ref short rx, ref short ry)
        {
            if (device < 0)
            {
                return;
            }

            short wheel = (short)Math.Clamp(state.SASteeringWheelEmulationUnit, short.MinValue, short.MaxValue);
            switch (Global.GetSASteeringWheelEmulationAxis(device))
            {
                case SASteeringWheelEmulationAxisType.LX:
                    lx = wheel;
                    break;
                case SASteeringWheelEmulationAxisType.LY:
                    ly = wheel;
                    break;
                case SASteeringWheelEmulationAxisType.RX:
                    rx = wheel;
                    break;
                case SASteeringWheelEmulationAxisType.RY:
                    ry = wheel;
                    break;
                case SASteeringWheelEmulationAxisType.L2R2:
                    l2 = r2 = 0;
                    if (wheel >= 0)
                    {
                        l2 = (byte)Math.Clamp(wheel / 128, 0, 255);
                    }
                    else
                    {
                        r2 = (byte)Math.Clamp(-wheel / 128, 0, 255);
                    }
                    break;
            }
        }

        private static void ApplySteeringWheelByteAxes(DS4State state, int device, ref byte l2, ref byte r2, ref byte lx, ref byte ly, ref byte rx, ref byte ry)
        {
            if (device < 0)
            {
                return;
            }

            byte wheel = (byte)Math.Clamp(state.SASteeringWheelEmulationUnit, 0, 255);
            switch (Global.GetSASteeringWheelEmulationAxis(device))
            {
                case SASteeringWheelEmulationAxisType.LX:
                    lx = wheel;
                    break;
                case SASteeringWheelEmulationAxisType.LY:
                    ly = wheel;
                    break;
                case SASteeringWheelEmulationAxisType.RX:
                    rx = wheel;
                    break;
                case SASteeringWheelEmulationAxisType.RY:
                    ry = wheel;
                    break;
                case SASteeringWheelEmulationAxisType.L2R2:
                    l2 = wheel >= 128 ? (byte)((wheel - 128) * 2) : (byte)0;
                    r2 = wheel < 128 ? (byte)((128 - wheel) * 2) : (byte)0;
                    break;
            }
        }

        private static void ApplySteeringWheelSwitchAxes(DS4State state, int device, ref ushort lx, ref ushort ly, ref ushort rx, ref ushort ry)
        {
            if (device < 0)
            {
                return;
            }

            ushort wheel = (ushort)Math.Clamp(state.SASteeringWheelEmulationUnit, 0, 4095);
            switch (Global.GetSASteeringWheelEmulationAxis(device))
            {
                case SASteeringWheelEmulationAxisType.LX:
                    lx = wheel;
                    break;
                case SASteeringWheelEmulationAxisType.LY:
                    ly = wheel;
                    break;
                case SASteeringWheelEmulationAxisType.RX:
                    rx = wheel;
                    break;
                case SASteeringWheelEmulationAxisType.RY:
                    ry = wheel;
                    break;
            }
        }

        private static byte ToSignedAxisByte(byte value)
        {
            return unchecked((byte)((sbyte)Math.Clamp(value - 128, sbyte.MinValue, sbyte.MaxValue)));
        }

        private static short AxisScaleX360(int value, bool flip)
        {
            unchecked
            {
                value -= 0x80;
                float recipRun = value >= 0 ? X360RecipInputPosResolution : X360RecipInputNegResolution;

                float temp = value * recipRun;
                if (flip)
                {
                    temp = -temp;
                }

                temp = (temp + 1.0f) * 0.5f;
                return (short)(temp * X360OutputResolution + (-32768));
            }
        }

        private static ushort ScaleSwitchAxis(byte value)
        {
            return (ushort)Math.Clamp((value * 4095 + 127) / 255, 0, 4095);
        }

        private static short ClampShort(int value)
        {
            return (short)Math.Clamp(value, short.MinValue, short.MaxValue);
        }

        private static void WriteUInt16(byte[] packet, int offset, ushort value)
        {
            packet[offset] = (byte)(value & 0xff);
            packet[offset + 1] = (byte)((value >> 8) & 0xff);
        }

        private static void WriteInt16(byte[] packet, int offset, short value)
        {
            WriteUInt16(packet, offset, unchecked((ushort)value));
        }

        private static void WriteUInt32(byte[] packet, int offset, uint value)
        {
            packet[offset] = (byte)(value & 0xff);
            packet[offset + 1] = (byte)((value >> 8) & 0xff);
            packet[offset + 2] = (byte)((value >> 16) & 0xff);
            packet[offset + 3] = (byte)((value >> 24) & 0xff);
        }
    }
}
