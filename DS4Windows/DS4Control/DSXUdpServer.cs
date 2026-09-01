/*
DS4Windows - DSX UDP Protocol Compatibility Server
Enables third-party game mods using the DSX UDP API to control adaptive triggers,
RGB lightbar, and player/mic LEDs directly on connected controllers.
*/

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using DS4Windows.InputDevices;
using NLog;

namespace DS4Windows.DS4Control
{
    public class DSXUdpServer : IDisposable
    {
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        public const int DEFAULT_PORT = 6969;
        public const string DEFAULT_LISTEN_ADDRESS = "127.0.0.1";

        private UdpClient _udpClient;
        private Thread _receiveThread;
        private CancellationTokenSource _cts;
        private bool _isRunning;
        private readonly object _lock = new object();

        public bool IsRunning
        {
            get { lock (_lock) return _isRunning; }
        }

        public int Port { get; private set; } = DEFAULT_PORT;
        public string ListenAddress { get; private set; } = DEFAULT_LISTEN_ADDRESS;

        // Callbacks for ControlService integration
        public delegate void TriggerUpdateHandler(int controllerIndex, TriggerId trigger, byte[] rawTriggerData);
        public delegate void RGBUpdateHandler(int controllerIndex, byte r, byte g, byte b, byte a);
        public delegate void MicLEDUpdateHandler(int controllerIndex, byte mode);
        public delegate void PlayerLEDUpdateHandler(int controllerIndex, bool[] leds);
        public delegate void ResetUserSettingsHandler(int controllerIndex);
        public delegate DSXStatusResponse StatusRequestHandler();

        public event TriggerUpdateHandler OnTriggerUpdate;
        public event RGBUpdateHandler OnRGBUpdate;
        public event MicLEDUpdateHandler OnMicLEDUpdate;
        public event PlayerLEDUpdateHandler OnPlayerLEDUpdate;
        public event ResetUserSettingsHandler OnResetUserSettings;
        public StatusRequestHandler GetStatus;

        public DSXUdpServer()
        {
        }

        public bool Start(int port = DEFAULT_PORT, string listenAddress = DEFAULT_LISTEN_ADDRESS)
        {
            lock (_lock)
            {
                if (_isRunning)
                {
                    Stop();
                }

                Port = port;
                ListenAddress = listenAddress;
                _cts = new CancellationTokenSource();

                try
                {
                    IPAddress ip = IPAddress.Parse(listenAddress);
                    _udpClient = new UdpClient(new IPEndPoint(ip, port));
                    _udpClient.EnableBroadcast = true;
                    _isRunning = true;

                    _receiveThread = new Thread(ReceiveLoop)
                    {
                        IsBackground = true,
                        Name = "DSX_UDP_Server_Thread"
                    };
                    _receiveThread.Start();

                    logger.Info($"DSX UDP Protocol Server started on {listenAddress}:{port}");
                    return true;
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
                {
                    logger.Warn(ex, $"DSX UDP Protocol Server port {port} is already in use (likely DSX or another instance).");
                    Stop();
                    return false;
                }
                catch (Exception ex)
                {
                    logger.Error(ex, $"Failed to start DSX UDP Protocol Server on {listenAddress}:{port}");
                    Stop();
                    return false;
                }
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                if (!_isRunning) return;

                _isRunning = false;
                _cts?.Cancel();

                try
                {
                    _udpClient?.Close();
                    _udpClient?.Dispose();
                }
                catch { }

                _udpClient = null;
                _cts?.Dispose();
                _cts = null;

                logger.Info("DSX UDP Protocol Server stopped.");
            }
        }

        private void ReceiveLoop()
        {
            IPEndPoint remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);

            while (_isRunning && _udpClient != null)
            {
                try
                {
                    byte[] data = _udpClient.Receive(ref remoteEndPoint);
                    if (data != null && data.Length > 0)
                    {
                        ProcessIncomingPacket(data, remoteEndPoint);
                    }
                }
                catch (SocketException) when (!_isRunning)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (_isRunning)
                    {
                        logger.Debug(ex, "Exception in DSX UDP ReceiveLoop");
                    }
                }
            }
        }

        public void ProcessIncomingPacket(byte[] data, IPEndPoint remoteEndPoint)
        {
            try
            {
                string jsonString = Encoding.UTF8.GetString(data).Trim();
                if (string.IsNullOrEmpty(jsonString)) return;

                using JsonDocument doc = JsonDocument.Parse(jsonString);
                JsonElement root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("instructions", out JsonElement instructionsElement) &&
                        instructionsElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement instructionElement in instructionsElement.EnumerateArray())
                        {
                            ProcessInstructionElement(instructionElement, remoteEndPoint);
                        }
                    }
                    else if (root.TryGetProperty("type", out _))
                    {
                        ProcessInstructionElement(root, remoteEndPoint);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Debug(ex, "Failed to parse incoming DSX UDP packet");
            }
        }

        private void ProcessInstructionElement(JsonElement instructionElement, IPEndPoint remoteEndPoint)
        {
            try
            {
                int instructionType = 0;
                if (instructionElement.TryGetProperty("type", out JsonElement typeProp))
                {
                    if (typeProp.ValueKind == JsonValueKind.Number)
                    {
                        instructionType = typeProp.GetInt32();
                    }
                    else if (typeProp.ValueKind == JsonValueKind.String)
                    {
                        instructionType = ParseInstructionTypeName(typeProp.GetString());
                    }
                }

                List<object> parameters = new List<object>();
                if (instructionElement.TryGetProperty("parameters", out JsonElement paramsProp) &&
                    paramsProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement p in paramsProp.EnumerateArray())
                    {
                        switch (p.ValueKind)
                        {
                            case JsonValueKind.Number:
                                if (p.TryGetInt32(out int iVal)) parameters.Add(iVal);
                                else if (p.TryGetDouble(out double dVal)) parameters.Add(dVal);
                                break;
                            case JsonValueKind.String:
                                parameters.Add(p.GetString());
                                break;
                            case JsonValueKind.True:
                            case JsonValueKind.False:
                                parameters.Add(p.GetBoolean());
                                break;
                            default:
                                parameters.Add(p.ToString());
                                break;
                        }
                    }
                }

                ExecuteInstruction(instructionType, parameters.ToArray(), remoteEndPoint);
            }
            catch (Exception ex)
            {
                logger.Debug(ex, "Error executing DSX UDP instruction");
            }
        }

        private static int ParseInstructionTypeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return 0;
            return name.Trim().ToLowerInvariant() switch
            {
                "getdsxstatus" or "status" => 0,
                "triggerupdate" or "trigger" => 1,
                "rgbupdate" or "rgb" or "lightbar" => 2,
                "playerled" or "playerleds" => 3,
                "triggerthreshold" => 4,
                "micled" or "mic" => 5,
                "playerlednewrevision" => 6,
                "resettousersettings" or "reset" => 7,
                "tomode" => 8,
                _ => int.TryParse(name, out int val) ? val : 0,
            };
        }

        public void ExecuteInstruction(int instructionType, object[] parameters, IPEndPoint remoteEndPoint)
        {
            int controllerIndex = (parameters.Length > 0 && parameters[0] != null)
                ? Convert.ToInt32(parameters[0])
                : 0;

            switch (instructionType)
            {
                case 0: // GetDSXStatus
                    SendStatusResponse(remoteEndPoint);
                    break;

                case 1: // TriggerUpdate
                    HandleTriggerUpdate(controllerIndex, parameters);
                    break;

                case 2: // RGBUpdate
                    HandleRGBUpdate(controllerIndex, parameters);
                    break;

                case 3: // PlayerLED
                case 6: // PlayerLEDNewRevision
                    HandlePlayerLEDUpdate(controllerIndex, parameters);
                    break;

                case 5: // MicLED
                    HandleMicLEDUpdate(controllerIndex, parameters);
                    break;

                case 7: // ResetToUserSettings
                    OnResetUserSettings?.Invoke(controllerIndex);
                    break;
            }
        }

        private void HandleTriggerUpdate(int controllerIndex, object[] parameters)
        {
            if (parameters.Length < 3) return;

            int triggerId = Convert.ToInt32(parameters[1]);
            TriggerId trigger = triggerId == 0 ? TriggerId.LeftTrigger : TriggerId.RightTrigger;

            int modeId = Convert.ToInt32(parameters[2]);

            // Unpack extra parameters which could be comma-delimited strings or separate items
            List<byte> rawParams = new List<byte>();
            for (int i = 3; i < parameters.Length; i++)
            {
                if (parameters[i] is string strVal && strVal.Contains(','))
                {
                    string[] parts = strVal.Split(',', StringSplitOptions.RemoveEmptyEntries);
                    foreach (string part in parts)
                    {
                        if (byte.TryParse(part.Trim(), out byte b)) rawParams.Add(b);
                        else if (int.TryParse(part.Trim(), out int val)) rawParams.Add((byte)Math.Clamp(val, 0, 255));
                    }
                }
                else
                {
                    try
                    {
                        rawParams.Add(Convert.ToByte(parameters[i]));
                    }
                    catch
                    {
                        rawParams.Add(0);
                    }
                }
            }

            byte[] raw11Bytes = EncodeTriggerPayload(modeId, rawParams.ToArray());
            OnTriggerUpdate?.Invoke(controllerIndex, trigger, raw11Bytes);
        }

        private void HandleRGBUpdate(int controllerIndex, object[] parameters)
        {
            if (parameters.Length < 4) return;

            byte r = Convert.ToByte(parameters[1]);
            byte g = Convert.ToByte(parameters[2]);
            byte b = Convert.ToByte(parameters[3]);
            byte a = parameters.Length > 4 && parameters[4] != null ? Convert.ToByte(parameters[4]) : (byte)255;

            OnRGBUpdate?.Invoke(controllerIndex, r, g, b, a);
        }

        private void HandleMicLEDUpdate(int controllerIndex, object[] parameters)
        {
            if (parameters.Length < 2) return;
            byte mode = Convert.ToByte(parameters[1]);
            OnMicLEDUpdate?.Invoke(controllerIndex, mode);
        }

        private void HandlePlayerLEDUpdate(int controllerIndex, object[] parameters)
        {
            if (parameters.Length < 2) return;

            bool[] leds = new bool[5];
            for (int i = 0; i < 5 && (i + 1) < parameters.Length; i++)
            {
                leds[i] = Convert.ToBoolean(parameters[i + 1]);
            }

            OnPlayerLEDUpdate?.Invoke(controllerIndex, leds);
        }

        private void SendStatusResponse(IPEndPoint remoteEndPoint)
        {
            if (remoteEndPoint == null || _udpClient == null) return;

            try
            {
                DSXStatusResponse response = GetStatus != null
                    ? GetStatus.Invoke()
                    : new DSXStatusResponse
                    {
                        Status = "DS4Windows DSX UDP Server Ready",
                        TimeReceived = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        isControllerConnected = true,
                        BatteryLevel = 100,
                        Devices = new List<DSXDeviceInfo>()
                    };

                string responseJson = JsonSerializer.Serialize(response);
                byte[] responseBytes = Encoding.UTF8.GetBytes(responseJson);
                _udpClient.Send(responseBytes, responseBytes.Length, remoteEndPoint);
            }
            catch (Exception ex)
            {
                logger.Debug(ex, "Failed to send DSX status response");
            }
        }

        public static byte[] EncodeTriggerPayload(int modeId, byte[] p)
        {
            byte[] dest = new byte[11];

            // Helper accessors
            byte P(int idx, byte def = 0) => idx < p.Length ? p[idx] : def;

            switch (modeId)
            {
                case 0: // Normal / Off
                case 20: // Off
                    EncodeOff(dest);
                    break;

                case 1: // GameCube
                    EncodeSimpleWeapon(dest, 144, 160, 255);
                    break;

                case 2: // Very Soft
                    EncodeSimpleFeedback(dest, 0, 30);
                    break;

                case 3: // Soft
                    EncodeSimpleFeedback(dest, 0, 70);
                    break;

                case 4: // Hard
                    EncodeSimpleFeedback(dest, 0, 140);
                    break;

                case 5: // Very Hard
                    EncodeSimpleFeedback(dest, 0, 190);
                    break;

                case 6: // Hardest
                    EncodeSimpleFeedback(dest, 0, 240);
                    break;

                case 7: // Rigid
                    EncodeRigid(dest);
                    break;

                case 8: // Vibrate
                case 23: // Vibration
                    EncodeVibration(dest, P(0, 0), P(1, 4), P(2, 20));
                    break;

                case 9: // Choppy
                    EncodeChoppy(dest);
                    break;

                case 10: // Medium
                    EncodeSimpleFeedback(dest, 0, 100);
                    break;

                case 11: // Vibrate Pulse
                    EncodeSimpleWeapon(dest, 0, 0, 0);
                    break;

                case 13: // Resistance / Feedback
                case 21: // Feedback
                    EncodeFeedback(dest, P(0, 0), P(1, 4));
                    break;

                case 14: // Bow
                    EncodeBow(dest, P(0, 0), P(1, 8), P(2, 4), P(3, 4));
                    break;

                case 15: // Galloping
                    EncodeGalloping(dest, P(0, 0), P(1, 8), P(2, 2), P(3, 4), P(4, 10));
                    break;

                case 16: // Semi-Automatic Gun / Weapon
                case 22: // Weapon
                    EncodeWeapon(dest, P(0, 2), P(1, 7), P(2, 4));
                    break;

                case 17: // Automatic Gun
                case 19: // Vibrate Trigger 10
                    EncodeVibration(dest, P(0, 0), P(1, 8), P(2, 10));
                    break;

                case 18: // Machine
                    EncodeMachine(dest, P(0, 1), P(1, 8), P(2, 3), P(3, 7), P(4, 10), P(5, 2));
                    break;

                case 24: // Slope Feedback
                    EncodeSlopeFeedback(dest, P(0, 0), P(1, 9), P(2, 1), P(3, 8));
                    break;

                case 12: // Custom Trigger Values
                case 25: // Multiple Position Feedback
                    EncodeMultiplePositionFeedback(dest, p);
                    break;

                case 26: // Multiple Position Vibration
                    EncodeMultiplePositionVibration(dest, P(10, 20), p);
                    break;

                default:
                    EncodeFeedback(dest, P(0, 0), P(1, 4));
                    break;
            }

            return dest;
        }

        #region Trigger Encoding Algorithms

        public static void EncodeOff(byte[] dest)
        {
            Array.Clear(dest, 0, 11);
            dest[0] = 0x05;
        }

        public static void EncodeRigid(byte[] dest)
        {
            Array.Clear(dest, 0, 11);
            dest[0] = 0x01;
        }

        public static void EncodeChoppy(byte[] dest)
        {
            Array.Clear(dest, 0, 11);
            dest[0] = 33;
            dest[1] = 2;
            dest[2] = 39;
            dest[3] = 24;
            dest[6] = 38;
        }

        public static void EncodeSimpleFeedback(byte[] dest, byte position, byte strength)
        {
            Array.Clear(dest, 0, 11);
            dest[0] = 0x01;
            dest[1] = position;
            dest[2] = strength;
        }

        public static void EncodeSimpleWeapon(byte[] dest, byte startPosition, byte endPosition, byte strength)
        {
            Array.Clear(dest, 0, 11);
            dest[0] = 0x02;
            dest[1] = startPosition;
            dest[2] = endPosition;
            dest[3] = strength;
        }

        public static void EncodeFeedback(byte[] dest, byte position, byte strength)
        {
            Array.Clear(dest, 0, 11);
            position = (byte)Math.Clamp((int)position, 0, 9);
            strength = (byte)Math.Clamp((int)strength, 0, 8);

            if (strength == 0)
            {
                EncodeOff(dest);
                return;
            }

            byte val = (byte)((strength - 1) & 0x07);
            uint packedForce = 0;
            ushort zoneMask = 0;

            for (int i = position; i < 10; i++)
            {
                packedForce |= (uint)(val << (3 * i));
                zoneMask |= (ushort)(1 << i);
            }

            dest[0] = 0x21; // 33
            dest[1] = (byte)(zoneMask & 0xFF);
            dest[2] = (byte)((zoneMask >> 8) & 0xFF);
            dest[3] = (byte)(packedForce & 0xFF);
            dest[4] = (byte)((packedForce >> 8) & 0xFF);
            dest[5] = (byte)((packedForce >> 16) & 0xFF);
            dest[6] = (byte)((packedForce >> 24) & 0xFF);
        }

        public static void EncodeWeapon(byte[] dest, byte startPosition, byte endPosition, byte strength)
        {
            Array.Clear(dest, 0, 11);
            startPosition = (byte)Math.Clamp((int)startPosition, 2, 7);
            endPosition = (byte)Math.Clamp((int)endPosition, startPosition + 1, 8);
            strength = (byte)Math.Clamp((int)strength, 1, 8);

            ushort zoneMask = (ushort)((1 << startPosition) | (1 << endPosition));
            dest[0] = 0x25; // 37
            dest[1] = (byte)(zoneMask & 0xFF);
            dest[2] = (byte)((zoneMask >> 8) & 0xFF);
            dest[3] = (byte)(strength - 1);
        }

        public static void EncodeVibration(byte[] dest, byte position, byte amplitude, byte frequency)
        {
            Array.Clear(dest, 0, 11);
            position = (byte)Math.Clamp((int)position, 0, 9);
            amplitude = (byte)Math.Clamp((int)amplitude, 0, 8);

            if (amplitude == 0 || frequency == 0)
            {
                EncodeOff(dest);
                return;
            }

            byte val = (byte)((amplitude - 1) & 0x07);
            uint packedForce = 0;
            ushort zoneMask = 0;

            for (int i = position; i < 10; i++)
            {
                packedForce |= (uint)(val << (3 * i));
                zoneMask |= (ushort)(1 << i);
            }

            dest[0] = 0x26; // 38
            dest[1] = (byte)(zoneMask & 0xFF);
            dest[2] = (byte)((zoneMask >> 8) & 0xFF);
            dest[3] = (byte)(packedForce & 0xFF);
            dest[4] = (byte)((packedForce >> 8) & 0xFF);
            dest[5] = (byte)((packedForce >> 16) & 0xFF);
            dest[6] = (byte)((packedForce >> 24) & 0xFF);
            dest[9] = frequency;
        }

        public static void EncodeBow(byte[] dest, byte startPosition, byte endPosition, byte strength, byte snapForce)
        {
            Array.Clear(dest, 0, 11);
            startPosition = (byte)Math.Clamp((int)startPosition, 0, 8);
            endPosition = (byte)Math.Clamp((int)endPosition, startPosition + 1, 8);
            strength = (byte)Math.Clamp((int)strength, 1, 8);
            snapForce = (byte)Math.Clamp((int)snapForce, 1, 8);

            ushort zoneMask = (ushort)((1 << startPosition) | (1 << endPosition));
            uint packedForce = (uint)(((strength - 1) & 0x07) | (((snapForce - 1) & 0x07) << 3));

            dest[0] = 0x22; // 34
            dest[1] = (byte)(zoneMask & 0xFF);
            dest[2] = (byte)((zoneMask >> 8) & 0xFF);
            dest[3] = (byte)(packedForce & 0xFF);
        }

        public static void EncodeGalloping(byte[] dest, byte startPosition, byte endPosition, byte firstFoot, byte secondFoot, byte frequency)
        {
            Array.Clear(dest, 0, 11);
            startPosition = (byte)Math.Clamp((int)startPosition, 0, 8);
            endPosition = (byte)Math.Clamp((int)endPosition, startPosition + 1, 9);
            firstFoot = (byte)Math.Clamp((int)firstFoot, 0, 6);
            secondFoot = (byte)Math.Clamp((int)secondFoot, firstFoot + 1, 7);

            if (frequency == 0)
            {
                EncodeOff(dest);
                return;
            }

            ushort zoneMask = (ushort)((1 << startPosition) | (1 << endPosition));
            uint packedForce = (uint)((secondFoot & 0x07) | ((firstFoot & 0x07) << 3));

            dest[0] = 0x23; // 35
            dest[1] = (byte)(zoneMask & 0xFF);
            dest[2] = (byte)((zoneMask >> 8) & 0xFF);
            dest[3] = (byte)(packedForce & 0xFF);
            dest[4] = frequency;
        }

        public static void EncodeMachine(byte[] dest, byte startPosition, byte endPosition, byte amplitudeA, byte amplitudeB, byte frequency, byte period)
        {
            Array.Clear(dest, 0, 11);
            startPosition = (byte)Math.Clamp((int)startPosition, 0, 8);
            endPosition = (byte)Math.Clamp((int)endPosition, startPosition + 1, 9);
            amplitudeA = (byte)Math.Clamp((int)amplitudeA, 0, 7);
            amplitudeB = (byte)Math.Clamp((int)amplitudeB, 0, 7);

            if (frequency == 0)
            {
                EncodeOff(dest);
                return;
            }

            ushort zoneMask = (ushort)((1 << startPosition) | (1 << endPosition));
            uint packedForce = (uint)((amplitudeA & 0x07) | ((amplitudeB & 0x07) << 3));

            dest[0] = 0x27; // 39
            dest[1] = (byte)(zoneMask & 0xFF);
            dest[2] = (byte)((zoneMask >> 8) & 0xFF);
            dest[3] = (byte)(packedForce & 0xFF);
            dest[4] = frequency;
            dest[5] = period;
        }

        public static void EncodeSlopeFeedback(byte[] dest, byte startPosition, byte endPosition, byte startStrength, byte endStrength)
        {
            startPosition = (byte)Math.Clamp((int)startPosition, 0, 8);
            endPosition = (byte)Math.Clamp((int)endPosition, startPosition + 1, 9);
            startStrength = (byte)Math.Clamp((int)startStrength, 1, 8);
            endStrength = (byte)Math.Clamp((int)endStrength, 1, 8);

            byte[] array = new byte[10];
            float slope = 1.0f * (endStrength - startStrength) / (endPosition - startPosition);
            for (int i = startPosition; i < 10; i++)
            {
                if (i <= endPosition)
                    array[i] = (byte)Math.Round(startStrength + slope * (i - startPosition));
                else
                    array[i] = endStrength;
            }

            EncodeMultiplePositionFeedback(dest, array);
        }

        public static void EncodeMultiplePositionFeedback(byte[] dest, byte[] strengths)
        {
            Array.Clear(dest, 0, 11);
            if (strengths == null || strengths.Length == 0)
            {
                EncodeOff(dest);
                return;
            }

            uint packedForce = 0;
            ushort zoneMask = 0;
            bool anyActive = false;

            for (int i = 0; i < Math.Min(10, strengths.Length); i++)
            {
                if (strengths[i] > 0)
                {
                    anyActive = true;
                    byte val = (byte)((Math.Clamp((int)strengths[i], 1, 8) - 1) & 0x07);
                    packedForce |= (uint)(val << (3 * i));
                    zoneMask |= (ushort)(1 << i);
                }
            }

            if (!anyActive)
            {
                EncodeOff(dest);
                return;
            }

            dest[0] = 0x21; // 33
            dest[1] = (byte)(zoneMask & 0xFF);
            dest[2] = (byte)((zoneMask >> 8) & 0xFF);
            dest[3] = (byte)(packedForce & 0xFF);
            dest[4] = (byte)((packedForce >> 8) & 0xFF);
            dest[5] = (byte)((packedForce >> 16) & 0xFF);
            dest[6] = (byte)((packedForce >> 24) & 0xFF);
        }

        public static void EncodeMultiplePositionVibration(byte[] dest, byte frequency, byte[] amplitudes)
        {
            Array.Clear(dest, 0, 11);
            if (amplitudes == null || amplitudes.Length == 0 || frequency == 0)
            {
                EncodeOff(dest);
                return;
            }

            uint packedForce = 0;
            ushort zoneMask = 0;
            bool anyActive = false;

            for (int i = 0; i < Math.Min(10, amplitudes.Length); i++)
            {
                if (amplitudes[i] > 0)
                {
                    anyActive = true;
                    byte val = (byte)((Math.Clamp((int)amplitudes[i], 1, 8) - 1) & 0x07);
                    packedForce |= (uint)(val << (3 * i));
                    zoneMask |= (ushort)(1 << i);
                }
            }

            if (!anyActive)
            {
                EncodeOff(dest);
                return;
            }

            dest[0] = 0x26; // 38
            dest[1] = (byte)(zoneMask & 0xFF);
            dest[2] = (byte)((zoneMask >> 8) & 0xFF);
            dest[3] = (byte)(packedForce & 0xFF);
            dest[4] = (byte)((packedForce >> 8) & 0xFF);
            dest[5] = (byte)((packedForce >> 16) & 0xFF);
            dest[6] = (byte)((packedForce >> 24) & 0xFF);
            dest[9] = frequency;
        }

        #endregion

        public void Dispose()
        {
            Stop();
        }
    }

    public class DSXStatusResponse
    {
        public string Status { get; set; } = "Running";
        public string TimeReceived { get; set; } = string.Empty;
        public bool isControllerConnected { get; set; }
        public int BatteryLevel { get; set; }
        public List<DSXDeviceInfo> Devices { get; set; } = new List<DSXDeviceInfo>();
    }

    public class DSXDeviceInfo
    {
        public int Index { get; set; }
        public string MacAddress { get; set; } = string.Empty;
        public int DeviceType { get; set; }
        public int ConnectionType { get; set; }
        public int BatteryLevel { get; set; }
        public bool IsSupportAT { get; set; } = true;
        public bool IsSupportLightBar { get; set; } = true;
        public bool IsSupportPlayerLED { get; set; } = true;
        public bool IsSupportLegacyPlayerLED { get; set; } = true;
        public bool IsSupportMicLED { get; set; } = true;
    }
}
